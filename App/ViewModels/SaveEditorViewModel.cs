using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

/// A category in the page's list: its name, how many the slot has of how many, and a bar
public sealed class SaveCategoryRow : ViewModelBase
{
    bool selected;

    public SaveCategoryRow(SaveEditorCategory category, Action<SaveCategoryRow> pick)
    {
        Category = category;
        PickCommand = new Command(() => pick(this));
    }

    public SaveEditorCategory Category { get; }
    public string Title => Category.Title;
    public string Count => Category.Of > 0 ? Tr.F("{0} of {1}", Category.Have, Category.Of) : "";
    public double Fraction => Category.Fraction;
    public bool Done => Category.Complete && Category.Of > 0;
    public bool Open => !Done;
    public bool NotCounted => !Category.Counted;

    public bool Selected
    {
        get => selected;
        set => Set(ref selected, value);
    }

    public Command PickCommand { get; }
}

/// An area of the selected category and what the slot has in it, what is missing there by name, and the edits the area takes
public sealed class SaveAreaRow
{
    public SaveAreaRow(SaveEditorArea area, IReadOnlyList<SaveMissingRow> entries, Command set, Command clear)
    {
        Name = SaveEditorText.Humanize(area.Area);
        Count = Tr.F("{0} of {1}", area.Have, area.Of);
        Done = area.Complete;
        HasAny = area.Have > 0;
        Entries = entries;
        SetCommand = set;
        ClearCommand = clear;
    }

    public string Name { get; }
    public string Count { get; }
    public bool Done { get; }
    public bool Open => !Done;
    /// The slot has something here to take away
    public bool HasAny { get; }
    public IReadOnlyList<SaveMissingRow> Entries { get; }
    public Command SetCommand { get; }
    public Command ClearCommand { get; }
}

/// One thing the slot has not got, by name and area, with what the report adds to it ("0 of 20", "in hand: 0")
public sealed class SaveMissingRow
{
    public SaveMissingRow(SaveEditorEntry entry, Command set)
    {
        Name = entry.Label;
        Where = entry.Area.Length > 0 ? SaveEditorText.Humanize(entry.Area) : "";
        // (a note that only says again what the name says, such as a place in both, is left out)
        Note = entry.Note is { Length: > 0 } note && !entry.Label.Contains(note, StringComparison.Ordinal) ? note : "";
        Count = entry.Of > 1 ? Tr.F("{0} of {1}", entry.Have, entry.Of) : "";
        SetCommand = set;
    }

    public string Name { get; }
    public string Where { get; }
    public bool HasWhere => Where.Length > 0;
    public string Note { get; }
    public bool HasNote => Note.Length > 0;
    public string Count { get; }
    public bool HasCount => Count.Length > 0;
    public Command SetCommand { get; }
}

/// A line of text in a list
public sealed record SaveLine(string Text);

/// A slot's progress, by the save editor (Core/SaveEditor.cs: Longshot's own scripts/longshot_save_editor.py in the build's tools):
/// every category with how many the slot has, and what is missing by name and area; and the edits, each confirmed first (what the
/// editor would change, where the backup goes) and refused while a game runs. The page reads the files as they are: after every
/// edit it reads them again. Opened from a slot of the Saves page
public sealed class SaveEditorViewModel : ViewModelBase
{
    readonly SavesViewModel saves;
    CancellationTokenSource? reading;
    bool loading = true;
    string? problem;
    SaveEditorSlot? data;
    IReadOnlyList<SaveCategoryRow> categories = Array.Empty<SaveCategoryRow>();
    SaveCategoryRow? selected;

    public SaveEditorViewModel(SavesViewModel saves, SaveEditing editing, string slot)
    {
        this.saves = saves;
        Editing = editing;
        Slot = slot;
        BackCommand = new Command(() =>
        {
            reading?.Cancel();
            saves.Reload();
            Main.Navigate(saves);
        });
        ReloadCommand = new Command(() => _ = LoadAsync(), () => !loading);
        GiveCommand = new Command(() => Main.Modal = new SaveEditAskViewModel(this, SaveAsk.Give), () => CanEdit);
        StartCommand = new Command(() => Main.Modal = new SaveEditAskViewModel(this, SaveAsk.Start), () => CanEdit);
        CopyCommand = new Command(() => Main.Modal = new SaveEditAskViewModel(this, SaveAsk.Copy), () => CanEdit);
        SetCategoryCommand = new Command(() => Confirm(SaveEditPlan.Set(Slot, selected!.Category.Id, Tr.F("Set everything in {0}: done, owned or found.", selected.Title))),
                                         () => CanEdit && selected != null);
        ClearCategoryCommand = new Command(() => Confirm(SaveEditPlan.Clear(Slot, selected!.Category.Id, Tr.F("Take away everything in {0}.", selected.Title))),
                                           () => CanEdit && selected != null);
        ReportCommand = Main.CopyReportCommand(() => Tr.F("The save editor could not read {0}: {1}", Slot, problem ?? ""),
                                               () => [$"save editor: {Slot} of {Editing.Save}"]);
        _ = LoadAsync();
    }

    public MainViewModel Main => saves.Main;
    public SaveEditing Editing { get; }
    public string Slot { get; }
    public SaveEditorSlot? Data => data;

    public string Title => Tr.F("Slot {0}: progress", SaveText.Number(Slot));

    /// Whose game files the editor reads the lists from: the build's own version
    public string Via => GameFolder.Version(Editing.Game) is { } version ? Tr.F("From the game files of {0}", Versions.Shown(version)) : "";
    public bool HasVia => Via.Length > 0;
    public string Sub => Tr.T("What this slot has and is missing, from your own game files. Edits back up the whole save first.");

    public bool Loading
    {
        get => loading;
        private set
        {
            if (!Set(ref loading, value)) return;
            RaiseAll(nameof(Showing), nameof(HasProblem), nameof(CanEdit));
            ReloadCommand.Changed();
            ChangedAll();
        }
    }

    public string? Problem
    {
        get => problem;
        private set
        {
            if (!Set(ref problem, value)) return;
            RaiseAll(nameof(HasProblem), nameof(Showing), nameof(CanEdit));
            ChangedAll();
        }
    }

    public bool HasProblem => problem != null && !loading;
    public bool Showing => data != null && problem == null;
    public bool NotShowing => !Showing;

    /// Edits only on a slot the report could read, of this game version, while nothing is being read
    public bool CanEdit => !loading && problem == null && data is { Problem: null };

    public string LoadingText => Tr.T("Reading your game's files to count what this slot has…");

    // ---- the slot's line ----

    /// "Child Link in Test Area"
    public string Where => data == null ? "" : data.Place is { Length: > 0 } place
        ? Tr.F("{0} in {1}", Link(data.Age), place) : Link(data.Age);

    static string Link(string? age) => age switch { "child" => Tr.T("Child Link"), "adult" => Tr.T("Adult Link"), _ => Tr.T("Link") };

    /// "5 hearts · 500 rupees · day 3 10:59 · saved 2026-11-09 20:08"
    public string Facts
    {
        get
        {
            if (data == null) return "";
            var facts = new List<string>
            {
                Tr.P((long)Math.Ceiling(data.Hearts), "1 heart", "{0} hearts"),
                Tr.P(data.Rupees, "1 rupee", "{0} rupees"),
            };
            if (data.Clock != null) facts.Add(Tr.F("game clock {0}", data.Clock));
            if (data.Saved != null) facts.Add(Tr.F("saved {0}", data.Saved));
            if (data.Cleared) facts.Add(Tr.T("game cleared"));
            return string.Join("  ·  ", facts);
        }
    }

    public bool Complete => data is { Complete: true };
    public bool Incomplete => data != null && !data.Complete;

    /// The counted categories together: what the slot has of what the game has, and the percentage the tool calls 100% only when
    /// nothing is missing
    public int Percent => data == null ? 0 : SaveEditorText.Percent(data.Categories.Where(c => c.Counted).Sum(c => c.Have), data.Categories.Where(c => c.Counted).Sum(c => c.Of));

    public double Overall => Percent / 100.0;

    public string Verdict => data == null ? ""
        : data.Complete ? Tr.T("100%: nothing is missing") : Tr.F("{0}%: {1} missing", Percent, data.MissingTotal);

    public string? Warning => data?.Problem is { } why ? Tr.F("{0}. Edits are off for it: use the game version that wrote it.", why) : null;
    public bool HasWarning => data?.Problem != null;

    // ---- the categories and the selected one ----

    public IReadOnlyList<SaveCategoryRow> Categories => categories;
    public int Listed => categories.Count;

    public SaveCategoryRow? Selected
    {
        get => selected;
        private set
        {
            if (selected != null) selected.Selected = false;
            if (!Set(ref selected, value)) return;
            if (value != null) value.Selected = true;
            RaiseDetail();
        }
    }

    void RaiseDetail()
    {
        var category = selected?.Category;
        rowCommands.Clear();
        // (each edit button of the rows is off while the page reads, and on a slot it cannot edit: CanEdit)
        Command Edit(Action run)
        {
            var command = new Command(run, () => CanEdit);
            rowCommands.Add(command);
            return command;
        }
        SaveMissingRow Row(SaveEditorEntry m) => new(m, Edit(() => Confirm(SaveEditPlan.Set(Slot, m.Id, Tr.F("Set {0}: done, owned or found.", m.Label)))));
        Areas = category == null ? Array.Empty<SaveAreaRow>()
            : category.Areas.Select(a => new SaveAreaRow(a, category.Missing.Where(m => m.Area == a.Area).Select(Row).ToList(),
                  Edit(() => Confirm(SaveEditPlan.Set(Slot, $"{category.Id}/{a.Area}", Tr.F("Set everything in {0}, {1}: done, owned or found.", category.Title, SaveEditorText.Humanize(a.Area))))),
                  Edit(() => Confirm(SaveEditPlan.Clear(Slot, $"{category.Id}/{a.Area}", Tr.F("Take away everything in {0}, {1}.", category.Title, SaveEditorText.Humanize(a.Area))))))).ToList();
        Missing = category == null ? Array.Empty<SaveMissingRow>() : category.Missing.Where(m => m.Area.Length == 0).Select(Row).ToList();
        Unreadable = category == null ? Array.Empty<SaveLine>() : category.Unreadable.Select(u => new SaveLine(Tr.F("{0}: the file does not say", u.Label))).ToList();
        RaiseAll(nameof(DetailTitle), nameof(DetailCount), nameof(DetailNote), nameof(HasDetailNote), nameof(Areas), nameof(HasAreas), nameof(Missing), nameof(HasMissing),
                 nameof(Unreadable), nameof(HasUnreadable), nameof(DetailDone), nameof(HasDetail));
        ChangedAll();
    }

    public bool HasDetail => selected != null;
    public string DetailTitle => selected?.Title ?? "";
    public string DetailCount => selected == null ? "" : selected.Category.Of > 0 ? Tr.F("{0} of {1}", selected.Category.Have, selected.Category.Of) : "";
    public string DetailNote => selected?.Category.Note ?? "";
    public bool HasDetailNote => DetailNote.Length > 0;
    public bool DetailDone => selected is { Done: true };

    public IReadOnlyList<SaveAreaRow> Areas { get; private set; } = Array.Empty<SaveAreaRow>();
    public bool HasAreas => Areas.Count > 0;

    /// What is missing in a category with no areas (items, quests...), by name
    public IReadOnlyList<SaveMissingRow> Missing { get; private set; } = Array.Empty<SaveMissingRow>();
    public bool HasMissing => Missing.Count > 0;

    /// What the file does not say (an entry the save has no flag for: another game version's)
    public IReadOnlyList<SaveLine> Unreadable { get; private set; } = Array.Empty<SaveLine>();

    public bool HasUnreadable => Unreadable.Count > 0;

    public Command BackCommand { get; }
    public Command ReloadCommand { get; }
    public Command GiveCommand { get; }
    public Command StartCommand { get; }
    public Command CopyCommand { get; }
    public Command SetCategoryCommand { get; }
    public Command ClearCategoryCommand { get; }
    /// Copy for a bug report, under a problem
    public Command ReportCommand { get; }

    readonly List<Command> rowCommands = new();

    void ChangedAll()
    {
        foreach (var c in new[] { GiveCommand, StartCommand, CopyCommand, SetCategoryCommand, ClearCategoryCommand }.Concat(rowCommands)) c?.Changed();
    }

    /// An edit, asked first: what the editor would change, where the backup goes
    void Confirm(SaveEditPlan plan) => Main.Modal = new SaveEditConfirmViewModel(this, plan, null);

    /// The slot's report, read from the files as they are now
    public async Task LoadAsync()
    {
        reading?.Cancel();
        var mine = reading = new CancellationTokenSource();
        Loading = true;
        Problem = null;
        try
        {
            var report = await Editing.ReportAsync(Slot, mine.Token);
            if (mine.IsCancellationRequested) return;
            var slot = report.Slots.FirstOrDefault(s => s.Slot == Slot) ?? throw new SaveFailure(Tr.F("The save editor's report has no {0}.", Slot));
            if (slot.Unreadable != null) throw new SaveFailure(Tr.F("{0} cannot be read: {1}", Tr.F("Slot {0}", slot.Number), slot.Unreadable));
            Fill(slot);
        }
        catch (OperationCanceledException) when (mine.IsCancellationRequested) { return; }
        catch (Exception e) when (e is SaveFailure or IOException)
        {
            data = null;
            LauncherLog.Add($"save editor: {Slot}: {e.Message}");
            Problem = e.Message;
        }
        finally
        {
            if (reading == mine) Loading = false;
        }
    }

    void Fill(SaveEditorSlot slot)
    {
        var keep = selected?.Category.Id;
        data = slot;
        categories = slot.Listed.Select(c => new SaveCategoryRow(c, row => Selected = row)).ToList();
        RaiseAll(nameof(Categories), nameof(Listed), nameof(Where), nameof(Facts), nameof(Complete), nameof(Incomplete), nameof(Percent), nameof(Overall), nameof(Verdict),
                 nameof(Warning), nameof(HasWarning), nameof(Showing), nameof(NotShowing), nameof(CanEdit));
        // (the category the player was in, else the first one with something missing)
        selected = null;
        Selected = categories.FirstOrDefault(c => c.Category.Id == keep) ?? categories.FirstOrDefault(c => c.Category is { Counted: true, Complete: false }) ?? categories.FirstOrDefault();
        RaiseDetail();
    }

    /// An edit that was written: the page reads the files again and says where the backup is
    public void Edited(SaveEditResult result)
    {
        Main.Modal = null;
        _ = LoadAsync();
        Main.ShowToast(result.Backup != null ? Tr.F("Done. The save was backed up first, in {0}", ShownPath.Of(result.Backup)) : Tr.T("Nothing needed changing."));
    }

    /// The slots of the Saves page, by their numbers (slot_00 is 1)
    public IReadOnlyList<int> SlotNumbers => saves.Slots.Select(s => s.Number).ToList();
}
