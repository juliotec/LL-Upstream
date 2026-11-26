using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

/// A row of the NFC page's list: a file of the NFC folder, a dump or not, ticked to delete several at once
public sealed class NfcRowViewModel : ViewModelBase
{
    readonly NfcViewModel page;
    bool picked;

    public NfcRowViewModel(NfcViewModel page, NfcTagFile file, bool picked, bool first)
    {
        this.page = page;
        this.picked = picked;
        File = file;
        First = first;
        DeleteCommand = new Command(() => page.AskDelete(new[] { this }));
    }

    public NfcTagFile File { get; }
    public string Name => File.Name;
    public string Format => File.Format;
    public bool Valid => File.Valid;
    public bool Invalid => !File.Valid;
    public string Model => File.Model ?? "";
    /// "Not an NFC tag dump: it is 12 bytes, and a .bin dump is 532, 540 or 572"
    public string Problem => File.Problem is { } p ? Tr.F("Not an NFC tag dump: {0}", p) : "";
    public bool First { get; }
    public Avalonia.Thickness Line => First ? default : new Avalonia.Thickness(0, 1, 0, 0);
    public string PickName => Tr.F("Select {0}", File.FileName);
    public Command DeleteCommand { get; }

    public bool IsPicked
    {
        get => picked;
        set
        {
            if (Set(ref picked, value)) page.PickedChanged();
        }
    }
}

/// NFC: the NFC tag dumps in the folder every build of the game reads (NfcTags.Folder), which the Insert menu's NFC tab lists.
/// Add one, many or a folder of them (each checked first, never over another file), scan one with a Switch 2 Pro Controller
/// over USB (Windows; NfcReader), delete them (asked first), open the folder
public sealed class NfcViewModel : ViewModelBase, ILeavingPage
{
    IReadOnlyList<string> report = Array.Empty<string>();
    CancellationTokenSource? scanning;
    string scanText = "";
    bool adding;
    int addRun;
    string addingText = "";

    public NfcViewModel(MainViewModel main)
    {
        Main = main;
        // (a relative LONGSHOT_NFC_DIR is read by the game from its build's folder, where it is started: the default build's here)
        Folder = NfcTags.Folder(main.Platform, main.Service.SaveEnv.Getenv, main.Home.Default?.Entry.Folder);
        AddCommand = new Command(() => _ = AddFilesAsync(), () => !Adding);
        AddFolderCommand = new Command(() => _ = AddFolderAsync(), () => !Adding);
        DeleteCommand = new Command(() => AskDelete(Rows.Where(r => r.IsPicked).ToList()), () => !Adding && Rows.Any(r => r.IsPicked));
        DeleteAllCommand = new Command(() => AskDelete(Rows, all: true), () => !Adding && Rows.Count > 0);
        OpenCommand = new Command(Open);
        ScanCommand = new Command(() => _ = ScanAsync(), () => CanScan && !Scanning);
        CancelScanCommand = new Command(() => scanning?.Cancel());
        Reload();
    }

    public MainViewModel Main { get; }
    /// The whole path: handed to the file manager, and the tooltip of the one shown
    public string Folder { get; }
    public string FolderShown => ShownPath.Of(Folder);
    public IReadOnlyList<NfcRowViewModel> Rows { get; private set; } = Array.Empty<NfcRowViewModel>();
    public bool HasRows => Rows.Count > 0;
    public bool NoRows => Rows.Count == 0;

    public string Count
    {
        get
        {
            var (tags, others) = (Rows.Count(r => r.Valid), Rows.Count(r => r.Invalid));
            var count = Tr.P(tags, "1 NFC tag", "{0} NFC tags");
            return others > 0 ? Tr.P(others, "{1}, 1 not a dump", "{1}, {0} not a dump", count) : count;
        }
    }

    /// (the game stops at 256 of the folder's .bin and .nfc files, dumps or not, as the folder lists them: said once there are more)
    public bool TooMany => Rows.Count > NfcTags.Most;
    public string TooManyText =>
        Tr.F("The game lists only {0} of the {1} .bin and .nfc files here, files that are not a dump included, and not the first by name: whichever the folder gives it first. Delete some to choose which it lists.",
             NfcTags.Most, Rows.Count);
    public string TooManyChip => Tr.F("The game lists {0}", NfcTags.Most);
    public string DeleteLabel => Rows.Count(r => r.IsPicked) is > 0 and var n ? Tr.F("Delete selected ({0})", n) : Tr.T("Delete selected");

    /// What the last add did, a line for each file that was not simply copied in
    public IReadOnlyList<string> Report
    {
        get => report;
        private set
        {
            report = value;
            RaiseAll(nameof(Report), nameof(HasReport));
        }
    }

    public bool HasReport => report.Count > 0;

    public Command AddCommand { get; }
    public Command AddFolderCommand { get; }
    public Command DeleteCommand { get; }
    /// Every file the list shows, asked first (off with an empty list, and while files are being added)
    public Command DeleteAllCommand { get; }
    public Command OpenCommand { get; }
    /// Left by a title bar tab: a scan stops, the controller let go
    public void Left() => scanning?.Cancel();
    public Command ScanCommand { get; }
    public Command CancelScanCommand { get; }

    /// (the controller is read through WinUSB: Windows only)
    public bool CanScan => Main.Platform == Platform.Windows && OperatingSystem.IsWindows();
    public string ScanHint => CanScan ? Tr.T("With a Switch 2 Pro Controller over USB") : Tr.T("Windows only, with a controller over USB");
    public bool Scanning => scanning != null;
    public bool NotScanning => scanning == null;

    /// What the scan is doing: "Hold the tag on the controller's logo… up to 10 s"
    public string ScanText
    {
        get => scanText;
        private set => Set(ref scanText, value);
    }

    /// Files being checked and copied in, off the window's thread: the add buttons and Delete are off meanwhile
    public bool Adding => adding;

    /// "Adding 40 of 120 files…"
    public string AddingText
    {
        get => addingText;
        private set => Set(ref addingText, value);
    }

    public void Reload()
    {
        var picked = Rows.Where(r => r.IsPicked).Select(r => r.File.FileName).ToHashSet();
        IReadOnlyList<NfcTagFile> files;
        try { files = NfcTags.List(Folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            files = Array.Empty<NfcTagFile>();
            Report = new[] { Tr.F("{0} could not be read: {1}", FolderShown, e.Message) };
        }
        Rows = files.Select((f, i) => new NfcRowViewModel(this, f, picked.Contains(f.FileName), i == 0)).ToList();
        RaiseAll(nameof(Rows), nameof(HasRows), nameof(NoRows), nameof(Count), nameof(TooMany), nameof(TooManyText), nameof(TooManyChip));
        DeleteAllCommand.Changed();
        PickedChanged();
    }

    public void PickedChanged()
    {
        Raise(nameof(DeleteLabel));
        DeleteCommand.Changed();
    }

    async Task AddFilesAsync()
    {
        if (Adding || Main.Host is not { } host) return;
        var files = await host.PickFilesAsync(Tr.T("Your NFC tag dumps (.bin, .nfc)"), Tr.T("NFC tag dumps"), new[] { "*.bin", "*.nfc", "*.BIN", "*.NFC" });
        if (files.Count > 0) await AddAsync(() => files, Tr.T("No file was picked."));
    }

    /// Files and folders dropped on the window while this page is shown (MainViewModel.Drop): the files as picked ones, a folder's
    /// .bin and .nfc files as Add a folder takes them
    public void AddDropped(IReadOnlyList<string> paths) => _ = AddAsync(() =>
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path)) files.AddRange(NfcTags.FilesUnder(path));
                else if (File.Exists(path)) files.Add(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return files;
    }, Tr.T("Nothing dropped was a .bin or .nfc file, or a folder with one in it."));

    async Task AddFolderAsync()
    {
        if (Adding || Main.Host is not { } host) return;
        if (await host.PickFolderAsync(Tr.T("A folder of NFC tag dumps"), null) is not { } from) return;
        await AddAsync(() => NfcTags.FilesUnder(from), Tr.F("{0} has no .bin or .nfc file in it.", ShownPath.Of(from)), from);
    }

    /// Finds the files (a folder is walked with the folders under it) and copies them in, both off the window's thread: a big
    /// folder (Downloads, a whole drive) would freeze the window. Then says what happened, back on the window's thread: the count
    /// in a toast, and on the page each file skipped or renamed, with why. None: says `none`
    async Task AddAsync(Func<IReadOnlyList<string>> find, string none, string? from = null)
    {
        if (Adding) return;
        var run = ++addRun;
        SetAdding(true);
        AddingText = Tr.T("Looking for NFC tag dumps…");
        Report = Array.Empty<string>();
        try
        {
            IReadOnlyList<string> files;
            try { files = await Task.Run(find); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Report = new[] { Tr.F("{0} could not be read: {1}", ShownPath.Of(from), e.Message) };
                return;
            }
            if (files.Count == 0)
            {
                Report = new[] { none };
                return;
            }
            AddingText = Tr.P(files.Count, "Adding 1 file…", "Adding {0} files…");
            // (how far it is, every few files: handed to the window's thread, and dropped once this add is over)
            var counted = files.Select((f, i) =>
            {
                if (i > 0 && i % 20 == 0) Ui.Post(() => { if (addRun == run && adding) AddingText = Tr.F("Adding {0} of {1} files…", i, files.Count); });
                return f;
            });
            IReadOnlyList<NfcTagAdded> done;
            try { done = await Task.Run(() => NfcTags.Add(Folder, counted)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Report = new[] { Tr.F("Nothing was added: {0}", e.Message) };
                Reload();
                return;
            }
            Added(done);
        }
        catch (Exception e)
        {
            // (anything else, so that the page never stops adding without a word)
            Report = new[] { Tr.F("The files could not be added: {0}", e.Message) };
            Reload();
        }
        finally
        {
            SetAdding(false);
        }
    }

    void SetAdding(bool on)
    {
        adding = on;
        Raise(nameof(Adding));
        Main.RaiseAcceptsDrop();
        AddCommand.Changed();
        AddFolderCommand.Changed();
        DeleteCommand.Changed();
        DeleteAllCommand.Changed();
    }

    void Added(IReadOnlyList<NfcTagAdded> done)
    {
        Report = done.Where(d => d.Outcome != NfcTagOutcome.Added).Select(d => d.Outcome switch
        {
            NfcTagOutcome.Numbered => Tr.F("{0}: added as {1} (another tag has its name)", Path.GetFileName(d.Source), d.FileName),
            NfcTagOutcome.Same => Tr.F("{0}: already there", Path.GetFileName(d.Source)),
            _ => Tr.F("{0}: skipped, not an NFC tag dump ({1})", Path.GetFileName(d.Source), d.Reason),
        }).ToList();
        var (added, skipped) = (done.Count(d => d.Copied), done.Count(d => !d.Copied));
        Main.ShowToast(skipped > 0 ? Tr.P(added, "Added 1 NFC tag, skipped {1}.", "Added {0} NFC tags, skipped {1}.", skipped)
                                   : Tr.P(added, "Added 1 NFC tag.", "Added {0} NFC tags."));
        Reload();
    }

    /// Reads the tag held on the controller, off the window's thread, and saves it as the game names its own scans. The tag is
    /// only read, never written
    async Task ScanAsync()
    {
        if (!CanScan || scanning != null) return;
        using var stop = new CancellationTokenSource();
        scanning = stop;
        ScanText = Tr.T("Looking for the controller…");
        Report = Array.Empty<string>();
        ScanChanged();
        try
        {
            var scan = await Task.Run(() => NfcReader.ScanUsb(stop.Token, step => Ui.Post(() =>
            {
                if (scanning == stop) ScanText = step == NfcScanStep.Looking ? Tr.T("Hold the tag on the controller's logo… up to 10 s") : Tr.T("Reading the tag… keep it still");
            })));
            Scanned(scan);
        }
        catch (NfcReaderFailure e)
        {
            Report = new[]
            {
                e.Problem switch
                {
                    NfcReaderProblem.NoController => Tr.T("No controller found over USB. Plug in a Switch 2 Pro Controller with its cable, then scan again."),
                    NfcReaderProblem.InUse => Tr.T("The controller is in use by another program. Close Steam, Longshot and any controller bridge, then scan again."),
                    _ => e.Message,
                },
            };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException)
        {
            Report = new[] { Tr.F("The controller could not be read: {0}", e.Message) };
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            // (anything else, such as the registry read or a P/Invoke failing, so that the spinner never just goes away)
            Report = new[] { Tr.F("The scan stopped: {0}", e.Message) };
        }
        finally
        {
            scanning = null;
            ScanChanged();
        }
    }

    void ScanChanged()
    {
        RaiseAll(nameof(Scanning), nameof(NotScanning));
        ScanCommand.Changed();
    }

    /// The scan's outcome: saved (as the files added are: the same one is left, another of its name numbered), or why not
    void Scanned(NfcScan scan)
    {
        switch (scan.Outcome)
        {
            case NfcScanOutcome.Cancelled: return;
            case NfcScanOutcome.NoTag:
                Report = new[] { Tr.T("No tag found. Hold the tag flat on the controller's logo, then scan again.") };
                return;
            case NfcScanOutcome.Failed:
                Report = new[] { Tr.F("The tag could not be read: {0}.", scan.Problem) };
                return;
        }
        var saved = NfcReader.Save(Folder, scan.Tag!);
        if (saved.Outcome == NfcTagOutcome.Refused)
        {
            Report = new[] { Tr.F("The tag was read, but it is not a figure's tag ({0}). Nothing was saved.", saved.Reason) };
            return;
        }
        if (saved.Outcome == NfcTagOutcome.Same) Report = new[] { Tr.F("This tag is already there, as {0}.", saved.FileName) };
        else Main.ShowToast(Tr.F("Saved as {0}.", saved.FileName));
        Reload();
    }

    /// all: Delete all, the whole list (the files listed now: one added meanwhile by another program is not deleted)
    public void AskDelete(IReadOnlyList<NfcRowViewModel> rows, bool all = false)
    {
        if (rows.Count > 0) Main.Modal = new NfcDeleteViewModel(this, rows.Select(r => r.File).ToList(), all);
    }

    /// The files the player said yes to (NfcDeleteViewModel)
    public void Delete(IReadOnlyList<NfcTagFile> files, IReadOnlyList<NfcTags.DeletionSnapshot?> snapshots)
    {
        var gone = new List<string>();
        var skipped = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            var name = files[i].FileName;
            if (NfcTags.DeleteSnapshot(Folder, name, snapshots[i], out var recovery)) gone.Add(name);
            else skipped.Add(recovery != null
                ? Tr.F("{0}: kept because it changed, disappeared, or could not be checked or deleted. The file was kept at {1}.", name, recovery)
                : Tr.F("{0}: kept because it changed, disappeared, or could not be checked or deleted.", name));
            snapshots[i]?.Dispose();
        }
        Report = skipped;
        var deleted = gone.Count == 1 ? Tr.F("Deleted {0}", gone[0]) : Tr.P(gone.Count, "Deleted 1 file", "Deleted {0} files");
        Main.ShowToast(skipped.Count > 0 ? Tr.P(skipped.Count, "{1}, kept 1.", "{1}, kept {0}.", deleted) : Tr.F("{0}.", deleted));
        Reload();
    }

    /// The folder in the file manager, made first when it is not there yet
    void Open()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Main.Host?.Open(Folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Report = new[] { Tr.F("{0} could not be made: {1}", FolderShown, e.Message) };
        }
    }
}

/// Deleting NFC tag dumps: which files go, asked before any does
public sealed class NfcDeleteViewModel : ViewModelBase, IDisposable
{
    const int Shown = 8;
    readonly IReadOnlyList<NfcTags.DeletionSnapshot?> snapshots;

    public NfcDeleteViewModel(NfcViewModel page, IReadOnlyList<NfcTagFile> files, bool all = false)
    {
        Files = files;
        All = all && files.Count > 1;
        DeleteAll = all;
        snapshots = files.Select(f => NfcTags.DeleteSnapshot(page.Folder, f.FileName)).ToList();
        KeepCommand = new Command(() => page.Main.Modal = null);
        DeleteCommand = new Command(() =>
        {
            try { page.Delete(files, snapshots); }
            finally { page.Main.Modal = null; }
        });
    }

    public void Dispose()
    {
        foreach (var snapshot in snapshots) snapshot?.Dispose();
    }

    public IReadOnlyList<NfcTagFile> Files { get; }
    /// Delete all: every file of the list, more than one
    public bool All { get; }
    bool DeleteAll { get; }
    public string Title => All ? Files.All(f => f.Valid) ? Tr.P(Files.Count, "Delete the 1 NFC tag?", "Delete all {0} NFC tags?")
                                                         : Tr.P(Files.Count, "Delete the 1 file?", "Delete all {0} files?")
        : Files.Count == 1 ? Tr.F("Delete {0}?", Files[0].Name) : Tr.P(Files.Count, "Delete 1 file?", "Delete {0} files?");
    /// (a scanned tag was saved straight into the folder: deleting it there is deleting the only copy)
    public string Sub => (DeleteAll
        ? Tr.T("This deletes every file in the list from the NFC folder, and it can't be undone. The files you added them from, and your figures, are not touched, but a tag you scanned with a controller is kept only here.")
        : Files.Count == 1 ? Tr.T("This deletes the file from the NFC folder. The files you added them from, and your figures, are not touched.")
        : Tr.T("This deletes the files from the NFC folder. The files you added them from, and your figures, are not touched.")) +
        (OperatingSystem.IsLinux() ? " " + Tr.T("Close other programs using these files first. On Linux, changes made while deleting may still be lost.") : "");
    /// (the first few by name, then how many more)
    public IReadOnlyList<string> Names => Files.Take(Shown).Select(f => f.FileName)
        .Concat(Files.Count > Shown ? new[] { Tr.P(Files.Count - Shown, "and 1 more", "and {0} more") } : Array.Empty<string>()).ToList();
    public string KeepLabel => Files.Count == 1 ? Tr.T("Keep it") : Tr.T("Keep them");
    public string DeleteLabel => All ? Tr.T("Delete all") : Tr.T("Delete");
    public Command KeepCommand { get; }
    public Command DeleteCommand { get; }
}
