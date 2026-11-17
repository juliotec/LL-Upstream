namespace LongshotLauncher.Core;

/// A completely cold build's start (BuildJob.Cold), for a build that keeps failing or a game that misbehaves after one: what
/// earlier builds kept is deleted before make_game.py starts, so nothing of theirs is used. The build files and both stores
/// (Disk.FreeWork, as Free up space), and the game folder's shader caches with their pipeline notes (ShaderCaches.Clear: the
/// build writes the notes again). The caller also takes no caches from the build it replaces (ShaderCaches.Keep) and no game
/// files from another build (LONGSHOT_ROMFS_FROM). Never saves, settings or mods: they are not among them
public static class ColdBuild
{
    /// What was deleted: the build files' and stores' bytes, and the shader caches'
    public sealed record Cleared(long Work, long Caches);

    /// work's build files and game's shader caches deleted. Null, and nothing deleted, while Longshot runs (it holds its caches;
    /// running: the tests' stand-in). A file that cannot be deleted is the caller's exception
    public static Cleared? Clear(string work, string game, Func<bool>? running = null)
    {
        if (ShaderCaches.Clear(game, notes: true, running) is not long caches) return null;
        var bytes = Disk.WorkBytes(work);
        Disk.FreeWork(work);
        return new Cleared(bytes, caches);
    }
}
