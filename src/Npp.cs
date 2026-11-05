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
    private const uint NPPMSG = 0x0400 + 1000;
    private const uint NPPM_SETSTATUSBAR = NPPMSG + 24;
    private const uint NPPM_SAVECURRENTFILE = NPPMSG + 38;
    private const uint NPPM_GETPLUGINSCONFIGDIR = NPPMSG + 46;
    private const uint NPPM_GETFULLPATHFROMBUFFERID = NPPMSG + 58;
    private const uint NPPM_GETCURRENTBUFFERID = NPPMSG + 60;
    private const uint NPPM_DOFILEOPEN = NPPMSG + 77;
    private const nuint STATUSBAR_DOC_TYPE = 0;

    public const uint NPPN_FILESAVED = 1000 + 8;

    public static IntPtr Handle;

    public static void OpenFile(string path) => Win32.SendMessage(Handle, NPPM_DOFILEOPEN, 0, path);

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
