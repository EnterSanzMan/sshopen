using System.Runtime.InteropServices;
using static SshOpen.DialogHost;
using static SshOpen.Win32;

namespace SshOpen;

/// <summary>Diálogo modal para pedir servidor, puerto, usuario, clave y ruta remota.</summary>
internal static unsafe class ConnectDialog
{
    private const int IdHost = 101, IdPort = 102, IdUser = 103, IdPass = 104, IdPath = 105, IdRemember = 106;
    private const int DC_HASDEFID = 0x534B;

    private static bool _done;
    private static bool _showRemember;
    private static bool _allowFolder;
    private static bool _remember;
    private static IntPtr _hwnd;
    private static ConnectionRequest? _result;

    /// <summary>
    /// Muestra el diálogo y devuelve los datos introducidos, o null si se cancela.
    /// Con <paramref name="allowFolder"/> la ruta puede ser una carpeta (o vacía = home) para explorarla.
    /// </summary>
    public static ConnectionRequest? Show(IntPtr owner, string title, string okText, ConnectionRequest defaults,
        ref bool remember, bool showRemember = true, bool allowFolder = true)
    {
        _done = false;
        _result = null;
        _remember = remember;
        _showRemember = showRemember;
        _allowFolder = allowFolder;

        _hwnd = CreateFrame(owner, "SshOpenConnectDialog", &WndProc, title, 420, showRemember ? 262 : 232);

        int row = 14;
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
        Row(allowFolder ? "Carpeta inicial:" : "Fichero remoto:", IdPath, defaults.RemotePath, 0,
            allowFolder ? "Opcional: /var/log  (vacío = carpeta del usuario)" : "/etc/hosts");

        if (showRemember)
        {
            IntPtr check = Child(_hwnd, 0, "BUTTON", "Recordar servidor, usuario y ruta (la clave nunca se guarda)",
                WS_TABSTOP | BS_AUTOCHECKBOX, 120, row, 286, 22, IdRemember);
            SendMessage(check, BM_SETCHECK, remember ? 1u : 0u, 0);
            row += 30;
        }

        row += 6;
        Child(_hwnd, 0, "BUTTON", okText, WS_TABSTOP | BS_DEFPUSHBUTTON, 218, row, 90, 28, IDOK);
        Child(_hwnd, 0, "BUTTON", "Cancelar", WS_TABSTOP | BS_PUSHBUTTON, 316, row, 90, 28, IDCANCEL);

        int focusId = defaults.Host.Length == 0 ? IdHost
            : defaults.User.Length == 0 ? IdUser
            : defaults.Password.Length == 0 ? IdPass
            : IdPath;
        SetFocus(GetDlgItem(_hwnd, focusId));

        RunModal(owner, _hwnd, () => _done);
        _hwnd = IntPtr.Zero;

        if (_result != null)
            remember = _remember;
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
                    if (id == IDOK)
                        Accept();
                    else if (id == IDCANCEL)
                        _done = true;
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

    private static void Accept()
    {
        string host = Text(_hwnd, IdHost).Trim();
        string user = Text(_hwnd, IdUser).Trim();
        string path = Text(_hwnd, IdPath).Trim();
        if (host.Length == 0 || user.Length == 0)
        {
            Warn("Rellena servidor y usuario.", host.Length == 0 ? IdHost : IdUser);
            return;
        }
        if (!int.TryParse(Text(_hwnd, IdPort).Trim(), out int port) || port is < 1 or > 65535)
        {
            Warn("El puerto debe ser un número entre 1 y 65535.", IdPort);
            return;
        }
        if (!_allowFolder && (path.Length == 0 || path.EndsWith('/') || path.EndsWith('\\')))
        {
            Warn("Indica la ruta completa de un fichero.", IdPath);
            return;
        }

        if (_showRemember)
            _remember = SendMessage(GetDlgItem(_hwnd, IdRemember), BM_GETCHECK, 0, 0) == 1;
        _result = new ConnectionRequest(host, port, user, Text(_hwnd, IdPass), path);
        _done = true;
    }

    private static void Warn(string text, int focusId)
    {
        MessageBox(_hwnd, text, Plugin.Name, MB_ICONWARNING);
        SetFocus(GetDlgItem(_hwnd, focusId));
    }
}
