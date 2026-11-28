# Steam capsule artwork: where it comes from

Four slot files, in the four slots Steam's library reads (the launcher writes them under both the
32-bit and the 64-bit grid key, never over a capsule the user chose), plus their **originals**:
`src_<slot>.png` are the shipped pre-wordmark art, kept as committed siblings because
`scripts/make_artwork.py` reads only those and writes only the slot files. Feeding the composed
capsules back in would cut the glyph out of a picture of itself, so the script refuses.

- `capsule_920x430.png` — the landscape tile (Steam deck/library); RGB like all capsules: Steam
  scales with premultiplied alpha, and a capsule with an alpha channel renders its dark edges wrong
- `capsulep_600x900.png` — the portrait tile (Gaming Mode on the Deck)
- `hero_1920x620.png` — the hero strip
- `logo_998x323.png` — the logo slot (the one slot that keeps real transparency)

**Do not hand-edit the slot files.** Run `python3 scripts/make_artwork.py assets/steam`,
look at the output, commit. The recipe mirrors the emulator's own start-up screen (Longshot
`gpu/vk/vk_menu.cpp` `draw_startup`, `gpu/vk/vk_ui.h`): the Longshot glyph, `LONGSHOT` under it
in Libertinus Serif Semibold (the fallback face the start-up screen itself uses; the game's own
menu font cannot ship) in cream `(246,232,204)` spaced by 0.14 em, on the same flat field the
source art carries (sampled, never assumed). The start-up screen's gold `(214,170,96)` flourish
rule under the word — line, two mirrored curls, centre diamond — is deliberately not drawn:
review had the line under the wording out of the artwork, and the banner the game build draws
leaves it out too. If the artwork changes upstream, replace
the `src_` originals and re-run the script; a visual review of the output is part of the change,
not optional. The compositor is deterministic: re-running on unchanged originals reproduces the
slot files byte for byte.
