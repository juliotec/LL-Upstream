using System.Collections.ObjectModel;
using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

/// One version in the home's list
public sealed class BuildItemViewModel : ViewModelBase
{
    readonly HomeViewModel home;
    bool selected;

    public BuildItemViewModel(HomeViewModel home, BuildEntry entry, bool built, string? update, IReadOnlyList<SaveSlot> saves)
    {
        this.home = home;
        Entry = entry;
        IsBuilt = built;
        Update = update;
        Saves = saves;
        SelectCommand = new Command(() => home.Select(this));
        PlayCommand = new Command(() => home.Play(this), () => IsBuilt);
        SetDefaultCommand = new Command(() => home.SetDefault(this));
        DeleteCommand = new Command(() => home.AskDelete(this));
        UpdateCommand = new Command(() => home.UpdateBuild(this));
        SavesCommand = new Command(() => home.Main.ShowSaves(Entry));
        FolderCommand = new Command(() => home.Main.Host?.Open(Entry.Folder));
    }

    public BuildEntry Entry { get; }
    public IReadOnlyList<SaveSlot> Saves { get; }
    /// The version as players see it (a nightly: "Nightly 2029 · c544828f", Versions.Shown), else the build's name
    public string Version => Entry.Version is { } v ? Versions.Shown(v) : Entry.Name;
    public string Channel => UpdatePolicy.ChannelOf(Entry.Channel, Entry.Version);
    public bool IsRelease => Channel == Versions.Stable;
    public bool IsCandidate => Channel == Versions.Candidate;
    public bool IsNightly => Channel == Versions.Nightly;
    public bool IsDefault => Entry.Default;
    public bool IsBuilt { get; }
    public string? Update { get; }
    public bool HasUpdate => Update != null;
    public string UpdateLine => Update == null ? "" : Versions.Shown(Update) + " is out";

    /// "Built 3 Nov, 38.4 GB, 3 save slots" (no saves count on Linux, where every build shares them)
    public string Meta
    {
        get
        {
            var bits = new List<string> { "Built " + Durations.Day(Entry.Updated.LocalDateTime, DateTime.Now) };
            if (!IsBuilt) bits.Add("not built");
            if (Entry.Bytes is long b) bits.Add(Durations.Size(b));
            if (!home.Main.Linux) bits.Add(SaveText.Count(Saves.Count));
            return string.Join(", ", bits);
        }
    }

    public bool IsSelected
    {
        get => selected;
        set
        {
            if (Set(ref selected, value)) RaiseAll(nameof(ShowActions), nameof(ShowOtherActions), nameof(ShowDefaultNote));
        }
    }

    public bool ShowActions => selected;
    public bool ShowOtherActions => selected && !IsDefault;
    public bool ShowDefaultNote => selected && IsDefault;
    public bool ShowRing => !IsDefault;
    public bool ShowMeta => !HasUpdate;
    public bool IsFirst { get; set; }
    public Avalonia.Thickness Separator => IsFirst ? default : new Avalonia.Thickness(0, 1, 0, 0);

    public void RaiseMeta() => Raise(nameof(Meta));

    public Command SelectCommand { get; }
    public Command PlayCommand { get; }
    public Command SetDefaultCommand { get; }
    public Command DeleteCommand { get; }
    public Command UpdateCommand { get; }
    public Command SavesCommand { get; }
    public Command FolderCommand { get; }
}

/// Home: Build when there is no build; else the default version on the left (Play, its update, its saves) and every version on
/// the right
public sealed class HomeViewModel : ViewModelBase
{
    string? notice;
    string status = "";
    BuildEntry? pendingUpdate;
    bool updatingLauncher;

    public HomeViewModel(MainViewModel main)
    {
        Main = main;
        BuildCommand = new Command(() => StartBuild(Service.DefaultChannel), () => NewestDefault != null);
        ChannelCommand = new Command(() => Main.Modal = new ChannelPickerViewModel(Main, Service.DefaultChannel));
        DismissProblemCommand = new Command(() => Notice = null);
        RetryCheckCommand = new Command(() => _ = CheckReleasesAsync());
        PlayDefaultCommand = new Command(() => { if (Default is { } d) Play(d); }, () => Default?.IsBuilt == true);
        UpdateDefaultCommand = new Command(() => { if (Default is { } d) UpdateBuild(d); });
        SavesDefaultCommand = new Command(() => Main.ShowSaves(Default?.Entry));
        UpdateLauncherCommand = new Command(() => _ = UpdateLauncherAsync(), () => !updatingLauncher);
        ImportHookshotCommand = new Command(() => Main.Modal = new HookshotImportViewModel(Main, Default?.Entry));
        NotNowHookshotCommand = new Command(() =>
        {
            Service.Settings.HookshotSavesAsked = true;
            try { Service.SaveSettings(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            HookshotOffer = null;
        });
    }

    public MainViewModel Main { get; }
    LauncherService Service => Main.Service;

    public ObservableCollection<BuildItemViewModel> Builds { get; } = new();

    public bool HasBuilds => Builds.Count > 0;
    public bool NoBuilds => Builds.Count == 0;
    public BuildItemViewModel? Default => Builds.FirstOrDefault(b => b.IsDefault);

    // ---- the empty home ----

    public string? NewestDefault => Service.NewestOf(Service.DefaultChannel);
    public string NewestDefaultText => NewestDefault is { } v ? Versions.Shown(v) : "";
    public string DefaultChannel => Service.DefaultChannel;
    public bool DefaultIsRelease => DefaultChannel == Versions.Stable;
    public bool DefaultIsCandidate => DefaultChannel == Versions.Candidate;
    public bool DefaultIsNightly => DefaultChannel == Versions.Nightly;
    public bool KnowsNewest => NewestDefault != null;
    public bool CheckFailed => NewestDefault == null && Service.Checked;
    public bool Checking => NewestDefault == null && !Service.Checked;

    // ---- the default version, on the left ----

    public string HeroVersion => Default?.Version ?? "";
    public string HeroChannel => Default?.Channel ?? Versions.Stable;
    public bool HeroIsRelease => HeroChannel == Versions.Stable;
    public bool HeroIsCandidate => HeroChannel == Versions.Candidate;
    public bool HeroIsNightly => HeroChannel == Versions.Nightly;

    /// "Your default version. Built 3 Nov, English (US), NVIDIA DLSS."
    public string HeroNote
    {
        get
        {
            if (Default is not { } d) return "";
            var e = d.Entry;
            var bits = new List<string>
            {
                "Built " + Durations.Day(e.Updated.LocalDateTime, DateTime.Now),
            };
            if (GameFolder.Language(e.Folder) is { } lang) bits.Add(Languages.NameOf(lang));
            if (e.Dlss ?? GameFolder.HasDlss(e.Folder)) bits.Add("NVIDIA DLSS");
            if (e.Fsr == true) bits.Add("AMD FSR 4");
            if (!d.IsBuilt) bits.Add("not built: its game is missing");
            return "Your default version. " + string.Join(", ", bits) + ".";
        }
    }

    public string HeroSaves => Default is { } d ? SaveText.Short(d.Saves, DateTime.Now) : "";
    public bool HeroHasUpdate => Default?.HasUpdate == true;
    public string HeroUpdate => Default is { Update: { } u } d ? $"{u} is out on the {d.Channel} channel." : "";

    /// "3 versions use 116.7 GB in D:\Games\Longshot."
    public string Footer
    {
        get
        {
            var n = Builds.Count;
            var known = Builds.Where(b => b.Entry.Bytes != null).Sum(b => b.Entry.Bytes!.Value);
            var what = n == 1 ? "1 version" : n + " versions";
            return known > 0 ? $"{what} {(n == 1 ? "uses" : "use")} {Durations.Size(known)} in {ShownPath.Of(Service.BuildsFolder)}." : $"{what} in {ShownPath.Of(Service.BuildsFolder)}.";
        }
    }

    // ---- notices ----

    /// A problem to read (a build that would not start): null for none
    public string? Notice
    {
        get => notice;
        set
        {
            if (Set(ref notice, value)) Raise(nameof(HasNotice));
        }
    }

    public bool HasNotice => notice != null;
    public bool ShowLeftPanel => HasBuilds;

    /// The quiet answer of the update check
    public string Status
    {
        get => status;
        set => Set(ref status, value);
    }

    public Command BuildCommand { get; }
    public Command ChannelCommand { get; }
    public Command DismissProblemCommand { get; }
    public Command RetryCheckCommand { get; }
    public Command PlayDefaultCommand { get; }
    public Command UpdateDefaultCommand { get; }
    public Command SavesDefaultCommand { get; }
    public Command UpdateLauncherCommand { get; }
    public Command ImportHookshotCommand { get; }
    public Command NotNowHookshotCommand { get; }

    // ---- the offer to import Hookshot's saves (once: until it is answered) ----

    string? hookshotOffer;
    public string? HookshotOffer
    {
        get => hookshotOffer;
        set { if (Set(ref hookshotOffer, value)) Raise(nameof(HasHookshotOffer)); }
    }
    public bool HasHookshotOffer => hookshotOffer != null;

    /// Hookshot saves on this PC, not offered yet: the home offers to import them (Windows: once there is a build to import into)
    void OfferHookshot()
    {
        if (Service.Settings.HookshotSavesAsked || (!Main.Linux && Service.Registry.Builds.Count == 0)) return;
        try
        {
            var found = HookshotSaves.Find(Service.SaveEnv).Where(s => s.Valid).ToList();
            if (found.Count > 0)
                HookshotOffer = $"Found {SaveText.Long(found[0].Slots, DateTime.Now).ToLowerInvariant()} in Hookshot. Copy them into Longshot? " +
                                "Hookshot keeps its own.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SaveFailure) { }
    }

    // ---- the launcher's own update ----

    public bool HasLauncherUpdate => Service.LauncherUpdate != null;
    /// Only a newer one "is out": after the player switched the launcher's channel, its newest can be older than this one, or a
    /// nightly that has no order against a release
    int LauncherUpdateOrder => Service.LauncherUpdate is { } r ? SelfUpdate.Compare(r.Version, Service.OwnTag) : 0;

    public string LauncherUpdateHeading => LauncherUpdateOrder > 0 ? "A new launcher is out" : $"Another launcher on the {Service.LauncherChannel} channel";

    public string LauncherUpdateButton => LauncherUpdateOrder > 0 ? "Update the launcher" : "Switch to it";

    public string LauncherUpdateText => Service.LauncherUpdate is { } r
        ? LauncherUpdateOrder switch
        {
            > 0 => $"Longshot Launcher {Versions.Shown(r.Version)} is out (you have {Versions.Shown(Service.OwnTag)}).",
            < 0 => $"Longshot Launcher {Versions.Shown(r.Version)} is available on {Service.LauncherChannel}. It is older than yours " +
                   $"({Versions.Shown(Service.OwnTag)}).",
            _ => $"Longshot Launcher {Versions.Shown(r.Version)} is available on {Service.LauncherChannel} (you have {Versions.Shown(Service.OwnTag)}).",
        }
        : "";

    /// Downloads the new launcher, checks it, puts it in place and starts it; this one closes. A failure leaves this one as it is
    async Task UpdateLauncherAsync()
    {
        updatingLauncher = true;
        UpdateLauncherCommand.Changed();
        try
        {
            Status = "Downloading the new launcher…";
            await Task.Run(() => Service.UpdateLauncherAsync(p => Ui.Post(() => Status = $"Downloading the new launcher… {p:P0}")));
            Main.Host?.Close();
        }
        catch (Exception e) when (e is FetchException or IOException or InvalidDataException or InvalidOperationException
                                      or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Status = "";
            Notice = "The launcher could not update itself: " + e.Message;
        }
        finally
        {
            updatingLauncher = false;
            UpdateLauncherCommand.Changed();
        }
    }

    /// The start's own request: --launch and --update name a build (listed if it was not)
    public void Open(StartRequest start)
    {
        var notes = new List<string>();
        if (Service.Registry.Problem is { } broken) notes.Add("The list of builds could not be read (kept as builds.json.broken): " + broken);
        if (start.Problem != null) notes.Add(start.Problem);
        if (start.Folder != null && start.Kind is StartKind.Launch or StartKind.Update)
        {
            var entry = Service.Ensure(start.Folder);
            if (start.Kind == StartKind.Update && start.Tag != null && Versions.IsTagName(start.Tag))
                Ui.Post(() => Main.StartWizard(JobKind.Update, UpdatePolicy.ChannelOf(entry.Channel, entry.Version), start.Tag, entry));
            // (no --tag: its channel's newest, once the update check has answered)
            else if (start.Kind == StartKind.Update) pendingUpdate = entry;
        }
        Notice = notes.Count > 0 ? string.Join("\n", notes) : null;
        OfferHookshot();
        Refresh();
    }

    public void Refresh()
    {
        var selected = Builds.FirstOrDefault(b => b.IsSelected)?.Entry.Id;
        Builds.Clear();
        foreach (var b in Service.Registry.Builds)
        {
            var item = new BuildItemViewModel(this, b, Service.IsBuilt(b), Service.UpdateFor(b), Service.SavesOf(b));
            item.IsSelected = b.Id == selected;
            item.IsFirst = Builds.Count == 0;
            Builds.Add(item);
        }
        RaiseAll(nameof(HasBuilds), nameof(NoBuilds), nameof(Default), nameof(ShowLeftPanel), nameof(HeroVersion), nameof(HeroChannel),
                 nameof(HeroIsRelease), nameof(HeroIsCandidate), nameof(HeroIsNightly), nameof(HeroNote), nameof(HeroSaves), nameof(HeroHasUpdate),
                 nameof(HeroUpdate), nameof(Footer), nameof(NewestDefault), nameof(NewestDefaultText), nameof(DefaultChannel), nameof(DefaultIsRelease), nameof(DefaultIsCandidate),
                 nameof(DefaultIsNightly), nameof(KnowsNewest), nameof(CheckFailed), nameof(Checking), nameof(HasLauncherUpdate),
                 nameof(LauncherUpdateText), nameof(LauncherUpdateHeading), nameof(LauncherUpdateButton));
        BuildCommand.Changed();
        PlayDefaultCommand.Changed();
        Service.Measure(() => { foreach (var b in Builds) b.RaiseMeta(); Raise(nameof(Footer)); });
    }

    /// The update check, in the background: a failure is one quiet line, never a stop
    public async Task CheckReleasesAsync()
    {
        Service.Checked = false;
        Refresh();
        try
        {
            var any = await Service.CheckReleasesAsync();
            Status = any ? "" : Service.Releases.Problem is { } problem
                ? "Updates were not checked: " + problem
                : "Could not reach git.magicbean.dev: updates were not checked.";
        }
        catch (Exception e)
        {
            Service.Checked = true;
            Status = "Could not check for updates: " + e.Message;
        }
        Refresh();
        if (pendingUpdate is { } p && Main.Page == this)
        {
            pendingUpdate = null;
            var version = p.Version is { } v ? Versions.Shown(v) : p.Name;
            if (Service.UpdateFor(p) is { } to) Main.StartWizard(JobKind.Update, UpdatePolicy.ChannelOf(p.Channel, p.Version), to, p);
            else if (Service.NewestOf(UpdatePolicy.ChannelOf(p.Channel, p.Version)) != null) Main.ShowToast($"{version} is up to date.");
        }
    }

    public void Select(BuildItemViewModel b)
    {
        var on = !b.IsSelected;
        foreach (var x in Builds) x.IsSelected = false;
        b.IsSelected = on;
    }

    public void Play(BuildItemViewModel b)
    {
        try
        {
            Service.Play(b.Entry);
            Main.ShowToast($"Starting Ocarina of Time, {b.Version}…");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Notice = $"{b.Version} could not start: {e.Message}";
        }
    }

    public void SetDefault(BuildItemViewModel b)
    {
        Service.SetDefault(b.Entry);
        Refresh();
        Main.ShowToast($"Play now starts {b.Version}.");
    }

    public void AskDelete(BuildItemViewModel b) => Main.Modal = new DeleteViewModel(Main, b);

    public void UpdateBuild(BuildItemViewModel b)
    {
        if (b.Update is not { } to) return;
        Main.StartWizard(JobKind.Update, b.Channel, to, b.Entry);
    }

    public void StartBuild(string channel)
    {
        if (Service.NewestOf(channel) is not { } version) return;
        var same = Service.Registry.Builds.FirstOrDefault(b => b.Version == version);
        Main.StartWizard(same != null ? JobKind.Rebuild : JobKind.Build, channel, version, same);
    }
}

/// Choosing what to build: release, rc or nightly, each with its newest version and whether you have it
public sealed class ChannelPickerViewModel : ViewModelBase
{
    string pick;

    public sealed class Row(ChannelPickerViewModel owner, string id, string name, string about, string? version, bool have) : ViewModelBase
    {
        public string Id => id;
        public string Name => name;
        public string About => about;
        public string Version => version == null ? "Not found" : Versions.Shown(version);
        public bool Known => version != null;
        public bool Have => have;
        public bool IsRelease => id == Versions.Stable;
        public bool IsCandidate => id == Versions.Candidate;
        public bool IsNightly => id == Versions.Nightly;
        public string Tag => id;

        public bool IsPicked
        {
            get => owner.Pick == id;
            set
            {
                if (value) owner.Pick = id;
            }
        }

        public void Changed() => Raise(nameof(IsPicked));
    }

    /// A channel's line; the one new builds start on while the player has not picked one says it follows the launcher
    public static string AboutOf(LauncherService service, string id, string about) =>
        service.Settings.DefaultChannel == null && service.DefaultChannel == id ? about + " It follows the launcher until you pick one in Settings." : about;

    public static readonly (string Id, string Name, string About)[] All =
    {
        (Versions.Stable, "Stable", "The tested version. Best for playing."),
        (Versions.Candidate, "Release candidate", "The next version, to try before it is released."),
        (Versions.Nightly, "Nightly", "The newest development build, with every change of the day."),
    };

    public ChannelPickerViewModel(MainViewModel main, string start)
    {
        Main = main;
        Rows = All.Select(c => new Row(this, c.Id, c.Name, AboutOf(main.Service, c.Id, c.About), main.Service.NewestOf(c.Id), Has(main.Service.NewestOf(c.Id)))).ToList();
        pick = start;
        GoCommand = new Command(Go, () => Version != null);
        CancelCommand = new Command(() => Main.Modal = null);
    }

    bool Has(string? v) => v != null && Main.Service.Registry.Builds.Any(b => b.Version == v);

    public MainViewModel Main { get; }
    public IReadOnlyList<Row> Rows { get; }
    public Command GoCommand { get; }
    public Command CancelCommand { get; }

    public string Pick
    {
        get => pick;
        set
        {
            if (!Set(ref pick, value)) return;
            foreach (var r in Rows) r.Changed();
            RaiseAll(nameof(Again), nameof(GoText));
            GoCommand.Changed();
        }
    }

    string? Version => Main.Service.NewestOf(pick);
    public bool Again => Has(Version);
    public string GoText => Version == null ? "Continue" : (Again ? "Rebuild " : "Continue with ") + Versions.Shown(Version);

    void Go()
    {
        Main.Modal = null;
        Main.Home.StartBuild(pick);
    }
}

/// Deleting a build: what goes with it (on Windows its saves), and who becomes the default
public sealed class DeleteViewModel : ViewModelBase
{
    readonly BuildItemViewModel build;

    public DeleteViewModel(MainViewModel main, BuildItemViewModel build)
    {
        Main = main;
        this.build = build;
        KeepCommand = new Command(() => Main.Modal = null);
        DeleteCommand = new Command(() => _ = DeleteAsync());
    }

    public MainViewModel Main { get; }
    public string Title => $"Delete {build.Version}?";
    bool GameFolderHere => GameFolder.IsGameFolder(build.Entry.Folder, Main.Platform);

    public string Sub => GameFolderHere
        ? "This deletes its folder" + (build.Entry.Bytes is long b ? $" and frees {Durations.Size(b)}:" : ":")
        : "This takes it off your versions. Its folder is not a Longshot game folder, so it stays on the disk:";
    public string Folder => ShownPath.Of(build.Entry.Folder);
    public string FolderFull => build.Entry.Folder;

    public bool Linux => Main.Linux;
    public bool Windows => !Main.Linux;
    public string SavesWarning => "Its saves go with it" + (build.Saves.Count > 0 ? $" ({SaveText.Long(build.Saves, DateTime.Now)})" : "") +
                                  ". To keep them, copy them to another build in Saves first.";
    BuildEntry? Next => Main.Service.Registry.Builds.FirstOrDefault(b => b.Id != build.Entry.Id);
    public bool DefaultNote => build.IsDefault && Next != null;
    public string DefaultText => Next is { } n ? $"It is your default: Play will start {(n.Version is { } v ? Versions.Shown(v) : n.Name)} instead." : "";

    public Command KeepCommand { get; }
    public Command DeleteCommand { get; }

    async Task DeleteAsync()
    {
        Main.Modal = null;
        var service = Main.Service;
        var entry = build.Entry;
        var files = GameFolderHere;
        // (a running game holds its files: on Windows the delete would stop half-way through the folder)
        if (files && Saves.Running(service.Platform).Contains(Saves.Longshot.Display))
        {
            Main.Home.Notice = $"Close Longshot first: {build.Version} is not deleted while a game runs.";
            return;
        }
        try
        {
            if (files)
            {
                // (tens of GB: off the window's thread, the folder first and the row once it is gone)
                var keep = service.KeepOf(entry);
                Main.ShowToast($"Deleting {build.Version}…");
                await Task.Run(() => BuildRegistry.DeleteFolder(entry.Folder, service.Platform, keep));
            }
            // (the default goes to a release when there is one left, as the mockup: Registry.Remove takes the first otherwise)
            var wasDefault = build.IsDefault;
            service.Remove(entry, deleteFiles: false);
            if (wasDefault && service.Registry.Builds.FirstOrDefault(b => UpdatePolicy.ChannelOf(b.Channel, b.Version) == Versions.Stable) is { } rel)
                service.SetDefault(rel);
            Main.ShowToast($"Deleted {build.Version}" + (entry.Bytes is long b && files ? $", {Durations.Size(b)} freed." : "."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Main.Home.Notice = $"{entry.Folder} could not be deleted: {e.Message}";
        }
        Main.Home.Refresh();
    }
}
