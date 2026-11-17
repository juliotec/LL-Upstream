namespace LongshotLauncher.Core;

/// A save folder summed up as the window writes it: "3 slots, saved yesterday 22:14" (Long) or "3 saves, last yesterday 22:14"
public static class SaveText
{
    public static DateTime? Last(IReadOnlyList<SaveSlot> slots) => slots.Select(s => s.Saved).Where(t => t != null).Max();

    public static string Long(IReadOnlyList<SaveSlot> slots, DateTime now)
    {
        if (slots.Count == 0) return Tr.T("No saves yet");
        return Last(slots) is { } t ? Tr.P(slots.Count, "1 slot, saved {1}", "{0} slots, saved {1}", Durations.When(t, now))
            : Tr.P(slots.Count, "1 slot", "{0} slots");
    }

    public static string Short(IReadOnlyList<SaveSlot> slots, DateTime now)
    {
        if (slots.Count == 0) return Tr.T("No saves yet");
        return Last(slots) is { } t ? Tr.P(slots.Count, "1 save, last {1}", "{0} saves, last {1}", Durations.When(t, now))
            : Tr.P(slots.Count, "1 save", "{0} saves");
    }

    /// "3 save slots", "1 save slot", "no saves" (a row of the home's list)
    public static string Count(int slots) => slots == 0 ? Tr.T("no saves") : Tr.P(slots, "1 save slot", "{0} save slots");

    /// What the home says once Saves.Gather brought the builds' own saves together, slot by slot, from what it did (g.Slots): the
    /// slots it brought in from the old versions' folders (each named by its game folder, with the slot it went to when that is
    /// another, and its profile when there are several), the ones no slot was free for, the older different copies left in the
    /// backup, the game's options when they come from an old folder, and where the backup of them all is
    public static string Gathered(SaveGathered g)
    {
        static string? Folder(string user) => System.IO.Path.GetDirectoryName(user);
        var taken = g.Slots.Where(s => !s.Shared && !s.Left).ToList();
        var left = g.Slots.Where(s => s.Left && !s.Older).ToList();
        var older = g.Slots.Where(s => s.Left && s.Older).ToList();
        if (!g.WasShared && left.Count == 0 && older.Count == 0 && taken.All(s => s.From == g.From && s.MovedTo == null))
            return Tr.F("Every Longshot version now uses the same saves: the newest, from {0}. All of them are backed up in {1}.", Folder(g.From), g.Backup);
        if (taken.Count == 0 && left.Count == 0 && older.Count == 0)
            return Tr.F("Every Longshot version now uses the same saves: the shared ones, which already held everything the other folders had. All of them are backed up in {0}.", g.Backup);
        var profiles = taken.Concat(left).Concat(older).Select(s => s.Profile).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        string Item(GatheredSlot s)
        {
            var item = s.MovedTo is { } to
                ? Tr.F("slot {0} from {1} as slot {2}", Number(s.Slot), Folder(s.From), Number(to))
                : Tr.F("slot {0} from {1}", Number(s.Slot), Folder(s.From));
            return profiles ? Tr.F("{0} (profile {1})", item, s.Profile) : item;
        }
        static string List(IEnumerable<string> items) => items.Aggregate((a, b) => Tr.F("{0}, {1}", a, b));
        var said = new List<string>
        {
            taken.Count > 0
                ? Tr.F("Every Longshot version now uses the same saves, and these were brought in: {0}. All of them, as they were before, are backed up in {1}.",
                       List(taken.Select(Item)), g.Backup)
                : Tr.F("Every Longshot version now uses the shared saves. All of them, as they were before, are backed up in {0}.", g.Backup),
        };
        if (left.Count > 0) said.Add(Tr.F("No slot was free for {0}, so it is only in that backup.", List(left.Select(Item))));
        if (older.Count > 0) said.Add(Tr.F("Older copies that differ from the slot kept are only in that backup: {0}.", List(older.Select(Item))));
        if (!g.WasShared) said.Add(Tr.F("The game's own options (option.sav) come from {0}, the save played last.", Folder(g.From)));
        return Tr.Sentences(said.ToArray());
    }

    /// A slot's number from its folder's name: slot_00 is 1
    public static int Number(string slotName) =>
        slotName.StartsWith("slot_", StringComparison.Ordinal) && int.TryParse(slotName[5..], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n + 1 : 0;
}
