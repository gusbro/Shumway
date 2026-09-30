using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Capturing the output of a goal that never stops writing.
///
/// <para>Issue #112: a quad describing a looping goal's output reached the
/// user as <c>ArgumentOutOfRangeException: PSTR length must be in [0,
/// 67108863]</c> — a .NET exception from the cell encoding's range check,
/// which no program can catch and no report can explain. Two things were
/// wrong: text arriving from the host was only checked where it was packed
/// (a layout guard), and the harness let a looping goal's output accumulate
/// without a ceiling.</para></summary>
public sealed class BoundedOutputCaptureTests
{
    [Fact]
    public void CapturingAGoalThatWritesWithoutEnd_IsACatchableRefusal()
    {
        // The ceiling lowered: the refusal is the same at any size, and the
        // real one is tens of millions of characters away.
        var e = new PrologEngine { Out = new System.IO.StringWriter(), CaptureCeiling = 100_000 };
        Assert.True(e.Query(
            "catch(with_output_to(atom(_), (repeat, write(hello), fail)), "
            + "error(resource_error(text_length), _), true).").Success);
        // ...and the engine is still usable.
        Assert.True(e.Query("with_output_to(atom(A), write(ok)), A == ok.").Success);
        // Anti-vacuity: below the ceiling the capture is whole.
        Assert.True(e.Query("with_output_to(atom(A), (between(1, 1000, _), write(hello), fail ; true)), "
            + "atom_length(A, 5000).").Success);
    }

    // At the real ceiling: hundreds of megabytes of text, and the collections
    // it forces slow down every test sharing the process.
    [Fact]
    [Trait("Category", "Slow")]
    public void CapturingAGoalThatWritesWithoutEnd_RefusesAtTheRealCeiling()
    {
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        Assert.True(e.Query(
            "catch(with_output_to(atom(_), (repeat, write(hello), fail)), "
            + "error(resource_error(text_length), _), true).").Success);
        Assert.True(e.Query("with_output_to(atom(A), write(ok)), A == ok.").Success);
    }

    [Fact]
    public void AHostsOwnCeiling_TruncatesInsteadOfRefusing()
    {
        // A host capturing to compare (the quad harness) sets a ceiling of
        // its own: past its longest pattern the answer cannot change, so
        // the prefix is kept and the goal is left running — which is how a
        // looping goal still reaches the limit that decides it loops.
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        Assert.True(e.Query(
            "setup_call_cleanup('$wot_begin'(atom(A), 10), "
            + "( between(1, 100, _), write(x), fail ; true ), "
            + "'$wot_end'(atom(A))), atom_length(A, 10).").Success);
    }

    [Fact]
    public void OrdinaryCapturesAreUntouched()
    {
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
        Assert.True(e.Query(
            "with_output_to(atom(A), (write(one), nl, write(two))), "
            + "atom_length(A, 7).").Success);
        // The memory sink still gives "\n" for nl, whatever the platform.
        Assert.True(e.Query(
            "with_output_to(atom(A), nl), atom_codes(A, [10]).").Success);
    }
}
