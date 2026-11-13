using System.Globalization;
using System.Text.RegularExpressions;

namespace LongshotLauncher.Core;

/// Longshot's tools.lock (docs/releases.md, "tools.lock"; scripts/tools.py and builder/setup/setup.c read it the same way):
///     tool &lt;name&gt; &lt;version&gt; &lt;system&gt; &lt;sha256&gt; &lt;size&gt; &lt;dest&gt; &lt;prefix&gt; &lt;url&gt; [&lt;url&gt; ...]
/// Each tool is a part of a builder's install (windows-x64: the Windows builder's; linux-x64: beside the unpacked Linux AppImage,
/// from the tools.lock inside it): its archive's &lt;prefix&gt; folder ("-": all of it) becomes &lt;dest&gt;.
/// The first URL is tried first (Longshot's copy, a generic package of the MagicBeanDev organization), the others (where the
/// archive came from) when it fails or gives other bytes. A tool another
/// installed version has (the same SHA-256, unpacked the same way) is taken from it, not downloaded again (Toolchains).
public static class ToolsLock
{
    /// The builder in the release zip, beside its tools (the setup's run line)
    public const string Run = "LongshotBuilder/LongshotBuilder.exe";
    static readonly Regex Sha256 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    static readonly Regex Name = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<Part> Parse(string text, string system)
    {
        var parts = new List<Part>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (w[0] != "tool" || w.Length < 9) throw new FormatException("Not a tools.lock line: " + line);
            string name = w[1], sys = w[3], sha = w[4], dest = w[6], prefix = w[7];
            if (!Name.IsMatch(name) || !Sha256.IsMatch(sha) || !PartsManifest.NameOk(dest, true) || dest.Contains('\\')
                || (prefix != "-" && (!PartsManifest.NameOk(prefix, true) || prefix.Contains('\\')))
                || !long.TryParse(w[5], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size <= 0
                || w.Skip(8).Any(u => !u.StartsWith("https://", StringComparison.Ordinal)))
                throw new FormatException("A tools.lock line the builder does not accept: " + line);
            if (sys != system) continue;
            // (as a part: unpacked into dest's parent, its prefix folder renamed to dest's last name)
            var slash = dest.LastIndexOf('/');
            var parent = slash < 0 ? "." : dest[..slash];
            var leaf = dest[(slash + 1)..];
            parts.Add(prefix == "-"
                ? new Part(sha, size, dest, null, null, w[8], null, w.Skip(9).ToList())
                : new Part(sha, size, parent, prefix, leaf, w[8], null, w.Skip(9).ToList()));
        }
        return parts;
    }
}
