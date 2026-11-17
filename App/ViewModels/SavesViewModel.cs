using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

public enum SaveMode { Import, Export, Restore }

/// A row of the Saves page's list: a group's title, a build's saves, or an emulator's
public sealed class SaveRowViewModel
{
    public bool IsGroup { get; init; }
    public bool IsRow => !IsGroup;
    public string Name { get; init; } = "";
    public string Where { get; init; } = "";
    public string Summary { get; init; } = "";
    public string? Channel { get; init; }
    public bool IsRelease => Channel == Versions.Stable;
    public bool IsCandidate => Channel == Versions.Candidate;
    public bool IsNightly => Channel == Versions.Nightly;
    public bool HasChannel => Channel != null;
    public bool IsThis { get; init; }
    public bool Shared { get; init; }
    public bool Dim { get; init; }
    public string? Profiles { get; init; }
    public bool HasProfiles => Profiles != null;
    public bool Waiting { get; init; }
    public bool First { get; init; }
    public bool AfterGroup { get; set; }
    public Avalonia.Thickness Line => AfterGroup ? default : new Avalonia.Thickness(0, 1, 0, 0);
}

/// A save slot of the build on the right
public sealed record SlotViewModel(int Number, string Name, string Saved);

/// A slot of the Saves page: its number and when it was saved, and the button that opens its progress (the save editor), off with
/// the reason when there is no editor for the default build
public sealed class SlotRowViewModel
{
    public SlotRowViewModel(SlotViewModel slot, Command progress, string? why)
    {
        Slot = slot;
        ProgressCommand = progress;
        Why = why;
    }

    public SlotViewModel Slot { get; }
    public int Number => Slot.Number;
    public string Name => Slot.Name;
    public string Saved => Slot.Saved;
    public Command ProgressCommand { get; }

    /// Why the button is off, for its tooltip (null: it is on)
    public string? Why { get; }
}

static class SaveViews
{
    /// A long path shortened as the mockup does: its first part, …, its last two
    public static string Short(string p)
    {
        p = ShownPath.Of(p);
        if (p.Length <= 34) return p;
        var sep = p.Contains('/') ? '/' : '\\';
        var parts = p.Split(sep);
        return parts.Length <= 3 ? p : parts[0] + sep + "…" + sep + string.Join(sep, parts[^2..]);
    }

    public static IReadOnlyList<SlotViewModel> Slots(IReadOnlyList<SaveSlot> slots) =>
        slots.Select(s => new SlotViewModel(SaveText.Number(s.Name), Tr.F("Slot {0}", SaveText.Number(s.Name)),
                                            s.Saved is { } t ? Durations.When(t, DateTime.Now) : "")).ToList();

    public static IReadOnlyList<SaveSlot> Describe(SaveProfile? p) => p == null ? Array.Empty<SaveSlot>() : Saves.Describe(p.Path);

    public static SlotViewModel Slot(SaveSlot s) =>
        new(SaveText.Number(s.Name), Tr.F("Slot {0}", SaveText.Number(s.Name)), s.Saved is { } t ? Durations.When(t, DateTime.Now) : "");
}

/// Saves: Longshot's slots, which every build shares, every other save found on this PC (the emulators'), and the copies: import
/// from and export to an emulator or another folder. Each copy is confirmed first and backs up what it replaces
public sealed class SavesViewModel : ViewModelBase
{
    public SavesViewModel(MainViewModel main)
    {
        Main = main;
        ImportCommand = new Command(() => Main.Modal = new SavePickViewModel(this, SaveMode.Import));
        ExportCommand = new Command(() => Main.Modal = new SavePickViewModel(this, SaveMode.Export), () => Slots.Count > 0);
        HookshotCommand = new Command(() => Main.Modal = new HookshotImportViewModel(Main));
        BackupsCommand = new Command(() => Main.Navigate(new BackupsViewModel(this)), () => Backups.Count > 0);
        DoneCommand = new Command(Main.ShowHome);
        OpenFolderCommand = new Command(OpenFolder);
        Reload();
    }

    public MainViewModel Main { get; }
    public Command HookshotCommand { get; }
    LauncherService Service => Main.Service;
    public SaveEnvironment Env => Service.SaveEnv;
    public IReadOnlyList<SaveRowViewModel> Rows { get; private set; } = Array.Empty<SaveRowViewModel>();
    public IReadOnlyList<SlotViewModel> Slots { get; private set; } = Array.Empty<SlotViewModel>();
    public IReadOnlyList<SlotRowViewModel> SlotRows { get; private set; } = Array.Empty<SlotRowViewModel>();
    /// Why there is no save editor for the slots (null: there is one, or no slot)
    public string? EditorNote { get; private set; }
    public bool HasEditorNote => EditorNote != null;
    public IReadOnlyList<SaveSet> Emulators { get; private set; } = Array.Empty<SaveSet>();
    public bool HasSlots => Slots.Count > 0;
    public bool NoSlots => Slots.Count == 0;

    public string Sub => Tr.T("All your Longshot versions share these saves.");
    public string EmptyHint => Tr.T("Import saves from an emulator or Hookshot. They will be available to every Longshot version.");
    public string SlotCount => Tr.P(Slots.Count, "1 save slot", "{0} save slots");
    public IReadOnlyList<SaveRowViewModel> OtherSaves => Rows.Where(r => r.IsRow && !r.IsThis && !r.Dim).ToList();
    public bool HasOtherSaves => OtherSaves.Count > 0;
    public string OtherSavesLabel => Tr.F("Other save locations ({0})", OtherSaves.Count);
    public string ExportHint => HasSlots ? Tr.T("Send to an emulator, a folder or a .zip") : Tr.T("Play or import saves to enable export");
    public Command ImportCommand { get; }
    public Command ExportCommand { get; }
    public Command DoneCommand { get; }
    public Command BackupsCommand { get; }
    public Command OpenFolderCommand { get; }
    public string SaveFolderShown => ShownPath.Of(Env.SaveFolder);

    /// Longshot's save folder in the system's file manager, to find the saves by hand: made first when there is none yet, as an
    /// import makes it (an empty save folder is what the game makes too)
    void OpenFolder()
    {
        try { Directory.CreateDirectory(Env.SaveFolder); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            Main.ShowToast(Tr.F("The saves folder could not be made: {0}", x.Message));
            return;
        }
        Main.Host?.Open(Env.SaveFolder);
    }

    /// Every save in the backups folder, newest first (SaveBackups.List)
    public IReadOnlyList<BackupSave> Backups { get; private set; } = Array.Empty<BackupSave>();

    public string BackupsHint => Backups.Count == 0 ? Tr.T("None yet: every copy makes one first")
        : Tr.P(Backups.Count, "1 backup, made {1}", "{0} backups, the newest {1}", Durations.When(Backups[0].When, DateTime.Now));

    public void Reload()
    {
        var now = DateTime.Now;
        var rows = new List<SaveRowViewModel> { new() { IsGroup = true, Name = "Longshot", First = true } };
        var set = Saves.Shared(Env);
        var slots = Service.SaveSlots();
        rows.Add(new SaveRowViewModel
        {
            Name = Tr.T("Every build"), Shared = true, IsThis = true,
            Where = SaveViews.Short(set.Found ? set.Profiles[0].Path : Env.SaveFolder),
            Summary = SaveText.Long(slots, now),
        });
        rows.Add(new SaveRowViewModel { IsGroup = true, Name = Tr.T("Emulators and ports") });
        Emulators = Saves.Emulator(Env);
        foreach (var e in Emulators)
        {
            if (!e.Found)
            {
                rows.Add(new SaveRowViewModel { Name = e.Display.Replace(" / ", ", "), Where = Tr.T("Not found on this PC"), Dim = true });
                continue;
            }
            rows.Add(new SaveRowViewModel
            {
                Name = e.Display.Replace(" / ", ", "),
                Where = SaveViews.Short(e.Folder ?? ""),
                Profiles = e.Profiles.Count > 1 ? Tr.P(e.Profiles.Count, "1 profile", "{0} profiles") : null,
                Waiting = e.Profiles.Any(p => Saves.PendingCommit(p.Path) != null),
                Summary = SaveText.Long(SaveViews.Describe(e.Profiles[0]), now),
            });
        }
        for (var i = 1; i < rows.Count; i++) rows[i].AfterGroup = rows[i - 1].IsGroup;
        Rows = rows;
        Slots = SaveViews.Slots(slots);
        var (editing, why) = Service.SaveEditor();
        var note = editing == null ? SaveEditing.Say(why) : null;
        SlotRows = slots.Select(s => new SlotRowViewModel(SaveViews.Slot(s),
            new Command(() => Main.Navigate(new SaveEditorViewModel(this, editing!, s.Name)), () => editing != null && s.Saved != null),
            note ?? (s.Saved == null ? Tr.T("This slot has no progress.sav yet.") : null))).ToList();
        EditorNote = slots.Count > 0 ? note : null;
        Backups = SaveBackups.List(Env);
        BackupsCommand.Changed();
        RaiseAll(nameof(Backups), nameof(BackupsHint), nameof(Rows), nameof(Slots), nameof(HasSlots), nameof(NoSlots), nameof(SlotCount), nameof(OtherSaves), nameof(HasOtherSaves),
                 nameof(OtherSavesLabel), nameof(ExportHint), nameof(SlotRows), nameof(EditorNote), nameof(HasEditorNote));
        ExportCommand.Changed();
    }
}

/// One side of a copy: Longshot's save, an emulator's profile, or a folder
public sealed record SaveSide(string Name, string Where, IReadOnlyList<SaveSlot> Slots, string? Path, bool Emulator);

/// The copy being set up: which way, from or to what
public sealed class SaveOp
{
    public SaveMode Mode { get; init; }
    public SaveSet? Set { get; set; }
    public string? Folder { get; set; }
    public string? Profile { get; set; }
    public bool KeepSettings { get; set; }
    /// A .zip file instead of an emulator or a folder: the one to import, or the one to write (SaveArchive)
    public string? Zip { get; set; }
    /// The .zip being imported, unpacked into a temporary folder of its own (deleted once the copy is done or left)
    public UnpackedSaves? Unpacked { get; set; }
    /// The backup a restore puts back (SaveMode.Restore)
    public BackupSave? Backup { get; init; }
    /// Into Longshot's save: an import or a restore (an export goes out of it)
    public bool IntoLongshot => Mode != SaveMode.Export;
    public string Verb => Mode switch { SaveMode.Import => Tr.T("Import"), SaveMode.Restore => Tr.T("Restore"), _ => Tr.T("Export") };
    public SaveProgram Program => Backup != null ? SaveBackups.Program : Zip != null ? Saves.Zip : Set != null ? Saves.Named(Set.Owner) : Saves.Folder;
    public string OtherName => Backup != null ? BackupsViewModel.What(Backup) : Zip != null ? Path.GetFileName(Zip) : Set != null ? Set.Display.Replace(" / ", ", ") : Tr.T("the folder");

    /// The .zip's temporary folder, deleted
    public void Forget()
    {
        Unpacked?.Dispose();
        Unpacked = null;
    }
}

/// Import or export: which emulator (or another folder), and which profile when it has several
public sealed class SavePickViewModel : ViewModelBase
{
    public sealed class Option(SavePickViewModel owner, string key, string name, string about, string right, bool enabled, string? channel = null,
                               bool waiting = false) : ViewModelBase
    {
        public string Key => key;
        public string Name => name;
        public string About => about;
        public bool HasAbout => about.Length > 0;
        public string Right => right;
        public bool Enabled => enabled;
        public bool Dim => !enabled;
        public string? Channel => channel;
        public bool HasChannel => channel != null;
        public bool IsRelease => channel == Versions.Stable;
        public bool IsCandidate => channel == Versions.Candidate;
        public bool IsNightly => channel == Versions.Nightly;
        public bool Waiting => waiting;

        public bool IsPicked
        {
            get => owner.Pick == key;
            set
            {
                if (value) owner.Pick = key;
            }
        }

        public void Changed() => Raise(nameof(IsPicked));
    }

    public sealed class ProfileOption(SavePickViewModel owner, SaveProfile p, string summary) : ViewModelBase
    {
        public string Id => p.Id;
        public string Name => Tr.F("Profile {0}", p.Id.Length > 16 ? p.Id[..8] + "…" + p.Id[^4..] : p.Id);
        public string Summary => summary;

        public bool IsPicked
        {
            get => owner.Profile == p.Id;
            set
            {
                if (value) owner.Profile = p.Id;
            }
        }

        public void Changed() => Raise(nameof(IsPicked));
    }

    string? pick, profile;
    IReadOnlyList<ProfileOption> profiles = Array.Empty<ProfileOption>();

    public SavePickViewModel(SavesViewModel page, SaveMode mode)
    {
        Page = page;
        Mode = mode;
        var now = DateTime.Now;
        var main = page.Main;
        Options = page.Emulators.Select(e => new Option(this, e.Owner, e.Display.Replace(" / ", ", "), e.Found ? "" : Tr.T("Not found on this PC"),
                !e.Found ? "" : e.Profiles.Count > 1 ? Tr.P(e.Profiles.Count, "1 profile", "{0} profiles") : SaveText.Long(SaveViews.Describe(e.Profiles[0]), now), e.Found,
                waiting: e.Profiles.Any(p => Saves.PendingCommit(p.Path) != null)))
            .Append(new Option(this, "folder", Tr.T("Another folder…"),
                               mode == SaveMode.Import ? Tr.T("A folder holding account.sav and slot folders, or one above it")
                                   : Tr.T("A portable emulator's user folder, or any folder"), "", true))
            .Append(new Option(this, "zip", Tr.T("A .zip file…"),
                               mode == SaveMode.Import ? Tr.T("A .zip of saves the launcher made, or of a save folder")
                                   : Tr.T("One file to keep or to send: the whole save folder, as it is"), "", true))
            .ToList();
        GoCommand = new Command(() => _ = GoAsync(), () => CanGo);
        Pick = Options.FirstOrDefault(o => o.Enabled)?.Key;
        CancelCommand = new Command(() => main.Modal = null);
    }

    public SavesViewModel Page { get; }
    public SaveMode Mode { get; }
    public IReadOnlyList<Option> Options { get; }
    public Command GoCommand { get; }
    public Command CancelCommand { get; }

    public string Title => Mode == SaveMode.Import ? Tr.T("Import from which emulator?") : Tr.T("Export to which emulator?");

    public string Sub => Mode == SaveMode.Export
        ? Tr.T("The saves every build uses replace the ones there, after a backup.")
        : Tr.T("Its saves replace the ones every build uses, after a backup.");

    SaveSet? PickedSet => Page.Emulators.FirstOrDefault(e => e.Owner == pick);

    public string? Pick
    {
        get => pick;
        set
        {
            if (!Set(ref pick, value)) return;
            foreach (var o in Options) o.Changed();
            var set = PickedSet;
            Profiles = set is { Profiles.Count: > 1 }
                ? set.Profiles.Select(p => new ProfileOption(this, p, SaveText.Long(Saves.Describe(p.Path), DateTime.Now))).ToList()
                : Array.Empty<ProfileOption>();
            profile = set is { Profiles.Count: 1 } ? set.Profiles[0].Id : null;
            RaiseAll(nameof(Profile), nameof(CanGo));
            GoCommand.Changed();
        }
    }

    public IReadOnlyList<ProfileOption> Profiles
    {
        get => profiles;
        private set
        {
            profiles = value;
            RaiseAll(nameof(Profiles), nameof(HasProfiles), nameof(ProfileHint));
        }
    }

    public bool HasProfiles => profiles.Count > 0;
    public string ProfileHint => PickedSet is { } s ? Tr.P(s.Profiles.Count, "{1} has saves in 1 profile. Pick the one you play on.",
                                                           "{1} has saves in {0} profiles. Pick the one you play on.", s.Display.Replace(" / ", ", ")) : "";

    public string? Profile
    {
        get => profile;
        set
        {
            if (!Set(ref profile, value)) return;
            foreach (var p in profiles) p.Changed();
            Raise(nameof(CanGo));
            GoCommand.Changed();
        }
    }

    public bool CanGo => pick != null && (PickedSet is not { Profiles.Count: > 1 } || profile != null);

    async Task GoAsync()
    {
        var main = Page.Main;
        var op = new SaveOp { Mode = Mode };
        if (pick == "zip")
        {
            if (main.Host is not { } host) return;
            var zip = Mode == SaveMode.Import
                ? await host.PickFileAsync(Tr.T("The .zip file with the saves"), new[] { "*.zip" })
                : await host.PickSaveFileAsync(Tr.T("Save the saves as"), SaveArchive.DefaultName(DateTime.Now), new[] { "*.zip" });
            if (zip == null) return;
            op.Zip = zip;
        }
        else if (pick == "folder")
        {
            if (main.Host is not { } host) return;
            var folder = await host.PickFolderAsync(Mode == SaveMode.Import ? Tr.T("The folder with the saves") : Tr.T("The folder to export to"), null);
            if (folder == null) return;
            op.Folder = folder;
        }
        else
        {
            op.Set = PickedSet;
            op.Folder = PickedSet?.Folder;
            op.Profile = profile;
        }
        main.Modal = null;
        main.Navigate(new SavesConfirmViewModel(Page, op));
    }
}

/// What a copy replaces, kept settings (option.sav), where the backup goes; nothing changes until the verb is pressed
public sealed class SavesConfirmViewModel : ViewModelBase
{
    public SavesConfirmViewModel(SavesViewModel page, SaveOp op)
    {
        Page = page;
        Op = op;
        BackCommand = new Command(() =>
        {
            op.Forget();
            // (a restore came from the backups list: back to it)
            page.Main.Navigate(op.Backup != null ? new BackupsViewModel(page) : page);
        });
        RunCommand = new Command(Run, () => Problem == null && From.Path != null && To.Path != null);
        Refresh();
    }

    public SavesViewModel Page { get; }
    public SaveOp Op { get; }
    LauncherService Service => Page.Main.Service;
    public SaveSide From { get; private set; } = null!;
    public SaveSide To { get; private set; } = null!;
    public IReadOnlyList<SlotViewModel> FromSlots => SaveViews.Slots(From.Slots);
    public IReadOnlyList<SlotViewModel> ToSlots => SaveViews.Slots(To.Slots);
    public bool FromEmpty => From.Slots.Count == 0;
    public bool ToEmpty => To.Slots.Count == 0;
    public string FromWhere => SaveViews.Short(From.Where);
    public string ToWhere => SaveViews.Short(To.Where);
    public string? Backup { get; private set; }
    public string? Problem { get; private set; }

    public string Title => Op.Mode switch
    {
        SaveMode.Import => Tr.F("Import saves from {0}", From.Name),
        SaveMode.Restore => Op.Backup?.Kind == BackupKind.Export ? Tr.F("Restore {0}'s old save into Longshot", BackupsViewModel.OtherOf(Op.Backup))
                          : Tr.T("Restore saves from a backup"),
        _ => Tr.F("Export saves to {0}", To.Name),
    };

    public string Sub => Tr.F("Check what gets replaced. Nothing changes until you choose {0}.", Op.Verb);
    public string Verb => Op.Verb;

    /// An export into a .zip: nothing of a save is replaced, the file is written (a new one, or one the Save As asked to write over)
    public bool ZipExport => Op.Zip != null && Op.Mode == SaveMode.Export;
    public bool ToReplaced => !ZipExport || File.Exists(Op.Zip);

    /// An import of a whole save folder (SaveArchive's layout): it takes the place of Longshot's save folder, all of it
    public bool WholeImport => Op.Zip != null && Op.Mode == SaveMode.Import && Op.Unpacked is { Whole: true };

    public string Replaced => ZipExport
        ? Tr.F(File.Exists(Op.Zip) ? "{0} is written over with Longshot's whole save folder: every slot, the game's settings, and every other file in it."
                                   : "A new file, {0}, with Longshot's whole save folder: every slot, the game's settings, and every other file in it.", To.Name)
        : WholeImport
        ? Tr.F(Op.KeepSettings ? "Replaced: Longshot's whole save folder ({0}) and every file in it, its game settings (option.sav) kept."
                               : "Replaced: Longshot's whole save folder ({0}) and every file in it, its game settings (option.sav) too.",
               To.Slots.Count == 0 ? Tr.T("nothing saved yet") : SaveText.Long(To.Slots, DateTime.Now))
        : (FromHas("account.sav"), Op.KeepSettings || !FromHas("option.sav")) switch
        {
            (true, true) => Tr.F("Replaced in {0}: {1} and its account.sav. Its game settings (option.sav) stay as they are.", To.Name, ToSaves),
            (true, false) => Tr.F("Replaced in {0}: {1}, its account.sav, and its game settings (option.sav).", To.Name, ToSaves),
            (false, true) => Tr.F("Replaced in {0}: {1}. Its account.sav and its game settings (option.sav) stay as they are.", To.Name, ToSaves),
            (false, false) => Tr.F("Replaced in {0}: {1} and its game settings (option.sav). Its account.sav stays as it is.", To.Name, ToSaves),
        };

    /// What the destination holds now, as the replaced text says it
    string ToSaves => To.Slots.Count == 0 ? Tr.T("nothing saved yet") : SaveText.Long(To.Slots, DateTime.Now);

    /// Whether the source's save holds that file (a copy only replaces the files the source has; the others stay); true when
    /// it cannot be told yet (no source)
    bool FromHas(string file) => From.Path == null || File.Exists(Path.Combine(From.Path, file));

    public string KeepLabel => Tr.F("Keep {0}'s own game settings (option.sav)", To.Name);

    /// What the tick does, either way: the destination keeps its own settings, or the source's replace them
    public string KeepHint => Op.KeepSettings
        ? Tr.T("Its camera, text speed and other game options stay as they are.")
        : Op.Mode == SaveMode.Restore ? Tr.T("Camera, text speed and other game options from the backup replace them.")
        : Tr.F("Camera, text speed and other game options from {0} replace them.", From.Name);

    /// Whether there are game settings in the destination to keep at all: a folder with no option.sav has none, and a tick
    /// that "keeps" them would only leave the game on its defaults, so it is not shown; nor for a .zip written, which replaces none
    public bool KeepVisible => !ZipExport && To.Path != null && File.Exists(Path.Combine(To.Path, "option.sav")) && (WholeImport || FromHas("option.sav"));

    public bool KeepSettings
    {
        get => Op.KeepSettings;
        set
        {
            Op.KeepSettings = value;
            RaiseAll(nameof(KeepSettings), nameof(Replaced), nameof(KeepHint));
        }
    }

    public string BackupLine => ZipExport ? Tr.T("Nothing in your saves is changed.") : Backup != null ? Tr.F("Backup first, to {0}", ShownPath.Of(Backup)) : Problem ?? Tr.T("Nothing there yet to back up.");
    public string CloseFirst => Op.Set == null ? Tr.T("Close the game first.")
        : Op.Set.Display.Contains('/') ? Tr.T("Close the game and the emulator first.") : Tr.F("Close the game and {0} first.", Op.OtherName);

    public Command BackCommand { get; }
    public Command RunCommand { get; }

    /// Longshot's side: the save every build shares
    SaveSide LongshotSide()
    {
        var path = Op.IntoLongshot ? Saves.ImportTarget(Page.Env) : Saves.Pick(Saves.Shared(Page.Env).Profiles, "Longshot").Path;
        return new SaveSide("Longshot", path, Saves.Describe(path), path, false);
    }

    void Refresh()
    {
        Problem = null;
        SaveSide other;
        if (Op.Backup is { } backup)
            other = Directory.Exists(backup.Path)
                ? new SaveSide(BackupsViewModel.What(backup), backup.Path, Saves.Describe(backup.Path), backup.Path, false)
                : new SaveSide(BackupsViewModel.What(backup), backup.Path, Array.Empty<SaveSlot>(), null, false);
        else if (Op.Zip != null) other = ZipSide();
        else try
        {
            var (_, found) = Op.Mode == SaveMode.Export
                ? Saves.ExportTarget(Op.Program, Op.Folder, Page.Env)
                : Saves.Locate(Op.Program, Op.Folder, Page.Env);
            var p = Saves.Pick(found, Op.OtherName, Op.Profile);
            other = new SaveSide(Op.OtherName, p.Path + (found.Count > 1 ? Tr.F(" (profile {0}…)", p.Id[..Math.Min(8, p.Id.Length)]) : ""), Saves.Describe(p.Path), p.Path, true);
        }
        catch (SaveFailure e)
        {
            Problem = e.Message;
            other = new SaveSide(Op.OtherName, Op.Folder ?? "", Array.Empty<SaveSlot>(), null, true);
        }
        SaveSide me;
        try { me = LongshotSide(); }
        catch (SaveFailure e)
        {
            Problem = e.Message;
            me = new SaveSide("Longshot", Page.Env.SaveFolder, Array.Empty<SaveSlot>(), null, false);
        }
        (From, To) = Op.IntoLongshot ? (other, me) : (me, other);
        // (where the backup goes: the dry run of the same copy; a .zip written replaces no save)
        if (WholeImport && From.Path != null) Backup = SaveArchive.ImportWhole(Op.Unpacked!, Page.Env, Op.KeepSettings, dryRun: true).Backup;
        else if (From.Path != null && To.Path != null && !ZipExport)
        {
            try
            {
                Backup = Saves.Copy(From.Path, To.Path, Page.Env.BackupRoot, Op.Mode switch { SaveMode.Import => "import", SaveMode.Restore => "restore", _ => "export" }, Op.Program.Name,
                                    Op.KeepSettings, dryRun: true, force: true).Backup;
            }
            catch (Exception e) when (e is SaveFailure or IOException or UnauthorizedAccessException) { Problem = e.Message; }
        }
    }

    /// The .zip's side: to import, unpacked and checked first (what is wrong with it is the problem shown); to export, the file
    SaveSide ZipSide()
    {
        var zip = Op.Zip!;
        var name = Path.GetFileName(zip);
        if (Op.Mode == SaveMode.Export)
            return new SaveSide(name, zip, File.Exists(zip) ? SaveArchive.Read(zip).Slots : Array.Empty<SaveSlot>(), zip, true);
        Op.Forget();
        var unpacked = SaveArchive.Unpack(zip);
        if (!unpacked.Valid)
        {
            Problem = Tr.F("This .zip can't be imported: {0}. Nothing was copied.", string.Join("; ", unpacked.Problems));
            return new SaveSide(name, zip, SaveArchive.Read(zip).Slots, null, true);
        }
        Op.Unpacked = unpacked;
        // (a whole save folder: the slots of Longshot's profile in it, the one the game opens)
        return new SaveSide(name, zip, Saves.Describe(unpacked.Whole ? Path.Combine(unpacked.Folder, Saves.UserId) : unpacked.Folder), unpacked.Folder, true);
    }

    void Run()
    {
        var main = Page.Main;
        try
        {
            if (ZipExport)
            {
                // (Longshot's save folder whole: every profile in it, not only the one shown)
                var written = SaveArchive.ExportSave(Page.Env.SaveFolder, Op.Zip!, Service.OwnTag);
                main.Navigate(new SavesResultViewModel(this, SaveResult.Done, null,
                    written.Skipped.Count > 0 ? Tr.F("Left out, being links: {0}.", string.Join(", ", written.Skipped)) : null));
                return;
            }
            if (WholeImport)
            {
                var whole = SaveArchive.ImportWhole(Op.Unpacked!, Page.Env, Op.KeepSettings);
                Op.Forget();
                main.Navigate(new SavesResultViewModel(this, SaveResult.Done, whole.Backup, null));
                return;
            }
            if (Op.IntoLongshot && Saves.ImportTarget(Page.Env) != To.Path)
                throw new SaveFailure(Tr.T("Longshot's save location changed. Go back and review the import again."));
            if (Op.Backup is { } backup)
            {
                var restored = SaveBackups.Restore(backup, Page.Env, Op.KeepSettings);
                main.Navigate(new SavesResultViewModel(this, SaveResult.Done, restored.Backup, null));
                return;
            }
            var done = Saves.ImportExport(Op.Mode == SaveMode.Import, Op.Program, Op.Unpacked?.Folder ?? Op.Folder, Page.Env, Op.Profile, Op.KeepSettings);
            Op.Forget();
            if (Op.Mode == SaveMode.Import && Op.Program.Name == "nx2navi")
            {
                Service.Settings.Nx2naviSavesAsked = true;
                try { Service.SaveSettings(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                main.Home.Nx2naviOffer = null;
            }
            main.Navigate(new SavesResultViewModel(this, SaveResult.Done, done.Backup, null));
        }
        catch (SaveFailure e)
        {
            var kind = e.Reason switch { SaveRefusal.Running => SaveResult.Running, SaveRefusal.Waiting => SaveResult.Waiting, _ => SaveResult.Failed };
            main.Navigate(new SavesResultViewModel(this, kind, e.Backup, e.Message, e.Who));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            main.Navigate(new SavesResultViewModel(this, SaveResult.Failed, null, e.Message));
        }
    }

    public void Retry() => Run();

    /// After the copy: the target's saves again
    public IReadOnlyList<SaveSlot> TargetSlotsNow() =>
        To.Path == null ? Array.Empty<SaveSlot>() : ZipExport ? SaveArchive.Read(To.Path).Slots : Saves.Describe(To.Path);
}

public enum SaveResult { Done, Running, Waiting, Failed }

/// The copy's outcome: done (with the backup one click away), or refused, with nothing changed
public sealed class SavesResultViewModel : ViewModelBase
{
    public SavesResultViewModel(SavesConfirmViewModel confirm, SaveResult kind, string? backup, string? message, IReadOnlyList<string>? who = null)
    {
        Confirm = confirm;
        Kind = kind;
        Backup = backup;
        Message = message;
        Who = who ?? Array.Empty<string>();
        var main = confirm.Page.Main;
        BackCommand = new Command(() =>
        {
            confirm.Op.Forget();
            confirm.Page.Reload();
            main.Navigate(confirm.Page);
            // (the Saves page shows the slots as they are now; this says, there too, that they came from the copy)
            if (Done) main.ShowToast(DoneToast);
        });
        RetryCommand = new Command(confirm.Retry);
        BackupCommand = new Command(() => { if (Backup != null) main.Host?.Open(Backup); });
        // (what failed and between which programs: no save's contents, and no user name (BugReport, Privacy.Scrub))
        CopyCommand = main.CopyReportCommand(() => Tr.Sentences(Heading, Sub),
                                             () => new[] { $"saves: {confirm.Op.Mode} with {confirm.Op.Program.Name}, from {confirm.From.Path} to {confirm.To.Path}" });
        if (kind == SaveResult.Failed) LauncherLog.Add($"saves {confirm.Op.Mode} with {confirm.Op.Program.Name} failed: {message}");
    }

    public SavesConfirmViewModel Confirm { get; }
    public SaveResult Kind { get; }
    public string? Backup { get; }
    public string? Message { get; }
    public IReadOnlyList<string> Who { get; }
    public bool Done => Kind == SaveResult.Done;
    public bool Refused => !Done;
    public bool HasBackup => Backup != null;
    /// The copy failed (not refused for a reason the player can act on: a game running, a save waiting): Copy for a bug report
    public bool Failed => Kind == SaveResult.Failed;

    public string Heading => Kind switch
    {
        SaveResult.Done => Confirm.Op.Mode switch
        {
            SaveMode.Import => Tr.F("Saves imported from {0}", Confirm.From.Name),
            SaveMode.Restore => Tr.T("Saves restored from the backup"),
            _ => Tr.F("Saves exported to {0}", Confirm.To.Name),
        },
        SaveResult.Running => Tr.T("Close the game first"),
        SaveResult.Waiting => Tr.T("NX2navi has a save waiting"),
        _ => Tr.T("The saves were not copied"),
    };

    public string Sub => Kind switch
    {
        SaveResult.Done => Tr.F(Confirm.Op.KeepSettings ? "{0} now has {1}, with its own game settings kept." : "{0} now has {1}.",
                                Confirm.To.Name, SaveText.Long(Confirm.TargetSlotsNow(), DateTime.Now)),
        SaveResult.Running => Tr.F("{0} is running. A running game can write its save again when it quits, and undo the copy. Close it, then try again.",
                                   Who.Count > 0 ? string.Join(Tr.T(" and "), Who) : Tr.T("A game")),
        SaveResult.Waiting => Tr.T("NX2navi writes a save to profile/save-tx first and moves it into place at its next start; until then that is the save it " +
                                   "loads. Start NX2navi and quit it, then try again."),
        _ => Message ?? "",
    };

    /// What the Saves page says, back from a copy that worked: "Imported 3 save slots from NX2navi."
    public string DoneToast
    {
        get
        {
            var n = Confirm.TargetSlotsNow().Count;
            return Confirm.Op.Mode switch
            {
                SaveMode.Import => Tr.P(n, "Imported 1 save slot from {1}.", "Imported {0} save slots from {1}.", Confirm.From.Name),
                SaveMode.Restore => Tr.P(n, "Restored 1 save slot from a backup.", "Restored {0} save slots from a backup."),
                _ => Tr.P(n, "Exported 1 save slot to {1}.", "Exported {0} save slots to {1}.", Confirm.To.Name),
            };
        }
    }

    public string Faint => Done && Confirm.ZipExport ? Message ?? Tr.T("Nothing in your saves was changed.")
        : Done ? (HasBackup ? Tr.F("The saves it replaced are backed up, in {0}.", ShownPath.Of(Backup)) : Tr.T("There was nothing there to back up."))
        : HasBackup ? Tr.F("Nothing was changed. The backup made first is in {0}.", ShownPath.Of(Backup)) : Tr.T("Nothing was changed.");

    public Command BackCommand { get; }
    public Command RetryCommand { get; }
    public Command BackupCommand { get; }
    public Command CopyCommand { get; }
}

/// Restore from a backup: every save the copies backed up first, newest first, what made each and its slots; Restore puts one
/// back into Longshot's save after the confirm page (Longshot's own is backed up first), Open folder shows it
public sealed class BackupsViewModel : ViewModelBase
{
    public sealed class Row(BackupsViewModel owner, BackupSave b, DateTime now)
    {
        public BackupSave Backup => b;
        public string What => BackupsViewModel.What(b);
        public string When => Durations.When(b.When, now);
        public string Summary => SaveText.Long(b.Slots, now) + " · " + Size(b.Bytes)
            + (owner.Page.Env.BackupRoots.Count > 1 ? " · " + Tr.F("in {0}", SaveViews.Short(b.Root)) : "");
        public string Where => SaveViews.Short(b.Path);
        public string FullPath => ShownPath.Of(b.Path);
        public Command RestoreCommand { get; } = new(() => owner.Restore(b));
        public Command FolderCommand { get; } = new(() => owner.Page.Main.Host?.Open(b.Path));
    }

    public BackupsViewModel(SavesViewModel page)
    {
        Page = page;
        var now = DateTime.Now;
        Rows = page.Backups.Select(b => new Row(this, b, now)).ToList();
        BackCommand = new Command(() => page.Main.Navigate(page));
        FolderCommand = new Command(() => page.Main.Host?.Open(page.Env.BackupRoot), () => Directory.Exists(page.Env.BackupRoot));
    }

    public SavesViewModel Page { get; }
    public IReadOnlyList<Row> Rows { get; }
    public Command BackCommand { get; }
    public Command FolderCommand { get; }
    public string Where => ShownPath.Of(Page.Env.BackupRoot);
    public string Count => Tr.P(Rows.Count, "1 backup", "{0} backups");

    /// A save's size: a few kilobytes, so in KB below a megabyte ("24 KB"), as Durations.Size above
    static string Size(long bytes) => bytes < 1L << 20 ? $"{Math.Max(1, bytes >> 10)} KB" : Durations.Size(bytes);

    void Restore(BackupSave b) => Page.Main.Navigate(new SavesConfirmViewModel(Page, new SaveOp { Mode = SaveMode.Restore, Backup = b }));

    /// What made a backup, as the window says it: "Before an import from Lichii", "v3.0.0's own save, before it was deleted"
    public static string What(BackupSave b)
    {
        var what = b.Kind switch
        {
            BackupKind.Import => Tr.F("Before an import from {0}", OtherOf(b)),
            BackupKind.Export => Tr.F("The save of {0} before an export to it, to restore into Longshot", OtherOf(b)),
            BackupKind.Hookshot => Tr.T("Before an import from Hookshot"),
            BackupKind.Restore => Tr.T("Before a backup was restored"),
            BackupKind.Gather => b.Part switch
            {
                "shared" or "shared (set aside)" => Tr.T("The shared save, before the versions' saves were brought together"),
                null => Tr.T("Before the versions' saves were brought together"),
                var p => Tr.F("{0}'s own save, before the versions' saves were brought together", p),
            },
            BackupKind.Delete => b.Part is { } part ? Tr.F("{0}'s own save, before it was deleted", part) : Tr.T("A version's own save, before it was deleted"),
            BackupKind.Edit => Tr.T("Before an edit in Progress"),
            _ => Path.GetFileName(b.Folder),
        };
        // (a backup with several profiles and not the game's: each named, as the import names one)
        return b.Profile is { } id ? what + Tr.F(" (profile {0}…)", id[..Math.Min(8, id.Length)]) : what;
    }

    /// The other side of the copy a backup was made for: Lichii, a .zip file, a folder
    public static string OtherOf(BackupSave b) => b.Name switch
    {
        null => Tr.T("the folder"),
        "zip" => Tr.T("a .zip file"),
        "folder" => Tr.T("a folder"),
        var n => Saves.Emulators.FirstOrDefault(p => p.Name == n)?.Display ?? n,
    };
}
