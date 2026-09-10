using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>Sub-argument indexing (ADR-027 second level, ADR-028
/// structure-keyed): the clause is chosen by a term reached through a
/// bounded path INTO the first argument, not by the argument itself. The
/// three opcodes are switch_on_atom_sub, switch_on_integer_sub and
/// switch_on_structure_sub, and they were the largest group the wasm
/// backend refused — three of six, all in clpfd.
///
/// <para>Tier-0 is the oracle throughout: the shapes below exercise the
/// hop's every outcome — a structure argument, a cons, an index past the
/// arity, a non-compound, an unbound variable, a nested two-hop path — and
/// the tier must answer exactly what the interpreter answers.</para></summary>
public sealed class WasmSubIndexTests(ITestOutputHelper o)
{
    // Heads that share a first-argument functor and differ INSIDE it: what
    // makes the compiler emit a sub-switch rather than a first-level one.
    // Sub-indexing fires where the top-level argument does NOT discriminate
    // (a list in every clause) but a term reached through a bounded path into
    // it does: the car for one hop, the car's own argument for two.
    private const string Corpus = """
        :- public tok/2.
        :- public num/2.
        :- public node/2.
        :- public deep/2.
        :- public bkt/2.
        tok([a|T], T).
        tok([b|T], T).
        tok([c|T], T).
        tok([d|T], T).
        tok([V|T], kept(V, T)).
        num([1|T], T).
        num([2|T], T).
        num([3|T], T).
        num([4|T], T).
        num([V|T], kept(V, T)).
        node([parse(X)|T], p(X, T)).
        node([amp(X)|T], a(X, T)).
        node([lit(X, Y)|T], l(X, Y, T)).
        node([V|T], kept(V, T)).
        deep([w(a)|T], T).
        deep([w(b)|T], T).
        deep([w(c)|T], T).
        deep([V|T], kept(V, T)).
        bkt([parse(X)|T], T) :- X = X.
        bkt([amp(X)|T], T) :- X = X.
        bkt([lit(X)|T], T) :- X = X.
        bkt([[H|R]|T], nested(H, R, T)).
        bkt([V|T], [V|T]).
        """;

    public static TheoryData<string> Goals() => new()
    {
        // atom sub-switch: every key, and every way to miss it
        "tok([a, z], R), R == [z].",
        "tok([d, z], R), R == [z].",
        "tok([zz, z], R), R == kept(zz, [z]).",
        "tok([1, z], R), R == kept(1, [z]).",         // wrong type at the terminal
        "tok([f(1), z], R), R == kept(f(1), [z]).",   // a structure there
        "tok([_, z], R), R = kept(_, [z]).",          // unbound terminal: only the catch-all
        "tok(notalist, R), R == kept_nothing ; true.", // not a cons at all
        "tok([], R), R == kept_nothing ; true.",       // nil: the hop misses
        "findall(R, tok([b, z], R), [[z], kept(b, [z])]).",
        "findall(R, tok([zz], R), [kept(zz, [])]).",
        // integer sub-switch
        "num([2, z], R), R == [z].",
        "num([9, z], R), R == kept(9, [z]).",
        "num([a, z], R), R == kept(a, [z]).",
        "num([2.0, z], R), R == kept(2.0, [z]).",     // a float is not the integer key
        "findall(R, num([4, z], R), [[z], kept(4, [z])]).",
        // structure sub-switch, including the cons key and arity mismatch
        "node([parse(1), z], R), R == p(1, [z]).",
        "node([amp(2), z], R), R == a(2, [z]).",
        "node([lit(3, 4), z], R), R == l(3, 4, [z]).",
        "node([lit(3), z], R), R == kept(lit(3), [z]).",   // same name, other arity
        "node([zz, z], R), R == kept(zz, [z]).",
        "node([[], z], R), R == kept([], [z]).",           // nil is an atom, not a cons
        "findall(R, node([amp(2), z], R), [a(2, [z]), kept(amp(2), [z])]).",
        // structure-keyed buckets (ADR-028): the same three keys, reached
        // through a cons, with a catch-all clause behind them
        "bkt([parse(1), z], R), R == [z].",
        "bkt([lit(3), z], R), R == [z].",
        "bkt([lit(3, 4), z], R), R == [lit(3, 4), z].",   // same name, other arity
        "bkt([zz, z], R), R == [zz, z].",
        // A nested list is a KEY of the table, and it keys by the ATOM id of
        // '.', not by an interned './2' -- get that wrong and this one falls
        // through to the catch-all.
        "bkt([[9, 8], z], R), R == nested(9, [8], [z]).",
        "bkt([[], z], R), R == [[], z].",                 // nil is an atom, not a cons
        "findall(R, bkt([amp(2), z], R), [[z], [amp(2), z]]).",
        // two hops
        "deep([w(b), z], R), R == [z].",
        "deep([w(zz), z], R), R == kept(w(zz), [z]).",
        "deep([w, z], R), R == kept(w, [z]).",             // second hop: not compound
        "deep([v(a), z], R), R == kept(v(a), [z]).",
        "deep([w(a, b), z], R), R == kept(w(a, b), [z]).", // wrong arity at hop one
        "findall(R, deep([w(c), z], R), [[z], kept(w(c), [z])]).",
        // backtracking through the switch with an unbound argument
        "findall(R, tok([a|_], R), L), length(L, N), N == 2.",
        "findall(X-R, (member(X, [a, b, zz]), tok([X, z], R)), L), length(L, 4).",
    };

    private static PrologEngine Tier0()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 0;
        e.ConsultString(Corpus);
        return e;
    }

    private static (PrologEngine Engine, List<WasmGroupMember> Members) Tiered()
    {
        var engine = new PrologEngine();
        var store = engine.IlPromotion;
        store.Threshold = 0;
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

    private static string Run(PrologEngine e, string goal)
    {
        try
        {
            var r = e.Query(goal);
            if (!r.Success) return "fail";
            var parts = new List<string>();
            foreach (var (name, value) in r.Bindings) parts.Add($"{name}={value}");
            parts.Sort(System.StringComparer.Ordinal);
            return "true " + string.Join(",", parts);
        }
        catch (System.Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
    }

    [Theory]
    [MemberData(nameof(Goals))]
    public void Tier0AndWasmAgree(string goal)
    {
        var t0 = Tier0();
        var (w, members) = Tiered();
        _ = Run(w, goal);                       // warm: promote what it calls
        string a = Run(t0, goal);
        string b = Run(w, goal);
        o.WriteLine($"{goal}\n  tier0: {a}\n  wasm : {b}");
        Assert.Equal(a, b);
        Assert.DoesNotContain("Exception", a);
        // ANTI-VACUITY: the predicate must be ON the tier, and it must be
        // there because the sub-switch compiled — the whole point.
        Assert.NotEmpty(members);
    }

    [Fact]
    public void TheSubSwitchesAreWhatGotCompiled()
    {
        // Without this, the theory above could pass with every predicate
        // refused and quietly running on Tier-0.
        var e = new PrologEngine();
        e.ConsultString(Corpus);
        e.Query("true.");
        var env = new EngineWasmCompileEnv();
        var seen = new HashSet<Opcode>();
        int compiled = 0;
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(e))
        {
            var (aid, _) = FunctorTable.Lookup(pred.FunctorId);
            string? name = AtomTable.GetById(aid)?.Name;
            if (name is null) continue;
            if (!(name.EndsWith("tok") || name.EndsWith("num")
                  || name.EndsWith("node") || name.EndsWith("deep") || name.EndsWith("bkt"))) continue;
            foreach (byte op in pred.Bytecode)
                if (op is (byte)Opcode.SwitchOnAtomSub or (byte)Opcode.SwitchOnIntegerSub
                    or (byte)Opcode.SwitchOnStructureSub)
                    seen.Add((Opcode)op);
            WasmPredicateCompiler.CompileGroup(
                new[] { new WasmGroupMember(pred, addr, null) }, env);
            compiled++;
        }
        o.WriteLine($"compiled={compiled} sub-opcodes seen: "
            + string.Join(" ", seen));
        Assert.True(compiled >= 4, $"expected the four predicates, saw {compiled}");
        Assert.Contains(Opcode.SwitchOnAtomSub, seen);
        Assert.Contains(Opcode.SwitchOnIntegerSub, seen);
        Assert.Contains(Opcode.SwitchOnStructureSub, seen);
    }
}
