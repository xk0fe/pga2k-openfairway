using System;
using System.Collections.Generic;
using OpenFairway.API;
using OpenFairway.Core.Il2Cpp;

namespace OpenFairway.Core.Games;

/// <summary>
/// The course list of PGA TOUR 2K23 (<see cref="CourseSource.StaticStorageView"/>). Its data bundle cannot name what
/// it holds, so the game's own offline storage (Storage.StaticStorage) is asked for its course view, exactly as the
/// game's course picker asks when it is offline. The storage answers a few frames later with the game's own course
/// records; it also says where each course's preview photo is, which is then read like 2K25's.
/// </summary>
internal sealed unsafe partial class Courses
{
    /// <summary>Records of another type are lesson areas and test courses, not courses to play.</summary>
    private const string PlayableType = "metadata";

    private const int MinimumHoles = 9;
    private const float YardsPerMetre = 1.0936f;
    private const string ThumbnailSuffix = "-Thumb";

    private bool _viewAsked;

    /// <summary>Starts reading the list where the game hands it over later (2K23); the platform calls it once the
    /// main menu is up, so the list is there by the time a mod asks.</summary>
    public void Preload()
    {
        if (_profile.Courses == CourseSource.StaticStorageView) AskForView();
    }

    private IReadOnlyList<CourseInfo> ViewCourses()
    {
        AskForView();
        return _all ?? (IReadOnlyList<CourseInfo>)Array.Empty<CourseInfo>();
    }

    private void AskForView()
    {
        if (_viewAsked) return;
        _viewAsked = true;
        try
        {
            var storageClass = _rt.FindClass("HB.Golf", "Storage", "StaticStorage");
            var metadataClass = _rt.FindClass("HB.Golf", "", "CourseMetadata");
            var thumbnailClass = _rt.FindClass("HB.Golf", "", "CourseThumbnail");
            var paramsClass = _rt.FindClass("HB.Golf", "", "ViewParamsBuilder");
            var getView = _rt.FindMethod(storageClass, "Get(System.Action<RestError,DB.List<Document>>,System.Type,ViewParamsBuilder)");
            var getPath = _rt.FindMethod(storageClass, "GetPath", 2);
            if (getView == IntPtr.Zero || getPath == IntPtr.Zero || metadataClass == IntPtr.Zero || thumbnailClass == IntPtr.Zero || paramsClass == IntPtr.Zero)
            {
                Log.Warn("The game's offline course storage was not found; there is no course list.");
                return;
            }
            var storage = _rt.NewObject(storageClass, ".ctor");
            var viewParams = _rt.NewObject(paramsClass, ".ctor");
            _rt.Pin(storage);
            _rt.Pin(viewParams);
            var thumbnailType = _rt.GetTypeObject(thumbnailClass);
            // The answer is (RestError, DB.List<Document>); the error is an enum, delivered in the low bits.
            var answer = _rt.CreateDelegate(_rt.GetParameterClass(getView, 0), (IntPtr error, IntPtr list) =>
            {
                try { ReadView((int)error, list, storage, getPath, thumbnailType); }
                catch (Exception e) { Log.Warn("The game's course list could not be read: " + e.Message); }
            });
            _rt.Invoke(getView, storage, answer, _rt.GetTypeObject(metadataClass), viewParams);
            Log.Detail("[courses] asked the game's offline storage for its course view");
        }
        catch (Exception e)
        {
            Log.Warn("The game's course list could not be asked for: " + e.Message);
        }
    }

    private void ReadView(int error, IntPtr list, IntPtr storage, IntPtr getPath, IntPtr thumbnailType)
    {
        if (list == IntPtr.Zero)
        {
            Log.Warn($"The game's offline storage gave no course list (error {error}).");
            _all = new List<CourseInfo>();
            return;
        }
        var courses = new List<CourseInfo>();
        var records = _rt.ReadArray(_rt.GetField<IntPtr>(list, _rt.GetClass(list), "listEntry"));
        foreach (var record in records)
        {
            if (record == IntPtr.Zero) continue;
            var klass = _rt.GetClass(record);
            string? Text(string field) => _rt.ReadString(_rt.GetField<IntPtr>(record, klass, field));
            var id = Text("courseId");
            var holes = _rt.GetField<int>(record, klass, "holes");
            if (Text("type") != PlayableType || holes < MinimumHoles || string.IsNullOrEmpty(id)) continue;
            courses.Add(new CourseInfo
            {
                Id = id,
                Name = Text("name") ?? id,
                Par = _rt.GetField<int>(record, klass, "par"),
                Holes = holes,
                Pars = Ints(_rt.GetField<IntPtr>(record, klass, "pars")),
                IsRealCourse = _rt.GetField<byte>(record, klass, "isRealCourse") != 0,
                Difficulty = _rt.GetField<int>(record, klass, "difficulty"),
                LengthYards = _rt.GetField<float>(record, klass, "length") * YardsPerMetre,
            });
            // Where the storage keeps this course's preview photo, in the same bundle.
            var path = _rt.ReadString(_rt.Invoke(getPath, storage, _rt.NewString(id + ThumbnailSuffix), thumbnailType));
            if (path != null) _thumbnailAssets[id] = path;
        }
        courses.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        _all = courses;
        Log.Info($"Courses: {courses.Count} from the game's offline storage ({records.Length} records).");
    }

    private static int[] Ints(IntPtr array)
    {
        if (array == IntPtr.Zero) return Array.Empty<int>();
        var length = (int)*(nuint*)(array + ArrayLengthOffset);
        var values = new int[length];
        for (var i = 0; i < length; i++) values[i] = *(int*)(array + ArrayDataOffset + i * sizeof(int));
        return values;
    }

    // Il2CppArray: object header, bounds, then the length and the elements.
    private const int ArrayLengthOffset = 0x18, ArrayDataOffset = 0x20;
}
