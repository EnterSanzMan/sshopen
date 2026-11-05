using System.Runtime.InteropServices;
using Renci.SshNet;
using static SshOpen.DialogHost;
using static SshOpen.Win32;

namespace SshOpen;

/// <summary>
/// Explorador modal de una carpeta remota: lista sus carpetas y ficheros, permite entrar en
/// carpetas (doble clic / Intro, Retroceso para subir) y devuelve el fichero elegido.
/// </summary>
internal static unsafe class BrowseDialog
{
    private const int IdDir = 201, IdGo = 202, IdList = 203, IdStatus = 204;
    private const int DC_HASDEFID = 0x534B;

    private sealed record Entry(string Name, string FullName, bool IsDirectory, bool IsLink, long Size, DateTime Modified);

    private static bool _done;
    private static IntPtr _hwnd, _list;
    private static SftpClient _client = null!;
    private static string _current = "/";
    private static List<Entry> _entries = [];
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
        _entries = [];

        _hwnd = CreateFrame(owner, "SshOpenBrowseDialog", &WndProc, title, 600, 432);

        Child(_hwnd, 0, "STATIC", "Carpeta:", 0, 14, 18, 60, 20, 0);
        Child(_hwnd, WS_EX_CLIENTEDGE, "EDIT", folder, WS_TABSTOP | ES_AUTOHSCROLL, 78, 14, 420, 24, IdDir);
        Child(_hwnd, 0, "BUTTON", "Ir", WS_TABSTOP | BS_PUSHBUTTON, 506, 13, 80, 26, IdGo);

        _list = Child(_hwnd, WS_EX_CLIENTEDGE, "SysListView32", "",
            WS_TABSTOP | LVS_REPORT | LVS_SINGLESEL | LVS_SHOWSELALWAYS, 14, 48, 572, 330, IdList);
        SendMessage(_list, LVM_SETEXTENDEDLISTVIEWSTYLE, 0, (nint)(LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER));
        AddColumn(0, "Nombre", 260, 0);
        AddColumn(1, "Tipo", 70, 0);
        AddColumn(2, "Tamaño", 85, LVCFMT_RIGHT);
        AddColumn(3, "Modificado", 125, 0);

        Child(_hwnd, 0, "STATIC", "", 0, 14, 394, 370, 20, IdStatus);
        Child(_hwnd, 0, "BUTTON", "Abrir", WS_TABSTOP | BS_DEFPUSHBUTTON, 398, 388, 90, 28, IDOK);
        Child(_hwnd, 0, "BUTTON", "Cancelar", WS_TABSTOP | BS_PUSHBUTTON, 496, 388, 90, 28, IDCANCEL);

        // Se muestra antes de listar para que los avisos de error cuelguen ya del diálogo.
        EnableWindow(owner, 0);
        ShowWindow(_hwnd, SW_SHOW);

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
                    if (id == IdGo || (id == IDOK && GetFocus() == GetDlgItem(_hwnd, IdDir)))
                        GoTo(Text(_hwnd, IdDir).Trim());
                    else if (id == IDOK)
                        ActivateSelected();
                    else if (id == IDCANCEL)
                        _done = true;
                    return 0;
                case WM_NOTIFY:
                    var header = (NMHDR*)lParam;
                    if (header->idFrom != IdList)
                        break;
                    // NMITEMACTIVATE.iItem y NMLVKEYDOWN.wVKey van justo detrás de la cabecera NMHDR.
                    byte* body = (byte*)lParam + sizeof(NMHDR);
                    if (header->code == NM_DBLCLK && *(int*)body >= 0)
                        ActivateSelected();
                    else if (header->code == LVN_KEYDOWN && *(ushort*)body == VK_BACK)
                        Navigate(Sftp.Parent(_current));
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
        int index = (int)SendMessage(_list, LVM_GETNEXTITEM, nuint.MaxValue, (nint)LVNI_SELECTED);
        if (index < 0 || index >= _entries.Count)
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
        List<Entry> entries;
        try
        {
            using (new WaitCursor())
            {
                entries = _client.ListDirectory(folder)
                    .Where(f => f.Name is not "." and not "..")
                    .Select(f => new Entry(f.Name, f.FullName, f.IsDirectory, f.IsSymbolicLink, f.Length, f.LastWriteTime))
                    .OrderByDescending(e => e.IsDirectory)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            if (showError)
                Plugin.ShowError(_hwnd, "No se puede listar " + folder, ex);
            return false;
        }

        if (folder != "/")
            entries.Insert(0, new Entry("..", Sftp.Parent(folder), true, false, 0, default));

        _current = folder;
        _entries = entries;
        SetText(_hwnd, IdDir, folder);

        SendMessage(_list, LVM_DELETEALLITEMS, 0, 0);
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Name == "..")
            {
                AddRow(i, "..", "Subir", "", "");
                continue;
            }
            AddRow(i, e.Name,
                e.IsDirectory ? "Carpeta" : e.IsLink ? "Enlace" : "Fichero",
                e.IsDirectory ? "" : FormatSize(e.Size),
                e.Modified.ToString("yyyy-MM-dd HH:mm"));
        }
        if (entries.Count > 0)
            Select(0);

        int folders = entries.Count(e => e.IsDirectory && e.Name != "..");
        SetText(_hwnd, IdStatus, $"{folders} carpetas, {entries.Count(e => !e.IsDirectory)} ficheros   ·   Retroceso: subir de carpeta");
        return true;
    }

    private static void AddColumn(int index, string title, int width, int format)
    {
        fixed (char* text = title)
        {
            var col = new LVCOLUMNW { mask = LVCF_TEXT | LVCF_WIDTH | LVCF_FMT, fmt = format, cx = S(width), pszText = text };
            SendMessage(_list, LVM_INSERTCOLUMNW, (nuint)index, (nint)(&col));
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
