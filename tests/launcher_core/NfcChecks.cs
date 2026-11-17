using System.Text;
using LongshotLauncher.Core;

/// The NFC folder (Core/NfcTags.cs) over fake tags made here only, never a real dump: 540 patterned bytes with the bytes the
/// game looks at set (the capability container, the figure's 0x02). Where the folder is on each system, what a .bin and a .nfc
/// must be, the list, and the copies in and the deletes.
static partial class Program
{
    /// A fake tag: no figure's bytes, only the markers; seed tells one from another (its UID and its model differ)
    static byte[] FakeTag(int seed = 1, int size = NfcTags.TagBytes)
    {
        var tag = new byte[NfcTags.TagBytes];
        for (var i = 0; i < tag.Length; i++) tag[i] = (byte)(i * 7 + seed);
        new byte[] { 0xF1, 0x10, 0xFF, 0xEE }.CopyTo(tag, 0x0C);
        tag[0x5B] = 0x02;
        var file = new byte[size];
        tag.AsSpan(0, Math.Min(size, tag.Length)).CopyTo(file);
        return file;
    }

    /// The tag as a Flipper Zero NFC file, with the pages asked for
    static byte[] FakeNfc(byte[] tag, int pages = 135, string first = "Filetype: Flipper NFC device", string newline = "\n")
    {
        var text = new StringBuilder(first + newline + "Version: 4" + newline + "Device type: NTAG215" + newline + "Pages total: 135" + newline);
        for (var p = 0; p < pages; p++)
            text.Append($"Page {p}: {tag[p * 4]:X2} {tag[p * 4 + 1]:X2} {tag[p * 4 + 2]:X2} {tag[p * 4 + 3]:X2}{newline}");
        return Encoding.ASCII.GetBytes(text + "Failed authentication attempts: 0" + newline);
    }

    static void NfcChecks()
    {
        var root = Folder("nfc");

        // ---- where the folder is ----
        var set = Path.Combine(root, "chosen ゲーム");
        var local = Path.Combine(root, "Local");
        var data = Path.Combine(root, "data");
        var home = Path.Combine(root, "home");
        Func<string, string?> windows = k => k == "LOCALAPPDATA" ? local : null;
        Func<string, string?> xdg = k => k switch { "XDG_DATA_HOME" => data, "HOME" => home, _ => null };
        Func<string, string?> plain = k => k == "HOME" ? home : null;
        Func<string, string?> chosen = k => k switch { NfcTags.Variable => set, "LOCALAPPDATA" => local, "XDG_DATA_HOME" => data, "HOME" => home, _ => null };
        Func<string, string?> empty = k => k switch { NfcTags.Variable => "", "LOCALAPPDATA" => local, "XDG_DATA_HOME" => "", "HOME" => home, _ => null };
        Require(NfcTags.Variable == "LONGSHOT_NFC_DIR", "The variable is the game's.");
        Require(NfcTags.Folder(Platform.Windows, windows) == Path.Combine(local, "Longshot", "nfc"), @"Windows: %LOCALAPPDATA%\Longshot\nfc.");
        Require(NfcTags.Folder(Platform.Linux, xdg) == Path.Combine(data, "longshot", "nfc"), "Linux: $XDG_DATA_HOME/longshot/nfc.");
        Require(NfcTags.Folder(Platform.Linux, plain) == Path.Combine(home, ".local", "share", "longshot", "nfc"), "Linux: ~/.local/share/longshot/nfc.");
        Require(NfcTags.Folder(Platform.Windows, chosen) == set && NfcTags.Folder(Platform.Linux, chosen) == set, "LONGSHOT_NFC_DIR on both.");
        Require(NfcTags.Folder(Platform.Windows, empty) == Path.Combine(local, "Longshot", "nfc")
                && NfcTags.Folder(Platform.Linux, empty) == Path.Combine(home, ".local", "share", "longshot", "nfc"), "An empty variable is not set.");
        // (a relative LONGSHOT_NFC_DIR: the game takes it as given, from the build's folder it is started in)
        var game = Path.Combine(root, "build");
        Func<string, string?> relative = k => k switch { NfcTags.Variable => "tags", "LOCALAPPDATA" => local, "XDG_DATA_HOME" => data, "HOME" => home, _ => null };
        Require(NfcTags.Folder(Platform.Windows, relative, game) == Path.Combine(game, "tags") && NfcTags.Folder(Platform.Linux, relative, game) == Path.Combine(game, "tags"),
                "A relative LONGSHOT_NFC_DIR is in the build's folder, as the game reads it.");
        Require(NfcTags.Folder(Platform.Windows, relative) == Path.Combine(local, "Longshot", "nfc") && NfcTags.Folder(Platform.Linux, relative) == Path.Combine(data, "longshot", "nfc"),
                "A relative LONGSHOT_NFC_DIR with no build is left aside.");
        // (the game the launcher starts gets LONGSHOT_NFC_DIR on Linux too, where Play drops the other LONGSHOT_ variables)
        var launcherEnv = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin", [NfcTags.Variable] = "/n", ["LONGSHOT_NFC_DIRS"] = "/x", ["LONGSHOT_TOOLS"] = "/t", ["HOME"] = "/h",
        };
        var linuxGame = GamePlay.Environment(Platform.Linux, launcherEnv);
        Require(linuxGame.TryGetValue(NfcTags.Variable, out var passed) && passed == "/n"
                && !linuxGame.ContainsKey("LONGSHOT_NFC_DIRS") && !linuxGame.ContainsKey("LONGSHOT_TOOLS")
                && GamePlay.Environment(Platform.Windows, launcherEnv)[NfcTags.Variable] == "/n",
                "The game started on Linux keeps LONGSHOT_NFC_DIR, the other LONGSHOT_ variables dropped: the page and the game read one folder.");
        Require(NfcTags.Folder(Platform.Windows, chosen, game) == set && NfcTags.Folder(Platform.Linux, chosen, game) == set, "A full LONGSHOT_NFC_DIR is taken whatever the build.");

        // ---- a .bin: 532, 540 or 572 bytes ----
        var tag = FakeTag();
        foreach (var size in new[] { 532, 540, 572 })
        {
            var read = NfcTags.TagOfBin(FakeTag(size: size), out var why);
            Require(read is { Length: NfcTags.TagBytes } && why == null && read.AsSpan(0, 532).SequenceEqual(tag.AsSpan(0, 532)) && NfcTags.Check(read) == null,
                    $"A .bin of {size} bytes is a dump.");
        }
        foreach (var size in new[] { 0, 1, 531, 533, 539, 541, 571, 573, 1024 })
            Require(NfcTags.TagOfBin(new byte[size], out var why) == null && why!.Contains(size + " bytes"), $"A .bin of {size} bytes is not a dump.");

        // ---- the figure's marks, its model and its UID ----
        Require(NfcTags.Check(tag) == null, "The fake tag is a figure's.");
        Require(NfcTags.Model(tag) == Convert.ToHexString(tag.AsSpan(0x54, 8)) && NfcTags.Model(tag).Length == 16 && NfcTags.Model(tag).EndsWith("02"),
                "The model: the 8 bytes at 0x54, 16 hex digits.");
        Require(NfcTags.Uid(tag) == Convert.ToHexString(new[] { tag[0], tag[1], tag[2], tag[4], tag[5], tag[6], tag[7] }), "The UID: bytes 0, 1, 2, 4, 5, 6, 7.");
        for (var at = 0x0C; at <= 0x0F; at++)
        {
            var bad = FakeTag();
            bad[at] ^= 0x01;
            Require(NfcTags.Check(bad) != null, $"A tag with another byte at 0x{at:X2} is not an NTAG215's.");
        }
        var noFigure = FakeTag();
        noFigure[0x5B] = 0x00;
        Require(NfcTags.Check(noFigure) != null && NfcTags.Check(new byte[NfcTags.TagBytes]) != null && NfcTags.Check(new byte[16]) != null,
                "A tag with no 0x02 at 0x5B, an empty one and a short one are not figures' tags.");
        Require(NfcTags.Read("a.bin", tag, out var none) != null && none == null && NfcTags.Read("A.BIN", tag, out _) != null
                && NfcTags.Read("a.bin", noFigure, out var said) == null && said != null, "Read: a .bin, whatever the case of its ending, and why not.");
        Require(NfcTags.Read("a.txt", tag, out said) == null && said!.Contains(".bin or .nfc") && NfcTags.Read("key_retail.bin", tag, out said) == null
                && said!.Contains("key"), "Read: another ending and a key file are refused.");

        // ---- a .nfc: Flipper's text, page N at N*4 ----
        Require(NfcTags.TagOfNfc(FakeNfc(tag), out none) is { } whole && whole.SequenceEqual(tag) && none == null, "A .nfc with its 135 pages is the tag.");
        Require(NfcTags.TagOfNfc(FakeNfc(tag, newline: "\r\n"), out _) is { } crlf && crlf.SequenceEqual(tag), "A .nfc with Windows line ends.");
        Require(NfcTags.TagOfNfc(FakeNfc(tag, 130), out _) is { } most && most.AsSpan(0, 520).SequenceEqual(tag.AsSpan(0, 520))
                && most.AsSpan(520).SequenceEqual(new byte[20]), "A .nfc with 130 pages is enough; the rest are zeros.");
        Require(NfcTags.Read("a.nfc", FakeNfc(tag), out _) != null && NfcTags.Read("a.NFC", FakeNfc(tag, 130), out _) != null, "Read: a .nfc.");
        Require(NfcTags.TagOfNfc(FakeNfc(tag, 129), out said) == null && said!.Contains("129"), "A .nfc with 129 pages is too few.");
        Require(NfcTags.TagOfNfc(FakeNfc(tag, 0), out said) == null && said!.Contains("no pages"), "A .nfc with no pages.");
        Require(NfcTags.TagOfNfc(FakeNfc(tag, first: "Flipper NFC device"), out said) == null && said!.Contains("Filetype:"), "A .nfc must start with Filetype:.");
        Require(NfcTags.TagOfNfc(Array.Empty<byte>(), out _) == null && NfcTags.TagOfNfc(tag, out _) == null
                && NfcTags.TagOfNfc(Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray(), out _) == null, "Garbage is not a .nfc.");
        // (pages past the tag, and lines that are not four hex bytes, count for nothing)
        var odd = "Filetype: Flipper NFC device\nPage 135: 01 02 03 04\nPage 9999999999: 01 02 03 04\nPage x: 01 02 03 04\nPage 1: 01 02 03\n"
                  + "Page 2: 01 02 03 0g\nPage 3: ?? ?? ?? ??\nPage -1: 01 02 03 04\nPage 4 01 02 03 04\n";
        Require(NfcTags.TagOfNfc(Encoding.ASCII.GetBytes(odd), out said) == null && said!.Contains("no pages"), "Lines that are no page of the tag are not counted.");
        Require(NfcTags.TagOfNfc(Encoding.ASCII.GetBytes(odd + "Page 5: aa bB Cc DD\n"), out said) == null && said!.Contains("1 of"), "A page in either case of hex.");
        Require(NfcTags.Read("a.nfc", FakeNfc(noFigure), out said) == null && said!.Contains("figure"), "A .nfc of a tag that is no figure's.");
        // (the same page twice is one page)
        var twice = Encoding.ASCII.GetString(FakeNfc(tag, 129)) + "Page 0: 00 00 00 00\n";
        Require(NfcTags.TagOfNfc(Encoding.ASCII.GetBytes(twice), out _) == null, "A page given twice is counted once.");

        // ---- the folder's list ----
        var folder = NfcTags.Folder(Platform.Linux, xdg);
        Require(NfcTags.List(folder).Count == 0 && !Directory.Exists(folder), "No folder yet: an empty list, and listing does not make it.");
        var from = Path.Combine(root, "from");
        Directory.CreateDirectory(from);
        string Source(string name, byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(from, name), bytes);
            return Path.Combine(from, name);
        }
        var added = NfcTags.Add(folder, new[]
        {
            Source("Zelda.bin", FakeTag(2)), Source("link.NFC", FakeNfc(FakeTag(3))), Source("Ganon ガノン 🎮.bin", FakeTag(4, 572)),
            Source("Älpha.bin", FakeTag(5, 532)), Source("short.bin", new byte[100]), Source("blank.bin", new byte[540]),
            Source("few.nfc", FakeNfc(FakeTag(6), 100)), Source("notes.txt", FakeTag(7)), Source("key_retail.bin", FakeTag(8)),
            Path.Combine(from, "missing.bin"),
        });
        Require(Directory.Exists(folder), "Adding makes the folder.");
        Require(added.Select(a => a.Outcome).SequenceEqual(new[]
        {
            NfcTagOutcome.Added, NfcTagOutcome.Added, NfcTagOutcome.Added, NfcTagOutcome.Added, NfcTagOutcome.Refused, NfcTagOutcome.Refused,
            NfcTagOutcome.Refused, NfcTagOutcome.Refused, NfcTagOutcome.Refused, NfcTagOutcome.Refused,
        }), "Added: the dumps; refused: the rest. " + string.Join(", ", added.Select(a => a.Outcome)));
        Require(added.Take(4).All(a => a.Copied && a.Reason == null && a.FileName == Path.GetFileName(a.Source)), "Each goes in under its own file name.");
        Require(added.Skip(4).All(a => !a.Copied && a.FileName == null && a.Reason is { Length: > 0 }) && added[4].Reason!.Contains("100 bytes")
                && added[5].Reason!.Contains("NTAG215") && added[6].Reason!.Contains("100 of") && added[7].Reason!.Contains(".bin or .nfc")
                && added[8].Reason!.Contains("key") && added[9].Reason!.Contains("could not be copied"), "Each refusal says why: "
                + string.Join(" | ", added.Skip(4).Select(a => a.Reason)));
        Require(File.ReadAllBytes(Path.Combine(folder, "Ganon ガノン 🎮.bin")).SequenceEqual(FakeTag(4, 572))
                && File.ReadAllBytes(Path.Combine(folder, "link.NFC")).SequenceEqual(FakeNfc(FakeTag(3))), "The copies are the files, byte for byte.");
        Require(Directory.EnumerateFileSystemEntries(folder).Count() == 4 && Directory.EnumerateFiles(from).Count() == 9,
                "Nothing else is in the folder (no temporary file), and the sources stay.");

        var list = NfcTags.List(folder);
        Require(list.Select(a => a.Name).SequenceEqual(new[] { "Ganon ガノン 🎮", "link", "Zelda", "Älpha" }), "By name, with no ending: " + string.Join(", ", list.Select(a => a.Name)));
        Require(list.Select(a => a.Format).SequenceEqual(new[] { ".bin", ".nfc", ".bin", ".bin" }) && list.All(a => a.Valid && a.Problem == null)
                && list[1].FileName == "link.NFC", "Each with its format.");
        Require(list[0].Model == NfcTags.Model(FakeTag(4)) && list[1].Model == NfcTags.Model(FakeTag(3)) && list[2].Uid == NfcTags.Uid(FakeTag(2))
                && list[3].Model == NfcTags.Model(FakeTag(5)), "Each with its model and its UID.");

        // (what is in the folder already and is no dump is listed, to be deleted; key files, other files and subfolders are not)
        File.WriteAllBytes(Path.Combine(folder, "broken.bin"), new byte[12]);
        File.WriteAllText(Path.Combine(folder, "text.nfc"), "hello");
        File.WriteAllBytes(Path.Combine(folder, "key_retail.bin"), FakeTag(9));
        File.WriteAllBytes(Path.Combine(folder, "KEY_other.nfc"), FakeNfc(FakeTag(9)));
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "x");
        File.WriteAllBytes(Path.Combine(folder, "Zelda.bin.0123.tmp"), FakeTag(2));
        Directory.CreateDirectory(Path.Combine(folder, "sub.bin"));
        File.WriteAllBytes(Path.Combine(folder, "sub.bin", "inside.bin"), FakeTag(10));
        list = NfcTags.List(folder);
        Require(list.Select(a => a.FileName).SequenceEqual(new[] { "broken.bin", "Ganon ガノン 🎮.bin", "link.NFC", "text.nfc", "Zelda.bin", "Älpha.bin" }),
                "Listed: the .bin and .nfc files, not key_ files, other files or folders: " + string.Join(", ", list.Select(a => a.FileName)));
        Require(list.Where(a => !a.Valid).Select(a => a.Name).SequenceEqual(new[] { "broken", "text" }) && list[0].Model == null && list[0].Uid == null
                && list[0].Problem!.Contains("12 bytes") && list[3].Problem!.Contains("Filetype:"), "What is no dump says why.");
        Require(NfcTags.IsKeyFile("key_retail.bin") && NfcTags.IsKeyFile(Path.Combine(folder, "Key_x.bin")) && !NfcTags.IsKeyFile("keyblade.bin")
                && NfcTags.IsCandidate("a.Bin") && !NfcTags.IsCandidate("a.bin.tmp") && !NfcTags.IsCandidate("key_a.nfc"), "Key files, and the two endings.");
        Require(NfcTags.FilesIn(from).Select(Path.GetFileName).SequenceEqual(new[] { "blank.bin", "few.nfc", "Ganon ガノン 🎮.bin", "link.NFC", "short.bin", "Zelda.bin", "Älpha.bin" }),
                "A folder's .bin and .nfc files, for adding a folder: " + string.Join(", ", NfcTags.FilesIn(from).Select(Path.GetFileName)));
        Require(NfcTags.FilesUnder(folder).Select(p => Path.GetRelativePath(folder, p).Replace('\\', '/')).Contains("sub.bin/inside.bin")
                && NfcTags.FilesUnder(folder).Count == NfcTags.FilesIn(folder).Count + 1 && !NfcTags.FilesUnder(folder).Any(NfcTags.IsKeyFile),
                "A folder of folders, for adding a collection: the files under it too, key_ files left out.");
        if (OperatingSystem.IsLinux())
        {
            // (two links back up to the folder, as a Wine or Proton prefix's dosdevices/z: is /: followed, the walk grows as 2^depth
            // and never ends. A link to a file is taken)
            var looped = Path.Combine(root, "looped");
            Directory.CreateDirectory(Path.Combine(looped, "inner"));
            File.WriteAllBytes(Path.Combine(looped, "top.bin"), FakeTag(11));
            File.WriteAllBytes(Path.Combine(looped, "inner", "deep.bin"), FakeTag(12));
            Directory.CreateSymbolicLink(Path.Combine(looped, "inner", "up"), "..");
            Directory.CreateSymbolicLink(Path.Combine(looped, "inner", "up again"), looped);
            File.CreateSymbolicLink(Path.Combine(looped, "linked.bin"), Path.Combine(looped, "inner", "deep.bin"));
            var walk = Task.Run(() => NfcTags.FilesUnder(looped));
            Require(walk.Wait(TimeSpan.FromSeconds(20)), "A folder with two links back up to it: the walk ends.");
            Require(walk.Result.Select(p => Path.GetRelativePath(looped, p)).SequenceEqual(new[] { "inner/deep.bin", "linked.bin", "top.bin" }),
                    "Links to folders are not gone into; a link to a file is taken: " + string.Join(", ", walk.Result.Select(p => Path.GetRelativePath(looped, p))));
            // (and added as the file it leads to: its size is the file's, not the link's own)
            var fromLinks = Path.Combine(root, "from links");
            var linkedAdded = NfcTags.Add(fromLinks, walk.Result);
            var copy = Path.Combine(fromLinks, "linked.bin");
            Require(linkedAdded.Select(a => (a.Outcome, a.FileName)).SequenceEqual(new[]
                    { (NfcTagOutcome.Added, (string?)"deep.bin"), (NfcTagOutcome.Added, "linked.bin"), (NfcTagOutcome.Added, "top.bin") })
                    && File.ReadAllBytes(copy).SequenceEqual(FakeTag(12)) && new FileInfo(copy).LinkTarget == null,
                    "A linked .bin is added as the file it leads to, copied: " + string.Join(", ", linkedAdded.Select(a => $"{a.FileName}: {a.Outcome} {a.Reason}")));
            // (a FIFO named as a dump, and a link to one: opening it would block until something writes to it, so the list,
            // which the page runs as it opens, would never come back)
            var piped = Path.Combine(root, "piped");
            Directory.CreateDirectory(piped);
            File.WriteAllBytes(Path.Combine(piped, "Zelda.bin"), FakeTag(13));
            using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", new[] { Path.Combine(piped, "pipe.bin"), Path.Combine(piped, "pipe.nfc") }))
                mkfifo.WaitForExit();
            File.CreateSymbolicLink(Path.Combine(piped, "to pipe.bin"), Path.Combine(piped, "pipe.bin"));
            var listed = Task.Run(() => NfcTags.List(piped));
            Require(listed.Wait(TimeSpan.FromSeconds(20)), "A FIFO named .bin or .nfc, or a link to one, in the folder: the list comes back.");
            Require(listed.Result.Count(t => t.Valid) == 1 && listed.Result.Count(t => !t.Valid) == 3,
                    "The FIFOs and the link to one are listed as no dumps, the real one as a tag: "
                    + string.Join(", ", listed.Result.Select(t => $"{System.IO.Path.GetFileName(t.Path)}: {t.Problem}")));
        }
        File.WriteAllBytes(Path.Combine(root, "large.bin"), new byte[600]);
        Require(NfcTags.Add(Path.Combine(root, "from links 2"), new[] { Path.Combine(root, "large.bin") }).Single() is { Outcome: NfcTagOutcome.Refused } large
                && large.Reason!.Contains("more than 572 bytes"), "A .bin larger than any dump is refused, from no more than its first 573 bytes.");

        // ---- a name that is taken ----
        // (the move itself never replaces a file: one that took the name after the check is kept, and nothing is left beside it)
        var taken = Path.Combine(root, "taken");
        Directory.CreateDirectory(taken);
        File.WriteAllBytes(Path.Combine(taken, "Scanned 04.bin"), FakeTag(30));
        Require(!AtomicFile.WriteNew(Path.Combine(taken, "Scanned 04.bin"), FakeTag(31))
                && File.ReadAllBytes(Path.Combine(taken, "Scanned 04.bin")).SequenceEqual(FakeTag(30))
                && AtomicFile.WriteNew(Path.Combine(taken, "Scanned 04 (2).bin"), FakeTag(31)) && Directory.GetFiles(taken).Length == 2,
                "A write that may not replace: refused over a file that is there, which is kept; made where none is.");
        var before = File.ReadAllBytes(Path.Combine(folder, "Zelda.bin"));
        var again = NfcTags.Add(folder, new[] { Path.Combine(from, "Zelda.bin") });
        Require(again.Single() is { Outcome: NfcTagOutcome.Same, FileName: "Zelda.bin", Copied: false } && !File.Exists(Path.Combine(folder, "Zelda (2).bin")),
                "The same file again: left as it is.");
        var other = Path.Combine(root, "other");
        Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(other, "Zelda.bin"), FakeTag(20));
        var numbered = NfcTags.Add(folder, new[] { Path.Combine(other, "Zelda.bin") });
        Require(numbered.Single() is { Outcome: NfcTagOutcome.Numbered, FileName: "Zelda (2).bin", Copied: true }
                && File.ReadAllBytes(Path.Combine(folder, "Zelda.bin")).SequenceEqual(before)
                && File.ReadAllBytes(Path.Combine(folder, "Zelda (2).bin")).SequenceEqual(FakeTag(20)), "A different file of that name: added as Zelda (2).bin, the first kept.");
        Require(NfcTags.Add(folder, new[] { Path.Combine(other, "Zelda.bin") }).Single() is { Outcome: NfcTagOutcome.Same, FileName: "Zelda (2).bin" },
                "The numbered one again: left as it is.");
        File.WriteAllBytes(Path.Combine(other, "Zelda.bin"), FakeTag(21));
        Require(NfcTags.Add(folder, new[] { Path.Combine(other, "Zelda.bin") }).Single() is { Outcome: NfcTagOutcome.Numbered, FileName: "Zelda (3).bin" },
                "A third: Zelda (3).bin.");
        // (a folder of that name is in the way too, and is never written over)
        Source("sub.bin", FakeTag(22));
        Require(NfcTags.Add(folder, new[] { Path.Combine(from, "sub.bin") }).Single() is { Outcome: NfcTagOutcome.Numbered, FileName: "sub (2).bin" }
                && File.Exists(Path.Combine(folder, "sub.bin", "inside.bin")), "A folder of that name is left alone.");
        // (a file of the folder itself, added to it: there already)
        Require(NfcTags.Add(folder, new[] { Path.Combine(folder, "Älpha.bin") }).Single().Outcome == NfcTagOutcome.Same, "A file of the NFC folder itself.");
        // (an invalid file never takes a name, or replaces a dump)
        File.WriteAllBytes(Path.Combine(other, "Zelda.bin"), new byte[540]);
        Require(NfcTags.Add(folder, new[] { Path.Combine(other, "Zelda.bin") }).Single().Outcome == NfcTagOutcome.Refused
                && File.ReadAllBytes(Path.Combine(folder, "Zelda.bin")).SequenceEqual(before) && !File.Exists(Path.Combine(folder, "Zelda (4).bin")),
                "A file that is no dump replaces nothing.");
        Require(!Directory.EnumerateFiles(folder).Any(f => f.EndsWith(".tmp", StringComparison.Ordinal) && !f.EndsWith("0123.tmp", StringComparison.Ordinal)),
                "No temporary file is left.");

        // ---- deleting ----
        var outside = Path.Combine(root, "data", "longshot", "outside.bin");
        File.WriteAllBytes(outside, FakeTag(30));
        var gone = NfcTags.Delete(folder, new[]
        {
            "Zelda (2).bin", "broken.bin", "Ganon ガノン 🎮.bin", "nothing.bin", "key_retail.bin", "readme.txt", "sub.bin",
            Path.Combine("..", "outside.bin"), outside, Path.Combine("sub.bin", "inside.bin"),
        });
        Require(gone.SequenceEqual(new[] { "Zelda (2).bin", "broken.bin", "Ganon ガノン 🎮.bin" }), "Deleted: the three named, of the folder: " + string.Join(", ", gone));
        Require(File.Exists(outside) && File.Exists(Path.Combine(folder, "key_retail.bin")) && File.Exists(Path.Combine(folder, "readme.txt"))
                && File.Exists(Path.Combine(folder, "sub.bin", "inside.bin")), "Never a file outside the folder, a key file, another file or a folder.");
        Require(NfcTags.List(folder).Select(a => a.FileName).SequenceEqual(new[] { "link.NFC", "sub (2).bin", "text.nfc", "Zelda.bin", "Zelda (3).bin", "Älpha.bin" }),
                "The list after: " + string.Join(", ", NfcTags.List(folder).Select(a => a.FileName)));
        Require(NfcTags.Delete(Path.Combine(root, "no such folder"), new[] { "a.bin" }).Count == 0, "Nothing to delete in no folder.");
    }
}
