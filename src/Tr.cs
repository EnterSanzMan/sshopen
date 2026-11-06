namespace SshOpen;

/// <summary>
/// Textos de la interfaz en inglés y en español. El idioma sigue al de Notepad++: español si su
/// fichero de idioma es spanish*.xml, inglés en cualquier otro caso (también sin fichero, que es
/// el inglés de Notepad++).
/// </summary>
internal static class Tr
{
    private static bool? _spanish;

    public static bool Spanish
    {
        get
        {
            if (_spanish is { } known)
                return known;
            if (Npp.Handle == default)
                return false; // aún sin Notepad++ (no debería pasar)
            // Se pregunta una sola vez, al crear el menú (getFuncsArray, en el hilo de Notepad++, que
            // ya ha cargado su idioma). Los hilos de trabajo que montan mensajes de error solo leen
            // el valor guardado: un SendMessage desde ellos podría bloquearse al cerrar Notepad++.
            string file = Path.GetFileName(Npp.GetNativeLangFileName());
            _spanish = file.StartsWith("spanish", StringComparison.OrdinalIgnoreCase);
            return _spanish.Value;
        }
    }

    private static string L(string en, string es) => Spanish ? es : en;

    private static string Time(DateTime t) => t.ToString(Spanish ? "dd/MM/yyyy HH:mm:ss" : "yyyy-MM-dd HH:mm:ss");

    // --- Comunes ---

    public static string Cancel => L("Cancel", "Cancelar");
    public static string Cancelling => L("Cancelling...", "Cancelando...");
    public static string UnexpectedError => L("Unexpected error", "Error inesperado");
    public static string WindowCreateFailed => L("Could not create the window.", "No se pudo crear la ventana.");

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };

    public static string TransferDetail(long done, long total) =>
        L($"{FormatSize(done)} of {FormatSize(total)}", $"{FormatSize(done)} de {FormatSize(total)}");

    // --- Menú ---

    public static string MenuOpenRemote => L("Open remote file (SSH)...", "Abrir fichero remoto (SSH)...");
    public static string MenuUpload => L("Upload current file to server", "Subir fichero actual al servidor");
    public static string MenuPanel => L("Remote panel", "Panel remoto");
    public static string MenuAbout => L("About...", "Acerca de...");

    // --- Diálogo de conexión ---

    public static string ProfileLabel => L("Profile:", "Perfil:");
    public static string Save => L("Save", "Guardar");
    public static string Delete => L("Delete", "Borrar");
    public static string ProfileHint => L("Name to save the connection", "Nombre para guardar la conexión");
    public static string HostLabel => L("Server (IP):", "Servidor (IP):");
    public static string PortLabel => L("Port:", "Puerto:");
    public static string UserLabel => L("User:", "Usuario:");
    public static string PasswordLabel => L("Password:", "Contraseña:");
    public static string PasswordHint => L("Optional with a private key", "Opcional si usas clave privada");
    public static string KeyLabel => L("Private key:", "Clave privada:");
    public static string KeyHint => L("Optional: id_ed25519 file, .ppk...", "Opcional: fichero id_ed25519, .ppk...");
    public static string PassphraseLabel => L("Key passphrase:", "Frase de la clave:");
    public static string PassphraseHint => L("Only if the key is protected", "Solo si la clave está protegida");
    public static string StartFolderLabel => L("Start folder:", "Carpeta inicial:");
    public static string StartFolderHint => L("Optional: /var/log  (empty = home folder)", "Opcional: /var/log  (vacío = carpeta del usuario)");
    public static string RemoteFileLabel => L("Remote file:", "Fichero remoto:");
    public static string SudoOption =>
        L("Use sudo to read and write files", "Usar sudo para leer y escribir");
    public static string RememberConnection =>
        L("Remember the last connection (secrets are never saved)", "Recordar la última conexión (las claves nunca se guardan)");
    public static string ChooseKeyTitle => L("Choose the private key", "Elige la clave privada");
    public static string KeyFilter => L(
        "Private keys (id_*, *.ppk, *.pem, *.key)\0id_*;*.ppk;*.pem;*.key\0All files (*.*)\0*.*\0\0",
        "Claves privadas (id_*, *.ppk, *.pem, *.key)\0id_*;*.ppk;*.pem;*.key\0Todos los ficheros (*.*)\0*.*\0\0");
    public static string ProfileNameRequired =>
        L("Type a name for the profile in the drop-down list.", "Escribe un nombre para el perfil en la lista desplegable.");
    public static string SelectProfileToDelete => L("Select the profile you want to delete.", "Selecciona el perfil que quieres borrar.");
    public static string ConfirmDeleteProfile(string name) => L($"Delete the profile \"{name}\"?", $"¿Borrar el perfil \"{name}\"?");
    public static string HostAndUserRequired => L("Fill in server and user.", "Rellena servidor y usuario.");
    public static string InvalidPort => L("The port must be a number between 1 and 65535.", "El puerto debe ser un número entre 1 y 65535.");
    public static string KeyFileMissing(string path) =>
        L("The private key file does not exist:\n", "No existe el fichero de clave privada:\n") + path;
    public static string FullFilePathRequired => L("Enter the full path of a file.", "Indica la ruta completa de un fichero.");

    // --- Explorador remoto ---

    public static string TypeFolder => L("Folder", "Carpeta");
    public static string TypeLink => L("Link", "Enlace");
    public static string TypeFile => L("File", "Fichero");
    public static string ColumnName => L("Name", "Nombre");
    public static string ColumnType => L("Type", "Tipo");
    public static string ColumnSize => L("Size", "Tamaño");
    public static string ColumnModified => L("Modified", "Modificado");
    public static string FolderLabel => L("Folder:", "Carpeta:");
    public static string Go => L("Go", "Ir");
    public static string FilterLabel => L("Filter:", "Filtro:");
    public static string FilterHint => L("Part of the name, or wildcards: *.conf", "Parte del nombre, o comodines: *.conf");
    public static string ShowHidden => L("Show hidden (.*)", "Mostrar ocultos (.*)");
    public static string Open => L("Open", "Abrir");
    public static string Up => L("Up", "Subir");
    public static string Root => L("Root", "Raíz");
    public static string SelectAFile => L("Select a file from the list.", "Selecciona un fichero de la lista.");
    public static string FolderNotAccessible(string folder) =>
        L($"Cannot access {folder}; showing the home folder.", $"No se puede acceder a {folder}; se muestra la carpeta del usuario.");
    public static string CannotAccess(string path) => L("Cannot access ", "No se puede acceder a ") + path;
    public static string LinkNotAccessible(string path) => L($"The link {path} is not accessible", $"El enlace {path} no es accesible");
    public static string CannotList(string folder) => L("Cannot list ", "No se puede listar ") + folder;

    public static string BrowseStatus(int folders, int files, int hidden) => Spanish
        ? $"{folders} carpetas, {files} ficheros" + (hidden > 0 ? $" ({hidden} sin mostrar)" : "") + "   ·   Retroceso: subir"
        : $"{folders} folders, {files} files" + (hidden > 0 ? $" ({hidden} hidden)" : "") + "   ·   Backspace: up";

    // --- Panel ---

    public static string Connect => L("Connect...", "Conectar...");
    public static string Refresh => L("Refresh", "Actualizar");
    public static string Disconnect => L("Disconnect", "Desconectar");
    public static string NotConnected => L("Not connected", "Sin conexión");
    public static string HiddenShort => L("Hidden", "Ocultos");
    public static string ConnectPanelTitle => L("Connect the remote panel (SSH/SFTP)", "Conectar el panel remoto (SSH/SFTP)");
    public static string Listing(string path) => L("Listing ", "Listando ") + path;

    // --- Conexión ---

    public static string ConnectButton => L("Connect", "Conectar");
    public static string Connecting(string user, string host, int port) =>
        L($"Connecting to {user}@{host}:{port}...", $"Conectando con {user}@{host}:{port}...");
    public static string CannotConnect(string target) => L("Could not connect to ", "No se pudo conectar con ") + target;
    public static string ConnectTo(string account) => L("Connect to ", "Conectar con ") + account;

    public static string TrustNewHost(string host, int port, string fingerprint) => Spanish
        ? $"Es la primera vez que te conectas a {host}:{port}.\n\nHuella del servidor:\n{fingerprint}\n\n¿Confiar en este servidor?"
        : $"This is the first time you connect to {host}:{port}.\n\nServer fingerprint:\n{fingerprint}\n\nTrust this server?";

    public static string TrustChangedHost(string host, int port, string known, string fingerprint) => Spanish
        ? $"¡ATENCIÓN! La huella de {host}:{port} ha CAMBIADO.\n\nGuardada:\n{known}\n\nRecibida:\n{fingerprint}\n\n" +
          "Puede ser una reinstalación del servidor o un ataque man-in-the-middle.\n¿Confiar en la nueva huella?"
        : $"WARNING! The fingerprint of {host}:{port} has CHANGED.\n\nSaved:\n{known}\n\nReceived:\n{fingerprint}\n\n" +
          "It may be a server reinstall or a man-in-the-middle attack.\nTrust the new fingerprint?";

    // --- Abrir, subir y sesión ---

    public static string OpenRemoteTitle => L("Open remote file (SSH/SFTP)", "Abrir fichero remoto (SSH/SFTP)");
    public static string BrowseTitle(string account) => account + L(" - Choose the file to open", " - Elige el fichero a abrir");
    public static string CannotOpen(string target) => L("Could not open ", "No se pudo abrir ") + target;
    public static string CannotDownload(string path) => L("Could not download ", "No se pudo descargar ") + path;
    public static string Downloading(string path) => L("Downloading ", "Descargando ") + path;
    public static string Uploading(string path) => L("Uploading ", "Subiendo ") + path;
    public static string UploadTitle(string fileName) => L($"Upload {fileName} to the server", $"Subir {fileName} al servidor");
    public static string UploadButton => L("Upload", "Subir");
    public static string NotOpenedWithPlugin => L("The current file was not opened with ", "El fichero actual no se abrió con ") + Plugin.Name + ".";
    public static string CouldNotSaveLocal =>
        L("The local copy could not be saved, so nothing was uploaded.", "No se pudo guardar la copia local, así que no se ha subido nada.");

    public static string StatusOpened(string target) => L("SFTP: opened ", "SFTP: abierto ") + target;
    public static string StatusUploaded(string target, DateTime time) =>
        L("SFTP: uploaded ", "SFTP: subido ") + target + $" ({time:HH:mm:ss})";
    public static string UploadCancelled => L("upload cancelled", "subida cancelada");
    public static string NotUploaded => L("not uploaded", "no se ha subido");
    public static string StatusGaveUp(string reason) =>
        L($"SFTP: {reason}, the changes are only in the local copy", $"SFTP: {reason}, los cambios solo están en la copia local");
    public static string StatusChecked(string account, int unchanged, int total) => L(
        $"SFTP: {account} checked, {unchanged} of {total} file(s) unchanged on the server",
        $"SFTP: {account} comprobado, {unchanged} de {total} fichero(s) sin cambios en el servidor");

    public static string UseSudo(string user, bool write, string path, string account) => Spanish
        ? $"{user} no tiene permiso para {(write ? "escribir en" : "leer")} {path}.\n\n¿Intentar con sudo (con la misma contraseña)?\n\n" +
          $"No se volverá a preguntar para {account} mientras Notepad++ siga abierto."
        : $"{user} has no permission to {(write ? "write" : "read")} {path}.\n\nTry with sudo (with the same password)?\n\n" +
          $"You will not be asked again for {account} while Notepad++ stays open.";

    public static string OverwriteLocalCopy(string path) => Spanish
        ? $"La copia local de {path} tiene cambios que no se subieron al servidor.\n\n" +
          "¿Descargar de nuevo el fichero del servidor?\n\n" +
          "Sí: se pierden los cambios locales.\nNo: se abre la copia local tal cual (podrás subirla al guardar)."
        : $"The local copy of {path} has changes that were not uploaded to the server.\n\n" +
          "Download the file from the server again?\n\n" +
          "Yes: the local changes are lost.\nNo: the local copy opens as it is (you can upload it when you save).";

    public static string RemoteGone(string path) => Spanish
        ? $"{path} ya no existe en el servidor.\n\n¿Crearlo de nuevo con tu copia?"
        : $"{path} no longer exists on the server.\n\nCreate it again with your copy?";

    public static string RemoteChanged(string path, DateTime modified) => Spanish
        ? $"{path} ha cambiado en el servidor desde que lo abriste\n(modificado el {Time(modified)}).\n\n" +
          "Si lo subes se perderán esos cambios.\n\n¿Sobrescribir el fichero del servidor?\n\n" +
          "No: tus cambios quedan en la copia local; con Ctrl+Alt+O puedes ver la versión del servidor."
        : $"{path} has changed on the server since you opened it\n(modified {Time(modified)}).\n\n" +
          "Uploading it will lose those changes.\n\nOverwrite the file on the server?\n\n" +
          "No: your changes stay in the local copy; Ctrl+Alt+O shows the server version.";

    public static string DownloadNewerVersion(string path, DateTime modified) => Spanish
        ? $"{path} ha cambiado en el servidor\n(modificado el {Time(modified)}).\n\n" +
          "¿Descargar la nueva versión? La copia local abierta en Notepad++ se sustituirá."
        : $"{path} has changed on the server\n(modified {Time(modified)}).\n\n" +
          "Download the new version? The local copy open in Notepad++ will be replaced.";

    public static string CheckSessionFiles(string account, IReadOnlyCollection<string> paths)
    {
        string list = string.Join("\n", paths.Select(p => "  " + p));
        return Spanish
            ? $"Hay {paths.Count} fichero(s) remoto(s) de la sesión anterior en {account}:\n\n{list}\n\n" +
              "¿Conectar ahora para comprobar si han cambiado en el servidor?\n\nSi no, se pedirá la clave al guardar."
            : $"There are {paths.Count} remote file(s) from the previous session on {account}:\n\n{list}\n\n" +
              "Connect now to check whether they changed on the server?\n\nIf not, the password will be asked when you save.";
    }

    public static string RetryUpload(string target, string reason, string localPath) => Spanish
        ? $"No se pudo subir a {target}\n\n{reason}\n\nLos cambios están guardados en la copia local:\n{localPath}"
        : $"Could not upload to {target}\n\n{reason}\n\nThe changes are saved in the local copy:\n{localPath}";

    // --- Errores ---

    public static string AuthRejected =>
        L("The server rejected the user, the password or the private key.", "El servidor rechazó el usuario, la contraseña o la clave privada.");
    public static string PassphraseRequired =>
        L("The private key is protected: type its passphrase.", "La clave privada está protegida: escribe su frase.");
    public static string PathNotFound => L("The remote path does not exist.", "La ruta remota no existe.");
    public static string PermissionDenied => L("Permission denied on the server.", "Permiso denegado en el servidor.");
    public static string Timeout => L("Timed out.", "Tiempo de espera agotado.");
    public static string HostKeyRejected => L(
        "The server fingerprint is not accepted or has changed: open it with Ctrl+Alt+O to review it.",
        "La huella del servidor no está aceptada o ha cambiado: ábrelo con Ctrl+Alt+O para revisarla.");
    public static string CannotReachServer(string detail) =>
        L("Cannot connect to the server: ", "No se puede conectar con el servidor: ") + detail;
    public static string SshError(string detail) => L("SSH connection error: ", "Error de conexión SSH: ") + detail;

    public static string HostKeyNotAccepted => L("The server fingerprint is not accepted.", "La huella del servidor no está aceptada.");
    public static string RemoteFileChanged => L("The remote file has changed on the server.", "El fichero remoto ha cambiado en el servidor.");
    public static string KeyBadPassphrase => L(
        "Could not open the private key: the passphrase is wrong or the format is not supported.",
        "No se pudo abrir la clave privada: la frase no es correcta o el formato no está admitido.");
    public static string KeyUnreadable => L(
        "Could not read the private key: the format is not supported or the file is damaged.",
        "No se pudo leer la clave privada: el formato no está admitido o el fichero está dañado.");
    public static string KeyFileNotFound(string path) =>
        L("The private key file was not found:\n", "No se encuentra el fichero de clave privada:\n") + path;

    public static string SudoNeedsPassword => L(
        "sudo asks for the user's password: type it in the Password field too.",
        "sudo pide la contraseña del usuario: escríbela también en el campo Contraseña.");
    public static string SudoRejected => L(
        "sudo rejected the password or the user cannot use sudo",
        "sudo ha rechazado la contraseña o el usuario no puede usar sudo");
    public static string SudoFailed => L("sudo failed", "sudo ha fallado");

    // --- Acerca de ---

    public static string About(string version) => Spanish
        ? Plugin.Name + " " + version + "\n\n" +
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
          "• La huella del servidor se pide la primera vez y se recuerda."
        : Plugin.Name + " " + version + "\n\n" +
          "Opens files on a Linux server over SSH (SFTP) with user and password or a private key.\n\n" +
          "• After connecting, folders and files are shown so you can browse to the file:\n" +
          "   double-click or Enter to go in or open, Backspace to go up a folder.\n" +
          "• There is also a dockable panel with the folder tree (Plugins → SshOpen → Remote panel).\n" +
          "• The file is downloaded to a copy in %TEMP%\\SshOpen.\n" +
          "• On save (Ctrl+S) it is uploaded to the server automatically; if the file changed\n" +
          "   on the server since you opened it, you are warned before it is overwritten.\n" +
          "• If the user has no permission on the file, it offers to read and write it with sudo.\n" +
          "• Profiles store server, port, user, folder and private key path.\n" +
          "• The password and the key passphrase are only kept in memory while Notepad++ is open.\n" +
          "• The server fingerprint is asked the first time and remembered.";
}
