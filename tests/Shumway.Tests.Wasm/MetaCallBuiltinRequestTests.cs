using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A meta-call whose goal is a DIRECT builtin is requested from
/// inside the module, the way a call_builtin site requests one, instead of
/// stepping aside for the host to dispatch it. The host publishes the
/// resolution (a negative call marker: WasmResumeTable.PublishBuiltin) on
/// the path it was taking anyway; every direct builtin is published up
/// front, so the bare atom path needs no resolution at all.
///
/// <para>Measured on clp(Z): a third of the browser's deopts were one
/// meta-call site whose goal the cache never held, because only jumps were
/// published.</para></summary>
public sealed class MetaCallBuiltinRequestTests(ITestOutputHelper o)
{
    // A variable goal, so the compiler cannot rewrite the call statically;
    // inside a module it travels as '$mqual'(mc, G), which is the cached
    // path, and in user it is the bare path with the atom table.
    private const string Corpus = """
        :- module(mc, [go/1, goa/1, goc/1, gob/2, gof/1, gon/1, gov/1, gova/1, gou/1, cxm/1, c2/2]).
        c2(G, A) :- call(G, A).
        loop(0, _) :- !.
        loop(N, G) :- call(G), N1 is N - 1, loop(N1, G).
        go(N) :- loop(N, true).
        goa(N) :- loop(N, atom(a)).
        goc(N) :- loop(N, atom_codes(abc, _)).
        gov(N) :- loop(N, var(_)).
        loopb(0, _, _) :- !.
        loopb(N, G, R) :- call(G, R), N1 is N - 1, loopb(N1, G, R).
        gob(N, R) :- loopb(N, succ(1), R).
        gof(N) :- ( loop(N, fail) -> fail ; true ).
        gon(N) :- loop(N, call(true)).
        loopv(0, _) :- !.
        loopv(N, G) :- call(G, _), N1 is N - 1, loopv(N1, G).
        gova(N) :- loopv(N, var).
        loopu(0, _) :- !.
        loopu(N, G) :- call(G, X), X == a, N1 is N - 1, loopu(N1, G).
        gou(N) :- loopu(N, =(a)).
        cxm(G) :- call(G), fail.
        """;

    private const string UserCorpus = """
        uloop(0, _) :- !.
        uloop(N, G) :- call(G), N1 is N - 1, uloop(N1, G).
        ugo(N) :- uloop(N, true).
        uga(N) :- uloop(N, atom(a)).
        ugc(N) :- uloop(N, atom_codes(abc, _)).
        ugv(N) :- uloop(N, var(_)).
        ugnv :- \+ uloop(1, var(a)).
        uloopv(0, _) :- !.
        uloopv(N, G) :- call(G, _), N1 is N - 1, uloopv(N1, G).
        ugva(N) :- uloopv(N, var).
        cx(G) :- call(G), fail.
        cxa :- put_attr(X, m, 1), \+ cx(X = 1).
        cxb :- put_attr(X, m, 1), \+ cxm(X = 1).
        """;

    /// <summary>The meta cache key a guard-9 stamp carries, in words:
    /// module atom + 1 above bit 36, the atom-goal flag at 35, the appended
    /// count at 32, the goal's functor or atom id below.</summary>
    internal static string DescribeMetaKey(long key)
    {
        if (key == 0) return "(none)";
        int module = (int)(key >> 36) - 1;
        bool atomGoal = ((key >> 35) & 1) != 0;
        int appended = (int)((key >> 32) & 7);
        int goalKey = (int)key;
        string goal;
        if (atomGoal) goal = (Shumway.Core.AtomTable.GetById(goalKey)?.Name ?? "?") + "/0";
        else
        {
            var (aid, ar) = Shumway.Core.FunctorTable.Lookup(goalKey);
            goal = (Shumway.Core.AtomTable.GetById(aid)?.Name ?? "?") + "/" + ar;
        }
        return $"{Shumway.Core.AtomTable.GetById(module)?.Name ?? "?"}:{goal}"
             + (appended > 0 ? $" +{appended}" : "");
    }

    [Fact]
    public void TheAnswersAreTheInterpreters()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        plain.ConsultString(UserCorpus);
        var (tiered, _) = TieredEngine.Build(Corpus + "\n" + UserCorpus);
        foreach (string goal in new[] { "go(30).", "goa(30).", "goc(30).", "gob(30, R), R == 2.",
                                        "gof(30).", "gon(30).", "gov(30).", "ugo(30).", "uga(30).",
                                        "ugc(30).", "ugv(30).", "ugnv.", "gova(30).", "gou(30).",
                                        "ugva(30).", "\\+ c2(var, a).", "c2(var, _).",
                                        "\\+ c2(=(a), b).", "c2(=(a), X), X == a." })
        {
            Assert.True(plain.Query(goal).Success, goal);
            Assert.True(tiered.Query(goal).Success, goal + " under the module");
        }
    }

    /// <summary>A form that cannot decide, once the callee's arguments are
    /// in the registers, declines to the REQUEST: X0 holds the first
    /// argument by then, so the host's re-dispatch of the instruction is no
    /// longer an option. The pair here is one the module's unifier hands
    /// over, an attributed variable against a value, meta-called from a
    /// module and from user; the answer is the interpreter's, and the
    /// binding wakes nothing (the module has no hook).</summary>
    [Fact]
    public void AFormDeclinesToTheRequestNotToTheHost()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        plain.ConsultString(UserCorpus);
        var (tiered, _) = TieredEngine.Build(Corpus + "\n" + UserCorpus);
        foreach (string goal in new[] { "cxa.", "cxb." })
        {
            Assert.True(plain.Query(goal).Success, goal);
            for (int i = 0; i < 3; i++)          // promoted on the first call, then run there
                Assert.True(tiered.Query(goal).Success, goal + " under the module");
        }
    }

    /// <summary>The counters. Each loop of N meta-calls to a direct builtin
    /// leaves N times as a builtin request and steps aside at most once,
    /// for the resolution that fills the cache -- except a goal the module
    /// open-codes as an inline form (atom/1), which never leaves at all. A
    /// NESTED call is not direct -- call/N needs the dispatcher -- so that
    /// loop still steps aside every time, which is what says the counters
    /// see these sites.</summary>
    [DiagFact]
    public void ADirectBuiltinGoalIsRequestedNotDeopted()
    {
        var (engine, _) = TieredEngine.Build(Corpus + "\n" + UserCorpus);
        foreach (string goal in new[] { "go(3).", "goa(3).", "goc(3).", "gob(3, _).",
                                        "ugo(3).", "ugc(3).", "gon(3)." })
            Assert.True(engine.Query(goal).Success, goal);

        void Check(string goal, string what, int requests)
        {
            WasmTierDelegate.ResetDiag();
            Assert.True(engine.Query(goal).Success, goal);
            o.WriteLine($"{what}: builtin requests={WasmTierDelegate.DiagBuiltins} "
                        + $"deopts={WasmTierDelegate.DiagDeopts}");
            if (requests == 0)
                Assert.True(WasmTierDelegate.DiagBuiltins == 0,
                    $"{what}: {WasmTierDelegate.DiagBuiltins} builtin requests for a goal the module "
                    + "answers itself");
            else
                Assert.True(WasmTierDelegate.DiagBuiltins >= requests - 1,   // the first resolves, then the cache serves
                    $"{what}: {WasmTierDelegate.DiagBuiltins} builtin requests, so the goal did not "
                    + "leave as a request");
            Assert.True(WasmTierDelegate.DiagDeopts <= 1,
                $"{what}: {WasmTierDelegate.DiagDeopts} deopts, so the module keeps asking the host");
        }
        Check("go(300).", "call(true) in a module", 300);
        Check("goa(300).", "call(atom(a)) in a module: an inline form, no exit", 0);
        Check("goc(300).", "call(atom_codes(abc, _)) in a module", 300);
        Check("gob(300, _).", "call(succ(1), R) in a module", 300);
        Check("ugo(300).", "call(true) in user", 300);
        Check("ugc(300).", "call(atom_codes(abc, _)) in user", 300);
        Check("gov(300).", "call(var(_)) in a module: a type test, no exit", 0);
        Check("ugv(300).", "call(var(_)) in user: a type test, no exit", 0);
        Check("gova(300).", "call(var, X) in a module: an atom goal plus one argument, no exit", 0);
        Check("gou(300).", "call(=(a), X) in a module: the unify form on the registers, no exit", 0);

        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query("gon(300).").Success);
        o.WriteLine($"call(call(true)): deopts={WasmTierDelegate.DiagDeopts}, "
                    + $"guard 9 key: {DescribeMetaKey(WasmTierDelegate.DiagGuardFids[9])}");
        Assert.True(WasmTierDelegate.DiagDeopts >= 300,
            $"a nested call stepped aside {WasmTierDelegate.DiagDeopts} times: the corpus "
            + "no longer exercises the path that must still leave");
    }
}
