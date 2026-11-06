using Windows.Win32.UI.Controls.Dialogs;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace SshOpen;

/// <summary>
/// Complemento de las declaraciones que genera CsWin32 (NativeMethods.txt):
/// - nombres cortos para valores que los metadatos agrupan en enumeraciones, para poder
///   combinarlos como en C (todos salen de los metadatos: aquí no hay ningún número a mano);
/// - funciones auxiliares que los diálogos usan a menudo.
/// </summary>
internal static unsafe class Win32
{
    // Estilos de ventana.
    public const uint WS_CHILD = (uint)WINDOW_STYLE.WS_CHILD, WS_VISIBLE = (uint)WINDOW_STYLE.WS_VISIBLE;
    public const uint WS_TABSTOP = (uint)WINDOW_STYLE.WS_TABSTOP, WS_BORDER = (uint)WINDOW_STYLE.WS_BORDER;
    public const uint WS_VSCROLL = (uint)WINDOW_STYLE.WS_VSCROLL, WS_CLIPCHILDREN = (uint)WINDOW_STYLE.WS_CLIPCHILDREN;
    public const uint WS_POPUP = (uint)WINDOW_STYLE.WS_POPUP, WS_CAPTION = (uint)WINDOW_STYLE.WS_CAPTION;
    public const uint WS_SYSMENU = (uint)WINDOW_STYLE.WS_SYSMENU;
    public const uint WS_EX_CLIENTEDGE = (uint)WINDOW_EX_STYLE.WS_EX_CLIENTEDGE;
    public const uint WS_EX_DLGMODALFRAME = (uint)WINDOW_EX_STYLE.WS_EX_DLGMODALFRAME;
    public const uint WS_EX_CONTROLPARENT = (uint)WINDOW_EX_STYLE.WS_EX_CONTROLPARENT;

    // Estilos de STATIC.
    public const uint SS_NOPREFIX = (uint)Windows.Win32.System.SystemServices.STATIC_STYLES.SS_NOPREFIX, SS_ENDELLIPSIS = (uint)Windows.Win32.System.SystemServices.STATIC_STYLES.SS_ENDELLIPSIS;

    // MessageBox.
    public const uint MB_YESNO = (uint)MESSAGEBOX_STYLE.MB_YESNO, MB_RETRYCANCEL = (uint)MESSAGEBOX_STYLE.MB_RETRYCANCEL;
    public const uint MB_DEFBUTTON2 = (uint)MESSAGEBOX_STYLE.MB_DEFBUTTON2;
    public const uint MB_ICONERROR = (uint)MESSAGEBOX_STYLE.MB_ICONERROR, MB_ICONWARNING = (uint)MESSAGEBOX_STYLE.MB_ICONWARNING;
    public const uint MB_ICONQUESTION = (uint)MESSAGEBOX_STYLE.MB_ICONQUESTION;
    public const uint MB_ICONINFORMATION = (uint)MESSAGEBOX_STYLE.MB_ICONINFORMATION;
    public const int IDOK = (int)MESSAGEBOX_RESULT.IDOK, IDCANCEL = (int)MESSAGEBOX_RESULT.IDCANCEL;
    public const int IDYES = (int)MESSAGEBOX_RESULT.IDYES, IDRETRY = (int)MESSAGEBOX_RESULT.IDRETRY;

    // Teclas.
    public const ushort VK_BACK = (ushort)VIRTUAL_KEY.VK_BACK, VK_RETURN = (ushort)VIRTUAL_KEY.VK_RETURN;
    public const ushort VK_F5 = (ushort)VIRTUAL_KEY.VK_F5;

    /// <summary>Instancia de Notepad++.exe, con la que se registran las clases de ventana del plugin.</summary>
    public static HINSTANCE Instance => GetModuleHandle(default(PCWSTR));

    public static int LoWord(nuint value) => (int)(value & 0xFFFF);

    public static int HiWord(nuint value) => (int)((value >> 16) & 0xFFFF);

    public static LRESULT SendMessage(HWND hwnd, uint msg, WPARAM wParam, string text)
    {
        fixed (char* p = text)
            return PInvoke.SendMessage(hwnd, msg, wParam, (nint)p);
    }

    /// <summary>MessageBox con el título del plugin; devuelve el botón pulsado (IDOK, IDYES...).</summary>
    public static int Message(HWND owner, string text, uint style) =>
        (int)MessageBox(owner, text, Plugin.Name, (MESSAGEBOX_STYLE)style);

    /// <summary>Diálogo estándar de Windows para elegir un fichero; null si se cancela.</summary>
    public static string? PickFile(HWND owner, string title, string filter, string? initialDir)
    {
        var buffer = new char[1024];
        fixed (char* file = buffer)
        fixed (char* filt = filter)
        fixed (char* ttl = title)
        fixed (char* dir = initialDir)
        {
            var ofn = new OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(OPENFILENAMEW),
                hwndOwner = owner,
                lpstrFilter = filt,
                nFilterIndex = 1,
                lpstrFile = file,
                nMaxFile = (uint)buffer.Length,
                lpstrInitialDir = dir,
                lpstrTitle = ttl,
                Flags = OPEN_FILENAME_FLAGS.OFN_FILEMUSTEXIST | OPEN_FILENAME_FLAGS.OFN_PATHMUSTEXIST
                    | OPEN_FILENAME_FLAGS.OFN_HIDEREADONLY | OPEN_FILENAME_FLAGS.OFN_NOCHANGEDIR,
            };
            if (!GetOpenFileName(&ofn))
                return null;
        }
        return new string(buffer).TrimEnd('\0');
    }

    /// <summary>Forma larga de una ruta existente (sin nombres 8.3 como DAVIDS~1).</summary>
    public static string LongPath(string path)
    {
        var buffer = new char[1024];
        uint len;
        fixed (char* input = path)
        fixed (char* output = buffer)
            len = GetLongPathName(input, output, (uint)buffer.Length);
        return len > 0 && len < buffer.Length ? new string(buffer, 0, (int)len) : path;
    }
}

/// <summary>Cursor de espera mientras dura una operación de red en el hilo de la interfaz.</summary>
internal readonly struct WaitCursor : IDisposable
{
    private readonly HCURSOR _previous;

    public WaitCursor() => _previous = SetCursor(LoadCursor(default(HINSTANCE), IDC_WAIT));

    public void Dispose() => SetCursor(_previous);
}
