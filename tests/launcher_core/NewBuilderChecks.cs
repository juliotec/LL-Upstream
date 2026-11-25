using System.Text;
using LongshotLauncher.Core;

/// A version built with the new builder (longshot-builder): this launcher refuses it and points to the nightly launcher. Detection
/// (the builder's manifest in the signed SHA256SUMS), the refusal, the update check, the Version list and the channel switch
static partial class Program
{
    static async Task NewBuilderChecks()
    {
        var root = Folder("new-builder");
        var (key, trusted) = NewKey();
        var keys = new[] { trusted };
        var image = Encoding.UTF8.GetBytes("\x7fELF an authored AppImage");
        var manifest = Encoding.UTF8.GetBytes("{\"authored\": true}");
        Dictionary<string, byte[]> Old(string v) => new() { [ReleaseManifest.BuilderFile(v, "linux-x64")] = image };
        Dictionary<string, byte[]> New(string v) => new() { [ReleaseManifest.BuilderManifestName(v, "linux-x64")] = manifest };

        var forge = new FakeForgejo(Product.Longshot);
        forge.Add("v3.0.0", key, files: Old("v3.0.0"));
        forge.Add("v3.1.0", key, files: New("v3.1.0"));
        forge.Add("nightly-10-aaaaaaaa", key, files: Old("nightly-10-aaaaaaaa"));
        forge.Add("nightly-11-bbbbbbbb", key, files: New("nightly-11-bbbbbbbb"));
        forge.Add("v3.0.1", key, files: Old("v3.0.1"));
        var linux = new Releases(forge.Server, Platform.Linux, keys, NoWait);
        var windows = new Releases(forge.Server, Platform.Windows, keys, NoWait);

        // ---- detection ----
        Require((await linux.VersionAsync("v3.1.0")).HasNewBuilder(Platform.Linux) && !(await linux.VersionAsync("v3.0.0")).HasNewBuilder(Platform.Linux),
                "A version is built with the new builder when its signed SHA256SUMS lists the builder's manifest for the system.");
        Require(!(await linux.VersionAsync("v3.1.0")).HasNewBuilder(Platform.Windows) && !(await windows.VersionAsync("v3.1.0")).HasNewBuilder(Platform.Windows),
                "A manifest for another system is no manifest for this one.");
        var listed = ReleaseManifest.ParseList(forge.Server.Texts[$"{Product.Longshot.Api}/releases?limit=50&page=1"]());
        Require(ReleaseManifest.ListsNewBuilder(listed.Single(r => r.Tag == "v3.1.0"), Platform.Linux)
                && !ReleaseManifest.ListsNewBuilder(listed.Single(r => r.Tag == "v3.0.0"), Platform.Linux)
                && !ReleaseManifest.ListsNewBuilder(listed.Single(r => r.Tag == "v3.1.0"), Platform.Windows),
                "The release list shows a version's manifest by its exact name, for this system.");

        // ---- the refusal ----
        Task Extract(string appImage, string folder, CancellationToken cancel)
        {
            var tree = Path.Combine(folder, "squashfs-root");
            Directory.CreateDirectory(Path.Combine(tree, "usr", "share", "longshot", "nx2"));
            File.WriteAllText(Path.Combine(tree, "AppRun"), "#!/bin/sh\n");
            File.WriteAllText(Path.Combine(tree, "usr", "share", "longshot", "nx2", "make_game.py"), "print('authored')\n");
            return Task.CompletedTask;
        }
        var chains = new Toolchains(Path.Combine(root, "toolchains"), forge.Server);
        var refused = (await ThrowsOf<InvalidDataException>(() => ToolsSource.EnsureAsync(chains, linux, "v3.1.0", null, default, Extract),
                                                                   "A version built with the new builder was built."));
        Require(refused.Message == "This version of Longshot is built with the new builder, which this launcher doesn't support. "
                + "Switch the Longshot Launcher to its nightly channel to play it." && NeedsNightlyLauncher.Is(refused) && !NeedsNightlyLauncher.Is(new InvalidDataException("another")),
                "The refusal says to switch the launcher to its nightly channel.");
        Require(!chains.IsInstalled("v3.1.0") && !forge.Server.Asked.Any(u => u.EndsWith(".json", StringComparison.Ordinal)),
                "Nothing of a refused version was downloaded.");
        await ThrowsOf<InvalidDataException>(() => linux.OldBuilderAsync("nightly-11-bbbbbbbb"), "A nightly built with the new builder passed.");
        var old = await ToolsSource.EnsureAsync(chains, linux, "v3.0.0", null, default, Extract);
        Require(File.Exists(old.MakeGame), "A version built with make_game.py is built as it was.");

        // ---- the update check ----
        var answers = await linux.ChannelsAsync(Versions.Channels);
        Require(answers["stable"]?.Version == "v3.0.1" && answers["nightly"]?.Version == "nightly-10-aaaaaaaa",
                "An update check offers the newest version it can build: the ones built with the new builder are skipped.");
        var only = new FakeForgejo(Product.Longshot);
        only.Add("v3.1.0", key, files: New("v3.1.0"));
        only.Add("nightly-11-bbbbbbbb", key, files: New("nightly-11-bbbbbbbb"));
        var none = new Releases(only.Server, Platform.Linux, keys, NoWait);
        var nothing = await none.ChannelsAsync(Versions.Channels);
        Require(nothing["stable"] == null && nothing["nightly"] == null && none.Problem == null,
                "An update check that finds only versions built with the new builder offers none.");
        Require(await none.NewestAsync("stable") == null, "The newest version of a channel is none when all are built with the new builder.");

        // ---- the Version list ----
        string Marks(IEnumerable<TrackVersion> vs) => string.Join(" ", vs.Select(v => v.Tag + (v.NeedsNightly ? "!" : "")));
        Require(Marks(VersionPick.Of("stable", Array.Empty<BuildEntry>(), listed, Product.Longshot, Platform.Linux)) == "v3.1.0! v3.0.1 v3.0.0"
                && Marks(VersionPick.Of("stable", Array.Empty<BuildEntry>(), listed, Product.Longshot)) == "v3.1.0 v3.0.1 v3.0.0",
                "The Version list marks the versions built with the new builder.");

        // ---- the channel switch ----
        var settings = new LauncherSettings();
        Require(SelfUpdate.ChannelOf("v1.2.0", settings.LauncherChannel) == "stable", "A stable launcher updates from stable.");
        Require(settings.SwitchLauncherToNightly("v1.2.0") && settings.LauncherChannel == "nightly"
                && SelfUpdate.ChannelOf("v1.2.0", settings.LauncherChannel) == "nightly" && !settings.SwitchLauncherToNightly("v1.2.0"),
                "The switch sets the launcher's own update channel to nightly, once.");
        Require(settings.DefaultChannel == "stable" && settings.BuildChannel("v1.2.0") == "stable",
                "The builds' default track stays stable: the switch moves only the launcher.");
        var path = Path.Combine(root, "settings.json");
        settings.Save(path);
        Require(LauncherSettings.Load(path).LauncherChannel == "nightly", "The switched channel is saved.");
        var own = new FakeForgejo(Product.Launcher);
        own.Add("v1.2.0", key, files: LauncherFiles("v1.2.0"));
        own.Add("nightly-30-cccccccc", key, files: LauncherFiles("nightly-30-cccccccc"));
        var launcher = new Releases(own.Server, Platform.Linux, keys, NoWait, Product.Launcher);
        var channel = SelfUpdate.ChannelOf("v1.2.0", settings.LauncherChannel);
        Require(SelfUpdate.UpdateTo("v1.2.0", await launcher.ChannelAsync(channel), Platform.Linux, channel) == "nightly-30-cccccccc",
                "After the switch, the launcher's own update is the newest nightly launcher.");
    }

    static async Task<T> ThrowsOf<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T e) { checks++; return e; }
        throw new Exception(message);
    }
}
