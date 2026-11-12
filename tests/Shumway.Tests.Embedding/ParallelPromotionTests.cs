using System.Text;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Several workers compile one engine's promotions at once: regions of
/// the same compiler, against the same view of the program. Every predicate
/// promotes and answers as before.</summary>
// Exclusive: the test raises the process's worker count and holds the workers.
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class ParallelPromotionTests
{
    private const int Predicates = 24;

    // p_i(X, Y) :- a_i(X, Z), b_i(Z, Y). with a_i and b_i facts only p_i
    // calls, so each p_i compiles as a region with its two members.
    private static string Program()
    {
        var sb = new StringBuilder(":- public ");
        for (int i = 0; i < Predicates; i++) sb.Append(i == 0 ? "" : ", ").Append($"p{i}/2");
        sb.Append(", go/1.\n");
        for (int i = 0; i < Predicates; i++)
        {
            sb.Append($"p{i}(X, Y) :- a{i}(X, Z), b{i}(Z, Y).\n");
            sb.Append($"a{i}(1, 2). a{i}(2, 3). a{i}(3, {i + 4}).\n");
            sb.Append($"b{i}(2, x). b{i}(3, y). b{i}({i + 4}, z{i}).\n");
        }
        // Called through call/3: no p_i is a member of another's region.
        sb.Append("go(L) :- findall(P-X-Y, (member(P, [");
        for (int i = 0; i < Predicates; i++) sb.Append(i == 0 ? "" : ", ").Append($"p{i}");
        sb.Append("]), call(P, X, Y)), L).\n");
        return sb.ToString();
    }

    private static string Expected()
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < Predicates; i++)
            sb.Append(i == 0 ? "" : ",").Append($"p{i}-1-x,p{i}-2-y,p{i}-3-z{i}");
        return sb.Append(']').ToString();
    }

    [Fact]
    public void PromotionsCompiledAtOnceAnswerAsBefore()
    {
        int before = IlCompileWorker.MaxWorkers;
        IlCompileWorker.MaxWorkers = Math.Max(before, 4);
        try
        {
            var e = new PrologEngine();
            e.IlPromotion.Threshold = 1;
            e.ConsultString(Program());
            string check = $"go(L), L == {Expected()}.";
            using (var held = IlCompileWorker.Hold())
            {
                // Every p_i's compile queued, none started.
                Assert.True(e.Query(check).Success);
                IlCompileWorker.ResetPeakBusy();
            }
            Assert.True(e.IlPromotion.WaitForPendingPromotions(120_000), "promotion did not settle");
            // ANTI-VACUITY: compiles ran at the same time.
            Assert.True(IlCompileWorker.PeakBusy >= 2,
                $"no two compiles overlapped: {IlCompileWorker.Describe()}");
            for (int i = 0; i < Predicates; i++)
            {
                int fid = FunctorTable.Intern(AtomTable.Intern($"p{i}").Id, 2);
                Assert.True(e.IlPromotion.IsPromoted(fid), $"p{i}/2 did not promote");
            }
            Assert.True(e.Query(check).Success);
        }
        finally { IlCompileWorker.MaxWorkers = before; }
    }
}
