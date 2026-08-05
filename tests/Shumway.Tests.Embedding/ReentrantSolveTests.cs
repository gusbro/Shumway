using System;
using Shumway.Embedding;
using Shumway.Compiler.Ast;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The re-entrant host→Prolog solve (<see cref="PrologEngine.SolveOnce(Activation, Term, out Solution)"/>):
/// a foreign predicate, running mid-query with the live activation in hand, calls a Prolog
/// goal back on THAT activation — reusing the linked program instead of a fresh top-level
/// query. This is the C#→Prolog crossing of the C#→main→C#→predX embedding pattern.
/// </summary>
public partial class ReentrantSolveTests
{
    public sealed partial class Bridge
    {
        // bridge_inc(X, Y): from C#, re-entrantly solve inc_pl(X, Y0) and return Y0.
        [PrologPredicate("bridge_inc/2")]
        public static int BridgeInc(Activation engine, int x)
        {
            var host = (PrologEngine)engine.Host!;
            bool ok = host.SolveOnce(engine,
                new CompoundTerm("inc_pl", new Term[] { new IntTerm(x), new VarTerm("Y") }),
                out var sol);
            if (!ok) throw new InvalidOperationException("inc_pl failed");
            return (int)sol.Get<long>("Y");
        }

        // bridge_sum(N, S): re-entrantly solve the recursive Prolog sum_to(N, S0).
        [PrologPredicate("bridge_sum/2")]
        public static long BridgeSum(Activation engine, int n)
        {
            var host = (PrologEngine)engine.Host!;
            host.SolveOnce(engine, "sum_to", new Term[] { new IntTerm(n), new VarTerm("S") }, out var sol);
            return sol.Get<long>("S");
        }

        // bridge_deep(X, Z): SolveOnce a goal that itself routes through a foreign
        // predicate that SolveOnce's again — true C#→Prolog→C#→Prolog nesting.
        [PrologPredicate("bridge_deep/2")]
        public static int BridgeDeep(Activation engine, int x)
        {
            var host = (PrologEngine)engine.Host!;
            host.SolveOnce(engine,
                new CompoundTerm("run_inc", new Term[] { new IntTerm(x), new VarTerm("Y") }), out var sol);
            return (int)sol.Get<long>("Y");
        }

        // bridge_lean(X, Y): the lean typed single-output path (no Solution).
        [PrologPredicate("bridge_lean/2")]
        public static int BridgeLean(Activation engine, int x)
        {
            var host = (PrologEngine)engine.Host!;
            bool ok = host.SolveOnce<long>(engine,
                new CompoundTerm("inc_pl", new Term[] { new IntTerm(x), new VarTerm("Y") }), "Y", out long y);
            if (!ok) throw new InvalidOperationException("inc_pl failed");
            return (int)y;
        }

        // bridge_atom(In, Out): lean path reading a string output.
        [PrologPredicate("bridge_atom/2")]
        public static string BridgeAtom(Activation engine, string s)
        {
            var host = (PrologEngine)engine.Host!;
            host.SolveOnce<string>(engine,
                new CompoundTerm("id_pl", new Term[] { new AtomTerm(s), new VarTerm("Y") }), "Y", out string y);
            return y;
        }

        // bridge_probe(R): R=1 if the re-entrant goal succeeded, R=0 if it failed.
        [PrologPredicate("bridge_probe/1")]
        public static int BridgeProbe(Activation engine)
        {
            var host = (PrologEngine)engine.Host!;
            Term list = new AtomTerm("[]");
            for (int i = 3; i >= 1; i--) list = new CompoundTerm(".", new Term[] { new IntTerm(i), list });
            bool ok = host.SolveOnce(engine, new CompoundTerm("member", new Term[] { new IntTerm(99), list }));
            return ok ? 1 : 0;
        }
    }

    private const string Prog = @"
        inc_pl(X, Y) :- Y is X + 1.
        id_pl(X, X).
        sum_to(0, 0) :- !.
        sum_to(N, S) :- N > 0, N1 is N - 1, sum_to(N1, S0), S is S0 + N.
        run_inc(X, Y) :- bridge_inc(X, Y).
        run_sum(N, S) :- bridge_sum(N, S).
        run_deep(X, Z) :- bridge_deep(X, Z).
        run_lean(X, Y) :- bridge_lean(X, Y).
        run_atom(X, Y) :- bridge_atom(X, Y).
        two_hops(X, Z) :- bridge_inc(X, Y), bridge_inc(Y, Z).
    ";

    private static PrologEngine NewEngine()
    {
        var e = new PrologEngine();
        e.RegisterPredicates(typeof(Bridge));
        e.ConsultString(Prog);
        return e;
    }

    [Fact]
    public void ReturnsBindingFromProlog()
    {
        var s = NewEngine().Query("run_inc(41, Y).");
        Assert.True(s.Success);
        Assert.Equal(42L, s.Get<long>("Y"));
    }

    [Fact]
    public void RunsRecursivePredicate()
    {
        var s = NewEngine().Query("run_sum(100, S).");
        Assert.True(s.Success);
        Assert.Equal(5050L, s.Get<long>("S"));
    }

    [Fact]
    public void NestsCSharpToPrologToCSharpToProlog()
    {
        var s = NewEngine().Query("run_deep(41, Z).");
        Assert.True(s.Success);
        Assert.Equal(42L, s.Get<long>("Z"));
    }

    [Fact]
    public void TwoSequentialReentrantSolvesInOneQuery()
    {
        var s = NewEngine().Query("two_hops(40, Z).");
        Assert.True(s.Success);
        Assert.Equal(42L, s.Get<long>("Z"));
    }

    [Fact]
    public void LeanTypedPathReturnsScalar()
    {
        var s = NewEngine().Query("run_lean(41, Y).");
        Assert.True(s.Success);
        Assert.Equal(42L, s.Get<long>("Y"));
    }

    [Fact]
    public void LeanTypedPathReadsStringOutput()
    {
        var s = NewEngine().Query("run_atom(hello, Y).");
        Assert.True(s.Success);
        Assert.Equal("hello", s.Get<string>("Y"));
    }

    [Fact]
    public void ReportsInnerFailure()
    {
        var s = NewEngine().Query("bridge_probe(R).");
        Assert.True(s.Success);
        Assert.Equal(0L, s.Get<long>("R"));
    }

    [Fact]
    public void SolveOnceOutsideAQueryThrows()
    {
        var e = NewEngine();
        // A brand-new activation created outside any running query has no ReentrantSolve
        // hook wired, so the API guards against misuse.
        var idle = new Activation();
        Assert.Throws<InvalidOperationException>(() =>
            e.SolveOnce(idle, new AtomTerm("true")));
    }
}
