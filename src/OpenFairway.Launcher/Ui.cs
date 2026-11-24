using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace OpenFairway.Launcher
{
    /// <summary>The OpenFairway palette (docs/design.md), shared in spirit with the in-game screens.</summary>
    internal static class Theme
    {
        // Sampled from PGA TOUR 2K25's own menus (docs/design.md).
        public static readonly Color Ink = Hex(0x0A0A14), Bg = Hex(0x0F0F1D), Strip = Hex(0x191923), Line = Hex(0x454850);
        public static readonly Color TileTop = Hex(0x3F4653), TileBottom = Hex(0x242731), RaisedTop = Hex(0x4A5160), RaisedBottom = Hex(0x2E323D);
        public static readonly Color Text = Hex(0xF4F4F4), Muted = Hex(0xAEB4C6), Faint = Hex(0x6E7486);
        public static readonly Color Cream = Hex(0xFDEDDA), Red = Hex(0xE0323F), Gold = Hex(0xE9B949);
        public static readonly Color TurfLight = Hex(0x84BE54), TurfDark = Hex(0x56943C);
        /// <summary>All good: ready to play, loaded. Between the two turf stripes, a little richer.</summary>
        public static readonly Color Green = Hex(0x62B444);
        /// <summary>The blueprint sheet behind the scene. tools/render-hole.py bakes the same paper and grid into the
        /// hole's frames (PAPER, GRID_MINOR, GRID_MAJOR, LINE there), so these must stay equal to those.</summary>
        public static readonly Color Paper = Hex(0x0D2646), GridMinor = Hex(0x132E52), GridMajor = Hex(0x1A385F), Blueprint = Hex(0xCEE2FF);

        public static Color Hex(int rgb, int alpha = 255) => Color.FromArgb(alpha, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        public static Color Alpha(Color c, float alpha) => Color.FromArgb(Clamp((int)Math.Round(alpha * c.A), 0, 255), c);

        public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }

    internal static class Ease
    {
        public static float Out(float t) { t = Theme.Clamp(t, 0f, 1f); var u = 1f - t; return 1f - u * u * u; }
        public static float InOut(float t) { t = Theme.Clamp(t, 0f, 1f); return t < 0.5f ? 4f * t * t * t : 1f - (float)Math.Pow(-2f * t + 2f, 3) / 2f; }
    }

    /// <summary>A value that glides to a new target over a fixed time with an ease-out curve, on the window's clock.</summary>
    internal sealed class Tween
    {
        private float _from, _to;
        private double _start;
        private float _duration = 0.0001f;

        public Tween(float value) { _from = _to = value; }

        public float Target => _to;

        public float Value(double now) => _from + (_to - _from) * Ease.Out((float)((now - _start) / _duration));

        public bool Moving(double now) => now - _start < _duration && _from != _to;

        public void Set(float target, double now, float duration)
        {
            if (target == _to) return;
            _from = Value(now);
            _to = target;
            _start = now;
            _duration = duration;
        }

        /// <summary>Jumps straight to a value (for stills).</summary>
        public void Snap(float value) { _from = _to = value; _duration = 0.0001f; }
    }

    internal static class Gfx
    {
        private static readonly StringFormat Typographic = MakeTypographic();
        private static readonly Bitmap MeasureSurface = new Bitmap(1, 1);
        private static readonly Graphics Measurer = MakeMeasurer();
        private const int WrapCacheLimit = 4000;
        private static readonly Dictionary<(string, Font, int), List<string>> WrapCache = new Dictionary<(string, Font, int), List<string>>();

        private static StringFormat MakeTypographic()
        {
            var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            return format;
        }

        private static Graphics MakeMeasurer()
        {
            var g = Graphics.FromImage(MeasureSurface);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            return g;
        }

        public static void Quality(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.CompositingQuality = CompositingQuality.HighSpeed;   // HighQuality (gamma-correct) is 4x slower, no visible gain
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void Fill(Graphics g, RectangleF r, float radius, Color color)
        {
            if (color.A == 0) return;
            using (var brush = new SolidBrush(color)) using (var path = Round(r, radius)) g.FillPath(brush, path);
        }

        public static void Stroke(Graphics g, RectangleF r, float radius, Color color, float width = 1f)
        {
            if (color.A == 0) return;
            var inset = RectangleF.Inflate(r, -width / 2, -width / 2);
            using (var pen = new Pen(color, width)) using (var path = Round(inset, Math.Max(0, radius - width / 2))) g.DrawPath(pen, path);
        }

        /// <summary>A rounded box with a top-to-bottom colour ramp (the game's slate tiles).</summary>
        public static void FillVertical(Graphics g, RectangleF r, float radius, Color top, Color bottom)
        {
            using (var brush = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), top, bottom, 90f))
            using (var path = Round(r, radius)) g.FillPath(brush, path);
        }

        // ---- text ----

        public static float Width(string text, Font font) => text.Length == 0 ? 0 : Measurer.MeasureString(text, font, PointF.Empty, Typographic).Width;

        public static float Width(Graphics g, string text, Font font) => Width(text, font);

        /// <summary>One line in a box, cut with an ellipsis if it does not fit.</summary>
        public static void TextFit(Graphics g, string text, Font font, Color color, RectangleF box, StringAlignment align = StringAlignment.Near)
        {
            if (color.A == 0 || text.Length == 0) return;
            using (var format = (StringFormat)Typographic.Clone())
            using (var brush = new SolidBrush(color))
            {
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.Alignment = align;
                g.DrawString(text, font, brush, box, format);
            }
        }

        public static void Text(Graphics g, string text, Font font, Color color, float x, float y)
        {
            if (color.A == 0 || text.Length == 0) return;
            using (var brush = new SolidBrush(color)) g.DrawString(text, font, brush, x, y, Typographic);
        }

        private static readonly Dictionary<(char, Font), float> CharWidths = new Dictionary<(char, Font), float>();

        private static float CharWidth(char c, Font font)
        {
            if (!CharWidths.TryGetValue((c, font), out var w)) CharWidths[(c, font)] = w = Width(c.ToString(), font);
            return w;
        }

        /// <summary>The width of an uppercase title set with <paramref name="tracking"/> pixels between letters.</summary>
        public static float TitleWidth(string text, Font font, float tracking)
        {
            var w = 0f;
            foreach (var c in text) w += CharWidth(c, font);
            return w + tracking * Math.Max(0, text.Length - 1);
        }

        /// <summary>An upright uppercase title with a little air between the letters.</summary>
        public static void Title(Graphics g, string text, Font font, Color color, float x, float y, float tracking)
        {
            if (color.A == 0) return;
            using (var brush = new SolidBrush(color))
                foreach (var c in text)
                {
                    g.DrawString(c.ToString(), font, brush, x, y, Typographic);
                    x += CharWidth(c, font) + tracking;
                }
        }

        /// <summary>Word-wrapped lines of a text for a width. Cached, since layout asks every frame.</summary>
        public static List<string> Wrap(string text, Font font, float width)
        {
            var key = (text, font, (int)width);
            if (WrapCache.TryGetValue(key, out var cached)) return cached;
            var lines = new List<string>();
            foreach (var paragraph in text.Replace("\r", "").Split('\n'))
            {
                var line = "";
                foreach (var word in paragraph.Split(' '))
                {
                    var candidate = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && Width(candidate, font) > width)
                    {
                        lines.Add(line);
                        line = word;
                    }
                    else line = candidate;
                }
                lines.Add(line);
            }
            if (WrapCache.Count > WrapCacheLimit) WrapCache.Clear();
            WrapCache[key] = lines;
            return lines;
        }

        /// <summary>Wrapped text, at most <paramref name="maxLines"/> lines (the last one cut with an ellipsis).
        /// Returns the height used.</summary>
        public static float Paragraph(Graphics g, string text, Font font, Color color, float x, float y, float width, float lineHeight, int maxLines = int.MaxValue)
        {
            var lines = Wrap(text, font, width);
            var count = Math.Min(lines.Count, maxLines);
            for (var i = 0; i < count; i++)
            {
                var line = lines[i];
                if (i == count - 1 && count < lines.Count) line += " " + string.Join(" ", lines.GetRange(count, lines.Count - count));
                TextFit(g, line, font, color, new RectangleF(x, y + i * lineHeight, width + 2, lineHeight + 4));
            }
            return count * lineHeight;
        }

        public static int LineCount(string text, Font font, float width, int maxLines = int.MaxValue) => Math.Min(maxLines, Wrap(text, font, width).Count);

        public static Font Font(string family, float px, FontStyle style, string fallback)
        {
            try
            {
                using (new FontFamily(family)) { }
                return new Font(family, px, style, GraphicsUnit.Pixel);
            }
            catch (ArgumentException)
            {
                return new Font(fallback, px, style, GraphicsUnit.Pixel);
            }
        }

        public static FontFamily Family(string name, string fallback)
        {
            try { return new FontFamily(name); }
            catch (ArgumentException) { return new FontFamily(fallback); }
        }

        // ---- line icons (drawn, so they stay sharp at any scale) ----

        private static Pen RoundPen(Color color, float width) =>
            new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        public static void Check(Graphics g, float cx, float cy, float r, Color fill, Color mark)
        {
            using (var brush = new SolidBrush(fill)) g.FillEllipse(brush, cx - r, cy - r, 2 * r, 2 * r);
            using (var pen = RoundPen(mark, Math.Max(1.3f, r * 0.24f)))
                g.DrawLines(pen, new[] { new PointF(cx - r * 0.42f, cy + r * 0.02f), new PointF(cx - r * 0.1f, cy + r * 0.34f), new PointF(cx + r * 0.44f, cy - r * 0.32f) });
        }

        public static void Alert(Graphics g, float cx, float cy, float r, Color fill, Color mark)
        {
            using (var brush = new SolidBrush(fill)) g.FillEllipse(brush, cx - r, cy - r, 2 * r, 2 * r);
            using (var pen = RoundPen(mark, Math.Max(1.3f, r * 0.24f))) g.DrawLine(pen, cx, cy - r * 0.48f, cx, cy + r * 0.08f);
            var d = Math.Max(1.6f, r * 0.27f);
            using (var brush = new SolidBrush(mark)) g.FillEllipse(brush, cx - d / 2, cy + r * 0.42f - d / 2, d, d);
        }

        public static void Ring(Graphics g, float cx, float cy, float r, Color color, float width = 1.4f)
        {
            using (var pen = new Pen(color, width)) g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
        }

        public static void Spinner(Graphics g, float cx, float cy, float r, Color color, double t)
        {
            using (var track = new Pen(Theme.Alpha(color, 0.25f), Math.Max(1.4f, r * 0.26f))) g.DrawEllipse(track, cx - r, cy - r, 2 * r, 2 * r);
            using (var pen = RoundPen(color, Math.Max(1.4f, r * 0.26f))) g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, (float)(t * 300 % 360), 100);
        }

        /// <summary>A chevron pointing right at angle 0 (degrees, clockwise).</summary>
        public static void Chevron(Graphics g, float cx, float cy, float size, float angle, Color color, float width = 1.8f)
        {
            var a = angle * Math.PI / 180;
            var c = (float)Math.Cos(a);
            var s = (float)Math.Sin(a);
            PointF P(float x, float y) => new PointF(cx + x * c - y * s, cy + x * s + y * c);
            using (var pen = RoundPen(color, width)) g.DrawLines(pen, new[] { P(-size * 0.3f, -size * 0.6f), P(size * 0.3f, 0), P(-size * 0.3f, size * 0.6f) });
        }

        /// <summary>The "opens outside" arrow, in a box of side <paramref name="s"/>.</summary>
        public static void External(Graphics g, float x, float y, float s, Color color)
        {
            using (var pen = RoundPen(color, 1.4f))
            {
                g.DrawLine(pen, x, y + s, x + s, y);
                g.DrawLines(pen, new[] { new PointF(x + s * 0.38f, y), new PointF(x + s, y), new PointF(x + s, y + s * 0.62f) });
            }
        }

        public static void Gear(Graphics g, float cx, float cy, float r, Color color)
        {
            using (var pen = new Pen(color, Math.Max(1.4f, r * 0.17f)))
            {
                g.DrawEllipse(pen, cx - r * 0.66f, cy - r * 0.66f, r * 1.32f, r * 1.32f);
                g.DrawEllipse(pen, cx - r * 0.26f, cy - r * 0.26f, r * 0.52f, r * 0.52f);
            }
            using (var tooth = new Pen(color, Math.Max(2.2f, r * 0.3f)) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
                for (var i = 0; i < 8; i++)
                {
                    var a = i * Math.PI / 4;
                    g.DrawLine(tooth, cx + (float)Math.Cos(a) * r * 0.72f, cy + (float)Math.Sin(a) * r * 0.72f, cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r);
                }
        }

        /// <summary>Stacked layers: the mods.</summary>
        public static void Layers(Graphics g, float cx, float cy, float s, Color color)
        {
            using (var pen = RoundPen(color, Math.Max(1.4f, s * 0.13f)))
            {
                g.DrawPolygon(pen, new[] { new PointF(cx, cy - s * 0.62f), new PointF(cx + s, cy - s * 0.12f), new PointF(cx, cy + s * 0.38f), new PointF(cx - s, cy - s * 0.12f) });
                g.DrawLines(pen, new[] { new PointF(cx - s, cy + s * 0.22f), new PointF(cx, cy + s * 0.72f), new PointF(cx + s, cy + s * 0.22f) });
            }
        }

        public static void Folder(Graphics g, float x, float y, Color color)
        {
            using (var pen = new Pen(color, 1.3f) { LineJoin = LineJoin.Round })
            using (var folder = new GraphicsPath())
            {
                folder.AddLines(new[] { new PointF(x, y + 8), new PointF(x, y - 2), new PointF(x + 5, y - 2), new PointF(x + 6.5f, y), new PointF(x + 14, y), new PointF(x + 14, y + 8) });
                folder.CloseFigure();
                g.DrawPath(pen, folder);
            }
        }

        /// <summary>An arrow down onto a line: download and install.</summary>
        public static void Download(Graphics g, float cx, float cy, float s, Color color, float width = 2f)
        {
            using (var pen = RoundPen(color, width))
            {
                g.DrawLine(pen, cx, cy - s * 0.7f, cx, cy + s * 0.2f);
                g.DrawLines(pen, new[] { new PointF(cx - s * 0.4f, cy - s * 0.15f), new PointF(cx, cy + s * 0.25f), new PointF(cx + s * 0.4f, cy - s * 0.15f) });
                g.DrawLine(pen, cx - s * 0.55f, cy + s * 0.7f, cx + s * 0.55f, cy + s * 0.7f);
            }
        }

        public static void PlayArrow(Graphics g, float x, float cy, float h, Color color)
        {
            using (var brush = new SolidBrush(color))
                g.FillPolygon(brush, new[] { new PointF(x + h * 0.15f, cy - h / 2), new PointF(x + h * 0.85f, cy), new PointF(x, cy + h / 2) });
        }
    }

    /// <summary>A mowed fairway seen from above: broad diagonal stripes alternating light and dark, with grass grain
    /// running along them and sunlight falling from the top left. Made in code, matching tools/make-wordmark.py.</summary>
    internal static class Turf
    {
        public static Bitmap Make(int width, int height, float stripe, float scale, float angleDeg = -28f, float darken = 0f)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var pixels = new int[width * height];
            var a = angleDeg * Math.PI / 180.0;
            var cos = Math.Cos(a);
            var sin = Math.Sin(a);
            var grainAcross = Math.Max(1.0, 1.1 * scale);
            var grainAlong = Math.Max(2.0, 5.0 * scale);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var u = x * cos + y * sin;
                    var v = -x * sin + y * cos;
                    var k = u / stripe;
                    var band = (int)Math.Floor(k) & 1;
                    var edge = Math.Abs((k - Math.Floor(k)) - 0.5) * 2.0;
                    var baseColor = band == 0 ? Theme.TurfLight : Theme.TurfDark;
                    var shade = 0.96 + 0.04 * edge;
                    shade *= 1.0 + 0.075 * Noise((int)Math.Floor(u / grainAcross), (int)Math.Floor(v / grainAlong));
                    shade *= 1.12 - 0.28 * y / height - 0.06 * x / width;
                    shade *= 1.0 - darken;
                    var r = Byte(baseColor.R * shade);
                    var gg = Byte(baseColor.G * shade);
                    var b = Byte(baseColor.B * shade);
                    pixels[y * width + x] = (255 << 24) | (r << 16) | (gg << 8) | b;
                }
            }
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            bitmap.UnlockBits(data);
            return bitmap;
        }

        private static int Byte(double v) => v < 0 ? 0 : v > 255 ? 255 : (int)v;

        /// <summary>Repeatable noise in [-1, 1] for a cell.</summary>
        private static double Noise(int x, int y)
        {
            unchecked
            {
                var h = (uint)(x * 374761393 + y * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFF) / 32767.5 - 1.0;
            }
        }
    }
}
