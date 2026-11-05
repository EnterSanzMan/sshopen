using System.Runtime.InteropServices;

namespace SshOpen;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
internal struct POINT { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public IntPtr hwnd;
    public uint message;
    public nuint wParam;
    public nint lParam;
    public uint time;
    public POINT pt;
    public uint lPrivate;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public delegate* unmanaged<IntPtr, uint, nuint, nint, nint> lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public IntPtr hInstance;
    public IntPtr hIcon;
    public IntPtr hCursor;
    public IntPtr hbrBackground;
    public char* lpszMenuName;
    public char* lpszClassName;
    public IntPtr hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NMHDR
{
    public IntPtr hwndFrom;
    public nuint idFrom;
    public int code;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LVCOLUMNW
{
    public uint mask;
    public int fmt;
    public int cx;
    public char* pszText;
    public int cchTextMax;
    public int iSubItem;
    public int iImage;
    public int iOrder;
    public int cxMin;
    public int cxDefault;
    public int cxIdeal;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LVITEMW
{
    public uint mask;
    public int iItem;
    public int iSubItem;
    public uint state;
    public uint stateMask;
    public char* pszText;
    public int cchTextMax;
    public int iImage;
    public nint lParam;
    public int iIndent;
    public int iGroupId;
    public uint cColumns;
    public uint* puColumns;
    public int* piColFmt;
    public int iGroup;
}

/// <summary>P/Invoke mínimo de user32/gdi32/kernel32 (sin WinForms: no es compatible con NativeAOT).</summary>
internal static unsafe partial class Win32
{
    public const uint WM_ACTIVATE = 0x0006, WM_CLOSE = 0x0010, WM_SETFONT = 0x0030, WM_COMMAND = 0x0111, DM_GETDEFID = 0x0400;
    public const uint WS_POPUP = 0x80000000, WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
    public const uint WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_TABSTOP = 0x00010000;
    public const uint WS_EX_DLGMODALFRAME = 0x0001, WS_EX_CLIENTEDGE = 0x0200, WS_EX_CONTROLPARENT = 0x00010000;
    public const uint ES_PASSWORD = 0x0020, ES_AUTOHSCROLL = 0x0080, ES_NUMBER = 0x2000;
    public const uint BS_PUSHBUTTON = 0, BS_DEFPUSHBUTTON = 1, BS_AUTOCHECKBOX = 3;
    public const uint BM_GETCHECK = 0x00F0, BM_SETCHECK = 0x00F1, EM_SETCUEBANNER = 0x1501;
    public const int IDOK = 1, IDCANCEL = 2, IDRETRY = 4, IDYES = 6;
    public const uint MB_OK = 0, MB_RETRYCANCEL = 5, MB_YESNO = 4, MB_DEFBUTTON2 = 0x100;
    public const uint MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, MB_ICONWARNING = 0x30, MB_ICONINFORMATION = 0x40;
    public const uint WM_NOTIFY = 0x004E, WS_BORDER = 0x00800000, WS_VSCROLL = 0x00200000;

    // ComboBox
    public const uint CBS_DROPDOWN = 0x0002, CBS_AUTOHSCROLL = 0x0040, CBS_SORT = 0x0100;
    public const uint CB_ADDSTRING = 0x0143, CB_GETCURSEL = 0x0147, CB_GETLBTEXT = 0x0148, CB_GETLBTEXTLEN = 0x0149;
    public const uint CB_RESETCONTENT = 0x014B, CB_SETCURSEL = 0x014E, CB_FINDSTRINGEXACT = 0x0158;
    public const int CB_ERR = -1, CBN_SELCHANGE = 1;

    // ListView (comctl32)
    public const uint LVS_REPORT = 0x0001, LVS_SINGLESEL = 0x0004, LVS_SHOWSELALWAYS = 0x0008;
    public const uint LVM_DELETEALLITEMS = 0x1009, LVM_GETNEXTITEM = 0x100C, LVM_ENSUREVISIBLE = 0x1013, LVM_SETITEMSTATE = 0x102B;
    public const uint LVM_SETEXTENDEDLISTVIEWSTYLE = 0x1036, LVM_INSERTITEMW = 0x104D, LVM_INSERTCOLUMNW = 0x1061;
    public const uint LVM_SETITEMTEXTW = 0x1074;
    public const uint LVS_EX_FULLROWSELECT = 0x0020, LVS_EX_DOUBLEBUFFER = 0x00010000;
    public const uint LVCF_FMT = 0x1, LVCF_WIDTH = 0x2, LVCF_TEXT = 0x4, LVIF_TEXT = 0x1, LVIF_STATE = 0x8;
    public const uint LVIS_FOCUSED = 0x1, LVIS_SELECTED = 0x2, LVNI_SELECTED = 0x2;
    public const int LVCFMT_RIGHT = 1, NM_DBLCLK = -3, LVN_KEYDOWN = -155, VK_BACK = 0x08;

    public const int SW_SHOW = 5, COLOR_BTNFACE = 15, IDC_ARROW = 32512, IDC_WAIT = 32514;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(IntPtr hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    public static partial ushort RegisterClassEx(WNDCLASSEXW* wc);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateWindowEx(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(IntPtr hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(MSG* msg, IntPtr hWnd, uint min, uint max);

    [LibraryImport("user32.dll", EntryPoint = "IsDialogMessageW")]
    public static partial int IsDialogMessage(IntPtr hDlg, MSG* msg);

    [LibraryImport("user32.dll")]
    public static partial int TranslateMessage(MSG* msg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(MSG* msg);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    public static partial int EnableWindow(IntPtr hWnd, int enable);

    [LibraryImport("user32.dll")]
    public static partial int DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ShowWindow(IntPtr hWnd, int cmd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr SetFocus(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetDlgItem(IntPtr hDlg, int id);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW")]
    public static partial int SetWindowText(IntPtr hWnd, char* text);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetFocus();

    [LibraryImport("user32.dll")]
    public static partial int IsChild(IntPtr parent, IntPtr hWnd);

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetLongPathName(string shortPath, char* buffer, uint bufferLength);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    public static partial int GetWindowTextLength(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    public static partial int GetWindowText(IntPtr hWnd, char* buffer, int maxCount);

    [LibraryImport("user32.dll")]
    public static partial int GetWindowRect(IntPtr hWnd, RECT* rect);

    [LibraryImport("user32.dll")]
    public static partial int AdjustWindowRectEx(RECT* rect, uint style, int menu, uint exStyle);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static partial IntPtr LoadCursor(IntPtr instance, nint cursorName);

    [LibraryImport("user32.dll")]
    public static partial IntPtr SetCursor(IntPtr cursor);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string faceName);

    [LibraryImport("gdi32.dll")]
    public static partial int DeleteObject(IntPtr obj);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandle(string? moduleName);

    public static nint SendMessage(IntPtr hWnd, uint msg, nuint wParam, string text)
    {
        fixed (char* p = text)
            return SendMessage(hWnd, msg, wParam, (nint)p);
    }

    /// <summary>Forma larga de una ruta existente (sin nombres 8.3 como DAVIDS~1).</summary>
    public static string GetLongPathName(string path)
    {
        var buffer = new char[1024];
        fixed (char* p = buffer)
        {
            uint len = GetLongPathName(path, p, (uint)buffer.Length);
            return len > 0 && len < buffer.Length ? new string(buffer, 0, (int)len) : path;
        }
    }
}

/// <summary>Cursor de espera mientras dura una operación de red en el hilo de la interfaz.</summary>
internal readonly struct WaitCursor : IDisposable
{
    private readonly IntPtr _previous;

    public WaitCursor() => _previous = Win32.SetCursor(Win32.LoadCursor(IntPtr.Zero, Win32.IDC_WAIT));

    public void Dispose() => Win32.SetCursor(_previous);
}
