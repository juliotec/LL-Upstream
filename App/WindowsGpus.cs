using System.Runtime.Versioning;
using LongshotLauncher.Core;
using Microsoft.Win32;

namespace LongshotLauncher;

/// This PC's display adapters, from the registry's display class: each one's maker (MatchingDeviceId's ven_) and name
/// (DriverDesc). Microsoft's basic display adapter and remote displays have no maker this knows, so they are left out
[SupportedOSPlatform("windows")]
static class WindowsGpus
{
    const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static IReadOnlyList<Gpu> Read()
    {
        var found = new List<Gpu>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (cls == null) return found;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsAsciiDigit)).Order(StringComparer.Ordinal))
            {
                using var key = cls.OpenSubKey(sub);
                if (key?.GetValue("MatchingDeviceId") is not string id || SystemInfo.VendorOfDeviceId(id) is not { } vendor) continue;
                var gpu = new Gpu(vendor, (key.GetValue("DriverDesc") as string)?.Trim() is { Length: > 0 } name ? name : null);
                if (!found.Contains(gpu)) found.Add(gpu);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return found;
    }
}
