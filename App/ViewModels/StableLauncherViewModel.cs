using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

/// The offer to replace this (nightly) launcher with the stable Longshot Launcher (Core/StableSwitch.cs): asked when the player
/// switches the launcher's channel from nightly to stable in Settings, and from an old version's "Switch to the stable launcher"
/// (OldVersions). It says that the versions built with longshot-builder won't run in the stable launcher, and offers to copy the
/// current profile's saves back to the --user folder the stable launcher reads (SavesCopyBack: a copy, newer files there kept
/// unless the player ticks them too). Replace copies them, sets the channel and puts the newest stable launcher in place, as the
/// launcher's own update does; Cancel changes nothing
public sealed class StableLauncherViewModel : ViewModelBase
{
    bool copySaves = true, replaceNewer;
    readonly bool canReplace;

    /// canReplace: whether this launcher can replace itself (LauncherService.CanReplaceItself; the screenshots' stand-in)
    public StableLauncherViewModel(MainViewModel main, bool? canReplace = null)
    {
        Main = main;
        this.canReplace = canReplace ?? LauncherService.CanReplaceItself;
        Plan = main.Service.StableSavesPlan();
        CancelCommand = new Command(Cancel, () => !ReplaceCommand!.IsBusy);
        ReplaceCommand = new BusyCommand(ReplaceAsync, () => this.canReplace,
                                         e => LauncherLog.Add("stable launcher: not put in place: " + e.Message));
        ReplaceCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BusyCommand.IsBusy)) { CancelCommand.Changed(); Raise(nameof(Asking)); }
        };
    }

    public MainViewModel Main { get; }
    /// What copying the saves back would do, worked out when the offer opened
    public SavesCopyBack Plan { get; }
    string Profile => LauncherProfilesText.Name(Main.Service.Profile);

    public string Title => Tr.T("Replace this launcher with the stable Longshot Launcher");
    public string Intro => Tr.T("The newest stable launcher is downloaded, checked and started in this one's place, as an update is.");
    public string Warning => Tr.T("Versions built with the new builder won't run in the stable launcher. It plays the versions built by Longshot's old builder; the new ones stay on this PC for when you come back to the nightly.");

    /// The saves: where the stable launcher reads them, and the tick to copy the profile's there
    public string SavesText => Tr.F("The stable launcher reads saves from {0}, not from this launcher's profiles. Copied there, {1}'s saves stay in the profile too.",
                                    ShownPath.Of(Plan.To), Profile);
    public bool HasSaves => !Plan.Empty;
    public string SavesNothing => Tr.T("Nothing to copy: that folder has these saves already.");
    public bool ShowNothing => Plan.Empty;
    public string CopyLabel => Tr.F("Copy {0}'s saves there", Profile);
    public bool CopySaves
    {
        get => copySaves;
        set { if (Set(ref copySaves, value)) Raise(nameof(ShowNewer)); }
    }
    public string CopySummary => Tr.P(Plan.New.Count + Plan.Older.Count, "1 file to copy.", "{0} files to copy.");
    /// Files there newer than the profile's: kept unless ticked
    public bool ShowNewer => copySaves && Plan.Newer.Count > 0;
    public string NewerText => Tr.P(Plan.Newer.Count, "1 file there is newer than {1}'s: it is kept unless you tick this.",
                                    "{0} files there are newer than {1}'s: they are kept unless you tick this.", Profile);
    public string ReplaceNewerLabel => Tr.T("Replace the newer files too");
    public bool ReplaceNewer { get => replaceNewer; set => Set(ref replaceNewer, value); }

    /// A launcher that can't replace itself (a Flatpak's, or one not started from its file) says why instead
    public bool CannotReplace => !canReplace;
    public string CannotReplaceText => Tr.T("This launcher can't replace itself here: download the stable Longshot Launcher from longshot.magicbean.dev instead.");

    public bool Asking => !ReplaceCommand.IsBusy;
    public Command CancelCommand { get; }
    public BusyCommand ReplaceCommand { get; }

    /// Nothing changed: the channel, the saves and this launcher as they were
    void Cancel()
    {
        LauncherLog.Add("stable launcher: not now");
        Main.Modal = null;
    }

    async Task ReplaceAsync(BusyCommand busy, CancellationToken cancel)
    {
        var plan = CopySaves && HasSaves ? Plan : null;
        var newer = ReplaceNewer;
        busy.Report(plan != null ? Tr.T("Copying the saves…") : Tr.T("Downloading the new launcher…"), canCancel: false);
        await Task.Run(() => Main.Service.SwitchToStableAsync(plan, newer,
            p => Ui.Post(() => busy.Report(p >= 1 ? Tr.T("Checking the download…") : Tr.F("Downloading the new launcher… {0}%", (int)Math.Round(p * 100)), p, canCancel: p < 1)),
            cancel,
            step => Ui.Post(() => busy.Report(step, canCancel: false))), CancellationToken.None);
        Main.Host?.Close();
    }
}
