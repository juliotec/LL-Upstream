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

    /// NVIDIA's shader cache size as Longshot needs it, in MB (Shader Cache Size in the NVIDIA Control Panel: the global
    /// profile's PS_SHADERDISKCACHE_MAX_SIZE). A start of Longshot builds about 2 GB of pipelines into it, the shader
    /// preparation's predicted ones into it alone (Longshot's vk_draw.cpp predict_run), and players' logs show caches held at
    /// 1 GB (1025 MB): the driver drops the oldest as it fills, and those are compiled again in play, the "everything compiles
    /// again" with no driver update. On Linux the game raises the limit for itself (__GL_SHADER_DISK_CACHE_SIZE, Longshot's
    /// hos_main.c driver_cache_room); Windows' driver reads only this setting
    public const uint ShaderCacheNeededMb = 4096;
    /// What a cache under ShaderCacheNeededMb is raised to: Linux's 10 GiB
    public const uint ShaderCacheRoomMb = 10240;

    /// The size NVIDIA's shader cache had, in MB, when it was raised to ShaderCacheRoomMb; null when nothing was done
    /// (ShaderCacheShort). Never lowered. An NvidiaProblem when the driver refused (NVAPI_INVALID_USER_PRIVILEGE, -137, where
    /// saving needs an administrator)
    public static uint? RaiseShaderCache(INvidiaShaderCache nvidia)
    {
        if (ShaderCacheShort(nvidia) is not { } mb) return null;
        nvidia.SetShaderCacheMb(ShaderCacheRoomMb);
        return mb;
    }

    /// NVIDIA's shader cache size in MB when it is under ShaderCacheNeededMb, so RaiseShaderCache would raise it; null when
    /// the driver does not know the setting, the cache is off (Disabled, 0, or the shader cache itself off: the player's
    /// choice) or it already holds ShaderCacheNeededMb (Unlimited is 0xFFFFFFFF)
    public static uint? ShaderCacheShort(INvidiaShaderCache nvidia)
    {
        var (on, mb) = nvidia.ShaderCache();
        return on && mb is { } m && m > 0 && m < ShaderCacheNeededMb ? m : null;
    }

    /// "1 GB", "512 MB": a cache size for the window
    public static string Size(uint mb) => mb % 1024 == 0 ? $"{mb / 1024} GB" : $"{mb} MB";
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

/// NVIDIA's shader cache size (the NVIDIA Control Panel's Shader Cache Size, for every program), behind an interface so that
/// the tests never touch the driver
public interface INvidiaShaderCache
{
    /// Whether the driver's shader cache is on (PS_SHADERDISKCACHE), and its size in MB (PS_SHADERDISKCACHE_MAX_SIZE: the
    /// value set, or the driver's default; null when this driver does not know the setting)
    (bool On, uint? Mb) ShaderCache();
    /// The size set to mb in the current global profile (what the control panel's Global Settings change), and saved
    void SetShaderCacheMb(uint mb);
}

/// INvidiaProfiles through nvapi64.dll, the NVIDIA driver's own (NVAPI's driver settings: NvAPI_DRS_*). Every call opens a
/// session and loads the settings; Add saves them. Function ids and layouts: NVIDIA's open nvapi headers (nvapi_interface.h,
/// NVDRS_APPLICATION_V4, NVDRS_PROFILE_V1). Null from Open: no NVIDIA driver here
[SupportedOSPlatform("windows")]
public sealed class NvApiProfiles : INvidiaProfiles, INvidiaShaderCache
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

    // ---- the shader cache's size: NVDRS_SETTING_V1 (pack 4, 12320 bytes) as bytes, its fields at their offsets ----
    const int SettingBytes = 12320, SettingIdAt = 4100, SettingTypeAt = 4104, CurrentAt = 8220;
    const int SettingNotFound = -160;   // (NVAPI_SETTING_NOT_FOUND: a driver that does not know the setting)
    const uint CacheMaxSizeId = 0x00AC8497, CacheId = 0x00198FFF;   // (PS_SHADERDISKCACHE_MAX_SIZE_ID, PS_SHADERDISKCACHE_ID)

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GlobalProfile(IntPtr session, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetSetting(IntPtr session, IntPtr profile, uint id, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetSetting(IntPtr session, IntPtr profile, IntPtr setting);

    static IntPtr Global(IntPtr session)
    {
        Check(Fn<GlobalProfile>(0x617BFF9F)(session, out var profile), "finding its global settings");   // (NvAPI_DRS_GetCurrentGlobalProfile)
        return profile;
    }

    static T WithSetting<T>(Func<IntPtr, T> body)
    {
        var p = Marshal.AllocHGlobal(SettingBytes);
        try
        {
            Marshal.Copy(new byte[SettingBytes], 0, p, SettingBytes);
            Marshal.WriteInt32(p, 0, SettingBytes | (1 << 16));   // (NVDRS_SETTING_VER1)
            return body(p);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// A DWORD setting of the global profile: its value (set, or the driver's default), null when the driver does not know it
    static uint? Dword(IntPtr session, uint id) => WithSetting<uint?>(p =>
    {
        var status = Fn<GetSetting>(0x73BF8338)(session, Global(session), id, p);   // (NvAPI_DRS_GetSetting)
        if (status == SettingNotFound) return null;
        Check(status, "reading a setting");
        return (uint)Marshal.ReadInt32(p, CurrentAt);
    });

    public (bool On, uint? Mb) ShaderCache() => WithSession(s => (Dword(s, CacheId) != 0, Dword(s, CacheMaxSizeId)));

    public void SetShaderCacheMb(uint mb) => WithSession(s => WithSetting(p =>
    {
        Marshal.WriteInt32(p, SettingIdAt, (int)CacheMaxSizeId);
        Marshal.WriteInt32(p, SettingTypeAt, 0);   // (NVDRS_DWORD_TYPE)
        Marshal.WriteInt32(p, CurrentAt, (int)mb);
        Check(Fn<SetSetting>(0x577DD202)(s, Global(s), p), "setting its shader cache size");   // (NvAPI_DRS_SetSetting)
        Check(Fn<SessionIn>(0xFCBC7E14)(s), "saving its settings");   // (NvAPI_DRS_SaveSettings)
        return 0;
    }));

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
