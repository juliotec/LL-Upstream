# Saves

What the launcher does with your saves: where Longshot keeps them, copying them between builds, importing them from
emulators and from Hookshot, and exporting them. Everything is on the **Saves** page (the *Saves* link on the home, under
the default version). Every copy backs up the save it replaces first, and every copy is refused while Longshot, Hookshot or
an emulator is running, since a running game can write its save again and undo the copy.

The save files are the same in Longshot, Hookshot and the emulators and ports that run the game: `account.sav`,
`option.sav` and one `slot_NN` folder per slot holding `progress.sav` and `caption.sav`. Longshot's own saves guide,
[docs/saves.md in Longshot](https://git.magicbean.dev/MagicBeanDev/Longshot/src/branch/main/docs/saves.md), has the format,
where every emulator keeps its saves, and `scripts/oot_saves.py import|export`, the command line that does what this page
does (for Lichii, Eden, NX2navi and the rest) without the launcher.

## Where Longshot keeps them

| | Where |
|---|---|
| Linux and Steam Deck | `$XDG_DATA_HOME/longshot/save/<user id>/` (default `~/.local/share/longshot/save/`): every build shares it |
| Windows | `<build folder>\user\save\<user id>\`: each build keeps its own, and the Saves page copies them between builds |

Backups go to `%LOCALAPPDATA%\Longshot\oot-save-backups` on Windows and `$XDG_DATA_HOME/oot-save-backups` on Linux, one
folder per copy, named by the time it was made.

## Between builds (Windows)

**Copy saves from another build** copies another build's save into the one the page is on. A new build or an update can
also take its saves from your default build by itself (*Move my saves*, in the build wizard's second step). On Linux every build already
uses the same saves, so the button is not shown.

## Importing

### From an emulator

**Import from emulator** lists the emulators whose saves were found on this PC (Lichii, Eden, Eglantine, Orchid,
PokeDinner, Frizti, NX2navi), with their slots and when they were saved, and copies the one you pick into the build. A
save somewhere else (a portable install, a copy on another drive) is picked with *Choose a folder*. The build must have
been started and saved once, so that its own save folder exists.

### From Hookshot

[Hookshot](https://github.com/NotRexed/Hookshot) is the project Longshot was forked from. Its game writes the same save
files in the same layout, so a Hookshot save carries on in Longshot. Apart from its saves, the launcher reads nothing of a
Hookshot install: not its settings, folders, channels, versions or updates. `Core/HookshotSaves.cs` does the import.

It only happens when you ask. **Saves > Import from Hookshot…** opens it, and the home offers it once when Hookshot saves
are found (**Import…** or **Not now**; either answer stops the offer).

**Where it looks.** These places come from Hookshot's source (its `host/nn_fs.c` save root, `scripts/oot_saves.py`, the
2.x Builders' default game folder and its 3.0 launcher's builds folder):

| | Where |
|---|---|
| Linux | `$XDG_DATA_HOME/hookshot/save/<user id>/` (default `~/.local/share/hookshot`): every Hookshot build shares it |
| Windows | `<game folder>\user\save\<user id>\`. The game folder is `%USERPROFILE%\Games\Hookshot` by default, and the version folders Hookshot's launcher made under it are searched too |

For any other place (a portable install, `--user <folder>`, a copy on another drive), use **Choose a folder…** and pick
either the save folder itself or a folder that holds one, such as a Hookshot game folder.

**What is checked.** Before anything is copied, the folder must hold only save files:

- only `account.sav`, `option.sav` and `slot_NN` folders, with nothing else in them;
- no links;
- no empty file, and no file larger than 4 MB;
- at least one slot, with a `progress.sav` in each.

The window lists each save it found, with its slots and when they were last saved. If a save cannot be imported, it says
why.

**What the copy does.**

- **It copies; Hookshot keeps its saves as they were.** Nothing in Hookshot's folders is moved, changed or deleted.
- **Where it goes:** Longshot's save of the same profile when there is one; else Longshot's only profile; else a new one
  with Hookshot's user id.
  - On Linux, in `$XDG_DATA_HOME/longshot/save/`.
  - On Windows, in the build's `user\save\`: the build the Saves page is on, or the default build.
- **It never replaces a Longshot save without asking.** When Longshot already has a save there, the window asks first. If
  you say yes, Longshot's save is copied whole to the backup folder before anything is replaced, and the message says
  where the backup is.
- **Nothing is converted.** The save files name no path, folder or program, and Longshot reads Hookshot's format as it
  is, so the files are copied byte for byte.

## Exporting

**Export to emulator** copies the build's save into an emulator's save folder (the same emulators as the import), or into
any folder you pick with *Choose a folder*. The emulator's save is backed up first, as for every copy. The launcher does
not export to Hookshot.

To keep a copy of your saves yourself, copy the save folder from the table at the top of this page. Copy it while the game
is closed.

## Tests

The launcher's tests use no real save and nothing from the game:

- `tests/launcher_core/HookshotChecks.cs` runs on fake save folders in `tests/fixtures/hookshot-saves`, made of one-line
  text files.
- The save checks in `tests/launcher_core` follow the cases of Longshot's `tests/test_oot_saves.py`.
