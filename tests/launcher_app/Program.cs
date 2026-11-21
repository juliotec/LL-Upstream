using LongshotLauncher;
using LongshotLauncher.Core;
using LongshotLauncher.ViewModels;

// Run with dotnet run --project tests/launcher_app/LauncherAppTests.csproj
var root = Path.Combine(Path.GetTempPath(), "launcher-app-" + Guid.NewGuid().ToString("N"));
try
{
    var home = new LauncherHome(root);
    Directory.CreateDirectory(root);
    new LauncherSettings
    {
        BuildsFolder = Path.Combine(root, "builds"),
        HookshotSavesAsked = true,
        Nx2naviSavesAsked = true,
        InstalledLauncherAsked = true,
    }.Save(home.Settings);
    var service = LauncherService.Open(Platform.Linux, home);
    var main = new MainViewModel(service, new StartRequest(StartKind.Launch, null, [], null));
    var settings = new SettingsViewModel(main);
    settings.Gamescope = true;
    settings.GamescopeOptions = "  a\\ ";

    var saved = LauncherSettings.Load(home.Settings);
    if (!saved.Gamescope || saved.GamescopeOptions != "  a\\ ")
        throw new Exception("The settings setter did not save the gamescope options as typed.");
    var command = GamePlay.StartInfo(root, Platform.Linux, new Dictionary<string, string>(),
                                     gamescope: saved.Gamescope, gamescopeOptions: saved.GamescopeOptions);
    if (command.FileName != "gamescope" || command.ArgumentList.Count != 3 ||
        command.ArgumentList[0] != "a " || command.ArgumentList[1] != "--" ||
        command.ArgumentList[2] != GameFolder.ExePath(root, Platform.Linux))
        throw new Exception("The saved escaped final space did not reach the gamescope argument.");

    NfcDeleteAll(root, service, main);
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

// The NFC page's Delete all (NfcViewModel.DeleteAllCommand), over a folder of fake tags made here (no real dump: CONTRIBUTING.md): it
// asks first, then deletes unchanged listed files and leaves replacements and files added after the question.
static void NfcDeleteAll(string root, LauncherService service, MainViewModel main)
{
    static void Require(bool ok, string what)
    {
        if (!ok) throw new Exception("NFC Delete all: " + what);
    }
    // (540 bytes with an NTAG215's capability container at 0x0C and a figure's 0x02 at 0x5B: docs/nfc.md)
    static byte[] Tag(int n)
    {
        var tag = new byte[540];
        for (var i = 0; i < tag.Length; i++) tag[i] = (byte)(i * 7 + n);
        new byte[] { 0xF1, 0x10, 0xFF, 0xEE }.CopyTo(tag, 0x0C);
        tag[0x5B] = 0x02;
        return tag;
    }

    var nfc = Path.Combine(root, "nfc");
    Directory.CreateDirectory(nfc);
    for (var i = 0; i < 257; i++) File.WriteAllBytes(Path.Combine(nfc, $"Tag {i:000}.bin"), Tag(i));
    File.WriteAllText(Path.Combine(nfc, "broken.nfc"), "not a dump");
    File.WriteAllText(Path.Combine(nfc, "key_retail.bin"), "a key file, not a tag");
    File.WriteAllText(Path.Combine(nfc, "notes.txt"), "not a tag");
    Directory.CreateDirectory(Path.Combine(nfc, "sub"));
    File.WriteAllBytes(Path.Combine(nfc, "sub", "Deeper.bin"), Tag(1));
    var beside = Path.Combine(root, "Beside.bin");
    File.WriteAllBytes(beside, Tag(2));

    var getenv = service.SaveEnv.Getenv;
    service.SaveEnv = service.SaveEnv with { Getenv = n => n == "LONGSHOT_NFC_DIR" ? nfc : getenv(n) };
    Ui.Post = _ => { };
    var page = new NfcViewModel(main);
    Require(page.Folder == nfc, "the page reads the test's folder, " + page.Folder);
    Require(page.Rows.Count == 258 && page.TooMany && page.Count == "257 NFC tags, 1 not a dump",
            $"the list before: {page.Rows.Count} rows, {page.Count}, too many {page.TooMany}");
    Require(page.DeleteAllCommand.CanExecute(null), "Delete all is on with files in the list");

    var raised = new HashSet<string>();
    page.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
    var turned = 0;
    page.DeleteAllCommand.CanExecuteChanged += (_, _) => turned++;

    // Asked first: nothing goes until the player says yes, and Keep them keeps every file
    page.DeleteAllCommand.Execute(null);
    Require(main.Modal is NfcDeleteViewModel { All: true, Title: "Delete all 258 files?", DeleteLabel: "Delete all", KeepLabel: "Keep them" } ask
            && ask.Files.Count == 258 && ask.Sub.Contains("can't be undone"), "Delete all asks first, naming how many go");
    var asked = (NfcDeleteViewModel)main.Modal!;
    Require(!OperatingSystem.IsLinux() || (asked.Sub.Contains("Close other programs") && asked.Sub.Contains("changes made while deleting may still be lost")),
            "Linux confirmation warns about concurrent writes");
    Require(Directory.GetFiles(nfc).Length == 260, "nothing is deleted before the player says yes");
    asked.KeepCommand.Execute(null);
    Require(main.Modal == null && Directory.GetFiles(nfc).Length == 260 && page.Rows.Count == 258, "Keep them keeps every file");

    // Yes: every listed file goes; a file put there after the ask, a key file, another kind of file, a subfolder and a file outside stay
    page.DeleteAllCommand.Execute(null);
    asked = (NfcDeleteViewModel)main.Modal!;
    File.WriteAllBytes(Path.Combine(nfc, "Later.bin"), Tag(3));
    var replaced = Path.Combine(nfc, "Tag 000.bin");
    File.Move(replaced, Path.Combine(root, "original-000.bin"));
    File.WriteAllBytes(replaced, Tag(42));
    var identical = Path.Combine(nfc, "Tag 001.bin");
    File.Move(identical, Path.Combine(root, "original-001.bin"));
    File.WriteAllBytes(identical, Tag(1));
    asked.DeleteCommand.Execute(null);
    Require(main.Modal == null, "the question closes");
    var left = Directory.GetFiles(nfc).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
    Require(left.SequenceEqual(new[] { "Later.bin", "Tag 000.bin", "Tag 001.bin", "key_retail.bin", "notes.txt" }), "what is left in the folder: " + string.Join(", ", left));
    Require(File.ReadAllBytes(replaced).SequenceEqual(Tag(42)) && page.Report.Any(r => r.Contains("Tag 000.bin") && r.Contains("kept")),
            "a replacement under the same name is kept and reported");
    Require(File.ReadAllBytes(identical).SequenceEqual(Tag(1)) && page.Report.Any(r => r.Contains("Tag 001.bin") && r.Contains("kept")),
            "an identical replacement is kept and reported");
    Require(File.Exists(Path.Combine(nfc, "sub", "Deeper.bin")) && File.Exists(beside), "a subfolder's file and a file outside the folder are not touched");
    Require(page.Rows.Count == 3 && !page.TooMany && page.Count == "3 NFC tags",
            $"the list after: {page.Rows.Count} rows, {page.Count}, too many {page.TooMany}");
    Require(raised.IsSupersetOf(new[] { "Rows", "Count", "TooMany", "TooManyText", "HasRows", "NoRows" }) && turned > 0,
            "the list, the count, the 256 note and the button are told: " + string.Join(", ", raised));

    // Remove the replacement through its row, leaving one file for Delete all.
    page.Rows.Single(r => r.File.FileName == "Tag 000.bin").DeleteCommand.Execute(null);
    Require(!OperatingSystem.IsLinux() || ((NfcDeleteViewModel)main.Modal!).Sub.Contains("changes made while deleting may still be lost"),
            "Linux row deletion also warns about concurrent writes");
    ((NfcDeleteViewModel)main.Modal!).DeleteCommand.Execute(null);
    page.Rows.Single(r => r.File.FileName == "Tag 001.bin").DeleteCommand.Execute(null);
    ((NfcDeleteViewModel)main.Modal!).DeleteCommand.Execute(null);
    // One file left: asked by its name with the Delete all warnings; then the list is empty and Delete all is off
    page.DeleteAllCommand.Execute(null);
    Require(main.Modal is NfcDeleteViewModel { All: false, Title: "Delete Later?", DeleteLabel: "Delete", KeepLabel: "Keep it" } single
            && single.Sub.Contains("can't be undone") && single.Sub.Contains("scanned with a controller") && single.Sub.Contains("only here"),
            "one file is asked by its name with the Delete all warnings");
    ((NfcDeleteViewModel)main.Modal!).DeleteCommand.Execute(null);
    Require(page.NoRows && page.Count == "0 NFC tags" && !page.DeleteAllCommand.CanExecute(null), "an empty list: Delete all is off");
    Require(Directory.Exists(nfc) && File.Exists(Path.Combine(nfc, "key_retail.bin")) && File.Exists(Path.Combine(nfc, "notes.txt")),
            "the folder itself, the key file and the other file stay");
    page.DeleteAllCommand.Execute(null);
    Require(main.Modal == null, "with an empty list Delete all asks nothing");
}
