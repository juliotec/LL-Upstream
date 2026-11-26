using LongshotLauncher.Core;

/// Longshot's new builder, longshot-builder (Core/LongshotBuilder.cs, Core/BuildEvents.cs, Core/Builders.cs): which builder a
/// release's files choose, its command lines, its events (recorded lines in tests/fixtures/longshot-builder, as build-core's
/// report.rs writes them) read into the build's progress, its error codes in the launcher's words, its check's answers, and
/// the .nup its game reads in place
static partial class Program
{
    static void LongshotBuildChecks(string fixtures)
    {
        string[] Lines(string name) => File.ReadAllLines(Path.Combine(fixtures, "longshot-builder", name));

        // ---- the events, one by one
        Require(BuildEvents.Parse("""{"v":1,"type":"stage","stage":"compile","text":"Compiling the game"}""")
                    is BuildEvent.Stage { Id: "compile", Text: "Compiling the game" }, "A stage event.");
        Require(BuildEvents.Parse("""{"v":1,"type":"progress","stage":"compile","fraction":0.5,"overall":0.565}""")
                    is BuildEvent.Progress { Id: "compile", Fraction: 0.5, Overall: 0.565 }, "A progress event, with the whole build's share.");
        Require(BuildEvents.Parse("""{"v":1,"type":"progress","stage":"lift","fraction":7,"overall":-1}""") is BuildEvent.Progress { Fraction: 1, Overall: 0 },
                "A progress out of 0..1 is held to it.");
        Require(BuildEvents.Parse("""{"v":1,"type":"compile_plan","changed":12,"total":148}""") is BuildEvent.CompilePlan { Changed: 12, Total: 148 },
                "A compile plan.");
        Require(BuildEvents.Parse("""{"v":1,"type":"unit","name":"lifted_7101a2c0.ll","seconds":41.2,"result":"compiled"}""")
                    is BuildEvent.Unit { Name: "lifted_7101a2c0.ll", Seconds: 41.2, Result: "compiled" }, "A unit.");
        Require(BuildEvents.Parse("""{"v":1,"type":"eta","seconds":310}""") is BuildEvent.Eta { Seconds: 310 }, "An eta, a whole number of seconds.");
        Require(BuildEvents.Parse("""{"v":1,"type":"warning","code":"slow_disk","text":"..."}""") is BuildEvent.Warning { Code: "slow_disk" }, "A warning.");
        Require(BuildEvents.Parse("""{"v":1,"type":"check","ok":true,"build":"e0f3ccfa","text":"t"}""") is BuildEvent.Check { Ok: true, Build: "e0f3ccfa" },
                "A check.");
        Require(BuildEvents.Parse("""{"v":1,"type":"error","code":"disk_full","text":"No room."}""") is BuildEvent.Error { Code: "disk_full", Text: "No room." },
                "An error, with its code.");
        Require(BuildEvents.Parse("""{"v":1,"type":"done","folder":"/g","seconds":812.0,"compile_seconds":530.4,"previous_seconds":null}""")
                    is BuildEvent.Done { Folder: "/g", Seconds: 812, CompileSeconds: 530.4, PreviousSeconds: null }
                && BuildEvents.Parse("""{"v":1,"type":"done","folder":"/g","seconds":1,"compile_seconds":1,"previous_seconds":790.1}""")
                    is BuildEvent.Done { PreviousSeconds: 790.1 }, "Done, with the last build's time or none.");
        Require(BuildEvents.Parse(Lines("share.jsonl")[0]) is BuildEvent.Shared { Pipelines: 555, Template: "t\nu" } shared
                && shared.Zip.EndsWith(".zip", StringComparison.Ordinal), "share's answer.");
        foreach (var line in new[]
                 {
                     "", "   ", "not json", "{", "[1,2]", """{"type":"stage","stage":"read","text":"no version"}""",
                     """{"v":2,"type":"stage","stage":"read","text":"a later version"}""", """{"v":"1","type":"stage","stage":"read"}""",
                     """{"v":1,"type":"dance"}""", """{"v":1,"type":"progress","stage":"read"}""", """{"v":1,"type":"error","text":"no code"}""",
                     """{"v":1,"type":"stage","stage":3}""", """{"v":1,"type":"check","ok":"yes"}""", """{"v":1,"type":"compile_plan","changed":-1,"total":3}""",
                 })
            Require(BuildEvents.Parse(line) == null, "Not an event of version 1 with its fields, passed over: " + line);
        Require(BuildEvents.Parse("""{"v":1,"type":"log","text":"kept","a_later_field":{"x":[1]}}""") is BuildEvent.Log { Text: "kept" },
                "A field it does not know is passed over.");

        // ---- a recorded build, through the progress the window shows (BuildProgress)
        var home = new LauncherHome(Path.Combine(scratch, "lb-home"));
        var win = new Toolchain("v4.0.0", Path.Combine(home.Toolchains, "v4.0.0"), "longshot-builder.exe");
        var lin = new Toolchain("v4.0.0", Path.Combine(home.Toolchains, "v4.0.0"), "longshot-builder");
        var linExe = lin.BuilderExe!;
        var builder = new LongshotBuilder(lin, linExe, Platform.Linux, home, "/L/Longshot.AppImage", _ => null);
        var progress = new BuildProgress(download: false, shaders: true);
        var said = new List<MakeGameLine>();
        foreach (var line in Lines("build.jsonl"))
            if (LongshotBuilder.Read(line) is { } parsed)
            {
                said.Add(parsed);
                progress.Apply(parsed);
            }
        Require(said.Count(l => l is MakeGameLine.Stage) == 5
                && said.OfType<MakeGameLine.Warning>().Select(w => w.Code).SequenceEqual(["slow_disk", BuildEvents.TranslationDamaged])
                && said.OfType<MakeGameLine.Plain>().Count() == 2, "Its stages, its warnings and its log lines; a later version's and an unknown type's passed over.");
        // (translation_damaged: the PC changed what the build made, the build went on. The Ready page says it in the launcher's
        // words; the other warnings, and shaders_not_prepared (the shaders' row says it), only in the details)
        Require(progress.Warnings.SequenceEqual(["slow_disk", BuildEvents.TranslationDamaged]), "The warnings said, each once: " + string.Join(", ", progress.Warnings));
        Require(Problems.FromWarning(BuildEvents.TranslationDamaged) is { } damaged && damaged.Contains("memory", StringComparison.Ordinal)
                && damaged.Contains("drive", StringComparison.Ordinal) && damaged.Contains("XMP", StringComparison.Ordinal),
                "translation_damaged: the launcher's words name the memory, the overclock and the drive.");
        foreach (var code in new[] { "slow_disk", "low_disk_space", BuildEvents.ShadersNotPrepared, "prompts_textures", "a_later_warning", "", null })
            Require(Problems.FromWarning(code) == null, $"{code}: only the details say it.");
        Require(progress.Finished && progress.Overall == 1 && progress.Folder == "/games/Longshot" && progress.Error == null,
                "The build done: the bar full, its folder said.");
        Require(progress.FilesChanged == 2 && progress.FilesTotal == 3 && progress.FilesCompiled == 2,
                "The compile plan, and its units compiled (compiled and retried count, a cached one does not).");
        Require(progress.Eta == 310 && progress.EtaLines == 1, "The compile's time left.");
        var shaders = progress.Stages.Single(s => s.Id == BuildProgress.ShadersStage);
        Require(shaders.State == StageState.Skipped && shaders.Note == "Left to the game's first start: the GPU driver stopped responding",
                "shaders_not_prepared skips the shaders' row, saying why: " + shaders.Note);
        Require(progress.Stages.Where(s => s.Id != BuildProgress.ShadersStage).All(s => s.State == StageState.Done), "Every other stage done.");

        progress = new BuildProgress(download: false, shaders: true);
        foreach (var line in Lines("nup-moved.jsonl"))
            if (LongshotBuilder.Read(line) is { } parsed) progress.Apply(parsed);
        Require(!progress.Finished && progress.ErrorCode == "nup_moved" && progress.Error!.Contains("Build again from the .nup", StringComparison.Ordinal)
                && progress.Stages.First(s => s.Id == "read").State == StageState.Waiting, "A stopped build: its error's code and text, where it stopped.");
        Require(LongshotBuilder.Read("a line a later builder writes on stdout") is MakeGameLine.Plain { Text: "a line a later builder writes on stdout" }
                && LongshotBuilder.Read("""{"v":9,"type":"stage","stage":"read"}""") == null, "Another line goes to the details; another version's event nowhere.");

        // ---- the codes in the launcher's words, else the builder's
        Require(Problems.FromCode("nup_moved") is { Kind: ProblemKind.NupMoved } moved && moved.Fixes.Single().Kind == FixKind.PickOtherFiles
                && Problems.FromCode("nup_missing")?.Kind == ProblemKind.NupMoved, "nup_moved: pick the .nup again.");
        foreach (var (code, kind) in new (string, ProblemKind)[]
                 {
                     ("game_not_found", ProblemKind.NupMoved), ("not_a_nup", ProblemKind.NotAGameFile), ("unsupported_build", ProblemKind.WrongGameVersion),
                     ("nup_incomplete", ProblemKind.IncompleteDump), ("romfs_incomplete", ProblemKind.IncompleteDump), ("disk_full", ProblemKind.DiskFull),
                     ("game_running", ProblemKind.GameRunning), ("work_folder_busy", ProblemKind.BuildRunning),
                     ("game_folder_busy", ProblemKind.BuildRunning), ("dump_damaged", ProblemKind.IncompleteDump),
                     ("game_folder_not_writable", ProblemKind.FolderRefused), ("game_folder_overlaps", ProblemKind.FolderRefused),
                     ("game_files_overlap", ProblemKind.FolderRefused), ("romfs_folder_theirs", ProblemKind.FolderRefused),
                     ("tools_incomplete", ProblemKind.ToolsIncomplete),
                 })
            Require(Problems.FromCode(code)?.Kind == kind, $"{code}: {kind}.");
        // (a .nup the game refuses, or one too deep for it: the launcher's own words, another .nup or the same one moved)
        Require(Problems.FromCode("nup_damaged") is { Kind: ProblemKind.NupDamaged } broken && broken.Fixes.Single().Kind == FixKind.PickOtherFiles
                && Problems.FromCode("nup_path_too_long") is { Kind: ProblemKind.NupPathTooLong } deep && deep.Fixes.Single().Kind == FixKind.PickOtherFiles
                && deep.Detail.Contains("shorter path", StringComparison.Ordinal),
                "nup_damaged: pick other files; nup_path_too_long: move the .nup to a shorter path.");
        foreach (var code in new[] { "nup_path_too_long", "nup_damaged", "not_the_same_nup" })
            Require(LongshotBuilder.ReadCheck([$$$"""{"v":1,"type":"error","code":"{{{code}}}","text":"t"}"""], null, 1).Status == GameCheckStatus.Unreadable,
                    $"{code}: the game files'.");
        Require(Problems.FromCode("game_files_overlap")!.Fixes.Any(f => f.Kind == FixKind.ChooseOtherFolder)
                && Problems.FromCode("romfs_folder_theirs") is { } theirs && theirs.Fixes.Any(f => f.Kind == FixKind.ChooseOtherFolder)
                && theirs.Detail.Contains("romfs folder", StringComparison.Ordinal)
                && Problems.FromCode("disk_full")!.Fixes.Any(f => f.Kind == FixKind.ChooseOtherFolder), "A folder's problem offers another folder.");
        foreach (var code in new[] { "io", "tool_failed", "bad_data", "internal_error", "compile_failed", "drive_error", "game_folder_link", "a_later_code", "" })
            Require(Problems.FromCode(code) == null, $"{code}: no words of the launcher's, the builder's text is said.");
        Require(Problems.FromCode(null) == null, "make_game.py has no codes.");

        // ---- the check's answers
        var ok = LongshotBuilder.ReadCheck(Lines("check-ok.jsonl"), null, 0);
        Require(ok.Ok && ok.Why == null, "check: the supported build.");
        var other = LongshotBuilder.ReadCheck(Lines("check-other.jsonl"), null, 1);
        Require(other.Status == GameCheckStatus.OtherVersion && other.Code == "unsupported_build" && other.Why == Tr.T(Problems.FromCode("unsupported_build")!.Detail),
                "check: another build, in the launcher's words.");
        var bad = LongshotBuilder.ReadCheck(Lines("check-not-a-nup.jsonl"), null, 1);
        Require(bad.Status == GameCheckStatus.Unreadable && bad.Code == "not_a_nup" && bad.Why == Tr.T(Problems.FromCode("not_a_nup")!.Detail),
                "check: not a .nup is the file's.");
        var links = LongshotBuilder.ReadCheck(["""{"v":1,"type":"error","code":"dump_has_links","text":"The dump has links in it."}"""], null, 1);
        Require(links.Status == GameCheckStatus.Unreadable && links.Why == "The dump has links in it.", "A file's code with no words of the launcher's: the builder's.");
        var tools = LongshotBuilder.ReadCheck(["""{"v":1,"type":"error","code":"tools_incomplete","text":"Longshot's tools are incomplete."}"""], null, 1);
        Require(tools.Status == GameCheckStatus.CheckFailed && tools.ThisPc, "tools_incomplete: this PC's, not the file's.");
        var own = LongshotBuilder.ReadCheck(["""{"v":1,"type":"error","code":"io","text":"The builder stopped on an error."}"""], null, 1);
        Require(own.Status == GameCheckStatus.CheckFailed && !own.ThisPc, "The builder's own failure: neither the file's nor this PC's.");
        var refused = LongshotBuilder.ReadCheck([], "longshot-builder: unknown argument \"--frobnicate\"", 2);
        Require(refused.Status == GameCheckStatus.CheckFailed && refused.Text.Contains("--frobnicate", StringComparison.Ordinal),
                "Nothing said, a command line it does not take: stderr says why.");
        // (exit status 2: "longshot-builder: why", then its usage: the first such line, not the usage's last)
        string? why = null;
        foreach (var l in new[] { "", "longshot-builder: unknown argument \"--frobnicate\"", "usage: longshot-builder build --game <file.nup | dump folder>",
                                  "       longshot-builder --version", "Every command also takes --cache <folder>", "--builder-exe <file> (a launcher's)" })
            why = LongshotBuilder.WhyFrom(why, l);
        Require(why == "longshot-builder: unknown argument \"--frobnicate\"", "Exit status 2: its own line of why, not the usage's last: " + why);
        Require(LongshotBuilder.WhyFrom(LongshotBuilder.WhyFrom(null, "first"), "last") == "last" && LongshotBuilder.WhyFrom("kept", "  ") == "kept",
                "Without one, the last line said.");
        // (drive_error is the folders' (the work or game folder's drive), not the game files'; internal_error the builder's own)
        var drive = LongshotBuilder.ReadCheck(["""{"v":1,"type":"error","code":"drive_error","text":"Your drive could not read x."}"""], null, 1);
        Require(drive.Status == GameCheckStatus.CheckFailed && drive.ThisPc, "drive_error: this PC's, not the file's.");
        var bug = LongshotBuilder.ReadCheck(["""{"v":1,"type":"error","code":"internal_error","text":"A bug: see build.log."}"""], null, 1);
        Require(bug.Status == GameCheckStatus.CheckFailed && !bug.ThisPc, "internal_error: the builder's own.");
        foreach (var code in new[] { "nup_drive_error", "nup_locked", "no_romfs", "extraction_link", "nup_path_not_unicode" })
            Require(LongshotBuilder.ReadCheck([$$$"""{"v":1,"type":"error","code":"{{{code}}}","text":"t"}"""], null, 1).Status == GameCheckStatus.Unreadable,
                    $"{code}: the game files'.");
        // (the launcher's sets are the doc's lists: a copy of build-events.md's Codes table, which test_launcher_core.py --longshot checks)
        var table = File.ReadAllLines(Path.Combine(fixtures, "longshot-builder", "codes.md")).Where(l => l.StartsWith('|')).ToList();
        HashSet<string> Row(string where)
        {
            var row = table.Single(l => l.StartsWith("| " + where + " |", StringComparison.Ordinal));
            var codes = System.Text.RegularExpressions.Regex.Replace(row[(where.Length + 4)..], @"\([^()]*\)", "");
            return System.Text.RegularExpressions.Regex.Matches(codes, "`([a-z0-9_]+)`").Select(m => m.Groups[1].Value).ToHashSet();
        }
        Require(Row("The game files").SetEquals(LongshotBuilder.FileCodes), "FileCodes are the doc's game files' codes: "
                + string.Join(", ", Row("The game files").Except(LongshotBuilder.FileCodes).Concat(LongshotBuilder.FileCodes.Except(Row("The game files")))));
        Require(Row("The builder's own failures").SetEquals(LongshotBuilder.OwnCodes), "OwnCodes are the doc's builder's own failures.");
        Require(Row("The folders").Contains("drive_error") && !LongshotBuilder.FileCodes.Contains("drive_error"), "drive_error is the folders'.");

        // ---- the builder: a version installed from a longshot-builder manifest, its one file
        var winExe = win.BuilderExe;
        Require(winExe == Path.Combine(win.Folder, "longshot-builder.exe") && linExe == Path.Combine(lin.Folder, "longshot-builder"),
                "Its one file in its version's folder.");
        Require(Builders.For(win, Platform.Windows, home, "L") is { Exe: var w } && w == winExe, "A version builds with its longshot-builder.");

        // ---- the command lines
        var full = new LongshotBuilder(lin, linExe, Platform.Linux, home, "/L/Longshot.AppImage", _ => null);
        var request = new BuildRequest(new BuildSource.Nup("/g/game.nup"), "/games/L", Language: "fr", Work: "/w", Jobs: 6, MemoryGb: 16,
                                       PrepareShaders: false, Dlss: true, DlssLicense: true, Fsr: true, FsrLicense: true, Xess: true, XessLicense: true);
        var run = full.Build(request);
        Require(run.Command.FileName == linExe && run.Command.WorkingDirectory == lin.Folder && run.Command.LowPriority, "longshot-builder itself, at low priority.");
        Require(run.Command.Arguments.SequenceEqual(new[]
                {
                    "build", "--game", "/g/game.nup", "--out", "/games/L", "--language", "fr", "--output", "json",
                    "--jobs", "6",
                    "--upscaler", "dlss,fsr4", "--accept-licenses", "nvidia", "--no-prepare-shaders",
                    "--cache", Path.Combine("/w", Disk.LongshotBuildCache), "--builder-exe", "/L/Longshot.AppImage",
                }), "build's arguments, the launcher's settings as flags (Linux: NVIDIA's licence for DLSS's SDK; FSR 4 and XeSS have nothing to download): " + string.Join(" ", run.Command.Arguments));
        Require(run.Dlss && run.Fsr && !run.Xess && run.Notes.Count == 0, "What it builds in (XeSS is Windows' only).");
        Require(!run.Command.Arguments.Contains(BuilderVersions.SendStatistics)
                && full.Build(request with { SendStatistics = true }).Command.Arguments.Contains(BuilderVersions.SendStatistics)
                && full.Build(request with { Source = new BuildSource.Update("/games/L"), SendStatistics = true }).Command.Arguments.Contains(BuilderVersions.SendStatistics),
                "The player's yes to statistics goes to build and update too: the builder sends the build's end.");
        var env = run.Command.Environment;
        Require(!env.ContainsKey("LONGSHOT_CACHE") && !env.ContainsKey("LONGSHOT_BUILDER_EXE") && !env.ContainsKey("LONGSHOT_NGX_SDK")
                && env["LONGSHOT_JOBS"] == null && env["LONGSHOT_ROOT"] == null && env["LONGSHOT_TOOLS"] == null
                && !env.ContainsKey("LONGSHOT_SYSROOT") && !env.ContainsKey("EMSDK") && !env.ContainsKey("LONGSHOT_ROMFS_FROM"),
                "Its environment: the launcher's settings are flags, no tools' folders (it carries its own), nothing of the PC's in their place.");
        Directory.CreateDirectory(home.NgxSdk);
        var sdk = new LongshotBuilder(lin, linExe, Platform.Linux, home, "L", _ => null).Build(request with { PrepareShaders = true });
        Require(sdk.Dlss && sdk.Command.Arguments.SkipWhile(a => a != "--upscaler").Skip(1).First() == "dlss,fsr4"
                && !sdk.Command.Arguments.Contains("--ngx-sdk") && sdk.Command.Arguments.SkipWhile(a => a != "--accept-licenses").Skip(1).First() == "nvidia"
                && !sdk.Command.Environment.ContainsKey("LONGSHOT_NGX_SDK") && !sdk.Command.Arguments.Contains("--no-prepare-shaders"),
                "DLSS: the builder's own pinned SDK under NVIDIA's licence, never the one an earlier make_game.py build fetched (--ngx-sdk).");
        Directory.Delete(home.NgxSdk);
        // (Build memory: the compilers' gate, in whole GB; none on Automatic)
        Require(run.Command.Environment["LONGSHOT_MEM_LIMIT_GB"] == "16" && !builder.Build(request with { MemoryGb = null }).Command.Environment.ContainsKey("LONGSHOT_MEM_LIMIT_GB"),
                "Build memory is LONGSHOT_MEM_LIMIT_GB.");
        Throws<ArgumentOutOfRangeException>(() => builder.Build(request with { MemoryGb = 0 }), "Build memory must be positive.");
        // (the PC's shader store kept for the shaders' preparation, as Play keeps it: one store for both)
        var stored = new LongshotBuilder(lin, linExe, Platform.Linux, home, "L", k => k == LongshotBuilder.ShaderStoreVar ? "/my/store" : null).Build(request);
        Require(stored.Command.Environment[LongshotBuilder.ShaderStoreVar] == "/my/store" && run.Command.Environment[LongshotBuilder.ShaderStoreVar] == null
                && GamePlay.Environment(Platform.Linux, new Dictionary<string, string> { [LongshotBuilder.ShaderStoreVar] = "/my/store", ["LONGSHOT_ROOT"] = "/r" })
                       is var played && played[LongshotBuilder.ShaderStoreVar] == "/my/store" && !played.ContainsKey("LONGSHOT_ROOT"),
                "LONGSHOT_SHADER_STORE: the PC's, set again after the drop, and kept by Play on Linux; unset, none.");
        // (longshot-builder's data, its notes of the licences accepted per game folder: the launcher's own folder in every run, never
        // a developer's LONGSHOT_DATA nor the builder's default, so Remove everything takes them with the launcher's home)
        var developer = new LongshotBuilder(lin, linExe, Platform.Linux, home, "L", k => k == LongshotBuilder.DataVar ? "/dev/data" : null);
        Require(BuildRegistry.Holds(home.Root, home.BuilderData) && !BuildRegistry.SameFolder(home.BuilderData, home.Root)
                && new[] { run.Command, developer.Build(request).Command, developer.Check(new BuildSource.Nup("/g/game.nup")),
                           developer.Build(request with { Source = new BuildSource.Update("/games/L"), Out = null }).Command }
                       .All(c => c.Environment[LongshotBuilder.DataVar] == home.BuilderData
                                 && c.StartInfo().Environment[LongshotBuilder.DataVar] == home.BuilderData),
                "LONGSHOT_DATA: the launcher's own folder in its home, for build, update and check alike.");
        var unaccepted = new LongshotBuilder(lin, linExe, Platform.Linux, home, "L", _ => null)
            .Build(request with { DlssLicense = false, FsrLicense = false, XessLicense = false });
        Require(!unaccepted.Dlss && !unaccepted.Fsr && !unaccepted.Command.Arguments.Contains("--accept-licenses") && !unaccepted.Command.Arguments.Contains("--ngx-sdk")
                && unaccepted.Command.Arguments.SkipWhile(a => a != "--upscaler").Skip(1).First() == "none",
                "No licence accepted: no upscaler asked for, and no licence named for the player.");
        var bare = builder.Build(request with { Dlss = false, Fsr = false, Jobs = null, Language = null, Work = null });
        Require(bare.Command.Arguments.SequenceEqual(new[]
                {
                    "build", "--game", "/g/game.nup", "--out", "/games/L", "--output", "json",
                    "--upscaler", "none", "--no-prepare-shaders",
                    "--cache", Path.Combine(home.Work, Disk.LongshotBuildCache), "--builder-exe", "/L/Longshot.AppImage",
                }),
                "No upscaler is said as none: " + string.Join(" ", bare.Command.Arguments));
        var update = builder.Build(request with { Source = new BuildSource.Update("/games/L"), Out = null, Dlss = false, Fsr = false });
        Require(update.Command.Arguments.Take(4).SequenceEqual(new[] { "update", "/games/L", "--output", "json" }) && !update.Command.Arguments.Contains("--game")
                && update.Command.Arguments.SkipWhile(a => a != "--cache").Skip(1).FirstOrDefault() == Path.Combine("/w", Disk.LongshotBuildCache),
                "An update of a folder: update, which builds it from its own record.");
        var winUpdate = new LongshotBuilder(win, winExe, Platform.Windows, home, "L", _ => null)
            .Build(request with { Source = new BuildSource.Update("/games/L"), Out = null });
        Require(winUpdate.Command.Arguments.SkipWhile(a => a != "--upscaler").Take(4).SequenceEqual(new[] { "--upscaler", "dlss,fsr4,xess", "--accept-licenses", "nvidia,amd,intel" })
                && winUpdate.Xess, "An update on Windows: every licence accepted named, as for a build.");
        Require(LongshotBuilder.Licences(request with { Fsr = false }, Platform.Windows).SequenceEqual(new[] { "nvidia", "intel" })
                && LongshotBuilder.Licences(request with { XessLicense = false }, Platform.Windows).SequenceEqual(new[] { "nvidia", "amd" })
                && LongshotBuilder.Licences(request, Platform.Linux).SequenceEqual(new[] { "nvidia" }), "The licences: the upscalers asked for whose licence was accepted.");

        // (a licence the build was refused for: in the launcher's words, its prompt offered, the vendor read from the error)
        progress = new BuildProgress(download: false, shaders: true);
        foreach (var line in Lines("license-not-accepted.jsonl"))
            if (LongshotBuilder.Read(line) is { } parsed) progress.Apply(parsed);
        Require(progress.ErrorCode == "license_not_accepted", "license_not_accepted: its code kept: " + progress.ErrorCode);
        Require(Problems.FromCode(progress.ErrorCode) is { Kind: ProblemKind.LicenceNeeded } lic && lic.Fixes.Single().Kind == FixKind.AcceptLicence,
                "license_not_accepted: in the launcher's words, its licence prompt offered.");
        Require(LongshotBuilder.LicenceOf(progress.Error) == "amd", "The vendor its error names, AMD here: " + progress.Error);
        Require(LongshotBuilder.LicenceOf("... (--accept-licenses nvidia), or build without it.") == "nvidia" && LongshotBuilder.LicenceOf("no vendor") == null
                && LongshotBuilder.LicenceOf(null) == null, "The vendor an error names, or none.");
        var folders = builder.Build(request with { Source = new BuildSource.Folders("/d/dump/romfs", "/d/dump/exefs/"), Dlss = false });
        Require(folders.Command.Arguments[2] == "/d/dump", "A dump folder: the folder holding romfs and exefs.");
        Throws<ArgumentException>(() => builder.Build(request with { Source = new BuildSource.Folders("/a/romfs", "/b/exefs") }),
                                  "romfs and exefs apart: refused, said.");
        Throws<ArgumentException>(() => builder.Build(request with { Jobs = 0 }), "Build workers must be positive.");
        Require(builder.Check(new BuildSource.Nup("/g/game.nup"), "/w").Arguments.SequenceEqual(new[]
                {
                    "check", "--game", "/g/game.nup", "--output", "json", "--cache", Path.Combine("/w", Disk.LongshotBuildCache), "--builder-exe", "/L/Longshot.AppImage",
                }) && builder.Check(new BuildSource.Nup("/g/game.nup")).Arguments.SkipWhile(a => a != "--cache").Skip(1).FirstOrDefault()
                      == Path.Combine(home.Work, Disk.LongshotBuildCache), "check's arguments, with the launcher's settings.");
        var winRun = new LongshotBuilder(win, winExe, Platform.Windows, home, "C:/L/LongshotLauncher.exe", _ => null)
            .Build(request with { IntelCompiles = GamePlay.IntelCompilesOneAtATime, Fsr = false });
        Require(!winRun.Command.Environment.ContainsKey("RX_CPM") && winRun.Command.Environment["LONGSHOT_TOOLS"] == null
                && winRun.Command.Environment[GamePlay.IntelCompilesVar] == "0" && winRun.Xess
                && winRun.Command.Arguments.SkipWhile(a => a != "--accept-licenses").Skip(1).First() == "nvidia,intel",
                "Windows: no tools' folders, Intel shader compiles for the shaders' preparation, XeSS under Intel's licence.");
        // (the PC's own LONGSHOT_, RX_ and A64_ variables never reach it: they would redirect the build; the launcher's own are set again)
        var stray = new[]
        {
            ("LONGSHOT_ROOT", "/stray/tree"), ("RX_CPM", "/stray/cpm"), ("A64_TRACE", "1"), ("LONGSHOT_STORE", "/stray/store"), ("LONGSHOT_CACHE", "/stray/cache"),
            ("LONGSHOT_BUILDER_EXE", "/stray/exe"), ("LONGSHOT_NGX_SDK", "/stray/ngx"),
        };
        foreach (var (k, v) in stray) Environment.SetEnvironmentVariable(k, v);
        try
        {
            var started = winRun.Command.StartInfo().Environment;
            Require(!started.ContainsKey("LONGSHOT_ROOT") && !started.ContainsKey("RX_CPM") && !started.ContainsKey("A64_TRACE") && !started.ContainsKey("LONGSHOT_STORE")
                    && !started.ContainsKey("LONGSHOT_CACHE") && !started.ContainsKey("LONGSHOT_BUILDER_EXE") && !started.ContainsKey("LONGSHOT_NGX_SDK")
                    && winRun.Command.Arguments.SkipWhile(a => a != "--builder-exe").Skip(1).FirstOrDefault() == "C:/L/LongshotLauncher.exe"
                    && started[GamePlay.IntelCompilesVar] == "0" && started.ContainsKey("PATH") == (Environment.GetEnvironmentVariable("PATH") != null),
                    "A stray LONGSHOT_ROOT, RX_CPM, A64_, LONGSHOT_STORE, LONGSHOT_CACHE, LONGSHOT_BUILDER_EXE or LONGSHOT_NGX_SDK on the PC doesn't reach " +
                    "longshot-builder; the launcher's own settings are flags, its Intel switch a variable, the rest of the PC's passed on.");
            var perfEnv = PerfReports.SendCommand(new Toolchain("v4.0.0", Path.Combine(scratch, "lb-perf-tools", "v4.0.0"), "longshot-builder"),
                                                  Path.Combine(scratch, "lb-perf-game")).Environment;
            Require(!perfEnv.Keys.Any(k => k.StartsWith("LONGSHOT_", StringComparison.Ordinal) || k.StartsWith("RX_", StringComparison.Ordinal) || k.StartsWith("A64_", StringComparison.Ordinal)),
                    "Nor longshot-builder perf-report.");
            Require(new MakeGameCommand("python", [], "/", new Dictionary<string, string?>()).StartInfo().Environment["LONGSHOT_ROOT"] == "/stray/tree",
                    "make_game.py's environment is as it was.");
        }
        finally
        {
            foreach (var (k, _) in stray) Environment.SetEnvironmentVariable(k, null);
        }
        Require(LongshotBuilder.ExeName(Platform.Windows) == LongshotBuilder.Name + ".exe" && LongshotBuilder.ExeName(Platform.Linux) == LongshotBuilder.Name,
                "The installed file is named for the program (LongshotBuilder.Name, the one place).");

        // ---- the .nup the game reads in place, for a folder whose game longshot-builder built last
        var game = Folder("lb-game");
        var inPlace = new BuildEntry { Folder = game, NupInPlace = true };
        Require(LongshotBuilder.RecordedNup(game) == null && LongshotBuilder.MissingNup(game, inPlace) == null && LongshotBuilder.MissingNup(game, null) == null,
                "A folder it did not build reads no .nup.");
        var nup = Path.Combine(Folder("lb-nup"), "ゲーム.nup");
        File.WriteAllText(nup, "not a game, a stand-in");
        File.WriteAllText(Path.Combine(game, LongshotBuilder.RecordFile), System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["format"] = 1, ["builder"] = "v4.0.0", ["game"] = new Dictionary<string, string> { ["nup"] = nup }, ["upscalers"] = new[] { "dlss", "xess" }, ["user"] = null,
        }));
        File.WriteAllText(Path.Combine(game, GameFolder.StampName), "version=v4.0.0\nsource=x\n");
        Require(LongshotBuilder.RecordedNup(game) == nup && LongshotBuilder.MissingNup(game, inPlace) == null, "Its .nup, there.");
        Require(LongshotBuilder.ReadRecord(game) is { Builder: "v4.0.0", Upscalers: ["dlss", "xess"] }, "The record: its release and its upscalers, as the build did them.");
        File.Move(nup, nup + ".moved");
        Require(LongshotBuilder.MissingNup(game, inPlace) is { DriveMissing: false } gone && gone.Nup == nup
                && LongshotBuilder.MissingNup(game, null)?.Nup == nup, "Its .nup moved: said, with where it was (--play too: the stamp names the record's release).");
        // (a make_game.py build over it since: its entry says so; for --play, its stamp names another release)
        Require(LongshotBuilder.MissingNup(game, new BuildEntry { Folder = game, NupInPlace = null }) == null, "A make_game.py build's entry: no .nup asked for.");
        File.WriteAllText(Path.Combine(game, GameFolder.StampName), "version=v3.9.0\n");
        Require(LongshotBuilder.MissingNup(game, null) == null && LongshotBuilder.MissingNup(game, inPlace) != null,
                "--play of a folder whose stamp is another release's: its record is stale, the game plays.");
        // (on a drive not connected: connect it, rather than build again)
        Require(LongshotBuilder.DriveRoot(@"e:\games\x.nup", Platform.Windows) == @"E:\" && LongshotBuilder.DriveRoot(@"\\nas\share\x.nup", Platform.Windows) == @"\\nas\share\"
                && LongshotBuilder.DriveRoot("/run/media/me/USB/x.nup", Platform.Linux) == "/run/media/me/USB" && LongshotBuilder.DriveRoot("/media/me/USB/a/x.nup", Platform.Linux) == "/media/me/USB"
                && LongshotBuilder.DriveRoot("/mnt/games/x.nup", Platform.Linux) == "/mnt/games" && LongshotBuilder.DriveRoot("/home/me/x.nup", Platform.Linux) == null
                && LongshotBuilder.DriveRoot("/mnt/x.nup", Platform.Linux) == null, "The drive a .nup is on.");
        var usbGame = Folder("lb-usb-game");
        File.WriteAllText(Path.Combine(usbGame, LongshotBuilder.RecordFile), """{"format":1,"builder":"v4.0.0","game":{"nup":"/run/media/me/USB/x.nup"}}""");
        Require(LongshotBuilder.MissingNup(usbGame, inPlace, _ => false, _ => false) is { DriveMissing: true }
                && LongshotBuilder.MissingNup(usbGame, inPlace, _ => false, _ => true) is { DriveMissing: false }
                && new NupMovedException("/run/media/me/USB/x.nup", true).Message.Contains("not connected", StringComparison.Ordinal),
                "Its drive not connected: said so, to connect it.");
        Require(new NupMovedException(nup).Nup == nup && new NupMovedException(nup) is IOException, "Play's refusal is an IOException, as a game that can't start is.");
        File.WriteAllText(Path.Combine(game, LongshotBuilder.RecordFile), """{"format":1,"game":{"folder":{"romfs":"/d/romfs","exefs":"/d/exefs"}}}""");
        Require(LongshotBuilder.MissingNup(game, inPlace) == null, "Built from a dump folder: no .nup to keep.");
        File.WriteAllText(Path.Combine(game, LongshotBuilder.RecordFile), "{ not json");
        Require(LongshotBuilder.RecordedNup(game) == null, "A damaged record reads no .nup.");

        // ---- a .nup moved since the build: the game pointed at it where it is now (longshot-builder index), not built again
        var repointGame = Folder("lb-repoint-game");
        var oldNup = Path.Combine(Folder("lb-repoint-was"), "ゲーム.nup");
        var newNup = Path.Combine(Folder("lb-repoint-now"), "ゲーム.nup");
        File.WriteAllText(newNup, "a stand-in");
        File.WriteAllText(Path.Combine(repointGame, LongshotBuilder.RecordFile), System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["format"] = 1, ["builder"] = "v4.0.0", ["game"] = new Dictionary<string, string> { ["nup"] = oldNup },
        }));
        var repointHome = Folder("lb-repoint-home");
        var registryPath = Path.Combine(repointHome, "builds.json");
        var settingsPath = Path.Combine(repointHome, "settings.json");
        var registry = BuildRegistry.Load(registryPath);
        var row = registry.Add(repointGame, "v4.0.0", "stable");
        row.Nup = oldNup;
        row.NupInPlace = true;
        registry.Save();
        var repointSettings = new LauncherSettings { Nup = oldNup };
        repointSettings.Save(settingsPath);
        Require(LongshotBuilder.MissingNup(repointGame, row)?.Nup == oldNup, "Its .nup is gone from where it was built from.");
        var fakeExe = Path.Combine(Folder("lb-repoint-tools"), "longshot-builder");
        var repointer = new LongshotBuilder(lin, fakeExe, Platform.Linux, home, "/L/Longshot.AppImage", _ => null);
        var index = repointer.Index(newNup, repointGame, "/w");
        Require(index.FileName == fakeExe && !index.LowPriority && index.Arguments.SequenceEqual(new[]
                {
                    "index", "--game", newNup, "--out", repointGame, "--output", "json",
                    "--cache", Path.Combine("/w", Disk.LongshotBuildCache), "--builder-exe", "/L/Longshot.AppImage",
                }), "index's arguments: the .nup where it is now, the game folder, the launcher's settings as flags: " + string.Join(" ", index.Arguments));
        Require(LongshotBuilder.ReadIndex(["""{"v":1,"type":"log","text":"index took 0.4 s"}"""], null, 0).Outcome == IndexOutcome.Done
                && LongshotBuilder.ReadIndex(["""{"v":1,"type":"error","code":"not_the_same_nup","text":"Build again."}"""], null, 1) is { Outcome: IndexOutcome.NotTheSameNup, Code: "not_the_same_nup" }
                && LongshotBuilder.ReadIndex(["""{"v":1,"type":"error","code":"game_folder_busy","text":"Busy."}"""], null, 1) is { Outcome: IndexOutcome.Failed, Text: "Busy." }
                && LongshotBuilder.ReadIndex([], "longshot-builder: no such command", 2) is { Outcome: IndexOutcome.Failed, Text: "longshot-builder: no such command" }
                && LongshotBuilder.ReadIndex([], null, 3).Text!.Contains("exit code 3", StringComparison.Ordinal),
                "index's answer: done, another .nup (built again from it), or why it stopped.");
        // (the moved .nup's flow, over a stand-in for longshot-builder: a shell script, not on Windows)
        if (!OperatingSystem.IsWindows())
        {
            File.WriteAllText(fakeExe, """
                #!/bin/sh
                d=$(dirname "$0")
                printf '%s\n' "$@" > "$d/args.txt"
                case "$3" in
                  *other*) printf '%s\n' '{"v":1,"type":"error","code":"not_the_same_nup","text":"Not the .nup it was built from."}'; exit 1 ;;
                  *broken*) printf 'longshot-builder: it stopped\n' >&2; exit 1 ;;
                esac
                printf '{"format":1,"builder":"v4.0.0","game":{"nup":"%s"}}' "$3" > "$5/longshot_build.json"
                printf '%s\n' '{"v":1,"type":"log","text":"index took 0.4 s"}'
                """.Replace("\r", "") + "\n");
            File.SetUnixFileMode(fakeExe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var argsSaid = Path.Combine(Path.GetDirectoryName(fakeExe)!, "args.txt");
            var done = repointer.RepointAsync(row, newNup, registry, repointSettings, settingsPath, "/w").GetAwaiter().GetResult();
            Require(done is { Outcome: IndexOutcome.Done, Text: null } && File.ReadAllLines(argsSaid)[0] == "index",
                    "A moved .nup runs longshot-builder index, not build.");
            Require(row.Nup == newNup && BuildRegistry.Load(registryPath).Find(row.Id) is { NupInPlace: true } saved && saved.Nup == newNup
                    && LauncherSettings.Load(settingsPath).Nup == newNup && repointSettings.Nup == newNup,
                    "Pointed at: the row and the game-file setting name the .nup where it is now, saved.");
            Require(LongshotBuilder.MissingNup(repointGame, row) == null, "Play finds its .nup again.");
            var otherNup = Path.Combine(Folder("lb-repoint-other"), "other.nup");
            File.WriteAllText(otherNup, "another");
            var notSame = repointer.RepointAsync(row, otherNup, registry, repointSettings, settingsPath, "/w").GetAwaiter().GetResult();
            Require(notSame is { Outcome: IndexOutcome.NotTheSameNup, Code: "not_the_same_nup" } && File.ReadAllLines(argsSaid)[0] == "index"
                    && row.Nup == newNup && LauncherSettings.Load(settingsPath).Nup == newNup && BuildRegistry.Load(registryPath).Find(row.Id)?.Nup == newNup,
                    "Another .nup than the one it was built from: not_the_same_nup, nothing changed (the build from it names it).");
            var stopped = repointer.RepointAsync(row, Path.Combine(Folder("lb-repoint-broken"), "broken.nup"), registry, repointSettings, settingsPath, "/w")
                .GetAwaiter().GetResult();
            Require(stopped is { Outcome: IndexOutcome.Failed, Text: "longshot-builder: it stopped" } && row.Nup == newNup,
                    "index that stops: why, from its stderr, and nothing changed.");
        }
        // (step 1 of the wizard: a moved .nup picked runs index, over step 1; not_the_same_nup goes on to the build's settings)
        var wizard = File.ReadAllText(Path.Combine(fixtures, "..", "..", "App", "ViewModels", "WizardViewModel.cs"));
        Require(System.Text.RegularExpressions.Regex.IsMatch(wizard, @"if \(Job\.MovedNup != null && Job\.Replaces is \{ \} entry && Job\.Builder is LongshotBuilder builder[^\n]*\n\s*\{\s*Main\.Modal = new RepointViewModel\(")
                && System.Text.RegularExpressions.Regex.IsMatch(wizard, @"case IndexOutcome\.NotTheSameNup:\s*Main\.Modal = null;\s*step\.ToSettings\(\);")
                && wizard.Contains("builder.RepointAsync(entry, Nup, s.Registry, s.Settings, s.Home.Settings, s.Work, cancel.Token)", StringComparison.Ordinal),
                "Step 1: a moved .nup is pointed at with index; another .nup falls back to the build.");

        // ---- a build's lock in its game folder (build, update, image, index): a build, told from a check or perf-report
        var locked = Folder("lb-locked");
        Require(!LongshotBuilder.Building(locked), "No lock file: no build.");
        var lockPath = Path.Combine(locked, LongshotBuilder.GameLockFile);
        File.WriteAllText(lockPath, "");
        Require(!LongshotBuilder.Building(locked), "A lock file left behind, held by no one: no build.");
        using (var held = OperatingSystem.IsWindows() ? new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)
                                                       : new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (OperatingSystem.IsWindows()) held.Lock(0, long.MaxValue);
            Require(LongshotBuilder.Building(locked), "The lock held, as longshot-builder holds it: a build.");
        }
        Require(!LongshotBuilder.Building(locked), "Let go: no build.");
        Require(Saves.BuilderRunning(Platform.Linux, [locked], candidates: ["longshot-builder"], building: f => f == locked)
                && !Saves.BuilderRunning(Platform.Linux, [locked], candidates: ["longshot-builder"], building: _ => false)
                && !Saves.BuilderRunning(Platform.Linux, [locked], candidates: ["bash"], building: _ => true),
                "A build runs: longshot-builder running and a game folder's lock held; its check or perf-report holds none.");
        Require(ShaderCaches.Busy(Platform.Linux, ["longshot-builder"], [locked], _ => false) == false
                && ShaderCaches.Busy(Platform.Linux, ["longshot-builder"], [locked], _ => true) && ShaderCaches.Busy(Platform.Linux, ["longshot"], [locked], _ => false),
                "The shader caches are busy with the game, or a build of their folder, not a check.");

        // ---- the performance statistics sent by longshot-builder perf-report (PerfReports), not perf_report.py
        var perf = Lines("perf-report.jsonl").Select(PerfReports.ReadState).ToList();
        Require(perf[0] is { Tick: true, KeepSent: true, Waiting: 1, Sent: 0, Version: "v3.1.0", Release: false, BusyUntil: null } p0
                && p0.Relay.StartsWith("https://", StringComparison.Ordinal), "perf-report's state: the tick, one waiting, the relay.");
        Require(perf[1] is { KeepSent: false, Waiting: 2, Kept: 3, Sent: 4, Refused: 1, Release: true, BusyUntil: 1795000000 },
                "A send: this run's counts, and when the busy relay may be asked again.");
        Require(perf[2] is { Tick: false, Relay: "" }, "Ticked off, nowhere to send.");
        Require(PerfReports.ReadState("""{"v":2,"type":"perf_report","tick":true}""") == null && PerfReports.ReadState("""{"v":1,"type":"log","text":"x"}""") == null
                && PerfReports.ReadState("not json") == null && PerfReports.ReadState(Lines("perf-report.jsonl"))?.Tick == false,
                "Another version or type passed over; the last event of a run is its answer.");
        var perfRoot = Folder("lb-perf-tools");
        var perfChains = new Toolchains(perfRoot, Platform.Linux);
        var perfTools = InstalledVersion(perfRoot, "v4.0.0");
        var perfGame = Folder("lb-perf-game");
        Game(perfGame, "v4.0.0");
        Require(PerfReports.ToolsFor(perfGame, perfChains, _ => false) == null
                && PerfReports.ToolsFor(perfGame, perfChains, f => f == perfTools.BuilderExe) == perfTools, "Its own version's longshot-builder sends its summaries.");
        var perfSend = PerfReports.SendCommand(perfTools, perfGame);
        Require(perfSend.FileName == perfTools.BuilderExe && perfSend.ArgumentList.SequenceEqual(new[] { "perf-report", perfGame, "--send", "--output", "json" })
                && !perfSend.UseShellExecute && perfSend.CreateNoWindow, "--send: longshot-builder perf-report, the folder as one argument, no window.");
        Require(PerfReports.StateCommand(perfTools, perfGame).ArgumentList.SequenceEqual(new[] { "perf-report", perfGame, "--output", "json" }),
                "The state: perf-report with nothing sent.");

        // ---- busy: the game (any case) or longshot-builder running keeps the shader caches and store (ShaderCaches.Busy)
        foreach (var (platform, names, busy) in new (Platform, string[], bool)[]
                 {
                     (Platform.Windows, ["explorer.exe", "Longshot.exe"], true), (Platform.Windows, ["longshot-builder.exe"], true),
                     (Platform.Windows, ["LongshotLauncher.exe", "explorer.exe"], false), (Platform.Windows, ["LongshotBuilder.exe"], false),
                     (Platform.Linux, ["bash", "longshot"], true), (Platform.Linux, ["longshot-builder"], true),
                     (Platform.Linux, ["longshot-builder-v4.0.0-linux-x64"], true), (Platform.Linux, ["Longshot-v4.0.0-x86_64.AppImage"], true),
                     (Platform.Linux, ["bash", "firefox", "LongshotLauncher-v1.0.0-x86_64.AppImage"], false),
                 })
            Require(ShaderCaches.Busy(platform, names) == busy, $"{platform} {string.Join(", ", names)}: {(busy ? "busy" : "not busy")}.");
        Require(Saves.BuilderAmong(["LONGSHOT-BUILDER.EXE"]) && !Saves.BuilderAmong(["longshot"]) && !Saves.RunningFrom(Platform.Linux, ["longshot-builder"]).Any(),
                "The builder in any case; it is no game: the saves never wait for a build.");
        // (the store's per-user program records count with it, so Free up space and Remove everything take them too)
        var store = Folder("lb-store");
        foreach (var n in new[] { "spirv.pack", "spirv.idx", "spirv.pack.programs-0123abcd", "spirv.idx.programs-0123abcd", "notes.txt" })
            File.WriteAllText(Path.Combine(store, n), "x");
        Require(ShaderCaches.StoreFiles(store).Select(f => f.Name).SequenceEqual(new[] { "spirv.idx", "spirv.idx.programs-0123abcd", "spirv.pack", "spirv.pack.programs-0123abcd" }),
                "The store's files: its pack and index, and the program records beside them; nothing else.");

        // ---- its build files in the launcher's
        Require(Disk.WorkParts.Contains(Disk.LongshotWork) && Storage.WorkPartsOf(Storage.Kind.BuilderFiles).SequenceEqual([Disk.LongshotWork])
                && !Storage.WorkPartsOf(Storage.Kind.RecentCode).Contains(Disk.LongshotWork) && Storage.Info(Storage.Kind.BuilderFiles).Name.Contains("longshot-builder", StringComparison.Ordinal),
                "longshot-builder's work folders are a row of their own in the build files Storage frees (BuilderStorageChecks: the rest of its cache).");
        var builderFiles = new Storage.Item(Storage.Kind.BuilderFiles, 1, 1);
        Require(Storage.CanRemove(builderFiles, running: true, downloading: true) == Storage.Refusal.None
                && Storage.CanRemove(builderFiles, false, false, building: true) == Storage.Refusal.Building
                && Storage.CanRemove(new Storage.Item(Storage.Kind.ShaderStore, 1, 1), false, false, building: true) == Storage.Refusal.Building
                && Storage.CanRemove(new Storage.Item(Storage.Kind.Logs, 1, 1), false, false, building: true) == Storage.Refusal.None,
                "While a build runs, its build files, the shader caches and the store stay; the game running holds none of the build files.");
    }
}
