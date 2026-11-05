"""Android's small notification icon: the DealGo mark as a white silhouette.

Android draws the status-bar icon from the picture's transparency alone, so a full-colour icon
turns into a white square. This cuts the mark out of the launcher's foreground (made by
make_icons.py) and writes it white-on-transparent at every density.

    python tool/make_notification_icon.py
"""
from pathlib import Path

from PIL import Image

RES = Path(__file__).resolve().parent.parent / "android" / "app" / "src" / "main" / "res"
SIZES = {"mdpi": 24, "hdpi": 36, "xhdpi": 48, "xxhdpi": 72, "xxxhdpi": 96}

source = Image.open(RES / "mipmap-xxhdpi" / "ic_launcher_foreground.png").convert("RGBA")
alpha = source.split()[3]
mark = alpha.crop(alpha.getbbox())

# Square, with a little room around the mark as Android's icon grid expects (20dp in 24dp).
side = int(max(mark.size) * 24 / 20)
square = Image.new("L", (side, side), 0)
square.paste(mark, ((side - mark.width) // 2, (side - mark.height) // 2))

for density, size in SIZES.items():
    out = RES / f"drawable-{density}"
    out.mkdir(exist_ok=True)
    a = square.resize((size, size), Image.LANCZOS)
    icon = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    icon.putalpha(a)
    icon.save(out / "ic_stat_notify.png")
    print(out / "ic_stat_notify.png")
