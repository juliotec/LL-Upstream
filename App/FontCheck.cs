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
    /// The DWriteCore.dll Windows' search found before the start narrowed it (PreferSystemDirectWrite); null: none
    static string? strayDWriteCore;

    /// Skia asks for DWriteCore.dll by name before Windows' own dwrite.dll, and keeps to it when it is found: a DWriteCore.dll that
    /// another program left on the PATH (the Windows App SDK's) and that gives Skia no factory leaves it with no fonts at all.
    /// DLLs named without a path are looked up in the launcher's folder and System32 only, from the start: no PATH, no current
    /// folder. (.NET loads the launcher's own native libraries by their full path)
    public static void PreferSystemDirectWrite()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var found = new System.Text.StringBuilder(1024);
            if (SearchPathW(null, "DWriteCore.dll", null, found.Capacity, found, IntPtr.Zero) is > 0 and < 1024) strayDWriteCore = found.ToString();
            SetDefaultDllDirectories(0x200 /* LOAD_LIBRARY_SEARCH_APPLICATION_DIR */ | 0x800 /* LOAD_LIBRARY_SEARCH_SYSTEM32 */
                                     | 0x400 /* LOAD_LIBRARY_SEARCH_USER_DIRS */);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [DllImport("kernel32.dll")] static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int SearchPathW(string? path, string fileName, string? extension, int bufferLength, System.Text.StringBuilder buffer, IntPtr filePart);

    /// The window's start failed for want of a font (Avalonia's text, on Windows)
    public static bool IsFontFailure(Exception e) =>
        OperatingSystem.IsWindows() && e is InvalidOperationException && e.StackTrace is { } trace
        && (trace.Contains("Avalonia.Media.FontManager") || trace.Contains("Avalonia.Media.Typeface") || trace.Contains("Avalonia.Media.TextFormatting"));

    /// The message: what failed, what Skia and DirectWrite have, and where to report it
    public static string Message(Exception e) =>
        "The launcher cannot start: Windows gave it no fonts to draw its window with.\n\n" +
        "Windows' DirectWrite, which every program's text goes through, did not give the launcher its fonts. Restarting the PC, " +
        "or Windows' System File Checker (sfc /scannow, in a Terminal opened as administrator), may put it right.\n\n" +
        "Press Ctrl+C to copy this message, and send it with your report.\n\n" +
        $"{e.GetType().Name}: {e.Message}\n" +
        $"Skia: {SkiaFamilies()}\n" +
        $"DirectWrite: {DirectWrite()}\n" +
        $"DWriteCore: {strayDWriteCore ?? "none found"}\n" +
        $"Windows: {Environment.OSVersion.Version}";

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
