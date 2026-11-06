namespace SshOpen;

/// <summary>Copias locales de los ficheros remotos en %TEMP%\SshOpen y su estado de sincronización.</summary>
internal static class LocalCopies
{
    // Copias cuya última subida falló o se canceló.
    private static readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase);

    // Margen para el desfase entre el reloj del equipo y el del servidor.
    private const long ClockSkewSeconds = 120;

    public static void MarkPending(string localPath) => Pending.Add(localPath);

    public static void MarkSynced(string localPath) => Pending.Remove(localPath);

    /// <summary>
    /// La copia local tiene cambios que no llegaron al servidor: lo sabemos de esta sesión, o la
    /// copia es claramente más reciente que el fichero remoto (<paramref name="remoteStamp"/>).
    /// </summary>
    public static bool HasUnsyncedChanges(string localPath, long remoteStamp)
    {
        if (!File.Exists(localPath))
            return false;
        if (Pending.Contains(localPath))
            return true;
        return remoteStamp != 0 && Sftp.Stamp(File.GetLastWriteTimeUtc(localPath)) > remoteStamp + ClockSkewSeconds;
    }

    /// <summary>
    /// %TEMP%\SshOpen\usuario@host_puerto\ruta\remota (sin "..", para no salir de la carpeta).
    /// Con la forma larga de %TEMP%: Notepad++ devuelve las rutas sin nombres 8.3 y la clave
    /// del mapa de ficheros debe coincidir con ellas.
    /// </summary>
    public static string PathFor(ConnectionRequest r)
    {
        var parts = new List<string> { Win32.LongPath(Path.GetTempPath()), Plugin.Name, Sanitize($"{r.User}@{r.Host}_{r.Port}") };
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
}
