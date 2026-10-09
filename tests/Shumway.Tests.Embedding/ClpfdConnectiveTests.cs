using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The reified connectives of clpfd over 0/1 variables (#/\, #\/,
/// #==>, #<==): B #<==> (C1 op C2) propagates each way, the truth value
/// to both sides and the sides to the truth value, and an answer shows the
/// connective as written.</summary>
public sealed class ClpfdConnectiveTests
{
    private static void Holds(string goal)
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(clpfd)).");
        Assert.True(e.Query(goal + ".").Success, goal);
    }

    [Theory]
    // B #<==> a connective of 0/1 variables propagates each way.
    [InlineData("[X,Y] ins 0..1, B #<==> (X #= 1 #/\\ Y #= 0), B = 1", "X-Y", "1-0")]
    [InlineData("[X,Y] ins 0..1, B #<==> (X #= 1 #\\/ Y #= 0), B = 0", "X-Y", "0-1")]
    [InlineData("[X,Y] ins 0..1, B #<==> (X #= 1 #==> Y #= 0), B = 0", "X-Y", "1-1")]
    [InlineData("[X,Y] ins 0..1, B #<==> (X #= 1 #<== Y #= 0), B = 0", "X-Y", "0-0")]
    [InlineData("[X,Y] ins 0..1, B #<==> (X #= 1 #/\\ Y #= 0), X = 0", "B", "0")]
    [InlineData("[X,Y] ins 0..1, B #<==> (X #= 1 #\\/ Y #= 0), Y = 0", "B", "1")]
    public void ConnectivesPropagate(string goal, string term, string expected)
        => Holds($"{goal}, {term} == {expected}");

    [Fact]
    public void AConnectiveReadsAsWritten()
        => Holds("X in 0..3, B #<==> (X #> 1 #/\\ X #< 3), copy_term(X-B, X-B, Gs), "
            + "member(G, Gs), G = (_ #/\\ _ #<==> B)");
}
