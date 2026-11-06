using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Renci.SshNet;
using static SshOpen.DialogHost;
using static SshOpen.Win32;

namespace SshOpen;

/// <summary>
/// Explorador modal de una carpeta remota: lista sus carpetas y ficheros, permite entrar en
/// carpetas (doble clic / Intro, Retroceso para subir), ordenar por columna, filtrar por nombre
/// y ocultar los ficheros que empiezan por punto. Devuelve el fichero elegido.
/// </summary>
internal static unsafe class BrowseDialog
{
    private const int IdDir = 201, IdGo = 202, IdList = 203, IdStatus = 204, IdFilter = 205, IdHidden = 206;
    private const int DC_HASDEFID = 0x534B;
    private static readonly string[] Columns = ["Nombre", "Tipo", "Tamaño", "Modificado"];

    private sealed record Entry(string Name, string FullName, bool IsDirectory, bool IsLink, long Size, DateTime Modified)
    {
        public string Type => IsDirectory ? "Carpeta" : IsLink ? "Enlace" : "Fichero";
    }

    private static bool _done;
    private static IntPtr _hwnd, _list;
    private static SftpClient _client = null!;
    private static string _current = "/";
    private static List<Entry> _all = [];      // contenido de la carpeta, sin ".."
    private static List<Entry> _entries = [];  // lo que se ve en la lista, en orden
    private static int _sortColumn;
    private static bool _sortDescending;
    private static bool _showHidden = true;
    private static string? _result;

    /// <summary>Devuelve la ruta absoluta del fichero elegido, o null si se cancela.</summary>
    /// <param name="select">Nombre de la entrada a dejar seleccionada al abrir, si existe.</param>
    /// <param name="lastFolder">Última carpeta mostrada, para recordarla en la siguiente apertura.</param>
    public static string? Show(IntPtr owner, SftpClient client, string folder, string? select, string title, out string lastFolder)
    {
        _done = false;
        _result = null;
        _client = client;
        _current = folder;
        _all = [];
        _entries = [];
        _showHidden = Config.GetFlag("hidden", true);

        _hwnd = CreateFrame(owner, "SshOpenBrowseDialog", &WndProc, title, 600, 432);

        Child(_hwnd, 0, "STATIC", "Carpeta:", 0, 14, 18, 60, 20, 0);
        Child(_hwnd, WS_EX_CLIENTEDGE, "EDIT", folder, WS_TABSTOP | ES_AUTOHSCROLL, 78, 14, 420, 24, IdDir);
        Child(_hwnd, 0, "BUTTON", "Ir", WS_TABSTOP | BS_PUSHBUTTON, 506, 13, 80, 26, IdGo);

        Child(_hwnd, 0, "STATIC", "Filtro:", 0, 14, 50, 60, 20, 0);
        IntPtr filter = Child(_hwnd, WS_EX_CLIENTEDGE, "EDIT", "", WS_TABSTOP | ES_AUTOHSCROLL, 78, 46, 280, 24, IdFilter);
        SendMessage(filter, EM_SETCUEBANNER, 1, "Parte del nombre, o comodines: *.conf");
        IntPtr hidden = Child(_hwnd, 0, "BUTTON", "Mostrar ocultos (.*)", WS_TABSTOP | BS_AUTOCHECKBOX, 372, 48, 214, 20, IdHidden);
        SendMessage(hidden, BM_SETCHECK, _showHidden ? 1u : 0u, 0);

        _list = Child(_hwnd, WS_EX_CLIENTEDGE, "SysListView32", "",
            WS_TABSTOP | LVS_REPORT | LVS_SINGLESEL | LVS_SHOWSELALWAYS, 14, 80, 572, 298, IdList);
        SendMessage(_list, LVM_SETEXTENDEDLISTVIEWSTYLE, 0, (nint)(LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER));
        AddColumn(0, 260, 0);
        AddColumn(1, 70, 0);
        AddColumn(2, 85, LVCFMT_RIGHT);
        AddColumn(3, 125, 0);
        UpdateColumnHeaders();

        Child(_hwnd, 0, "STATIC", "", 0, 14, 394, 370, 20, IdStatus);
        Child(_hwnd, 0, "BUTTON", "Abrir", WS_TABSTOP | BS_DEFPUSHBUTTON, 398, 388, 90, 28, IDOK);
        Child(_hwnd, 0, "BUTTON", "Cancelar", WS_TABSTOP | BS_PUSHBUTTON, 496, 388, 90, 28, IDCANCEL);

        // Se muestra antes de listar para que los avisos de error cuelguen ya del diálogo.
        EnableWindow(owner, 0);
        DialogHost.Show(_hwnd);

        if (Navigate(folder, showError: false))
        {
            int index = select == null ? -1 : _entries.FindIndex(e => e.Name == select);
            if (index >= 0)
                Select(index);
        }
        else if (Navigate(client.WorkingDirectory))
        {
            SetText(_hwnd, IdStatus, $"No se puede acceder a {folder}; se muestra la carpeta del usuario.");
        }
        SetFocus(_list);

        RunModal(owner, _hwnd, () => _done);
        _hwnd = _list = IntPtr.Zero;
        _client = null!;
        lastFolder = _current;
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
                    OnActivate(hwnd, wParam, _list);
                    return 0;
                case WM_CLOSE:
                    _done = true;
                    return 0;
                case WM_COMMAND:
                    int id = (int)(wParam & 0xFFFF);
                    int code = (int)(wParam >> 16);
                    IntPtr focus = GetFocus();
                    if (id == IdGo || (id == IDOK && focus == GetDlgItem(_hwnd, IdDir)))
                        GoTo(Text(_hwnd, IdDir).Trim());
                    else if (id == IDOK)
                        ActivateSelected();
                    else if (id == IDCANCEL)
                        _done = true;
                    else if (id == IdFilter && code == EN_CHANGE)
                        Render(keepSelection: false);
                    else if (id == IdHidden && code == BN_CLICKED)
                        ToggleHidden();
                    return 0;
                case WM_NOTIFY:
                    var header = (NMHDR*)lParam;
                    if (header->idFrom != IdList)
                        break;
                    // NMITEMACTIVATE.iItem, NMLVKEYDOWN.wVKey y NMLISTVIEW.iItem van justo detrás de NMHDR;
                    // NMLISTVIEW.iSubItem (la columna pulsada) va detrás de iItem.
                    byte* body = (byte*)lParam + sizeof(NMHDR);
                    if (header->code == NM_DBLCLK && *(int*)body >= 0)
                        ActivateSelected();
                    else if (header->code == LVN_KEYDOWN && *(ushort*)body == VK_BACK)
                        Navigate(Sftp.Parent(_current));
                    else if (header->code == LVN_COLUMNCLICK)
                        SortBy(*(int*)(body + sizeof(int)));
                    return 0;
            }
        }
        catch (Exception ex)
        {
            Plugin.ShowError(hwnd, "Error en el explorador remoto", ex);
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Ruta escrita a mano: si es carpeta se muestra; si es fichero se abre.</summary>
    private static void GoTo(string text)
    {
        string path = Sftp.ResolvePath(_client, text.Length == 0 ? _current : text);
        try
        {
            if (_client.GetAttributes(path).IsDirectory)
            {
                if (Navigate(path))
                    SetFocus(_list);
            }
            else
            {
                Choose(path);
            }
        }
        catch (Exception ex)
        {
            Plugin.ShowError(_hwnd, "No se puede acceder a " + path, ex);
        }
    }

    private static void ActivateSelected()
    {
        int index = SelectedIndex();
        if (index < 0)
        {
            MessageBox(_hwnd, "Selecciona un fichero de la lista.", Plugin.Name, MB_ICONINFORMATION);
            return;
        }

        var entry = _entries[index];
        bool isDirectory = entry.IsDirectory;
        if (entry.IsLink)
        {
            try
            {
                isDirectory = _client.GetAttributes(entry.FullName).IsDirectory; // sigue el enlace
            }
            catch (Exception ex)
            {
                Plugin.ShowError(_hwnd, "El enlace " + entry.FullName + " no es accesible", ex);
                return;
            }
        }

        if (isDirectory)
            Navigate(entry.FullName);
        else
            Choose(entry.FullName);
    }

    private static void Choose(string remotePath)
    {
        _result = remotePath;
        _done = true;
    }

    private static bool Navigate(string folder, bool showError = true)
    {
        List<Entry> all;
        try
        {
            using (new WaitCursor())
            {
                all = _client.ListDirectory(folder)
                    .Where(f => f.Name is not "." and not "..")
                    .Select(f => new Entry(f.Name, f.FullName, f.IsDirectory, f.IsSymbolicLink, f.Length, f.LastWriteTime))
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            if (showError)
                Plugin.ShowError(_hwnd, "No se puede listar " + folder, ex);
            return false;
        }

        _current = folder;
        _all = all;
        SetText(_hwnd, IdDir, folder);
        Render(keepSelection: false);
        return true;
    }

    /// <summary>Vuelve a pintar la lista con el filtro, la opción de ocultos y el orden actuales (sin ir al servidor).</summary>
    private static void Render(bool keepSelection)
    {
        string? selected = keepSelection && SelectedIndex() is int i and >= 0 ? _entries[i].Name : null;

        var filter = BuildFilter(Text(_hwnd, IdFilter).Trim());
        var visible = _all
            .Where(e => _showHidden || !e.Name.StartsWith('.'))
            .Where(e => filter == null || filter(e.Name))
            .ToList();
        visible.Sort(Compare);
        if (_current != "/")
            visible.Insert(0, new Entry("..", Sftp.Parent(_current), true, false, 0, default));
        _entries = visible;

        SendMessage(_list, LVM_DELETEALLITEMS, 0, 0);
        for (int n = 0; n < visible.Count; n++)
        {
            var e = visible[n];
            if (e.Name == "..")
                AddRow(n, "..", "Subir", "", "");
            else
                AddRow(n, e.Name, e.Type, e.IsDirectory ? "" : FormatSize(e.Size), e.Modified.ToString("yyyy-MM-dd HH:mm"));
        }

        int index = selected != null ? visible.FindIndex(e => e.Name == selected) : -1;
        if (index < 0 && filter != null)
            index = visible.FindIndex(e => e.Name != ".."); // con filtro, el primer resultado real
        if (index < 0 && visible.Count > 0)
            index = 0;
        if (index >= 0)
            Select(index);

        int folders = visible.Count(e => e.IsDirectory && e.Name != "..");
        int files = visible.Count(e => !e.IsDirectory);
        int hidden = _all.Count - folders - files;
        SetText(_hwnd, IdStatus, $"{folders} carpetas, {files} ficheros" +
            (hidden > 0 ? $" ({hidden} sin mostrar)" : "") + "   ·   Retroceso: subir");
    }

    /// <summary>Texto con * o ? = comodines; si no, "contiene". Sin distinguir mayúsculas.</summary>
    private static Func<string, bool>? BuildFilter(string text)
    {
        if (text.Length == 0)
            return null;
        if (text.Contains('*') || text.Contains('?'))
        {
            var regex = new Regex("^" + Regex.Escape(text).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return regex.IsMatch;
        }
        return name => name.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Carpetas siempre primero; dentro de cada grupo, la columna elegida y luego el nombre.</summary>
    private static int Compare(Entry a, Entry b)
    {
        if (a.IsDirectory != b.IsDirectory)
            return a.IsDirectory ? -1 : 1;
        int result = _sortColumn switch
        {
            1 => string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase),
            2 => a.Size.CompareTo(b.Size),
            3 => a.Modified.CompareTo(b.Modified),
            _ => 0,
        };
        if (result == 0)
            result = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return _sortDescending ? -result : result;
    }

    private static void SortBy(int column)
    {
        if (column < 0 || column >= Columns.Length)
            return;
        if (column == _sortColumn)
            _sortDescending = !_sortDescending;
        else
            (_sortColumn, _sortDescending) = (column, column == 3); // por fecha, lo más reciente primero
        UpdateColumnHeaders();
        Render(keepSelection: true);
    }

    private static void ToggleHidden()
    {
        _showHidden = SendMessage(GetDlgItem(_hwnd, IdHidden), BM_GETCHECK, 0, 0) == 1;
        Config.SetFlag("hidden", _showHidden);
        Render(keepSelection: true);
    }

    private static int SelectedIndex()
    {
        int index = (int)SendMessage(_list, LVM_GETNEXTITEM, nuint.MaxValue, (nint)LVNI_SELECTED);
        return index >= 0 && index < _entries.Count ? index : -1;
    }

    private static void AddColumn(int index, int width, int format)
    {
        fixed (char* text = Columns[index])
        {
            var col = new LVCOLUMNW { mask = LVCF_TEXT | LVCF_WIDTH | LVCF_FMT, fmt = format, cx = S(width), pszText = text };
            SendMessage(_list, LVM_INSERTCOLUMNW, (nuint)index, (nint)(&col));
        }
    }

    /// <summary>Marca con ▲/▼ la columna por la que se ordena.</summary>
    private static void UpdateColumnHeaders()
    {
        for (int i = 0; i < Columns.Length; i++)
        {
            string title = i == _sortColumn ? Columns[i] + (_sortDescending ? " ▼" : " ▲") : Columns[i];
            fixed (char* text = title)
            {
                var col = new LVCOLUMNW { mask = LVCF_TEXT, pszText = text };
                SendMessage(_list, LVM_SETCOLUMNW, (nuint)i, (nint)(&col));
            }
        }
    }

    private static void AddRow(int index, params string[] cells)
    {
        fixed (char* first = cells[0])
        {
            var item = new LVITEMW { mask = LVIF_TEXT, iItem = index, pszText = first };
            SendMessage(_list, LVM_INSERTITEMW, 0, (nint)(&item));
        }
        for (int c = 1; c < cells.Length; c++)
        {
            fixed (char* text = cells[c])
            {
                var sub = new LVITEMW { iSubItem = c, pszText = text };
                SendMessage(_list, LVM_SETITEMTEXTW, (nuint)index, (nint)(&sub));
            }
        }
    }

    private static void Select(int index)
    {
        var item = new LVITEMW { stateMask = LVIS_SELECTED | LVIS_FOCUSED, state = LVIS_SELECTED | LVIS_FOCUSED };
        SendMessage(_list, LVM_SETITEMSTATE, (nuint)index, (nint)(&item));
        SendMessage(_list, LVM_ENSUREVISIBLE, (nuint)index, 0);
    }
}
