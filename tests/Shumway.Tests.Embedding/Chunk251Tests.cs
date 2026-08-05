using Shumway.Core;
using Shumway.TopLevel;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 251: REPL error rendering. The interactive
/// <c>PrintError</c> branches on exception type and adds a
/// stack-trace block — those paths surface through the real
/// REPL's stdout and are covered by manual smoke tests. This
/// file unit-tests the pure formatting helper.
/// </summary>
public class Chunk251Tests
{
    [Fact]
    public void FormatRuntimeError_KindOnly()
    {
        var re = new PrologRuntimeException("instantiation_error");
        Assert.Equal("instantiation_error", ErrorRendering.FormatRuntimeError(re));
    }

    [Fact]
    public void FormatRuntimeError_KindAndDetail()
    {
        var re = new PrologRuntimeException("evaluation_error", "zero_divisor");
        Assert.Equal("evaluation_error(zero_divisor)",
            ErrorRendering.FormatRuntimeError(re));
    }

    [Fact]
    public void FormatRuntimeError_WithBuiltinContext()
    {
        var re = new PrologRuntimeException("evaluation_error", "zero_divisor");
        re.StampBuiltin("is", 2);
        Assert.Equal("evaluation_error(zero_divisor) in is/2",
            ErrorRendering.FormatRuntimeError(re));
    }

    [Fact]
    public void FormatRuntimeError_StampBuiltin_OnlyKind()
    {
        // No detail, but a stamped context — still composes
        // sensibly.
        var re = new PrologRuntimeException("instantiation_error");
        re.StampBuiltin("atom_codes", 2);
        Assert.Equal("instantiation_error in atom_codes/2",
            ErrorRendering.FormatRuntimeError(re));
    }

    [Fact]
    public void FormatRuntimeError_StampBuiltin_DoesNotOverwriteFirstStamp()
    {
        // Chunk 130 contract: StampBuiltin is idempotent — the
        // outermost dispatch site can't overwrite the innermost
        // throw's identity. Verify the formatter shows the
        // original stamp.
        var re = new PrologRuntimeException("type_error", "integer");
        re.StampBuiltin("between", 3);
        re.StampBuiltin("findall", 3);    // outer dispatch — should NOT win
        Assert.Equal("type_error(integer) in between/3",
            ErrorRendering.FormatRuntimeError(re));
    }
}
