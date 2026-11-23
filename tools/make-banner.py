"""Generates the OpenFairway menu banner artwork (original art, drawn procedurally).

    python tools/make-banner.py mods/OfflineMenu/assets/banner.png
"""
import math
import random
import sys

from PIL import Image, ImageDraw, ImageFilter

W, H = 1152, 716
SCALE = 2  # draw large, shrink for smooth edges


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def gradient(size, stops):
    """Vertical gradient through (position, colour) stops."""
    w, h = size
    img = Image.new('RGB', size)
    px = img.load()
    for y in range(h):
        t = y / (h - 1)
        for (p0, c0), (p1, c1) in zip(stops, stops[1:]):
            if p0 <= t <= p1:
                c = lerp(c0, c1, (t - p0) / (p1 - p0))
                break
        for x in range(w):
            px[x, y] = c
    return img


def hill(draw, w, h, base, amp, freq, phase, colour):
    points = [(0, h)]
    for x in range(0, w + 8, 8):
        y = base + amp * math.sin(x / w * math.pi * freq + phase) + amp * 0.35 * math.sin(x / w * math.pi * freq * 2.3 + phase * 1.7)
        points.append((x, y))
    points.append((w, h))
    draw.polygon(points, fill=colour)


def main(path):
    w, h = W * SCALE, H * SCALE
    random.seed(7)

    # Dusk sky: deep navy down to a warm horizon.
    img = gradient((w, h), [
        (0.00, (9, 14, 34)),
        (0.38, (22, 40, 78)),
        (0.58, (88, 78, 112)),
        (0.70, (214, 132, 92)),
        (1.00, (240, 176, 110)),
    ])

    # Low sun with a soft glow.
    glow = Image.new('RGB', (w, h), (0, 0, 0))
    gd = ImageDraw.Draw(glow)
    sun = (int(w * 0.70), int(h * 0.66))
    for radius, value in ((int(h * 0.55), 26), (int(h * 0.36), 44), (int(h * 0.20), 70)):
        gd.ellipse([sun[0] - radius, sun[1] - radius, sun[0] + radius, sun[1] + radius], fill=(value, int(value * 0.72), int(value * 0.45)))
    glow = glow.filter(ImageFilter.GaussianBlur(h * 0.08))
    img = Image.composite(img, img, Image.new('L', (w, h), 255))
    img = Image.blend(img, Image.eval(Image.merge('RGB', [Image.eval(c, lambda v: v) for c in img.split()]), lambda v: v), 0)
    img = Image.fromarray(__import__('numpy').clip(__import__('numpy').asarray(img, dtype='int16') + __import__('numpy').asarray(glow, dtype='int16'), 0, 255).astype('uint8'))
    draw = ImageDraw.Draw(img)
    r = int(h * 0.075)
    draw.ellipse([sun[0] - r, sun[1] - r, sun[0] + r, sun[1] + r], fill=(255, 226, 170))

    # A few stars in the upper sky.
    for _ in range(90):
        x, y = random.randrange(w), random.randrange(int(h * 0.42))
        s = random.choice((1, 1, 2)) * SCALE // 2 + 1
        v = random.randrange(120, 220)
        draw.ellipse([x, y, x + s, y + s], fill=(v, v, min(255, v + 25)))

    # Rolling fairways, far to near.
    hill(draw, w, h, h * 0.72, h * 0.030, 2.2, 0.6, (38, 62, 78))
    hill(draw, w, h, h * 0.77, h * 0.040, 1.7, 2.1, (30, 78, 70))
    hill(draw, w, h, h * 0.83, h * 0.050, 1.3, 4.0, (28, 98, 66))
    hill(draw, w, h, h * 0.91, h * 0.045, 1.1, 1.2, (22, 78, 52))

    # Mown stripes on the nearest fairway.
    stripes = Image.new('RGBA', (w, h), (0, 0, 0, 0))
    sd = ImageDraw.Draw(stripes)
    for i in range(-6, 26):
        x0 = i * w * 0.06
        sd.polygon([(x0, h), (x0 + w * 0.03, h), (x0 + w * 0.27, h * 0.84), (x0 + w * 0.255, h * 0.84)], fill=(255, 255, 255, 14))
    img.paste(stripes, (0, 0), stripes)
    draw = ImageDraw.Draw(img)

    # The green with its flag, on the middle hill.
    gx, gy = int(w * 0.27), int(h * 0.80)
    draw.ellipse([gx - w * 0.085, gy - h * 0.018, gx + w * 0.085, gy + h * 0.022], fill=(52, 138, 84))
    pole_top = gy - int(h * 0.20)
    draw.rectangle([gx - SCALE, pole_top, gx + SCALE, gy], fill=(236, 236, 230))
    draw.polygon([(gx + SCALE, pole_top), (gx + int(w * 0.045), pole_top + int(h * 0.028)), (gx + SCALE, pole_top + int(h * 0.056))], fill=(226, 64, 58))
    draw.ellipse([gx - 5 * SCALE, gy - 2 * SCALE, gx + 5 * SCALE, gy + 2 * SCALE], fill=(14, 40, 30))

    # Darken the top and bottom so the menu's white text stays readable over the art.
    shade = Image.new('L', (w, h), 0)
    sp = shade.load()
    for y in range(h):
        t = y / (h - 1)
        top = max(0.0, 1 - t / 0.30) * 90
        bottom = max(0.0, (t - 0.42) / 0.58) ** 1.2 * 150
        v = int(min(255, top + bottom))
        for x in range(w):
            sp[x, y] = v
    img = Image.composite(Image.new('RGB', (w, h), (6, 10, 22)), img, shade)

    img.resize((W, H), Image.LANCZOS).save(path, optimize=True)
    print('wrote', path)


if __name__ == '__main__':
    main(sys.argv[1])
