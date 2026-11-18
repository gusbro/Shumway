using System.Text;
using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A predicate with rules whose own method would be past the JIT's
/// limits (<see cref="IlJitLimits"/>) stays on the interpreter, at run time and
/// in a bundle with IL: the JIT compiles such a method without optimization,
/// and the interpreter runs the predicate faster than that code does. A
/// predicate of 300 guarded clauses is past them; one of 40 is not. A table of
/// facts keeps its method either way.</summary>
public sealed class TierOneJitLimitsTests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    // name(X, Y) :- X == kI, !, Y = r(kI, I).  for I below clauses, then name(_, none).
    // Public, so it is promoted as itself and not as a member of its caller's region.
    private static string Guarded(string name, int clauses)
    {
        var src = new StringBuilder(":- public ").Append(name).Append("/2.\n");
        for (int i = 0; i < clauses; i++)
            src.Append(name).Append("(X, Y) :- X == k").Append(i).Append(", !, Y = r(k")
                .Append(i).Append(", ").Append(i).Append(").\n");
        src.Append(name).Append("(_, none).\n");
        return src.ToString();
    }

    private static PrologEngine Promoting(string src)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString(src);
        return e;
    }

    private static void Answers(PrologEngine e, string name, int clauses)
    {
        int last = clauses - 1, mid = clauses / 2;
        Assert.True(e.Query($"{name}(k{mid}, Y), Y == r(k{mid}, {mid}).").Success);
        Assert.True(e.Query($"{name}(k{last}, Y), Y == r(k{last}, {last}).").Success);
        Assert.True(e.Query($"{name}(zz, Y), Y == none.").Success);
        Assert.True(e.Query($"findall(Y, {name}(k0, Y), L), L == [r(k0, 0)].").Success);
    }

    [Fact]
    public void APredicatePastTheLimitsStaysOnTheInterpreter_AndAnswersTheSame()
    {
        var e = Promoting(Guarded("big", 300));
        Answers(e, "big", 300);
        Answers(e, "big", 300);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");

        int big = Fid("big", 2);
        Assert.False(e.IlPromotion.IsPromoted(big));
        Assert.Contains(e.IlPromotion.UnpromotableEntries(),
            u => u.FunctorId == big && u.Reason.Contains("past the JIT's limits"));
        Answers(e, "big", 300);
    }

    [Fact]
    public void APredicateWithinTheLimitsPromotes()
    {
        var e = Promoting(Guarded("small", 40));
        Answers(e, "small", 40);
        Answers(e, "small", 40);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");

        Assert.True(e.IlPromotion.IsPromoted(Fid("small", 2)));
        Answers(e, "small", 40);
    }

    // A call to a table of facts runs a dispatch and one head: compiled past the
    // limits it measured about even with the interpreter, so it keeps its method.
    [Fact]
    public void ATableOfFactsPastTheLimitsKeepsItsMethod()
    {
        var src = new StringBuilder(":- public facts/1.\n");
        for (int i = 0; i < 4200; i++) src.Append("facts(a").Append(i).Append(").\n");
        var e = Promoting(src.ToString());
        Assert.True(e.Query("facts(a5).").Success);
        Assert.True(e.Query("facts(a6).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(120_000), "promotion did not settle");

        Assert.True(e.IlPromotion.IsPromoted(Fid("facts", 1)));
        Assert.True(e.Query("facts(a4199).").Success);
        Assert.False(e.Query("facts(zzz).").Success);
    }

    // Guards the first test: without the decline the same predicate compiles,
    // so it is the limits that keep it on the interpreter.
    [Fact]
    public void WithTheDeclineOff_ThePredicatePastTheLimitsPromotes()
    {
        bool before = IlJitLimits.DeclineWhenExceeded;
        IlJitLimits.DeclineWhenExceeded = false;
        try
        {
            var e = Promoting(Guarded("bigoff", 300));
            Answers(e, "bigoff", 300);
            Answers(e, "bigoff", 300);
            Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
            Assert.True(e.IlPromotion.IsPromoted(Fid("bigoff", 2)));
        }
        finally { IlJitLimits.DeclineWhenExceeded = before; }
    }

    [Fact]
    public void ABundlesIlLeavesOutThePredicatePastTheLimits_WhichRunsItsBytecode()
    {
        var bundle = new Bundle(new[] { new BundleEntry("limits",
            Guarded("bigb", 300) + Guarded("smallb", 40)) });
        byte[] bytes = BundleWriter.ToBytes(bundle, includeCompiledBytecode: true, includeCompiledIl: true);
        var e = new PrologEngine();   // threshold 0: only the bundle's IL is promoted
        e.LoadBundle(BundleReader.FromBytes(bytes));
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");

        Assert.True(e.IlPromotion.IsPromoted(Fid("smallb", 2)));
        Assert.False(e.IlPromotion.IsPromoted(Fid("bigb", 2)));
        Answers(e, "bigb", 300);
        Answers(e, "smallb", 40);
    }
}
