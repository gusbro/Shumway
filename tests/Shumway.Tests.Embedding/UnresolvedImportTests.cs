using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A <c>use_module(library(X))</c> naming a library the search path
/// does not hold is a warning: reported, load continues. The warning goes to
/// a text sink while the caller's result says only whether something THREW,
/// so a consult whose every import resolved to nothing looked exactly like
/// one that worked.
///
/// <para>That is not hypothetical. A browser measurement ran a whole clp(Z)
/// benchmark against a page where the library was absent and reported
/// timings for predicates that did not exist, because consult had answered
/// "no error". The engine now records the fact as well as writing it.</para>
/// </summary>
public sealed class UnresolvedImportTests
{
    [Fact]
    public void AnUnknownLibraryIsRecorded()
    {
        var e = new PrologEngine { Warnings = new System.IO.StringWriter() };
        Assert.Empty(e.UnresolvedImports);

        e.ConsultString(":- use_module(library(no_such_library_xyz)).");

        Assert.Contains("no_such_library_xyz", e.UnresolvedImports);
    }

    /// <summary>ANTI-VACUITY: a library that DOES resolve records nothing, so
    /// the list means "unresolved" and not "imported".</summary>
    [Fact]
    public void AResolvedLibraryIsNotRecorded()
    {
        var e = new PrologEngine { Warnings = new System.IO.StringWriter() };
        // lists is covered natively; the import resolves.
        e.ConsultString(":- use_module(library(lists)).\np(X) :- append([1], [2], X).");
        Assert.True(e.Query("p([1,2]).").Success, "the resolved library did not work");
        Assert.Empty(e.UnresolvedImports);
    }

    /// <summary>The whole point: consult reports NO error for a load that
    /// imported nothing, and the record is the only thing that says so.
    /// If consult ever starts failing here, this test says so rather than
    /// the change going unnoticed.</summary>
    [Fact]
    public void ConsultStillSucceedsAndOnlyTheRecordTellsYou()
    {
        var warnings = new System.IO.StringWriter();
        var e = new PrologEngine { Warnings = warnings };

        // No throw: an unknown library is a warning, per Prolog practice.
        e.ConsultString(":- use_module(library(absent_one_xyz)).\nq(1).");
        Assert.True(e.Query("q(1).").Success, "the rest of the file must still load");

        Assert.Single(e.UnresolvedImports);
        Assert.Equal("absent_one_xyz", e.UnresolvedImports[0]);
        // The warning still goes where it always went, and still names where
        // it looked -- that half is what makes a report actionable.
        Assert.Contains("absent_one_xyz", warnings.ToString());
    }

    [Fact]
    public void SeveralAreKeptInOrderAndClearingForgetsThem()
    {
        var e = new PrologEngine { Warnings = new System.IO.StringWriter() };
        e.ConsultString(":- use_module(library(aaa_xyz)).\n:- use_module(library(bbb_xyz)).");
        Assert.Equal(new[] { "aaa_xyz", "bbb_xyz" }, e.UnresolvedImports);

        e.ClearUnresolvedImports();
        Assert.Empty(e.UnresolvedImports);

        // And it keeps recording afterwards: clearing scopes the question to
        // one consult, it does not switch the recording off.
        e.ConsultString(":- use_module(library(ccc_xyz)).");
        Assert.Equal(new[] { "ccc_xyz" }, e.UnresolvedImports);
    }

    /// <summary>The goal form raises instead of warning (it always has), so
    /// it must NOT also record: a caller that caught the error would then see
    /// a phantom entry for a library it already handled.</summary>
    [Fact]
    public void TheGoalFormRaisesAndDoesNotRecord()
    {
        var e = new PrologEngine { Warnings = new System.IO.StringWriter() };
        Assert.True(e.Query(
            "catch(use_module(library(goal_form_xyz)), "
            + "error(existence_error(library, goal_form_xyz), _), true).").Success);
        Assert.Empty(e.UnresolvedImports);
    }
}
