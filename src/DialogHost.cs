using static SshOpen.Win32;

namespace SshOpen;

/// <summary>
/// Utilidades comunes de los diálogos Win32 del plugin: ventana centrada sobre Notepad++,
/// controles hijos con la fuente de la interfaz, escalado por DPI y bucle modal.
/// Solo hay un diálogo abierto a la vez, así que el estado es estático.
/// </summary>
internal static unsafe class DialogHost
{
    private static readonly HashSet<string> Registered = [];
    private static bool _commonControlsReady;
    private static uint _dpi = 96;
    private static IntPtr _savedFocus;

    public static IntPtr Font { get; private set; }

    /// <summary>Hay un diálogo del plugin abierto (el estado estático no admite dos a la vez).</summary>
    public static bool IsOpen { get; private set; }

    /// <summary>Escala una medida pensada para 96 ppp al DPI del monitor de Notepad++.</summary>
    public static int S(int value) => (int)(value * _dpi / 96);

    public static IntPtr CreateFrame(IntPtr owner, string className, delegate* unmanaged<IntPtr, uint, nuint, nint, nint> wndProc,
        string title, int clientWidth, int clientHeight)
    {
        if (IsOpen)
            throw new InvalidOperationException("Ya hay un diálogo de " + Plugin.Name + " abierto.");
        if (!_commonControlsReady)
        {
            var icc = new INITCOMMONCONTROLSEX { dwSize = (uint)sizeof(INITCOMMONCONTROLSEX), dwICC = ICC_LISTVIEW_CLASSES | ICC_PROGRESS_CLASS };
            InitCommonControlsEx(&icc);
            _commonControlsReady = true;
        }
        Register(className, wndProc);
        _dpi = GetDpi(owner);
        _savedFocus = IntPtr.Zero;
        Font = CreateFont(-S(12), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");

        const uint style = WS_POPUP | WS_CAPTION | WS_SYSMENU;
        const uint exStyle = WS_EX_DLGMODALFRAME | WS_EX_CONTROLPARENT;
        var rc = new RECT { Right = S(clientWidth), Bottom = S(clientHeight) };
        AdjustWindowRectEx(&rc, style, 0, exStyle);
        int width = rc.Right - rc.Left, height = rc.Bottom - rc.Top;
        RECT owr;
        GetWindowRect(owner, &owr);
        int x = owr.Left + (owr.Right - owr.Left - width) / 2;
        int y = owr.Top + (owr.Bottom - owr.Top - height) / 3;

        IntPtr hwnd = CreateWindowEx(exStyle, className, title, style, x, y, width, height,
            owner, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            // Sin esta comprobación el bucle modal nunca terminaría.
            DeleteObject(Font);
            Font = IntPtr.Zero;
            throw new InvalidOperationException("No se pudo crear la ventana del diálogo.");
        }
        IsOpen = true;
        return hwnd;
    }

    /// <summary>
    /// WM_ACTIVATE: DefWindowProc deja el foco en el marco al volver al diálogo (tras un
    /// MessageBox o Alt+Tab), así que se recuerda el control enfocado y se restaura.
    /// </summary>
    public static void OnActivate(IntPtr hwnd, nuint wParam, IntPtr fallback)
    {
        if ((wParam & 0xFFFF) == 0)
        {
            _savedFocus = GetFocus();
            return;
        }
        SetFocus(_savedFocus != IntPtr.Zero && IsChild(hwnd, _savedFocus) != 0 ? _savedFocus : fallback);
    }

    private static uint GetDpi(IntPtr hwnd)
    {
        try
        {
            uint dpi = GetDpiForWindow(hwnd); // Windows 10 1607+
            return dpi == 0 ? 96 : dpi;
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }

    /// <summary>Crea un control hijo; posición y tamaño en unidades de 96 ppp.</summary>
    public static IntPtr Child(IntPtr parent, uint exStyle, string cls, string text, uint style,
        int x, int y, int w, int h, int id)
    {
        IntPtr hwnd = CreateWindowEx(exStyle, cls, text, WS_CHILD | WS_VISIBLE | style, S(x), S(y), S(w), S(h),
            parent, id, GetModuleHandle(null), IntPtr.Zero);
        SendMessage(hwnd, WM_SETFONT, (nuint)Font, 1);
        return hwnd;
    }

    /// <summary>Bucle de mensajes modal: deshabilita Notepad++ hasta que <paramref name="isDone"/> sea true.</summary>
    public static void RunModal(IntPtr owner, IntPtr hwnd, Func<bool> isDone)
    {
        EnableWindow(owner, 0);
        ShowWindow(hwnd, SW_SHOW);

        MSG msg;
        while (!isDone())
        {
            int r = GetMessage(&msg, IntPtr.Zero, 0, 0);
            if (r <= 0)
            {
                if (r == 0)
                    PostQuitMessage((int)msg.wParam); // devolver WM_QUIT al bucle de Notepad++
                break;
            }
            if (IsDialogMessage(hwnd, &msg) == 0)
            {
                TranslateMessage(&msg);
                DispatchMessage(&msg);
            }
        }

        EnableWindow(owner, 1);
        DestroyWindow(hwnd);
        DeleteObject(Font);
        Font = IntPtr.Zero;
        IsOpen = false;
    }

    public static string Text(IntPtr parent, int id)
    {
        IntPtr ctl = GetDlgItem(parent, id);
        int len = GetWindowTextLength(ctl);
        var buffer = new char[len + 1];
        fixed (char* p = buffer)
            GetWindowText(ctl, p, buffer.Length);
        return new string(buffer, 0, len);
    }

    public static void SetText(IntPtr parent, int id, string text)
    {
        fixed (char* p = text)
            SetWindowText(GetDlgItem(parent, id), p);
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };

    private static void Register(string className, delegate* unmanaged<IntPtr, uint, nuint, nint, nint> wndProc)
    {
        if (!Registered.Add(className))
            return;
        fixed (char* name = className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = wndProc,
                hInstance = GetModuleHandle(null),
                hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
                hbrBackground = COLOR_BTNFACE + 1,
                lpszClassName = name,
            };
            RegisterClassEx(&wc);
        }
    }
}
