using Shumway.Embedding;
using Shumway.TopLevel;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>What a reported call stack says. A frame's line number is only
/// meaningful for code the user has a file for: quoting one for the prelude
/// or a bundled library points at source they cannot open (issue #76 showed
/// `at copy_term/3 (906:30)`). Such frames report the predicate alone. A
/// module-local predicate is stored mangled (`module$name`) and now reads as
/// the standard `module:name` indicator, bare in the default module, while
/// engine machinery — a `$`-named local, which mangling hid from the old
/// bare-`$` rule — is dropped.</summary>
public class StackTraceFramePresentationTests
{
    private static IReadOnlyList<string> DescribeFailure(PrologEngine e, string goal)
    {
        var ex = Record.Exception(() => e.Query(goal));
        Assert.NotNull(ex);
        return ErrorRendering.Describe(e, ex!);
    }

    [Fact]
    public void AUserFrameKeepsItsPosition()
    {
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        e.ConsultString("""
            :- public divider/2.
            divider(N, D) :- _ is N / D.
            """);
        var lines = DescribeFailure(e, "divider(10, 0).");
        Assert.Contains(lines, l => l.StartsWith("  at divider/2 (2:"));
    }

    [Fact]
    public void AUserModuleFrameReadsAsAQualifiedIndicator()
    {
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        e.ConsultString("""
            :- module(mymod, [entry/1]).
            entry(A) :- helper(A).
            helper(A) :- atom_length(A, _), fail.
            """);
        var lines = DescribeFailure(e, "entry(_).");
        Assert.Contains(lines, l => l.StartsWith("  at mymod:helper/1 (3:"));
        // Never the internal mangling.
        Assert.DoesNotContain(lines, l => l.Contains("mymod$"));
    }

    [Fact]
    public void ALibraryFrameReportsNoPosition()
    {
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        Assert.True(e.Query("use_module(library(coroutining)).").Success);
        var lines = DescribeFailure(e, "when(_C, true).");
        Assert.Contains(lines, l => l == "  at when/2");
        // No line number for source the user has no file for, and no
        // compiler-generated or meta-call helper frames.
        Assert.DoesNotContain(lines, l => l.StartsWith("  at when/2 ("));
        Assert.DoesNotContain(lines, l => l.Contains("$"));
    }

    [Fact]
    public void TheFrameItselfCarriesTheInternalFlag()
    {
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        Assert.True(e.Query("use_module(library(coroutining)).").Success);
        Assert.NotNull(Record.Exception(() => e.Query("when(_C, true).")));
        var frames = e.LastErrorStackTraceWithPositions;
        var when = Assert.Single(frames, f => f.Name == "when" && f.Arity == 2);
        Assert.True(when.IsInternal);
        Assert.Equal("when/2", when.ToString());
    }

    [Fact]
    public void AUserFrameIsNotInternal()
    {
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        e.ConsultString("""
            :- public divider/2.
            divider(N, D) :- _ is N / D.
            """);
        Assert.NotNull(Record.Exception(() => e.Query("divider(10, 0).")));
        var frames = e.LastErrorStackTraceWithPositions;
        var divider = Assert.Single(frames, f => f.Name == "divider");
        Assert.False(divider.IsInternal);
        Assert.Equal(2, divider.Position.Line);
    }
}
