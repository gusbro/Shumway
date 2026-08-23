using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Strict UTF-8 on text read streams (ISO 8.12.1.3 i): an ill-formed byte
/// sequence raises representation_error(character) instead of decoding to
/// U+FFFD. Peek must not consume what it reports on (repeatable); a read
/// consumes exactly the offending lead byte, so byte-wise resync works.
/// Explicit iso_latin_1 keeps the byte-faithful .NET reader, and a UTF-16
/// BOM'd file keeps StreamReader's auto-detection.
/// </summary>
public class StrictUtf8StreamTests : IDisposable
{
    private readonly string _tmp;

    public StrictUtf8StreamTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(),
            "shumway_utf8_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private string File(string name, byte[] bytes)
    {
        string p = Path.Combine(_tmp, name).Replace('\\', '/');
        System.IO.File.WriteAllBytes(p, bytes);
        return p;
    }

    [Fact]
    public void WellFormedTwoByte_DecodesAndPeeksStable()
    {
        string f = File("ok.txt", new byte[] { 0xC3, 0xA9, (byte)'z' });   // é z
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{f}', read, S), peek_char(S, C1), peek_char(S, C2), "
            + "get_char(S, C3), get_char(S, Z), close(S), "
            + "C1 == 'é', C2 == 'é', C3 == 'é', Z == z.").Success);
    }

    [Fact]
    public void IllFormedByte_PeekThrowsWithoutConsuming_ReadResyncs()
    {
        string f = File("bad.txt", new byte[] { 0xFF, (byte)'a' });
        var e = new PrologEngine();
        // Two peeks both see the error (nothing consumed); the failed GET
        // consumes the one bad byte, so the next get reads 'a'.
        Assert.True(e.Query(
            $"open('{f}', read, S), "
            + "catch(peek_char(S, _), error(E1, _), true), "
            + "catch(peek_char(S, _), error(E2, _), true), "
            + "catch(get_char(S, _), error(E3, _), true), "
            + "get_char(S, A), close(S), "
            + "E1 == representation_error(character), E1 == E2, E2 == E3, "
            + "A == a.").Success);
    }

    [Fact]
    public void TruncatedSequenceAtEof_Throws()
    {
        string f = File("trunc.txt", new byte[] { (byte)'a', 0xC3 });
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{f}', read, S), get_char(S, a), "
            + "catch(get_char(S, _), error(E, _), true), close(S), "
            + "E == representation_error(character).").Success);
    }

    [Fact]
    public void Latin1Encoding_KeepsByteFaithfulReads()
    {
        string f = File("l1.txt", new byte[] { 0xE9 });   // é in Latin-1
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{f}', read, S, [encoding(iso_latin_1)]), "
            + "get_char(S, C), close(S), C == 'é'.").Success);
    }

    [Fact]
    public void Utf16Bom_KeepsAutoDetection()
    {
        // UTF-16LE BOM + "hi" — StreamReader auto-detects; the strict UTF-8
        // reader would call the 0xFF lead ill-formed.
        string f = File("u16.txt",
            new byte[] { 0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0 });
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{f}', read, S), get_char(S, h), get_char(S, i), "
            + "close(S).").Success);
    }

    [Fact]
    public void Reposition_WorksOnTheStrictReader()
    {
        string f = File("pos.txt", new byte[] { (byte)'a', 0xC3, 0xA9, (byte)'b' });
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{f}', read, S, [reposition(true)]), "
            + "get_char(S, a), get_char(S, 'é'), "
            + "set_stream_position(S, 1), get_char(S, C), close(S), "
            + "C == 'é'.").Success);
    }

    [Fact]
    public void CrBeforeIllFormedByte_KeepsTheCr()
    {
        // ADR-045 CR look-ahead: the LF probe behind a CR must not lose the
        // CR when what follows is ill-formed; the error surfaces on the read
        // positioned AT the bad byte.
        string f = File("cr.txt", new byte[] { (byte)'\r', 0xFF, (byte)'x' });
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{f}', read, S), get_char(S, C1), "
            + "catch(get_char(S, _), error(E, _), true), get_char(S, X), "
            + "close(S), char_code(C1, 13), "
            + "E == representation_error(character), X == x.").Success);
    }
}
