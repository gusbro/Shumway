using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// A partial string IS the code list it represents, so a callee that
/// head-matches <c>[H|T]</c> must accept a PSTR argument (GetListSlow's lazy
/// uncons). Inline <c>=/2</c> always handled it (UnifyPstrLis); the head-match
/// path returned false — which broke every Scryer-style string DCG
/// (<c>phrase(nt(X), "text")</c>) and inline-ITE guards over string-bound
/// variables. Also covers the compile-time <c>phrase(M:NT, L, R)</c>
/// expansion: the two DCG arguments belong to the nonterminal INSIDE the
/// qualification, not to <c>':'</c> itself.
/// </summary>
public class PstrHeadMatchTests
{
    [Fact]
    public void HeadListMatch_AcceptsPstrArgument()
    {
        var e = new PrologEngine();
        e.ConsultString("h([H|T], H, T).");
        var s = e.Query("h(\"ab\", H, T).");
        Assert.True(s.Success);
        // The default is chars (ADR-047), so the head is a one-character atom.
        Assert.Equal("a", Assert.IsType<AtomTerm>(s["H"]).Name);
        // The tail stays a (lazy) packed slice representing "b".
        Assert.True(e.Query("h(\"ab\", _, T), T = [b].").Success);
    }

    [Fact]
    public void DcgOverDoubleQuotedString_Matches()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            digits([D|T]) --> digit(D), digits(T).
            digits([D]) --> digit(D).
            digit(D) --> [D], { char_code(D, C), C >= 0'0, C =< 0'9 }.
            """);
        var s = e.Query("phrase(digits(L), \"123\", R), R == [].");
        Assert.True(s.Success);
        Assert.Equal(".(1, .(2, .(3, [])))", s["L"]!.ToString());
    }

    [Fact]
    public void InlineIteGuard_ListMatchOnStringBoundVariable()
    {
        var e = new PrologEngine();
        var s = e.Query("X = \"ab\", ( X = [H|_] -> R = H ; R = no ).");
        Assert.True(s.Success);
        Assert.Equal("a", Assert.IsType<AtomTerm>(s["R"]).Name);
    }

    [Fact]
    public void PhraseWithModuleQualifiedNonterminal_ExpandsInsideTheQualification()
    {
        // phrase(M:NT, L, R) → M:NT'(…, L, R) — appending to ':' itself
        // raised existence_error(':'/4).
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(gm, []).
            letters([C|T]) --> [C], letters(T).
            letters([]) --> [].
            """);
        e.ConsultString("use_it(L, R) :- phrase(gm:letters(L), [a, b], R).");
        var s = e.Query("use_it(L, []).");
        Assert.True(s.Success);
        Assert.Equal(".(a, .(b, []))", s["L"]!.ToString());
    }
}
