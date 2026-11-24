"""Renders the launcher's golf hole as a turning 3D scan of a real one: a par 4 in Orlando, Florida, built from
public-domain data (USGS 3DEP lidar for the shape, trees included, and a USDA NAIP aerial photo for the colour).
The hole is not named anywhere; it is just a piece of real ground.

    pip install moderngl numpy pillow scipy rasterio laspy[lazrs] requests
    python tools/render-hole.py src/OpenFairway.Launcher/Assets/hole.bin [--frames 240] [--fps 20] [--size 760x540] [--blur 3] [--preview dir]

The first run downloads about 150 MB of lidar tiles and a photo, and keeps a 0.5 m surface model and the photo in
third_party/hole-scan (not committed). The output is the file the launcher embeds: "OFH2", frame count, width,
height, frames per second (int32 each), then per frame an int32 length and a JPEG, drawn on the launcher's ink colour.
Each frame averages a few moments of the turn (--blur), as a camera shutter would, so the turn looks smooth.
"""
import io
import math
import struct
import sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import moderngl
import numpy as np
import requests
from PIL import Image, ImageFilter
from scipy import ndimage

REPO = Path(__file__).resolve().parent.parent
CACHE = REPO / 'third_party' / 'hole-scan' / 'scan.npz'
INK = (10, 10, 20)

# The piece of ground: centre, the square that is downloaded, and the circle that is shown.
CENTRE = (28.46498, -81.51055)      # latitude, longitude
SQUARE = 300.0                      # metres
CELL = 0.5                          # metres per surface sample (the photo is twice as fine)
SHOW_CENTRE = (0.08 * SQUARE, 0.0)  # where the circle sits relative to the square's centre: metres east, north
RADIUS = 104.0                      # metres
UP = 1.0                            # heights as surveyed
CROWN = 0.55                        # tree crowns, relative to their surveyed height

EPT = 'https://s3-us-west-2.amazonaws.com/usgs-lidar-public/FL_Peninsular_FDEM_Orange_2018/'
STAC = 'https://planetarycomputer.microsoft.com/api/stac/v1/search'
TOKEN = 'https://planetarycomputer.microsoft.com/api/sas/v1/token/naip'
UTM = 'EPSG:26917'


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


def prepare():
    """The height field, its occlusion, a water mask, and the graded photo."""
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
    canopy = np.maximum(surface - ground, 0)
    crowns = canopy > 1.5
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
    return surface, occlusion, ndimage.gaussian_filter(wet.astype(float), 1.5), np.clip(rgb, 0, 1), water_colour


def mesh(surface, occlusion, wet):
    n = surface.shape[0]
    half = SQUARE / 2
    xs = (np.arange(n) + 0.5) * CELL - half - SHOW_CENTRE[0]
    zs = (np.arange(n) + 0.5) * CELL - half + SHOW_CENTRE[1]  # rows run north to south: +z is south
    X, Z = np.meshgrid(xs, zs)
    base = np.percentile(surface, 5)
    Y = (surface - base) * UP
    gz, gx = np.gradient(Y, CELL)
    normal = np.stack([-gx, np.ones_like(gx), -gz], -1)
    normal /= np.linalg.norm(normal, axis=-1, keepdims=True)
    U = (X + SHOW_CENTRE[0] + half) / SQUARE
    V = (Z - SHOW_CENTRE[1] + half) / SQUARE
    inside = np.hypot(X, Z) <= RADIUS * 1.03
    verts = np.concatenate([np.stack([X, Y, Z], -1), normal, np.stack([U, V], -1), occlusion[..., None], wet[..., None]], -1)
    ids = np.full((n, n), -1)
    ids[inside] = np.arange(inside.sum())
    a, b, c, d = ids[:-1, :-1], ids[:-1, 1:], ids[1:, :-1], ids[1:, 1:]
    ok = (a >= 0) & (b >= 0) & (c >= 0) & (d >= 0)
    tris = np.concatenate([np.stack([a[ok], c[ok], b[ok]], -1), np.stack([b[ok], c[ok], d[ok]], -1)])
    return verts[inside].astype('f4'), tris.astype('i4')


# ---- rendering -------------------------------------------------------------------------------------------------

VERTEX = """
#version 330
uniform mat4 u_mvp;
in vec3 in_pos; in vec3 in_nrm; in vec2 in_uv; in float in_occ; in float in_wet;
out vec3 v_pos; out vec3 v_nrm; out vec2 v_uv; out float v_occ; out float v_wet;
void main() {
    v_pos = in_pos; v_nrm = in_nrm; v_uv = in_uv; v_occ = in_occ; v_wet = in_wet;
    gl_Position = u_mvp * vec4(in_pos, 1.0);
}
"""

FRAGMENT = """
#version 330
uniform sampler2D u_photo;
uniform vec3 u_sun;      // towards the sun, in the ground's own frame (it matches the shadows in the photo)
uniform vec3 u_eye;
uniform float u_radius;
uniform float u_phase;
uniform vec3 u_ink;
uniform vec3 u_water;
in vec3 v_pos; in vec3 v_nrm; in vec2 v_uv; in float v_occ; in float v_wet;
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

void main() {
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
    float flat_light = max(u_sun.y, 0.0);
    float relief = max(dot(n, u_sun), 0.0) - flat_light;
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
    // A thin morning haze over distance.
    float far = clamp((length(u_eye - v_pos) - 260.0) / 260.0, 0.0, 1.0);
    c = mix(c, vec3(0.42, 0.47, 0.55) * 0.55, far * 0.18);
    // The edge of the scan dissolves into the background, a little unevenly, like the edge of real capture data.
    float r = length(v_pos.xz) / u_radius;
    float a = atan(v_pos.z, v_pos.x);
    float ragged = 0.035 * noise(vec2(a * 5.0, 1.3)) + 0.015 * noise(vec2(a * 13.0, 4.1)) + 0.012 * noise(v_pos.xz * 0.3);
    float fade = smoothstep(0.50, 0.97, r + ragged * 0.6);
    fade = fade * fade * (3.0 - 2.0 * fade);  // a long, gentle falloff: the ground emerges from the dark
    c = mix(c, pow(u_ink, vec3(2.2)), fade);
    f_colour = vec4(pow(clamp(c, 0.0, 1.0), vec3(1.0 / 2.2)), 1.0);
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


def main():
    out = sys.argv[1]
    frames = int(sys.argv[sys.argv.index('--frames') + 1]) if '--frames' in sys.argv else 240
    fps = int(sys.argv[sys.argv.index('--fps') + 1]) if '--fps' in sys.argv else 20
    blur = int(sys.argv[sys.argv.index('--blur') + 1]) if '--blur' in sys.argv else 3
    size = sys.argv[sys.argv.index('--size') + 1] if '--size' in sys.argv else '760x540'
    preview = sys.argv[sys.argv.index('--preview') + 1] if '--preview' in sys.argv else None
    only = [int(x) for x in sys.argv[sys.argv.index('--only') + 1].split(',')] if '--only' in sys.argv else None
    w, h = (int(v) for v in size.split('x'))
    W, H = w * 2, h * 2

    if not CACHE.exists():
        print('downloading the scan (lidar and photo) ...')
        fetch_scan()
    surface, occlusion, wet, rgb, water_colour = prepare()
    verts, tris = mesh(surface, occlusion, wet)

    ctx = moderngl.create_standalone_context(require=330)
    ctx.enable(moderngl.DEPTH_TEST)
    prog = ctx.program(vertex_shader=VERTEX, fragment_shader=FRAGMENT)
    vao = ctx.vertex_array(prog, [(ctx.buffer(verts.tobytes()), '3f 3f 2f 1f 1f', 'in_pos', 'in_nrm', 'in_uv', 'in_occ', 'in_wet')],
                           ctx.buffer(tris.tobytes()))
    photo = ctx.texture((rgb.shape[1], rgb.shape[0]), 3, (rgb * 255).astype(np.uint8).tobytes())
    photo.build_mipmaps()
    photo.filter = (moderngl.LINEAR_MIPMAP_LINEAR, moderngl.LINEAR)
    photo.anisotropy = 16.0
    photo.repeat_x = photo.repeat_y = False
    fbo = ctx.framebuffer([ctx.renderbuffer((W, H), samples=8)], ctx.depth_renderbuffer((W, H), samples=8))
    resolved = ctx.framebuffer([ctx.renderbuffer((W, H))])

    # The morning sun of the photo: from the south-east, about 38 degrees up (+x east, +z south).
    sun = np.array([0.55, 0.62, 0.56])
    sun /= np.linalg.norm(sun)
    prog['u_sun'].value = tuple(sun)
    prog['u_radius'].value = RADIUS
    prog['u_ink'].value = tuple(np.array(INK) / 255.0)
    prog['u_photo'].value = 0
    prog['u_water'].value = tuple(float(v) for v in water_colour)
    eye0 = np.array([0.0, 278.0, 194.0])  # a drone's view, about 55 degrees down
    view = look_at(eye0, np.array([0.0, -6.0, 6.0]))  # aimed a little short of the centre, so the near edge clears the frame
    proj = perspective(math.radians(30), W / H, 10, 1200)

    packed = []
    for f in range(frames):
        if only and f not in only:
            continue
        total = np.zeros((H, W, 3), np.float64)
        for k in range(blur):
            # Moments spread over half the frame's time, centred on it (a half-open shutter).
            t = f + ((k + 0.5) / blur - 0.5) * 0.5 if blur > 1 else f
            turn = 2 * math.pi * t / frames
            rot = np.array([[math.cos(turn), 0, math.sin(turn), 0], [0, 1, 0, 0], [-math.sin(turn), 0, math.cos(turn), 0], [0, 0, 0, 1]])
            prog['u_mvp'].write((proj @ view @ rot).T.astype('f4').tobytes())
            prog['u_eye'].value = tuple((np.linalg.inv(rot) @ np.append(eye0, 1))[:3])
            prog['u_phase'].value = 2 * math.pi * 4 * t / frames  # the water's ripples repeat exactly once per turn
            fbo.use()
            fbo.clear(*(np.array(INK) / 255.0), 1.0)
            photo.use(0)
            vao.render()
            ctx.copy_framebuffer(resolved, fbo)
            # Averaged in linear light, as a shutter would.
            total += (np.frombuffer(resolved.read(components=3), np.uint8).reshape(H, W, 3) / 255.0) ** 2.2
        pixels = np.clip((total / blur) ** (1 / 2.2) * 255 + 0.5, 0, 255).astype(np.uint8)
        image = Image.fromarray(pixels[::-1])
        image = image.resize((w, h), Image.LANCZOS).filter(ImageFilter.UnsharpMask(radius=1.1, percent=55, threshold=2))
        buffer = io.BytesIO()
        image.save(buffer, 'JPEG', quality=82, optimize=True)
        packed.append(buffer.getvalue())
        if preview:
            image.save(f'{preview}/hole_{f:03d}.png')
    with open(out, 'wb') as fh:
        fh.write(b'OFH2' + struct.pack('<iiii', len(packed), w, h, fps))
        for data in packed:
            fh.write(struct.pack('<i', len(data)) + data)
    print(f'wrote {out}: {len(packed)} frames, {sum(map(len, packed)) / 1e6:.1f} MB')


if __name__ == '__main__':
    main()
