using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SshOpen;

/// <summary>
/// Funciones C exportadas que Notepad++ busca en la DLL del plugin. Ninguna excepción puede
/// salir de aquí: cruzar la frontera nativa con una excepción tumba Notepad++.
/// </summary>
internal static unsafe class Exports
{
    private const int FuncCount = 5;
    private static readonly IntPtr NamePtr = Marshal.StringToHGlobalUni(Plugin.Name);
    private static FuncItem* _funcs;

    [UnmanagedCallersOnly(EntryPoint = "isUnicode")]
    private static int IsUnicode() => 1;

    [UnmanagedCallersOnly(EntryPoint = "setInfo")]
    private static void SetInfo(NppData data) => Npp.Handle = data.NppHandle;

    [UnmanagedCallersOnly(EntryPoint = "getName")]
    private static IntPtr GetName() => NamePtr;

    [UnmanagedCallersOnly(EntryPoint = "getFuncsArray")]
    private static FuncItem* GetFuncsArray(int* count)
    {
        if (_funcs == null)
        {
            _funcs = (FuncItem*)NativeMemory.AllocZeroed((nuint)(sizeof(FuncItem) * FuncCount));
            SetFunc(0, "Abrir fichero remoto (SSH)...", &Commands.OpenRemote, Shortcut(ctrl: true, alt: true, key: 'O'));
            SetFunc(1, "Subir fichero actual al servidor", &Commands.UploadCurrent, Shortcut(ctrl: true, alt: true, key: 'U'));
            SetFunc(RemotePanel.MenuIndex, "Panel remoto", &Commands.TogglePanel);
            SetFunc(3, "", null); // separador
            SetFunc(4, "Acerca de...", &Commands.About);
        }
        *count = FuncCount;
        return _funcs;
    }

    [UnmanagedCallersOnly(EntryPoint = "beNotified")]
    private static void BeNotified(NotifyHeader* header)
    {
        if (header->HwndFrom != Npp.Handle)
            return;
        switch (header->Code)
        {
            case Npp.NPPN_READY:
                Commands.Guard(Plugin.OnReady);
                break;
            case Npp.NPPN_FILESAVED:
                nuint bufferId = header->IdFrom;
                Commands.Guard(() => Plugin.OnFileSaved(bufferId));
                break;
            case Npp.NPPN_SHUTDOWN:
                Commands.Guard(RemotePanel.Shutdown);
                break;
        }
    }

    /// <summary>Identificador de menú que Notepad++ asignó al comando <paramref name="index"/>.</summary>
    public static int CmdId(int index) => _funcs == null ? 0 : _funcs[index].CmdID;

    [UnmanagedCallersOnly(EntryPoint = "messageProc")]
    private static nint MessageProc(uint message, nuint wParam, nint lParam) => 1;

    private static void SetFunc(int index, string name, delegate* unmanaged<void> func, ShortcutKey* shortcut = null)
    {
        FuncItem* item = &_funcs[index];
        name.AsSpan(0, Math.Min(name.Length, 63)).CopyTo(new Span<char>(item->ItemName, 64));
        item->PFunc = (IntPtr)func;
        item->PShKey = (IntPtr)shortcut;
    }

    /// <summary>Atajo por defecto; el usuario puede cambiarlo en Configuración → Administrador de atajos.</summary>
    private static ShortcutKey* Shortcut(bool ctrl, bool alt, char key)
    {
        var sk = (ShortcutKey*)NativeMemory.AllocZeroed((nuint)sizeof(ShortcutKey)); // vive mientras el plugin
        sk->IsCtrl = ctrl ? (byte)1 : (byte)0;
        sk->IsAlt = alt ? (byte)1 : (byte)0;
        sk->Key = (byte)key;
        return sk;
    }
}

internal static class Commands
{
    [UnmanagedCallersOnly]
    public static void OpenRemote() => Guard(Plugin.OpenRemote);

    [UnmanagedCallersOnly]
    public static void UploadCurrent() => Guard(Plugin.UploadCurrent);

    [UnmanagedCallersOnly]
    public static void TogglePanel() => Guard(() => RemotePanel.Toggle());

    [UnmanagedCallersOnly]
    public static void About() => Guard(Prompts.About);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Prompts.Error("Error inesperado", ex);
        }
    }
}
