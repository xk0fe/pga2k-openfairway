using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using OpenFairway.API;
using OpenFairway.Core.Il2Cpp;

namespace OpenFairway.Core.Games;

/// <summary>
/// A full-screen custom screen for mods, drawn with the engine's own UI components and the game's own fonts and
/// interface sprites (rounded panels, soft shadows, icons), on top of the game. The mod describes the screen in a
/// 1920x1080 space through <see cref="IUiCanvas"/>; this class scales it to the window, animates it, tracks the
/// mouse and Escape, and uses the game's own "block all input" switch so nothing underneath reacts.
/// </summary>
internal sealed unsafe class UiScreen : IUiScreen, IUiCanvas
{
    private const float VirtualWidth = 1920f, VirtualHeight = 1080f;
    private const double FadeSeconds = 0.16, HoverSeconds = 0.10, EnterSeconds = 0.30, TweenSeconds = 0.60, TooltipDelay = 0.40;

    private readonly GameRuntime _rt;
    private readonly UiToolkit _kit;
    private readonly List<IntPtr> _oldPages = new();
    private readonly List<HitArea> _hits = new();
    private readonly List<Entrance> _entrances = new();
    private readonly List<Tween> _tweens = new();
    private readonly Dictionary<string, double> _shown = new();
    private readonly List<Drift> _drifts = new();
    private readonly List<PulseHalo> _pulses = new();
    private uint _textShadow;
    private float _textShadowDistance;
    private IntPtr _canvasObject, _canvasTransform, _page, _pageTransform, _pageGroup, _tooltip;
    private IntPtr _parent;
    private float _originX, _originY;
    private int _width, _height;
    private float _scale, _offsetX, _offsetY;
    private double _fadeStart, _lastTime, _hoverSince;
    private bool _dirty, _animateNext, _mouseWasDown, _escapeWasDown;
    private HitArea? _hovered, _pressed, _tooltipFor;
    private (float X, float Y, float W, float H) _hoverNotified = (-1, -1, -1, -1);
    private (float X, float Y)? _testHover;

    // Keyboard and gamepad navigation: a focus that moves between clickable areas.
    private HitArea? _focus, _ringFor;
    private bool _keyMode, _acceptWasDown;
    private float _focusX, _focusY, _lastMouseX = -1, _lastMouseY;
    private int _heldDirection = -1;
    private double _nextRepeat;
    private IntPtr _focusRing;

    private sealed class HitArea
    {
        public float X, Y, W, H;
        public IntPtr Image, Container;
        public uint Fill, Hover;
        public float Blend, Shown = -1f;
        public string? Tooltip;
        public Action? Click, OnHover;
    }

    private sealed class Entrance
    {
        public IntPtr Transform, Group;
        public double Start;
        public float Rise;
    }

    private sealed class Tween
    {
        public IntPtr Target;
        public string Key = "";
        public double From, To, Start;
        public Func<double, string>? Format; // a number label
        public float Width, Height;          // a meter
        public bool Fill;                    // a ring: the image's fill amount
        public bool Move;                    // a slide: the element's position and size
        public float FromX, FromY, FromW, FromH, ToX, ToY, ToW, ToH;
        public double Duration = TweenSeconds;
    }

    /// <summary>A backdrop photo that slowly zooms and pans within its crop.</summary>
    private sealed class Drift
    {
        public IntPtr Component;
        public float U, V, W, H, Amount, Phase;
    }

    private sealed class PulseHalo
    {
        public IntPtr Graphic;
        public uint Color;
    }

    public UiScreen(GameRuntime runtime, UiToolkit kit)
    {
        _rt = runtime;
        _kit = kit;
    }

    public Action<IUiCanvas>? Render { get; set; }
    public Action? OnBack { get; set; }
    public bool IsOpen { get; private set; }
    public bool HideGameUi { get; set; }
    public bool Entering { get; private set; }

    public void Open()
    {
        if (IsOpen || !_kit.Resolve()) return;
        _canvasObject = _kit.NewCanvas();
        _canvasTransform = _rt.Invoke(_kit.GoGetTransform, _canvasObject);
        IsOpen = true;
        _dirty = true;
        _animateNext = true;
        _width = _height = 0;
        _mouseWasDown = true; // ignore the click or button press that opened the screen
        _escapeWasDown = true;
        _acceptWasDown = true;
        _lastMouseX = -1;
        _testHover = null;
        _hoverNotified = (-1, -1, -1, -1);
        _kit.BlockGameInput(true);
        _kit.Register(this);
        if (HideGameUi) _kit.ShowGameCanvases(false, _canvasObject);
        Top = this;
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        if (Top == this) Top = null;
        _kit.Unregister(this);
        _kit.BlockGameInput(false);
        _kit.ShowGameCanvases(true, IntPtr.Zero);
        _oldPages.Clear();
        _entrances.Clear();
        _tweens.Clear();
        _drifts.Clear();
        _pulses.Clear();
        _hits.Clear();
        _page = _pageTransform = _pageGroup = _tooltip = _focusRing = IntPtr.Zero;
        _hovered = _pressed = _tooltipFor = _focus = _ringFor = null;
        if (_rt.IsAlive(_canvasObject)) _rt.Invoke(_kit.Destroy, IntPtr.Zero, _canvasObject);
        _canvasObject = IntPtr.Zero;
    }

    public void Invalidate(bool animate = false)
    {
        _dirty = true;
        _animateNext |= animate;
    }

    /// <summary>The screen opened last, for the developer console's test commands.</summary>
    internal static UiScreen? Top;

    /// <summary>Test hook: clicks whatever is at a point of the 1920x1080 space, as the mouse would.</summary>
    internal string TestClick(float x, float y)
    {
        for (var i = _hits.Count - 1; i >= 0; i--)
        {
            var h = _hits[i];
            if (h.Click == null || x < h.X || x >= h.X + h.W || y < h.Y || y >= h.Y + h.H) continue;
            h.Click();
            return $"clicked {h.X},{h.Y} {h.W}x{h.H}";
        }
        return "nothing there";
    }

    /// <summary>Test hook: gamepad button bits to use instead of a real controller (null for the real one).</summary>
    internal static int? TestPad;

    internal string TestBack() { OnBack?.Invoke(); return IsOpen ? "open" : "closed"; }

    /// <summary>Test hook: pretends the pointer rests on a point, to show hover states and tooltips.</summary>
    internal string TestHover(float x, float y)
    {
        _testHover = x < 0 ? null : (x, y);
        return "ok";
    }

    /// <summary>Called every frame by the toolkit while the screen is open.</summary>
    public void Update()
    {
        if (!IsOpen) return;
        if (!_rt.IsAlive(_canvasObject)) { Close(); return; }

        var width = _kit.ScreenWidth();
        var height = _kit.ScreenHeight();
        if (width != _width || height != _height)
        {
            _width = width;
            _height = height;
            _scale = Math.Min(width / VirtualWidth, height / VirtualHeight);
            _offsetX = (width - VirtualWidth * _scale) / 2f;
            _offsetY = (height - VirtualHeight * _scale) / 2f;
            _dirty = true;
        }
        var now = UiToolkit.Now;
        if (_dirty)
        {
            _dirty = false;
            Entering = _animateNext;
            _animateNext = false;
            // The new page is drawn over the old one and fades in; the old one is removed once it is covered.
            // (Removing first would show the game for a frame.)
            if (_page != IntPtr.Zero) _oldPages.Add(_page);
            _hits.Clear();
            _entrances.Clear();
            _tweens.Clear();
            _drifts.Clear();
            _pulses.Clear();
            _textShadow = 0;
            _hovered = _pressed = null;
            HideTooltip();
            HideFocusRing();
            NewPage(now);
            Render?.Invoke(this);
            Entering = false;
            // The page was rebuilt: put the focus back on whatever is now nearest to where it was.
            _focus = _keyMode ? Nearest(_focusX, _focusY) : null;
        }
        var elapsed = (float)Math.Min(0.1, now - _lastTime);
        _lastTime = now;
        AnimatePage(now);
        AnimateEntrances(now);
        AnimateTweens(now);
        AnimateDrifts(now);
        AnimatePulses(now);
        HandleInput(now);
        if (!IsOpen) return;
        AnimateHover(elapsed);
        UpdateTooltip(now);
        UpdateFocusRing();
    }

    // ---- keyboard and gamepad ----------------------------------------------------------------------------------

    /// <summary>The areas the focus may visit: everything clickable, or only a dialog's buttons while one is open.</summary>
    private List<HitArea> Focusable()
    {
        var start = 0;
        for (var i = 0; i < _hits.Count; i++)
            if (_hits[i].W >= VirtualWidth - 20 && _hits[i].H >= VirtualHeight - 20) start = i + 1; // a dialog's backdrop
        var list = new List<HitArea>();
        for (var i = start; i < _hits.Count; i++)
            if (_hits[i].Click != null) list.Add(_hits[i]);
        return list;
    }

    private HitArea? Nearest(float x, float y)
    {
        HitArea? best = null;
        var bestDistance = float.MaxValue;
        foreach (var h in Focusable())
        {
            var dx = h.X + h.W / 2 - x;
            var dy = h.Y + h.H / 2 - y;
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance) { bestDistance = distance; best = h; }
        }
        return best;
    }

    private void SetFocus(HitArea? area)
    {
        _focus = area;
        if (area == null) return;
        _focusX = area.X + area.W / 2;
        _focusY = area.Y + area.H / 2;
    }

    /// <summary>Moves the focus one step: 0 left, 1 up, 2 right, 3 down.</summary>
    private void MoveFocus(int direction)
    {
        var candidates = Focusable();
        if (candidates.Count == 0) return;
        if (!_keyMode || _focus == null || !candidates.Contains(_focus))
        {
            // The first press only shows where the focus is: on what the pointer is over, else the first control.
            _keyMode = true;
            SetFocus(_hovered != null && candidates.Contains(_hovered) ? _hovered : candidates[0]);
            return;
        }
        HitArea? best = null;
        var bestScore = float.MaxValue;
        foreach (var h in candidates)
        {
            if (h == _focus) continue;
            var dx = h.X + h.W / 2 - _focusX;
            var dy = h.Y + h.H / 2 - _focusY;
            var (along, across) = direction switch { 0 => (-dx, dy), 1 => (-dy, dx), 2 => (dx, dy), _ => (dy, dx) };
            if (along < 8) continue;
            var score = along + 2.4f * Math.Abs(across);
            if (score < bestScore) { bestScore = score; best = h; }
        }
        if (best != null) SetFocus(best);
    }

    /// <summary>The direction being held on the arrow keys, d-pad or left stick, with key repeat; -1 for none.</summary>
    private int NavigationStep(int pad, double now)
    {
        var direction = UiToolkit.KeyDown(0x25) || (pad & 0x4) != 0 ? 0
            : UiToolkit.KeyDown(0x26) || (pad & 0x1) != 0 ? 1
            : UiToolkit.KeyDown(0x27) || (pad & 0x8) != 0 ? 2
            : UiToolkit.KeyDown(0x28) || (pad & 0x2) != 0 ? 3 : -1;
        if (direction != _heldDirection)
        {
            _heldDirection = direction;
            _nextRepeat = now + 0.36;
            return direction;
        }
        if (direction >= 0 && now >= _nextRepeat)
        {
            _nextRepeat = now + 0.13;
            return direction;
        }
        return -1;
    }

    private void UpdateFocusRing()
    {
        var wanted = _keyMode ? _focus : null;
        if (wanted == _ringFor) return;
        HideFocusRing();
        if (wanted == null) return;
        _ringFor = wanted;
        var savedParent = _parent;
        var (savedX, savedY) = (_originX, _originY);
        _focusRing = NewContainer(_canvasTransform, _offsetX, -_offsetY, out var transform);
        _parent = transform;
        _originX = _originY = 0;
        Outline(wanted.X - 5, wanted.Y - 5, wanted.W + 10, wanted.H + 10, 0xFFFFFFF0, 15);
        _parent = savedParent;
        (_originX, _originY) = (savedX, savedY);
    }

    private void HideFocusRing()
    {
        if (_focusRing != IntPtr.Zero && _rt.IsAlive(_focusRing)) _rt.Invoke(_kit.Destroy, IntPtr.Zero, _focusRing);
        _focusRing = IntPtr.Zero;
        _ringFor = null;
    }

    // ---- pages and animation ---------------------------------------------------------------------------------

    private void NewPage(double now)
    {
        _page = NewContainer(_canvasTransform, _offsetX, -_offsetY, out _pageTransform);
        _pageGroup = _rt.Invoke(_kit.AddComponent, _page, _kit.CanvasGroupType);
        _parent = _pageTransform;
        _originX = _originY = 0;
        _fadeStart = now;
        SetAlpha(_pageGroup, 0f);
    }

    /// <summary>An empty, zero-sized object at a pixel position inside a parent; children are placed relative to it.</summary>
    private IntPtr NewContainer(IntPtr parent, float pixelX, float pixelY, out IntPtr transform)
    {
        var container = _rt.NewObject(_kit.GameObjectClass, ".ctor(System.String)", _rt.NewString("Group"));
        transform = _rt.Invoke(_kit.AddComponent, container, _kit.RectTransformType);
        byte worldPositionStays = 0;
        _rt.Invoke(_kit.SetParent, transform, parent, (IntPtr)(&worldPositionStays));
        var layer = 5;
        _rt.Invoke(_kit.SetLayer, container, (IntPtr)(&layer));
        var topLeft = stackalloc float[2] { 0f, 1f };
        var position = stackalloc float[2] { pixelX, pixelY };
        var zero = stackalloc float[2] { 0f, 0f };
        var one = stackalloc float[3] { 1f, 1f, 1f };
        _rt.Invoke(_kit.SetAnchorMin, transform, (IntPtr)topLeft);
        _rt.Invoke(_kit.SetAnchorMax, transform, (IntPtr)topLeft);
        _rt.Invoke(_kit.SetPivot, transform, (IntPtr)topLeft);
        _rt.Invoke(_kit.SetLocalScale, transform, (IntPtr)one);
        _rt.Invoke(_kit.SetAnchoredPosition, transform, (IntPtr)position);
        _rt.Invoke(_kit.SetSizeDelta, transform, (IntPtr)zero);
        return container;
    }

    private void SetAlpha(IntPtr group, float alpha)
    {
        if (group != IntPtr.Zero) _rt.Invoke(_kit.SetAlpha, group, (IntPtr)(&alpha));
    }

    private static float Ease(float t) => 1f - (1f - t) * (1f - t) * (1f - t);

    private void AnimatePage(double now)
    {
        if (_fadeStart < 0) return;
        var t = (float)Math.Clamp((now - _fadeStart) / FadeSeconds, 0, 1);
        SetAlpha(_pageGroup, t * t * (3f - 2f * t));
        if (t < 1f) return;
        _fadeStart = -1;
        foreach (var old in _oldPages) if (_rt.IsAlive(old)) _rt.Invoke(_kit.Destroy, IntPtr.Zero, old);
        _oldPages.Clear();
    }

    private void AnimateEntrances(double now)
    {
        var position = stackalloc float[2];
        for (var i = _entrances.Count - 1; i >= 0; i--)
        {
            var e = _entrances[i];
            var t = (float)Math.Clamp((now - e.Start) / EnterSeconds, 0, 1);
            var eased = Ease(t);
            SetAlpha(e.Group, eased);
            position[0] = 0f;
            position[1] = -(1f - eased) * e.Rise * _scale;
            _rt.Invoke(_kit.SetAnchoredPosition, e.Transform, (IntPtr)position);
            if (t >= 1f) _entrances.RemoveAt(i);
        }
    }

    private void AnimateTweens(double now)
    {
        var size = stackalloc float[2];
        for (var i = _tweens.Count - 1; i >= 0; i--)
        {
            var tween = _tweens[i];
            var t = (float)Math.Clamp((now - tween.Start) / tween.Duration, 0, 1);
            if (tween.Move)
            {
                var e = Ease(t);
                var (vx, vy) = (tween.FromX + (tween.ToX - tween.FromX) * e, tween.FromY + (tween.ToY - tween.FromY) * e);
                var (vw, vh) = (tween.FromW + (tween.ToW - tween.FromW) * e, tween.FromH + (tween.ToH - tween.FromH) * e);
                _shown[tween.Key + "#x"] = vx;
                _shown[tween.Key + "#y"] = vy;
                _shown[tween.Key + "#w"] = vw;
                _shown[tween.Key + "#h"] = vh;
                // The element sits in its page at its target; the offset carries it from where it was.
                size[0] = (vx - tween.ToX) * _scale + tween.Width;
                size[1] = -(vy - tween.ToY) * _scale + tween.Height;
                _rt.Invoke(_kit.SetAnchoredPosition, tween.Target, (IntPtr)size);
                size[0] = vw * _scale;
                size[1] = vh * _scale;
                _rt.Invoke(_kit.SetSizeDelta, tween.Target, (IntPtr)size);
                if (t >= 1f) _tweens.RemoveAt(i);
                continue;
            }
            var value = tween.From + (tween.To - tween.From) * Ease(t);
            _shown[tween.Key] = value;
            if (tween.Format != null) _rt.Invoke(_kit.SetText, tween.Target, _rt.NewString(tween.Format(value)));
            else if (tween.Fill)
            {
                var amount = (float)value;
                _rt.Invoke(_kit.SetFillAmount, tween.Target, (IntPtr)(&amount));
            }
            else
            {
                size[0] = Math.Max(0.01f, tween.Width * (float)value) * _scale;
                size[1] = tween.Height * _scale;
                _rt.Invoke(_kit.SetSizeDelta, tween.Target, (IntPtr)size);
            }
            if (t >= 1f) _tweens.RemoveAt(i);
        }
    }

    private void AnimateDrifts(double now)
    {
        var uv = stackalloc float[4];
        foreach (var d in _drifts)
        {
            // Two slow, unrelated waves for zoom and pan, so the motion never visibly repeats.
            var zoom = 1f - d.Amount * (0.55f + 0.45f * (float)Math.Sin(now * 0.11 + d.Phase));
            var w = d.W * zoom;
            var h = d.H * zoom;
            var px = 0.5f + 0.5f * (float)Math.Sin(now * 0.073 + d.Phase * 1.7);
            var py = 0.5f + 0.5f * (float)Math.Cos(now * 0.057 + d.Phase * 0.9);
            uv[0] = d.U + (d.W - w) * px;
            uv[1] = d.V + (d.H - h) * py;
            uv[2] = w;
            uv[3] = h;
            _rt.Invoke(_kit.SetUvRect, d.Component, (IntPtr)uv);
        }
    }

    private void AnimatePulses(double now)
    {
        if (_pulses.Count == 0) return;
        var wave = 0.5f + 0.5f * (float)Math.Sin(now * Math.PI * 2 / 1.9);
        var strength = 0.35f + 0.65f * wave * wave;
        foreach (var p in _pulses)
        {
            var alpha = (uint)Math.Clamp((int)((p.Color & 0xFF) * strength), 0, 255);
            SetColor(p.Graphic, (p.Color & 0xFFFFFF00u) | alpha);
        }
    }

    /// <summary>Eases each clickable area between its normal and highlighted colour, darker while pressed, and
    /// lets buttons grow a touch under the pointer.</summary>
    private void AnimateHover(float elapsed)
    {
        var scale = stackalloc float[3];
        foreach (var h in _hits)
        {
            if (h.Click == null) continue;
            var target = h == _hovered ? 1f : 0f;
            h.Blend = target > h.Blend ? Math.Min(target, h.Blend + elapsed / (float)HoverSeconds) : Math.Max(target, h.Blend - elapsed / (float)HoverSeconds);
            var pressed = h == _pressed;
            var shown = h.Blend - (pressed ? 2f : 0f);
            if (shown == h.Shown) continue;
            h.Shown = shown;
            if (h.Fill != h.Hover) SetColor(h.Image, Mix(h.Fill, h.Hover, h.Blend, pressed ? 0.82f : 1f));
            if (h.Container != IntPtr.Zero)
            {
                var s = pressed ? 0.98f : 1f + 0.025f * Ease(h.Blend);
                (scale[0], scale[1], scale[2]) = (s, s, 1f);
                _rt.Invoke(_kit.SetLocalScale, h.Container, (IntPtr)scale);
            }
        }
    }

    private static uint Mix(uint a, uint b, float t, float brightness)
    {
        uint result = 0;
        for (var shift = 24; shift >= 0; shift -= 8)
        {
            var x = (a >> shift) & 0xFF;
            var y = (b >> shift) & 0xFF;
            var v = x + (y - (float)x) * t;
            if (shift != 0) v *= brightness;
            result |= (uint)Math.Clamp((int)Math.Round(v), 0, 255) << shift;
        }
        return result;
    }

    // ---- input -----------------------------------------------------------------------------------------------

    private void HandleInput(double now)
    {
        float x, y;
        bool down;
        if (_testHover is { } test)
        {
            (x, y) = test;
            down = false;
        }
        else
        {
            if (!_kit.WindowHasFocus()) { _mouseWasDown = _escapeWasDown = _acceptWasDown = true; return; }
            var pad = TestPad ?? _kit.Gamepad();
            var escape = UiToolkit.KeyDown(0x1B) || (pad & 0x2000) != 0; // Escape, or B on a gamepad
            if (escape && !_escapeWasDown) { _escapeWasDown = true; OnBack?.Invoke(); return; }
            _escapeWasDown = escape;
            if (!_kit.MousePosition(out var px, out var py)) return;
            x = (px - _offsetX) / _scale;
            y = (py - _offsetY) / _scale;
            down = UiToolkit.KeyDown(0x01);

            // Moving or pressing the mouse hands control back to it.
            if (_lastMouseX >= 0 && (Math.Abs(px - _lastMouseX) + Math.Abs(py - _lastMouseY) > 3 || down)) _keyMode = false;
            (_lastMouseX, _lastMouseY) = (px, py);

            var step = NavigationStep(pad, now);
            if (step >= 0) MoveFocus(step);
            var accept = UiToolkit.KeyDown(0x0D) || UiToolkit.KeyDown(0x20) || (pad & 0x1000) != 0; // Enter, Space, or A
            if (accept && !_acceptWasDown && _keyMode && _focus?.Click != null)
            {
                _acceptWasDown = true;
                var click = _focus.Click;
                click();
                return;
            }
            _acceptWasDown = accept;
            if (_keyMode && _focus != null)
            {
                // While the keys or the gamepad are in charge, the focus plays the pointer's part.
                x = _focus.X + _focus.W / 2;
                y = _focus.Y + _focus.H / 2;
                down = false;
            }
        }

        HitArea? over = null;
        for (var i = _hits.Count - 1; i >= 0 && over == null; i--) // last drawn is on top
        {
            var h = _hits[i];
            if (x >= h.X && x < h.X + h.W && y >= h.Y && y < h.Y + h.H) over = h;
        }
        if (over != _hovered)
        {
            _hovered = over;
            _hoverSince = now;
            // Tell the screen once per area: a page rebuilt with the same area under the pointer stays quiet.
            if (over?.OnHover != null && (over.X, over.Y, over.W, over.H) != _hoverNotified)
            {
                _hoverNotified = (over.X, over.Y, over.W, over.H);
                over.OnHover();
            }
        }

        if (down && !_mouseWasDown) _pressed = over;
        if (!down && _mouseWasDown && _pressed != null && _pressed == over && _pressed.Click != null)
        {
            var click = _pressed.Click;
            _pressed = null;
            _mouseWasDown = down;
            click();
            return;
        }
        if (!down) _pressed = null;
        _mouseWasDown = down;
    }

    // ---- tooltips --------------------------------------------------------------------------------------------

    private void UpdateTooltip(double now)
    {
        var wanted = _hovered is { Tooltip: not null } && now - _hoverSince >= TooltipDelay && _pressed == null ? _hovered : null;
        if (wanted == _tooltipFor) return;
        HideTooltip();
        if (wanted == null) return;
        _tooltipFor = wanted;

        var text = wanted.Tooltip!;
        const int size = 19;
        const float maxWidth = 460f, pad = 18f;
        var perLine = (int)(maxWidth / (size * 0.52f));
        var lines = 0;
        var longest = 0;
        foreach (var line in text.Split('\n'))
        {
            lines += Math.Max(1, (int)Math.Ceiling(line.Length / (double)perLine));
            longest = Math.Max(longest, Math.Min(line.Length, perLine));
        }
        var w = Math.Min(maxWidth, longest * size * 0.52f) + pad * 2;
        var h = lines * size * 1.32f + pad * 2;
        var x = Math.Clamp(wanted.X + wanted.W / 2 - w / 2, 16, VirtualWidth - w - 16);
        var y = wanted.Y - h - 14;
        if (y < 16) y = wanted.Y + wanted.H + 14;

        var savedParent = _parent;
        var (savedX, savedY) = (_originX, _originY);
        _tooltip = NewContainer(_canvasTransform, _offsetX, -_offsetY, out var transform);
        _parent = transform;
        _originX = _originY = 0;
        Shadow(x, y, w, h, 0.7f);
        Panel(x, y, w, h, 0x0B1017F5, 10);
        Outline(x, y, w, h, 0xFFFFFF26, 10);
        Label(x + pad, y + pad - 2, w - pad * 2, h - pad * 2 + 4, text, size, 0xDDE3EAFF, UiAlign.TopLeft);
        _parent = savedParent;
        (_originX, _originY) = (savedX, savedY);
    }

    private void HideTooltip()
    {
        if (_tooltip != IntPtr.Zero && _rt.IsAlive(_tooltip)) _rt.Invoke(_kit.Destroy, IntPtr.Zero, _tooltip);
        _tooltip = IntPtr.Zero;
        _tooltipFor = null;
    }

    // ---- IUiCanvas -------------------------------------------------------------------------------------------

    public void Rect(float x, float y, float w, float h, uint rgba) => NewImage(x, y, w, h, rgba, 0, null);

    public void Panel(float x, float y, float w, float h, uint rgba, float radius = 14) => NewImage(x, y, w, h, rgba, radius, UiToolkit.RoundedSprite);

    public void Outline(float x, float y, float w, float h, uint rgba, float radius = 14) => NewImage(x, y, w, h, rgba, radius, UiToolkit.StrokeSprite);

    public void Shadow(float x, float y, float w, float h, float strength = 0.5f)
    {
        const float spread = 34f;
        var alpha = (uint)Math.Clamp((int)(strength * 255), 0, 255);
        NewImage(x - spread, y - spread * 0.55f, w + spread * 2, h + spread * 2, alpha, spread * 1.5f, UiToolkit.ShadowSprite);
    }

    public void Gradient(float x, float y, float w, float h, uint rgba, UiGradient direction)
    {
        var vertical = direction is UiGradient.Down or UiGradient.Up;
        var texture = _kit.GradientTexture(vertical);
        if (texture == IntPtr.Zero) return;
        var element = NewElement("Gradient", x, y, w, h);
        var component = _rt.Invoke(_kit.AddComponent, element, _kit.RawImageType);
        if (component == IntPtr.Zero) return;
        _rt.Invoke(_kit.SetTexture, component, texture);
        // The textures fade left to right and top to bottom; the other two directions flip them.
        var uv = stackalloc float[4] { 0f, 0f, 1f, 1f };
        if (direction == UiGradient.Left) { uv[0] = 1f; uv[2] = -1f; }
        if (direction == UiGradient.Up) { uv[1] = 1f; uv[3] = -1f; }
        _rt.Invoke(_kit.SetUvRect, component, (IntPtr)uv);
        NoRaycast(component);
        SetColor(component, rgba);
    }

    public void Icon(float x, float y, float size, string name, uint rgba) => Sprite(x, y, size, size, name, rgba);

    public void Sprite(float x, float y, float w, float h, string name, uint rgba)
    {
        var sprite = _kit.Sprite(name);
        if (sprite == IntPtr.Zero) return;
        var element = NewElement("Sprite", x, y, w, h);
        var component = _rt.Invoke(_kit.AddComponent, element, _kit.ImageType);
        if (component == IntPtr.Zero) return;
        _rt.Invoke(_kit.SetSprite, component, sprite);
        byte yes = 1;
        _rt.Invoke(_kit.SetPreserveAspect, component, (IntPtr)(&yes));
        NoRaycast(component);
        SetColor(component, rgba);
    }

    public void Label(float x, float y, float w, float h, string text, int size, uint rgba, UiAlign align = UiAlign.Left, bool heading = false) =>
        NewLabel(x, y, w, h, text, size, rgba, align, heading);

    public void Number(float x, float y, float w, float h, string key, double value, Func<double, string> format, int size, uint rgba,
        UiAlign align = UiAlign.Left, bool heading = true)
    {
        var from = _shown.TryGetValue(key, out var shown) ? shown : value;
        var component = NewLabel(x, y, w, h, format(from), size, rgba, align, heading);
        _shown[key] = from;
        if (component != IntPtr.Zero && Math.Abs(from - value) > 1e-9)
            _tweens.Add(new Tween { Target = component, Key = key, From = from, To = value, Start = UiToolkit.Now + 0.12, Format = format });
    }

    public void Meter(float x, float y, float w, float h, string key, float fraction, uint fill, uint back)
    {
        fraction = Math.Clamp(fraction, 0f, 1f);
        var radius = h / 2f;
        NewImage(x, y, w, h, back, radius, UiToolkit.RoundedSprite);
        var from = _shown.TryGetValue(key, out var shown) ? (float)shown : (Entering ? 0f : fraction);
        _shown[key] = from;
        var bar = NewImage(x, y, Math.Max(0.01f, w * from), h, fill, radius, UiToolkit.RoundedSprite, out var transform);
        if (bar != IntPtr.Zero && Math.Abs(from - fraction) > 1e-6)
            _tweens.Add(new Tween { Target = transform, Key = key, From = from, To = fraction, Start = UiToolkit.Now + 0.15, Width = w, Height = h });
    }

    public void Picture(float x, float y, float w, float h, string path, uint tint = 0xFFFFFFFF, float radius = 0)
    {
        var texture = _kit.Texture(path, out var tw, out var th);
        if (texture != IntPtr.Zero) DrawTexture(x, y, w, h, texture, tw, th, tint, radius);
    }

    public void Image(float x, float y, float w, float h, UiImage image, uint tint = 0xFFFFFFFF, float radius = 0)
    {
        if (image.Handle != IntPtr.Zero && _rt.IsAlive(image.Handle)) DrawTexture(x, y, w, h, image.Handle, image.Width, image.Height, tint, radius);
    }

    /// <summary>Draws a texture so that it fills the box, cropping what does not fit, optionally with rounded corners.</summary>
    private void DrawTexture(float x, float y, float w, float h, IntPtr texture, int textureWidth, int textureHeight, uint tint, float radius, float drift = 0f)
    {
        var savedParent = _parent;
        var (savedX, savedY) = (_originX, _originY);
        if (radius > 0 && _kit.MaskType != IntPtr.Zero && _kit.Sprite(UiToolkit.RoundedSprite) != IntPtr.Zero)
        {
            // A rounded shape that clips its child: the engine's own mask component.
            var mask = NewImage(x, y, w, h, 0xFFFFFFFF, radius, UiToolkit.RoundedSprite, out var maskTransform);
            var maskObject = _rt.Invoke(_kit.ComponentGetGameObject, mask);
            var maskComponent = _rt.Invoke(_kit.AddComponent, maskObject, _kit.MaskType);
            byte no = 0;
            if (maskComponent != IntPtr.Zero) _rt.Invoke(_kit.SetShowMaskGraphic, maskComponent, (IntPtr)(&no));
            _parent = maskTransform;
            (_originX, _originY) = (x, y);
        }
        var element = NewElement("Picture", x, y, w, h);
        var component = _rt.Invoke(_kit.AddComponent, element, _kit.RawImageType);
        if (component != IntPtr.Zero)
        {
            _rt.Invoke(_kit.SetTexture, component, texture);
            var uv = stackalloc float[4] { 0f, 0f, 1f, 1f };
            if (textureWidth > 0 && textureHeight > 0 && w > 0 && h > 0)
            {
                var boxAspect = w / h;
                var imageAspect = textureWidth / (float)textureHeight;
                if (imageAspect > boxAspect) { uv[2] = boxAspect / imageAspect; uv[0] = (1f - uv[2]) / 2f; }
                else { uv[3] = imageAspect / boxAspect; uv[1] = (1f - uv[3]) / 2f; }
            }
            _rt.Invoke(_kit.SetUvRect, component, (IntPtr)uv);
            NoRaycast(component);
            SetColor(component, tint);
            if (drift > 0)
                _drifts.Add(new Drift { Component = component, U = uv[0], V = uv[1], W = uv[2], H = uv[3], Amount = Math.Clamp(drift, 0f, 0.4f), Phase = (x * 0.37f + y * 0.11f) % 6.28f });
        }
        _parent = savedParent;
        (_originX, _originY) = (savedX, savedY);
    }

    public void Button(float x, float y, float w, float h, string label, Action onClick, uint fill, uint hoverFill, uint textColor,
        int size = 26, bool heading = true, float radius = 10, string? tooltip = null)
    {
        // The button's parts sit in a container at its centre so it can grow slightly under the pointer.
        var savedParent = _parent;
        var (savedX, savedY) = (_originX, _originY);
        var (cx, cy) = (x + w / 2, y + h / 2);
        NewContainer(_parent, (cx - _originX) * _scale, -(cy - _originY) * _scale, out var transform);
        _parent = transform;
        (_originX, _originY) = (cx, cy);
        var image = NewImage(x, y, w, h, fill, radius, UiToolkit.RoundedSprite);
        NewLabel(x + 12, y, w - 24, h, label, size, textColor, UiAlign.Center, heading);
        _parent = savedParent;
        (_originX, _originY) = (savedX, savedY);
        _hits.Add(new HitArea { X = x, Y = y, W = w, H = h, Image = image, Container = transform, Fill = fill, Hover = hoverFill, Click = onClick, Tooltip = tooltip });
    }

    public void Clickable(float x, float y, float w, float h, Action onClick, uint fill, uint hoverFill, float radius = 0, string? tooltip = null,
        Action? onHover = null)
    {
        var image = NewImage(x, y, w, h, fill, radius, radius > 0 ? UiToolkit.RoundedSprite : null);
        _hits.Add(new HitArea { X = x, Y = y, W = w, H = h, Image = image, Fill = fill, Hover = hoverFill, Click = onClick, Tooltip = tooltip, OnHover = onHover });
    }

    public void Slide(string key, float x, float y, float w, float h, uint rgba, float radius = 4)
    {
        var known = _shown.TryGetValue(key + "#x", out var fx);
        _shown.TryGetValue(key + "#y", out var fy);
        _shown.TryGetValue(key + "#w", out var fw);
        _shown.TryGetValue(key + "#h", out var fh);
        // Positions are kept in screen space (without the group origin) so a slide survives a page rebuild.
        var (ax, ay) = (x - _originX, y - _originY);
        var image = NewImage(x, y, w, h, rgba, radius, radius > 0 ? UiToolkit.RoundedSprite : null, out var transform);
        _shown[key + "#x"] = ax;
        _shown[key + "#y"] = ay;
        _shown[key + "#w"] = w;
        _shown[key + "#h"] = h;
        if (image == IntPtr.Zero || !known || Entering) return;
        if (Math.Abs(fx - ax) + Math.Abs(fy - ay) + Math.Abs(fw - w) + Math.Abs(fh - h) < 0.5) return;
        _tweens.Add(new Tween
        {
            Target = transform, Key = key, Move = true, Start = UiToolkit.Now, Duration = 0.24,
            FromX = (float)fx, FromY = (float)fy, FromW = (float)fw, FromH = (float)fh, ToX = ax, ToY = ay, ToW = w, ToH = h,
            // The element's own anchored position, which the offset is added to.
            Width = ax * _scale, Height = -ay * _scale,
        });
        // Start where it was, not at the target, so the first frame does not flash at the new place.
        var start = stackalloc float[2] { (float)fx * _scale, -(float)fy * _scale };
        _rt.Invoke(_kit.SetAnchoredPosition, transform, (IntPtr)start);
        var size = stackalloc float[2] { (float)fw * _scale, (float)fh * _scale };
        _rt.Invoke(_kit.SetSizeDelta, transform, (IntPtr)size);
    }

    public void Hint(float x, float y, float w, float h, string tooltip) =>
        _hits.Add(new HitArea { X = x, Y = y, W = w, H = h, Tooltip = tooltip });

    public IDisposable Group(float delay = 0f, float rise = 22f)
    {
        var savedParent = _parent;
        var (savedX, savedY) = (_originX, _originY);
        var container = NewContainer(_parent, -_originX * _scale, _originY * _scale, out var transform);
        _parent = transform;
        (_originX, _originY) = (0f, 0f);
        if (Entering)
        {
            var group = _rt.Invoke(_kit.AddComponent, container, _kit.CanvasGroupType);
            SetAlpha(group, 0f);
            _entrances.Add(new Entrance { Transform = transform, Group = group, Start = UiToolkit.Now + delay, Rise = rise });
        }
        return new Scope(() =>
        {
            _parent = savedParent;
            (_originX, _originY) = (savedX, savedY);
        });
    }

    public double Time => UiToolkit.Now;

    public void Glow(float x, float y, float w, float h, uint rgba)
    {
        var texture = _kit.GlowTexture();
        if (texture == IntPtr.Zero) return;
        var element = NewElement("Glow", x, y, w, h);
        var component = _rt.Invoke(_kit.AddComponent, element, _kit.RawImageType);
        if (component == IntPtr.Zero) return;
        _rt.Invoke(_kit.SetTexture, component, texture);
        NoRaycast(component);
        SetColor(component, rgba);
    }

    public void Ring(float x, float y, float size, float thickness, string key, float fraction, uint fill, uint back)
    {
        fraction = Math.Clamp(fraction, 0f, 1f);
        var sprite = _kit.RingSprite(thickness / Math.Max(1f, size));
        if (sprite == IntPtr.Zero) return;
        RingImage(x, y, size, sprite, back, 1f);
        var from = _shown.TryGetValue(key, out var shown) ? (float)shown : (Entering ? 0f : fraction);
        _shown[key] = from;
        var image = RingImage(x, y, size, sprite, fill, from);
        if (image != IntPtr.Zero && Math.Abs(from - fraction) > 1e-6)
            _tweens.Add(new Tween { Target = image, Key = key, From = from, To = fraction, Start = UiToolkit.Now + 0.2, Fill = true });
    }

    private IntPtr RingImage(float x, float y, float size, IntPtr sprite, uint rgba, float amount)
    {
        var element = NewElement("Ring", x, y, size, size);
        var component = _rt.Invoke(_kit.AddComponent, element, _kit.ImageType);
        if (component == IntPtr.Zero) return IntPtr.Zero;
        _rt.Invoke(_kit.SetSprite, component, sprite);
        int filled = 3, radial360 = 4, top = 2;
        byte clockwise = 1;
        _rt.Invoke(_kit.SetImageType, component, (IntPtr)(&filled));
        _rt.Invoke(_kit.SetFillMethod, component, (IntPtr)(&radial360));
        _rt.Invoke(_kit.SetFillOrigin, component, (IntPtr)(&top));
        _rt.Invoke(_kit.SetFillClockwise, component, (IntPtr)(&clockwise));
        _rt.Invoke(_kit.SetFillAmount, component, (IntPtr)(&amount));
        NoRaycast(component);
        SetColor(component, rgba);
        return component;
    }

    public void Backdrop(float x, float y, float w, float h, UiImage? image, string? path, uint tint = 0xFFFFFFFF, float drift = 0.08f)
    {
        if (image != null && image.Handle != IntPtr.Zero && _rt.IsAlive(image.Handle))
        {
            DrawTexture(x, y, w, h, image.Handle, image.Width, image.Height, tint, 0, drift);
            return;
        }
        if (path == null) return;
        var texture = _kit.Texture(path, out var tw, out var th);
        if (texture != IntPtr.Zero) DrawTexture(x, y, w, h, texture, tw, th, tint, 0, drift);
    }

    public void Pulse(float x, float y, float w, float h, uint rgba, float radius = 12)
    {
        const float spread = 26f;
        var halo = NewImage(x - spread, y - spread, w + spread * 2, h + spread * 2, rgba, radius + spread, UiToolkit.ShadowSprite);
        if (halo != IntPtr.Zero) _pulses.Add(new PulseHalo { Graphic = halo, Color = rgba });
    }

    public IDisposable TextShadow(uint rgba = 0x000000B4, float distance = 2f)
    {
        var (savedColor, savedDistance) = (_textShadow, _textShadowDistance);
        (_textShadow, _textShadowDistance) = (rgba, distance);
        return new Scope(() => (_textShadow, _textShadowDistance) = (savedColor, savedDistance));
    }

    private sealed class Scope(Action end) : IDisposable
    {
        public void Dispose() => end();
    }

    // ---- element plumbing ----------------------------------------------------------------------------------

    private IntPtr NewLabel(float x, float y, float w, float h, string text, int size, uint rgba, UiAlign align, bool heading)
    {
        var element = NewElement("Label", x, y, w, h);
        var component = _rt.Invoke(_kit.AddComponent, element, _kit.TextType);
        if (component == IntPtr.Zero) return IntPtr.Zero;
        _rt.Invoke(_kit.SetFont, component, heading ? _kit.HeadingFont : _kit.BodyFont);
        var fontSize = Math.Max(8, (int)Math.Round(size * _scale));
        var anchor = (int)align;       // UnityEngine.TextAnchor: 0-2 upper, 3-5 middle
        int wrap = 0, overflow = 1;
        _rt.Invoke(_kit.SetFontSize, component, (IntPtr)(&fontSize));
        _rt.Invoke(_kit.SetAlignment, component, (IntPtr)(&anchor));
        _rt.Invoke(_kit.SetHorizontalOverflow, component, (IntPtr)(&wrap));
        _rt.Invoke(_kit.SetVerticalOverflow, component, (IntPtr)(&overflow));
        NoRaycast(component);
        SetColor(component, rgba);
        _rt.Invoke(_kit.SetText, component, _rt.NewString(text));
        if (_textShadow != 0 && _kit.ShadowEffectType != IntPtr.Zero)
        {
            var effect = _rt.Invoke(_kit.AddComponent, element, _kit.ShadowEffectType);
            if (effect != IntPtr.Zero)
            {
                var color = stackalloc float[4]
                {
                    ((_textShadow >> 24) & 0xFF) / 255f, ((_textShadow >> 16) & 0xFF) / 255f, ((_textShadow >> 8) & 0xFF) / 255f, (_textShadow & 0xFF) / 255f,
                };
                var distance = stackalloc float[2] { _textShadowDistance * _scale, -_textShadowDistance * _scale };
                _rt.Invoke(_kit.SetEffectColor, effect, (IntPtr)color);
                _rt.Invoke(_kit.SetEffectDistance, effect, (IntPtr)distance);
            }
        }
        return component;
    }

    private void NoRaycast(IntPtr graphic)
    {
        byte no = 0;
        _rt.Invoke(_kit.SetRaycastTarget, graphic, (IntPtr)(&no));
    }

    private IntPtr NewImage(float x, float y, float w, float h, uint rgba, float radius, string? spriteName) =>
        NewImage(x, y, w, h, rgba, radius, spriteName, out _);

    private IntPtr NewImage(float x, float y, float w, float h, uint rgba, float radius, string? spriteName, out IntPtr transform)
    {
        var element = NewElement("Rect", x, y, w, h, out transform);
        var component = _rt.Invoke(_kit.AddComponent, element, _kit.ImageType);
        if (component == IntPtr.Zero) return IntPtr.Zero;
        var sprite = spriteName != null && radius > 0 ? _kit.Sprite(spriteName) : IntPtr.Zero;
        if (sprite != IntPtr.Zero)
        {
            // A nine-sliced sprite from the game's interface: its corners keep their shape at any size. The
            // multiplier scales the sprite's corner to the radius asked for.
            var sliced = 1;
            var multiplier = Math.Max(0.05f, _kit.SpriteCorner(spriteName!) / Math.Max(1f, radius * _scale));
            _rt.Invoke(_kit.SetSprite, component, sprite);
            _rt.Invoke(_kit.SetImageType, component, (IntPtr)(&sliced));
            _rt.Invoke(_kit.SetPixelsPerUnitMultiplier, component, (IntPtr)(&multiplier));
        }
        NoRaycast(component);
        SetColor(component, rgba);
        return component;
    }

    private IntPtr NewElement(string name, float x, float y, float w, float h) => NewElement(name, x, y, w, h, out _);

    private IntPtr NewElement(string name, float x, float y, float w, float h, out IntPtr rect)
    {
        var element = _rt.NewObject(_kit.GameObjectClass, ".ctor(System.String)", _rt.NewString(name));
        // A RectTransform (rather than a plain Transform) so the element can be laid out.
        rect = _rt.Invoke(_kit.AddComponent, element, _kit.RectTransformType);
        byte worldPositionStays = 0;
        _rt.Invoke(_kit.SetParent, rect, _parent, (IntPtr)(&worldPositionStays));
        var layer = 5; // the engine's UI layer
        _rt.Invoke(_kit.SetLayer, element, (IntPtr)(&layer));
        var topLeft = stackalloc float[2] { 0f, 1f };
        var position = stackalloc float[2] { (x - _originX) * _scale, -(y - _originY) * _scale };
        var size = stackalloc float[2] { w * _scale, h * _scale };
        var one = stackalloc float[3] { 1f, 1f, 1f };
        _rt.Invoke(_kit.SetAnchorMin, rect, (IntPtr)topLeft);
        _rt.Invoke(_kit.SetAnchorMax, rect, (IntPtr)topLeft);
        _rt.Invoke(_kit.SetPivot, rect, (IntPtr)topLeft);
        _rt.Invoke(_kit.SetLocalScale, rect, (IntPtr)one);
        _rt.Invoke(_kit.SetAnchoredPosition, rect, (IntPtr)position);
        _rt.Invoke(_kit.SetSizeDelta, rect, (IntPtr)size);
        return element;
    }

    private void SetColor(IntPtr graphic, uint rgba)
    {
        if (graphic == IntPtr.Zero) return;
        var color = stackalloc float[4]
        {
            ((rgba >> 24) & 0xFF) / 255f, ((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f,
        };
        _rt.Invoke(_kit.SetGraphicColor, graphic, (IntPtr)color);
    }
}

/// <summary>Engine functions, fonts, sprites and window input shared by all custom screens.</summary>
internal sealed unsafe class UiToolkit
{
    public const string RoundedSprite = "WhiteRoundedCorner20px", StrokeSprite = "WhiteRoundedCorner80pxStroke", ShadowSprite = "textureBlurShadow";

    private readonly GameRuntime _rt;
    private readonly List<UiScreen> _open = new();
    private readonly Dictionary<string, (IntPtr Texture, int Width, int Height)> _textures = new();
    private readonly Dictionary<string, IntPtr> _sprites = new();
    private readonly Dictionary<string, float> _corners = new();
    private readonly List<IntPtr> _hiddenCanvases = new();
    private readonly HashSet<IntPtr> _pinned = new();
    private bool _resolved, _failed, _spritesLoaded;
    private IntPtr _window, _gradientHorizontal, _gradientVertical;

    public IntPtr GameObjectClass, GoGetTransform, AddComponent, SetParent, SetLayer, Destroy, ComponentGetGameObject;
    public IntPtr SetAnchorMin, SetAnchorMax, SetPivot, SetLocalScale, SetAnchoredPosition, SetSizeDelta;
    public IntPtr RectTransformType, ImageType, TextType, RawImageType, CanvasType, CanvasGroupType, MaskType, SetAlpha;
    public IntPtr SetGraphicColor, SetRaycastTarget, SetFont, SetFontSize, SetAlignment, SetHorizontalOverflow, SetVerticalOverflow, SetText, SetTexture;
    public IntPtr SetSprite, SetImageType, SetPixelsPerUnitMultiplier, SetPreserveAspect, SetUvRect, SetShowMaskGraphic;
    public IntPtr HeadingFont, BodyFont;
    public IntPtr SetFillMethod, SetFillOrigin, SetFillClockwise, SetFillAmount, ShadowEffectType, SetEffectColor, SetEffectDistance;
    private IntPtr _spriteCreate, _glowTexture;
    private readonly Dictionary<int, IntPtr> _rings = new();
    private IntPtr _setRenderMode, _setSortingOrder, _dontDestroyOnLoad, _screenWidth, _screenHeight, _setBlockAllInput;
    private IntPtr _texture2DClass, _loadImage, _getFont, _mvcTextClass, _objectGetName, _spriteClass, _findAll, _spriteGetBorder;
    private IntPtr _canvasClass, _isRootCanvas, _behaviourGetEnabled, _behaviourSetEnabled, _textureGetWidth, _textureGetHeight, _setWrapMode;

    public static double Now => AnimationClock.Now;

    public UiToolkit(GameRuntime runtime, MainThread mainThread)
    {
        Instance = this;
        _rt = runtime;
        mainThread.Update += () =>
        {
            for (var i = _open.Count - 1; i >= 0; i--)
            {
                if (i >= _open.Count) continue;
                var screen = _open[i];
                try { screen.Update(); }
                catch (Exception e) { Log.Error("Custom screen failed and was closed: " + e); try { screen.Close(); } catch (Exception) { } }
            }
        };
        mainThread.Quitting += () => { _open.Clear(); BlockGameInput(false); };
    }

    /// <summary>Test hook: closes every open custom screen.</summary>
    internal static int CloseAll()
    {
        var screens = Instance?._open.ToArray() ?? Array.Empty<UiScreen>();
        foreach (var screen in screens) screen.Close();
        return screens.Length;
    }

    internal static UiToolkit? Instance;

    public void Register(UiScreen screen) => _open.Add(screen);
    public void Unregister(UiScreen screen) => _open.Remove(screen);

    public bool Resolve()
    {
        if (_failed) return false;
        if (_resolved) return ResolveFonts();
        const string core = "UnityEngine.CoreModule", unity = "UnityEngine", ui = "UnityEngine.UI";
        var gameObject = _rt.FindClass(core, unity, "GameObject");
        var transform = _rt.FindClass(core, unity, "Transform");
        var rectTransform = _rt.FindClass(core, unity, "RectTransform");
        var unityObject = _rt.FindClass(core, unity, "Object");
        var component = _rt.FindClass(core, unity, "Component");
        var behaviour = _rt.FindClass(core, unity, "Behaviour");
        var texture = _rt.FindClass(core, unity, "Texture");
        var resources = _rt.FindClass(core, unity, "Resources");
        var canvas = _rt.FindClass("UnityEngine.UIModule", unity, "Canvas");
        var canvasGroup = _rt.FindClass("UnityEngine.UIModule", unity, "CanvasGroup");
        var graphic = _rt.FindClass(ui, ui, "Graphic");
        var image = _rt.FindClass(ui, ui, "Image");
        var text = _rt.FindClass(ui, ui, "Text");
        var rawImage = _rt.FindClass(ui, ui, "RawImage");
        var mask = _rt.FindClass(ui, ui, "Mask");
        var screen = _rt.FindClass(core, unity, "Screen");
        var input = _rt.FindClass("HB.Golf", "InputLib", "GameInput");
        _spriteClass = _rt.FindClass(core, unity, "Sprite");
        _texture2DClass = _rt.FindClass(core, unity, "Texture2D");
        _mvcTextClass = _rt.FindClass("HB.Golf", "MVC", "MVCText");
        _canvasClass = canvas;
        if (new[] { gameObject, transform, rectTransform, unityObject, component, behaviour, texture, resources, canvas, canvasGroup, graphic, image, text, rawImage,
                    screen, _spriteClass, _texture2DClass, _mvcTextClass }.AsSpan().Contains(IntPtr.Zero))
            return Fail("engine UI types");

        GameObjectClass = gameObject;
        GoGetTransform = _rt.FindMethod(gameObject, "get_transform", 0);
        AddComponent = _rt.FindMethod(gameObject, "AddComponent(System.Type)");
        SetLayer = _rt.FindMethod(gameObject, "set_layer", 1);
        ComponentGetGameObject = _rt.FindMethod(component, "get_gameObject", 0);
        SetParent = _rt.FindMethod(transform, "SetParent(UnityEngine.Transform,System.Boolean)");
        SetLocalScale = _rt.FindMethod(transform, "set_localScale", 1);
        Destroy = _rt.FindMethod(unityObject, "Destroy(UnityEngine.Object)");
        _dontDestroyOnLoad = _rt.FindMethod(unityObject, "DontDestroyOnLoad", 1);
        _objectGetName = _rt.FindMethod(unityObject, "get_name", 0);
        _findAll = _rt.FindMethod(resources, "FindObjectsOfTypeAll(System.Type)");
        SetAnchorMin = _rt.FindMethod(rectTransform, "set_anchorMin", 1);
        SetAnchorMax = _rt.FindMethod(rectTransform, "set_anchorMax", 1);
        SetPivot = _rt.FindMethod(rectTransform, "set_pivot", 1);
        SetAnchoredPosition = _rt.FindMethod(rectTransform, "set_anchoredPosition", 1);
        SetSizeDelta = _rt.FindMethod(rectTransform, "set_sizeDelta", 1);
        SetGraphicColor = _rt.FindMethod(graphic, "set_color", 1);
        SetRaycastTarget = _rt.FindMethod(graphic, "set_raycastTarget", 1);
        SetFont = _rt.FindMethod(text, "set_font", 1);
        _getFont = _rt.FindMethod(text, "get_font", 0);
        SetFontSize = _rt.FindMethod(text, "set_fontSize", 1);
        SetAlignment = _rt.FindMethod(text, "set_alignment", 1);
        SetHorizontalOverflow = _rt.FindMethod(text, "set_horizontalOverflow", 1);
        SetVerticalOverflow = _rt.FindMethod(text, "set_verticalOverflow", 1);
        SetText = _rt.FindMethod(text, "set_text", 1);
        SetTexture = _rt.FindMethod(rawImage, "set_texture", 1);
        SetUvRect = _rt.FindMethod(rawImage, "set_uvRect", 1);
        SetSprite = _rt.FindMethod(image, "set_sprite", 1);
        SetImageType = _rt.FindMethod(image, "set_type", 1);
        SetPixelsPerUnitMultiplier = _rt.FindMethod(image, "set_pixelsPerUnitMultiplier", 1);
        SetPreserveAspect = _rt.FindMethod(image, "set_preserveAspect", 1);
        SetAlpha = _rt.FindMethod(canvasGroup, "set_alpha", 1);
        _spriteGetBorder = _rt.FindMethod(_spriteClass, "get_border", 0);
        _setRenderMode = _rt.FindMethod(canvas, "set_renderMode", 1);
        _setSortingOrder = _rt.FindMethod(canvas, "set_sortingOrder", 1);
        _isRootCanvas = _rt.FindMethod(canvas, "get_isRootCanvas", 0);
        _behaviourGetEnabled = _rt.FindMethod(behaviour, "get_enabled", 0);
        _behaviourSetEnabled = _rt.FindMethod(behaviour, "set_enabled", 1);
        _textureGetWidth = _rt.FindMethod(texture, "get_width", 0);
        _textureGetHeight = _rt.FindMethod(texture, "get_height", 0);
        _setWrapMode = _rt.FindMethod(texture, "set_wrapMode", 1);
        _screenWidth = _rt.FindMethod(screen, "get_width", 0);
        _screenHeight = _rt.FindMethod(screen, "get_height", 0);
        _setBlockAllInput = input == IntPtr.Zero ? IntPtr.Zero : _rt.FindMethod(input, "set_blockAllInput", 1);
        _loadImage = _rt.FindMethod(_rt.FindClass("UnityEngine.ImageConversionModule", unity, "ImageConversion"), "LoadImage(UnityEngine.Texture2D,System.Byte[])");
        if (new[] { GoGetTransform, AddComponent, SetLayer, ComponentGetGameObject, SetParent, SetLocalScale, Destroy, _findAll, SetAnchorMin, SetAnchorMax, SetPivot,
                    SetAnchoredPosition, SetSizeDelta, SetGraphicColor, SetRaycastTarget, SetFont, _getFont, SetFontSize, SetAlignment,
                    SetHorizontalOverflow, SetVerticalOverflow, SetText, SetTexture, SetUvRect, SetSprite, SetImageType, SetPixelsPerUnitMultiplier,
                    SetPreserveAspect, SetAlpha, _spriteGetBorder, _setRenderMode, _setSortingOrder, _isRootCanvas, _behaviourGetEnabled, _behaviourSetEnabled,
                    _textureGetWidth, _textureGetHeight, _screenWidth, _screenHeight, _loadImage, _objectGetName }.AsSpan().Contains(IntPtr.Zero))
            return Fail("engine UI functions");

        RectTransformType = _rt.GetTypeObject(rectTransform);
        ImageType = _rt.GetTypeObject(image);
        TextType = _rt.GetTypeObject(text);
        RawImageType = _rt.GetTypeObject(rawImage);
        CanvasType = _rt.GetTypeObject(canvas);
        CanvasGroupType = _rt.GetTypeObject(canvasGroup);
        // Optional extras (rings, text shadows): screens still work without them.
        SetFillMethod = _rt.FindMethod(image, "set_fillMethod", 1);
        SetFillOrigin = _rt.FindMethod(image, "set_fillOrigin", 1);
        SetFillClockwise = _rt.FindMethod(image, "set_fillClockwise", 1);
        SetFillAmount = _rt.FindMethod(image, "set_fillAmount", 1);
        _spriteCreate = _rt.FindMethod(_spriteClass, "Create(UnityEngine.Texture2D,UnityEngine.Rect,UnityEngine.Vector2)");
        var shadowEffect = _rt.FindClass(ui, ui, "Shadow");
        if (shadowEffect != IntPtr.Zero)
        {
            SetEffectColor = _rt.FindMethod(shadowEffect, "set_effectColor", 1);
            SetEffectDistance = _rt.FindMethod(shadowEffect, "set_effectDistance", 1);
            if (SetEffectColor != IntPtr.Zero && SetEffectDistance != IntPtr.Zero) ShadowEffectType = _rt.GetTypeObject(shadowEffect);
        }
        if (mask != IntPtr.Zero)
        {
            SetShowMaskGraphic = _rt.FindMethod(mask, "set_showMaskGraphic", 1);
            if (SetShowMaskGraphic != IntPtr.Zero) MaskType = _rt.GetTypeObject(mask);
        }
        _resolved = true;
        return ResolveFonts();
    }

    private bool Fail(string what)
    {
        _failed = true;
        Log.Warn($"Custom screens are unavailable: {what} not found (game update?).");
        return false;
    }

    /// <summary>Borrows the game's own fonts from text that is on screen: the heavy heading face and the body face.</summary>
    private bool ResolveFonts()
    {
        if (_rt.IsAlive(HeadingFont) && _rt.IsAlive(BodyFont)) return true;
        var byName = new Dictionary<string, IntPtr>();
        foreach (var label in _rt.FindObjectsOfType(_mvcTextClass))
        {
            var font = _rt.Invoke(_getFont, label);
            if (font == IntPtr.Zero) continue;
            byName.TryAdd(_rt.ReadString(_rt.Invoke(_objectGetName, font)) ?? "", font);
        }
        if (byName.Count == 0) return false;
        IntPtr Find(params string[] parts)
        {
            foreach (var part in parts)
                foreach (var (name, font) in byName)
                    if (name.Contains(part, StringComparison.OrdinalIgnoreCase)) return font;
            return IntPtr.Zero;
        }
        var heading = Find("extraboldit", "extrabold", "black", "bold");
        var body = Find("semibold", "medium", "regular");
        foreach (var font in byName.Values)
        {
            if (heading == IntPtr.Zero) heading = font;
            if (body == IntPtr.Zero) body = font;
        }
        _rt.Pin(heading);
        _rt.Pin(body);
        HeadingFont = heading;
        BodyFont = body;
        Log.Info("Custom screen fonts: " + string.Join(", ", byName.Keys));
        return true;
    }

    // ---- the game's interface sprites ------------------------------------------------------------------------

    /// <summary>One of the game's loaded interface sprites by name (rounded panels, shadows, icons).</summary>
    public IntPtr Sprite(string name)
    {
        if (_sprites.TryGetValue(name, out var sprite) && _rt.IsAlive(sprite)) return sprite;
        if (_spritesLoaded && !_sprites.ContainsKey(name)) return IntPtr.Zero;
        // First use, or a sprite was unloaded: index what is loaded now.
        _spritesLoaded = true;
        _sprites.Clear();
        _corners.Clear();
        try
        {
            foreach (var candidate in _rt.ReadArray(_rt.Invoke(_findAll, IntPtr.Zero, _rt.GetTypeObject(_spriteClass))))
            {
                if (candidate == IntPtr.Zero || !_rt.IsAlive(candidate)) continue;
                var spriteName = _rt.ReadString(_rt.Invoke(_objectGetName, candidate));
                if (string.IsNullOrEmpty(spriteName) || !_sprites.TryAdd(spriteName, candidate)) continue;
                if (_pinned.Add(candidate)) _rt.Pin(candidate);
            }
            Log.Info($"Custom screens: {_sprites.Count} game interface sprites available.");
        }
        catch (Exception e) { Log.Warn("Could not index the game's interface sprites: " + e.Message); }
        return _sprites.TryGetValue(name, out sprite) ? sprite : IntPtr.Zero;
    }

    /// <summary>Size in pixels of a nine-sliced sprite's corner.</summary>
    public float SpriteCorner(string name)
    {
        if (_corners.TryGetValue(name, out var corner)) return corner;
        corner = 20f;
        var sprite = Sprite(name);
        if (sprite != IntPtr.Zero)
        {
            var border = (float*)(_rt.Invoke(_spriteGetBorder, sprite) + 16); // left, bottom, right, top
            corner = Math.Max(1f, (border[0] + border[1] + border[2] + border[3]) / 4f);
        }
        return _corners[name] = corner;
    }

    public IntPtr NewCanvas()
    {
        var canvasObject = _rt.NewObject(GameObjectClass, ".ctor(System.String)", _rt.NewString("OpenFairway Screen"));
        var layer = 5;
        _rt.Invoke(SetLayer, canvasObject, (IntPtr)(&layer));
        var canvas = _rt.Invoke(AddComponent, canvasObject, CanvasType);
        int overlay = 0, order = 30000;
        _rt.Invoke(_setRenderMode, canvas, (IntPtr)(&overlay));
        _rt.Invoke(_setSortingOrder, canvas, (IntPtr)(&order));
        if (_dontDestroyOnLoad != IntPtr.Zero) _rt.Invoke(_dontDestroyOnLoad, IntPtr.Zero, canvasObject);
        _rt.Pin(canvasObject);
        return canvasObject;
    }

    /// <summary>Hides (or brings back) the game's own interface, leaving its 3D scene, while a screen is open.</summary>
    public void ShowGameCanvases(bool show, IntPtr ownCanvasObject)
    {
        try
        {
            byte on = 1, off = 0;
            if (show)
            {
                foreach (var canvas in _hiddenCanvases)
                    if (_rt.IsAlive(canvas)) _rt.Invoke(_behaviourSetEnabled, canvas, (IntPtr)(&on));
                _hiddenCanvases.Clear();
                return;
            }
            foreach (var canvas in _rt.FindObjectsOfType(_canvasClass))
            {
                if (canvas == IntPtr.Zero || !_rt.IsAlive(canvas)) continue;
                if (_rt.Invoke(ComponentGetGameObject, canvas) == ownCanvasObject) continue;
                if (*(byte*)(_rt.Invoke(_isRootCanvas, canvas) + 16) == 0 || *(byte*)(_rt.Invoke(_behaviourGetEnabled, canvas) + 16) == 0) continue;
                if (_pinned.Add(canvas)) _rt.Pin(canvas);
                _rt.Invoke(_behaviourSetEnabled, canvas, (IntPtr)(&off));
                _hiddenCanvases.Add(canvas);
            }
        }
        catch (Exception e) { Log.Warn("Could not change the game's interface visibility: " + e.Message); }
    }

    // ---- textures --------------------------------------------------------------------------------------------

    public IntPtr Texture(string path, out int width, out int height)
    {
        if (_textures.TryGetValue(path, out var cached) && (cached.Texture == IntPtr.Zero || _rt.IsAlive(cached.Texture)))
        {
            (width, height) = (cached.Width, cached.Height);
            return cached.Texture;
        }
        var texture = IntPtr.Zero;
        width = height = 0;
        try { texture = TextureFromBytes(File.ReadAllBytes(path), out width, out height); }
        catch (Exception e) { Log.Warn($"Image {path} not loaded: {e.Message}"); }
        _textures[path] = (texture, width, height);
        return texture;
    }

    /// <summary>Makes a texture from PNG or JPG bytes. The texture is kept alive for the session.</summary>
    public IntPtr TextureFromBytes(byte[] bytes, out int width, out int height)
    {
        int w = 2, h = 2;
        var texture = _rt.NewObject(_texture2DClass, ".ctor(System.Int32,System.Int32)", (IntPtr)(&w), (IntPtr)(&h));
        _rt.Invoke(_loadImage, IntPtr.Zero, texture, _rt.NewByteArray(bytes));
        _rt.Pin(texture);
        var clamp = 1;
        if (_setWrapMode != IntPtr.Zero) _rt.Invoke(_setWrapMode, texture, (IntPtr)(&clamp));
        width = *(int*)(_rt.Invoke(_textureGetWidth, texture) + 16);
        height = *(int*)(_rt.Invoke(_textureGetHeight, texture) + 16);
        return texture;
    }

    /// <summary>A white texture whose opacity fades from full to none, left to right or top to bottom.</summary>
    public IntPtr GradientTexture(bool vertical)
    {
        var existing = vertical ? _gradientVertical : _gradientHorizontal;
        if (existing != IntPtr.Zero && _rt.IsAlive(existing)) return existing;
        const int steps = 256;
        var (w, h) = vertical ? (1, steps) : (steps, 1);
        var pixels = new byte[steps * 4];
        for (var i = 0; i < steps; i++)
        {
            var t = 1f - i / (float)(steps - 1);
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = 255;
            pixels[i * 4 + 3] = (byte)Math.Round(255 * t * t * (3f - 2f * t));
        }
        var texture = IntPtr.Zero;
        try { texture = TextureFromBytes(Png.Encode(w, h, pixels), out _, out _); }
        catch (Exception e) { Log.Warn("Gradient texture not made: " + e.Message); }
        if (vertical) _gradientVertical = texture; else _gradientHorizontal = texture;
        return texture;
    }

    /// <summary>A white disc whose opacity falls off smoothly from the centre to the edge.</summary>
    public IntPtr GlowTexture()
    {
        if (_glowTexture != IntPtr.Zero && _rt.IsAlive(_glowTexture)) return _glowTexture;
        const int size = 128;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dx = (x + 0.5f) / size * 2f - 1f;
                var dy = (y + 0.5f) / size * 2f - 1f;
                var t = Math.Clamp(1f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f);
                var i = (y * size + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
                pixels[i + 3] = (byte)Math.Round(255 * t * t * (3f - 2f * t));
            }
        try { _glowTexture = TextureFromBytes(Png.Encode(size, size, pixels), out _, out _); }
        catch (Exception e) { Log.Warn("Glow texture not made: " + e.Message); }
        return _glowTexture;
    }

    /// <summary>A white ring sprite (for radially filled images) of a given thickness relative to its size.</summary>
    public IntPtr RingSprite(float thicknessRatio)
    {
        if (_spriteCreate == IntPtr.Zero || SetFillAmount == IntPtr.Zero || SetFillMethod == IntPtr.Zero) return IntPtr.Zero;
        var key = (int)Math.Round(Math.Clamp(thicknessRatio, 0.02f, 0.5f) * 100);
        if (_rings.TryGetValue(key, out var cached) && _rt.IsAlive(cached)) return cached;
        const int size = 256;
        var outer = size / 2f - 1.5f;
        var inner = outer - key / 100f * size;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dx = x + 0.5f - size / 2f;
                var dy = y + 0.5f - size / 2f;
                var r = MathF.Sqrt(dx * dx + dy * dy);
                var coverage = Math.Clamp(outer - r + 0.5f, 0f, 1f) * Math.Clamp(r - inner + 0.5f, 0f, 1f);
                var i = (y * size + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
                pixels[i + 3] = (byte)Math.Round(255 * coverage);
            }
        var sprite = IntPtr.Zero;
        try
        {
            var texture = TextureFromBytes(Png.Encode(size, size, pixels), out _, out _);
            var rect = stackalloc float[4] { 0f, 0f, size, size };
            var pivot = stackalloc float[2] { 0.5f, 0.5f };
            sprite = _rt.Invoke(_spriteCreate, IntPtr.Zero, texture, (IntPtr)rect, (IntPtr)pivot);
            if (sprite != IntPtr.Zero) _rt.Pin(sprite);
        }
        catch (Exception e) { Log.Warn("Ring sprite not made: " + e.Message); }
        _rings[key] = sprite;
        return sprite;
    }

    public int ScreenWidth() => *(int*)(_rt.Invoke(_screenWidth, IntPtr.Zero) + 16);
    public int ScreenHeight() => *(int*)(_rt.Invoke(_screenHeight, IntPtr.Zero) + 16);

    /// <summary>The game's own switch that makes it ignore all input, used while a custom screen is in front.</summary>
    public void BlockGameInput(bool block)
    {
        if (_setBlockAllInput == IntPtr.Zero) return;
        if (!block && _open.Count > 0) return; // another custom screen is still open
        byte value = block ? (byte)1 : (byte)0;
        try { _rt.Invoke(_setBlockAllInput, IntPtr.Zero, (IntPtr)(&value)); }
        catch (Exception e) { Log.Warn("Could not change the game's input block: " + e.Message); }
    }

    // ---- window input (independent of the game's input system, which is blocked while a screen is open) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    private IntPtr Window()
    {
        if (_window == IntPtr.Zero)
        {
            using var process = Process.GetCurrentProcess();
            _window = process.MainWindowHandle;
        }
        return _window;
    }

    public bool WindowHasFocus() => Window() != IntPtr.Zero && GetForegroundWindow() == Window();

    public bool MousePosition(out float x, out float y)
    {
        x = y = 0;
        if (!GetCursorPos(out var point) || !ScreenToClient(Window(), ref point)) return false;
        x = point.X;
        y = point.Y;
        return true;
    }

    public static bool KeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct PadState
    {
        public uint Packet;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] private static extern uint PadGetState(int index, out PadState state);

    private int _padIndex = -1, _padScanCountdown;
    private bool _padUnavailable;

    /// <summary>
    /// The first connected gamepad's buttons (XInput bits: d-pad up 1, down 2, left 4, right 8, A 0x1000, B 0x2000),
    /// with the left stick folded into the d-pad bits. Zero when there is no gamepad.
    /// </summary>
    public int Gamepad()
    {
        if (_padUnavailable) return 0;
        try
        {
            PadState state = default;
            if (_padIndex < 0 || PadGetState(_padIndex, out state) != 0)
            {
                // Looking for a pad that is not there is slow, so only look now and then.
                _padIndex = -1;
                if (--_padScanCountdown > 0) return 0;
                _padScanCountdown = 120;
                for (var i = 0; i < 4 && _padIndex < 0; i++)
                    if (PadGetState(i, out state) == 0) _padIndex = i;
                if (_padIndex < 0) return 0;
            }
            int buttons = state.Buttons;
            const int threshold = 18000;
            if (state.LeftY > threshold) buttons |= 0x1;
            if (state.LeftY < -threshold) buttons |= 0x2;
            if (state.LeftX < -threshold) buttons |= 0x4;
            if (state.LeftX > threshold) buttons |= 0x8;
            return buttons;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _padUnavailable = true;
            return 0;
        }
    }
}

/// <summary>A minimal PNG writer (8-bit RGBA), used to make gradient textures at run time.</summary>
internal static class Png
{
    public static byte[] Encode(int width, int height, byte[] rgba)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8;  // bits per channel
        header[9] = 6;  // RGBA
        Chunk(output, "IHDR", header);

        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0); // no filter
            raw.Write(rgba, y * width * 4, width * 4);
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true)) zlib.Write(raw.GetBuffer(), 0, (int)raw.Length);
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        output.Write(length);
        var typed = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) typed[i] = (byte)type[i];
        data.CopyTo(typed, 4);
        output.Write(typed);
        var crc = new byte[4];
        WriteBigEndian(crc, 0, Crc(typed));
        output.Write(crc);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
