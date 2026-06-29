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
    /// <summary>The resolved, cached marshalling signature of a native prototype:
    /// the CLR types <c>calli</c> uses, and which parameters are reftype (so the
    /// call site materializes them).</summary>
    internal sealed class Signature
    {
        public required Type ReturnType { get; init; }
        public required Type[] ParamClrTypes { get; init; }
        public required bool[] ParamIsReftype { get; init; }
        public required Func<IntPtr, object?[], object?> Invoker { get; init; }
    }

    /// <summary>Builds a marshalling signature from a parsed <c>:- c</c> prototype
    /// (typedefs resolved). Throws if a type is outside the first-cut set.</summary>
    public static Signature FromPrototype(CPrototype proto, IReadOnlyDictionary<string, CType> typedefs)
    {
        Type ret = MapReturn(Resolve(proto.ReturnType, typedefs));
        var clr = new Type[proto.Params.Count];
        var isRef = new bool[proto.Params.Count];
        for (int i = 0; i < proto.Params.Count; i++)
        {
            var (reftype, t) = MapParam(Resolve(proto.Params[i].Type, typedefs), proto.Name, proto.Params[i].Type);
            clr[i] = t;
            isRef[i] = reftype;
        }
        return new Signature
        {
            ReturnType = ret,
            ParamClrTypes = clr,
            ParamIsReftype = isRef,
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

    private static (bool IsReftype, Type Clr) MapParam(CType t, string fn, CType raw)
    {
        if (t.Name is "reftype" or "t_reftype" or "preftype")
            return (true, typeof(IntPtr));               // the materialized t_reftype* pointer
        if (t.PointerDepth > 0)
            throw new InvalidOperationException(
                $":- native '{fn}': unsupported pointer parameter '{raw}' (char* and out-scalar "
                + "pointer params are a follow-up; reftype and by-value scalars are supported).");
        return (false, t.Name switch
        {
            "int" or "signed" or "unsigned" => typeof(int),
            "short" => typeof(short),
            "long" or "int64_t" or "int64" => typeof(long),
            "double" => typeof(double),
            "float" => typeof(float),
            _ => throw new InvalidOperationException(
                $":- native '{fn}': unsupported parameter type '{raw}' (first-cut native interop supports "
                + "int/short/long/double/float and reftype)."),
        });
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
