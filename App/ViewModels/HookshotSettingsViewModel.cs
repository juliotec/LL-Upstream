using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

/// Hookshot's compatible settings copied into one installed Longshot version, separately from saves.
public sealed class HookshotSettingsViewModel : ViewModelBase
{
    string? sourceFolder;
    BuildEntry? target;
    HookshotSettingsCopy? preview;
    IReadOnlyList<string> files = Array.Empty<string>();
    string? problem;
    bool busy;
    readonly bool severalSources;

    public HookshotSettingsViewModel(MainViewModel main)
    {
        Main = main;
        BackCommand = new Command(() => Main.Modal = new HookshotImportViewModel(Main));
        CloseCommand = new Command(() => Main.Modal = null);
        PickFolderCommand = new Command(() => _ = PickFolderAsync());
        ImportCommand = new Command(Import, () => CanImport);
        Targets = main.Service.Registry.Builds.Where(main.Service.IsBuilt).ToList();
        // (no version chosen for the player: the import changes the one they pick, and Import waits for that choice)
        target = null;
        var roots = HookshotSaves.Roots(main.Service.SaveEnv)
            .Where(r => File.Exists(Path.Combine(r, "hookshot_options.ini")) || File.Exists(Path.Combine(r, "language.txt")))
            .ToList();
        severalSources = roots.Count > 1;
        sourceFolder = roots.Count == 1 ? roots[0] : null;
        Refresh();
    }

    public MainViewModel Main { get; }
    public IReadOnlyList<BuildEntry> Targets { get; }
    public bool HasTargets => Targets.Count > 0;
    public bool NoTargets => !HasTargets;
    public string SourcePath => ShownPath.Of(sourceFolder);
    public bool HasSource => sourceFolder != null;
    public string SourceHint => severalSources
        ? "Several Hookshot folders have settings. Choose the game folder you want to import from."
        : "Choose your Hookshot game folder, which holds hookshot_options.ini or language.txt.";
    public string TargetPath => ShownPath.Of(Target?.Folder);
    public bool HasTarget => Target != null;
    public bool NeedsTarget => HasTargets && !HasTarget;
    public string FilesText => files.Count > 0 ? "Files to import: " + string.Join(", ", files) + "." : "";
    public bool HasFiles => files.Count > 0;
    public string BackupText => preview?.Backup is { } backup
        ? "Existing files will be backed up in " + ShownPath.Of(backup) + "."
        : "There are no matching Longshot files to back up.";
    public bool HasPreview => preview != null;
    public string SkippedText => preview is { SkippedKeys.Count: > 0 } copy
        ? $"{copy.SkippedKeys.Count} Hookshot options are unknown to Longshot or have a value it does not take, and will be skipped: " +
          string.Join(", ", copy.SkippedKeys) + "."
        : "";
    public bool HasSkipped => preview?.SkippedKeys.Count > 0;
    /// (make_game.py writes the launcher's game language, when one is set, into language.txt at each update: the imported one
    /// becomes the launcher's too, or the next update would undo it)
    string? LanguageChange => preview?.Language is { } imported && Languages.Normalize(Main.Service.Settings.Language) is { } current
                              && current != imported ? imported : null;
    public string LanguageText => LanguageChange is { } language
        ? $"The launcher's game language also becomes {Languages.NameOf(language)}, so updating this version keeps it. Other versions take it when they are next updated."
        : "";
    public bool HasLanguageText => LanguageChange != null;
    public string? Problem => problem;
    public bool HasProblem => problem != null;
    public bool CanImport => !busy && preview != null;

    public BuildEntry? Target
    {
        get => target;
        set
        {
            if (Set(ref target, value)) Refresh();
        }
    }

    public Command BackCommand { get; }
    public Command CloseCommand { get; }
    public Command PickFolderCommand { get; }
    public Command ImportCommand { get; }

    async Task PickFolderAsync()
    {
        if (Main.Host is not { } host) return;
        var folder = await host.PickFolderAsync("Your Hookshot game folder", sourceFolder);
        if (folder == null) return;
        sourceFolder = folder;
        Refresh();
    }

    void Refresh()
    {
        problem = null;
        preview = null;
        files = Array.Empty<string>();
        try
        {
            if (sourceFolder != null)
            {
                files = HookshotSettings.FilesIn(sourceFolder);
                if (Target != null)
                    preview = HookshotSettings.Import(sourceFolder, Target.Folder, Main.Service.SaveEnv.BackupRoot, overwrite: false, dryRun: true);
            }
        }
        catch (Exception e) when (e is SaveFailure or IOException or UnauthorizedAccessException) { problem = e.Message; }
        Changed();
    }

    void Import()
    {
        if (!CanImport || sourceFolder == null || Target == null) return;
        busy = true;
        problem = null;
        Changed();
        try
        {
            if (!Main.Service.IsBuilt(Target)) throw new SaveFailure("This Longshot version is no longer built. Build it before importing settings.");
            var language = LanguageChange;
            var done = HookshotSettings.Import(sourceFolder, Target.Folder, Main.Service.SaveEnv.BackupRoot, overwrite: true);
            if (language != null && done.Language == language)
            {
                Main.Service.Settings.Language = language;
                try { Main.Service.SaveSettings(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            Main.Modal = null;
            Main.ShowToast("Imported Hookshot settings into " + Target.Name +
                           (done.Backup != null ? ". The earlier files are in " + ShownPath.Of(done.Backup) + "." : "."));
            Main.Home.Refresh();
        }
        catch (Exception e) when (e is SaveFailure or IOException or UnauthorizedAccessException)
        {
            problem = e is SaveFailure { Backup: { } backup } ? e.Message + " Backup: " + ShownPath.Of(backup) + "." : e.Message;
        }
        finally
        {
            busy = false;
            Changed();
        }
    }

    void Changed()
    {
        RaiseAll(nameof(SourcePath), nameof(HasSource), nameof(TargetPath), nameof(HasTarget), nameof(NeedsTarget), nameof(FilesText), nameof(HasFiles),
                 nameof(BackupText), nameof(HasPreview), nameof(SkippedText), nameof(HasSkipped), nameof(LanguageText), nameof(HasLanguageText), nameof(Problem), nameof(HasProblem), nameof(CanImport));
        ImportCommand.Changed();
    }
}
