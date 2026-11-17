# Saves

What the launcher does with your saves: where Longshot keeps them (one place, shared by every build), importing them from
emulators and from Hookshot, and exporting them. Everything is on the **Saves** page (the *Saves* link on the home, under
the default version). Every copy backs up the save it replaces first, and every copy is refused while Longshot, Hookshot or
an emulator is running, since a running game can write its save again and undo the copy.

The save files are the same in Longshot, Hookshot and the emulators and ports that run the game: `account.sav`,
`option.sav` and one `slot_NN` folder per slot holding `progress.sav` and `caption.sav`. Longshot's own saves guide,
[docs/saves.md in Longshot](https://git.magicbean.dev/MagicBeanDev/Longshot/src/branch/main/docs/saves.md), has the format,
where every emulator keeps its saves, and `scripts/longshot_saves.py import|export`, the command line that does what this page
does (for Lichii, Eden, NX2navi and the rest) without the launcher.

## Where Longshot keeps them

| | Where |
|---|---|
| Linux and Steam Deck | `$XDG_DATA_HOME/longshot/save/<user id>/` (default `~/.local/share/longshot/save/`) |
| Windows | `%APPDATA%\Longshot\save\<user id>\`; a portable launcher's in `LongshotLauncher-data\user\save\` beside it |

Every build uses the same saves, on both systems, so a new build or an update starts where you left off, and there is nothing
to copy between builds. The launcher starts every build with `--user <that folder>` (on Linux, not while the build's folder
has a `user/` of its own: the game moves an old one into the shared folder by itself, and keeps a link you made). On Linux the
game also finds that folder by itself; on Windows a build started on its own (`bin\longshot.exe` double-clicked) still keeps
its saves in its own folder's `user\`, and the launcher brings them in at its next start (below).

The game settings (`longshot_options.ini`: the Insert menu's choices and your controls) are in the same folder once a build
that keeps them there has run. Before Play, a build that has none of its own, such as a new build of another channel, gets the
newest settings of your other versions, its own Compile shaders choice kept.

Backups go to `%LOCALAPPDATA%\Longshot\oot-save-backups` on Windows and `$XDG_DATA_HOME/longshot/save-backup` on Linux (default
`~/.local/share/longshot/save-backup/`), one folder per copy, named by the time it was made; a portable launcher keeps its
own in `LongshotLauncher-data/save-backups/` beside it.

Where each kind of backup is:

| | Windows | Linux |
|---|---|---|
| The launcher's (every copy, the saves brought together, a version deleted, a restore) | `%LOCALAPPDATA%\Longshot\oot-save-backups` | `$XDG_DATA_HOME/longshot/save-backup` |
| The launcher's from before #80 | the same | `$XDG_DATA_HOME/oot-save-backups` |
| Longshot's `scripts/longshot_saves.py` | the same | `$XDG_DATA_HOME/oot-save-backups` |
| The game's own (Longshot#1428) | the same | `$XDG_DATA_HOME/oot-save-backups` |

On Windows that is one folder. On Linux it is two, and *Restore from a backup* lists both (issue #188): new backups keep
going to `longshot/save-backup`, nothing is moved from one to the other, and the old folder is only read.

## Saves from before (Windows)

Windows builds used to keep their saves each in its own game folder (`<build folder>\user\save\`), and the 2.x Builder's game
in `%USERPROFILE%\Games\Longshot\user\save\`. When the launcher opens, and before it starts a game, it brings those into the
shared folder by itself (`Saves.Gather`):

- **Where it looks:** every build in its list, every Longshot game folder in the builds folder (a version an update replaced
  is no longer listed, but its saves may still be there), and the 2.x Builder's game folder (not for a portable launcher).
- **They are merged slot by slot, never over a slot of the shared save.** The shared save's slots, and its `account.sav`
  and `option.sav`, stay as they are; with no shared save yet, the save played last (the newest `progress.sav` of all) is
  kept that way, and also gives `mouse_sens.txt` and `present.txt` beside `save\`. A slot (`slot_NN`, in each profile) the
  kept save lacks comes from the copy whose `progress.sav` was written last (a missing or empty one counts as the oldest; on
  a tie the first folder's). Every other copy that is different, byte for byte, from all the slots kept may be another
  adventure (a build started on its own may have begun a new game in that slot) or the same one played on: it goes to the
  first free slot of that profile, `slot_00` to `slot_05`, the ones the game shows, the newest first. With none free it
  stays in the backup only, and so does a copy older than the kept save's own different slot; the home names both. A copy
  the same as one already kept changes nothing. The merge is made beside the shared save and takes its place by two
  renames, so a failure changes nothing.
- **Every one is backed up first,** whole, in one folder: `oot-save-backups\gather-<yyyyMMdd-HHmmss>\<game folder name>`
  (`shared` for the one that was already shared). The home says once which slots came from which version's folder, and
  into which slot, which stayed in the backup only, and where the backup is.
- **Nothing is deleted.** Each old `save` folder is renamed `save.moved-<yyyyMMdd-HHmmss>` beside it, so the game no longer
  finds it there. A folder an update left behind (no longer listed) is then offered for deletion on the home, `save.moved-*`
  with it: the backup above keeps its save.
- **Never while Longshot runs:** the next start tries again. A failure changes nothing and says why.
- **Deleting a version takes its save in first.** Before a version's folder is deleted (*Delete* on its row), a save in its
  own `user\save` (a build started outside the launcher, from Explorer, the taskbar or Steam, saves there) is brought into
  the shared one as above, backed up. A save still there afterwards (the gather failed or could not rename it; or Linux,
  where nothing is gathered) is copied whole to the backups first, `delete-<yyyyMMdd-HHmmss>\<game folder name>`, and the copy
  checked file by file; the home says where it is, also when the delete then stops half-way (a file in use). A copy that
  cannot be made leaves the folder as it was, and the home says why; so does a folder the launcher does not delete, before
  its save is touched. Deleting an old version's folder from the home's offer does the same (`BuildRegistry.DeleteGame`,
  `DeleteFolder`).
- **One at a time.** The launcher's start, Play, Delete and a desktop shortcut's Play (a launcher of its own) each bring the
  saves together holding one lock, `.longshot-gather.lock` in Longshot's user folder; one that waits for it more than 10
  seconds changes nothing and says so (`Saves.GatherAlone`).

## Importing

### From an emulator

**Import from emulator** lists the emulators whose saves were found on this PC (Lichii, Eden, Eglantine, Orchid,
PokeDinner, Frizti, NX2navi), with their slots and when they were saved, and copies the one you pick into Longshot's. A
save somewhere else (a portable install, a copy on another drive) is picked with *Choose a folder*. You can import before
starting Longshot for the first time: the launcher creates the save folder for the player Longshot uses. The destination
is always that player's folder, the only one the game opens; another profile folder beside it (a stray one, or a copy
made by hand) is left alone.

The home also offers to import when it finds NX2navi saves. **Import…** opens the emulator picker with NX2navi selected;
pick a profile if there is more than one, then check the source and destination before confirming. **Not now** or a
successful import stops this offer; cancelling leaves it available. The Hookshot offer is remembered separately.
The original saves stay where they are.

NX2navi keeps saves in `<game folder>/profile/save/<account>/`. The launcher looks in
`%LOCALAPPDATA%\Programs\NX2navi\game` and `C:\Games\Ocarina` on Windows, and `~/Games/Ocarina` on Linux.
For portable installs, a custom game folder or an installation on another drive, use **Choose a folder**.

NX2navi writes a save to `profile/save-tx` first. A finished one waiting there (`save-tx/ready-<n>`) is the save it loads
at its next start, so a copy from or to NX2navi is refused until NX2navi has been started and quit once. The rest of
`save-tx` is not a save in waiting and is ignored: `work` (the working copy NX2navi leaves after a quit), `new` and `old`.
A completed import from NX2navi's menu waiting in `profile/save-import` also blocks the copy: that is what NX2navi will
load on its next start. An unfinished `save-import.part` is ignored.

A copy that worked says so: the page that follows names what was copied and where the backup went, and the Saves page
says it again, briefly, when you go back to it.

### From Hookshot

[Hookshot](https://github.com/NotRexed/Hookshot) is the project Longshot was forked from. Its game writes the same save
files in the same layout, so a Hookshot save carries on in Longshot. `Core/HookshotSaves.cs` does the save import.
Compatible game settings have their own import below; the launcher's settings, channels, versions, caches and updates
are not brought over.

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
- **Where it goes:** the save folder of Longshot's player (`5a3c9e1f0b7d24680000000000000001`), the only one the game
  opens, whether it is there yet or not. Another profile folder beside it is left alone.
  - In the shared folder above: `$XDG_DATA_HOME/longshot/save/` on Linux, `%APPDATA%\Longshot\save\` on Windows.
- **It never replaces a Longshot save without asking.** When Longshot already has a save there, the window asks first. If
  you say yes, Longshot's save is copied whole to the backup folder before anything is replaced, and the message says
  where the backup is.
- **Nothing is converted.** The save files name no path, folder or program, and Longshot reads Hookshot's format as it
  is, so the files are copied byte for byte.

### Hookshot's game settings

The save import already includes `option.sav`: the game's own options, such as camera and text speed. To also bring over
Hookshot's graphics, controls and game language, choose **Import graphics and controls…** in the Hookshot import window.
This is a separate copy, with its own confirmation and backup.

Choose Hookshot's **game folder** and an installed Longshot version. These settings go to the file that build reads, while saves
are shared. If no Longshot build is installed yet, import the saves now and return for these settings after building the
game. On Linux, Hookshot's shared save folder does not identify its game folder: choose the game folder yourself.

The settings of `hookshot_options.ini` whose format is compatible with Longshot are written into the
`longshot_options.ini` the build reads (the user folder's, once a build has kept its settings there; else the build's own):
each replaces the same setting's line, or is added at the end. Every other line of that file stays
as it was, such as Compile shaders (`compile_shaders`), V-Sync, the memory budgets, Auto (this PC)'s choices and the
ocarina note bindings. Keys Longshot does not know, and values outside the range of Longshot's menu, are skipped and
listed (Longshot would clamp such a value on load anyway); when a key appears more than once, its last line decides, as in
the game. The file keeps Hookshot's `options_version` (none, when Hookshot's file has none), so Longshot updates Hookshot's
old settings as it would Hookshot's own file. A few of the build's own lines that Longshot reads by that version (Game
speed, Descriptor reuse, Menu shortcut, Aspect ratio, and the Y and ZL keys) are removed when the versions differ for them:
those take Longshot's default. An empty `longshot_options.ini` is filled without asking. `language.txt` is copied when present and recognized; when the
launcher has a game language set (Settings), it becomes the imported one too, since each update writes the launcher's
language into `language.txt`. Existing destination files are backed up whole before they change. The original Hookshot files are never changed. A failed write restores the
destination files; if restoration also fails, the error gives the backup location or identifies the incomplete new files.
The copy is refused while a game or emulator is running, or when a settings file itself is a link. A game or backup
folder reached through a link (a symlinked `~/Games`, a folder in OneDrive) is fine: the paths are compared once resolved.

This does not import shader caches, `env.txt`, launcher or builder preferences, or the legacy `present.txt` and
`mouse_sens.txt` files. It does not change how other installed Longshot builds are configured.

## Exporting

**Export to emulator** copies the build's save into an emulator's save folder (the same emulators as the import), into
any folder you pick with *Choose a folder* (a folder with no save in it is made into the save folder, and the other files
in it stay), or into a .zip file (below). A save it replaces is backed up first, as for every copy. The launcher does not
export to Hookshot.

To keep a copy of your saves yourself, export the save folder as a .zip (below), or copy the save folder from the table at the top of
this page while the game is closed.

## As a .zip file

**Export saves… > A .zip file…** zips Longshot's save folder whole, as it is on the disk, into one file to keep or to send:
the Save As dialog picks where, `Longshot saves <date>.zip` to start with (`Core/SaveArchive.cs`, `ExportSave`).

- **What is in it:** the `save` folder as the zip's top folder, and everything in it: `save/<user id>/account.sav`,
  `option.sav`, every `slot_NN/` with whatever it holds, any other file or folder there (nested and empty ones too), and
  every other profile folder beside Longshot's. Each file keeps its time, so every slot still says when it was saved.
- **What is not:** nothing from beside the save folder in Longshot's user folder: not `longshot_options.ini`,
  `mouse_sens.txt` or `present.txt` (the settings are separate). A link or junction in the save folder is left out, never
  followed, and the result page names it.
- **Nothing is added among the files.** What made the zip (the launcher's version), when, and its slots are the zip's
  comment, which any zip tool shows.

It is written beside where it goes and renamed into place, so a failure leaves what was there. It is refused while Longshot,
Hookshot or an emulator runs, into the save folder itself, and for a folder above the caps below (so every zip it writes
can be imported again).

**From an emulator… > A .zip file…** imports one. Which rule applies depends on what the zip holds:

- **A whole save folder** (what Export writes: every entry under `save/`, and `save/5a3c9e1f0b7d24680000000000000001/`, the
  profile the game opens, holding a slot with a `progress.sav`). It is taken as it is: any file, folder or profile in it.
  Only what makes it safe is checked: no name that leads out of the zip (`..`, `/`, a drive, backslashes included), no link,
  no file in it twice, and the caps. It takes the place of Longshot's whole save folder: the confirmation says so, Longshot's
  is backed up whole first (`import-zip-<yyyyMMdd-HHmmss>`), the new one is made beside it and swapped in by two renames, so
  a failure leaves it as it was. *Keep Longshot's game settings* keeps each of its profiles' `option.sav`.
- **Anything else**: a zip made by hand of a save folder or of the folder above it, Hookshot's or an emulator's save folder
  zipped (another profile id than Longshot's), a slot's zip, or the launcher's first zips. Only save files are taken, checked as
  a Hookshot save is, and refused, saying why, otherwise: only `account.sav`, `option.sav`, `slot_NN/progress.sav` and
  `slot_NN/caption.sav` (the folders every save file sits in are skipped, and a file manager's leftovers, `__MACOSX`,
  `.DS_Store`, `Thumbs.db`, `desktop.ini`, left out); no empty file, none above 4 MB; at least one slot, a `progress.sav` in
  each; and the same safety checks. They are copied into Longshot's profile as an import from a folder is: its slots,
  `account.sav` and (unless kept) `option.sav` replaced, its other files left.

**The caps**, for both: the zip at most 512 MB; at most 512 MB unpacked in all, and 10,000 entries; a file of a whole save
folder at most 64 MB. A save folder is a few hundred kilobytes for each profile, so only a bomb or a folder holding something
else entirely stops there. What comes out is counted while it is unpacked, whatever sizes the zip claims.

Either way the zip is unpacked into a folder of its own in the system's temporary folder (never a save folder, never the
backups), checked there, and deleted when the import is done or left; one an import never finished (the launcher closed in
between) goes at the next import, a day later.

## Progress and edits

The **Progress** button on each slot of the Saves page shows what the slot has and is missing, and changes it. It is Longshot's
own save editor ([`scripts/longshot_save_editor.py`](https://git.magicbean.dev/MagicBeanDev/Longshot/src/branch/main/docs/save-editor.md),
which also runs from the command line): the launcher does not read the game or a save itself, it runs that script from the
default build's own tools with that version's Python, asks it for JSON (`--json`), and shows what it answers
(`Core/SaveEditor.cs`, following how `PerfReports` runs `perf_report.py`). The rules about what a save can have, and where, are the editor's alone.

**What it needs.** A build the launcher made, whose version's tools are still on this PC and have the editor (a version from
before it has none: Progress is off, and the page says so), and the game's files in the build's folder (`romfs`, which the editor
reads the game's own lists from: its names, chests, Gold Skulltulas, dungeons, quests). Nothing of the game is kept by the
launcher or leaves the PC. The page edits the save the Saves page lists; a save written by another game version is shown and not
edited (edit it with the build that wrote it).

**What it shows.** The slot's place, hearts, rupees, the game's clock and when it was saved; then every category with how many the
slot has of how many (equipment, items, masks, upgrades, songs, quest items, hearts, heart containers and pieces, rupees, chests,
Gold Skulltulas, dungeon maps, compasses, boss keys and small keys, bosses, the Hall of Fallen Foes, story and side quests,
and the mini-games, which are listed and never count), and for the one you pick: its areas, and every missing entry by its
name in the game's text and by its area. The slot is 100% when nothing counted is missing; the percentage under the title never
says 100 while one thing is.

**Edits.** Each is asked first (what the editor says it would change, from its own `--dry-run`, where the backup goes, and that the
game must be closed) and then made:

| | |
|---|---|
| Set all, Set, Take away | a category, an area of it, or one entry: done, owned or found; or taken away again |
| Give… | an item (a counted one with an amount, within what the bags Link has hold), hearts, rupees (within the wallet), or a dungeon's small keys |
| Start place… | one of the game's named start places, with Link's age where the area has both |
| Copy slot… | this slot's files to another slot; a slot that holds a save is replaced only when you choose it |

An edit writes the flags the game's own lists name and nothing more (the editor's guide says what that leaves out: a boss set done
is not a boss fight won, a heart piece set does not raise the hearts, items are not equipped for you).

**Safety.** Refused while a game or an emulator runs (the launcher checks, leaving itself out, and the editor checks again); the whole
save folder is copied first to the backup folder above, named `edit-<player>-<date>-<time>`; each file is written whole beside its
place and swapped in by a rename, read back, and put back when that fails. The launcher never passes the editor's `--force`.
Nothing is edited that the editor refuses (an unknown target, a cap, another game version, a link in the save folder). A refusal
comes with a reason code: for a game running, NX2navi waiting, a link, a failed write, another game version and a few others the
launcher says it in its own words, and for the rest (a count the game does not allow, the bag to give first, a name that fits two
items) the editor's own words are shown as they are.

## Restore from a backup

Every copy backs up the save it replaces first (above: an import, an export, Hookshot's import, the versions' saves brought
together, a version deleted), and **Restore from a backup…** on the Saves page, under the slots, puts one back
(`Core/SaveBackups.cs`). It lists every save in the backups folder (on Linux both of them, each row saying which), newest first, each with what made it, when, its slots
and its size: "Before an import from Lichii", "The save of Lichii before an export to it, to restore into Longshot" (an
export's backup is the other program's old save, and a restore puts it into Longshot, not back into that program),
"v3.0.0-rc.5's own save, before the versions' saves were brought together" (a gather's backup holds one per version folder;
its `shared (set aside)` folder reads as the shared save), "nightly's own save, before it was deleted". A folder the launcher
did not name is listed by its name, at its folder's time; one with no save, a copy's hidden staging and the files there are
left out.

**One row a save the game would open.** A backup that holds a whole save folder (a .zip import's, a gather's or a delete's
version folder) may hold more than the game's profile: a stray one, a copy made by hand. Where the game's profile
(`5a3c9e1f0b7d24680000000000000001`) is there, it alone is listed; where it is not, each profile is listed with its id in
the row ("(profile 01234567…)"), so no two rows look the same. Whichever is restored goes into the game's profile.

**Restore…** opens the same confirmation as an import: the backup's slots, what they replace in Longshot's save (only the
files the backup has: one with no `account.sav` or `option.sav` leaves Longshot's), *Keep Longshot's own game settings*, and
where Longshot's own save is backed up first (`restore-backup-<yyyyMMdd-HHmmss>`, which the list then shows too). Back returns
to the backups list. The backup's slots, `account.sav` and (unless kept) `option.sav` become Longshot's; the backup itself is
only read. A whole save folder's import from a .zip is undone only in part this way: its backup holds the whole folder, but
a restore puts back one profile's save (its slots, `account.sav`, `option.sav`), not the rest of the folder the import
replaced; the folder button opens the backup for the rest. Refused while a game or an emulator runs, as every copy is. The folder button opens the backup, and *Open the
backups folder* the folder they are all in. Nothing on the page deletes a backup: that stays a file manager's job.

## Tests

The launcher's tests use no real save and nothing from the game:

- `tests/launcher_core/HookshotChecks.cs` runs on fake save folders in `tests/fixtures/hookshot-saves`, made of one-line
  text files.
- The save checks in `tests/launcher_core` follow the cases of Longshot's `tests/test_longshot_saves.py`.
- `SaveArchiveChecks.cs` makes its saves and its zips itself: a save folder with a nested folder, an empty one, a file
  nobody knows, a binary file and another profile, exported and imported back byte for byte; links left out; and the zips
  a careless or a hostile one would be (a path out of the zip, a drive, a link, a bomb, a file that is no save).
- `tests/launcher_core/SaveEditorChecks.cs` reads `tests/fixtures/save-editor`: what Longshot's save editor answers with
  `--json` (its guide, "For programs") on the made-up game and made-up saves of Longshot's own tests, kept as JSON text (the report,
  the item and place lists, each edit's answer, and the refusals with their codes), and stands a script in for the tools' Python to
  run the real process code. With `--longshot`, `tests/save_editor_fixtures.py` runs the editor in the checkout and fails when it
  answers something else, so a change to what the launcher reads shows there first (`--write` regenerates the files; read the
  diff). `tools/screenshots/save-editor-report.json` is a report authored with invented names, for the screenshots.
- `SaveBackupsChecks.cs` makes backups the way the copies do (`Saves.Copy`, `Saves.BackUpForDelete`, a gather's layout),
  lists them and restores them.
- `HookshotSettingsChecks.cs` uses authored INI and language text, including the merge into an existing
  `longshot_options.ini`, replacement backups, failed writes, restoration, skipped values and linked paths.

Settings compatibility was compared on 2026-11-13 against Hookshot
[`363038d8`](https://github.com/brunobaccari/Hookshot/tree/363038d8903009d8ef06a6f6df87c7c5bde370e1)
(`gpu/vk/vk_menu.cpp`, `upscaler_rows.h`, `host/nn_hid.c`) and Longshot `d9648863`. Hookshot writes `options_version=4`;
the import preserves that marker, or its absence, so Longshot applies its own migrations, and removes the build's lines
that those migrations would read differently (`HookshotSettings.Merge`). Earlier markers 0–3 are accepted using
Longshot's legacy reader, but every historical Hookshot release has not been tested. Later versions are refused until
their schema is checked; unknown keys and values out of range are listed and skipped.

The first-import destination is `5a3c9e1f0b7d24680000000000000001`. This comes from Longshot's fixed `user_uid` in
`host/nn_applet.c` and the high-word/low-word folder name in `host/nn_fs.c`, checked against Longshot `d9648863` on 2026-11-13.
If Longshot adds selectable players, this destination rule must follow that change. Tests use text fixtures with different
source profile IDs, and a stray Longshot profile folder, to check that an imported save lands in the folder the game
actually opens. The core test runner's
`--longshot` check also compares the destination ID with Longshot's source.
