using Shumway.Compiler.Il;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-049 point 11: a stretch of unifications is atomic with respect
/// to woken goals, as SICStus documents. The goal woken by binding Z records
/// which of R, R2, R3 were bound when it ran. The answers are SICStus 4.8's
/// except where a clause with a frame exits inside the stretch: SICStus wakes
/// there too, which it does not document, and these continue the stretch into
/// the caller. With continuation methods (ADR-061) compiled code answers the
/// same.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class WakeStretchTests : IDisposable
{
    private readonly bool _savedCpsMode = IlPredicateCompiler.CpsMode;

    public void Dispose() => IlPredicateCompiler.CpsMode = _savedCpsMode;

    private const string Corpus = """
        st(R, R2, R3, S) :- b(R, A), b(R2, B), b(R3, C), S = [A, B, C].
        b(V, X) :- ( var(V) -> X = f ; X = b ).
        nop.
        fact3(k).
        e_exit(Z, R) :- fact3(Z), R = done.
        e_neck_inline(k, R) :- R = done.
        e_neck_call(k, R, R2) :- R = done, nop, R2 = x.
        e_cut(k, R, R2) :- R = done, !, R2 = x.
        e_builtin(k, R, R2) :- R = done, atom_length(abc, _), R2 = x.
        e_arith(k, R, R2) :- R = done, 1 < 2, atom(R), R2 = x.
        e_body_eq(Z, R, R2) :- R = done, Z = k, R2 = x, nop.
        e_frame(Z, R) :- nop, Z = k, R = done.
        e_noframe(Z, R) :- Z = k, R = done.
        e_frame_callee(Z, R) :- nop, fact3(Z), R = done.
        e_outer(Z, R) :- e_frame(Z, R).
        c(exit_caller_inline, S) :-
            freeze(Z, st(R, R2, R3, S)), e_exit(Z, R), R2 = x, nop, R3 = y.
        c(neck_inline_body, S) :-
            freeze(Z, st(R, R2, R3, S)), e_neck_inline(Z, R), R2 = x, nop, R3 = y.
        c(neck_then_call, S) :-
            freeze(Z, st(R, R2, R3, S)), e_neck_call(Z, R, R2), R3 = y.
        c(cut, S) :-
            freeze(Z, st(R, R2, R3, S)), e_cut(Z, R, R2), R3 = y.
        c(builtin, S) :-
            freeze(Z, st(R, R2, R3, S)), e_builtin(Z, R, R2), R3 = y.
        c(arith_type_test, S) :-
            freeze(Z, st(R, R2, R3, S)), e_arith(Z, R, R2), nop, R3 = y.
        c(body_unify, S) :-
            freeze(Z, st(R, R2, R3, S)), e_body_eq(Z, R, R2), R3 = y.
        c(top_unify_inline, S) :-
            freeze(Z, st(R, R2, R3, S)), Z = k, R = done, nop, R2 = x, R3 = y.
        c(top_unify_call, S) :-
            freeze(Z, st(R, R2, R3, S)), Z = k, nop, R = done, R2 = x, R3 = y.
        c(frame, S) :- freeze(Z, st(R, R2, R3, S)), e_frame(Z, R), R2 = x, nop, R3 = y.
        c(noframe, S) :- freeze(Z, st(R, R2, R3, S)), e_noframe(Z, R), R2 = x, nop, R3 = y.
        c(frame_callee, S) :- freeze(Z, st(R, R2, R3, S)), e_frame_callee(Z, R), R2 = x, nop, R3 = y.
        c(outer, S) :- freeze(Z, st(R, R2, R3, S)), e_outer(Z, R), R2 = x, nop, R3 = y.
        c(fail_after_stretch, S) :- ( freeze(Z, S = ran), Z = k, fail ; S = never_ran ).
        """;

    // SICStus 4.8 gives [b,f,f] for the four rows marked: a clause with a
    // frame exits inside the stretch.
    private static readonly (string Case, string Expected)[] Cases =
    {
        ("exit_caller_inline", "[b,b,f]"),   // frame exit
        ("neck_inline_body", "[b,b,f]"),
        ("neck_then_call", "[b,f,f]"),
        ("cut", "[b,f,f]"),
        ("builtin", "[b,f,f]"),
        ("arith_type_test", "[b,f,f]"),
        ("body_unify", "[b,b,f]"),
        ("top_unify_inline", "[b,f,f]"),
        ("top_unify_call", "[f,f,f]"),
        ("frame", "[b,b,f]"),                // frame exit
        ("noframe", "[b,b,f]"),
        ("frame_callee", "[b,b,f]"),         // frame exit
        ("outer", "[b,b,f]"),                // frame exit
        ("fail_after_stretch", "never_ran"),
    };

    private static PrologEngine Engine(int threshold)
    {
        var e = new PrologEngine();
        e.UseCoroutining();
        e.IlPromotion.Threshold = threshold;
        e.ConsultString(Corpus);
        return e;
    }

    private static void Agrees(PrologEngine e, string name, string expected)
        => Assert.True(e.Query($"c({name}, S), S == {expected}.").Success,
            $"{name}: the woken goal did not see {expected}");

    [Fact]
    public void TheInterpreter_WakesAtTheEndOfTheStretch()
    {
        var e = Engine(0);
        foreach (var (name, expected) in Cases) Agrees(e, name, expected);
    }

    [Fact]
    public void ContinuationMethods_WakeWhereTheInterpreterDoes()
    {
        IlPredicateCompiler.CpsMode = true;
        var e = Engine(1);
        for (int round = 0; round < 3; round++)
        {
            foreach (var (name, _) in Cases) e.Query($"c({name}, _).");
            Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        }
        foreach (var (name, expected) in Cases) Agrees(e, name, expected);
    }
}
