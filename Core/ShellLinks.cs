using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace LongshotLauncher.Core;

/// (Windows) A shortcut (.lnk) written through the shell's own ShellLink object, with its AppUserModelID: the Start menu lists
/// one entry for each app it tells apart, and it tells shortcuts apart by that id. Without one, two shortcuts to the same
/// program are the same app to it, and it shows only the first by name: the game's Longshot.lnk (the launcher with --play) hid
/// the launcher's own Longshot Launcher.lnk, which seemed deleted. Each shortcut the launcher makes names its id
/// (Shortcuts.LauncherAppId, GameAppId). Through source-generated COM (ComWrappers), as the release is built without the
/// runtime's own COM support; in place of WScript.Shell through PowerShell, which cannot set the id and gave no word when it
/// failed
[SupportedOSPlatform("windows")]
public static partial class ShellLinks
{
    /// The shortcut at link (written over, its folder made): target with arguments, started in workingDirectory, icon's first
    /// icon, description its tooltip, appId its AppUserModelID. IOException when the shell refused
    public static void Write(string link, string target, string arguments, string workingDirectory, string icon, string description,
                             string appId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(link))!);
        WithCom(() =>
        {
            var shell = Create();
            shell.SetPath(target);
            shell.SetArguments(arguments);
            shell.SetWorkingDirectory(workingDirectory);
            shell.SetIconLocation(icon, 0);
            shell.SetDescription(description);
            var store = (IPropertyStore)shell;
            var value = new PropVariant { Type = VtLpwstr, Pointer = Marshal.StringToCoTaskMemUni(appId) };
            try
            {
                var key = AppUserModelId;
                store.SetValue(in key, in value);
                store.Commit();
            }
            finally { Marshal.FreeCoTaskMem(value.Pointer); }
            ((IPersistFile)shell).Save(link, 1);
        });
    }

    /// What the shortcut at link starts and the AppUserModelID it names ("" none); null when there is no shortcut there
    public static (string Target, string Arguments, string AppId)? Read(string link)
    {
        if (!File.Exists(link)) return null;
        (string, string, string)? read = null;
        WithCom(() =>
        {
            var shell = Create();
            ((IPersistFile)shell).Load(link, 0);
            var path = Marshal.AllocCoTaskMem(2 * 32768);
            try
            {
                shell.GetPath(path, 32768, IntPtr.Zero, 0x4);   // (SLGP_RAWPATH: as stored)
                var target = Marshal.PtrToStringUni(path) ?? "";
                shell.GetArguments(path, 32768);
                var arguments = Marshal.PtrToStringUni(path) ?? "";
                var key = AppUserModelId;
                ((IPropertyStore)shell).GetValue(in key, out var value);
                var id = value.Type == VtLpwstr && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) ?? "" : "";
                PropVariantClear(ref value);
                read = (target, arguments, id);
            }
            finally { Marshal.FreeCoTaskMem(path); }
        });
        return read;
    }

    // ---- the shell's interfaces (ShObjIdl_core.h, ObjIdl.h, PropSys.h), in their vtable order ----

    const ushort VtLpwstr = 31;
    static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");
    // (PKEY_AppUserModel_ID: {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5)
    static readonly PropertyKey AppUserModelId = new() { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]   // (PROPVARIANT: the type, three reserved words, a 16-byte union)
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        void GetPath(IntPtr file, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription(IntPtr name, int size);
        void SetDescription(string name);
        void GetWorkingDirectory(IntPtr dir, int size);
        void SetWorkingDirectory(string dir);
        void GetArguments(IntPtr args, int size);
        void SetArguments(string args);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation(IntPtr path, int size, out int index);
        void SetIconLocation(string path, int index);
        void SetRelativePath(string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath(string file);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("0000010b-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        void GetClassID(out Guid classId);   // (IPersist's)
        [PreserveSig] int IsDirty();
        void Load(string file, uint mode);
        void Save(string file, int remember);
        void SaveCompleted(string file);
        void GetCurFile(out IntPtr file);
    }

    [GeneratedComInterface, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal partial interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(in PropertyKey key, out PropVariant value);
        void SetValue(in PropertyKey key, in PropVariant value);
        void Commit();
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid classId, IntPtr outer, uint context, in Guid interfaceId, out IntPtr instance);
    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(IntPtr reserved, uint mode);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial int PropVariantClear(ref PropVariant value);

    static readonly StrategyBasedComWrappers Wrappers = new();

    static IShellLinkW Create()
    {
        var iid = typeof(IShellLinkW).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ShellLinkClass, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, iid, out var unknown));
        try { return (IShellLinkW)Wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance); }
        finally { Marshal.Release(unknown); }
    }

    /// body with COM started on this thread, as the shell's objects need (the window's thread has it; the start before it may not).
    /// What the shell refused comes out as an IOException, which every caller of the shortcuts already takes
    static void WithCom(Action body)
    {
        var hr = CoInitializeEx(IntPtr.Zero, 0x2 /* COINIT_APARTMENTTHREADED */);
        try { body(); }
        catch (Exception e) when (e is COMException or InvalidCastException) { throw new IOException("The shortcut could not be written: " + e.Message, e); }
        finally { if (hr >= 0) CoUninitialize(); }   // (S_OK or S_FALSE; RPC_E_CHANGED_MODE: started otherwise by its owner)
    }
}
