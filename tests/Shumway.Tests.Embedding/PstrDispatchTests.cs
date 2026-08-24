using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Clause-selection dispatch treats a non-empty packed list as the
/// cons it is (ADR-047/048): it routes to the term-switch list bucket and
/// sub-argument indexing keys through its computed head/tail. Regressing
/// this to the var chain does not change answers — it leaves a choice point
/// behind, which lgtunit-style deterministic tests (nmea, jwt) catch as
/// "succeeded non-deterministically".</summary>
public sealed class PstrDispatchTests
{
    [Fact]
    public void PackedListArgument_SelectsClausesDeterministically()
    {
        var e = new PrologEngine();
        e.ConsultString("q([a|_]). q([b|_]). q([z|_]).");
        // Same determinism through a packed argument as through cons.
        Assert.True(e.Query(
            "'$choice_level'(B0), q([a,b,c]), '$choice_level'(B0).").Success);
        Assert.True(e.Query(
            "'$choice_level'(B0), X = \"abc\", q(X), '$choice_level'(B0).").Success);
    }

    [Fact]
    public void PackedCodesArgument_MatchesConsDeterminismExactly()
    {
        // Integer-sub indexing may or may not fire for this clause shape —
        // the pin is PARITY: a packed codes argument must leave exactly as
        // many choice points behind as the equivalent cons list does.
        var e = new PrologEngine();
        e.ConsultString(
            "r([0'a|_], one). r([0'b|_], two). r([0'z|_], three).\n"
            + "delta(G, D) :- '$choice_level'(B0), call(G), '$choice_level'(B1), "
            + "D is B1 - B0, !.");
        var sol = e.Query(
            "atom_codes(abc, X), delta(r(X, R1), D1), "
            + "delta(r([0'a, 0'b, 0'c], R2), D2), D1 =:= D2, R1 == R2.");
        Assert.True(sol.Success);
        Assert.Equal("one", ((Shumway.Compiler.Ast.AtomTerm)sol["R1"]!).Name);
    }

    [Fact]
    public void EmptyPackedList_StillReachesTheNilClause()
    {
        // The list-bucket routing is guarded on non-empty: an empty packed
        // list IS [] and its clause lives in the const bucket.
        var e = new PrologEngine();
        e.ConsultString("s([], empty). s([_|_], cons).");
        Assert.True(e.Query(
            "atom_chars(a, [C|_]), atom_chars(ab, L), s(L, cons), s([], empty), C == a.").Success);
    }
}
