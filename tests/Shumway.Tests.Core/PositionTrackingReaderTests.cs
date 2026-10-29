using System.IO;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>ADR-045 — a text stream collapses CR-LF to <c>\n</c>. These pin
/// the decorator itself with the flag set explicitly, so the rules hold on
/// every platform; the platform default is pinned separately.</summary>
public sealed class PositionTrackingReaderTests
{
    private static PositionTrackingReader On(string text, bool translate = true) =>
        new(new StringReader(text), translate);

    private static string ReadAll(PositionTrackingReader r)
    {
        var sb = new System.Text.StringBuilder();
        int c;
        while ((c = r.Read()) >= 0) sb.Append((char)c);
        return sb.ToString();
    }

    [Fact]
    public void CrLf_ReadsAsOneNewline()
    {
        Assert.Equal("abc\ndef\n", ReadAll(On("abc\r\ndef\r\n")));
    }

    [Fact]
    public void LoneCr_IsData_NotALineTerminator()
    {
        // C stdio's rule, which GNU inherits: only the pair is a terminator,
        // so a classic-Mac file reads unchanged.
        Assert.Equal("abc\rdef\r", ReadAll(On("abc\rdef\r")));
    }

    [Fact]
    public void CrAtEndOfInput_Survives()
    {
        Assert.Equal("ab\r", ReadAll(On("ab\r")));
    }

    [Fact]
    public void TranslationOff_LeavesBytesAlone()
    {
        Assert.Equal("a\r\nb", ReadAll(On("a\r\nb", translate: false)));
    }

    [Fact]
    public void Peek_AcrossCrLf_AgreesWithRead()
    {
        var r = On("a\r\nb");
        Assert.Equal('a', r.Read());
        Assert.Equal('\n', r.Peek());
        Assert.Equal('\n', r.Peek());     // idempotent
        Assert.Equal('\n', r.Read());
        Assert.Equal('b', r.Read());
        Assert.Equal(-1, r.Read());
    }

    [Fact]
    public void Peek_DoesNotConsume_WhenNextIsNotCr()
    {
        var r = On("ab");
        Assert.Equal('a', r.Peek());
        Assert.Equal('a', r.Read());
    }

    [Fact]
    public void Position_CountsTheTranslatedCharacter_Once()
    {
        var r = On("a\r\nb");
        r.Read(); r.Read(); r.Read();
        Assert.Equal(3, r.CharsConsumed);   // a, \n, b — not 4
    }

    [Fact]
    public void BlockRead_TranslatesToo()
    {
        var r = On("a\r\nb\r\n");
        var buf = new char[10];
        int n = r.Read(buf, 0, buf.Length);
        Assert.Equal("a\nb\n", new string(buf, 0, n));
        Assert.Equal(4, r.CharsConsumed);
    }

    // Line is the line the next character comes from: one more than the line
    // terminators read, whichever way they are read.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Line_CountsTheTerminatorsRead(bool translate)
    {
        var r = On("a\r\nb\nc", translate);
        Assert.Equal(1, r.Line);
        Assert.Equal('a', r.Read());
        Assert.Equal(1, r.Line);
        ReadAll(r);
        Assert.Equal(3, r.Line);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Line_CountsInABlockReadToo(bool translate)
    {
        var r = On("a\nb\r\nc\n", translate);
        var buf = new char[16];
        r.Read(buf, 0, buf.Length);
        Assert.Equal(4, r.Line);
    }

    [Fact]
    public void Line_PeekingAPairDoesNotCountIt()
    {
        var r = On("\r\nx");
        Assert.Equal('\n', r.Peek());
        Assert.Equal(1, r.Line);
        r.Read();
        Assert.Equal(2, r.Line);
    }

    [Fact]
    public void ResetCount_StartsTheLinesOver()
    {
        var r = On("a\nb\n");
        ReadAll(r);
        r.ResetCount();
        Assert.Equal(1, r.Line);
    }

    [Fact]
    public void ResetCount_DropsTheLookahead()
    {
        var r = On("\r\nxy");
        Assert.Equal('\n', r.Peek());       // buffers the translated char
        r.ResetCount();
        Assert.Equal(0, r.CharsConsumed);
        Assert.Equal('x', r.Read());        // the buffered '\n' is gone with it
    }

    [Fact]
    public void PlatformDefault_TranslatesOnWindowsOnly()
    {
        Assert.Equal(System.OperatingSystem.IsWindows(),
                     PositionTrackingReader.TranslateNewlinesByDefault);
        Assert.Equal(System.OperatingSystem.IsWindows(),
                     new PositionTrackingReader(new StringReader("")).TranslatesNewlines);
    }
}
