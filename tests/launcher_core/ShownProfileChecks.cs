using LongshotLauncher.Core;

/// Saves.ShownProfile: the profile of Longshot's save the Saves page lists and the save editor (Progress) edits is the one the game
/// opens (Saves.UserId), not the first folder found: a stray profile beside it (a copy made by hand) sorts first and the game never reads it
static partial class Program
{
    static void ShownProfileChecks()
    {
        var root = Folder("shown-profile");
        SaveEnvironment Env(string name) => new(Platform.Linux, k => k switch { "HOME" => Path.Combine(root, name, "home"), "XDG_DATA_HOME" => Path.Combine(root, name, "data"), _ => null });
        void Profile(SaveEnvironment env, string id)
        {
            var folder = Path.Combine(env.LongshotUser, "save", id);
            Directory.CreateDirectory(Path.Combine(folder, "slot_00"));
            File.WriteAllText(Path.Combine(folder, "account.sav"), "account");
            File.WriteAllText(Path.Combine(folder, "slot_00", "progress.sav"), "progress");
        }
        var stray = "0123456789abcdef0123456789abcdef";

        var none = Env("none");
        Require(Saves.ShownProfile(none) == null, "No save yet: no profile is shown.");
        var only = Env("one");
        Profile(only, Saves.UserId);
        Require(Saves.ShownProfile(only) == Saves.ImportTarget(only), "One profile, the game's: it is the one shown.");
        var both = Env("both");
        Profile(both, stray);
        Profile(both, Saves.UserId);
        Require(Saves.Shared(both).Profiles[0].Id == stray, "(the stray profile sorts before the game's, as a copy made by hand does)");
        Require(Saves.ShownProfile(both) == Saves.ImportTarget(both), "A stray profile beside the game's does not come first: the game's is shown, and edited.");
        var onlyStray = Env("stray");
        Profile(onlyStray, stray);
        Require(Saves.ShownProfile(onlyStray) == Path.Combine(onlyStray.LongshotUser, "save", stray), "With no profile of the game's, the one there is, as before.");
    }
}
