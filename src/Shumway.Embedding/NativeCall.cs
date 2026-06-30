using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Shumway.Compiler.NativeC;

namespace Shumway.Embedding;

/// <summary>ADR-024 — the P/Invoke side of the materializer tier. A function declared
/// <c>:- native</c> that does <b>not</b> resolve to a C# interop method is a real
/// native C function exported by a registered native library
/// (<see cref="PrologEngine.UseNativeLibrary"/>): its reftype arguments are
/// materialized into native <c>t_reftype</c> memory (<see cref="NativeReftype"/>),
/// the function is invoked by pointer, and the (possibly modified) structs are
/// dematerialized back. The <c>:- c</c> prototype gives the marshalling signature.
///
/// <para>First cut: scalar (int/short/long/double) and reftype parameters by value,
/// and a scalar/void return. A native function may read the struct and modify its
/// scalar fields in place; allocating new sub-nodes (e.g. building a list) needs a
/// shared allocator and is a follow-up. The <c>calli</c> invoker is JIT-only (not
/// Native AOT).</para></summary>
internal static class NativeCall
{
    /// <summary>How a native parameter is marshalled.</summary>
    internal enum Kind
    {
        Scalar,     // by value (int/short/long/double/float)
        Reftype,    // a materialized t_reftype* (IntPtr)
        OutScalar,  // a pointer to a scalar (`&local`, e.g. `short* ptype`) — allocate,
                    // pass the pointer, read the scalar back into the block-local.
        StringIn,   // a `char*` input — a Prolog string materialized to native memory,
                    // passed, then freed.
        OutString,  // a `char**` output — pass a pointer to a char* cell; the native
                    // function writes a (borrowed) char* into it; read + decode the
                    // string into the block-local, free the cell (not the string).
    }

    /// <summary>The resolved, cached marshalling signature of a native prototype:
    /// the CLR types <c>calli</c> uses (a pointer for reftype / out-scalar), each
    /// parameter's <see cref="Kind"/>, and — for an out-scalar — its element type.</summary>
    internal sealed class Signature
    {
        public required Type ReturnType { get; init; }
        public required Type[] ParamClrTypes { get; init; }   // calli types
        public required Kind[] ParamKinds { get; init; }
        public required Type[] ParamElemTypes { get; init; }  // OutScalar: the scalar's CLR type
        public required Func<IntPtr, object?[], object?> Invoker { get; init; }
    }

    /// <summary>Builds a marshalling signature from a parsed <c>:- c</c> prototype
    /// (typedefs resolved). Throws if a type is outside the supported set.</summary>
    public static Signature FromPrototype(CPrototype proto, IReadOnlyDictionary<string, CType> typedefs)
    {
        Type ret = MapReturn(Resolve(proto.ReturnType, typedefs));
        int n = proto.Params.Count;
        var clr = new Type[n];
        var kinds = new Kind[n];
        var elems = new Type[n];
        for (int i = 0; i < n; i++)
        {
            var (kind, calli, elem) = MapParam(Resolve(proto.Params[i].Type, typedefs), proto.Name, proto.Params[i].Type);
            clr[i] = calli;
            kinds[i] = kind;
            elems[i] = elem;
        }
        return new Signature
        {
            ReturnType = ret,
            ParamClrTypes = clr,
            ParamKinds = kinds,
            ParamElemTypes = elems,
            Invoker = BuildInvoker(ret, clr),
        };
    }

    private static CType Resolve(CType t, IReadOnlyDictionary<string, CType> typedefs)
    {
        // Follow typedef chains (bounded) and accumulate pointer depth.
        int depth = t.PointerDepth;
        string name = t.Name;
        for (int guard = 0; guard < 16 && typedefs.TryGetValue(name, out var u); guard++)
        {
            depth += u.PointerDepth;
            name = u.Name;
        }
        return new CType(name, depth);
    }

    private static (Kind Kind, Type Calli, Type Elem) MapParam(CType t, string fn, CType raw)
    {
        if (t.Name is "reftype" or "t_reftype" or "preftype")
            return (Kind.Reftype, typeof(IntPtr), typeof(IntPtr));   // materialized t_reftype*
        if (t.PointerDepth == 1 && t.Name is "char" or "uchar" or "schar")
            return (Kind.StringIn, typeof(IntPtr), typeof(IntPtr));   // `char*` input string
        if (t.PointerDepth == 2 && t.Name is "char" or "uchar" or "schar")
            return (Kind.OutString, typeof(IntPtr), typeof(string));  // `char**` out-string
        if (t.PointerDepth == 1 && TryScalar(t.Name, out var elem))
            return (Kind.OutScalar, typeof(IntPtr), elem);           // `&local` out-scalar (short*/int*/…)
        if (t.PointerDepth > 0)
            throw new InvalidOperationException(
                $":- native '{fn}': unsupported pointer parameter '{raw}' (reftype, char*, char**, "
                + "by-value scalars and scalar out-pointers are supported; deeper pointers are a follow-up).");
        if (TryScalar(t.Name, out var st))
            return (Kind.Scalar, st, st);
        throw new InvalidOperationException(
            $":- native '{fn}': unsupported parameter type '{raw}' (supported: int/short/long/double/float, "
            + "reftype, and scalar out-pointers).");
    }

    private static bool TryScalar(string name, out Type clr)
    {
        clr = name switch
        {
            "int" or "signed" or "unsigned" => typeof(int),
            "short" => typeof(short),
            "long" or "int64_t" or "int64" => typeof(long),
            "double" => typeof(double),
            "float" => typeof(float),
            _ => typeof(void),
        };
        return clr != typeof(void);
    }

    private static Type MapReturn(CType t)
    {
        if (t.Name == "void" && t.PointerDepth == 0) return typeof(void);
        if (t.PointerDepth > 0) return typeof(IntPtr);
        return t.Name switch
        {
            "int" or "short" or "signed" or "unsigned" => typeof(int),
            "long" or "int64_t" or "int64" => typeof(long),
            "double" => typeof(double),
            "float" => typeof(float),
            _ => typeof(int),   // default
        };
    }

    // Emits `object? Invoke(IntPtr fn, object[] args)` doing a cdecl calli with the
    // native signature. Args are boxed; each is unboxed to its CLR param type, the
    // function pointer pushed, calli, the result boxed (null for void).
    private static Func<IntPtr, object?[], object?> BuildInvoker(Type retType, Type[] paramTypes)
    {
        var dm = new DynamicMethod(
            "shumway_native_calli", typeof(object),
            new[] { typeof(IntPtr), typeof(object[]) },
            typeof(NativeCall).Module, skipVisibility: true);
        var il = dm.GetILGenerator();
        for (int i = 0; i < paramTypes.Length; i++)
        {
            il.Emit(OpCodes.Ldarg_1);              // object[] args
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Ldelem_Ref);           // args[i] (boxed)
            il.Emit(OpCodes.Unbox_Any, paramTypes[i]);
        }
        il.Emit(OpCodes.Ldarg_0);                  // function pointer
        il.EmitCalli(OpCodes.Calli, CallingConvention.Cdecl, retType, paramTypes);
        if (retType == typeof(void))
            il.Emit(OpCodes.Ldnull);
        else
            il.Emit(OpCodes.Box, retType);
        il.Emit(OpCodes.Ret);
        return dm.CreateDelegate<Func<IntPtr, object?[], object?>>();
    }
}
