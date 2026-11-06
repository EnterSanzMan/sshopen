using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SshOpen;

/// <summary>
/// Flujo del plugin: se descarga el fichero remoto a una copia en %TEMP%, se abre en
/// Notepad++ y cada vez que se guarda esa copia se vuelve a subir al servidor.
/// </summary>
internal static class Plugin
{
    public const string Name = "SshOpen";

    /// <summary>Credenciales que funcionaron en esta sesión de Notepad++ (solo en memoria).</summary>
    private sealed record Secret(string Password, string KeyFile, string Passphrase);

    // Por cuenta. Ordinal: los usuarios de Linux distinguen mayúsculas.
    private static readonly Dictionary<string, Secret> Secrets = new(StringComparer.Ordinal);

    // Copias locales cuya última subida falló o se canceló.
    private static readonly HashSet<string> PendingUpload = new(StringComparer.OrdinalIgnoreCase);

    // Cuentas para las que el usuario ya aceptó usar sudo en esta sesión.
    private static readonly HashSet<string> SudoAccounts = new(StringComparer.Ordinal);

    private static bool _suppressAutoUpload;

    // --- Abrir ---

    public static void OpenRemote()
    {
        var (last, remember, profile) = Config.LoadLast();
        var request = WithKnownSecrets(last);
        var options = new ConnectOptions { Remember = remember, Profile = profile };

        while (true)
        {
            var r = ConnectDialog.Show(Npp.Handle, "Abrir fichero remoto (SSH/SFTP)", "Conectar", request, options);
            if (r is null)
                return;
            request = r;

            try
            {
                using var client = ConnectInteractive(request);
                if (client is null)
                    continue;
                RememberSecrets(request);

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

                OpenFile(client, request, path);
                return;
            }
            catch (OperationCanceledException)
            {
                continue;
            }
            catch (Exception ex)
            {
                ForgetSecretsOnAuthError(request, ex);
                ShowError("No se pudo abrir " + request.Display, ex);
            }
        }
    }

    /// <summary>
    /// Descarga el fichero remoto a su copia local (salvo que la copia tenga cambios sin subir y
    /// el usuario prefiera conservarlos), la enlaza para subirla al guardar y la abre.
    /// </summary>
    internal static void OpenFile(SftpClient client, ConnectionRequest request, string remotePath)
    {
        var target = request with { RemotePath = remotePath, Sudo = false };
        string localPath = LocalPathFor(target);
        long stamp = Config.TryGetLink(localPath, out var link) ? link.RemoteStamp : 0;
        if (link != null)
            target = target with { Sudo = link.Target.Sudo };
        if (!HasUnsyncedChanges(client, remotePath, localPath) || ConfirmOverwriteLocal(remotePath))
        {
            (target, stamp) = Download(client, target, localPath);
            PendingUpload.Remove(localPath);
        }

        Config.MapFile(localPath, target, stamp);
        Npp.OpenFile(localPath);
        Npp.SetStatus($"SFTP: abierto {target.Display}{(target.Sudo ? " (sudo)" : "")}");
    }

    /// <summary>
    /// Conecta mostrando progreso; si la huella del servidor no está aceptada la enseña y, si el
    /// usuario la acepta, la guarda y reintenta. Devuelve null si la rechaza.
    /// </summary>
    internal static SftpClient? ConnectInteractive(ConnectionRequest request)
    {
        while (true)
        {
            try
            {
                return ProgressDialog.Run(Npp.Handle, $"Conectando con {request.User}@{request.Host}:{request.Port}...",
                    p => Sftp.Connect(request, p.Token));
            }
            catch (UntrustedHostKeyException e)
            {
                if (!AskTrust(request, e))
                    return null;
                Config.TrustHost(request.Host, request.Port, e.Fingerprint);
            }
        }
    }

    private static bool AskTrust(ConnectionRequest r, UntrustedHostKeyException e)
    {
        string text = e.Known == null
            ? $"Es la primera vez que te conectas a {r.Host}:{r.Port}.\n\nHuella del servidor:\n{e.Fingerprint}\n\n¿Confiar en este servidor?"
            : $"¡ATENCIÓN! La huella de {r.Host}:{r.Port} ha CAMBIADO.\n\nGuardada:\n{e.Known}\n\nRecibida:\n{e.Fingerprint}\n\n" +
              "Puede ser una reinstalación del servidor o un ataque man-in-the-middle.\n¿Confiar en la nueva huella?";
        uint flags = Win32.MB_YESNO | (e.Known == null ? Win32.MB_ICONQUESTION : Win32.MB_ICONWARNING | Win32.MB_DEFBUTTON2);
        return Win32.MessageBox(Npp.Handle, text, Name, flags) == Win32.IDYES;
    }

    /// <summary>Descarga por SFTP; sin permiso de lectura ofrece hacerlo con sudo. Devuelve el destino y la fecha remota.</summary>
    private static (ConnectionRequest Target, long Stamp) Download(SftpClient client, ConnectionRequest target, string localPath)
    {
        string title = "Descargando " + target.RemotePath;
        if (!target.Sudo)
        {
            try
            {
                long stamp = ProgressDialog.Run(Npp.Handle, title, p => Sftp.Download(client, target.RemotePath, localPath, p));
                return (target, stamp);
            }
            catch (SftpPermissionDeniedException) when (AskSudo(target, "leer"))
            {
            }
        }
        long sudoStamp = ProgressDialog.Run(Npp.Handle, title + " (sudo)", p => Sudo.Download(target, localPath, p));
        return (target with { Sudo = true }, sudoStamp);
    }

    private static bool AskSudo(ConnectionRequest r, string action)
    {
        if (SudoAccounts.Contains(r.Account))
            return true;
        int answer = Win32.MessageBox(Npp.Handle,
            $"{r.User} no tiene permiso para {action} {r.RemotePath}.\n\n¿Intentar con sudo (con la misma contraseña)?\n\n" +
            $"No se volverá a preguntar para {r.Account} mientras Notepad++ siga abierto.",
            Name, Win32.MB_YESNO | Win32.MB_ICONQUESTION);
        if (answer != Win32.IDYES)
            return false;
        SudoAccounts.Add(r.Account);
        return true;
    }

    internal static bool IsFile(SftpClient client, string path)
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
    private static bool HasUnsyncedChanges(SftpClient client, string remotePath, string localPath)
    {
        if (!File.Exists(localPath))
            return false;
        if (PendingUpload.Contains(localPath))
            return true;
        long remote = Sftp.GetStamp(client, remotePath);
        return remote != 0 && Sftp.Stamp(File.GetLastWriteTimeUtc(localPath)) > remote + 120;
    }

    private static bool ConfirmOverwriteLocal(string remotePath) =>
        Win32.MessageBox(Npp.Handle,
            $"La copia local de {remotePath} tiene cambios que no se subieron al servidor.\n\n" +
            "¿Descargar de nuevo el fichero del servidor?\n\n" +
            "Sí: se pierden los cambios locales.\nNo: se abre la copia local tal cual (podrás subirla al guardar).",
            Name, Win32.MB_YESNO | Win32.MB_ICONWARNING | Win32.MB_DEFBUTTON2) == Win32.IDYES;

    // --- Guardar / subir ---

    public static void OnFileSaved(nuint bufferId)
    {
        // Con un diálogo abierto no se puede abrir otro (estado estático); el fichero queda
        // pendiente y se sube con "Subir fichero actual" o en el siguiente guardado.
        if (_suppressAutoUpload || DialogHost.IsOpen)
            return;
        string? path = Npp.GetBufferPath(bufferId);
        if (path != null && Config.TryGetLink(path, out var link))
            Upload(path, link);
    }

    public static void UploadCurrent()
    {
        string? path = Npp.GetBufferPath(Npp.CurrentBufferId);
        if (path == null || !Config.TryGetLink(path, out var link))
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
        Upload(path, link);
    }

    private static void Upload(string localPath, FileLink link)
    {
        var request = WithKnownSecrets(link.Target);
        long expected = link.RemoteStamp;
        bool force = false;
        bool ask = !HasCredentials(request);
        while (true)
        {
            if (ask)
            {
                var r = ConnectDialog.Show(Npp.Handle, "Subir " + Path.GetFileName(localPath) + " al servidor",
                    "Subir", request, options: null);
                if (r is null)
                {
                    GiveUpUpload(localPath, "subida cancelada");
                    return;
                }
                request = r with { Sudo = request.Sudo };
                ask = false;
            }

            try
            {
                string title = $"Subiendo {request.RemotePath}{(request.Sudo ? " (sudo)" : "")}";
                var req = request; // copia estable para el hilo de trabajo
                bool f = force;
                long stamp = ProgressDialog.Run(Npp.Handle, title, p => req.Sudo
                    ? Sudo.Upload(req, localPath, p, expected, f)
                    : Sftp.Upload(req, localPath, p, expected, f));

                PendingUpload.Remove(localPath);
                RememberSecrets(request);
                Config.MapFile(localPath, request, stamp);
                Npp.SetStatus($"SFTP: subido {request.Display}{(request.Sudo ? " (sudo)" : "")} ({DateTime.Now:HH:mm:ss})");
                return;
            }
            catch (SftpPermissionDeniedException) when (!request.Sudo && AskSudo(request, "escribir en"))
            {
                request = request with { Sudo = true };
            }
            catch (RemoteChangedException e)
            {
                if (ConfirmOverwriteRemote(request, e.Current))
                {
                    force = true;
                    continue;
                }
                GiveUpUpload(localPath, "no se ha subido");
                return;
            }
            catch (OperationCanceledException)
            {
                GiveUpUpload(localPath, "subida cancelada");
                return;
            }
            catch (Exception ex)
            {
                // Con credenciales malas, "Reintentar" vuelve a pedirlas.
                ask = ForgetSecretsOnAuthError(request, ex);
                int answer = Win32.MessageBox(Npp.Handle,
                    $"No se pudo subir a {request.Display}\n\n{Describe(ex)}\n\nLos cambios están guardados en la copia local:\n{localPath}",
                    Name, Win32.MB_RETRYCANCEL | Win32.MB_ICONERROR);
                if (answer == Win32.IDRETRY)
                    continue;
                GiveUpUpload(localPath, "no se ha subido");
                return;
            }
        }
    }

    private static void GiveUpUpload(string localPath, string reason)
    {
        PendingUpload.Add(localPath);
        Npp.SetStatus($"SFTP: {reason}, los cambios solo están en la copia local");
    }

    private static bool ConfirmOverwriteRemote(ConnectionRequest r, long current)
    {
        string text = current == 0
            ? $"{r.RemotePath} ya no existe en el servidor.\n\n¿Crearlo de nuevo con tu copia?"
            : $"{r.RemotePath} ha cambiado en el servidor desde que lo abriste\n(modificado el {Sftp.LocalTime(current):dd/MM/yyyy HH:mm:ss}).\n\n" +
              "Si lo subes se perderán esos cambios.\n\n¿Sobrescribir el fichero del servidor?\n\n" +
              "No: tus cambios quedan en la copia local; con Ctrl+Alt+O puedes ver la versión del servidor.";
        return Win32.MessageBox(Npp.Handle, text, Name, Win32.MB_YESNO | Win32.MB_ICONWARNING | Win32.MB_DEFBUTTON2) == Win32.IDYES;
    }

    // --- Arranque de Notepad++ ---

    /// <summary>
    /// Notepad++ restauró la sesión anterior: para los ficheros remotos que siguen abiertos,
    /// ofrece conectar (una vez por cuenta) y comprobar si cambiaron en el servidor.
    /// </summary>
    public static void OnReady()
    {
        if (Config.GetFlag("panel", false))
            RemotePanel.Toggle(show: true);

        var links = new List<(string LocalPath, FileLink Link)>();
        foreach (nuint id in Npp.OpenBufferIds())
        {
            string? path = Npp.GetBufferPath(id);
            if (path != null && Config.TryGetLink(path, out var link))
                links.Add((path, link));
        }

        foreach (var group in links.GroupBy(l => l.Link.Target.Account))
        {
            var files = group.ToList();
            var request = WithKnownSecrets(files[0].Link.Target);
            string list = string.Join("\n", files.Select(f => "  " + f.Link.Target.RemotePath));
            int answer = Win32.MessageBox(Npp.Handle,
                $"Hay {files.Count} fichero(s) remoto(s) de la sesión anterior en {group.Key}:\n\n{list}\n\n" +
                "¿Conectar ahora para comprobar si han cambiado en el servidor?\n\nSi no, se pedirá la clave al guardar.",
                Name, Win32.MB_YESNO | Win32.MB_ICONQUESTION);
            if (answer != Win32.IDYES)
                continue;

            if (!HasCredentials(request))
            {
                var r = ConnectDialog.Show(Npp.Handle, "Conectar con " + group.Key, "Conectar", request, options: null);
                if (r is null)
                    continue;
                request = r;
            }

            try
            {
                using var client = ConnectInteractive(request);
                if (client is null)
                    continue;
                RememberSecrets(request);
                int unchanged = 0;
                foreach (var (localPath, link) in files)
                    if (!RefreshIfChanged(client, localPath, link))
                        unchanged++;
                Npp.SetStatus($"SFTP: {group.Key} comprobado, {unchanged} de {files.Count} fichero(s) sin cambios en el servidor");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ForgetSecretsOnAuthError(request, ex);
                ShowError("No se pudo comprobar " + group.Key, ex);
            }
        }
    }

    /// <summary>Si el fichero remoto cambió desde la última sincronización, ofrece descargarlo y recargarlo. True si cambió.</summary>
    private static bool RefreshIfChanged(SftpClient client, string localPath, FileLink link)
    {
        long current = Sftp.GetStamp(client, link.Target.RemotePath);
        if (link.RemoteStamp == 0 || current == 0 || current == link.RemoteStamp)
            return false;

        int answer = Win32.MessageBox(Npp.Handle,
            $"{link.Target.RemotePath} ha cambiado en el servidor\n(modificado el {Sftp.LocalTime(current):dd/MM/yyyy HH:mm:ss}).\n\n" +
            "¿Descargar la nueva versión? La copia local abierta en Notepad++ se sustituirá.",
            Name, Win32.MB_YESNO | Win32.MB_ICONQUESTION);
        if (answer != Win32.IDYES)
            return true;

        try
        {
            var (target, stamp) = Download(client, link.Target, localPath);
            Config.MapFile(localPath, target, stamp);
            PendingUpload.Remove(localPath);
            Npp.ReloadFile(localPath);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowError("No se pudo descargar " + link.Target.RemotePath, ex);
        }
        return true;
    }

    // --- Credenciales en memoria ---

    internal static void RememberSecrets(ConnectionRequest r) =>
        Secrets[r.Account] = new Secret(r.Password, r.KeyFile, r.Passphrase);

    /// <summary>Completa la petición con las últimas credenciales que funcionaron para esa cuenta.</summary>
    internal static ConnectionRequest WithKnownSecrets(ConnectionRequest r) =>
        Secrets.TryGetValue(r.Account, out var s)
            ? r with { Password = s.Password, KeyFile = s.KeyFile, Passphrase = s.Passphrase }
            : r;

    /// <summary>Se puede conectar sin preguntar: credenciales de esta sesión, o clave privada sin frase.</summary>
    internal static bool HasCredentials(ConnectionRequest r) =>
        Secrets.ContainsKey(r.Account) ||
        (r.KeyFile.Length > 0 && File.Exists(r.KeyFile) && !Sftp.KeyNeedsPassphrase(r.KeyFile));

    /// <summary>Si el error es de credenciales, las olvida para volver a pedirlas. Devuelve si lo era.</summary>
    internal static bool ForgetSecretsOnAuthError(ConnectionRequest r, Exception ex)
    {
        bool credentials = ex is SshAuthenticationException or SshPassPhraseNullOrEmptyException
            || (ex is FileNotFoundException f && f.FileName == r.KeyFile)
            || ex is KeyLoadException
            || ex is SudoException { BadPassword: true };
        if (!credentials)
            return false;
        Secrets.Remove(r.Account);
        return true;
    }

    // --- Varios ---

    public static void About()
    {
        Win32.MessageBox(Npp.Handle,
            Name + " " + Version + "\n\n" +
            "Abre ficheros de un servidor Linux por SSH (SFTP) con usuario y contraseña o clave privada.\n\n" +
            "• Al conectar se muestran las carpetas y ficheros para navegar hasta el fichero:\n" +
            "   doble clic o Intro para entrar o abrir, Retroceso para subir de carpeta.\n" +
            "• También hay un panel acoplable con el árbol de carpetas (Plugins → SshOpen → Panel remoto).\n" +
            "• El fichero se descarga a una copia en %TEMP%\\SshOpen.\n" +
            "• Al guardar (Ctrl+S) se sube automáticamente al servidor; si el fichero cambió\n" +
            "   en el servidor desde que lo abriste, avisa antes de sobrescribirlo.\n" +
            "• Si el usuario no tiene permiso sobre el fichero, ofrece leerlo y escribirlo con sudo.\n" +
            "• Los perfiles guardan servidor, puerto, usuario, carpeta y ruta de la clave privada.\n" +
            "• La contraseña y la frase de la clave solo se guardan en memoria mientras Notepad++ está abierto.\n" +
            "• La huella del servidor se pide la primera vez y se recuerda.",
            Name, Win32.MB_ICONINFORMATION);
    }

    private static string Version =>
        typeof(Plugin).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

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

    internal static string Describe(Exception ex) => ex switch
    {
        SshAuthenticationException => "El servidor rechazó el usuario, la contraseña o la clave privada.",
        SshPassPhraseNullOrEmptyException => "La clave privada está protegida: escribe su frase.",
        FileNotFoundException => ex.Message,
        SftpPathNotFoundException => "La ruta remota no existe.",
        SftpPermissionDeniedException => "Permiso denegado en el servidor.",
        SshOperationTimeoutException => "Tiempo de espera agotado.",
        UntrustedHostKeyException => "La huella del servidor no está aceptada o ha cambiado: ábrelo con Ctrl+Alt+O para revisarla.",
        SocketException se => "No se puede conectar con el servidor: " + se.Message,
        SshConnectionException sc => "Error de conexión SSH: " + sc.Message,
        KeyLoadException => ex.Message,
        _ => ex.Message,
    };
}
