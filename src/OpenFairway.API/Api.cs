using System;

namespace OpenFairway.API;

/// <summary>Implemented by every mod. One instance is created per mod assembly.</summary>
public interface IFairwayMod
{
    /// <summary>Stable identifier, e.g. "yourname.customcareer".</summary>
    string Id { get; }

    /// <summary>Called once on the platform thread after the game's runtime is up and the offline guard has passed.</summary>
    void OnLoad(IModContext context);
}

/// <summary>Everything the platform gives a mod, passed to <see cref="IFairwayMod.OnLoad"/>.</summary>
public interface IModContext
{
    ILog Log { get; }
    IGameRuntime Runtime { get; }

    /// <summary>Scheduling on the game's main thread. Anything that touches game objects should run there.</summary>
    IMainThread MainThread { get; }

    /// <summary>Menus and popups in the game's own style.</summary>
    IGameUi Ui { get; }

    /// <summary>Installed mods and their on/off switches.</summary>
    IModManager Mods { get; }

    /// <summary>Named actions that mods offer to each other (for example a menu mod opening another mod's feature).</summary>
    IActionRegistry Actions { get; }

    /// <summary>The folder this mod is installed in (its DLL, mod.json and assets).</summary>
    string ModDirectory { get; }

    /// <summary>Version of the OpenFairway platform, e.g. "0.4.0".</summary>
    string PlatformVersion { get; }

    /// <summary>Per-mod writable folder (config, saves). Created on demand.</summary>
    string DataDirectory { get; }
}

/// <summary>Native-looking UI built on the game's own menu system. Everything here is a rule that the platform
/// keeps applied, because the game rebuilds its menus often.</summary>
public interface IGameUi
{
    /// <summary>Shows one of the game's own notice popups with your text.</summary>
    void ShowNotice(string header, string message);

    /// <summary>Replaces a piece of on-screen text. '*' in the pattern matches any run of characters.</summary>
    void ReplaceText(string pattern, string replacement);

    /// <summary>Shows one of the game's own choice popups: a header, a line of text and a list of rows to pick from.
    /// Available while the main menu is up.</summary>
    void ShowChoices(string header, string info, System.Collections.Generic.IReadOnlyList<MenuChoice> choices);

    /// <summary>Rewrites one of the game's popups (by its internal name) with your header and message. With a null
    /// message the popup is suppressed instead.</summary>
    void ReplacePopup(string gamePopupName, string? header, string? message);

    IMainMenu MainMenu { get; }

    /// <summary>True while the main menu is the screen in front and no popup is open: a good moment to show your own.</summary>
    bool MainMenuReady { get; }

    /// <summary>Name of the game screen in front (for example "MainMenu" or "LocalQuickplay") while it is fully
    /// shown with no popup over it; null during play, transitions and popups.</summary>
    string? IdleScreen { get; }

    /// <summary>Goes back one game screen, as the Back button would. False if there is nothing to go back from.</summary>
    bool BackOut();

    /// <summary>Creates a full custom screen that you draw yourself, in the game's fonts. Open it from the main menu.</summary>
    IUiScreen CreateScreen();

    /// <summary>The courses that ship with the game.</summary>
    ICourseCatalog Courses { get; }
}

/// <summary>A full-screen custom screen. Set <see cref="Render"/>, then <see cref="Open"/>; call
/// <see cref="Invalidate"/> whenever what it shows has changed.</summary>
public interface IUiScreen
{
    /// <summary>Draws the whole screen. Called when it opens, after Invalidate, and when the window size changes.</summary>
    Action<IUiCanvas>? Render { get; set; }

    /// <summary>Runs when the player presses Escape.</summary>
    Action? OnBack { get; set; }

    /// <summary>Hide the game's own interface while this screen is open, so its 3D scene shows through any part of
    /// the screen you leave undrawn. Set before <see cref="Open"/>.</summary>
    bool HideGameUi { get; set; }

    bool IsOpen { get; }
    void Open();
    void Close();

    /// <summary>Redraws the screen. With <paramref name="animate"/>, groups play their entrance again (use it when
    /// the player moves to a different page, not for small updates).</summary>
    void Invalidate(bool animate = false);
}

/// <summary>Where text sits inside a label's box.</summary>
public enum UiAlign { TopLeft = 0, TopCenter = 1, TopRight = 2, Left = 3, Center = 4, Right = 5 }

/// <summary>Which way a gradient fades out: Right is solid at its left edge and clear at its right edge.</summary>
public enum UiGradient { Right, Left, Down, Up }

/// <summary>An image owned by the game or the platform (for example a course photo) that a canvas can draw.</summary>
public sealed class UiImage
{
    public UiImage(IntPtr handle, int width, int height)
    {
        Handle = handle;
        Width = width;
        Height = height;
    }

    public IntPtr Handle { get; }
    public int Width { get; }
    public int Height { get; }
}

/// <summary>Drawing surface of a custom screen: a 1920x1080 space with the origin at the top left. Colours are
/// 0xRRGGBBAA. Later calls draw on top of earlier ones. Text supports the engine's rich text tags
/// (&lt;color=#RRGGBB&gt;, &lt;b&gt;, &lt;size=N&gt;).</summary>
public interface IUiCanvas
{
    /// <summary>True while the page is being drawn for an entrance (first open, or Invalidate(animate: true)).</summary>
    bool Entering { get; }

    void Rect(float x, float y, float w, float h, uint rgba);

    /// <summary>A rectangle with rounded corners.</summary>
    void Panel(float x, float y, float w, float h, uint rgba, float radius = 14);

    /// <summary>A rounded outline.</summary>
    void Outline(float x, float y, float w, float h, uint rgba, float radius = 14);

    /// <summary>A soft drop shadow for a panel of this position and size. Draw it before the panel.</summary>
    void Shadow(float x, float y, float w, float h, float strength = 0.5f);

    /// <summary>A colour that fades to nothing across the box.</summary>
    void Gradient(float x, float y, float w, float h, uint rgba, UiGradient direction);

    /// <summary>One of the game's own interface icons, by its sprite name. Nothing is drawn if the game has no such sprite.</summary>
    void Icon(float x, float y, float size, string name, uint rgba);

    void Sprite(float x, float y, float w, float h, string name, uint rgba);
    void Label(float x, float y, float w, float h, string text, int size, uint rgba, UiAlign align = UiAlign.Left, bool heading = false);

    /// <summary>A number that counts from the value it showed last time (under the same key) to the new one.</summary>
    void Number(float x, float y, float w, float h, string key, double value, Func<double, string> format, int size, uint rgba,
        UiAlign align = UiAlign.Left, bool heading = true);

    /// <summary>A progress bar that glides from the fraction it showed last time (under the same key) to the new one.</summary>
    void Meter(float x, float y, float w, float h, string key, float fraction, uint fill, uint back);

    /// <summary>An image file, scaled to fill the box (cropping what does not fit).</summary>
    void Picture(float x, float y, float w, float h, string path, uint tint = 0xFFFFFFFF, float radius = 0);

    /// <summary>A game or platform image, scaled to fill the box (cropping what does not fit).</summary>
    void Image(float x, float y, float w, float h, UiImage image, uint tint = 0xFFFFFFFF, float radius = 0);

    void Button(float x, float y, float w, float h, string label, Action onClick, uint fill, uint hoverFill, uint textColor,
        int size = 26, bool heading = true, float radius = 10, string? tooltip = null);

    /// <summary>A rectangle that changes colour under the pointer and runs an action when clicked (for cards and rows).
    /// <paramref name="onHover"/> runs when the pointer (or the keyboard/gamepad focus) moves onto it from another
    /// area, which lets a screen show details for the row being pointed at.</summary>
    void Clickable(float x, float y, float w, float h, Action onClick, uint fill, uint hoverFill, float radius = 0, string? tooltip = null,
        Action? onHover = null);

    /// <summary>A filled shape that glides from where it was last drawn under the same key to this position and
    /// size (a selection that slides between rows, a switch knob). It simply appears the first time.</summary>
    void Slide(string key, float x, float y, float w, float h, uint rgba, float radius = 4);

    /// <summary>An invisible area that shows a tooltip when the pointer rests on it.</summary>
    void Hint(float x, float y, float w, float h, string tooltip);

    /// <summary>Everything drawn until the returned scope is disposed fades and rises in together, after a delay.
    /// Only plays while <see cref="Entering"/>; otherwise the content simply appears.</summary>
    IDisposable Group(float delay = 0f, float rise = 22f);

    /// <summary>A soft round light, brightest in the middle and fading to nothing at the edge of the box.</summary>
    void Glow(float x, float y, float w, float h, uint rgba);

    /// <summary>A circular meter filled clockwise from the top. It sweeps from the fraction it showed last time
    /// under the same key (from empty on an entrance).</summary>
    void Ring(float x, float y, float size, float thickness, string key, float fraction, uint fill, uint back);

    /// <summary>A photo that fills the box and drifts slowly (a gentle zoom and pan), for backdrops. Give a game or
    /// platform image, or a file path; the image wins when both are given.</summary>
    void Backdrop(float x, float y, float w, float h, UiImage? image, string? path, uint tint = 0xFFFFFFFF, float drift = 0.08f);

    /// <summary>A soft halo around a box that slowly breathes, to draw the eye to the next thing to do. Draw it
    /// before the box.</summary>
    void Pulse(float x, float y, float w, float h, uint rgba, float radius = 12);

    /// <summary>Text drawn until the returned scope is disposed casts a soft shadow, for legibility over photos.</summary>
    IDisposable TextShadow(uint rgba = 0x000000B4, float distance = 2f);

    /// <summary>Seconds on the screen's animation clock. It follows recorded time while the game is being captured
    /// frame by frame, so use it (not the wall clock) for anything timed on screen.</summary>
    double Time { get; }
}

/// <summary>One of the courses that ship with the game.</summary>
public sealed class CourseInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public int Par { get; init; }
    public int Holes { get; init; }
    public int[] Pars { get; init; } = Array.Empty<int>();
    public bool IsRealCourse { get; init; }

    /// <summary>The game's own difficulty rating of the course, 0 (easiest) to 4 (hardest).</summary>
    public int Difficulty { get; init; }

    public float LengthYards { get; init; }
}

/// <summary>Which holes a match plays.</summary>
public enum MatchHoles { FullCourse = 0, FrontNine = 1, BackNine = 2 }

/// <summary>The courses installed with the game, read from the game's own data at run time.</summary>
public interface ICourseCatalog
{
    System.Collections.Generic.IReadOnlyList<CourseInfo> All { get; }
    CourseInfo? Find(string courseId);

    /// <summary>The game's own preview photo of a course, or null if it has none.</summary>
    UiImage? Thumbnail(string courseId);

    /// <summary>Opens the game's local match screen on this course with the given holes, ready to begin the round.
    /// Call it while the main menu is in front. False if it could not be started.</summary>
    bool OpenMatch(string courseId, MatchHoles holes);
}

/// <summary>The game's main menu: its tabs, the tiles on each tab, and the banner above them.</summary>
public interface IMainMenu
{
    /// <summary>Which main-menu tabs to show, in order.</summary>
    void SetTabs(params MenuTab[] tabs);

    /// <summary>Replaces a tab's tiles. Slots with fixed artwork are turned into plain text tiles.</summary>
    void SetTiles(MenuTab tab, params MenuTile[] tiles);

    /// <summary>Removes the "Go Online" prompt and its action from the menu banner.</summary>
    void RemoveGoOnline();

    /// <summary>Restyles the large panel above the tiles: your artwork, title, subtitle and click action.</summary>
    void SetBanner(MenuBanner banner);

    /// <summary>Opens one of the game's own main-menu features, as if its tile had been clicked. False if it is not available.</summary>
    bool Open(GameTile tile);
}

/// <summary>The large panel above the main menu tiles.</summary>
public sealed class MenuBanner
{
    public required string Title { get; init; }
    public string? Subtitle { get; init; }

    /// <summary>Full path of a transparent PNG drawn in place of the title text (a wordmark). The title text is
    /// used if the image cannot be loaded.</summary>
    public string? LogoPath { get; init; }

    /// <summary>Full path of a PNG or JPG drawn as the panel's background (about 16:10).</summary>
    public string? ImagePath { get; init; }

    /// <summary>Use one of the game's own course photos as the background (a different one each day), read from
    /// the installed game at run time. <see cref="ImagePath"/> is then only the fallback.</summary>
    public bool CoursePhoto { get; init; }

    /// <summary>Text of the prompt in the panel's corner (shown with its key). Null hides the prompt.</summary>
    public string? CalloutLabel { get; init; }

    /// <summary>Runs when the player selects the panel or presses the prompt's key.</summary>
    public Action? OnClick { get; init; }
}

/// <summary>A row in a choice popup.</summary>
public sealed class MenuChoice
{
    public required string Label { get; init; }
    public string? Description { get; init; }
    public required Action OnSelect { get; init; }
}

/// <summary>An installed mod and whether it will be loaded on the next start.</summary>
public sealed class ModEntry
{
    public required string Folder { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string Version { get; init; } = "";
    public string Author { get; init; } = "";

    /// <summary>One of the game's interface icons named in mod.json ("icon"), or empty.</summary>
    public string Icon { get; init; } = "";

    /// <summary>True if the mod is running in this session.</summary>
    public bool Running { get; init; }

    /// <summary>True if the mod will be loaded the next time the game starts.</summary>
    public bool EnabledNextStart { get; set; }
}

/// <summary>The installed mods and their on/off switches (the same switches the launcher shows).</summary>
public interface IModManager
{
    System.Collections.Generic.IReadOnlyList<ModEntry> Installed { get; }

    /// <summary>Switches a mod on or off for the next start. Mods cannot be loaded or unloaded mid-session.</summary>
    void SetEnabled(ModEntry mod, bool enabled);
}

/// <summary>The main menu's tabs.</summary>
public enum MenuTab { Home = 0, Play = 1, Compete = 2, Design = 3 }

/// <summary>Tiles the game already has and that work offline.</summary>
public enum GameTile { QuickPlay, Training, NewCourse, UnpublishedCourses, Career }

/// <summary>A tile on a main-menu tab: your own (label and action), or one of the game's via <see cref="Game"/>.</summary>
public sealed class MenuTile
{
    /// <summary>Tile text. Null keeps the game's own name (for a custom tile, the "MyCAREER" naming).</summary>
    public string? Label { get; init; }

    /// <summary>Longer text shown for the highlighted tile.</summary>
    public string? Description { get; init; }

    /// <summary>Runs on the game's main thread when the player selects the tile.</summary>
    public Action? OnClick { get; init; }

    /// <summary>Set to reuse one of the game's own tiles, with its own behaviour, instead of a custom one.</summary>
    public GameTile? Existing { get; init; }

    public static MenuTile Game(GameTile tile) => new() { Existing = tile };
}

/// <summary>Lets one mod offer an action by name and another call it, without the two referencing each other.</summary>
public interface IActionRegistry
{
    void Register(string name, Action action);

    /// <summary>Registers an action that takes one text argument (a number, an id...).</summary>
    void Register(string name, Action<string> action);

    /// <summary>Runs the action if some mod registered it. Returns false if nobody did.</summary>
    bool Invoke(string name);

    /// <summary>Runs an action with an argument. Returns false if nobody registered it.</summary>
    bool Invoke(string name, string argument);
}

/// <summary>The game's main thread, where all game objects must be touched: a per-frame update, the quit event,
/// and a way to run code there from any thread.</summary>
public interface IMainThread
{
    /// <summary>Raised once per rendered frame on the game's main thread.</summary>
    event Action? Update;

    /// <summary>Raised on the main thread when the game starts to quit: the last chance to save.</summary>
    event Action? Quitting;

    /// <summary>Runs an action once on the game's main thread, on the next frame. Safe to call from any thread.</summary>
    void Post(Action action);

    long Frame { get; }

    /// <summary>Seconds on the platform's animation clock: real time normally, recorded time while the game is
    /// being captured frame by frame. Use it for anything timed on screen (toasts, banners).</summary>
    double Time { get; }
}

/// <summary>Writes to OpenFairway\logs\openfairway.log, each line tagged with the mod's id.</summary>
public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

/// <summary>
/// In-memory access to the running game through the IL2CPP runtime API the game itself exports.
/// Handles are raw runtime pointers; nothing here touches files on disk.
/// </summary>
public unsafe interface IGameRuntime
{
    /// <summary>Finds a class. <paramref name="assembly"/> is e.g. "HB.Golf" (with or without ".dll"); null searches all.
    /// Use "Outer.Inner" as the name for nested types.</summary>
    IntPtr FindClass(string? assembly, string @namespace, string name);

    /// <summary>Class of a live object.</summary>
    IntPtr GetClass(IntPtr instance);

    /// <summary>All loaded Unity objects (components, assets) of a class. Main thread only.</summary>
    IntPtr[] FindObjectsOfType(IntPtr klass);

    /// <summary>False once a Unity object has been destroyed by the engine. Check before reusing a cached pointer.</summary>
    bool IsAlive(IntPtr unityObject);

    /// <summary>Creates a game-side delegate (e.g. System.Action for a button) that runs your callback on the main thread.</summary>
    IntPtr CreateDelegate(IntPtr delegateClass, Action callback);

    /// <summary>Same, for delegates taking one reference argument, which is passed to the callback.</summary>
    IntPtr CreateDelegate(IntPtr delegateClass, Action<IntPtr> callback);

    /// <summary>A game-side delegate with no arguments that returns the object reference your callback gives.</summary>
    IntPtr CreateFunc(IntPtr delegateClass, Func<IntPtr> callback);

    /// <summary>Same, for delegates taking one reference argument.</summary>
    IntPtr CreateFunc(IntPtr delegateClass, Func<IntPtr, IntPtr> callback);

    /// <summary>Creates an object of a game class and runs a constructor (".ctor" or ".ctor(Type,Type)").</summary>
    IntPtr NewObject(IntPtr klass, string constructor, params IntPtr[] args);

    /// <summary>For delegates taking two arguments, each delivered as a pointer-sized value (object references as-is,
    /// enums and integers in the low bits).</summary>
    IntPtr CreateDelegate(IntPtr delegateClass, Action<IntPtr, IntPtr> callback);

    /// <summary>Keeps an object alive for the rest of the session even if the game drops its own references to it.</summary>
    void Pin(IntPtr obj);

    /// <summary>The System.Type object of a class, for game functions that take a type.</summary>
    IntPtr GetTypeObject(IntPtr klass);

    /// <summary>Name of a class, including its namespace.</summary>
    string GetClassName(IntPtr klass);

    /// <summary>Stores an object reference at an address inside <paramref name="owner"/> (a field or array slot).</summary>
    void WriteReference(IntPtr owner, IntPtr address, IntPtr value);

    /// <summary>Creates a reference-type array in the game's runtime.</summary>
    IntPtr NewArray(IntPtr elementClass, IntPtr[] items);

    /// <summary>Elements of a reference-type array.</summary>
    IntPtr[] ReadArray(IntPtr array);

    /// <summary>Class of a method's parameter (e.g. to get a closed generic delegate type).</summary>
    IntPtr GetParameterClass(IntPtr method, int index);

    /// <summary>A real game delegate bound to one of the game's own static methods.</summary>
    IntPtr CreateDelegateForMethod(IntPtr delegateClass, IntPtr method);

    /// <summary>Boxes a value (enum, number, struct) so it can be passed where the game expects an object.</summary>
    IntPtr Box(IntPtr valueClass, void* value);

    /// <summary>Finds a method by name and parameter count (-1 = any). Use "Name(Full.Type.A,Full.Type.B)" to pick an
    /// overload by its parameter types.</summary>
    IntPtr FindMethod(IntPtr klass, string name, int parameterCount = -1);

    /// <summary>Invokes a method. Value-type arguments are passed as pointers to the value, reference types as object pointers.</summary>
    IntPtr Invoke(IntPtr method, IntPtr instance, params IntPtr[] args);

    T GetStaticField<T>(IntPtr klass, string field) where T : unmanaged;
    void SetStaticField<T>(IntPtr klass, string field, T value) where T : unmanaged;

    /// <summary>Stores an object reference in a static field.</summary>
    void SetStaticReference(IntPtr klass, string field, IntPtr value);

    /// <summary>Stores an object reference in an instance field.</summary>
    void SetReference(IntPtr instance, string field, IntPtr value);

    T GetField<T>(IntPtr instance, IntPtr klass, string field) where T : unmanaged;
    void SetField<T>(IntPtr instance, IntPtr klass, string field, T value) where T : unmanaged;

    IntPtr NewString(string value);
    string? ReadString(IntPtr il2cppString);
}
