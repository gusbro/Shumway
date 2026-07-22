using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Shumway.Builtins;
using Shumway.Compiler.NativeC;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>ADR-022 — a registered native block plus its lazily-compiled fast
/// path. The interpreter (<see cref="NativeBlockRunner.RunBlock"/>) is always
/// available; on first execution <c>'$native_run'</c> compiles the block to a
/// delegate (<see cref="NativeBlockCompiler"/>) in engine context and caches it,
/// falling back to the interpreter when compilation isn't possible (an
/// unsupported construct, or Native AOT — no runtime IL generation).</summary>
public sealed class NativeBlockEntry
{
    public NativeVar[] Vars { get; }
    public CStmt[] Stmts { get; }
    /// <summary>ADR-022 — the scalar `:- c` globals this block reads/writes, mapped
    /// to per-engine persistent storage (Arity static-storage semantics).</summary>
    public NativeScalarGlobal[] ScalarGlobals { get; }
    internal Func<Activation, bool>? Compiled;
    internal bool CompileTried;

    // the block-invariant lookup maps the interpreter fallback used
    // to rebuild on EVERY call (three dictionaries + fill loops per dispatch).
    // Built once, lazily, on first interpreted run.
    internal Dictionary<string, int>? IndexMap;
    internal Dictionary<string, NativeKind>? KindMap;
    internal Dictionary<string, bool>? ScalarFloatMap;

    internal void EnsureMaps()
    {
        if (IndexMap is not null) return;
        var index = new Dictionary<string, int>(Vars.Length);
        var kind = new Dictionary<string, NativeKind>(Vars.Length);
        for (int i = 0; i < Vars.Length; i++)
        {
            index[Vars[i].Name] = i;
            kind[Vars[i].Name] = Vars[i].Kind;
        }
        var sf = new Dictionary<string, bool>(ScalarGlobals.Length);
        foreach (var g in ScalarGlobals) sf[g.Name] = g.IsFloat;
        ScalarFloatMap = sf;
        KindMap = kind;
        IndexMap = index;   // published last — the maps above are complete
    }

    public NativeBlockEntry(NativeVar[] vars, CStmt[] stmts, NativeScalarGlobal[] scalarGlobals)
    { Vars = vars; Stmts = stmts; ScalarGlobals = scalarGlobals; }
}

/// <summary>ADR-022 step 4 (first cut) — turns an analysed native block into a
/// <see cref="BuiltinImpl"/>: a synthesized foreign whose argument registers are
/// the block's Prolog variables (in <paramref name="vars"/> order). It marshals
/// the inputs Prolog→.NET, runs the statement sequence (the <c>MakeCString</c> /
/// <c>MakePrologString</c> string intrinsics inline, every other call dispatched
/// to a <c>Shumway.Native.Interop</c> static method via the supplied resolver),
/// and unifies the outputs back. This first cut INTERPRETS the (small, linear)
/// statement list; the IL form is a later refinement. Only the int/float/string
/// tier is handled — the term/reftype tier is deferred.</summary>
public static class NativeBlockRunner
{
    /// <summary>Builds the foreign for a block. The interop resolver is taken
    /// from the engine the builtin is invoked on (<see cref="PrologEngine.
    /// ResolveNativeInterop"/>), so the same synthesized builtin works for any
    /// engine and any configured interop class.</summary>
    public static BuiltinImpl Build(
        IReadOnlyList<NativeVar> vars,
        IReadOnlyList<CStmt> stmts)
        => engine => RunBlock(engine, vars, stmts,
            System.Array.Empty<NativeScalarGlobal>(), regOffset: 0);

    /// <summary>Runs a native block against the engine: the block's Prolog
    /// variables are in argument registers <paramref name="regOffset"/> ..
    /// <c>regOffset + vars.Count - 1</c> (in <paramref name="vars"/> order). Used
    /// by <see cref="Build"/> (offset 0, a per-block builtin) and by the
    /// <c>'$native_run'</c> dispatch (offset 1, after the block-name argument).</summary>
    public static bool RunBlock(Activation engine, IReadOnlyList<NativeVar> vars,
        IReadOnlyList<CStmt> stmts, IReadOnlyList<NativeScalarGlobal> scalarGlobals, int regOffset)
    {
        // Standalone form (tests / ad-hoc callers): builds the lookup maps fresh.
        var index = new Dictionary<string, int>();
        var kindOf = new Dictionary<string, NativeKind>();
        for (int i = 0; i < vars.Count; i++) { index[vars[i].Name] = i; kindOf[vars[i].Name] = vars[i].Kind; }
        var scalarFloat = new Dictionary<string, bool>();
        foreach (var g in scalarGlobals) scalarFloat[g.Name] = g.IsFloat;
        return RunBlockCore(engine, vars, stmts, scalarGlobals, regOffset, index, kindOf, scalarFloat);
    }

    /// <summary>the '$native_run' dispatch entry: reuses the entry's
    /// lazily-built block-invariant maps instead of rebuilding three dictionaries
    /// per call.</summary>
    internal static bool RunBlock(Activation engine, NativeBlockEntry entry, int regOffset)
    {
        entry.EnsureMaps();
        return RunBlockCore(engine, entry.Vars, entry.Stmts, entry.ScalarGlobals, regOffset,
            entry.IndexMap!, entry.KindMap!, entry.ScalarFloatMap!);
    }

    private static bool RunBlockCore(Activation engine, IReadOnlyList<NativeVar> vars,
        IReadOnlyList<CStmt> stmts, IReadOnlyList<NativeScalarGlobal> scalarGlobals, int regOffset,
        Dictionary<string, int> index, Dictionary<string, NativeKind> kindOf,
        Dictionary<string, bool> scalarFloat)
    {
        var host = (PrologEngine)engine.Host!;
        Func<string, MethodInfo?> resolveInterop = host.ResolveNativeInterop;

        var env = new Dictionary<string, object?>();
        foreach (var v in vars)
            if (v.Mode == NativeMode.Input)
                env[v.Name] = ReadInput(host, engine, regOffset + index[v.Name], v.Kind);

        // ADR-022 — seed each scalar `:- c` global from per-engine persistent
        // storage (Arity static-storage). Writes are flushed back through
        // ExecStmt (write-through), so a later failure doesn't revert them.
        foreach (var g in scalarGlobals)
        {
            env[g.Name] = g.IsFloat
                ? host.GetNativeGlobalFloat(g.Name)
                : (object)host.GetNativeGlobalInt(g.Name);
        }

        var outputs = new Dictionary<string, object?>();
        foreach (var st in stmts)
            ExecStmt(st, host, env, outputs, index, kindOf, scalarFloat, resolveInterop);

        foreach (var (name, value) in outputs)
        {
            if (!index.TryGetValue(name, out int i)) continue;   // not a register var
            // ADR-024 — a reftype output is the slot cursor; bind the register to
            // the slot's Foreign cell so fill_par / reftype_term / a later block
            // recover it.
            if (vars[i].Kind == NativeKind.Reftype)
            {
                if (!engine.UnifyRegisterWithCell(regOffset + i,
                        engine.MakeForeign((TermSlot)value!)))
                    return false;
                continue;
            }
            var term = ToTerm(host, vars[i].Kind, value);
            if (!RegisterMarshalling.UnifyRegisterWithTerm(engine, regOffset + i, term))
                return false;
        }
        return true;
    }

    // -----------------------------------------------------------------------

    private static object? ReadInput(PrologEngine host, Activation engine, int reg, NativeKind kind)
    {
        // ADR-024 — a reftype input is a slot handle (a Foreign cell).
        // A3: unwrap it straight from the dereferenced cell — no Term walk.
        if (kind == NativeKind.Reftype)
        {
            var c = RegisterMarshalling.DerefRegisterCell(engine, reg);
            return c.Tag == Shumway.Core.Tag.Foreign ? engine.AsForeign<TermSlot>(c) : null;
        }
        var term = RegisterMarshalling.ReadRegisterAsTerm(engine, reg);
        return kind switch
        {
            NativeKind.Int or NativeKind.Long => host.FromTerm<long>(term),
            NativeKind.Float or NativeKind.Double => host.FromTerm<double>(term),
            NativeKind.String => host.FromTerm<string>(term),
            _ => throw new System.NotSupportedException($"native input kind {kind}"),
        };
    }

    private static Shumway.Compiler.Ast.Term ToTerm(PrologEngine host, NativeKind kind, object? value)
        => kind switch
        {
            NativeKind.Int or NativeKind.Long => host.ToTerm<long>(System.Convert.ToInt64(value)),
            NativeKind.Float or NativeKind.Double => host.ToTerm<double>(System.Convert.ToDouble(value)),
            // Arity "string" is an ATOM — emit an AtomTerm so it unifies with an
            // atom literal (and round-trips with the FromTerm<string> input read).
            NativeKind.String => new Shumway.Compiler.Ast.AtomTerm((string)value!),
            _ => throw new System.NotSupportedException($"native output kind {kind}"),
        };

    private static void ExecStmt(CStmt st, PrologEngine host, Dictionary<string, object?> env,
        Dictionary<string, object?> outputs, Dictionary<string, int> prologVars,
        Dictionary<string, NativeKind> kindOf, Dictionary<string, bool> scalarFloat,
        Func<string, MethodInfo?> resolve)
    {
        switch (st)
        {
            case CVarDeclStmt:
                break;   // a local declaration — storage materialises on first assignment
            // ADR-024 — `Par1 is par1str` where Par1 is a holder (the inference
            // typed it Reftype, from a holder global): Par1 = the global's slot
            // cursor, created on first reference (works in a bundle too).
            case CBindStmt { Value: CIdentExpr g } b
                when kindOf.TryGetValue(b.Var, out var bk) && bk == NativeKind.Reftype:
                (prologVars.ContainsKey(b.Var) ? outputs : env)[b.Var] =
                    host.GetOrCreateReftypeSlot(g.Name);
                break;
            case CBindStmt b:
                // `Var is e` binds an OUTPUT Prolog variable (goes to `outputs`,
                // unified at the end) — but the same `is` form also binds a
                // block-local intermediate (e.g. `T is sum(A,B)` where T is read
                // by a later statement); that goes to the local environment.
                (prologVars.ContainsKey(b.Var) ? outputs : env)[b.Var] =
                    Eval(b.Value, host, env, outputs, resolve);
                break;
            case CAssignStmt { Target: CIdentExpr id } a:
                env[id.Name] = Eval(a.Value, host, env, outputs, resolve);
                // ADR-022 — write-through to persistent storage for a scalar global.
                if (scalarFloat.TryGetValue(id.Name, out bool isFloat))
                {
                    if (isFloat) host.SetNativeGlobalFloat(id.Name, System.Convert.ToDouble(env[id.Name]));
                    else host.SetNativeGlobalInt(id.Name, System.Convert.ToInt64(env[id.Name]));
                }
                break;
            case CCallStmt c:
                Eval(c.Call, host, env, outputs, resolve);   // side effect (an interop call / intrinsic)
                break;
            default:
                throw new System.NotSupportedException($"native statement {st.GetType().Name}");
        }
    }

    private static object? Eval(CExpr e, PrologEngine host, Dictionary<string, object?> env,
        Dictionary<string, object?> outputs, Func<string, MethodInfo?> resolve)
    {
        switch (e)
        {
            case CIntExpr n: return n.Value;
            case CStringExpr s: return s.Value;
            // ADR-024 — `&name` is that reftype global's slot cursor (the `&` is
            // vestigial in the cursor model — `name` and `&name` resolve to the same
            // slot). Created on first reference so it works in a bundle too. (A
            // `&Var` for a string intrinsic is handled in the intrinsic cases above,
            // so a bare `&ident` here is a reftype global.)
            case CAddrOfExpr { Operand: CIdentExpr g }:
                return host.GetOrCreateReftypeSlot(g.Name);
            case CIdentExpr id:
                if (env.TryGetValue(id.Name, out var val)) return val;
                if (outputs.TryGetValue(id.Name, out var ov)) return ov;
                if (host.ReftypeSlot(id.Name) is { } s2) return s2;   // a reftype global
                return null;
            case CCallExpr { Name: "MakeCString" } mk:
                // buf := the input Prolog string named by `&Str`.
                {
                    string? buf = mk.Args.OfType<CIdentExpr>().FirstOrDefault()?.Name;
                    string? str = StrArg(mk);
                    if (buf is not null && str is not null) env[buf] = env.GetValueOrDefault(str);
                    return null;
                }
            case CCallExpr { Name: "MakePrologString" or "MakePrologStringEx" } mp:
                // the output Prolog string named by `&Var` := the source value.
                {
                    var src = mp.Args.FirstOrDefault(a => a is not CAddrOfExpr);
                    string? outVar = StrArg(mp);
                    if (outVar is not null) outputs[outVar] = src is null ? null : Eval(src, host, env, outputs, resolve);
                    return null;
                }
            case CBinaryExpr b:
                return EvalBinary(b.Op,
                    Eval(b.Left, host, env, outputs, resolve), Eval(b.Right, host, env, outputs, resolve));
            case CCallExpr c:
                return CallInterop(c, host, env, outputs, resolve);
            default:
                throw new System.NotSupportedException($"native expression {e.GetType().Name}");
        }
    }

    /// <summary>The reftype-global name an interop argument names — a bare
    /// identifier (<c>par1ref</c>) or its address (<c>&amp;par1ref</c>), or null
    /// when the argument is not a plain global reference.</summary>
    private static string? ReftypeName(CExpr e) => e switch
    {
        CIdentExpr id => id.Name,
        CAddrOfExpr { Operand: CIdentExpr id } => id.Name,
        _ => null,
    };

    /// <summary>The name of the Prolog variable an intrinsic marshals — its
    /// <c>&amp;Var</c> argument.</summary>
    private static string? StrArg(CCallExpr c)
        => c.Args.OfType<CAddrOfExpr>()
            .Select(a => (a.Operand as CIdentExpr)?.Name)
            .FirstOrDefault(n => n is not null);

    private static object? CallInterop(CCallExpr c, PrologEngine host, Dictionary<string, object?> env,
        Dictionary<string, object?> outputs, Func<string, MethodInfo?> resolve)
    {
        // ADR-024 — a `:- native` function uses the materializer tier. Its resolution
        // (a C# interop method → managed snapshot, or a native library export →
        // P/Invoke) is decided once and cached, so subsequent calls dispatch directly.
        bool isNative = host.IsNativeFunction(c.Name, c.Args.Count);
        MethodInfo m;
        if (isNative)
        {
            var res = host.ResolveNativeCall(c.Name, c.Args.Count);
            if (res.CsMethod is null)
                return PInvokeCall(res, c, host, env, outputs, resolve);   // native C, P/Invoke
            m = res.CsMethod;
        }
        else
        {
            m = resolve(c.Name)
                ?? throw new System.InvalidOperationException(
                    $"native function '{c.Name}' is not a public static method of the interop class.");
        }
        var ps = m.GetParameters();
        var args = new object?[c.Args.Count];
        // A Reftype parameter receives a MANAGED SNAPSHOT of the reftype global's
        // term (materialized); the (possibly mutated) snapshot is written back to the
        // slot after the call.
        System.Collections.Generic.List<(TermSlot Slot, Reftype Snapshot)>? writebacks = null;
        for (int i = 0; i < c.Args.Count; i++)
        {
            if (isNative && i < ps.Length && ps[i].ParameterType == typeof(Reftype)
                && ReftypeName(c.Args[i]) is { } nrn)
            {
                var slot = host.GetOrCreateReftypeSlot(nrn);
                var snap = Reftype.Materialize(slot.Materialize());
                args[i] = snap;
                (writebacks ??= new()).Add((slot, snap));
                continue;
            }
            // ADR-024 — an interop parameter of type TermSlot receives a reftype
            // global directly (a `reftype` argument, e.g. `'i_form_exp'(.., par1ref)`):
            // resolve the name to its slot (creating it on first reference) rather
            // than marshalling a value.
            if (i < ps.Length && ps[i].ParameterType == typeof(TermSlot)
                && ReftypeName(c.Args[i]) is { } rn)
            {
                args[i] = host.GetOrCreateReftypeSlot(rn);
                continue;
            }
            object? v = Eval(c.Args[i], host, env, outputs, resolve);
            args[i] = i < ps.Length ? ConvertArg(v, ps[i].ParameterType) : v;
        }
        object? result = InvokerFor(m)(args);
        // Dematerialize each snapshot back into its slot so a following
        // reftype_term sees what the native function built / modified.
        if (writebacks is not null)
            foreach (var (slot, snap) in writebacks)
                slot.SetValue(Reftype.Dematerialize(snap));
        return result;
    }

    /// <summary>ADR-024 — the P/Invoke path: a `:- native` function exported by a
    /// registered native library. Each reftype argument is materialized to native
    /// <c>t_reftype</c> memory; the function is invoked by pointer (cdecl calli);
    /// then the reftype structs are dematerialized back into their slots and freed.
    /// (First cut: scalar + reftype params; the native function may modify the
    /// struct's scalar fields in place.)</summary>
    private static object? PInvokeCall(PrologEngine.NativeResolution res, CCallExpr c, PrologEngine host,
        Dictionary<string, object?> env, Dictionary<string, object?> outputs, Func<string, MethodInfo?> resolve)
    {
        var sig = res.Signature!;
        var enc = host.NativeTextEncoding;
        var alloc = host.NativeAllocator;   // null → HGlobal in-place path
        var args = new object?[c.Args.Count];
        // Handle = the allocator cell (alloc mode) or the arena struct pointer.
        System.Collections.Generic.List<(TermSlot Slot, IntPtr Handle)>? reftypes = null;
        // OutScalar: a `&local` pointer the native function writes through.
        System.Collections.Generic.List<(string Local, IntPtr Ptr, Type Elem)>? outScalars = null;
        // OutString: a `char**` cell the native function writes a (borrowed) char* into.
        System.Collections.Generic.List<(string Local, IntPtr Cell)>? outStrings = null;
        // EVERY call-scoped native buffer (out cells, char*
        // inputs, and the whole t_reftype graph) bump-allocates from the
        // engine's chunked native arena; the finally releases them all with
        // one mark restore. No AllocHGlobal, no graph-walking free — measured
        // 96% of the reftype marshal cost. Safety unchanged from the
        // recorded-allocations mode this replaces: the restore frees exactly
        // OUR blocks; a foreign pointer the native fn linked in is untouched.
        long scratchMark = host.NativeScratchMark;
        // All native memory is released in the finally: an exception anywhere in
        // marshalling, the native invoke, or the read-back must not leak buffers.
        try
        {
            for (int i = 0; i < c.Args.Count; i++)
            {
                var kind = i < sig.ParamKinds.Length ? sig.ParamKinds[i] : NativeCall.Kind.Scalar;
                if (kind == NativeCall.Kind.OutString && AddrOfLocal(c.Args[i]) is { } slocal)
                {
                    IntPtr cell = host.NativeArenaAlloc(8);
                    (outStrings ??= new()).Add((slocal, cell));
                    System.Runtime.InteropServices.Marshal.WriteIntPtr(cell, System.IntPtr.Zero);
                    args[i] = cell;
                    continue;
                }
                if (kind == NativeCall.Kind.StringIn)
                {
                    // A char* input: render the Prolog argument to a string and copy it
                    // into NUL-terminated arena memory (call-scoped).
                    string s = AsNativeString(Eval(c.Args[i], host, env, outputs, resolve), host);
                    args[i] = NativeReftype.AllocString(s, enc, arena: host);
                    continue;
                }
                if (kind == NativeCall.Kind.Reftype && ReftypeName(c.Args[i]) is { } rn)
                {
                    var slot = host.GetOrCreateReftypeSlot(rn);
                    IntPtr handle, structPtr;
                    if (alloc is not null)
                    {
                        // Library-allocated: the native function may build sub-nodes; the
                        // whole graph (and ours) is freed by freepar.
                        handle = alloc.Materialize(slot.Materialize(), enc);
                        structPtr = NativeReftypeAllocator.StructPointer(handle);
                    }
                    else
                    {
                        handle = NativeReftype.Materialize(slot.Materialize(), enc, arena: host);
                        structPtr = handle;
                    }
                    args[i] = structPtr;
                    (reftypes ??= new()).Add((slot, handle));
                    continue;
                }
                if (kind == NativeCall.Kind.OutScalar && AddrOfLocal(c.Args[i]) is { } local)
                {
                    Type elem = sig.ParamElemTypes[i];
                    IntPtr ptr = host.NativeArenaAlloc(8);
                    (outScalars ??= new()).Add((local, ptr, elem));
                    // Seed from the local's current value (in/out), or zero.
                    WriteScalar(ptr, elem, env.TryGetValue(local, out var cur) ? cur : null);
                    args[i] = ptr;
                    continue;
                }
                object? v = Eval(c.Args[i], host, env, outputs, resolve);
                args[i] = ConvertArg(v, i < sig.ParamClrTypes.Length ? sig.ParamClrTypes[i] : typeof(IntPtr));
            }
            object? ret = sig.Invoker(res.NativeFn, args);
            // Read-backs run only on a successful native call.
            if (reftypes is not null)
                foreach (var (slot, handle) in reftypes)
                {
                    IntPtr structPtr = alloc is not null
                        ? NativeReftypeAllocator.StructPointer(handle) : handle;
                    slot.SetValue(NativeReftype.Dematerialize(structPtr, enc));
                }
            if (outScalars is not null)
                foreach (var (local, ptr, elem) in outScalars)
                    env[local] = ReadScalar(ptr, elem);   // the native function wrote it
            if (outStrings is not null)
                foreach (var (local, cell) in outStrings)
                {
                    // The native function wrote a borrowed char* into the cell: decode
                    // the string (the cell itself is freed in the finally; the string
                    // is native-owned and never freed).
                    IntPtr sp = System.Runtime.InteropServices.Marshal.ReadIntPtr(cell);
                    env[local] = NativeReftype.ReadString(sp, enc);
                }
            // ADR-024 char* return: a pointer return (char* / reftype*) comes back as
            // an IntPtr; surface it to the block as a raw pointer integer (a long) so
            // a following `Ptr \= 0` / make_prolog_string(Ptr, X) can use it.
            if (ret is IntPtr ip) return ip.ToInt64();
            return ret;
        }
        finally
        {
            if (reftypes is not null && alloc is not null)
                foreach (var (_, handle) in reftypes)
                    alloc.Free(handle);
            // one mark restore releases every call-scoped arena
            // buffer: reftype graphs, pars arrays, char* inputs, out cells.
            host.NativeScratchRelease(scratchMark);
        }
    }

    /// <summary>Renders a native-call argument value to the string passed as a
    /// <c>char*</c>. Atoms / strings use their text; numbers their decimal form.</summary>
    private static string AsNativeString(object? v, PrologEngine host) => v switch
    {
        null => string.Empty,
        string s => s,
        Shumway.Compiler.Ast.AtomTerm a => a.Name,
        Shumway.Compiler.Ast.StringTerm st => st.Content,
        _ => System.Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
    };


    /// <summary>ADR-024 — the IL-path P/Invoke entry. Same marshalling as
    /// <see cref="PInvokeCall"/> but over <b>pre-evaluated</b> argument values (the
    /// emitted block passes them as a boxed array), so there is no AST walk / env
    /// dictionary on the hot path. Reftype args are named by their reftype global;
    /// scalar / string-in args are boxed values. Out-scalar params are not supported
    /// here — the IL compiler bails a block that has any to the interpreter. The
    /// result is normalized to a boxed <c>long</c> (integer / pointer) or
    /// <c>double</c> (floating) so the emitted IL unboxes one model type.</summary>
    internal static object? PInvokeFromIl(PrologEngine host, PrologEngine.NativeResolution res,
        object?[] args, byte[] kinds, string?[] reftypeNames, object?[]? outScalars = null)
    {
        var sig = res.Signature!;
        var enc = host.NativeTextEncoding;
        var alloc = host.NativeAllocator;
        var callArgs = new object?[args.Length];
        System.Collections.Generic.List<(TermSlot Slot, IntPtr Handle)>? reftypes = null;
        // OutScalar: (param index, native ptr, element type) — read back into
        // outScalars[index] after the call, for the emitted IL to store to its local.
        System.Collections.Generic.List<(int Index, IntPtr Ptr, Type Elem)>? outs = null;
        // OutString: (param index, char** cell) — decode into outScalars[index].
        System.Collections.Generic.List<(int Index, IntPtr Cell)>? outStrs = null;
        // All call-scoped native buffers from the engine's
        // chunked arena; released wholesale by the mark restore (see PInvokeCall).
        long scratchMark = host.NativeScratchMark;
        // All native memory is released in the finally — no leak on an exception
        // in marshalling, the native invoke, or the read-back.
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch ((NativeCall.Kind)kinds[i])
                {
                    case NativeCall.Kind.OutString:
                    {
                        IntPtr cell = host.NativeArenaAlloc(8);
                        (outStrs ??= new()).Add((i, cell));
                        System.Runtime.InteropServices.Marshal.WriteIntPtr(cell, System.IntPtr.Zero);
                        callArgs[i] = cell;
                        break;
                    }
                    case NativeCall.Kind.OutScalar:
                    {
                        Type elem = sig.ParamElemTypes[i];
                        IntPtr ptr = host.NativeArenaAlloc(8);
                        (outs ??= new()).Add((i, ptr, elem));
                        WriteScalar(ptr, elem, args[i]);   // seed from the block-local value (in/out)
                        callArgs[i] = ptr;
                        break;
                    }
                    case NativeCall.Kind.Reftype:
                    {
                        var slot = host.GetOrCreateReftypeSlot(reftypeNames[i]!);
                        IntPtr handle, structPtr;
                        if (alloc is not null)
                        {
                            handle = alloc.Materialize(slot.Materialize(), enc);
                            structPtr = NativeReftypeAllocator.StructPointer(handle);
                        }
                        else
                        {
                            handle = NativeReftype.Materialize(slot.Materialize(), enc, arena: host);
                            structPtr = handle;
                        }
                        callArgs[i] = structPtr;
                        (reftypes ??= new()).Add((slot, handle));
                        break;
                    }
                    case NativeCall.Kind.StringIn:
                    {
                        callArgs[i] = NativeReftype.AllocString(
                            AsNativeString(args[i], host), enc, arena: host);
                        break;
                    }
                    default:   // Scalar
                        callArgs[i] = ConvertArg(args[i],
                            i < sig.ParamClrTypes.Length ? sig.ParamClrTypes[i] : typeof(IntPtr));
                        break;
                }
            }
            object? ret = sig.Invoker(res.NativeFn, callArgs);
            // Read-backs run only on a successful native call.
            if (reftypes is not null)
                foreach (var (slot, handle) in reftypes)
                {
                    IntPtr structPtr = alloc is not null
                        ? NativeReftypeAllocator.StructPointer(handle) : handle;
                    slot.SetValue(NativeReftype.Dematerialize(structPtr, enc));
                }
            if (outs is not null)
                foreach (var (idx, ptr, elem) in outs)
                    if (outScalars is not null) outScalars[idx] = ReadScalar(ptr, elem);
            if (outStrs is not null)
                foreach (var (idx, cell) in outStrs)
                {
                    IntPtr sp = System.Runtime.InteropServices.Marshal.ReadIntPtr(cell);
                    if (outScalars is not null) outScalars[idx] = NativeReftype.ReadString(sp, enc);
                }
            if (sig.ReturnType == typeof(void)) return null;
            if (sig.ReturnType == typeof(double) || sig.ReturnType == typeof(float))
                return System.Convert.ToDouble(ret);
            if (ret is IntPtr ip) return ip.ToInt64();
            return System.Convert.ToInt64(ret);
        }
        finally
        {
            if (reftypes is not null && alloc is not null)
                foreach (var (_, handle) in reftypes)
                    alloc.Free(handle);
            // one mark restore releases every call-scoped arena
            // buffer (reftype graphs, char* inputs, out cells).
            host.NativeScratchRelease(scratchMark);
        }
    }

    /// <summary>The block-local name an out-scalar argument addresses — <c>&amp;id</c>
    /// (or a bare <c>id</c>); null if the argument is not a plain local reference.
    /// The bare-ident form is deliberate: this helper only runs for a parameter the
    /// `:- c` prototype already types as a POINTER (out-scalar / out-string), where
    /// a by-value reading of the same local is meaningless — corpus blocks pass
    /// pointer-typed locals (e.g. a `pshort` declared local) without `&amp;`.</summary>
    private static string? AddrOfLocal(CExpr e) => e switch
    {
        CAddrOfExpr { Operand: CIdentExpr id } => id.Name,
        CIdentExpr id => id.Name,
        _ => null,
    };

    private static int SizeOf(Type t) =>
        t == typeof(short) ? 2 : t == typeof(int) || t == typeof(float) ? 4 : 8;

    private static void WriteScalar(IntPtr p, Type t, object? v)
    {
        if (t == typeof(short)) System.Runtime.InteropServices.Marshal.WriteInt16(p, v is null ? (short)0 : System.Convert.ToInt16(v));
        else if (t == typeof(int)) System.Runtime.InteropServices.Marshal.WriteInt32(p, v is null ? 0 : System.Convert.ToInt32(v));
        else if (t == typeof(long)) System.Runtime.InteropServices.Marshal.WriteInt64(p, v is null ? 0L : System.Convert.ToInt64(v));
        else if (t == typeof(float)) System.Runtime.InteropServices.Marshal.WriteInt32(p, BitConverter.SingleToInt32Bits(v is null ? 0f : System.Convert.ToSingle(v)));
        else System.Runtime.InteropServices.Marshal.WriteInt64(p, BitConverter.DoubleToInt64Bits(v is null ? 0.0 : System.Convert.ToDouble(v)));
    }

    // If/return on purpose: a single ?:-chain here types the WHOLE expression as
    // double (the branches' best common type — long converts to double, never
    // back), so every integer read was silently boxed as a Double and the emitted
    // IL's unbox-to-long threw InvalidCastException. Each return boxes its own type.
    private static object ReadScalar(IntPtr p, Type t)
    {
        if (t == typeof(short)) return (long)System.Runtime.InteropServices.Marshal.ReadInt16(p);
        if (t == typeof(int)) return (long)System.Runtime.InteropServices.Marshal.ReadInt32(p);
        if (t == typeof(long)) return System.Runtime.InteropServices.Marshal.ReadInt64(p);
        if (t == typeof(float)) return (double)BitConverter.Int32BitsToSingle(System.Runtime.InteropServices.Marshal.ReadInt32(p));
        return BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(p));
    }

    // Quick-win over the interpreted path: invoke each interop method through a
    // compiled thunk (a `(object?[]) => method(...)` delegate) cached per method,
    // instead of paying reflection's `MethodInfo.Invoke` on every call (the
    // dominant cost — invoke is ~100× a direct call). The args are already coerced
    // to the parameter types by ConvertArg, so the thunk only unboxes + calls.
    // Under Native AOT there is no runtime IL generation, so the thunk falls back
    // to reflection invoke — this interpreted path is exactly what runs under AOT,
    // and correctness there matters more than the speed-up. (Tier-1 IL emission —
    // item 2 — bypasses this whole path, emitting the call inline.)
    private static readonly ConcurrentDictionary<MethodInfo, Func<object?[], object?>>
        _invokers = new();

    private static Func<object?[], object?> InvokerFor(MethodInfo m)
        => _invokers.GetOrAdd(m, BuildInvoker);

    private static Func<object?[], object?> BuildInvoker(MethodInfo m)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return args => m.Invoke(null, args);

        var argsParam = Expression.Parameter(typeof(object?[]), "args");
        var ps = m.GetParameters();
        var callArgs = new Expression[ps.Length];
        for (int i = 0; i < ps.Length; i++)
            callArgs[i] = Expression.Convert(
                Expression.ArrayIndex(argsParam, Expression.Constant(i)),
                ps[i].ParameterType);
        Expression call = Expression.Call(m, callArgs);
        Expression body = m.ReturnType == typeof(void)
            ? Expression.Block(call, Expression.Constant(null, typeof(object)))
            : Expression.Convert(call, typeof(object));
        return Expression.Lambda<Func<object?[], object?>>(body, argsParam).Compile();
    }

    private static object EvalBinary(char op, object? l, object? r)
    {
        if (l is double || r is double)
        {
            double a = System.Convert.ToDouble(l), b = System.Convert.ToDouble(r);
            return op switch { '+' => a + b, '-' => a - b, '*' => a * b, '/' => a / b,
                _ => throw new System.NotSupportedException($"native operator '{op}'") };
        }
        long la = System.Convert.ToInt64(l), lb = System.Convert.ToInt64(r);
        return op switch { '+' => la + lb, '-' => la - lb, '*' => la * lb, '/' => la / lb,
            _ => throw new System.NotSupportedException($"native operator '{op}'") };
    }

    private static object? ConvertArg(object? v, System.Type t)
    {
        if (v is null) return null;
        if (t == typeof(string)) return v as string ?? v.ToString();
        if (t == typeof(long)) return System.Convert.ToInt64(v);
        if (t == typeof(int)) return System.Convert.ToInt32(v);
        if (t == typeof(short)) return System.Convert.ToInt16(v);
        if (t == typeof(double)) return System.Convert.ToDouble(v);
        if (t == typeof(float)) return System.Convert.ToSingle(v);
        return v;
    }
}
