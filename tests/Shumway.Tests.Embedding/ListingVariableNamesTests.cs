using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A listed clause keeps the variable names its source gave it when
/// it is the clause the program wrote. A clause with variables the compiler
/// made (a grammar rule's list arguments) lists them all named in order of
/// first occurrence, as numbervars/3 names what clause/2 returns. Keeping the
/// source's names beside fresh letters printed two variables as one:
/// `p, [A] --> [A], q.` listed as `p(A, B) :- A=[A | C], ...`, which reads
/// back as a different clause (issue #222).</summary>
public sealed class ListingVariableNamesTests
{
    private static string Listing(string source, string spec)
    {
        var e = new PrologEngine();
        e.ConsultString(source);
        var sw = new StringWriter();
        e.Out = sw;
        Assert.True(e.Query($"listing({spec}).").Success);
        return sw.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void APushbackRule_ListsAsClauseAndNumbervarsNameIt()
    {
        string output = Listing(":- dynamic p/2.\np, [A] --> [A], q.\n", "p/2");
        Assert.Contains("p(A, B) :-\n    A=[C | D],\n    q(D, E),\n    B=[C | E].\n", output);
    }

    [Fact]
    public void AStaticRuleListsTheSame()
    {
        string output = Listing("p, [A] --> [A], q.\n", "p/2");
        Assert.Contains("p(A, B) :-\n    A=[C | D],\n    q(D, E),\n    B=[C | E].\n", output);
    }

    [Fact]
    public void ARuleWithSourceNamesLaterInTheAlphabet()
    {
        string output = Listing("s(C) --> [x], t(C).\n", "s/3");
        Assert.Contains("s(A, B, C) :-\n    B=[x | D],\n    t(A, D, C).\n", output);
    }

    [Fact]
    public void AnAnonymousVariableStaysAnonymous()
    {
        string output = Listing("v(_, X) --> [X].\n", "v/4");
        Assert.Contains("v(_, A, B, C) :-\n    B=[A | C].\n", output);
    }

    [Fact]
    public void AClauseAsWritten_KeepsItsNames()
    {
        string output = Listing("w(X, Y) :- foo(X, Z), bar(Z, Y).\n", "w/2");
        Assert.Contains("w(X, Y) :-\n    foo(X, Z),\n    bar(Z, Y).\n", output);
    }
}
