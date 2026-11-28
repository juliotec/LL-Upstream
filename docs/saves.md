# Profiles and saves

Open **Saves** to see the files in the active profile. Select a file to see its location, save time, thumbnail and adventure progress. **Continue from here** tells Longshot which file to open. **+ New save** selects an unused slot for this profile and returns to Home. Press **Play Longshot** when you’re ready to open the game’s file list and start a new adventure. You can also choose an existing save before pressing Play.

**Progress and details** reads the slot's local caption. When the caption provides them, the launcher shows the thumbnail, area, hearts, medallions and spiritual stones. A save with a missing or unrecognized caption still appears by slot number and file modification time. Story, chest and heart-piece completion require a report from the game. The launcher never uploads a save, its thumbnail, or information read from the save.

## Moving from the stable launcher

Launcher preferences start fresh after upgrading from 1.3.x, except what would only make the player repeat setup: the launcher language, the game language and a custom build-files folder are reused when the previous launcher had them set. The update channel, active profile and every consent are chosen here. Choose a cache drive in **Settings → Game** before your first build if the previous launcher used one. The save editor and its binary-editing contract tests were retired. The Progress view displays save details read-only. Hookshot game settings must be configured in Longshot.

On the first upgrade from stable, the launcher copies the shared save and its pending writes into the default profile. Existing adventures stay intact. If the default profile already has different saves, the launcher creates a separate **Stable saves** profile. Windows installations with saves still inside individual game folders can produce additional profiles. The original files stay available to stable, and later launches keep the progress you make in each launcher. **Saves → Import** also lists detected stable save slots.

The launcher also imports loose mods from stable’s shared user folder and detected game folders. It preserves their load order and enabled settings. Versions with different mod sets can have separate profiles. Legacy cheat files are retained, but the current mod format cannot run those cheats. Mods that need code permissions still require your approval.

To return to the published stable launcher, open **Settings → Launcher**, select **Stable**, then choose **Install and restart** when the stable release appears. **Later** keeps the current launcher running. Stable opens its original saves and mods. Progress and mod changes made in a nightly profile stay in that profile for the next time you use nightly.

If stable has old builds or caches that can be removed, **Settings → Storage → Previous launcher** lists the generated files by type and folder. Choose **Clean up** beside a row to review that folder and file type immediately. Confirm to see removal progress. The banner at the bottom of Versions appears when this scan finds files to clean up. **Clean all** reviews all eligible categories. Extracted RomFS and ExeFS copies qualify when the old builder recorded their source and the matching original package is available. Both launchers keep their saves, mods and original game files. The renderer’s shared shader store stays under **Shader caches** because standalone games can use it too. A stable build you clean up may need rebuilding when you return.

## Switch or manage profiles

The profile switcher is in the header. Open **Manage** to create, duplicate, recolour or delete a profile. Use the edit button next to the selected profile’s name to rename it. **Switch to** opens Home with that profile’s settings and saves. Each profile keeps its game options, mods and saves. Installed versions are shared across profiles. Deleting a profile first backs up its complete directory and checks the copy. After deletion, choose among the remaining profiles, or continue with the only one left. If there are no profiles left, choose **Create profile** to start again.

The Rust launcher reads current profile directories in place. A fresh launcher creates a default profile. When there are multiple profiles, it asks who is playing each time it starts. Detached saves from another installation can be brought in through **Import**.

| Location inside the launcher data directory | Contents |
| --- | --- |
| `profiles/<id>/profile.json` | Profile name, colour, selected game source, version preference, `start_slot`, mods and other builder settings |
| `profiles/<id>/settings.ini` | Per-profile game options |
| `profiles/<id>/saves/save/<user-id>/` | Slot directories, `account.sav` and `option.sav` |
| `profiles/<id>/mods/` | Manual mods for this profile |

The default profile ID is `default`. Additional IDs use `p-` followed by eight hexadecimal digits. The game user ID is `5a3c9e1f0b7d24680000000000000001`. Each `slot_NN` contains `progress.sav` and can contain `caption.sav`. The edit button beside a save title gives the slot a display name in the profile’s `save_names` map. Clearing the name restores its recorded location, or “Saved game” if the caption has no location. These labels stay in the launcher profile and follow explicit copies and moves between profiles. **Open save folder** opens the current profile's save directory. Profile edits preserve unrelated JSON fields. A profile with a newer file format or unreadable metadata can be inspected as damaged.

## Import, export and restore

- **Import** shows detected Hookshot, NX2navi and emulator saves. Expand a location, select a slot and confirm the destination. **Open file** accepts a save ZIP or a standalone `progress.sav`. **Open folder** lets you select slots from another save directory or a local backup. Dropping a save ZIP on the Saves page also opens its import review.
- **Export** opens Save As for the selected slot. The ZIP contains that slot’s progress and caption. Importing a slot preserves the other slots in the destination profile. Older complete launcher ZIPs can still restore the entire `save/` folder after confirmation.
- **Duplicate** copies the selected save and thumbnail to the next unused slot in the same profile, gives the copy a distinct display name and selects it.
- **Copy to profile** copies or moves just the selected slot. If its slot number is occupied, the launcher looks for a free slot. A move verifies the copied files before removing the source.

Longshot makes a backup before replacing existing saves, then stages the incoming files and swaps in the completed copy. If the game is using the source or destination saves, close it before transferring them. The launcher identifies the blocking process. A Longshot instance using a separate save directory can stay open. Close Hookshot or other emulators if the launcher cannot determine which saves they are using. NX2navi must finish pending save transactions. Source and destination must be separate directories. The launcher refuses linked save files and directories during a transfer.

| Installation | Backup root |
| --- | --- |
| Windows | `%LOCALAPPDATA%\Longshot\save-backups` |
| Linux and Steam Deck | `$XDG_DATA_HOME/longshot/save-backups` or `~/.local/share/longshot/save-backups` |
| Portable | `save-backups/` in the launcher data directory |

## Import from Hookshot

Open **Saves → Import**, expand **Hookshot**, and select a slot. Detected-location import copies the selected slot. A complete save ZIP also carries `account.sav` and `option.sav`. Each `slot_NN/` directory holds `progress.sav` and an optional `caption.sav`. Every entry must be a regular file or directory, and each save file must be between one byte and 4 MiB.

The profile store, save transfers, archive validation and caption reader are in [src/players](../src/players). Run `cargo test players::` to exercise them with synthetic data.
