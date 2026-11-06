using Renci.SshNet;
using Renci.SshNet.Common;

namespace SshOpen;

internal sealed record ConnectionRequest(string Host, int Port, string User, string Password, string RemotePath)
{
    /// <summary>El fichero se lee y escribe con sudo (el usuario no tiene permiso directo).</summary>
    public bool Sudo { get; init; }

    /// <summary>Fichero local de clave privada (OpenSSH, PEM o PuTTY .ppk); vacío = solo contraseña.</summary>
    public string KeyFile { get; init; } = "";

    /// <summary>Frase de la clave privada, si está protegida (solo en memoria, como la contraseña).</summary>
    public string Passphrase { get; init; } = "";

    /// <summary>Identifica la cuenta en el servidor (para reutilizar la clave durante la sesión).</summary>
    public string Account => $"{User}@{Host}:{Port}";

    public string Display => $"{User}@{Host}:{RemotePath}";

    /// <summary>SFTP no expande "~": las rutas relativas ya parten del home del usuario.</summary>
    public string SftpPath => RemotePath is "" or "~" ? "." : RemotePath.StartsWith("~/") ? RemotePath[2..] : RemotePath;

    public ConnectionRequest WithoutPassword() => this with { Password = "", Passphrase = "" };

    // El ToString generado por el record incluiría la clave.
    public override string ToString() => Display;
}

/// <summary>El servidor presentó una huella que no está aceptada (primera conexión o cambio).</summary>
internal sealed class UntrustedHostKeyException(string fingerprint, string? known)
    : Exception("La huella del servidor no está aceptada.")
{
    public string Fingerprint => fingerprint;
    public string? Known => known;
}

/// <summary>El fichero remoto cambió (o desapareció: <see cref="Current"/> = 0) desde la última sincronización.</summary>
internal sealed class RemoteChangedException(long current) : Exception("El fichero remoto ha cambiado en el servidor.")
{
    /// <summary>Fecha de modificación actual en segundos Unix; 0 si ya no existe.</summary>
    public long Current => current;
}

/// <summary>No se pudo leer la clave privada (frase incorrecta, formato no admitido o fichero dañado).</summary>
internal sealed class KeyLoadException(string path, bool withPassphrase, Exception inner) : Exception(
    (withPassphrase
        ? "No se pudo abrir la clave privada: la frase no es correcta o el formato no está admitido."
        : "No se pudo leer la clave privada: el formato no está admitido o el fichero está dañado.")
    + "\n" + path + "\n(" + inner.Message + ")", inner);

/// <summary>Descarga y subida de ficheros por SFTP (subsistema SSH; el servidor no necesita scp).</summary>
internal static class Sftp
{
    /// <summary>Descarga el fichero y devuelve su fecha de modificación remota (segundos Unix).</summary>
    public static long Download(SftpClient client, string remotePath, string localPath, TransferProgress progress)
    {
        var attrs = client.GetAttributes(remotePath);
        progress.Start(attrs.Size);
        WriteLocal(localPath, file => client.DownloadFile(remotePath, new ProgressStream(file, progress)));
        return Stamp(attrs.LastWriteTimeUtc);
    }

    /// <summary>
    /// Sube la copia local y devuelve la nueva fecha remota. Si <paramref name="expectedStamp"/> no
    /// es 0 y el fichero remoto ya no tiene esa fecha, lanza <see cref="RemoteChangedException"/>
    /// sin tocar nada (salvo con <paramref name="force"/>).
    /// </summary>
    public static long Upload(ConnectionRequest r, string localPath, TransferProgress progress, long expectedStamp, bool force)
    {
        using var client = Connect(r, progress.Token);
        if (!force)
            CheckUnchanged(GetStamp(client, r.SftpPath), expectedStamp);
        using var file = File.OpenRead(localPath);
        progress.Start(file.Length);
        // Sobrescribe el fichero existente (mismo inodo): conserva propietario y permisos.
        client.UploadFile(new ProgressStream(file, progress), r.SftpPath, canOverride: true);
        return GetStamp(client, r.SftpPath);
    }

    /// <summary>Fecha de modificación remota en segundos Unix; 0 si no existe o no se puede consultar.</summary>
    public static long GetStamp(SftpClient client, string path)
    {
        try
        {
            return Stamp(client.GetLastWriteTimeUtc(path));
        }
        catch (SshException)
        {
            return 0;
        }
    }

    public static long Stamp(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    public static DateTime LocalTime(long stamp) => DateTimeOffset.FromUnixTimeSeconds(stamp).LocalDateTime;

    public static void CheckUnchanged(long current, long expected)
    {
        if (expected != 0 && current != expected)
            throw new RemoteChangedException(current);
    }

    /// <summary>Escribe la copia local a través de un .part para no dejar un fichero a medias.</summary>
    public static void WriteLocal(string localPath, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        string partial = localPath + ".part";
        try
        {
            using (var file = File.Create(partial))
                write(file);
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

    /// <summary>Ruta entre comillas simples para la shell remota.</summary>
    public static string ShellQuote(string path) => "'" + path.Replace("'", "'\\''") + "'";

    // --- Conexión ---

    public static SftpClient Connect(ConnectionRequest r, CancellationToken token) => Connect(r, token, info => new SftpClient(info)
    {
        // Sin estos límites una conexión caída (VPN, servidor colgado) no terminaría nunca.
        OperationTimeout = TimeSpan.FromSeconds(60),
        KeepAliveInterval = TimeSpan.FromSeconds(15),
    });

    /// <summary>Canal de comandos (para sudo); misma comprobación de huella.</summary>
    public static SshClient ConnectSsh(ConnectionRequest r, CancellationToken token) => Connect(r, token, info => new SshClient(info)
    {
        KeepAliveInterval = TimeSpan.FromSeconds(15),
    });

    /// <summary>
    /// Un intento de conexión comprobando la huella del servidor (trust-on-first-use, como
    /// known_hosts). Si la huella no está aceptada lanza <see cref="UntrustedHostKeyException"/>
    /// para que la interfaz pregunte; esto corre en un hilo de trabajo y no puede mostrar nada.
    /// </summary>
    private static T Connect<T>(ConnectionRequest r, CancellationToken token, Func<ConnectionInfo, T> create) where T : BaseClient
    {
        string? known = Config.KnownHost(r.Host, r.Port);
        string? fingerprint = null;
        var client = create(CreateConnectionInfo(r));
        client.HostKeyReceived += (_, e) =>
        {
            fingerprint = "SHA256:" + e.FingerPrintSHA256;
            e.CanTrust = known == fingerprint;
        };
        try
        {
            client.ConnectAsync(token).GetAwaiter().GetResult();
            return client;
        }
        catch (SshConnectionException) when (fingerprint != null && known != fingerprint)
        {
            client.Dispose();
            throw new UntrustedHostKeyException(fingerprint, known);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static ConnectionInfo CreateConnectionInfo(ConnectionRequest r)
    {
        var methods = new List<AuthenticationMethod>();
        if (r.KeyFile.Length > 0)
            methods.Add(new PrivateKeyAuthenticationMethod(r.User, LoadKey(r.KeyFile, r.Passphrase)));

        // Con clave privada la contraseña es opcional (puede hacer falta para sudo o como segundo factor).
        if (r.KeyFile.Length == 0 || r.Password.Length > 0)
        {
            // Muchos servidores solo admiten "keyboard-interactive" para claves: se ofrecen ambos métodos.
            var keyboard = new KeyboardInteractiveAuthenticationMethod(r.User);
            keyboard.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                    prompt.Response = r.Password;
            };
            methods.Add(new PasswordAuthenticationMethod(r.User, r.Password));
            methods.Add(keyboard);
        }

        return new ConnectionInfo(r.Host, r.Port, r.User, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    // Claves ya descifradas, para no cargar (y dejar sin liberar) una por conexión. Se
    // invalidan si cambia el fichero; la frase forma parte de la clave del diccionario.
    private static readonly Dictionary<(string Path, string Passphrase, DateTime Modified), PrivateKeyFile> Keys = [];
    private static readonly Lock KeysLock = new();

    private static PrivateKeyFile LoadKey(string path, string passphrase)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("No se encuentra el fichero de clave privada:\n" + path, path);
        var id = (path, passphrase, File.GetLastWriteTimeUtc(path));
        lock (KeysLock)
        {
            if (Keys.TryGetValue(id, out var cached))
                return cached;
            PrivateKeyFile key;
            try
            {
                key = passphrase.Length > 0 ? new PrivateKeyFile(path, passphrase) : new PrivateKeyFile(path);
            }
            catch (SshPassPhraseNullOrEmptyException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Frase incorrecta o formato no admitido: SSH.NET da excepciones distintas según el formato.
                throw new KeyLoadException(path, passphrase.Length > 0, ex);
            }
            Keys[id] = key;
            return key;
        }
    }

    /// <summary>La clave privada está cifrada y no se puede usar sin frase.</summary>
    public static bool KeyNeedsPassphrase(string path)
    {
        try
        {
            using var _ = new PrivateKeyFile(path);
            return false;
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            return true;
        }
        catch
        {
            return false; // otros errores (formato, fichero) se informan al conectar
        }
    }
}
