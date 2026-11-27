#!/usr/bin/env python3
"""Steam capsule art: the Longshot glyph, its name under it, the flourish under that.

The look is the emulator's own start-up screen (Longshot's gpu/vk/vk_menu.cpp draw_startup and
gpu/vk/vk_ui.h): the logo glyph, LONGSHOT under it in the heading face with letters spaced by 0.14
of the letter size in cream (246,232,204), and the gold flourish (214,170,96) under that. The
face is Libertinus Serif Semibold, the font the start-up screen falls back to when the game's own
menu font is not there -- the game's file cannot ship, so this is the closest legal match.

INPUTS ARE THE ORIGINALS, and the originals only: assets/steam/src_<slot>.png are the shipped
pre-wordmark art, kept as committed siblings. Reading composed capsules back in would cut the
glyph out of a picture of itself every re-run (the soft glow of one pass becomes fringe in the
next); so the script takes only src_ files and writes only the four slot files.

Capsules save as RGB, the logo as RGBA: a capsule is a flat picture, and Steam scales with
premultiplied alpha -- a capsule carrying a near-opaque alpha channel renders its dark edges too
dark against the UI.

    python3 scripts/make_artwork.py OUT_DIR [--dry]     (OUT_DIR may be assets/steam itself)

Review the output by eye before committing: the composition is deterministic, its judgement is not.
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
STEAM = os.path.join(ROOT, "assets", "steam")
FONT = os.path.join(ROOT, "assets", "fonts", "LibertinusSerif-Semibold.ttf")
assert os.path.exists(FONT), f"the face the start-up screen falls back to is missing: {FONT}"

CREAM = (246, 232, 204)          # the heading colour on the start-up screen
GOLD = (214, 170, 96)            # ui::kAccent: the flourish's colour
WORD = "LONGSHOT"
GAP_RATIO = 0.14                 # ui::spaced's gap as a share of the letter size
# the start-up screen's own proportions, from its scale factor: the word is 42*k, the flourish
# curls are drawn in k, and k = 0.86*s -- so s (the flourish's unit) per em of word:
K_PER_EM = 42.0 * 0.86
SPANS = {"hero_1920x620": 0.72, "capsule_920x430": 0.84, "capsulep_600x900": 0.84, "logo_998x323": 0.80}
# slot -> (width, height, glyph share of the small side, word size as a share of the small side)
SLOTS = {
    "capsule_920x430":  (920, 430, 0.66, 0.105),
    "capsulep_600x900": (600, 900, 0.48, 0.100),
    "hero_1920x620":    (1920, 620, 0.66, 0.088),
    "logo_998x323":     (998, 323, 0.74, 0.088),
}


def original(slot):
    """The original art for a slot: committed beside it, the shipped file before any wordmark.
    Refuse anything else: the script must never feed on its own output."""
    path = os.path.join(STEAM, f"src_{slot}.png")
    assert os.path.exists(path), f"no original for {slot}: expected {path}"
    return Image.open(path)


def trim_bars(im):
    """Trim baked near-black border bars (letterboxing inside the asset itself, not the field) by
    shrinking any border row/column that is both off-field (sampled) and dark down its whole
    length. A column holding art is off-field too but bright: it never trims. On today's originals
    this trims nothing; it is a guard for future source art that does carry bars."""
    rgb = im.convert("RGB")
    w, h = rgb.size
    px = rgb.load()
    counts = {}
    for yy in range(0, h, max(1, h // 48)):
        for xx in range(0, w, max(1, w // 48)):
            counts[px[xx, yy]] = counts.get(px[xx, yy], 0) + 1
    field = max(counts.items(), key=lambda kv: kv[1])[0]

    def is_field(pt):
        return all(abs(a - b) <= 14 for a, b in zip(pt, field))

    def field_row(y):
        xs = range(0, w, max(1, w // 96))
        return sum(1 for x in xs if is_field(px[x, y])) >= len(list(xs)) * 0.9

    def field_col(x):
        ys = range(0, h, max(1, h // 96))
        return sum(1 for y in ys if is_field(px[x, y])) >= len(list(ys)) * 0.9

    def bar_col(x):
        ys = range(0, h, max(1, h // 96))
        return sum(1 for y in ys if sum(px[x, y]) < 45) >= len(list(ys)) * 0.85

    def bar_row(y):
        xs = range(0, w, max(1, w // 96))
        return sum(1 for x in xs if sum(px[x, y]) < 45) >= len(list(xs)) * 0.85

    x0, x1, y0, y1 = 0, w, 0, h
    cap = 60                                    # (bars are tens of px, not hundreds: a cap keeps a bad field guess from eating the art)
    n = 0
    while x0 < x1 and not field_col(x0) and bar_col(x0) and n < cap: x0 += 1; n += 1
    n = 0
    while x1 > x0 and not field_col(x1 - 1) and bar_col(x1 - 1) and n < cap: x1 -= 1; n += 1
    n = 0
    while y0 < y1 and not field_row(y0) and bar_row(y0) and n < cap: y0 += 1; n += 1
    n = 0
    while y1 > y0 and not field_row(y1 - 1) and bar_row(y1 - 1) and n < cap: y1 -= 1; n += 1
    return im.crop((x0, y0, x1, y1))


def field_color(im):
    """The flat field the glyph sits on, sampled from the corners (never assumed)."""
    rgb = im.convert("RGB")
    w, h = rgb.size
    px = rgb.load()
    c0, c1, c2, c3 = px[2, 2], px[w - 3, 2], px[2, h - 3], px[w - 3, h - 3]
    if c0 == c1 == c2 == c3:
        return (int(c0[0]), int(c0[1]), int(c0[2]))
    return None


def glyph_box(rgb, bg):
    """(x0,y0,x1,y1) of everything that is not the field: the glyph and the glow baked with it."""
    w, h = rgb.size
    px = rgb.load()
    tol = 24
    xs, ys = [], []
    for y in range(h):
        for x in range(w):
            r, g, b = px[x, y]
            if abs(r - bg[0]) + abs(g - bg[1]) + abs(b - bg[2]) > tol:
                xs.append(x)
                ys.append(y)
    return min(xs), min(ys), max(xs) + 1, max(ys) + 1


def cut_glyph(im, bg):
    """The glyph with its glow, on transparency: alpha from distance from the field, clamped to at
    most what the source already had (never brighten the source's own edges). Field pixels go fully
    transparent; the glow keeps its own soft ramp."""
    rgb = im.convert("RGBA")
    box = glyph_box(im.convert("RGB"), bg)
    glyph = rgb.crop(box)
    px = glyph.load()
    w, h = glyph.size
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            d = abs(r - bg[0]) + abs(g - bg[1]) + abs(b - bg[2])
            alpha = 0 if d <= 12 else min(a, int(a * d / 60.0) if d < 60 else a)
            px[x, y] = (r, g, b, alpha)
    return glyph


def bez(pts, n=40):
    """A cubic bezier as a polyline: the same four control points AddBezierCubic is given."""
    (x0, y0), (x1, y1), (x2, y2), (x3, y3) = pts
    out = []
    for i in range(n + 1):
        t = i / float(n)
        u = 1.0 - t
        out.append((u*u*u*x0 + 3*u*u*t*x1 + 3*u*t*t*x2 + t*t*t*x3,
                    u*u*u*y0 + 3*u*u*t*y1 + 3*u*t*t*y2 + t*t*t*y3))
    return out


def flourish(d, x0, x1, y, em):
    """ui::flourish from vk_ui.h: a faint line, two mirrored curls, a small diamond at the middle."""
    s = em / K_PER_EM
    m = (x0 + x1) / 2.0
    t = max(1.0, 0.8 * s)
    faint = tuple(GOLD) + (int(0.35 * 255),)
    curl = tuple(GOLD) + (int(0.8 * 255),)
    d.line([(x0, y), (m - 30 * s, y)], fill=faint, width=int(round(t)))
    d.line([(m + 30 * s, y), (x1, y)], fill=faint, width=int(round(t)))
    for side in (-1, 1):
        k = float(side)
        d.line(bez([(m + k * 4 * s, y), (m + k * 11 * s, y - 7 * s),
                    (m + k * 21 * s, y - 6 * s), (m + k * 19 * s, y)]), fill=curl, width=max(1, int(round(1.3 * s))))
        d.line(bez([(m + k * 19 * s, y), (m + k * 17 * s, y + 5 * s),
                    (m + k * 11 * s, y + 4 * s), (m + k * 12 * s, y + 1 * s)]), fill=curl, width=max(1, int(round(1.3 * s))))
    r = 2.2 * s
    d.polygon([(m, y - r), (m + r, y), (m, y + r), (m - r, y)], fill=curl)


def spaced_width(font, em, text, gap):
    return sum(font.getlength(ch) for ch in text) + gap * (len(text) - 1)


def draw_spaced(d, cx, y, font, em, text, gap, color):
    total = spaced_width(font, em, text, gap)
    x = cx - total / 2.0
    for ch in text:
        d.text((x, y), ch, font=font, fill=color)
        x += font.getlength(ch) + gap


def fit_em(em, gap_ratio, max_w):
    """Shrink the letter size until the spaced word fits the slot (never let the name be cut)."""
    while em > 8 and spaced_width(ImageFont.truetype(FONT, em), em, WORD, em * gap_ratio) > max_w:
        em -= 1
    return ImageFont.truetype(FONT, em), em


def make(slot, out, dry=False):
    """Recompose one slot from its original: the glyph cut from its flat field, the word under it,
    the flourish under that. A flat field (the capsules) means the glyph comes out with its baked
    glow and goes back on the same colour, no seam; the logo's asset carries real alpha, so its
    glyph keeps it and the word goes under it on transparency."""
    w, h, glyph_frac, em_frac = SLOTS[slot]
    src = original(slot)
    assert src.size == (w, h), f"src_{slot}.png is {src.size}, the table says {(w, h)}"
    src = trim_bars(src)
    has_alpha = src.mode == "RGBA" and src.getchannel("A").getextrema()[0] < 255
    bg = field_color(src)
    if has_alpha:
        glyph = src.copy()
        glyph = glyph.crop(glyph.getbbox())           # the alpha's own box
        canvas = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    elif bg is not None:
        glyph = cut_glyph(src, bg)
        canvas = Image.new("RGBA", (w, h), (bg[0], bg[1], bg[2], 255))
    else:
        raise SystemExit(f"src_{slot}.png: neither a flat field nor an alpha channel: refusing to recompose it blind")

    gw, gh = glyph.size
    target = int(min(w, h) * glyph_frac)
    scale = target / float(max(gw, gh))
    gw, gh = max(1, int(gw * scale)), max(1, int(gh * scale))
    glyph = glyph.resize((gw, gh), Image.Resampling.LANCZOS)

    em = int(min(w, h) * em_frac)
    font, em = fit_em(em, GAP_RATIO, 0.88 * w)
    gap = em * GAP_RATIO
    ascent, descent = font.getmetrics()
    text_h = ascent + descent
    gap_gl = int(em * 0.30)          # the start-up screen's 16*k between logo and word, trimmed for a capsule
    gap_fl = int(em * 1.10)          # its 66*k down to the flourish, trimmed likewise
    total_h = gh + gap_gl + text_h + gap_fl
    free = h - total_h
    top = max(0, int(free * 0.60))   # (a hair below centre: the eye reads centred art as high)
    canvas.alpha_composite(glyph, ((w - gw) // 2, top))
    d = ImageDraw.Draw(canvas)
    y = top + gh + gap_gl
    draw_spaced(d, w / 2.0, y, font, em, WORD, gap, tuple(CREAM) + (255,))
    span = min(SPANS[slot], 0.88) * w
    flourish(d, w / 2.0 - span / 2.0, w / 2.0 + span / 2.0, y + text_h + int(em * 0.18), em)

    path = os.path.join(out, f"{slot}.png")
    if dry:
        print("dry:", path, w, h, "em", em, "glyph", gw, gh)
        return path
    if has_alpha:
        canvas.save(path)                                  # the logo slot keeps its transparency
    else:
        canvas.convert("RGB").save(path)                   # a capsule is a flat picture: RGB, no alpha
    print("made:", path, (w, h), "word px", em, "glyph", gw, gh)
    return path


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    out = sys.argv[1]
    dry = "--dry" in sys.argv
    if not dry:
        os.makedirs(out, exist_ok=True)
    for slot in SLOTS:
        make(slot, out, dry=dry)
    return 0


if __name__ == "__main__":
    sys.exit(main())
