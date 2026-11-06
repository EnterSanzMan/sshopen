using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static SshOpen.Win32;

namespace SshOpen;

/// <summary>
/// Ventana Win32 del plugin como objeto: cada diálogo o panel guarda su estado en su instancia.
/// Windows llama a un único procedimiento de ventana estático, que localiza el objeto por su
/// HWND y le pasa el mensaje (<see cref="OnMessage"/>). También aporta la fuente y el escalado
/// por DPI y los atajos para crear y leer controles.
/// </summary>
internal abstract unsafe class NativeWindow
{
    private static readonly Dictionary<nint, NativeWindow> Live = [];
    private static readonly HashSet<string> Registered = [];
    private static bool _commonControlsReady;

    // Ventana que se está creando: recibe los primeros mensajes antes de que CreateWindowEx devuelva su HWND.
    private static NativeWindow? _creating;

    public HWND Handle { get; private set; }

    protected uint Dpi { get; private set; } = 96;

    protected HFONT Font { get; private set; }

    /// <summary>Escala una medida pensada para 96 ppp al DPI de la ventana.</summary>
    protected int S(int value) => (int)(value * Dpi / 96);

    /// <summary>Propietario de los mensajes de error que se produzcan dentro de esta ventana.</summary>
    protected virtual HWND ErrorOwner => Handle;

    /// <summary>Procesa un mensaje; null = comportamiento por defecto (DefWindowProc).</summary>
    protected abstract nint? OnMessage(uint msg, WPARAM wParam, LPARAM lParam);

    /// <summary>La ventana ya se destruyó (WM_NCDESTROY).</summary>
    protected virtual void OnDestroyed()
    {
    }

    /// <summary>Crea la ventana con su clase (registrada la primera vez), la fuente y el DPI indicados.</summary>
    protected void CreateHandle(string className, uint exStyle, uint style, string title,
        int x, int y, int width, int height, HWND parent, uint dpi)
    {
        EnsureCommonControls();
        RegisterClass(className);
        Dpi = dpi;
        Font = CreateUiFont(dpi);
        _creating = this;
        HWND hwnd;
        try
        {
            fixed (char* cls = className)
            fixed (char* text = title)
                hwnd = CreateWindowEx((WINDOW_EX_STYLE)exStyle, cls, text, (WINDOW_STYLE)style,
                    x, y, width, height, parent, default, Instance, null);
        }
        finally
        {
            _creating = null;
        }
        if (hwnd == default)
        {
            DeleteObject(Font);
            Font = default;
            Live.Remove(Handle);
            throw new InvalidOperationException(Tr.WindowCreateFailed);
        }
    }

    /// <summary>Crea un control hijo; posición y tamaño en unidades de 96 ppp.</summary>
    protected HWND Child(uint exStyle, string cls, string text, uint style, int x, int y, int w, int h, int id) =>
        ChildPx(exStyle, cls, text, style, S(x), S(y), S(w), S(h), id);

    /// <summary>Crea un control hijo con posición y tamaño ya en píxeles (los que se recolocan en WM_SIZE).</summary>
    protected HWND ChildPx(uint exStyle, string cls, string text, uint style, int x, int y, int w, int h, int id)
    {
        HWND hwnd;
        fixed (char* c = cls)
        fixed (char* t = text)
            hwnd = CreateWindowEx((WINDOW_EX_STYLE)exStyle, c, t, (WINDOW_STYLE)(WS_CHILD | WS_VISIBLE | style),
                x, y, w, h, Handle, (HMENU)(nint)id, Instance, null);
        SendMessage(hwnd, WM_SETFONT, (nuint)(nint)Font, 1);
        return hwnd;
    }

    protected HWND Item(int id) => GetDlgItem(Handle, id);

    protected string Text(int id)
    {
        HWND ctl = Item(id);
        int len = GetWindowTextLength(ctl);
        var buffer = new char[len + 1];
        fixed (char* p = buffer)
            GetWindowText(ctl, p, buffer.Length);
        return new string(buffer, 0, len);
    }

    protected void SetText(int id, string text)
    {
        fixed (char* p = text)
            SetWindowText(Item(id), p);
    }

    protected bool IsChecked(int id) => SendMessage(Item(id), BM_GETCHECK, 0, 0) == 1;

    protected void SetChecked(int id, bool value) => SendMessage(Item(id), BM_SETCHECK, value ? 1u : 0u, 0);

    protected void Enable(int id, bool enabled) => EnableWindow(Item(id), enabled);

    // --- Infraestructura común ---

    /// <summary>Lista, árbol y barra de progreso: se registran una vez para todo el plugin.</summary>
    private static void EnsureCommonControls()
    {
        if (_commonControlsReady)
            return;
        var icc = new INITCOMMONCONTROLSEX
        {
            dwSize = (uint)sizeof(INITCOMMONCONTROLSEX),
            dwICC = INITCOMMONCONTROLSEX_ICC.ICC_LISTVIEW_CLASSES | INITCOMMONCONTROLSEX_ICC.ICC_TREEVIEW_CLASSES
                | INITCOMMONCONTROLSEX_ICC.ICC_PROGRESS_CLASS,
        };
        InitCommonControlsEx(&icc);
        _commonControlsReady = true;
    }

    /// <summary>Fuente de la interfaz (Segoe UI 9 pt) para ese DPI; la ventana la libera al destruirse.</summary>
    private static HFONT CreateUiFont(uint dpi)
    {
        fixed (char* face = "Segoe UI")
            return CreateFont(-(int)(12 * dpi / 96), 0, 0, 0, 400, 0, 0, 0, FONT_CHARSET.DEFAULT_CHARSET,
                FONT_OUTPUT_PRECISION.OUT_DEFAULT_PRECIS, FONT_CLIP_PRECISION.CLIP_DEFAULT_PRECIS,
                FONT_QUALITY.CLEARTYPE_QUALITY, 0, face);
    }

    /// <summary>DPI del monitor de la ventana (96 en Windows anteriores a 10 1607).</summary>
    public static uint DpiOf(HWND hwnd)
    {
        try
        {
#pragma warning disable CA1416 // en Windows anteriores salta EntryPointNotFoundException y se usa 96
            uint dpi = GetDpiForWindow(hwnd);
#pragma warning restore CA1416
            return dpi == 0 ? 96 : dpi;
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }

    private static void RegisterClass(string className)
    {
        if (!Registered.Add(className))
            return;
        fixed (char* name = className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = Instance,
                hCursor = LoadCursor(default(HINSTANCE), IDC_ARROW),
                hbrBackground = (HBRUSH)(nint)((int)SYS_COLOR_INDEX.COLOR_BTNFACE + 1),
                lpszClassName = name,
            };
            RegisterClassEx(&wc);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (!Live.TryGetValue(hwnd, out var window))
        {
            if (_creating is null)
                return DefWindowProc(hwnd, msg, wParam, lParam);
            window = _creating;
            window.Handle = hwnd;
            Live[hwnd] = window;
        }

        nint? result = null;
        try
        {
            result = window.OnMessage(msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            // Ninguna excepción puede volver a Windows: tumbaría Notepad++.
            try
            {
                Prompts.Error(window.ErrorOwner, Tr.UnexpectedError, ex);
            }
            catch
            {
            }
        }

        LRESULT value = result is { } r ? (LRESULT)r : DefWindowProc(hwnd, msg, wParam, lParam);
        if (msg == WM_NCDESTROY)
        {
            Live.Remove(hwnd);
            DeleteObject(window.Font);
            window.Font = default;
            window.Handle = default;
            try
            {
                window.OnDestroyed();
            }
            catch
            {
            }
        }
        return value;
    }
}
