using System.Text.RegularExpressions;
using LongshotLauncher.Core;

/// The PC kept from sleeping while a build runs (Core/KeepAwake.cs): holds counted, the platform's hold taken once and let go once,
/// a failing hold never stopping the build; and the build page holding it while its build runs, let go however it ends
static partial class Program
{
    sealed class FakeHold : IDisposable
    {
        public int Released;
        public void Dispose() => Released++;
    }

    static void KeepAwakeChecks(string repo)
    {
        var before = KeepAwake.Backend;
        try
        {
            var taken = new List<FakeHold>();
            KeepAwake.Backend = _ => { var h = new FakeHold(); taken.Add(h); return h; };
            var a = KeepAwake.Hold("Building Longshot");
            var b = KeepAwake.Hold("Building Longshot");
            Require(taken.Count == 1 && KeepAwake.Holds == 2, "Two holds take the platform's hold twice.");
            a.Dispose();
            a.Dispose();
            Require(taken[0].Released == 0 && KeepAwake.Holds == 1, "The PC is let go while a hold remains, or a hold let go twice counts twice.");
            b.Dispose();
            Require(taken[0].Released == 1 && KeepAwake.Holds == 0, "The last hold let go does not let the PC sleep again.");
            KeepAwake.Hold("again").Dispose();
            Require(taken.Count == 2 && taken[1].Released == 1, "A hold after the last one let go is not taken again.");

            KeepAwake.Backend = _ => throw new IOException("no inhibitor");
            var failing = KeepAwake.Hold("Building Longshot");
            failing.Dispose();
            Require(KeepAwake.Holds == 0, "A platform that cannot hold the PC awake stops the build, or leaves a hold counted.");
            KeepAwake.Backend = _ => null;
            KeepAwake.Hold("nothing to hold").Dispose();
            Require(KeepAwake.Holds == 0, "A platform with nothing to hold leaves a hold counted.");
        }
        finally { KeepAwake.Backend = before; }

        // (the build page: every change of its running state goes through SetRunning, which holds the PC and lets it go)
        var build = File.ReadAllText(Path.Combine(repo, "App", "ViewModels", "BuildStepViewModel.cs"));
        Require(!Regex.IsMatch(build, @"\brunning\s*=\s*(true|false)\s*;"), "The build's running state changes without SetRunning, so the PC may sleep or stay awake.");
        Require(Regex.IsMatch(build, @"void SetRunning\(bool on\)[\s\S]*?KeepAwake\.Hold\([\s\S]*?\.Dispose\(\)"),
                "SetRunning does not hold the PC awake while a build runs and let it go after.");
        Require(Regex.Matches(build, @"SetRunning\(true\)").Count >= 1 && Regex.Matches(build, @"SetRunning\(false\)").Count >= 4,
                "A way a build ends (finished, failed, stopped, cancelled) no longer lets the PC go.");
    }
}
