"""Renders the launcher's golf hole: a small 3D diorama (a dogleg par 4 with mowing stripes, a pond inside the
dogleg, bunkers, the green with a flag, trees, and soil showing on the sides of the tile), turning once around,
as a loop of frames. Original art made from code; no game assets.

    pip install moderngl numpy pillow
    python render-hole.py <out.bin> [--frames 160] [--size 600x420] [--preview dir]

The output is one file the launcher embeds: "OFH1", frame count, width, height (int32 each), then per frame an
int32 length and a JPEG. Frames are drawn on the launcher's ink colour so they sit in it without an edge.
"""
import io
import math
import struct
import sys

import moderngl
import numpy as np
from PIL import Image

INK = (10, 10, 20)

# ---- the hole, in island units (the tile spans about -1..1 on x and z) ----------------------------------------

TEE = np.array([-0.56, 0.64])
GREEN = np.array([0.52, -0.50])
GREEN_R = np.array([0.17, 0.13])  # an ellipse, turned GREEN_ANGLE
GREEN_ANGLE = 0.5
PATH_CTRL = np.array([-0.50, -0.30])
FAIRWAY_HALF = 0.125
POND = np.array([0.14, 0.18, 0.21])  # x, z, radius
BUNKERS = [(0.29, -0.67, 0.085), (0.75, -0.29, 0.075), (-0.72, -0.14, 0.090), (0.06, -0.46, 0.060)]
WATER_LEVEL = -0.035


def bezier(t):
    t = np.asarray(t)[..., None]
    return (1 - t) ** 2 * TEE + 2 * (1 - t) * t * PATH_CTRL + t ** 2 * GREEN


PATH = bezier(np.linspace(0.0, 1.0, 48))


def path_distance(x, z):
    """Distance to the fairway's centre line and how far along it (0 at the tee, 1 at the green)."""
    p = np.stack([x, z], -1)[..., None, :]
    a, b = PATH[:-1], PATH[1:]
    ab = b - a
    t = np.clip(((p - a) * ab).sum(-1) / (ab * ab).sum(-1), 0, 1)
    d = np.linalg.norm(p - (a + ab * t[..., None]), axis=-1)
    i = d.argmin(-1)
    tt = (np.take_along_axis(t, i[..., None], -1)[..., 0] + i) / (len(PATH) - 1)
    return d.min(-1), tt


def wobble(angle, seed):
    return 1 + 0.12 * np.sin(3 * angle + seed) + 0.06 * np.sin(5 * angle + 2.1 * seed)


def blob(x, z, cx, cz, r, seed):
    """Normalised distance to a lumpy round shape: below 1 inside."""
    dx, dz = x - cx, z - cz
    return np.hypot(dx, dz) / (r * wobble(np.arctan2(dz, dx), seed))


def green_distance(x, z):
    dx, dz = x - GREEN[0], z - GREEN[1]
    c, s = math.cos(GREEN_ANGLE), math.sin(GREEN_ANGLE)
    u, v = c * dx + s * dz, -s * dx + c * dz
    return np.hypot(u / GREEN_R[0], v / GREEN_R[1])


def smooth(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


def boundary(angle):
    """The tile's outline: a rounded square with a slightly hand-cut edge."""
    p = 3.4
    r = 1.0 / (np.abs(np.cos(angle)) ** p + np.abs(np.sin(angle)) ** p) ** (1 / p)
    return r * (1 + 0.018 * np.sin(4 * angle + 0.7) + 0.012 * np.sin(9 * angle + 0.3))


def height(x, z, s=None):
    h = 0.030 * np.sin(1.9 * x + 0.4) * np.cos(1.5 * z - 0.2) + 0.016 * np.sin(3.3 * x - 2.1 * z + 1.0)
    h += 0.040 * smooth(1.7, 0.7, green_distance(x, z))
    tee = np.maximum(np.abs(x - TEE[0]) / 0.075, np.abs(z - TEE[1]) / 0.05)
    h = np.where(tee < 1.6, h * smooth(1.0, 1.6, tee) + 0.02 * smooth(1.6, 1.0, tee), h)
    for i, (bx, bz, br) in enumerate(BUNKERS):
        h -= 0.030 * smooth(1.0, 0.45, blob(x, z, bx, bz, br, i + 1))
    pond = blob(x, z, POND[0], POND[1], POND[2], 7)
    h = h * smooth(0.8, 1.3, pond) + (WATER_LEVEL - 0.06) * smooth(1.3, 0.8, pond)
    if s is not None:
        h -= 0.025 * smooth(0.965, 1.0, s)  # a soft lip at the edge of the tile
    return h


# ---- meshes ----------------------------------------------------------------------------------------------------

RINGS, SPOKES, WALL_STEPS, BOTTOM = 150, 420, 8, -0.32


def terrain_mesh():
    s = np.linspace(0, 1, RINGS + 1)
    a = np.linspace(0, 2 * np.pi, SPOKES, endpoint=False)
    S, A = np.meshgrid(s, a, indexing='ij')
    R = boundary(A) * S
    x, z = R * np.cos(A), R * np.sin(A)
    y = height(x, z, S)
    e = 0.004
    nx = -(height(x + e, z, S) - height(x - e, z, S)) / (2 * e)
    nz = -(height(x, z + e, S) - height(x, z - e, S)) / (2 * e)
    n = np.stack([nx, np.ones_like(nx), nz], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    verts = np.concatenate([np.stack([x, y, z], -1), n, np.zeros_like(x)[..., None], np.zeros(x.shape + (3,))], -1).reshape(-1, 10)

    def vid(i, j):
        return i * SPOKES + j % SPOKES

    tris = []
    for i in range(RINGS):
        for j in range(SPOKES):
            a0, a1, b0, b1 = vid(i, j), vid(i, j + 1), vid(i + 1, j), vid(i + 1, j + 1)
            tris += [(a0, b0, b1), (a0, b1, a1)]
    top = verts[:, :]

    # The sides: from the turf lip down to a rounded bottom, coloured as soil in the shader (kind 1).
    edge = top.reshape(RINGS + 1, SPOKES, 10)[-1]
    rows = []
    for k in range(WALL_STEPS + 1):
        f = k / WALL_STEPS
        inset = 0.05 * f ** 3
        ang = a
        r = boundary(ang) * (1 - inset)
        y = edge[:, 1] + (BOTTOM - edge[:, 1]) * f
        nrm = np.stack([np.cos(ang), np.full_like(ang, -0.6 * f ** 3), np.sin(ang)], -1)
        nrm /= np.linalg.norm(nrm, axis=-1, keepdims=True)
        rows.append(np.concatenate([np.stack([r * np.cos(ang), y, r * np.sin(ang)], -1), nrm, np.ones((SPOKES, 1)), np.stack([edge[:, 1], np.zeros(SPOKES), np.zeros(SPOKES)], -1)], -1))
    wall = np.concatenate(rows)
    base = len(top)
    for k in range(WALL_STEPS):
        for j in range(SPOKES):
            a0, a1 = base + k * SPOKES + j, base + k * SPOKES + (j + 1) % SPOKES
            b0, b1 = a0 + SPOKES, a1 + SPOKES
            tris += [(a0, a1, b1), (a0, b1, b0)]
    return np.concatenate([top, wall]).astype('f4'), np.array(tris, 'i4')


def icosphere(level):
    t = (1 + 5 ** 0.5) / 2
    v = [(-1, t, 0), (1, t, 0), (-1, -t, 0), (1, -t, 0), (0, -1, t), (0, 1, t), (0, -1, -t), (0, 1, -t), (t, 0, -1), (t, 0, 1), (-t, 0, -1), (-t, 0, 1)]
    f = [(0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
         (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9), (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1)]
    v = [np.array(p, float) / np.linalg.norm(p) for p in v]
    for _ in range(level):
        cache, nf = {}, []

        def mid(i, j):
            key = (min(i, j), max(i, j))
            if key not in cache:
                m = v[i] + v[j]
                v.append(m / np.linalg.norm(m))
                cache[key] = len(v) - 1
            return cache[key]

        for a, b, c in f:
            ab, bc, ca = mid(a, b), mid(b, c), mid(c, a)
            nf += [(a, ab, ca), (b, bc, ab), (c, ca, bc), (ab, bc, ca)]
        f = nf
    return np.array(v), np.array(f)


class Builder:
    """Collects coloured meshes (kind 2: the colour is in the vertex)."""

    def __init__(self):
        self.verts, self.tris = [], []

    def add(self, pos, nrm, color, faces):
        base = sum(len(v) for v in self.verts)
        col = np.broadcast_to(np.asarray(color, float), (len(pos), 3))
        self.verts.append(np.concatenate([pos, nrm, np.full((len(pos), 1), 2.0), col], -1))
        self.tris.append(np.asarray(faces) + base)

    def sphere(self, centre, radii, color, rng, level=2, lumpy=0.0):
        v, f = icosphere(level)
        bump = 1 + lumpy * (rng.random(len(v)) - 0.5)
        pos = centre + v * radii * bump[:, None]
        self.add(pos, v, color, f)

    def cylinder(self, bottom, radius, length, color, sides=10, top_radius=None):
        top_radius = radius if top_radius is None else top_radius
        a = np.linspace(0, 2 * np.pi, sides, endpoint=False)
        ring = np.stack([np.cos(a), np.zeros_like(a), np.sin(a)], -1)
        pos = np.concatenate([bottom + ring * radius, bottom + ring * top_radius + [0, length, 0]])
        nrm = np.concatenate([ring, ring])
        faces = []
        for j in range(sides):
            k = (j + 1) % sides
            faces += [(j, k, sides + k), (j, sides + k, sides + j)]
        self.add(pos, nrm, color, faces)

    def cone(self, bottom, radius, length, color, sides=14):
        a = np.linspace(0, 2 * np.pi, sides, endpoint=False)
        ring = np.stack([np.cos(a), np.zeros_like(a), np.sin(a)], -1)
        slope = radius / length
        nrm = ring + np.array([0, slope, 0])
        nrm /= np.linalg.norm(nrm, axis=-1, keepdims=True)
        pos = np.concatenate([bottom + ring * radius, np.repeat([bottom + [0, length, 0]], sides, 0)])
        faces = [(j, sides + j, (j + 1) % sides) for j in range(sides)]
        self.add(pos, np.concatenate([nrm, nrm]), color, faces)

    def mesh(self):
        return np.concatenate(self.verts).astype('f4'), np.concatenate(self.tris).astype('i4')


def srgb(hex_colour):
    c = np.array([(hex_colour >> 16) & 255, (hex_colour >> 8) & 255, hex_colour & 255]) / 255.0
    return c ** 2.2


def scenery():
    rng = np.random.default_rng(11)
    b = Builder()
    trees = []
    tries = 0
    while len(trees) < 30 and tries < 20000:
        tries += 1
        ang = rng.random() * 2 * np.pi
        rad = boundary(ang) * (0.62 + 0.32 * rng.random())
        x, z = rad * math.cos(ang), rad * math.sin(ang)
        d, _ = path_distance(np.array([x]), np.array([z]))
        if d[0] < 0.27 or green_distance(np.array([x]), np.array([z]))[0] < 1.9 or np.hypot(x - TEE[0], z - TEE[1]) < 0.2:
            continue
        if blob(np.array([x]), np.array([z]), POND[0], POND[1], POND[2], 7)[0] < 1.35:
            continue
        if any(np.hypot(x - tx, z - tz) < 0.13 for tx, tz, _ in trees):
            continue
        trees.append((x, z, rng.random()))
    greens = [0x2C4A24, 0x355A2A, 0x263F21, 0x4A5F2C, 0x3A4E26]
    for x, z, k in trees:
        y = float(height(np.array([x]), np.array([z]))[0])
        scale = 0.8 + 0.5 * rng.random()
        b.cylinder(np.array([x, y - 0.01, z]), 0.010 * scale, 0.07 * scale, srgb(0x5A4030), sides=7)
        colour = srgb(greens[int(k * len(greens))]) * (0.85 + 0.3 * rng.random())
        if k < 0.35:  # a pine: two cones
            b.cone(np.array([x, y + 0.035 * scale, z]), 0.070 * scale, 0.16 * scale, colour * 0.85)
            b.cone(np.array([x, y + 0.10 * scale, z]), 0.050 * scale, 0.12 * scale, colour * 0.95)
        else:  # a broadleaf: a few lumps of canopy
            for _ in range(3):
                off = (rng.random(3) - 0.5) * np.array([0.06, 0.04, 0.06]) * scale
                r = (0.05 + 0.03 * rng.random()) * scale
                b.sphere(np.array([x, y + 0.11 * scale, z]) + off, np.array([r, r * 0.9, r]), colour, rng, level=2, lumpy=0.18)
    # The tee markers, and a ball on the fairway.
    for dx in (-0.045, 0.045):
        tx, tz = TEE[0] + dx, TEE[1] - 0.02
        b.sphere(np.array([tx, float(height(np.array([tx]), np.array([tz]))[0]) + 0.010, tz]), np.array([0.009] * 3), srgb(0xF2F2F2), rng, level=1)
    bx, bz = bezier(0.55)
    b.sphere(np.array([bx + 0.02, float(height(np.array([bx]), np.array([bz]))[0]) + 0.006, bz]), np.array([0.0065] * 3), srgb(0xFFFFFF), rng, level=1)
    return b.mesh()


def flag_mesh(phase):
    """The pin and its flag, the flag rippling with `phase` (radians)."""
    b = Builder()
    gx, gz = GREEN + np.array([0.03, 0.01])
    gy = float(height(np.array([gx]), np.array([gz]))[0])
    b.cylinder(np.array([gx, gy - 0.005, gz]), 0.0045, 0.20, srgb(0xEDEDED), sides=8)
    cols, rows = 10, 4
    verts, faces = [], []
    for i in range(cols + 1):
        u = i / cols
        for j in range(rows + 1):
            v = j / rows
            wave = 0.012 * u * math.sin(phase + u * 5.0)
            width = 0.085 * (1 - 0.35 * v * u)
            verts.append((gx + width * u, gy + 0.195 - 0.055 * v + 0.004 * u * v, gz + wave))
    for i in range(cols):
        for j in range(rows):
            a, c = i * (rows + 1) + j, (i + 1) * (rows + 1) + j
            faces += [(a, c, c + 1), (a, c + 1, a + 1)]
    pos = np.array(verts)
    nrm = np.tile([0.0, 0.0, 1.0], (len(pos), 1))
    b.add(pos, nrm, srgb(0xE0323F), faces)
    b.add(pos, -nrm, srgb(0xE0323F), [(a, c, d) for a, d, c in faces])  # the back of the flag
    return b.mesh()


def water_mesh():
    a = np.linspace(0, 2 * np.pi, 120, endpoint=False)
    r = POND[2] * wobble(a, 7) * 1.25
    ring = np.stack([POND[0] + r * np.cos(a), np.full_like(a, WATER_LEVEL), POND[1] + r * np.sin(a)], -1)
    pos = np.concatenate([[[POND[0], WATER_LEVEL, POND[1]]], ring])
    verts = np.concatenate([pos, np.tile([0, 1, 0], (len(pos), 1)), np.full((len(pos), 1), 3.0)], -1)
    faces = [(0, 1 + (j + 1) % 120, 1 + j) for j in range(120)]
    return verts.astype('f4'), np.array(faces, 'i4')


# ---- shaders ---------------------------------------------------------------------------------------------------

VERTEX = """
#version 330
uniform mat4 u_mvp;
uniform mat4 u_light;
in vec3 in_pos; in vec3 in_nrm; in float in_kind; in vec3 in_col;
out vec3 v_pos; out vec3 v_nrm; out float v_kind; out vec3 v_col; out vec4 v_shadow;
void main() {
    v_pos = in_pos; v_nrm = in_nrm; v_kind = in_kind; v_col = in_col;
    v_shadow = u_light * vec4(in_pos, 1.0);
    gl_Position = u_mvp * vec4(in_pos, 1.0);
}
"""

DEPTH_VERTEX = """
#version 330
uniform mat4 u_mvp;
in vec3 in_pos;
void main() { gl_Position = u_mvp * vec4(in_pos, 1.0); }
"""

DEPTH_FRAGMENT = """
#version 330
void main() { }
"""

FRAGMENT = """
#version 330
uniform sampler2D u_shadow_map;
uniform vec3 u_sun;      // towards the sun
uniform vec3 u_eye;
uniform float u_phase;
uniform vec2 u_path[48];
uniform vec2 u_green; uniform vec2 u_green_r; uniform float u_green_angle;
uniform vec3 u_pond;
uniform vec3 u_bunkers[4];
uniform vec2 u_tee;
in vec3 v_pos; in vec3 v_nrm; in float v_kind; in vec3 v_col; in vec4 v_shadow;
out vec4 f_colour;

vec3 lin(vec3 c) { return pow(c, vec3(2.2)); }
float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float noise(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}
float fbm(vec2 p) { return 0.5 * noise(p) + 0.25 * noise(p * 2.1) + 0.125 * noise(p * 4.3); }
float wobble(float a, float s) { return 1.0 + 0.12 * sin(3.0 * a + s) + 0.06 * sin(5.0 * a + 2.1 * s); }
float blob(vec2 p, vec3 c, float s) { vec2 d = p - c.xy; return length(d) / (c.z * wobble(atan(d.y, d.x), s)); }

float shadowAt(vec4 sp) {
    vec3 p = sp.xyz / sp.w * 0.5 + 0.5;
    if (p.x < 0.0 || p.x > 1.0 || p.y < 0.0 || p.y > 1.0) return 1.0;
    float lit = 0.0;
    vec2 texel = 1.0 / vec2(textureSize(u_shadow_map, 0));
    for (int i = -2; i <= 2; i++) for (int j = -2; j <= 2; j++)
        lit += (p.z - 0.0016 <= texture(u_shadow_map, p.xy + vec2(i, j) * texel * 1.2).r) ? 1.0 : 0.0;
    return lit / 25.0;
}

vec3 turf(vec2 p, out float shine) {
    shine = 0.0;
    float n = fbm(p * 38.0);
    vec3 rough = mix(lin(vec3(0.17, 0.25, 0.12)), lin(vec3(0.23, 0.31, 0.15)), n);
    // The fairway: distance to the centre line and the mowing stripes across it.
    float best = 1e9, along = 0.0;
    for (int i = 0; i < 47; i++) {
        vec2 a = u_path[i], b = u_path[i + 1], ab = b - a;
        float t = clamp(dot(p - a, ab) / dot(ab, ab), 0.0, 1.0);
        float d = length(p - (a + ab * t));
        if (d < best) { best = d; along = (float(i) + t) / 47.0; }
    }
    float half_width = 0.125 * smoothstep(0.08, 0.28, along) * (1.0 + 0.08 * sin(along * 19.0));
    float on_fairway = 1.0 - smoothstep(half_width - 0.006, half_width + 0.006, best);
    float stripe = step(0.5, fract(along * 13.0));
    vec3 fair = mix(lin(vec3(0.44, 0.62, 0.28)), lin(vec3(0.35, 0.52, 0.22)), stripe) * (0.95 + 0.1 * n);
    vec3 c = mix(rough, fair, on_fairway);
    // A slightly lighter first cut around the fairway.
    float cut = (1.0 - smoothstep(half_width + 0.006, half_width + 0.03, best)) * (1.0 - on_fairway);
    c = mix(c, lin(vec3(0.32, 0.45, 0.21)), cut * 0.8);
    // The tee box.
    vec2 tq = abs(p - u_tee) / vec2(0.075, 0.05);
    float tee = 1.0 - smoothstep(0.95, 1.0, max(tq.x, tq.y));
    c = mix(c, mix(lin(vec3(0.44, 0.68, 0.27)), lin(vec3(0.36, 0.60, 0.23)), step(0.5, fract(p.x * 40.0))), tee);
    // The green, a fringe around it, and its finer diagonal stripes.
    vec2 d = p - u_green;
    float ca = cos(u_green_angle), sa = sin(u_green_angle);
    vec2 q = vec2(ca * d.x + sa * d.y, -sa * d.x + ca * d.y) / u_green_r;
    float g = length(q);
    c = mix(c, lin(vec3(0.38, 0.62, 0.24)), 1.0 - smoothstep(1.18, 1.24, g));
    float gs = step(0.5, fract((p.x + p.y) * 34.0));
    c = mix(c, mix(lin(vec3(0.50, 0.76, 0.31)), lin(vec3(0.43, 0.70, 0.27)), gs), 1.0 - smoothstep(0.98, 1.02, g));
    shine = (1.0 - smoothstep(0.98, 1.02, g)) * 0.15;
    // The cup.
    c = mix(c, vec3(0.01), 1.0 - smoothstep(0.0045, 0.0065, length(p - (u_green + vec2(0.03, 0.01)))));
    // Bunkers: sand with a darker raked edge.
    for (int i = 0; i < 4; i++) {
        float b = blob(p, u_bunkers[i], float(i + 1));
        vec3 sand = lin(vec3(0.89, 0.82, 0.64)) * (0.92 + 0.12 * fbm(p * 90.0));
        c = mix(c, mix(sand, sand * 0.78, smoothstep(0.75, 0.98, b)), 1.0 - smoothstep(0.98, 1.04, b));
    }
    return c;
}

vec3 soil(vec3 p, float top) {
    float depth = top - p.y;
    float n = fbm(vec2(atan(p.z, p.x) * 18.0, p.y * 30.0));
    vec3 c = mix(lin(vec3(0.33, 0.23, 0.15)), lin(vec3(0.25, 0.17, 0.11)), n);
    c = mix(c, lin(vec3(0.20, 0.14, 0.10)), smoothstep(0.08, 0.10, depth) * (1.0 - smoothstep(0.13, 0.15, depth)));
    c = mix(c, lin(vec3(0.30, 0.29, 0.28)) * (0.8 + 0.4 * n), smoothstep(0.2, 0.26, depth));
    c = mix(c, lin(vec3(0.24, 0.40, 0.17)), 1.0 - smoothstep(0.008, 0.016, depth)); // the turf lip
    return c;
}

void main() {
    vec3 n = normalize(v_nrm);
    vec3 view = normalize(u_eye - v_pos);
    float shine = 0.0;
    vec3 albedo;
    if (v_kind < 0.5) albedo = turf(v_pos.xz, shine);
    else if (v_kind < 1.5) albedo = soil(v_pos, v_col.x);
    else albedo = v_col;
    float sh = shadowAt(v_shadow);
    vec3 sun = vec3(1.0, 0.93, 0.82) * 2.3;
    vec3 sky = mix(vec3(0.10, 0.09, 0.08), vec3(0.42, 0.52, 0.70), n.y * 0.5 + 0.5) * 0.85;
    float ndl = max(dot(n, u_sun), 0.0);
    vec3 c;
    if (v_kind > 2.5) {
        // Water: deep colour, the sky in it at grazing angles, and a glint of sun that ripples.
        vec2 rp = v_pos.xz * 60.0;
        vec3 wn = normalize(vec3(0.03 * sin(rp.x + u_phase * 2.0) + 0.02 * sin(rp.y * 1.3 - u_phase * 3.0), 1.0,
                                 0.03 * cos(rp.y + u_phase * 2.0)));
        float fres = 0.04 + 0.96 * pow(1.0 - max(dot(wn, view), 0.0), 5.0);
        vec3 deep = lin(vec3(0.06, 0.24, 0.32));
        vec3 refl = vec3(0.55, 0.68, 0.85);
        float edge = 1.0 - smoothstep(0.75, 0.95, blob(v_pos.xz, u_pond, 7.0));
        c = mix(deep * (0.5 + 0.5 * sh), refl, fres * 0.8);
        c = mix(c, lin(vec3(0.20, 0.42, 0.40)), (1.0 - edge) * 0.6);
        vec3 h = normalize(u_sun + view);
        c += sun * pow(max(dot(wn, h), 0.0), 220.0) * 1.6 * sh;
        f_colour = vec4(c, 1.0);
    } else {
        c = albedo * (sun * ndl * sh + sky);
        vec3 h = normalize(u_sun + view);
        c += sun * shine * pow(max(dot(n, h), 0.0), 40.0) * sh;
        // A cool rim where surfaces turn away, so the tile reads against the dark.
        float rim = pow(1.0 - max(dot(n, view), 0.0), 3.0);
        c += vec3(0.20, 0.26, 0.38) * rim * 0.35;
        f_colour = vec4(c, 1.0);
    }
    // Filmic tone, then display gamma.
    vec3 x = f_colour.rgb * 0.9;
    x = (x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14);
    f_colour = vec4(pow(clamp(x, 0.0, 1.0), vec3(1.0 / 2.2)), 1.0);
}
"""


def perspective(fov, aspect, near, far):
    f = 1 / math.tan(fov / 2)
    return np.array([[f / aspect, 0, 0, 0], [0, f, 0, 0], [0, 0, (far + near) / (near - far), 2 * far * near / (near - far)], [0, 0, -1, 0]])


def ortho(l, r, b, t, n, f):
    return np.array([[2 / (r - l), 0, 0, -(r + l) / (r - l)], [0, 2 / (t - b), 0, -(t + b) / (t - b)], [0, 0, -2 / (f - n), -(f + n) / (f - n)], [0, 0, 0, 1]])


def look_at(eye, target, up=(0, 1, 0)):
    f = target - eye
    f = f / np.linalg.norm(f)
    s = np.cross(f, up)
    s /= np.linalg.norm(s)
    u = np.cross(s, f)
    m = np.eye(4)
    m[0, :3], m[1, :3], m[2, :3] = s, u, -f
    m[:3, 3] = -m[:3, :3] @ eye
    return m


def main():
    out = sys.argv[1]
    frames = int(sys.argv[sys.argv.index('--frames') + 1]) if '--frames' in sys.argv else 160
    size = sys.argv[sys.argv.index('--size') + 1] if '--size' in sys.argv else '600x420'
    preview = sys.argv[sys.argv.index('--preview') + 1] if '--preview' in sys.argv else None
    only = [int(x) for x in sys.argv[sys.argv.index('--only') + 1].split(',')] if '--only' in sys.argv else None
    w, h = (int(v) for v in size.split('x'))
    ss = 2
    W, H = w * ss, h * ss

    ctx = moderngl.create_standalone_context(require=330)
    ctx.enable(moderngl.DEPTH_TEST)
    prog = ctx.program(vertex_shader=VERTEX, fragment_shader=FRAGMENT)
    depth_prog = ctx.program(vertex_shader=DEPTH_VERTEX, fragment_shader=DEPTH_FRAGMENT)

    def upload(verts, tris):
        if verts.shape[1] == 7:  # no colour column: add one
            verts = np.concatenate([verts, np.zeros((len(verts), 3), 'f4')], 1)
        vbo = ctx.buffer(verts.astype('f4').tobytes())
        ibo = ctx.buffer(tris.astype('i4').tobytes())
        fmt = (vbo, '3f 3f 1f 3f', 'in_pos', 'in_nrm', 'in_kind', 'in_col')
        return ctx.vertex_array(prog, [fmt], ibo), ctx.vertex_array(depth_prog, [(vbo, '3f 28x', 'in_pos')], ibo)

    static = [upload(*terrain_mesh()), upload(*scenery()), upload(*water_mesh())]

    prog['u_path'].value = [tuple(p) for p in PATH]
    prog['u_green'].value = tuple(GREEN)
    prog['u_green_r'].value = tuple(GREEN_R)
    prog['u_green_angle'].value = GREEN_ANGLE
    prog['u_pond'].value = tuple(POND)
    prog['u_bunkers'].value = list(BUNKERS)
    prog['u_tee'].value = tuple(TEE)

    shadow_tex = ctx.depth_texture((2048, 2048))
    shadow_tex.compare_func = ''
    shadow_fbo = ctx.framebuffer(depth_attachment=shadow_tex)
    colour = ctx.renderbuffer((W, H), samples=8)
    depth = ctx.depth_renderbuffer((W, H), samples=8)
    fbo = ctx.framebuffer([colour], depth)
    resolved = ctx.framebuffer([ctx.renderbuffer((W, H))])

    packed = []
    for f in range(frames):
        if only and f not in only:
            continue
        turn = 2 * math.pi * f / frames
        phase = 2 * math.pi * 3 * f / frames  # the flag and water repeat three times a turn, so the loop is seamless
        flag = upload(*flag_mesh(phase))
        # The tile turns under a fixed studio light and camera.
        rot = np.array([[math.cos(turn), 0, math.sin(turn), 0], [0, 1, 0, 0], [-math.sin(turn), 0, math.cos(turn), 0], [0, 0, 0, 1]])
        eye = np.array([0.0, 1.75, 3.55])
        view = look_at(eye, np.array([0.0, -0.10, 0.0]))
        proj = perspective(math.radians(27), W / H, 0.5, 10)
        model_eye = np.linalg.inv(rot) @ np.append(eye, 1)
        sun_world = np.array([-0.55, 0.78, 0.45])
        sun_world /= np.linalg.norm(sun_world)
        sun_model = (np.linalg.inv(rot)[:3, :3] @ sun_world)
        light_view = look_at(sun_model * 4, np.zeros(3))
        light = ortho(-1.6, 1.6, -1.6, 1.6, 0.5, 8) @ light_view

        depth_prog['u_mvp'].write(light.T.astype('f4').tobytes())
        shadow_fbo.use()
        shadow_fbo.clear(depth=1.0)
        for _, dvao in [*static, flag]:
            dvao.render()

        fbo.use()
        fbo.clear(*(np.array(INK) / 255.0), 1.0)
        prog['u_mvp'].write((proj @ view @ rot).T.astype('f4').tobytes())
        prog['u_light'].write(light.T.astype('f4').tobytes())
        prog['u_sun'].value = tuple(sun_model)
        prog['u_eye'].value = tuple(model_eye[:3])
        prog['u_phase'].value = phase
        shadow_tex.use(0)
        prog['u_shadow_map'].value = 0
        for vao, _ in [*static, flag]:
            vao.render()
        ctx.copy_framebuffer(resolved, fbo)
        image = Image.frombytes('RGB', (W, H), resolved.read(components=3)).transpose(Image.FLIP_TOP_BOTTOM)
        image = image.resize((w, h), Image.LANCZOS)
        flag[0].release(); flag[1].release()
        buffer = io.BytesIO()
        image.save(buffer, 'JPEG', quality=90, optimize=True)
        packed.append(buffer.getvalue())
        if preview:
            image.save(f'{preview}/hole_{f:03d}.png')
    with open(out, 'wb') as fh:
        fh.write(b'OFH1' + struct.pack('<iii', len(packed), w, h))
        for data in packed:
            fh.write(struct.pack('<i', len(data)) + data)
    print(f'wrote {out}: {len(packed)} frames, {sum(map(len, packed)) / 1e6:.1f} MB')


if __name__ == '__main__':
    main()
