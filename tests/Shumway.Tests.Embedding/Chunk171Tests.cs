using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 14 chunk 171: parser error recovery, C-compiler-style.
/// <see cref="ShmoCompiler.TryCompileSource"/> /
/// <see cref="ShmoCompiler.TryCompileFile"/> accumulate parse and
/// directive errors instead of throwing on the first one, resyncing
/// to the next clause-terminator dot between attempts. Stops after
/// a configurable max-error cap (default 100) so a hopelessly
/// malformed file doesn't drown the diagnostics stream.
/// </summary>
public class Chunk171Tests
{
    [Fact]
    public void TryCompileSource_Success_ReturnsObjectWithNoErrors()
    {
        var result = ShmoCompiler.TryCompileSource(":- module(m).\np(1).\n");
        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.NotNull(result.Object);
        Assert.Equal("m", result.Object!.ModuleName);
    }

    [Fact]
    public void TryCompileSource_SingleParseError_Captured_NoObject()
    {
        // Missing closing bracket — used to throw ParseException.
        var result = ShmoCompiler.TryCompileSource("p(1, 2.\nq(3).\n");
        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Null(result.Object);
    }

    [Fact]
    public void TryCompileSource_ResumesAfterErrorAndCollectsMore()
    {
        var src =
            "good(1).\n"
            + "bad(1, 2.\n"       // missing ')'
            + "good(2).\n"
            + "uglier(1.\n"       // also broken
            + "good(3).\n";
        var result = ShmoCompiler.TryCompileSource(src);
        Assert.False(result.Success);
        // Two distinct errors collected.
        Assert.True(result.Errors.Count >= 2, $"got {result.Errors.Count} errors");
    }

    [Fact]
    public void TryCompileSource_MalformedDirectiveCaptured()
    {
        // :- public foo. — missing /Arity.
        var result = ShmoCompiler.TryCompileSource(":- public foo.\np(1).\n");
        Assert.False(result.Success);
        Assert.Contains(result.Errors,
            e => e.Message.Contains("public", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryCompileSource_ErrorsCarryLineColumn()
    {
        var src =
            "good(1).\n"
            + "broken(1, 2.\n";  // line 2, malformed
        var result = ShmoCompiler.TryCompileSource(src);
        Assert.False(result.Success);
        var first = result.Errors[0];
        // Position is on the offending line.
        Assert.True(first.Line >= 2);
    }

    [Fact]
    public void TryCompileSource_StopsAtMaxErrors()
    {
        // 5 broken clauses, max=3 → should stop early.
        var src = string.Concat(Enumerable.Repeat("broken(1, 2.\n", 10));
        var result = ShmoCompiler.TryCompileSource(src, maxErrors: 3);
        Assert.False(result.Success);
        // Last entry is the "too many errors" message.
        Assert.Contains(result.Errors,
            e => e.Message.Contains("Too many parse errors", StringComparison.Ordinal));
    }

    [Fact]
    public void TryCompileSource_FollowingGoodFileAfterRecovery_StillCompiles()
    {
        var src =
            "first(1).\n"
            + "second(2).\n"
            + "third(3).\n";
        var result = ShmoCompiler.TryCompileSource(src);
        Assert.True(result.Success);
        Assert.Equal(3, result.Object!.Defined.Count);
    }

    [Fact]
    public void CompileSource_LegacyThrowingPath_StillThrowsOnFirstError()
    {
        // Backwards-compatible: the exception-on-error API is kept.
        Assert.Throws<InvalidOperationException>(
            () => ShmoCompiler.CompileSource("bad(1, 2."));
    }
}
