using static SshOpen.Win32;

namespace SshOpen;

/// <summary>Carpeta o fichero remoto mostrado en el árbol del panel.</summary>
internal sealed class RemoteNode(string path, bool isDirectory)
{
    public string Path { get; } = path;
    public bool IsDirectory { get; } = isDirectory;

    /// <summary>Ya se listó su contenido (los hijos del árbol son reales).</summary>
    public bool Loaded { get; set; }

    public string Name => Path == "/" ? "/" : Path[(Path.LastIndexOf('/') + 1)..];

    // Datos del TreeView, solo para RemoteTree.
    internal IntPtr Item;
    internal nint Id;

    public static string Combine(string folder, string name) => folder == "/" ? "/" + name : folder + "/" + name;
}

/// <summary>
/// Envoltorio de un SysTreeView32 que guarda un <see cref="RemoteNode"/> por elemento.
/// Cada elemento lleva en su lParam el Id del nodo; los HTREEITEM no salen de esta clase, así
/// que nadie usa uno ya borrado.
/// </summary>
internal sealed unsafe class RemoteTree(IntPtr hwnd)
{
    private readonly Dictionary<nint, RemoteNode> _nodes = [];
    private nint _nextId = 1;

    public IntPtr Handle => hwnd;

    /// <summary>Vacía el árbol y crea la raíz.</summary>
    public RemoteNode Reset(string rootPath = "/")
    {
        Clear();
        var root = new RemoteNode(rootPath, true);
        Insert(TVI_ROOT, root);
        return root;
    }

    public void Clear()
    {
        SendMessage(hwnd, TVM_DELETEITEM, 0, TVI_ROOT);
        _nodes.Clear();
    }

    public void Add(RemoteNode parent, RemoteNode child) => Insert(parent.Item, child);

    /// <summary>Sustituye los hijos del nodo por <paramref name="children"/> y lo marca como cargado.</summary>
    public void ReplaceChildren(RemoteNode parent, IReadOnlyCollection<RemoteNode> children)
    {
        IntPtr child;
        while ((child = Next(TVGN_CHILD, parent.Item)) != IntPtr.Zero)
        {
            Forget(child);
            SendMessage(hwnd, TVM_DELETEITEM, 0, child);
        }
        foreach (var node in children)
            Insert(parent.Item, node);
        parent.Loaded = true;
        // Sin hijos se quita el "+"; con hijos se repone (pudo quitarse en una carga anterior).
        var tv = new TVITEMEXW { mask = TVIF_CHILDREN, hItem = parent.Item, cChildren = children.Count > 0 ? 1 : 0 };
        SendMessage(hwnd, TVM_SETITEMW, 0, (nint)(&tv));
    }

    public bool Contains(RemoteNode node) => _nodes.TryGetValue(node.Id, out var n) && n == node;

    public RemoteNode? FromId(nint id) => _nodes.GetValueOrDefault(id);

    public RemoteNode? Selected => NodeOf(Next(TVGN_CARET, IntPtr.Zero));

    public RemoteNode? ParentOf(RemoteNode node) => Contains(node) ? NodeOf(Next(TVGN_PARENT, node.Item)) : null;

    public List<RemoteNode> ChildrenOf(RemoteNode node)
    {
        var result = new List<RemoteNode>();
        if (!Contains(node))
            return result;
        for (IntPtr child = Next(TVGN_CHILD, node.Item); child != IntPtr.Zero; child = Next(TVGN_NEXT, child))
            if (NodeOf(child) is { } n)
                result.Add(n);
        return result;
    }

    /// <summary>Despliega el nodo (debe estar ya cargado: si no, el aviso de despliegue lo impide).</summary>
    public void Expand(RemoteNode node)
    {
        if (Contains(node))
            SendMessage(hwnd, TVM_EXPAND, TVE_EXPAND, node.Item);
    }

    public void Select(RemoteNode node)
    {
        if (!Contains(node))
            return;
        SendMessage(hwnd, TVM_SELECTITEM, TVGN_CARET, node.Item);
        SendMessage(hwnd, TVM_ENSUREVISIBLE, 0, node.Item);
    }

    private void Insert(IntPtr parent, RemoteNode node)
    {
        node.Id = _nextId++;
        _nodes[node.Id] = node;
        fixed (char* text = node.Name)
        {
            var insert = new TVINSERTSTRUCTW
            {
                hParent = parent,
                hInsertAfter = TVI_LAST,
                item = new TVITEMEXW
                {
                    mask = TVIF_TEXT | TVIF_PARAM | TVIF_CHILDREN,
                    pszText = text,
                    cChildren = node.IsDirectory ? 1 : 0, // botón "+" hasta saber si tiene contenido
                    lParam = node.Id,
                },
            };
            node.Item = (IntPtr)SendMessage(hwnd, TVM_INSERTITEMW, 0, (nint)(&insert));
        }
    }

    private RemoteNode? NodeOf(IntPtr item)
    {
        if (item == IntPtr.Zero)
            return null;
        var tv = new TVITEMEXW { mask = TVIF_PARAM, hItem = item };
        return SendMessage(hwnd, TVM_GETITEMW, 0, (nint)(&tv)) != 0 ? _nodes.GetValueOrDefault(tv.lParam) : null;
    }

    /// <summary>Quita del diccionario un elemento y todos sus descendientes.</summary>
    private void Forget(IntPtr item)
    {
        for (IntPtr child = Next(TVGN_CHILD, item); child != IntPtr.Zero; child = Next(TVGN_NEXT, child))
            Forget(child);
        if (NodeOf(item) is { } node)
            _nodes.Remove(node.Id);
    }

    private IntPtr Next(nuint relation, IntPtr item) => (IntPtr)SendMessage(hwnd, TVM_GETNEXTITEM, relation, item);
}
