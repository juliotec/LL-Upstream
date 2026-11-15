using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

/// A problem in plain words (launcher/Core/Problems.cs): what happened, and the fix there is (the graphics driver's page for
/// the card's maker)
public sealed class ProblemPageViewModel : ViewModelBase
{
    readonly Action back;

    public ProblemPageViewModel(MainViewModel main, Problem problem, Action back, bool crashed)
    {
        this.back = back;
        Problem = problem;
        Crashed = crashed;
        Fix = problem.Fixes.FirstOrDefault(f => f.Kind == FixKind.DriverPage);
        FixCommand = new Command(() => { if (Fix?.Link is { } link) main.Host?.Open(link); }, () => Fix?.Link != null);
        CloseCommand = new Command(Close);
        // (the problem in English, as Problems names it, and its signature: what identifies the same crash on another PC)
        CopyCommand = main.CopyReportCommand(() => (crashed ? "the game closed unexpectedly: " : "") + problem.Headline,
                                             () => new[] { $"problem: {problem.Kind}", $"signature: {problem.Signature ?? "none"}", "said: " + problem.Detail });
    }

    public Problem Problem { get; }
    public bool Crashed { get; }
    public Fix? Fix { get; }
    public string Headline => Tr.T(Problem.Headline);
    public string Detail => Tr.T(Problem.Detail);
    public string FixLabel => Fix is { } fix ? Tr.T(fix.Label) : "";
    public string Sub => Crashed ? Tr.T("This was the last time you played. Your saves are not touched.") : Tr.T("Your other versions and your saves are not touched.");

    public Command FixCommand { get; }
    public Command CloseCommand { get; }
    public Command CopyCommand { get; }

    void Close() => back();
}
