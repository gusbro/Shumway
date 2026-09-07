using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A listing shows the program, not the loader's bookkeeping.
///
/// <para>An in-file <c>term_expansion</c> / <c>goal_expansion</c> clause may
/// only expand the clauses written AFTER it, and the loader enforces that by
/// wrapping the hook's body with <c>'$te_after'(N)</c>. That guard is not
/// something anyone wrote, and it was showing up in <c>listing/1</c> —
/// especially visible after a load that failed part way, where a user
/// inspecting the database found an internal goal inside their own
/// clause.</para></summary>
public class ListingHookGuardTests
{
    private static string Listing(string source)
    {
        var engine = new PrologEngine { Warnings = new StringWriter() };
        // A load that RAISES still leaves what it loaded — which is the state
        // being listed here.
        try { engine.ConsultString(source); }
        catch (ShumwayPrologException) { }
        catch (Shumway.Core.PrologRuntimeException) { }
        var sw = new StringWriter();
        engine.Out = sw;
        engine.Query("listing.");
        return sw.ToString();
    }

    [Fact]
    public void ARuleHook_ListsAsWritten()
    {
        string output = Listing(
            "goal_expansion(zzz, true) :- ttt.\n"
            + "ttt.\n");
        Assert.DoesNotContain("$te_after", output);
        Assert.Contains("goal_expansion(zzz, true)", output);
        Assert.Contains("ttt", output);
    }

    [Fact]
    public void AFactHook_ListsAsAFact()
    {
        // A fact hook is stored as `Head :- '$te_after'(N)`: with the guard
        // gone the body is empty, so it must print as the fact it was.
        string output = Listing("term_expansion(aaa, bbb).\nunrelated.\n");
        Assert.DoesNotContain("$te_after", output);
        Assert.Contains("term_expansion(aaa, bbb).", output);
    }

    [Fact]
    public void AfterALoadThatFailed_WhatLoadedStillListsAsWritten()
    {
        // The reported shape: a self-feeding goal_expansion aborts the load
        // with resource_error(expansion_depth); what did load is then listed,
        // and the hook among it is the user's clause, not the guarded one.
        string output = Listing(
            "b((_, p(_))).\n"
            + "goal_expansion(p(B), B) :- b(B).\n"
            + "p :- p(_).\n");
        Assert.DoesNotContain("$te_after", output);
        Assert.Contains("goal_expansion(p(B), B)", output);
        Assert.Contains("b(B)", output);
    }
}
