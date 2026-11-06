using System.Runtime.InteropServices;
using Renci.SshNet;
using Renci.SshNet.Common;
using static SshOpen.Win32;

namespace SshOpen;

/// <summary>
/// Panel acoplable de Notepad++ con el árbol de carpetas del servidor (<see cref="RemoteTree"/>)
/// y una conexión abierta mientras está conectado (<see cref="PanelConnection"/>). Las carpetas
/// se cargan al desplegarlas. Doble clic o Intro sobre un fichero lo abre; F5 recarga.
///
/// Reentrada: las operaciones de red muestran diálogos modales (progreso, huella, errores) que
/// bombean mensajes. Por eso nada se hace dentro de una notificación del TreeView (se aplaza con
/// PostMessage) y, mientras hay una operación en curso (<see cref="_busy"/>), se ignoran clics.
/// </summary>
internal sealed unsafe class RemotePanel : NativeWindow
{
    /// <summary>Índice del comando "Panel remoto" en el menú (dlgID del panel).</summary>
    public const int MenuIndex = 2;

    private const int IdConnect = 301, IdRefresh = 302, IdDisconnect = 303, IdStatus = 304, IdHidden = 305, IdTree = 306;

    // Acciones aplazadas; wParam = Id del nodo.
    private const uint WM_EXPAND_NODE = WM_APP + 1, WM_OPEN_NODE = WM_APP + 2, WM_REFRESH = WM_APP + 3;

    /// <summary>El panel es único: se crea la primera vez que se muestra y vive hasta cerrar Notepad++.</summary>
    private static RemotePanel? _instance;
    private static bool _visible;

    private readonly PanelConnection _connection;
    private RemoteTree _tree = null!;
    private TbData* _tbData;
    private bool _busy;

    private RemotePanel() => _connection = new PanelConnection(onLost: ShowDisconnected);

    protected override HWND ErrorOwner => Npp.Handle;

    /// <summary>Hay un diálogo del plugin o una operación del panel en marcha.</summary>
    private bool Busy => _busy || ModalDialog.AnyOpen;

    // --- Mostrar / ocultar ---

    /// <summary>Muestra u oculta el panel (por defecto alterna). Lo crea y registra la primera vez.</summary>
    public static void Toggle(bool? show = null)
    {
        bool target = show ?? !_visible;
        if (target)
        {
            _instance ??= Create();
            Npp.ShowDockingPanel(_instance.Handle);
        }
        else if (_instance != null)
        {
            Npp.HideDockingPanel(_instance.Handle);
        }
        SetVisible(target);
    }

    private static void SetVisible(bool visible)
    {
        _visible = visible;
        Npp.SetMenuItemCheck(Exports.CmdId(MenuIndex), visible);
        Config.SetFlag("panel", visible);
    }

    /// <summary>Notepad++ se cierra: se suelta la conexión.</summary>
    public static void Shutdown()
    {
        if (_instance is not { } panel)
            return;
        panel._connection.Close();
        if (panel.Handle != default)
            Npp.RegisterModeless(panel.Handle, add: false);
    }

    private static RemotePanel Create()
    {
        var panel = new RemotePanel();
        panel.Build();
        return panel;
    }

    private void Build()
    {
        uint dpi = DpiOf(Npp.Handle);
        // Ventana hija oculta: el gestor de paneles de Notepad++ la recoloca y la muestra.
        CreateHandle("SshOpenPanel", WS_EX_CONTROLPARENT, WS_CHILD | WS_CLIPCHILDREN, Plugin.Name,
            0, 0, (int)(260 * dpi / 96), (int)(400 * dpi / 96), Npp.Handle, dpi);

        Control("BUTTON", Tr.Connect, WS_TABSTOP | BS_PUSHBUTTON, IdConnect);
        Control("BUTTON", Tr.Refresh, WS_TABSTOP | BS_PUSHBUTTON, IdRefresh);
        Control("BUTTON", Tr.Disconnect, WS_TABSTOP | BS_PUSHBUTTON, IdDisconnect);
        Control("STATIC", Tr.NotConnected, SS_ENDELLIPSIS | SS_NOPREFIX, IdStatus);
        Control("BUTTON", Tr.HiddenShort, WS_TABSTOP | BS_AUTOCHECKBOX, IdHidden);
        SyncHiddenCheckbox();
        _tree = new RemoteTree(Control("SysTreeView32", "",
            WS_TABSTOP | WS_BORDER | TVS_HASBUTTONS | TVS_HASLINES | TVS_LINESATROOT | TVS_SHOWSELALWAYS, IdTree));
        UpdateButtons();

        // Registro como panel acoplable. La estructura y sus cadenas deben vivir siempre.
        _tbData = (TbData*)NativeMemory.AllocZeroed((nuint)sizeof(TbData));
        _tbData->hClient = Handle;
        _tbData->pszName = Marshal.StringToHGlobalUni(Plugin.Name);
        _tbData->dlgID = MenuIndex;
        _tbData->uMask = Npp.DWS_DF_CONT_LEFT;
        _tbData->pszModuleName = Marshal.StringToHGlobalUni(Plugin.Name + ".dll");
        Npp.RegisterDockingPanel(_tbData);
        Npp.RegisterModeless(Handle, add: true);
    }

    /// <summary>Control con tamaño provisional: <see cref="Layout"/> lo coloca.</summary>
    private HWND Control(string cls, string text, uint style, int id) => ChildPx(0, cls, text, style, 0, 0, 10, 10, id);

    private void Layout(int width, int height)
    {
        int pad = S(4), row = S(26);
        int buttonWidth = Math.Max(S(40), (width - 4 * pad) / 3);
        for (int i = 0; i < 3; i++)
            MoveWindow(Item(IdConnect + i), pad + i * (buttonWidth + pad), pad, buttonWidth, row, true);

        int y = pad + row + pad;
        int checkWidth = S(70);
        MoveWindow(Item(IdStatus), pad, y + S(3), Math.Max(0, width - 3 * pad - checkWidth), S(18), true);
        MoveWindow(Item(IdHidden), width - pad - checkWidth, y, checkWidth, S(22), true);

        y += S(22) + pad;
        MoveWindow(Item(IdTree), pad, y, Math.Max(0, width - 2 * pad), Math.Max(0, height - y - pad), true);
    }

    protected override void OnDestroyed() => _instance = null;

    // --- Mensajes ---

    protected override nint? OnMessage(uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case WM_SIZE:
                Layout(LoWord((nuint)(nint)lParam), HiWord((nuint)(nint)lParam));
                return 0;

            case DM_GETDEFID:
                return 0; // sin botón por defecto: Intro llega como IDOK

            case WM_COMMAND:
                OnCommand(LoWord(wParam), HiWord(wParam));
                return 0;

            case WM_NOTIFY:
                return OnNotify((NMHDR*)(nint)lParam);

            case WM_EXPAND_NODE:
            case WM_OPEN_NODE:
            case WM_REFRESH:
                if (!Busy && _tree.FromId((nint)(nuint)wParam) is { } node)
                    RunBusy(() =>
                    {
                        if (msg == WM_EXPAND_NODE) Expand(node);
                        else if (msg == WM_OPEN_NODE) Open(node);
                        else Refresh(node);
                    });
                return 0;
        }
        return null;
    }

    private void OnCommand(int id, int code)
    {
        if (Busy)
        {
            // BS_AUTOCHECKBOX ya cambió la marca: se deja como estaba.
            if (id == IdHidden)
                SyncHiddenCheckbox();
            return;
        }
        if (id == IDOK)
        {
            // Intro (vía IsDialogMessage) con el foco en el árbol.
            if (GetFocus() == _tree.Handle && _tree.Selected is { } node)
                Post(node.IsDirectory ? WM_EXPAND_NODE : WM_OPEN_NODE, node);
            return;
        }
        if ((uint)code != BN_CLICKED)
            return;
        switch (id)
        {
            case IdConnect: RunBusy(Connect); break;
            case IdRefresh when _tree.Selected is { } node: Post(WM_REFRESH, node); break;
            case IdDisconnect: Disconnect(); break;
            case IdHidden: RunBusy(ToggleHidden); break;
        }
    }

    private nint OnNotify(NMHDR* header)
    {
        if (header->code == Npp.DMN_CLOSE)
        {
            // El usuario cerró el panel con su "X": Notepad++ lo oculta.
            SetVisible(false);
            return 0;
        }
        if (header->idFrom != IdTree)
            return 0;

        switch (header->code)
        {
            case TVN_ITEMEXPANDINGW:
            {
                // Una carpeta sin cargar no se despliega aquí: se carga fuera de la notificación
                // y luego se despliega con TVM_EXPAND.
                var info = (NMTREEVIEWW*)header;
                if ((info->action & NM_TREEVIEW_ACTION.TVE_EXPAND) == 0
                    || _tree.FromId(info->itemNew.lParam) is not { Loaded: false } node)
                    return 0;
                if (!Busy)
                    Post(WM_EXPAND_NODE, node);
                return 1;
            }
            case NM_DBLCLK:
                // Para carpetas, el doble clic ya despliega el nodo.
                if (!Busy && _tree.Selected is { IsDirectory: false } file)
                    Post(WM_OPEN_NODE, file);
                return 0;
            case TVN_KEYDOWN:
                if (((NMTVKEYDOWN*)header)->wVKey == VK_F5 && !Busy && _tree.Selected is { } selected)
                    Post(WM_REFRESH, selected);
                return 0;
        }
        return 0;
    }

    private void Post(uint msg, RemoteNode node) => PostMessage(Handle, msg, (nuint)node.Id, 0);

    private void RunBusy(Action action)
    {
        _busy = true;
        try
        {
            action();
        }
        finally
        {
            _busy = false;
        }
    }

    // --- Conexión ---

    private void Connect()
    {
        var (last, remember, profile) = Config.LoadLast();
        var options = new ConnectOptions { Remember = remember, Profile = profile };
        if (Connector.ConnectWithDialog(Tr.ConnectPanelTitle, _connection.Request ?? last, options) is not { } connection)
            return;

        var (client, request) = connection;
        string start;
        try
        {
            start = Connector.StartFolder(client, request.RemotePath, out _);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        Config.SaveLast(request with { RemotePath = start }, options.Remember, options.Profile);

        _connection.Attach(client, request);
        SetText(IdStatus, $"{request.User}@{request.Host}");
        UpdateButtons();
        Rebuild(start);
        SetFocus(_tree.Handle);
    }

    private void Disconnect()
    {
        _connection.Close();
        ShowDisconnected();
    }

    /// <summary>Vacía el árbol y deja el panel como "Sin conexión". Nunca desde una notificación del árbol.</summary>
    private void ShowDisconnected()
    {
        if (Handle == default)
            return;
        _tree.Clear();
        SetText(IdStatus, Tr.NotConnected);
        UpdateButtons();
    }

    // --- Árbol ---

    /// <summary>Vacía el árbol, crea la raíz "/" y despliega hasta <paramref name="path"/>.</summary>
    private void Rebuild(string path)
    {
        var node = _tree.Reset();
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; ; i++)
        {
            bool last = i == parts.Length;
            if (!node.Loaded && !Load(node, showError: last))
            {
                if (last || !_connection.IsOpen)
                    break;
                // Carpeta intermedia sin permiso de lectura (p. ej. /home con 711): se crea
                // solo el siguiente nivel para poder llegar a la carpeta inicial.
                _tree.ReplaceChildren(node, [new RemoteNode(RemoteNode.Combine(node.Path, parts[i]), true)]);
            }
            _tree.Expand(node);
            if (last)
                break;
            string next = RemoteNode.Combine(node.Path, parts[i]);
            if (_tree.ChildrenOf(node).FirstOrDefault(c => c.Path == next) is not { IsDirectory: true } child)
                break;
            node = child;
        }
        _tree.Select(node);
    }

    private void Expand(RemoteNode node)
    {
        if (node.Loaded || Load(node, showError: true))
            _tree.Expand(node);
    }

    private void Refresh(RemoteNode node)
    {
        if (!node.IsDirectory && _tree.ParentOf(node) is { } folder)
            node = folder;
        if (node.IsDirectory && Load(node, showError: true))
            _tree.Expand(node);
    }

    /// <summary>Lista la carpeta del nodo (fuera del hilo de la interfaz) y sustituye sus hijos. False si falla.</summary>
    private bool Load(RemoteNode node, bool showError)
    {
        List<RemoteNode> children;
        bool showHidden = Config.GetFlag("hidden", true);
        try
        {
            children = _connection.Run((client, _) =>
                ProgressDialog.Run(Npp.Handle, Tr.Listing(node.Path), _ => List(client, node.Path, showHidden)));
        }
        catch (OperationCanceledException)
        {
            // Si se canceló a medias, la conexión puede seguir ocupada en otro hilo: se cierra.
            if (_connection.IsOpen)
                Disconnect();
            return false;
        }
        catch (Exception ex)
        {
            if (showError)
                Prompts.Error(Tr.CannotList(node.Path), ex);
            return false;
        }

        if (Handle == default || !_tree.Contains(node)) // el árbol se vació mientras tanto
            return false;
        _tree.ReplaceChildren(node, children);
        return true;
    }

    /// <summary>Contenido de una carpeta, carpetas primero; se ejecuta en un hilo de trabajo.</summary>
    private static List<RemoteNode> List(SftpClient client, string folder, bool showHidden)
    {
        var result = new List<RemoteNode>();
        foreach (var f in client.ListDirectory(folder))
        {
            if (f.Name is "." or ".." || (!showHidden && f.Name.StartsWith('.')))
                continue;
            bool isDirectory = f.IsDirectory;
            if (f.IsSymbolicLink)
            {
                try
                {
                    isDirectory = client.GetAttributes(f.FullName).IsDirectory; // sigue el enlace
                }
                catch (Exception ex) when (ex is SftpPathNotFoundException or SftpPermissionDeniedException)
                {
                    isDirectory = false; // enlace roto o inaccesible: se muestra como fichero
                }
            }
            // Ruta construida con el nombre: SSH.NET da FullName con la carpeta ya resuelta
            // (realpath), y si una carpeta es un enlace no coincidiría con la ruta del árbol.
            result.Add(new RemoteNode(RemoteNode.Combine(folder, f.Name), isDirectory));
        }
        return result
            .OrderBy(n => n.IsDirectory ? 0 : 1)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void Open(RemoteNode node)
    {
        try
        {
            _connection.Run((client, request) =>
            {
                Plugin.OpenFile(client, request, node.Path);
                return true;
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Prompts.Error(Tr.CannotOpen(node.Path), ex);
        }
    }

    private void ToggleHidden()
    {
        Config.SetFlag("hidden", IsChecked(IdHidden));
        if (_connection.IsOpen)
            Rebuild(_tree.Selected?.Path ?? "/");
    }

    // --- Controles ---

    private void SyncHiddenCheckbox() => SetChecked(IdHidden, Config.GetFlag("hidden", true));

    private void UpdateButtons()
    {
        Enable(IdRefresh, _connection.IsOpen);
        Enable(IdDisconnect, _connection.IsOpen);
    }
}
