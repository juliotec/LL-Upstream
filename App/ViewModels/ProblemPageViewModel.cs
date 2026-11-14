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
    }

    public Problem Problem { get; }
    public bool Crashed { get; }
    public Fix? Fix { get; }
    public string Headline => Problem.Headline;
    public string Detail => Problem.Detail;
    public string Sub => Crashed ? "This was the last time you played. Your saves are not touched." : "Your other versions and your saves are not touched.";

    public Command FixCommand { get; }
    public Command CloseCommand { get; }

    void Close() => back();
}
