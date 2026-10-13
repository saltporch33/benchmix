"""
Draws Benchmix's icon (Daniel, 2026-10-12: "please give benchmix shortcut a simple graphical icon so
i know what i'm clicking in windows") and writes src/Benchmix.App/Benchmix.ico.

A blue tile in the app's accent colour with a white waveform on it: two songs' worth of bars, the
second in the yellow of a tempo ramp. Each size is drawn on its own, at four times the size and
scaled down, so the small ones are not a blurred copy of the big one; at 16 and 24 px there are
fewer, wider bars so they still read as bars.

Run it from the repository root:  python scripts/make-icon.py
"""
from pathlib import Path
from PIL import Image, ImageDraw

ACCENT_TOP = (0x5C, 0x9A, 0xFF)
ACCENT_BOTTOM = (0x2C, 0x5F, 0xD8)
WHITE = (0xFF, 0xFF, 0xFF)
YELLOW = (0xFF, 0xEB, 0x3B)

BARS = [0.30, 0.55, 0.85, 0.62, 1.00, 0.48, 0.72, 0.38]   # heights, as a share of the room
SMALL_BARS = [0.45, 0.85, 1.00, 0.60, 0.80]


def tile(size: int) -> Image.Image:
    s = size * 4
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # The tile: a vertical gradient, clipped to a rounded square.
    gradient = Image.new("RGBA", (s, s))
    g = ImageDraw.Draw(gradient)
    for y in range(s):
        t = y / (s - 1)
        g.line([(0, y), (s, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(ACCENT_TOP, ACCENT_BOTTOM)) + (255,))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=round(s * 0.22), fill=255)
    img.paste(gradient, (0, 0), mask)

    # The waveform: bars centred on the middle line, the later ones in ramp yellow.
    heights = SMALL_BARS if size <= 24 else BARS
    n = len(heights)
    margin = s * (0.20 if size <= 24 else 0.18)
    room = s - 2 * margin
    pitch = room / n
    width = pitch * (0.62 if size <= 24 else 0.56)
    half = s * 0.33
    d = ImageDraw.Draw(img)
    for i, h in enumerate(heights):
        x0 = margin + i * pitch + (pitch - width) / 2
        colour = WHITE if i < (n + 1) // 2 else YELLOW
        d.rounded_rectangle([x0, s / 2 - half * h, x0 + width, s / 2 + half * h], radius=width / 2, fill=colour + (255,))

    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = [tile(n) for n in sizes]
    out = Path("src/Benchmix.App/Benchmix.ico")
    images[-1].save(out, format="ICO", sizes=[(n, n) for n in sizes], append_images=images[:-1])
    images[-1].save(Path("docs/figures/icon-256.png"))
    print(f"wrote {out} ({', '.join(map(str, sizes))} px)")


if __name__ == "__main__":
    main()
