using System.Text.RegularExpressions;

namespace LongshotLauncher.Core;

/// A graphics card: its maker (SystemInfo.Nvidia, Amd, Intel) and, where the system names it (Windows), its name
public sealed record Gpu(string Vendor, string? Name = null);

/// What the build settings say about this PC: its memory, its CPU threads and the makers of its graphics cards
public static class SystemInfo
{
    public const string Nvidia = "NVIDIA", Amd = "AMD", Intel = "Intel";

    /// The memory this PC has, in whole GB (what .NET may use: the PC's, or a container's limit)
    public static int MemoryGb() => (int)Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (double)(1L << 30));

    public static int Threads() => Environment.ProcessorCount;

    /// Build workers when the setting is Automatic: every CPU thread but one, so the PC stays usable while it builds (make_game's
    /// own automatic takes them all)
    public static int AutomaticWorkers() => Math.Max(1, Threads() - 1);

    /// The PCI vendor id's maker: 0x10de NVIDIA, 0x1002 AMD, 0x8086 Intel; null for another
    public static string? VendorOf(string pciVendor) => pciVendor.Trim().ToLowerInvariant() switch
    {
        "0x10de" => Nvidia,
        "0x1002" => Amd,
        "0x8086" => Intel,
        _ => null,
    };

    /// The maker of a graphics card by its Vulkan device name (the game log's "vk: NVIDIA GeForce RTX 4070 Ti, Vulkan 1.4, ..."
    /// line); null for another (llvmpipe, a Mali, ...)
    public static string? VendorOfName(string deviceName)
    {
        bool Has(string s) => deviceName.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("NVIDIA") || Has("GeForce") || Has("Quadro")) return Nvidia;
        if (Has("AMD") || Has("Radeon")) return Amd;
        if (Has("Intel")) return Intel;
        return null;
    }

    /// The makers of the graphics cards Linux lists in /sys/class/drm (card&lt;n&gt;/device/vendor), each once, in card order.
    /// drm: that folder (the tests' stand-in). Windows' are read from its registry (App/WindowsGpus.cs)
    public static IReadOnlyList<string> GpuVendors(Platform platform, string drm = "/sys/class/drm")
    {
        var found = new List<string>();
        if (platform != Platform.Linux) return found;
        try
        {
            foreach (var card in Directory.EnumerateDirectories(drm, "card*").Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(card);
                if (name.Contains('-')) continue;   // (card0-DP-1 and the like are its outputs)
                var vendorFile = Path.Combine(card, "device", "vendor");
                if (!File.Exists(vendorFile)) continue;
                if (VendorOf(File.ReadAllText(vendorFile)) is { } v && !found.Contains(v)) found.Add(v);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return found;
    }

    /// NVIDIA DLSS runs on it: an NVIDIA GeForce RTX (or RTX workstation) card. A card known only by its maker (Linux) is taken
    /// as able; a named one without RTX in its name (GTX, Quadro P) is not
    public static bool DlssRuns(Gpu gpu) =>
        gpu.Vendor == Nvidia && (gpu.Name is not { } n || n.Contains("RTX", StringComparison.OrdinalIgnoreCase));

    /// AMD FSR 4 runs on it: an AMD Radeon of the RX 6000 series (RDNA 2) or newer. A card known only by its maker (Linux) is taken
    /// as able, as is a name this can't place (an APU's "AMD Radeon(TM) Graphics"); RX 5000 and older, Vega and the R5/R7/R9 and
    /// HD cards are not
    public static bool FsrRuns(Gpu gpu) => gpu.Vendor == Amd && (gpu.Name is not { } n || !OldRadeon(n));

    static bool OldRadeon(string name)
    {
        if (Regex.Match(name, @"\bRX\s*(\d{3,4})\b", RegexOptions.IgnoreCase) is { Success: true } rx)
            return int.Parse(rx.Groups[1].Value) < 6000;
        return Regex.IsMatch(name, @"\b(Vega|R[579]\s|HD\s*\d)", RegexOptions.IgnoreCase);
    }

    /// Whether this PC runs an upscaler: one of its cards runs it, or no card was found (unknown). Else its switch says so in red,
    /// and still turns on (Upscalers.Unsupported)
    public static bool Offers(IReadOnlyList<Gpu> gpus, Func<Gpu, bool> runs) => gpus.Count == 0 || gpus.Any(runs);

    /// Intel shader compiles applies here: Windows with Intel graphics, the only place Longshot holds Intel's compiler to one
    /// thread (its gpu/vk/vk_internal.h IntelDriverGate). No card found: not offered, as the choice only matters on Intel
    public static bool IntelCompilesApply(Platform platform, IReadOnlyList<Gpu> gpus) =>
        platform == Platform.Windows && gpus.Any(g => g.Vendor == Intel);

    /// The maker of a Windows display adapter by its MatchingDeviceId (pci\ven_10de&amp;dev_2782...); null for another
    public static string? VendorOfDeviceId(string deviceId) =>
        Regex.Match(deviceId, @"ven_([0-9a-f]{4})", RegexOptions.IgnoreCase) is { Success: true } m ? VendorOf("0x" + m.Groups[1].Value) : null;
}
