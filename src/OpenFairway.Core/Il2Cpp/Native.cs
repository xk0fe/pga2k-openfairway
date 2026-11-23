using System;
using System.Runtime.InteropServices;

namespace OpenFairway.Core.Il2Cpp;

/// <summary>
/// Bindings to the IL2CPP embedding API that GameAssembly.dll exports. This is the only way the platform
/// talks to the game: no file on disk is read for code or type data, and nothing is patched.
/// </summary>
internal static unsafe class Native
{
    public static IntPtr Module { get; private set; }

    public static delegate* unmanaged[Cdecl]<IntPtr> get_corlib;
    public static delegate* unmanaged[Cdecl]<IntPtr> domain_get;
    public static delegate* unmanaged[Cdecl]<IntPtr, nuint*, IntPtr*> domain_get_assemblies;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> thread_attach;
    public static delegate* unmanaged[Cdecl]<IntPtr, void> thread_detach;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> assembly_get_image;
    public static delegate* unmanaged[Cdecl]<IntPtr, byte*> image_get_name;
    public static delegate* unmanaged[Cdecl]<IntPtr, nuint> image_get_class_count;
    public static delegate* unmanaged[Cdecl]<IntPtr, nuint, IntPtr> image_get_class;

    public static delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, IntPtr> class_from_name;
    public static delegate* unmanaged[Cdecl]<IntPtr, byte*> class_get_name;
    public static delegate* unmanaged[Cdecl]<IntPtr, byte*> class_get_namespace;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> class_get_parent;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> class_get_declaring_type;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> class_get_type;
    public static delegate* unmanaged[Cdecl]<IntPtr, int> class_get_flags;
    public static delegate* unmanaged[Cdecl]<IntPtr, byte> class_is_valuetype;
    public static delegate* unmanaged[Cdecl]<IntPtr, byte> class_is_enum;
    public static delegate* unmanaged[Cdecl]<IntPtr, byte> class_is_interface;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, IntPtr> class_get_fields;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, IntPtr> class_get_methods;
    public static delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr> class_get_field_from_name;
    public static delegate* unmanaged[Cdecl]<IntPtr, byte*, int, IntPtr> class_get_method_from_name;

    public static delegate* unmanaged[Cdecl]<IntPtr, byte*> field_get_name;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> field_get_type;
    public static delegate* unmanaged[Cdecl]<IntPtr, nuint> field_get_offset;
    public static delegate* unmanaged[Cdecl]<IntPtr, int> field_get_flags;
    public static delegate* unmanaged[Cdecl]<IntPtr, void*, void> field_static_get_value;
    public static delegate* unmanaged[Cdecl]<IntPtr, void*, void> field_static_set_value;

    public static delegate* unmanaged[Cdecl]<IntPtr, byte*> method_get_name;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> method_get_return_type;
    public static delegate* unmanaged[Cdecl]<IntPtr, uint> method_get_param_count;
    public static delegate* unmanaged[Cdecl]<IntPtr, uint, IntPtr> method_get_param;
    public static delegate* unmanaged[Cdecl]<IntPtr, uint, byte*> method_get_param_name;
    public static delegate* unmanaged[Cdecl]<IntPtr, uint*, uint> method_get_flags;

    public static delegate* unmanaged[Cdecl]<IntPtr, byte*> type_get_name;
    public static delegate* unmanaged[Cdecl]<void*, void> free;

    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr*, IntPtr*, IntPtr> runtime_invoke;
    public static delegate* unmanaged[Cdecl]<byte*, IntPtr> string_new;
    public static delegate* unmanaged[Cdecl]<IntPtr, char*> string_chars;
    public static delegate* unmanaged[Cdecl]<IntPtr, int> string_length;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> object_get_class;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> type_get_object;
    public static delegate* unmanaged[Cdecl]<IntPtr, int> type_get_type;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, IntPtr> class_get_nested_types;
    public static delegate* unmanaged[Cdecl]<IntPtr, uint*, int> class_value_size;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> class_get_element_class;
    public static delegate* unmanaged[Cdecl]<IntPtr, int> class_array_element_size;
    public static delegate* unmanaged[Cdecl]<IntPtr, nuint, IntPtr> array_new;
    public static delegate* unmanaged[Cdecl]<IntPtr, void*, IntPtr, void> gc_wbarrier_set_field;
    public static delegate* unmanaged[Cdecl]<IntPtr, byte, uint> gchandle_new;
    public static delegate* unmanaged[Cdecl]<IntPtr, void*, IntPtr> value_box;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr> method_get_object;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> method_get_class;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> object_new;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> class_from_type;

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);

    public static bool Init()
    {
        Module = GetModuleHandleW("GameAssembly.dll");
        if (Module == IntPtr.Zero) return false;
        try
        {
            get_corlib = (delegate* unmanaged[Cdecl]<IntPtr>)E("il2cpp_get_corlib");
            domain_get = (delegate* unmanaged[Cdecl]<IntPtr>)E("il2cpp_domain_get");
            domain_get_assemblies = (delegate* unmanaged[Cdecl]<IntPtr, nuint*, IntPtr*>)E("il2cpp_domain_get_assemblies");
            thread_attach = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)E("il2cpp_thread_attach");
            thread_detach = (delegate* unmanaged[Cdecl]<IntPtr, void>)E("il2cpp_thread_detach");
            assembly_get_image = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)E("il2cpp_assembly_get_image");
            image_get_name = (delegate* unmanaged[Cdecl]<IntPtr, byte*>)E("il2cpp_image_get_name");
            image_get_class_count = (delegate* unmanaged[Cdecl]<IntPtr, nuint>)E("il2cpp_image_get_class_count");
            image_get_class = (delegate* unmanaged[Cdecl]<IntPtr, nuint, IntPtr>)E("il2cpp_image_get_class");

            class_from_name = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, IntPtr>)E("il2cpp_class_from_name");
            class_get_name = (delegate* unmanaged[Cdecl]<IntPtr, byte*>)E("il2cpp_class_get_name");
            class_get_namespace = (delegate* unmanaged[Cdecl]<IntPtr, byte*>)E("il2cpp_class_get_namespace");
            class_get_parent = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)E("il2cpp_class_get_parent");
            class_get_declaring_type = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)E("il2cpp_class_get_declaring_type");
            class_get_type = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)E("il2cpp_class_get_type");
            class_get_flags = (delegate* unmanaged[Cdecl]<IntPtr, int>)E("il2cpp_class_get_flags");
            class_is_valuetype = (delegate* unmanaged[Cdecl]<IntPtr, byte>)E("il2cpp_class_is_valuetype");
            class_is_enum = (delegate* unmanaged[Cdecl]<IntPtr, byte>)E("il2cpp_class_is_enum");
            class_is_interface = (delegate* unmanaged[Cdecl]<IntPtr, byte>)E("il2cpp_class_is_interface");
            class_get_fields = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, IntPtr>)E("il2cpp_class_get_fields");
            class_get_methods = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, IntPtr>)E("il2cpp_class_get_methods");
            class_get_field_from_name = (delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr>)E("il2cpp_class_get_field_from_name");
            class_get_method_from_name = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int, IntPtr>)E("il2cpp_class_get_method_from_name");

            field_get_name = (delegate* unmanaged[Cdecl]<IntPtr, byte*>)E("il2cpp_field_get_name");
            field_get_type = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)E("il2cpp_field_get_type");
            field_get_offset = (delegate* unmanaged[Cdecl]<IntPtr, nuint>)E("il2cpp_field_get_offset");
            field_get_flags = (delegate* unmanaged[Cdecl]<IntPtr, int>)E("il2cpp_field_get_flags");
            field_static_get_value = (delegate* unmanaged[Cdecl]<IntPtr, void*, void>)E("il2cpp_field_static_get_value");
            field_static_set_value = (delegate* unmanaged[Cdecl]<IntPtr, void*, void>)E("il2cpp_field_static_set_value");

            method_get_name = (delegate* unmanaged[Cdecl]<IntPtr, byte*>)E("il2cpp_method_get_name");
            method_get_return_type = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)E("il2cpp_method_get_return_type");
            method_get_param_count = (delegate* unmanaged[Cdecl]<IntPtr, uint>)E("il2cpp_method_get_param_count");
            method_get_param = (delegate* unmanaged[Cdecl]<IntPtr, uint, IntPtr>)E("il2cpp_method_get_param");
            method_get_param_name = (delegate* unmanaged[Cdecl]<IntPtr, uint, byte*>)E("il2cpp_method_get_param_name");
            method_get_flags = (delegate* unmanaged[Cdecl]<IntPtr, uint*, uint>)E("il2cpp_method_get_flags");

            type_get_name = (delegate* unmanaged[Cdecl]<IntPtr, byte*>)E("il2cpp_type_get_name");
            free = (delegate* unmanaged[Cdecl]<void*, void>)E("il2cpp_free");

            runtime_invoke = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr*, IntPtr*, IntPtr>)E("il2cpp_runtime_invoke");
            string_new = (delegate* unmanaged[Cdecl]<byte*, IntPtr>)E("il2cpp_string_new");
            string_chars = (delegate* unmanaged[Cdecl]<IntPtr, char*>)E("il2cpp_string_chars");
            string_length = (delegate* unmanaged[Cdecl]<IntPtr, int>)E("il2cpp_string_length");
            object_get_class = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)E("il2cpp_object_get_class");
            type_get_object = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)E("il2cpp_type_get_object");
            type_get_type = (delegate* unmanaged[Cdecl]<IntPtr, int>)E("il2cpp_type_get_type");
            class_get_nested_types = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, IntPtr>)E("il2cpp_class_get_nested_types");
            class_value_size = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)E("il2cpp_class_value_size");
            class_get_element_class = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)E("il2cpp_class_get_element_class");
            class_array_element_size = (delegate* unmanaged[Cdecl]<IntPtr, int>)E("il2cpp_class_array_element_size");
            array_new = (delegate* unmanaged[Cdecl]<IntPtr, nuint, IntPtr>)E("il2cpp_array_new");
            gc_wbarrier_set_field = (delegate* unmanaged[Cdecl]<IntPtr, void*, IntPtr, void>)E("il2cpp_gc_wbarrier_set_field");
            gchandle_new = (delegate* unmanaged[Cdecl]<IntPtr, byte, uint>)E("il2cpp_gchandle_new");
            value_box = (delegate* unmanaged[Cdecl]<IntPtr, void*, IntPtr>)E("il2cpp_value_box");
            method_get_object = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)E("il2cpp_method_get_object");
            method_get_class = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)E("il2cpp_method_get_class");
            object_new = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)E("il2cpp_object_new");
            class_from_type = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)E("il2cpp_class_from_il2cpp_type");
            return true;
        }
        catch (EntryPointNotFoundException e)
        {
            Log.Error("Missing IL2CPP export: " + e.Message);
            return false;
        }
    }

    private static IntPtr E(string name) => NativeLibrary.GetExport(Module, name);

    public static IntPtr class_get_field_from_name_managed(IntPtr klass, string name)
    {
        var bytes = new byte[System.Text.Encoding.UTF8.GetByteCount(name) + 1];
        System.Text.Encoding.UTF8.GetBytes(name, bytes);
        fixed (byte* n = bytes) return class_get_field_from_name(klass, n);
    }

    public static string Str(byte* utf8) => utf8 == null ? "" : Marshal.PtrToStringUTF8((IntPtr)utf8) ?? "";

    public static string TypeName(IntPtr type)
    {
        if (type == IntPtr.Zero) return "?";
        var raw = type_get_name(type);
        var name = Str(raw);
        if (raw != null) free(raw);
        return name;
    }
}
