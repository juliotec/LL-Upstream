using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

/// Other versions of a build (its row's Other versions): the versions of its channel the server still has, to build one in its
/// place. It runs as an update does (BuildJob, JobKind.Update): in the build's folder, its row then that version. An older one
/// goes back (BuildJob.Older), and Update then offers the newest again
public sealed class VersionPickerViewModel : ViewModelBase
{
    readonly BuildEntry build;
    string? pick;

    public sealed class Row(VersionPickerViewModel owner, OtherVersion version) : ViewModelBase
    {
        public string Tag => version.Tag;
        public string Version => Versions.Shown(version.Tag);
        public string When => version.Published is { } at ? Durations.Day(at.LocalDateTime, DateTime.Now) : "";
        public bool Older => version.Older;
        public string Order => version.Older ? Tr.T("Older") : Tr.T("Newer");

        public bool IsPicked
        {
            get => owner.Pick == version.Tag;
            set
            {
                if (value) owner.Pick = version.Tag;
            }
        }

        public void Changed() => Raise(nameof(IsPicked));
    }

    public VersionPickerViewModel(MainViewModel main, BuildEntry build, IReadOnlyList<OtherVersion> others)
    {
        Main = main;
        this.build = build;
        Rows = others.Select(o => new Row(this, o)).ToList();
        // (the newest older one first: the player is most likely here to step back from the version they have)
        pick = others.FirstOrDefault(o => o.Older)?.Tag ?? others.FirstOrDefault()?.Tag;
        GoCommand = new Command(Go, () => Picked != null);
        CancelCommand = new Command(() => Main.Modal = null);
    }

    public MainViewModel Main { get; }
    public IReadOnlyList<Row> Rows { get; }
    public Command GoCommand { get; }
    public Command CancelCommand { get; }

    string Channel => UpdatePolicy.ChannelOf(build.Channel, build.Version);
    string Have => build.Version is { } v ? Versions.Shown(v) : build.Name;

    public string Title => Tr.F("Other versions of {0}", Have);
    public bool IsNightly => Channel == Versions.Nightly;

    public string? Pick
    {
        get => pick;
        set
        {
            if (!Set(ref pick, value)) return;
            foreach (var r in Rows) r.Changed();
            RaiseAll(nameof(GoText), nameof(Hint));
            GoCommand.Changed();
        }
    }

    Row? Picked => Rows.FirstOrDefault(r => r.Tag == pick);

    public string GoText => Picked is { } r ? Tr.F(r.Older ? "Go back to {0}" : "Update to {0}", r.Version) : Tr.T("Continue");

    /// What building it does to the version you have
    public string Hint => Picked is { Older: true } r
        ? Tr.F("{0} is built in place of {1}, in its folder. Update brings the newest back whenever you want it.", r.Version, Have)
        : Picked is { } n ? Tr.F("{0} is built in place of {1}, in its folder.", n.Version, Have) : "";

    void Go()
    {
        if (Picked is not { } r) return;
        Main.Modal = null;
        Main.StartWizard(JobKind.Update, Channel, r.Tag, build);
    }
}
