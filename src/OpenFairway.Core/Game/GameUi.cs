using System;
using System.Collections.Generic;
using System.Linq;
using OpenFairway.API;
using OpenFairway.Core.Il2Cpp;

namespace OpenFairway.Core.Game;

/// <summary>
/// Game UI services for mods, built on the game's own menu system so everything looks native: tiles come from
/// the game's tile builder, popups from the game's popup function, and labels are the game's own text objects.
/// The game rebuilds its menu often, so every change here is a rule that is re-applied, not a one-off edit.
/// </summary>
internal sealed unsafe class GameUi : IGameUi, IMainMenu
{
    private const string NameKey = "MVC.Bindings+Name", DescriptionKey = "MVC.Bindings+SubName", OnClickKey = "MVC.Bindings+OnClick";
    private const string OurLabel = "OpenFairway Label";
    private const int TextTileName = 13;   // Localized.MainMenuPlayNow.QuickPlay_Upper
    private const int CareerTileName = 14; // Localized.MainMenuPlayNow.CareerHome

    // Artwork and status children of a tile that are switched off when a slot is turned into a plain text tile.
    private static readonly HashSet<string> ArtChildren = new()
        { "Image", "Image_Highlight", "Logo", "Logo_Highlight", "ClubPassSeasonLabel", "Grid", "Offline Label" };
    private static readonly string[] ButtonNames = { "Button_Default", "ButtonDefault", "Button" };

    private static readonly Dictionary<GameTile, string> GameTileNames = new()
    {
        [GameTile.QuickPlay] = "QuickPlay_Upper",
        [GameTile.Training] = "Training_Upper",
        [GameTile.NewCourse] = "NewCourse_Upper",
        [GameTile.UnpublishedCourses] = "UnpublishedCourses",
        [GameTile.Career] = "CareerHome",
    };

    private readonly GameRuntime _rt;
    private readonly MainThread _mainThread;

    // Rules registered by mods.
    private MenuTab[]? _tabs;
    private readonly Dictionary<MenuTab, MenuTile[]> _tiles = new();
    private bool _removeGoOnline;
    private MenuBanner? _banner;
    private IntPtr _bannerTexture, _bannerAction, _bannerLogo;
    private bool _bannerTextureFailed, _bannerLogoFailed;
    private float _bannerLogoAspect = 10f;
    private readonly List<(string Pattern, string Replacement)> _textRules = new();
    private readonly Dictionary<string, (string? Header, string? Message)> _popupRules = new();
    private readonly HashSet<IntPtr> _handledPopups = new();
    private int _tileResets;
    private IntPtr _mainMenuClass, _getComponentsInChildren, _detailedChoice;
    private readonly Dictionary<IntPtr, string> _labelOverrides = new();

    // Resolved game types and methods.
    private bool _resolved, _broken;
    private IntPtr _screenContainerClass, _popupContainerClass, _modelClass, _panelClass, _nameEnumClass, _actionClass, _textClass;
    private IntPtr _trigger, _setArrayData, _getHighlightedIndex, _setHighlightedIndex, _notice;
    private IntPtr _getTransform, _getGameObject, _find, _getChildCount, _getChild, _getName, _setName, _setActive, _getActiveSelf;
    private IntPtr _instantiate, _getComponent, _getText, _setText, _textType, _goGetTransform, _getColor, _setColor, _setDisabledIf;
    private IntPtr _getFontSize, _setFontSize, _getAnchoredPosition, _setAnchoredPosition, _setAnchorMin, _setAnchorMax, _setOffsetMin, _setOffsetMax;
    private IntPtr _setSiblingIndex, _texture2DClass, _loadImage, _rawImageType, _setTexture, _setRawColor, _setSizeDelta, _addComponent, _setBestFit, _setHorizontalOverflow, _setVerticalOverflow;
    private IntPtr _gameObjectClass, _setParent, _getLayer, _setLayer, _setLocalScale, _setRaycastTarget, _nodeTransformType, _destroy;

    // Live state.
    private IntPtr _screenContainer, _popupContainer, _labelTemplate;
    private MenuState? _menu;

    private sealed class MenuState
    {
        public IntPtr Controller;
        public IntPtr[] Tabs = [];     // the game's four tab models, in MenuTab order
        public IntPtr[] Panels = [];   // the matching panel objects
        public readonly Dictionary<(MenuTab, int), IntPtr> Built = new();
        public int RefreshStage;       // 1 = tiles changed, 2 = switched away, 0 = settled
        public bool Refreshed;
        public int RefreshReturnTo;
    }

    public GameUi(GameRuntime runtime, MainThread mainThread)
    {
        _rt = runtime;
        _mainThread = mainThread;
        mainThread.Update += Tick;
    }

    // ---- IGameUi / IMainMenu ------------------------------------------------------------------------------

    public IMainMenu MainMenu => this;

    public bool MainMenuReady { get; private set; }

    public string? IdleScreen { get; private set; }

    public bool BackOut()
    {
        try
        {
            if (IdleScreen == null || IdleScreen == "MainMenu" || _screenContainerClass == IntPtr.Zero) return false;
            // The game goes back a screen when a screen request answers with its "back out" marker.
            var marker = _rt.GetStaticField<IntPtr>(_screenContainerClass, "backOut");
            var show = _rt.FindMethod(_screenContainerClass, "Show(System.Func<MVC.BaseController>)");
            if (marker == IntPtr.Zero || show == IntPtr.Zero) return false;
            _rt.Invoke(show, IntPtr.Zero, _rt.CreateFunc(_rt.GetParameterClass(show, 0), () => marker));
            IdleScreen = null;
            return true;
        }
        catch (Exception e)
        {
            Log.Warn("Could not go back a screen: " + e.Message);
            return false;
        }
    }

    private UiToolkit? _toolkit;

    private UiToolkit Toolkit => _toolkit ??= new UiToolkit(_rt, _mainThread);
    private Courses? _courses;

    public IUiScreen CreateScreen() => new UiScreen(_rt, Toolkit);

    public ICourseCatalog Courses => _courses ??= new Courses(_rt, Toolkit, () => MainMenuReady);

    public void SetTabs(params MenuTab[] tabs) => _tabs = tabs;

    public void SetTiles(MenuTab tab, params MenuTile[] tiles) => _tiles[tab] = tiles;

    public void RemoveGoOnline() => _removeGoOnline = true;

    public bool Open(GameTile tile)
    {
        if (_menu == null || !MainMenuReady) return false;
        var model = FindGameTile(GameTileNames[tile]);
        if (model == IntPtr.Zero) return false;
        foreach (var (key, entry) in _rt.ReadStringDictionary(Field(model, "looseData")))
        {
            if (key != OnClickKey || entry == IntPtr.Zero) continue;
            var actions = Field(entry, "data");
            if (actions == IntPtr.Zero || *(nuint*)(actions + 0x18) == 0) continue;
            // MVC.InteractiveAction: "action" (no arguments) 0x30 into the struct, "nodeAction" (one argument) at 0x38.
            var element = actions + 0x20;
            var plain = *(IntPtr*)(element + 0x30);
            var node = *(IntPtr*)(element + 0x38);
            if (plain != IntPtr.Zero) { _rt.Invoke(_rt.FindMethod(_rt.GetClass(plain), "Invoke", 0), plain); return true; }
            if (node != IntPtr.Zero) { _rt.Invoke(_rt.FindMethod(_rt.GetClass(node), "Invoke", 1), node, IntPtr.Zero); return true; }
        }
        return false;
    }

    public void SetBanner(MenuBanner banner) => _banner = banner;

    public void ReplaceText(string pattern, string replacement) => _textRules.Add((pattern, replacement));

    public void ReplacePopup(string gamePopupName, string? header, string? message) => _popupRules[gamePopupName] = (header, message);

    public void ShowNotice(string header, string message)
    {
        try
        {
            if (!Resolve() || _notice == IntPtr.Zero) return;
            // MVC.StringSource is a struct of three references: { task, localized, inspectorText }.
            var msg = stackalloc IntPtr[3];
            var head = stackalloc IntPtr[3];
            msg[0] = head[0] = IntPtr.Zero;
            msg[1] = msg[2] = _rt.NewString(message);
            head[1] = head[2] = _rt.NewString(header);
            byte force = 0;
            long noPriority = 0; // Nullable<int> with no value
            _rt.Invoke(_notice, IntPtr.Zero, (IntPtr)msg, (IntPtr)head, (IntPtr)(&force), (IntPtr)(&noPriority));
        }
        catch (Exception e)
        {
            Log.Error("ShowNotice failed: " + e.Message);
        }
    }

    public void ShowChoices(string header, string info, IReadOnlyList<MenuChoice> choices)
    {
        try
        {
            if (!Resolve()) return;
            if (_menu == null)
            {
                ShowNotice(header, info); // the choice rows are built with the main menu's tile builder
                return;
            }
            var rows = new IntPtr[choices.Count];
            for (var i = 0; i < rows.Length; i++)
            {
                var choice = choices[i];
                var nameValue = TextTileName;
                byte lockWhenOffline = 0;
                rows[i] = _rt.Invoke(_trigger, _menu.Panels[1], _rt.Box(_nameEnumClass, &nameValue),
                    _rt.CreateDelegate(_actionClass, () => { ClosePopupNamed(header); choice.OnSelect(); }), (IntPtr)(&lockWhenOffline));
                Relabel(rows[i], choice.Label, choice.Description);
            }
            var title = stackalloc IntPtr[3];
            var body = stackalloc IntPtr[3];
            title[0] = body[0] = IntPtr.Zero;
            title[1] = title[2] = _rt.NewString(header);
            body[1] = body[2] = _rt.NewString(info);
            _rt.Invoke(_detailedChoice, IntPtr.Zero, (IntPtr)title, (IntPtr)body, _rt.NewArray(_modelClass, rows));
        }
        catch (Exception e)
        {
            Log.Error("ShowChoices failed: " + e.Message);
        }
    }

    /// <summary>Closes the visible popup if it is the one with this header (the game may already have closed it).</summary>
    private void ClosePopupNamed(string header)
    {
        if (!Live(ref _popupContainer, _popupContainerClass)) return;
        var popup = Field(_popupContainer, "topPopup");
        if (popup == IntPtr.Zero) return;
        var instance = HasField(popup, "instance") ? Field(popup, "instance") : IntPtr.Zero;
        var model = instance == IntPtr.Zero ? IntPtr.Zero : Field(instance, "model");
        if (model == IntPtr.Zero || InspectorName(model) != header) return;
        var controller = Field(popup, "controller");
        var menu = controller != IntPtr.Zero && HasField(controller, "menu") ? Field(controller, "menu") : IntPtr.Zero;
        var cancel = menu == IntPtr.Zero ? IntPtr.Zero : Field(menu, "onCancel");
        if (cancel != IntPtr.Zero) _rt.Invoke(_rt.FindMethod(_rt.GetClass(cancel), "Invoke", 0), cancel);
    }

    /// <summary>Sets a freshly built tile's name and description before it is ever shown.</summary>
    private void Relabel(IntPtr model, string? label, string? description)
    {
        foreach (var (key, entry) in _rt.ReadStringDictionary(Field(model, "looseData")))
        {
            var text = key == NameKey ? label : key == DescriptionKey ? description : null;
            if (text == null || entry == IntPtr.Zero) continue;
            var source = Field(entry, "data");
            if (source == IntPtr.Zero) continue;
            var value = _rt.NewString(text);
            _rt.WriteReference(source, source + (nint)_rt.FieldOffset(_rt.GetClass(source), "localized"), value);
            _rt.WriteReference(source, source + (nint)_rt.FieldOffset(_rt.GetClass(source), "inspectorText"), value);
        }
    }

    // ---- per-frame work ------------------------------------------------------------------------------------

    private void Tick()
    {
        if (_broken) return;
        var frame = _mainThread.Frame;
        try
        {
            if (!Resolve()) return;
            if (_popupRules.Count > 0) ApplyPopupRules();
            if (frame % 10 == 0) ApplyMenuRules();
            if (_textRules.Count > 0 && frame % 30 == 0) ApplyTextRules();
        }
        catch (Exception e)
        {
            _broken = true;
            Log.Error("Custom UI disabled after an error: " + e);
        }
    }

    private void ApplyPopupRules()
    {
        // The game's "offline mode" follow-up alert is only shown while this flag is clear.
        if (_popupRules.TryGetValue("OfflineModeBootAlert", out var bootAlert) && bootAlert.Message == null)
        {
            if (_mainMenuClass == IntPtr.Zero) _mainMenuClass = _rt.FindClass("HB.Golf", "MVC", "MainMenu");
            if (_mainMenuClass != IntPtr.Zero) _rt.SetStaticField<byte>(_mainMenuClass, "offlineModeInfoPopupShown", 1);
        }

        if (!Live(ref _popupContainer, _popupContainerClass)) return;
        // Waiting popups are rewritten before they are ever drawn; the visible one is checked as well.
        var queue = Field(_popupContainer, "popupQueue");
        if (queue != IntPtr.Zero)
            foreach (var waiting in _rt.ReadArray(Field(queue, "_array")))
                if (waiting != IntPtr.Zero) ApplyPopupRule(waiting);
        var top = Field(_popupContainer, "topPopup");
        if (top != IntPtr.Zero) ApplyPopupRule(top);
    }

    private void ApplyPopupRule(IntPtr popup)
    {
        // PopupScreen { controller, instance }: the popup's identifying name is on the screen instance's model.
        var instance = HasField(popup, "instance") ? Field(popup, "instance") : IntPtr.Zero;
        var model = instance == IntPtr.Zero ? IntPtr.Zero : Field(instance, "model");
        var name = model == IntPtr.Zero ? null : InspectorName(model);
        if (name == null || !_popupRules.TryGetValue(name, out var rule)) return;

        if (rule.Message == null)
        {
            // No replacement text: close it through its own cancel handler.
            var controller = Field(popup, "controller");
            var menu = controller != IntPtr.Zero && HasField(controller, "menu") ? Field(controller, "menu") : IntPtr.Zero;
            var cancel = menu == IntPtr.Zero ? IntPtr.Zero : Field(menu, "onCancel");
            if (cancel != IntPtr.Zero && _handledPopups.Add(popup)) _rt.Invoke(_rt.FindMethod(_rt.GetClass(cancel), "Invoke", 0), cancel);
            return;
        }

        // Rewrite the header and body where the popup keeps them, then fix any text object already on screen.
        if (!HasField(model, "_models")) return;
        var parts = Models(model);
        if (parts.Length == 0 || parts[0] == IntPtr.Zero) return;
        string? oldHeader = null, oldMessage = null;
        foreach (var (key, entry) in _rt.ReadStringDictionary(Field(parts[0], "looseData")))
        {
            var replacement = key == NameKey ? rule.Header : key == "MVC.Bindings+Value" ? rule.Message : null;
            if (replacement == null || entry == IntPtr.Zero) continue;
            var source = Field(entry, "data");
            if (source == IntPtr.Zero) continue;
            var current = _rt.ReadString(Field(source, "localized"));
            if (current == replacement) continue;
            if (key == NameKey) oldHeader = current; else oldMessage = current;
            var value = _rt.NewString(replacement);
            _rt.WriteReference(source, source + (nint)_rt.FieldOffset(_rt.GetClass(source), "localized"), value);
            _rt.WriteReference(source, source + (nint)_rt.FieldOffset(_rt.GetClass(source), "inspectorText"), value);
        }

        var root = Field(instance, "root");
        if (root == IntPtr.Zero || !_rt.IsAlive(root)) return;
        byte includeInactive = 1;
        foreach (var text in _rt.ReadArray(_rt.Invoke(_getComponentsInChildren, root, _textType, (IntPtr)(&includeInactive))))
        {
            if (text == IntPtr.Zero) continue;
            var shown = _rt.ReadString(_rt.Invoke(_getText, text));
            if (shown == null) continue;
            // Compare against both the texts just replaced and the game's known wording for a view built earlier.
            if (rule.Header != null && shown != rule.Header && (shown == oldHeader || shown == "ALERT"))
                _rt.Invoke(_setText, text, _rt.NewString(rule.Header));
            else if (shown != rule.Message && (shown == oldMessage || shown.StartsWith("You have lost your connection", StringComparison.Ordinal)))
                _rt.Invoke(_setText, text, _rt.NewString(rule.Message));
        }
    }

    private void ApplyTextRules()
    {
        foreach (var text in _rt.FindObjectsOfType(_textClass))
        {
            var current = _rt.ReadString(_rt.Invoke(_getText, text));
            if (string.IsNullOrEmpty(current)) continue;
            foreach (var (pattern, replacement) in _textRules)
            {
                if (!Matches(pattern, current)) continue;
                Step("replacing text " + current);
                _rt.Invoke(_setText, text, _rt.NewString(replacement));
                break;
            }
        }
    }

    /// <summary>Wildcard match where '*' stands for any run of characters.</summary>
    private static bool Matches(string pattern, string text)
    {
        var parts = pattern.Split('*');
        if (parts.Length == 1) return text == pattern;
        if (!text.StartsWith(parts[0], StringComparison.Ordinal)) return false;
        var position = parts[0].Length;
        for (var i = 1; i < parts.Length - 1; i++)
        {
            var at = text.IndexOf(parts[i], position, StringComparison.Ordinal);
            if (at < 0) return false;
            position = at + parts[i].Length;
        }
        return text.Length - position >= parts[^1].Length && text.EndsWith(parts[^1], StringComparison.Ordinal);
    }

    private void ApplyMenuRules()
    {
        MainMenuReady = false;
        IdleScreen = null;
        if (!Live(ref _screenContainer, _screenContainerClass)) return;
        var screen = Field(_screenContainer, "currentScreen");
        if (screen == IntPtr.Zero) return;
        var screenName = _rt.ReadString(Field(screen, "name"));
        if (_rt.GetField<byte>(screen, _rt.GetClass(screen), "<isBuilt>k__BackingField") != 0 &&
            _rt.GetField<byte>(screen, _rt.GetClass(screen), "<isShown>k__BackingField") != 0 &&
            (!Live(ref _popupContainer, _popupContainerClass) || Field(_popupContainer, "topPopup") == IntPtr.Zero))
            IdleScreen = screenName;
        if (screenName != "MainMenu") return;
        if (_rt.GetField<byte>(screen, _rt.GetClass(screen), "<isBuilt>k__BackingField") == 0) return;
        MainMenuReady = _menu != null && _rt.GetField<byte>(screen, _rt.GetClass(screen), "<isShown>k__BackingField") != 0 &&
                        (!Live(ref _popupContainer, _popupContainerClass) || Field(_popupContainer, "topPopup") == IntPtr.Zero);
        if (_tabs == null && _tiles.Count == 0 && !_removeGoOnline && _banner == null) return;
        var controller = Field(screen, "controller");
        if (controller == IntPtr.Zero) return;
        var panelsModel = Field(controller, "panels");
        if (panelsModel == IntPtr.Zero) return;
        var currentTabs = Models(panelsModel);

        if (_menu == null || _menu.Controller != controller)
        {
            // A freshly built menu: remember the game's own tabs, panels and tiles before changing anything.
            var panels = _rt.ReadArray(Field(controller, "allPanels"));
            if (currentTabs.Length != 4 || panels.Length != 4) return;
            _menu = new MenuState { Controller = controller, Tabs = currentTabs, Panels = panels };
        }

        foreach (var (tab, tiles) in _tiles)
        {
            var tabModel = _menu.Tabs[(int)tab];
            var wanted = new List<IntPtr>();
            for (var i = 0; i < tiles.Length; i++)
            {
                var model = ModelFor(tab, i, tiles[i]);
                if (model == IntPtr.Zero) continue;
                wanted.Add(model);
                if (tiles[i].Label != null) _labelOverrides[model] = tiles[i].Label!;
            }
            if (!Models(tabModel).SequenceEqual(wanted))
            {
                Step("setting tiles of " + tab);
                if (++_tileResets <= 20) Log.Detail($"[ui] tiles of {tab} re-applied (#{_tileResets})");
                // One view refresh per freshly built menu is enough; never restart it while it is in progress.
                if (!_menu.Refreshed && _menu.RefreshStage == 0) _menu.RefreshStage = 1;
                _rt.Invoke(_setArrayData, tabModel, _rt.NewArray(_modelClass, wanted.ToArray()));
                Step("tiles set for " + tab);
            }
        }

        if (_tabs != null)
        {
            var wantedTabs = _tabs.Select(t => _menu.Tabs[(int)t]).ToArray();
            if (!currentTabs.SequenceEqual(wantedTabs))
            {
                Step("setting tabs");
                var wantedPanels = _rt.NewArray(_panelClass, _tabs.Select(t => _menu.Panels[(int)t]).ToArray());
                _rt.WriteReference(controller, controller + (nint)_rt.FieldOffset(_rt.GetClass(controller), "allPanels"), wantedPanels);
                _rt.Invoke(_setArrayData, panelsModel, _rt.NewArray(_modelClass, wantedTabs));
                currentTabs = wantedTabs;
                Step("tabs set");
            }
        }

        var rootComponent = Field(screen, "root");
        if (rootComponent == IntPtr.Zero || !_rt.IsAlive(rootComponent)) return;
        var screenRoot = Find(Transform(rootComponent), "Screen Root");
        if (screenRoot == IntPtr.Zero) return;

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
        if (_menu.RefreshStage == 1 && currentTabs.Length > 1)
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
        var visibleTab = (MenuTab)Array.IndexOf(_menu.Tabs, currentTabs[highlighted]);
        if (!_tiles.ContainsKey(visibleTab)) return;
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

    private IntPtr ModelFor(MenuTab tab, int index, MenuTile tile)
    {
        if (tile.Existing is { } existing)
            return FindGameTile(GameTileNames[existing]);
        if (_menu!.Built.TryGetValue((tab, index), out var built)) return built;

        Step($"building tile {tab} {index}");
        var nameValue = tile.Label == null ? CareerTileName : TextTileName;
        var onClick = tile.OnClick ?? (() => { });
        byte lockWhenOffline = 0;
        var model = _rt.Invoke(_trigger, _menu.Panels[1], _rt.Box(_nameEnumClass, &nameValue),
            _rt.CreateDelegate(_actionClass, () => onClick()), (IntPtr)(&lockWhenOffline));

        Relabel(model, tile.Label, tile.Description);
        Native.gchandle_new(model, 0); // ours to keep alive: the game may drop the tab array that references it
        return _menu.Built[(tab, index)] = model;
    }

    /// <summary>
    /// Looks one of the game's own tiles up by name in the tabs as they are right now. The game rebuilds a tab's
    /// tiles at times and frees the old ones, so these are never remembered between frames.
    /// </summary>
    private IntPtr FindGameTile(string inspectorName)
    {
        foreach (var tab in new[] { 1, 0, 3, 2 })
            foreach (var tile in Models(_menu!.Tabs[tab]))
                if (tile != IntPtr.Zero && InspectorName(tile) == inspectorName) return tile;
        return IntPtr.Zero;
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

    private readonly HashSet<string> _steps = new();

    /// <summary>Writes a breadcrumb the first time each step runs, so a crash can be traced to it.</summary>
    private void Step(string what) { if (_steps.Add(what)) Log.Detail("[ui] " + what); }

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
        if (_bannerLogoFailed) return false;
        if (_bannerLogo == IntPtr.Zero)
        {
            try
            {
                var bytes = System.IO.File.ReadAllBytes(path);
                int width = 2, height = 2;
                var texture = _rt.NewObject(_texture2DClass, ".ctor(System.Int32,System.Int32)", (IntPtr)(&width), (IntPtr)(&height));
                var loaded = _rt.Invoke(_loadImage, IntPtr.Zero, texture, _rt.NewByteArray(bytes));
                if (*(byte*)(loaded + 16) == 0) throw new InvalidOperationException("the image could not be decoded");
                Native.gchandle_new(texture, 0);
                _bannerLogo = texture;
                // PNG width and height sit in the header at fixed offsets.
                _bannerLogoAspect = bytes.Length > 24
                    ? (float)((bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19]) / Math.Max(1, (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23])
                    : 10f;
            }
            catch (Exception e)
            {
                _bannerLogoFailed = true;
                Log.Warn("Banner logo not loaded: " + e.Message);
                return false;
            }
        }
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

    // ---- small helpers ---------------------------------------------------------------------------------------

    private bool Live(ref IntPtr cached, IntPtr klass)
    {
        if (_rt.IsAlive(cached)) return true;
        var found = _rt.FindObjectsOfType(klass);
        cached = found.Length > 0 ? found[0] : IntPtr.Zero;
        return cached != IntPtr.Zero;
    }

    private IntPtr Field(IntPtr obj, string name) => _rt.GetField<IntPtr>(obj, _rt.GetClass(obj), name);
    private bool HasField(IntPtr obj, string name) => GameRuntime.TryField(_rt.GetClass(obj), name) != IntPtr.Zero;
    private string? InspectorName(IntPtr model) => _rt.ReadString(Field(model, "<inspectorName>k__BackingField"));
    private IntPtr[] Models(IntPtr arrayModel) => _rt.ReadArray(Field(arrayModel, "_models"));

    private string? NameText(IntPtr model)
    {
        foreach (var (key, entry) in _rt.ReadStringDictionary(Field(model, "looseData")))
        {
            if (key != NameKey || entry == IntPtr.Zero) continue;
            var source = Field(entry, "data");
            return source == IntPtr.Zero ? null : _rt.ReadString(Field(source, "localized"));
        }
        return null;
    }

    private static int Int(IntPtr boxed) => *(int*)(boxed + 16);
    private IntPtr Transform(IntPtr component) => _rt.Invoke(_getTransform, component);
    private IntPtr GameObject(IntPtr component) => _rt.Invoke(_getGameObject, component);
    private IntPtr Find(IntPtr transform, string path) => _rt.Invoke(_find, transform, _rt.NewString(path));
    private int ChildCount(IntPtr transform) => Int(_rt.Invoke(_getChildCount, transform));
    private IntPtr GetChild(IntPtr transform, int index) => _rt.Invoke(_getChild, transform, (IntPtr)(&index));
    private string? Name(IntPtr unityObject) => _rt.ReadString(_rt.Invoke(_getName, unityObject));
    private bool IsActive(IntPtr gameObject) => *(byte*)(_rt.Invoke(_getActiveSelf, gameObject) + 16) != 0;

    private void SetActive(IntPtr gameObject, bool active)
    {
        byte value = active ? (byte)1 : (byte)0;
        _rt.Invoke(_setActive, gameObject, (IntPtr)(&value));
    }

    private bool Resolve()
    {
        if (_resolved) return true;
        if (_broken) return false;
        const string core = "UnityEngine.CoreModule", unity = "UnityEngine";
        _screenContainerClass = _rt.FindClass("HB.Golf", "MVC", "ScreenContainer");
        _popupContainerClass = _rt.FindClass("HB.Golf", "MVC", "PopupContainer");
        _modelClass = _rt.FindClass("HB.Golf", "MVC", "Model");
        _panelClass = _rt.FindClass("HB.Golf", "MVC", "MainMenuPanel");
        _nameEnumClass = _rt.FindClass("HB.Golf", "", "Localized.MainMenuPlayNow");
        _actionClass = _rt.FindClass("mscorlib", "System", "Action");
        _textClass = _rt.FindClass("HB.Golf", "MVC", "MVCText");
        var modelArray = _rt.FindClass("HB.Golf", "MVC", "ModelArray");
        var component = _rt.FindClass(core, unity, "Component");
        var gameObject = _rt.FindClass(core, unity, "GameObject");
        var transform = _rt.FindClass(core, unity, "Transform");
        var unityObject = _rt.FindClass(core, unity, "Object");
        if (new[] { _screenContainerClass, _popupContainerClass, _modelClass, _panelClass, _nameEnumClass, _textClass, modelArray, component, gameObject, transform, unityObject }.Contains(IntPtr.Zero))
            return Fail("menu types");

        _trigger = _rt.FindMethod(_panelClass, "Trigger(System.Enum,System.Action,System.Boolean)");
        _setArrayData = _rt.FindMethod(modelArray, "SetArrayData", 1);
        _getHighlightedIndex = _rt.FindMethod(modelArray, "get_highlightedIndex", 0);
        _setHighlightedIndex = _rt.FindMethod(modelArray, "SetHighlightedIndex", 1);
        _notice = _rt.FindMethod(_rt.FindClass("HB.Golf", "MVC", "PopupMenu"),
            "Notice(MVC.StringSource,MVC.StringSource,System.Boolean,System.Nullable<System.Int32>)");
        _getTransform = _rt.FindMethod(component, "get_transform", 0);
        _getGameObject = _rt.FindMethod(component, "get_gameObject", 0);
        _find = _rt.FindMethod(transform, "Find", 1);
        _getChildCount = _rt.FindMethod(transform, "get_childCount", 0);
        _getChild = _rt.FindMethod(transform, "GetChild", 1);
        _getName = _rt.FindMethod(unityObject, "get_name", 0);
        _setName = _rt.FindMethod(unityObject, "set_name", 1);
        _setActive = _rt.FindMethod(gameObject, "SetActive", 1);
        _getActiveSelf = _rt.FindMethod(gameObject, "get_activeSelf", 0);
        _goGetTransform = _rt.FindMethod(gameObject, "get_transform", 0);
        _instantiate = _rt.FindMethod(unityObject, "Instantiate(UnityEngine.Object,UnityEngine.Transform)");
        _getComponent = _rt.FindMethod(gameObject, "GetComponent(System.Type)");
        _getText = _rt.FindMethod(_textClass, "get_text", 0);
        _setText = _rt.FindMethod(_textClass, "set_text", 1);
        _getColor = _rt.FindMethod(_textClass, "get_color", 0);
        _setColor = _rt.FindMethod(_textClass, "set_color", 1);
        _setDisabledIf = _rt.FindMethod(_modelClass, "SetDisabledIf", 1);
        _getComponentsInChildren = _rt.FindMethod(component, "GetComponentsInChildren(System.Type,System.Boolean)");
        _detailedChoice = _rt.FindMethod(_rt.FindClass("HB.Golf", "MVC", "PopupMenu"), "DetailedChoice(MVC.StringSource,MVC.StringSource,MVC.Model[])");
        var rectTransform = _rt.FindClass(core, unity, "RectTransform");
        var rawImage = _rt.FindClass("UnityEngine.UI", "UnityEngine.UI", "RawImage");
        _texture2DClass = _rt.FindClass(core, unity, "Texture2D");
        _getFontSize = _rt.FindMethod(_textClass, "get_fontSize", 0);
        _setFontSize = _rt.FindMethod(_textClass, "set_fontSize", 1);
        _getAnchoredPosition = _rt.FindMethod(rectTransform, "get_anchoredPosition", 0);
        _setAnchoredPosition = _rt.FindMethod(rectTransform, "set_anchoredPosition", 1);
        _setAnchorMin = _rt.FindMethod(rectTransform, "set_anchorMin", 1);
        _setAnchorMax = _rt.FindMethod(rectTransform, "set_anchorMax", 1);
        _setOffsetMin = _rt.FindMethod(rectTransform, "set_offsetMin", 1);
        _setOffsetMax = _rt.FindMethod(rectTransform, "set_offsetMax", 1);
        _setSiblingIndex = _rt.FindMethod(transform, "SetSiblingIndex", 1);
        _setSizeDelta = _rt.FindMethod(rectTransform, "set_sizeDelta", 1);
        _gameObjectClass = gameObject;
        _setParent = _rt.FindMethod(transform, "SetParent(UnityEngine.Transform,System.Boolean)");
        _getLayer = _rt.FindMethod(gameObject, "get_layer", 0);
        _setLayer = _rt.FindMethod(gameObject, "set_layer", 1);
        _setLocalScale = _rt.FindMethod(transform, "set_localScale", 1);
        _setRaycastTarget = rawImage == IntPtr.Zero ? IntPtr.Zero : _rt.FindMethod(rawImage, "set_raycastTarget", 1);
        _destroy = _rt.FindMethod(unityObject, "Destroy(UnityEngine.Object)");
        var nodeTransform = _rt.FindClass("HB.Golf", "MVC", "NodeTransform");
        _nodeTransformType = nodeTransform == IntPtr.Zero ? IntPtr.Zero : Native.type_get_object(Native.class_get_type(nodeTransform));
        if (new[] { _setParent, _getLayer, _setLayer, _setLocalScale, _setRaycastTarget, _destroy, _nodeTransformType }.Contains(IntPtr.Zero))
            Log.Warn("Some object functions were not found; the custom banner may be incomplete.");
        _setBestFit = _rt.FindMethod(_textClass, "set_resizeTextForBestFit", 1);
        _setHorizontalOverflow = _rt.FindMethod(_textClass, "set_horizontalOverflow", 1);
        _setVerticalOverflow = _rt.FindMethod(_textClass, "set_verticalOverflow", 1);
        _addComponent = _rt.FindMethod(gameObject, "AddComponent(System.Type)");
        _loadImage = _rt.FindMethod(_rt.FindClass("UnityEngine.ImageConversionModule", unity, "ImageConversion"), "LoadImage(UnityEngine.Texture2D,System.Byte[])");
        _setTexture = rawImage == IntPtr.Zero ? IntPtr.Zero : _rt.FindMethod(rawImage, "set_texture", 1);
        _setRawColor = rawImage == IntPtr.Zero ? IntPtr.Zero : _rt.FindMethod(rawImage, "set_color", 1);
        _rawImageType = rawImage == IntPtr.Zero ? IntPtr.Zero : Native.type_get_object(Native.class_get_type(rawImage));
        if (new[] { rectTransform, rawImage, _texture2DClass, _getFontSize, _setFontSize, _getAnchoredPosition, _setAnchoredPosition, _setAnchorMin,
                    _setAnchorMax, _setOffsetMin, _setOffsetMax, _setSiblingIndex, _loadImage, _setTexture, _setRawColor, _setSizeDelta, _addComponent, _setBestFit, _setHorizontalOverflow, _setVerticalOverflow }.Contains(IntPtr.Zero))
            Log.Warn("Some banner functions were not found; the custom banner may be incomplete.");
        _textType = Native.type_get_object(Native.class_get_type(_textClass));
        if (new[] { _trigger, _setArrayData, _getHighlightedIndex, _setHighlightedIndex, _notice, _getTransform, _getGameObject, _find, _getChildCount, _getChild,
                    _getName, _setName, _setActive, _getActiveSelf, _goGetTransform, _getColor, _setColor, _setDisabledIf, _getComponentsInChildren, _detailedChoice, _instantiate, _getComponent, _getText, _setText }.Contains(IntPtr.Zero))
            return Fail("menu methods");
        return _resolved = true;
    }

    private bool Fail(string what)
    {
        _broken = true;
        Log.Warn($"Custom UI is disabled: {what} not found (game update?).");
        return false;
    }
}
