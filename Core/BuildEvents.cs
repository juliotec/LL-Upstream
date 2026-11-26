using System.Text.Json;

namespace LongshotLauncher.Core;

/// One event of longshot-builder --json (Longshot's docs/dev/reference/build-events.md, build-core's report.rs): a JSON object
/// a line on stdout, with its format's version "v" and its "type". Only version 1 is read; a field it does not know is passed
/// over, as the format asks
public abstract record BuildEvent
{
    public sealed record Stage(string Id, string Text) : BuildEvent;
    /// Fraction: the step's own, 0..1; Overall: the whole build's, 0..1 (by the builder's usual shares of the time)
    public sealed record Progress(string Id, double Fraction, double Overall) : BuildEvent;
    public sealed record CompilePlan(int Changed, int Total) : BuildEvent;
    /// Result: compiled, cached, retried (compiled again alone after failing among others) or failed
    public sealed record Unit(string Name, double Seconds, string Result) : BuildEvent;
    public sealed record Eta(double Seconds) : BuildEvent;
    public sealed record Log(string Text) : BuildEvent;
    public sealed record Warning(string Code, string Text) : BuildEvent;
    /// longshot-builder check's answer: Build, the game's main module's build id (short)
    public sealed record Check(bool Ok, string Build, string Text) : BuildEvent;
    /// longshot-builder share's answer: the pipeline list zipped, for the issue to attach it to, with the text to paste there
    public sealed record Shared(string Zip, int Pipelines, string Issue, string Template) : BuildEvent;
    /// The build stopped: the last event of a failed run. Code: stable (Problems.FromCode); Text: the builder's sentence
    public sealed record Error(string Code, string Text) : BuildEvent;
    /// The build is done: the last event of a build that worked. PreviousSeconds: the last build's in this work folder
    public sealed record Done(string Folder, double Seconds, double CompileSeconds, double? PreviousSeconds) : BuildEvent;
    /// longshot-builder fetch as it goes: Step (list, manifest, delta, patch, check, full, place), the delta of a chain (Hop of
    /// Hops, 0 of 0 for the full file), the step's downloads (Bytes of Total; Total 0 when not a download)
    public sealed record FetchProgress(string Step, int Hop, int Hops, long Bytes, long Total) : BuildEvent;
    /// longshot-builder fetch's answer, the program in place: How (present, delta, chain, full), From (the version a delta or
    /// chain started from), Downloaded (bytes)
    public sealed record Fetched(string Version, string Program, string How, string? From, long Downloaded) : BuildEvent;
    /// longshot-builder from-source's answer, just before its done: the version folder made, its program, the commit and the
    /// version's name it carries
    public sealed record FromSource(string Folder, string Program, string Commit, string Version) : BuildEvent;
    /// longshot-builder steam-shortcut's answer: Action (added, updated, unchanged, removed, absent), the tile's Steam id, the
    /// library written, and whether Steam runs (the change shows once it starts again)
    public sealed record SteamShortcut(string Action, string Name, uint AppId, string Library, bool SteamRunning) : BuildEvent;
    /// longshot-builder storage's answer (BuilderStorage)
    public sealed record Storage(BuilderStorage Value) : BuildEvent;
    /// longshot-builder info's answer (BuilderInfo)
    public sealed record Info(BuilderInfo Value) : BuildEvent;
}

public static class BuildEvents
{
    /// The events' format this launcher reads (build-core's report::VERSION)
    public const int Version = 1;

    /// The warning that says the shaders are left to the game's first start
    public const string ShadersNotPrepared = "shaders_not_prepared";

    /// The warning that says a unit the compiler refused came out different when made again from the same translation: the PC
    /// changed what the build made (its memory, processor or drive). The build compiled it again and goes on
    /// (Problems.FromWarning: the Ready page says it)
    public const string TranslationDamaged = "translation_damaged";

    /// A line of stdout read; null for one that is not an event of version 1 with its fields (blank, not JSON, another
    /// version, a type this launcher does not know, a field of the wrong kind)
    public static BuildEvent? Parse(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object || !Int(e, "v", out var v) || v != Version || Str(e, "type") is not { } type) return null;
            switch (type)
            {
                case "stage" when Str(e, "stage") is { } id:
                    return new BuildEvent.Stage(id, Str(e, "text") ?? "");
                case "progress" when Str(e, "stage") is { } id && Num(e, "fraction", out var f):
                    return new BuildEvent.Progress(id, Math.Clamp(f, 0, 1), Num(e, "overall", out var o) ? Math.Clamp(o, 0, 1) : 0);
                case "compile_plan" when Int(e, "changed", out var changed) && Int(e, "total", out var total) && changed >= 0 && total >= 0:
                    return new BuildEvent.CompilePlan((int)changed, (int)total);
                case "unit" when Str(e, "name") is { } name && Str(e, "result") is { } result:
                    return new BuildEvent.Unit(name, Num(e, "seconds", out var s) ? Math.Max(0, s) : 0, result);
                case "eta" when Num(e, "seconds", out var eta):
                    return new BuildEvent.Eta(Math.Max(0, eta));
                case "log" when Str(e, "text") is { } text:
                    return new BuildEvent.Log(text);
                case "warning" when Str(e, "code") is { } code:
                    return new BuildEvent.Warning(code, Str(e, "text") ?? "");
                case "check" when e.TryGetProperty("ok", out var ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    return new BuildEvent.Check(ok.GetBoolean(), Str(e, "build") ?? "", Str(e, "text") ?? "");
                case "shared" when Str(e, "zip") is { } zip:
                    return new BuildEvent.Shared(zip, Int(e, "pipelines", out var n) ? (int)n : 0, Str(e, "issue") ?? "", Str(e, "template") ?? "");
                case "error" when Str(e, "code") is { } code:
                    return new BuildEvent.Error(code, Str(e, "text") ?? "");
                case "fetch_progress" when Str(e, "step") is { } step:
                    return new BuildEvent.FetchProgress(step, Int(e, "hop", out var hop) ? (int)hop : 0, Int(e, "hops", out var hops) ? (int)hops : 0,
                                                        Int(e, "bytes", out var bytes) ? Math.Max(0, bytes) : 0, Int(e, "total", out var all) ? Math.Max(0, all) : 0);
                case "fetched" when Str(e, "version") is { } version && Str(e, "program") is { } program:
                    return new BuildEvent.Fetched(version, program, Str(e, "how") ?? "", Str(e, "from"), Int(e, "downloaded", out var got) ? got : 0);
                case "from_source" when Str(e, "folder") is { } made && Str(e, "commit") is { } commit:
                    return new BuildEvent.FromSource(made, Str(e, "program") ?? "", commit, Str(e, "version") ?? "");
                case "steam_shortcut" when Str(e, "action") is { } action:
                    return new BuildEvent.SteamShortcut(action, Str(e, "name") ?? "", Int(e, "app_id", out var appId) && appId is >= 0 and <= uint.MaxValue ? (uint)appId : 0,
                                                        Str(e, "library") ?? "", e.TryGetProperty("steam_running", out var up) && up.ValueKind == JsonValueKind.True);
                case "storage" when Str(e, "cache") is { } cache && e.TryGetProperty("kinds", out var kinds) && kinds.ValueKind == JsonValueKind.Array:
                    return new BuildEvent.Storage(new BuilderStorage(cache, Int(e, "budget", out var budget) ? budget : 0, Int(e, "total", out var sum) ? sum : 0,
                        kinds.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.Object && Str(k, "kind") is not null)
                             .Select(k => new BuilderStorage.Part(Str(k, "kind")!, Int(k, "bytes", out var b) ? Math.Max(0, b) : 0, Int(k, "entries", out var n) ? n : 0))
                             .ToList()));
                case "info" when BuilderInfo.Parse(e) is { } info:
                    return new BuildEvent.Info(info);
                case "done" when Str(e, "folder") is { } folder:
                    return new BuildEvent.Done(folder, Num(e, "seconds", out var secs) ? secs : 0, Num(e, "compile_seconds", out var c) ? c : 0,
                                               Num(e, "previous_seconds", out var p) ? p : null);
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    /// An event as BuildProgress and the build's details take the old builder's lines (MakeGameLine): the stages have the same
    /// names (read, lift, compile, finish, shaders), a progress keeps its stage, a unit compiled or retried counts as a file
    /// compiled, the warning that the shaders were left to the first start skips their row, an error keeps its code and done its
    /// times (the details say them). Null for one the progress has no use for (share's answer)
    public static MakeGameLine? ToLine(BuildEvent e) => e switch
    {
        BuildEvent.Stage s => new MakeGameLine.Stage(s.Id, s.Text),
        BuildEvent.Progress p => new MakeGameLine.Progress(p.Fraction, p.Id),
        BuildEvent.CompilePlan c => new MakeGameLine.Plan(c.Changed, c.Total),
        // (the old builder's kinds: "new" and "again" are counted as compiled, "cached" not)
        BuildEvent.Unit u => new MakeGameLine.File(u.Name, u.Seconds, u.Result switch
        {
            "compiled" => "new",
            "retried" => "again",
            _ => u.Result,
        }),
        BuildEvent.Eta t => new MakeGameLine.Eta(t.Seconds),
        BuildEvent.Log l => new MakeGameLine.Plain(l.Text),
        // (its text is "The shaders were not prepared now (why): ...": the row says why)
        BuildEvent.Warning { Code: ShadersNotPrepared } w => new MakeGameLine.NotPrepared(
            w.Text.IndexOf('(') is var open and >= 0 && w.Text.IndexOf("):", open, StringComparison.Ordinal) is var close and > 0
                ? w.Text[(open + 1)..close] : w.Text),
        BuildEvent.Warning w => new MakeGameLine.Warning(w.Code, w.Text),
        BuildEvent.Check c => new MakeGameLine.Check(c.Ok, c.Text),
        BuildEvent.Error x => new MakeGameLine.Error(x.Text, x.Code),
        BuildEvent.Done d => new MakeGameLine.Done(d.Folder, d.Seconds, d.CompileSeconds, d.PreviousSeconds),
        _ => null,
    };

    /// The details' line for a done event's times, in English as the rest of the details: "built in 812 s (compile 530 s; the last
    /// build 790 s)"
    public static string DoneLine(double seconds, double compileSeconds, double? previousSeconds) =>
        FormattableString.Invariant($"built in {seconds:0} s (compile {compileSeconds:0} s")
        + (previousSeconds is double p ? FormattableString.Invariant($"; the last build {p:0} s") : "") + ")";

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    static bool Num(JsonElement e, string name, out double v)
    {
        v = 0;
        return e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out v) && double.IsFinite(v);
    }

    static bool Int(JsonElement e, string name, out long v)
    {
        v = 0;
        return e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out v);
    }
}
