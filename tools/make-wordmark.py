"""Draws the OpenFairway wordmark: OPEN in white, FAIRWAY filled with a mowed-fairway texture (alternating
light and dark mowing stripes with fine grass grain). Original art, no game assets.

Usage: python make-wordmark.py <out.png> [height_px] [--stacked] [--shadow]
The output is a transparent PNG cropped to the lettering."""
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

FONT = r"C:\Windows\Fonts\seguibli.ttf"  # Segoe UI Black Italic, close to the game's heavy italic headings


def grass(w, h, stripe, angle_deg=-28, seed=3):
    """A mowed fairway seen from above: broad stripes alternating light and dark, grain along the stripes."""
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:w].astype(np.float32)
    a = np.deg2rad(angle_deg)
    u = x * np.cos(a) + y * np.sin(a)  # across the stripes
    v = -x * np.sin(a) + y * np.cos(a)  # along the stripes
    band = np.floor(u / stripe).astype(int) % 2
    # Soft edges between stripes, the way mower lines look.
    edge = np.abs(((u / stripe) % 1.0) - 0.5) * 2  # 1 at stripe centre .. 0 at edges
    light = np.array([132, 190, 84], np.float32)
    dark = np.array([86, 148, 60], np.float32)
    base = np.where(band[..., None] == 0, light, dark)
    base = base * (0.96 + 0.04 * edge[..., None])
    # Grass grain: noise stretched along the mowing direction.
    noise = rng.standard_normal((h, w)).astype(np.float32)
    grain = Image.fromarray(np.uint8(np.clip(noise * 40 + 128, 0, 255)))
    grain = grain.rotate(angle_deg, resample=Image.BILINEAR, expand=False)
    grain = grain.filter(ImageFilter.BoxBlur(1))
    g = (np.asarray(grain, np.float32) - 128) / 128
    base = base * (1 + 0.07 * g[..., None])
    # Sunlight falling from the top left.
    lightfall = 1.12 - 0.28 * (y / h) - 0.06 * (x / w)
    base = base * lightfall[..., None]
    return np.clip(base, 0, 255).astype(np.uint8)


def wordmark(height=160, stacked=False, open_color=(244, 246, 250), shadow=False):
    scale = 4
    size = int(height * scale * (0.5 if stacked else 1.0) * 1.25)
    font = ImageFont.truetype(FONT, size)
    words = ["OPEN", "FAIRWAY"]
    boxes = [font.getbbox(wd) for wd in words]
    gap = int(size * 0.02)
    if stacked:
        w = max(b[2] for b in boxes) + size
        h = int(size * 2.4)
        pos = [(int(size * 0.18), 0), (0, int(size * 1.02))]
    else:
        w = boxes[0][2] + gap + boxes[1][2] + size // 2
        h = int(size * 1.35)
        pos = [(0, 0), (boxes[0][2] + gap, 0)]
    canvas = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    # OPEN: flat white.
    d = ImageDraw.Draw(canvas)
    d.text(pos[0], words[0], font=font, fill=open_color + (255,))
    # FAIRWAY: grass texture through a text mask.
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).text(pos[1], words[1], font=font, fill=255)
    turf = Image.fromarray(grass(w, h, stripe=size * 0.62)).convert("RGBA")
    turf.putalpha(mask)
    # A thin lighter lip along the top of each letter, like the cut edge of the fairway.
    lip = mask.filter(ImageFilter.MaxFilter(1))
    shifted = Image.new("L", (w, h), 0)
    shifted.paste(mask, (0, int(size * 0.025)))
    edge = np.clip(np.asarray(mask, np.int16) - np.asarray(shifted, np.int16), 0, 255).astype(np.uint8)
    hi = Image.new("RGBA", (w, h), (196, 228, 150, 0))
    hi.putalpha(Image.fromarray(edge).point(lambda p: int(p * 0.0)))
    canvas = Image.alpha_composite(canvas, turf)
    canvas = Image.alpha_composite(canvas, hi)
    if shadow:
        # A soft dark shadow under both words keeps them readable on course photos.
        both = canvas.getchannel("A")
        blur = both.filter(ImageFilter.GaussianBlur(size * 0.05))
        offset = Image.new("L", (w, h), 0)
        offset.paste(blur, (int(size * 0.01), int(size * 0.035)))
        under = Image.new("RGBA", (w, h), (4, 6, 12, 0))
        under.putalpha(offset.point(lambda p: int(p * 0.85)))
        pad = int(size * 0.12)
        framed = Image.new("RGBA", (w + pad * 2, h + pad * 2), (0, 0, 0, 0))
        framed.alpha_composite(under, (pad, pad))
        framed.alpha_composite(canvas, (pad, pad))
        canvas = framed
    canvas = canvas.crop(canvas.getbbox())
    out = canvas.resize((max(1, canvas.width // scale), max(1, canvas.height // scale)), Image.LANCZOS)
    if out.height != height and not stacked and not shadow:
        out = out.resize((int(out.width * height / out.height), height), Image.LANCZOS)
    return out


if __name__ == "__main__":
    path = sys.argv[1]
    height = int(sys.argv[2]) if len(sys.argv) > 2 and sys.argv[2].isdigit() else 160
    stacked = "--stacked" in sys.argv
    wordmark(height, stacked, shadow="--shadow" in sys.argv).save(path)
    print("wrote", path)
