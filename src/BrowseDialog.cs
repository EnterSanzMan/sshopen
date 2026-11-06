using System.Text.RegularExpressions;
using Renci.SshNet;
using static SshOpen.Win32;

namespace SshOpen;

/// <summary>
/// Explorador modal de una carpeta remota: lista sus carpetas y ficheros, permite entrar en
/// carpetas (doble clic / Intro, Retroceso o ".." para subir, "/" para ir a la raíz), ordenar por columna, filtrar por nombre
/// y ocultar los ficheros que empiezan por punto. Devuelve el fichero elegido.
/// </summary>
internal sealed unsafe class BrowseDialog : ModalDialog
{
    private const int IdDir = 201, IdGo = 202, IdList = 203, IdStatus = 204, IdFilter = 205, IdHidden = 206;

    private sealed record Entry(string Name, string FullName, bool IsDirectory, bool IsLink, long Size, DateTime Modified)
    {
        public string Type => IsDirectory ? Tr.TypeFolder : IsLink ? Tr.TypeLink : Tr.TypeFile;
    }

    private readonly SftpClient _client;
    private HWND _list;
    private string _current;
    private List<Entry> _all = [];      // contenido de la carpeta, sin ".." ni "/"
    private List<Entry> _entries = [];  // lo que se ve en la lista, en orden
    private int _sortColumn;
    private bool _sortDescending;
    private bool _showHidden = Config.GetFlag("hidden", true);
    private string? _result;

    private BrowseDialog(SftpClient client, string folder)
    {
        _client = client;
        _current = folder;
    }

    /// <summary>Devuelve la ruta absoluta del fichero elegido, o null si se cancela.</summary>
    /// <param name="select">Nombre de la entrada a dejar seleccionada al abrir, si existe.</param>
    /// <param name="lastFolder">Última carpeta mostrada, para recordarla en la siguiente apertura.</param>
    public static string? Show(HWND owner, SftpClient client, string folder, string? select, string title, out string lastFolder)
    {
        var dialog = new BrowseDialog(client, folder);
        dialog.Build(owner, title, folder, select);
        dialog.RunModal();
        lastFolder = dialog._current;
        return dialog._result;
    }

    protected override HWND DefaultFocus => _list;

    private static string[] Columns => [Tr.ColumnName, Tr.ColumnType, Tr.ColumnSize, Tr.ColumnModified];

    private void Build(HWND owner, string title, string folder, string? select)
    {
        CreateFrame(owner, "SshOpenBrowseDialog", title, 600, 432);
        try
        {
            Child(0, "STATIC", Tr.FolderLabel, 0, 14, 18, 60, 20, 0);
            Child(WS_EX_CLIENTEDGE, "EDIT", folder, WS_TABSTOP | ES_AUTOHSCROLL, 78, 14, 420, 24, IdDir);
            Child(0, "BUTTON", Tr.Go, WS_TABSTOP | BS_PUSHBUTTON, 506, 13, 80, 26, IdGo);

            Child(0, "STATIC", Tr.FilterLabel, 0, 14, 50, 60, 20, 0);
            HWND filter = Child(WS_EX_CLIENTEDGE, "EDIT", "", WS_TABSTOP | ES_AUTOHSCROLL, 78, 46, 280, 24, IdFilter);
            SendMessage(filter, EM_SETCUEBANNER, 1, Tr.FilterHint);
            Child(0, "BUTTON", Tr.ShowHidden, WS_TABSTOP | BS_AUTOCHECKBOX, 372, 48, 214, 20, IdHidden);
            SetChecked(IdHidden, _showHidden);

            _list = Child(WS_EX_CLIENTEDGE, "SysListView32", "",
                WS_TABSTOP | LVS_REPORT | LVS_SINGLESEL | LVS_SHOWSELALWAYS, 14, 80, 572, 298, IdList);
            SendMessage(_list, LVM_SETEXTENDEDLISTVIEWSTYLE, 0, (nint)(LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER));
            AddColumn(0, 260, 0);
            AddColumn(1, 70, 0);
            AddColumn(2, 85, LVCOLUMNW_FORMAT.LVCFMT_RIGHT);
            AddColumn(3, 125, 0);
            UpdateColumnHeaders();

            Child(0, "STATIC", "", 0, 14, 394, 370, 20, IdStatus);
            Child(0, "BUTTON", Tr.Open, WS_TABSTOP | BS_DEFPUSHBUTTON, 398, 388, 90, 28, IDOK);
            Child(0, "BUTTON", Tr.Cancel, WS_TABSTOP | BS_PUSHBUTTON, 496, 388, 90, 28, IDCANCEL);

            // Se muestra antes de listar para que los avisos de error cuelguen ya del diálogo.
            ShowFrame();
            if (Navigate(folder, showError: false))
            {
                int index = select == null ? -1 : _entries.FindIndex(e => e.Name == select);
                if (index >= 0)
                    Select(index);
            }
            else if (Navigate(_client.WorkingDirectory))
            {
                SetText(IdStatus, Tr.FolderNotAccessible(folder));
            }
            SetFocus(_list);
        }
        catch
        {
            Close();
            throw;
        }
    }

    protected override nint? OnDialogMessage(uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case DM_GETDEFID:
                return (nint)((DC_HASDEFID << 16) | (uint)IDOK);
            case WM_COMMAND:
                int id = LoWord(wParam);
                uint code = (uint)HiWord(wParam);
                if (id == IdGo || (id == IDOK && GetFocus() == Item(IdDir)))
                    GoTo(Text(IdDir).Trim());
                else if (id == IDOK)
                    ActivateSelected();
                else if (id == IDCANCEL)
                    Done = true;
                else if (id == IdFilter && code == EN_CHANGE)
                    Render(keepSelection: false);
                else if (id == IdHidden && code == BN_CLICKED)
                    ToggleHidden();
                return 0;
            case WM_NOTIFY:
                var header = (NMHDR*)(nint)lParam;
                if (header->idFrom != IdList)
                    return null;
                if (header->code == NM_DBLCLK && ((NMITEMACTIVATE*)header)->iItem >= 0)
                    ActivateSelected();
                else if (header->code == LVN_KEYDOWN && ((NMLVKEYDOWN*)header)->wVKey == VK_BACK)
                    Navigate(Sftp.Parent(_current));
                else if (header->code == LVN_COLUMNCLICK)
                    SortBy(((NMLISTVIEW*)header)->iSubItem);
                return 0;
        }
        return null;
    }

    /// <summary>Ruta escrita a mano: si es carpeta se muestra; si es fichero se abre.</summary>
    private void GoTo(string text)
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
            Prompts.Error(Handle, Tr.CannotAccess(path), ex);
        }
    }

    private void ActivateSelected()
    {
        int index = SelectedIndex();
        if (index < 0)
        {
            Message(Handle, Tr.SelectAFile, MB_ICONINFORMATION);
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
                Prompts.Error(Handle, Tr.LinkNotAccessible(entry.FullName), ex);
                return;
            }
        }

        if (isDirectory)
            Navigate(entry.FullName);
        else
            Choose(entry.FullName);
    }

    private void Choose(string remotePath)
    {
        _result = remotePath;
        Done = true;
    }

    private bool Navigate(string folder, bool showError = true)
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
                Prompts.Error(Handle, Tr.CannotList(folder), ex);
            return false;
        }

        _current = folder;
        _all = all;
        SetText(IdDir, folder);
        Render(keepSelection: false);
        return true;
    }

    /// <summary>Vuelve a pintar la lista con el filtro, la opción de ocultos y el orden actuales (sin ir al servidor).</summary>
    private void Render(bool keepSelection)
    {
        string? selected = keepSelection && SelectedIndex() is int i and >= 0 ? _entries[i].Name : null;

        var filter = BuildFilter(Text(IdFilter).Trim());
        var visible = _all
            .Where(e => _showHidden || !e.Name.StartsWith('.'))
            .Where(e => filter == null || filter(e.Name))
            .ToList();
        visible.Sort(Compare);
        if (_current != "/")
        {
            // Atajos de navegación: ".." sube un nivel y "/" va directamente a la raíz.
            visible.Insert(0, new Entry("/", "/", true, false, 0, default));
            visible.Insert(0, new Entry("..", Sftp.Parent(_current), true, false, 0, default));
        }
        _entries = visible;

        SendMessage(_list, LVM_DELETEALLITEMS, 0, 0);
        for (int n = 0; n < visible.Count; n++)
        {
            var e = visible[n];
            if (IsShortcut(e))
                AddRow(n, e.Name, e.Name == "/" ? Tr.Root : Tr.Up, "", "");
            else
                AddRow(n, e.Name, e.Type, e.IsDirectory ? "" : Tr.FormatSize(e.Size), e.Modified.ToString("yyyy-MM-dd HH:mm"));
        }

        int index = selected != null ? visible.FindIndex(e => e.Name == selected) : -1;
        if (index < 0 && filter != null)
            index = visible.FindIndex(e => !IsShortcut(e)); // con filtro, el primer resultado real
        if (index < 0 && visible.Count > 0)
            index = 0;
        if (index >= 0)
            Select(index);

        int folders = visible.Count(e => e.IsDirectory && !IsShortcut(e));
        int files = visible.Count(e => !e.IsDirectory);
        SetText(IdStatus, Tr.BrowseStatus(folders, files, _all.Count - folders - files));
    }

    /// <summary>Filas ".." y "/" que añade la lista (ningún fichero real puede llamarse "/").</summary>
    private static bool IsShortcut(Entry e) => e.Name is ".." or "/";

    /// <summary>Texto con * o ? = comodines; si no, "contiene". Sin distinguir mayúsculas.</summary>
    internal static Func<string, bool>? BuildFilter(string text)
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
    private int Compare(Entry a, Entry b)
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

    private void SortBy(int column)
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

    private void ToggleHidden()
    {
        _showHidden = IsChecked(IdHidden);
        Config.SetFlag("hidden", _showHidden);
        Render(keepSelection: true);
    }

    private int SelectedIndex()
    {
        int index = (int)SendMessage(_list, LVM_GETNEXTITEM, nuint.MaxValue, (nint)LVNI_SELECTED);
        return index >= 0 && index < _entries.Count ? index : -1;
    }

    private void AddColumn(int index, int width, LVCOLUMNW_FORMAT format)
    {
        fixed (char* text = Columns[index])
        {
            var col = new LVCOLUMNW
            {
                mask = LVCOLUMNW_MASK.LVCF_TEXT | LVCOLUMNW_MASK.LVCF_WIDTH | LVCOLUMNW_MASK.LVCF_FMT,
                fmt = format,
                cx = S(width),
                pszText = text,
            };
            SendMessage(_list, LVM_INSERTCOLUMNW, (nuint)index, (nint)(&col));
        }
    }

    /// <summary>Marca con ▲/▼ la columna por la que se ordena.</summary>
    private void UpdateColumnHeaders()
    {
        var columns = Columns;
        for (int i = 0; i < columns.Length; i++)
        {
            string title = i == _sortColumn ? columns[i] + (_sortDescending ? " ▼" : " ▲") : columns[i];
            fixed (char* text = title)
            {
                var col = new LVCOLUMNW { mask = LVCOLUMNW_MASK.LVCF_TEXT, pszText = text };
                SendMessage(_list, LVM_SETCOLUMNW, (nuint)i, (nint)(&col));
            }
        }
    }

    private void AddRow(int index, params string[] cells)
    {
        fixed (char* first = cells[0])
        {
            var item = new LVITEMW { mask = LIST_VIEW_ITEM_FLAGS.LVIF_TEXT, iItem = index, pszText = first };
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

    private void Select(int index)
    {
        const LIST_VIEW_ITEM_STATE_FLAGS flags = LIST_VIEW_ITEM_STATE_FLAGS.LVIS_SELECTED | LIST_VIEW_ITEM_STATE_FLAGS.LVIS_FOCUSED;
        var item = new LVITEMW { stateMask = flags, state = flags };
        SendMessage(_list, LVM_SETITEMSTATE, (nuint)index, (nint)(&item));
        SendMessage(_list, LVM_ENSUREVISIBLE, (nuint)index, 0);
    }
}
