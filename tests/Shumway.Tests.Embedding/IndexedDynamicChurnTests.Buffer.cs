using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding
{
    public sealed partial class IndexedDynamicChurnTests
    {
        // A hot predicate churned under the same keys, query after query: a
        // round's asserts take the holes the retracts before them left once
        // the sweep unlinked their entries, so the buffer stays where it is
        // instead of growing by a round each time.
        [Fact]
        public void AHotIndexedPredicateChurnedQueryAfterQueryKeepsItsBufferBounded()
        {
            var hot = Engine(hot: true);
            // No compaction: it is the layout's recycling that keeps the buffer.
            hot.CompactWatermark = long.MaxValue;
            // Hot with its clauses in place: the next setup lays it out indexed.
            Assert.True(hot.Query("fillt(40), readt(40).").Success);
            const string round = "( between(1, 40, I), retract(row(I, _)), fail ; true ), "
                + "( between(1, 40, I), assertz(row(I, v(I))), fail ; true ).";
            for (int i = 0; i < 10; i++) Assert.True(hot.Query(round).Success);
            int early = hot._persistentLength;
            long retracts = hot.IndexedRetracts, sweeps = hot.IndexedSweeps;
            long drops = hot.PersistentInvalidations, builds = hot.ProgramProductBuilds;
            for (int i = 0; i < 50; i++) Assert.True(hot.Query(round).Success);
            Assert.Equal(drops, hot.PersistentInvalidations);
            Assert.Equal(builds, hot.ProgramProductBuilds);
            Assert.True(hot.IndexedRetracts > retracts && hot.IndexedSweeps > sweeps,
                "the churn did not take the indexed path");
            Assert.True(hot._persistentLength <= early + 4096,
                $"the buffer grew from {early} to {hot._persistentLength} bytes");
            Assert.True(hot.Query("findall(K, row(K, _), L), length(L, 40), row(7, V), V == v(7).").Success);
        }
    }
}
