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
    internal Func<Engine, bool>? Compiled;
    internal bool CompileTried;
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
    public static bool RunBlock(Engine engine, IReadOnlyList<NativeVar> vars,
        IReadOnlyList<CStmt> stmts, IReadOnlyList<NativeScalarGlobal> scalarGlobals, int regOffset)
    {
        var host = (PrologEngine)engine.Host!;
        Func<string, MethodInfo?> resolveInterop = host.ResolveNativeInterop;
        var index = new Dictionary<string, int>();
        var kindOf = new Dictionary<string, NativeKind>();
        for (int i = 0; i < vars.Count; i++) { index[vars[i].Name] = i; kindOf[vars[i].Name] = vars[i].Kind; }

        var env = new Dictionary<string, object?>();
        foreach (var v in vars)
            if (v.Mode == NativeMode.Input)
                env[v.Name] = ReadInput(host, engine, regOffset + index[v.Name], v.Kind);

        // ADR-022 — seed each scalar `:- c` global from per-engine persistent
        // storage (Arity static-storage). Writes are flushed back through
        // ExecStmt (write-through), so a later failure doesn't revert them.
        var scalarFloat = new Dictionary<string, bool>();
        foreach (var g in scalarGlobals)
        {
            scalarFloat[g.Name] = g.IsFloat;
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

    private static object? ReadInput(PrologEngine host, Engine engine, int reg, NativeKind kind)
    {
        var term = RegisterMarshalling.ReadRegisterAsTerm(engine, reg);
        return kind switch
        {
            NativeKind.Int or NativeKind.Long => host.FromTerm<long>(term),
            NativeKind.Float or NativeKind.Double => host.FromTerm<double>(term),
            NativeKind.String => host.FromTerm<string>(term),
            // ADR-024 — a reftype input is a slot handle (a Foreign cell): unwrap
            // the TermSlot so the block / interop can read or build through it.
            NativeKind.Reftype => term is Shumway.Compiler.Ast.CompoundTerm { Functor: "$foreign", Args.Length: 1 } ct
                && ct.Args[0] is Shumway.Compiler.Ast.IntTerm id
                ? engine.AsForeign<TermSlot>(Shumway.Core.Cell.Foreign((int)id.Value))
                : null,
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
        // Handle = the allocator cell (alloc mode) or the HGlobal struct pointer.
        System.Collections.Generic.List<(TermSlot Slot, IntPtr Handle)>? reftypes = null;
        for (int i = 0; i < c.Args.Count; i++)
        {
            if (i < sig.ParamIsReftype.Length && sig.ParamIsReftype[i]
                && ReftypeName(c.Args[i]) is { } rn)
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
                    handle = NativeReftype.Materialize(slot.Materialize(), enc);
                    structPtr = handle;
                }
                args[i] = structPtr;
                (reftypes ??= new()).Add((slot, handle));
                continue;
            }
            object? v = Eval(c.Args[i], host, env, outputs, resolve);
            args[i] = ConvertArg(v, i < sig.ParamClrTypes.Length ? sig.ParamClrTypes[i] : typeof(IntPtr));
        }
        object? ret = sig.Invoker(res.NativeFn, args);
        if (reftypes is not null)
            foreach (var (slot, handle) in reftypes)
            {
                IntPtr structPtr = alloc is not null
                    ? NativeReftypeAllocator.StructPointer(handle) : handle;
                slot.SetValue(NativeReftype.Dematerialize(structPtr, enc));
                if (alloc is not null) alloc.Free(handle); else NativeReftype.Free(handle);
            }
        return ret;
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
