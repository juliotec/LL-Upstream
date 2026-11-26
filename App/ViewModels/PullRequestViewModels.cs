using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

/// What building a pull request will do once the player agrees: the wizard for its build (JobKind.Build, or Update in the place of
/// another pull request's build or an older push of the same one)
public sealed record PullRequestBuild(PullRequest Pr, JobKind Kind, BuildEntry? Replaces);

/// The first warning before a pull request's code is built (docs/pull-requests.md), for every pull request: what
/// a pull request is, that it is built and run on this PC, and that saves are shared. Soft for one a BigBean wrote or approved
/// (PullRequest.Trusted): it says so, and Build goes straight to the wizard. For any other, Continue leads to the second warning
/// (PullRequestConfirmViewModel)
public sealed class PullRequestWarningViewModel : ViewModelBase
{
    readonly MainViewModel main;

    public PullRequestWarningViewModel(MainViewModel main, PullRequestBuild build)
    {
        this.main = main;
        Build = build;
        CancelCommand = new Command(() => main.Modal = null);
        ReadCommand = new Command(() => main.Host?.Open(build.Pr.Page));
        GoCommand = new Command(() =>
        {
            if (build.Pr.Trusted) PullRequestConfirmViewModel.Start(main, build);
            else main.Modal = new PullRequestConfirmViewModel(main, build);
        });
    }

    public PullRequestBuild Build { get; }
    PullRequest Pr => Build.Pr;

    public string Title => Tr.F("Build pull request #{0}?", Pr.Number);
    public string PrTitle => Pr.Title.Length > 0 ? Pr.Title : Tr.F("Pull request #{0}", Pr.Number);
    /// "by mooki · at 1a2b3c4d"
    public string PrMeta => Pr.Author.Length > 0 ? Tr.F("by {0} · at {1}", Pr.Author, Pr.Head[..8]) : Tr.F("at {0}", Pr.Head[..8]);

    public bool Trusted => Pr.Trusted;
    /// A work in progress (WIP: a draft, or a WIP: title): built all the same, once the player read that it may not build or run
    public bool IsDraft => Pr.Draft;
    /// Why it is trusted: a BigBean wrote it, or approved this push for the Test Farm
    public string TrustText =>
        Pr.Approved && Pr.ApprovedBy.Count > 0 ? Tr.F("A BigBean approved this push for the Test Farm ({0}).", string.Join(", ", Pr.ApprovedBy))
        : Pr.Approved ? Tr.T("A BigBean approved this push for the Test Farm.")
        : Tr.F("{0} is a BigBean, one of the people who review Longshot's changes.", Pr.Author);

    /// What it replaces: another pull request's build, or an older push of this one, in its folder
    public string? ReplacesText => Build.Replaces is { } r
        ? Tr.F("It is built in place of {0}, in its folder.", r.Version is { } v ? Versions.Shown(v) : r.Name) : null;
    public bool HasReplaces => ReplacesText != null;

    public string GoText => Pr.Trusted ? Tr.T("Build it") : Tr.T("Continue");

    public Command CancelCommand { get; }
    public Command ReadCommand { get; }
    public Command GoCommand { get; }
}

/// The second warning, for a pull request no BigBean wrote or approved: its code has not been looked at by anyone who reviews
/// Longshot, and it runs on this PC with the player's rights. Cancel is the safe default; Build it anyway starts the wizard
public sealed class PullRequestConfirmViewModel : ViewModelBase
{
    readonly MainViewModel main;

    public PullRequestConfirmViewModel(MainViewModel main, PullRequestBuild build)
    {
        this.main = main;
        Build = build;
        CancelCommand = new Command(() => main.Modal = null);
        ReadCommand = new Command(() => main.Host?.Open(build.Pr.Page));
        GoCommand = new Command(() => Start(main, build));
    }

    public PullRequestBuild Build { get; }

    public string Title => Tr.F("Nobody has approved #{0}", Build.Pr.Number);
    public string Sub => Build.Pr.Author.Length > 0
        ? Tr.F("{0} is not a BigBean, and no BigBean has approved this push for the Test Farm.", Build.Pr.Author)
        : Tr.T("No BigBean wrote it, and none has approved this push for the Test Farm.");

    public Command CancelCommand { get; }
    public Command ReadCommand { get; }
    public Command GoCommand { get; }

    /// The wizard for the build the player agreed to
    public static void Start(MainViewModel main, PullRequestBuild build)
    {
        main.Modal = null;
        LauncherLog.Add($"pull request #{build.Pr.Number} at {build.Pr.Head}: the player agreed to build it " +
                        $"({(build.Pr.Approved ? "approved" : build.Pr.AuthorBigBean ? "by a BigBean" : "not approved")})");
        main.StartWizard(build.Kind, Versions.PullRequest, build.Pr.Tag, build.Replaces);
    }
}

/// Find a pull request: its number or its link, as copied from the browser (PullRequests.NumberOf), looked up in Beanstalk's list
/// of open ones. Found, it is listed on the Pull request track and picked; it is built only after the warnings
public sealed class PullRequestFindViewModel : ViewModelBase
{
    readonly MainViewModel main;
    string text = "";
    string? problem;

    public PullRequestFindViewModel(MainViewModel main)
    {
        this.main = main;
        CancelCommand = new Command(() => main.Modal = null);
        FindCommand = new BusyCommand(FindAsync, () => PullRequests.NumberOf(text) != null);
        FindCommand.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BusyCommand.IsBusy)) Raise(nameof(Loading)); };
    }

    public string Text
    {
        get => text;
        set
        {
            if (!Set(ref text, value ?? "")) return;
            Problem = null;
            FindCommand.Changed();
        }
    }

    public string? Problem
    {
        get => problem;
        private set
        {
            if (Set(ref problem, value)) Raise(nameof(HasProblem));
        }
    }

    public bool HasProblem => problem != null;
    public bool Loading => FindCommand.IsBusy;

    public Command CancelCommand { get; }
    public BusyCommand FindCommand { get; }

    async Task FindAsync(BusyCommand busy, CancellationToken cancel)
    {
        if (PullRequests.NumberOf(text) is not { } number) return;
        busy.Report(Tr.F("Looking for #{0}…", number));
        Problem = null;
        var list = await main.Service.PullRequestsAsync(fresh: true, cancel);
        if (list == null)
        {
            Problem = Tr.T("Beanstalk did not answer, so the pull requests are not known. Try again in a moment.");
            return;
        }
        if (list.FirstOrDefault(p => p.Number == number) is not { } pr)
        {
            Problem = Tr.F("#{0} is not an open pull request of Longshot.", number);
            return;
        }
        if (PullRequests.Problem(pr, main.Platform) is { } why)
        {
            Problem = why;
            return;
        }
        main.Service.AddFound(number);
        main.Modal = null;
        main.Home.PickPullRequest(pr);
    }
}
