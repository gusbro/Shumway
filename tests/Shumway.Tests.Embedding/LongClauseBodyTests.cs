using System.Text;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A clause is as big as the program that wrote it.
///
/// <para>Generated Prolog reaches thousands of goals in one body, and reading
/// and compiling one used to spend C# stack PER GOAL: the wall stood around
/// nine hundred, and hitting it was a .NET stack overflow — the process dies,
/// mid-consult, with nothing to catch and nothing to report. The body's
/// conjunction spine is now walked iteratively everywhere it is walked (the
/// reader, the pipeline transforms, the module rewrite, the assert scan), so
/// depth there is bounded by memory rather than by stack.</para>
///
/// <para>What stays recursive is nesting the source shapes term by term:
/// parentheses, prefix operators, each <c>;</c> synthesising a helper.
/// <c>RecursionGuard</c> makes that ceiling a catchable
/// <c>resource_error(term_nesting)</c> instead of a dead process, and
/// <c>DeepStackHost</c> — which the command-line tools run on — puts the
/// ceiling where real programs do not reach it.</para>
///
/// <para>These run on an xUnit thread with an ORDINARY stack, which is the
/// point: an embedding host gets no special thread either. A regression on
/// the iterative paths does not fail politely, it takes the test run down —
/// the honest signal.</para></summary>
public class LongClauseBodyTests
{
    private static string Conjunction(int goals)
    {
        var sb = new StringBuilder("p :- ");
        for (int i = 0; i < goals; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append("q(").Append(i).Append(')');
        }
        return sb.Append(".\nq(_).\n").ToString();
    }

    private static string DeeplyNestedClause()
        => "deep :- X = " + new string('(', 60_000) + "1" + new string(')', 60_000) + ".";

    [Fact]
    public void ABodyOfTwentyThousandGoals_LoadsAndRuns()
    {
        var e = new PrologEngine();
        e.ConsultString(Conjunction(20_000));
        Assert.True(e.Query("p.").Success);
    }

    [Fact]
    public void ABodyOfTwentyThousandGoals_KeepsItsShapeAndOrder()
    {
        // The iterative read must build the same right-nested tree the
        // recursion built: 20.000 conjuncts, in source order.
        var e = new PrologEngine();
        e.ConsultString(
            ":- dynamic(p/0).\n" + Conjunction(20_000)
            + "spine((_, B), N) :- !, spine(B, M), N is M + 1.\n"
            + "spine(_, 1).\n"
            + "last_conjunct((_, B), L) :- !, last_conjunct(B, L).\n"
            + "last_conjunct(G, G).\n");
        Assert.True(e.Query(
            "clause(p, Body), spine(Body, 20000), last_conjunct(Body, q(19999)).").Success);
    }

    [Fact]
    public void ADisjunctionInsideALongBody_StillWorks()
    {
        var sb = new StringBuilder("r(X) :- ");
        for (int i = 0; i < 5_000; i++) sb.Append("q(").Append(i).Append("), ");
        sb.Append("( X > 3 -> X0 = big ; X0 = small ), X0 == big.\nq(_).\n");
        var e = new PrologEngine();
        e.ConsultString(sb.ToString());
        Assert.True(e.Query("r(9).").Success);
        Assert.False(e.Query("r(1).").Success);
    }

    [Fact]
    public void NestingDeeperThanTheStack_IsACatchableRefusal_NotADeadProcess()
    {
        // 60.000 parentheses: nothing is wrong with the text, there is simply
        // no stack to descend it on. The engine says so — and lives.
        var strict = new PrologEngine { StrictConsultSyntax = true };
        var ex = Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => strict.ConsultString(DeeplyNestedClause()));
        Assert.Equal("resource_error", ex.Kind);
        Assert.Equal("term_nesting", ex.Detail);
        strict.ConsultString("ok_after.");
        Assert.True(strict.Query("ok_after.").Success);
    }

    [Fact]
    public void AClauseTooDeepToRead_IsSkipped_AndTheFileLoads()
    {
        // Recovery, as for a syntax error (issue #109): the clause that could
        // not be read is a diagnostic, the ones around it load.
        var warnings = new System.IO.StringWriter();
        var e = new PrologEngine { Warnings = warnings };
        e.ConsultString("before(1).\n" + DeeplyNestedClause() + "\nafter(2).\n");
        Assert.True(e.Query("before(1), after(2).").Success);
        Assert.Contains("term_nesting", warnings.ToString());
    }

    [Fact]
    public void ADeepStack_LiftsTheCeiling_ForTheToolsThatRunOnOne()
    {
        // The shape the command-line tools load programs on: with room to
        // descend, a disjunction thousands deep compiles and runs.
        int rc = DeepStackHost.Run(() =>
        {
            var sb = new StringBuilder("d(X) :- ( ");
            for (int i = 0; i < 3_000; i++)
            {
                if (i > 0) sb.Append(" ; ");
                sb.Append("X = ").Append(i);
            }
            sb.Append(" ).\n");
            var e = new PrologEngine();
            e.ConsultString(sb.ToString());
            return e.Query("d(2999).").Success ? 0 : 1;
        });
        Assert.Equal(0, rc);
    }
}
