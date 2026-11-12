using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Compiled code reaches a register below the bank's floor
/// (<c>Activation.MinRegisterCount</c>) with no bounds check and one above it
/// through a checked access. A predicate of arity 70 takes a list apart in a
/// low argument and in a high one, and moves values between low and high
/// registers; compiled, it answers as Tier-0 does.</summary>
public sealed class RegisterBankFloorTests
{
    private const int Arity = 70;

    // wide(L1, A2, ..., A65, L66, A67, ..., A70): heads of the lists in A1
    // and A66, A2 and A67 swapped, the rest passed on.
    private static string Program()
    {
        string Args(System.Func<int, string> arg) =>
            string.Join(", ", Enumerable.Range(1, Arity).Select(arg));
        string head = "wide(" + Args(i => i switch
        {
            1 => "[H1|T1]",
            66 => "[H66|T66]",
            _ => $"A{i}",
        }) + ", R)";
        string body = "R = r(H1, H66, A67, A2, T1, T66)";
        string call = "go(R) :- wide(" + Args(i => i switch
        {
            1 => "[a, b]",
            66 => "[c, d]",
            2 => "x2",
            67 => "x67",
            _ => $"{i}",
        }) + ", R).";
        return $":- public go/1, warm/1, wide/{Arity + 1}.\n{head} :- {body}.\n{call}\n"
            + "warm(0) :- !.\nwarm(N) :- go(_), N1 is N - 1, warm(N1).\n";
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void APredicateOfSeventyArgumentsAnswersAsTierZero(int threshold)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = threshold;
        e.ConsultString(Program());
        int wide = Shumway.Core.FunctorTable.Intern(Shumway.Core.AtomTable.Intern("wide").Id, Arity + 1);
        const string Check = "go(R), R == r(a, c, x67, x2, [b], [d]).";
        Assert.True(e.Query(Check).Success);
        // Warmed in a loop until it is compiled.
        for (int i = 0; i < 20 && !(threshold > 0 && e.IlPromotion.IsPromoted(wide)); i++)
        {
            Assert.True(e.Query("warm(200).").Success);
            e.IlPromotion.WaitForPendingPromotions(60_000);
        }
        Assert.True(e.Query(Check).Success);
        // ANTI-VACUITY: the last answer came from the compiled code.
        if (threshold > 0)
            Assert.True(e.IlPromotion.IsPromoted(wide), "wide/71 did not promote");
    }
}
