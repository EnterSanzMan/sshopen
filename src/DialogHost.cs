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
        EnsureCommonControls();
        RegisterClass(className, wndProc);
        _dpi = DpiOf(owner);
        _savedFocus = IntPtr.Zero;
        Font = CreateUiFont(_dpi);

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

    /// <summary>Lista, árbol y barra de progreso: se registran una vez para todo el plugin.</summary>
    public static void EnsureCommonControls()
    {
        if (_commonControlsReady)
            return;
        var icc = new INITCOMMONCONTROLSEX
        {
            dwSize = (uint)sizeof(INITCOMMONCONTROLSEX),
            dwICC = ICC_LISTVIEW_CLASSES | ICC_TREEVIEW_CLASSES | ICC_PROGRESS_CLASS,
        };
        InitCommonControlsEx(&icc);
        _commonControlsReady = true;
    }

    /// <summary>Fuente de la interfaz (Segoe UI 9 pt) para ese DPI; quien la crea la libera.</summary>
    public static IntPtr CreateUiFont(uint dpi) =>
        CreateFont(-(int)(12 * dpi / 96), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");

    /// <summary>DPI del monitor de la ventana (96 en Windows anteriores a 10 1607).</summary>
    public static uint DpiOf(IntPtr hwnd)
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
    /// <param name="show">false: el diálogo se encarga de mostrarse él mismo (o no) más tarde.</param>
    public static void RunModal(IntPtr owner, IntPtr hwnd, Func<bool> isDone, bool show = true)
    {
        EnableWindow(owner, 0);
        if (show && IsWindowVisible(hwnd) == 0)
            Show(hwnd);

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

    /// <summary>Muestra el diálogo con el modo oscuro de Notepad++ aplicado (debe tener ya sus controles).</summary>
    public static void Show(IntPtr hwnd)
    {
        Npp.ApplyDarkMode(hwnd);
        ShowWindow(hwnd, SW_SHOW);
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

    /// <summary>Registra una clase de ventana del plugin (una sola vez por nombre).</summary>
    public static void RegisterClass(string className, delegate* unmanaged<IntPtr, uint, nuint, nint, nint> wndProc)
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
