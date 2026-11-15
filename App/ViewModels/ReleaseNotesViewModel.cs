using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

/// What's new, over Home: the notes of every version from the player's to the one offered, newest first (ReleaseNotes), from the
/// release list the update check read; with the release page, and the update itself
public sealed class ReleaseNotesViewModel : ViewModelBase
{
    /// One version's notes; its heading only when there are several
    public sealed class Version(ReleaseNotesViewModel owner, ReleaseNote note, bool several) : ViewModelBase
    {
        public string Heading => note.Published is { } when
            ? Versions.Shown(note.Tag) + " \u00b7 " + Durations.Day(when.LocalDateTime, DateTime.Now) : Versions.Shown(note.Tag);
        public bool ShowHeading => several;
        public IReadOnlyList<MdBlock> Blocks => note.Blocks;
        public bool Empty => note.Blocks.Count == 0;
        public Action<string> Open => owner.Open;
    }

    readonly Action? go;
    readonly string page;

    ReleaseNotesViewModel(MainViewModel main, string product, string? have, string offered, IReadOnlyList<ReleaseNote> notes, string goText, Action? go)
    {
        Main = main;
        this.go = go;
        page = notes[0].Page;
        Title = Tr.F("What's new in {0}", product + " " + Versions.Shown(offered));
        Sub = have != null && notes.Count > 1 ? Tr.F("Every version since yours ({0}), newest first.", Versions.Shown(have))
            : have != null ? Tr.F("You have {0}.", Versions.Shown(have)) : "";
        Notes = notes.Select(n => new Version(this, n, notes.Count > 1)).ToList();
        GoText = goText;
        CloseCommand = new Command(() => Main.Modal = null);
        PageCommand = new Command(() => Open(page));
        GoCommand = new Command(() =>
        {
            Main.Modal = null;
            this.go?.Invoke();
        }, () => this.go != null);
    }

    /// The notes from have to offered, over the window; the release page in the browser when the list has none (the check did not
    /// answer, or the version is not in it)
    public static void Show(MainViewModel main, Product product, string? have, string offered, IReadOnlyList<ReleaseNote> notes, string goText, Action? go)
    {
        if (notes.Count == 0) main.Host?.Open(ReleaseNotes.PageOf(product, offered));
        else main.Modal = new ReleaseNotesViewModel(main, product.Display, have, offered, notes, goText, go);
    }

    public MainViewModel Main { get; }
    public string Title { get; }
    public string Sub { get; }
    public bool HasSub => Sub.Length > 0;
    public IReadOnlyList<Version> Notes { get; }
    public string GoText { get; }
    public bool CanGo => go != null;
    public Command CloseCommand { get; }
    public Command PageCommand { get; }
    public Command GoCommand { get; }

    /// A link of the notes, in the browser: only a web page's (Markdown keeps no other)
    public void Open(string url)
    {
        if (Markdown.Web(url) != null) Main.Host?.Open(url);
    }
}
