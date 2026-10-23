using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>findall/3 over a cyclic solution. The copy that takes a solution
/// off the heap registers each structure and list pair before it copies what is
/// inside, so a term that cycles back into itself (L = [a|L]) closes the cycle in
/// the copy instead of walking it until memory runs out, an
/// OutOfMemoryException that kills the engine. ISO: an answer is a copy, and
/// copying a rational tree yields that rational tree.</summary>
public class CyclicFindallTests
{
    private static PrologEngine NewEngine()
        => new() { Out = new System.IO.StringWriter() };

    [Fact]
    public void ASelfCyclicListRoundTrips()
    {
        // The exact shape that used to OOM.
        var e = NewEngine();
        Assert.True(e.Query(
            "L = [a|L], findall(X, X = L, [R]), R == L.").Success);
    }

    [Fact]
    public void ACycleReEnteringAtAnInnerConsRoundTrips()
    {
        var e = NewEngine();
        Assert.True(e.Query(
            "L = [a|T], T = [b|T], findall(X, X = L, [R]), R == L.").Success);
    }

    [Fact]
    public void ACyclicCompoundWithSharingRoundTrips()
    {
        var e = NewEngine();
        Assert.True(e.Query(
            "X = f(X), findall(Y, Y = g(X, X), [R]), R == g(X, X).").Success);
    }

    [Fact]
    public void EverySolutionKeepsItsOwnCycle()
    {
        var e = NewEngine();
        Assert.True(e.Query(
            "L = [a|L], findall(X, member(X, [L, L]), [A, B]), A == L, B == L.")
            .Success);
    }

    [Fact]
    public void TheAstFallbackLegKeepsCyclesToo()
    {
        // A float head forces the value-leaf fallback (TermReader →
        // Materializer), which carries the cycle as a knot.
        var e = NewEngine();
        Assert.True(e.Query(
            "L = [1.5|L], findall(X, X = L, [R]), R == L.").Success);
    }

    [Fact]
    public void BagofSharesTheMachinery()
    {
        var e = NewEngine();
        Assert.True(e.Query(
            "L = [a|L], bagof(X, X = L, [R]), R == L.").Success);
    }

    [Fact]
    public void PlainFindallIsUntouched()
    {
        var e = NewEngine();
        Assert.True(e.Query(
            "findall(X, member(X, [a,b,c]), R), R == [a,b,c].").Success);
    }
}
