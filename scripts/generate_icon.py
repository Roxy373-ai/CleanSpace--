from pathlib import Path

from PIL import Image, ImageChops


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "cleanspace" / "assets"
ASSETS.mkdir(parents=True, exist_ok=True)

source = Image.open(ASSETS / "cleanspace-source.jpg").convert("RGB")
white = Image.new("RGB", source.size, "white")
difference = ImageChops.difference(source, white).convert("L")
difference = difference.point(lambda value: 255 if value > 18 else 0)
bounds = difference.getbbox() or (0, 0, source.width, source.height)
left, top, right, bottom = bounds
padding = max(right - left, bottom - top) // 16
left, top = max(0, left - padding), max(0, top - padding)
right, bottom = min(source.width, right + padding), min(source.height, bottom + padding)
cropped = source.crop((left, top, right, bottom))

side = max(cropped.size)
square = Image.new("RGB", (side, side), "white")
square.paste(cropped, ((side - cropped.width) // 2, (side - cropped.height) // 2))
master = square.resize((256, 256), Image.Resampling.LANCZOS)
master.save(ASSETS / "cleanspace.png")
master.save(
    ASSETS / "cleanspace.ico",
    format="ICO",
    sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
)
print(ASSETS / "cleanspace.ico")
