namespace SshOpen;

/// <summary>Conexión guardada con nombre (nunca incluye la clave).</summary>
internal sealed record Profile(string Name, string Host, int Port, string User, string RemotePath)
{
    public ConnectionRequest ToRequest() => new(Host, Port, User, "", RemotePath);
}

/// <summary>
/// Ficheros del plugin en la carpeta de configuración de plugins de Notepad++:
///   SshOpen.ini          última conexión (sin clave) y último perfil usado
///   SshOpen.profiles     perfiles de servidor, "nombre TAB host TAB puerto TAB usuario TAB carpeta"
///   SshOpen.known_hosts  huellas aceptadas, "host:puerto SHA256:..."
///   SshOpen.files        copia local -> fichero remoto, para subir al guardar
/// </summary>
internal static class Config
{
    private static string? _dir;
    private static Dictionary<string, ConnectionRequest>? _files;

    private static string Dir => _dir ??= Npp.GetPluginConfigDir();
    private static string IniPath => Path.Combine(Dir, "SshOpen.ini");
    private static string ProfilesPath => Path.Combine(Dir, "SshOpen.profiles");
    private static string KnownHostsPath => Path.Combine(Dir, "SshOpen.known_hosts");
    private static string FilesPath => Path.Combine(Dir, "SshOpen.files");

    // --- Última conexión ---

    public static (ConnectionRequest Request, bool Remember, string Profile) LoadLast()
    {
        var ini = ReadPairs(IniPath, '=');
        string Get(string key) => ini.GetValueOrDefault(key, "");
        int port = int.TryParse(Get("port"), out int p) ? p : 22;
        return (new ConnectionRequest(Get("host"), port, Get("user"), "", Get("path")), Get("remember") != "0", Get("profile"));
    }

    public static void SaveLast(ConnectionRequest r, bool remember, string profile)
    {
        var lines = remember
            ? new[] { "remember=1", $"profile={profile}", $"host={r.Host}", $"port={r.Port}", $"user={r.User}", $"path={r.RemotePath}" }
            : new[] { "remember=0" };
        Write(IniPath, lines);
    }

    // --- Perfiles ---

    public static List<Profile> LoadProfiles()
    {
        var profiles = new List<Profile>();
        if (!File.Exists(ProfilesPath))
            return profiles;
        foreach (var line in File.ReadAllLines(ProfilesPath))
        {
            var f = line.Split('\t');
            if (f.Length == 5 && f[0].Length > 0 && int.TryParse(f[2], out int port))
                profiles.Add(new Profile(f[0], f[1], port, f[3], f[4]));
        }
        return profiles;
    }

    public static void SaveProfiles(IEnumerable<Profile> profiles) =>
        Write(ProfilesPath, profiles
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => string.Join('\t', p.Name, p.Host, p.Port, p.User, p.RemotePath)));

    // --- Huellas de servidores ---

    public static string? KnownHost(string host, int port) =>
        ReadPairs(KnownHostsPath, ' ').GetValueOrDefault($"{host}:{port}");

    public static void TrustHost(string host, int port, string fingerprint)
    {
        var hosts = ReadPairs(KnownHostsPath, ' ');
        hosts[$"{host}:{port}"] = fingerprint;
        Write(KnownHostsPath, hosts.Select(kv => $"{kv.Key} {kv.Value}"));
    }

    // --- Ficheros abiertos ---

    public static bool TryGetTarget(string localPath, out ConnectionRequest target) =>
        Files.TryGetValue(localPath, out target!);

    public static void MapFile(string localPath, ConnectionRequest r)
    {
        Files[localPath] = r.WithoutPassword();
        Write(FilesPath, Files.Where(kv => File.Exists(kv.Key))
            .Select(kv => string.Join('\t', kv.Key, kv.Value.Host, kv.Value.Port, kv.Value.User, kv.Value.RemotePath, kv.Value.Sudo ? 1 : 0)));
    }

    private static Dictionary<string, ConnectionRequest> Files => _files ??= LoadFiles();

    private static Dictionary<string, ConnectionRequest> LoadFiles()
    {
        var map = new Dictionary<string, ConnectionRequest>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(FilesPath))
            return map;
        foreach (var line in File.ReadAllLines(FilesPath))
        {
            var f = line.Split('\t');
            if (f.Length is 5 or 6 && int.TryParse(f[2], out int port))
                map[f[0]] = new ConnectionRequest(f[1], port, f[3], "", f[4]) { Sudo = f.Length == 6 && f[5] == "1" };
        }
        return map;
    }

    // --- Utilidades ---

    private static Dictionary<string, string> ReadPairs(string path, char separator)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return map;
        foreach (var line in File.ReadAllLines(path))
        {
            int i = line.IndexOf(separator);
            if (i > 0)
                map[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return map;
    }

    private static void Write(string path, IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(path, lines);
    }
}
