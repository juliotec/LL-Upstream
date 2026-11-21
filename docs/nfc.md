# NFC

What the launcher does with your NFC tags: where Longshot reads them from, what a dump must be, adding them, scanning one with a controller, and deleting
them. Everything is on the **NFC** page (the *NFC* tab in the title bar). The tags are dumps of figures
you own. In the game, the Insert menu's NFC tab lists them, picks one and taps it.

Nothing of a dump leaves your PC: the launcher uploads none of it and writes none of it to a log. A message names a file
and says why it was skipped, never what is in it.

## Where Longshot keeps them

One folder for your user, shared by every build of the game and by the launcher:

| | Where |
|---|---|
| Windows | `%LOCALAPPDATA%\Longshot\nfc` |
| Linux and Steam Deck | `$XDG_DATA_HOME/longshot/nfc` (default `~/.local/share/longshot/nfc`) |
| Either | `LONGSHOT_NFC_DIR=<folder>`, when it is set and not empty, instead |

The launcher passes `LONGSHOT_NFC_DIR` on to the game it starts, on Windows and on Linux alike (on Linux it drops its other
`LONGSHOT_` variables, but not this one), so the page and the game use the same folder.

Give `LONGSHOT_NFC_DIR` as a full path. The game takes it as it is given, and the launcher starts the game in the build's
folder on both systems, so a relative one is a folder inside each build: the page then uses the one in the default build's
folder, and, before any build exists, the usual folder above.

The folder is made the first time something is added to it or it is opened. **Open the folder** shows it in the file
manager, and the page shows its path.

Each NFC tag is one file directly in the folder (subfolders are not read), ending in `.bin` or `.nfc` in either case. The
game shows the file name without its ending as the tag's name and lists the tags by name. Files whose name starts with
`key_` are not tags: the game ignores them, and so does the page.

The game lists at most 256 files. As it reads the folder today (`host/nn_nfp.c` in
https://git.magicbean.dev/MagicBeanDev/Longshot/pulls/280), it stops at 256 while it is still reading the folder and only
then sorts them by name, so with more than 256 it keeps whichever 256 the folder gave it first, not the first 256 by name.
It counts every `.bin` and `.nfc` file that is not a `key_` file, those that are not a dump included. Past 256 the page says
so, counting the same files; deleting some is the way to choose which the game lists. (Sorting first and then keeping 256
is a change for the game, in that pull request.)

## What a dump must be

| | |
|---|---|
| `.bin` | A raw NTAG215 dump of exactly 532, 540 or 572 bytes. The tag is its first 540 bytes, 4 bytes per page |
| `.nfc` | A Flipper Zero NFC file: text that starts with `Filetype:`, with lines `Page N: xx xx xx xx` (N in decimal, four bytes in hex) for at least 130 of the tag's 135 pages. Page N is at byte N×4 of the tag |

Either way the 540 bytes must be a figure's tag: `F1 10 FF EE` at `0x0C` (an NTAG215's capability container) and `0x02`
at `0x5B`. The page shows each tag's model ID, the 8 bytes at `0x54` to `0x5B` as 16 hex digits. (The tag's UID is its
bytes 0, 1, 2, 4, 5, 6 and 7; the page does not show it.)

A `.bin` or `.nfc` file in the folder that is none of this is still listed, marked **Not an NFC tag dump** with the
reason, so that you can delete it.

## Adding

- **Add files…** picks one file or many at once.
- **Add a folder…** takes every `.bin` and `.nfc` file in the folder you pick and in the folders under it (not `key_`
  files). A link to a folder under it (a symlink, or a junction on Windows) is not gone into, since one can lead back up
  (a Wine or Proton prefix's `dosdevices/z:` is `/`); a link to a file is taken as that file.
- **Dropping** files or folders on the window while the page is shown adds them the same way: files as *Add files…* takes
  them, a folder as *Add a folder…* does. While an add is still running the window takes no drop (the cursor says so);
  drop them again once it is done.

The files are looked for and copied away from the window, which stays responsive with a big folder; the page shows how
far it is, and the add buttons and *Delete selected* are off until it is done.

Each file is checked first, then **copied** in under its own file name; the file you picked stays where it is. A file that
is not a dump is skipped, and the page names it and says why. A name that is taken is never written over:

- the same file is there already, byte for byte: nothing is copied, and the page says *already there*;
- a different file has that name: the new one goes in as `Name (2).bin` (then `Name (3).bin`…).

Each copy is written to a temporary file and renamed into place, so a copy cut short leaves no half file. The rename itself
never replaces a file: if one took the name in the meantime (a tag scanned while files are being added), that one is
kept and the new one takes the next number.

To rename a tag, rename its file in the folder: the game shows the new name at its next start.

## Scanning a tag (Windows)

**Scan a tag…** reads a tag with a Nintendo Switch 2 Pro Controller and saves it into the NFC folder. Scan only tags of
figures you own.

What it needs:

- Windows. On Linux the button is off.
- The controller plugged in **over USB**, with its cable. Bluetooth is not enough.
- **Steam, Longshot and any controller bridge (such as Switch2Connect) closed.** Only one program can hold the controller's
  USB interface; while another has it, the page says the controller is in use by another program.

Choose *Scan a tag…*, then hold the tag flat on the controller's logo. The launcher waits up to 10 seconds for it (*Cancel*
stops the wait), reads it, and stops the controller's reader again.

- The tag is **only read, never written**: the launcher has no command that writes one.
- A figure's tag is saved as `Scanned <UID>.bin`, the UID as 14 hex digits, 540 bytes. The game names its own scans the
  same way, so a tag scanned in both is one file. It then goes in as any added file does: the same dump already there is
  left, and a different dump of that name (the same tag with other data on it) goes in as `Scanned <UID> (2).bin`.
- A tag that is not a figure's (see [What a dump must be](#what-a-dump-must-be)) is read, and the page says so; nothing is
  saved.
- Otherwise the page says what happened: no tag found, no controller found over USB, the controller is in use by another
  program, or the tag could not be read (hold it still on the logo and scan again).

How it talks to the controller (`Core/NfcReader.cs`): through WinUSB, to interface 1 of USB device `057E:2069`, one command
out and its answer back. It starts the reader looking, asks its state until a tag is found, reads the tag with the figure
tags' password and, when the tag refuses it, again without, then fetches the read's buffer, whose bytes 60 to 600 are the
tag.

## Deleting

The bin on a row deletes that file; tick several rows and **Delete selected** deletes them together. Either asks first and
lists the files. Only `.bin` and `.nfc` files directly in the NFC folder are ever deleted; the files you added them from
are not touched.

## Tests

`tests/launcher_core/NfcChecks.cs` checks `Core/NfcTags.cs` with **fake tags it makes itself**: 540 patterned bytes with
only the bytes above set. No real dump is in the repository, and none may be added (CONTRIBUTING.md). It covers the folder on
both systems and with `LONGSHOT_NFC_DIR` (full or relative), the game started on Linux keeping `LONGSHOT_NFC_DIR`, the `.bin` sizes, the `.nfc` text (whole, too few pages, garbage), the two marks,
`key_` files, a folder with links that loop back up to it (Linux), names that are taken, deleting, the order by name, and file names outside ASCII.

`tests/launcher_core/NfcReaderChecks.cs` checks the scan with **no controller**: a fake one, scripted in the test, that
answers as the real one does over a fake tag. It covers each command's bytes, the state and its UID, the buffer put together
from its parts, the read without the password after a tag refuses it, no tag in 10 seconds, a scan that is cancelled, and
the file name a scan is saved under. The WinUSB side (finding and opening the controller) needs the controller and is not
covered by a test.
