# NFC tags

Open **Settings → NFC tags** to manage dumps of figures you own. Longshot reads these local files for the NFC tab of the game's Insert menu.

| Platform | Default folder |
| --- | --- |
| Windows | `%LOCALAPPDATA%\Longshot\nfc` |
| Linux and Steam Deck | `$XDG_DATA_HOME/longshot/nfc` or `~/.local/share/longshot/nfc` |
| Portable | `Longshot/data/nfc/` beside the launcher. Earlier portable layouts use their existing launcher data directory. |

Set `LONGSHOT_NFC_DIR` to override the folder. An absolute path works across installed versions. A relative path is resolved against the selected version's `builds/default/` directory and needs an installed version. **Open the folder** opens the location in your file manager and creates it if needed. Rename a file there to change the tag's name in the game.

## Add or remove tags

Use **Add files**, **Add a folder**, or drop files and folders onto **NFC tags** in Settings. Folder imports search subdirectories and skip linked directories. The launcher validates each file before copying it and displays its model ID. Identical files with the same name share one copy. A different file with that name gets a numeric suffix. The original file stays in its selected folder.

| Format | Accepted input |
| --- | --- |
| `.bin` | Raw NTAG215 dump of 532, 540 or 572 bytes |
| `.nfc` | Flipper Zero text beginning with `Filetype:` and containing at least 130 distinct four-byte pages of the 135-page tag |

Both formats must have the NTAG215 capability container and figure marker. The launcher ignores file names beginning with `key_`. Existing invalid tag files are listed with the reason. The game reads up to 256 tag files directly in the NFC folder.

Select the unwanted tags and choose **Delete selected**. Review the file names and confirm. The launcher checks that each selected file is still the file you reviewed before deleting it. NFC dumps stay on your PC.

## Scan a figure on Windows

Connect a Nintendo Switch 2 Pro Controller over USB with WinUSB on its NFC interface. Choose **Scan a tag** and hold the figure over the reader until the scan finishes. The launcher can cancel a scan and releases the reader when it finishes. Other platforms support importing an existing dump.

The Rust [tag reader](../src/players/reader.rs) and [file handling](../src/players/nfc.rs) validate tag data locally. `cargo test players::` runs synthetic NFC file and controller-response tests.
