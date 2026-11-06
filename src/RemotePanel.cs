using System.Net.Sockets;
using System.Runtime.InteropServices;
using Renci.SshNet;
using Renci.SshNet.Common;
using static SshOpen.Win32;

namespace SshOpen;

/// <summary>
/// Panel acoplable de Notepad++ con el árbol de carpetas del servidor. Mantiene una conexión
/// SFTP abierta mientras está conectado; las carpetas se cargan al desplegarlas.
/// Doble clic o Intro sobre un fichero lo abre; F5 recarga la carpeta seleccionada.
///
/// Reentrada: las operaciones de red muestran diálogos modales (progreso, huella, errores) que
/// bombean mensajes. Por eso nada se hace dentro de una notificación del TreeView (se aplaza con
/// PostMessage) y, mientras hay una operación en curso (<see cref="_busy"/>), se ignoran clics.
/// </summary>
internal static unsafe class RemotePanel
{
    /// <summary>Índice del comando "Panel remoto" en el menú (dlgID del panel).</summary>
    public const int MenuIndex = 2;

    private const int IdConnect = 301, IdRefresh = 302, IdDisconnect = 303, IdStatus = 304, IdHidden = 305, IdTree = 306;
    private const string ClassName = "SshOpenPanel";

    // Acciones aplazadas; wParam = id del nodo.
    private const uint WM_EXPAND_NODE = WM_APP + 1, WM_OPEN_NODE = WM_APP + 2, WM_REFRESH = WM_APP + 3;

    private sealed class Node(string path, bool isDirectory)
    {
        public string Path { get; } = path;
        public bool IsDirectory { get; } = isDirectory;
        public bool Loaded { get; set; }
        public IntPtr Item { get; set; }
    }

    private static IntPtr _hwnd, _tree, _font;
    private static TbData* _tbData;
    private static uint _dpi = 96;
    private static bool _visible, _busy;

    private static SftpClient? _client;
    private static ConnectionRequest? _request;
    private static readonly Dictionary<nint, Node> Nodes = [];
    private static nint _nextId = 1;

    private static int S(int value) => (int)(value * _dpi / 96);

    /// <summary>Hay un diálogo del plugin o una operación del panel en marcha.</summary>
    private static bool Busy => _busy || DialogHost.IsOpen;

    // --- Mostrar / ocultar ---

    /// <summary>Muestra u oculta el panel (por defecto alterna). Lo crea y registra la primera vez.</summary>
    public static void Toggle(bool? show = null)
    {
        bool target = show ?? !_visible;
        if (target)
        {
            if (_hwnd == IntPtr.Zero)
                Create();
            Npp.ShowDockingPanel(_hwnd);
        }
        else if (_hwnd != IntPtr.Zero)
        {
            Npp.HideDockingPanel(_hwnd);
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
        DropConnection();
        if (_hwnd != IntPtr.Zero)
            Npp.RegisterModeless(_hwnd, add: false);
    }

    private static void Create()
    {
        var icc = new INITCOMMONCONTROLSEX { dwSize = (uint)sizeof(INITCOMMONCONTROLSEX), dwICC = ICC_TREEVIEW_CLASSES };
        InitCommonControlsEx(&icc);

        fixed (char* name = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = GetModuleHandle(null),
                hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
                hbrBackground = COLOR_BTNFACE + 1,
                lpszClassName = name,
            };
            RegisterClassEx(&wc);
        }

        try
        {
            uint dpi = GetDpiForWindow(Npp.Handle);
            _dpi = dpi == 0 ? 96 : dpi;
        }
        catch (EntryPointNotFoundException)
        {
            _dpi = 96;
        }
        _font = CreateFont(-S(12), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");

        // Ventana hija oculta: el gestor de paneles de Notepad++ la recoloca y la muestra.
        _hwnd = CreateWindowEx(WS_EX_CONTROLPARENT, ClassName, Plugin.Name, WS_CHILD | WS_CLIPCHILDREN,
            0, 0, S(260), S(400), Npp.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("No se pudo crear el panel remoto.");

        Control("BUTTON", "Conectar...", WS_TABSTOP | BS_PUSHBUTTON, IdConnect);
        Control("BUTTON", "Actualizar", WS_TABSTOP | BS_PUSHBUTTON, IdRefresh);
        Control("BUTTON", "Desconectar", WS_TABSTOP | BS_PUSHBUTTON, IdDisconnect);
        Control("STATIC", "Sin conexión", SS_ENDELLIPSIS | SS_NOPREFIX, IdStatus);
        Control("BUTTON", "Ocultos", WS_TABSTOP | BS_AUTOCHECKBOX, IdHidden);
        SyncHiddenCheckbox();
        _tree = Control("SysTreeView32", "",
            WS_TABSTOP | WS_BORDER | TVS_HASBUTTONS | TVS_HASLINES | TVS_LINESATROOT | TVS_SHOWSELALWAYS, IdTree);
        UpdateButtons();

        // Registro como panel acoplable. La estructura y sus cadenas deben vivir siempre.
        _tbData = (TbData*)NativeMemory.AllocZeroed((nuint)sizeof(TbData));
        _tbData->hClient = _hwnd;
        _tbData->pszName = Marshal.StringToHGlobalUni(Plugin.Name);
        _tbData->dlgID = MenuIndex;
        _tbData->uMask = Npp.DWS_DF_CONT_LEFT;
        _tbData->pszModuleName = Marshal.StringToHGlobalUni(Plugin.Name + ".dll");
        Npp.RegisterDockingPanel(_tbData);
        Npp.RegisterModeless(_hwnd, add: true);
    }

    private static IntPtr Control(string cls, string text, uint style, int id)
    {
        IntPtr hwnd = CreateWindowEx(0, cls, text, WS_CHILD | WS_VISIBLE | style, 0, 0, 10, 10,
            _hwnd, id, GetModuleHandle(null), IntPtr.Zero);
        SendMessage(hwnd, WM_SETFONT, (nuint)_font, 0);
        return hwnd;
    }

    private static void Layout(int width, int height)
    {
        int pad = S(4), row = S(26);
        int buttonWidth = Math.Max(S(40), (width - 4 * pad) / 3);
        for (int i = 0; i < 3; i++)
            MoveWindow(GetDlgItem(_hwnd, IdConnect + i), pad + i * (buttonWidth + pad), pad, buttonWidth, row, 1);

        int y = pad + row + pad;
        int checkWidth = S(70);
        MoveWindow(GetDlgItem(_hwnd, IdStatus), pad, y + S(3), Math.Max(0, width - 3 * pad - checkWidth), S(18), 1);
        MoveWindow(GetDlgItem(_hwnd, IdHidden), width - pad - checkWidth, y, checkWidth, S(22), 1);

        y += S(22) + pad;
        MoveWindow(_tree, pad, y, Math.Max(0, width - 2 * pad), Math.Max(0, height - y - pad), 1);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(IntPtr hwnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_SIZE:
                    Layout((int)(lParam & 0xFFFF), (int)((lParam >> 16) & 0xFFFF));
                    return 0;

                case DM_GETDEFID:
                    return 0; // sin botón por defecto: Intro llega como IDOK

                case WM_COMMAND:
                    OnCommand((int)(wParam & 0xFFFF), (int)(wParam >> 16));
                    return 0;

                case WM_NOTIFY:
                    return OnNotify((NMHDR*)lParam);

                case WM_EXPAND_NODE:
                case WM_OPEN_NODE:
                case WM_REFRESH:
                    if (!Busy && Nodes.TryGetValue((nint)wParam, out var node))
                        RunBusy(() =>
                        {
                            if (msg == WM_EXPAND_NODE) Expand(node);
                            else if (msg == WM_OPEN_NODE) Open(node);
                            else Refresh(node);
                        });
                    return 0;
            }
        }
        catch (Exception ex)
        {
            Plugin.ShowError("Error en el panel remoto", ex);
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void OnCommand(int id, int code)
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
            if (GetFocus() == _tree && SelectedNode() is { } node)
                Post(node.IsDirectory ? WM_EXPAND_NODE : WM_OPEN_NODE, node);
            return;
        }
        if (code != BN_CLICKED)
            return;
        switch (id)
        {
            case IdConnect: RunBusy(Connect); break;
            case IdRefresh when SelectedNode() is { } node: Post(WM_REFRESH, node); break;
            case IdDisconnect: DropConnection(); break;
            case IdHidden: RunBusy(ToggleHidden); break;
        }
    }

    private static nint OnNotify(NMHDR* header)
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
                if ((info->action & TVE_EXPAND) == 0 || !Nodes.TryGetValue(info->itemNew.lParam, out var node) || node.Loaded)
                    return 0;
                if (!Busy)
                    Post(WM_EXPAND_NODE, node);
                return 1;
            }
            case NM_DBLCLK:
                // Para carpetas, el doble clic ya despliega el nodo.
                if (!Busy && SelectedNode() is { IsDirectory: false } file)
                    Post(WM_OPEN_NODE, file);
                return 0;
            case TVN_KEYDOWN:
                ushort key = *(ushort*)((byte*)header + sizeof(NMHDR));
                if (key == VK_F5 && !Busy && SelectedNode() is { } selected)
                    Post(WM_REFRESH, selected);
                return 0;
        }
        return 0;
    }

    private static void Post(uint msg, Node node)
    {
        nint id = Nodes.First(kv => kv.Value == node).Key;
        PostMessage(_hwnd, msg, (nuint)id, 0);
    }

    private static void RunBusy(Action action)
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

    private static void Connect()
    {
        var (last, remember, profile) = Config.LoadLast();
        var request = Plugin.WithKnownSecrets(_request ?? last);
        var options = new ConnectOptions { Remember = remember, Profile = profile };

        while (true)
        {
            var r = ConnectDialog.Show(Npp.Handle, "Conectar el panel remoto (SSH/SFTP)", "Conectar", request, options);
            if (r is null)
                return;
            request = r;
            SftpClient? client = null;
            try
            {
                client = Plugin.ConnectInteractive(request);
                if (client is null)
                    continue;
                Plugin.RememberSecrets(request);

                string start = Sftp.ResolvePath(client, request.RemotePath);
                if (IsFileQuiet(client, start))
                    start = Sftp.Parent(start);
                Config.SaveLast(request with { RemotePath = start }, options.Remember, options.Profile);

                DropConnection();
                _client = client;
                _request = request;
                client = null; // ya es del panel
                SetText(IdStatus, $"{request.User}@{request.Host}");
                UpdateButtons();
                Rebuild(start);
                SetFocus(_tree);
                return;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.ForgetSecretsOnAuthError(request, ex);
                Plugin.ShowError("No se pudo conectar con " + request.Display, ex);
            }
            finally
            {
                client?.Dispose();
            }
        }
    }

    private static bool IsFileQuiet(SftpClient client, string path)
    {
        try
        {
            return Plugin.IsFile(client, path);
        }
        catch (SftpPermissionDeniedException)
        {
            return false;
        }
    }

    /// <summary>Cierra la conexión y vacía el árbol. No se llama nunca desde una notificación del árbol.</summary>
    private static void DropConnection()
    {
        _client?.Dispose();
        _client = null;
        ClearTree();
        if (_hwnd != IntPtr.Zero)
        {
            SetText(IdStatus, "Sin conexión");
            UpdateButtons();
        }
    }

    /// <summary>
    /// Ejecuta una operación con la conexión del panel; si se había caído (servidor reiniciado,
    /// VPN, suspensión del equipo) reconecta una vez con las mismas credenciales y reintenta.
    /// Si no se puede reconectar, desconecta el panel y relanza el error.
    /// </summary>
    private static T WithClient<T>(Func<SftpClient, T> action)
    {
        if (_client is null || _request is null)
            throw new OperationCanceledException(); // se desconectó mientras tanto
        if (_client.IsConnected)
        {
            try
            {
                return action(_client);
            }
            catch (Exception ex) when (IsConnectionLost(ex))
            {
            }
        }

        var request = _request;
        _client?.Dispose();
        _client = null;
        try
        {
            var client = Plugin.ConnectInteractive(request) ?? throw new OperationCanceledException();
            _client = client;
            return action(client);
        }
        catch (Exception ex)
        {
            Plugin.ForgetSecretsOnAuthError(request, ex);
            DropConnection();
            throw;
        }
    }

    private static bool IsConnectionLost(Exception ex) =>
        ex is SshConnectionException or SshOperationTimeoutException or SocketException or ObjectDisposedException;

    // --- Árbol ---

    /// <summary>Vacía el árbol, crea la raíz "/" y despliega hasta <paramref name="path"/>.</summary>
    private static void Rebuild(string path)
    {
        ClearTree();
        var node = new Node("/", true);
        Insert(TVI_ROOT, "/", node);
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; ; i++)
        {
            bool last = i == parts.Length;
            if (!node.Loaded && !Load(node, showError: last))
            {
                if (last || _client is null)
                    break;
                // Carpeta intermedia sin permiso de lectura (p. ej. /home con 711): se crea
                // solo el siguiente nivel para poder llegar a la carpeta inicial.
                Insert(node.Item, parts[i], new Node(Combine(node.Path, parts[i]), true));
                node.Loaded = true;
            }
            SendMessage(_tree, TVM_EXPAND, TVE_EXPAND, node.Item);
            if (last)
                break;
            var child = Children(node).FirstOrDefault(c => c.Path == Combine(node.Path, parts[i]));
            if (child is null)
                break;
            node = child;
            if (!node.IsDirectory)
                break;
        }
        if (_client is not null)
        {
            SendMessage(_tree, TVM_SELECTITEM, TVGN_CARET, node.Item);
            SendMessage(_tree, TVM_ENSUREVISIBLE, 0, node.Item);
        }
    }

    private static void Expand(Node node)
    {
        if (node.Loaded || Load(node, showError: true))
            SendMessage(_tree, TVM_EXPAND, TVE_EXPAND, node.Item);
    }

    private static void Refresh(Node node)
    {
        if (!node.IsDirectory)
        {
            IntPtr parent = (IntPtr)SendMessage(_tree, TVM_GETNEXTITEM, TVGN_PARENT, node.Item);
            if (NodeOf(parent) is not { } folder)
                return;
            node = folder;
        }
        if (Load(node, showError: true))
            SendMessage(_tree, TVM_EXPAND, TVE_EXPAND, node.Item);
    }

    /// <summary>Lista la carpeta del nodo (fuera del hilo de la interfaz) y sustituye sus hijos. False si falla.</summary>
    private static bool Load(Node node, bool showError)
    {
        List<Node> children;
        bool showHidden = Config.GetFlag("hidden", true);
        try
        {
            children = WithClient(client =>
                ProgressDialog.Run(Npp.Handle, "Listando " + node.Path, _ => List(client, node.Path, showHidden)));
        }
        catch (OperationCanceledException)
        {
            // Si se canceló a medias, la conexión puede seguir ocupada en otro hilo: se cierra.
            if (_client is not null)
                DropConnection();
            return false;
        }
        catch (Exception ex)
        {
            if (showError)
                Plugin.ShowError("No se puede listar " + node.Path, ex);
            return false;
        }
        if (!Nodes.ContainsValue(node)) // el árbol se rehízo mientras tanto
            return false;

        DeleteChildren(node.Item);
        foreach (var child in children)
            Insert(node.Item, child.Path[(child.Path.LastIndexOf('/') + 1)..], child);
        node.Loaded = true;
        SetHasChildren(node.Item, children.Count > 0);
        return true;
    }

    /// <summary>Contenido de una carpeta; se ejecuta en un hilo de trabajo.</summary>
    private static List<Node> List(SftpClient client, string folder, bool showHidden)
    {
        var result = new List<Node>();
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
            result.Add(new Node(Combine(folder, f.Name), isDirectory));
        }
        return result
            .OrderBy(n => n.IsDirectory ? 0 : 1)
            .ThenBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Combine(string folder, string name) => folder == "/" ? "/" + name : folder + "/" + name;

    private static void ToggleHidden()
    {
        bool show = SendMessage(GetDlgItem(_hwnd, IdHidden), BM_GETCHECK, 0, 0) == 1;
        Config.SetFlag("hidden", show);
        if (_client is not null)
            Rebuild(SelectedNode()?.Path ?? "/");
    }

    private static void SyncHiddenCheckbox() =>
        SendMessage(GetDlgItem(_hwnd, IdHidden), BM_SETCHECK, Config.GetFlag("hidden", true) ? 1u : 0u, 0);

    private static void Open(Node node)
    {
        if (_request is null)
            return;
        var request = _request;
        try
        {
            WithClient(client =>
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
            Plugin.ShowError("No se pudo abrir " + node.Path, ex);
        }
    }

    // --- Utilidades del TreeView ---

    private static void Insert(IntPtr parent, string text, Node node)
    {
        nint id = _nextId++;
        Nodes[id] = node;
        fixed (char* p = text)
        {
            var insert = new TVINSERTSTRUCTW
            {
                hParent = parent,
                hInsertAfter = TVI_LAST,
                item = new TVITEMEXW
                {
                    mask = TVIF_TEXT | TVIF_PARAM | TVIF_CHILDREN,
                    pszText = p,
                    cChildren = node.IsDirectory ? 1 : 0, // botón "+" hasta saber si tiene contenido
                    lParam = id,
                },
            };
            node.Item = (IntPtr)SendMessage(_tree, TVM_INSERTITEMW, 0, (nint)(&insert));
        }
    }

    private static void SetHasChildren(IntPtr item, bool value)
    {
        var tv = new TVITEMEXW { mask = TVIF_CHILDREN, hItem = item, cChildren = value ? 1 : 0 };
        SendMessage(_tree, TVM_SETITEMW, 0, (nint)(&tv));
    }

    private static Node? NodeOf(IntPtr item)
    {
        if (item == IntPtr.Zero)
            return null;
        var tv = new TVITEMEXW { mask = TVIF_PARAM, hItem = item };
        if (SendMessage(_tree, TVM_GETITEMW, 0, (nint)(&tv)) == 0)
            return null;
        return Nodes.GetValueOrDefault(tv.lParam);
    }

    private static Node? SelectedNode() =>
        _tree == IntPtr.Zero ? null : NodeOf((IntPtr)SendMessage(_tree, TVM_GETNEXTITEM, TVGN_CARET, 0));

    private static IEnumerable<Node> Children(Node parent)
    {
        var result = new List<Node>();
        for (IntPtr child = (IntPtr)SendMessage(_tree, TVM_GETNEXTITEM, TVGN_CHILD, parent.Item);
             child != IntPtr.Zero;
             child = (IntPtr)SendMessage(_tree, TVM_GETNEXTITEM, TVGN_NEXT, child))
        {
            if (NodeOf(child) is { } node)
                result.Add(node);
        }
        return result;
    }

    private static void DeleteChildren(IntPtr parent)
    {
        IntPtr child;
        while ((child = (IntPtr)SendMessage(_tree, TVM_GETNEXTITEM, TVGN_CHILD, parent)) != IntPtr.Zero)
        {
            ForgetSubtree(child);
            SendMessage(_tree, TVM_DELETEITEM, 0, child);
        }
    }

    /// <summary>Quita del diccionario los nodos de un elemento y sus descendientes.</summary>
    private static void ForgetSubtree(IntPtr item)
    {
        for (IntPtr child = (IntPtr)SendMessage(_tree, TVM_GETNEXTITEM, TVGN_CHILD, item);
             child != IntPtr.Zero;
             child = (IntPtr)SendMessage(_tree, TVM_GETNEXTITEM, TVGN_NEXT, child))
        {
            ForgetSubtree(child);
        }
        var tv = new TVITEMEXW { mask = TVIF_PARAM, hItem = item };
        if (SendMessage(_tree, TVM_GETITEMW, 0, (nint)(&tv)) != 0)
            Nodes.Remove(tv.lParam);
    }

    private static void ClearTree()
    {
        if (_tree != IntPtr.Zero)
            SendMessage(_tree, TVM_DELETEITEM, 0, TVI_ROOT);
        Nodes.Clear();
    }

    private static void UpdateButtons()
    {
        bool connected = _client is not null;
        EnableWindow(GetDlgItem(_hwnd, IdRefresh), connected ? 1 : 0);
        EnableWindow(GetDlgItem(_hwnd, IdDisconnect), connected ? 1 : 0);
    }

    private static void SetText(int id, string text)
    {
        fixed (char* p = text)
            SetWindowText(GetDlgItem(_hwnd, id), p);
    }
}
