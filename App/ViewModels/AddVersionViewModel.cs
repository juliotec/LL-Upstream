using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

/// The Versions page's Add version: mostly a release, Stable or Nightly (each channel's newest picked, its older versions in the
/// Version list), and under Advanced, closed until opened, a build from Longshot's source: main's newest commit, or a pull
/// request from Beanstalk's list, a WIP one too (marked so). A pull request goes through its warnings (PullRequestWarningViewModel)
/// before anything is built; main has a lighter note here (untested, not a release). Both are built by SourceBuilds
public sealed class AddVersionViewModel : ViewModelBase
{
    /// What is picked: a channel's version, main, or a pull request
    public const string Main = "main";

    readonly MainViewModel main;
    string kind;
    VersionChoice? version;
    PullChoice? pull;
    bool advanced, loadingPulls, loadingMain;
    string? mainHead, pullsProblem;
    IReadOnlyList<PullChoice> pulls = Array.Empty<PullChoice>();

    /// One pull request in Advanced's list: "#1548 · its title", WIP when it is a draft, who wrote it and whether a BigBean approved it
    public sealed class PullChoice(PullRequest pr)
    {
        public PullRequest Pr => pr;
        public string Title => pr.Title.Length > 0 ? $"#{pr.Number} · {pr.Title}" : $"#{pr.Number}";
        public bool IsDraft => pr.Draft;
        public string Note => pr.Approved ? Tr.T("approved") : pr.AuthorBigBean ? Tr.T("BigBean") : Tr.T("not approved");
        public string Meta => pr.Author.Length > 0 ? Tr.F("by {0} · at {1}", pr.Author, pr.Head[..8]) : Tr.F("at {0}", pr.Head[..8]);
    }

    public AddVersionViewModel(MainViewModel main, string? start = null)
    {
        this.main = main;
        kind = start ?? main.Service.DefaultChannel;
        Channels = ChannelPickerViewModel.All.Select(c => new ChannelRow(this, c.Id, c.Name,
            ChannelPickerViewModel.AboutOf(main.Service, c.Id, c.About), main.Service.NewestOf(c.Id))).ToList();
        CancelCommand = new Command(() => main.Modal = null);
        GoCommand = new Command(Go, CanGo);
        AdvancedCommand = new Command(() => AdvancedOpen = !AdvancedOpen);
        ShowVersions();
        if (kind is Main or Versions.PullRequest) AdvancedOpen = true;
    }

    public MainViewModel MainWindow => main;
    LauncherService Service => main.Service;

    /// Stable and Nightly, each with its newest version
    public IReadOnlyList<ChannelRow> Channels { get; }

    public sealed class ChannelRow(AddVersionViewModel owner, string id, string name, string about, string? newest) : ViewModelBase
    {
        public string Id => id;
        public string Name => Tr.T(name);
        public string About => about;
        public string Newest => newest == null ? Tr.T("Not found") : Versions.Shown(newest);
        public bool IsNightly => id == Versions.Nightly;
        public string Tag => id;
        public bool IsPicked
        {
            get => owner.Kind == id;
            set { if (value) owner.Kind = id; }
        }
        public void Changed() => Raise(nameof(IsPicked));
    }

    /// stable, nightly, main (AddVersionViewModel.Main) or pr (Versions.PullRequest)
    public string Kind
    {
        get => kind;
        set
        {
            if (!Set(ref kind, value)) return;
            foreach (var c in Channels) c.Changed();
            ShowVersions();
            if (kind == Main && mainHead == null && !loadingMain) _ = LoadMainAsync();
            if (kind == Versions.PullRequest && pulls.Count == 0) _ = LoadPullsAsync();
            RaiseAll(nameof(IsMain), nameof(IsPull), nameof(IsChannel), nameof(ShowsPullWarning), nameof(ShowsMainNote), nameof(GoText),
                     nameof(DraftPicked));
            GoCommand.Changed();
        }
    }

    public bool IsChannel => Versions.IsChannel(kind);
    public bool IsMain
    {
        get => kind == Main;
        set { if (value) Kind = Main; }
    }
    public bool IsPull
    {
        get => kind == Versions.PullRequest;
        set { if (value) Kind = Versions.PullRequest; }
    }

    // ---- a channel's versions: its newest picked, the older ones listed (VersionPick, as the update check read them)

    public IReadOnlyList<VersionChoice> ChannelVersions { get; private set; } = Array.Empty<VersionChoice>();
    public bool HasChannelVersions => ChannelVersions.Count > 0;

    public VersionChoice? PickedVersion
    {
        get => version;
        set
        {
            if (value == null || !Set(ref version, value)) return;
            RaiseAll(nameof(GoText), nameof(PickNote));
            GoCommand.Changed();
        }
    }

    void ShowVersions()
    {
        if (!IsChannel) return;
        var defaultId = Service.Registry.Builds.FirstOrDefault(b => b.Default)?.Id;
        ChannelVersions = Service.VersionsOn(kind).Select(v =>
        {
            var c = new VersionChoice(v.Tag);
            c.Show(v, defaultId != null && v.Build?.Id == defaultId, false);
            return c;
        }).ToList();
        version = ChannelVersions.FirstOrDefault(v => Versions.IsChannel(kind) && v.Tag == Service.NewestOf(kind)) ?? ChannelVersions.FirstOrDefault();
        RaiseAll(nameof(ChannelVersions), nameof(HasChannelVersions), nameof(PickedVersion), nameof(GoText), nameof(PickNote));
    }

    /// What the version picked does: on this PC already, or built (in the channel's build's place when it has one)
    PickPlan? Plan => IsChannel && version != null ? VersionPick.Plan(kind, version.Tag, Service.Registry.Builds) : null;

    public string PickNote => Plan switch
    {
        { Action: PickAction.Play } => Tr.T("It is on this PC already: play it from Home."),
        { Action: PickAction.Replace, Build: { } b } p => Tr.F(p.Older ? "Built in the place of {0}: it goes back to this version." : "Built in the place of {0}, which it updates.",
                                                          Versions.Shown(b.Version)),
        { } => Tr.T("Built in a folder of its own, beside your other versions."),
        _ => "",
    };

    // ---- Advanced: main and the pull requests, built from source

    public bool AdvancedOpen
    {
        get => advanced;
        set
        {
            if (!Set(ref advanced, value)) return;
            Raise(nameof(AdvancedClosed));
            // (main's newest commit asked as Advanced opens, so its row names it)
            if (advanced && mainHead == null && !loadingMain) _ = LoadMainAsync();
            // (closed again on a build from source: back to the channel that new builds start on)
            if (!advanced && !IsChannel) Kind = Service.DefaultChannel;
        }
    }
    public bool AdvancedClosed => !advanced;
    public Command AdvancedCommand { get; }

    /// main's newest commit (40 hex), once git.magicbean.dev answered; the screenshots set their own
    public string? MainHead
    {
        get => mainHead;
        set
        {
            mainHead = value;
            RaiseAll(nameof(MainHead), nameof(MainLine), nameof(GoText));
            GoCommand.Changed();
        }
    }

    /// main's newest commit, once git.magicbean.dev answered
    public string MainLine => loadingMain ? Tr.T("Asking git.magicbean.dev for main's newest commit…")
        : mainHead is { } h ? Tr.F("main's newest commit, {0}", h[..8])
        : Tr.T("main's newest commit could not be read: try again in a moment.");

    async Task LoadMainAsync()
    {
        loadingMain = true;
        Raise(nameof(MainLine));
        var head = await SourceBuilds.MainHeadAsync(Service.Fetcher);
        loadingMain = false;
        MainHead = head ?? mainHead;
    }

    /// Beanstalk's open pull requests this PC can build (PullRequests.Problem), drafts too, newest change first
    public IReadOnlyList<PullChoice> Pulls
    {
        get => pulls;
        private set
        {
            pulls = value;
            RaiseAll(nameof(Pulls), nameof(HasPulls));
        }
    }
    public bool HasPulls => pulls.Count > 0;
    public string? PullsProblem
    {
        get => loadingPulls ? Tr.T("Asking Beanstalk for the open pull requests…") : pullsProblem;
    }
    public bool HasPullsProblem => PullsProblem != null;

    async Task LoadPullsAsync()
    {
        loadingPulls = true;
        RaiseAll(nameof(PullsProblem), nameof(HasPullsProblem));
        var list = await Service.PullRequestsAsync(fresh: false);
        loadingPulls = false;
        pullsProblem = list == null ? Tr.T("Beanstalk did not answer, so the pull requests are not known. Try again in a moment.")
            : list.Count == 0 ? Tr.T("No pull request is open.") : null;
        Pulls = (list ?? []).Where(p => PullRequests.Problem(p, Service.Platform) == null).Select(p => new PullChoice(p)).ToList();
        pull ??= Pulls.FirstOrDefault();
        RaiseAll(nameof(PullsProblem), nameof(HasPullsProblem), nameof(PickedPull), nameof(GoText), nameof(DraftPicked));
        GoCommand.Changed();
    }

    public PullChoice? PickedPull
    {
        get => pull;
        set
        {
            if (value == null || !Set(ref pull, value)) return;
            RaiseAll(nameof(GoText), nameof(DraftPicked));
            GoCommand.Changed();
        }
    }

    /// A pull request's code, a WIP one's too, has been reviewed by nobody yet and runs here with the player's rights: said
    /// in red before Build; its warnings follow (PullRequestWarningViewModel)
    public bool ShowsPullWarning => IsPull;
    public bool DraftPicked => IsPull && pull?.IsDraft == true;
    /// main: Longshot's newest code, untested, not a release
    public bool ShowsMainNote => IsMain;

    // ---- the button

    public string GoText => kind switch
    {
        Main => mainHead is { } h ? Tr.F("Build main ({0})", h[..8]) : Tr.T("Build main"),
        Versions.PullRequest => pull is { } p ? Tr.F("Build #{0}…", p.Pr.Number) : Tr.T("Build a pull request"),
        _ => Plan switch
        {
            { Action: PickAction.Play } => Tr.T("On this PC"),
            { Action: PickAction.Replace } p => Tr.F(p.Older ? "Go back to {0}" : "Update to {0}", Versions.Shown(p.Version)),
            { } p => Tr.F("Build {0}", Versions.Shown(p.Version)),
            _ => Tr.T("Build"),
        },
    };

    bool CanGo() => main.Home.CanStartBuild && kind switch
    {
        Main => mainHead != null,
        Versions.PullRequest => pull != null,
        _ => Plan is { Action: not PickAction.Play },
    };

    public Command GoCommand { get; }
    public Command CancelCommand { get; }

    void Go()
    {
        switch (kind)
        {
            case Main when mainHead is { } head:
                main.Modal = null;
                LauncherLog.Add($"main at {head}: the player asked to build it");
                main.StartWizard(JobKind.Build, Versions.PullRequest, Versions.MainTag(head));
                break;
            case Versions.PullRequest when pull is { } p:
                // (in the place of the build of the same pull request, an older push of it; else a new one. Its warnings next)
                var plan = VersionPick.Plan(Versions.PullRequest, p.Pr.Tag, Service.Registry.Builds);
                main.Modal = null;
                main.Home.AskPullRequest(p.Pr.Tag, plan is { Action: PickAction.Replace } ? JobKind.Update : JobKind.Build, plan.Build);
                break;
            default:
                if (version == null) return;
                main.Modal = null;
                main.Home.BuildVersion(kind, version.Tag);
                break;
        }
    }
}
