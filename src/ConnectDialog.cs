using static SshOpen.Win32;

namespace SshOpen;

/// <summary>Opciones del diálogo completo (abrir): perfiles y "recordar". Se leen y se actualizan.</summary>
internal sealed class ConnectOptions
{
    public bool Remember = true;
    public string Profile = "";
}

/// <summary>
/// Diálogo modal para pedir servidor, puerto, usuario, contraseña o clave privada y ruta remota.
/// Con <see cref="ConnectOptions"/> muestra además los perfiles guardados y la ruta puede ser
/// una carpeta; sin ellas (subida) pide obligatoriamente un fichero.
/// </summary>
internal sealed unsafe class ConnectDialog : ModalDialog
{
    private const int IdProfile = 100, IdHost = 101, IdPort = 102, IdUser = 103, IdPass = 104, IdPath = 105;
    private const int IdRemember = 106, IdSaveProfile = 107, IdDeleteProfile = 108, IdKey = 109, IdBrowseKey = 110, IdPassphrase = 111;

    private readonly ConnectOptions? _options;
    private List<Profile> _profiles = [];
    private ConnectionRequest? _result;

    private ConnectDialog(ConnectOptions? options) => _options = options;

    /// <summary>Muestra el diálogo y devuelve los datos introducidos, o null si se cancela.</summary>
    public static ConnectionRequest? Show(HWND owner, string title, string okText, ConnectionRequest defaults,
        ConnectOptions? options)
    {
        var dialog = new ConnectDialog(options);
        dialog.Build(owner, title, okText, defaults);
        dialog.RunModal();
        return dialog._result;
    }

    protected override HWND DefaultFocus => Item(IdHost);

    private void Build(HWND owner, string title, string okText, ConnectionRequest defaults)
    {
        bool full = _options != null;
        CreateFrame(owner, "SshOpenConnectDialog", title, 420, full ? 358 : 290);
        try
        {
            int row = 14;
            if (full)
            {
                Child(0, "STATIC", Tr.ProfileLabel, 0, 14, row + 4, 104, 20, 0);
                HWND combo = Child(0, "COMBOBOX", "", WS_TABSTOP | WS_VSCROLL | CBS_DROPDOWN | CBS_AUTOHSCROLL | CBS_SORT,
                    120, row, 166, 200, IdProfile);
                Child(0, "BUTTON", Tr.Save, WS_TABSTOP | BS_PUSHBUTTON, 292, row, 55, 24, IdSaveProfile);
                Child(0, "BUTTON", Tr.Delete, WS_TABSTOP | BS_PUSHBUTTON, 351, row, 55, 24, IdDeleteProfile);
                SendMessage(combo, EM_SETCUEBANNER, 1, Tr.ProfileHint);
                row += 32;
                FillProfiles(_options!.Profile);
            }

            void Row(string label, int id, string value, uint extraStyle, string? hint = null, int width = 286)
            {
                Child(0, "STATIC", label, 0, 14, row + 4, 104, 20, 0);
                HWND edit = Child(WS_EX_CLIENTEDGE, "EDIT", value, WS_TABSTOP | ES_AUTOHSCROLL | extraStyle, 120, row, width, 24, id);
                if (hint != null)
                    SendMessage(edit, EM_SETCUEBANNER, 1, hint);
                row += 32;
            }

            Row(Tr.HostLabel, IdHost, defaults.Host, 0, "192.168.1.10");
            Row(Tr.PortLabel, IdPort, defaults.Port.ToString(), ES_NUMBER);
            Row(Tr.UserLabel, IdUser, defaults.User, 0, "root");
            Row(Tr.PasswordLabel, IdPass, defaults.Password, ES_PASSWORD, Tr.PasswordHint);
            Row(Tr.KeyLabel, IdKey, defaults.KeyFile, 0, Tr.KeyHint, width: 250);
            // Después del campo de la clave, para que el orden de tabulación sea el natural.
            Child(0, "BUTTON", "...", WS_TABSTOP | BS_PUSHBUTTON, 376, row - 32, 30, 24, IdBrowseKey);
            Row(Tr.PassphraseLabel, IdPassphrase, defaults.Passphrase, ES_PASSWORD, Tr.PassphraseHint);
            Row(full ? Tr.StartFolderLabel : Tr.RemoteFileLabel, IdPath, defaults.RemotePath, 0,
                full ? Tr.StartFolderHint : "/etc/hosts");

            if (full)
            {
                Child(0, "BUTTON", Tr.RememberConnection, WS_TABSTOP | BS_AUTOCHECKBOX, 120, row, 286, 22, IdRemember);
                SetChecked(IdRemember, _options!.Remember);
                row += 30;
            }

            row += 6;
            Child(0, "BUTTON", okText, WS_TABSTOP | BS_DEFPUSHBUTTON, 218, row, 90, 28, IDOK);
            Child(0, "BUTTON", Tr.Cancel, WS_TABSTOP | BS_PUSHBUTTON, 316, row, 90, 28, IDCANCEL);

            FocusFirstEmpty();
        }
        catch
        {
            Close();
            throw;
        }
    }

    protected override nint? OnDialogMessage(uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case DM_GETDEFID:
                return (nint)((DC_HASDEFID << 16) | (uint)IDOK);
            case WM_COMMAND:
                switch (LoWord(wParam))
                {
                    case IDOK: Accept(); break;
                    case IDCANCEL: Done = true; break;
                    case IdSaveProfile: SaveProfile(); break;
                    case IdDeleteProfile: DeleteProfile(); break;
                    case IdBrowseKey: BrowseKey(); break;
                    case IdProfile when (uint)HiWord(wParam) == CBN_SELCHANGE: LoadProfile(SelectedProfileName()); break;
                }
                return 0;
        }
        return null;
    }

    private void BrowseKey()
    {
        string current = Text(IdKey).Trim();
        string sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        string? currentDir = current.Length > 0 ? Path.GetDirectoryName(current) : null;
        string? initialDir = Directory.Exists(currentDir) ? currentDir : Directory.Exists(sshDir) ? sshDir : null;
        string? path = PickFile(Handle, Tr.ChooseKeyTitle, Tr.KeyFilter, initialDir);
        if (path == null)
            return;
        if (path != current)
            SetText(IdPassphrase, "");
        SetText(IdKey, path);
        SetFocus(Item(Sftp.KeyNeedsPassphrase(path) ? IdPassphrase : IdPath));
    }

    // --- Perfiles ---

    private void FillProfiles(string select)
    {
        _profiles = Config.LoadProfiles();
        HWND combo = Item(IdProfile);
        SendMessage(combo, CB_RESETCONTENT, 0, 0);
        foreach (var p in _profiles)
            SendMessage(combo, CB_ADDSTRING, 0, p.Name);
        nint index = select.Length == 0 ? CB_ERR : SendMessage(combo, CB_FINDSTRINGEXACT, unchecked((nuint)(-1)), select);
        SendMessage(combo, CB_SETCURSEL, unchecked((nuint)index), 0);
    }

    /// <summary>Nombre de la entrada seleccionada en la lista (no el texto escrito a mano).</summary>
    private string SelectedProfileName()
    {
        HWND combo = Item(IdProfile);
        nint index = SendMessage(combo, CB_GETCURSEL, 0, 0);
        if (index == CB_ERR)
            return "";
        int len = (int)SendMessage(combo, CB_GETLBTEXTLEN, (nuint)index, 0);
        var buffer = new char[len + 1];
        fixed (char* p = buffer)
            SendMessage(combo, CB_GETLBTEXT, (nuint)index, (nint)p);
        return new string(buffer, 0, len);
    }

    private Profile? FindProfile(string name) =>
        _profiles.Find(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));

    private void LoadProfile(string name)
    {
        var profile = FindProfile(name);
        if (profile == null)
            return;
        // Los secretos son de la cuenta o la clave anteriores: no se reutilizan en otras.
        if (Text(IdHost).Trim() != profile.Host || Text(IdUser).Trim() != profile.User)
            SetText(IdPass, "");
        if (Text(IdKey).Trim() != profile.KeyFile)
            SetText(IdPassphrase, "");
        SetText(IdHost, profile.Host);
        SetText(IdPort, profile.Port.ToString());
        SetText(IdUser, profile.User);
        SetText(IdKey, profile.KeyFile);
        SetText(IdPath, profile.RemotePath);
        FocusFirstEmpty();
    }

    private void SaveProfile()
    {
        string name = Text(IdProfile).Trim();
        if (name.Length == 0)
        {
            Warn(Tr.ProfileNameRequired, IdProfile);
            return;
        }
        var fields = ReadFields(requireFile: false);
        if (fields == null)
            return;
        _profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        _profiles.Add(new Profile(name, fields.Host, fields.Port, fields.User, fields.RemotePath, fields.KeyFile));
        Config.SaveProfiles(_profiles);
        FillProfiles(name);
        FocusFirstEmpty();
    }

    private void DeleteProfile()
    {
        string name = Text(IdProfile).Trim();
        if (FindProfile(name) == null)
        {
            Warn(Tr.SelectProfileToDelete, IdProfile);
            return;
        }
        if (Message(Handle, Tr.ConfirmDeleteProfile(name), MB_YESNO | MB_ICONQUESTION) != IDYES)
            return;
        _profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        Config.SaveProfiles(_profiles);
        FillProfiles("");
        SetText(IdProfile, "");
        SetFocus(Item(IdProfile));
    }

    // --- Aceptar ---

    private void Accept()
    {
        var fields = ReadFields(requireFile: _options == null);
        if (fields == null)
            return;

        if (_options != null)
        {
            _options.Remember = IsChecked(IdRemember);
            _options.Profile = FindProfile(Text(IdProfile).Trim())?.Name ?? "";
        }
        _result = fields;
        Done = true;
    }

    /// <summary>Lee y valida los campos; con error avisa, enfoca el campo y devuelve null.</summary>
    private ConnectionRequest? ReadFields(bool requireFile)
    {
        string host = Text(IdHost).Trim();
        string user = Text(IdUser).Trim();
        string path = Text(IdPath).Trim();
        string key = Text(IdKey).Trim().Trim('"');
        if (host.Length == 0 || user.Length == 0)
        {
            Warn(Tr.HostAndUserRequired, host.Length == 0 ? IdHost : IdUser);
            return null;
        }
        if (!int.TryParse(Text(IdPort).Trim(), out int port) || port is < 1 or > 65535)
        {
            Warn(Tr.InvalidPort, IdPort);
            return null;
        }
        if (key.Length > 0 && !File.Exists(key))
        {
            Warn(Tr.KeyFileMissing(key), IdKey);
            return null;
        }
        if (requireFile && (path.Length == 0 || path.EndsWith('/') || path.EndsWith('\\')))
        {
            Warn(Tr.FullFilePathRequired, IdPath);
            return null;
        }
        return new ConnectionRequest(host, port, user, Text(IdPass), path)
        {
            KeyFile = key,
            Passphrase = key.Length > 0 ? Text(IdPassphrase) : "",
        };
    }

    private void FocusFirstEmpty()
    {
        string key = Text(IdKey).Trim();
        int id = Text(IdHost).Length == 0 ? IdHost
            : Text(IdUser).Length == 0 ? IdUser
            : key.Length > 0 ? (Text(IdPassphrase).Length == 0 && File.Exists(key) && Sftp.KeyNeedsPassphrase(key) ? IdPassphrase : IdPath)
            : Text(IdPass).Length == 0 ? IdPass
            : IdPath;
        SetFocus(Item(id));
    }
}
