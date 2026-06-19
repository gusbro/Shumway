using System.Linq;
using System.Reflection;
using Shumway.Builtins;
using Shumway.Compiler.NativeC;
using Shumway.Core;

namespace Shumway.Embedding;

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
    /// <param name="resolveInterop">Maps a C function name to the implementing
    /// <see cref="MethodInfo"/> (a <c>public static</c> of the interop class).
    /// Supplied by the linker's <c>--foreign-dll</c> reflection (step 5); a test
    /// passes its own class.</param>
    public static BuiltinImpl Build(
        IReadOnlyList<NativeVar> vars,
        IReadOnlyList<CStmt> stmts,
        Func<string, MethodInfo?> resolveInterop)
    {
        // register index per Prolog variable name
        var index = new Dictionary<string, int>();
        for (int i = 0; i < vars.Count; i++) index[vars[i].Name] = i;
        var kindOf = vars.ToDictionary(v => v.Name, v => v.Kind);

        return engine =>
        {
            var host = (PrologEngine)engine.Host!;
            var env = new Dictionary<string, object?>();

            // ----- marshal inputs (Prolog → .NET) -----
            foreach (var v in vars)
                if (v.Mode == NativeMode.Input)
                    env[v.Name] = ReadInput(host, engine, index[v.Name], v.Kind);

            // ----- run the statements -----
            var outputs = new Dictionary<string, object?>();
            foreach (var st in stmts)
                ExecStmt(st, env, outputs, resolveInterop);

            // ----- marshal outputs (.NET → Prolog) + unify -----
            foreach (var (name, value) in outputs)
            {
                if (!index.ContainsKey(name)) continue;   // an output that is not a register var
                var term = ToTerm(host, kindOf[name], value);
                if (!RegisterMarshalling.UnifyRegisterWithTerm(engine, index[name], term))
                    return false;
            }
            return true;
        };
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

    private static void ExecStmt(CStmt st, Dictionary<string, object?> env,
        Dictionary<string, object?> outputs, Func<string, MethodInfo?> resolve)
    {
        switch (st)
        {
            case CVarDeclStmt:
                break;   // a local declaration — storage materialises on first assignment
            case CBindStmt b:
                outputs[b.Var] = Eval(b.Value, env, outputs, resolve);
                break;
            case CAssignStmt { Target: CIdentExpr id } a:
                env[id.Name] = Eval(a.Value, env, outputs, resolve);
                break;
            case CCallStmt c:
                Eval(c.Call, env, outputs, resolve);   // side effect (an interop call / intrinsic)
                break;
            default:
                throw new System.NotSupportedException($"native statement {st.GetType().Name}");
        }
    }

    private static object? Eval(CExpr e, Dictionary<string, object?> env,
        Dictionary<string, object?> outputs, Func<string, MethodInfo?> resolve)
    {
        switch (e)
        {
            case CIntExpr n: return n.Value;
            case CStringExpr s: return s.Value;
            case CIdentExpr id:
                return env.TryGetValue(id.Name, out var val) ? val : null;
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
                    if (outVar is not null) outputs[outVar] = src is null ? null : Eval(src, env, outputs, resolve);
                    return null;
                }
            case CCallExpr c:
                return CallInterop(c, env, outputs, resolve);
            default:
                throw new System.NotSupportedException($"native expression {e.GetType().Name}");
        }
    }

    /// <summary>The name of the Prolog variable an intrinsic marshals — its
    /// <c>&amp;Var</c> argument.</summary>
    private static string? StrArg(CCallExpr c)
        => c.Args.OfType<CAddrOfExpr>()
            .Select(a => (a.Operand as CIdentExpr)?.Name)
            .FirstOrDefault(n => n is not null);

    private static object? CallInterop(CCallExpr c, Dictionary<string, object?> env,
        Dictionary<string, object?> outputs, Func<string, MethodInfo?> resolve)
    {
        var m = resolve(c.Name)
            ?? throw new System.InvalidOperationException(
                $"native function '{c.Name}' is not a public static method of the interop class.");
        var ps = m.GetParameters();
        var args = new object?[c.Args.Count];
        for (int i = 0; i < c.Args.Count; i++)
        {
            object? v = Eval(c.Args[i], env, outputs, resolve);
            args[i] = i < ps.Length ? ConvertArg(v, ps[i].ParameterType) : v;
        }
        return m.Invoke(null, args);
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
