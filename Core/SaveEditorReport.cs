using System.Text.Json;
using System.Text.RegularExpressions;

namespace LongshotLauncher.Core;

/// One thing a slot has not got (or the file cannot say): the id `set` takes ("skulltulas/KakarikoVillage/2"), its name in the
/// game's own text, its area ("" for an item), how many of how many, and what the report adds ("0 of 20", "in hand: 2")
public sealed record SaveEditorEntry(string Id, string Label, string Area, int Have, int Of, string? Note);

/// An area of a category and how many of its entries the slot has
public sealed record SaveEditorArea(string Area, int Have, int Of)
{
    public bool Complete => Have >= Of;
}

/// One category of a slot's report: what it counts, how many the slot has, and what is missing, by area and by name.
/// Counted: false for the mini-games, which the report lists and never counts toward 100%
public sealed record SaveEditorCategory(string Id, string Title, int Have, int Of, string? Note, bool Counted,
                                        IReadOnlyList<SaveEditorArea> Areas, IReadOnlyList<SaveEditorEntry> Missing,
                                        IReadOnlyList<SaveEditorEntry> Unreadable)
{
    public bool Complete => Have >= Of && Unreadable.Count == 0;

    /// The text report leaves out a category the game has nothing in
    public bool Listed => Of > 0 || Unreadable.Count > 0;

    public double Fraction => Of <= 0 ? 1 : Math.Clamp((double)Have / Of, 0, 1);
}

/// What the save editor says of one slot (`report --json`: Longshot's scripts/longshot_save_editor.py evaluate). Unreadable: why this
/// slot's file could not be read (then nothing else is set); Problem: a save of another game version
public sealed record SaveEditorSlot(string Slot, string? Unreadable, string? Problem, string? Place, string? Age, double Hearts, long Rupees,
                                    string? Clock, bool Cleared, string? Saved, bool Complete, int MissingTotal,
                                    IReadOnlyList<SaveEditorCategory> Categories)
{
    /// As the Saves page numbers it: slot_00 is 1
    public int Number => SaveText.Number(Slot);
    public IReadOnlyList<SaveEditorCategory> Listed => Categories.Where(c => c.Listed).ToList();
}

/// The report of a save folder: its slots (a damaged one does not hide the others)
public sealed record SaveEditorReport(string Save, IReadOnlyList<SaveEditorSlot> Slots)
{
    /// The editor's answer to `report --json` (SaveEditorAnswer.Of: a SaveFailure when it is not that answer)
    public static SaveEditorReport Parse(string json)
    {
        using var doc = SaveEditorAnswer.Of(new SaveEditorRun(0, json, ""), "report");
        return Read(doc.RootElement);
    }

    /// The report in an answer that said ok; a SaveFailure when it lacks what the launcher reads
    public static SaveEditorReport Read(JsonElement root)
    {
        try
        {
            var slots = root.GetProperty("slots").EnumerateArray().Select(Slot).ToList();
            return new SaveEditorReport(root.GetProperty("save").GetString() ?? "", slots);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new SaveFailure(Tr.T("The save editor's report is not in a form this launcher reads."));
        }
    }

    static SaveEditorSlot Slot(JsonElement s)
    {
        var name = s.GetProperty("slot").GetString() ?? throw new FormatException("slot");
        if (s.TryGetProperty("unreadable", out var bad) && bad.ValueKind == JsonValueKind.String)
            return new SaveEditorSlot(name, bad.GetString(), null, null, null, 0, 0, null, false, null, false, 0, Array.Empty<SaveEditorCategory>());
        var place = s.GetProperty("place");
        return new SaveEditorSlot(name, null, Text(s, "problem"), Text(place, "name"), Text(s, "age"), s.GetProperty("hearts").GetDouble(),
                                  s.GetProperty("rupees").GetInt64(), Text(s, "clock"), s.GetProperty("cleared").GetBoolean(), Text(s, "saved"),
                                  s.GetProperty("complete").GetBoolean(), s.GetProperty("missing_total").GetInt32(),
                                  s.GetProperty("categories").EnumerateArray().Select(Category).ToList());
    }

    static SaveEditorCategory Category(JsonElement c) =>
        new(c.GetProperty("id").GetString() ?? "", c.GetProperty("title").GetString() ?? "", c.GetProperty("have").GetInt32(),
            c.GetProperty("of").GetInt32(), Text(c, "note"), c.GetProperty("counted").GetBoolean(),
            c.GetProperty("areas").EnumerateArray().Select(a => new SaveEditorArea(a.GetProperty("area").GetString() ?? "",
                                                                                     a.GetProperty("have").GetInt32(), a.GetProperty("of").GetInt32())).ToList(),
            c.GetProperty("missing").EnumerateArray().Select(Entry).ToList(),
            c.GetProperty("unreadable").EnumerateArray().Select(Entry).ToList());

    // (an unreadable entry says only its id, label and area)
    static SaveEditorEntry Entry(JsonElement m) =>
        new(m.GetProperty("id").GetString() ?? "", m.GetProperty("label").GetString() ?? "", m.GetProperty("area").GetString() ?? "",
            m.TryGetProperty("have", out var have) ? have.GetInt32() : 0, m.TryGetProperty("of", out var of) ? of.GetInt32() : 0, Text(m, "note"));

    static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// The words the report prints, as the tool prints them (scripts/longshot_save_editor.py humanize)
public static partial class SaveEditorText
{
    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex Camel();

    [GeneratedRegex(" +")]
    private static partial Regex Spaces();

    /// Weapon_Sword_Master is "Weapon Sword Master", KakarikoVillage "Kakariko Village"
    public static string Humanize(string identifier) => Spaces().Replace(Camel().Replace(identifier.Replace('_', ' '), "$1 $2"), " ").Trim();

    /// A percentage as the page says it: whole numbers, never "100%" while one thing is missing
    public static int Percent(int have, int of) => of <= 0 ? 100 : have >= of ? 100 : Math.Min(99, (int)Math.Floor(100.0 * have / of));

    /// The hearts as the report says them: "3" and "10.5" (the tool's %g)
    public static string Hearts(double hearts) => hearts.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
