using Avalonia.Controls;

namespace LongshotLauncher.Views;

// The pages and dialogs: each its .axaml, bound to its view model (compiled bindings); no logic here but where the view scrolls

public sealed partial class HomeView : UserControl { public HomeView() => InitializeComponent(); }
public sealed partial class BuildCardView : UserControl { public BuildCardView() => InitializeComponent(); }
public sealed partial class VersionsView : UserControl { public VersionsView() => InitializeComponent(); }
public sealed partial class ProfilesView : UserControl { public ProfilesView() => InitializeComponent(); }
public sealed partial class WhoView : UserControl { public WhoView() => InitializeComponent(); }
public sealed partial class FilesStepView : UserControl { public FilesStepView() => InitializeComponent(); }
public sealed partial class SettingsStepView : UserControl
{
    public SettingsStepView()
    {
        InitializeComponent();
        // (opening Advanced scrolls the step's content, never the footer, to show it: the mockup's revealAdvanced)
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.SettingsStepViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(vm.AdvancedOpen) && vm.AdvancedOpen)
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => Body.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Background);
                };
        };
    }

    /// For the screenshots: the content scrolled to its end, as opening Advanced leaves it
    public void ScrollToEnd() => Body.ScrollToEnd();
}
public sealed partial class BuildStepView : UserControl
{
    ViewModels.BuildStepViewModel? vm;

    public BuildStepView()
    {
        InitializeComponent();
        // (the details follow the log as a terminal does: while at the bottom, a new line scrolls them there again; scrolled up
        // to read, they stay where they are (the view model holds the text still) until scrolled back down. Opening them, or
        // another level or search, starts at the bottom)
        LogScroll.ScrollChanged += (_, e) =>
        {
            if (vm is not { } m) return;
            // (one event per layout pass, a new line's growth and the reader's wheel together: a move up that isn't the clamp
            // of a shrinking text is the reader's)
            if (e.OffsetDelta.Y < 0 && e.ExtentDelta.Y >= 0) m.Following = false;
            else if (e.ExtentDelta.Y == 0 && e.OffsetDelta.Y != 0) m.Following = AtBottom();
            if (m.Following && e.ExtentDelta.Y != 0) FollowLater();
        };
        DataContextChanged += (_, _) =>
        {
            if (vm != null) vm.PropertyChanged -= Changed;   // (the page's view is reused for the next build: the last one let go)
            vm = DataContext as ViewModels.BuildStepViewModel;
            if (vm != null) vm.PropertyChanged += Changed;
            written = null;
            WriteLog();
        };
    }

    bool AtBottom() => LogScroll.Offset.Y + LogScroll.Viewport.Height >= LogScroll.Extent.Height - 4;

    /// To the bottom once the text is laid out, unless the reader scrolled up in the meantime
    void FollowLater() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (vm?.Following == true) LogScroll.ScrollToEnd(); },
                                                    Avalonia.Threading.DispatcherPriority.Background);

    IReadOnlyList<(Core.LogLevel Level, string Text)>? written;
    Avalonia.Threading.DispatcherTimer? writeSoon;

    /// The details' lines, each after its level's prefix in its colour (LogColors). Not again when they are the ones shown already
    /// (a new line the level or search leaves out, the build's tick)
    void WriteLog()
    {
        if (vm is not { ShowLog: true } m) return;
        var lines = m.LogLines;
        if (written is { } w && w.Count == lines.Count
            && (w.Count == 0 || (ReferenceEquals(w[0].Text, lines[0].Text) && ReferenceEquals(w[^1].Text, lines[^1].Text))))
            return;
        written = lines;
        LogBlock.ItemsSource = lines.Select(l => new LogRow(Core.LogLevels.Prefix(l.Level) + "  ", Controls.LogColors.Of(l.Level), l.Text)).ToList();
    }

    /// WriteLog at most every fifth of a second: a build at Trace adds thousands of lines a second, each raising LogText
    void WriteLogSoon()
    {
        writeSoon ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(200), Avalonia.Threading.DispatcherPriority.Normal,
                                                             (_, _) => { writeSoon!.Stop(); WriteLog(); });
        if (!writeSoon.IsEnabled) writeSoon.Start();
    }

    void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (vm is not { } m) return;
        if (e.PropertyName == nameof(m.LogText)) WriteLogSoon();
        if ((e.PropertyName == nameof(m.ShowLog) && m.ShowLog) || e.PropertyName is nameof(m.Level) or nameof(m.Search))
        {
            // (another level or search, or the details opened: written now, from the bottom)
            written = null;
            WriteLog();
            m.Following = true;
            FollowLater();
        }
    }
}

/// A line of the build's details, as BuildStepView shows it
public sealed record LogRow(string Prefix, Avalonia.Media.IBrush Colour, string Text);

public sealed partial class ReadyView : UserControl { public ReadyView() => InitializeComponent(); }
public sealed partial class ProblemView : UserControl { public ProblemView() => InitializeComponent(); }
public sealed partial class ProblemPageView : UserControl { public ProblemPageView() => InitializeComponent(); }
public sealed partial class SettingsView : UserControl { public SettingsView() => InitializeComponent(); }
public sealed partial class StorageView : UserControl { public StorageView() => InitializeComponent(); }
public sealed partial class SavesView : UserControl { public SavesView() => InitializeComponent(); }
public sealed partial class SavesConfirmView : UserControl { public SavesConfirmView() => InitializeComponent(); }
public sealed partial class SavesResultView : UserControl { public SavesResultView() => InitializeComponent(); }
public sealed partial class SaveEditorView : UserControl { public SaveEditorView() => InitializeComponent(); }
public sealed partial class SaveEditConfirmView : UserControl { public SaveEditConfirmView() => InitializeComponent(); }
public sealed partial class SaveEditAskView : UserControl { public SaveEditAskView() => InitializeComponent(); }
public sealed partial class BackupsView : UserControl { public BackupsView() => InitializeComponent(); }
public sealed partial class ChannelPickerView : UserControl { public ChannelPickerView() => InitializeComponent(); }
public sealed partial class AddVersionView : UserControl { public AddVersionView() => InitializeComponent(); }
public sealed partial class DeleteView : UserControl { public DeleteView() => InitializeComponent(); }
public sealed partial class BuildFilesView : UserControl { public BuildFilesView() => InitializeComponent(); }
public sealed partial class LicenceView : UserControl { public LicenceView() => InitializeComponent(); }
public sealed partial class StopBuildView : UserControl { public StopBuildView() => InitializeComponent(); }
public sealed partial class SavePickView : UserControl { public SavePickView() => InitializeComponent(); }
public sealed partial class HookshotImportView : UserControl { public HookshotImportView() => InitializeComponent(); }
public sealed partial class StableLauncherView : UserControl { public StableLauncherView() => InitializeComponent(); }
public sealed partial class HookshotSettingsView : UserControl { public HookshotSettingsView() => InitializeComponent(); }
public sealed partial class ReleaseNotesView : UserControl { public ReleaseNotesView() => InitializeComponent(); }
// (files and folders dropped on the NFC page go through the window's own drop handling, as step 1's do: MainViewModel.Drop)
public sealed partial class NfcView : UserControl { public NfcView() => InitializeComponent(); }
public sealed partial class NfcDeleteView : UserControl { public NfcDeleteView() => InitializeComponent(); }
public sealed partial class RemoveEverythingView : UserControl { public RemoveEverythingView() => InitializeComponent(); }
public sealed partial class PullRequestWarningView : UserControl { public PullRequestWarningView() => InitializeComponent(); }
public sealed partial class PullRequestConfirmView : UserControl { public PullRequestConfirmView() => InitializeComponent(); }
public sealed partial class PullRequestFindView : UserControl { public PullRequestFindView() => InitializeComponent(); }
public sealed partial class RepointView : UserControl { public RepointView() => InitializeComponent(); }
