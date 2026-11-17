using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

public enum SaveAsk { Give, Start, Copy }

/// One choice of a picker: what it is called, and the key the edit takes
public sealed record SaveChoice(string Key, string Label);

/// An item the editor gives, with the name of its category as the report says it ("Quest items")
public sealed record SaveItemChoice(SaveEditorItem Item, string Category)
{
    public string Label => Item.Label;
}

/// What an edit that needs more than a click asks first: Give (an item, hearts, rupees or a dungeon's small keys), Start (where Link
/// begins) or Copy (this slot to another). Review makes the edit's plan and opens its confirmation (SaveEditConfirmViewModel)
public sealed class SaveEditAskViewModel : ViewModelBase
{
    bool loading;
    string? problem;
    SaveChoice giveKind;
    SaveItemChoice? item;
    SaveEditorGives? gives;
    SaveChoice? dungeon;
    SaveEditorPlace? place;
    SaveChoice age;
    SaveChoice? target;
    string amount = "";

    public SaveEditAskViewModel(SaveEditorViewModel page, SaveAsk kind)
    {
        Page = page;
        Kind = kind;
        GiveKinds = [new("item", Tr.T("An item")), new("hearts", Tr.T("Hearts")), new("rupees", Tr.T("Rupees")), new("keys", Tr.T("Small keys"))];
        giveKind = GiveKinds[0];
        Ages = [new("", Tr.T("The game's choice")), new("child", Tr.T("Child")), new("adult", Tr.T("Adult"))];
        age = Ages[0];
        Dungeons = Array.Empty<SaveChoice>();
        Targets = CopyTargets();
        target = Targets.FirstOrDefault();
        CancelCommand = new Command(() => Main.Modal = null);
        ReviewCommand = new Command(Review, () => !loading);
        if (kind != SaveAsk.Copy) _ = LoadAsync();
    }

    public SaveEditorViewModel Page { get; }
    public SaveAsk Kind { get; }
    MainViewModel Main => Page.Main;

    public bool IsGive => Kind == SaveAsk.Give;
    public bool IsStart => Kind == SaveAsk.Start;
    public bool IsCopy => Kind == SaveAsk.Copy;

    public string Title => Kind switch
    {
        SaveAsk.Give => Tr.T("Give Link something"),
        SaveAsk.Start => Tr.T("Where Link starts"),
        _ => Tr.F("Copy Slot {0}", SaveText.Number(Page.Slot)),
    };

    public string Sub => Kind switch
    {
        SaveAsk.Give => Tr.T("Items, hearts, rupees and small keys, within what the bags, the wallet and the game allow. Nothing is equipped for you."),
        SaveAsk.Start => Tr.T("Link wakes up there the next time the slot is loaded. The slot's picture on the Load screen keeps the old place."),
        _ => Tr.T("Copies this slot's files to another slot. A slot that holds a save is replaced, and the backup keeps it."),
    };

    public bool Loading
    {
        get => loading;
        private set
        {
            if (!Set(ref loading, value)) return;
            ReviewCommand.Changed();
        }
    }

    public string? Problem
    {
        get => problem;
        private set
        {
            if (Set(ref problem, value)) Raise(nameof(HasProblem));
        }
    }

    public bool HasProblem => problem != null;

    public Command CancelCommand { get; }
    public Command ReviewCommand { get; }

    // ---- Give ----

    public IReadOnlyList<SaveChoice> GiveKinds { get; }
    public IReadOnlyList<SaveItemChoice> Items { get; private set; } = Array.Empty<SaveItemChoice>();
    public IReadOnlyList<SaveChoice> Dungeons { get; private set; }

    public SaveChoice GiveKind
    {
        get => giveKind;
        set
        {
            if (value == null || !Set(ref giveKind, value)) return;
            Amount = "";
            Problem = null;
            RaiseAll(nameof(GiveItem), nameof(GiveHearts), nameof(GiveRupees), nameof(GiveKeys), nameof(NeedsAmount), nameof(AmountHint));
        }
    }

    public bool GiveItem => IsGive && giveKind.Key == "item";
    public bool GiveHearts => IsGive && giveKind.Key == "hearts";
    public bool GiveRupees => IsGive && giveKind.Key == "rupees";
    public bool GiveKeys => IsGive && giveKind.Key == "keys";

    public SaveItemChoice? SelectedItem
    {
        get => item;
        set
        {
            if (!Set(ref item, value)) return;
            RaiseAll(nameof(NeedsAmount), nameof(AmountHint));
        }
    }

    public SaveChoice? Dungeon
    {
        get => dungeon;
        set
        {
            if (Set(ref dungeon, value)) Raise(nameof(AmountHint));
        }
    }

    /// Hearts, rupees and small keys take a number, so does a counted item (bombs, arrows...)
    public bool NeedsAmount => GiveHearts || GiveRupees || GiveKeys || GiveItem && item is { Item.Counted: true };

    public string Amount
    {
        get => amount;
        set
        {
            if (Set(ref amount, value ?? "")) Problem = null;
        }
    }

    /// What the amount can be, from the report (the most hearts the game gives, the wallet Link has, the dungeon's keys)
    public string AmountHint
    {
        get
        {
            if (GiveHearts) return Tr.F("From {0} to {1} hearts.", gives?.HeartsMin ?? 1, gives?.HeartsMax ?? 0);
            if (GiveRupees)
                return Of("rupees") is > 0 and var wallet ? Tr.F("From 0 to {0}. Leave empty to fill the wallet.", wallet)
                    : Tr.T("Link has no wallet yet: give one first (Give, An item).");
            if (GiveKeys)
                return Tr.F("From 0 to {0}. Leave empty for all of them.", gives?.SmallKeys.FirstOrDefault(k => k.Dungeon == dungeon?.Key)?.Of ?? 0);
            return Tr.T("As many as the bags Link has hold. Leave empty to fill them.");
        }
    }

    /// How many the game has of a category in this slot's report: the wallet Link has
    int Of(string category) => Page.Data?.Categories.FirstOrDefault(c => c.Id == category)?.Of ?? 0;

    // ---- Start ----

    public IReadOnlyList<SaveEditorPlace> Places { get; private set; } = Array.Empty<SaveEditorPlace>();
    public IReadOnlyList<SaveChoice> Ages { get; }

    public SaveEditorPlace? SelectedPlace
    {
        get => place;
        set
        {
            if (Set(ref place, value)) Raise(nameof(ShowAge));
        }
    }

    /// Link's age is asked only for an area both ages have (the editor says which age an area is for, and refuses a wrong one)
    public bool ShowAge => IsStart && place is { Age: null };

    public SaveChoice Age
    {
        get => age;
        set
        {
            if (value != null) Set(ref age, value);
        }
    }

    // ---- Copy ----

    public IReadOnlyList<SaveChoice> Targets { get; }

    public SaveChoice? Target
    {
        get => target;
        set => Set(ref target, value);
    }

    /// The other slots of the Saves page, which this copy would replace, and the next free one
    IReadOnlyList<SaveChoice> CopyTargets()
    {
        var mine = SaveText.Number(Page.Slot);
        var numbers = Page.SlotNumbers;
        var choices = numbers.Where(n => n != mine).Select(n => new SaveChoice($"slot_{n - 1:00}", Tr.F("Slot {0}: replaced by this copy", n))).ToList();
        var next = (numbers.Count == 0 ? 0 : numbers.Max()) + 1;
        if (next <= 99) choices.Add(new SaveChoice($"slot_{next - 1:00}", Tr.F("Slot {0}: a new slot", next)));
        return choices.OrderBy(c => c.Key, StringComparer.Ordinal).ToList();
    }

    // ---- the lists the pickers show, from the editor ----

    async Task LoadAsync()
    {
        Loading = true;
        try
        {
            if (IsGive)
            {
                gives = await Page.Editing.ItemsAsync();
                var titles = (Page.Data?.Categories ?? []).ToDictionary(c => c.Id, c => c.Title);
                Items = gives.Items.Select(i => new SaveItemChoice(i, titles.GetValueOrDefault(i.Category) ?? SaveEditorText.Humanize(i.Category))).ToList();
                Dungeons = gives.SmallKeys.Select(k => new SaveChoice(k.Dungeon, $"{SaveEditorText.Humanize(k.Dungeon)} ({k.Of})")).ToList();
                item = Items.FirstOrDefault();
                dungeon = Dungeons.FirstOrDefault();
                RaiseAll(nameof(Items), nameof(SelectedItem), nameof(Dungeons), nameof(Dungeon), nameof(NeedsAmount), nameof(AmountHint));
            }
            else
            {
                Places = (await Page.Editing.PlacesAsync()).Where(p => p.CanLoad).ToList();
                place = Places.FirstOrDefault();
                RaiseAll(nameof(Places), nameof(SelectedPlace), nameof(ShowAge));
            }
        }
        catch (Exception e) when (e is SaveFailure or IOException)
        {
            Problem = e.Message;
        }
        finally
        {
            Loading = false;
        }
    }

    void Review()
    {
        try
        {
            var plan = Kind switch { SaveAsk.Give => GivePlan(), SaveAsk.Start => StartPlan(), _ => CopyPlan() };
            Main.Modal = new SaveEditConfirmViewModel(Page, plan, this);
        }
        catch (FormatException e)
        {
            Problem = e.Message;
        }
    }

    /// The number typed: empty is null (the tool's own "all"), else whole and not negative
    int? Number()
    {
        var text = amount.Trim();
        if (text.Length == 0) return null;
        if (!text.All(char.IsAsciiDigit) || text.Length > 6) throw new FormatException(Tr.T("Type a whole number, or leave it empty."));
        return int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }

    SaveEditPlan GivePlan()
    {
        var n = Number();
        switch (giveKind.Key)
        {
            case "hearts":
                return SaveEditPlan.Give(Page.Slot, [$"hearts={n ?? throw new FormatException(Tr.T("Type how many hearts."))}"], Tr.F("Give Link {0} hearts in all.", n));
            case "rupees":
                return SaveEditPlan.Give(Page.Slot, [n is { } r ? $"rupees={r}" : "rupees"], n is { } r2 ? Tr.F("Give Link {0} rupees.", r2) : Tr.T("Fill Link's wallet."));
            case "keys":
                if (dungeon == null) throw new FormatException(Tr.T("This game has no small keys to give."));
                return SaveEditPlan.Give(Page.Slot, [n is { } k ? $"{dungeon.Key.ToLowerInvariant()}-keys={k}" : $"{dungeon.Key.ToLowerInvariant()}-keys"],
                                         n is { } k2 ? Tr.F("Put {0} small keys in Link's hand for {1}.", k2, SaveEditorText.Humanize(dungeon.Key)) : Tr.F("Give every small key of {0}.", SaveEditorText.Humanize(dungeon.Key)));
            default:
                if (item == null) throw new FormatException(Tr.T("Choose an item."));
                return SaveEditPlan.Give(Page.Slot, [item.Item.Counted && n is { } c ? $"{item.Item.Id}={c}" : item.Item.Id],
                                         item.Item.Counted && n is { } c2 ? Tr.F("Give Link {0}: {1}.", item.Label, c2) : Tr.F("Give Link {0}.", item.Label));
        }
    }

    SaveEditPlan StartPlan()
    {
        if (place == null) throw new FormatException(Tr.T("Choose a place."));
        return SaveEditPlan.Start(Page.Slot, place.Spec, place.Age == null && age.Key.Length > 0 ? age.Key : null, Tr.F("Put Link at {0}.", place.Name));
    }

    SaveEditPlan CopyPlan()
    {
        if (target == null) throw new FormatException(Tr.T("Choose a slot."));
        var to = SaveText.Number(target.Key);
        var exists = Page.SlotNumbers.Contains(to);
        return SaveEditPlan.Copy(Page.Slot, target.Key, exists,
                                 exists ? Tr.F("Copy Slot {0} over Slot {1}. What Slot {1} holds is replaced; the backup keeps it.", SaveText.Number(Page.Slot), to)
                                        : Tr.F("Copy Slot {0} to a new Slot {1}.", SaveText.Number(Page.Slot), to));
    }
}

/// An edit, checked before it is made: what the editor says it would change (--dry-run, which writes nothing), where the backup of
/// the whole save goes, and that the game must be closed. Apply makes the edit; a refusal (a game running, the editor's own) stays
/// here with its words, and nothing is changed
public sealed class SaveEditConfirmViewModel : ViewModelBase
{
    readonly SaveEditAskViewModel? back;
    bool previewing = true;
    bool working;
    string? problem;
    string? backup;
    SaveEditResult? preview;
    IReadOnlyList<string> busy = Array.Empty<string>();

    public SaveEditConfirmViewModel(SaveEditorViewModel page, SaveEditPlan plan, SaveEditAskViewModel? back)
    {
        Page = page;
        Plan = plan;
        this.back = back;
        BackCommand = new Command(() => Main.Modal = back, () => !working);
        CancelCommand = new Command(() => Main.Modal = null, () => !working);
        ApplyCommand = new Command(() => _ = ApplyAsync(), () => !previewing && !working && preview is { Changed: true } && Problem == null);
        _ = PreviewAsync();
    }

    public SaveEditorViewModel Page { get; }
    public SaveEditPlan Plan { get; }
    MainViewModel Main => Page.Main;

    public string Title => Tr.T("Check the edit");
    public string What => Plan.What;
    public string SlotLine => Tr.F("In Slot {0}", SaveText.Number(Plan.Slot));

    public bool Previewing
    {
        get => previewing;
        private set
        {
            if (!Set(ref previewing, value)) return;
            RaiseAll(nameof(HasLines), nameof(NothingToChange));
            Changed();
        }
    }

    public bool Working
    {
        get => working;
        private set
        {
            if (!Set(ref working, value)) return;
            Changed();
        }
    }

    public string? Problem
    {
        get => problem;
        private set
        {
            if (!Set(ref problem, value)) return;
            RaiseAll(nameof(HasProblem), nameof(NothingToChange));
            Changed();
        }
    }

    public bool HasProblem => problem != null;
    public string? BackupMade
    {
        get => backup;
        private set
        {
            if (Set(ref backup, value)) Raise(nameof(HasBackupMade));
        }
    }
    public bool HasBackupMade => backup != null;
    public string BackupMadeText => backup == null ? "" : Tr.F("Nothing was changed. The backup made first is in {0}.", ShownPath.Of(backup));

    /// What the editor said it would do, a line each
    public IReadOnlyList<SaveLine> Lines => (preview?.Said ?? Array.Empty<string>()).Select(l => new SaveLine(l)).ToList();
    public bool HasLines => !previewing && Lines.Count > 0;
    public bool NothingToChange => !previewing && problem == null && preview is { Changed: false };
    /// The files the edit would write, relative to the save folder
    public string FilesLine => preview is { Files.Count: > 0 } ? Tr.F("Writes {0}.", string.Join(", ", preview.Files)) : "";
    public bool HasFilesLine => FilesLine.Length > 0;
    public string PreviewingText => Tr.T("Asking the save editor what this would change…");

    public string BackupLine => Tr.F("The whole save is backed up first, in {0}.", ShownPath.Of(Page.Editing.BackupRoot));

    /// A game that runs now (the edit is refused while one does)
    public string CloseFirst => busy.Count > 0
        ? Tr.F("{0} is running. A running game writes its save again when it quits and would undo the edit. Close it first.", string.Join(Tr.T(" and "), busy))
        : Tr.T("Close the game first: a running game writes its save again when it quits and would undo the edit.");

    public bool IsBusy => busy.Count > 0;
    public bool NotBusy => busy.Count == 0;
    public bool HasBack => back != null;

    public Command BackCommand { get; }
    public Command CancelCommand { get; }
    public Command ApplyCommand { get; }

    void Changed()
    {
        BackCommand.Changed();
        CancelCommand.Changed();
        ApplyCommand.Changed();
    }

    async Task PreviewAsync()
    {
        try
        {
            busy = Page.Editing.Busy();
            RaiseAll(nameof(CloseFirst), nameof(IsBusy), nameof(NotBusy));
            preview = await Page.Editing.PreviewAsync(Plan);
            RaiseAll(nameof(Lines), nameof(FilesLine), nameof(HasFilesLine));
        }
        catch (Exception e) when (e is SaveFailure or IOException)
        {
            Problem = e.Message;
        }
        finally
        {
            Previewing = false;
        }
    }

    async Task ApplyAsync()
    {
        Working = true;
        Problem = null;
        try
        {
            var done = await Page.Editing.ApplyAsync(Plan);
            LauncherLog.Add($"save editor: {Plan.Kind} {Plan.Slot}: {string.Join("; ", done.Said)}");
            Page.Edited(done);
        }
        catch (SaveFailure e)
        {
            busy = e.Reason == SaveRefusal.Running && e.Who.Count > 0 ? e.Who : Page.Editing.Busy();
            RaiseAll(nameof(CloseFirst), nameof(IsBusy), nameof(NotBusy));
            BackupMade = e.Backup;
            // (a game that runs is said once, in its own box; the editor's own words, and the editor's refusal of a game it found, here)
            if (e.Reason != SaveRefusal.Running || busy.Count == 0) Problem = e.Message;
        }
        catch (IOException e)
        {
            Problem = e.Message;
        }
        finally
        {
            Working = false;
        }
    }
}
