using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>findall/3 over a cyclic solution. FindallSnapshot's spine walk
/// consulted structMap to stop, but the CURRENT spine's conses are only
/// registered after the walk — so a list cycling back into its own spine
/// (L = [a|L]) re-walked itself until the image overflowed: an
/// engine-killing OutOfMemoryException, where HeapTermCopy (copy_term's
/// path) had already fixed the very same walk. The snapshot now marks the
/// in-progress spine and closes the cycle in the image; the AST fallback
/// leg (value leaves) preserves cycles via the TermReader/Materializer
/// knots. ISO: an answer is a copy, and copying a rational tree yields
/// that rational tree.</summary>
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
