using System.Runtime.InteropServices;

namespace SshOpen;

// Estructuras de la interfaz de plugins de Notepad++ (PluginInterface.h / Notepad_plus_msgs.h).

[StructLayout(LayoutKind.Sequential)]
internal struct NppData
{
    public IntPtr NppHandle;
    public IntPtr ScintillaMainHandle;
    public IntPtr ScintillaSecondHandle;
}

/// <summary>Solo la cabecera (NMHDR) de SCNotification: es lo único que usamos.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NotifyHeader
{
    public IntPtr HwndFrom;
    public nuint IdFrom;
    public uint Code;
}

/// <summary>DockedWidgetData / tTbData de Docking.h.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TbData
{
    public IntPtr hClient;
    public IntPtr pszName;
    public int dlgID;          // índice en funcItem del comando que muestra el panel
    public uint uMask;         // DWS_DF_CONT_*, DWS_ICONTAB...
    public IntPtr hIconTab;
    public IntPtr pszAddInfo;
    public RECT rcFloat;       // uso interno de Notepad++
    public int iPrevCont;      // uso interno de Notepad++
    public IntPtr pszModuleName; // nombre del fichero DLL, con ".dll"
}

[StructLayout(LayoutKind.Sequential)]
internal struct ShortcutKey
{
    public byte IsCtrl;
    public byte IsAlt;
    public byte IsShift;
    public byte Key; // código de tecla virtual
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FuncItem
{
    public fixed char ItemName[64];
    public IntPtr PFunc;
    public int CmdID;
    public byte Init2Check;
    public IntPtr PShKey;
}

/// <summary>Mensajes a la ventana principal de Notepad++.</summary>
internal static unsafe class Npp
{
    public const uint DWS_DF_CONT_LEFT = 0 << 28;

    private const uint NPPMSG = 0x0400 + 1000;
    private const uint NPPM_GETNBOPENFILES = NPPMSG + 7;
    private const uint NPPM_MODELESSDIALOG = NPPMSG + 12;
    private const nuint MODELESSDIALOGADD = 0, MODELESSDIALOGREMOVE = 1;
    private const uint NPPM_SETSTATUSBAR = NPPMSG + 24;
    private const uint NPPM_RELOADFILE = NPPMSG + 36;
    private const uint NPPM_SAVECURRENTFILE = NPPMSG + 38;
    private const uint NPPM_GETPLUGINSCONFIGDIR = NPPMSG + 46;
    private const uint NPPM_GETFULLPATHFROMBUFFERID = NPPMSG + 58;
    private const uint NPPM_GETBUFFERIDFROMPOS = NPPMSG + 59;
    private const uint NPPM_GETCURRENTBUFFERID = NPPMSG + 60;
    private const uint NPPM_DMMSHOW = NPPMSG + 30;
    private const uint NPPM_DMMHIDE = NPPMSG + 31;
    private const uint NPPM_DMMREGASDCKDLG = NPPMSG + 33;
    private const uint NPPM_SETMENUITEMCHECK = NPPMSG + 40;
    private const uint NPPM_DOOPEN = NPPMSG + 77;
    private const uint NPPM_DARKMODESUBCLASSANDTHEME = NPPMSG + 112;
    private const nuint DarkModeInit = 0x0000000B; // NppDarkMode::dmfInit
    private const nuint STATUSBAR_DOC_TYPE = 0;

    public const uint NPPN_READY = 1000 + 1;
    public const uint NPPN_FILESAVED = 1000 + 8;
    public const uint NPPN_SHUTDOWN = 1000 + 9;

    /// <summary>Notificación de cierre de un panel acoplado (dockingResource.h: DMN_FIRST + 1).</summary>
    public const int DMN_CLOSE = 1050 + 1;

    public static IntPtr Handle;

    public static void OpenFile(string path) => Win32.SendMessage(Handle, NPPM_DOOPEN, 0, path);

    public static void SetMenuItemCheck(int cmdId, bool check) =>
        Win32.SendMessage(Handle, NPPM_SETMENUITEMCHECK, (nuint)cmdId, check ? 1 : 0);

    /// <summary>Registra una ventana como panel acoplable. <paramref name="data"/> y sus cadenas deben vivir siempre.</summary>
    public static void RegisterDockingPanel(TbData* data) => Win32.SendMessage(Handle, NPPM_DMMREGASDCKDLG, 0, (nint)data);

    public static void ShowDockingPanel(IntPtr hwnd) => Win32.SendMessage(Handle, NPPM_DMMSHOW, 0, hwnd);

    public static void HideDockingPanel(IntPtr hwnd) => Win32.SendMessage(Handle, NPPM_DMMHIDE, 0, hwnd);

    /// <summary>
    /// Mete o saca una ventana de la lista de diálogos no modales: Notepad++ le pasa entonces los
    /// mensajes por IsDialogMessage antes que por sus atajos (Tab, Intro y F5 llegan al panel).
    /// </summary>
    public static void RegisterModeless(IntPtr hwnd, bool add) =>
        Win32.SendMessage(Handle, NPPM_MODELESSDIALOG, add ? MODELESSDIALOGADD : MODELESSDIALOGREMOVE, hwnd);

    /// <summary>Aplica el modo oscuro de Notepad++ (si está activo) a un diálogo propio ya con sus controles.</summary>
    public static void ApplyDarkMode(IntPtr hwnd) => Win32.SendMessage(Handle, NPPM_DARKMODESUBCLASSANDTHEME, DarkModeInit, hwnd);

    /// <summary>Recarga desde disco un fichero ya abierto, sin preguntar.</summary>
    public static void ReloadFile(string path) => Win32.SendMessage(Handle, NPPM_RELOADFILE, 0, path);

    /// <summary>Buffers abiertos en las dos vistas (incluye los "new N" sin ruta).</summary>
    public static List<nuint> OpenBufferIds()
    {
        var ids = new List<nuint>();
        for (int view = 0; view < 2; view++)
        {
            int count = (int)Win32.SendMessage(Handle, NPPM_GETNBOPENFILES, 0, view + 1); // 1 = vista principal, 2 = secundaria
            for (int i = 0; i < count; i++)
            {
                nuint id = (nuint)Win32.SendMessage(Handle, NPPM_GETBUFFERIDFROMPOS, (nuint)i, view);
                if (id != 0)
                    ids.Add(id);
            }
        }
        return ids;
    }

    public static bool SaveCurrentFile() => Win32.SendMessage(Handle, NPPM_SAVECURRENTFILE, 0, 0) != 0;

    public static void SetStatus(string text) => Win32.SendMessage(Handle, NPPM_SETSTATUSBAR, STATUSBAR_DOC_TYPE, text);

    public static nuint CurrentBufferId => (nuint)Win32.SendMessage(Handle, NPPM_GETCURRENTBUFFERID, 0, 0);

    public static string? GetBufferPath(nuint bufferId)
    {
        int len = (int)Win32.SendMessage(Handle, NPPM_GETFULLPATHFROMBUFFERID, bufferId, 0);
        if (len <= 0)
            return null;
        var buffer = new char[len + 1];
        fixed (char* p = buffer)
            Win32.SendMessage(Handle, NPPM_GETFULLPATHFROMBUFFERID, bufferId, (nint)p);
        return new string(buffer, 0, len);
    }

    public static string GetPluginConfigDir()
    {
        var buffer = new char[1024];
        fixed (char* p = buffer)
            Win32.SendMessage(Handle, NPPM_GETPLUGINSCONFIGDIR, (nuint)buffer.Length, (nint)p);
        string dir = new string(buffer).TrimEnd('\0');
        if (dir.Length == 0)
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Notepad++", "plugins", "config");
        return dir;
    }
}
