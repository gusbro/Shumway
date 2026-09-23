using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>An attribute MODIFIED inside a predicate that cuts, and then
/// backtracked over.
///
/// <para>This is the shape the clp(Z) divergence was traced to. At the same
/// cut, the interpreter's extra trail drops an AttrModify entry --
/// Activation.Cut compacts, dropping entries a cut has made unreachable --
/// and the tier's keeps it, because the emitter's cut lowers B and does
/// nothing else. The question that decides which of them is wrong is
/// whether that entry's restore is OBSERVABLE, and this asks it.</para>
///
/// <para>The oracle is Scryer, running the same question over the library
/// this came from: <c>X in 1..9, ( narrow(X), fail ; true ), fd_dom(X, D)</c>
/// answers <c>D = 1..9</c> there, in all three shapes -- narrowed under a
/// cut, under a cut that is not the last goal, and undone by an exception
/// rather than a failure, which is what clp(Z)'s with_local_attributes
/// does. So the narrowing must NOT survive the backtrack.</para>
///
/// <para>Asked here without fd_dom, which this engine's own clpfd need not
/// have: if the narrowing survived, the variable could no longer take a
/// value the original domain allowed.</para></summary>
public sealed class CutThenBacktrackRestoresAttributesTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- use_module(library(clpfd)).
        narrow(X) :- X #< 5, !.
        narrow2(X) :- X #< 5, !, true.
        % 7 is in 1..9 and not in 1..4, so it answers whether the narrowing
        % was undone.
        cut_then_fail(R) :-
            X in 1..9, ( narrow(X), fail ; true ),
            ( X #= 7 -> R = restored ; R = kept ).
        cut_not_last(R) :-
            X in 1..9, ( narrow2(X), fail ; true ),
            ( X #= 7 -> R = restored ; R = kept ).
        % The undo done by an exception, which is the shape clp(Z) uses.
        thrown(R) :-
            X in 1..9,
            catch(( X #< 5, throw(undo) ), undo, true),
            ( X #= 7 -> R = restored ; R = kept ).
        % ANTI-VACUITY: no backtrack, so the narrowing MUST stand.
        kept(R) :- X in 1..9, X #< 5, ( X #= 7 -> R = restored ; R = kept ).
        """;

    [Theory]
    [InlineData("cut_then_fail", "restored")]
    [InlineData("cut_not_last", "restored")]
    [InlineData("thrown", "restored")]
    [InlineData("kept", "kept")]
    public void ABacktrackUndoesANarrowingTakenUnderACut(string goal, string expected)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        string p0 = Answer(plain, goal);
        // Scryer answers "restored" for the first three, and the fourth is
        // the check that this probe can report the other outcome at all.
        Assert.Equal(expected, p0);

        var (tiered, _) = TieredEngine.Build(Corpus);
        string t = Answer(tiered, goal);
        o.WriteLine($"{goal}: tier0={p0} tier={t}");
        Assert.Equal(p0, t);
    }

    private static string Answer(PrologEngine e, string goal)
    {
        try
        {
            var r = e.Query($"{goal}(R).");
            if (!r.Success) return "failed";
            foreach (var b in r.Bindings) if (b.Key == "R") return b.Value.ToString()!;
            return "?";
        }
        catch (System.Exception ex) { return ex.Message; }
    }
}
