using System.Collections.ObjectModel;
using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

/// One version in the home's list
public sealed class BuildItemViewModel : ViewModelBase
{
    readonly HomeViewModel home;
    bool selected;

    /// old: built by make_game.py (OldVersions): shown as not supported, never played, built or updated here
    public BuildItemViewModel(HomeViewModel home, BuildEntry entry, bool built, string? update, IReadOnlyList<SaveSlot> saves, bool deleting = false,
                              bool old = false)
    {
        this.home = home;
        Entry = entry;
        IsBuilt = built;
        IsOld = old && built;
        Update = IsOld ? null : update;
        Saves = saves;
        IsDeleting = deleting;
        SelectCommand = new Command(() => home.Select(this));
        // (a build being deleted: nothing more starts on it, and its delete does not start twice)
        PlayCommand = new Command(() => home.Play(this), () => IsBuilt && !IsOld && !IsDeleting);
        SetDefaultCommand = new Command(() => home.SetDefault(this), () => !IsDeleting);
        StableCommand = new Command(() => home.Main.Modal = new StableLauncherViewModel(home.Main), () => IsOld && !IsDeleting);
        DeleteCommand = new Command(() => home.AskDelete(this), () => !IsDeleting);
        SavesCommand = new Command(() => home.Main.ShowSaves(), () => !IsDeleting);
        FolderCommand = new Command(() => home.Main.Host?.Open(Entry.Folder));
        UpdateToCommand = new Command(() => home.UpdateBuild(this), () => HasUpdate && !IsDeleting && home.CanStartBuild);
        WhatsNewCommand = new Command(() =>
        {
            if (Versions.PullRequestOf(Update) is { } n) home.Main.Host?.Open(PullRequests.PageOf(n.Number));
            else home.ShowNotes(this);
        }, () => HasUpdate);
        RebuildCommand = new Command(() =>
        {
            if (Entry.Version is { } v)
                home.Main.StartWizard(JobKind.Rebuild, Versions.IsPullRequest(v) ? Versions.PullRequest : Channel, v, Entry);
        }, () => IsMissing && Entry.Version != null && home.CanStartBuild);
    }

    /// Its card's state on the Versions page: Ready, Update available, Game missing, Not supported, Deleting
    public string StateText => IsDeleting ? Tr.T("Deleting…") : !IsBuilt ? Tr.T("Game missing") : IsOld ? Tr.T("Not supported")
        : HasUpdate ? Tr.T("Update available") : Tr.T("Ready");
    public bool StateReady => IsBuilt && !IsOld && !IsDeleting && !HasUpdate;
    public bool StateUpdate => IsBuilt && !IsDeleting && HasUpdate;
    /// "Update to v3.2.0" (a pull request's newer push: "Update to its newer push")
    public string UpdateToText => Update is { } u ? Versions.IsPullRequest(u) ? Tr.T("Update to its newer push") : Tr.F("Update to {0}", Versions.Shown(u)) : "";
    public bool ShowUpdateTo => HasUpdate && !IsDeleting;
    public bool ShowRebuild => IsMissing;
    public bool ShowSetDefault => !IsDefault && !IsDeleting && !IsOld;

    /// Built by Longshot's old builder (make_game.py, OldVersions): this launcher neither plays nor updates it, and says so on its
    /// card with the way back to the stable launcher (StableCommand); the player may still delete it
    public bool IsOld { get; }
    public bool ShowOld => IsOld && !IsDeleting;
    public string OldText => OldVersions.Shown;
    /// The offer to replace this launcher with the stable one (StableLauncherViewModel), which plays it
    public Command StableCommand { get; }
    public Command UpdateToCommand { get; }
    public Command WhatsNewCommand { get; }
    public Command RebuildCommand { get; }

    public void BusyChanged()
    {
        UpdateToCommand.Changed();
        RebuildCommand.Changed();
    }

    /// Its folder is being deleted (DeleteViewModel): the row says so with a spinner until it goes
    public bool IsDeleting { get; }

    public BuildEntry Entry { get; }
    public IReadOnlyList<SaveSlot> Saves { get; }
    /// The version as players see it (a nightly: "Nightly 2029 · c544828f", Versions.Shown), else the build's name
    public string Version => Entry.Version is { } v ? Versions.Shown(v) : Entry.Name;

    /// Its name without the commit, for a narrow field ("Nightly 187", "PR #1548"; a release as it is): the full name is its tooltip
    public string ShortVersion => Version.Split(" \u00b7 ")[0];
    public string Channel => UpdatePolicy.ChannelOf(Entry.Channel, Entry.Version);
    public bool IsRelease => Channel == Versions.Stable;
    public bool IsNightly => Channel == Versions.Nightly;
    /// A pull request's build (pr-&lt;number&gt;-&lt;commit&gt;): its update is a newer push of the same pull request
    public bool IsPullRequest => Channel == Versions.PullRequest;
    public bool IsDefault => Entry.Default;
    public bool IsBuilt { get; }
    public string? Update { get; }
    public bool HasUpdate => Update != null;

    /// "Built 3 Nov, 12.2 GB": what it holds of its own (Disk.Own; no saves count: every build shares them)
    public string Meta
    {
        get
        {
            var bits = new List<string> { Tr.F("Built {0}", Durations.Day(Entry.Updated.LocalDateTime, DateTime.Now)) };
            // (its pull request merged or closed since: Beanstalk lists only the open ones. The build stays until the player deletes it)
            if (Versions.PullRequestOf(Entry.Version) is { } pr && home.Main.Service.Pulls != null && home.Main.Service.PullRequestOf(pr.Number) == null)
                bits.Add(Tr.T("its pull request is closed"));
            // (what it holds of its own: the game files it shares with another build are in the footer's total, once)
            if (Disk.Own(Entry) is long b) bits.Add(Durations.Size(b));
            return string.Join(Tr.T(", "), bits);
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

    /// Its folder is listed but its game is not there (deleted by hand, or moved): built again before it plays
    public bool IsMissing => !IsBuilt && !IsDeleting;
    /// "Game missing" on the row, in the warning's colour: the hero's button builds it again
    public string MissingText => Tr.T("Game missing");

    public bool ShowActions => selected;
    public bool ShowOtherActions => selected && !IsDefault;
    public bool ShowDefaultNote => selected && IsDefault;
    public bool ShowRing => !IsDefault;
    public bool ShowMeta => !IsDeleting;
    /// "Update available" on the row: the hero's Update and What's new act on it once it is picked
    public bool ShowUpdateBadge => HasUpdate && !IsDeleting;
    public string DeletingText => Tr.F("Deleting {0}…", Version);
    public bool IsFirst { get; set; }
    public Avalonia.Thickness Separator => IsFirst ? default : new Avalonia.Thickness(0, 1, 0, 0);

    public void RaiseMeta() => Raise(nameof(Meta));

    public Command SelectCommand { get; }
    public Command PlayCommand { get; }
    public Command SetDefaultCommand { get; }
    public Command DeleteCommand { get; }
    public Command SavesCommand { get; }
    public Command FolderCommand { get; }
}

/// A save of Home's list: its slot, place, when and hearts; Chosen: Play starts on it
public sealed record HomeSaveRow(string Title, string Place, string When, string? Hearts, bool Chosen, Command PickCommand, string? Detail = null)
{
    public bool HasHearts => Hearts != null;
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

/// One entry of Home's Saves picker: a slot, or New game (Slot null) when the profile has none, with what it says under its name and its hearts
public sealed class SaveStartChoice(string? slot, string title, string detail, string? hearts)
{
    public string? Slot => slot;
    public string Title => title;
    public string Detail => detail;
    public string? Hearts => hearts;
    public bool HasHearts => hearts != null;
}

/// The last rows of a Home picker that are not a choice but a way to the page that manages them: Manage versions…, New
/// version…, Manage saves…
public sealed class ManageChoice(string text, Action go, string icon = "list")
{
    public string Text => text;
    public string Icon => icon;
    public void Go() => go();
}

/// One track in the hero's Track dropdown: stable, nightly or a pull request, by its name only (what it holds is the Version
/// field's). They are made once and kept: a dropdown given new items drops its selection after the pick was shown again
public sealed class TrackChoice(string id, string name) : ViewModelBase
{
    public string Id => id;
    public string Name => Tr.T(name);
    /// In the closed dropdown, so it reads as the track and not as a version: "Stable track"
    public string Shown => Tr.F("{0} track", Name);
}

/// One version in the hero's Version dropdown (VersionPick.Of): a check when it is installed on the track, and whether it is the
/// default or the track's newest. Kept while its track is picked, so the version picked stays the dropdown's selection
public sealed class VersionChoice(string tag) : ViewModelBase
{
    bool installed, hasUpdate;
    string note = "";

    string? label;

    public string Tag => tag;
    /// "Nightly 429 · 6112edb6"; a pull request's "#1558 · its title" once Beanstalk named it
    public string Shown => label ?? Versions.Shown(tag);
    public string? Label
    {
        get => label;
        set
        {
            if (Set(ref label, value)) Raise(nameof(Shown));
        }
    }
    public bool Installed
    {
        get => installed;
        set => Set(ref installed, value);
    }
    public string Note
    {
        get => note;
        set
        {
            Set(ref note, value);
        }
    }
    /// Its build has a newer version on its channel: "Update available" in place of the note
    public bool HasUpdate
    {
        get => hasUpdate;
        set => Set(ref hasUpdate, value);
    }

    public void Show(TrackVersion version, bool isDefault, bool hasUpdate)
    {
        Installed = version.Installed;
        Note = isDefault ? Tr.T("default") : version.Newest ? Tr.T("newest") : "";
        HasUpdate = hasUpdate;
    }
}

/// Home: Build when there is no build; else on the left the Track and Version dropdowns and the button for the version picked
/// (Play, Update to, Go back to or Build: VersionPick), and every version on the right
public sealed class HomeViewModel : ViewModelBase
{
    string? notice;
    string status = "";
    bool checkAgain, asked;
    /// The last check: some channel had a version (the server answered), or the check itself failed (its words)
    bool listed;
    string? checkProblem;
    readonly HashSet<string> deleting = new();

    public HomeViewModel(MainViewModel main)
    {
        Main = main;
        // (the update check and the launcher's update are BusyCommands: off while they run, with what they are doing on screen)
        CheckCommand = new BusyCommand(CheckAsync);
        UpdateLauncherCommand = new BusyCommand(UpdateLauncherAsync, () => HasLauncherUpdate,
                                                e => Notice = Tr.F("The launcher could not update itself: {0}", e.Message));
        CheckCommand.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BusyCommand.IsBusy)) BusyChanged(); };
        UpdateLauncherCommand.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BusyCommand.IsBusy)) BusyChanged(); };
        BuildCommand = new Command(() => StartBuild(Service.DefaultChannel), () => NewestDefault != null && CanStartBuild);
        ChannelCommand = new Command(() => Main.Modal = new ChannelPickerViewModel(Main, Service.DefaultChannel), () => CanStartBuild);
        DismissProblemCommand = new Command(() => Notice = null);
        CopyNoticeReportCommand = main.CopyReportCommand(() => Notice ?? "");
        CopyCheckReportCommand = main.CopyReportCommand(() => CheckFailed ? Tr.Sentences(CheckFailedText, CheckWhy) : Status);
        PlayDefaultCommand = new Command(() => { if (Default is { } d) Play(d); }, () => Default is { IsBuilt: true, IsDeleting: false });
        GoCommand = new Command(Go, CanGo);
        HeroPlayCommand = new Command(PlayHero, CanPlayHero);
        HeroNotesCommand = new Command(() =>
        {
            if (HeroItem is not { Update: { } u } h) return;
            if (Versions.PullRequestOf(u) is { } n) Main.Host?.Open(PullRequests.PageOf(n.Number));
            else ShowNotes(h);
        }, () => HeroHasUpdate);
        // (the Versions page's Add version: a channel's version, or under Advanced main or a pull request)
        AddVersionCommand = new Command(() => Main.Modal = new AddVersionViewModel(Main), () => CanStartBuild);
        OldOnlyAddCommand = new Command(() => Main.Modal = new AddVersionViewModel(Main), () => CanStartBuild);
        OldOnlyStableCommand = new Command(() => Main.Modal = new StableLauncherViewModel(Main));
        OldOnlyNotNowCommand = new Command(() =>
        {
            oldOnlyDismissed = true;
            Raise(nameof(ShowOldOnly));
        });
        RemoveCommand = new Command(() => { if (PickItem is { } h) AskDelete(h); }, () => PickItem is { IsDeleting: false });
        // (the hero's build: the version picked when it is installed)
        UpdateDefaultCommand = new Command(() => { if (HeroItem is { } d) UpdateBuild(d); }, () => CanStartBuild && HeroItem is { IsDeleting: false });
        CancelLauncherUpdateCommand = new Command(() => UpdateLauncherCommand.Cancel());
        NotesDefaultCommand = new Command(() => { if (TrackUpdateItem is { } d) ShowNotes(d); });
        ReadPullRequestCommand = new Command(() =>
        {
            if (Versions.PullRequestOf(TrackUpdateItem?.Update ?? pickTag) is { } n) Main.Host?.Open(PullRequests.PageOf(n.Number));
        }, () => HasPickedPullRequest || TrackUpdateIsPullRequest);
        FindPullRequestCommand = new Command(() => Main.Modal = new PullRequestFindViewModel(Main), () => CanStartBuild);
        LauncherNotesCommand = new Command(ShowLauncherNotes, () => HasLauncherUpdate);
        // (not while the launcher updates itself: a check forgets the update it is putting in place)
        CheckForUpdatesCommand = new Command(() =>
        {
            asked = true;
            _ = CheckReleasesAsync();
        }, () => CanStartBuild);
        ImportHookshotCommand = new Command(() => Main.Modal = new HookshotImportViewModel(Main));
        NotNowHookshotCommand = new Command(() =>
        {
            Service.Settings.HookshotSavesAsked = true;
            try { Service.SaveSettings(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            HookshotOffer = null;
        });
        ImportNx2naviCommand = new Command(() =>
        {
            var page = new SavesViewModel(Main);
            Main.Navigate(page);
            Main.Modal = new SavePickViewModel(page, SaveMode.Import) { Pick = "nx2navi" };
        });
        NotNowNx2naviCommand = new Command(() =>
        {
            Service.Settings.Nx2naviSavesAsked = true;
            try { Service.SaveSettings(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Nx2naviOffer = null;
        });
        UseInstalledCommand = new Command(UseInstalled, () => !UpdateLauncherCommand.IsBusy && !deletingLeftovers && !renaming && deleting.Count == 0);
        NotNowInstalledCommand = new Command(() =>
        {
            Service.Settings.InstalledLauncherAsked = true;
            try { Service.SaveSettings(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            InstalledOffer = null;
        });
        // (not while a version is deleted: its gather and this copy share the save folder; the update check is no reason)
        AddFolderCommand = new Command(() => _ = AddFolderAsync(), () => !UpdateLauncherCommand.IsBusy && !deletingLeftovers && !renaming && deleting.Count == 0);
        KeepLeftoversCommand = new Command(KeepLeftovers, () => !deletingLeftovers);
        DeleteLeftoversCommand = new Command(() => _ = DeleteLeftoversAsync(), () => !deletingLeftovers);
        KeepNamesCommand = new Command(KeepNames, () => !renaming);
        RenameFoldersCommand = new Command(() => _ = RenameFoldersAsync(), () => !renaming);
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
    public bool DefaultIsNightly => DefaultChannel == Versions.Nightly;
    public bool KnowsNewest => NewestDefault != null;
    public bool CheckFailed => NewestDefault == null && Service.Checked;
    public bool Checking => NewestDefault == null && !Service.Checked;
    /// Why the check failed, under its line: no answer (Releases.Unreachable: no address, no connection, a timeout, an HTTP error),
    /// a release refused (Releases.Problem) or the check's own failure; "" when none says
    public string CheckWhy => Service.Releases.Unreachable ?? Service.Releases.Problem ?? checkProblem ?? "";
    public bool HasCheckWhy => CheckFailed && CheckWhy.Length > 0;
    /// Copy for a bug report beside the check's failure, not beside a channel with no version yet (nothing failed)
    public bool ShowCheckReport => CheckFailed && !ChannelEmpty;
    /// The server answered with versions, none on the default channel (no stable is out yet): that, not "did not answer"
    public bool ChannelEmpty => CheckFailed && listed && CheckWhy.Length == 0;
    public string CheckFailedText => ChannelEmpty
        ? Tr.F("No version is out on the {0} channel yet.", ChannelPickerViewModel.NameOf(DefaultChannel))
        : Tr.T("git.magicbean.dev did not answer, so the version to build is not known.");

    // ---- on the left: the Track and Version dropdowns, and the button for the version picked (VersionPick) ----

    /// The pick: a track and one of its versions (the version's tag, null when the track has none known). The source of truth:
    /// the dropdowns' items are made again on every Refresh, and found again by these
    string? pickTrack, pickTag;

    public IReadOnlyList<TrackChoice> Tracks { get; } = ChannelPickerViewModel.All.Select(c => new TrackChoice(c.Id, c.Name))
        .Append(new TrackChoice(Versions.PullRequest, Tr.N("Pull request"))).ToList();
    public ObservableCollection<VersionChoice> TrackVersions { get; } = new();
    public bool HasTrackVersions => TrackVersions.Count > 0;

    public TrackChoice? PickTrack
    {
        get => Tracks.FirstOrDefault(t => t.Id == pickTrack);
        set
        {
            // (null: the dropdown's items were made again by Refresh, which keeps the pick)
            if (value == null || value.Id == pickTrack) return;
            pickTrack = value.Id;
            pickTag = null;
            ShowPick();
            // (the pull requests: asked of Beanstalk when the track is picked, not at every start)
            if (pickTrack == Versions.PullRequest) _ = LoadPullRequestsAsync();
        }
    }

    public VersionChoice? PickVersion
    {
        get => TrackVersions.FirstOrDefault(v => v.Tag == pickTag);
        set
        {
            if (value == null || value.Tag == pickTag) return;
            pickTag = value.Tag;
            // (an installed version picked: its row is the one selected in Your versions)
            if (Plan is { Action: PickAction.Play, Build: { } b })
                foreach (var x in Builds) x.IsSelected = x.Entry.Id == b.Id;
            RaisePick();
        }
    }

    /// What the button does for the pick; null when the track has no version known (no build, and no answer from the server)
    public PickPlan? Plan => pickTrack != null && pickTag != null
        ? VersionPick.Plan(pickTrack, pickTag, Service.Registry.Builds, Builds.FirstOrDefault(b => b.IsSelected)?.Entry)
        : null;

    /// The Versions page's pick when it is installed already (its card is in the list): nothing to build
    public BuildItemViewModel? PickItem => Plan is { Action: PickAction.Play, Build: { } b } ? Builds.FirstOrDefault(x => x.Entry.Id == b.Id) : null;

    /// The pick is on this PC already: the Versions page says so in place of a button
    public bool GoIsPlay => Plan?.Action == PickAction.Play;
    /// Build, Update to, Go back to, Switch to: the Versions page's button for a version not on this PC
    public bool GoIsBuild => Plan is { Action: not PickAction.Play };
    public bool ShowRemove => PickItem != null;

    /// Play; Update to vX or Go back to vX, built in the track's build's place; Build vX, a new build of the track
    public string GoText => Plan switch
    {
        { Action: PickAction.Play } => Tr.T("On this PC"),
        { Action: PickAction.Replace } p when IsPullRequestTrack => Tr.F("Switch to #{0}", Versions.PullRequestOf(p.Version)!.Value.Number),
        { } p when IsPullRequestTrack => Tr.F("Build #{0}", Versions.PullRequestOf(p.Version)!.Value.Number),
        { Action: PickAction.Replace } p => Tr.F(p.Older ? "Go back to {0}" : "Update to {0}", Versions.Shown(p.Version)),
        { } p => Tr.F("Build {0}", Versions.Shown(p.Version)),
        _ => Tr.T("Play"),
    };

    /// The Versions page's button: Update to / Go back to / Build vX / Switch to #N
    public Command GoCommand { get; }
    /// Remove, beside it when the version picked is installed: the row's Delete (DeleteViewModel)
    public Command RemoveCommand { get; }

    bool CanGo() => Plan switch
    {
        { Action: PickAction.Play } => false,
        { Action: PickAction.Replace, Build: { } b } => CanStartBuild && !deleting.Contains(b.Id),
        { Action: PickAction.Build } => CanStartBuild,
        _ => false,
    };

    void Go()
    {
        switch (Plan)
        {
            case { Action: PickAction.Replace, Build: { } b } p when IsPullRequestTrack:
                // (another pull request in the place of the one installed: switching, in its folder, after the warnings)
                AskPullRequest(p.Version, JobKind.Update, b);
                break;
            case { Action: PickAction.Build } p when IsPullRequestTrack:
                AskPullRequest(p.Version, JobKind.Build, null);
                break;
            case { Action: PickAction.Replace, Build: { } b } p:
                // (in its folder, as Update does: the row then that version; an older one goes back, BuildJob.Older)
                Main.StartWizard(JobKind.Update, pickTrack!, p.Version, b);
                break;
            case { Action: PickAction.Build } p:
                // (a version another track's build has: built again there, as Build another version does)
                var same = Service.Registry.Builds.FirstOrDefault(x => x.Version == p.Version);
                Main.StartWizard(same != null ? JobKind.Rebuild : JobKind.Build, pickTrack!, p.Version, same);
                break;
        }
    }

    /// A channel's version built, as the Versions page's Add version asks: in the track's build's place (Update to, Go back to),
    /// built again where another track's build has it, else a new build
    public void BuildVersion(string track, string tag)
    {
        switch (VersionPick.Plan(track, tag, Service.Registry.Builds))
        {
            case { Action: PickAction.Replace, Build: { } b } p:
                Main.StartWizard(JobKind.Update, track, p.Version, b);
                break;
            case { Action: PickAction.Build } p:
                var same = Service.Registry.Builds.FirstOrDefault(x => x.Version == p.Version);
                Main.StartWizard(same != null ? JobKind.Rebuild : JobKind.Build, track, p.Version, same);
                break;
        }
    }

    /// The dropdowns made again from the builds and the update check's list, the pick kept while it is still there. A pick with
    /// no track yet (the home opening) is the default build's track and version
    void ShowPick()
    {
        var builds = Service.Registry.Builds;
        if (pickTrack == null)
        {
            // (the version Play started last, while it is listed; else the default)
            var last = Builds.FirstOrDefault(b => b.Entry.Id == Service.Profile.LastPlayed && b.Entry.Version != null)?.Entry ?? Default?.Entry;
            pickTrack = last is { } d ? VersionPick.TrackOf(d) : Service.DefaultChannel;
            pickTag = last?.Version;
        }
        var versions = Service.VersionsOn(pickTrack);
        // (a pick no longer there, or a track just picked: its build's version, the default's first, else its newest)
        if (pickTag == null || versions.All(v => v.Tag != pickTag))
            pickTag = (versions.FirstOrDefault(v => v.Build?.Default == true) ?? versions.FirstOrDefault(v => v.Installed) ?? versions.FirstOrDefault())?.Tag;
        // (in place: the versions still there keep their item, so the one picked stays the dropdown's selection)
        var defaultId = Default?.Entry.Id;
        for (var i = TrackVersions.Count - 1; i >= 0; i--)
            if (versions.All(v => v.Tag != TrackVersions[i].Tag)) TrackVersions.RemoveAt(i);
        for (var i = 0; i < versions.Count; i++)
        {
            var at = TrackVersions.Select((c, n) => (c, n)).FirstOrDefault(x => x.c.Tag == versions[i].Tag);
            var item = at.c ?? new VersionChoice(versions[i].Tag);
            if (at.c == null) TrackVersions.Insert(i, item);
            else if (at.n != i) TrackVersions.Move(at.n, i);
            var build = versions[i].Build;
            item.Show(versions[i], defaultId != null && build?.Id == defaultId,
                      build != null && Builds.Any(x => x.Entry.Id == build.Id && x.HasUpdate));
            if (Versions.PullRequestOf(versions[i].Tag) is { } number && Service.PullRequestOf(number.Number) is { } pr)
            {
                item.Label = pr.Title.Length > 0 ? $"#{pr.Number} \u00b7 {pr.Title}" : null;
                if (item.Note.Length == 0) item.Note = pr.Approved ? Tr.T("approved") : pr.AuthorBigBean ? Tr.T("BigBean") : Tr.T("not approved");
            }
        }
        Raise(nameof(HasTrackVersions));
        RaisePick();
    }

    void RaisePick()
    {
        RaiseAll(nameof(PickTrack), nameof(PickVersion), nameof(GoText), nameof(GoIsPlay), nameof(GoIsBuild), nameof(ShowRemove),
                 nameof(PickNote), nameof(PickHasUpdate), nameof(HasTrackUpdate), nameof(TrackUpdate),
                 nameof(IsPullRequestTrack), nameof(PickedPullRequest), nameof(HasPickedPullRequest), nameof(PullRequestTrust),
                 nameof(PullRequestTrusted), nameof(PullRequestUntrusted), nameof(TrackUpdateIsPullRequest));
        ReadPullRequestCommand.Changed();
        GoCommand.Changed();
        RemoveCommand.Changed();
        UpdateDefaultCommand.Changed();
    }

    /// Under the button, what it does: the installed version's line ("Your default version. Built 3 Nov, English (US), NVIDIA
    /// DLSS."), the build a version replaces, or a new build's folder
    public string PickNote => Plan switch
    {
        null when IsPullRequestTrack => loadingPulls ? Tr.T("Asking Beanstalk for the pull requests…")
            : Service.Pulls == null ? Tr.T("Beanstalk did not answer, so the pull requests are not known.")
            : Tr.T("No pull request a BigBean wrote or approved is open. Find one by its number."),
        { Action: PickAction.Replace, Build: { } b } p when IsPullRequestTrack => Tr.F("{0} is built from its source on this PC, in place of {1}, in its folder.",
            Versions.Shown(p.Version), b.Version is { } v ? Versions.Shown(v) : b.Name),
        { Action: PickAction.Build } when IsPullRequestTrack =>
            Tr.T("Built from its source on this PC, in about 15 to 30 minutes, with the newest nightly's tools."),
        { Action: PickAction.Play } when PickItem is { } d => Tr.F("{0} is on this PC: play it from Home.", d.Version),
        { Action: PickAction.Replace, Build: { } b } p => Tr.F(p.Older
            ? "{0} is built in place of {1}, in its folder. Update brings the newest back whenever you want it."
            : "{0} is built in place of {1}, in its folder.", Versions.Shown(p.Version), b.Version is { } v ? Versions.Shown(v) : b.Name),
        { Action: PickAction.Build } => Tr.T("Nothing on this track yet: it is built into a folder of its own, in about 15 to 30 minutes."),
        _ => Tr.T("Its versions show once git.magicbean.dev answers."),
    };

    /// "Built 3 Nov, English (US), NVIDIA DLSS"
    static string BuildLine(BuildItemViewModel d)
    {
        var e = d.Entry;
        var bits = new List<string> { Tr.F("Built {0}", Durations.Day(e.Updated.LocalDateTime, DateTime.Now)) };
        if (GameFolder.Language(e.Folder) is { } lang) bits.Add(Tr.T(Languages.NameOf(lang)));
        if (e.Dlss ?? GameFolder.HasDlss(e.Folder)) bits.Add("NVIDIA DLSS");
        if (e.Fsr == true) bits.Add("AMD FSR 4");
        if (e.Xess == true) bits.Add("Intel XeSS");
        return string.Join(Tr.T(", "), bits);
    }

    /// Update available beside the Version field: the version picked is installed and its build has a newer version
    public bool PickHasUpdate => PickVersion?.HasUpdate == true;

    // ---- Home: what Play plays: a version on this PC, and the profile's save it starts on. Picking, building and deleting versions is the Versions page's (VersionsViewModel, on this model)

    string? heroId;

    /// The version Home plays: the one picked there, else the one Play started last, else the default, else the first
    public BuildItemViewModel? HeroItem => Builds.FirstOrDefault(b => b.Entry.Id == heroId)
                                           ?? Builds.FirstOrDefault(b => b.Entry.Id == Service.Profile.LastPlayed)
                                           ?? Default ?? Builds.FirstOrDefault();

    /// Home's version picker: the versions on this PC (built, or their game missing), then Manage versions…
    public IReadOnlyList<object> HeroChoices { get; private set; } = Array.Empty<object>();

    public object? HeroChoice
    {
        get => HeroItem;
        set
        {
            if (value is ManageChoice manage)
            {
                Ui.Post(() => Raise(nameof(HeroChoice)));   // (the picker stays on the version)
                manage.Go();
                return;
            }
            if (value is not BuildItemViewModel b || b.Entry.Id == HeroItem?.Entry.Id) return;
            heroId = b.Entry.Id;
            RaiseHero();
        }
    }

    // ---- Home's one button: Build with no version, Build it again when its game is gone, Update when there is one (Play beside
    // it, "Play v3.1.0"), else Play; one status line under it only when it must be said

    /// An update for the version shown, which it can take now: the button is Update, and Play is the link beside it
    public bool HeroIsUpdate => HeroItem is { IsBuilt: true, HasUpdate: true, IsDeleting: false };
    /// An old version shown (OldVersions): no Play, the stable launcher offered in its place
    public bool HeroIsOld => HasBuilds && HeroItem is { IsOld: true, IsDeleting: false };
    public bool HeroIsPlayOnly => HasBuilds && HeroIsPlay;
    public bool HeroIsPlayAndUpdate => HasBuilds && HeroIsUpdate;

    /// Why the last Play did not get to the game (LauncherService.PlayFailed), until the next Play
    string? playError;
    public string? PlayError
    {
        get => playError;
        set { if (Set(ref playError, value)) RaiseAll(nameof(StatusLine), nameof(HasStatusLine), nameof(StatusIsWarning)); }
    }

    /// The one line under the button: why the last Play failed, a version this launcher doesn't play (OldVersions), or with no
    /// version why there is nothing to build yet (asking, or the check failed)
    public string? StatusLine => PlayError
        ?? (HeroIsOld ? OldVersions.Shown : null)
        ?? (NoBuilds && CheckFailed ? CheckFailedText : null)
        ?? (NoBuilds && Checking ? Tr.T("Asking git.magicbean.dev for the newest version…") : null);
    public bool HasStatusLine => StatusLine != null;
    public bool StatusIsWarning => PlayError != null || HeroIsOld;
    public bool StatusCanRetry => PlayError == null && NoBuilds && CheckFailed;

    /// With no version yet, the Version picker shows the one a build makes, then Manage versions… and New version… (pick another
    /// to build: the channel picker)
    public IReadOnlyList<object> BuildChoices { get; private set; } = Array.Empty<object>();
    public object? BuildChoice
    {
        get => BuildChoices.OfType<BuildTargetChoice>().FirstOrDefault();
        set
        {
            if (value is not ManageChoice manage) return;
            Ui.Post(() => Raise(nameof(BuildChoice)));
            manage.Go();
        }
    }

    void MakeBuildChoices() => BuildChoices = new object[]
    {
        new BuildTargetChoice(KnowsNewest ? NewestDefaultText : Tr.T("Newest version"), DefaultChannel, DefaultIsNightly),
        new ManageChoice(Tr.T("Manage versions…"), () => Main.ShowVersions()),
        new ManageChoice(Tr.T("New version…"), () => ChannelCommand.Execute(null), "plus"),
    };

    /// Play when its game is there (and this launcher plays it: not an old version); Build it again when its folder lost it
    public bool HeroIsPlay => HeroItem is not ({ IsBuilt: false } or { IsOld: true });
    public bool HeroIsRebuild => HeroItem is { IsBuilt: false };
    public string HeroGoText => HeroItem is { IsBuilt: false } h ? Tr.F("Build {0} again", h.Version) : Tr.T("Play");
    public Command HeroPlayCommand { get; }

    bool CanPlayHero() => HeroItem switch
    {
        { IsDeleting: true } or { IsOld: true } or null => false,
        { IsBuilt: false } h => CanStartBuild && h.Entry.Version != null,
        _ => true,
    };

    void PlayHero()
    {
        PlayError = null;
        switch (HeroItem)
        {
            case { IsBuilt: false, Entry.Version: { } again } gone:
                // (its game is gone from its folder: built again there, as a rebuild, with its settings)
                Main.StartWizard(JobKind.Rebuild, Versions.IsPullRequest(again) ? Versions.PullRequest : gone.Channel, again, gone.Entry);
                break;
            case { } h:
                // (the profile remembers what it played, so Home opens on it again)
                Play(h);
                break;
        }
    }

    /// Under the version: what it is ("Your default version. Built 3 Nov, English (US), NVIDIA DLSS."), or why it cannot play
    public string HeroNote => HeroItem switch
    {
        { IsDeleting: true } h => h.DeletingText,
        { IsBuilt: false } => Tr.T("Its folder is there, but its game is not: it was deleted or moved. Build it again to play; your saves are not in it."),
        { IsOld: true } => OldVersions.Shown + ".",
        { } d => Tr.F(d.IsDefault ? "Your default version. {0}." : "{0}.", BuildLine(d)),
        null => "",
    };

    /// An update for Home's version, in one line: "v3.2.0 is out", What's new and Update; nothing when there is none
    public bool HeroHasUpdate => HeroItem is { HasUpdate: true, IsDeleting: false };
    public string HeroUpdate => HeroItem is { Update: { } u } ? Versions.PullRequestOf(u) is { } pr
            ? Tr.F("#{0} has a newer push: {1}", pr.Number, pr.Commit)
            : Tr.F("{0} is out", Versions.Shown(u)) : "";
    public bool HeroUpdateIsPullRequest => HeroItem?.IsPullRequest == true;
    public Command HeroNotesCommand { get; }

    /// The version's state, beside it: Ready, Game missing, Not supported, Deleting
    public string HeroState => HeroItem switch
    {
        { IsDeleting: true } => Tr.T("Deleting…"),
        { IsBuilt: false } => Tr.T("Game missing"),
        { IsOld: true } => Tr.T("Not supported"),
        { } => Tr.T("Ready"),
        null => "",
    };
    public bool HeroReady => HeroItem is { IsBuilt: true, IsOld: false, IsDeleting: false };
    public bool HeroMissing => HeroItem is { IsBuilt: false, IsDeleting: false } or { IsOld: true, IsDeleting: false };
    public bool HasHeroState => HeroItem != null;
    /// The brand's kicker over the game's name, in capitals: Ready to play, Update available, Game missing, Not supported, Deleting
    public string HeroKicker => (HeroItem switch
    {
        { IsDeleting: true } => Tr.T("Deleting…"),
        { IsBuilt: false } => Tr.T("Game missing"),
        { IsOld: true } => Tr.T("Not supported"),
        { HasUpdate: true } => Tr.T("Update available"),
        { } => Tr.T("Ready to play"),
        null => "",
    }).ToUpper(System.Globalization.CultureInfo.CurrentCulture);
    /// The band's kicker and line in both states: the version's (HeroKicker, HeroNote), or with no version a welcome
    public string BandKicker => HasBuilds ? HeroKicker : Caps(Tr.T("Welcome"));
    public string BandNote => HasBuilds ? HeroNote : Tr.T("A PC version of your own copy of the game, built on this PC. Build it once, then play it here.");
    public bool HeroKickerGo => HeroItem is { IsBuilt: true, IsOld: false, IsDeleting: false, HasUpdate: false };
    /// The old version's way back: the offer of the stable launcher (StableLauncherViewModel)
    public Command HeroStableCommand => HeroItem?.StableCommand ?? NoCommand;
    static readonly Command NoCommand = new(() => { }, () => false);

    /// The Versions page's Add version… (AddVersionViewModel)
    public Command AddVersionCommand { get; }

    /// Home's pickers made again for the version shown, and everything said of it
    void RaiseHero()
    {
        var manageVersions = new ManageChoice(Tr.T("Manage versions…"), () => Main.ShowVersions());
        // (every picker ends with Manage… and New…: here the Versions page, and building another version there)
        HeroChoices = Builds.Cast<object>().Append(manageVersions)
            .Append(new ManageChoice(Tr.T("New version…"), () => Main.Modal = new AddVersionViewModel(Main), "plus")).ToList();
        MakeSaveChoices();
        MakeSaveRows();
        MakeBuildChoices();
        ReadSlots();
        RaiseAll(nameof(HeroItem), nameof(HeroChoices), nameof(HeroChoice), nameof(HeroIsPlay), nameof(HeroIsRebuild), nameof(HeroGoText),
                 nameof(HeroIsOld), nameof(HeroStableCommand),
                 nameof(HeroNote), nameof(HeroHasUpdate), nameof(HeroUpdate), nameof(HeroUpdateIsPullRequest), nameof(HeroState),
                 nameof(HeroReady), nameof(HeroMissing), nameof(HasHeroState),
                 nameof(SavesLine), nameof(SavesNote), nameof(HasNewestSave), nameof(HeroStartsOnSlot), nameof(SaveChoices),
                 nameof(SaveChoice), nameof(SavesPickNote), nameof(NewestSlot), nameof(NewestWhen), nameof(NewestPlace),
                 nameof(HasNewestPlace), nameof(NewestHearts), nameof(HasNewestHearts),
                 nameof(LastPlayedTitle), nameof(LastPlayedWhen), nameof(HeroKicker), nameof(HeroKickerGo),
                 nameof(BandKicker), nameof(BandNote),                 nameof(HeroIsUpdate), nameof(HeroIsPlayOnly), nameof(ShowSavePicker), nameof(ShowSaveTitleNote), nameof(HeroIsPlayAndUpdate), nameof(StatusLine),
                 nameof(HasStatusLine), nameof(StatusIsWarning), nameof(StatusCanRetry), nameof(BuildChoices), nameof(BuildChoice),
                 nameof(NewestLine));
        HeroPlayCommand.Changed();
        HeroNotesCommand.Changed();
        UpdateDefaultCommand.Changed();
    }

    IReadOnlyList<SaveSlot> saves = Array.Empty<SaveSlot>();
    /// What the save editor read of each slot (its place, age and hearts), by the slot's folder name; empty until it answered,
    /// or when this PC's versions have no editor. Read once for the saves as they are (savesRead: the newest save's time)
    IReadOnlyDictionary<string, SaveEditorSlot> slotsRead = new Dictionary<string, SaveEditorSlot>();
    DateTime? savesRead;

    /// The saves, which every version and profile shares: "3 save slots"
    public string SavesLine => saves.Count == 0 ? Tr.T("No save yet") : Tr.P(saves.Count, "1 save slot", "{0} save slots");
    public string SavesNote => Newest is { } s
        ? Tr.F("Last saved: {0}, {1}.", Tr.F("Slot {0}", SaveText.Number(s.Name)), Durations.When(s.Saved!.Value, DateTime.Now))
        : Tr.T("No save yet: the game starts a new one.");
    public bool HasNewestSave => Newest != null;
    SaveSlot? Newest => saves.Where(s => s.Saved != null).OrderByDescending(s => s.Saved).FirstOrDefault();

    // ---- the Saves picker: each of the profile's slots, then Manage saves… (the Saves page); New game alone with none. A slot
    // picked is the profile's StartSlot (its profile.json's start_slot), which the version shown's game reads itself when it plays
    // a profile; other versions offer no picker, and a quiet line saying the title screen asks

    /// The version shown starts on a slot when one is picked: a builder's version whose game plays a profile (GameFeatures'
    /// "profile"), which reads the profile.json's start_slot
    public bool HeroStartsOnSlot => HeroItem is { IsBuilt: true, IsOld: false } h
        && BuilderVersions.VersionOfGame(h.Entry.Folder, Platforms.Current) != null && GameFeatures.Has(h.Entry.Folder, "profile");
    public IReadOnlyList<object> SaveChoices { get; private set; } = Array.Empty<object>();

    public object? SaveChoice
    {
        // (the slot picked, else the newest: the one played last)
        get => SaveChoices.OfType<SaveStartChoice>().FirstOrDefault(c => c.Slot != null && c.Slot == Service.Profile.StartSlot)
               ?? SaveChoices.OfType<SaveStartChoice>().FirstOrDefault(c => c.Slot != null && c.Slot == Newest?.Name)
               ?? SaveChoices.OfType<SaveStartChoice>().FirstOrDefault();
        set
        {
            if (value is ManageChoice manage)
            {
                Ui.Post(() => Raise(nameof(SaveChoice)));
                manage.Go();
                return;
            }
            if (value is not SaveStartChoice c || c.Slot == Service.Profile.StartSlot) return;
            try { Service.SetStartSlot(c.Slot); } catch (Exception e) when (e is ProfileFailure or IOException or UnauthorizedAccessException) { }
            RaiseAll(nameof(SaveChoice), nameof(SavesPickNote));
            MakeSaveRows();
        }
    }

    /// Under the picker's caption, its tooltip: what Play does with the save
    public string SavesPickNote => SaveChoice is SaveStartChoice { Slot: not null } c ? Tr.F("Play starts on {0}.", c.Title)
        : Tr.T("No save yet: Play starts a new game.");

    /// The Save picker is shown for a version that starts on a slot (and with no version yet, off); another version asks for the
    /// save on its title screen, which the quiet line in its place says
    public bool ShowSavePicker => NoBuilds || HeroStartsOnSlot;
    public bool ShowSaveTitleNote => HasBuilds && !HeroStartsOnSlot && !HeroIsOld;

    /// The profile's saves as Home's list: each slot, its place, when and hearts; the one Play starts on ticked. A row picks its
    /// slot to start on (a version that takes one), else opens the Saves page
    public IReadOnlyList<HomeSaveRow> SaveRows { get; private set; } = Array.Empty<HomeSaveRow>();
    public bool HasSaveRows => SaveRows.Count > 0;
    public bool NoSaveRows => SaveRows.Count == 0;
    public string SavesCaption => Caps(Tr.F("{0}'s saves", LauncherProfilesText.Name(Service.Profile)));

    void MakeSaveRows()
    {
        SaveRows = saves.Where(s => s.Saved != null).OrderByDescending(s => s.Saved).Select(slot =>
        {
            slotsRead.TryGetValue(slot.Name, out var read);
            var when = Durations.When(slot.Saved!.Value, DateTime.Now);
            var place = read is { Unreadable: null, Place: { } p } ? p : null;
            // (what the editor read of it: Link's age, rupees and the game's clock)
            var detail = read is { Unreadable: null } d
                ? string.Join(" · ", new[] { d.Age == null ? null : d.Age == "adult" ? Tr.T("Adult Link") : Tr.T("Child Link"),
                                              d.Rupees > 0 ? Tr.P(d.Rupees, "1 rupee", "{0} rupees") : null, d.Clock }.OfType<string>())
                : null;
            return new HomeSaveRow(Tr.F("Slot {0}", SaveText.Number(slot.Name)), place ?? Tr.T("Place not read"), when,
                read is { Unreadable: null } r && r.Hearts > 0 ? Tr.F("{0} ♥", SaveEditorText.Hearts(r.Hearts)) : null,
                HeroStartsOnSlot && Service.Profile.StartSlot == slot.Name,
                new Command(() =>
                {
                    if (!HeroStartsOnSlot) { Main.ShowSaves(); return; }
                    SaveChoice = SaveChoices.OfType<SaveStartChoice>().FirstOrDefault(c => c.Slot == slot.Name);
                    MakeSaveRows();
                }), detail);
        }).ToList();
        RaiseAll(nameof(SaveRows), nameof(HasSaveRows), nameof(NoSaveRows), nameof(SavesCaption));
    }

    void MakeSaveChoices()
    {
        // (the profile's slots, the newest picked unless the player picked one; none yet: New game, alone and inert)
        var choices = new List<object>();
        foreach (var slot in saves.Where(s => s.Saved != null).OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            slotsRead.TryGetValue(slot.Name, out var read);
            var when = Durations.When(slot.Saved!.Value, DateTime.Now);
            var detail = read is { Unreadable: null, Place: { } place } ? Tr.F("{0} · {1}", place, when) : when;
            choices.Add(new SaveStartChoice(slot.Name, Tr.F("Slot {0}", SaveText.Number(slot.Name)), detail,
                                       read is { Unreadable: null } r && r.Hearts > 0 ? SaveEditorText.Hearts(r.Hearts) : null));
        }
        if (choices.Count == 0)
        {
            SaveChoices = new object[] { new SaveStartChoice(null, Tr.T("New game"), Tr.T("No save yet: Play starts a new game."), null) };
            return;
        }
        choices.Add(new ManageChoice(Tr.T("Manage saves…"), () => Main.GoSavesCommand.Execute(null)));
        SaveChoices = choices;
    }

    /// The save editor's report of the slots (places, hearts), read in the background once per state of the saves: a version
    /// with the editor reads them (LauncherService.SaveEditor); the picker and the newest save's card show them when it answers
    async void ReadSlots()
    {
        var newest = Newest?.Saved;
        if (newest == null || newest == savesRead) return;
        savesRead = newest;
        if (Service.SaveEditor().Editing is not { } editing) return;
        try
        {
            var report = await editing.ReportAsync(null);
            slotsRead = report.Slots.ToDictionary(s => s.Slot, StringComparer.Ordinal);
            MakeSaveChoices();
            MakeSaveRows();
            RaiseAll(nameof(SaveChoices), nameof(SaveChoice), nameof(NewestPlace), nameof(HasNewestPlace), nameof(NewestHearts),
                     nameof(HasNewestHearts), nameof(NewestLine));
        }
        catch (Exception e) when (e is SaveFailure or IOException or InvalidOperationException or System.ComponentModel.Win32Exception
                                  or OperationCanceledException or System.Text.Json.JsonException)
        {
            LauncherLog.Add($"home: the save editor's report was not read ({e.Message})");
        }
    }

    // ---- the strip under the hero: the newest save, the last start

    static string Caps(string text) => text.ToUpper(System.Globalization.CultureInfo.CurrentCulture);
    public string WelcomeKicker => Caps(Tr.T("Welcome"));
    public string NewestCaption => Caps(Tr.T("Newest save"));
    public string PlayedCaption => Caps(Tr.T("Last played"));
    /// Under the newest save's slot: where it is and when ("Kokiri Forest · today 18:40"), or when alone
    public string NewestLine => HasNewestPlace ? Tr.F("{0} · {1}", NewestPlace, NewestWhen) : NewestWhen;

    /// "Slot 1", "today 18:40", "Kokiri Forest", "5 hearts"
    public string NewestSlot => Newest is { } s ? Tr.F("Slot {0}", SaveText.Number(s.Name)) : Tr.T("No save yet");
    public string NewestWhen => Newest is { Saved: { } t } ? Durations.When(t, DateTime.Now) : Tr.T("the game starts a new one");
    public string NewestPlace => Newest is { } s && slotsRead.TryGetValue(s.Name, out var r) && r is { Unreadable: null, Place: { } p } ? p : "";
    public string NewestHearts => Newest is { } s && slotsRead.TryGetValue(s.Name, out var r) && r is { Unreadable: null, Hearts: > 0 }
        ? Tr.F("{0} ♥", SaveEditorText.Hearts(r.Hearts)) : "";
    public bool HasNewestHearts => NewestHearts.Length > 0;
    public bool HasNewestPlace => NewestPlace.Length > 0;

    /// The last start: the version ("v3.1.0") and when ("yesterday 21:04"); before the first, "Not yet"
    BuildItemViewModel? LastPlayedItem => Service.Profile.LastPlayedAt != null ? Builds.FirstOrDefault(b => b.Entry.Id == Service.Profile.LastPlayed) : null;
    public string LastPlayedTitle => LastPlayedItem is { } b ? b.ShortVersion : Tr.T("Not yet");
    public string LastPlayedWhen => LastPlayedItem != null && Service.Profile.LastPlayedAt is { } t ? Durations.When(t, DateTime.Now)
        : Tr.T("Play is ready when you are");

    /// The picked track's build with a newer version: the one picked when it has one, else the track's default, else its first.
    /// Its newest version shows opposite the title, with What's new in it
    BuildItemViewModel? TrackUpdateItem => PickItem is { HasUpdate: true } h ? h
        : pickTrack == null ? null
        : Builds.Where(b => b.HasUpdate && !b.IsDeleting && VersionPick.TrackOf(b.Entry) == pickTrack).OrderByDescending(b => b.IsDefault).FirstOrDefault();
    public bool HasTrackUpdate => TrackUpdateItem != null;
    /// "Nightly 429 · 6112edb6 is out"
    public string TrackUpdate => TrackUpdateItem is { Update: { } u } ? Versions.PullRequestOf(u) is { } pr
            ? Tr.F("#{0} has a newer push: {1}", pr.Number, pr.Commit)
            : Tr.F("{0} is out", Versions.Shown(u)) : "";
    /// The update opposite the title is a pull request's newer push: Read it, in place of What's new (it has no release notes)
    public bool TrackUpdateIsPullRequest => TrackUpdateItem?.IsPullRequest == true;

    // ---- the Pull request track (docs/pull-requests.md) ----

    bool loadingPulls;

    public bool IsPullRequestTrack => pickTrack == Versions.PullRequest;

    /// The pull request picked, as Beanstalk lists it (null: none, or no longer open)
    public PullRequest? PickedPullRequest =>
        IsPullRequestTrack && Versions.PullRequestOf(pickTag) is { } n ? Service.PullRequestOf(n.Number) : null;
    public bool HasPickedPullRequest => IsPullRequestTrack && Versions.IsPullRequest(pickTag);
    public bool PullRequestTrusted => PickedPullRequest?.Trusted == true;
    public bool PullRequestUntrusted => PickedPullRequest is { Trusted: false };
    /// Under the button: who wrote it and whether a BigBean approved it, or that it is closed
    public string PullRequestTrust => PickedPullRequest switch
    {
        { Approved: true } pr => Tr.F("by {0} · approved for the Test Farm", pr.Author),
        { AuthorBigBean: true } pr => Tr.F("by {0}, a BigBean", pr.Author),
        { } pr => Tr.F("by {0} · not approved by a BigBean", pr.Author),
        null when HasPickedPullRequest && Service.Pulls != null => Tr.T("Its pull request is closed."),
        _ => "",
    };

    /// Read: the pull request picked, on git.magicbean.dev
    public Command ReadPullRequestCommand { get; }
    /// Find a pull request…: by its number or link (PullRequestFindViewModel)
    public Command FindPullRequestCommand { get; }

    async Task LoadPullRequestsAsync()
    {
        if (loadingPulls) return;
        loadingPulls = true;
        RaisePick();
        try { await Service.PullRequestsAsync(); }
        finally
        {
            loadingPulls = false;
            Refresh();
        }
    }

    /// One the player found (PullRequestFindViewModel): the Pull request track, with it picked
    public void PickPullRequest(PullRequest pr)
    {
        pickTrack = Versions.PullRequest;
        // (its build when it is installed, at whichever push; else its head)
        pickTag = Service.Registry.Builds.Select(b => b.Version).FirstOrDefault(v => Versions.PullRequestOf(v)?.Number == pr.Number) ?? pr.Tag;
        ShowPick();
    }

    /// A pull request's build after its warnings (PullRequestWarningViewModel): new, or (Update) in replaces' place
    public void AskPullRequest(string tag, JobKind kind, BuildEntry? replaces)
    {
        if (Versions.PullRequestOf(tag) is not { } n) return;
        // (a build of that very push again: the player agreed to its code already)
        if (Service.Registry.Builds.FirstOrDefault(b => b.Version == tag) is { } same)
        {
            Main.StartWizard(JobKind.Rebuild, Versions.PullRequest, tag, same);
            return;
        }
        if (Service.PullRequestOf(n.Number) is not { } pr)
        {
            Notice = Tr.F("#{0} is not open any more, or Beanstalk did not answer.", n.Number);
            return;
        }
        Main.Modal = new PullRequestWarningViewModel(Main, new PullRequestBuild(pr, kind, replaces));
    }

    /// "2 instances use 48.2 GB in D:\Games\Longshot: 24.4 GB of it is the game's files, which they share." (Disk.InstancesUse:
    /// a game file hard-linked into several builds counted once, and the shader store they share)
    public string Footer => Disk.InstancesUse(Builds.Select(b => b.Entry).ToList(), Service.ShaderStoreBytes ?? 0, ShownPath.Of(Service.BuildsFolder));

    // ---- notices ----

    /// A problem to read (a build that would not start): null for none
    public string? Notice
    {
        get => notice;
        set
        {
            if (!Set(ref notice, value)) return;
            Raise(nameof(HasNotice));
            if (value != null) LauncherLog.Add("shown on Home: " + value);
        }
    }

    public bool HasNotice => notice != null;

    // ---- NVIDIA's shader cache, too small for Longshot ----

    /// (Windows) NVIDIA's shader cache under 4 GB, which the driver would raise only for an administrator
    /// (LauncherService.NvidiaCacheNeedsAdmin): offered once a launcher start, as a card over the versions; its button asks
    /// Windows for an administrator (RaiseNvidiaCacheAsAdmin). Null: no card
    public string? NvidiaCacheOffer
    {
        get => nvidiaCacheOffer;
        private set { if (Set(ref nvidiaCacheOffer, value)) Raise(nameof(HasNvidiaCacheOffer)); }
    }
    string? nvidiaCacheOffer;
    public bool HasNvidiaCacheOffer => nvidiaCacheOffer != null;
    /// The card's quieter second line
    public string NvidiaCacheNote { get => nvidiaCacheNote; private set => Set(ref nvidiaCacheNote, value); }
    string nvidiaCacheNote = "";
    /// Its button: Raise to 10 GB, or Try again after Windows' question was answered no
    public string NvidiaCacheButton { get => nvidiaCacheButton; private set => Set(ref nvidiaCacheButton, value); }
    string nvidiaCacheButton = "";
    /// While Windows asks and the launcher it started raises it
    public bool RaisingNvidiaCache { get => raisingNvidiaCache; private set => Set(ref raisingNvidiaCache, value); }
    bool raisingNvidiaCache;
    bool nvidiaCacheOffered;

    public void OfferNvidiaCache(uint mb)
    {
        nvidiaCacheOffered = true;
        var room = DriverSettings.Size(DriverSettings.ShaderCacheRoomMb);
        NvidiaCacheOffer = Tr.F("NVIDIA's shader cache holds {0}, and Longshot needs about 2 GB.", DriverSettings.Size(mb));
        NvidiaCacheNote = Tr.T("For every game on this PC. Windows asks for permission first.");
        NvidiaCacheButton = Tr.F("Raise to {0}", room);
    }

    public Command RaiseNvidiaCacheCommand => raiseNvidiaCacheCommand ??= new Command(() =>
    {
        if (RaisingNvidiaCache) return;
        RaisingNvidiaCache = true;
        var room = DriverSettings.Size(DriverSettings.ShaderCacheRoomMb);
        // (off the window's thread: Windows' question waits for the player)
        Task.Run(() =>
        {
            var raised = OperatingSystem.IsWindows() && Service.RaiseNvidiaCacheAsAdmin();
            Ui.Post(() =>
            {
                RaisingNvidiaCache = false;
                if (raised)
                {
                    NvidiaCacheOffer = null;
                    Main.ShowToast(Tr.F("NVIDIA's shader cache now holds {0}.", room));
                    return;
                }
                NvidiaCacheOffer = Tr.T("It was not raised.");
                NvidiaCacheNote = Tr.F("To do it yourself: NVIDIA Control Panel › Manage 3D settings › Shader Cache Size › {0} or Unlimited.", room);
                NvidiaCacheButton = Tr.T("Try again");
            });
        });
    });
    Command? raiseNvidiaCacheCommand;
    public Command NotNowNvidiaCacheCommand => notNowNvidiaCacheCommand ??= new Command(() => NvidiaCacheOffer = null);
    Command? notNowNvidiaCacheCommand;
    public bool ShowLeftPanel => HasBuilds;

    /// The quiet answer of the update check
    public string Status
    {
        get => status;
        set { if (Set(ref status, value)) RaiseAll(nameof(ShowStatus), nameof(ShowStatusReport)); }
    }

    /// The check's last answer is a failure (Status says why): Copy for a bug report beside it
    bool statusFailed;
    public bool ShowStatusReport => ShowStatus && statusFailed;

    /// Copy for a bug report, beside a notice: what it says
    public Command CopyNoticeReportCommand { get; }
    /// Copy for a bug report, beside the check's failure: on the empty home its line and why, else the status line under the versions
    public Command CopyCheckReportCommand { get; }

    public Command BuildCommand { get; }
    public Command ChannelCommand { get; }
    public Command DismissProblemCommand { get; }
    /// The update check: at start, from Try again, and after the launcher's channel changed in Settings
    public BusyCommand CheckCommand { get; }
    /// Check for updates, under the versions: the same check, for Longshot's channels and the launcher's own, which says so when
    /// it found nothing new
    public Command CheckForUpdatesCommand { get; }
    /// Try again, on the empty home when the check got no answer: the check itself
    public BusyCommand RetryCheckCommand => CheckCommand;
    public Command PlayDefaultCommand { get; }
    public Command UpdateDefaultCommand { get; }
    public BusyCommand UpdateLauncherCommand { get; }
    /// What's new in the default version's update, beside it
    public Command NotesDefaultCommand { get; }
    /// What's new in the launcher the banner offers
    public Command LauncherNotesCommand { get; }
    /// Stops the launcher's download (only while it downloads: BusyCommand.CanCancel)
    public Command CancelLauncherUpdateCommand { get; }
    /// The check running with a version already known (Try again after an answer, a channel switch): a line with a spinner; the
    /// empty home's first check has its own (Checking)
    public bool ShowCheckLine => CheckCommand.IsBusy && !Checking;
    /// The check's last answer, when no check runs
    public bool ShowStatus => !CheckCommand.IsBusy && !string.IsNullOrEmpty(status);

    /// A build may start: not while the check may still change what is newest, nor while the launcher replaces itself (it closes
    /// at the end, and a build would stop with it)
    /// (not while old versions' folders are deleted either: a new build may be given an unlisted game folder, BuildRegistry.NewFolder)
    public bool CanStartBuild => !CheckCommand.IsBusy && !UpdateLauncherCommand.IsBusy && !deletingLeftovers && !renaming;

    void BusyChanged()
    {
        BuildCommand.Changed();
        AddVersionCommand.Changed();
        CheckForUpdatesCommand.Changed();
        ChannelCommand.Changed();
        UpdateDefaultCommand.Changed();
        GoCommand.Changed();
        HeroPlayCommand.Changed();
        foreach (var b in Builds) b.BusyChanged();
        AddFolderCommand.Changed();
        UseInstalledCommand.Changed();
        OldOnlyAddCommand.Changed();
        RaiseAll(nameof(CanStartBuild), nameof(ShowCheckLine), nameof(ShowStatus), nameof(ShowStatusReport));
    }
    public Command ImportHookshotCommand { get; }
    public Command NotNowHookshotCommand { get; }
    public Command ImportNx2naviCommand { get; }
    public Command NotNowNx2naviCommand { get; }

    string? nx2naviOffer;
    public string? Nx2naviOffer
    {
        get => nx2naviOffer;
        set { if (Set(ref nx2naviOffer, value)) Raise(nameof(HasNx2naviOffer)); }
    }
    public bool HasNx2naviOffer => nx2naviOffer != null;

    // ---- every version is an old one (OldVersions): the player came from the stable launcher, and has a version to add ----

    bool oldOnlyDismissed;
    /// The card over the versions: all of them were built by Longshot's old builder, so none plays here. It says a version has to
    /// be added again (the clean break), with Add a version… and the way back to the stable launcher; Not now hides it until the
    /// next start
    public bool ShowOldOnly => !oldOnlyDismissed && OldCount > 0 && Builds.All(b => b.IsOld);
    /// The old builder's versions: the ones on this launcher's list and the stable launcher's (its own list since the split,
    /// VersionLists), counted at each Refresh
    int OldCount => Builds.Count + stableCount;
    int stableCount;
    public string OldOnlyText => Tr.P(OldCount,
        "Your version was built by Longshot's old builder, which this launcher doesn't run. Add a version to play here. The stable launcher still plays the old one.",
        "Your {0} versions were built by Longshot's old builder, which this launcher doesn't run. Add a version to play here. The stable launcher still plays the old ones.");
    public Command OldOnlyAddCommand { get; }
    public Command OldOnlyStableCommand { get; }
    public Command OldOnlyNotNowCommand { get; }

    void OfferNx2navi()
    {
        Nx2naviOffer = null;
        if (Service.Settings.Nx2naviSavesAsked) return;
        try
        {
            var found = Saves.Emulator(Service.SaveEnv).FirstOrDefault(s => s.Owner == "nx2navi" && s.Found);
            if (found == null) return;
            var profiles = found.Profiles.Select(p => Saves.Describe(p.Path)).Where(s => s.Count > 0).ToList();
            if (profiles.Count > 0)
            {
                var folder = ShownPath.Of(found.Folder);
                Nx2naviOffer = profiles.Count > 1
                    ? Tr.P(profiles.Count, "Found saves in 1 profile in {1}. Copy them into Longshot? The originals stay where they are.",
                           "Found saves in {0} profiles in {1}. Copy them into Longshot? The originals stay where they are.", folder)
                    : Tr.F("Found {0} in {1}. Copy them into Longshot? The originals stay where they are.",
                           SaveText.Long(profiles[0], DateTime.Now), folder);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SaveFailure) { }
    }

    // ---- another launcher's builds and saves: a portable launcher's offer of the installed one's, and Add a build folder… ----

    public Command UseInstalledCommand { get; }
    public Command NotNowInstalledCommand { get; }
    /// Lists a game folder, a folder of them, or a launcher's data folder (LongshotLauncher-data) the player picks
    public Command AddFolderCommand { get; }

    OtherLauncher? installed;
    string? installedOffer;
    public string? InstalledOffer
    {
        get => installedOffer;
        set { if (Set(ref installedOffer, value)) Raise(nameof(HasInstalledOffer)); }
    }
    public bool HasInstalledOffer => installedOffer != null;

    /// A portable launcher with no build yet, on a PC whose installed launcher has builds or saves: use them here? (once)
    void OfferInstalled()
    {
        InstalledOffer = null;
        if (Service.Settings.InstalledLauncherAsked || Service.InstalledLauncher() is not { } other) return;
        installed = other;
        var profiles = other.SavesFitIn(Service.SaveEnv) ? other.Profiles : [];   // (saves only when they could be copied in)
        var builds = other.Builds.Count > 0 ? Tr.P(other.Builds.Count, "1 build", "{0} builds") : null;
        var saves = profiles.Count > 0 ? SaveText.Long(Saves.Describe(profiles[0].Path), DateTime.Now) : null;
        if (builds == null && saves == null) return;
        InstalledOffer = builds != null && saves != null
            ? Tr.F("The launcher installed on this PC has {0} and saves ({1}). Use them here? Its builds stay where they are; its saves are copied.", builds, saves)
            : builds != null ? Tr.F("The launcher installed on this PC has {0}. Use them here? They stay where they are.", builds)
            : Tr.F("The launcher installed on this PC has saves ({0}). Copy them here?", saves!);
    }

    void UseInstalled()
    {
        if (installed is not { } other) return;
        var busy = false;
        Taken(() =>
        {
            var done = Service.TakeIn(other);
            busy = done.Busy;
            return done;
        });
        if (busy) return;   // (the offer stays: another gather held the saves; Use them again copies them)
        Service.Settings.InstalledLauncherAsked = true;
        try { Service.SaveSettings(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        InstalledOffer = null;
    }

    async Task AddFolderAsync()
    {
        if (Main.Host is not { } host) return;
        if (await host.PickFolderAsync(Tr.T("A Longshot game folder, a folder of them, or a portable launcher's LongshotLauncher-data"),
                                       Service.BuildsFolder) is not { } picked) return;
        if (!Taken(() => Service.AddFolder(picked)))
            Notice = Service.Finds(picked)
                ? Tr.F("Everything in {0} is listed already.", picked)
                : Tr.F("No Longshot game to play in {0}. Pick a game folder (it holds the game Longshot built), a folder of them, or a portable launcher's LongshotLauncher-data.", picked);
    }

    /// What taking another launcher's builds and saves did, on the home; false when it found nothing new
    bool Taken(Func<OtherTaken> take)
    {
        OtherTaken done;
        try { done = take(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notice = Tr.F("The builds could not be listed: {0}", e.Message);
            return true;
        }
        if (done.Added.Count > 0)
            Main.ShowToast(done.SavesCopied ? Tr.P(done.Added.Count, "1 build listed, and its saves copied.", "{0} builds listed, and their saves copied.")
                                            : Tr.P(done.Added.Count, "1 build listed.", "{0} builds listed."));
        else if (done.SavesCopied) Main.ShowToast(Tr.T("Saves copied."));
        if (done.SavesLeft != null) Notice = done.SavesLeft;
        Refresh();
        return done.Added.Count > 0 || done.SavesCopied || done.SavesLeft != null;
    }

    // ---- the old versions' folders updates left on the disk: Keep or Delete ----

    IReadOnlyList<Leftover> leftovers = Array.Empty<Leftover>();
    long? leftoverFrees;
    bool deletingLeftovers;

    public bool HasLeftoverOffer => leftovers.Count > 0;
    public Command KeepLeftoversCommand { get; }
    public Command DeleteLeftoversCommand { get; }

    /// Which old versions, where, and what deleting them frees once measured
    public string LeftoverOffer
    {
        get
        {
            if (leftovers.Count == 0) return "";
            var names = string.Join(Tr.T(", "), leftovers.Select(l => l.Shown));
            var where = ShownPath.Of(Service.BuildsFolder);
            var what = Tr.P(leftovers.Count,
                            "An update left the old version {1} in {2}, with no row in your versions. Nothing of yours is in it: your saves are in the shared folder.",
                            "Updates left {0} old versions in {2}, with no row in your versions: {1}. Nothing of yours is in them: your saves are in the shared folder.",
                            names, where);
            return Tr.Sentences(what, leftoverFrees is long b && b > 0 ? Tr.F("Delete frees {0}.", Durations.Size(b)) : "");
        }
    }

    /// Looks for them once, as the home opens (after Saves.Gather took their saves in), and measures them in the background
    public void OfferLeftovers()
    {
        IReadOnlyList<Leftover> found;
        try { found = Service.FindLeftovers(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { found = Array.Empty<Leftover>(); }
        ShowLeftovers(found);
        if (leftovers.Count == 0) return;
        var measuring = leftovers;
        _ = Task.Run(() =>
        {
            long? frees = null;
            try { frees = LauncherService.LeftoverFrees(measuring); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Ui.Post(() =>
            {
                if (!ReferenceEquals(measuring, leftovers)) return;
                leftoverFrees = frees;
                Raise(nameof(LeftoverOffer));
            });
        });
    }

    /// The offer for these folders, with what deleting them frees when measured (also the screenshots' stand-in for the disk)
    public void ShowLeftovers(IReadOnlyList<Leftover> found, long? frees = null)
    {
        leftovers = found;
        leftoverFrees = frees;
        RaiseAll(nameof(HasLeftoverOffer), nameof(LeftoverOffer));
    }

    /// Keep: they stay, and are not offered again
    void KeepLeftovers()
    {
        foreach (var l in leftovers)
            if (!Service.Settings.KeptFolders.Any(k => BuildRegistry.SameFolder(k, l.Folder))) Service.Settings.KeptFolders.Add(l.Folder);
        try { Service.SaveSettings(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        leftovers = Array.Empty<Leftover>();
        RaiseAll(nameof(HasLeftoverOffer), nameof(LeftoverOffer));
    }

    /// Delete: each folder off the window's thread (tens of GB), never while Longshot runs (Windows would stop half-way)
    async Task DeleteLeftoversAsync()
    {
        if (leftovers.Count == 0 || deletingLeftovers) return;
        if (Saves.Running(Service.Platform).Contains(Saves.Longshot.Display))
        {
            Notice = Tr.T("Close Longshot first: the old versions are not deleted while a game runs.");
            return;
        }
        var doing = leftovers;
        var frees = leftoverFrees;
        deletingLeftovers = true;
        KeepLeftoversCommand.Changed();
        DeleteLeftoversCommand.Changed();
        BusyChanged();
        Main.ShowToast(Tr.P(doing.Count, "Deleting the old version…", "Deleting the {0} old versions…"));
        var failed = new List<string>();
        var saved = new List<string>();
        foreach (var l in doing)
        {
            try
            {
                // (a save in it, which Find left out, may have come since: copied to the backups first, BuildRegistry.DeleteFolder)
                if (await Task.Run(() => Service.DeleteLeftover(l)) is { } backup)
                    saved.Add(Tr.F("The save in {0} is backed up in {1}.", Path.Combine(l.Folder, "user", "save"), backup));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (e is FolderNotDeleted { Backup: { } backup })
                    saved.Add(Tr.F("The save in {0} is backed up in {1}.", Path.Combine(l.Folder, "user", "save"), backup));
                failed.Add(Tr.F("{0} could not be deleted: {1}", l.Folder, e.Message));
            }
        }
        deletingLeftovers = false;
        KeepLeftoversCommand.Changed();
        DeleteLeftoversCommand.Changed();
        BusyChanged();
        leftovers = Array.Empty<Leftover>();
        RaiseAll(nameof(HasLeftoverOffer), nameof(LeftoverOffer));
        if (failed.Count > 0 || saved.Count > 0) Notice = string.Join("\n", saved.Concat(failed));
        if (failed.Count == 0) Main.ShowToast(frees is long b && b > 0
            ? Tr.P(doing.Count, "Deleted the old version, {1} freed.", "Deleted the {0} old versions, {1} freed.", Durations.Size(b))
            : Tr.P(doing.Count, "Deleted the old version.", "Deleted the {0} old versions."));
        Refresh();
    }

    // ---- instances' folders from before builds/<track>/<version>: Keep or Move (InstanceFolders) ----

    IReadOnlyList<InstanceFolders.Move> renames = Array.Empty<InstanceFolders.Move>();
    bool renaming;

    public bool HasRenameOffer => renames.Count > 0;
    public Command KeepNamesCommand { get; }
    public Command RenameFoldersCommand { get; }

    /// The card's line: how many, where Move puts them, and that saves, settings and mods go with them (which folders: RenameMoves,
    /// under Which folders)
    public string RenameSummary => renames.Count == 0 ? "" :
        Tr.P(renames.Count, "Your version uses the old folder layout. Move puts it in {1}, with its saves, settings and mods.",
             "{0} versions use the old folder layout. Move puts them in {1}, with their saves, settings and mods.",
             Path.Combine("<track>", "<version>"));

    /// Which folders go where, one line each ("v3.0.0-rc.5 → stable/v3.0.0"), under Which folders
    public IReadOnlyList<string> RenameMoves => renames.Select(m => $"{m.FromName}  →  {m.ToName}").ToList();
    /// The one thing a move does not take care of, under the list
    public string RenameCaveat => Tr.T("A shortcut or Steam entry you made yourself to the game's program needs making again.");

    /// Looks for them once, as the home opens
    void OfferRenames()
    {
        IReadOnlyList<InstanceFolders.Move> found;
        try { found = InstanceFolders.Plan(Service.Registry, Service.BuildsFolder, Service.Platform, Service.Settings.KeptFolderNames); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { found = Array.Empty<InstanceFolders.Move>(); }
        ShowRenames(found);
    }

    /// The offer for these folders (also the screenshots' stand-in for the disk)
    public void ShowRenames(IReadOnlyList<InstanceFolders.Move> found)
    {
        renames = found;
        RaiseAll(nameof(HasRenameOffer), nameof(RenameSummary), nameof(RenameMoves));
    }

    /// Keep: they stay where they are, and are not offered again
    void KeepNames()
    {
        foreach (var m in renames)
            if (!Service.Settings.KeptFolderNames.Any(k => BuildRegistry.SameFolder(k, m.From))) Service.Settings.KeptFolderNames.Add(m.From);
        try { Service.SaveSettings(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        ShowRenames(Array.Empty<InstanceFolders.Move>());
    }

    /// Move: each folder renamed into its place (instant, nothing copied), never while Longshot runs; one that cannot be moved stays as it was
    async Task RenameFoldersAsync()
    {
        if (renames.Count == 0 || renaming) return;
        if (Saves.Running(Service.Platform).Contains(Saves.Longshot.Display))
        {
            Notice = Tr.T("Close Longshot first: its folders are not moved while a game runs.");
            return;
        }
        var doing = renames;
        renaming = true;
        KeepNamesCommand.Changed();
        RenameFoldersCommand.Changed();
        BusyChanged();
        var failed = new List<string>();
        foreach (var m in doing)
        {
            try
            {
                await Task.Run(() => InstanceFolders.Rename(Service.Registry, m, Service.Platform, Service.SaveEnv));
                Service.FolderMoved(m.From, m.To);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed.Add(Tr.F("{0} could not be moved: {1}", m.From, e.Message));
            }
        }
        renaming = false;
        KeepNamesCommand.Changed();
        RenameFoldersCommand.Changed();
        BusyChanged();
        ShowRenames(Array.Empty<InstanceFolders.Move>());
        if (failed.Count > 0) Notice = string.Join("\n", failed);
        else Main.ShowToast(Tr.P(doing.Count, "Moved the folder.", "Moved the {0} folders."));
        Refresh();
    }

    // ---- the offer to import Hookshot's saves (once: until it is answered) ----

    string? hookshotOffer;
    public string? HookshotOffer
    {
        get => hookshotOffer;
        set { if (Set(ref hookshotOffer, value)) Raise(nameof(HasHookshotOffer)); }
    }
    public bool HasHookshotOffer => hookshotOffer != null;

    /// Hookshot saves on this PC, not offered yet: the home offers to import them
    void OfferHookshot()
    {
        if (Service.Settings.HookshotSavesAsked) return;
        try
        {
            var found = HookshotSaves.Find(Service.SaveEnv).Where(s => s.Valid).ToList();
            if (found.Count > 0)
                HookshotOffer = Tr.F("Found {0} in Hookshot. Copy them into Longshot? Hookshot keeps its own.",
                                     SaveText.Long(found[0].Slots, DateTime.Now));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SaveFailure) { }
    }

    // ---- the launcher's own update ----

    public bool HasLauncherUpdate => Service.LauncherUpdate != null;
    /// Only a newer one "is out": after the player switched the launcher's channel, its newest can be older than this one, or a
    /// nightly that has no order against a release
    int LauncherUpdateOrder => Service.LauncherUpdate is { } r ? SelfUpdate.Compare(r.Version, Service.OwnTag) : 0;

    public string LauncherUpdateHeading => LauncherUpdateOrder > 0 ? Tr.T("A new launcher is out")
        : Tr.F("Another launcher on the {0} channel", ChannelPickerViewModel.NameOf(Service.LauncherChannel));

    public string LauncherUpdateButton => LauncherUpdateOrder > 0 ? Tr.T("Update the launcher") : Tr.T("Switch to it");

    public string LauncherUpdateText => Service.LauncherUpdate is { } r
        ? LauncherUpdateOrder switch
        {
            > 0 => Tr.F("Longshot Launcher {0} is out (you have {1}).", Versions.Shown(r.Version), Versions.Shown(Service.OwnTag)),
            < 0 => Tr.F("Longshot Launcher {0} is available on {1}. It is older than yours ({2}).", Versions.Shown(r.Version),
                        ChannelPickerViewModel.NameOf(Service.LauncherChannel), Versions.Shown(Service.OwnTag)),
            _ => Tr.F("Longshot Launcher {0} is available on {1} (you have {2}).", Versions.Shown(r.Version),
                      ChannelPickerViewModel.NameOf(Service.LauncherChannel), Versions.Shown(Service.OwnTag)),
        }
        : "";

    /// Downloads the new launcher, checks it, puts it in place and starts it; this one closes. On the banner meanwhile: the button
    /// off ("Updating…"), the download's percentage and bar, then each step; Cancel while it downloads. A failure leaves this one as
    /// it is, says why (Notice) and the button is on again
    async Task UpdateLauncherAsync(BusyCommand busy, CancellationToken cancel)
    {
        busy.Report(Tr.T("Downloading the new launcher…"), 0, canCancel: true);
        await Task.Run(() => Service.UpdateLauncherAsync(
            p => Ui.Post(() => busy.Report(p >= 1 ? Tr.T("Checking the download…") : Tr.F("Downloading the new launcher… {0}%", (int)Math.Round(p * 100)), p, canCancel: p < 1)),
            cancel,
            step => Ui.Post(() => busy.Report(step, canCancel: false))), CancellationToken.None);
        Main.Host?.Close();
    }

    /// The start's own request: --launch names a build (listed if it was not)
    public void Open(StartRequest start)
    {
        var notes = new List<string>();
        if (Service.Registry.Problem is { } broken) notes.Add(Tr.F("The list of builds could not be read (kept as builds.json.broken): {0}", broken));
        if (Service.Settings.Problem is { } unreadable) notes.Add(Tr.F("The launcher's settings could not be read (kept as settings.json.broken): {0}", unreadable));
        if (start.Problem != null) notes.Add(start.Problem);
        if (start.Folder != null && start.Kind == StartKind.Launch)
        {
            Service.Ensure(start.Folder);
        }
        Notice = notes.Count > 0 ? string.Join("\n", notes) : null;
        // (before the leftovers: the game a row takes back, or a game whose row went (#141), is no leftover)
        // (one toast for all: a toast shows one at a time)
        var relisted = Service.Relist();
        if (relisted.Count == 1) Main.ShowToast(Tr.F("{0} was found in its own folder and is listed again.", Versions.Shown(relisted[0].Version)));
        else if (relisted.Count > 1)
            Main.ShowToast(Tr.P(relisted.Count, "{0} versions were found in their own folders and are listed again: {1}.",
                                "{0} versions were found in their own folders and are listed again: {1}.",
                                string.Join(", ", relisted.Select(b => Versions.Shown(b.Version)))));
        OfferHookshot();
        OfferNx2navi();
        OfferInstalled();
        OfferLeftovers();
        OfferRenames();
        // (a Play that did not get to the game, or a profile's save folder the game could not use: said on Home under its button
        // until the next Play, and as a toast on another page)
        Service.PlayFailed += said => Ui.Post(() =>
        {
            PlayError = said;
            if (!Main.IsHome) Main.ShowToast(said);
        });
        // (the versions longshot-builder built in a folder of their own: copied once into their version folders, then listed there)
        // (then the version folders updates left that nothing uses any more: pruned, the last one replaced kept for going back;
        // never while the move copies into one)
        if (!Service.MoveToVersionFolders(() => { Refresh(); _ = Service.PruneVersions(); })) _ = Service.PruneVersions();
        Refresh();
        // (what each version's builder says of it and of the profile's game: Home shows that, once they answered)
        Service.RefreshInfos(Refresh);
    }

    public void Refresh()
    {
        var selected = Builds.FirstOrDefault(b => b.IsSelected)?.Entry.Id;
        // (a build whose folder was deleted by hand, not here: its row goes, as Delete's would)
        if (Service.ForgetGone(deleting) is { Count: > 0 } gone)
            Main.ShowToast(Tr.P(gone.Count, "1 version was deleted outside the launcher, so it is no longer listed.",
                                "{0} versions were deleted outside the launcher, so they are no longer listed."));
        Builds.Clear();
        saves = Service.SaveSlots();
        foreach (var b in Service.Registry.Builds)
        {
            var item = new BuildItemViewModel(this, b, Service.IsBuilt(b), Service.UpdateFor(b), saves, deleting.Contains(b.Id), Service.IsOld(b));
            item.IsSelected = b.Id == selected;
            item.IsFirst = Builds.Count == 0;
            Builds.Add(item);
        }
        stableCount = Service.StableFolders.Count;
        ShowPick();
        RaiseHero();
        RaiseAll(nameof(HasBuilds), nameof(NoBuilds), nameof(Default), nameof(ShowLeftPanel), nameof(Footer), nameof(NewestDefault), nameof(NewestDefaultText), nameof(DefaultChannel), nameof(DefaultIsRelease),
                 nameof(DefaultIsNightly), nameof(KnowsNewest), nameof(CheckFailed), nameof(CheckWhy), nameof(HasCheckWhy), nameof(ShowCheckReport), nameof(ChannelEmpty), nameof(CheckFailedText), nameof(Checking), nameof(ShowCheckLine), nameof(HasLauncherUpdate),
                 nameof(LauncherUpdateText), nameof(LauncherUpdateHeading), nameof(LauncherUpdateButton), nameof(ShowOldOnly), nameof(OldOnlyText));
        OldOnlyAddCommand.Changed();
        BuildCommand.Changed();
        PlayDefaultCommand.Changed();
        UpdateDefaultCommand.Changed();
        UpdateLauncherCommand.Changed();
        LauncherNotesCommand.Changed();
        Service.Measure(() => { foreach (var b in Builds) b.RaiseMeta(); Raise(nameof(Footer)); });
        Service.MeasureShaderStore(() => Raise(nameof(Footer)));
    }

    /// The update check, in the background: a failure is one quiet line, never a stop. Asked while one runs (the launcher's channel
    /// changed in Settings), it runs once more after it, for the new channel
    public async Task CheckReleasesAsync()
    {
        if (CheckCommand.IsBusy)
        {
            checkAgain = true;
            return;
        }
        do
        {
            checkAgain = false;
            await CheckCommand.RunAsync();
        } while (checkAgain);
    }

    async Task CheckAsync(BusyCommand busy, CancellationToken cancel)
    {
        busy.Report(Tr.T("Checking for updates…"));
        var wasAsked = asked;
        asked = false;
        Service.Checked = false;
        listed = false;
        checkProblem = null;
        Refresh();
        var any = false;
        statusFailed = false;
        try
        {
            any = listed = await Service.CheckReleasesAsync();
            statusFailed = !any;
            // (why, in words a player can act on: no address, no connection, a timeout, an HTTP error: NetFailure)
            Status = any ? "" : Service.Releases.Unreachable is { } why
                ? Tr.F("Updates were not checked. {0}", why)
                : Service.Releases.Problem is { } problem
                ? Tr.F("Updates were not checked: {0}", problem)
                : Tr.T("Could not reach git.magicbean.dev: updates were not checked.");
        }
        catch (Exception e)
        {
            Service.Checked = true;
            checkProblem = e.Message;
            statusFailed = true;
            Status = Tr.F("Could not check for updates: {0}", e.Message);
        }
        // (the answer in the launcher's log: each request's own lines are there already, this is what the check made of them)
        LauncherLog.Add(any ? "update check: answered" : "update check failed: " +
                        (Service.Releases.Unreachable ?? Service.Releases.Problem ?? checkProblem ?? "no version listed"));
        Refresh();
        Raise(nameof(ShowStatusReport));
        // (a check someone asked for answers even when nothing is new; an update shows itself: the banner, the Update buttons)
        if (wasAsked && any && !HasLauncherUpdate && !Builds.Any(b => b.HasUpdate))
            Status = Tr.F("Everything is up to date, the launcher too. Checked at {0}.", DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// For the screenshots: the update check got no answer, for why
    public void ShowCheckFailed(string why)
    {
        Service.Checked = true;
        checkProblem = why;
        statusFailed = true;
        Refresh();
    }

    public void Select(BuildItemViewModel b)
    {
        var on = !b.IsSelected;
        foreach (var x in Builds) x.IsSelected = false;
        b.IsSelected = on;
        // (a row opened: its track and version in the dropdowns, so the button is for it; closed: the pick stays, and what it
        // replaces is the track's default again)
        if (on && b.Entry.Version is { } version)
        {
            pickTrack = VersionPick.TrackOf(b.Entry);
            pickTag = version;
            ShowPick();
        }
        else RaisePick();
    }

    public void Play(BuildItemViewModel b)
    {
        // (the profile remembers what it played: the version, and when)
        try { Service.MarkPlayed(b.Entry.Id); } catch (Exception e) when (e is ProfileFailure or IOException or UnauthorizedAccessException) { }
        try
        {
            // (longshot-builder's game reads its files from the .nup where it is: gone, step 1 says why and asks for it where it is
            // now, and the game is pointed at it there in seconds, longshot-builder index (RepointViewModel). Only another .nup than
            // the one it was built from is built again from: a rebuild of the same row, so it finds its work folder
            // (BuildRequest.WorkId) and its store. On a drive not connected, Play says to connect it: NupMovedException)
            if (LongshotBuilder.MissingNup(b.Entry.Folder, b.Entry) is { DriveMissing: false } moved && b.Entry.Version is { } version
                && CanStartBuild && !b.IsDeleting)
            {
                Main.StartWizard(JobKind.Rebuild, Versions.IsPullRequest(version) ? Versions.PullRequest : b.Channel, version, b.Entry,
                                 movedNup: moved.Nup);
                return;
            }
            if (Service.Play(b.Entry) is { } said)
            {
                Notice = said;
                Refresh();
            }
            if (OperatingSystem.IsWindows() && !nvidiaCacheOffered && Service.NvidiaCacheNeedsAdmin is { } mb) OfferNvidiaCache(mb);
            Main.ShowToast(Tr.F("Starting Longshot, {0}…", b.Version));
        }
        catch (OldVersionException e) { Notice = e.Message; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Notice = Tr.F("{0} could not start: {1}", b.Version, e.Message);
        }
    }

    public void SetDefault(BuildItemViewModel b)
    {
        Service.SetDefault(b.Entry);
        // (the dropdowns on it too: Play starts it now, as the toast says, and the home opens on it)
        pickTrack = VersionPick.TrackOf(b.Entry);
        pickTag = b.Entry.Version;
        Refresh();
        Main.ShowToast(Tr.F("Play now starts {0}.", b.Version));
    }

    /// began: its folder starts to go (the Storage page pins its progress, in place of the toast); after: the delete ended,
    /// with whether the version went (the page drops its rows and measures again)
    public void AskDelete(BuildItemViewModel b, Action<bool>? after = null, Action? began = null) =>
        Main.Modal = new DeleteViewModel(Main, b, after, began);

    /// A build's folder being deleted (tens of GB: seconds to minutes): its row shows it, and nothing starts on it meanwhile
    public void Deleting(BuildEntry b, bool on)
    {
        if (on ? deleting.Add(b.Id) : deleting.Remove(b.Id))
        {
            AddFolderCommand.Changed();
            UseInstalledCommand.Changed();
            Refresh();
        }
    }

    /// What's new from a build's version to its update, with Update in the dialog
    public void ShowNotes(BuildItemViewModel b)
    {
        if (b.Update is not { } to) return;
        ReleaseNotesViewModel.Show(Main, Product.Longshot, b.Entry.Version, to, Service.NotesFor(b.Entry.Version, to), Tr.T("Update"),
                                   CanStartBuild && !b.IsDeleting ? () => UpdateBuild(b) : null);
    }

    void ShowLauncherNotes()
    {
        if (Service.LauncherUpdate is not { } r) return;
        ReleaseNotesViewModel.Show(Main, Product.Launcher, Service.OwnTag, r.Version, Service.LauncherNotes(), LauncherUpdateButton,
                                   UpdateLauncherCommand.CanExecute(null) ? () => UpdateLauncherCommand.Execute(null) : null);
    }

    public void UpdateBuild(BuildItemViewModel b)
    {
        if (b.Update is not { } to) return;
        // (a pull request's newer push: new code, so its warnings again; approval is pinned to a commit)
        if (Versions.IsPullRequest(to))
        {
            AskPullRequest(to, JobKind.Update, b.Entry);
            return;
        }
        Main.StartWizard(JobKind.Update, b.Channel, to, b.Entry);
    }

    public void StartBuild(string channel)
    {
        if (Service.NewestOf(channel) is not { } version) return;
        var same = Service.Registry.Builds.FirstOrDefault(b => b.Version == version);
        Main.StartWizard(same != null ? JobKind.Rebuild : JobKind.Build, channel, version, same);
    }
}

/// Choosing what to build: release or nightly, each with its newest version and whether you have it
public sealed class ChannelPickerViewModel : ViewModelBase
{
    string pick;

    public sealed class Row(ChannelPickerViewModel owner, string id, string name, string about, string? version, bool have) : ViewModelBase
    {
        public string Id => id;
        public string Name => Tr.T(name);
        public string About => about;
        public string Version => version == null ? Tr.T("Not found") : Versions.Shown(version);
        public bool Known => version != null;
        public bool Have => have;
        public bool IsRelease => id == Versions.Stable;
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
        service.Settings.DefaultChannel == null && service.DefaultChannel == id
            ? Tr.Sentences(Tr.T(about), Tr.T("It follows the launcher until you pick one in Settings.")) : Tr.T(about);

    /// The channels, in English (Tr.T where shown)
    public static readonly (string Id, string Name, string About)[] All =
    {
        (Versions.Stable, Tr.N("Stable"), Tr.N("The tested version. Best for playing.")),
        (Versions.Nightly, Tr.N("Nightly"), Tr.N("The newest development build, with every change of the day.")),
    };

    /// A channel's name in the language in use (stable: Stable); an unknown one as it is
    public static string NameOf(string id) => All.FirstOrDefault(c => c.Id == id).Name is { } name ? Tr.T(name) : id;

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
    public string GoText => Version == null ? Tr.T("Continue") : Tr.F(Again ? "Rebuild {0}" : "Continue with {0}", Versions.Shown(Version));

    void Go()
    {
        Main.Modal = null;
        Main.Home.StartBuild(pick);
    }
}

/// Deleting a build: what goes with it (its folder; never the saves, which every build shares), and who becomes the default
public sealed class DeleteViewModel : ViewModelBase
{
    readonly BuildItemViewModel build;
    readonly Action<bool>? after;
    readonly Action? began;

    public DeleteViewModel(MainViewModel main, BuildItemViewModel build, Action<bool>? after = null, Action? began = null)
    {
        Main = main;
        this.build = build;
        this.after = after;
        this.began = began;
        KeepCommand = new Command(() => Main.Modal = null);
        DeleteCommand = new Command(() => _ = DeleteAsync());
    }

    public MainViewModel Main { get; }
    public string Title => Tr.F("Delete {0}?", build.Version);
    bool GameFolderHere => GameFolder.IsGameFolder(build.Entry.Folder, Main.Platform);

    public string Sub => GameFolderHere
        ? Freed(build.Entry) is long b ? Tr.F("This deletes its folder and frees {0}:", Durations.Size(b)) : Tr.T("This deletes its folder:")
        : Tr.T("This takes it off your versions. Its folder is not a Longshot game folder, so it stays on the disk:");
    public string Folder => ShownPath.Of(build.Entry.Folder);
    public string FolderFull => ShownPath.Of(build.Entry.Folder);

    /// What deleting it frees: its files with no hard link outside its folder (game files shared with another build stay with
    /// that one: BuildEntry.Frees); null when not measured
    static long? Freed(BuildEntry e) => Disk.Own(e);

    BuildEntry? Next => Main.Service.Registry.Builds.FirstOrDefault(b => b.Id != build.Entry.Id);
    public bool DefaultNote => build.IsDefault && Next != null;
    public string DefaultText => Next is { } n ? Tr.F("It is your default: Play will start {0} instead.", n.Version is { } v ? Versions.Shown(v) : n.Name) : "";

    public Command KeepCommand { get; }
    public Command DeleteCommand { get; }

    async Task DeleteAsync()
    {
        var deleted = false;
        try { deleted = await DeleteNowAsync(); }
        finally { after?.Invoke(deleted); }
    }

    /// What the delete has to say: on Home's notice, and as a toast too when it was asked from another page (Storage), where
    /// Home's notice is not seen
    void Say(string text)
    {
        Main.Home.Notice = text;
        if (!Main.IsHome) Main.ShowToast(text);
    }

    /// Whether the version went
    async Task<bool> DeleteNowAsync()
    {
        Main.Modal = null;
        var service = Main.Service;
        var entry = build.Entry;
        var files = GameFolderHere;
        // (a running game holds its files: on Windows the delete would stop half-way through the folder)
        var running = files ? Saves.Running(service.Platform) : Array.Empty<string>();
        if (running.Contains(Saves.Longshot.Display))
        {
            Say(Tr.F("Close Longshot first: {0} is not deleted while a game runs.", build.Version));
            return false;
        }
        try
        {
            if (files)
            {
                // (tens of GB: off the window's thread, the folder first and the row once it is gone; the row marked first, so that
                // nothing starts on it meanwhile. The save the folder holds itself goes first: a Windows build started outside the
                // launcher saves there. Brought into the shared one, or backed up, never deleted: BuildRegistry.DeleteGame)
                // (asked from Storage: its pinned progress says it for as long as it takes, not a toast that fades first)
                if (began != null) began();
                else Main.ShowToast(Tr.F("Deleting {0}…", build.Version));
                Main.Home.Deleting(entry, true);
                DeletedGame done;
                try { done = await Task.Run(() => service.DeleteGame(entry, running)); }
                finally { Main.Home.Deleting(entry, false); }
                if (done.Said() is { } said) Say(said);
                if (!done.Deleted)
                {
                    Main.Home.Refresh();
                    return false;
                }
            }
            // (the default goes to a release when there is one left, as the mockup: Registry.Remove takes the first otherwise)
            var wasDefault = build.IsDefault;
            service.Remove(entry, deleteFiles: false);
            if (wasDefault && service.Registry.Builds.FirstOrDefault(b => UpdatePolicy.ChannelOf(b.Channel, b.Version) == Versions.Stable) is { } rel)
                service.SetDefault(rel);
            Main.ShowToast(Freed(entry) is long b && files ? Tr.F("Deleted {0}, {1} freed.", build.Version, Durations.Size(b)) : Tr.F("Deleted {0}.", build.Version));
            Main.Home.Refresh();
            // (the last version gone: the build files it left, often many GB, offered to go too (asked from Storage, the page
            // lists them itself, under Faster updates). Measured off the window's thread: objcache is thousands of files)
            if (began == null && service.Registry.Builds.Count == 0)
            {
                var work = service.Work;
                long left = 0;
                try { left = await Task.Run(() => Storage.LeftAfterLast(work, service.Registry.Builds.Count)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                if (left > 0 && Main.Modal == null && !Main.InWizard && service.Registry.Builds.Count == 0)
                    Main.Modal = new BuildFilesViewModel(Main, work, left);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say(Tr.F("{0} could not be deleted: {1}", entry.Folder, e.Message));
        }
        Main.Home.Refresh();
        return false;
    }
}

/// After the last version went (Home's delete): the build files left in the build files folder (compiled code, translations
/// and their stores: often many GB), deleted too or kept. Only what make_game made goes (Disk.FreeWork), never the folder
/// itself, which the player may have chosen and which may hold other things; saves and their backups are never in it. Kept,
/// the Storage page frees them later (Faster updates)
public sealed class BuildFilesViewModel : ViewModelBase
{
    readonly string work;
    readonly long bytes;
    bool deleting;

    public BuildFilesViewModel(MainViewModel main, string work, long bytes)
    {
        Main = main;
        this.work = work;
        this.bytes = bytes;
        KeepCommand = new Command(() => Main.Modal = null, () => !deleting);
        DeleteCommand = new Command(() => _ = DeleteAsync(), () => !deleting);
    }

    public MainViewModel Main { get; }
    public string Title => Tr.T("Delete the build files too?");
    public string Sub => Tr.F("No version is installed now, but the files your builds made are still here, {0}:", Durations.Size(bytes));
    public string Folder => ShownPath.Of(work);
    public string FolderFull => work;
    public string Note => Tr.T("They only make the next build quicker: none of it is needed to play. Kept, you can free them later in Storage.");
    public string DeleteLabel => deleting ? Tr.T("Deleting…") : Tr.T("Delete them");

    public Command KeepCommand { get; }
    public Command DeleteCommand { get; }

    /// The dialog stays until they are gone, so that no build starts on the folder meanwhile
    async Task DeleteAsync()
    {
        deleting = true;
        KeepCommand.Changed();
        DeleteCommand.Changed();
        Raise(nameof(DeleteLabel));
        try
        {
            await Task.Run(() => Disk.FreeWork(work));
            Main.StorageMeasured = null;
            Main.ShowToast(Tr.F("Deleted the build files, {0} freed.", Durations.Size(bytes)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Main.Home.Notice = Tr.F("Some build files could not be deleted: {0} Storage can try again.", e.Message);
        }
        finally { Main.Modal = null; }
    }
}

/// The version a first build makes, as Home's Version picker shows it with no version yet
public sealed class BuildTargetChoice(string version, string channel, bool nightly)
{
    public string Version => version;
    public string Channel => channel;
    public bool IsNightly => nightly;
}
