using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OpenFairway.API;
using OpenFairway.Core.Il2Cpp;

namespace OpenFairway.Core.Game;

/// <summary>
/// The courses that ship with the game, read at run time from the game's own "static storage" bundle (the data it
/// uses when it is offline). Nothing is copied out of the game: names, pars and preview photos are read from the
/// installed game each session, and a match is started through the game's own local match screen.
/// </summary>
internal sealed unsafe class Courses : ICourseCatalog
{
    private const string MetadataFolder = "/coursemetadata/", ThumbnailFolder = "/coursethumbnail/";

    private readonly GameRuntime _rt;
    private readonly UiToolkit _kit;
    private readonly Func<bool> _mainMenuReady;
    private readonly Dictionary<string, string> _thumbnailAssets = new();
    private readonly Dictionary<string, UiImage?> _thumbnails = new();
    private List<CourseInfo>? _all;
    private IntPtr _bundle, _loadAsset, _getText, _textAssetType;
    private readonly List<IntPtr> _keepAlive = new();

    public Courses(GameRuntime runtime, UiToolkit kit, Func<bool> mainMenuReady)
    {
        _rt = runtime;
        _kit = kit;
        _mainMenuReady = mainMenuReady;
    }

    public IReadOnlyList<CourseInfo> All => _all ??= Load();

    public CourseInfo? Find(string courseId) => All.FirstOrDefault(c => c.Id == courseId);

    private List<CourseInfo> Load()
    {
        var courses = new List<CourseInfo>();
        try
        {
            if (!ResolveBundle()) return courses;
            var bundleClass = _rt.GetClass(_bundle);
            var names = _rt.ReadArray(_rt.Invoke(_rt.FindMethod(bundleClass, "GetAllAssetNames", 0), _bundle));
            foreach (var entry in names)
            {
                var name = _rt.ReadString(entry) ?? "";
                if (name.Contains(ThumbnailFolder) && name.EndsWith("-thumb.json"))
                {
                    var id = name[(name.LastIndexOf('/') + 1)..^"-thumb.json".Length];
                    _thumbnailAssets[id] = name;
                }
                if (!name.Contains(MetadataFolder) || !name.EndsWith("-meta.json") || name.EndsWith("-generic-meta.json")) continue;
                var text = ReadText(name);
                if (text == null) continue;
                try
                {
                    using var document = JsonDocument.Parse(text);
                    var root = document.RootElement;
                    // "hiddenmetadata" entries are lesson areas and test courses, not courses to play.
                    if (Text(root, "type") != "metadata") continue;
                    var holes = Number(root, "holes");
                    var id = Text(root, "courseId");
                    if (holes < 9 || string.IsNullOrEmpty(id)) continue;
                    courses.Add(new CourseInfo
                    {
                        Id = id,
                        Name = Text(root, "name") ?? id,
                        Par = Number(root, "par"),
                        Holes = holes,
                        Pars = root.TryGetProperty("pars", out var pars) && pars.ValueKind == JsonValueKind.Array
                            ? pars.EnumerateArray().Select(p => p.TryGetInt32(out var v) ? v : 4).ToArray() : Array.Empty<int>(),
                        IsRealCourse = root.TryGetProperty("isRealCourse", out var real) && real.ValueKind == JsonValueKind.True,
                        Difficulty = Number(root, "difficulty"),
                        LengthYards = root.TryGetProperty("length", out var length) && length.TryGetSingle(out var metres) ? metres * 1.0936f : 0f,
                    });
                }
                catch (JsonException) { /* not a course record */ }
            }
            courses.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            Log.Info($"Courses: {courses.Count} found in the game's data, {_thumbnailAssets.Count} preview photos.");
        }
        catch (Exception e)
        {
            Log.Warn("The game's course list could not be read: " + e.Message);
        }
        return courses;
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private bool ResolveBundle()
    {
        if (_bundle != IntPtr.Zero && _rt.IsAlive(_bundle)) return true;
        var bundles = _rt.FindClass("HB.Golf", "", "GolfBundles");
        var textAsset = _rt.FindClass("UnityEngine.CoreModule", "UnityEngine", "TextAsset");
        var assetBundle = _rt.FindClass("UnityEngine.AssetBundleModule", "UnityEngine", "AssetBundle");
        if (bundles == IntPtr.Zero || textAsset == IntPtr.Zero || assetBundle == IntPtr.Zero) return false;
        var getter = _rt.FindMethod(bundles, "get_StaticStorage", 0);
        if (getter == IntPtr.Zero) return false;
        var wrapper = _rt.Invoke(getter, IntPtr.Zero);
        if (wrapper == IntPtr.Zero) return false;
        var reference = _rt.GetField<IntPtr>(wrapper, _rt.GetClass(wrapper), "bundleRef");
        if (reference == IntPtr.Zero) return false;
        _bundle = _rt.GetField<IntPtr>(reference, _rt.GetClass(reference), "bundle");
        _loadAsset = _rt.FindMethod(assetBundle, "LoadAsset(System.String,System.Type)");
        _getText = _rt.FindMethod(textAsset, "get_text", 0);
        _textAssetType = _rt.GetTypeObject(textAsset);
        return _bundle != IntPtr.Zero && _rt.IsAlive(_bundle) && _loadAsset != IntPtr.Zero && _getText != IntPtr.Zero;
    }

    private string? ReadText(string assetName)
    {
        var asset = _rt.Invoke(_loadAsset, _bundle, _rt.NewString(assetName), _textAssetType);
        return asset == IntPtr.Zero ? null : _rt.ReadString(_rt.Invoke(_getText, asset));
    }

    public UiImage? Thumbnail(string courseId)
    {
        if (_thumbnails.TryGetValue(courseId, out var cached) && (cached == null || _rt.IsAlive(cached.Handle))) return cached;
        UiImage? image = null;
        try
        {
            _ = All;
            if (_thumbnailAssets.TryGetValue(courseId, out var asset) && ResolveBundle() && _kit.Resolve() && ReadText(asset) is { } text)
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.TryGetProperty("image", out var data))
                {
                    var bytes = data.ValueKind switch
                    {
                        JsonValueKind.String => Convert.FromBase64String(data.GetString() ?? ""),
                        JsonValueKind.Array => data.EnumerateArray().Select(b => b.GetByte()).ToArray(),
                        _ => Array.Empty<byte>(),
                    };
                    if (bytes.Length > 0)
                    {
                        var texture = _kit.TextureFromBytes(bytes, out var width, out var height);
                        if (texture != IntPtr.Zero && width > 8) image = new UiImage(texture, width, height);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Preview photo of course {courseId} not loaded: {e.Message}");
        }
        return _thumbnails[courseId] = image;
    }

    public bool OpenMatch(string courseId, MatchHoles holes)
    {
        try
        {
            if (!_mainMenuReady()) return false;
            var preRound = _rt.FindClass("HB.Golf", "MVC", "BasePrivateMatchPreRound");
            var flow = _rt.FindClass("HB.Golf", "", "AsyncFlow");
            if (preRound == IntPtr.Zero || flow == IntPtr.Zero) return false;
            var loadMeta = _rt.FindMethod(preRound, "LoadCourseMeta", 2);
            var begin = _rt.FindMethod(flow, "Begin(System.Collections.IEnumerable)");
            if (loadMeta == IntPtr.Zero || begin == IntPtr.Zero) return false;

            // The game looks the course up itself (from its offline data) and hands back its own record of it.
            var callback = _rt.CreateDelegate(_rt.GetParameterClass(loadMeta, 1), (IntPtr metadata) =>
            {
                try { ShowMatch(metadata, holes, courseId); }
                catch (Exception e) { Log.Error("Could not open the match screen: " + e); }
            });
            _keepAlive.Add(callback);
            _rt.Pin(callback);
            var task = _rt.Invoke(loadMeta, IntPtr.Zero, _rt.NewString(courseId), callback);
            _rt.Invoke(begin, IntPtr.Zero, task);
            return true;
        }
        catch (Exception e)
        {
            Log.Error("Could not start a match: " + e);
            return false;
        }
    }

    private void ShowMatch(IntPtr metadata, MatchHoles holes, string courseId)
    {
        if (metadata == IntPtr.Zero) { Log.Warn($"The game has no course {courseId}."); return; }
        var overridesClass = _rt.FindClass("HB.Golf", "", "CourseOverrides");
        var matchClass = _rt.FindClass("HB.Golf", "MVC", "LocalQuickplay");
        var groupClass = _rt.FindClass("HB.Golf", "MVC", "GolfGroup");
        var screens = _rt.FindClass("HB.Golf", "MVC", "ScreenContainer");
        if (overridesClass == IntPtr.Zero || matchClass == IntPtr.Zero || groupClass == IntPtr.Zero || screens == IntPtr.Zero) return;

        var overrides = _rt.NewObject(overridesClass, ".ctor()");
        _rt.Invoke(_rt.FindMethod(overridesClass, "SetCourse", 1), overrides, metadata);
        var holesToPlay = (int)holes;
        var noLimit = stackalloc byte[8]; // an empty Nullable<int>
        for (var i = 0; i < 8; i++) noLimit[i] = 0;
        _rt.Invoke(_rt.FindMethod(overridesClass, "SetHolesToPlay", 2), overrides, (IntPtr)(&holesToPlay), (IntPtr)noLimit);

        var group = _rt.Invoke(_rt.FindMethod(groupClass, "Get", 0), IntPtr.Zero);
        byte isPostGame = 0;
        var match = _rt.NewObject(matchClass, ".ctor(MVC.GolfGroup,CourseMetadata,System.Boolean,CourseOverrides)", group, metadata, (IntPtr)(&isPostGame), overrides);
        _rt.Pin(match);
        var show = _rt.FindMethod(screens, "Show(System.Func<MVC.BaseController>)");
        _rt.Invoke(show, IntPtr.Zero, _rt.CreateFunc(_rt.GetParameterClass(show, 0), () => match));
    }
}
