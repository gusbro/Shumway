using Shumway.Compiler.NativeC;

namespace Shumway.Embedding;

public sealed partial class PrologEngine
{
    /// <summary>The native-interop runtime (extracted component) — see
    /// <see cref="NativeRuntime"/>. The engine owns the instance and forwards
    /// the surface below.</summary>
    private readonly NativeRuntime _native = new();

    /// <summary>Binds the class whose <c>public static</c> methods implement the
    /// C functions called from embedded native blocks (ADR-022). Call before
    /// consulting Arity sources that use <c>{...}</c> blocks. Without an explicit
    /// call the engine auto-discovers a class named <c>Shumway.Native.Interop</c>
    /// in the loaded assemblies.</summary>
    public void UseNativeInterop(Type interopClass) => _native.UseNativeInterop(interopClass);

    internal System.Reflection.MethodInfo? ResolveNativeInterop(string name)
        => _native.ResolveNativeInterop(name);

    internal long NativeScratchMark => _native.NativeScratchMark;
    internal void NativeScratchRelease(long mark) => _native.NativeScratchRelease(mark);
    internal IntPtr NativeArenaAlloc(int bytes) => _native.NativeArenaAlloc(bytes);

    internal NativeBlockEntry? NativeBlockByAtomId(int atomId) => _native.NativeBlockByAtomId(atomId);
    internal void AddNativeBlock(string name, NativeVar[] vars, CStmt[] stmts,
        NativeScalarGlobal[] scalarGlobals)
        => _native.AddNativeBlock(name, vars, stmts, scalarGlobals);

    internal void RegisterReftypeGlobals(
        System.Collections.Generic.IReadOnlyList<CDecl> decls)
        => _native.RegisterReftypeGlobals(decls);
    internal NativeBlockEntry? NativeBlock(string name) => _native.NativeBlock(name);

    /// <summary>Text encoding for native <c>char*</c> marshalling (default
    /// UTF-8). Set per engine before running native calls.</summary>
    public System.Text.Encoding NativeTextEncoding
    {
        get => _native.NativeTextEncoding;
        set => _native.NativeTextEncoding = value;
    }

    internal bool IsNativeFunction(string name, int arity) => _native.IsNativeFunction(name, arity);
    internal bool IsNativeFunctionName(string name) => _native.IsNativeFunctionName(name);
    internal void MarkNativeFunction(string name, int arity) => _native.MarkNativeFunction(name, arity);

    internal System.Collections.Generic.IReadOnlyDictionary<string, CType>? NativeTypedefsView
        => _native.NativeTypedefsView;

    /// <summary>Test hook — process-wide count of real native-library loads
    /// (a path is loaded once per process and shared across engines).</summary>
    internal static int NativeLibraryLoadCount => NativeRuntime.NativeLibraryLoadCount;

    /// <summary>Registers a native C library (DLL / .so / .dylib) whose exports
    /// back <c>:- native</c> functions. Loaded once per path for the process.</summary>
    public void UseNativeLibrary(string path) => _native.UseNativeLibrary(path);

    internal NativeReftypeAllocator? NativeAllocator => _native.NativeAllocator;

    internal void RegisterNativePrototypes(
        System.Collections.Generic.IReadOnlyList<CDecl> cDecls)
        => _native.RegisterNativePrototypes(cDecls);

    internal NativeRuntime.NativeResolution ResolveNativeCall(string name, int arity)
        => _native.ResolveNativeCall(name, arity);

    public long GetNativeGlobalInt(string name) => _native.GetNativeGlobalInt(name);
    public void SetNativeGlobalInt(string name, long v) => _native.SetNativeGlobalInt(name, v);
    public double GetNativeGlobalFloat(string name) => _native.GetNativeGlobalFloat(name);
    public void SetNativeGlobalFloat(string name, double v) => _native.SetNativeGlobalFloat(name, v);

    internal TermSlot? ReftypeSlot(string name) => _native.ReftypeSlot(name);
    internal TermSlot GetOrCreateReftypeSlot(string name) => _native.GetOrCreateReftypeSlot(name);

    internal NativeInlineContext? GetNativeInlineContext() => _native.GetNativeInlineContext();
}
