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

        // (a row whose game is not there (an unplugged drive, an antivirus) never has an older game of its channel listed beside it)
        var builds5 = Folder("unlisted-row-gone");
        Built(builds5, "v3.0.0-rc.3", "v3.0.0-rc.3");
        var reg5 = BuildRegistry.Load(Path.Combine(Folder("unlisted-home5"), "builds.json"));
        reg5.Add(Path.Combine(Folder("unplugged"), "rc"), "v3.0.0-rc.5", Versions.Candidate);
        Require(Leftovers.Relist(reg5, builds5, p).Count == 0 && reg5.Builds.Count == 1,
                "An older game is not listed beside a row of its channel whose game is not there.");

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
}
