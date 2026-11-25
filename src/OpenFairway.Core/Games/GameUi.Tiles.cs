using System;
using System.Collections.Generic;
using OpenFairway.API;
using OpenFairway.Core.Il2Cpp;

namespace OpenFairway.Core.Games;

/// <summary>
/// The main menu of PGA TOUR 2K25 (<see cref="MenuStyle.Tiles"/>): four tabs of tiles, several of them drawn with
/// fixed artwork that is switched off to show a mod's text, under a banner panel that mods restyle.
/// </summary>
internal sealed unsafe partial class GameUi
{
    private const string OurLabel = "OpenFairway Label";

    // Artwork and status children of a tile that are switched off when a slot is turned into a plain text tile.
    private static readonly HashSet<string> ArtChildren = new()
        { "Image", "Image_Highlight", "Logo", "Logo_Highlight", "ClubPassSeasonLabel", "Grid", "Offline Label" };
    private static readonly string[] ButtonNames = { "Button_Default", "ButtonDefault", "Button" };

    private void ApplyTileView(IntPtr screen, IntPtr screenRoot, IntPtr panelsModel, IntPtr[] currentTabs)
    {
        if (_banner != null) ApplyBanner(screenRoot, Field(screen, "model"), _banner);
        else if (_removeGoOnline)
        {
            var callout = Find(screenRoot, "PromoBanner/GoOnline/CalloutAdvance");
            Step("hiding go-online");
            if (callout != IntPtr.Zero) SetActive(GameObject(callout), false);
            ClearClick(Field(screen, "model"), "Offline Banner", 0);
            Step("go-online cleared");
        }

        // Turn the visible tab's slots into plain text tiles where a mod has taken the tab over.
        var highlighted = Int(_rt.Invoke(_getHighlightedIndex, panelsModel));
        if (highlighted < 0 || highlighted >= currentTabs.Length) return;

        // A tab view built for the game's original tiles keeps their locked look. After changing tiles, switch to
        // another tab and straight back once, which makes the game rebuild the view for the new tiles.
        if (_menu!.RefreshStage == 1 && currentTabs.Length > 1)
        {
            _menu.RefreshReturnTo = highlighted;
            var other = highlighted == 0 ? 1 : 0;
            _rt.Invoke(_setHighlightedIndex, panelsModel, (IntPtr)(&other));
            _menu.RefreshStage = 2;
            return;
        }
        if (_menu.RefreshStage == 2)
        {
            var back = _menu.RefreshReturnTo;
            _rt.Invoke(_setHighlightedIndex, panelsModel, (IntPtr)(&back));
            _menu.RefreshStage = 0;
            _menu.Refreshed = true;
            return;
        }
        if (TabOf(currentTabs[highlighted]) is not { } visibleTab || !_tiles.ContainsKey(visibleTab)) return;
        var buttons = IntPtr.Zero;
        for (var i = 0; i < ChildCount(screenRoot) && buttons == IntPtr.Zero; i++)
            buttons = Find(GetChild(screenRoot, i), "ButtonsDescription/0: Buttons");
        if (buttons == IntPtr.Zero) return;
        var visibleModels = Models(currentTabs[highlighted]);
        for (var i = 0; i < ChildCount(buttons) && i < visibleModels.Length; i++)
        {
            Step($"text tile {visibleTab} {i}");
            MakeTextTile(GetChild(buttons, i), _labelOverrides.TryGetValue(visibleModels[i], out var overrideText) ? overrideText : NameText(visibleModels[i]));
            Step($"text tile {visibleTab} {i} done");
        }
    }


    /// <summary>Hides a slot's fixed artwork and gives it a text label, using a copy of the game's own label object.</summary>
    private void MakeTextTile(IntPtr slot, string? text)
    {
        var button = slot;
        foreach (var name in ButtonNames)
        {
            var child = Find(slot, name);
            if (child != IntPtr.Zero) { button = child; break; }
        }

        IntPtr ownLabel = IntPtr.Zero, ourLabel = IntPtr.Zero;
        for (var i = 0; i < ChildCount(button); i++)
        {
            var child = GetChild(button, i);
            var childName = Name(child);
            if (childName == "Label") ownLabel = child;
            else if (childName == OurLabel) ourLabel = child;
            else if (childName != null && ArtChildren.Contains(childName) && IsActive(GameObject(child))) SetActive(GameObject(child), false);
        }

        if (ownLabel != IntPtr.Zero)
        {
            // A slot that already draws its model's name. Keep a hidden spare of its label for slots that have none.
            if (!_rt.IsAlive(_labelTemplate) && _rt.IsAlive(_screenContainer))
            {
                _labelTemplate = _rt.Invoke(_instantiate, IntPtr.Zero, GameObject(ownLabel), Transform(_screenContainer));
                Native.gchandle_new(_labelTemplate, 0);
                SetActive(_labelTemplate, false);
            }
            // Some slots print a fixed caption for the tile they were designed for (the first HOME slot says
            // QUICK PLAY whatever it holds), so make the label say what this tile is.
            if (text != null)
            {
                var own = _rt.Invoke(_getComponent, GameObject(ownLabel), _textType);
                if (own != IntPtr.Zero && _rt.ReadString(_rt.Invoke(_getText, own)) != text) _rt.Invoke(_setText, own, _rt.NewString(text));
            }
            return;
        }
        if (text == null) return;
        if (ourLabel == IntPtr.Zero)
        {
            if (!_rt.IsAlive(_labelTemplate)) return;
            var clone = _rt.Invoke(_instantiate, IntPtr.Zero, _labelTemplate, button);
            _rt.Invoke(_setName, clone, _rt.NewString(OurLabel));
            SetActive(clone, true);
            ourLabel = _rt.Invoke(_goGetTransform, clone);
        }
        var component = _rt.Invoke(_getComponent, GameObject(ourLabel), _textType);
        if (component == IntPtr.Zero) return;
        if (_rt.ReadString(_rt.Invoke(_getText, component)) != text) _rt.Invoke(_setText, component, _rt.NewString(text));
        // A copied label is not driven by the tile's own colour states, so give it the normal (white) label colour.
        var white = stackalloc float[4] { 1f, 1f, 1f, 1f };
        _rt.Invoke(_setColor, component, (IntPtr)white);
    }


    /// <summary>Empties the click actions of a named model in a model tree (searching to a small depth).</summary>
    private bool ClearClick(IntPtr model, string inspectorName, int depth)
    {
        if (model == IntPtr.Zero || depth > 5) return false;
        if (InspectorName(model) == inspectorName)
        {
            foreach (var (key, entry) in _rt.ReadStringDictionary(Field(model, "looseData")))
            {
                if (key != OnClickKey || entry == IntPtr.Zero) continue;
                var actions = Field(entry, "data");
                if (actions == IntPtr.Zero || *(nuint*)(actions + 0x18) == 0) continue;
                var empty = Native.array_new(Native.class_get_element_class(_rt.GetClass(actions)), 0);
                _rt.WriteReference(entry, entry + (nint)_rt.FieldOffset(_rt.GetClass(entry), "data"), empty);
            }
            return true;
        }
        if (!HasField(model, "_models")) return false;
        foreach (var child in Models(model)) if (ClearClick(child, inspectorName, depth + 1)) return true;
        return false;
    }


    // ---- banner --------------------------------------------------------------------------------------------

    private const string BannerImageName = "OpenFairway Banner", BannerSubtitleName = "OpenFairway Subtitle";

    /// <summary>Restyles the panel above the tiles using the game's own objects: its text, a copy of one of its image
    /// objects for the artwork, and its click slot for the action.</summary>
    private void ApplyBanner(IntPtr screenRoot, IntPtr screenModel, MenuBanner banner)
    {
        var panel = Find(screenRoot, "PromoBanner/GoOnline");
        if (panel == IntPtr.Zero) return;

        var icon = Find(panel, "Icon_Connection");
        if (icon != IntPtr.Zero && IsActive(GameObject(icon))) SetActive(GameObject(icon), false);

        var callout = Find(panel, "CalloutAdvance");
        if (callout != IntPtr.Zero)
        {
            var wanted = banner.CalloutLabel != null;
            if (IsActive(GameObject(callout)) != wanted) SetActive(GameObject(callout), wanted);
            if (wanted)
            {
                // The prompt has separate labels for its normal and highlighted states; relabel all of them.
                byte includeInactive = 1;
                foreach (var label in _rt.ReadArray(_rt.Invoke(_getComponentsInChildren, callout, _textType, (IntPtr)(&includeInactive))))
                    if (label != IntPtr.Zero && _rt.ReadString(_rt.Invoke(_getText, label)) != banner.CalloutLabel)
                        _rt.Invoke(_setText, label, _rt.NewString(banner.CalloutLabel!));
            }
        }

        // Title in the game's heavy heading style (a copy of a tile label) when one is available, else the panel's own text.
        var plainTitle = Find(panel, "OfflineState_Default");
        if (plainTitle == IntPtr.Zero) return;
        var title = Find(panel, BannerTitleName);
        if (title == IntPtr.Zero && _rt.IsAlive(_labelTemplate))
        {
            var clone = _rt.Invoke(_instantiate, IntPtr.Zero, _labelTemplate, panel);
            _rt.Invoke(_setName, clone, _rt.NewString(BannerTitleName));
            StripAnimation(clone);
            SetActive(clone, true);
            title = _rt.Invoke(_goGetTransform, clone);
            Centre(title, 840f, 70f);
            SetFontSize(clone, BannerTitleSize);
        }
        if (title != IntPtr.Zero)
        {
            if (IsActive(GameObject(plainTitle))) SetActive(GameObject(plainTitle), false);
        }
        else title = plainTitle;
        SetText(title, banner.Title);
        MoveTo(title, 0f, BannerTitleY);
        MakeOpaque(title);
        if (banner.LogoPath != null && ApplyBannerLogo(panel, banner.LogoPath) && IsActive(GameObject(title)))
            SetActive(GameObject(title), false);

        if (banner.Subtitle != null)
        {
            var subtitle = Find(panel, BannerSubtitleName);
            if (subtitle == IntPtr.Zero)
            {
                var clone = _rt.Invoke(_instantiate, IntPtr.Zero, GameObject(plainTitle), panel);
                _rt.Invoke(_setName, clone, _rt.NewString(BannerSubtitleName));
                StripAnimation(clone);
                SetActive(clone, true);
                subtitle = _rt.Invoke(_goGetTransform, clone);
                SetFontSize(clone, BannerSubtitleSize);
            }
            SetText(subtitle, banner.Subtitle);
            MoveTo(subtitle, 0f, BannerTitleY - BannerSubtitleGap);
            MakeOpaque(subtitle);
        }

        if (banner.ImagePath != null && !_bannerTextureFailed && Find(panel, BannerImageName) == IntPtr.Zero)
        {
            var fromGame = false;
            if (_bannerTexture == IntPtr.Zero && banner.CoursePhoto)
            {
                try
                {
                    var courses = Courses.All;
                    for (var attempt = 0; attempt < 6 && courses.Count > 0 && _bannerTexture == IntPtr.Zero; attempt++)
                    {
                        var photo = Courses.Thumbnail(courses[(DateTime.Now.DayOfYear * 7 + attempt) % courses.Count].Id);
                        if (photo != null) _bannerTexture = photo.Handle;
                    }
                    fromGame = _bannerTexture != IntPtr.Zero;
                }
                catch (Exception e) { Log.Warn("No course photo for the banner: " + e.Message); }
            }
            if (_bannerTexture == IntPtr.Zero) _bannerTexture = LoadTexture(banner.ImagePath);
            if (_bannerTexture != IntPtr.Zero)
            {
                // A plain new image object, not a copy of a game object: copies carry the menu's animation component,
                // and screen transitions wait on every one of those.
                var created = _rt.NewObject(_gameObjectClass, ".ctor(System.String)", _rt.NewString(BannerImageName));
                var image = _rt.Invoke(_addComponent, created, _rawImageType);
                var rect = _rt.Invoke(_goGetTransform, created);
                byte worldPositionStays = 0;
                _rt.Invoke(_setParent, rect, panel, (IntPtr)(&worldPositionStays));
                var layer = *(int*)(_rt.Invoke(_getLayer, GameObject(panel)) + 16);
                _rt.Invoke(_setLayer, created, (IntPtr)(&layer));
                var zero = stackalloc float[2] { 0f, 0f };
                var one = stackalloc float[2] { 1f, 1f };
                var scale = stackalloc float[3] { 1f, 1f, 1f };
                _rt.Invoke(_setAnchorMin, rect, (IntPtr)zero);
                _rt.Invoke(_setAnchorMax, rect, (IntPtr)one);
                _rt.Invoke(_setOffsetMin, rect, (IntPtr)zero);
                _rt.Invoke(_setOffsetMax, rect, (IntPtr)zero);
                _rt.Invoke(_setLocalScale, rect, (IntPtr)scale);
                var index = 1; // just above the panel's plain background
                _rt.Invoke(_setSiblingIndex, rect, (IntPtr)(&index));
                if (image != IntPtr.Zero)
                {
                    _rt.Invoke(_setTexture, image, _bannerTexture);
                    if (fromGame)
                    {
                        // Photos are bright; dim them so the white title stays readable.
                        var dim = stackalloc float[4] { 0.44f, 0.46f, 0.52f, 1f };
                        _rt.Invoke(_setColor, image, (IntPtr)dim);
                    }
                    byte raycast = 0; // clicks go through to the panel
                    _rt.Invoke(_setRaycastTarget, image, (IntPtr)(&raycast));
                }
            }
        }

        SetBannerAction(screenModel, banner);
    }

    private const string BannerLogoName = "OpenFairway Logo";
    private const float BannerLogoHeight = 80f;

    /// <summary>Puts the wordmark image where the title text would be. True when the logo is showing.</summary>
    private bool ApplyBannerLogo(IntPtr panel, string path)
    {
        if (Find(panel, BannerLogoName) != IntPtr.Zero) return true;
        if (!LoadBannerLogo(path)) return false;
        var created = _rt.NewObject(_gameObjectClass, ".ctor(System.String)", _rt.NewString(BannerLogoName));
        var image = _rt.Invoke(_addComponent, created, _rawImageType);
        var rect = _rt.Invoke(_goGetTransform, created);
        byte worldPositionStays = 0;
        _rt.Invoke(_setParent, rect, panel, (IntPtr)(&worldPositionStays));
        var layer = *(int*)(_rt.Invoke(_getLayer, GameObject(panel)) + 16);
        _rt.Invoke(_setLayer, created, (IntPtr)(&layer));
        var scale = stackalloc float[3] { 1f, 1f, 1f };
        _rt.Invoke(_setLocalScale, rect, (IntPtr)scale);
        Centre(rect, BannerLogoHeight * _bannerLogoAspect, BannerLogoHeight);
        var position = stackalloc float[2] { 0f, BannerTitleY };
        _rt.Invoke(_setAnchoredPosition, rect, (IntPtr)position);
        if (image != IntPtr.Zero)
        {
            _rt.Invoke(_setTexture, image, _bannerLogo);
            byte raycast = 0;
            _rt.Invoke(_setRaycastTarget, image, (IntPtr)(&raycast));
        }
        return true;
    }

    // Positions and sizes inside the panel, in the menu's own layout units, measured from the panel's centre.
    private const float BannerTitleY = 96f, BannerSubtitleGap = 52f;
    private const int BannerTitleSize = 54, BannerSubtitleSize = 20;
    private const string BannerTitleName = "OpenFairway Title";

    /// <summary>Removes the menu's layout/animation component from a copied object so screen transitions do not wait on it.</summary>
    private void StripAnimation(IntPtr gameObject)
    {
        if (_nodeTransformType == IntPtr.Zero) return;
        var node = _rt.Invoke(_getComponent, gameObject, _nodeTransformType);
        if (node != IntPtr.Zero) _rt.Invoke(_destroy, IntPtr.Zero, node);
    }

    private void SetFontSize(IntPtr gameObject, int size)
    {
        var text = _rt.Invoke(_getComponent, gameObject, _textType);
        if (text == IntPtr.Zero) return;
        // Fixed size, allowed to draw past its box: the copied label was set up to shrink to a tile.
        byte bestFit = 0;
        var overflow = 1;
        _rt.Invoke(_setBestFit, text, (IntPtr)(&bestFit));
        _rt.Invoke(_setHorizontalOverflow, text, (IntPtr)(&overflow));
        _rt.Invoke(_setVerticalOverflow, text, (IntPtr)(&overflow));
        _rt.Invoke(_setFontSize, text, (IntPtr)(&size));
    }

    /// <summary>Anchors a text object to the panel's centre with a fixed box, so it can be positioned freely.</summary>
    private void Centre(IntPtr rectTransform, float width, float height)
    {
        var half = stackalloc float[2] { 0.5f, 0.5f };
        var size = stackalloc float[2] { width, height };
        _rt.Invoke(_setAnchorMin, rectTransform, (IntPtr)half);
        _rt.Invoke(_setAnchorMax, rectTransform, (IntPtr)half);
        _rt.Invoke(_setSizeDelta, rectTransform, (IntPtr)size);
    }

    /// <summary>Copied text objects start faded out (their fade-in is driven by an owner they no longer have).</summary>
    private void MakeOpaque(IntPtr transform)
    {
        var text = _rt.Invoke(_getComponent, GameObject(transform), _textType);
        if (text == IntPtr.Zero) return;
        var color = _rt.Invoke(_getColor, text);
        if (*(float*)(color + 16 + 12) > 0.99f && *(float*)(color + 16) > 0.9f) return;
        var white = stackalloc float[4] { 0.96f, 0.96f, 0.96f, 1f };
        _rt.Invoke(_setColor, text, (IntPtr)white);
    }

    private void SetText(IntPtr transform, string text)
    {
        if (transform == IntPtr.Zero) return;
        var component = _rt.Invoke(_getComponent, GameObject(transform), _textType);
        if (component != IntPtr.Zero && _rt.ReadString(_rt.Invoke(_getText, component)) != text)
            _rt.Invoke(_setText, component, _rt.NewString(text));
    }

    private void MoveTo(IntPtr rectTransform, float x, float y)
    {
        var current = _rt.Invoke(_getAnchoredPosition, rectTransform);
        if (Math.Abs(*(float*)(current + 16) - x) < 0.5f && Math.Abs(*(float*)(current + 20) - y) < 0.5f) return;
        var position = stackalloc float[2] { x, y };
        _rt.Invoke(_setAnchoredPosition, rectTransform, (IntPtr)position);
    }

    private IntPtr LoadTexture(string path)
    {
        try
        {
            var bytes = System.IO.File.ReadAllBytes(path);
            int width = 2, height = 2;
            var texture = _rt.NewObject(_texture2DClass, ".ctor(System.Int32,System.Int32)", (IntPtr)(&width), (IntPtr)(&height));
            var loaded = _rt.Invoke(_loadImage, IntPtr.Zero, texture, _rt.NewByteArray(bytes));
            if (*(byte*)(loaded + 16) == 0) throw new InvalidOperationException("the image could not be decoded");
            Native.gchandle_new(texture, 0);
            return texture;
        }
        catch (Exception e)
        {
            _bannerTextureFailed = true;
            Log.Warn("Banner image not loaded: " + e.Message);
            return IntPtr.Zero;
        }
    }

    /// <summary>Points the panel's existing click slots (select and hotkey) at the mod's action instead of "go online".</summary>
    private void SetBannerAction(IntPtr screenModel, MenuBanner banner)
    {
        var model = FindModel(screenModel, "Offline Banner", 0);
        if (model == IntPtr.Zero) return;
        foreach (var (key, entry) in _rt.ReadStringDictionary(Field(model, "looseData")))
        {
            if (key != OnClickKey || entry == IntPtr.Zero) continue;
            var actions = Field(entry, "data");
            if (actions == IntPtr.Zero) continue;
            var count = (int)*(nuint*)(actions + 0x18);
            uint align;
            var stride = Native.class_value_size(Native.class_get_element_class(_rt.GetClass(actions)), &align);
            for (var i = 0; i < count; i++)
            {
                // MVC.InteractiveAction: "action" (no arguments) sits 0x30 into the struct, "nodeAction" (one argument) at 0x38.
                var element = actions + 0x20 + i * stride;
                var plain = *(IntPtr*)(element + 0x30);
                var node = *(IntPtr*)(element + 0x38);
                if (plain != IntPtr.Zero && plain != _bannerAction)
                {
                    if (_bannerAction == IntPtr.Zero) _bannerAction = _rt.CreateDelegate(_actionClass, () => _banner?.OnClick?.Invoke());
                    _rt.WriteReference(actions, element + 0x30, _bannerAction);
                }
                if (node != IntPtr.Zero && !_bannerNodeActions.Contains(node))
                {
                    var replacement = _rt.CreateDelegate(_rt.GetClass(node), _ => _banner?.OnClick?.Invoke());
                    _bannerNodeActions.Add(replacement);
                    _rt.WriteReference(actions, element + 0x38, replacement);
                }
            }
        }
    }

    private readonly HashSet<IntPtr> _bannerNodeActions = new();

    private IntPtr FindModel(IntPtr model, string inspectorName, int depth)
    {
        if (model == IntPtr.Zero || depth > 5) return IntPtr.Zero;
        if (InspectorName(model) == inspectorName) return model;
        if (!HasField(model, "_models")) return IntPtr.Zero;
        foreach (var child in Models(model))
        {
            var found = FindModel(child, inspectorName, depth + 1);
            if (found != IntPtr.Zero) return found;
        }
        return IntPtr.Zero;
    }

    /// <summary>Loads the banner's wordmark once (both menu styles draw it). False if it cannot be read.</summary>
    private bool LoadBannerLogo(string path)
    {
        if (_bannerLogo != IntPtr.Zero) return true;
        if (_bannerLogoFailed) return false;
        try
        {
            var bytes = System.IO.File.ReadAllBytes(path);
            int width = 2, height = 2;
            var texture = _rt.NewObject(_texture2DClass, ".ctor(System.Int32,System.Int32)", (IntPtr)(&width), (IntPtr)(&height));
            var loaded = _rt.Invoke(_loadImage, IntPtr.Zero, texture, _rt.NewByteArray(bytes));
            if (*(byte*)(loaded + 16) == 0) throw new InvalidOperationException("the image could not be decoded");
            // Clamped, so a box larger than the image shows its clear edge rather than the image again.
            var clamp = TextureWrapClamp;
            if (_setWrapMode != IntPtr.Zero) _rt.Invoke(_setWrapMode, texture, (IntPtr)(&clamp));
            Native.gchandle_new(texture, 0);
            _bannerLogo = texture;
            // PNG width and height sit in the header at fixed offsets.
            _bannerLogoAspect = bytes.Length > 24
                ? (float)((bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19]) / Math.Max(1, (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23])
                : 10f;
            return true;
        }
        catch (Exception e)
        {
            _bannerLogoFailed = true;
            Log.Warn("Banner logo not loaded: " + e.Message);
            return false;
        }
    }

    private const int TextureWrapClamp = 1;
}
