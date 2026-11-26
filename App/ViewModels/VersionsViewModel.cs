namespace LongshotLauncher.ViewModels;

/// The Versions page: every version on this PC as a card (its state, size, when it was built, and what is done to it: set as
/// default, its folder, update, build again, delete), building another (a track and a version: VersionPick), adding
/// a folder built already, and the update check. Its list and its actions are the home's (HomeViewModel: the one model of the
/// versions, which Home's picker reads too), shown here; Home only plays
public sealed class VersionsViewModel : ViewModelBase
{
    public VersionsViewModel(MainViewModel main)
    {
        Main = main;
        Home = main.Home;
        Home.Refresh();
    }

    public MainViewModel Main { get; }
    public HomeViewModel Home { get; }
}
