from pathlib import Path
from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "ReExtractor.Gui" / "Assets"
ASSETS.mkdir(parents=True, exist_ok=True)

S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# Compact Windows-app silhouette.
d.rounded_rectangle((60, 60, 964, 964), radius=210, fill="#101720", outline="#2d719a", width=28)
d.rounded_rectangle((92, 92, 932, 932), radius=180, outline="#17394e", width=10)

# Isometric resource package.
top = [(245, 350), (506, 205), (775, 350), (510, 500)]
left = [(245, 350), (510, 500), (510, 770), (245, 615)]
right = [(510, 500), (775, 350), (775, 615), (510, 770)]
d.polygon(top, fill="#56d8f4")
d.polygon(left, fill="#178bc7")
d.polygon(right, fill="#075e99")
d.line(top + [top[0]], fill="#b8f4ff", width=18, joint="curve")
d.line([(245, 350), (510, 500), (775, 350)], fill="#d0f8ff", width=14, joint="curve")
d.line([(510, 500), (510, 770)], fill="#093e63", width=14)

# Open/package seam and extraction arrow.
d.line([(510, 500), (650, 422)], fill="#0f4e77", width=22)
d.rounded_rectangle((600, 620, 848, 746), radius=58, fill="#ff9d35", outline="#ffd19a", width=14)
d.polygon([(824, 574), (948, 684), (824, 794)], fill="#ff9d35", outline="#ffd19a")
d.line([(824, 588), (930, 684), (824, 780)], fill="#ffd19a", width=14, joint="curve")

# Small path-list marks remain readable at taskbar sizes.
for y in (428, 490, 552):
    d.rounded_rectangle((315, y, 400, y + 28), radius=14, fill="#d7f8ff")

png_path = ASSETS / "AppIcon.png"
ico_path = ASSETS / "AppIcon.ico"
img.save(png_path, optimize=True)
img.save(ico_path, format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print(png_path)
print(ico_path)
