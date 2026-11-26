using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using LongshotLauncher.Core;
using LongshotLauncher.ViewModels;

namespace LongshotLauncher;

/// The window: the mockup's own title bar (drag it to move the window; minimize and close in its corner), the page, a dialog over
/// it, the toast; and what the view models ask of it (IWindowHost)
public sealed partial class MainWindow : Window, IWindowHost
{
    /// The window's size: 1200x760 (MainWindow.axaml), roomy on a 1080p or 1440p screen. On a smaller screen (a 1366x768 laptop,
    /// whose taskbar leaves about 720) it opens as big as the screen's free area allows, down to its minimum (MinWidth, MinHeight);
    /// the smallest size, 1040x680, is the one every page is laid out for too
    public const double DefaultWidth = 1200, DefaultHeight = 760, SmallestWidth = 1040, SmallestHeight = 680;

    /// What the TV view and a window that fills the screen scale the pages up from: the smallest size, so the TV's text stays as
    /// big as a 10-foot view needs, keeping their shape
    const double DesignWidth = SmallestWidth, DesignHeight = SmallestHeight;

    /// The TV view (Core/TvMode: Steam's Gaming Mode and Big Picture, gamescope): full screen, scaled up, square corners, no
    /// Minimize
    readonly bool tv = TvMode.Detect(Environment.GetEnvironmentVariable);
    PadInput? pad;
    /// tools/screenshots: no controller of this PC is read; its scenes press for themselves (Press)
    public static bool Headless { get; set; }

    /// A controller's press, as if made on one (tools/screenshots)
    public void Press(PadAction action) => (pad ??= new PadInput(this, read: false)).Do(action);

    /// Where a controller's moves stay (PadInput): the dialog while one is open, else a page's question while one is shown
    /// (c:Pad.Scope), else null (the whole window)
    internal Control? PadScope => (DataContext as MainViewModel)?.HasModal == true ? Dialog : Controls.Pad.ShownScope(Pages);
    /// The page, for a controller's first press and its B
    internal Control PadPage => Pages;
    /// The title bar's tabs: their Home is B on a page with no Back of its own
    internal Control PadNav => NavBar;

    public MainWindow()
    {
        InitializeComponent();
        if (tv)
        {
            Classes.Add("pad");
            WindowState = WindowState.FullScreen;
            MinimizeButton.IsVisible = false;
        }
        else FitScreen();
        AddGrips();
        Shape();
        // (Windows: the .ico, so the title bar, taskbar and Alt-Tab each take their size, not the 512 pixel logo scaled down)
        if (OperatingSystem.IsWindows()) Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://LongshotLauncher/Assets/longshot.ico")));
        // (pages cross-fade quickly; with less motion asked for, they change at once and nothing in the window animates)
        if (Motion.Reduced) Classes.Add("still");
        Pages.PageTransition = Motion.Reduced ? null : new CrossFade(TimeSpan.FromMilliseconds(140));
        TitleBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && WindowState == WindowState.Normal) BeginMoveDrag(e);
        };
        // (F11: full screen and back, as in a browser; the TV view starts in it)
        KeyDown += (_, e) =>
        {
            // (Esc: the page's or the dialog's Back, Cancel or Keep, as a controller's B; a list or a text box takes it first)
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && !e.Handled)
            {
                (pad ??= new PadInput(this, read: false)).GoBack();
                e.Handled = true;
                return;
            }
            if (e.Key != Key.F11 || e.KeyModifiers != KeyModifiers.None) return;
            WindowState = WindowState != WindowState.FullScreen ? WindowState.FullScreen : WindowState.Normal;
            e.Handled = true;
        };
        Opened += (_, _) => pad ??= new PadInput(this, read: !Design.IsDesignMode && !Headless);
        Closed += (_, _) => pad?.Dispose();
        // (back to the launcher: a build's folder deleted meanwhile, in the file manager, leaves the list)
        Activated += (_, _) => (DataContext as MainViewModel)?.HomeAgain();
        AddHandler(DragDrop.DragEnterEvent, (_, e) => Drag(e, true));
        AddHandler(DragDrop.DragOverEvent, (_, e) => Drag(e, true));
        AddHandler(DragDrop.DragLeaveEvent, (_, e) => Drag(e, false));
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (DataContext is not MainViewModel vm) return;
            var files = e.DataTransfer.TryGetFiles();
            vm.Drop(files?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? new List<string>());
        });
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm) vm.Host = this;
        Shape();
    }

    /// Closing while a build runs asks first, as Cancel does (make_game and its compilers would go on with no window and no row in
    /// the list); step 1's download and checks stop with the window
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is not MainViewModel vm) return;
        if (vm.Page is BuildStepViewModel { Running: true } build)
        {
            e.Cancel = true;
            if (build.Finalizing) build.CloseAfterFinalizing ??= Close;
            else vm.Modal = new StopBuildViewModel(build);
            return;
        }
        if (vm.Page is FilesStepViewModel files) files.Job.Stop();
        else if (vm.Page is SettingsStepViewModel settings) settings.Job.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == ClientSizeProperty || change.Property == ActualTransparencyLevelProperty)
            Shape();
    }

    /// The window's corners and scale: the platform's corners (Core/WindowCorners), square and borderless when the window fills
    /// the screen, where the pages are scaled up to it (wider screens give them a little more width, never a smaller font)
    void Shape()
    {
        if (Frame == null) return;
        var fills = tv || WindowState is WindowState.FullScreen or WindowState.Maximized;
        // (the launcher's platform: tools/screenshots draws Windows' window on Linux too, as Windows 11's)
        var platform = (DataContext as MainViewModel)?.Linux is { } linux ? (linux ? Platform.Linux : Platform.Windows)
            : OperatingSystem.IsWindows() ? Platform.Windows : Platform.Linux;
        var build = OperatingSystem.IsWindows() ? Environment.OSVersion.Version.Build : 22000;
        var radius = WindowCorners.Radius(platform, build, Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), fills,
            ActualTransparencyLevel != WindowTransparencyLevel.None);
        Frame.CornerRadius = new CornerRadius(radius);
        Frame.BorderThickness = new Thickness(fills ? 0 : 1);
        Grips.IsVisible = !fills;
        var scale = fills && ClientSize.Width > 0 && ClientSize.Height > 0
            ? Math.Min(ClientSize.Width / DesignWidth, ClientSize.Height / DesignHeight) : 1;
        Scaler.LayoutTransform = Math.Abs(scale - 1) < 0.01 ? null : new ScaleTransform(scale, scale);
    }

    /// The default size, or less on a screen whose free area (its taskbar and panels left out) is smaller, down to the window's
    /// minimum (MinWidth, MinHeight); the pages lay out in the room they have, and scroll. Nothing to fit without a screen
    /// (tools/screenshots)
    void FitScreen()
    {
        if (Screens?.Primary is not { } screen || screen.Scaling <= 0) return;
        var free = screen.WorkingArea.Size.ToSize(screen.Scaling);
        // (a few pixels clear of the area's edges, so the window's border shows whole)
        Width = Math.Clamp(free.Width - 16, MinWidth, DefaultWidth);
        Height = Math.Clamp(free.Height - 16, MinHeight, DefaultHeight);
    }

    /// The window's edges and corners, each a strip a press on which resizes the window from there (the window draws its own
    /// frame, WindowDecorations None, so the system gives it none to hold); the corners over the edges' ends
    void AddGrips()
    {
        const double edge = 5, corner = 12;
        (WindowEdge Edge, HorizontalAlignment H, VerticalAlignment V, StandardCursorType Cursor)[] grips =
        [
            (WindowEdge.West, HorizontalAlignment.Left, VerticalAlignment.Stretch, StandardCursorType.LeftSide),
            (WindowEdge.East, HorizontalAlignment.Right, VerticalAlignment.Stretch, StandardCursorType.RightSide),
            (WindowEdge.North, HorizontalAlignment.Stretch, VerticalAlignment.Top, StandardCursorType.TopSide),
            (WindowEdge.South, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, StandardCursorType.BottomSide),
            (WindowEdge.NorthWest, HorizontalAlignment.Left, VerticalAlignment.Top, StandardCursorType.TopLeftCorner),
            (WindowEdge.NorthEast, HorizontalAlignment.Right, VerticalAlignment.Top, StandardCursorType.TopRightCorner),
            (WindowEdge.SouthWest, HorizontalAlignment.Left, VerticalAlignment.Bottom, StandardCursorType.BottomLeftCorner),
            (WindowEdge.SouthEast, HorizontalAlignment.Right, VerticalAlignment.Bottom, StandardCursorType.BottomRightCorner),
        ];
        foreach (var (side, h, v, cursor) in grips)
        {
            var isCorner = h != HorizontalAlignment.Stretch && v != VerticalAlignment.Stretch;
            var grip = new Border
            {
                Background = Brushes.Transparent, HorizontalAlignment = h, VerticalAlignment = v, Cursor = new Cursor(cursor),
                Width = h == HorizontalAlignment.Stretch ? double.NaN : isCorner ? corner : edge,
                Height = v == VerticalAlignment.Stretch ? double.NaN : isCorner ? corner : edge,
            };
            grip.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || WindowState != WindowState.Normal) return;
                BeginResizeDrag(side, e);
                e.Handled = true;
            };
            Grips.Children.Add(grip);
        }
    }

    void Drag(DragEventArgs e, bool over)
    {
        if (DataContext is not MainViewModel vm) return;
        var ok = over && vm.AcceptsDrop;
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        vm.Dropping = ok;
    }

    public async Task<string?> PickFileAsync(string title, string[]? patterns)
    {
        var options = new FilePickerOpenOptions { Title = title, AllowMultiple = false };
        if (patterns != null) options.FileTypeFilter = new[] { new FilePickerFileType(string.Join(", ", patterns)) { Patterns = patterns } };
        var files = await StorageProvider.OpenFilePickerAsync(options);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title, string kind, IReadOnlyList<string> patterns)
    {
        var options = new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = true, FileTypeFilter = new[] { new FilePickerFileType(kind) { Patterns = patterns.ToArray() } },
        };
        var files = await StorageProvider.OpenFilePickerAsync(options);
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickFolderAsync(string title, string? start)
    {
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (start != null && Directory.Exists(start)) options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start);
        var folders = await StorageProvider.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string[] patterns)
    {
        var options = new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = patterns.Length > 0 ? Path.GetExtension(patterns[0]).TrimStart('.') : null,
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType(string.Join(", ", patterns)) { Patterns = patterns } },
        };
        var file = await StorageProvider.SaveFilePickerAsync(options);
        return file?.TryGetLocalPath();
    }

    public async Task CopyAsync(string text)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    /// A folder in the file manager, or a link in the browser
    public void Open(string path)
    {
        try
        {
            // (a folder goes to Explorer by name: the shell's "open" on a bare path can run a program of that name instead)
            using var _ = OperatingSystem.IsWindows() && Directory.Exists(path)
                ? Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { Path.TrimEndingDirectorySeparator(path) } })
                : Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            if (DataContext is MainViewModel vm) vm.ShowToast(Core.Tr.F("Could not open {0}: {1}", path, e.Message));
        }
    }

    public void Minimize() => WindowState = WindowState.Minimized;
}
