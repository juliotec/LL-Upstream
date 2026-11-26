using LongshotLauncher.Core;

/// The builder's commands the launcher asks besides a build (Longshot's build.md, build-events.md), each over a fake builder script
/// that says the command's events as the builder does: info (what a version's builder and its game can do, the game folder's
/// state), read in place of the game folder's files; storage (the builder's cache by kind), shown by the Storage page in place
/// of walking the cache
static partial class Program
{
    const string InfoLine = """{"v":1,"type":"info","version":{"tag":"v4.0.0","channel":"stable","source":"abc","stamp":"s"},"program":"/v/Longshot.x86_64","platform":"linux-x64","capabilities":{"builder":["play","profiles","fetch","info","steam_shortcut","storage","from_source"],"game":["save_dir","profile"]},"profile":{"dir":"/p","exists":true},"build":{"folder":"/v/builds/default","present":true,"up_to_date":false,"stale":"another builder","builder":"v3.9.0","upscalers":["fsr4","dlss"]},"features":["save_dir","profile"],"game":{"path":"/g.nup","exists":true},"upscalers":["dlss"]}""";

    static async Task BuilderInfoChecks()
    {
        // ---- the info event, read
        var info = (BuildEvents.Parse(InfoLine) as BuildEvent.Info)?.Value;
        Require(info is { Tag: "v4.0.0", Channel: "stable" } && info.Can("steam_shortcut") && info.Can("from_source") && !info.Can("mod")
                && info.Game.SetEquals(new[] { "save_dir", "profile" })
                && info.Build is { Folder: "/v/builds/default", Present: true, UpToDate: false, Stale: "another builder", BuiltBy: "v3.9.0" }
                && info.Build.Upscalers.SequenceEqual(new[] { "dlss", "fsr4" }),
                "info: the builder's capabilities, the game's, and the profile's game folder's state.");
        Require(BuildEvents.Parse("""{"v":1,"type":"info","version":{"tag":null}}""") == null && BuildEvents.Parse(InfoLine.Replace("\"v\":1", "\"v\":2")) == null,
                "An info with no capabilities, or of another format, is none.");
        Require(BuilderInfo.Arguments("/p/x").SequenceEqual(new[] { "info", "--profile", Path.GetFullPath("/p/x"), "--output", "json" })
                && BuilderInfo.Arguments(null).SequenceEqual(new[] { "info", "--output", "json" }), "info --profile <the profile's folder> --output json.");

        // ---- a version folder's game: what it can do is its builder's info, never its folder's features= line
        var data = Folder("info-data");
        var version = BuilderVersions.FolderOf(data, "v4.0.0");
        var game = BuilderVersions.GameFolderOf(version);
        Directory.CreateDirectory(game);
        File.WriteAllText(BuilderVersions.ExeOf(version, Platforms.Current), "builder");
        File.WriteAllText(Path.Combine(game, GameFolder.StampName), "version=v4.0.0\nfeatures=start_slot,save_dir\n");
        BuilderInfos.Forget(version);
        Require(GameFeatures.Of(game).Count == 0,
                "A version folder's game whose builder was not asked: no feature, whatever its stamp says.");
        Require(BuilderInfos.Set(version, info!) && !BuilderInfos.Set(version, info!), "An info set; the same again is no change.");
        Require(GameFeatures.Has(game, "profile") && BuilderInfos.Of(game, Platforms.Current) == info,
                "Its builder's info: its capabilities.");
        BuilderInfos.Forget(version);
        Require(BuilderInfos.Of(game, Platforms.Current) == null, "Forgotten: asked again.");

        if (OperatingSystem.IsWindows()) return;
        // ---- asked of the version folder's builder
        var home = new LauncherHome(Folder("info-home"));
        var exe = BuilderVersions.ExeOf(version, Platform.Linux);
        ScriptBuilder(exe, "printf '%s\\n' \"$@\" > \"$(dirname \"$0\")/args.txt\"\necho '" + InfoLine + "'");
        var builder = new LongshotBuilder(new Toolchain("v4.0.0", version, "Longshot.x86_64"), exe, Platform.Linux, home, "/L/launcher", _ => null);
        var asked = await builder.InfoAsync("/p/x");
        Require(asked?.Tag == "v4.0.0" && asked.Build.Present
                && File.ReadAllLines(Path.Combine(version, "args.txt")).SequenceEqual(new[] { "info", "--profile", Path.GetFullPath("/p/x"), "--output", "json" }),
                "info asked of the version folder's builder.");
        ScriptBuilder(exe, "echo 'longshot-builder: unknown command: info' >&2\nexit 2");
        Require(await builder.InfoAsync("/p/x") == null, "A builder from before info: none.");
    }

    static async Task BuilderStorageCommandChecks()
    {
        // ---- the storage event, read
        var line = """{"v":1,"type":"storage","cache":"/w/longshot-builder","budget":12884901888,"total":700,"kinds":[{"kind":"objects","bytes":100,"entries":3},{"kind":"lifts","bytes":200,"entries":1},{"kind":"work","bytes":300,"entries":2},{"kind":"tools","bytes":50,"entries":1},{"kind":"shaders","bytes":40,"entries":1},{"kind":"runtimes","bytes":10,"entries":1}],"removed":0,"freed":0}""";
        var counted = (BuildEvents.Parse(line) as BuildEvent.Storage)?.Value;
        Require(counted is { Cache: "/w/longshot-builder", Budget: 12884901888, Total: 700 } && counted.Kinds.Count == 6 && counted.Kinds[2] == new BuilderStorage.Part("work", 300, 2),
                "storage: the cache, its budget, its total and each kind's bytes and entries.");
        Require(BuilderStorage.Arguments("/w/longshot-builder").SequenceEqual(new[] { "storage", "--cache", "/w/longshot-builder", "--output", "json" }),
                "storage --cache <the launcher's builder cache> --output json.");
        Require(BuilderStorage.KindOf("work") == Storage.Kind.BuilderFiles && BuilderStorage.KindOf("objects") == Storage.Kind.OlderCode
                && BuilderStorage.KindOf("lifts") == Storage.Kind.OlderTranslations && BuilderStorage.KindOf("shaders") == Storage.Kind.ShaderStore
                && BuilderStorage.KindOf("tools") == Storage.Kind.Builders && BuilderStorage.KindOf("runtimes") == Storage.Kind.Builders
                && BuilderStorage.KindOf("a later kind") == Storage.Kind.Builders,
                "Each of the builder's kinds is one of the page's: its work folders, its stores, its shader caches, the rest the builders'.");

        // ---- the page shows the builder's count, never walks its cache
        var work = Folder("bst-work");
        var cache = Path.Combine(work, Disk.LongshotBuildCache);
        Directory.CreateDirectory(Path.Combine(cache, "work", "a"));
        File.WriteAllBytes(Path.Combine(cache, "work", "a", "big.o"), new byte[5000]);
        Directory.CreateDirectory(Path.Combine(cache, "tools", "t"));
        File.WriteAllBytes(Path.Combine(cache, "tools", "t", "clang"), new byte[7000]);
        var places = new Storage.Places(work, Folder("bst-tools"), Folder("bst-logs"), [], [], []);
        Require(Storage.Measure(places).Where(i => i.Kind == Storage.Kind.BuilderFiles).Sum(i => i.Bytes) == 5000
                && Storage.Measure(places).Where(i => i.Kind == Storage.Kind.Builders).Sum(i => i.Bytes) == 7000,
                "With no answer from a builder, its cache's folders are walked as before.");
        var told = places with { Builder = counted! with { Cache = cache } };
        var items = Storage.Measure(told);
        long Of(Storage.Kind k) => items.Where(i => i.Kind == k).Sum(i => i.Bytes);
        Require(Of(Storage.Kind.BuilderFiles) == 300 && Of(Storage.Kind.OlderCode) == 100 && Of(Storage.Kind.OlderTranslations) == 200
                && Of(Storage.Kind.Builders) == 60 && Of(Storage.Kind.ShaderStore) == 40
                && items.Where(i => i.Kind == Storage.Kind.BuilderFiles).All(i => i.Drive == Disk.DriveName(cache)),
                "The builder's count is shown by kind, on its cache's drive, and its folders are not walked again: "
                + string.Join(", ", items.Select(i => $"{i.Kind}={i.Bytes}")));

        if (OperatingSystem.IsWindows()) return;
        // ---- asked of a version's builder
        var version = Folder("bst-version");
        var exe = Path.Combine(version, "Longshot.x86_64");
        ScriptBuilder(exe, "printf '%s\\n' \"$@\" > \"$(dirname \"$0\")/args.txt\"\necho '" + line + "'");
        var builder = new LongshotBuilder(new Toolchain("v4.0.0", version, "Longshot.x86_64"), exe, Platform.Linux, new LauncherHome(Folder("bst-home")), "/L", _ => null);
        var asked = await BuilderStorage.AskAsync(builder, "/w/longshot-builder");
        Require(asked?.Total == 700 && File.ReadAllLines(Path.Combine(version, "args.txt")).SequenceEqual(BuilderStorage.Arguments("/w/longshot-builder")),
                "storage asked of the builder.");
        ScriptBuilder(exe, "exit 2");
        Require(await BuilderStorage.AskAsync(builder, "/w/longshot-builder") == null, "A builder from before storage: none, the cache is walked.");
    }
}
