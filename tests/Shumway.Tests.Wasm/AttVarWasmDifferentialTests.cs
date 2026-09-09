using Shumway.Builtins;
using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>The attributed-variable corpus of the IL differential, run
/// against the WASM tier with Tier-0 as the oracle. An attvar cell exists
/// only at its home — Deref does not follow AttVar — so a tier that copies
/// one leaves an ORPHAN the attribute machinery cannot see; the probe scans
/// for exactly that after every shape.</summary>
public sealed class AttVarWasmDifferentialTests(ITestOutputHelper o)
{
    private static readonly object _regLock = new();
    private static bool _registered;

    private static void EnsureProbe()
    {
        lock (_regLock)
        {
            if (_registered) return;
            StandardBuiltins.EnsureRegistered();
            BuiltinsRegistry.Register("$orphan_attvar", 1,
                engine => engine.UnifyRegisterWithCell(
                    0, Cell.Int(engine.FindOrphanAttVar())));
            _registered = true;
        }
    }

    private const string Corpus = """
        :- use_module(library(coroutining)).
        :- public samep/2.
        :- public wrap/2.
        :- public unwrap/2.
        :- public through/3.
        :- public twice/2.
        samep(X, X).
        wrap(X, w(X)).
        unwrap(w(X), X).
        through(X, Y, p(X, Y)).
        twice(X, pair(X, X)).
        """;

    public static TheoryData<string, string> Shapes() => new()
    {
        { "put_attr(X, m2, hello), samep(X, Y), get_attr(Y, m2, V), V == hello.",
          "get_value: attvar as the bound side" },
        { "put_attr(X, m2, hello), samep(Y, X), get_attr(Y, m2, V), V == hello.",
          "get_value: attvar as the free side" },
        { "put_attr(X, m2, hello), wrap(X, W), unwrap(W, Y), get_attr(Y, m2, V), V == hello.",
          "unify_variable / unify_value through a structure" },
        { "put_attr(X, m2, a), twice(X, P), P = pair(A, B), get_attr(A, m2, V), get_attr(B, m2, W), V == W.",
          "one attvar reaching two argument positions" },
        { "put_attr(X, m2, a), through(X, X, T), T = p(L, R), L == R.",
          "the same attvar twice in one build" },
        { "put_attr(X, m2, a), samep(X, Y), put_attr(Y, m2, b), get_attr(X, m2, V), V == b.",
          "writing the attribute through the alias" },
        { "put_attr(X, m2, a), del_attr(X, m2), samep(X, Y), ( get_attr(Y, m2, _) -> fail ; true ).",
          "del_attr then alias" },
        { "put_attr(X, m2, a), copy_term(X, Y), ( get_attr(Y, m2, _) -> fail ; true ).",
          "a copy drops attributes (engine rule)" },
        { "dif(X, a), samep(X, Y), Y = b.",
          "dif through get_value, satisfied" },
        { "dif(X, a), samep(X, Y), ( Y = a -> fail ; true ).",
          "dif through get_value, violated" },
        { "freeze(X, true), samep(X, Y), Y = 1, X == 1.",
          "freeze woken through the alias" },
        { "freeze(X, fail), samep(X, Y), ( Y = 1 -> fail ; true ).",
          "a woken goal that fails undoes the binding" },
        { "put_attr(X, m2, a), findall(Z, samep(X, Z), [_]).",
          "attvar through findall's copy" },
        { "put_attr(X, m2, a), samep(X, Y), '$orphan_attvar'(O), O == -1.",
          "no orphan after aliasing" },
    };

    private static PrologEngine Tier0()
    {
        EnsureProbe();
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 0;
        e.ConsultString(Corpus);
        return e;
    }

    private static (PrologEngine Engine, List<WasmGroupMember> Members) Wasm()
    {
        EnsureProbe();
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;                    // the IL tier stands aside
        var world = new DesktopWasmWorld();
        var env = new EngineWasmCompileEnv();
        var members = new List<WasmGroupMember>();

        void Install()
        {
            var entry = WasmPredicateCompiler.CompileGroup(members, env);
            var addrMap = new Dictionary<int, int>(members.Count);
            foreach (var mm in members) addrMap[mm.Predicate.FunctorId] = mm.Bias;
            world.InstallGroup(entry.Module, entry.EntryCursorByFid,
                entry.CursorByAddress, addrMap, entry.RegisterDemand);
        }

        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = 1,
            Promoter = (pred, linkedBase) =>
            {
                var m = new WasmGroupMember(pred, linkedBase,
                    store.FloatPoolProvider?.Invoke(pred.FunctorId));
                members.Add(m);
                try
                {
                    Install();
                    return new WasmTierDelegate(pred.FunctorId, world).Invoke;
                }
                catch (WasmCompileException)
                {
                    members.Remove(m);
                    if (members.Count > 0) Install();
                    return null;
                }
            },
        };
        engine.ConsultString(Corpus);
        return (engine, members);
    }

    private static (bool Ok, string Detail) Run(PrologEngine e, string goal)
    {
        try
        {
            var r = e.Query(goal);
            if (!r.Success) return (false, "fail");
            var parts = new List<string>();
            foreach (var (name, value) in r.Bindings) parts.Add($"{name}={value}");
            parts.Sort(System.StringComparer.Ordinal);
            return (true, string.Join(",", parts));
        }
        catch (System.Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Tier0AndWasmAgree(string goal, string what)
    {
        var t0 = Tier0();
        var (w, members) = Wasm();

        WasmTierDelegate.DiagOrphanScan = true;
        try
        {
            _ = Run(w, goal);                  // warm: promote what the goal calls
            var a = Run(t0, goal);
            var b = Run(w, goal);
            o.WriteLine($"{what}\n  tier0: {a.Ok} {a.Detail}\n  wasm : {b.Ok} {b.Detail}"
                + $"\n  group: [{string.Join(" ", MemberNames(members))}]");
            Assert.Equal(a.Ok, b.Ok);
            Assert.Equal(a.Detail, b.Detail);
            Assert.False(a.Detail.Contains("Exception"), $"tier0 raised: {a.Detail}");

            // ANTI-VACUITY: a goal calling a corpus predicate must have run ON
            // the tier, or this compares Tier-0 with Tier-0.
            bool callsCorpus = goal.Contains("samep(") || goal.Contains("wrap(")
                || goal.Contains("unwrap(") || goal.Contains("through(")
                || goal.Contains("twice(");
            if (callsCorpus) Assert.NotEmpty(members);
        }
        finally { WasmTierDelegate.DiagOrphanScan = false; }

        foreach (var (label, engine) in new[] { ("tier0", t0), ("wasm", w) })
        {
            var probe = engine.Query("'$orphan_attvar'(O).");
            Assert.True(probe.Success, $"{label}: the probe must answer");
            Assert.Equal("-1", probe.Bindings["O"].ToString());
        }
    }

    /// <summary>The reported shape: a constraint library with its own hook,
    /// promoted whole, with the wakeup drain running on the tier. This is
    /// what crashed with "reserved_invalid opcode ... bytecode corruption"
    /// before the engine stopped moving addresses on a consult.</summary>
    public static TheoryData<string, string> ClpfdShapes() => new()
    {
        { "X in 1..3.", "a domain, nothing woken" },
        { "X in 1..3, X #> 1.", "a propagator on a domain" },
        { "X in 1..3, X #> 1, X = 2.", "binding an attvar the library owns" },
        { "X in 1..3, X #> 1, ( X = 1 -> fail ; true ).", "a binding the constraint vetoes" },
        { "X in 1..3, Y in 1..3, X #< Y, X = 1, Y = 2.", "two constrained variables" },
        { "X in 1..3, X #> 1, findall(V, (member(V, [1,2,3]), V #>= X), L), L == [2,3].",
          "the constraint inside findall" },
        { "length(Qs, 4), Qs ins 1..4, all_distinct(Qs), labeling([], Qs), Qs = [_,_,_,_].",
          "all_distinct plus labeling" },
    };

    [Theory]
    [MemberData(nameof(ClpfdShapes))]
    public void Tier0AndWasmAgreeOnClpfd(string goal, string what)
    {
        const string prog = ":- use_module(library(clpfd)).";
        EnsureProbe();
        var t0 = new PrologEngine();
        t0.IlPromotion.Threshold = 0;
        t0.ConsultString(prog);

        var (w, members) = Wasm();
        w.ConsultString(prog);

        WasmTierDelegate.DiagOrphanScan = true;
        try
        {
            _ = Run(w, goal);
            var a = Run(t0, goal);
            var b = Run(w, goal);
            o.WriteLine($"{what} :: tier0={a.Ok} {a.Detail} :: wasm={b.Ok} {b.Detail}"
                + $" :: group members={members.Count}");
            Assert.Equal(a.Ok, b.Ok);
            Assert.Equal(a.Detail, b.Detail);
            Assert.False(a.Detail.Contains("Exception"), $"tier0 raised: {a.Detail}");
            // The library must actually be ON the tier, or this proves nothing.
            Assert.True(members.Count > 5,
                $"expected the library in the group, saw {members.Count}");
        }
        finally { WasmTierDelegate.DiagOrphanScan = false; }

        foreach (var (label, engine) in new[] { ("tier0", t0), ("wasm", w) })
        {
            var probe = engine.Query("'$orphan_attvar'(O).");
            Assert.True(probe.Success, $"{label}: the probe must answer");
            Assert.Equal("-1", probe.Bindings["O"].ToString());
        }
    }

    private static List<string> MemberNames(List<WasmGroupMember> members)
    {
        var names = new List<string>();
        foreach (var m in members)
        {
            var (aid, ar) = FunctorTable.Lookup(m.Predicate.FunctorId);
            names.Add($"{AtomTable.GetById(aid)?.Name}/{ar}");
        }
        return names;
    }
}
