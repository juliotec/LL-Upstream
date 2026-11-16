using System.Runtime.Versioning;
using Microsoft.Win32;

namespace LongshotLauncher;

/// This PC's CPU name, from the registry (CentralProcessor\0's ProcessorNameString), spaces made single; null when it has none
[SupportedOSPlatform("windows")]
static class WindowsCpu
{
    const string Processor = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    public static string? Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(Processor);
            return key?.GetValue("ProcessorNameString") is string name &&
                   System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ").Trim() is { Length: > 0 } single ? single : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}
