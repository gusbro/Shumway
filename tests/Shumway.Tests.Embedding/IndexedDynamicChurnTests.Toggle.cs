using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding
{
    public sealed partial class IndexedDynamicChurnTests
    {
        // A hot predicate the compiler lays out as a plain chain (no argument
        // to index, or one clause), retracted and asserted again by every
        // query. Each mutation stays in place: a retract that drops the
        // persistent buffer makes every later mutation of the query drop it
        // again, and the next setup builds the whole product and links the
        // buffer anew.
        [Theory]
        [InlineData(":- dynamic(d/1).\nrun :- retractall(d(_)), assert(d(1)), forall(between(1, 5, _), d(_)).", "d(X), X == 1, \\+ (d(_), d(_), fail).")]
        [InlineData(":- dynamic(f/0).\nrun :- ( f -> retract(f) ; assert(f) ), forall(between(1, 5, _), ( f -> true ; true )).", "\\+ f.")]
        public void AHotToggleMutatesInPlaceQueryAfterQuery(string program, string check)
        {
            var e = new PrologEngine();
            e.IlPromotion.Threshold = 0;
            e.JitIndexing.Threshold = 1;
            e.ConsultString(program);
            // The first run makes the predicate hot; the second query's setup
            // compiles it as such. From then on the product and the buffer stay.
            Assert.True(e.Query("run.").Success);
            Assert.True(e.Query("run.").Success);
            long builds = e.ProgramProductBuilds, drops = e.PersistentInvalidations;
            for (int i = 0; i < 4; i++)
                Assert.True(e.Query("run.").Success);
            Assert.True(e.Query(check).Success);
            Assert.Equal(drops, e.PersistentInvalidations);
            Assert.Equal(builds, e.ProgramProductBuilds);
        }
    }
}
