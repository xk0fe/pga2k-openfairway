using System;
using System.Collections.Generic;
using System.Linq;
using OpenFairway.API;
using OpenFairway.Core.Il2Cpp;

namespace OpenFairway.Core.Games;

/// <summary>
/// Game UI services for mods, built on the game's own menu system (the MVC system that PGA TOUR 2K23 and 2K25
/// share) so everything looks native: tiles come from the game's tile builder, popups from the game's popup
/// function, and labels are the game's own text objects. The game rebuilds its menu often, so every change here
/// is a rule that is re-applied, not a one-off edit. What differs between the games comes from the game's
/// <see cref="MenuLayout"/>: GameUi.Tiles.cs draws 2K25's tile grid and banner, GameUi.Rows.cs 2K23's tab rows.
/// </summary>
internal sealed unsafe partial class GameUi : IGameUi, IMainMenu
{
    private const string NameKey = "MVC.Bindings+Name", DescriptionKey = "MVC.Bindings+SubName", OnClickKey = "MVC.Bindings+OnClick";
    private const string InspectorNameField = "<inspectorName>k__BackingField";

    /// <summary>The internal name our own tiles get, so they are never mistaken for the game's tile they were built as.</summary>
    private const string OurTilePrefix = "OpenFairway ";

    private readonly GameRuntime _rt;
    private readonly MainThread _mainThread;
    private readonly GameProfile _profile;
    private readonly MenuLayout _layout;

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
    private IntPtr _getTexture, _setUvRect, _getRect, _setWrapMode;

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

    public GameUi(GameRuntime runtime, MainThread mainThread, GameProfile profile)
    {
        _rt = runtime;
        _mainThread = mainThread;
        _profile = profile;
        _layout = profile.Menu;
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
    private bool _coursesAsked;

    private UiToolkit Toolkit => _toolkit ??= new UiToolkit(_rt, _mainThread);
    private Courses? _courses;

    public IUiScreen CreateScreen() => new UiScreen(_rt, Toolkit);

    public ICourseCatalog Courses => _courses ??= new Courses(_rt, Toolkit, _profile, _mainThread, () => MainMenuReady);

    public void SetTabs(params MenuTab[] tabs) => _tabs = tabs;

    public void SetTiles(MenuTab tab, params MenuTile[] tiles) => _tiles[tab] = tiles;

    public void RemoveGoOnline() => _removeGoOnline = true;

    public bool Open(GameTile tile)
    {
        if (_menu == null || !MainMenuReady || !_layout.GameTiles.TryGetValue(tile, out var name)) return false;
        var model = FindGameTile(name);
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
                rows[i] = BuildTile(_layout.TextTileName, () => { ClosePopupNamed(header); choice.OnSelect(); });
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
            if (!_coursesAsked && IdleScreen == "MainMenu")
            {
                // Where the game hands its course list over later (2K23), have it ready before a mod asks.
                _coursesAsked = true;
                ((Courses)Courses).Preload();
            }
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
            if (currentTabs.Length != _layout.TabCount || panels.Length != _layout.TabCount) return;
            _menu = new MenuState { Controller = controller, Tabs = currentTabs, Panels = panels };
        }

        foreach (var (tab, tiles) in _tiles)
        {
            if (!_layout.Tabs.TryGetValue(tab, out var tabIndex)) continue;
            var tabModel = _menu.Tabs[tabIndex];
            var wanted = new List<IntPtr>();
            for (var i = 0; i < tiles.Length; i++)
            {
                var model = ModelFor(tab, i, tiles[i]);
                if (model == IntPtr.Zero) continue;
                wanted.Add(model);
                if (tiles[i].Label is not { } label) continue;
                _labelOverrides[model] = label;
                // A row shows its model's own name, so a game row given a new name is renamed in its model.
                if (_layout.Style == MenuStyle.Rows && tiles[i].Existing != null && NameText(model) != label) Relabel(model, label, tiles[i].Description);
            }
            if (!Models(tabModel).SequenceEqual(wanted))
            {
                Step("setting tiles of " + tab);
                if (++_tileResets <= 20) Log.Detail($"[ui] tiles of {tab} re-applied (#{_tileResets})");
                // One view refresh per freshly built menu is enough; never restart it while it is in progress.
                if (_layout.Style == MenuStyle.Tiles && !_menu.Refreshed && _menu.RefreshStage == 0) _menu.RefreshStage = 1;
                _rt.Invoke(_setArrayData, tabModel, _rt.NewArray(_modelClass, wanted.ToArray()));
                Step("tiles set for " + tab);
            }
        }
        if (_removeGoOnline && _layout.GoOnlineRows.Length > 0) RemoveGoOnlineRows();

        if (_tabs != null)
        {
            var wantedIndices = _tabs.Where(_layout.Tabs.ContainsKey).Select(t => _layout.Tabs[t]).ToArray();
            var wantedTabs = wantedIndices.Select(i => _menu.Tabs[i]).ToArray();
            if (!currentTabs.SequenceEqual(wantedTabs))
            {
                Step("setting tabs");
                var wantedPanels = _rt.NewArray(_panelClass, wantedIndices.Select(i => _menu.Panels[i]).ToArray());
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
        if (_layout.Style == MenuStyle.Tiles) ApplyTileView(screen, screenRoot, panelsModel, currentTabs);
        else ApplyRowView(screenRoot, panelsModel, currentTabs);
    }

    /// <summary>The API's tab for one of the game's own tab models, or null for a tab the API does not name.</summary>
    private MenuTab? TabOf(IntPtr tabModel)
    {
        var index = Array.IndexOf(_menu!.Tabs, tabModel);
        foreach (var (tab, at) in _layout.Tabs)
            if (at == index) return tab;
        return null;
    }

    /// <summary>Takes the rows that only offer to go online out of every tab a mod has not laid out itself.</summary>
    private void RemoveGoOnlineRows()
    {
        foreach (var tabModel in _menu!.Tabs)
        {
            if (TabOf(tabModel) is { } tab && _tiles.ContainsKey(tab)) continue;
            var rows = Models(tabModel);
            var kept = rows.Where(r => r == IntPtr.Zero || Array.IndexOf(_layout.GoOnlineRows, InspectorName(r)) < 0).ToArray();
            if (kept.Length == rows.Length) continue;
            Step("removing go-online rows");
            _rt.Invoke(_setArrayData, tabModel, _rt.NewArray(_modelClass, kept));
        }
    }

    private IntPtr ModelFor(MenuTab tab, int index, MenuTile tile)
    {
        if (tile.Existing is { } existing)
            return _layout.GameTiles.TryGetValue(existing, out var name) ? FindGameTile(name) : IntPtr.Zero;
        if (_menu!.Built.TryGetValue((tab, index), out var built)) return built;

        Step($"building tile {tab} {index}");
        var onClick = tile.OnClick ?? (() => { });
        var model = BuildTile(tile.Label == null ? _layout.CareerTileName : _layout.TextTileName, () => onClick());
        Relabel(model, tile.Label, tile.Description);
        Native.gchandle_new(model, 0); // ours to keep alive: the game may drop the tab array that references it
        return _menu.Built[(tab, index)] = model;
    }

    /// <summary>A new tile from the game's own tile builder, named after one of its Localized.MainMenuPlayNow values.</summary>
    private IntPtr BuildTile(int nameValue, Action onClick)
    {
        var panel = _menu!.Panels[_layout.BuilderPanel];
        var name = _rt.Box(_nameEnumClass, &nameValue);
        var action = _rt.CreateDelegate(_actionClass, onClick);
        byte lockWhenOffline = 0;
        var model = _layout.BuilderTakesOfflineLock
            ? _rt.Invoke(_trigger, panel, name, action, (IntPtr)(&lockWhenOffline))
            : _rt.Invoke(_trigger, panel, name, action);
        _rt.SetReference(model, InspectorNameField, _rt.NewString(OurTilePrefix + InspectorName(model)));
        return model;
    }

    /// <summary>
    /// Looks one of the game's own tiles up by name in the tabs as they are right now. The game rebuilds a tab's
    /// tiles at times and frees the old ones, so these are never remembered between frames.
    /// </summary>
    private IntPtr FindGameTile(string inspectorName)
    {
        // The builder panel's tab first: that is where the game keeps the tiles a mod is most likely to reuse.
        var builderTab = _menu!.Tabs[_layout.BuilderPanel];
        foreach (var tab in _menu.Tabs.Where(t => t == builderTab).Concat(_menu.Tabs.Where(t => t != builderTab)))
            foreach (var tile in Models(tab))
                if (tile != IntPtr.Zero && InspectorName(tile) == inspectorName) return tile;
        return IntPtr.Zero;
    }

    private readonly HashSet<string> _steps = new();

    /// <summary>Writes a breadcrumb the first time each step runs, so a crash can be traced to it.</summary>
    private void Step(string what) { if (_steps.Add(what)) Log.Detail("[ui] " + what); }

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

        _trigger = _rt.FindMethod(_panelClass, _layout.BuilderTakesOfflineLock ? "Trigger(System.Enum,System.Action,System.Boolean)" : "Trigger(System.Enum,System.Action)");
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
        // For the 2K23 menu's title image (GameUi.Rows.cs); the rest of the menu works without them.
        _getTexture = rawImage == IntPtr.Zero ? IntPtr.Zero : _rt.FindMethod(rawImage, "get_texture", 0);
        _setUvRect = rawImage == IntPtr.Zero ? IntPtr.Zero : _rt.FindMethod(rawImage, "set_uvRect", 1);
        _getRect = _rt.FindMethod(rectTransform, "get_rect", 0);
        _setWrapMode = _rt.FindMethod(_rt.FindClass(core, unity, "Texture"), "set_wrapMode", 1);
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
