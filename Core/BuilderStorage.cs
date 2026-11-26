namespace LongshotLauncher.Core;

/// What longshot-builder's cache holds, by kind, as the builder counts it against its budget: `longshot-builder storage --cache
/// &lt;the launcher's builder cache&gt; --output json`'s storage event (Longshot's build.md "The cache's budget"). The Storage page
/// shows the builder's cache from it rather than walking its folders. Kinds: objects, lifts, shaders, runtimes, tools, sources,
/// work, mods (and any later one); each its bytes (a file hard-linked elsewhere counts its share) and how many entries
public sealed record BuilderStorage(string Cache, long Budget, long Total, IReadOnlyList<BuilderStorage.Part> Kinds)
{
    public sealed record Part(string Kind, long Bytes, long Entries);

    /// The cache's folder of shader caches (&lt;cache&gt;/shaders/&lt;translator stamp&gt;/&lt;driver&gt;/), the shaders kind's
    public const string Shaders = "shaders";

    /// The command line: storage --cache &lt;cache&gt; --output json
    public static IReadOnlyList<string> Arguments(string cache) => ["storage", "--cache", cache, .. LongshotBuilder.OutputJson];

    /// The Storage page's kind for one of the builder's: its work folders (each version's last build), its stores (the compiled
    /// code and translations kept for switching versions), the shader caches it shares, the Mod API's store, and the rest (the
    /// unpacked sources and tools, the vendors' files, a Rust from-source installed) the builders' own, which only Remove
    /// everything takes
    public static Storage.Kind KindOf(string kind) => kind switch
    {
        "work" => Storage.Kind.BuilderFiles,
        "objects" => Storage.Kind.OlderCode,
        "lifts" => Storage.Kind.OlderTranslations,
        "shaders" => Storage.Kind.ShaderStore,
        "mods" => Storage.Kind.ModsAndSettings,
        _ => Storage.Kind.Builders,
    };

    /// The cache as builder counts it; null when it gave no storage event (an older builder, or one that did not start)
    public static async Task<BuilderStorage?> AskAsync(LongshotBuilder builder, string cache, CancellationToken cancel = default) =>
        (await builder.AskAsync(Arguments(cache), cancel: cancel).ConfigureAwait(false)).Events.OfType<BuildEvent.Storage>().LastOrDefault()?.Value;
}
