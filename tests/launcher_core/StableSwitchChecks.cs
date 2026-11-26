using LongshotLauncher.Core;

/// Switching the launcher's own channel from nightly to stable (Core/StableSwitch.cs): the offer to replace this launcher with the
/// stable one, only for that switch; the profile's saves copied back to the --user folder first (SavesCopyBack: a copy, newer files
/// there kept unless asked); Cancel changes nothing. Authored folders, no network, no window
static partial class Program
{
    static void StableSwitchChecks(string repo)
    {
        Require(StableSwitch.Offers(Versions.Nightly, Versions.Stable) && !StableSwitch.Offers(Versions.Stable, Versions.Nightly)
                && !StableSwitch.Offers(Versions.Nightly, Versions.Nightly) && !StableSwitch.Offers(Versions.Stable, Versions.Stable),
                "The offer is for nightly to stable only.");

        // ---- the saves copied back: the profile's saves/ (save/<uid>/slot_NN, save-work/) to the --user folder ----
        var root = Folder("stable-saves");
        var profile = Path.Combine(root, "profiles", "p-1a2b3c4d", "saves");
        var user = Path.Combine(root, "user");
        var uid = Saves.UserId;
        var earlier = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var later = earlier.AddDays(3);
        void Put(string folder, string rel, string text, DateTime when)
        {
            var path = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            File.SetLastWriteTimeUtc(path, when);
        }
        string? Read(string folder, string rel) =>
            File.Exists(Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar))) ? File.ReadAllText(Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar))) : null;
        Put(profile, $"save/{uid}/slot_00/progress.sav", "profile's new slot", later);
        Put(profile, $"save/{uid}/slot_01/progress.sav", "profile's, played on", later);
        Put(profile, $"save/{uid}/slot_02/progress.sav", "profile's, older", earlier);
        Put(profile, $"save/{uid}/account.sav", "same", earlier);
        Put(profile, "save-work/commit", "a commit", later);
        Put(profile, "settings.ini", "not a save", later);
        Put(user, $"save/{uid}/slot_01/progress.sav", "user's, from before", earlier);
        Put(user, $"save/{uid}/slot_02/progress.sav", "user's, played since", later);
        Put(user, $"save/{uid}/account.sav", "same", later);
        Put(user, "longshot_options.ini", "the player's settings", earlier);
        var plan = SavesCopyBack.Plan(profile, user);
        Require(plan.New.Order(StringComparer.Ordinal).SequenceEqual(new[] { "save-work/commit", $"save/{uid}/slot_00/progress.sav" }.Order(StringComparer.Ordinal))
                && plan.Older.SequenceEqual(new[] { $"save/{uid}/slot_01/progress.sav" }) && plan.Newer.SequenceEqual(new[] { $"save/{uid}/slot_02/progress.sav" })
                && !plan.Empty && plan.From == profile && plan.To == user,
                "The plan: new files, older ones there to replace, newer ones there to ask about; the same file and the profile's other files left out.");
        // (worked out, not done: what Cancel leaves is everything as it was)
        Require(Read(user, $"save/{uid}/slot_00/progress.sav") == null && Read(user, $"save/{uid}/slot_01/progress.sav") == "user's, from before"
                && Read(user, $"save/{uid}/slot_02/progress.sav") == "user's, played since",
                "The plan alone (Cancel) changes nothing in the --user folder.");
        Require(plan.Copy(replaceNewer: false) == 3
                && Read(user, $"save/{uid}/slot_00/progress.sav") == "profile's new slot" && Read(user, $"save/{uid}/slot_01/progress.sav") == "profile's, played on"
                && Read(user, "save-work/commit") == "a commit" && Read(user, $"save/{uid}/slot_02/progress.sav") == "user's, played since"
                && Read(user, "longshot_options.ini") == "the player's settings" && Read(user, "settings.ini") == null,
                "Copied: the new and the older files; a newer file there kept, and nothing else of the folder touched.");
        Require(File.GetLastWriteTimeUtc(Path.Combine(user, "save", uid, "slot_00", "progress.sav")) == later
                && Read(profile, $"save/{uid}/slot_01/progress.sav") == "profile's, played on" && Read(profile, $"save/{uid}/slot_02/progress.sav") == "profile's, older"
                && !Directory.EnumerateFiles(user, "*.part", SearchOption.AllDirectories).Any(),
                "A copy keeps the profile's write time, the profile's own files stay, and no half-made file is left.");
        var again = SavesCopyBack.Plan(profile, user);
        Require(again.New.Count == 0 && again.Older.Count == 0 && again.Newer.SequenceEqual(new[] { $"save/{uid}/slot_02/progress.sav" }),
                "Copied once: only the newer file there is left to ask about.");
        Require(again.Copy(replaceNewer: true) == 1 && Read(user, $"save/{uid}/slot_02/progress.sav") == "profile's, older" && SavesCopyBack.Plan(profile, user).Empty,
                "Asked to: the newer file there is replaced too; then nothing is left to copy.");
        Require(SavesCopyBack.Plan(Path.Combine(root, "no-profile"), user).Empty && SavesCopyBack.Plan(user, user).Empty,
                "No saves in the profile, or the profile's saves already the --user folder's: nothing to copy.");
        if (TestLinks.Available)
        {
            var linked = Folder("stable-saves-link");
            Directory.CreateDirectory(Path.Combine(linked, "elsewhere"));
            File.WriteAllText(Path.Combine(linked, "elsewhere", "x.sav"), "not the profile's");
            Directory.CreateDirectory(Path.Combine(linked, "profile", "save"));
            Directory.CreateSymbolicLink(Path.Combine(linked, "profile", "save", "out"), Path.Combine(linked, "elsewhere"));
            Require(SavesCopyBack.Plan(Path.Combine(linked, "profile"), Path.Combine(linked, "user")).Empty, "A link in the profile's saves is never followed.");
        }

        // ---- the window, in its source: the offer instead of the switch, Cancel the safe answer, the copy before the replacement ----
        var settings = File.ReadAllText(Path.Combine(repo, "App", "ViewModels", "SettingsViewModel.cs"));
        var setter = settings[settings.IndexOf("public string LauncherChannel", StringComparison.Ordinal)..];
        setter = setter[..setter.IndexOf("\n    }", StringComparison.Ordinal)];
        Require(setter.IndexOf("StableSwitch.Offers(Main.Service.LauncherChannel, value)", StringComparison.Ordinal) is var offers and > 0
                && setter.IndexOf("Main.Modal = new StableLauncherViewModel(Main);", StringComparison.Ordinal) > offers
                && setter.IndexOf("return;", offers, StringComparison.Ordinal) < setter.IndexOf("s.LauncherChannel = value;", StringComparison.Ordinal),
                "Settings: nightly to stable opens the offer and leaves the channel as it is; any other switch is set at once.");
        var offer = File.ReadAllText(Path.Combine(repo, "App", "ViewModels", "StableLauncherViewModel.cs"));
        var cancel = offer[offer.IndexOf("    void Cancel()", StringComparison.Ordinal)..];
        cancel = cancel[..cancel.IndexOf("\n    }", StringComparison.Ordinal)];
        Require(cancel.Contains("Main.Modal = null;") && !cancel.Contains("Settings") && !cancel.Contains("Copy(") && !cancel.Contains("SwitchToStableAsync"),
                "Cancel closes the offer and changes nothing: no setting, no save, no launcher.");
        var service = File.ReadAllText(Path.Combine(repo, "App", "LauncherService.cs"));
        var swap = service[service.IndexOf("public async Task SwitchToStableAsync(", StringComparison.Ordinal)..];
        swap = swap[..swap.IndexOf("\n    }", StringComparison.Ordinal)];
        Require(swap.IndexOf("copyBack.Copy(replaceNewer)", StringComparison.Ordinal) is var copied and > 0
                && copied < swap.IndexOf("ReplaceLauncherAsync(release", StringComparison.Ordinal)
                && swap.Contains("LauncherReleases.ChannelAsync(Versions.Stable, cancel)") && swap.Contains("Settings.LauncherChannel = before;"),
                "The saves are copied before the replacement, the stable channel's newest is what replaces this launcher, and a failure puts the channel back.");
        Require(service.Contains("SavesCopyBack.Plan(LauncherProfiles.SavesHome(SaveEnv, Profile.Id) ?? SaveEnv.LongshotUser, SaveEnv.LongshotUser)"),
                "The copy goes from the current profile's saves to the --user folder the stable launcher reads.");
    }
}
