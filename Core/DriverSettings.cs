using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LongshotLauncher.Core;

/// (Windows) What the system and the GPU driver keep per program, by the program's full path, brought to a build in a new folder
/// before it starts: an update made a new folder, so a new path, and the player's choices for the old one stopped applying
/// (Longshot issue 224: NVIDIA Smooth Motion had to be set again after every update). The two kept so far:
/// - Windows' graphics preference (Settings > System > Display > Graphics: which GPU the program runs on), a value of
///   HKEY_CURRENT_USER\Software\Microsoft\DirectX\UserGpuPreferences named by the program's path (GpuPreference);
/// - an NVIDIA driver profile that names the old build's program by its path (the NVIDIA app's and Control Panel's
///   program settings, Nvidia): the new program is added to the same profile, so every setting in it applies, whatever it is.
/// The sources are the other builds' programs, the most recently updated first. A program that has its own entry already is
/// left alone, and nothing is ever taken away
public static class DriverSettings
{
    public const string GpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

    static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// Windows' graphics preference of the first source that has one given to target, when target has none: the source's
    /// program path, null when nothing was done
    public static string? GpuPreference(IUserRegistry registry, string target, IEnumerable<string> sources)
    {
        var values = registry.Read(GpuPreferencesKey);
        if (values == null) return null;
        if (values.Keys.Any(k => Same(k, target))) return null;
        foreach (var source in sources)
        {
            if (Same(source, target)) continue;
            var found = values.FirstOrDefault(kv => Same(kv.Key, source));
            if (found.Key == null || found.Value is not string preference || preference.Length == 0) continue;
            registry.Write(GpuPreferencesKey, new Dictionary<string, object> { [target] = preference });
            return found.Key;
        }
        return null;
    }

    /// The NVIDIA profile of the first source a profile names by its path, given target too, when no profile names target (by
    /// its path or its file name): the profile's name, null when nothing was done. An NvidiaProblem when the driver refused
    public static string? Nvidia(INvidiaProfiles nvidia, string target, IEnumerable<string> sources)
    {
        if (nvidia.Covers(target)) return null;
        foreach (var source in sources)
        {
            if (Same(source, target)) continue;
            if (nvidia.ProfileNaming(source) is not { } profile) continue;
            nvidia.Add(profile, target, source);
            return profile;
        }
        return null;
    }
}

/// Why the NVIDIA driver refused (its NVAPI status, e.g. -137 NVAPI_INVALID_USER_PRIVILEGE)
public sealed class NvidiaProblem(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// The NVIDIA driver's program profiles (DRS), behind an interface so that the tests never touch the driver
public interface INvidiaProfiles
{
    /// Whether a profile names program, by its path or its file name (then a new copy of it is already covered)
    bool Covers(string program);
    /// The name of the profile that names program by its full path, null when none does
    string? ProfileNaming(string program);
    /// program added to the profile named profile, as like holds there (its friendly name, its folder file), and saved
    void Add(string profile, string program, string like);
}

/// INvidiaProfiles through nvapi64.dll, the NVIDIA driver's own (NVAPI's driver settings: NvAPI_DRS_*). Every call opens a
/// session and loads the settings; Add saves them. Function ids and layouts: NVIDIA's open nvapi headers (nvapi_interface.h,
/// NVDRS_APPLICATION_V4, NVDRS_PROFILE_V1). Null from Open: no NVIDIA driver here
[SupportedOSPlatform("windows")]
public sealed class NvApiProfiles : INvidiaProfiles
{
    const int UnicodeMax = 2048;   // (NVAPI_UNICODE_STRING_MAX)
    const int ExecutableNotFound = -166;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct App
    {
        public uint Version, IsPredefined;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = UnicodeMax)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = UnicodeMax)] public string UserFriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = UnicodeMax)] public string Launcher;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = UnicodeMax)] public string FileInFolder;
        public uint Flags;   // (isMetro:1, isCommandLine:1)
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = UnicodeMax)] public string CommandLine;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Profile
    {
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = UnicodeMax)] public string ProfileName;
        public uint GpuSupport, IsPredefined, NumOfApps, NumOfSettings;
    }

    static uint VersionOf<T>(int version) => (uint)Marshal.SizeOf<T>() | (uint)(version << 16);

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface")] static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SessionOut(out IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SessionIn(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    delegate int FindApp(IntPtr session, [MarshalAs(UnmanagedType.LPWStr)] string name, out IntPtr profile, ref App app);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ProfileInfo(IntPtr session, IntPtr profile, ref Profile info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CreateApp(IntPtr session, IntPtr profile, ref App app);

    static T Fn<T>(uint id) where T : Delegate =>
        QueryInterface(id) is var p && p != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<T>(p)
            : throw new NvidiaProblem($"NVAPI has no function {id:x8}", -3);

    NvApiProfiles() { }

    /// The driver's profiles, or null when there is no NVIDIA driver (no nvapi64.dll, or it does not start)
    public static NvApiProfiles? Open()
    {
        try { return Fn<NoArgs>(0x0150E828)() == 0 ? new NvApiProfiles() : null; }   // (NvAPI_Initialize)
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NvidiaProblem) { return null; }
    }

    static void Check(int status, string what)
    {
        if (status != 0) throw new NvidiaProblem($"NVIDIA's driver refused {what} (NVAPI status {status})", status);
    }

    static T WithSession<T>(Func<IntPtr, T> body)
    {
        Check(Fn<SessionOut>(0x0694D52E)(out var session), "a settings session");   // (NvAPI_DRS_CreateSession)
        try
        {
            Check(Fn<SessionIn>(0x375DBD6B)(session), "loading its settings");   // (NvAPI_DRS_LoadSettings)
            return body(session);
        }
        finally { Fn<SessionIn>(0xDAD9CFF8)(session); }   // (NvAPI_DRS_DestroySession)
    }

    static (IntPtr Profile, App App)? Find(IntPtr session, string program)
    {
        var app = new App { Version = VersionOf<App>(4) };
        var status = Fn<FindApp>(0xEEE566B2)(session, program, out var profile, ref app);   // (NvAPI_DRS_FindApplicationByName)
        if (status == ExecutableNotFound) return null;
        Check(status, "finding a program");
        return (profile, app);
    }

    public bool Covers(string program) => WithSession(s => Find(s, program) != null);

    public string? ProfileNaming(string program) => WithSession(s =>
    {
        if (Find(s, program) is not { } found || !string.Equals(found.App.AppName, program, StringComparison.OrdinalIgnoreCase)) return null;
        var info = new Profile { Version = VersionOf<Profile>(1) };
        Check(Fn<ProfileInfo>(0x61CD6FD6)(s, found.Profile, ref info), "reading a profile");   // (NvAPI_DRS_GetProfileInfo)
        return info.ProfileName;
    });

    public void Add(string profile, string program, string like) => WithSession(s =>
    {
        var old = Find(s, like) ?? throw new NvidiaProblem($"No NVIDIA profile names {like} any more", ExecutableNotFound);
        var app = new App
        {
            Version = VersionOf<App>(4), AppName = program, UserFriendlyName = old.App.UserFriendlyName, Launcher = "",
            FileInFolder = old.App.FileInFolder, Flags = old.App.Flags, CommandLine = old.App.CommandLine,
        };
        Check(Fn<CreateApp>(0x4347A9DE)(s, old.Profile, ref app), "adding the program to the profile");   // (NvAPI_DRS_CreateApplication)
        Check(Fn<SessionIn>(0xFCBC7E14)(s), "saving its settings");   // (NvAPI_DRS_SaveSettings)
        return 0;
    });
}
