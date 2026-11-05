using System.Net.Sockets;
using Renci.SshNet.Common;

namespace SshOpen;

/// <summary>
/// Flujo del plugin: se descarga el fichero remoto a una copia en %TEMP%, se abre en
/// Notepad++ y cada vez que se guarda esa copia se vuelve a subir al servidor.
/// </summary>
internal static class Plugin
{
    public const string Name = "SshOpen";

    // Claves introducidas en esta sesión de Notepad++, por cuenta (solo en memoria).
    // Ordinal: los usuarios de Linux distinguen mayúsculas.
    private static readonly Dictionary<string, string> Passwords = new(StringComparer.Ordinal);

    // Copias locales cuya última subida falló o se canceló.
    private static readonly HashSet<string> PendingUpload = new(StringComparer.OrdinalIgnoreCase);
    private static bool _suppressAutoUpload;

    public static void OpenRemote()
    {
        var (last, remember, profile) = Config.LoadLast();
        var request = WithKnownPassword(last);
        var options = new ConnectOptions { Remember = remember, Profile = profile };

        while (true)
        {
            var r = ConnectDialog.Show(Npp.Handle, "Abrir fichero remoto (SSH/SFTP)", "Conectar", request, options);
            if (r is null)
                return;
            request = r;

            try
            {
                using var client = ConnectWithWaitCursor(request);
                Passwords[request.Account] = request.Password;

                // Siempre se navega con el explorador. Si la ruta es un fichero, se empieza en su
                // carpeta con él seleccionado; si no existe, el explorador vuelve al home.
                string folder = Sftp.ResolvePath(client, request.RemotePath);
                string? select = null;
                if (IsFile(client, folder))
                {
                    select = folder[(folder.LastIndexOf('/') + 1)..];
                    folder = Sftp.Parent(folder);
                }

                string? path = BrowseDialog.Show(Npp.Handle, client, folder, select,
                    $"{request.User}@{request.Host} - Elige el fichero a abrir", out string lastFolder);
                Config.SaveLast(request with { RemotePath = lastFolder }, options.Remember, options.Profile);
                if (path is null)
                    return;

                var target = request with { RemotePath = path };
                string localPath = LocalPathFor(target);
                if (!HasUnsyncedChanges(client, path, localPath) || ConfirmOverwriteLocal(path))
                {
                    target = Download(client, target, localPath);
                    PendingUpload.Remove(localPath);
                }

                Config.MapFile(localPath, target);
                Npp.OpenFile(localPath);
                Npp.SetStatus($"SFTP: abierto {target.Display}{(target.Sudo ? " (sudo)" : "")}");
                return;
            }
            catch (OperationCanceledException)
            {
                continue;
            }
            catch (Exception ex)
            {
                if (ex is SshAuthenticationException)
                    Passwords.Remove(request.Account);
                ShowError("No se pudo abrir " + request.Display, ex);
            }
        }
    }

    /// <summary>Descarga por SFTP; sin permiso de lectura ofrece hacerlo con sudo.</summary>
    private static ConnectionRequest Download(Renci.SshNet.SftpClient client, ConnectionRequest target, string localPath)
    {
        try
        {
            using (new WaitCursor())
                Sftp.Download(client, target.RemotePath, localPath);
            return target with { Sudo = false };
        }
        catch (SftpPermissionDeniedException) when (AskSudo(target, "leer"))
        {
            using (new WaitCursor())
                Sudo.Download(target, localPath);
            return target with { Sudo = true };
        }
    }

    // Cuentas para las que el usuario ya aceptó usar sudo en esta sesión.
    private static readonly HashSet<string> SudoAccounts = new(StringComparer.Ordinal);

    private static bool AskSudo(ConnectionRequest r, string action)
    {
        if (SudoAccounts.Contains(r.Account))
            return true;
        int answer = Win32.MessageBox(Npp.Handle,
            $"{r.User} no tiene permiso para {action} {r.RemotePath}.\n\n¿Intentar con sudo (con la misma clave)?\n\n" +
            $"No se volverá a preguntar para {r.Account} mientras Notepad++ siga abierto.",
            Name, Win32.MB_YESNO | Win32.MB_ICONQUESTION);
        if (answer != Win32.IDYES)
            return false;
        SudoAccounts.Add(r.Account);
        return true;
    }

    private static bool IsFile(Renci.SshNet.SftpClient client, string path)
    {
        try
        {
            return !client.GetAttributes(path).IsDirectory;
        }
        catch (SftpPathNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// La copia local tiene cambios que no llegaron al servidor: lo sabemos de esta sesión, o la
    /// copia es claramente más reciente que el fichero remoto (margen por desfase de relojes).
    /// </summary>
    private static bool HasUnsyncedChanges(Renci.SshNet.SftpClient client, string remotePath, string localPath)
    {
        if (!File.Exists(localPath))
            return false;
        if (PendingUpload.Contains(localPath))
            return true;
        return File.GetLastWriteTimeUtc(localPath) > client.GetLastWriteTimeUtc(remotePath).AddMinutes(2);
    }

    private static bool ConfirmOverwriteLocal(string remotePath) =>
        Win32.MessageBox(Npp.Handle,
            $"La copia local de {remotePath} tiene cambios que no se subieron al servidor.\n\n" +
            "¿Descargar de nuevo el fichero del servidor?\n\n" +
            "Sí: se pierden los cambios locales.\nNo: se abre la copia local tal cual (podrás subirla al guardar).",
            Name, Win32.MB_YESNO | Win32.MB_ICONWARNING | Win32.MB_DEFBUTTON2) == Win32.IDYES;

    private static Renci.SshNet.SftpClient ConnectWithWaitCursor(ConnectionRequest r)
    {
        using (new WaitCursor())
            return Sftp.Connect(r);
    }

    public static void OnFileSaved(nuint bufferId)
    {
        // Con un diálogo abierto no se puede abrir otro (estado estático); el fichero queda
        // pendiente y se sube con "Subir fichero actual" o en el siguiente guardado.
        if (_suppressAutoUpload || DialogHost.IsOpen)
            return;
        string? path = Npp.GetBufferPath(bufferId);
        if (path != null && Config.TryGetTarget(path, out var target))
            Upload(path, target);
    }

    public static void UploadCurrent()
    {
        string? path = Npp.GetBufferPath(Npp.CurrentBufferId);
        if (path == null || !Config.TryGetTarget(path, out var target))
        {
            Win32.MessageBox(Npp.Handle, "El fichero actual no se abrió con " + Name + ".", Name, Win32.MB_ICONINFORMATION);
            return;
        }

        bool saved;
        _suppressAutoUpload = true;
        try
        {
            saved = Npp.SaveCurrentFile();
        }
        finally
        {
            _suppressAutoUpload = false;
        }
        if (!saved)
        {
            Win32.MessageBox(Npp.Handle, "No se pudo guardar la copia local, así que no se ha subido nada.", Name, Win32.MB_ICONWARNING);
            return;
        }
        Upload(path, target);
    }

    public static void About()
    {
        Win32.MessageBox(Npp.Handle,
            Name + "\n\n" +
            "Abre ficheros de un servidor Linux por SSH (SFTP) indicando IP, usuario y clave.\n\n" +
            "• Al conectar se muestran las carpetas y ficheros para navegar hasta el fichero:\n" +
            "   doble clic o Intro para entrar o abrir, Retroceso para subir de carpeta.\n" +
            "• El fichero se descarga a una copia en %TEMP%\\SshOpen.\n" +
            "• Al guardar (Ctrl+S) se sube automáticamente al servidor.\n" +
            "• Si el usuario no tiene permiso sobre el fichero, ofrece leerlo y escribirlo con sudo.\n" +
            "• Los perfiles guardan servidor, puerto, usuario y carpeta (nunca la clave).\n" +
            "• La clave solo se guarda en memoria mientras Notepad++ está abierto.\n" +
            "• La huella del servidor se pide la primera vez y se recuerda.",
            Name, Win32.MB_ICONINFORMATION);
    }

    private static void Upload(string localPath, ConnectionRequest target)
    {
        var request = WithKnownPassword(target);
        while (true)
        {
            if (!Passwords.ContainsKey(request.Account))
            {
                var r = ConnectDialog.Show(Npp.Handle, "Subir " + Path.GetFileName(localPath) + " al servidor",
                    "Subir", request, options: null);
                if (r is null)
                {
                    PendingUpload.Add(localPath);
                    Npp.SetStatus("SFTP: subida cancelada, los cambios solo están en la copia local");
                    return;
                }
                request = r with { Sudo = request.Sudo };
            }

            try
            {
                using (new WaitCursor())
                {
                    if (request.Sudo)
                        Sudo.Upload(request, localPath);
                    else
                        Sftp.Upload(request, localPath);
                }
            }
            catch (SftpPermissionDeniedException) when (!request.Sudo && AskSudo(request, "escribir en"))
            {
                request = request with { Sudo = true };
                continue;
            }
            catch (OperationCanceledException)
            {
                PendingUpload.Add(localPath);
                Npp.SetStatus("SFTP: subida cancelada, los cambios solo están en la copia local");
                return;
            }
            catch (Exception ex)
            {
                if (ex is SshAuthenticationException)
                    Passwords.Remove(request.Account);
                int answer = Win32.MessageBox(Npp.Handle,
                    $"No se pudo subir a {request.Display}\n\n{Describe(ex)}\n\nLos cambios están guardados en la copia local:\n{localPath}",
                    Name, Win32.MB_RETRYCANCEL | Win32.MB_ICONERROR);
                if (answer == Win32.IDRETRY)
                    continue;
                PendingUpload.Add(localPath);
                return;
            }

            PendingUpload.Remove(localPath);
            Passwords[request.Account] = request.Password;
            if (request.WithoutPassword() != target)
                Config.MapFile(localPath, request);
            Npp.SetStatus($"SFTP: subido {request.Display}{(request.Sudo ? " (sudo)" : "")} ({DateTime.Now:HH:mm:ss})");
            return;
        }
    }

    private static ConnectionRequest WithKnownPassword(ConnectionRequest r) =>
        Passwords.TryGetValue(r.Account, out var password) ? r with { Password = password } : r;

    /// <summary>
    /// %TEMP%\SshOpen\usuario@host_puerto\ruta\remota (sin "..", para no salir de la carpeta).
    /// Con la forma larga de %TEMP%: Notepad++ devuelve las rutas sin nombres 8.3 y la clave
    /// del mapa de ficheros debe coincidir con ellas.
    /// </summary>
    private static string LocalPathFor(ConnectionRequest r)
    {
        var parts = new List<string> { Win32.GetLongPathName(Path.GetTempPath()), Name, Sanitize($"{r.User}@{r.Host}_{r.Port}") };
        parts.AddRange(r.SftpPath.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != "." && p != "..")
            .Select(Sanitize));
        return Path.Combine(parts.ToArray());
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    public static void ShowError(string title, Exception ex) => ShowError(Npp.Handle, title, ex);

    /// <summary>
    /// Desde dentro de un diálogo modal el propietario debe ser ese diálogo: MessageBox vuelve a
    /// habilitar a su propietario al cerrarse, y si fuera Notepad++ quedaría activo bajo el modal.
    /// </summary>
    public static void ShowError(IntPtr owner, string title, Exception ex) =>
        Win32.MessageBox(owner, $"{title}\n\n{Describe(ex)}", Name, Win32.MB_ICONERROR);

    private static string Describe(Exception ex) => ex switch
    {
        SshAuthenticationException => "Usuario o clave incorrectos.",
        SftpPathNotFoundException => "La ruta remota no existe.",
        SftpPermissionDeniedException => "Permiso denegado en el servidor.",
        SshOperationTimeoutException => "Tiempo de espera agotado.",
        SocketException se => "No se puede conectar con el servidor: " + se.Message,
        SshConnectionException sc => "Error de conexión SSH: " + sc.Message,
        _ => ex.Message,
    };
}
