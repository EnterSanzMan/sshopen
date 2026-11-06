using Renci.SshNet;
using Renci.SshNet.Common;

namespace SshOpen;

/// <summary>
/// Flujo del plugin: se descarga el fichero remoto a una copia en %TEMP%, se abre en
/// Notepad++ y cada vez que se guarda esa copia se vuelve a subir al servidor.
/// Las preguntas al usuario están en <see cref="Prompts"/>, las credenciales en
/// <see cref="Credentials"/> y la conexión interactiva en <see cref="Connector"/>.
/// </summary>
internal static class Plugin
{
    public const string Name = "SshOpen";

    private static bool _suppressAutoUpload;

    // --- Abrir ---

    public static void OpenRemote()
    {
        var (last, remember, profile) = Config.LoadLast();
        var options = new ConnectOptions { Remember = remember, Profile = profile };
        var request = last;

        while (true)
        {
            if (Connector.ConnectWithDialog(Tr.OpenRemoteTitle, request, options) is not { } connection)
                return;
            request = connection.Request;
            using var client = connection.Client;
            try
            {
                // Siempre se navega con el explorador. Si la ruta es un fichero, se empieza en su
                // carpeta con él seleccionado; si no existe, el explorador vuelve al home.
                string folder = Connector.StartFolder(client, request.RemotePath, out string? select);
                string? path = BrowseDialog.Show(Npp.Handle, client, folder, select,
                    Tr.BrowseTitle($"{request.User}@{request.Host}"), out string lastFolder);
                Config.SaveLast(request with { RemotePath = lastFolder }, options.Remember, options.Profile);
                if (path is null)
                    return;

                OpenFile(client, request, path);
                return;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Credentials.ForgetIfRejected(request, ex);
                Prompts.Error(Tr.CannotOpen(request.Display), ex);
            }
        }
    }

    /// <summary>
    /// Descarga el fichero remoto a su copia local (salvo que la copia tenga cambios sin subir y
    /// el usuario prefiera conservarlos), la enlaza para subirla al guardar y la abre.
    /// </summary>
    public static void OpenFile(SftpClient client, ConnectionRequest request, string remotePath)
    {
        var target = request with { RemotePath = remotePath, Sudo = false };
        string localPath = LocalCopies.PathFor(target);
        long stamp = 0;
        if (Config.TryGetLink(localPath, out var link))
        {
            stamp = link.RemoteStamp;
            target = target with { Sudo = link.Target.Sudo }; // si ya hizo falta sudo, directamente con sudo
        }

        if (!LocalCopies.HasUnsyncedChanges(localPath, Sftp.GetStamp(client, remotePath)) || Prompts.OverwriteLocalCopy(remotePath))
        {
            (target, stamp) = Download(client, target, localPath);
            LocalCopies.MarkSynced(localPath);
        }

        Config.MapFile(localPath, target, stamp);
        Npp.OpenFile(localPath);
        Npp.SetStatus(Tr.StatusOpened(target.Display + SudoSuffix(target)));
    }

    /// <summary>Descarga por SFTP; sin permiso de lectura ofrece hacerlo con sudo. Devuelve el destino y la fecha remota.</summary>
    private static (ConnectionRequest Target, long Stamp) Download(SftpClient client, ConnectionRequest target, string localPath)
    {
        string title = Tr.Downloading(target.RemotePath);
        if (!target.Sudo)
        {
            try
            {
                long stamp = ProgressDialog.Run(Npp.Handle, title, p => Sftp.Download(client, target.RemotePath, localPath, p));
                return (target, stamp);
            }
            catch (SftpPermissionDeniedException) when (Prompts.UseSudo(target, write: false))
            {
            }
        }
        long sudoStamp = ProgressDialog.Run(Npp.Handle, title + " (sudo)", p => Sudo.Download(target, localPath, p));
        return (target with { Sudo = true }, sudoStamp);
    }

    // --- Guardar / subir ---

    public static void OnFileSaved(nuint bufferId)
    {
        // Con un diálogo abierto no se pregunta nada más (credenciales, conflictos); el fichero queda
        // pendiente y se sube con "Subir fichero actual" o en el siguiente guardado.
        if (_suppressAutoUpload || ModalDialog.AnyOpen)
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
            Prompts.Info(Tr.NotOpenedWithPlugin);
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
            Prompts.Warning(Tr.CouldNotSaveLocal);
            return;
        }
        Upload(path, link);
    }

    private static void Upload(string localPath, FileLink link)
    {
        var request = Credentials.Fill(link.Target);
        long expected = link.RemoteStamp;
        bool force = false;
        bool ask = !Credentials.CanConnectSilently(request);
        while (true)
        {
            if (ask)
            {
                var r = ConnectDialog.Show(Npp.Handle, Tr.UploadTitle(Path.GetFileName(localPath)),
                    Tr.UploadButton, request, options: null);
                if (r is null)
                {
                    GiveUpUpload(localPath, Tr.UploadCancelled);
                    return;
                }
                request = r with { Sudo = request.Sudo };
                ask = false;
            }

            try
            {
                var req = request; // copia estable para el hilo de trabajo
                bool f = force;
                long stamp = ProgressDialog.Run(Npp.Handle, Tr.Uploading(request.RemotePath + SudoSuffix(request)), p => req.Sudo
                    ? Sudo.Upload(req, localPath, p, expected, f)
                    : Sftp.Upload(req, localPath, p, expected, f));

                LocalCopies.MarkSynced(localPath);
                Credentials.Remember(request);
                Config.MapFile(localPath, request, stamp);
                Npp.SetStatus(Tr.StatusUploaded(request.Display + SudoSuffix(request), DateTime.Now));
                return;
            }
            catch (SftpPermissionDeniedException) when (!request.Sudo && Prompts.UseSudo(request, write: true))
            {
                request = request with { Sudo = true };
            }
            catch (RemoteChangedException e)
            {
                if (!Prompts.OverwriteRemote(request, e.Current))
                {
                    GiveUpUpload(localPath, Tr.NotUploaded);
                    return;
                }
                force = true;
            }
            catch (OperationCanceledException)
            {
                GiveUpUpload(localPath, Tr.UploadCancelled);
                return;
            }
            catch (Exception ex)
            {
                // Con credenciales malas, "Reintentar" vuelve a pedirlas.
                ask = Credentials.ForgetIfRejected(request, ex);
                if (!Prompts.RetryUpload(request, ex, localPath))
                {
                    GiveUpUpload(localPath, Tr.NotUploaded);
                    return;
                }
            }
        }
    }

    private static void GiveUpUpload(string localPath, string reason)
    {
        LocalCopies.MarkPending(localPath);
        Npp.SetStatus(Tr.StatusGaveUp(reason));
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
            if (!Prompts.CheckSessionFiles(group.Key, files.Select(f => f.Link.Target.RemotePath).ToList()))
                continue;

            var request = Credentials.Fill(files[0].Link.Target);
            var connection = Connector.ConnectWithDialog(Tr.ConnectTo(group.Key), request, options: null,
                askFirst: !Credentials.CanConnectSilently(request));
            if (connection is null)
                continue;

            using var client = connection.Value.Client;
            int unchanged = 0;
            foreach (var (localPath, link) in files)
                if (!RefreshIfChanged(client, localPath, link))
                    unchanged++;
            Npp.SetStatus(Tr.StatusChecked(group.Key, unchanged, files.Count));
        }
    }

    /// <summary>Si el fichero remoto cambió desde la última sincronización, ofrece descargarlo y recargarlo. True si cambió.</summary>
    private static bool RefreshIfChanged(SftpClient client, string localPath, FileLink link)
    {
        long current = Sftp.GetStamp(client, link.Target.RemotePath);
        if (link.RemoteStamp == 0 || current == 0 || current == link.RemoteStamp)
            return false;
        if (!Prompts.DownloadNewerVersion(link.Target.RemotePath, current))
            return true;

        try
        {
            var (target, stamp) = Download(client, link.Target, localPath);
            Config.MapFile(localPath, target, stamp);
            LocalCopies.MarkSynced(localPath);
            Npp.ReloadFile(localPath);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Prompts.Error(Tr.CannotDownload(link.Target.RemotePath), ex);
        }
        return true;
    }

    private static string SudoSuffix(ConnectionRequest r) => r.Sudo ? " (sudo)" : "";
}
