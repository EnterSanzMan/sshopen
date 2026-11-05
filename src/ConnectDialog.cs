using System.Runtime.InteropServices;
using static SshOpen.DialogHost;
using static SshOpen.Win32;

namespace SshOpen;

/// <summary>Opciones del diálogo completo (abrir): perfiles y "recordar". Se leen y se actualizan.</summary>
internal sealed class ConnectOptions
{
    public bool Remember = true;
    public string Profile = "";
}

/// <summary>
/// Diálogo modal para pedir servidor, puerto, usuario, clave y ruta remota.
/// Con <see cref="ConnectOptions"/> muestra además los perfiles guardados y la ruta puede ser
/// una carpeta; sin ellas (subida) pide obligatoriamente un fichero.
/// </summary>
internal static unsafe class ConnectDialog
{
    private const int IdProfile = 100, IdHost = 101, IdPort = 102, IdUser = 103, IdPass = 104, IdPath = 105;
    private const int IdRemember = 106, IdSaveProfile = 107, IdDeleteProfile = 108;
    private const int DC_HASDEFID = 0x534B;

    private static bool _done;
    private static ConnectOptions? _options;
    private static IntPtr _hwnd;
    private static List<Profile> _profiles = [];
    private static ConnectionRequest? _result;

    /// <summary>Muestra el diálogo y devuelve los datos introducidos, o null si se cancela.</summary>
    public static ConnectionRequest? Show(IntPtr owner, string title, string okText, ConnectionRequest defaults,
        ConnectOptions? options)
    {
        _done = false;
        _result = null;
        _options = options;
        bool full = options != null;

        _hwnd = CreateFrame(owner, "SshOpenConnectDialog", &WndProc, title, 420, full ? 294 : 232);

        int row = 14;
        if (full)
        {
            Child(_hwnd, 0, "STATIC", "Perfil:", 0, 14, row + 4, 104, 20, 0);
            Child(_hwnd, 0, "COMBOBOX", "", WS_TABSTOP | WS_VSCROLL | CBS_DROPDOWN | CBS_AUTOHSCROLL | CBS_SORT,
                120, row, 166, 200, IdProfile);
            Child(_hwnd, 0, "BUTTON", "Guardar", WS_TABSTOP | BS_PUSHBUTTON, 292, row, 55, 24, IdSaveProfile);
            Child(_hwnd, 0, "BUTTON", "Borrar", WS_TABSTOP | BS_PUSHBUTTON, 351, row, 55, 24, IdDeleteProfile);
            SendMessage(GetDlgItem(_hwnd, IdProfile), EM_SETCUEBANNER, 1, "Nombre para guardar la conexión");
            row += 32;
            FillProfiles(options!.Profile);
        }

        void Row(string label, int id, string value, uint extraStyle, string? hint = null)
        {
            Child(_hwnd, 0, "STATIC", label, 0, 14, row + 4, 104, 20, 0);
            IntPtr edit = Child(_hwnd, WS_EX_CLIENTEDGE, "EDIT", value, WS_TABSTOP | ES_AUTOHSCROLL | extraStyle,
                120, row, 286, 24, id);
            if (hint != null)
                SendMessage(edit, EM_SETCUEBANNER, 1, hint);
            row += 32;
        }

        Row("Servidor (IP):", IdHost, defaults.Host, 0, "192.168.1.10");
        Row("Puerto:", IdPort, defaults.Port.ToString(), ES_NUMBER);
        Row("Usuario:", IdUser, defaults.User, 0, "root");
        Row("Clave:", IdPass, defaults.Password, ES_PASSWORD);
        Row(full ? "Carpeta inicial:" : "Fichero remoto:", IdPath, defaults.RemotePath, 0,
            full ? "Opcional: /var/log  (vacío = carpeta del usuario)" : "/etc/hosts");

        if (full)
        {
            IntPtr check = Child(_hwnd, 0, "BUTTON", "Recordar la última conexión (la clave nunca se guarda)",
                WS_TABSTOP | BS_AUTOCHECKBOX, 120, row, 286, 22, IdRemember);
            SendMessage(check, BM_SETCHECK, options!.Remember ? 1u : 0u, 0);
            row += 30;
        }

        row += 6;
        Child(_hwnd, 0, "BUTTON", okText, WS_TABSTOP | BS_DEFPUSHBUTTON, 218, row, 90, 28, IDOK);
        Child(_hwnd, 0, "BUTTON", "Cancelar", WS_TABSTOP | BS_PUSHBUTTON, 316, row, 90, 28, IDCANCEL);

        FocusFirstEmpty();
        RunModal(owner, _hwnd, () => _done);
        _hwnd = IntPtr.Zero;
        _options = null;
        return _result;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(IntPtr hwnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case DM_GETDEFID:
                    return (DC_HASDEFID << 16) | IDOK;
                case WM_ACTIVATE:
                    OnActivate(hwnd, wParam, GetDlgItem(hwnd, IdHost));
                    return 0;
                case WM_CLOSE:
                    _done = true;
                    return 0;
                case WM_COMMAND:
                    int id = (int)(wParam & 0xFFFF);
                    int code = (int)(wParam >> 16);
                    switch (id)
                    {
                        case IDOK: Accept(); break;
                        case IDCANCEL: _done = true; break;
                        case IdSaveProfile: SaveProfile(); break;
                        case IdDeleteProfile: DeleteProfile(); break;
                        case IdProfile when code == CBN_SELCHANGE: LoadProfile(SelectedProfileName()); break;
                    }
                    return 0;
            }
        }
        catch (Exception ex)
        {
            MessageBox(hwnd, ex.Message, Plugin.Name, MB_ICONERROR);
            _done = true;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // --- Perfiles ---

    private static void FillProfiles(string select)
    {
        _profiles = Config.LoadProfiles();
        IntPtr combo = GetDlgItem(_hwnd, IdProfile);
        SendMessage(combo, CB_RESETCONTENT, 0, 0);
        foreach (var p in _profiles)
            SendMessage(combo, CB_ADDSTRING, 0, p.Name);
        int index = select.Length == 0 ? CB_ERR : (int)SendMessage(combo, CB_FINDSTRINGEXACT, unchecked((nuint)(-1)), select);
        SendMessage(combo, CB_SETCURSEL, unchecked((nuint)index), 0);
    }

    /// <summary>Nombre de la entrada seleccionada en la lista (no el texto escrito a mano).</summary>
    private static string SelectedProfileName()
    {
        IntPtr combo = GetDlgItem(_hwnd, IdProfile);
        int index = (int)SendMessage(combo, CB_GETCURSEL, 0, 0);
        if (index == CB_ERR)
            return "";
        int len = (int)SendMessage(combo, CB_GETLBTEXTLEN, (nuint)index, 0);
        var buffer = new char[len + 1];
        fixed (char* p = buffer)
            SendMessage(combo, CB_GETLBTEXT, (nuint)index, (nint)p);
        return new string(buffer, 0, len);
    }

    private static Profile? FindProfile(string name) =>
        _profiles.Find(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));

    private static void LoadProfile(string name)
    {
        var profile = FindProfile(name);
        if (profile == null)
            return;
        // La clave es de la cuenta anterior: no se reutiliza en otra.
        if (Text(_hwnd, IdHost).Trim() != profile.Host || Text(_hwnd, IdUser).Trim() != profile.User)
            SetText(_hwnd, IdPass, "");
        SetText(_hwnd, IdHost, profile.Host);
        SetText(_hwnd, IdPort, profile.Port.ToString());
        SetText(_hwnd, IdUser, profile.User);
        SetText(_hwnd, IdPath, profile.RemotePath);
        FocusFirstEmpty();
    }

    private static void SaveProfile()
    {
        string name = Text(_hwnd, IdProfile).Trim();
        if (name.Length == 0)
        {
            Warn("Escribe un nombre para el perfil en la lista desplegable.", IdProfile);
            return;
        }
        var fields = ReadFields(requireFile: false);
        if (fields == null)
            return;
        _profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        _profiles.Add(new Profile(name, fields.Host, fields.Port, fields.User, fields.RemotePath));
        Config.SaveProfiles(_profiles);
        FillProfiles(name);
        SetFocus(GetDlgItem(_hwnd, Text(_hwnd, IdPass).Length == 0 ? IdPass : IdProfile));
    }

    private static void DeleteProfile()
    {
        string name = Text(_hwnd, IdProfile).Trim();
        if (FindProfile(name) == null)
        {
            Warn("Selecciona el perfil que quieres borrar.", IdProfile);
            return;
        }
        if (MessageBox(_hwnd, $"¿Borrar el perfil \"{name}\"?", Plugin.Name, MB_YESNO | MB_ICONQUESTION) != IDYES)
            return;
        _profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        Config.SaveProfiles(_profiles);
        FillProfiles("");
        SetText(_hwnd, IdProfile, "");
        SetFocus(GetDlgItem(_hwnd, IdProfile));
    }

    // --- Aceptar ---

    private static void Accept()
    {
        var fields = ReadFields(requireFile: _options == null);
        if (fields == null)
            return;

        if (_options != null)
        {
            _options.Remember = SendMessage(GetDlgItem(_hwnd, IdRemember), BM_GETCHECK, 0, 0) == 1;
            string name = Text(_hwnd, IdProfile).Trim();
            _options.Profile = FindProfile(name)?.Name ?? "";
        }
        _result = fields;
        _done = true;
    }

    /// <summary>Lee y valida los campos; con error avisa, enfoca el campo y devuelve null.</summary>
    private static ConnectionRequest? ReadFields(bool requireFile)
    {
        string host = Text(_hwnd, IdHost).Trim();
        string user = Text(_hwnd, IdUser).Trim();
        string path = Text(_hwnd, IdPath).Trim();
        if (host.Length == 0 || user.Length == 0)
        {
            Warn("Rellena servidor y usuario.", host.Length == 0 ? IdHost : IdUser);
            return null;
        }
        if (!int.TryParse(Text(_hwnd, IdPort).Trim(), out int port) || port is < 1 or > 65535)
        {
            Warn("El puerto debe ser un número entre 1 y 65535.", IdPort);
            return null;
        }
        if (requireFile && (path.Length == 0 || path.EndsWith('/') || path.EndsWith('\\')))
        {
            Warn("Indica la ruta completa de un fichero.", IdPath);
            return null;
        }
        return new ConnectionRequest(host, port, user, Text(_hwnd, IdPass), path);
    }

    private static void FocusFirstEmpty()
    {
        int id = Text(_hwnd, IdHost).Length == 0 ? IdHost
            : Text(_hwnd, IdUser).Length == 0 ? IdUser
            : Text(_hwnd, IdPass).Length == 0 ? IdPass
            : IdPath;
        SetFocus(GetDlgItem(_hwnd, id));
    }

    private static void Warn(string text, int focusId)
    {
        MessageBox(_hwnd, text, Plugin.Name, MB_ICONWARNING);
        SetFocus(GetDlgItem(_hwnd, focusId));
    }
}
