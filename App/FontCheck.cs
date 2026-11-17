using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace LongshotLauncher;

/// Windows' fonts as the window gets them. Avalonia makes every font through Skia, and Skia on Windows only through DirectWrite:
/// the system's and the launcher's own Inter alike. On a PC where DirectWrite gives Skia nothing, the window cannot draw a letter
/// and Avalonia throws before it shows ("Default font family name can't be null or empty", then "Could not create glyphTypeface"
/// once Inter was the default): the launcher and its setup closed without a word (#123). This says so instead, with what
/// DirectWrite answered, for the report
static class FontCheck
{
    /// The DWriteCore.dll Windows' search finds, which Skia asks for before Windows' own dwrite.dll (FindDWriteCore); null: none
    static string? strayDWriteCore;

    /// Skia asks for DWriteCore.dll by name before Windows' own dwrite.dll, and keeps to it when it is found: the message names
    /// any it would find. (DLLs are no longer looked up only in the launcher's folder and System32: no DWriteCore.dll was the
    /// cause in #123, and Windows' own dwrite.dll failed to load all the same)
    public static void FindDWriteCore()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var found = new System.Text.StringBuilder(1024);
            if (SearchPathW(null, "DWriteCore.dll", null, found.Capacity, found, IntPtr.Zero) is > 0 and < 1024) strayDWriteCore = found.ToString();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int SearchPathW(string? path, string fileName, string? extension, int bufferLength, System.Text.StringBuilder buffer, IntPtr filePart);

    /// The window's start failed for want of a font (Avalonia's text, on Windows)
    public static bool IsFontFailure(Exception e) =>
        OperatingSystem.IsWindows() && e is InvalidOperationException && e.StackTrace is { } trace
        && (trace.Contains("Avalonia.Media.FontManager") || trace.Contains("Avalonia.Media.Typeface") || trace.Contains("Avalonia.Media.TextFormatting"));

    /// The message: what failed, what Skia and DirectWrite have, and where to report it
    public static string Message(Exception e) =>
        "The launcher cannot start: Windows gave it no fonts to draw its window with.\n\n" +
        "Windows' DirectWrite, which every program's text goes through, did not give the launcher its fonts. Security software " +
        "that runs unknown programs with restrictions can cause this: if one is installed, allow the launcher in it.\n\n" +
        "Press Ctrl+C to copy this message, and send it with your report.\n\n" +
        $"{e.GetType().Name}: {e.Message}\n" +
        $"Skia: {SkiaFamilies()}\n" +
        $"DirectWrite: {DirectWrite()}\n" +
        $"dwrite.dll from System32: {SystemDWrite()}\n" +
        $"DWriteCore: {strayDWriteCore ?? "none found"}\n" +
        $"Restrictions: {Restrictions()}\n" +
        $"Other modules: {OtherModules()}\n" +
        $"Windows: {Environment.OSVersion.Version}, {RuntimeInformation.ProcessArchitecture} process";

    /// dwrite.dll loaded by its full path, and Windows' own error when it is not: (only this process fails to load it on a PC
    /// where PowerShell loads it and lists its fonts, #123)
    static string SystemDWrite()
    {
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, "dwrite.dll");
            if (!File.Exists(path)) return "missing: " + path;
            var module = LoadLibraryExW(path, IntPtr.Zero, 0);
            if (module != IntPtr.Zero) return "loads";
            var error = Marshal.GetLastPInvokeError();
            return $"error {error} (0x{error:X}): {new System.ComponentModel.Win32Exception(error).Message}";
        }
        catch (Exception x) { return $"{x.GetType().Name}: {x.Message}"; }
    }

    /// What Windows holds this process to, when anything: signed images only, no remote or low-label images, no dynamic code
    static string Restrictions()
    {
        try
        {
            var found = new List<string>();
            var self = Process.GetCurrentProcess().Handle;
            foreach (var (policy, name) in new[] { (2, "dynamic code"), (8, "signature"), (10, "image load"), (12, "child process"), (13, "side channel") })
                if (GetProcessMitigationPolicy(self, policy, out var flags, sizeof(uint)) && flags != 0) found.Add($"{name} 0x{flags:X}");
            return found.Count == 0 ? "none" : string.Join(", ", found);
        }
        catch (Exception x) { return $"{x.GetType().Name}: {x.Message}"; }
    }

    /// The DLLs in this process that are neither Windows' nor the launcher's own (.NET's, Skia's): what security software or an
    /// overlay put in it
    static string OtherModules()
    {
        try
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var own = Path.GetDirectoryName(AppContext.BaseDirectory) ?? "";
            var others = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Select(m => m.FileName)
                .Where(f => !f.StartsWith(windows, StringComparison.OrdinalIgnoreCase) && !f.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)
                            && (own.Length == 0 || !f.StartsWith(own, StringComparison.OrdinalIgnoreCase))
                            && !Path.GetFileName(f).StartsWith("libSkiaSharp", StringComparison.OrdinalIgnoreCase)
                            && !Path.GetFileName(f).StartsWith("libHarfBuzzSharp", StringComparison.OrdinalIgnoreCase)
                            && !Path.GetFileName(f).StartsWith("av_libglesv2", StringComparison.OrdinalIgnoreCase)
                            && !f.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                .Take(12).ToList();
            return others.Count == 0 ? "none" : string.Join("; ", others);
        }
        catch (Exception x) { return $"{x.GetType().Name}: {x.Message}"; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll")]
    static extern bool GetProcessMitigationPolicy(IntPtr process, int policy, out uint buffer, int length);

    static string SkiaFamilies()
    {
        try { return $"{SKFontManager.Default.FontFamilyCount} font families"; }
        catch (Exception x) { return $"{x.GetType().Name}: {x.Message}"; }
    }

    /// DirectWrite asked as Skia asks it: its shared factory, then the system's font collection
    static string DirectWrite()
    {
        try
        {
            var created = DWriteCreateFactory(0 /* DWRITE_FACTORY_TYPE_SHARED */, FactoryId, out var factory);
            if (created < 0 || factory == IntPtr.Zero) return $"DWriteCreateFactory failed, 0x{created:X8}";
            try
            {
                var got = Method<GetSystemFontCollectionFn>(factory, 3)(factory, out var collection, 0);
                if (got < 0 || collection == IntPtr.Zero) return $"GetSystemFontCollection failed, 0x{got:X8}";
                try { return $"{Method<GetFontFamilyCountFn>(collection, 3)(collection)} font families"; }
                finally { Method<ReleaseFn>(collection, 2)(collection); }
            }
            finally { Method<ReleaseFn>(factory, 2)(factory); }
        }
        catch (Exception x) { return $"{x.GetType().Name}: {x.Message}"; }
    }

    static readonly Guid FactoryId = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");   // (IID_IDWriteFactory)

    /// A COM object's method, by its place in the object's table
    static T Method<T>(IntPtr self, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), slot * IntPtr.Size));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint ReleaseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetSystemFontCollectionFn(IntPtr self, out IntPtr collection, int checkForUpdates);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint GetFontFamilyCountFn(IntPtr self);

    [DllImport("dwrite.dll")] static extern int DWriteCreateFactory(int factoryType, in Guid iid, out IntPtr factory);
}
