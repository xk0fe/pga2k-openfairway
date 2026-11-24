using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenFairway.Launcher
{
    /// <summary>The turning hole as tools/render-hole.py exports it (Assets/hole.scene): camera, light, shaders, the
    /// ground's grids, the photo, the drawing, and a still for when the launcher cannot draw live.</summary>
    internal sealed class SceneData
    {
        public int N;
        public float Cell, Square, ShowX, ShowZ, Radius, Outer, PlanHalf, PlaneY;
        public float[] Sun = new float[3], Water = new float[3], Eye = new float[3], Target = new float[3];
        public float FovDegrees, Near, Far, EdgeFade, TurnSeconds;
        public int FrameWidth, FrameHeight, OriginX, OriginY, Supersample;
        public float[] Paper = new float[3], Line = new float[3];
        public string Vertex = "", Fragment = "";
        public float[] Height = new float[0], Ground = new float[0];
        public byte[] Occlusion = new byte[0], Wet = new byte[0], Plan = new byte[0];
        public int PhotoWidth, PhotoHeight, DrawingSize;
        public byte[] Photo = new byte[0], Drawing = new byte[0], Still = new byte[0];

        private static SceneData? _shared;
        private static bool _tried;

        public static SceneData? Shared
        {
            get
            {
                if (_tried) return _shared;
                _tried = true;
                try
                {
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OpenFairway.Launcher.hole.scene"))
                        _shared = stream == null ? null : Read(new BinaryReader(stream));
                }
                catch (Exception e)
                {
                    LauncherLog.Write("The hole's scene could not be read: " + e.Message);
                    _shared = null;
                }
                return _shared;
            }
        }

        private static SceneData Read(BinaryReader r)
        {
            if (new string(r.ReadChars(4)) != "OFS1" || r.ReadInt32() != 1) throw new InvalidDataException("not a hole scene");
            var d = new SceneData { N = r.ReadInt32() };
            d.Cell = r.ReadSingle(); d.Square = r.ReadSingle(); d.ShowX = r.ReadSingle(); d.ShowZ = r.ReadSingle();
            d.Radius = r.ReadSingle(); d.Outer = r.ReadSingle(); d.PlanHalf = r.ReadSingle(); d.PlaneY = r.ReadSingle();
            foreach (var v in new[] { d.Sun, d.Water, d.Eye, d.Target })
                for (var i = 0; i < 3; i++) v[i] = r.ReadSingle();
            d.FovDegrees = r.ReadSingle(); d.Near = r.ReadSingle(); d.Far = r.ReadSingle(); d.EdgeFade = r.ReadSingle(); d.TurnSeconds = r.ReadSingle();
            d.FrameWidth = r.ReadInt32(); d.FrameHeight = r.ReadInt32(); d.OriginX = r.ReadInt32(); d.OriginY = r.ReadInt32(); d.Supersample = r.ReadInt32();
            for (var i = 0; i < 3; i++) d.Paper[i] = r.ReadSingle();
            for (var i = 0; i < 3; i++) d.Line[i] = r.ReadSingle();
            d.Vertex = Encoding.UTF8.GetString(Block(r));
            d.Fragment = Encoding.UTF8.GetString(Block(r));
            var cells = d.N * d.N;
            d.Height = Heights(r, cells);
            d.Ground = Heights(r, cells);
            d.Occlusion = Inflate(Block(r), cells);
            d.Wet = Inflate(Block(r), cells);
            d.Plan = Inflate(Block(r), cells);
            d.PhotoWidth = r.ReadInt32(); d.PhotoHeight = r.ReadInt32();
            d.Photo = Block(r);
            d.DrawingSize = r.ReadInt32();
            d.Drawing = Block(r);
            d.Still = Block(r);
            return d;
        }

        private static byte[] Block(BinaryReader r) => r.ReadBytes(r.ReadInt32());

        private static float[] Heights(BinaryReader r, int count)
        {
            var step = r.ReadSingle();
            var lowest = r.ReadSingle();
            var raw = Inflate(Block(r), count * 2);
            var heights = new float[count];
            for (var i = 0; i < count; i++) heights[i] = lowest + step * (raw[2 * i] | (raw[2 * i + 1] << 8));
            return heights;
        }

        private static byte[] Inflate(byte[] packed, int size)
        {
            var result = new byte[size];
            using (var input = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress))
            {
                var at = 0;
                int read;
                while (at < size && (read = input.Read(result, at, size - at)) > 0) at += read;
            }
            return result;
        }

        public Bitmap StillImage()
        {
            using (var stream = new MemoryStream(Still))
            using (var image = Image.FromStream(stream))
            {
                var still = new Bitmap(FrameWidth, FrameHeight, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(still)) g.DrawImage(image, 0, 0, FrameWidth, FrameHeight);
                return still;
            }
        }
    }

    /// <summary>
    /// Draws the launcher window with OpenGL: its blueprint sheet, the hole turning live in 3D (the same mesh and the
    /// same shaders tools/render-hole.py renders with), the interface over it, and the wordmark's passing light. A
    /// fresh picture every display refresh, so the turn is perfectly smooth at any speed.
    /// </summary>
    internal sealed class LiveScene : IDisposable
    {
        private const int VertexFloats = 12;
        private const int Samples = 4;
        private const float PlaneReach = 1500f;
        private const float GroundNone = -999f;
        private const float RipplesPerTurn = 4f;
        private const float Anisotropy = 16f;
        private const int SwapWithoutWaiting = 0;   // the window's frame pacer already waits for each display refresh

        private readonly SceneData _scene;
        private readonly IntPtr _window;
        private readonly int _width, _height;
        private IntPtr _dc, _context;
        private uint _sceneProgram, _quadProgram, _meshArray, _quadArray;
        private int _indexCount;
        private uint _photo, _drawing, _sheet, _interface, _sweep, _picture;
        private uint _multisampled, _resolved;
        private int _renderWidth, _renderHeight;
        private int _sweepWidth, _sweepHeight;
        private int _mvpAt, _eyeAt, _phaseAt, _rectAt, _alphaAt, _flipAt;

        private LiveScene(SceneData scene, IntPtr window, int width, int height)
        {
            _scene = scene;
            _window = window;
            _width = width;
            _height = height;
        }

        /// <summary>The live scene on a window, or null (and a line in the launcher's log) when it cannot be drawn.</summary>
        public static LiveScene? Start(SceneData scene, IntPtr window, int width, int height)
        {
            var live = new LiveScene(scene, window, width, height);
            try
            {
                if (live.Open()) return live;
            }
            catch (Exception e)
            {
                LauncherLog.Write("Live scene failed: " + e.Message);
            }
            live.Dispose();
            return null;
        }

        private bool Open()
        {
            Gl.LoadLibrary("opengl32.dll");
            _dc = Gl.GetDC(_window);
            var format = new Gl.PixelFormatDescriptor
            {
                Size = (ushort)Marshal.SizeOf(typeof(Gl.PixelFormatDescriptor)), Version = 1,
                Flags = Gl.PfdDrawToWindow | Gl.PfdSupportOpenGl | Gl.PfdDoubleBuffer, ColorBits = 32, AlphaBits = 8, DepthBits = 24,
            };
            var chosen = Gl.ChoosePixelFormat(_dc, ref format);
            if (chosen == 0 || !Gl.SetPixelFormat(_dc, chosen, ref format)) return Fail("no pixel format");
            _context = Gl.wglCreateContext(_dc);
            if (_context == IntPtr.Zero || !Gl.wglMakeCurrent(_dc, _context)) return Fail("no context");
            var version = Marshal.PtrToStringAnsi(Gl.glGetString(Gl.Version)) ?? "";
            LauncherLog.Write("OpenGL " + version);
            if (!Gl.Load()) return Fail("OpenGL 3 functions missing");
            Gl.SwapInterval?.Invoke(SwapWithoutWaiting);

            _sceneProgram = Program(_scene.Vertex, _scene.Fragment, "in_pos", "in_nrm", "in_uv", "in_occ", "in_wet", "in_plan", "in_ground");
            _quadProgram = Program(QuadVertex, QuadFragment);
            if (_sceneProgram == 0 || _quadProgram == 0) return Fail("shaders did not compile");

            BuildMesh();
            Gl.GenVertexArrays(1, out _quadArray);
            _photo = Texture(Decode(_scene.Photo), _scene.PhotoWidth, _scene.PhotoHeight, mipmapped: true);
            _drawing = DrawingTexture();
            _sheet = Texture(null, _width, _height, mipmapped: false);
            _interface = Texture(null, _width, _height, mipmapped: false);
            BuildTargets();
            SetFixedUniforms();
            return true;
        }

        private static bool Fail(string why)
        {
            LauncherLog.Write("Live scene unavailable: " + why);
            return false;
        }

        // ---- building ----

        private uint Program(string vertex, string fragment, params string[] attributes)
        {
            uint Compile(uint type, string source)
            {
                var shader = Gl.CreateShader(type);
                Gl.ShaderSource(shader, 1, new[] { source }, null);
                Gl.CompileShader(shader);
                Gl.GetShaderiv(shader, Gl.CompileStatus, out var ok);
                if (ok != 0) return shader;
                var log = new StringBuilder(4096);
                Gl.GetShaderInfoLog(shader, log.Capacity, out _, log);
                LauncherLog.Write("Shader: " + log);
                return 0;
            }

            var v = Compile(Gl.VertexShader, vertex);
            var f = Compile(Gl.FragmentShader, fragment);
            if (v == 0 || f == 0) return 0;
            var program = Gl.CreateProgram();
            Gl.AttachShader(program, v);
            Gl.AttachShader(program, f);
            for (var i = 0; i < attributes.Length; i++) Gl.BindAttribLocation(program, (uint)i, attributes[i]);
            Gl.LinkProgram(program);
            Gl.GetProgramiv(program, Gl.LinkStatus, out var linked);
            if (linked != 0) return program;
            var message = new StringBuilder(4096);
            Gl.GetProgramInfoLog(program, message.Capacity, out _, message);
            LauncherLog.Write("Program: " + message);
            return 0;
        }

        /// <summary>The same mesh tools/render-hole.py draws: the surveyed grid inside its outer edge, then the plane
        /// the drawing goes on beyond it.</summary>
        private void BuildMesh()
        {
            var s = _scene;
            var n = s.N;
            var half = s.Square / 2;
            var ids = new int[n * n];
            var count = 0;
            for (var i = 0; i < n; i++)
                for (var j = 0; j < n; j++)
                {
                    var x = (j + 0.5f) * s.Cell - half - s.ShowX;
                    var z = (i + 0.5f) * s.Cell - half + s.ShowZ;
                    ids[i * n + j] = Math.Sqrt(x * x + z * z) <= s.Outer ? count++ : -1;
                }

            var vertices = new float[(count + 4) * VertexFloats];
            float Y(int i, int j) => s.Height[i * n + j];
            for (var i = 0; i < n; i++)
                for (var j = 0; j < n; j++)
                {
                    var id = ids[i * n + j];
                    if (id < 0) continue;
                    var x = (j + 0.5f) * s.Cell - half - s.ShowX;
                    var z = (i + 0.5f) * s.Cell - half + s.ShowZ;
                    // Slopes as numpy's gradient takes them: centred inside, one-sided at the grid's edges.
                    var gx = j == 0 ? (Y(i, 1) - Y(i, 0)) / s.Cell : j == n - 1 ? (Y(i, j) - Y(i, j - 1)) / s.Cell : (Y(i, j + 1) - Y(i, j - 1)) / (2 * s.Cell);
                    var gz = i == 0 ? (Y(1, j) - Y(0, j)) / s.Cell : i == n - 1 ? (Y(i, j) - Y(i - 1, j)) / s.Cell : (Y(i + 1, j) - Y(i - 1, j)) / (2 * s.Cell);
                    var length = (float)Math.Sqrt(gx * gx + 1 + gz * gz);
                    var o = id * VertexFloats;
                    vertices[o] = x;
                    vertices[o + 1] = Y(i, j);
                    vertices[o + 2] = z;
                    vertices[o + 3] = -gx / length;
                    vertices[o + 4] = 1 / length;
                    vertices[o + 5] = -gz / length;
                    vertices[o + 6] = (x + s.ShowX + half) / s.Square;
                    vertices[o + 7] = (z - s.ShowZ + half) / s.Square;
                    vertices[o + 8] = s.Occlusion[i * n + j] / 255f;
                    vertices[o + 9] = s.Wet[i * n + j] / 255f;
                    vertices[o + 10] = s.Plan[i * n + j] / 255f;
                    vertices[o + 11] = s.Ground[i * n + j];
                }
            var corners = new[] { (-PlaneReach, -PlaneReach), (PlaneReach, -PlaneReach), (-PlaneReach, PlaneReach), (PlaneReach, PlaneReach) };
            for (var k = 0; k < 4; k++)
            {
                var o = (count + k) * VertexFloats;
                vertices[o] = corners[k].Item1;
                vertices[o + 1] = s.PlaneY;
                vertices[o + 2] = corners[k].Item2;
                vertices[o + 4] = 1;
                vertices[o + 8] = 1;
                vertices[o + 10] = 1;
                vertices[o + 11] = GroundNone;
            }

            var indices = new System.Collections.Generic.List<int>(count * 6);
            for (var i = 0; i + 1 < n; i++)
                for (var j = 0; j + 1 < n; j++)
                {
                    int a = ids[i * n + j], b = ids[i * n + j + 1], c = ids[(i + 1) * n + j], e = ids[(i + 1) * n + j + 1];
                    if (a < 0 || b < 0 || c < 0 || e < 0) continue;
                    indices.Add(a); indices.Add(c); indices.Add(b);
                    indices.Add(b); indices.Add(c); indices.Add(e);
                }
            indices.AddRange(new[] { count, count + 2, count + 1, count + 1, count + 2, count + 3 });
            var indexArray = indices.ToArray();
            _indexCount = indexArray.Length;

            Gl.GenVertexArrays(1, out _meshArray);
            Gl.BindVertexArray(_meshArray);
            Gl.GenBuffers(1, out var vertexBuffer);
            Gl.BindBuffer(Gl.ArrayBuffer, vertexBuffer);
            Gl.BufferDataFloats(Gl.ArrayBuffer, (IntPtr)(vertices.Length * 4), vertices, Gl.StaticDraw);
            Gl.GenBuffers(1, out var indexBuffer);
            Gl.BindBuffer(Gl.ElementArrayBuffer, indexBuffer);
            Gl.BufferDataInts(Gl.ElementArrayBuffer, (IntPtr)(indexArray.Length * 4), indexArray, Gl.StaticDraw);
            var layout = new[] { 3, 3, 2, 1, 1, 1, 1 };
            var offset = 0;
            for (var a = 0; a < layout.Length; a++)
            {
                Gl.VertexAttribPointer((uint)a, layout[a], Gl.Float, 0, VertexFloats * 4, (IntPtr)(offset * 4));
                Gl.EnableVertexAttribArray((uint)a);
                offset += layout[a];
            }
            Gl.BindVertexArray(0);
        }

        private static int[] Decode(byte[] encoded)
        {
            using (var stream = new MemoryStream(encoded))
            using (var image = new Bitmap(stream))
            {
                var pixels = new int[image.Width * image.Height];
                var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                image.UnlockBits(data);
                return pixels;
            }
        }

        private static uint Texture(int[]? pixels, int width, int height, bool mipmapped)
        {
            Gl.glGenTextures(1, out var texture);
            Gl.glBindTexture(Gl.Texture2D, texture);
            Gl.glPixelStorei(Gl.UnpackAlignment, 4);
            if (pixels != null) Gl.glTexImage2D(Gl.Texture2D, 0, Gl.Rgba8, width, height, 0, Gl.Bgra, Gl.UnsignedByte, pixels);
            else Gl.glTexImage2D(Gl.Texture2D, 0, Gl.Rgba8, width, height, 0, Gl.Bgra, Gl.UnsignedByte, IntPtr.Zero);
            Filtering(mipmapped);
            return texture;
        }

        private uint DrawingTexture()
        {
            var rgba = Decode(_scene.Drawing);
            var size = _scene.DrawingSize;
            var ink = new byte[size * size];
            for (var i = 0; i < ink.Length; i++) ink[i] = (byte)(rgba[i] & 0xFF);
            Gl.glGenTextures(1, out var texture);
            Gl.glBindTexture(Gl.Texture2D, texture);
            Gl.glPixelStorei(Gl.UnpackAlignment, 1);
            Gl.glTexImage2D(Gl.Texture2D, 0, Gl.R8, size, size, 0, Gl.Red, Gl.UnsignedByte, ink);
            Gl.glPixelStorei(Gl.UnpackAlignment, 4);
            Filtering(mipmapped: true);
            return texture;
        }

        private static void Filtering(bool mipmapped)
        {
            if (mipmapped)
            {
                Gl.GenerateMipmap(Gl.Texture2D);
                Gl.glTexParameteri(Gl.Texture2D, Gl.TextureMinFilter, Gl.LinearMipmapLinear);
                Gl.glTexParameterf(Gl.Texture2D, Gl.TextureMaxAnisotropy, Anisotropy);
                Gl.glGetError();   // anisotropy is an extension; without it the texture is still fine
            }
            else Gl.glTexParameteri(Gl.Texture2D, Gl.TextureMinFilter, Gl.Linear);
            Gl.glTexParameteri(Gl.Texture2D, Gl.TextureMagFilter, Gl.Linear);
            Gl.glTexParameteri(Gl.Texture2D, Gl.TextureWrapS, Gl.ClampToEdge);
            Gl.glTexParameteri(Gl.Texture2D, Gl.TextureWrapT, Gl.ClampToEdge);
        }

        /// <summary>The hole is drawn at twice the size with multisampling, then halved when it goes on screen.</summary>
        private void BuildTargets()
        {
            _renderWidth = _scene.FrameWidth * _scene.Supersample;
            _renderHeight = _scene.FrameHeight * _scene.Supersample;
            Gl.glGetIntegerv(Gl.MaxSamples, out var most);
            var samples = Math.Max(1, Math.Min(Samples, most));

            Gl.GenFramebuffers(1, out _multisampled);
            Gl.BindFramebuffer(Gl.Framebuffer, _multisampled);
            Gl.GenRenderbuffers(1, out var colour);
            Gl.BindRenderbuffer(Gl.Renderbuffer, colour);
            Gl.RenderbufferStorageMultisample(Gl.Renderbuffer, samples, (uint)Gl.Rgba8, _renderWidth, _renderHeight);
            Gl.FramebufferRenderbuffer(Gl.Framebuffer, Gl.ColorAttachment0, Gl.Renderbuffer, colour);
            Gl.GenRenderbuffers(1, out var depth);
            Gl.BindRenderbuffer(Gl.Renderbuffer, depth);
            Gl.RenderbufferStorageMultisample(Gl.Renderbuffer, samples, Gl.DepthComponent24, _renderWidth, _renderHeight);
            Gl.FramebufferRenderbuffer(Gl.Framebuffer, Gl.DepthAttachment, Gl.Renderbuffer, depth);
            if (Gl.CheckFramebufferStatus(Gl.Framebuffer) != Gl.FramebufferComplete) throw new InvalidOperationException("multisampled target incomplete");

            _picture = Texture(null, _renderWidth, _renderHeight, mipmapped: false);
            Gl.GenFramebuffers(1, out _resolved);
            Gl.BindFramebuffer(Gl.Framebuffer, _resolved);
            Gl.FramebufferTexture2D(Gl.Framebuffer, Gl.ColorAttachment0, Gl.Texture2D, _picture, 0);
            if (Gl.CheckFramebufferStatus(Gl.Framebuffer) != Gl.FramebufferComplete) throw new InvalidOperationException("resolve target incomplete");
            Gl.BindFramebuffer(Gl.Framebuffer, 0);
        }

        private int Uniform(string name) => Gl.GetUniformLocation(_sceneProgram, name);

        private void SetFixedUniforms()
        {
            var s = _scene;
            Gl.UseProgram(_sceneProgram);
            var sunLength = (float)Math.Sqrt(s.Sun[0] * s.Sun[0] + s.Sun[1] * s.Sun[1] + s.Sun[2] * s.Sun[2]);
            Gl.Uniform3f(Uniform("u_sun"), s.Sun[0] / sunLength, s.Sun[1] / sunLength, s.Sun[2] / sunLength);
            Gl.Uniform3f(Uniform("u_paper"), Linear(s.Paper[0]), Linear(s.Paper[1]), Linear(s.Paper[2]));
            Gl.Uniform3f(Uniform("u_line"), Linear(s.Line[0]), Linear(s.Line[1]), Linear(s.Line[2]));
            Gl.Uniform3f(Uniform("u_water"), s.Water[0], s.Water[1], s.Water[2]);
            Gl.Uniform1i(Uniform("u_photo"), 0);
            Gl.Uniform1i(Uniform("u_map"), 1);
            Gl.Uniform1f(Uniform("u_map_half"), s.PlanHalf);
            Gl.Uniform2f(Uniform("u_size"), _renderWidth, _renderHeight);
            Gl.Uniform1f(Uniform("u_edge"), s.EdgeFade);
            Gl.Uniform1f(Uniform("u_outer"), s.Outer);
            Gl.Uniform1f(Uniform("u_live"), 1);
            _mvpAt = Uniform("u_mvp");
            _eyeAt = Uniform("u_eye");
            _phaseAt = Uniform("u_phase");
            Gl.UseProgram(_quadProgram);
            _rectAt = Gl.GetUniformLocation(_quadProgram, "u_rect");
            _alphaAt = Gl.GetUniformLocation(_quadProgram, "u_alpha");
            _flipAt = Gl.GetUniformLocation(_quadProgram, "u_flip");
            Gl.Uniform1i(Gl.GetUniformLocation(_quadProgram, "u_image"), 0);
            Gl.Uniform2f(Gl.GetUniformLocation(_quadProgram, "u_window"), _width, _height);
        }

        private static float Linear(float srgb) => (float)Math.Pow(srgb, 2.2);

        // ---- each frame ----

        public void UploadSheet(int[] pixels) => Upload(_sheet, pixels, _width, _height);

        public void UploadInterface(int[] pixels) => Upload(_interface, pixels, _width, _height);

        public void UploadSweep(int[] pixels, int width, int height)
        {
            Gl.wglMakeCurrent(_dc, _context);
            if (_sweep == 0 || width != _sweepWidth || height != _sweepHeight)
            {
                _sweep = Texture(pixels, width, height, mipmapped: false);
                _sweepWidth = width;
                _sweepHeight = height;
            }
            else Upload(_sweep, pixels, width, height);
        }

        private void Upload(uint texture, int[] pixels, int width, int height)
        {
            Gl.wglMakeCurrent(_dc, _context);
            Gl.glBindTexture(Gl.Texture2D, texture);
            Gl.glTexSubImage2D(Gl.Texture2D, 0, 0, 0, width, height, Gl.Bgra, Gl.UnsignedByte, pixels);
        }

        /// <summary>One picture: the sheet, the hole at this moment (faded in by <paramref name="holeAlpha"/>), the
        /// interface, and the wordmark's light where <paramref name="sweep"/> is not empty.</summary>
        public void Present(double seconds, float holeAlpha, Rectangle sweep)
        {
            Gl.wglMakeCurrent(_dc, _context);
            if (holeAlpha > 0f) DrawHole(seconds);

            Gl.BindFramebuffer(Gl.Framebuffer, 0);
            Gl.glViewport(0, 0, _width, _height);
            Gl.glDisable(Gl.DepthTest);
            Gl.UseProgram(_quadProgram);
            Gl.BindVertexArray(_quadArray);
            Gl.ActiveTexture(Gl.Texture0);
            Gl.glDisable(Gl.Blend);
            Quad(_sheet, new Rectangle(0, 0, _width, _height), 1f, flip: false);
            Gl.glEnable(Gl.Blend);
            Gl.glBlendFunc(Gl.One, Gl.OneMinusSrcAlpha);
            if (holeAlpha > 0f)
                Quad(_picture, new Rectangle(_scene.OriginX, _scene.OriginY, _scene.FrameWidth, _scene.FrameHeight), holeAlpha, flip: true);
            Quad(_interface, new Rectangle(0, 0, _width, _height), 1f, flip: false);
            if (!sweep.IsEmpty && _sweep != 0) Quad(_sweep, sweep, 1f, flip: false);
            Gl.SwapBuffers(_dc);
        }

        private void Quad(uint texture, Rectangle rect, float alpha, bool flip)
        {
            Gl.glBindTexture(Gl.Texture2D, texture);
            Gl.Uniform4f(_rectAt, rect.X, rect.Y, rect.Width, rect.Height);
            Gl.Uniform1f(_alphaAt, alpha);
            Gl.Uniform1f(_flipAt, flip ? 1f : 0f);
            Gl.glDrawArrays(Gl.TriangleStrip, 0, 4);
        }

        private void DrawHole(double seconds)
        {
            var s = _scene;
            var turn = 2 * Math.PI * seconds / s.TurnSeconds;
            double c = Math.Cos(turn), sn = Math.Sin(turn);
            var rotation = new double[,] { { c, 0, sn, 0 }, { 0, 1, 0, 0 }, { -sn, 0, c, 0 }, { 0, 0, 0, 1 } };
            var eye = new[] { (double)s.Eye[0], s.Eye[1], s.Eye[2] };
            var view = LookAt(eye, new double[] { s.Target[0], s.Target[1], s.Target[2] });
            var projection = Perspective(s.FovDegrees * Math.PI / 180, (double)_renderWidth / _renderHeight, s.Near, s.Far);
            var mvp = Multiply(Multiply(projection, view), rotation);
            var columns = new float[16];
            for (var row = 0; row < 4; row++)
                for (var col = 0; col < 4; col++) columns[col * 4 + row] = (float)mvp[row, col];

            Gl.BindFramebuffer(Gl.Framebuffer, _multisampled);
            Gl.glViewport(0, 0, _renderWidth, _renderHeight);
            Gl.glClearColor(0, 0, 0, 0);
            Gl.glClear(Gl.ColorBufferBit | Gl.DepthBufferBit);
            Gl.glEnable(Gl.DepthTest);
            Gl.glDisable(Gl.Blend);
            Gl.UseProgram(_sceneProgram);
            Gl.UniformMatrix4fv(_mvpAt, 1, 0, columns);
            // The eye in the ground's own frame: the turn undone.
            Gl.Uniform3f(_eyeAt, (float)(c * eye[0] - sn * eye[2]), (float)eye[1], (float)(sn * eye[0] + c * eye[2]));
            Gl.Uniform1f(_phaseAt, (float)(2 * Math.PI * RipplesPerTurn * seconds / s.TurnSeconds));
            Gl.ActiveTexture(Gl.Texture0);
            Gl.glBindTexture(Gl.Texture2D, _photo);
            Gl.ActiveTexture(Gl.Texture0 + 1);
            Gl.glBindTexture(Gl.Texture2D, _drawing);
            Gl.BindVertexArray(_meshArray);
            Gl.glDrawElements(Gl.Triangles, _indexCount, Gl.UnsignedInt, IntPtr.Zero);
            Gl.BindVertexArray(0);
            Gl.ActiveTexture(Gl.Texture0);

            Gl.BindFramebuffer(Gl.ReadFramebuffer, _multisampled);
            Gl.BindFramebuffer(Gl.DrawFramebuffer, _resolved);
            Gl.BlitFramebuffer(0, 0, _renderWidth, _renderHeight, 0, 0, _renderWidth, _renderHeight, Gl.ColorBufferBit, (uint)Gl.Nearest);
        }

        // ---- the camera, as tools/render-hole.py sets it up ----

        private static double[,] Perspective(double fov, double aspect, double near, double far)
        {
            var f = 1 / Math.Tan(fov / 2);
            return new[,] { { f / aspect, 0, 0, 0 }, { 0, f, 0, 0 }, { 0, 0, (far + near) / (near - far), 2 * far * near / (near - far) }, { 0, 0, -1, 0 } };
        }

        private static double[,] LookAt(double[] eye, double[] target)
        {
            var f = Normalise(new[] { target[0] - eye[0], target[1] - eye[1], target[2] - eye[2] });
            var s = Normalise(Cross(f, new double[] { 0, 1, 0 }));
            var u = Cross(s, f);
            var m = new double[,] { { s[0], s[1], s[2], 0 }, { u[0], u[1], u[2], 0 }, { -f[0], -f[1], -f[2], 0 }, { 0, 0, 0, 1 } };
            for (var row = 0; row < 3; row++) m[row, 3] = -(m[row, 0] * eye[0] + m[row, 1] * eye[1] + m[row, 2] * eye[2]);
            return m;
        }

        private static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

        private static double[] Normalise(double[] v)
        {
            var length = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return new[] { v[0] / length, v[1] / length, v[2] / length };
        }

        private static double[,] Multiply(double[,] a, double[,] b)
        {
            var m = new double[4, 4];
            for (var i = 0; i < 4; i++)
                for (var j = 0; j < 4; j++)
                    for (var k = 0; k < 4; k++) m[i, j] += a[i, k] * b[k, j];
            return m;
        }

        public void Dispose()
        {
            if (_context != IntPtr.Zero)
            {
                Gl.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                Gl.wglDeleteContext(_context);
                _context = IntPtr.Zero;
            }
            if (_dc != IntPtr.Zero)
            {
                Gl.ReleaseDC(_window, _dc);
                _dc = IntPtr.Zero;
            }
        }

        private const string QuadVertex = @"#version 330
uniform vec4 u_rect;     // x, y, width, height in window pixels, from the top left
uniform vec2 u_window;
out vec2 v_uv;
void main() {
    vec2 corner = vec2(float(gl_VertexID & 1), float(gl_VertexID >> 1));
    vec2 pixel = u_rect.xy + corner * u_rect.zw;
    gl_Position = vec4(pixel.x / u_window.x * 2.0 - 1.0, 1.0 - pixel.y / u_window.y * 2.0, 0.0, 1.0);
    v_uv = corner;
}";

        private const string QuadFragment = @"#version 330
uniform sampler2D u_image;
uniform float u_alpha;
uniform float u_flip;    // 1 for pictures OpenGL drew, which start at the bottom
in vec2 v_uv;
out vec4 f_colour;
void main() {
    f_colour = texture(u_image, vec2(v_uv.x, mix(v_uv.y, 1.0 - v_uv.y, u_flip))) * u_alpha;
}";
    }
}
