using LongshotLauncher.Core;

/// A game folder whose row went, listed again (Leftovers.Relist, issue #141)
static partial class Program
{
    static void UnlistedChecks()
    {
        var p = Platform.Linux;
        string Built(string builds, string name, string version, bool plays = true)
        {
            var dir = Path.Combine(builds, name);
            Directory.CreateDirectory(Path.Combine(dir, "bin"));
            File.WriteAllText(Path.Combine(dir, GameFolder.StampName), $"version={version}\n");
            File.WriteAllText(Path.Combine(dir, "longshot"), "setup");
            if (plays) File.WriteAllText(Path.Combine(dir, "bin", "longshot"), "game");
            return dir;
        }
        string Names(IEnumerable<string> folders) => string.Join(" ", folders.Select(Path.GetFileName).Order(StringComparer.Ordinal));

        // (issue #141: a portable launcher's rows went (before #153 made them follow a renamed folder, ForgetGone dropped every one
        // whose path no longer was), while its games stayed in its own builds folder, LongshotLauncher-data\builds: `rc` and
        // `v3.0.0-rc.3`. The home asked for a first build, a dump and an hour. Each channel's newest game no listed build plays is
        // listed again, the first one the default; an older one stays unlisted, an old version the home may offer to delete)
        var home = Path.Combine(Folder("unlisted"), LauncherLayout.PortableData);
        var builds = Path.Combine(home, "builds");
        var rc = Built(builds, "rc", "v3.0.0-rc.5");
        Built(builds, "v3.0.0-rc.3", "v3.0.0-rc.3");                             // (an older one of the same channel)
        var night = Built(builds, "nightly", "nightly-200-55555555");
        Built(builds, "stable", "v3.0.0", plays: false);                          // (its game gone: nothing to play)
        Built(builds, "dev", "dev");                                              // (no release, no nightly: a version of nothing)
        Directory.CreateDirectory(Path.Combine(builds, "photos"));                // (not a game folder)

        var reg = BuildRegistry.Load(Path.Combine(home, "builds.json"));
        var added = Leftovers.Relist(reg, builds, p, keep: new[] { home });
        Require(Names(added.Select(b => b.Folder)) == "nightly rc",
                "Each channel's newest game whose row went is listed again, from a portable launcher's own builds folder too, and nothing else (got: "
                + Names(added.Select(b => b.Folder)) + ").");
        Require(reg.Builds.SingleOrDefault(b => b.Folder == rc) is { Version: "v3.0.0-rc.5", Channel: Versions.Candidate }
                && reg.Builds.SingleOrDefault(b => b.Folder == night) is { Version: "nightly-200-55555555", Channel: Versions.Nightly },
                "Listed with its version, on its channel.");
        Require(reg.Default?.Folder == rc, "The first one listed again is the default, a release candidate before a nightly: " + reg.Default?.Folder);
        Require(Leftovers.Relist(reg, builds, p, keep: new[] { home }).Count == 0, "Listed once: nothing more at the next start.");
        var offered = Names(Leftovers.Find(builds, reg.Builds.Select(b => b.Folder), p, keep: new[] { home }).Select(l => l.Folder));
        Require(offered == "stable v3.0.0-rc.3",
                "The older rc.3 is an old version now, offered (from a portable launcher's builds folder too), with the game folder that has "
                + "nothing to play; the one with no version is neither listed nor offered (got: " + offered + ").");

        // (never: a game folder that holds the launcher's home; one the player kept when it was offered for deletion. A folder named
        // after a channel its version is not on goes on its version's)
        var builds2 = Folder("unlisted-other");
        var holder = Built(builds2, "portable", "v3.0.0");
        var home2 = Path.Combine(holder, LauncherLayout.PortableData);
        Directory.CreateDirectory(home2);
        var kept = Built(builds2, "kept", "nightly-300-77777777");
        Built(builds2, "nightly", "v3.1.0-rc.1");                                 // (a nightly folder switched to rc, updated in place)
        var reg2 = BuildRegistry.Load(Path.Combine(Folder("unlisted-home2"), "builds.json"));
        var added2 = Leftovers.Relist(reg2, builds2, p, keep: new[] { home2 }, kept: new[] { kept });
        Require(Names(added2.Select(b => b.Folder)) == "nightly" && reg2.Builds.Single().Channel == Versions.Candidate,
                "Never a folder holding the launcher's home nor one the player kept; a channel's name is the channel only for a version on it (got: "
                + Names(added2.Select(b => b.Folder)) + ", " + reg2.Builds.FirstOrDefault()?.Channel + ").");

        // (a listed build of the channel that plays a version as new: an unlisted game of it stays unlisted)
        var builds3 = Folder("unlisted-listed");
        var listed = Built(builds3, "rc", "v3.0.0-rc.6");
        Built(builds3, "v3.0.0-rc.5", "v3.0.0-rc.5");
        var reg3 = BuildRegistry.Load(Path.Combine(Folder("unlisted-home3"), "builds.json"));
        reg3.Add(listed, "v3.0.0-rc.6", Versions.Candidate);
        Require(Leftovers.Relist(reg3, builds3, p).Count == 0 && reg3.Builds.Count == 1,
                "An unlisted game older than a listed build of its channel is not listed: an old version.");

        // (the rc channel follows stable too: a folder named rc holding a stable release goes on rc, the channel it was built on)
        var builds4 = Folder("unlisted-rc-stable");
        var rcStable = Built(builds4, "rc", "v3.0.0");
        var reg4 = BuildRegistry.Load(Path.Combine(Folder("unlisted-home4"), "builds.json"));
        Require(Leftovers.Relist(reg4, builds4, p).Count == 1 && reg4.Builds.Single() is { Version: "v3.0.0", Channel: Versions.Candidate } r4 && r4.Folder == rcStable,
                "A folder named rc holding a stable release is listed on rc: " + reg4.Builds.FirstOrDefault()?.Channel);

        // (a row whose game is not there (an unplugged drive: the folder that held it is not there either; an antivirus) never has
        // an older game of its channel listed beside it)
        var builds5 = Folder("unlisted-row-gone");
        Built(builds5, "v3.0.0-rc.3", "v3.0.0-rc.3");
        var reg5 = BuildRegistry.Load(Path.Combine(Folder("unlisted-home5"), "builds.json"));
        reg5.Add(Path.Combine(Folder("unplugged"), "drive-not-there", "rc"), "v3.0.0-rc.5", Versions.Candidate);
        Require(Leftovers.Relist(reg5, builds5, p).Count == 0 && reg5.Builds.Count == 1,
                "An older game is not listed beside a row of its channel whose drive is not there.");

        // (#187's review, 6: a row whose folder was deleted by hand (the folder that held it is there: BuildRegistry.Gone, which
        // ForgetGone drops at the home's Refresh) counts as gone already, whether Relist or ForgetGone runs first)
        var builds5b = Folder("unlisted-row-deleted");
        var rc3 = Built(builds5b, "v3.0.0-rc.3", "v3.0.0-rc.3");
        var reg5b = BuildRegistry.Load(Path.Combine(Folder("unlisted-home5b"), "builds.json"));
        reg5b.Add(Path.Combine(builds5b, "rc"), "v3.0.0-rc.5", Versions.Candidate);
        Require(Leftovers.Relist(reg5b, builds5b, p) is [{ Version: "v3.0.0-rc.3" } a5b] && a5b.Folder == rc3,
                "A row whose folder was deleted by hand does not hold back the older game of its channel: the same at the first start as at the next.");

        // (#187's review, 3 and 4: the channel a row is on decides, each row's own. builds/rc and builds/stable both at v3.0.0 are
        // two instances, stable first (the default when the list has none), rc on rc; Find offers neither)
        var builds7 = Folder("unlisted-same-version");
        var st7 = Built(builds7, "stable", "v3.0.0");
        var rc7 = Built(builds7, "rc", "v3.0.0");
        var reg7 = BuildRegistry.Load(Path.Combine(Folder("unlisted-home7"), "builds.json"));
        var added7 = Leftovers.Relist(reg7, builds7, p);
        Require(added7.Count == 2 && reg7.Builds.SingleOrDefault(b => b.Folder == st7)?.Channel == Versions.Stable
                && reg7.Builds.SingleOrDefault(b => b.Folder == rc7)?.Channel == Versions.Candidate && reg7.Default?.Folder == st7,
                "One version in builds/stable and builds/rc: both listed, each on its folder's channel, the stable one the default (got: "
                + string.Join(", ", reg7.Builds.Select(b => Path.GetFileName(b.Folder) + " " + b.Channel + (b.Default ? " default" : ""))) + ").");
        Require(Leftovers.Find(builds7, reg7.Builds.Select(b => b.Folder), p).Count == 0, "Neither is offered for deletion.");

        // (a stable row at v3.0.0 holds back no rc-channel game: rows of another channel don't count)
        var builds8 = Folder("unlisted-other-channel");
        var rc8 = Built(builds8, "rc", "v3.0.0-rc.5");
        var reg8 = BuildRegistry.Load(Path.Combine(Folder("unlisted-home8"), "builds.json"));
        var st8 = reg8.Add(Built(builds8, "stable", "v3.0.0"), "v3.0.0", Versions.Stable);
        Require(Leftovers.Relist(reg8, builds8, p) is [{ Channel: Versions.Candidate } a8] && a8.Folder == rc8 && reg8.Default?.Id == st8.Id,
                "A stable row does not hold back the rc channel's own game, and the default the list had stays.");

        // (#187's review, 5: a second build's folder, builds/rc (2) or builds/nightly (2)/<version>, is on the channel its name says)
        var builds9 = Folder("unlisted-numbered");
        var rc9 = Built(builds9, "rc (2)", "v3.0.0");
        var n9 = Built(Path.Combine(builds9, "nightly (2)"), "nightly-400-12345678", "nightly-400-12345678");
        var reg9 = BuildRegistry.Load(Path.Combine(Folder("unlisted-home9"), "builds.json"));
        Leftovers.Relist(reg9, builds9, p);
        Require(reg9.Builds.SingleOrDefault(b => b.Folder == rc9)?.Channel == Versions.Candidate
                && reg9.Builds.SingleOrDefault(b => b.Folder == n9)?.Channel == Versions.Nightly,
                "builds/rc (2) holding a stable release stays on rc, builds/nightly (2)/<version> on nightly (got: "
                + string.Join(", ", reg9.Builds.Select(b => Path.GetFileName(b.Folder) + " " + b.Channel)) + ").");

        // (#187's review, 1, 2 and 7: a recovery, once. A first start, or a builds.json that was lost: yes. A launcher that never ran
        // it: once. After that, never: a build the player took off the list stays off. And never when builds.json could not be read,
        // which would be written over)
        var home10 = Folder("unlisted-once");
        var list10 = Path.Combine(home10, "builds.json");
        Require(Leftovers.ShouldRelist(BuildRegistry.Load(list10), relistedBefore: true), "No builds.json (a first start, or a lost one): listed again.");
        var reg10 = BuildRegistry.Load(list10);
        var b10 = reg10.Add(Built(Folder("unlisted-once-builds"), "rc", "v3.0.0-rc.5"), "v3.0.0-rc.5", Versions.Candidate);
        reg10.Save();
        Require(Leftovers.ShouldRelist(BuildRegistry.Load(list10), relistedBefore: false), "A list that never had it done: once.");
        reg10.Remove(b10.Id);
        reg10.Save();
        Require(!Leftovers.ShouldRelist(BuildRegistry.Load(list10), relistedBefore: true),
                "Done once: a build taken off the list (Delete with its folder left, or a folder deleted by hand) is not listed again.");
        File.WriteAllText(list10, "{ \"format\": 2, \"builds\": [] }");
        var broken10 = BuildRegistry.Load(list10);
        Require(broken10.Problem != null && !Leftovers.ShouldRelist(broken10, relistedBefore: false),
                "A builds.json that could not be read (a newer launcher's) is never relisted into, so never written over.");

        // (a portable launcher's lost update: its builds folder is in its home, which is no reason to leave the game unlisted)
        var lostRow = new BuildEntry { Id = "a", Folder = Path.Combine(builds, "gone"), Version = "v3.0.0-rc.4", Channel = Versions.Candidate };
        Require(Leftovers.LostUpdate(builds, lostRow, Array.Empty<string>(), p, keep: new[] { home }) is { Version: "v3.0.0-rc.5" } l && l.Folder == rc,
                "A row that lost its update's game takes it back from a portable launcher's own builds folder.");

        // (#184's layout, builds/<track>/<version>, InstanceFolders.Folders: a game in a track's folder whose row went is listed again,
        // on the track its folder is in, beside one from before the layout right in the builds folder; an older one of a track stays
        // unlisted and is offered for deletion; the track's folder that holds them is no game of its own)
        var builds6 = Folder("unlisted-layout");
        var n310 = Built(Path.Combine(builds6, "nightly"), "nightly-310-88888888", "nightly-310-88888888");
        var n305 = Built(Path.Combine(builds6, "nightly"), "nightly-305-99999999", "nightly-305-99999999");
        var rc2 = Built(Path.Combine(builds6, "rc"), "v3.1.0-rc.2", "v3.1.0-rc.2");
        var flat = Built(builds6, "stable", "v3.0.1");                         // (an instance from before the layout: builds/stable)
        var reg6 = BuildRegistry.Load(Path.Combine(Folder("unlisted-home6"), "builds.json"));
        var added6 = Leftovers.Relist(reg6, builds6, p);
        Require(added6.Count == 3 && reg6.Builds.SingleOrDefault(b => b.Folder == n310) is { Version: "nightly-310-88888888", Channel: Versions.Nightly }
                && reg6.Builds.SingleOrDefault(b => b.Folder == rc2) is { Version: "v3.1.0-rc.2", Channel: Versions.Candidate }
                && reg6.Builds.SingleOrDefault(b => b.Folder == flat) is { Version: "v3.0.1", Channel: Versions.Stable },
                "In the layout, builds/nightly/<version> and builds/rc/<version> are listed again on their track's channel, beside an old "
                + "builds/stable (got: " + string.Join(", ", reg6.Builds.Select(b => Path.GetFileName(b.Folder) + " " + b.Channel)) + ").");
        Require(reg6.Builds.All(b => b.Folder != n305 && b.Folder != Path.Combine(builds6, "nightly")),
                "The older nightly stays unlisted, and the track's folder is no game.");
        var offered6 = Leftovers.Find(builds6, reg6.Builds.Select(b => b.Folder), p).Select(l => l.Folder).ToList();
        Require(offered6.SequenceEqual(new[] { n305 }), "The older nightly in the track's folder is offered for deletion (got: " + string.Join(", ", offered6) + ").");
    }

    /// LauncherService.Relist in its source: nothing when builds.json could not be read, and Leftovers.Relist only when
    /// ShouldRelist says so (not at every start), remembered in the settings
    static void RelistSourceChecks(string repo)
    {
        var service = File.ReadAllText(Path.Combine(repo, "App", "LauncherService.cs"));
        var body = service[service.IndexOf("public IReadOnlyList<BuildEntry> Relist()", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\n    }", StringComparison.Ordinal)];
        Require(body.Contains("if (Registry.Problem != null) return") && body.Contains("Leftovers.ShouldRelist(Registry,")
                && body.IndexOf("Leftovers.ShouldRelist(", StringComparison.Ordinal) < body.IndexOf("Leftovers.Relist(", StringComparison.Ordinal)
                && body.Contains("Settings.RelistedLostBuilds = true"),
                "LauncherService.Relist lists lost games again only when ShouldRelist says so, remembers it, and does nothing on a list it could not read.");
    }
}
