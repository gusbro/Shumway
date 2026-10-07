using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>call/1 of an n-goal conjunction checks and converts the body once,
/// at the boundary, and then runs it piece by piece through the '$call'/2 of
/// the control helpers. Re-checking the rest of the body at every piece made
/// the whole call quadratic: minutes for the length below in a Debug build,
/// against a fraction of a second when linear. The deadline sits between the
/// two by a wide margin either way.</summary>
public sealed partial class LongConjunctionTests(ITestOutputHelper o)
{
    private const int Length = 40_000;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private const string Corpus = """
        :- public run/1.
        run(G) :- call(G).
        lconj(1, true) :- !.
        lconj(N, (B, true)) :- N1 is N - 1, lconj(N1, B).
        rconj(1, true) :- !.
        rconj(N, (true, B)) :- N1 is N - 1, rconj(N1, B).
        """;

    private void WithinDeadline(PrologEngine e, string query)
    {
        bool? ok = null;
        Exception? error = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t = new Thread(() =>
        {
            try { ok = e.Query(query).Success; }
            catch (Exception ex) { error = ex; }
        }, 64 * 1024 * 1024) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(Deadline), $"{query} did not finish within {Deadline.TotalSeconds} s");
        o.WriteLine($"{query} {sw.ElapsedMilliseconds} ms");
        Assert.Null(error);
        Assert.True(ok, query);
    }

    [Theory]
    [InlineData("lconj")]
    [InlineData("rconj")]
    public void Tier0(string shape)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 0;
        e.ConsultString(Corpus);
        WithinDeadline(e, $"{shape}({Length}, B), run(B).");
    }

    [Theory]
    [InlineData("lconj")]
    [InlineData("rconj")]
    public void Il(string shape)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString(Corpus);
        Assert.True(e.Query("run(true).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        Assert.True(e.IlPromotion.IsPromoted(FunctorTable.Intern(AtomTable.Intern("run").Id, 1)),
            "run/1 did not promote: this would be Tier-0 again");
        WithinDeadline(e, $"{shape}({Length}, B), run(B).");
    }

    public sealed partial class Bridge
    {
        // solve_backtracking: (member(X, [1, 2]), X > 1) needs member/2's
        // second answer, which a conjunction run goal by goal, each once,
        // never asked for.
        [PrologPredicate("solve_backtracking/0")]
        public static bool SolveBacktracking(Activation engine)
        {
            var x = new VarTerm("X");
            Term list = new CompoundTerm(".", new Term[] { new IntTerm(1), new CompoundTerm(".", new Term[] { new IntTerm(2), new AtomTerm("[]") }) });
            Term body = new CompoundTerm(",", new Term[]
                { new CompoundTerm("member", new Term[] { x, list }), new CompoundTerm(">", new Term[] { x, new IntTerm(1) }) });
            return ((PrologEngine)engine.Host!).SolveOnce(engine, body);
        }

        // solve_long(N): from C#, solve a left-nested conjunction of N goals
        // on the live activation (the in-engine meta-call).
        [PrologPredicate("solve_long/1")]
        public static bool SolveLong(Activation engine, int n)
        {
            Term body = new AtomTerm("true");
            for (int i = 1; i < n; i++)
                body = new CompoundTerm(",", new[] { body, new AtomTerm("true") });
            return ((PrologEngine)engine.Host!).SolveOnce(engine, body);
        }

        // solve_late_conjunction: X = (3, foo), X. The conjunction X stands
        // for is bound after the outer one was checked, so it is checked
        // when reached: type_error(callable, (3, foo)).
        [PrologPredicate("solve_late_conjunction/0")]
        public static bool SolveLateConjunction(Activation engine)
        {
            var x = new VarTerm("X");
            Term late = new CompoundTerm(",", new Term[] { new IntTerm(3), new AtomTerm("foo") });
            Term body = new CompoundTerm(",", new Term[]
                { new CompoundTerm("=", new Term[] { x, late }), x });
            return ((PrologEngine)engine.Host!).SolveOnce(engine, body);
        }
    }

    [Fact]
    public void SolveOnce()
    {
        var e = new PrologEngine();
        e.RegisterPredicates<Bridge>();
        WithinDeadline(e, $"solve_long({Length}).");
    }

    [Fact]
    public void SolveOnce_BacktracksWithinAConjunction()
    {
        var e = new PrologEngine();
        e.RegisterPredicates<Bridge>();
        Assert.True(e.Query("solve_backtracking.").Success);
    }

    [Fact]
    public void SolveOnce_ChecksAConjunctionBoundOnTheWay()
    {
        var e = new PrologEngine();
        e.RegisterPredicates<Bridge>();
        Assert.True(e.Query(
            "catch(solve_late_conjunction, error(type_error(callable, C), _), true), C == (3, foo).").Success);
    }
}
