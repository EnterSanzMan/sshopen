using System.Net.Sockets;
using Renci.SshNet.Common;
using static SshOpen.Win32;

namespace SshOpen;

/// <summary>Todas las preguntas y avisos al usuario (MessageBox) y los textos de error.</summary>
internal static class Prompts
{
    // Cuentas para las que el usuario ya aceptó usar sudo en esta sesión.
    private static readonly HashSet<string> SudoAccounts = new(StringComparer.Ordinal);

    // --- Conexión ---

    public static bool TrustHost(ConnectionRequest r, UntrustedHostKeyException e)
    {
        string text = e.Known == null
            ? $"Es la primera vez que te conectas a {r.Host}:{r.Port}.\n\nHuella del servidor:\n{e.Fingerprint}\n\n¿Confiar en este servidor?"
            : $"¡ATENCIÓN! La huella de {r.Host}:{r.Port} ha CAMBIADO.\n\nGuardada:\n{e.Known}\n\nRecibida:\n{e.Fingerprint}\n\n" +
              "Puede ser una reinstalación del servidor o un ataque man-in-the-middle.\n¿Confiar en la nueva huella?";
        return YesNo(text, e.Known == null ? MB_ICONQUESTION : MB_ICONWARNING | MB_DEFBUTTON2);
    }

    /// <summary>Sin permiso para leer o escribir: ¿usar sudo? Se pregunta una vez por cuenta y sesión.</summary>
    public static bool UseSudo(ConnectionRequest r, string action)
    {
        if (SudoAccounts.Contains(r.Account))
            return true;
        if (!YesNo($"{r.User} no tiene permiso para {action} {r.RemotePath}.\n\n¿Intentar con sudo (con la misma contraseña)?\n\n" +
                   $"No se volverá a preguntar para {r.Account} mientras Notepad++ siga abierto.", MB_ICONQUESTION))
            return false;
        SudoAccounts.Add(r.Account);
        return true;
    }

    // --- Ficheros ---

    public static bool OverwriteLocalCopy(string remotePath) =>
        YesNo($"La copia local de {remotePath} tiene cambios que no se subieron al servidor.\n\n" +
              "¿Descargar de nuevo el fichero del servidor?\n\n" +
              "Sí: se pierden los cambios locales.\nNo: se abre la copia local tal cual (podrás subirla al guardar).",
              MB_ICONWARNING | MB_DEFBUTTON2);

    public static bool OverwriteRemote(ConnectionRequest r, long current) =>
        YesNo(current == 0
            ? $"{r.RemotePath} ya no existe en el servidor.\n\n¿Crearlo de nuevo con tu copia?"
            : $"{r.RemotePath} ha cambiado en el servidor desde que lo abriste\n(modificado el {Sftp.LocalTime(current):dd/MM/yyyy HH:mm:ss}).\n\n" +
              "Si lo subes se perderán esos cambios.\n\n¿Sobrescribir el fichero del servidor?\n\n" +
              "No: tus cambios quedan en la copia local; con Ctrl+Alt+O puedes ver la versión del servidor.",
            MB_ICONWARNING | MB_DEFBUTTON2);

    public static bool DownloadNewerVersion(string remotePath, long current) =>
        YesNo($"{remotePath} ha cambiado en el servidor\n(modificado el {Sftp.LocalTime(current):dd/MM/yyyy HH:mm:ss}).\n\n" +
              "¿Descargar la nueva versión? La copia local abierta en Notepad++ se sustituirá.", MB_ICONQUESTION);

    public static bool CheckSessionFiles(string account, IReadOnlyCollection<string> remotePaths) =>
        YesNo($"Hay {remotePaths.Count} fichero(s) remoto(s) de la sesión anterior en {account}:\n\n" +
              string.Join("\n", remotePaths.Select(p => "  " + p)) + "\n\n" +
              "¿Conectar ahora para comprobar si han cambiado en el servidor?\n\nSi no, se pedirá la clave al guardar.",
              MB_ICONQUESTION);

    /// <summary>La subida falló; true si el usuario quiere reintentar.</summary>
    public static bool RetryUpload(ConnectionRequest r, Exception ex, string localPath) =>
        MessageBox(Npp.Handle,
            $"No se pudo subir a {r.Display}\n\n{Describe(ex)}\n\nLos cambios están guardados en la copia local:\n{localPath}",
            Plugin.Name, MB_RETRYCANCEL | MB_ICONERROR) == IDRETRY;

    // --- Avisos ---

    public static void Info(string text) => MessageBox(Npp.Handle, text, Plugin.Name, MB_ICONINFORMATION);

    public static void Warning(string text) => MessageBox(Npp.Handle, text, Plugin.Name, MB_ICONWARNING);

    public static void Error(string title, Exception ex) => Error(Npp.Handle, title, ex);

    /// <summary>
    /// Desde dentro de un diálogo modal el propietario debe ser ese diálogo: MessageBox vuelve a
    /// habilitar a su propietario al cerrarse, y si fuera Notepad++ quedaría activo bajo el modal.
    /// </summary>
    public static void Error(IntPtr owner, string title, Exception ex) =>
        MessageBox(owner, $"{title}\n\n{Describe(ex)}", Plugin.Name, MB_ICONERROR);

    public static string Describe(Exception ex) => ex switch
    {
        SshAuthenticationException => "El servidor rechazó el usuario, la contraseña o la clave privada.",
        SshPassPhraseNullOrEmptyException => "La clave privada está protegida: escribe su frase.",
        SftpPathNotFoundException => "La ruta remota no existe.",
        SftpPermissionDeniedException => "Permiso denegado en el servidor.",
        SshOperationTimeoutException => "Tiempo de espera agotado.",
        UntrustedHostKeyException => "La huella del servidor no está aceptada o ha cambiado: ábrelo con Ctrl+Alt+O para revisarla.",
        SocketException se => "No se puede conectar con el servidor: " + se.Message,
        SshConnectionException sc => "Error de conexión SSH: " + sc.Message,
        _ => ex.Message, // incluye KeyLoadException, FileNotFoundException y SudoException, ya redactadas
    };

    public static void About() => Info(
        Plugin.Name + " " + Version + "\n\n" +
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
        "• La huella del servidor se pide la primera vez y se recuerda.");

    private static string Version =>
        typeof(Prompts).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    private static bool YesNo(string text, uint icon) =>
        MessageBox(Npp.Handle, text, Plugin.Name, MB_YESNO | icon) == IDYES;
}
