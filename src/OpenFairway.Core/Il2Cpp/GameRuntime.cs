using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using OpenFairway.API;

namespace OpenFairway.Core.Il2Cpp;

internal sealed unsafe class GameRuntime : IGameRuntime
{
    private const int ObjectHeaderSize = 16; // Il2CppObject: klass + monitor

    public int AssemblyCount => Images().Count;

    /// <summary>
    /// Attaches the calling thread to the game's runtime for the lifetime of the returned scope.
    /// Threads must not stay attached while idle: the game waits for attached threads when it exits.
    /// </summary>
    public static AttachScope Attach() => new(Native.thread_attach(Native.domain_get()));

    public readonly struct AttachScope(IntPtr thread) : IDisposable
    {
        public void Dispose() => Native.thread_detach(thread);
    }

    public static List<(string Name, IntPtr Image)> Images()
    {
        nuint count = 0;
        var assemblies = Native.domain_get_assemblies(Native.domain_get(), &count);
        var result = new List<(string, IntPtr)>((int)count);
        for (nuint i = 0; i < count; i++)
        {
            var image = Native.assembly_get_image(assemblies[i]);
            result.Add((Native.Str(Native.image_get_name(image)), image));
        }
        return result;
    }

    public IntPtr FindClass(string? assembly, string @namespace, string name)
    {
        // "Outer.Inner" resolves a nested type.
        var parts = name.Split('.');
        var klass = FindTopLevelClass(assembly, @namespace, parts[0]);
        for (var i = 1; i < parts.Length && klass != IntPtr.Zero; i++)
        {
            IntPtr iter = IntPtr.Zero, nested, match = IntPtr.Zero;
            while ((nested = Native.class_get_nested_types(klass, &iter)) != IntPtr.Zero)
                if (Native.Str(Native.class_get_name(nested)) == parts[i]) { match = nested; break; }
            klass = match;
        }
        return klass;
    }

    /// <summary>The game's own assemblies, by the names PGA TOUR 2K25 gives them, mapped to what this game calls
    /// them (Games.GameProfile.AssemblyNames), so a class asked for as "HB.Golf" is found in every game.</summary>
    public static IReadOnlyDictionary<string, string> AssemblyNames { get; set; } = new Dictionary<string, string>();

    private static IntPtr FindTopLevelClass(string? assembly, string @namespace, string name)
    {
        var asked = assembly != null && assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? assembly[..^4] : assembly;
        if (asked != null && AssemblyNames.TryGetValue(asked, out var own)) asked = own;
        var wanted = asked == null ? null : asked + ".dll";
        fixed (byte* ns = Utf8(@namespace), n = Utf8(name))
        {
            foreach (var (imageName, image) in Images())
            {
                if (wanted != null && !imageName.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
                var klass = Native.class_from_name(image, ns, n);
                if (klass != IntPtr.Zero) return klass;
            }
        }
        return IntPtr.Zero;
    }

    public IntPtr GetClass(IntPtr instance) => Native.object_get_class(instance);

    public string GetClassName(IntPtr klass) => Native.TypeName(Native.class_get_type(klass));

    public bool IsAlive(IntPtr unityObject) => unityObject != IntPtr.Zero && *(IntPtr*)(unityObject + ObjectHeaderSize) != IntPtr.Zero;

    private IntPtr _findObjects;

    public IntPtr[] FindObjectsOfType(IntPtr klass)
    {
        if (_findObjects == IntPtr.Zero)
            _findObjects = FindMethod(FindClass("UnityEngine.CoreModule", "UnityEngine", "Object"), "FindObjectsOfType", 1);
        var type = Native.type_get_object(Native.class_get_type(klass));
        return ReadArray(Invoke(_findObjects, IntPtr.Zero, type));
    }

    // ---- delegates backed by managed callbacks -------------------------------------------------------------
    // A delegate object is created in the game's runtime whose entry point is one of the dispatchers below.
    // The runtime passes the delegate's "method_code" slot as the first argument, which carries the callback id.

    private const int DelegateMethodPtr = 0x10, DelegateInvokeImpl = 0x18, DelegateMethod = 0x28, DelegateMethodCode = 0x40;
    private static readonly List<Action<IntPtr>> Callbacks = new();
    private IntPtr _placeholderMethod;

    public IntPtr CreateDelegate(IntPtr delegateClass, Action callback) => CreateDelegate(delegateClass, _ => callback(), 0);

    public IntPtr CreateDelegate(IntPtr delegateClass, Action<IntPtr> callback) => CreateDelegate(delegateClass, callback, 1);

    private IntPtr CreateDelegate(IntPtr delegateClass, Action<IntPtr> callback, int argumentCount)
    {
        if (_placeholderMethod == IntPtr.Zero)
            _placeholderMethod = FindMethod(FindClass("mscorlib", "System", "Object"), "Finalize", 0);
        int id;
        lock (Callbacks) { Callbacks.Add(callback); id = Callbacks.Count; }
        var entry = argumentCount == 0
            ? (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&Dispatch0
            : (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&Dispatch1;
        var del = Native.object_new(delegateClass);
        *(IntPtr*)(del + DelegateMethodPtr) = entry;
        *(IntPtr*)(del + DelegateInvokeImpl) = entry;
        *(IntPtr*)(del + DelegateMethod) = _placeholderMethod;
        *(IntPtr*)(del + DelegateMethodCode) = id;
        Native.gchandle_new(del, 0); // keep it alive for the session
        return del;
    }

    private static readonly List<Func<IntPtr, IntPtr>> Functions = new();

    /// <summary>A game-side delegate with no arguments whose return value (an object reference) comes from your callback.</summary>
    public IntPtr CreateFunc(IntPtr delegateClass, Func<IntPtr> callback) => CreateFunc(delegateClass, _ => callback(), 0);

    /// <summary>Same, for delegates taking one reference argument.</summary>
    public IntPtr CreateFunc(IntPtr delegateClass, Func<IntPtr, IntPtr> callback) => CreateFunc(delegateClass, callback, 1);

    private IntPtr CreateFunc(IntPtr delegateClass, Func<IntPtr, IntPtr> callback, int argumentCount)
    {
        if (_placeholderMethod == IntPtr.Zero)
            _placeholderMethod = FindMethod(FindClass("mscorlib", "System", "Object"), "Finalize", 0);
        int id;
        lock (Functions) { Functions.Add(callback); id = Functions.Count; }
        var entry = argumentCount == 0
            ? (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr>)&DispatchFunc0
            : (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)&DispatchFunc1;
        var del = Native.object_new(delegateClass);
        *(IntPtr*)(del + DelegateMethodPtr) = entry;
        *(IntPtr*)(del + DelegateInvokeImpl) = entry;
        *(IntPtr*)(del + DelegateMethod) = _placeholderMethod;
        *(IntPtr*)(del + DelegateMethodCode) = id;
        Native.gchandle_new(del, 0);
        return del;
    }

    private static IntPtr RunFunc(IntPtr id, IntPtr argument)
    {
        try
        {
            Func<IntPtr, IntPtr> callback;
            lock (Functions) callback = Functions[(int)id - 1];
            return callback(argument);
        }
        catch (Exception e)
        {
            Log.Error($"Callback {id} failed: {e}");
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly]
    private static IntPtr DispatchFunc0(IntPtr id, IntPtr method) => RunFunc(id, IntPtr.Zero);

    [UnmanagedCallersOnly]
    private static IntPtr DispatchFunc1(IntPtr id, IntPtr argument, IntPtr method) => RunFunc(id, argument);

    /// <summary>Creates an object of a game class and runs the constructor that matches the given arguments.</summary>
    public IntPtr NewObject(IntPtr klass, string constructor, params IntPtr[] args)
    {
        var obj = Native.object_new(klass);
        var ctor = constructor.Contains('(') ? FindMethod(klass, constructor) : FindMethod(klass, constructor, args.Length);
        if (ctor == IntPtr.Zero) throw new GameException("Constructor not found: " + constructor);
        Invoke(ctor, obj, args);
        return obj;
    }

    private static readonly List<Action<IntPtr, IntPtr>> Callbacks2 = new();

    /// <summary>A game-side delegate with two arguments (each passed as a pointer-sized value: object references as-is,
    /// enums and integers in the low bits).</summary>
    public IntPtr CreateDelegate(IntPtr delegateClass, Action<IntPtr, IntPtr> callback)
    {
        if (_placeholderMethod == IntPtr.Zero)
            _placeholderMethod = FindMethod(FindClass("mscorlib", "System", "Object"), "Finalize", 0);
        int id;
        lock (Callbacks2) { Callbacks2.Add(callback); id = Callbacks2.Count; }
        var entry = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&Dispatch2;
        var del = Native.object_new(delegateClass);
        *(IntPtr*)(del + DelegateMethodPtr) = entry;
        *(IntPtr*)(del + DelegateInvokeImpl) = entry;
        *(IntPtr*)(del + DelegateMethod) = _placeholderMethod;
        *(IntPtr*)(del + DelegateMethodCode) = id;
        Native.gchandle_new(del, 0);
        return del;
    }

    [UnmanagedCallersOnly]
    private static void Dispatch2(IntPtr id, IntPtr first, IntPtr second, IntPtr method)
    {
        try
        {
            Action<IntPtr, IntPtr> callback;
            lock (Callbacks2) callback = Callbacks2[(int)id - 1];
            callback(first, second);
        }
        catch (Exception e)
        {
            Log.Error($"Callback {id} failed: {e}");
        }
    }

    /// <summary>Keeps an object alive for the rest of the session even if the game drops its own references to it.</summary>
    public void Pin(IntPtr obj) => Native.gchandle_new(obj, 0);

    public IntPtr GetTypeObject(IntPtr klass) => Native.type_get_object(Native.class_get_type(klass));

    private static void Run(IntPtr id, IntPtr argument)
    {
        try
        {
            Action<IntPtr> callback;
            lock (Callbacks) callback = Callbacks[(int)id - 1];
            callback(argument);
        }
        catch (Exception e)
        {
            Log.Error($"Callback {id} failed: {e}");
        }
    }

    [UnmanagedCallersOnly]
    private static void Dispatch0(IntPtr id, IntPtr method) => Run(id, IntPtr.Zero);

    [UnmanagedCallersOnly]
    private static void Dispatch1(IntPtr id, IntPtr argument, IntPtr method) => Run(id, argument);

    /// <summary>Stores an object reference into a field or array slot, telling the garbage collector about it.</summary>
    public void WriteReference(IntPtr owner, IntPtr address, IntPtr value) => Native.gc_wbarrier_set_field(owner, (void*)address, value);

    public IntPtr NewArray(IntPtr elementClass, IntPtr[] items)
    {
        var array = Native.array_new(elementClass, (nuint)items.Length);
        for (var i = 0; i < items.Length; i++) WriteReference(array, array + 0x20 + i * IntPtr.Size, items[i]);
        return array;
    }

    /// <summary>Creates a byte[] in the game's runtime with this content.</summary>
    public IntPtr NewByteArray(byte[] data)
    {
        var array = Native.array_new(FindClass("mscorlib", "System", "Byte"), (nuint)data.Length);
        Marshal.Copy(data, 0, array + 0x20, data.Length);
        return array;
    }

    public IntPtr[] ReadArray(IntPtr array)
    {
        if (array == IntPtr.Zero) return [];
        var length = (int)*(nuint*)(array + 0x18);
        var result = new IntPtr[length];
        for (var i = 0; i < length; i++) result[i] = *(IntPtr*)(array + 0x20 + i * IntPtr.Size);
        return result;
    }

    public IntPtr FindMethod(IntPtr klass, string name, int parameterCount = -1)
    {
        // The runtime dereferences the class without checking it, so a class that was not found stops here.
        if (klass == IntPtr.Zero) return IntPtr.Zero;
        // "Name(Full.Type.A,Full.Type.B)" selects an overload by parameter types; the class chain is searched.
        var open = name.IndexOf('(');
        if (open < 0)
        {
            fixed (byte* n = Utf8(name)) return Native.class_get_method_from_name(klass, n, parameterCount);
        }
        var wanted = SplitTopLevel(name[(open + 1)..^1]);
        var bare = name[..open];
        for (var k = klass; k != IntPtr.Zero; k = Native.class_get_parent(k))
        {
            IntPtr iter = IntPtr.Zero, method;
            while ((method = Native.class_get_methods(k, &iter)) != IntPtr.Zero)
            {
                if (Native.Str(Native.method_get_name(method)) != bare || Native.method_get_param_count(method) != wanted.Count) continue;
                var match = true;
                for (var i = 0; i < wanted.Count && match; i++)
                    match = Native.TypeName(Native.method_get_param(method, (uint)i)) == wanted[i];
                if (match) return method;
            }
        }
        return IntPtr.Zero;
    }

    private static List<string> SplitTopLevel(string list)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (var i = 0; i < list.Length; i++)
        {
            if (list[i] == '<') depth++;
            else if (list[i] == '>') depth--;
            else if (list[i] == ',' && depth == 0) { parts.Add(list[start..i].Trim()); start = i + 1; }
        }
        if (list.Trim().Length > 0) parts.Add(list[start..].Trim());
        return parts;
    }

    /// <summary>Class of a method's parameter (useful for obtaining closed generic delegate types).</summary>
    public IntPtr GetParameterClass(IntPtr method, int index) => Native.class_from_type(Native.method_get_param(method, (uint)index));

    public IntPtr Box(IntPtr valueClass, void* value) => Native.value_box(valueClass, value);

    private IntPtr _createDelegate;

    /// <summary>A real game delegate bound to one of the game's own static methods.</summary>
    public IntPtr CreateDelegateForMethod(IntPtr delegateClass, IntPtr method)
    {
        if (_createDelegate == IntPtr.Zero)
            _createDelegate = FindMethod(FindClass("mscorlib", "System", "Delegate"), "CreateDelegate(System.Type,System.Reflection.MethodInfo)");
        var type = Native.type_get_object(Native.class_get_type(delegateClass));
        var methodObject = Native.method_get_object(method, Native.method_get_class(method));
        return Invoke(_createDelegate, IntPtr.Zero, type, methodObject);
    }

    private readonly Dictionary<IntPtr, bool> _isStatic = new();

    public IntPtr Invoke(IntPtr method, IntPtr instance, params IntPtr[] args)
    {
        // Calling into the game with no method, or an instance method with no object, crashes the process
        // instead of raising an error, so refuse both here.
        if (method == IntPtr.Zero) throw new InvalidOperationException("Invoke: the method was not found.");
        if (instance == IntPtr.Zero)
        {
            if (!_isStatic.TryGetValue(method, out var isStatic))
            {
                uint implFlags;
                _isStatic[method] = isStatic = (Native.method_get_flags(method, &implFlags) & 0x10) != 0;
            }
            if (!isStatic) throw new InvalidOperationException($"Invoke: {Native.Str(Native.method_get_name(method))} needs an object and was given none.");
        }
        IntPtr exception = IntPtr.Zero;
        IntPtr result;
        fixed (IntPtr* a = args) result = Native.runtime_invoke(method, instance, args.Length == 0 ? null : a, &exception);
        if (exception != IntPtr.Zero)
        {
            var klass = Native.object_get_class(exception);
            throw new GameException($"{Native.Str(Native.class_get_namespace(klass))}.{Native.Str(Native.class_get_name(klass))} thrown by {Native.Str(Native.method_get_name(method))}");
        }
        return result;
    }

    public T GetStaticField<T>(IntPtr klass, string field) where T : unmanaged
    {
        T value = default;
        Native.field_static_get_value(Field(klass, field), &value);
        return value;
    }

    public void SetStaticField<T>(IntPtr klass, string field, T value) where T : unmanaged =>
        Native.field_static_set_value(Field(klass, field), &value);

    // For reference-type static fields the runtime takes the object pointer itself as the value.
    public void SetStaticReference(IntPtr klass, string field, IntPtr value) =>
        Native.field_static_set_value(Field(klass, field), (void*)value);

    public void SetReference(IntPtr instance, string field, IntPtr value)
    {
        var klass = Native.object_get_class(instance);
        WriteReference(instance, instance + (nint)InstanceOffset(klass, field), value);
    }

    public T GetField<T>(IntPtr instance, IntPtr klass, string field) where T : unmanaged =>
        *(T*)(instance + (nint)InstanceOffset(klass, field));

    public void SetField<T>(IntPtr instance, IntPtr klass, string field, T value) where T : unmanaged =>
        *(T*)(instance + (nint)InstanceOffset(klass, field)) = value;

    public IntPtr NewString(string value)
    {
        fixed (byte* v = Utf8(value)) return Native.string_new(v);
    }

    public string? ReadString(IntPtr il2cppString) =>
        il2cppString == IntPtr.Zero ? null : new string(Native.string_chars(il2cppString), 0, Native.string_length(il2cppString));

    private static nuint InstanceOffset(IntPtr klass, string field)
    {
        var offset = Native.field_get_offset(Field(klass, field));
        if (offset < ObjectHeaderSize) throw new GameException($"Field '{field}' is not an instance field.");
        return offset;
    }

    public nuint FieldOffset(IntPtr klass, string field) => Native.field_get_offset(Field(klass, field));

    /// <summary>Entries of a Dictionary&lt;string, reference type&gt;.</summary>
    public List<(string Key, IntPtr Value)> ReadStringDictionary(IntPtr dictionary)
    {
        var result = new List<(string, IntPtr)>();
        if (dictionary == IntPtr.Zero) return result;
        var dictClass = Native.object_get_class(dictionary);
        var entries = *(IntPtr*)(dictionary + (nint)FieldOffset(dictClass, "_entries"));
        var count = *(int*)(dictionary + (nint)FieldOffset(dictClass, "_count"));
        if (entries == IntPtr.Zero) return result;
        var entryClass = Native.class_get_element_class(Native.object_get_class(entries));
        uint align;
        var stride = Native.class_value_size(entryClass, &align);
        // Struct field offsets are reported as if boxed, hence the header subtraction.
        var keyOffset = (nint)FieldOffset(entryClass, "key") - ObjectHeaderSize;
        var valueOffset = (nint)FieldOffset(entryClass, "value") - ObjectHeaderSize;
        for (var i = 0; i < count; i++)
        {
            var entry = entries + 0x20 + i * stride;
            var key = *(IntPtr*)(entry + keyOffset);
            if (key != IntPtr.Zero) result.Add((ReadString(key) ?? "", *(IntPtr*)(entry + valueOffset)));
        }
        return result;
    }

    public IntPtr FindField(IntPtr klass, string name) => Field(klass, name);

    /// <summary>Looks a field up on the class or any of its base classes; zero if absent.</summary>
    public static IntPtr TryField(IntPtr klass, string name)
    {
        fixed (byte* n = Utf8(name))
        {
            for (var k = klass; k != IntPtr.Zero; k = Native.class_get_parent(k))
            {
                var field = Native.class_get_field_from_name(k, n);
                if (field != IntPtr.Zero) return field;
            }
        }
        return IntPtr.Zero;
    }

    private static IntPtr Field(IntPtr klass, string name)
    {
        if (klass == IntPtr.Zero) throw new GameException($"Field '{name}' asked of a class that was not found.");
        var field = TryField(klass, name);
        if (field == IntPtr.Zero) throw new GameException($"Field '{name}' not found on {Native.TypeName(Native.class_get_type(klass))}.");
        return field;
    }

    private static byte[] Utf8(string s)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, bytes);
        return bytes;
    }
}

public sealed class GameException(string message) : Exception(message);
