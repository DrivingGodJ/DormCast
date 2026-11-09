"""Render the project's simple radio mark; no external assets or network fonts."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1] / "src" / "DormCast" / "wwwroot"
for name, size in [("apple-touch-icon.png", 180), ("icon-192.png", 192), ("icon-512.png", 512), ("favicon.png", 64)]:
    scale = 4
    image = Image.new("RGB", (size * scale, size * scale), "#101612")
    draw = ImageDraw.Draw(image)
    unit = size * scale / 100
    def bounds(a, b, c, d): return tuple(round(v * unit) for v in (a, b, c, d))
    green = "#c9f37f"
    for radius in (22, 37):
        box = bounds(50-radius, 50-radius, 50+radius, 50+radius)
        for start, end in [(-55, 55), (125, 235)]:
            draw.arc(box, start, end, fill=green, width=max(1, round(4.5 * unit)))
    draw.ellipse(bounds(44, 44, 56, 56), fill=green)
    image.resize((size, size), Image.Resampling.LANCZOS).save(root / name, optimize=True)
print("Created 180/192/512px home-screen icons and 64px favicon")
