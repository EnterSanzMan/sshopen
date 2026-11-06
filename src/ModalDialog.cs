using static SshOpen.Win32;

namespace SshOpen;

/// <summary>
/// Diálogo modal del plugin: ventana centrada sobre su propietario, bucle de mensajes propio
/// con navegación por teclado (IsDialogMessage) y el propietario deshabilitado mientras dura.
/// Cada diálogo es un objeto; uno puede abrirse encima de otro.
/// </summary>
internal abstract unsafe class ModalDialog : NativeWindow
{
    /// <summary>Diálogos del plugin abiertos ahora mismo (todos comparten el hilo de la interfaz).</summary>
    private static int _openCount;

    private HWND _owner;
    private HWND _savedFocus;
    private bool _open;

    /// <summary>Hay algún diálogo del plugin abierto: no conviene abrir flujos que pidan datos.</summary>
    public static bool AnyOpen => _openCount > 0;

    /// <summary>Pone fin al bucle modal.</summary>
    protected bool Done { get; set; }

    /// <summary>Control que recibe el foco al volver al diálogo si no había otro recordado.</summary>
    protected abstract HWND DefaultFocus { get; }

    /// <summary>Crea el marco del diálogo (aún oculto) centrado sobre <paramref name="owner"/>.</summary>
    protected void CreateFrame(HWND owner, string className, string title, int clientWidth, int clientHeight)
    {
        _owner = owner;
        uint dpi = DpiOf(owner);
        int Scale(int v) => (int)(v * dpi / 96);

        const uint style = WS_POPUP | WS_CAPTION | WS_SYSMENU;
        const uint exStyle = WS_EX_DLGMODALFRAME | WS_EX_CONTROLPARENT;
        var rc = new RECT { right = Scale(clientWidth), bottom = Scale(clientHeight) };
        AdjustWindowRectEx(&rc, (WINDOW_STYLE)style, false, (WINDOW_EX_STYLE)exStyle);
        int width = rc.right - rc.left, height = rc.bottom - rc.top;
        RECT owr;
        GetWindowRect(owner, &owr);
        int x = owr.left + (owr.right - owr.left - width) / 2;
        int y = owr.top + (owr.bottom - owr.top - height) / 3;

        CreateHandle(className, exStyle, style, title, x, y, width, height, owner, dpi);
        _open = true;
        _openCount++;
        EnableWindow(owner, false);
    }

    /// <summary>Muestra el diálogo con el modo oscuro de Notepad++ aplicado (debe tener ya sus controles).</summary>
    protected void ShowFrame()
    {
        Npp.ApplyDarkMode(Handle);
        ShowWindow(Handle, SHOW_WINDOW_CMD.SW_SHOW);
    }

    /// <summary>Bucle de mensajes modal hasta que <see cref="Done"/>; después cierra el diálogo.</summary>
    /// <param name="show">false: el diálogo se muestra él mismo más tarde (o nunca).</param>
    protected void RunModal(bool show = true)
    {
        try
        {
            if (show && !IsWindowVisible(Handle))
                ShowFrame();

            MSG msg;
            while (!Done)
            {
                int r = GetMessage(&msg, default, 0, 0);
                if (r <= 0)
                {
                    if (r == 0)
                        PostQuitMessage((int)(nuint)msg.wParam); // devolver WM_QUIT al bucle de Notepad++
                    break;
                }
                if (!IsDialogMessage(Handle, &msg))
                {
                    TranslateMessage(&msg);
                    DispatchMessage(&msg);
                }
            }
        }
        finally
        {
            Close();
        }
    }

    /// <summary>Rehabilita el propietario y destruye el diálogo. Se puede llamar más de una vez.</summary>
    protected void Close()
    {
        if (!_open)
            return;
        _open = false;
        _openCount--;
        EnableWindow(_owner, true); // antes de destruir, para que Windows active al propietario
        DestroyWindow(Handle);
    }

    /// <summary>Mensajes comunes; los demás van a <see cref="OnDialogMessage"/>.</summary>
    protected sealed override nint? OnMessage(uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case WM_ACTIVATE:
                // DefWindowProc deja el foco en el marco al volver al diálogo (tras un MessageBox o
                // Alt+Tab), así que se recuerda el control enfocado y se restaura.
                if (LoWord(wParam) == 0)
                {
                    _savedFocus = GetFocus();
                }
                else
                {
                    SetFocus(_savedFocus != default && IsChild(Handle, _savedFocus) ? _savedFocus : DefaultFocus);
                }
                return 0;
            case WM_CLOSE:
                OnCancel();
                return 0;
        }
        return OnDialogMessage(msg, wParam, lParam);
    }

    protected abstract nint? OnDialogMessage(uint msg, WPARAM wParam, LPARAM lParam);

    /// <summary>Si la ventana se destruye por otra vía, el bucle modal no puede seguir esperando.</summary>
    protected override void OnDestroyed() => Done = true;

    /// <summary>Cerrar con la X o Escape.</summary>
    protected virtual void OnCancel() => Done = true;

    /// <summary>Aviso que deja el foco en un control del diálogo.</summary>
    protected void Warn(string text, int focusId)
    {
        Message(Handle, text, MB_ICONWARNING);
        SetFocus(Item(focusId));
    }
}
