using Shumway.Compiler.Il;
using Shumway.Embedding;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-063: a head stream under an image is emitted in the
/// read-only form on the IL tier, the unify pointer in a local and no
/// mode test; a nested cell that comes unbound is bound to a copy of its
/// block of the image. The answers are the streams' and the copies'.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class ReadStreamTests
{
    // Every form: an argument's own stream with two deferred parts and an
    // inline chain (rd), a list and its chain (rl, rl2), boxed literals
    // (rb), anonymous variables (rv), permanent variables (ry), a part with
    // an image of its own and parts under it (rp), an attributed variable
    // at a nested cell (rat).
    private const string Program = """
        :- public rd/2, rl/2, rl2/2, rb/2, rv/2, ry/2, rp/2, rat/2.
        :- use_module(library(coroutining)).
        rd(f(g(X, Y), h(Y, 1), i(a, b, c, d, e, f)), X-Y).
        rl([a, b, c, d, e, f, g, h, i, j], 1).
        rl2([X, b, c, d, e, f, g, h | T], X-T).
        rb(f(1.5, "str", a, b, c, d, e), 1).
        rv(f(_, _, _, a, b, c, d, e), 1).
        ry(f(X, g(X, Y), a, b, c, d), Z) :- id(Y, Y1), id(X, X1), Z = X1-Y1.
        rp(k(f(g(X), h(X), i(Z, 1, 2, 3, 4, 5, 6, 7, 8, 9)), z), R) :- R = X-Z.
        rat(V, R) :- freeze(V, R = woke), rd(f(V, h(1, 1), i(a, b, c, d, e, f)), _).
        id(X, X).
        """;

    private static readonly (string Goal, string Var)[] Goals =
    {
        ("rd(f(g(1, 2), h(2, 1), i(a, b, c, d, e, f)), R).", "R"),
        ("rd(f(g(1, 2), H, i(a, b, c, d, e, f)), R), R2 = H-R.", "R2"),
        ("rd(f(G, h(2, 1), i(a, b, c, d, e, f)), R), R = X-_, X = 7, R2 = G-R.", "R2"),
        ("rd(f(g(1, 2), h(2, 1), I), R), R2 = I-R.", "R2"),
        ("rd(f(g(1, 2), h(2, 1), i(a, b, c, d, e, W)), R), R2 = W-R.", "R2"),
        ("rd(f(g(1, 2), h(2, V), i(a, b, c, d, e, f)), R), R2 = V-R.", "R2"),
        ("rd(F, R), F = f(g(1, 2), _, _), R2 = F-R.", "R2"),
        ("\\+ rd(f(g(1, 2), h(3, 1), i(a, b, c, d, e, f)), _), R = yes.", "R"),
        ("\\+ rd(f(g(1, 2), h(2, 1), i(a, b, c, d, e, x)), _), R = yes.", "R"),
        ("\\+ rd(f(g(1, 2), h(2, 1), j(a, b, c, d, e, f)), _), R = yes.", "R"),
        ("\\+ rd(f(g(1, 2), k(2, 1), i(a, b, c, d, e, f)), _), R = yes.", "R"),
        ("\\+ rd(f(g(1, 2), h(2, 1), 5), _), R = yes.", "R"),
        ("\\+ rd(f(g(1, 2), 5, i(a, b, c, d, e, f)), _), R = yes.", "R"),
        ("rl([a, b, c, d, e, f, g, h, i, j], R).", "R"),
        ("rl([a, b | T], R), R2 = T-R.", "R2"),
        ("rl(L, R), R2 = L-R.", "R2"),
        ("rl([a, b, c, d, e, f, g, h, i | T], R), R2 = T-R.", "R2"),
        ("\\+ rl([a, b, c, d, e, f, g, h, i, k], _), R = yes.", "R"),
        ("\\+ rl([a, b, c, d, e, f, g, h, i], _), R = yes.", "R"),
        ("\\+ rl([a, b, c, d, e, f, g, h, i, j, k], _), R = yes.", "R"),
        ("rl2([1, b, c, d, e, f, g, h, 9, 10], R).", "R"),
        ("rl2([1, b | T], R), T = [c, d, e, f, g, h | U], U = end, R2 = R-U.", "R2"),
        ("rl2(L, R), L = [1, b, c, d, e, f, g, h | end], R2 = L-R.", "R2"),
        ("rb(f(1.5, \"str\", a, b, c, d, e), R).", "R"),
        ("rb(f(X, Y, a, b, c, d, e), R), R2 = X-Y-R.", "R2"),
        ("\\+ rb(f(2.5, \"str\", a, b, c, d, e), _), R = yes.", "R"),
        ("\\+ rb(f(1.5, \"stx\", a, b, c, d, e), _), R = yes.", "R"),
        ("rv(f(1, 2, 3, a, b, c, d, e), R).", "R"),
        ("rv(f(_, _, _, a, b, c, d, e), R).", "R"),
        ("rv(F, R), F = f(1, 2, 3, _, _, _, _, _), R2 = F-R.", "R2"),
        ("\\+ rv(f(1, 2, 3, a, b, c, d, x), _), R = yes.", "R"),
        ("ry(f(1, g(1, 2), a, b, c, d), R).", "R"),
        ("ry(f(1, G, a, b, c, d), R), G = g(_, 5), R2 = G-R.", "R2"),
        ("ry(F, R), F = f(3, g(_, 4), _, _, _, _), R2 = F-R.", "R2"),
        ("\\+ ry(f(1, g(2, 2), a, b, c, d), _), R = yes.", "R"),
        ("rp(k(f(g(1), h(1), i(2, 1, 2, 3, 4, 5, 6, 7, 8, 9)), z), R).", "R"),
        ("rp(k(F, z), R), F = f(g(1), _, i(2, _, _, _, _, _, _, _, _, _)), R2 = F-R.", "R2"),
        ("rp(k(f(G, h(1), i(2, 1, 2, 3, 4, 5, 6, 7, 8, 9)), z), R), R2 = G-R.", "R2"),
        ("rp(k(f(g(1), H, i(2, 1, 2, 3, 4, 5, 6, 7, 8, 9)), z), R), R2 = H-R.", "R2"),
        ("rp(k(f(g(1), h(1), I), z), R), R2 = I-R.", "R2"),
        ("rp(k(f(g(P), h(Q), i(R1, 1, 2, 3, 4, 5, 6, 7, 8, 9)), z), W), P = 1, R1 = 2, R = Q-W.", "R"),
        ("\\+ rp(k(f(g(1), h(2), i(2, 1, 2, 3, 4, 5, 6, 7, 8, 9)), z), _), R = yes.", "R"),
        ("rat(V, R).", "R"),
        ("set_prolog_flag(occurs_check, true), ( rd(f(g(1, W), W, i(a, b, c, d, e, f)), _) -> R = yes ; R = no ), set_prolog_flag(occurs_check, false).", "R"),
        ("set_prolog_flag(occurs_check, true), ( rd(f(g(1, 2), W, i(a, b, c, d, e, f)), X) -> R = W-X ; R = no ), set_prolog_flag(occurs_check, false).", "R"),
    };

    private static readonly string[] Predicates = { "rd/2", "rl/2", "rl2/2", "rb/2", "rv/2", "ry/2", "rp/2" };

    private static string Answer(PrologEngine e, string goal, string var)
    {
        var r = e.Query(goal);
        return r.Success ? $"{var} = {r.Bindings[var]}" : "false";
    }

    // The program with no image: every term built and matched by instructions.
    private static PrologEngine Reference()
    {
        int threshold = ClauseCompiler.ImageThreshold;
        ClauseCompiler.ImageThreshold = 0;
        try
        {
            var e = new PrologEngine();
            e.IlPromotion.Threshold = 0;
            e.ConsultString(Program);
            return e;
        }
        finally { ClauseCompiler.ImageThreshold = threshold; }
    }

    private static PrologEngine Tiered()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.IlPromotion.BackgroundCompilation = false;
        e.ConsultString(Program);
        return e;
    }

    private static bool Promoted(PrologEngine e, string indicator)
    {
        foreach (int fid in e.IlPromotion.PromotedFunctorIds())
        {
            var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
            string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
            if ($"{name[(name.LastIndexOf('$') + 1)..]}/{arity}" == indicator) return true;
        }
        return false;
    }

    [Fact]
    public void TheReadFormGivesTheStreamsAnswers()
    {
        bool prev = IlPredicateCompiler.ReadOnlyStreams;
        try
        {
            var reference = Reference();
            IlPredicateCompiler.ReadOnlyStreams = false;
            var helpers = Tiered();
            IlPredicateCompiler.ReadOnlyStreams = true;
            int before = IlPredicateCompiler.ReadStreamUnits;
            var read = Tiered();
            // Twice: the first pass promotes, the second runs the promoted code.
            for (int pass = 0; pass < 2; pass++)
                foreach (var (goal, v) in Goals)
                {
                    string expected = Answer(reference, goal, v);
                    Assert.Equal(expected, Answer(helpers, goal, v));
                    Assert.Equal(expected, Answer(read, goal, v));
                }
            foreach (string pi in Predicates)
            {
                Assert.True(Promoted(helpers, pi), $"{pi} did not promote (helpers)\n{helpers.IlPromotion.DescribePromotionState()}");
                Assert.True(Promoted(read, pi), $"{pi} did not promote (read form)\n{read.IlPromotion.DescribePromotionState()}");
            }
            // ANTI-VACUITY: the read form was emitted for the units.
            Assert.True(IlPredicateCompiler.ReadStreamUnits - before >= Predicates.Length,
                $"read-only units emitted: {IlPredicateCompiler.ReadStreamUnits - before}");
        }
        finally { IlPredicateCompiler.ReadOnlyStreams = prev; }
    }

    [Fact]
    public void TheWorkersCompilesCarryTheImages()
    {
        // The worker reads the pool the engine thread captured, as it reads
        // the float pool: the read form is emitted there too.
        bool prev = IlPredicateCompiler.ReadOnlyStreams;
        try
        {
            IlPredicateCompiler.ReadOnlyStreams = true;
            var reference = Reference();
            int before = IlPredicateCompiler.ReadStreamUnits;
            var e = new PrologEngine();
            e.IlPromotion.Threshold = 1;
            e.ConsultString(Program);
            foreach (var (goal, v) in Goals) Answer(e, goal, v);
            e.IlPromotion.WaitForPendingPromotions();
            foreach (var (goal, v) in Goals)
                Assert.Equal(Answer(reference, goal, v), Answer(e, goal, v));
            foreach (string pi in Predicates)
                Assert.True(Promoted(e, pi), $"{pi} did not promote\n{e.IlPromotion.DescribePromotionState()}");
            // The optimized compiles the worker runs after the profile come
            // in their own time: some, not all, by now.
            Assert.True(IlPredicateCompiler.ReadStreamUnits > before,
                $"read-only units emitted on the worker: {IlPredicateCompiler.ReadStreamUnits - before}");
        }
        finally { IlPredicateCompiler.ReadOnlyStreams = prev; }
    }

    [Fact]
    public void TheSwitchKeepsTheHelpers()
    {
        bool prev = IlPredicateCompiler.ReadOnlyStreams;
        try
        {
            IlPredicateCompiler.ReadOnlyStreams = false;
            int before = IlPredicateCompiler.ReadStreamUnits;
            var e = Tiered();
            foreach (var (goal, v) in Goals) Answer(e, goal, v);
            Assert.True(Promoted(e, "rd/2"), e.IlPromotion.DescribePromotionState());
            Assert.Equal(before, IlPredicateCompiler.ReadStreamUnits);
        }
        finally { IlPredicateCompiler.ReadOnlyStreams = prev; }
    }
}
