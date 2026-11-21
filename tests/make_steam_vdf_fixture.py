#!/usr/bin/env python3
"""Make the Steam library fixtures under tests/fixtures/steam/ :

  shortcuts.vdf          two tiles in the exact binary grammar Steam's own shortcuts.vdf uses on
                         the Deck (a type 0 section is 00 name 00 body 08; type 1 string; type 2
                         int32; a lone 08 closes each map), one tile kept value-faithful from
                         Angela's Deck (the real EmulationStation tile with its real appid) and
                         one with a quoted, spaced Exe; no Longshot tile, so the launcher's
                         checks can add one

  shortcuts-handmade.vdf the same library plus a tile named Longshot the user made by hand in
                         Steam's own dialog, running their own script: the launcher must leave
                         that one exactly as it is

The appid rule is Steam's own on this file, the same one the ChimeraOS, Heroic and
SteamTinkerLaunch shortcut writers document: crc32(Exe + AppName) over the file's own values,
high bit set, read as signed. When the reference `vdf` Python library is installed (it parses
the Deck's real file and writes it back byte for byte) the fixtures are checked against it.

Run from the repo root: python3 tests/make_steam_vdf_fixture.py
"""
import binascii, struct, os

def crc(s): return binascii.crc32(s.encode()) & 0xFFFFFFFF

def s(b, k, v):
    b.append(1); b += k.encode() + b"\x00" + v.encode() + b"\x00"

def i(b, k, v):
    b.append(2); b += k.encode() + b"\x00" + struct.pack("<i", v)

def entry(b, idx, appid, name, exe, startdir, tags):
    b.append(0); b += str(idx).encode() + b"\x00"     # the entry is a section named by its number
    i(b, "appid", appid); s(b, "AppName", name); s(b, "Exe", exe); s(b, "StartDir", startdir)
    s(b, "icon", ""); s(b, "ShortcutPath", ""); s(b, "LaunchOptions", "")
    i(b, "IsHidden", 0); i(b, "AllowDesktopConfig", 1); i(b, "AllowOverlay", 1)
    i(b, "OpenVR", 0); i(b, "Devkit", 0); s(b, "DevkitGameID", "")
    i(b, "DevkitOverrideAppID", 0); i(b, "LastPlayTime", 0); s(b, "FlatpakAppID", "")
    s(b, "sortas", "")
    b.append(0); b += b"tags\x00"
    # Steam's own file on the Deck writes each tag as a plain string pair: 01 "0" 00 "favorite" 00
    for n, tag in enumerate(tags): s(b, str(n), tag)
    b.append(8)                         # end of tags
    b.append(8)                         # end of the entry

# The tile Steam itself wrote on the Deck, kept value-faithful: the real Exe (Steam quotes the
# path in the file) and the real appid, which equals crc32(Exe + AppName) over the file's own
# quoted value with the high bit set read as signed
ES_EXE = '"/home/deck/Emulation/tools/EmulationStation-DE-x64_SteamDeck.AppImage"'
ES_NAME = "EmulationStation-DE-x64_SteamDeck"
assert (crc(ES_EXE + ES_NAME) | 0x80000000) - 2**32 == -1467123759

# A second tile, quoted Exe, as Steam's own file writes it; its appid follows the same rule over
# the file's own values, spaces and quotes and all
HL_NAME = "Half-Life"
HL_EXE = '"steam steam://run/220"'

# A tile the user added by hand in Steam's dialog for their own script: Steam computed its id
# from the values they typed, which name no launcher AppImage
HAND_NAME = "Longshot"
HAND_EXE = "/home/deck/Games/Longshot.sh"

def library(entries):
    out = bytearray()
    out.append(0); out += b"shortcuts\x00"
    for idx, (appid, name, exe, startdir, tags) in enumerate(entries):
        entry(out, idx, appid, name, exe, startdir, tags)
    out.append(8)                       # end of shortcuts
    out.append(8)                       # end of file
    return bytes(out)

base = [
    (-1467123759, ES_NAME, ES_EXE, '"/home/deck/Emulation/tools/"', ["favorite"]),
    ((crc(HL_EXE + HL_NAME) | 0x80000000) - 2**32, HL_NAME, HL_EXE, "/home/deck", []),
]
files = {
    "shortcuts.vdf": library(base),
    "shortcuts-handmade.vdf": library(base + [
        ((crc(HAND_EXE + HAND_NAME) | 0x80000000) - 2**32, HAND_NAME, HAND_EXE, "/home/deck/Games/", []),
    ]),
}

here = os.path.join(os.path.dirname(__file__), "fixtures", "steam")
os.makedirs(here, exist_ok=True)
for name, data in files.items():
    path = os.path.join(here, name)
    open(path, "wb").write(data)
    print(f"{path}: {len(data)} bytes")

try:
    import vdf
except ImportError:
    print("vdf library not installed: the reference check was skipped"); raise SystemExit
for name, data in files.items():
    d = vdf.binary_loads(data)
    assert vdf.binary_dumps(d) == data, name + " does not round-trip through the reference parser"
    assert len(d["shortcuts"]) == len(base) + (1 if "handmade" in name else 0), name
print("reference vdf library: both files parse and write back identical")
