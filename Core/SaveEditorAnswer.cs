using System.Text.Json;

namespace LongshotLauncher.Core;

/// What Longshot's save editor says to a command run with --json (its guide, "For programs"): one JSON object on stdout with
/// `schema`, `command` and `ok`; a refusal exits 1 and says `{"ok": false, "error": {"code", "message"}}` (`games` for a game
/// running, `backup` when it put the files back). Anything that is not that is a program's failure, not an answer
static class SaveEditorAnswer
{
    /// The newest schema this launcher reads: the editor raises it only when a key is removed or changes meaning
    public const int Schema = 1;

    /// The object of an answer that says ok (the caller disposes it); else a SaveFailure: the editor's refusal with its code and
    /// words, or what a command that printed no answer left (argparse's own usage text, a Python that stopped)
    public static JsonDocument Of(SaveEditorRun done, string command)
    {
        var doc = Parse(done.Output);
        if (doc == null) throw done.Exit == 0 ? NotAnAnswer() : Stopped(done);
        try
        {
            var root = doc.RootElement;
            if (root.GetProperty("schema").GetInt32() > Schema)
                throw new SaveFailure(Tr.T("This version's save editor is newer than the launcher knows. Update the launcher."));
            if (root.GetProperty("command").GetString() != command) throw NotAnAnswer();
            if (root.GetProperty("ok").GetBoolean() && done.Exit == 0) return doc;
            throw root.GetProperty("ok").GetBoolean() ? Stopped(done) : Refusal(root);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            doc.Dispose();
            throw NotAnAnswer();
        }
        catch
        {
            doc.Dispose();
            throw;
        }
    }

    /// An editor's refusal as a SaveFailure (done: what the command left), from its JSON, else from what it printed
    public static SaveFailure Failure(SaveEditorRun done)
    {
        try
        {
            using var doc = Parse(done.Output);
            if (doc != null && doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False) return Refusal(doc.RootElement);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException) { }
        return Stopped(done);
    }

    static JsonDocument? Parse(string text)
    {
        try
        {
            var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
            doc.Dispose();
        }
        catch (JsonException) { }
        return null;
    }

    static SaveFailure NotAnAnswer() => new(Tr.T("The save editor's answer is not in a form this launcher reads."));

    static SaveFailure Refusal(JsonElement root)
    {
        var error = root.GetProperty("error");
        var code = error.GetProperty("code").GetString() ?? "error";
        var message = error.GetProperty("message").GetString() ?? "";
        var games = error.TryGetProperty("games", out var g) && g.ValueKind == JsonValueKind.Array
            ? g.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList() : [];
        var backup = error.TryGetProperty("backup", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        var reason = code == "running" ? SaveRefusal.Running : code == "nx2navi_pending" ? SaveRefusal.Waiting : SaveRefusal.Other;
        return new SaveFailure(Say(code, message, games), reason, games, backup, code);
    }

    /// What the window says for a refusal's code. Most refusals carry what only the editor knows (the most hearts the game gives, the
    /// bag to give first, the name that fits two items), so its own words stay the answer for them and for any code this launcher
    /// does not know; the codes below are the ones the launcher can say better, in its own words, with the editor's words after
    /// them where they hold something the sentence does not
    public static string Say(string code, string message, IReadOnlyList<string> games) => code switch
    {
        "running" => Tr.F("{0} is running. A running game writes its save again when it quits and would undo the edit. Close it, then try again.",
                          string.Join(Tr.T(" and "), games.Count > 0 ? games : [Tr.T("A game")])),
        "nx2navi_pending" => Tr.T("NX2navi has a save waiting. Start NX2navi and quit it once, then try again."),
        "link" => Tr.F("The save folder holds a link, and the save editor will not write through one. ({0})", message),
        "backup_inside_save" => Tr.F("The backup folder is inside the save folder, so the editor will not use it. ({0})", message),
        "write_failed" => Tr.F("The edit could not be written, and the files were put back as they were. ({0})", message),
        "io_error" => Tr.F("A file could not be read or written: {0}", message),
        "no_game" => Tr.F("The save editor could not read this build's game files: {0}", message),
        "no_zstd" => Tr.F("The Python of this version's tools cannot read the game's compressed files: {0}", message),
        "unsupported_game_file" => Tr.F("This game version has a file the save editor cannot read: {0}", message),
        "other_version" => Tr.F("This save was written by another game version, so it is not edited. Use the build that wrote it. ({0})", message),
        "would_overwrite" => Tr.T("That slot already holds a save. Choose to replace it, or another slot."),
        "same_slot" => Tr.T("That is the same slot. Choose another."),
        "needs_age" => Tr.F("That area has both ages: choose Link's age. ({0})", message),
        "damaged" => Tr.F("A save file is damaged: {0}", message),
        "no_slot" => Tr.F("That slot is not in the save any more. Go back to the Saves page. ({0})", message),
        _ => message,
    };

    /// A command that printed no answer: its last line on stderr (argparse's usage error, a Python traceback's last line), else
    /// its exit code
    static SaveFailure Stopped(SaveEditorRun done)
    {
        var last = done.Error.Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.Length > 0);
        return new SaveFailure(last ?? Tr.F("The save editor stopped with exit code {0} without saying why.", done.Exit));
    }
}
