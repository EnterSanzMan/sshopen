namespace SshOpen;

/// <summary>Copia local enlazada a un fichero remoto y fecha remota (segundos Unix, 0 = desconocida) de la última sincronización.</summary>
internal sealed record FileLink(ConnectionRequest Target, long RemoteStamp);

/// <summary>Conexión guardada con nombre (nunca incluye la contraseña ni la frase de la clave).</summary>
internal sealed record Profile(string Name, string Host, int Port, string User, string RemotePath, string KeyFile);

/// <summary>
/// Ficheros del plugin en la carpeta de configuración de plugins de Notepad++:
///   SshOpen.ini          última conexión (sin secretos), último perfil y opciones (ocultos, panel)
///   SshOpen.profiles     perfiles, "nombre TAB host TAB puerto TAB usuario TAB carpeta TAB clave privada"
///   SshOpen.known_hosts  huellas aceptadas, "host:puerto SHA256:..."
///   SshOpen.files        copia local -> fichero remoto, para subir al guardar
/// </summary>
internal static class Config
{
    private static string? _dir;
    private static Dictionary<string, FileLink>? _files;

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
        var request = new ConnectionRequest(Get("host"), port, Get("user"), "", Get("path")) { KeyFile = Get("key") };
        return (request, Get("remember") != "0", Get("profile"));
    }

    public static void SaveLast(ConnectionRequest r, bool remember, string profile)
    {
        var ini = ReadPairs(IniPath, '=');
        foreach (var key in new[] { "profile", "host", "port", "user", "path", "key" })
            ini.Remove(key);
        ini["remember"] = remember ? "1" : "0";
        if (remember)
        {
            ini["profile"] = profile;
            ini["host"] = r.Host;
            ini["port"] = r.Port.ToString();
            ini["user"] = r.User;
            ini["path"] = r.RemotePath;
            ini["key"] = r.KeyFile;
        }
        Write(IniPath, ini.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    // --- Opciones sueltas del .ini ---

    public static bool GetFlag(string key, bool defaultValue) =>
        ReadPairs(IniPath, '=').TryGetValue(key, out var v) ? v == "1" : defaultValue;

    public static void SetFlag(string key, bool value)
    {
        var ini = ReadPairs(IniPath, '=');
        ini[key] = value ? "1" : "0";
        Write(IniPath, ini.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    // --- Perfiles ---

    public static List<Profile> LoadProfiles()
    {
        var profiles = new List<Profile>();
        if (!File.Exists(ProfilesPath))
            return profiles;
        foreach (var line in File.ReadAllLines(ProfilesPath))
        {
            // Formato anterior: 5 columnas (sin clave privada).
            var f = line.Split('\t');
            if (f.Length is 5 or 6 && f[0].Length > 0 && int.TryParse(f[2], out int port))
                profiles.Add(new Profile(f[0], f[1], port, f[3], f[4], f.Length == 6 ? f[5] : ""));
        }
        return profiles;
    }

    public static void SaveProfiles(IEnumerable<Profile> profiles) =>
        Write(ProfilesPath, profiles
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => string.Join('\t', p.Name, p.Host, p.Port, p.User, p.RemotePath, p.KeyFile)));

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

    public static bool TryGetLink(string localPath, out FileLink link) =>
        Files.TryGetValue(localPath, out link!);

    public static void MapFile(string localPath, ConnectionRequest r, long remoteStamp)
    {
        Files[localPath] = new FileLink(r.WithoutPassword(), remoteStamp);
        Write(FilesPath, Files.Where(kv => File.Exists(kv.Key)).Select(kv =>
        {
            var t = kv.Value.Target;
            return string.Join('\t', kv.Key, t.Host, t.Port, t.User, t.RemotePath, t.Sudo ? 1 : 0, kv.Value.RemoteStamp, t.KeyFile);
        }));
    }

    private static Dictionary<string, FileLink> Files => _files ??= LoadFiles();

    private static Dictionary<string, FileLink> LoadFiles()
    {
        var map = new Dictionary<string, FileLink>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(FilesPath))
            return map;
        foreach (var line in File.ReadAllLines(FilesPath))
        {
            // Formatos anteriores: 5 columnas (sin sudo), 6 (sin fecha remota) y 7 (sin clave privada).
            var f = line.Split('\t');
            if (f.Length is < 5 or > 8 || !int.TryParse(f[2], out int port))
                continue;
            var target = new ConnectionRequest(f[1], port, f[3], "", f[4])
            {
                Sudo = f.Length >= 6 && f[5] == "1",
                KeyFile = f.Length == 8 ? f[7] : "",
            };
            long stamp = f.Length >= 7 && long.TryParse(f[6], out long s) ? s : 0;
            map[f[0]] = new FileLink(target, stamp);
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
