using Avalonia.Media;
using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

/// A profile as a card (the Profiles page, Who's playing?) or a row of the profile chip's menu: its avatar (colour and letter),
/// its name, and what it holds at a glance: its saves, its game copy and upscalers
public sealed class ProfileCardViewModel
{
    public ProfileCardViewModel(MainViewModel main, LauncherProfile p, ProfilesViewModel? page = null)
    {
        Main = main;
        Profile = p;
        var service = main.Service;
        IsCurrent = p.Id == service.Profile.Id;
        var slots = Saves.Describe(LauncherProfiles.SlotsFolder(service.SaveEnv, p.Id)).Where(s => s.Saved != null).ToList();
        SavesLine = slots.Count == 0 ? Tr.T("No save yet")
            : Tr.P(slots.Count, "1 save, last {1}", "{0} saves, last {1}", Durations.When(slots.Max(s => s.Saved!.Value), DateTime.Now));
        // (its profile.json's game copy, by its last part in either separator: a copy's path as the PC that wrote it spells it)
        var file = LauncherProfiles.FolderOf(service.SaveEnv, p.Id) is { } dir ? ProfileStore.Load(dir) : null;
        var game = file?.Game is { } g ? g.TrimEnd('/', '\\').Split('/', '\\')[^1] : Tr.T("no game copy yet");
        var ups = file == null || file.Upscalers.Count == 0 ? Tr.T("no upscaler")
            : string.Join(", ", file.Upscalers.Select(u => u switch { "dlss" => "DLSS", "fsr4" => "FSR 4", "xess" => "XeSS", _ => u }));
        GameLine = p.Problem is { } why ? Tr.F("Damaged: {0}", why) : Tr.F("Game: {0} · {1}", game, ups);
        PickCommand = new Command(() => main.SwitchProfile(p.Id, home: true), () => !p.Damaged);
        if (page != null)
        {
            // (a damaged profile.json is not changed here: the card offers Delete, and its folder holds the file to fix)
            RenameCommand = new Command(() => page.Edit(ProfileEdit.Rename, p), () => !p.Damaged);
            DuplicateCommand = new Command(() => page.Edit(ProfileEdit.Duplicate, p), () => !p.Damaged);
            ColourCommand = new Command(() => page.NextColour(p), () => !p.Damaged);
            DeleteCommand = new Command(() => page.AskDelete(p), () => p.Id != LauncherProfiles.DefaultId);
        }
    }

    public MainViewModel Main { get; }
    public LauncherProfile Profile { get; }
    public string Name => LauncherProfilesText.Name(Profile);
    public string Initial => LauncherProfilesText.Initial(Profile);
    public IBrush Colour => Brush.Parse(Profile.Color);
    public bool IsCurrent { get; }
    public bool IsOther => !IsCurrent;
    public string SavesLine { get; }
    public string GameLine { get; }
    public Command PickCommand { get; }
    public Command? RenameCommand { get; }
    public Command? DuplicateCommand { get; }
    public Command? ColourCommand { get; }
    public Command? DeleteCommand { get; }
    public bool CanDelete => Profile.Id != LauncherProfiles.DefaultId;
}

public enum ProfileEdit { None, New, Rename, Duplicate }

/// The Profiles page: a card per profile (its avatar, name, saves and game at a glance), New, Rename, Colour, Copy, Delete
public sealed class ProfilesViewModel : ViewModelBase
{
    ProfileEdit editing;
    LauncherProfile? editingOf;
    string name = "";
    string? problem;
    LauncherProfile? deleting;

    public ProfilesViewModel(MainViewModel main, bool create = false)
    {
        Main = main;
        NewCommand = new Command(() => Edit(ProfileEdit.New, null));
        SaveCommand = new Command(SaveEdit);
        CancelCommand = new Command(() => Edit(ProfileEdit.None, null));
        ConfirmDeleteCommand = new Command(Delete);
        KeepCommand = new Command(() => Deleting = null);
        Reload();
        if (create) Edit(ProfileEdit.New, null);
    }

    public MainViewModel Main { get; }
    public IReadOnlyList<ProfileCardViewModel> Cards { get; private set; } = Array.Empty<ProfileCardViewModel>();
    public Command NewCommand { get; }
    public Command SaveCommand { get; }
    public Command CancelCommand { get; }
    public Command ConfirmDeleteCommand { get; }
    public Command KeepCommand { get; }

    public void Reload()
    {
        Cards = Main.Service.Profiles.Select(p => new ProfileCardViewModel(Main, p, this)).ToList();
        Raise(nameof(Cards));
        Main.RaiseProfile();
    }

    // ---- New, Rename, Copy: a name in a line under the header (Save or Cancel)

    public bool Editing => editing != ProfileEdit.None;
    public string EditTitle => editing switch
    {
        ProfileEdit.New => Tr.T("New profile: its own saves and settings"),
        ProfileEdit.Rename => Tr.F("Rename {0}", editingOf is { } p ? LauncherProfilesText.Name(p) : ""),
        ProfileEdit.Duplicate => Tr.F("Copy {0}: its saves and settings", editingOf is { } d ? LauncherProfilesText.Name(d) : ""),
        _ => "",
    };
    public string Name
    {
        get => name;
        set => Set(ref name, value ?? "");
    }
    public string? Problem
    {
        get => problem;
        private set { if (Set(ref problem, value)) Raise(nameof(HasProblem)); }
    }
    public bool HasProblem => problem != null;

    public void Edit(ProfileEdit kind, LauncherProfile? of)
    {
        editing = kind;
        editingOf = of;
        Name = kind switch
        {
            ProfileEdit.Rename when of != null => LauncherProfilesText.Name(of),
            ProfileEdit.Duplicate when of != null => Tr.F("{0} copy", LauncherProfilesText.Name(of)),
            _ => "",
        };
        Problem = null;
        RaiseAll(nameof(Editing), nameof(EditTitle));
    }

    void SaveEdit()
    {
        var env = Main.Service.SaveEnv;
        try
        {
            switch (editing)
            {
                case ProfileEdit.New:
                    var made = LauncherProfiles.Create(env, Name, knownGame: Main.Service.KnownGame());
                    Main.ShowToast(Tr.F("Made the profile {0}.", made.Name));
                    break;
                case ProfileEdit.Rename when editingOf != null:
                    LauncherProfiles.Rename(env, editingOf.Id, Name);
                    break;
                case ProfileEdit.Duplicate when editingOf != null:
                    var copy = LauncherProfiles.Duplicate(env, editingOf.Id, Name);
                    Main.ShowToast(Tr.F("Made the profile {0}, a copy.", copy.Name));
                    break;
            }
        }
        catch (Exception e) when (e is ProfileFailure or IOException or UnauthorizedAccessException)
        {
            Problem = e.Message;
            return;
        }
        Edit(ProfileEdit.None, null);
        Reload();
    }

    public void NextColour(LauncherProfile p)
    {
        var colours = LauncherProfiles.Colors;
        var i = colours.ToList().IndexOf(p.Color);
        try { LauncherProfiles.SetColor(Main.Service.SaveEnv, p.Id, colours[(i + 1) % colours.Count]); }
        catch (Exception e) when (e is ProfileFailure or IOException or UnauthorizedAccessException) { Problem = e.Message; }
        Reload();
    }

    // ---- Delete: asked first; its folder goes to the backups, never thrown away

    LauncherProfile? Deleting
    {
        get => deleting;
        set { if (Set(ref deleting, value)) RaiseAll(nameof(HasDeleting), nameof(DeletingTitle)); }
    }
    public bool HasDeleting => deleting != null;
    public string DeletingTitle => deleting is { } p ? Tr.F("Delete the profile {0}?", LauncherProfilesText.Name(p)) : "";
    public string DeletingQuestion => Tr.T("Its saves and settings are kept in the launcher's backups (Saves, Restore from a backup).");

    public void AskDelete(LauncherProfile p) => Deleting = p;

    void Delete()
    {
        if (deleting is not { } p) return;
        Deleting = null;
        try
        {
            var wasCurrent = p.Id == Main.Service.Profile.Id;
            LauncherProfiles.Delete(Main.Service.Settings, Main.Service.SaveEnv, p.Id);
            if (wasCurrent) Main.Service.SwitchProfile(LauncherProfiles.DefaultId);
            Main.Service.SaveSettings();
            Main.ShowToast(Tr.F("Deleted the profile {0}; its saves are in the backups.", LauncherProfilesText.Name(p)));
        }
        catch (Exception e) when (e is ProfileFailure or IOException or UnauthorizedAccessException) { Problem = e.Message; }
        Reload();
    }
}

/// Who's playing?: the launcher's first page with two or more profiles, a big card per profile, and New
public sealed class WhoViewModel(MainViewModel main) : ViewModelBase
{
    public MainViewModel Main => main;
    public IReadOnlyList<ProfileCardViewModel> Cards { get; } = main.Service.Profiles.Select(p => new ProfileCardViewModel(main, p)).ToList();
    public Command NewCommand { get; } = new(() => main.ShowProfiles(create: true));
}
