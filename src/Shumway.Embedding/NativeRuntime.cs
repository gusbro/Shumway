using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;

namespace Shumway.Embedding;

/// <summary>
/// The native-interop runtime (extracted component): the ADR-022 interop
/// class binding, embedded native blocks, the per-engine native ARENA
/// (chunked bump allocator with mark/restore), loaded native libraries and
/// their prototypes, the per-functor call-resolution cache, native globals,
/// reftype slots and the native-inline context. Owned by a PrologEngine,
/// which forwards its public/internal surface; access is serialized by the
/// owning engine's contract.
/// </summary>
internal sealed class NativeRuntime
{

    // ADR-022 — the interop class whose public static methods back the C
    // functions called from embedded native `{...}` blocks. Defaults to
    // auto-discovering `Shumway.Native.Interop` across the loaded assemblies;
    // UseNativeInterop overrides it with an explicit class.
    private Dictionary<string, System.Reflection.MethodInfo>? _nativeInterop;

    /// <summary>Binds the class whose <c>public static</c> methods implement the
    /// C functions called from embedded native blocks (ADR-022). Call before
    /// consulting Arity sources that use <c>{...}</c> blocks. Without an explicit
    /// call the engine auto-discovers a class named <c>Shumway.Native.Interop</c>
    /// in the loaded assemblies.</summary>
    public void UseNativeInterop(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)]
        Type interopClass)
    {
        ArgumentNullException.ThrowIfNull(interopClass);
        _nativeInterop = interopClass
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .GroupBy(m => m.Name)
            .ToDictionary(g => g.Key, g => g.First());   // C has no overloading
    }

    /// <summary>Resolves a native-block C function name to its implementing
    /// method, auto-discovering <c>Shumway.Native.Interop</c> on first use if
    /// <see cref="UseNativeInterop"/> was never called. Returns null when no such
    /// method exists.</summary>
    internal System.Reflection.MethodInfo? ResolveNativeInterop(string name)
    {
        if (_nativeInterop is null)
        {
            AutoDiscoverInterop();
            _nativeInterop ??= new Dictionary<string, System.Reflection.MethodInfo>();
        }
        return _nativeInterop.TryGetValue(name, out var m) ? m : null;
    }

    /// <summary>Looks for a class named <c>Shumway.Native.Interop</c> across the
    /// loaded assemblies. Discovery by name cannot be made trim-safe — nothing
    /// statically references the class, so a trimmed build may not contain it.
    /// An application that relies on auto-discovery under trimming must root the
    /// class itself, or call <see cref="UseNativeInterop"/> explicitly, which is
    /// annotated and trim-safe.</summary>
    /// <para>Suppressed rather than propagated: the lookup DEGRADES, it does not
    /// break. If trimming removed the class, no assembly reports it, the map stays
    /// empty and every native-block call reports an unresolved function — the same
    /// outcome as a program that never defined one. Propagating
    /// <c>RequiresUnreferencedCode</c> from here would mark consult itself as
    /// trim-unsafe for every program, including the vast majority that use no
    /// native blocks at all.</para>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Auto-discovery by type name degrades to 'no interop class' "
        + "when trimmed; callers that need it under trimming call UseNativeInterop "
        + "explicitly, which is annotated and trim-safe.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "The discovered type is handed straight to UseNativeInterop, "
        + "whose parameter is annotated; if trimming removed its methods the map "
        + "simply comes back empty, which is the same degradation.")]
    private void AutoDiscoverInterop()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? t = null;
            try { t = asm.GetType("Shumway.Native.Interop"); } catch { }
            if (t is not null) { UseNativeInterop(t); break; }
        }
    }

    // ADR-022 item 1 — per-engine table of embedded native blocks, keyed by a
    // stable name. The `'$native_goal'(Text)` capture is rewritten to
    // `'$native_run'('$nb$…', V1..Vk)`; the `$native_run` builtin looks the block
    // up here by name (portable cross-process — the bytecode references the name,
    // not a synthesized-builtin id) and runs it. Populated at consult (in-process)
    // and at bundle load (from the serialized table).
    private readonly Dictionary<string, NativeBlockEntry> _nativeBlocks = new();




    // the per-engine NATIVE ARENA: a chunked unmanaged bump
    // allocator with mark/restore, serving every call-scoped native buffer a
    // `:- native` P/Invoke needs — out-scalar/out-string cells (D4), and now
    // (D1) the whole t_reftype graph: nodes, pars arrays and char* buffers.
    // The D1 measurement showed AllocHGlobal/FreeHGlobal was 96% of the
    // reftype marshal cost on a 50-element list (mat+free 92.9 of 96.7 µs
    // roundtrip); with the arena, Materialize bump-allocates and the entire
    // release is a mark restore — no graph walk, no per-node free.
    //
    // Safety contract (same as the recorded-allocations mode this replaces):
    // release frees exactly the memory WE allocated for the call — a native
    // function that swapped a cstr/pars with its own allocator leaves our
    // (now unlinked) block to die with the mark and its foreign pointer is
    // never touched. Nested native calls compose via mark/restore; the
    // engine is single-threaded. Chunks are engine-lifetime (high-watermark
    // retention, like the WAM heap); an oversize request gets its own chunk.
    private readonly System.Collections.Generic.List<IntPtr> _nativeArenaChunks = new();
    private readonly System.Collections.Generic.List<int> _nativeArenaSizes = new();
    private int _nativeArenaChunk;
    private int _nativeArenaOffset;
    private const int NativeArenaChunkSize = 64 * 1024;

    internal long NativeScratchMark => ((long)_nativeArenaChunk << 32) | (uint)_nativeArenaOffset;

    internal void NativeScratchRelease(long mark)
    {
        _nativeArenaChunk = (int)(mark >> 32);
        _nativeArenaOffset = (int)(uint)mark;
    }

    /// <summary>Bump-allocates <paramref name="bytes"/> (8-byte aligned) of
    /// call-scoped native memory. Released wholesale by
    /// <see cref="NativeScratchRelease"/> with the mark taken at call entry.</summary>
    internal IntPtr NativeArenaAlloc(int bytes)
    {
        int aligned = (bytes + 7) & ~7;
        while (true)
        {
            if (_nativeArenaChunk == _nativeArenaChunks.Count)
            {
                int size = System.Math.Max(NativeArenaChunkSize, aligned);
                _nativeArenaChunks.Add(System.Runtime.InteropServices.Marshal.AllocHGlobal(size));
                _nativeArenaSizes.Add(size);
            }
            if (_nativeArenaOffset + aligned <= _nativeArenaSizes[_nativeArenaChunk])
            {
                IntPtr p = _nativeArenaChunks[_nativeArenaChunk] + _nativeArenaOffset;
                _nativeArenaOffset += aligned;
                return p;
            }
            _nativeArenaChunk++;
            _nativeArenaOffset = 0;
        }
    }

    // the per-dispatch block lookup keyed by ATOM ID: '$native_run'
    // reads the block-name register as a raw atom cell (no Term materialization)
    // and probes this int-keyed cache instead of hashing the name string per call.
    // Populated lazily; cleared whenever a block is (re)registered.
    private readonly Dictionary<int, NativeBlockEntry> _nativeBlocksByAtomId = new();

    internal NativeBlockEntry? NativeBlockByAtomId(int atomId)
    {
        if (_nativeBlocksByAtomId.TryGetValue(atomId, out var hit)) return hit;
        var entry = NativeBlock(Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "");
        if (entry is not null) _nativeBlocksByAtomId[atomId] = entry;
        return entry;
    }

    internal void AddNativeBlock(string name,
        Shumway.Compiler.NativeC.NativeVar[] vars, Shumway.Compiler.NativeC.CStmt[] stmts,
        Shumway.Compiler.NativeC.NativeScalarGlobal[] scalarGlobals)
    {
        _nativeBlocks[name] = new NativeBlockEntry(vars, stmts, scalarGlobals);
        _nativeBlocksByAtomId.Clear();   // re-registration invalidates the id cache
    }

    // ADR-024 — the text encoding for char* marshalling to/from native t_reftype
    // structs (atom/string content + functor names), used by the materializer tier.
    // Default UTF-8 (the common native-C convention); set to Latin1 / a codepage to
    // match a particular native library. Must be a byte-oriented encoding.
    private System.Text.Encoding _nativeTextEncoding = NativeReftype.DefaultEncoding;

    /// <summary>The <c>char*</c> text encoding for the native materializer tier
    /// (ADR-024). Defaults to UTF-8; set it to match the native library you call.
    /// Must be byte-oriented (UTF-8 / ASCII / Latin1 / a codepage): native strings
    /// are NUL-terminated byte sequences, so an encoding that emits interior zero
    /// bytes for ordinary characters (UTF-16/32) would silently truncate every
    /// marshalled string — rejected here rather than corrupting at the call.</summary>
    public System.Text.Encoding NativeTextEncoding
    {
        get => _nativeTextEncoding;
        set
        {
            if (value is null) throw new System.ArgumentNullException(nameof(value));
            // Byte-oriented check: a plain ASCII character must encode to exactly
            // one byte with no embedded NUL. UTF-16 gives "A\0" (2 bytes), UTF-32
            // four — both would break single-NUL-terminated char* marshalling.
            byte[] probe = value.GetBytes("A");
            if (probe.Length != 1 || probe[0] == 0)
                throw new System.ArgumentException(
                    $"NativeTextEncoding must be a byte-oriented encoding (UTF-8, ASCII, "
                    + $"Latin1, or a single/multi-byte codepage); '{value.WebName}' encodes "
                    + "ASCII characters to multiple bytes and cannot represent NUL-terminated "
                    + "native char* strings.", nameof(value));
            _nativeTextEncoding = value;
        }
    }

    // ADR-024 — functor ids declared `:- native fn/N`: a native function using the
    // materializer protocol (P/Invoke or a managed Reftype snapshot) rather than a
    // plain .NET interop method.
    private readonly HashSet<int> _nativeFunctions = new();
    private readonly HashSet<string> _nativeFunctionNames = new();

    /// <summary>True if <paramref name="name"/>/<paramref name="arity"/> was declared
    /// <c>:- native</c>.</summary>
    internal bool IsNativeFunction(string name, int arity)
        => _nativeFunctions.Count > 0
           && _nativeFunctions.Contains(FunctorTable.Intern(AtomTable.Intern(name).Id, arity));

    /// <summary>True if any <c>:- native</c> declaration uses <paramref name="name"/>
    /// (at any arity) — so the consult-time block validation does not require it to
    /// be a C# interop method (a native function resolves at run time).</summary>
    internal bool IsNativeFunctionName(string name) => _nativeFunctionNames.Contains(name);

    /// <summary>Marks <c>name/arity</c> as a `:- native` function (both the
    /// functor-id set and the name set the consult validation reads).</summary>
    internal void MarkNativeFunction(string name, int arity)
    {
        _nativeFunctions.Add(Shumway.Core.FunctorTable.Intern(
            Shumway.Core.AtomTable.Intern(name, permanent: true).Id, arity));
        _nativeFunctionNames.Add(name);
    }

    // ADR-024 — registered native libraries (handles) for `:- native` functions
    // resolved by P/Invoke; the `:- c` prototypes (signature) and typedefs collected
    // at consult; and the per-function resolution cache so a call resolves once
    // (C# interop method vs native export) and reuses the decision thereafter.
    private readonly System.Collections.Generic.List<IntPtr> _nativeLibraries = new();
    private System.Collections.Generic.Dictionary<string, Shumway.Compiler.NativeC.CPrototype>? _nativePrototypes;
    private System.Collections.Generic.Dictionary<string, Shumway.Compiler.NativeC.CType>? _nativeTypedefs;
    private readonly System.Collections.Generic.Dictionary<int, NativeResolution> _nativeCallCache = new();

    /// <summary>The consulted <c>:- c</c> typedef table, for the native-block code
    /// generators — a block-local declared via a typedef (`s: pchar`) must type as
    /// the resolved C type (char* → string model), matching the interpreter.</summary>
    internal System.Collections.Generic.IReadOnlyDictionary<string, Shumway.Compiler.NativeC.CType>? NativeTypedefsView
        => _nativeTypedefs;

    // ADR-024 — native libraries are loaded ONCE PER PATH for the process lifetime,
    // not once per engine. The OS maps a module once (LoadLibrary/dlopen refcounts),
    // so a per-engine Load would leak one refcount per engine under churn; this
    // process-global table makes the shared mapping explicit and deduplicates the
    // load. The handle is never freed — the mapping lives until the process exits.
    // (A global table guarded by a lock, like the atom / functor tables.)
    private static readonly System.Collections.Generic.Dictionary<string, IntPtr> _loadedNativeLibraries = new();
    private static readonly object _loadedNativeLibrariesLock = new();
    /// <summary>Test/diagnostic: the number of real <c>NativeLibrary.Load</c> calls
    /// (a distinct path is loaded once for the whole process).</summary>
    internal static int NativeLibraryLoadCount;

    /// <summary>ADR-024 — registers a native library (a C DLL/.so/.dylib) whose
    /// exported functions back <c>:- native</c> declarations resolved by P/Invoke.
    /// Call before querying; later registrations invalidate the resolution cache.
    /// The library is loaded once per path for the whole process and shared across
    /// engines (see the <c>_loadedNativeLibraries</c> note).</summary>
    public void UseNativeLibrary(string path)
    {
        System.ArgumentNullException.ThrowIfNull(path);
        // Key by full path when it names an existing file (the --native-dll /
        // LoadBundle case); otherwise by the raw string (an OS-searched bare name).
        string key = System.IO.File.Exists(path) ? System.IO.Path.GetFullPath(path) : path;
        IntPtr h;
        lock (_loadedNativeLibrariesLock)
        {
            if (!_loadedNativeLibraries.TryGetValue(key, out h))
            {
                h = System.Runtime.InteropServices.NativeLibrary.Load(key);
                _loadedNativeLibraries[key] = h;
                System.Threading.Interlocked.Increment(ref NativeLibraryLoadCount);
            }
        }
        if (!_nativeLibraries.Contains(h)) _nativeLibraries.Add(h);
        // ADR-024 — if the library provides the reftype allocator API
        // (newreftype/freepar/…), use it so a native function that builds sub-nodes
        // and the materializer share one heap (freepar can release the mixed graph).
        _nativeAllocator ??= NativeReftypeAllocator.TryResolve(h);
        _nativeCallCache.Clear();
    }

    private NativeReftypeAllocator? _nativeAllocator;

    /// <summary>The native library's reftype allocator, if one is registered — used
    /// to materialize/free reftype graphs through the library's own heap (ADR-024).</summary>
    internal NativeReftypeAllocator? NativeAllocator => _nativeAllocator;

    /// <summary>Collects the <c>:- c</c> prototypes + typedefs so a P/Invoke
    /// <c>:- native</c> call can derive its marshalling signature. Called at consult
    /// once the C symbol table is parsed.</summary>
    internal void RegisterNativePrototypes(System.Collections.Generic.IReadOnlyList<Shumway.Compiler.NativeC.CDecl> cDecls)
    {
        foreach (var d in cDecls)
            switch (d)
            {
                case Shumway.Compiler.NativeC.CPrototype p:
                    (_nativePrototypes ??= new())[p.Name] = p;
                    break;
                case Shumway.Compiler.NativeC.CTypedef td:
                    (_nativeTypedefs ??= new())[td.Alias] = td.Underlying;
                    break;
            }
        _nativeCallCache.Clear();
    }

    /// <summary>The cached resolution of a <c>:- native</c> call — a C# interop
    /// method (managed snapshot) or a native export (P/Invoke). Resolved once per
    /// functor and reused.</summary>
    internal sealed class NativeResolution
    {
        public System.Reflection.MethodInfo? CsMethod;     // non-null → managed path
        public IntPtr NativeFn;                            // P/Invoke target
        public NativeCall.Signature? Signature;            // P/Invoke marshalling
    }

    internal NativeResolution ResolveNativeCall(string name, int arity)
    {
        int fid = FunctorTable.Intern(AtomTable.Intern(name).Id, arity);
        if (_nativeCallCache.TryGetValue(fid, out var cached)) return cached;
        var r = BuildNativeResolution(name, arity);
        _nativeCallCache[fid] = r;
        return r;
    }

    private NativeResolution BuildNativeResolution(string name, int arity)
    {
        // 1. A C# interop method → managed (snapshot) path.
        var m = ResolveNativeInterop(name);
        if (m is not null) return new NativeResolution { CsMethod = m };

        // 2. A native export from a registered library → P/Invoke path.
        IntPtr fn = IntPtr.Zero;
        foreach (var lib in _nativeLibraries)
            if (System.Runtime.InteropServices.NativeLibrary.TryGetExport(lib, name, out fn))
                break;
        if (fn != IntPtr.Zero)
        {
            if (_nativePrototypes is null || !_nativePrototypes.TryGetValue(name, out var proto))
                throw new System.InvalidOperationException(
                    $":- native '{name}': no ':- c' prototype found to derive its native signature.");
            var sig = NativeCall.FromPrototype(proto,
                _nativeTypedefs ?? new System.Collections.Generic.Dictionary<string, Shumway.Compiler.NativeC.CType>());
            return new NativeResolution { NativeFn = fn, Signature = sig };
        }
        throw new System.InvalidOperationException(
            $":- native '{name}/{arity}': not a public static method of the interop class and not exported "
            + "by any registered native library (UseNativeLibrary).");
    }

    // ADR-022 — per-engine persistent storage for SCALAR `:- c` globals (a plain
    // int/long/float/double global, as opposed to a char*/reftype holder). Like
    // _reftypeSlots these persist across calls/queries — Arity static-storage
    // semantics. A native block seeds its value on entry and writes it through on
    // every assignment. Plain CLR values, heap-independent, so they survive query
    // teardown.
    private readonly Dictionary<string, long> _nativeGlobalInt = new();
    private readonly Dictionary<string, double> _nativeGlobalFloat = new();

    /// <summary>Reads an integer scalar `:- c` native global's persistent value
    /// (0 if never written). Public so runtime Expression / Tier-1 IL native-block
    /// codegen can emit a direct call.</summary>
    public long GetNativeGlobalInt(string name)
        => _nativeGlobalInt.TryGetValue(name, out var v) ? v : 0L;
    /// <summary>Writes an integer scalar `:- c` native global's persistent value.</summary>
    public void SetNativeGlobalInt(string name, long v) => _nativeGlobalInt[name] = v;
    /// <summary>Reads a float scalar `:- c` native global's persistent value.</summary>
    public double GetNativeGlobalFloat(string name)
        => _nativeGlobalFloat.TryGetValue(name, out var v) ? v : 0.0;
    /// <summary>Writes a float scalar `:- c` native global's persistent value.</summary>
    public void SetNativeGlobalFloat(string name, double v) => _nativeGlobalFloat[name] = v;

    internal NativeBlockEntry? NativeBlock(string name)
        => _nativeBlocks.TryGetValue(name, out var b) ? b : null;

    // ADR-024 — per-engine term slots for `reftype` globals declared in `:- c`
    // regions (par1ref… and the program's own). Persist across queries (an Arity
    // global buffer is reused between calls; fill_par overwrites it). The slot
    // holds an AST term, self-contained and heap-independent, so it survives query
    // teardown. `&name` / `name` in a native block resolves to the slot.
    private readonly Dictionary<string, TermSlot> _reftypeSlots = new();

    /// <summary>The term slot for a `reftype` global, or null if the name isn't a
    /// registered reftype global.</summary>
    internal TermSlot? ReftypeSlot(string name)
        => _reftypeSlots.TryGetValue(name, out var s) ? s : null;

    /// <summary>The term slot for a `reftype` global, created on first reference.
    /// Used by the native-block runner when a block takes the address of a reftype
    /// global (<c>&amp;name</c>) or passes one to an interop function expecting a
    /// <see cref="TermSlot"/> — so a slot exists even when the `:- c` declarations
    /// didn't travel (a source-stripped bundle: the declarations are compile-time;
    /// the block runs in the interpreter and creates its slots here).</summary>
    internal TermSlot GetOrCreateReftypeSlot(string name)
    {
        if (!_reftypeSlots.TryGetValue(name, out var s))
            _reftypeSlots[name] = s = new TermSlot();
        return s;
    }

    internal void RegisterReftypeGlobals(IReadOnlyList<Shumway.Compiler.NativeC.CDecl> decls)
    {
        foreach (var d in decls)
            if (d is Shumway.Compiler.NativeC.CGlobalVar g
                && g.Type.Name is "reftype" or "preftype" or "t_reftype"
                && !_reftypeSlots.ContainsKey(g.Name))
                _reftypeSlots[g.Name] = new TermSlot();
    }

    private Shumway.Compiler.NativeC.NativeInlineContext? _nativeInlineContext;

    /// <summary>ADR-022 item 2 — the context the IL compiler uses to inline this
    /// engine's native blocks (build-time IL). Null until a block is registered;
    /// then built once (the marshalling handles are constant; the block lookup and
    /// interop resolver close over this engine, so they track later state).</summary>
    internal Shumway.Compiler.NativeC.NativeInlineContext? GetNativeInlineContext()
    {
        if (_nativeBlocks.Count == 0) return null;
        return _nativeInlineContext ??= BuildNativeInlineContext();
    }

    private Shumway.Compiler.NativeC.NativeInlineContext BuildNativeInlineContext()
    {
        var fromTerm = typeof(PrologEngine).GetMethod(nameof(PrologEngine.FromTerm))!;
        var toTerm = typeof(PrologEngine).GetMethod(nameof(PrologEngine.ToTerm))!;
        return new Shumway.Compiler.NativeC.NativeInlineContext
        {
            BlockProvider = n =>
            {
                var e = NativeBlock(n);
                return e is null ? null
                    : new Shumway.Compiler.NativeC.NativeBlockBody(e.Vars, e.Stmts, e.ScalarGlobals);
            },
            InteropResolver = ResolveNativeInterop,
            TypedefsProvider = () => _nativeTypedefs,
            ReadRegisterAsTerm = typeof(RegisterMarshalling)
                .GetMethod(nameof(RegisterMarshalling.ReadRegisterAsTerm))!,
            UnifyRegisterWithTerm = typeof(RegisterMarshalling)
                .GetMethod(nameof(RegisterMarshalling.UnifyRegisterWithTerm))!,
            HostGetter = typeof(Activation).GetProperty(nameof(Activation.Host))!.GetGetMethod()!,
            HostType = typeof(PrologEngine),
            FromTermLong = fromTerm.MakeGenericMethod(typeof(long)),
            FromTermDouble = fromTerm.MakeGenericMethod(typeof(double)),
            FromTermString = fromTerm.MakeGenericMethod(typeof(string)),
            ToTermLong = toTerm.MakeGenericMethod(typeof(long)),
            ToTermDouble = toTerm.MakeGenericMethod(typeof(double)),
            AtomTermCtor = typeof(Shumway.Compiler.Ast.AtomTerm)
                .GetConstructor(new[] { typeof(string) })!,
            // ADR-022 persistent scalar-global accessors.
            GetNativeGlobalInt = typeof(PrologEngine).GetMethod(nameof(GetNativeGlobalInt))!,
            SetNativeGlobalInt = typeof(PrologEngine).GetMethod(nameof(SetNativeGlobalInt))!,
            GetNativeGlobalFloat = typeof(PrologEngine).GetMethod(nameof(GetNativeGlobalFloat))!,
            SetNativeGlobalFloat = typeof(PrologEngine).GetMethod(nameof(SetNativeGlobalFloat))!,
            // ADR-024 reftype tier handles.
            TermSlotType = typeof(TermSlot),
            GetOrCreateReftypeSlot = typeof(PrologEngine).GetMethod(
                nameof(GetOrCreateReftypeSlot),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!,
            MakeForeign = typeof(Activation).GetMethod(nameof(Activation.MakeForeign))!,
            UnifyRegisterWithCell = typeof(Activation).GetMethod(nameof(Activation.UnifyRegisterWithCell))!,
            ReadReftypeSlot = typeof(NativeBlockCompiler).GetMethod(
                nameof(NativeBlockCompiler.ReadReftypeSlot))!,
            SlotSetValue = typeof(TermSlot).GetMethod(nameof(TermSlot.SetValue))!,
            SlotMaterialize = typeof(TermSlot).GetMethod(nameof(TermSlot.Materialize))!,
        };
    }

}
