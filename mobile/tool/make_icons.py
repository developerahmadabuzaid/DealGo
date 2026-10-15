"""Draws the DealGo app icon for Android, iOS and the web.

The artwork is the same mark the app draws in lib/shared/brand.dart (_MarkPainter): three speed
lines and an arrow running into a black D and an orange G, on the same 162 x 68 box. It is redrawn
here with Pillow so the launcher icons are made from that one definition rather than a second
drawing of it. Change the mark there, change it here, and run:

    python tool/make_icons.py

Everything is drawn at 8x and scaled down, which is what gives the edges their smoothing.
"""
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw

ROOT = Path(__file__).resolve().parent.parent

ORANGE = (0xF2, 0x6A, 0x1B, 255)
INK = (0x1C, 0x1F, 0x24, 255)
WHITE = (255, 255, 255, 255)

BOX_W, BOX_H = 162.0, 68.0
D_BOWL_X, G_RING_X, MID_Y = 62.0, 126.0, 34.0
OUTER_R, INNER_R = 29.0, 16.0
SS = 8  # supersampling factor


def draw_mark(width_px):
    """The mark alone on a transparent ground, `width_px` wide."""
    scale = width_px * SS / BOX_W
    w, h = round(BOX_W * scale), round(BOX_H * scale)
    img = Image.new('RGBA', (w, h), (0, 0, 0, 0))

    def s(v):
        return v * scale

    def mask():
        return Image.new('L', (w, h), 0)

    def rrect(d, x, y, rw, rh, r, fill=255):
        d.rounded_rectangle([s(x), s(y), s(x + rw), s(y + rh)], radius=s(r), fill=fill)

    def paint(m, colour):
        img.paste(Image.new('RGBA', (w, h), colour), (0, 0), m)

    # Speed lines, the shaft and the arrowhead.
    orange = mask()
    d = ImageDraw.Draw(orange)
    for x, y, rw in ((2, 19, 27), (0, 30.5, 19), (5, 42, 24)):
        rrect(d, x, y, rw, 7, 3.5)
    rrect(d, 16, 30.5, 42, 7, 3.5)
    d.polygon([(s(52), s(23.5)), (s(70), s(34)), (s(52), s(44.5))], fill=255)
    paint(orange, ORANGE)

    # The D: a stem with a half-round bowl, hollowed by the same shape one size down.
    def d_shape(stem_x, r):
        m = mask()
        dd = ImageDraw.Draw(m)
        dd.rectangle([s(stem_x), s(MID_Y - r), s(D_BOWL_X), s(MID_Y + r)], fill=255)
        dd.pieslice([s(D_BOWL_X - r), s(MID_Y - r), s(D_BOWL_X + r), s(MID_Y + r)], -90, 90, fill=255)
        return m

    letter_d = ImageChops.subtract(d_shape(D_BOWL_X - 20, OUTER_R), d_shape(D_BOWL_X - 7, INNER_R))
    paint(letter_d, INK)

    # The G: a ring, plus a crossbar clipped to the letter's own circle.
    def disc(r):
        m = mask()
        ImageDraw.Draw(m).ellipse([s(G_RING_X - r), s(MID_Y - r), s(G_RING_X + r), s(MID_Y + r)], fill=255)
        return m

    outer = disc(OUTER_R)
    ring = ImageChops.subtract(outer, disc(INNER_R))
    bar = mask()
    ImageDraw.Draw(bar).rectangle([s(G_RING_X), s(MID_Y - 6), s(G_RING_X + OUTER_R), s(MID_Y + 6)], fill=255)
    paint(ImageChops.lighter(ring, ImageChops.multiply(bar, outer)), ORANGE)

    return img.resize((round(w / SS), round(h / SS)), Image.LANCZOS)


def icon(size, mark_fraction, background=WHITE, rounded=False):
    """A square icon with the mark centred, `mark_fraction` of the width wide."""
    img = Image.new('RGBA', (size * SS, size * SS), (0, 0, 0, 0))
    if background is not None:
        d = ImageDraw.Draw(img)
        if rounded:
            d.rounded_rectangle([0, 0, size * SS - 1, size * SS - 1], radius=size * SS * 0.22, fill=background)
        else:
            d.rectangle([0, 0, size * SS, size * SS], fill=background)
    img = img.resize((size, size), Image.LANCZOS)

    mark = draw_mark(max(1, round(size * mark_fraction)))
    img.alpha_composite(mark, ((size - mark.width) // 2, (size - mark.height) // 2))
    return img


def save(img, path, opaque=False):
    path.parent.mkdir(parents=True, exist_ok=True)
    if opaque:
        # iOS rejects app icons with an alpha channel.
        flat = Image.new('RGB', img.size, (255, 255, 255))
        flat.paste(img, (0, 0), img)
        flat.save(path)
    else:
        img.save(path)
    print('wrote', path.relative_to(ROOT))


def main():
    res = ROOT / 'android/app/src/main/res'

    # Legacy launcher icon, for Android 7 and older: a white rounded square.
    for folder, size in (('mdpi', 48), ('hdpi', 72), ('xhdpi', 96), ('xxhdpi', 144), ('xxxhdpi', 192)):
        save(icon(size, 0.78, rounded=True), res / f'mipmap-{folder}/ic_launcher.png')

    # Adaptive icon foreground, 108dp. The launcher crops it to a circle or a squircle, and only the
    # middle 66dp is sure to show, so the wide mark is kept inside that.
    for folder, size in (('mdpi', 108), ('hdpi', 162), ('xhdpi', 216), ('xxhdpi', 324), ('xxxhdpi', 432)):
        save(icon(size, 0.58, background=None), res / f'mipmap-{folder}/ic_launcher_foreground.png')

    # iOS draws its own rounded corners, so the square is left square and opaque.
    ios = ROOT / 'ios/Runner/Assets.xcassets/AppIcon.appiconset'
    for name in sorted(p.name for p in ios.glob('Icon-App-*.png')):
        points, scale = name[len('Icon-App-'):-len('.png')].split('@')
        size = round(float(points.split('x')[0]) * int(scale.rstrip('x')))
        save(icon(size, 0.78), ios / name, opaque=True)

    # The web build and its installable-app icons. "Maskable" icons may be cropped to a circle, so
    # the mark sits inside the same safe zone as Android's.
    web = ROOT / 'web'
    save(icon(32, 0.84, rounded=True), web / 'favicon.png')
    save(icon(192, 0.78, rounded=True), web / 'icons/Icon-192.png')
    save(icon(512, 0.78, rounded=True), web / 'icons/Icon-512.png')
    save(icon(192, 0.58), web / 'icons/Icon-maskable-192.png')
    save(icon(512, 0.58), web / 'icons/Icon-maskable-512.png')


if __name__ == '__main__':
    main()
