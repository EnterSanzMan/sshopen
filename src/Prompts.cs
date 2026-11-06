using System.Net.Sockets;
using Renci.SshNet.Common;
using static SshOpen.Win32;

namespace SshOpen;

/// <summary>Todas las preguntas y avisos al usuario (MessageBox) y los textos de error. Los textos están en <see cref="Tr"/>.</summary>
internal static class Prompts
{
    // Cuentas para las que el usuario ya aceptó usar sudo en esta sesión.
    private static readonly HashSet<string> SudoAccounts = new(StringComparer.Ordinal);

    // --- Conexión ---

    public static bool TrustHost(ConnectionRequest r, UntrustedHostKeyException e) => e.Known == null
        ? YesNo(Tr.TrustNewHost(r.Host, r.Port, e.Fingerprint), MB_ICONQUESTION)
        : YesNo(Tr.TrustChangedHost(r.Host, r.Port, e.Known, e.Fingerprint), MB_ICONWARNING | MB_DEFBUTTON2);

    /// <summary>Sin permiso para leer o escribir: ¿usar sudo? Se pregunta una vez por cuenta y sesión.</summary>
    public static bool UseSudo(ConnectionRequest r, bool write)
    {
        if (SudoAccounts.Contains(r.Account))
            return true;
        if (!YesNo(Tr.UseSudo(r.User, write, r.RemotePath, r.Account), MB_ICONQUESTION))
            return false;
        SudoAccounts.Add(r.Account);
        return true;
    }

    // --- Ficheros ---

    public static bool OverwriteLocalCopy(string remotePath) =>
        YesNo(Tr.OverwriteLocalCopy(remotePath), MB_ICONWARNING | MB_DEFBUTTON2);

    public static bool OverwriteRemote(ConnectionRequest r, long current) =>
        YesNo(current == 0 ? Tr.RemoteGone(r.RemotePath) : Tr.RemoteChanged(r.RemotePath, Sftp.LocalTime(current)),
            MB_ICONWARNING | MB_DEFBUTTON2);

    public static bool DownloadNewerVersion(string remotePath, long current) =>
        YesNo(Tr.DownloadNewerVersion(remotePath, Sftp.LocalTime(current)), MB_ICONQUESTION);

    public static bool CheckSessionFiles(string account, IReadOnlyCollection<string> remotePaths) =>
        YesNo(Tr.CheckSessionFiles(account, remotePaths), MB_ICONQUESTION);

    /// <summary>La subida falló; true si el usuario quiere reintentar.</summary>
    public static bool RetryUpload(ConnectionRequest r, Exception ex, string localPath) =>
        Message(Npp.Handle, Tr.RetryUpload(r.Display, Describe(ex), localPath), MB_RETRYCANCEL | MB_ICONERROR) == IDRETRY;

    // --- Avisos ---

    public static void Info(string text) => Message(Npp.Handle, text, MB_ICONINFORMATION);

    public static void Warning(string text) => Message(Npp.Handle, text, MB_ICONWARNING);

    public static void Error(string title, Exception ex) => Error(Npp.Handle, title, ex);

    /// <summary>
    /// Desde dentro de un diálogo modal el propietario debe ser ese diálogo: MessageBox vuelve a
    /// habilitar a su propietario al cerrarse, y si fuera Notepad++ quedaría activo bajo el modal.
    /// </summary>
    public static void Error(HWND owner, string title, Exception ex) =>
        Message(owner, $"{title}\n\n{Describe(ex)}", MB_ICONERROR);

    public static string Describe(Exception ex) => ex switch
    {
        SshAuthenticationException => Tr.AuthRejected,
        SshPassPhraseNullOrEmptyException => Tr.PassphraseRequired,
        SftpPathNotFoundException => Tr.PathNotFound,
        SftpPermissionDeniedException => Tr.PermissionDenied,
        SshOperationTimeoutException => Tr.Timeout,
        UntrustedHostKeyException => Tr.HostKeyRejected,
        SocketException se => Tr.CannotReachServer(se.Message),
        SshConnectionException sc => Tr.SshError(sc.Message),
        _ => ex.Message, // incluye KeyLoadException, FileNotFoundException y SudoException, ya redactadas
    };

    public static void About() => Info(Tr.About(Version));

    private static string Version =>
        typeof(Prompts).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    private static bool YesNo(string text, uint icon) => Message(Npp.Handle, text, MB_YESNO | icon) == IDYES;
}
