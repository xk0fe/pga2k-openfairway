using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenFairway.Launcher
{
    /// <summary>The few OpenGL calls the launcher's live scene needs: Windows' opengl32 for OpenGL 1.1, the rest
    /// loaded from the driver once a context exists.</summary>
    internal static class Gl
    {
        public const uint ColorBufferBit = 0x4000, DepthBufferBit = 0x100;
        public const uint DepthTest = 0x0B71, Blend = 0x0BE2, ScissorTest = 0x0C11;
        public const uint One = 1, OneMinusSrcAlpha = 0x0303;
        public const uint Triangles = 4, TriangleStrip = 5;
        public const uint UnsignedByte = 0x1401, UnsignedInt = 0x1405, Float = 0x1406;
        public const uint Texture2D = 0x0DE1, Texture0 = 0x84C0;
        public const uint TextureMinFilter = 0x2801, TextureMagFilter = 0x2800, TextureWrapS = 0x2802, TextureWrapT = 0x2803;
        public const uint TextureMaxAnisotropy = 0x84FE;
        public const int Linear = 0x2601, Nearest = 0x2600, LinearMipmapLinear = 0x2703, ClampToEdge = 0x812F;
        public const int Rgba8 = 0x8058, R8 = 0x8229;
        public const uint Bgra = 0x80E1, Red = 0x1903, Rgba = 0x1908;
        public const uint UnpackAlignment = 0x0CF5, PackAlignment = 0x0D05;
        public const uint ArrayBuffer = 0x8892, ElementArrayBuffer = 0x8893, StaticDraw = 0x88E4;
        public const uint VertexShader = 0x8B31, FragmentShader = 0x8B30, CompileStatus = 0x8B81, LinkStatus = 0x8B82;
        public const uint Framebuffer = 0x8D40, ReadFramebuffer = 0x8CA8, DrawFramebuffer = 0x8CA9, Renderbuffer = 0x8D41;
        public const uint ColorAttachment0 = 0x8CE0, DepthAttachment = 0x8D00, DepthComponent24 = 0x81A6, FramebufferComplete = 0x8CD5;
        public const uint MaxSamples = 0x8D57, Version = 0x1F02;

        // ---- Windows ----

        [StructLayout(LayoutKind.Sequential)]
        public struct PixelFormatDescriptor
        {
            public ushort Size, Version;
            public uint Flags;
            public byte PixelType, ColorBits, RedBits, RedShift, GreenBits, GreenShift, BlueBits, BlueShift, AlphaBits, AlphaShift;
            public byte AccumBits, AccumRedBits, AccumGreenBits, AccumBlueBits, AccumAlphaBits, DepthBits, StencilBits, AuxBuffers, LayerType, Reserved;
            public uint LayerMask, VisibleMask, DamageMask;
        }

        public const uint PfdDrawToWindow = 0x4, PfdSupportOpenGl = 0x20, PfdDoubleBuffer = 0x1;

        [DllImport("kernel32.dll")] public static extern IntPtr LoadLibrary(string name);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern int ChoosePixelFormat(IntPtr dc, ref PixelFormatDescriptor descriptor);
        [DllImport("gdi32.dll")] public static extern bool SetPixelFormat(IntPtr dc, int format, ref PixelFormatDescriptor descriptor);
        [DllImport("gdi32.dll")] public static extern bool SwapBuffers(IntPtr dc);
        [DllImport("opengl32.dll")] public static extern IntPtr wglCreateContext(IntPtr dc);
        [DllImport("opengl32.dll")] public static extern bool wglMakeCurrent(IntPtr dc, IntPtr context);
        [DllImport("opengl32.dll")] public static extern bool wglDeleteContext(IntPtr context);
        [DllImport("opengl32.dll")] public static extern IntPtr wglGetProcAddress(string name);

        // ---- OpenGL 1.1 ----

        [DllImport("opengl32.dll")] public static extern void glViewport(int x, int y, int width, int height);
        [DllImport("opengl32.dll")] public static extern void glScissor(int x, int y, int width, int height);
        [DllImport("opengl32.dll")] public static extern void glClearColor(float r, float g, float b, float a);
        [DllImport("opengl32.dll")] public static extern void glClear(uint mask);
        [DllImport("opengl32.dll")] public static extern void glEnable(uint capability);
        [DllImport("opengl32.dll")] public static extern void glDisable(uint capability);
        [DllImport("opengl32.dll")] public static extern void glBlendFunc(uint source, uint destination);
        [DllImport("opengl32.dll")] public static extern void glDrawElements(uint mode, int count, uint type, IntPtr indices);
        [DllImport("opengl32.dll")] public static extern void glDrawArrays(uint mode, int first, int count);
        [DllImport("opengl32.dll")] public static extern void glGenTextures(int count, out uint texture);
        [DllImport("opengl32.dll")] public static extern void glBindTexture(uint target, uint texture);
        [DllImport("opengl32.dll")] public static extern void glTexParameteri(uint target, uint name, int value);
        [DllImport("opengl32.dll")] public static extern void glTexParameterf(uint target, uint name, float value);
        [DllImport("opengl32.dll")] public static extern void glPixelStorei(uint name, int value);
        [DllImport("opengl32.dll")] public static extern void glTexImage2D(uint target, int level, int internalFormat, int width, int height, int border, uint format, uint type, IntPtr pixels);
        [DllImport("opengl32.dll")] public static extern void glTexImage2D(uint target, int level, int internalFormat, int width, int height, int border, uint format, uint type, int[] pixels);
        [DllImport("opengl32.dll")] public static extern void glTexImage2D(uint target, int level, int internalFormat, int width, int height, int border, uint format, uint type, byte[] pixels);
        [DllImport("opengl32.dll")] public static extern void glTexSubImage2D(uint target, int level, int x, int y, int width, int height, uint format, uint type, int[] pixels);
        [DllImport("opengl32.dll")] public static extern void glReadPixels(int x, int y, int width, int height, uint format, uint type, int[] pixels);
        [DllImport("opengl32.dll")] public static extern void glGetIntegerv(uint name, out int value);
        [DllImport("opengl32.dll")] public static extern IntPtr glGetString(uint name);
        [DllImport("opengl32.dll")] public static extern uint glGetError();
        [DllImport("opengl32.dll")] public static extern void glFinish();

        // ---- OpenGL 2 and later, from the driver ----

        public delegate uint CreateShaderFn(uint type);
        public delegate void ShaderSourceFn(uint shader, int count, string[] sources, int[]? lengths);
        public delegate void CompileShaderFn(uint shader);
        public delegate void GetShaderivFn(uint shader, uint name, out int value);
        public delegate void GetInfoLogFn(uint item, int capacity, out int length, StringBuilder log);
        public delegate uint CreateProgramFn();
        public delegate void AttachShaderFn(uint program, uint shader);
        public delegate void BindAttribLocationFn(uint program, uint index, string name);
        public delegate void LinkProgramFn(uint program);
        public delegate void UseProgramFn(uint program);
        public delegate int GetUniformLocationFn(uint program, string name);
        public delegate void Uniform1iFn(int location, int value);
        public delegate void Uniform1fFn(int location, float value);
        public delegate void Uniform2fFn(int location, float x, float y);
        public delegate void Uniform3fFn(int location, float x, float y, float z);
        public delegate void Uniform4fFn(int location, float x, float y, float z, float w);
        public delegate void UniformMatrix4fvFn(int location, int count, byte transpose, float[] value);
        public delegate void GenFn(int count, out uint item);
        public delegate void BindFn(uint target, uint item);
        public delegate void BindVertexArrayFn(uint array);
        public delegate void BufferDataFloatsFn(uint target, IntPtr size, float[] data, uint usage);
        public delegate void BufferDataIntsFn(uint target, IntPtr size, int[] data, uint usage);
        public delegate void VertexAttribPointerFn(uint index, int size, uint type, byte normalized, int stride, IntPtr offset);
        public delegate void EnableVertexAttribArrayFn(uint index);
        public delegate void ActiveTextureFn(uint unit);
        public delegate void GenerateMipmapFn(uint target);
        public delegate void RenderbufferStorageMultisampleFn(uint target, int samples, uint format, int width, int height);
        public delegate void FramebufferRenderbufferFn(uint target, uint attachment, uint renderbufferTarget, uint renderbuffer);
        public delegate void FramebufferTexture2DFn(uint target, uint attachment, uint textureTarget, uint texture, int level);
        public delegate uint CheckFramebufferStatusFn(uint target);
        public delegate void BlitFramebufferFn(int sx0, int sy0, int sx1, int sy1, int dx0, int dy0, int dx1, int dy1, uint mask, uint filter);
        public delegate int SwapIntervalFn(int interval);

        public static CreateShaderFn CreateShader = null!;
        public static ShaderSourceFn ShaderSource = null!;
        public static CompileShaderFn CompileShader = null!;
        public static GetShaderivFn GetShaderiv = null!, GetProgramiv = null!;
        public static GetInfoLogFn GetShaderInfoLog = null!, GetProgramInfoLog = null!;
        public static CreateProgramFn CreateProgram = null!;
        public static AttachShaderFn AttachShader = null!;
        public static BindAttribLocationFn BindAttribLocation = null!;
        public static LinkProgramFn LinkProgram = null!;
        public static UseProgramFn UseProgram = null!;
        public static GetUniformLocationFn GetUniformLocation = null!;
        public static Uniform1iFn Uniform1i = null!;
        public static Uniform1fFn Uniform1f = null!;
        public static Uniform2fFn Uniform2f = null!;
        public static Uniform3fFn Uniform3f = null!;
        public static Uniform4fFn Uniform4f = null!;
        public static UniformMatrix4fvFn UniformMatrix4fv = null!;
        public static GenFn GenBuffers = null!, GenVertexArrays = null!, GenFramebuffers = null!, GenRenderbuffers = null!;
        public static BindFn BindBuffer = null!, BindFramebuffer = null!, BindRenderbuffer = null!;
        public static BindVertexArrayFn BindVertexArray = null!;
        public static BufferDataFloatsFn BufferDataFloats = null!;
        public static BufferDataIntsFn BufferDataInts = null!;
        public static VertexAttribPointerFn VertexAttribPointer = null!;
        public static EnableVertexAttribArrayFn EnableVertexAttribArray = null!;
        public static ActiveTextureFn ActiveTexture = null!;
        public static GenerateMipmapFn GenerateMipmap = null!;
        public static RenderbufferStorageMultisampleFn RenderbufferStorageMultisample = null!;
        public static FramebufferRenderbufferFn FramebufferRenderbuffer = null!;
        public static FramebufferTexture2DFn FramebufferTexture2D = null!;
        public static CheckFramebufferStatusFn CheckFramebufferStatus = null!;
        public static BlitFramebufferFn BlitFramebuffer = null!;
        public static SwapIntervalFn? SwapInterval;

        /// <summary>Loads the calls above from the current context's driver. False when one is missing (OpenGL older
        /// than 3.0, or a software renderer).</summary>
        public static bool Load()
        {
            T? Get<T>(string name) where T : Delegate
            {
                var address = wglGetProcAddress(name);
                var value = address.ToInt64();
                return value == 0 || value == 1 || value == 2 || value == 3 || value == -1 ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
            }

            try
            {
                CreateShader = Get<CreateShaderFn>("glCreateShader")!;
                ShaderSource = Get<ShaderSourceFn>("glShaderSource")!;
                CompileShader = Get<CompileShaderFn>("glCompileShader")!;
                GetShaderiv = Get<GetShaderivFn>("glGetShaderiv")!;
                GetProgramiv = Get<GetShaderivFn>("glGetProgramiv")!;
                GetShaderInfoLog = Get<GetInfoLogFn>("glGetShaderInfoLog")!;
                GetProgramInfoLog = Get<GetInfoLogFn>("glGetProgramInfoLog")!;
                CreateProgram = Get<CreateProgramFn>("glCreateProgram")!;
                AttachShader = Get<AttachShaderFn>("glAttachShader")!;
                BindAttribLocation = Get<BindAttribLocationFn>("glBindAttribLocation")!;
                LinkProgram = Get<LinkProgramFn>("glLinkProgram")!;
                UseProgram = Get<UseProgramFn>("glUseProgram")!;
                GetUniformLocation = Get<GetUniformLocationFn>("glGetUniformLocation")!;
                Uniform1i = Get<Uniform1iFn>("glUniform1i")!;
                Uniform1f = Get<Uniform1fFn>("glUniform1f")!;
                Uniform2f = Get<Uniform2fFn>("glUniform2f")!;
                Uniform3f = Get<Uniform3fFn>("glUniform3f")!;
                Uniform4f = Get<Uniform4fFn>("glUniform4f")!;
                UniformMatrix4fv = Get<UniformMatrix4fvFn>("glUniformMatrix4fv")!;
                GenBuffers = Get<GenFn>("glGenBuffers")!;
                GenVertexArrays = Get<GenFn>("glGenVertexArrays")!;
                GenFramebuffers = Get<GenFn>("glGenFramebuffers")!;
                GenRenderbuffers = Get<GenFn>("glGenRenderbuffers")!;
                BindBuffer = Get<BindFn>("glBindBuffer")!;
                BindFramebuffer = Get<BindFn>("glBindFramebuffer")!;
                BindRenderbuffer = Get<BindFn>("glBindRenderbuffer")!;
                BindVertexArray = Get<BindVertexArrayFn>("glBindVertexArray")!;
                BufferDataFloats = Get<BufferDataFloatsFn>("glBufferData")!;
                BufferDataInts = Get<BufferDataIntsFn>("glBufferData")!;
                VertexAttribPointer = Get<VertexAttribPointerFn>("glVertexAttribPointer")!;
                EnableVertexAttribArray = Get<EnableVertexAttribArrayFn>("glEnableVertexAttribArray")!;
                ActiveTexture = Get<ActiveTextureFn>("glActiveTexture")!;
                GenerateMipmap = Get<GenerateMipmapFn>("glGenerateMipmap")!;
                RenderbufferStorageMultisample = Get<RenderbufferStorageMultisampleFn>("glRenderbufferStorageMultisample")!;
                FramebufferRenderbuffer = Get<FramebufferRenderbufferFn>("glFramebufferRenderbuffer")!;
                FramebufferTexture2D = Get<FramebufferTexture2DFn>("glFramebufferTexture2D")!;
                CheckFramebufferStatus = Get<CheckFramebufferStatusFn>("glCheckFramebufferStatus")!;
                BlitFramebuffer = Get<BlitFramebufferFn>("glBlitFramebuffer")!;
                SwapInterval = Get<SwapIntervalFn>("wglSwapIntervalEXT");
                return new Delegate?[]
                {
                    CreateShader, ShaderSource, CompileShader, GetShaderiv, GetProgramiv, GetShaderInfoLog, GetProgramInfoLog, CreateProgram,
                    AttachShader, BindAttribLocation, LinkProgram, UseProgram, GetUniformLocation, Uniform1i, Uniform1f, Uniform2f, Uniform3f,
                    Uniform4f, UniformMatrix4fv, GenBuffers, GenVertexArrays, GenFramebuffers, GenRenderbuffers, BindBuffer, BindFramebuffer,
                    BindRenderbuffer, BindVertexArray, BufferDataFloats, BufferDataInts, VertexAttribPointer, EnableVertexAttribArray,
                    ActiveTexture, GenerateMipmap, RenderbufferStorageMultisample, FramebufferRenderbuffer, FramebufferTexture2D,
                    CheckFramebufferStatus, BlitFramebuffer,
                }.All(d => d != null);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool All(this Delegate?[] items, Func<Delegate?, bool> test)
        {
            foreach (var item in items)
                if (!test(item)) return false;
            return true;
        }
    }
}
