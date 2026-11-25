using LongshotLauncher.Core;

static partial class Program
{
    /// An NVIDIA driver's program profiles as a dictionary: program (a path, or a file name) to profile name
    sealed class FakeNvidia : INvidiaProfiles
    {
        public readonly Dictionary<string, string> Apps = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string Profile, string Program, string Like)> Added = new();
        public bool Covers(string program) => Apps.ContainsKey(program) || Apps.ContainsKey(Path.GetFileName(program.Replace('\\', '/')));
        public string? ProfileNaming(string program) => Apps.TryGetValue(program, out var p) ? p : null;
        public void Add(string profile, string program, string like)
        {
            Added.Add((profile, program, like));
            Apps[program] = profile;
        }
    }

    /// NVIDIA's shader cache size as a value: On, Mb as the driver gives them; Set what was written
    sealed class FakeCache(bool on, uint? mb) : INvidiaShaderCache
    {
        public uint? Set;
        public (bool On, uint? Mb) ShaderCache() => (on, Set ?? mb);
        public void SetShaderCacheMb(uint value) => Set = value;
    }

    /// NVIDIA's shader cache size raised to 10 GB when it is under 4 GB, and left alone otherwise (DriverSettings.RaiseShaderCache)
    static void NvidiaCacheChecks()
    {
        var small = new FakeCache(true, 1024);
        Require(DriverSettings.RaiseShaderCache(small) == 1024 && small.Set == DriverSettings.ShaderCacheRoomMb,
                "A 1 GB shader cache (the default players' logs show) is raised to 10 GB.");
        Require(DriverSettings.RaiseShaderCache(small) == null && small.Set == DriverSettings.ShaderCacheRoomMb,
                "A raised cache is not raised again.");
        foreach (var (on, mb, why) in new (bool, uint?, string)[]
                 {
                     (true, 4096, "4 GB holds Longshot's pipelines"), (true, 102400, "100 GB is more"), (true, 0xFFFFFFFF, "Unlimited"),
                     (true, 0, "Disabled is the player's"), (false, 1024, "the shader cache turned off is the player's"),
                     (true, null, "a driver that does not know the setting"),
                 })
        {
            var cache = new FakeCache(on, mb);
            Require(DriverSettings.RaiseShaderCache(cache) == null && cache.Set == null, "Left alone: " + why + ".");
        }
        Require(DriverSettings.Size(1024) == "1 GB" && DriverSettings.Size(10240) == "10 GB" && DriverSettings.Size(512) == "512 MB",
                "Cache sizes read as the control panel gives them.");

        // (a player who never changed Shader Cache Size: the global profile holds no such setting, NVAPI_SETTING_NOT_FOUND, and the
        // driver's default holds 1 GB. Read as no setting at all, it was never raised nor offered: an RTX 4050 Laptop's cache stayed
        // at 1025 MB and Longshot built its 40,000 pipelines again before the title)
        const int found = 0, notFound = DriverSettings.SettingNotFound;
        Require(DriverSettings.SettingOrDefault(found, 4096, known: true, 1024) == 4096, "A size set is read as it is.");
        Require(DriverSettings.SettingOrDefault(notFound, 0, known: true, DriverSettings.DriverDefaultCacheMb) == DriverSettings.DriverDefaultCacheMb,
                "Driver Default (no setting in the profile, one the driver knows) is its default's size.");
        Require(DriverSettings.SettingOrDefault(notFound, 0, known: false, 1024) == null, "A setting the driver does not know: none.");
        var byDefault = new FakeCache(true, DriverSettings.SettingOrDefault(notFound, 0, known: true, DriverSettings.DriverDefaultCacheMb));
        Require(DriverSettings.ShaderCacheShort(byDefault) == 1024 && DriverSettings.RaiseShaderCache(byDefault) == 1024
                && byDefault.Set == DriverSettings.ShaderCacheRoomMb,
                "Driver Default's 1 GB is offered and raised to 10 GB, as a 1 GB set by hand.");
        // (the real driver, read only: a size it knows, or a driver without the setting)
        if (OperatingSystem.IsWindows() && NvApiProfiles.Open() is { } real)
            Require(real.ShaderCache().Mb is null or > 0 || !real.ShaderCache().On,
                    "NVIDIA's shader cache size read through NVAPI (NVDRS_SETTING_V1).");
    }

    /// (Windows) Windows' graphics preference and an NVIDIA program profile, kept by the program's path, given to a new build's
    /// program (DriverSettings); and, where an NVIDIA driver is, its profiles read for real (nothing written)
    static void DriverSettingsChecks()
    {
        var root = Folder("driver-settings");
        string Exe(string version) => Path.Combine(root, version, "bin", "longshot.exe");
        var (v1, v2, v3) = (Exe("v1"), Exe("v2"), Exe("v3"));

        // Windows' graphics preference
        var reg = new FakeRegistry();
        Require(DriverSettings.GpuPreference(reg, v3, new[] { v2, v1 }) == null && reg.Read(DriverSettings.GpuPreferencesKey) == null,
                "With no graphics preferences at all nothing is written.");
        reg.Write(DriverSettings.GpuPreferencesKey, new Dictionary<string, object> { [v1] = "GpuPreference=2;", [@"C:\other.exe"] = "GpuPreference=1;" });
        Require(DriverSettings.GpuPreference(reg, v3, new[] { v2, v1 }) == v1
                && (string)reg.Read(DriverSettings.GpuPreferencesKey)![v3] == "GpuPreference=2;",
                "A new build's program takes the graphics preference of the newest build that has one.");
        reg.Write(DriverSettings.GpuPreferencesKey, new Dictionary<string, object> { [v2] = "GpuPreference=1;" });
        Require(DriverSettings.GpuPreference(reg, v3, new[] { v2, v1 }) == null
                && (string)reg.Read(DriverSettings.GpuPreferencesKey)![v3] == "GpuPreference=2;",
                "A program with a preference of its own keeps it.");
        Require(DriverSettings.GpuPreference(reg, Exe("v4"), new[] { Exe("v4") }) == null, "A program is not its own source.");

        // NVIDIA's program profiles
        var nv = new FakeNvidia();
        Require(DriverSettings.Nvidia(nv, v3, new[] { v2, v1 }) == null && nv.Added.Count == 0,
                "With no profile naming an old build nothing is added.");
        nv.Apps[v1] = "Longshot (Smooth Motion)";
        Require(DriverSettings.Nvidia(nv, v3, new[] { v2, v1 }) == "Longshot (Smooth Motion)"
                && nv.Added.SequenceEqual(new[] { ("Longshot (Smooth Motion)", v3, v1) }),
                "A new build's program joins the profile that named an old build's, by its path.");
        Require(DriverSettings.Nvidia(nv, v3, new[] { v2, v1 }) == null && nv.Added.Count == 1, "A program a profile names is left alone.");
        var byName = new FakeNvidia();
        byName.Apps["longshot.exe"] = "Longshot";
        byName.Apps[v1] = "Other";
        Require(DriverSettings.Nvidia(byName, v3, new[] { v1 }) == null && byName.Added.Count == 0,
                "A profile that names the program by its file name already covers a new copy of it.");

        // (the real driver, read only: a predefined profile names notepad.exe; no profile names a made-up path)
        if (OperatingSystem.IsWindows() && NvApiProfiles.Open() is { } real)
            Require(real.Covers("notepad.exe") && real.ProfileNaming(Path.Combine(root, "nowhere", "longshot.exe")) == null,
                    "NVIDIA's driver profiles read through NVAPI (NVDRS_APPLICATION_V4, NVDRS_PROFILE_V1).");
    }
}
