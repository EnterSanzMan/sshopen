using Renci.SshNet;
using Renci.SshNet.Common;

namespace SshOpen;

internal sealed record ConnectionRequest(string Host, int Port, string User, string Password, string RemotePath)
{
    /// <summary>Identifica la cuenta en el servidor (para reutilizar la clave durante la sesión).</summary>
    public string Account => $"{User}@{Host}:{Port}";

    public string Display => $"{User}@{Host}:{RemotePath}";

    /// <summary>SFTP no expande "~": las rutas relativas ya parten del home del usuario.</summary>
    public string SftpPath => RemotePath is "" or "~" ? "." : RemotePath.StartsWith("~/") ? RemotePath[2..] : RemotePath;

    public ConnectionRequest WithoutPassword() => this with { Password = "" };

    // El ToString generado por el record incluiría la clave.
    public override string ToString() => Display;
}

/// <summary>Descarga y subida de ficheros por SFTP (subsistema SSH; el servidor no necesita scp).</summary>
internal static class Sftp
{
    public static void Download(SftpClient client, string remotePath, string localPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        string partial = localPath + ".part";
        try
        {
            using (var file = File.Create(partial))
                client.DownloadFile(remotePath, file);
            File.Move(partial, localPath, overwrite: true);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }

    /// <summary>Ruta absoluta y normalizada; las relativas (y "~") parten del home del usuario.</summary>
    public static string ResolvePath(SftpClient client, string path)
    {
        path = path.Replace('\\', '/');
        if (path is "" or "." or "~")
            path = client.WorkingDirectory;
        else if (path.StartsWith("~/"))
            path = client.WorkingDirectory + path[1..];
        else if (!path.StartsWith('/'))
            path = client.WorkingDirectory + "/" + path;

        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
            }
            else if (part != ".")
            {
                parts.Add(part);
            }
        }
        return "/" + string.Join('/', parts);
    }

    public static string Parent(string folder)
    {
        int i = folder.TrimEnd('/').LastIndexOf('/');
        return i <= 0 ? "/" : folder[..i];
    }

    public static void Upload(ConnectionRequest r, string localPath)
    {
        using var client = Connect(r);
        using var file = File.OpenRead(localPath);
        // Sobrescribe el fichero existente (mismo inodo): conserva propietario y permisos.
        client.UploadFile(file, r.SftpPath, canOverride: true);
    }

    /// <summary>
    /// Conecta comprobando la huella del servidor (trust-on-first-use, como known_hosts).
    /// La pregunta al usuario se hace fuera del evento HostKeyReceived, que llega desde un
    /// hilo de SSH.NET mientras el hilo de la interfaz está bloqueado en Connect().
    /// </summary>
    public static SftpClient Connect(ConnectionRequest r)
    {
        while (true)
        {
            string? fingerprint = null;
            var client = new SftpClient(CreateConnectionInfo(r))
            {
                // Todo corre en el hilo de la interfaz: sin estos límites una conexión caída
                // (VPN, servidor colgado) dejaría Notepad++ bloqueado indefinidamente.
                OperationTimeout = TimeSpan.FromSeconds(60),
                KeepAliveInterval = TimeSpan.FromSeconds(15),
            };
            client.HostKeyReceived += (_, e) =>
            {
                fingerprint = "SHA256:" + e.FingerPrintSHA256;
                e.CanTrust = Config.KnownHost(r.Host, r.Port) == fingerprint;
            };
            try
            {
                client.Connect();
                return client;
            }
            catch (SshConnectionException) when (fingerprint != null && Config.KnownHost(r.Host, r.Port) != fingerprint)
            {
                client.Dispose();
                if (!AskTrust(r, fingerprint))
                    throw new OperationCanceledException();
                Config.TrustHost(r.Host, r.Port, fingerprint);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    private static ConnectionInfo CreateConnectionInfo(ConnectionRequest r)
    {
        // Muchos servidores solo admiten "keyboard-interactive" para claves: se ofrecen ambos métodos.
        var keyboard = new KeyboardInteractiveAuthenticationMethod(r.User);
        keyboard.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
                prompt.Response = r.Password;
        };
        return new ConnectionInfo(r.Host, r.Port, r.User, new PasswordAuthenticationMethod(r.User, r.Password), keyboard)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    private static bool AskTrust(ConnectionRequest r, string fingerprint)
    {
        string? known = Config.KnownHost(r.Host, r.Port);
        string text = known == null
            ? $"Es la primera vez que te conectas a {r.Host}:{r.Port}.\n\nHuella del servidor:\n{fingerprint}\n\n¿Confiar en este servidor?"
            : $"¡ATENCIÓN! La huella de {r.Host}:{r.Port} ha CAMBIADO.\n\nGuardada:\n{known}\n\nRecibida:\n{fingerprint}\n\n" +
              "Puede ser una reinstalación del servidor o un ataque man-in-the-middle.\n¿Confiar en la nueva huella?";
        uint flags = Win32.MB_YESNO | (known == null ? Win32.MB_ICONQUESTION : Win32.MB_ICONWARNING | Win32.MB_DEFBUTTON2);
        return Win32.MessageBox(Npp.Handle, text, Plugin.Name, flags) == Win32.IDYES;
    }
}
