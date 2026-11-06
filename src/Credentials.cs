using Renci.SshNet.Common;

namespace SshOpen;

/// <summary>
/// Contraseñas y frases de clave que funcionaron en esta sesión de Notepad++, por cuenta.
/// Solo viven en memoria: nunca se escriben a disco.
/// </summary>
internal static class Credentials
{
    private sealed record Secret(string Password, string KeyFile, string Passphrase);

    // Ordinal: los usuarios de Linux distinguen mayúsculas.
    private static readonly Dictionary<string, Secret> Secrets = new(StringComparer.Ordinal);

    public static void Remember(ConnectionRequest r) =>
        Secrets[r.Account] = new Secret(r.Password, r.KeyFile, r.Passphrase);

    /// <summary>Completa la petición con las últimas credenciales que funcionaron para esa cuenta.</summary>
    public static ConnectionRequest Fill(ConnectionRequest r) =>
        Secrets.TryGetValue(r.Account, out var s)
            ? r with { Password = s.Password, KeyFile = s.KeyFile, Passphrase = s.Passphrase }
            : r;

    /// <summary>Se puede conectar sin preguntar: credenciales de esta sesión, o clave privada sin frase.</summary>
    public static bool CanConnectSilently(ConnectionRequest r) =>
        Secrets.ContainsKey(r.Account) ||
        (r.KeyFile.Length > 0 && File.Exists(r.KeyFile) && !Sftp.KeyNeedsPassphrase(r.KeyFile));

    /// <summary>Si el error se debe a las credenciales, las olvida para volver a pedirlas. Devuelve si era así.</summary>
    public static bool ForgetIfRejected(ConnectionRequest r, Exception ex)
    {
        bool rejected = ex is SshAuthenticationException or SshPassPhraseNullOrEmptyException or KeyLoadException
            || (ex is FileNotFoundException f && f.FileName == r.KeyFile)
            || ex is SudoException { BadPassword: true };
        if (rejected)
            Secrets.Remove(r.Account);
        return rejected;
    }
}
