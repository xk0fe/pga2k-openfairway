"""Renders the launcher's golf hole as a turning 3D scan of a real one, a par 4 in Orlando, Florida, drawn on its own
blueprint. In the middle is the scan, from public-domain data (USGS 3DEP lidar for the shape, trees included, and a
USDA NAIP aerial photo for the colour). Outward it dissolves into a plan of the course on the same ground and turning
with it: OpenStreetMap's fairways, greens, bunkers, water, paths and hole routings, the lidar's contour lines, and a
symbol for every tree. The course is not named anywhere.

    pip install moderngl numpy pillow scipy rasterio laspy[lazrs] requests shapely
    python tools/render-hole.py src/OpenFairway.Launcher/Assets/hole.scene
    python tools/render-hole.py frames.bin [--frames 300] [--fps 15] [--size 760x540] [--quality 68] [--blur 3] [--preview dir]

The first run downloads about 150 MB of lidar tiles, a photo and the map, and keeps them in third_party/hole-scan (not
committed).

A .scene output is what the launcher embeds and draws live with OpenGL (LiveScene.cs reads it): the camera and the
light, these shaders, the ground's grids, the photo, the drawing, and a still for when it cannot draw live. Any other
output is a strip of pre-rendered frames ("OFH2", frame count, width, height, frames per second, then each frame as a
length and a JPEG), for videos; each frame averages a few moments of the turn (--blur), as a camera shutter would.

Frames and the still sit in the launcher window at ORIGIN, on its blueprint paper. Their edges are that paper and its
grid, pixel for pixel (MainForm.Sheet in the launcher draws the same), so they join the window without a seam.
"""
import io
import json
import math
import struct
import sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import moderngl
import numpy as np
import requests
from PIL import Image, ImageDraw, ImageFilter, ImageFont
from scipy import ndimage

REPO = Path(__file__).resolve().parent.parent
CACHE = REPO / 'third_party' / 'hole-scan' / 'scan.npz'
MAP = REPO / 'third_party' / 'hole-scan' / 'osm.json'

# The blueprint sheet, shared with the launcher (src/OpenFairway.Launcher/MainForm.cs, Sheet).
PAPER = (13, 38, 70)
GRID_MINOR = (19, 46, 82)
GRID_MAJOR = (26, 56, 95)
GRID = 24                  # pixels between grid lines; every fifth is a major line
ORIGIN = (256, 30)         # where the frame's top-left corner sits in the launcher window
LINE = (206, 226, 255)     # blueprint ink

# The piece of ground: centre, the square that is downloaded, and the circle that is shown.
CENTRE = (28.46498, -81.51055)      # latitude, longitude
SQUARE = 300.0                      # metres
CELL = 0.5                          # metres per surface sample (the photo is twice as fine)
SHOW_CENTRE = (0.08 * SQUARE, 0.0)  # where the circle sits relative to the square's centre: metres east, north
RADIUS = 104.0                      # metres
SCAN_TO_PLAN = (0.86, 0.92)         # where the scan turns into the drawing, as a fraction of RADIUS
OUTER = 122.0                       # metres: the surveyed ground ends here, inside the square; the drawing goes on
UP = 1.0                            # heights as surveyed
CROWN = 0.55                        # tree crowns, relative to their surveyed height
PLAN_HALF = 380.0                   # metres around the circle's centre that the drawing covers
PLAN_PX = 4096                      # its texture (drawn at twice the size, then reduced, for clean lines)

# The camera and the light, shared with the launcher through the scene file.
SUN = (0.55, 0.62, 0.56)            # the morning sun of the photo: from the south-east, about 38 degrees up
EYE = (0.0, 315.0, 221.0)           # a drone's view, about 55 degrees down
TARGET = (0.0, -6.0, 6.0)
FOV_DEGREES, NEAR, FAR = 30.0, 10.0, 1200.0
FRAME = (760, 540)                  # pixels, in the launcher window at ORIGIN
SUPERSAMPLE = 2
EDGE_FADE = 70.0                    # pixels over which the drawing fades out at the frame's edges
TURN_SECONDS = 40.0

EPT = 'https://s3-us-west-2.amazonaws.com/usgs-lidar-public/FL_Peninsular_FDEM_Orange_2018/'
STAC = 'https://planetarycomputer.microsoft.com/api/stac/v1/search'
TOKEN = 'https://planetarycomputer.microsoft.com/api/sas/v1/token/naip'
OVERPASS = 'https://overpass-api.de/api/interpreter'
UTM = 'EPSG:26917'


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


# ---- data ------------------------------------------------------------------------------------------------------

def fetch_scan():
    """Lidar points to a highest-return surface model on a 0.5 m grid, and the aerial photo on a 0.25 m grid."""
    import laspy
    import rasterio
    from rasterio.transform import from_origin
    from rasterio.warp import Resampling, reproject, transform

    ept = requests.get(EPT + 'ept.json', timeout=60).json()
    srs = 'EPSG:' + str(ept['srs']['horizontal'])
    cx, cy = (v[0] for v in transform('EPSG:4326', UTM, [CENTRE[1]], [CENTRE[0]]))
    half = SQUARE / 2
    xs, ys = transform(UTM, srs, [cx - half - 5, cx + half + 5], [cy - half - 5, cy + half + 5])
    box = [min(xs), min(ys), max(xs), max(ys)]

    bounds = ept['bounds']
    size0 = bounds[3] - bounds[0]
    keys, pending = [], [requests.get(EPT + 'ept-hierarchy/0-0-0-0.json', timeout=60).json()]
    while pending:
        for key, count in pending.pop().items():
            d, x, y, _ = (int(v) for v in key.split('-'))
            size = size0 / 2 ** d
            x0, y0 = bounds[0] + x * size, bounds[1] + y * size
            if x0 > box[2] or x0 + size < box[0] or y0 > box[3] or y0 + size < box[1]:
                continue
            if count == -1:
                pending.append(requests.get(EPT + f'ept-hierarchy/{key}.json', timeout=60).json())
            elif count > 0:
                keys.append(key)

    def tile(key):
        las = laspy.read(io.BytesIO(requests.get(EPT + f'ept-data/{key}.laz', timeout=120).content))
        return np.stack([np.asarray(las.x), np.asarray(las.y), np.asarray(las.z), np.asarray(las.classification, dtype=np.float64)], 1)

    with ThreadPoolExecutor(8) as pool:
        pts = np.concatenate(list(pool.map(tile, keys)))
    ux, uy = (np.array(v) for v in transform(srs, UTM, pts[:, 0].tolist(), pts[:, 1].tolist()))
    keep = (np.abs(ux - cx) < half) & (np.abs(uy - cy) < half) & (pts[:, 3] != 7) & (pts[:, 3] != 18)
    ux, uy, z, cls = ux[keep], uy[keep], pts[keep, 2], pts[keep, 3]
    n = int(SQUARE / CELL)
    idx = (((cy + half) - uy) / CELL).astype(int).clip(0, n - 1) * n + ((ux - (cx - half)) / CELL).astype(int).clip(0, n - 1)
    surface = np.full(n * n, -np.inf)
    np.maximum.at(surface, idx, z)
    water = np.zeros(n * n)
    np.add.at(water, idx[cls == 9], 1)

    bbox_lon, bbox_lat = transform(UTM, 'EPSG:4326', [cx - half - 20, cx + half + 20], [cy - half - 20, cy + half + 20])
    bbox = [min(bbox_lon), min(bbox_lat), max(bbox_lon), max(bbox_lat)]
    items = requests.post(STAC, json={'collections': ['naip'], 'bbox': bbox, 'limit': 20}, timeout=60).json()['features']
    token = requests.get(TOKEN, timeout=30).json()['token']
    photo = None
    for item in sorted(items, key=lambda f: f['properties'].get('datetime') or '', reverse=True):
        with rasterio.open(item['assets']['image']['href'] + '?' + token) as src:
            dst = np.zeros((4, n * 2, n * 2), np.uint8)
            reproject(rasterio.band(src, [1, 2, 3, 4]), dst, dst_transform=from_origin(cx - half, cy + half, CELL / 2, CELL / 2),
                      dst_crs=UTM, resampling=Resampling.lanczos)
            if (dst[0] > 0).mean() > 0.98:
                photo = dst
                break
    CACHE.parent.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(CACHE, surface=surface.reshape(n, n), water=water.reshape(n, n), photo=photo)


def fetch_map():
    """The course from OpenStreetMap (ODbL): its golf features, water, paths, roads and buildings around the hole."""
    dlat, dlon = 450 / 110540, 450 / (111320 * math.cos(math.radians(CENTRE[0])))
    s, w, n, e = CENTRE[0] - dlat, CENTRE[1] - dlon, CENTRE[0] + dlat, CENTRE[1] + dlon
    query = f"""[out:json][timeout:90];
(
  way["golf"]({s},{w},{n},{e});
  way["natural"~"water|sand|wood|scrub"]({s},{w},{n},{e});
  way["water"]({s},{w},{n},{e});
  way["building"]({s},{w},{n},{e});
  way["highway"]({s},{w},{n},{e});
);
out geom;"""
    reply = requests.post(OVERPASS, data={'data': query}, headers={'User-Agent': 'course-plan-render/1.0'}, timeout=120)
    reply.raise_for_status()
    MAP.parent.mkdir(parents=True, exist_ok=True)
    MAP.write_text(json.dumps(reply.json()))


def prepare():
    """The height field and the bare ground under it, its occlusion, a water mask, the graded photo, and the trees."""
    d = np.load(CACHE)
    surface, water_hits, photo = d['surface'], d['water'], d['photo']
    n = surface.shape[0]
    empty = ~np.isfinite(surface)
    surface = surface[tuple(ndimage.distance_transform_edt(empty, return_distances=False, return_indices=True))]

    # Water: dark, non-vegetated pixels in the photo, or lidar water returns; ponds are flat.
    red, nir = photo[0].astype(float), photo[3].astype(float)
    ndvi = (nir - red) / (nir + red + 1e-6)
    bright = photo[:3].astype(float).mean(0)
    wet_px = (ndvi < 0.05) & (bright < 75)
    wet = wet_px.reshape(n, 2, n, 2).mean((1, 3)) > 0.5
    wet |= water_hits > 0
    wet = ndimage.binary_opening(ndimage.binary_closing(wet, iterations=2), iterations=2)
    labels, count = ndimage.label(wet)
    sizes = ndimage.sum(wet, labels, range(1, count + 1))
    wet = np.isin(labels, [i + 1 for i, s in enumerate(sizes) if s > 400])
    wet = ndimage.binary_fill_holes(wet)  # a fountain's bright spray is still pond
    if wet.any():
        ring = ndimage.binary_dilation(wet, iterations=4) & ~ndimage.binary_dilation(wet, iterations=1)
        level = np.percentile(surface[ring], 15) - 0.25
        surface = np.where(wet, level, surface)
        surface = np.where(ndimage.binary_dilation(wet, iterations=1) & ~wet, np.minimum(surface, level + 0.15), surface)

    # Trees: lidar records a crown as a sheer wall from its edge to the ground. Real crowns are domes, so each
    # crown's height grows with the distance from its edge.
    ground = ndimage.gaussian_filter(ndimage.grey_opening(surface, size=(31, 31)), 6)
    # Roofs: well above the ground and smooth, where tree crowns are rough.
    rough = np.sqrt(ndimage.gaussian_filter((surface - ndimage.gaussian_filter(surface, 1.0)) ** 2, 2.0))
    roofs = ndimage.binary_opening((surface - ground > 2.2) & (rough < 0.2), iterations=2)
    labels, count = ndimage.label(roofs)
    sizes = ndimage.sum(roofs, labels, range(1, count + 1))
    roofs = np.isin(labels, [i + 1 for i, s in enumerate(sizes) if s > 160])
    canopy = np.maximum(surface - ground, 0)
    crowns = canopy > 1.5
    # The trees for the drawing: one symbol at each crown's top, as wide as the crown is there.
    tops = (ndimage.maximum_filter(ndimage.gaussian_filter(canopy, 2), size=11) == ndimage.gaussian_filter(canopy, 2)) & (canopy > 4)
    reach = ndimage.distance_transform_edt(crowns) * CELL
    rows, cols = np.nonzero(tops)
    trees = [((c + 0.5) * CELL - SQUARE / 2, (r + 0.5) * CELL - SQUARE / 2, float(np.clip(reach[r, c], 1.5, 7.0))) for r, c in zip(rows, cols)]
    # Close the gaps between lidar returns and soften the tops, so crowns read as broad canopies of leaves.
    canopy = np.where(crowns, ndimage.gaussian_filter(ndimage.grey_closing(canopy, size=(5, 5)), 1.3), 0)
    inset = ndimage.distance_transform_edt(crowns) * CELL
    t = np.clip(inset / 4.5, 0, 1)
    edge = np.sqrt(1 - (1 - t) ** 2)  # a round shoulder over the outer 4.5 m of each crown
    # Leaves: sunlit clumps stand a little proud of the dark gaps between them, which the photo shows.
    lum = (photo[:3].astype(float).mean(0) / 255.0).reshape(n, 2, n, 2).mean((1, 3))
    clumps = (lum - ndimage.gaussian_filter(lum, 3)) * 9.0 * np.clip(inset / 1.5, 0, 1)
    # Crowns at a little over half their surveyed height: the photo only saw them from above, and lower crowns
    # keep it reading as a photograph that moves rather than as modelled trees.
    shaped = ndimage.gaussian_filter(canopy * CROWN * edge, 0.6) + np.where(crowns, clumps, 0)
    surface = np.where(wet, surface, ground + np.where(crowns, np.maximum(shaped, 0.3), np.minimum(surface - ground, 1.0)))
    # A light smoothing removes lidar speckle without losing the shape of the ground.
    surface = ndimage.gaussian_filter(surface, 0.6)
    # Occlusion: how far a point sits below its surroundings (under canopies, in hollows).
    around = ndimage.gaussian_filter(surface, 8)
    occlusion = np.clip(1.0 - np.maximum(around - surface, 0) * 0.09, 0.45, 1.0)
    near = ndimage.gaussian_filter(surface, 2.5)
    occlusion *= np.clip(1.0 - np.maximum(near - surface, 0) * 0.12, 0.6, 1.0)

    # The photo, graded like a photograph: one black and white point for all channels (so the colours stay true),
    # a gentle S-curve, and barely more colour.
    rgb = np.transpose(photo[:3], (1, 2, 0)).astype(np.float32) / 255.0
    lo, hi = np.percentile(rgb, 0.3), np.percentile(rgb, 99.8)
    rgb = np.clip((rgb - lo) / (hi - lo), 0, 1)
    rgb = rgb + 0.10 * (rgb - 0.5) * (1 - np.abs(2 * rgb - 1))
    grey = rgb.mean(2, keepdims=True)
    rgb = grey + (rgb - grey) * 1.03
    # The ponds' own colour, from the photo, away from their banks.
    core = ndimage.binary_erosion(wet, iterations=6)
    core_px = np.repeat(np.repeat(core, 2, 0), 2, 1)
    water_colour = np.median(rgb[core_px], axis=0) if core_px.any() else np.array([0.12, 0.16, 0.14])
    return {
        'surface': surface, 'ground': ndimage.gaussian_filter(ground, 1.5), 'occlusion': occlusion,
        'wet': ndimage.gaussian_filter(wet.astype(float), 1.5), 'rgb': np.clip(rgb, 0, 1), 'water': water_colour, 'trees': trees,
        'roofs': roofs,
    }


# ---- the plan ---------------------------------------------------------------------------------------------------

_ways = None


def map_ways():
    """The map's ways on the ground's own frame (x east, z south, metres from the circle's centre): a list of
    (points, closed, tags)."""
    global _ways
    if _ways is None:
        from rasterio.warp import transform
        cx, cy = (v[0] for v in transform('EPSG:4326', UTM, [CENTRE[1]], [CENTRE[0]]))
        sx, sy = cx + SHOW_CENTRE[0], cy + SHOW_CENTRE[1]
        _ways = []
        for element in json.loads(MAP.read_text())['elements']:
            geometry = element.get('geometry', [])
            if element.get('type') != 'way' or len(geometry) < 2:
                continue
            xs, ys = transform('EPSG:4326', UTM, [p['lon'] for p in geometry], [p['lat'] for p in geometry])
            points = [(x - sx, sy - y) for x, y in zip(xs, ys)]
            _ways.append((points, len(points) > 3 and geometry[0] == geometry[-1], element.get('tags', {})))
    return _ways


def plan_texture(trees):
    """The course drawn as a plan on the ground's own frame, as line intensities 0 to 1."""
    from shapely.geometry import LineString, Point, Polygon
    from shapely.ops import substring
    from shapely.prepared import prep

    size = PLAN_PX * 2
    k = size / (2 * PLAN_HALF)  # pixels per metre
    image = Image.new('L', (size, size), 0)
    draw = ImageDraw.Draw(image)
    rng = np.random.default_rng(3)
    try:
        font = 'C:/Windows/Fonts/bahnschrift.ttf'
        ImageFont.truetype(font, 10)
    except OSError:
        font = 'C:/Windows/Fonts/segoeui.ttf'

    def px(p):
        return ((p[0] + PLAN_HALF) * k, (p[1] + PLAN_HALF) * k)

    def stroke(coords, width, value):
        if len(coords) > 1:
            draw.line([px(p) for p in coords], fill=int(value * 255), width=max(1, round(width * k)), joint='curve')

    def outline(poly, width, value):
        stroke(list(poly.exterior.coords), width, value)
        for ring in poly.interiors:
            stroke(list(ring.coords), width, value)

    def dashed(line, width, value, dash, gap):
        at = 0.0
        while at < line.length:
            stroke(list(substring(line, at, min(at + dash, line.length)).coords), width, value)
            at += dash + gap

    def hatch(poly, spacing, angle, width, value):
        minx, miny, maxx, maxy = poly.bounds
        mx, my, reach = (minx + maxx) / 2, (miny + maxy) / 2, math.hypot(maxx - minx, maxy - miny)
        dx, dy = math.cos(math.radians(angle)), math.sin(math.radians(angle))
        offset = -reach
        while offset <= reach:
            ox, oy = mx - dy * offset, my + dx * offset
            cut = LineString([(ox - dx * reach, oy - dy * reach), (ox + dx * reach, oy + dy * reach)]).intersection(poly)
            for part in getattr(cut, 'geoms', [cut]):
                if part.geom_type == 'LineString' and part.length > 0:
                    stroke(list(part.coords), width, value)
            offset += spacing

    def stipple(poly, per_m2, radius, value):
        inside = prep(poly)
        minx, miny, maxx, maxy = poly.bounds
        want, placed = int(poly.area * per_m2), 0
        for x, y in rng.uniform((minx, miny), (maxx, maxy), size=(max(1, want) * 4, 2)):
            if placed >= want:
                break
            if inside.contains(Point(x, y)):
                cx_, cy_ = px((x, y))
                r = radius * k
                draw.ellipse((cx_ - r, cy_ - r, cx_ + r, cy_ + r), fill=int(value * 255))
                placed += 1

    def circle(centre, radius, width, value):
        cx_, cy_ = px(centre)
        r = radius * k
        draw.ellipse((cx_ - r, cy_ - r, cx_ + r, cy_ + r), outline=int(value * 255), width=max(1, round(width * k)))

    def label(centre, text, height, value):
        draw.text(px(centre), text, fill=int(value * 255), font=ImageFont.truetype(font, max(8, round(height * k))), anchor='mm')

    fills, outlines, lines, holes = [], [], [], []
    for coords, closed, tags in map_ways():
        poly = Polygon(coords).buffer(0) if closed else None
        golf, natural = tags.get('golf'), tags.get('natural')
        if golf == 'hole':
            holes.append((coords, tags))
        elif poly is not None and poly.area > 1 and golf in ('fairway', 'rough', 'driving_range'):
            if golf == 'fairway':
                fills.append(('fairway', poly))
            outlines.append((poly, 0.5 if golf == 'fairway' else 0.25, 1.0 if golf == 'fairway' else 0.5))
        elif poly is not None and poly.area > 1 and golf == 'green':
            fills.append(('green', poly))
            outlines.append((poly, 0.6, 1.0))
            inner = poly.buffer(-1.1)
            if not inner.is_empty and inner.geom_type == 'Polygon':
                outlines.append((inner, 0.18, 0.55))
        elif poly is not None and poly.area > 1 and golf == 'tee':
            outlines.append((poly, 0.3, 0.85))
        elif poly is not None and poly.area > 1 and (golf == 'bunker' or natural == 'sand'):
            fills.append(('stipple', poly))
            outlines.append((poly, 0.3, 0.85))
        elif poly is not None and poly.area > 1 and (natural == 'water' or 'water' in tags or golf in ('water_hazard', 'lateral_water_hazard')):
            fills.append(('water', poly))
            outlines.append((poly, 0.4, 0.95))
        elif poly is not None and poly.area > 1 and 'building' in tags:
            fills.append(('building', poly))
            outlines.append((poly, 0.3, 0.6))
        elif poly is not None and poly.area > 1 and natural in ('scrub', 'wood'):
            lines.append(('dashed', LineString(list(poly.exterior.coords)), 0.25, 0.45, 2.0, 1.5))
        elif golf == 'cartpath' or tags.get('highway') in ('path', 'footway', 'service', 'cycleway'):
            lines.append(('dashed', LineString(coords), 0.25, 0.55, 3.0, 2.0))
        elif 'highway' in tags:
            road = LineString(coords).buffer(4.0, cap_style=2)
            for part in getattr(road, 'geoms', [road]):
                outlines.append((part, 0.2, 0.4))

    # Fills first (hatching, sand), then the lines over them.
    for kind, poly in fills:
        for part in getattr(poly, 'geoms', [poly]):
            if kind == 'stipple':
                stipple(part, 0.45, 0.12, 0.6)
            elif kind == 'fairway':
                hatch(part, 5.0, 62, 0.16, 0.16)   # broad, faint stripes: the mowing
            elif kind == 'green':
                stipple(part, 1.2, 0.07, 0.35)
            elif kind == 'water':
                hatch(part, 2.2, 0, 0.14, 0.32)
                inner = part.buffer(-1.4)
                for ring in getattr(inner, 'geoms', [inner]):
                    if not ring.is_empty:
                        outline(ring, 0.15, 0.45)
            else:
                hatch(part, 1.6, 45, 0.12, 0.25)
    # Trees from the lidar, where it was surveyed: a circle the size of the crown and a dot at its top.
    for tx, tz, r in trees:
        x, z = tx - SHOW_CENTRE[0], tz + SHOW_CENTRE[1]
        if math.hypot(x, z) < OUTER - 2:
            circle((x, z), r, 0.18, 0.5)
            circle((x, z), 0.25, 0.25, 0.6)
    for poly, width, value in outlines:
        for part in getattr(poly, 'geoms', [poly]):
            if part.geom_type == 'Polygon':
                outline(part, width, value)
    for _, line, width, value, dash, gap in lines:
        dashed(line, width, value, dash, gap)
    # Each hole's routing: a dashed line from the tee to the green, its number in a circle behind the tee.
    for coords, tags in holes:
        line = LineString(coords)
        dashed(line, 0.3, 0.62, 4.0, 3.0)
        circle(coords[0], 1.8, 0.3, 0.8)
        circle(coords[-1], 1.2, 0.3, 0.8)
        back = np.array(coords[0]) - np.array(coords[1])
        back /= max(np.linalg.norm(back), 1e-6)
        at = tuple(np.array(coords[0]) + back * 13)
        circle(at, 6.2, 0.32, 0.95)
        label(at, tags.get('ref', ''), 6.0, 1.0)
        if 'par' in tags:
            label((at[0], at[1] + 9.6), 'PAR ' + tags['par'], 2.6, 0.8)

    plan = np.asarray(image.resize((PLAN_PX, PLAN_PX), Image.LANCZOS), np.float32) / 255.0
    # The drawing fades out before the edge of the mapped area.
    yy, xx = np.mgrid[0:PLAN_PX, 0:PLAN_PX]
    r = np.hypot((xx + 0.5) / PLAN_PX * 2 - 1, (yy + 0.5) / PLAN_PX * 2 - 1) * PLAN_HALF
    plan *= 1 - smoothstep(PLAN_HALF - 70, PLAN_HALF - 15, r)
    return plan


def grids(d):
    """The surveyed ground on its 0.5 m grid: height (the scan's surface in the middle, flattening to the bare ground
    where it turns into the drawing, and down to the plane at its edge), where the drawing takes over, the bare
    ground's height for contour lines, occlusion and water. The launcher builds the same mesh from these."""
    surface, ground, occlusion, wet = d['surface'], d['ground'], d['occlusion'], d['wet']
    n = surface.shape[0]
    half = SQUARE / 2
    xs = (np.arange(n) + 0.5) * CELL - half - SHOW_CENTRE[0]
    zs = (np.arange(n) + 0.5) * CELL - half + SHOW_CENTRE[1]  # rows run north to south: +z is south
    X, Z = np.meshgrid(xs, zs)
    base = np.percentile(surface, 5)
    r = np.hypot(X, Z)
    # The scan's edge is uneven, like the edge of real capture data: a smooth random field moves it in and out.
    rng = np.random.default_rng(7)
    ragged = ndimage.gaussian_filter(rng.standard_normal((n, n)), 16)
    fine = ndimage.gaussian_filter(rng.standard_normal((n, n)), 4)
    edge = r / RADIUS + 0.07 * ragged / ragged.std() + 0.02 * fine / fine.std()
    plan = smoothstep(*SCAN_TO_PLAN, edge)
    # The trees settle into their plan symbols just before the colour turns into the drawing.
    flat = smoothstep(SCAN_TO_PLAN[0] - 0.06, SCAN_TO_PLAN[1] - 0.03, edge)
    # Houses and streets beside the course belong to the drawing, not the scan: only the course is photographed.
    mask = Image.new('L', (n, n), 0)
    pen = ImageDraw.Draw(mask)

    def cell(p):
        return ((p[0] + SHOW_CENTRE[0] + half) / CELL, (p[1] - SHOW_CENTRE[1] + half) / CELL)

    for points, closed, tags in map_ways():
        if 'building' in tags and closed:
            pen.polygon([cell(p) for p in points], fill=255)
        elif tags.get('highway') in ('residential', 'unclassified', 'tertiary', 'secondary', 'primary'):
            pen.line([cell(p) for p in points], fill=255, width=round(9 / CELL))
    built = np.maximum(np.asarray(mask, np.float32) / 255, d['roofs'].astype(np.float32))  # the map's, and the lidar's
    built = ndimage.maximum_filter(built, size=round(6 / CELL))  # a margin round each
    built = ndimage.gaussian_filter(built, 3)
    plan = np.maximum(plan, smoothstep(0.35, 0.65, built))
    flat = np.maximum(flat, smoothstep(0.15, 0.5, built))
    height = surface * (1 - flat) + ground * flat
    Y = (height - base) * UP * (1 - smoothstep(OUTER - 16, OUTER, r))
    return {'Y': Y, 'plan': plan, 'ground': ground - base, 'occlusion': occlusion, 'wet': wet}


def mesh(g):
    """The grids as vertices and triangles, plus the plane the drawing goes on beyond the survey."""
    Y, plan, ground, occlusion, wet = g['Y'], g['plan'], g['ground'], g['occlusion'], g['wet']
    n = Y.shape[0]
    half = SQUARE / 2
    xs = (np.arange(n) + 0.5) * CELL - half - SHOW_CENTRE[0]
    zs = (np.arange(n) + 0.5) * CELL - half + SHOW_CENTRE[1]
    X, Z = np.meshgrid(xs, zs)
    r = np.hypot(X, Z)
    gz, gx = np.gradient(Y, CELL)
    normal = np.stack([-gx, np.ones_like(gx), -gz], -1)
    normal /= np.linalg.norm(normal, axis=-1, keepdims=True)
    U = (X + SHOW_CENTRE[0] + half) / SQUARE
    V = (Z - SHOW_CENTRE[1] + half) / SQUARE
    inside = r <= OUTER
    verts = np.concatenate([np.stack([X, Y, Z], -1), normal, np.stack([U, V], -1), occlusion[..., None], wet[..., None],
                            plan[..., None], ground[..., None]], -1)
    ids = np.full((n, n), -1)
    ids[inside] = np.arange(inside.sum())
    a, b, c, e = ids[:-1, :-1], ids[:-1, 1:], ids[1:, :-1], ids[1:, 1:]
    ok = (a >= 0) & (b >= 0) & (c >= 0) & (e >= 0)
    tris = np.concatenate([np.stack([a[ok], c[ok], b[ok]], -1), np.stack([b[ok], c[ok], e[ok]], -1)])
    verts = verts[inside]
    # Beyond the survey the drawing goes on, on a plane under the lowest ground (the ponds sit below the rest).
    far, below = 1500.0, float(verts[:, 1].min()) - 0.3
    plane = np.array([[x, below, z, 0, 1, 0, 0, 0, 1, 0, 1, -999] for x, z in ((-far, -far), (far, -far), (-far, far), (far, far))])
    start = len(verts)
    tris = np.concatenate([tris, np.array([[start, start + 2, start + 1], [start + 1, start + 2, start + 3]])])
    return np.concatenate([verts, plane]).astype('f4'), tris.astype('i4')


# ---- rendering -------------------------------------------------------------------------------------------------

VERTEX = """
#version 330
uniform mat4 u_mvp;
in vec3 in_pos; in vec3 in_nrm; in vec2 in_uv; in float in_occ; in float in_wet; in float in_plan; in float in_ground;
out vec3 v_pos; out vec3 v_nrm; out vec2 v_uv; out float v_occ; out float v_wet; out float v_plan; out float v_ground;
void main() {
    v_pos = in_pos; v_nrm = in_nrm; v_uv = in_uv; v_occ = in_occ; v_wet = in_wet; v_plan = in_plan; v_ground = in_ground;
    gl_Position = u_mvp * vec4(in_pos, 1.0);
}
"""

FRAGMENT = """
#version 330
uniform sampler2D u_photo;
uniform sampler2D u_map;
uniform vec3 u_sun;      // towards the sun, in the ground's own frame (it matches the shadows in the photo)
uniform vec3 u_eye;
uniform float u_phase;
uniform vec3 u_paper;    // the launcher's blueprint paper, linear
uniform vec3 u_line;     // blueprint ink, linear
uniform vec3 u_water;
uniform float u_map_half;
uniform vec2 u_size;     // the frame, in render pixels
uniform float u_edge;    // the drawing fades out over this many pixels at the frame's edges
uniform float u_outer;
uniform float u_live;    // 1 in the launcher, drawing over its sheet
in vec3 v_pos; in vec3 v_nrm; in vec2 v_uv; in float v_occ; in float v_wet; in float v_plan; in float v_ground;
out vec4 f_colour;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float noise(vec2 p) {
    vec2 i = floor(p), f = fract(p), u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

// Wind on the water: layers of noise, each drifting round a small circle, so the surface is irregular like real
// ripples and still comes back to where it started once per turn.
float waves(vec2 p) {
    vec2 o1 = vec2(cos(u_phase), sin(u_phase)) * 0.9;
    vec2 o2 = vec2(cos(1.7 - 2.0 * u_phase), sin(1.7 - 2.0 * u_phase)) * 0.6;
    return 0.65 * noise(p * 0.32 + o1) + 0.35 * noise(p * 0.85 + o2);  // waves of 1 to 3 m: larger than a pixel
}

// A contour line every `step` metres, about `width` pixels wide whatever the slope or the distance.
float contour(float h, float step, float width) {
    float t = h / step;
    float d = abs(fract(t + 0.5) - 0.5) / max(fwidth(t), 1e-5);
    return 1.0 - smoothstep(width * 0.5, width * 0.5 + 1.0, d);
}

vec3 scan() {
    vec3 n = normalize(v_nrm);
    vec3 view = normalize(u_eye - v_pos);
    vec3 albedo = pow(texture(u_photo, v_uv).rgb, vec3(2.2));
    // The sides of crowns: the photo only saw them from above, so take their colour from just inside the crown
    // and break it up like foliage, instead of stretching the edge pixels down to the ground.
    float steep = 1.0 - smoothstep(0.30, 0.78, n.y);
    vec2 inward = -normalize(n.xz + vec2(1e-4)) * (1.6 / 300.0);
    vec3 side = pow(textureLod(u_photo, v_uv + inward, 1.2).rgb, vec3(2.2));
    float leaf = 0.82 + 0.36 * noise(v_pos.xz * 1.9 + vec2(v_pos.y * 2.7)) * noise(v_pos.zx * 4.3 - vec2(v_pos.y * 3.1));
    albedo = mix(albedo, side * leaf, steep);
    // The photo already holds the morning's light and shadows. Add only what a flat photo cannot show: the
    // relief of slopes and tree crowns relative to flat ground, and occlusion under the canopy.
    float relief = max(dot(n, u_sun), 0.0) - max(u_sun.y, 0.0);
    vec3 c = albedo * (1.04 + 0.85 * relief) * mix(0.70, 1.0, v_occ);
    c *= mix(0.86, 1.0, smoothstep(0.15, 0.75, n.y));   // crowns are a little darker underneath
    float wet = smoothstep(0.35, 0.65, v_wet);
    if (wet > 0.0) {
        vec3 land = c;
        // Water: the sky in it at grazing angles, and a glint of the sun that ripples.
        const float e = 0.25;
        float dx = waves(v_pos.xz + vec2(e, 0.0)) - waves(v_pos.xz - vec2(e, 0.0));
        float dz = waves(v_pos.xz + vec2(0.0, e)) - waves(v_pos.xz - vec2(0.0, e));
        vec3 wn = normalize(vec3(-dx * 0.10 / e, 1.0, -dz * 0.10 / e));
        float fres = 0.02 + 0.98 * pow(1.0 - max(dot(wn, view), 0.0), 5.0);
        // One even body colour (the pond's own, from the photo), so the surface reads as calm, deep water.
        vec3 body = pow(u_water, vec3(2.2)) * vec3(0.80, 0.90, 0.85);
        vec3 sky = mix(vec3(0.34, 0.42, 0.55), vec3(0.70, 0.78, 0.88), clamp(1.0 - view.y, 0.0, 1.0));
        vec3 water = mix(body, sky, 0.02 + fres * 0.45);
        vec3 h = normalize(u_sun + view);
        float facing = max(dot(wn, h), 0.0);
        water += vec3(1.0, 0.96, 0.88) * (0.08 * pow(facing, 60.0) + 0.45 * pow(facing, 1400.0));
        c = mix(land, water, wet);
    }
    // A photographer's grade: warm light, slightly cooler shade.
    float luma = dot(c, vec3(0.2126, 0.7152, 0.0722));
    c *= mix(vec3(0.96, 0.98, 1.03), vec3(1.04, 1.01, 0.95), smoothstep(0.02, 0.25, luma));
    return c;
}

const float INK_STRENGTH = 0.82;
const float CONTOUR_STEP = 1.0, CONTOUR_INDEX_STEP = 5.0;
const float TRACE_STRENGTH = 0.5;

float ink() {
    float amount = texture(u_map, (v_pos.xz + u_map_half) / (2.0 * u_map_half)).r;
    if (v_ground > -100.0) {
        float iso = max(contour(v_ground, CONTOUR_STEP, 1.0) * 0.16, contour(v_ground, CONTOUR_INDEX_STEP, 1.6) * 0.32);
        amount = max(amount, iso * (1.0 - smoothstep(u_outer - 20.0, u_outer - 3.0, length(v_pos.xz))));
    }
    vec2 fc = gl_FragCoord.xy;
    float frameEdge = min(min(fc.x, u_size.x - fc.x), min(fc.y, u_size.y - fc.y));
    return clamp(amount, 0.0, 1.0) * smoothstep(0.0, 1.0, frameEdge / u_edge) * INK_STRENGTH;
}

vec3 srgb(vec3 c) { return pow(clamp(c, 0.0, 1.0), vec3(1.0 / 2.2)); }

void main() {
    float plan = v_plan;
    // Both are always worked out: the contour lines need screen derivatives, which branches would break.
    vec3 photo = scan();
    float drawn = ink();
    float trace = TRACE_STRENGTH * (1.0 - smoothstep(0.6, 1.8, abs(plan - 0.5) / max(fwidth(plan), 1e-5)));
    if (u_live < 0.5) {
        vec3 c = mix(photo, mix(u_paper, u_line, drawn), plan);
        f_colour = vec4(srgb(mix(c, u_line, trace)), 1.0 - plan);
    } else {
        // In the launcher the sheet (paper and grid) is already on screen: the photo covers it, the ink is laid over
        // it. Premultiplied, and the ink's cover chosen so it looks as it does mixed in linear light offline.
        vec3 paper = srgb(u_paper), line = srgb(u_line);
        vec3 inked = srgb(mix(u_paper, u_line, drawn));
        float cover = clamp(dot(inked - paper, line - paper) / dot(line - paper, line - paper), 0.0, 1.0);
        vec4 c = vec4(srgb(photo), 1.0) * (1.0 - plan) + vec4(line * cover, cover) * plan;
        f_colour = c * (1.0 - trace) + vec4(line, 1.0) * trace;
    }
}
"""


def perspective(fov, aspect, near, far):
    f = 1 / math.tan(fov / 2)
    return np.array([[f / aspect, 0, 0, 0], [0, f, 0, 0], [0, 0, (far + near) / (near - far), 2 * far * near / (near - far)], [0, 0, -1, 0]])


def look_at(eye, target):
    f = (target - eye) / np.linalg.norm(target - eye)
    s = np.cross(f, [0, 1, 0])
    s /= np.linalg.norm(s)
    u = np.cross(s, f)
    m = np.eye(4)
    m[0, :3], m[1, :3], m[2, :3] = s, u, -f
    m[:3, 3] = -m[:3, :3] @ eye
    return m


def sheet(w, h):
    """The launcher's blueprint paper and grid where the frame sits, pixel for pixel (MainForm.Sheet draws the same)."""
    ys, xs = np.mgrid[0:h, 0:w]
    gx, gy = xs + ORIGIN[0], ys + ORIGIN[1]
    paper = np.empty((h, w, 3), np.float32)
    paper[:] = PAPER
    minor = (gx % GRID == 0) | (gy % GRID == 0)
    major = (gx % (GRID * 5) == 0) | (gy % (GRID * 5) == 0)
    paper[minor] = GRID_MINOR
    paper[major] = GRID_MAJOR
    return paper


def write_scene(path, d, g, plan, still, plane_y, sun):
    """Everything the launcher needs to draw the hole live (MainForm.Scene.cs reads it): the camera and the light,
    the shaders, the ground's grids, the photo and the drawing, and a still for when it cannot draw live."""
    import zlib

    def deflate(data):
        packer = zlib.compressobj(9, zlib.DEFLATED, -15)   # raw deflate, as .NET's DeflateStream reads it
        return packer.compress(data) + packer.flush()

    def block(data):
        return struct.pack('<i', len(data)) + data

    def heights(a):
        lo, hi = float(a.min()), float(a.max())
        step = (hi - lo) / 65535 or 1.0
        return struct.pack('<ff', step, lo) + block(deflate(np.round((a - lo) / step).astype('<u2').tobytes()))

    def fractions(a):
        return block(deflate(np.clip(np.round(a * 255), 0, 255).astype(np.uint8).tobytes()))

    def encoded(image, kind, **options):
        buffer = io.BytesIO()
        image.save(buffer, kind, **options)
        return buffer.getvalue()

    rgb = d['rgb']
    n = g['Y'].shape[0]
    photo = encoded(Image.fromarray((rgb * 255 + 0.5).astype(np.uint8)), 'JPEG', quality=92)
    drawing = encoded(Image.fromarray((plan * 255 + 0.5).astype(np.uint8), 'L'), 'PNG', optimize=True)
    data = (b'OFS1' + struct.pack('<ii', 1, n)
            + struct.pack('<8f', CELL, SQUARE, SHOW_CENTRE[0], SHOW_CENTRE[1], RADIUS, OUTER, PLAN_HALF, plane_y)
            + struct.pack('<3f', *sun) + struct.pack('<3f', *d['water']) + struct.pack('<3f', *EYE) + struct.pack('<3f', *TARGET)
            + struct.pack('<5f', FOV_DEGREES, NEAR, FAR, EDGE_FADE * SUPERSAMPLE, TURN_SECONDS)
            + struct.pack('<5i', FRAME[0], FRAME[1], ORIGIN[0], ORIGIN[1], SUPERSAMPLE)
            + struct.pack('<6f', *(np.array(PAPER) / 255.0), *(np.array(LINE) / 255.0))
            + block(VERTEX.encode()) + block(FRAGMENT.encode())
            + heights(g['Y']) + heights(g['ground']) + fractions(g['occlusion']) + fractions(g['wet']) + fractions(g['plan'])
            + struct.pack('<ii', rgb.shape[1], rgb.shape[0]) + block(photo)
            + struct.pack('<i', PLAN_PX) + block(drawing)
            + block(still))
    Path(path).write_bytes(data)
    print(f'wrote {path}: {len(data) / 1e6:.1f} MB (photo {len(photo) / 1e6:.1f}, drawing {len(drawing) / 1e6:.1f}, still {len(still) / 1e3:.0f} KB)')


def main():
    out = sys.argv[1]
    frames = int(sys.argv[sys.argv.index('--frames') + 1]) if '--frames' in sys.argv else 300
    fps = int(sys.argv[sys.argv.index('--fps') + 1]) if '--fps' in sys.argv else 15  # one slow turn every 20 seconds
    blur = int(sys.argv[sys.argv.index('--blur') + 1]) if '--blur' in sys.argv else 3
    size = sys.argv[sys.argv.index('--size') + 1] if '--size' in sys.argv else f'{FRAME[0]}x{FRAME[1]}'
    quality = int(sys.argv[sys.argv.index('--quality') + 1]) if '--quality' in sys.argv else 68
    preview = sys.argv[sys.argv.index('--preview') + 1] if '--preview' in sys.argv else None
    only = [int(x) for x in sys.argv[sys.argv.index('--only') + 1].split(',')] if '--only' in sys.argv else None
    w, h = (int(v) for v in size.split('x'))
    W, H = w * SUPERSAMPLE, h * SUPERSAMPLE
    scene = out.endswith('.scene')
    if scene:
        only = [0]   # the still the launcher shows when it cannot draw live

    if not CACHE.exists():
        print('downloading the scan (lidar and photo) ...')
        fetch_scan()
    if not MAP.exists():
        print('downloading the map ...')
        fetch_map()
    d = prepare()
    g = grids(d)
    verts, tris = mesh(g)
    plan = plan_texture(d['trees'])

    ctx = moderngl.create_standalone_context(require=330)
    ctx.enable(moderngl.DEPTH_TEST)
    prog = ctx.program(vertex_shader=VERTEX, fragment_shader=FRAGMENT)
    vao = ctx.vertex_array(prog, [(ctx.buffer(verts.tobytes()), '3f 3f 2f 1f 1f 1f 1f', 'in_pos', 'in_nrm', 'in_uv', 'in_occ', 'in_wet', 'in_plan', 'in_ground')],
                           ctx.buffer(tris.tobytes()))
    rgb = d['rgb']
    photo = ctx.texture((rgb.shape[1], rgb.shape[0]), 3, (rgb * 255).astype(np.uint8).tobytes())
    plan_tex = ctx.texture((PLAN_PX, PLAN_PX), 1, (plan * 255 + 0.5).astype(np.uint8).tobytes())
    for texture in (photo, plan_tex):
        texture.build_mipmaps()
        texture.filter = (moderngl.LINEAR_MIPMAP_LINEAR, moderngl.LINEAR)
        texture.anisotropy = 16.0
        texture.repeat_x = texture.repeat_y = False
    fbo = ctx.framebuffer([ctx.renderbuffer((W, H), samples=8)], ctx.depth_renderbuffer((W, H), samples=8))
    resolved = ctx.framebuffer([ctx.renderbuffer((W, H))])

    sun = np.array(SUN)
    sun /= np.linalg.norm(sun)
    prog['u_sun'].value = tuple(sun)
    prog['u_paper'].value = tuple((np.array(PAPER) / 255.0) ** 2.2)
    prog['u_line'].value = tuple((np.array(LINE) / 255.0) ** 2.2)
    prog['u_photo'].value = 0
    prog['u_map'].value = 1
    prog['u_water'].value = tuple(float(v) for v in d['water'])
    prog['u_map_half'].value = PLAN_HALF
    prog['u_size'].value = (W, H)
    prog['u_edge'].value = EDGE_FADE * SUPERSAMPLE
    prog['u_outer'].value = OUTER
    prog['u_live'].value = 0.0
    eye0 = np.array(EYE)
    view = look_at(eye0, np.array(TARGET))
    proj = perspective(math.radians(FOV_DEGREES), W / H, NEAR, FAR)
    paper = sheet(w, h)
    clear = tuple(np.array(PAPER) / 255.0)

    packed = []
    for f in range(frames):
        if only and f not in only:
            continue
        total = np.zeros((H, W, 4), np.float64)
        for k in range(blur):
            # Moments spread over half the frame's time, centred on it (a half-open shutter).
            t = f + ((k + 0.5) / blur - 0.5) * 0.5 if blur > 1 else f
            turn = 2 * math.pi * t / frames
            rot = np.array([[math.cos(turn), 0, math.sin(turn), 0], [0, 1, 0, 0], [-math.sin(turn), 0, math.cos(turn), 0], [0, 0, 0, 1]])
            prog['u_mvp'].write((proj @ view @ rot).T.astype('f4').tobytes())
            prog['u_eye'].value = tuple((np.linalg.inv(rot) @ np.append(eye0, 1))[:3])
            prog['u_phase'].value = 2 * math.pi * 4 * t / frames  # the water's ripples repeat exactly once per turn
            fbo.use()
            fbo.clear(*clear, 0.0)
            photo.use(0)
            plan_tex.use(1)
            vao.render()
            ctx.copy_framebuffer(resolved, fbo)
            raw = np.frombuffer(resolved.read(components=4), np.uint8).reshape(H, W, 4) / 255.0
            # Colour averaged in linear light, as a shutter would; the scan's coverage as it is.
            total[..., :3] += raw[..., :3] ** 2.2
            total[..., 3] += raw[..., 3]
        colour = np.clip((total[..., :3] / blur) ** (1 / 2.2) * 255 + 0.5, 0, 255).astype(np.uint8)[::-1]
        cover = np.clip(total[..., 3] / blur * 255 + 0.5, 0, 255).astype(np.uint8)[::-1]
        image = Image.fromarray(colour).resize((w, h), Image.LANCZOS).filter(ImageFilter.UnsharpMask(radius=1.1, percent=55, threshold=2))
        scan = np.asarray(Image.fromarray(cover).resize((w, h), Image.LANCZOS), np.float32)[..., None] / 255.0
        # The sheet's grid, wherever the drawing shows, at exactly the launcher's pixels.
        pixels = np.asarray(image, np.float32)
        pixels = pixels + (paper - np.array(PAPER, np.float32)) * (1 - scan)
        image = Image.fromarray(np.clip(pixels + 0.5, 0, 255).astype(np.uint8))
        buffer = io.BytesIO()
        image.save(buffer, 'JPEG', quality=quality, optimize=True)
        packed.append(buffer.getvalue())
        if preview:
            image.save(f'{preview}/hole_{f:03d}.png')
    if scene:
        write_scene(out, d, g, plan, packed[0], float(verts[-1][1]), sun)
        return
    with open(out, 'wb') as fh:
        fh.write(b'OFH2' + struct.pack('<iiii', len(packed), w, h, fps))
        for data in packed:
            fh.write(struct.pack('<i', len(data)) + data)
    print(f'wrote {out}: {len(packed)} frames, {sum(map(len, packed)) / 1e6:.1f} MB')


if __name__ == '__main__':
    main()
