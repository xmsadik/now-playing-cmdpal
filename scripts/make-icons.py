"""Regenerate the package icons in src/NowPlaying/Assets.

Usage: python scripts/make-icons.py   (needs Pillow)

The mark is a purple rounded square with a white beamed music note.
Everything is drawn at 1024 px and downsampled, so small sizes stay smooth.
"""

from pathlib import Path

from PIL import Image, ImageDraw

ASSETS = Path(__file__).resolve().parent.parent / "src" / "NowPlaying" / "Assets"
BASE = 1024
TILE = (124, 58, 237, 255)  # violet
INK = (255, 255, 255, 255)


def icon(size: int) -> Image.Image:
    img = Image.new("RGBA", (BASE, BASE), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((0, 0, BASE - 1, BASE - 1), radius=int(BASE * 0.22), fill=TILE)

    # Two note heads joined by a beam: heads at the bottom, stems up, slanted beam on top.
    r = BASE * 0.095
    stem = BASE * 0.05
    lx, rx = BASE * 0.34, BASE * 0.68  # stem x (right edge of each head)
    ly, ry = BASE * 0.70, BASE * 0.62  # head centres (y)
    for x, y in ((lx, ly), (rx, ry)):
        d.ellipse((x - 2 * r, y - r * 0.8, x, y + r * 0.8), fill=INK)
    top_l, top_r = BASE * 0.27, BASE * 0.20
    d.rectangle((lx - stem, top_l, lx, ly), fill=INK)
    d.rectangle((rx - stem, top_r, rx, ry), fill=INK)
    beam = BASE * 0.11
    d.polygon(
        [(lx - stem, top_l), (rx, top_r), (rx, top_r + beam), (lx - stem, top_l + beam)],
        fill=INK,
    )

    return img.resize((size, size), Image.LANCZOS)


def centered(width: int, height: int, mark: int) -> Image.Image:
    img = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    img.alpha_composite(icon(mark), ((width - mark) // 2, (height - mark) // 2))
    return img


def main() -> None:
    squares = {
        "StoreLogo.png": 50,
        "LockScreenLogo.scale-200.png": 48,
        "Square44x44Logo.scale-200.png": 88,
        "Square44x44Logo.targetsize-24_altform-unplated.png": 24,
        "Square150x150Logo.scale-200.png": 300,
    }
    for name, size in squares.items():
        icon(size).save(ASSETS / name)

    centered(620, 300, 200).save(ASSETS / "Wide310x150Logo.scale-200.png")
    centered(1240, 600, 360).save(ASSETS / "SplashScreen.scale-200.png")
    print(f"Wrote {len(squares) + 2} icons to {ASSETS}")


if __name__ == "__main__":
    main()
