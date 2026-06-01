using System.Text;
using Shumway.Repl;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 250: REPL tab completion. Tests the pure helpers
/// (<see cref="LineEditor.FindWordStart"/>,
/// <see cref="LineEditor.LongestCommonPrefix"/>) that drive the
/// Tab key path. The interactive editor itself stays untested —
/// it depends on a real terminal and the chunk-249 tests already
/// cover the surrounding line-editor scaffolding.
/// </summary>
public class Chunk250Tests
{
    [Theory]
    [InlineData("assertz", 7, 0)]   // entire buffer is one word
    [InlineData("?- asse", 7, 3)]   // word starts after the space
    [InlineData("call(memb", 9, 5)] // word starts after the '('
    [InlineData(",  X = 1", 4, 3)]  // cursor just past 'X', word starts at 3
    [InlineData(",  X = 1", 5, 5)]  // cursor on a space — no word at cursor
    [InlineData("", 0, 0)]          // empty buffer
    [InlineData("foo()", 4, 4)]     // cursor inside the parens, no word
    public void FindWordStart_WalksBackOverIdentifierChars(
        string text, int cursor, int expected)
    {
        var sb = new StringBuilder(text);
        Assert.Equal(expected, LineEditor.FindWordStart(sb, cursor));
    }

    [Fact]
    public void FindWordStart_StopsAtUnderscoreBoundaryCorrectly()
    {
        // Underscore is part of an identifier; the word covers it.
        var sb = new StringBuilder("foo_bar");
        Assert.Equal(0, LineEditor.FindWordStart(sb, 7));
    }

    [Fact]
    public void FindWordStart_StopsAtDigits_TreatsThemAsIdentifierChars()
    {
        // Digits after a letter are valid identifier chars.
        var sb = new StringBuilder("abc123");
        Assert.Equal(0, LineEditor.FindWordStart(sb, 6));
    }

    [Fact]
    public void LongestCommonPrefix_EmptyList_ReturnsEmpty()
    {
        Assert.Equal("", LineEditor.LongestCommonPrefix(System.Array.Empty<string>()));
    }

    [Fact]
    public void LongestCommonPrefix_Single_ReturnsItself()
    {
        Assert.Equal("foo", LineEditor.LongestCommonPrefix(new[] { "foo" }));
    }

    [Fact]
    public void LongestCommonPrefix_ManyMatching_ReturnsCommonStem()
    {
        // assertz, asserta, assert all share "assert".
        Assert.Equal("assert",
            LineEditor.LongestCommonPrefix(new[] { "assertz", "asserta", "assert" }));
    }

    [Fact]
    public void LongestCommonPrefix_NoCommon_ReturnsEmpty()
    {
        Assert.Equal("",
            LineEditor.LongestCommonPrefix(new[] { "foo", "bar" }));
    }

    [Fact]
    public void LongestCommonPrefix_DivergeMidString()
    {
        Assert.Equal("ab",
            LineEditor.LongestCommonPrefix(new[] { "abc", "abx", "abqr" }));
    }
}
