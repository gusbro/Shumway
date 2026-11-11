using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>What a query's setup does must not grow with the program: a
/// consult runs one query per directive and per expansion hook call, and the
/// prelude alone is a few thousand predicates.</summary>
public sealed class QuerySetupCostTests
{
    /// <summary>The dispatch graphs the baked prelude carries for its stripped
    /// predicates reach the activation by reference; none is decoded or
    /// registered until a dispatch goes through it.</summary>
    [Fact]
    public void ASetupRegistersNoPersistedGraph()
    {
        var e = new PrologEngine();
        Assert.True(e._persistedIndexGraphs.Count > 0,
            "the engine loaded no persisted dispatch graph, so this proves nothing");
        Assert.True(e.Query("X = 1, X == 1.").Success);
        var activation = e.LiveActivation!;
        Assert.Same(e._persistedIndexGraphs, activation.PersistedIndexGraphs);
        Assert.True(activation.IlIndexedDispatchCache is null
                    or System.Collections.ICollection { Count: 0 },
            "the setup registered dispatch graphs the query never used");
    }
}
