"""
Draws documentation callouts onto a Benchmix screenshot.

The point of this script is that it never guesses where a control is. `--uiprobe` writes a
`.map.tsv` beside the PNG giving the real pixel rectangle of every labelled control, so a figure
spec refers to controls by name ("knob:TEMPO") and the boxes land exactly on them. When the layout
changes, re-run the probe and the same spec still points at the right things.

    python scripts/annotate.py docs/figures/overview.json

Figure spec (JSON):
{
  "source": "raw.png",              # relative to the spec file; the .map.tsv beside it is used
  "out":    "overview.png",
  "crop":   ["knob:TEMPO", "toggle:S"],   # optional: labels (or [x,y,w,h]) to frame, plus "pad"
  "pad":    24,
  "scale":  1.0,                    # applied after cropping
  "title":  "The main window",
  "callouts": [
    {"target": "knob:TEMPO", "text": "Tempo - speeds the song up or down...", "badge": "top-left"},
    {"target": ["knob:LOW", "knob:HIGH"], "text": "..."}   # one box spanning several controls
  ]
}
"""

import json
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

# The app's own dark palette, so a figure looks like part of the program rather than a pasted-on
# diagram. Themes/Dark.xaml is the source of these.
BG = (15, 17, 20)
PANEL = (28, 32, 38)
LINE = (46, 52, 61)
TEXT0 = (232, 235, 240)
TEXT1 = (154, 164, 178)
ACCENT = (76, 141, 255)

FONTS = Path("C:/Windows/Fonts")


def font(size, bold=False):
    return ImageFont.truetype(str(FONTS / ("segoeuib.ttf" if bold else "segoeui.ttf")), size)


def load_map(png_path):
    tsv = png_path.with_suffix("").with_suffix(".map.tsv")
    if not tsv.exists():
        tsv = Path(str(png_path)[: -len(png_path.suffix)] + ".map.tsv")
    rects = {}
    for line in tsv.read_text(encoding="utf-8").splitlines()[1:]:
        if not line.strip():
            continue
        label, x, y, w, h = line.split("\t")
        rects[label] = (int(x), int(y), int(w), int(h))
    return rects


def resolve(target, rects):
    """A label, a list of labels (their bounding box), or a literal [x, y, w, h]."""
    if isinstance(target, (list, tuple)) and len(target) == 4 and all(isinstance(v, int) for v in target):
        return tuple(target)
    labels = [target] if isinstance(target, str) else list(target)
    boxes = []
    for name in labels:
        if name not in rects:
            raise SystemExit(f"no control named {name!r} in the map — "
                             f"nearest: {[k for k in rects if name.split(':')[-1][:4].lower() in k.lower()][:5]}")
        boxes.append(rects[name])
    x0 = min(b[0] for b in boxes)
    y0 = min(b[1] for b in boxes)
    x1 = max(b[0] + b[2] for b in boxes)
    y1 = max(b[1] + b[3] for b in boxes)
    return (x0, y0, x1 - x0, y1 - y0)


def rounded(draw, box, radius, **kw):
    draw.rounded_rectangle([box[0], box[1], box[0] + box[2], box[1] + box[3]], radius, **kw)


def main(spec_path):
    spec_path = Path(spec_path)
    spec = json.loads(spec_path.read_text(encoding="utf-8"))
    src = (spec_path.parent / spec["source"]).resolve()

    rects = load_map(src)
    shot = Image.open(src).convert("RGB")

    # --- crop, then move every rectangle into the cropped frame -------------------------------
    pad = spec.get("pad", 24)
    if "crop" in spec:
        # Widen the crop to hold every callout target as well, so a badge pinned to a box corner
        # always has room and never gets shoved onto neighbouring controls.
        boxes = [resolve(spec["crop"], rects)] + [resolve(c["target"], rects) for c in spec["callouts"]]
        cx = min(b[0] for b in boxes)
        cy = min(b[1] for b in boxes)
        cw = max(b[0] + b[2] for b in boxes) - cx
        ch = max(b[1] + b[3] for b in boxes) - cy
        cx, cy = max(0, cx - pad), max(0, cy - pad)
        cw = min(shot.width - cx, cw + 2 * pad)
        ch = min(shot.height - cy, ch + 2 * pad)
        shot = shot.crop((cx, cy, cx + cw, cy + ch))
    else:
        cx = cy = 0

    scale = spec.get("scale", 1.0)

    def place(box):
        x, y, w, h = box
        return tuple(int(round(v * scale)) for v in (x - cx, y - cy, w, h))

    if scale != 1.0:
        shot = shot.resize((int(shot.width * scale), int(shot.height * scale)), Image.LANCZOS)

    # --- lay out the canvas: screenshot on top, legend underneath ------------------------------
    f_badge = font(max(15, int(19 * min(1.0, scale * 1.4))), bold=True)
    f_text = font(19)
    f_lead = font(19, bold=True)
    f_title = font(26, bold=True)

    callouts = spec["callouts"]
    margin = 28
    text_x = margin + 40
    wrap_w = shot.width + 2 * (f_badge.size + 14) - text_x - margin

    scratch = ImageDraw.Draw(Image.new("RGB", (10, 10)))

    def wrap(text, f, width, first_indent=0):
        """Wrap, allowing the first line to be shortened by a bold lead-in already drawn on it."""
        words, lines, cur = text.split(), [], ""
        avail = width - first_indent
        for word in words:
            trial = (cur + " " + word).strip()
            if scratch.textlength(trial, font=f) <= avail or not cur:
                cur = trial
            else:
                lines.append(cur)
                cur = word
                avail = width
        if cur:
            lines.append(cur)
        return lines

    entries = []
    for c in callouts:
        lead, _, rest = c["text"].partition(" — ")
        if not rest:
            lead, rest = "", c["text"]
        indent = scratch.textlength(lead + "  ", font=f_lead) if lead else 0
        entries.append((lead, indent, wrap(rest, f_text, wrap_w, indent)))

    line_h = 27
    legend_h = margin
    if spec.get("title"):
        legend_h += 38
    for _, _, lines in entries:
        legend_h += line_h * len(lines) + 14
    legend_h += margin - 14

    # A gutter of background around the shot, so a badge pinned to a control at the very edge of
    # the window has somewhere to sit instead of being shoved on top of its neighbour.
    r = f_badge.size + 6
    gut = spec.get("gutter", r + 8)

    canvas = Image.new("RGB", (shot.width + 2 * gut, shot.height + 2 * gut + legend_h), BG)
    canvas.paste(shot, (gut, gut))
    d = ImageDraw.Draw(canvas, "RGBA")
    shot_bottom = shot.height + 2 * gut

    # --- boxes and badges on the screenshot ----------------------------------------------------
    for i, c in enumerate(callouts, 1):
        x, y, w, h = place(resolve(c["target"], rects))
        grow = c.get("grow", 5)
        box = (gut + x - grow, gut + y - grow, w + 2 * grow, h + 2 * grow)
        rounded(d, box, c.get("radius", 8), outline=ACCENT, width=3)

        at = c.get("badge", "top-left")
        left, right = box[0], box[0] + box[2]
        top, bottom = box[1], box[1] + box[3]
        bx = left if "left" in at else right if "right" in at else (left + right) // 2
        by = top if "top" in at else bottom if "bottom" in at else (top + bottom) // 2
        nx, ny = c.get("nudge", (0, 0))
        bx, by = bx + nx, by + ny
        bx = min(max(r, bx), canvas.width - r)
        by = min(max(r, by), shot_bottom - r)
        d.ellipse([bx - r, by - r, bx + r, by + r], fill=ACCENT, outline=BG, width=3)
        n = str(i)
        d.text((bx - scratch.textlength(n, font=f_badge) / 2, by - f_badge.size * 0.72),
               n, font=f_badge, fill=(255, 255, 255))

    # --- legend --------------------------------------------------------------------------------
    y = shot_bottom + margin
    d.line([(0, shot_bottom), (canvas.width, shot_bottom)], fill=LINE, width=2)

    if spec.get("title"):
        d.text((margin, y), spec["title"], font=f_title, fill=TEXT0)
        y += 38

    for i, (lead, indent, lines) in enumerate(entries, 1):
        r = 15
        d.ellipse([margin, y + 2, margin + 2 * r, y + 2 + 2 * r], fill=ACCENT)
        n = str(i)
        d.text((margin + r - scratch.textlength(n, font=f_badge) / 2, y + 2 + r - f_badge.size * 0.72),
               n, font=f_badge, fill=(255, 255, 255))

        if lead:
            d.text((text_x, y), lead, font=f_lead, fill=TEXT0)
        for j, ln in enumerate(lines):
            d.text((text_x + (indent if j == 0 else 0), y), ln, font=f_text, fill=TEXT1)
            y += line_h
        y += 14

    out = (spec_path.parent / spec["out"]).resolve()
    canvas.save(out)
    print(f"{out}  {canvas.width}x{canvas.height}  ({len(callouts)} callouts)")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "docs/figures/overview.json")
