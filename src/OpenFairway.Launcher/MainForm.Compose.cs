using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpenFairway.Launcher
{
    /// <summary>
    /// How the window reaches the screen. The interface is drawn with GDI+ into a cached layer only when it changes;
    /// the turning hole changes at the display's rate. Each paint is one pass over the pixels it needs: the sheet or
    /// the hole (the two frames either side of the moment, mixed), the interface over them, and the wordmark's light
    /// when it passes; then one copy to the window. No GDI+ drawing per frame, so the turn costs little.
    /// </summary>
    internal sealed partial class MainForm
    {
        private int[]? _sheetPixels, _uiPixels, _sweepPixels, _stillPixels;
        private int[] _out = new int[0];
        private Rectangle _sweepRect;

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_live != null)
            {
                PresentLive();
                return;
            }
            var clip = Rectangle.Intersect(e.ClipRectangle, new Rectangle(0, 0, W, H));
            if (clip.Width <= 0 || clip.Height <= 0) return;
            if (_uiDirty || _uiPixels == null) DrawInterfaceLayer();
            Compose(clip);
            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf(typeof(BitmapInfoHeader)), Width = clip.Width, Height = -clip.Height, Planes = 1, BitCount = 32,
            };
            var hdc = e.Graphics.GetHdc();
            try { SetDIBitsToDevice(hdc, clip.X, clip.Y, clip.Width, clip.Height, 0, 0, 0, clip.Height, _out, ref header, 0); }
            finally { e.Graphics.ReleaseHdc(hdc); }
        }

        /// <summary>The window through OpenGL: the interface uploaded when it changed, the light when it passes.</summary>
        private void PresentLive()
        {
            if (_uiDirty || _uiPixels == null)
            {
                DrawInterfaceLayer();
                _live!.UploadInterface(_uiPixels!);
            }
            var sweep = SweepLayer();
            if (sweep != null) _live!.UploadSweep(sweep, _sweepRect.Width, _sweepRect.Height);
            _live!.Present(_now, HoleFade, sweep != null ? _sweepRect : Rectangle.Empty);
        }

        private void DrawInterfaceLayer()
        {
            if (_uiLayer == null) _uiLayer = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(_uiLayer))
            {
                g.Clear(Color.Transparent);
                Gfx.Quality(g);
                PaintInterface(g);
            }
            _uiPixels = Pixels(_uiLayer, _uiPixels);
            _uiDirty = false;
        }

        private static int[] Pixels(Bitmap bitmap, int[]? reuse)
        {
            var pixels = reuse != null && reuse.Length == bitmap.Width * bitmap.Height ? reuse : new int[bitmap.Width * bitmap.Height];
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            bitmap.UnlockBits(data);
            return pixels;
        }

        /// <summary>The pixels of the clip, composed into _out (rows of the clip's width).</summary>
        private void Compose(Rectangle clip)
        {
            var count = clip.Width * clip.Height;
            if (_out.Length < count) _out = new int[count];
            var sheet = _sheetPixels ??= Pixels(Sheet(), null);
            var ui = _uiPixels!;

            int[]? first = null, second = null;
            uint mix = 0, fade = 256;
            var hole = Rectangle.Empty;
            if (SceneData.Shared is { } scene && Still() is { } still && HoleFade > 0f)
            {
                first = second = _stillPixels ??= Pixels(still, null);
                fade = (uint)Math.Round(HoleFade * 256);
                hole = HoleRect(scene);
                _holeArea = hole;
            }
            var keep = 256u - mix;
            var sweep = SweepLayer();

            var output = _out;
            for (var y = clip.Top; y < clip.Bottom; y++)
            {
                var row = y * W;
                var o = (y - clip.Top) * clip.Width - clip.Left;
                // Each row in three runs: sheet, hole, sheet. No per-pixel tests inside a run.
                var holeFrom = clip.Right;
                var holeTo = clip.Right;
                if (first != null && y >= hole.Top && y < hole.Bottom)
                {
                    holeFrom = Math.Max(clip.Left, Math.Min(hole.Left, clip.Right));
                    holeTo = Math.Max(holeFrom, Math.Min(hole.Right, clip.Right));
                }
                for (var x = clip.Left; x < holeFrom; x++)
                {
                    var top = ui[row + x];
                    output[o + x] = top == 0 ? sheet[row + x] : (int)Over((uint)top, (uint)sheet[row + x]);
                }
                if (holeTo > holeFrom)
                {
                    var h = (y - hole.Top) * hole.Width - hole.Left;
                    for (var x = holeFrom; x < holeTo; x++)
                    {
                        var p = (uint)first![h + x];
                        if (mix != 0)
                        {
                            var q = (uint)second![h + x];
                            p = ((((p & 0xFF00FFu) * keep + (q & 0xFF00FFu) * mix) >> 8) & 0xFF00FFu) | ((((p & 0x00FF00u) * keep + (q & 0x00FF00u) * mix) >> 8) & 0x00FF00u);
                        }
                        if (fade < 256)
                        {
                            var s = (uint)sheet[row + x];
                            p = ((((s & 0xFF00FFu) * (256 - fade) + (p & 0xFF00FFu) * fade) >> 8) & 0xFF00FFu) | ((((s & 0x00FF00u) * (256 - fade) + (p & 0x00FF00u) * fade) >> 8) & 0x00FF00u);
                        }
                        var top = ui[row + x];
                        output[o + x] = top == 0 ? (int)(p | 0xFF000000u) : (int)Over((uint)top, p);
                    }
                }
                for (var x = holeTo; x < clip.Right; x++)
                {
                    var top = ui[row + x];
                    output[o + x] = top == 0 ? sheet[row + x] : (int)Over((uint)top, (uint)sheet[row + x]);
                }
            }

            if (sweep == null) return;
            var area = Rectangle.Intersect(_sweepRect, clip);
            for (var y = area.Top; y < area.Bottom; y++)
            {
                var o = (y - clip.Top) * clip.Width - clip.Left;
                var s = (y - _sweepRect.Top) * _sweepRect.Width - _sweepRect.Left;
                for (var x = area.Left; x < area.Right; x++)
                    _out[o + x] = (int)Over((uint)sweep[s + x], (uint)_out[o + x]);
            }
        }

        /// <summary>A premultiplied pixel over an opaque one.</summary>
        private static uint Over(uint top, uint under)
        {
            var a = top >> 24;
            if (a == 0) return under | 0xFF000000u;
            if (a == 255) return top;
            var keep = 256u - a;
            return 0xFF000000u
                | (((top & 0xFF00FFu) + (((under & 0xFF00FFu) * keep) >> 8)) & 0xFF00FFu)
                | (((top & 0x00FF00u) + (((under & 0x00FF00u) * keep) >> 8)) & 0x00FF00u);
        }

        /// <summary>The wordmark's passing light, drawn small, only while it passes.</summary>
        private int[]? SweepLayer()
        {
            if (_wordFairway == null || !SweepActive(_now) || Entered(0.08) < 1f || _modal != null || _dropFade.Value(_now) > 0.001f) return null;
            _sweepRect = Rectangle.Intersect(Rectangle.Ceiling(RectangleF.Inflate(_wordBounds, 4, 4)), new Rectangle(0, 0, W, H));
            using (var layer = new Bitmap(_sweepRect.Width, _sweepRect.Height, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(layer))
                {
                    g.Clear(Color.Transparent);
                    g.TranslateTransform(-_sweepRect.X, -_sweepRect.Y);
                    PaintSweep(g);
                }
                return _sweepPixels = Pixels(layer, _sweepPixels);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public int Size, Width, Height;
            public short Planes, BitCount;
            public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        }

        [DllImport("gdi32.dll")]
        private static extern int SetDIBitsToDevice(IntPtr hdc, int xDest, int yDest, int width, int height, int xSrc, int ySrc,
            int startScan, int scanLines, int[] bits, ref BitmapInfoHeader header, int colorUse);
    }
}
