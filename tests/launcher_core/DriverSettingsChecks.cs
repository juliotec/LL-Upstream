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
