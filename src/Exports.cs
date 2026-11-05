using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SshOpen;

/// <summary>
/// Funciones C exportadas que Notepad++ busca en la DLL del plugin. Ninguna excepción puede
/// salir de aquí: cruzar la frontera nativa con una excepción tumba Notepad++.
/// </summary>
internal static unsafe class Exports
{
    private const int FuncCount = 4;
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
            SetFunc(0, "Abrir fichero remoto (SSH)...", &Commands.OpenRemote);
            SetFunc(1, "Subir fichero actual al servidor", &Commands.UploadCurrent);
            SetFunc(2, "", null); // separador
            SetFunc(3, "Acerca de...", &Commands.About);
        }
        *count = FuncCount;
        return _funcs;
    }

    [UnmanagedCallersOnly(EntryPoint = "beNotified")]
    private static void BeNotified(NotifyHeader* header)
    {
        if (header->Code != Npp.NPPN_FILESAVED || header->HwndFrom != Npp.Handle)
            return;
        nuint bufferId = header->IdFrom;
        Commands.Guard(() => Plugin.OnFileSaved(bufferId));
    }

    [UnmanagedCallersOnly(EntryPoint = "messageProc")]
    private static nint MessageProc(uint message, nuint wParam, nint lParam) => 1;

    private static void SetFunc(int index, string name, delegate* unmanaged<void> func)
    {
        FuncItem* item = &_funcs[index];
        name.AsSpan(0, Math.Min(name.Length, 63)).CopyTo(new Span<char>(item->ItemName, 64));
        item->PFunc = (IntPtr)func;
    }
}

internal static class Commands
{
    [UnmanagedCallersOnly]
    public static void OpenRemote() => Guard(Plugin.OpenRemote);

    [UnmanagedCallersOnly]
    public static void UploadCurrent() => Guard(Plugin.UploadCurrent);

    [UnmanagedCallersOnly]
    public static void About() => Guard(Plugin.About);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Plugin.ShowError("Error inesperado", ex);
        }
    }
}
