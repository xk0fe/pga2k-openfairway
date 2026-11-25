using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using OpenFairway.Core.Il2Cpp;

namespace OpenFairway.Core;

/// <summary>
/// Developer-only inspection console (off unless "DevConsole": true in config.json). It listens on a local named
/// pipe, never on the network, and runs each command on the game's main thread. Used to learn how the game's own
/// systems behave while it is running, so mods can be written against them. Not for end users.
///
/// Commands (one per line):
///   class &lt;assembly|*&gt; &lt;namespace|-&gt; &lt;Name[.Nested]&gt;   -> class pointer
///   statics &lt;class&gt;                                    -> static fields and values
///   fields &lt;object&gt;                                    -> instance fields and values
///   methods &lt;class&gt; [filter]                           -> method signatures
///   find &lt;class&gt;                                       -> live Unity objects of that class
///   call &lt;class&gt; &lt;method&gt; &lt;argc&gt; &lt;this|null&gt; [args]     -> invoke; args: i:1 l:1 b:1 f:1.5 d:1.5 s:text p:HEX
///   set &lt;object|class&gt; &lt;field&gt; &lt;arg&gt;                    -> write an instance (object) or static (class) field
///   str &lt;ptr&gt; | arr &lt;ptr&gt; | list &lt;ptr&gt; | typeof &lt;ptr&gt;
/// </summary>
internal sealed unsafe class DevConsole(GameRuntime runtime, MainThread mainThread)
{
    private const string PipeName = "openfairway-dev";
    private Games.Recorder? _recorder;

    public void Start() => new Thread(Serve) { IsBackground = true, Name = "OpenFairway.DevConsole" }.Start();

    private void Serve()
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                pipe.WaitForConnection();
                using var reader = new StreamReader(pipe, new UTF8Encoding(false));
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var command = line.Trim();
                    if (command.Length == 0) continue;
                    var done = new ManualResetEventSlim();
                    var output = "";
                    mainThread.Post(() =>
                    {
                        try { output = Execute(command); }
                        catch (Exception e) { output = "ERROR " + e.Message; }
                        done.Set();
                    });
                    if (!done.Wait(TimeSpan.FromSeconds(30))) output = "ERROR timed out waiting for the main thread";
                    writer.WriteLine(output);
                    writer.WriteLine(".");
                }
            }
            catch (IOException) { }
            catch (Exception e) { Log.Warn("Dev console: " + e.Message); Thread.Sleep(1000); }
        }
    }

    private string Execute(string command)
    {
        CalibrateObjectCheck();
        var a = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        switch (a[0])
        {
            case "uiclick":
                return Games.UiScreen.Top?.TestClick(float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture)) ?? "no custom screen open";
            case "action":
                return (a.Length > 2 ? ModLoader.Current?.Invoke(a[1], a[2]) : ModLoader.Current?.Invoke(a[1])) == true ? "ok" : "no such action";
            case "uipad":
                Games.UiScreen.TestPad = a[1] == "off" ? null : int.Parse(a[1]);
                return "ok";
            case "uihover":
                return Games.UiScreen.Top?.TestHover(float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture)) ?? "no custom screen open";
            case "uiback":
                return Games.UiScreen.Top?.TestBack() ?? "no custom screen open";
            case "uiclose":
                return "closed " + Games.UiToolkit.CloseAll();
            case "record":
            {
                // record <folder> [fps] [superSize] | record stop | record status
                _recorder ??= new Games.Recorder(runtime, mainThread);
                if (a.Length < 2 || a[1] == "status") return _recorder.Status();
                if (a[1] == "stop") return _recorder.Stop();
                return _recorder.Start(a[1], a.Length > 2 ? int.Parse(a[2]) : 30, a.Length > 3 ? int.Parse(a[3]) : 1);
            }
            case "shot":
            {
                var capture = runtime.FindMethod(runtime.FindClass("UnityEngine.ScreenCaptureModule", "UnityEngine", "ScreenCapture"), "CaptureScreenshot", 1);
                runtime.Invoke(capture, IntPtr.Zero, runtime.NewString(a[1]));
                return "ok";
            }
            case "invoke":
            {
                // Calls a delegate (e.g. a button's click handler).
                var del = Ptr(a[1]);
                var invoke = runtime.FindMethod(Native.object_get_class(del), "Invoke", a.Length - 2);
                var args = new IntPtr[a.Length - 2];
                for (var i = 0; i < args.Length; i++) args[i] = ParseArg(a[2 + i]).Pointer;
                var result = runtime.Invoke(invoke, del, args);
                return result == IntPtr.Zero ? "ok" : Describe(result);
            }
            case "tree":
            {
                Tree(sb, Ptr(a[1]), 0, a.Length > 2 ? int.Parse(a[2]) : 4, new HashSet<IntPtr>(), a.Length > 3 ? a[3] : "MVC.");
                return sb.ToString();
            }
            case "calibration":
                return "class self-pointer offset 0x" + ClassSelfPointer.ToString("X");
            case "method":
                return Hex(runtime.FindMethod(Ptr(a[1]), a[2]));
            case "paramclass":
                return Hex(runtime.GetParameterClass(runtime.FindMethod(Ptr(a[1]), a[2]), int.Parse(a[3])));
            case "bind":
            {
                // bind <delegate class> <class> <method spec>: a game delegate for one of the game's static methods
                return Describe(runtime.CreateDelegateForMethod(Ptr(a[1]), runtime.FindMethod(Ptr(a[2]), a[3])));
            }
            case "box":
            {
                var (bytes, _) = ParseArg(a[2]);
                fixed (byte* b = bytes) return Hex(runtime.Box(Ptr(a[1]), b));
            }
            case "hier":
            {
                // hier <GameObject|Component> [depth]: the on-screen object tree with component types and text
                Hierarchy(sb, TransformOf(Ptr(a[1])), 0, a.Length > 2 ? int.Parse(a[2]) : 6);
                return sb.ToString();
            }
            case "new":
                return Describe(Native.object_new(Ptr(a[1])));
            case "peek":
            {
                var address = Ptr(a[1]);
                var n = a.Length > 2 ? int.Parse(a[2]) : 8;
                for (var i = 0; i < n; i++)
                {
                    var at = address + i * 8;
                    if (!Readable(at)) { sb.AppendLine($"+{i * 8:X2} (unreadable)"); break; }
                    var v = *(IntPtr*)at;
                    sb.AppendLine($"+{i * 8:X2} {Hex(v),-16} {(IsObject(v) ? Describe(v) : "")}");
                }
                return sb.ToString();
            }
            case "poke":
            {
                // poke <owner object> <address> <arg>: raw write (reference writes go through the GC barrier)
                var owner = Ptr(a[1]);
                var address = Ptr(a[2]);
                var (bytes, pointer) = ParseArg(a[3]);
                if (bytes != null) for (var i = 0; i < bytes.Length; i++) ((byte*)address)[i] = bytes[i];
                else runtime.WriteReference(owner, address, pointer);
                return "ok";
            }
            case "newarr":
            {
                var items = new IntPtr[a.Length - 2];
                for (var i = 0; i < items.Length; i++) items[i] = Ptr(a[2 + i]);
                return Describe(runtime.NewArray(Ptr(a[1]), items));
            }
            case "findall":
            {
                // findall <class> [name filter]: every loaded object of a class, including assets such as textures
                var all = runtime.FindMethod(runtime.FindClass("UnityEngine.CoreModule", "UnityEngine", "Resources"), "FindObjectsOfTypeAll(System.Type)");
                var klass = ClassPtr(a[1]);
                var objects = runtime.ReadArray(runtime.Invoke(all, IntPtr.Zero, Native.type_get_object(Native.class_get_type(klass))));
                var getName = runtime.FindMethod(runtime.FindClass("UnityEngine.CoreModule", "UnityEngine", "Object"), "get_name", 0);
                var getWidth = runtime.FindMethod(klass, "get_width", 0);
                var getHeight = runtime.FindMethod(klass, "get_height", 0);
                var shown = 0;
                foreach (var o in objects)
                {
                    var name = SafeString(runtime.Invoke(getName, o)) ?? "";
                    if (a.Length > 2 && !name.Contains(a[2], StringComparison.OrdinalIgnoreCase)) continue;
                    var size = getWidth != IntPtr.Zero ? $" {*(int*)(runtime.Invoke(getWidth, o) + 16)}x{*(int*)(runtime.Invoke(getHeight, o) + 16)}" : "";
                    if (++shown <= 400) sb.AppendLine($"{Hex(o)}{size} {name}");
                }
                return $"count {objects.Length}, matching {shown}" + Environment.NewLine + sb;
            }
            case "newfunc":
            {
                // newfunc <delegate class> <object>: a no-argument delegate that returns that object
                var result = Ptr(a[2]);
                return Describe(runtime.CreateFunc(Ptr(a[1]), () => result));
            }
            case "newaction":
            {
                // A System.Action that writes a line to the log when the game invokes it.
                var text = a.Length > 1 ? a[1] : "action";
                var action = runtime.CreateDelegate(runtime.FindClass("mscorlib", "System", "Action"), () => Log.Info("[console action] " + text));
                return Describe(action);
            }
            case "class":
                return Hex(runtime.FindClass(a[1] == "*" ? null : a[1], a[2] == "-" ? "" : a[2], a[3]));
            case "typeof":
                return runtime.GetClassName(Native.object_get_class(Ptr(a[1])));
            case "str":
                return SafeString(Ptr(a[1])) ?? "null";
            case "arr":
            {
                // arr <array> [filter]: elements (first 200, or every element whose text contains the filter)
                var items = runtime.ReadArray(Ptr(a[1]));
                var shownItems = 0;
                for (var i = 0; i < items.Length && shownItems < 400; i++)
                {
                    var text = Describe(items[i]);
                    if (a.Length > 2 ? !text.Contains(a[2], StringComparison.OrdinalIgnoreCase) : i >= 200) continue;
                    sb.AppendLine($"[{i}] {text}");
                    shownItems++;
                }
                return $"length {items.Length}" + Environment.NewLine + sb;
            }
            case "typeobj":
                return Hex(Native.type_get_object(Native.class_get_type(Ptr(a[1]))));
            case "list":
            {
                var list = Ptr(a[1]);
                var size = *(int*)(list + 0x18);
                var items = runtime.ReadArray(*(IntPtr*)(list + 0x10));
                for (var i = 0; i < size && i < items.Length && i < 200; i++) sb.AppendLine($"[{i}] {Describe(items[i])}");
                return $"count {size}\n{sb}";
            }
            case "find":
            {
                var objects = runtime.FindObjectsOfType(Ptr(a[1]));
                foreach (var o in objects) sb.AppendLine(Describe(o));
                return $"count {objects.Length}\n{sb}";
            }
            case "statics":
                WriteFields(sb, Ptr(a[1]), IntPtr.Zero, true);
                return sb.ToString();
            case "fields":
            {
                var obj = Ptr(a[1]);
                for (var k = Native.object_get_class(obj); k != IntPtr.Zero; k = Native.class_get_parent(k))
                {
                    sb.AppendLine("-- " + runtime.GetClassName(k));
                    WriteFields(sb, k, obj, false);
                }
                return sb.ToString();
            }
            case "methods":
            {
                IntPtr iter = IntPtr.Zero, m;
                var klass = ClassPtr(a[1]);
                while ((m = Native.class_get_methods(klass, &iter)) != IntPtr.Zero)
                {
                    var name = Native.Str(Native.method_get_name(m));
                    if (a.Length > 2 && !name.Contains(a[2], StringComparison.OrdinalIgnoreCase)) continue;
                    var ps = new List<string>();
                    for (uint p = 0; p < Native.method_get_param_count(m); p++)
                        ps.Add(Native.TypeName(Native.method_get_param(m, p)) + " " + Native.Str(Native.method_get_param_name(m, p)));
                    sb.AppendLine($"{Native.TypeName(Native.method_get_return_type(m))} {name}({string.Join(", ", ps)})");
                }
                return sb.ToString();
            }
            case "set":
            {
                var target = Ptr(a[1]);
                var (bytes, pointer) = ParseArg(a[3]);
                // A class pointer has no object header whose class has fields by this name; try instance first.
                // A class reference ("@...") or anything that is not a live object means a static field.
                var isInstance = a[1][0] != '@' && IsObject(target);
                var field = isInstance ? FindField(Native.object_get_class(target), a[2]) : IntPtr.Zero;
                if (field != IntPtr.Zero && (Native.field_get_flags(field) & 0x10) == 0)
                {
                    var dest = (byte*)(target + (nint)Native.field_get_offset(field));
                    if (bytes != null) for (var i = 0; i < bytes.Length; i++) dest[i] = bytes[i];
                    else runtime.WriteReference(target, (IntPtr)dest, pointer);
                    return "ok (instance)";
                }
                field = FindField(target, a[2]);
                if (field == IntPtr.Zero) return "ERROR field not found";
                if (bytes != null) fixed (byte* b = bytes) Native.field_static_set_value(field, b);
                else Native.field_static_set_value(field, (void*)pointer);
                return "ok (static)";
            }
            case "call":
            {
                var klass = ClassPtr(a[1]);
                var argc = int.Parse(a[3]);
                var method = a[2].Contains('(') ? runtime.FindMethod(klass, a[2]) : runtime.FindMethod(klass, a[2], argc);
                if (method == IntPtr.Zero) return "ERROR method not found";
                var self = a[4] == "null" ? IntPtr.Zero : Ptr(a[4]);
                var args = new IntPtr[argc];
                var pins = new List<GCHandle>();
                try
                {
                    for (var i = 0; i < argc; i++)
                    {
                        var (bytes, pointer) = ParseArg(a[5 + i]);
                        if (bytes == null) { args[i] = pointer; continue; }
                        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                        pins.Add(pin);
                        args[i] = pin.AddrOfPinnedObject();
                    }
                    var result = runtime.Invoke(method, self, args);
                    var returnType = Native.method_get_return_type(method);
                    if (Native.type_get_type(returnType) == 1) return "void";
                    if (result == IntPtr.Zero) return "null";
                    return IsValueType(returnType) ? FormatValue(returnType, (byte*)(result + 16)) : Describe(result);
                }
                finally
                {
                    foreach (var pin in pins) pin.Free();
                }
            }
            default:
                return "ERROR unknown command";
        }
    }

    /// <summary>Recursive dump of an object graph, following references whose type name starts with the given prefix.</summary>
    private void Tree(StringBuilder sb, IntPtr obj, int depth, int maxDepth, HashSet<IntPtr> seen, string prefix)
    {
        var pad = new string(' ', depth * 2);
        if (sb.Length > 200_000) return;
        if (!IsObject(obj)) { sb.AppendLine($"{pad}(bad pointer {Hex(obj)})"); return; }
        if (!seen.Add(obj)) { sb.AppendLine($"{pad}(seen {Hex(obj)})"); return; }
        var objClass = Native.object_get_class(obj);
        sb.AppendLine($"{pad}{Hex(obj)} {runtime.GetClassName(objClass)}");
        for (var k = objClass; k != IntPtr.Zero; k = Native.class_get_parent(k))
        {
            var className = runtime.GetClassName(k);
            if (className is "System.Object" or "UnityEngine.Object") break;
            IntPtr iter = IntPtr.Zero, field;
            while ((field = Native.class_get_fields(k, &iter)) != IntPtr.Zero)
            {
                if ((Native.field_get_flags(field) & 0x50) != 0) continue;
                var type = Native.field_get_type(field);
                var name = Native.Str(Native.field_get_name(field));
                var address = (byte*)(obj + (nint)Native.field_get_offset(field));
                if (IsValueType(type)) { sb.AppendLine($"{pad}  {name} = {FormatValue(type, address)}"); continue; }
                var value = *(IntPtr*)address;
                if (value == IntPtr.Zero) continue;
                if (!IsObject(value)) { sb.AppendLine($"{pad}  {name} = (bad pointer {Hex(value)}, type {Native.TypeName(type)})"); continue; }
                var valueClassName = runtime.GetClassName(Native.object_get_class(value));
                if (valueClassName == "System.String") { sb.AppendLine($"{pad}  {name} = \"{SafeString(value)}\""); continue; }
                if (IsDelegate(value)) { sb.AppendLine($"{pad}  {name} = delegate {Hex(value)} -> {DelegateTarget(value)}"); continue; }
                if (depth >= maxDepth) { sb.AppendLine($"{pad}  {name} = {Hex(value)} {valueClassName}"); continue; }
                if (valueClassName.EndsWith("[]") && !valueClassName.StartsWith("System."))
                {
                    if (Native.class_is_valuetype(Native.class_get_element_class(Native.object_get_class(value))) != 0)
                    {
                        sb.AppendLine($"{pad}  {name} = {valueClassName} (struct array)");
                        continue;
                    }
                    var items = runtime.ReadArray(value);
                    sb.AppendLine($"{pad}  {name} = {valueClassName} length {items.Length}");
                    foreach (var item in items) Child(sb, item, depth, maxDepth, seen, prefix, pad, null);
                }
                else if (valueClassName.StartsWith("System.Collections.Generic.List<") && !valueClassName.StartsWith("System.Collections.Generic.List<System.") && !valueClassName.StartsWith("System.Collections.Generic.List<UnityEngine."))
                {
                    var size = *(int*)(value + 0x18);
                    var items = runtime.ReadArray(*(IntPtr*)(value + 0x10));
                    sb.AppendLine($"{pad}  {name} = {valueClassName} count {size}");
                    for (var i = 0; i < size && i < items.Length; i++) Child(sb, items[i], depth, maxDepth, seen, prefix, pad, null);
                }
                else if (valueClassName.StartsWith("System.Collections.Generic.Dictionary<System.String,") && !valueClassName.Contains("System.Collections.Generic.List<"))
                {
                    var dictClass = Native.object_get_class(value);
                    var entries = *(IntPtr*)(value + (nint)Native.field_get_offset(Native.class_get_field_from_name_managed(dictClass, "_entries")));
                    var count = *(int*)(value + (nint)Native.field_get_offset(Native.class_get_field_from_name_managed(dictClass, "_count")));
                    if (!IsObject(entries)) { sb.AppendLine($"{pad}  {name} = dictionary (empty)"); continue; }
                    var entriesClass = Native.object_get_class(entries);
                    var entryClass = Native.class_get_element_class(entriesClass);
                    uint entryAlign;
                    var stride = Native.class_value_size(entryClass, &entryAlign);
                    var keyField = Native.class_get_field_from_name_managed(entryClass, "key");
                    var valueField = Native.class_get_field_from_name_managed(entryClass, "value");
                    if (keyField == IntPtr.Zero || valueField == IntPtr.Zero || IsValueType(Native.field_get_type(valueField)))
                    {
                        sb.AppendLine($"{pad}  {name} = dictionary count {count} (struct values, keys only)");
                        if (keyField != IntPtr.Zero)
                            for (var i = 0; i < count; i++)
                            {
                                var k2 = *(IntPtr*)(entries + 0x20 + i * stride + (nint)Native.field_get_offset(keyField) - 0x10);
                                if (k2 != IntPtr.Zero) sb.AppendLine($"{pad}    [{SafeString(k2)}]");
                            }
                        continue;
                    }
                    sb.AppendLine($"{pad}  {name} = dictionary count {count}");
                    for (var i = 0; i < count; i++)
                    {
                        var entry = (byte*)(entries + 0x20 + i * stride);
                        var key = *(IntPtr*)(entry + (nint)Native.field_get_offset(keyField) - 0x10);
                        if (key == IntPtr.Zero) continue;
                        Child(sb, *(IntPtr*)(entry + (nint)Native.field_get_offset(valueField) - 0x10), depth, maxDepth, seen, prefix, pad, SafeString(key));
                    }
                }
                else if (valueClassName.StartsWith(prefix))
                {
                    sb.AppendLine($"{pad}  {name}:");
                    Tree(sb, value, depth + 2, maxDepth, seen, prefix);
                }
                else sb.AppendLine($"{pad}  {name} = {Describe(value)}");
            }
        }
    }

    private void Child(StringBuilder sb, IntPtr item, int depth, int maxDepth, HashSet<IntPtr> seen, string prefix, string pad, string? key)
    {
        var label = key == null ? "" : $"[{key}] ";
        if (item == IntPtr.Zero) { if (key != null) sb.AppendLine($"{pad}    {label}null"); return; }
        if (!IsObject(item)) { sb.AppendLine($"{pad}    {label}(bad pointer {Hex(item)})"); return; }
        var itemClass = runtime.GetClassName(Native.object_get_class(item));
        if (itemClass == "System.String") sb.AppendLine($"{pad}    {label}\"{SafeString(item)}\"");
        else if (IsDelegate(item)) sb.AppendLine($"{pad}    {label}delegate {Hex(item)} -> {DelegateTarget(item)}");
        else if (itemClass.StartsWith(prefix))
        {
            if (key != null) sb.AppendLine($"{pad}    {label}");
            Tree(sb, item, depth + 2, maxDepth, seen, prefix);
        }
        else sb.AppendLine($"{pad}    {label}{Describe(item)}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public ushort PartitionId; public nint RegionSize; public uint State, Protect, Type; }

    [DllImport("kernel32")]
    private static extern nint VirtualQuery(IntPtr address, out MemoryInfo info, nint length);

    /// <summary>True if the address is committed, readable memory. Lets a wrong guess show up as text instead of a crash.</summary>
    private static bool Readable(IntPtr address)
    {
        if ((long)address < 0x10000 || ((long)address & 7) != 0) return false;
        if (VirtualQuery(address, out var info, sizeof(MemoryInfo)) == 0) return false;
        const uint commit = 0x1000, noAccess = 0x01, guard = 0x100;
        return info.State == commit && (info.Protect & (noAccess | guard)) == 0;
    }

    /// <summary>True if the pointer looks like a live runtime object (readable, with a readable class).</summary>
    private static bool IsObject(IntPtr obj)
    {
        if (!Readable(obj)) return false;
        var klass = *(IntPtr*)obj;
        // Every runtime class record points back at itself at a fixed slot; random memory does not.
        return Readable(klass) && Readable(klass + ClassSelfPointer) && *(IntPtr*)(klass + ClassSelfPointer) == klass;
    }

    private static bool IsClass(IntPtr klass) =>
        Readable(klass) && Readable(klass + ClassSelfPointer) && *(IntPtr*)(klass + ClassSelfPointer) == klass;

    /// <summary>A class argument, refused unless it really is a class record (calling into the runtime with
    /// anything else crashes the game).</summary>
    private IntPtr ClassPtr(string token)
    {
        var klass = Ptr(token);
        if (!IsClass(klass)) throw new ArgumentException("not a class: " + token);
        return klass;
    }

    private static int ClassSelfPointer = -1;

    /// <summary>Finds the self-pointer slot once, using a class we know is real.</summary>
    private void CalibrateObjectCheck()
    {
        if (ClassSelfPointer >= 0) return;
        var known = runtime.FindClass("mscorlib", "System", "Object");
        for (var offset = 0x40; offset <= 0xC0; offset += 8)
        {
            if (*(IntPtr*)(known + offset) != known) continue;
            var other = runtime.FindClass("mscorlib", "System", "String");
            if (*(IntPtr*)(other + offset) == other) ClassSelfPointer = offset; // keep the last match: earlier slots differ for array classes
        }
        if (ClassSelfPointer < 0) throw new InvalidOperationException("could not calibrate the object check");
    }

    private string? SafeString(IntPtr str)
    {
        if (str == IntPtr.Zero) return null;
        if (!IsObject(str)) return $"(bad string {Hex(str)})";
        var length = Native.string_length(str);
        return length is < 0 or > 100_000 ? $"(bad string {Hex(str)})" : runtime.ReadString(str);
    }

    private bool IsDelegate(IntPtr obj)
    {
        for (var k = Native.object_get_class(obj); k != IntPtr.Zero; k = Native.class_get_parent(k))
            if (runtime.GetClassName(k) == "System.Delegate") return true;
        return false;
    }

    private string DelegateTarget(IntPtr del)
    {
        var method = *(IntPtr*)(del + 0x28);
        if (method == IntPtr.Zero) return "?";
        var target = *(IntPtr*)(del + 0x20);
        return $"{runtime.GetClassName(Native.method_get_class(method))}::{Native.Str(Native.method_get_name(method))} target={Hex(target)}";
    }

    private IntPtr _componentClass, _gameObjectClass, _transformClass, _objectClass, _getComponents, _componentType;

    private IntPtr Call(IntPtr klass, string method, IntPtr self, params IntPtr[] args) =>
        runtime.Invoke(method.Contains('(') ? runtime.FindMethod(klass, method) : runtime.FindMethod(klass, method, args.Length), self, args);

    private void ResolveUnity()
    {
        if (_componentClass != IntPtr.Zero) return;
        _componentClass = runtime.FindClass("UnityEngine.CoreModule", "UnityEngine", "Component");
        _gameObjectClass = runtime.FindClass("UnityEngine.CoreModule", "UnityEngine", "GameObject");
        _transformClass = runtime.FindClass("UnityEngine.CoreModule", "UnityEngine", "Transform");
        _objectClass = runtime.FindClass("UnityEngine.CoreModule", "UnityEngine", "Object");
        _getComponents = runtime.FindMethod(_gameObjectClass, "GetComponents(System.Type)");
        _componentType = Native.type_get_object(Native.class_get_type(_componentClass));
    }

    private bool IsA(IntPtr obj, IntPtr klass)
    {
        for (var k = Native.object_get_class(obj); k != IntPtr.Zero; k = Native.class_get_parent(k)) if (k == klass) return true;
        return false;
    }

    private IntPtr TransformOf(IntPtr obj)
    {
        ResolveUnity();
        if (IsA(obj, _transformClass)) return obj;
        return IsA(obj, _gameObjectClass) ? Call(_gameObjectClass, "get_transform", obj) : Call(_componentClass, "get_transform", obj);
    }

    private void Hierarchy(StringBuilder sb, IntPtr transform, int depth, int maxDepth)
    {
        if (sb.Length > 300_000) return;
        var go = Call(_componentClass, "get_gameObject", transform);
        var name = SafeString(Call(_objectClass, "get_name", go));
        var active = *(byte*)(Call(_gameObjectClass, "get_activeSelf", go) + 16) != 0;
        var parts = new List<string>();
        foreach (var component in runtime.ReadArray(runtime.Invoke(_getComponents, go, _componentType)))
        {
            if (component == IntPtr.Zero) continue;
            var componentClass = Native.object_get_class(component);
            var typeName = runtime.GetClassName(componentClass);
            if (typeName is "UnityEngine.RectTransform" or "UnityEngine.Transform" or "UnityEngine.CanvasRenderer") continue;
            var shortName = typeName[(typeName.LastIndexOf('.') + 1)..];
            var getText = runtime.FindMethod(componentClass, "get_text", 0);
            if (getText != IntPtr.Zero && (typeName.Contains("Text") || typeName.Contains("TMP")))
                shortName += $"(\"{SafeString(runtime.Invoke(getText, component))}\")";
            parts.Add($"{shortName}@{Hex(component)}");
        }
        sb.AppendLine($"{new string(' ', depth * 2)}{name}{(active ? "" : " [off]")} go={Hex(go)} {string.Join(" ", parts)}");
        if (depth >= maxDepth) return;
        var count = *(int*)(Call(_transformClass, "get_childCount", transform) + 16);
        for (var i = 0; i < count; i++)
        {
            var index = i;
            Hierarchy(sb, Call(_transformClass, "GetChild", transform, (IntPtr)(&index)), depth + 1, maxDepth);
        }
    }

    private void WriteFields(StringBuilder sb, IntPtr klass, IntPtr instance, bool wantStatic)
    {
        IntPtr iter = IntPtr.Zero, field;
        var buffer = stackalloc byte[256];
        while ((field = Native.class_get_fields(klass, &iter)) != IntPtr.Zero)
        {
            var flags = Native.field_get_flags(field);
            if ((flags & 0x40) != 0 || ((flags & 0x10) != 0) != wantStatic) continue;
            var type = Native.field_get_type(field);
            string value;
            if (wantStatic)
            {
                uint align;
                var size = IsValueType(type) ? Native.class_value_size(Native.class_from_type(type), &align) : IntPtr.Size;
                if (size > 256) { value = "(large struct)"; }
                else
                {
                    Native.field_static_get_value(field, buffer);
                    value = IsValueType(type) ? FormatValue(type, buffer) : Describe(*(IntPtr*)buffer);
                }
            }
            else
            {
                var address = (byte*)(instance + (nint)Native.field_get_offset(field));
                value = IsValueType(type) ? FormatValue(type, address) : Describe(*(IntPtr*)address);
            }
            sb.AppendLine($"{Native.TypeName(type)} {Native.Str(Native.field_get_name(field))} = {value}");
        }
    }

    private static bool IsValueType(IntPtr type)
    {
        var t = Native.type_get_type(type);
        if (t is >= 2 and <= 13 or 0x18 or 0x19 or 0x11 or 0x0f) return true;
        return t == 0x15 && Native.class_is_valuetype(Native.class_from_type(type)) != 0;
    }

    private static string FormatValue(IntPtr type, byte* p)
    {
        switch (Native.type_get_type(type))
        {
            case 2: return (*p != 0).ToString();
            case 3: return ((char)*(ushort*)p).ToString();
            case 4: return (*(sbyte*)p).ToString();
            case 5: return (*p).ToString();
            case 6: return (*(short*)p).ToString();
            case 7: return (*(ushort*)p).ToString();
            case 8: return (*(int*)p).ToString();
            case 9: return (*(uint*)p).ToString();
            case 10: return (*(long*)p).ToString();
            case 11: return (*(ulong*)p).ToString();
            case 12: return (*(float*)p).ToString(CultureInfo.InvariantCulture);
            case 13: return (*(double*)p).ToString(CultureInfo.InvariantCulture);
            case 0x0f or 0x18 or 0x19: return Hex(*(IntPtr*)p);
        }
        var klass = Native.class_from_type(type);
        if (Native.class_is_enum(klass) != 0) return (*(int*)p).ToString() + " (enum)";
        uint align;
        var size = Math.Min(Native.class_value_size(klass, &align), 64);
        var hex = new StringBuilder("struct ");
        for (var i = 0; i < size; i++) hex.Append(p[i].ToString("X2"));
        return hex.ToString();
    }

    private string Describe(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return "null";
        if (!IsObject(obj)) return $"(bad pointer {Hex(obj)})";
        var klass = Native.object_get_class(obj);
        var name = runtime.GetClassName(klass);
        if (name == "System.String") return $"\"{SafeString(obj)}\"";
        if (Native.class_is_valuetype(klass) != 0) return $"boxed {name} {FormatValue(Native.class_get_type(klass), (byte*)(obj + 16))}";
        return $"{Hex(obj)} {name}";
    }

    private static IntPtr FindField(IntPtr klass, string name)
    {
        for (var k = klass; k != IntPtr.Zero; k = Native.class_get_parent(k))
        {
            var field = Native.class_get_field_from_name_managed(k, name);
            if (field != IntPtr.Zero) return field;
        }
        return IntPtr.Zero;
    }

    /// <summary>Returns raw bytes for value arguments, or a pointer for reference arguments.</summary>
    private (byte[]? Bytes, IntPtr Pointer) ParseArg(string arg)
    {
        var value = arg[2..];
        switch (arg[0])
        {
            case 'i': return (BitConverter.GetBytes(int.Parse(value)), IntPtr.Zero);
            case 'l': return (BitConverter.GetBytes(long.Parse(value)), IntPtr.Zero);
            case 'b': return (new[] { (byte)(value is "1" or "true" ? 1 : 0) }, IntPtr.Zero);
            case 'f': return (BitConverter.GetBytes(float.Parse(value, CultureInfo.InvariantCulture)), IntPtr.Zero);
            case 'd': return (BitConverter.GetBytes(double.Parse(value, CultureInfo.InvariantCulture)), IntPtr.Zero);
            case 's': return (null, runtime.NewString(value.Replace("%20", " ")));
            case 'p': return (null, value == "0" ? IntPtr.Zero : Ptr(value));
            default: throw new ArgumentException("bad argument " + arg);
        }
    }

    /// <summary>A hex pointer, or an inline class reference "@assembly|namespace|Name" ("-" for the empty namespace).</summary>
    private IntPtr Ptr(string token)
    {
        if (token[0] != '@') return (IntPtr)long.Parse(token, NumberStyles.HexNumber);
        var parts = token[1..].Split('|');
        var klass = runtime.FindClass(parts[0] == "*" ? null : parts[0], parts[1] == "-" ? "" : parts[1], parts[2]);
        if (klass == IntPtr.Zero) throw new ArgumentException("class not found: " + token);
        return klass;
    }
    private static string Hex(IntPtr p) => ((long)p).ToString("X");
}
