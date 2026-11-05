using System;
using System.IO;
using System.Text;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The <c>:- encoding/1</c> directive and its stream-side half
/// (ADR-048 follow-up): consult decodes a source file per its BOM and its
/// leading encoding directive — tolerantly, so a file whose bytes are not
/// valid under the default encoding still gets its directive found and is
/// then re-decoded — and open/4 detects BOMs, takes UTF-16/32 encodings,
/// and reports encoding/bom via stream_property/2.</summary>
public sealed class EncodingDirectiveTests : IDisposable
{
    // net48 has no Directory.CreateTempSubdirectory.
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "shumway-enc-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, byte[] bytes)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p.Replace('\\', '/');
    }

    private static byte[] Utf16Be(string s) => Encoding.BigEndianUnicode.GetBytes(s);

    [Fact]
    public void NoBomFileWithDirective_ConsultsWithoutBlowingUp()
    {
        // UTF-16BE, no BOM: read under the UTF-8 default this is NUL-laced
        // garbage — the directive must still be found and the file re-read.
        var e = new PrologEngine();
        string f = Write("be.pl",
            Utf16Be(":- encoding(utf16be).\np('griego_αβγ_\U0001F600').\n"));
        e.ConsultFile(f);
        var sol = e.Query("p(A), atom_length(A, L).");
        Assert.True(sol.Success);
        Assert.Equal(12L, ((IntTerm)sol["L"]!).Value);   // code points, not units
    }

    [Fact]
    public void QuotedCharsetSpelling_IsAccepted()
    {
        // The Logtalk-style quoted name in the directive.
        var e = new PrologEngine();
        string f = Write("le32.pl",
            Encoding.UTF32.GetBytes(":- encoding('UTF-32LE').\nq(ancho).\n"));
        e.ConsultFile(f);
        Assert.True(e.Query("q(ancho).").Success);
    }

    private static byte[] Latin1(string s) => Encoding.GetEncoding(28591).GetBytes(s);

    [Theory]
    [InlineData(@":- encoding(iso_latin_1).")]
    [InlineData(@":-(encoding(iso_latin_1)).")]
    [InlineData(@":- (encoding(iso_latin_1)).")]
    [InlineData(@":-( ( encoding( 'ISO-8859-1' ) ) ) .")]
    public void TheDirective_IsFoundInEveryWritingOfIt(string directive)
    {
        var e = new PrologEngine();
        string f = Write("latin.pl", Latin1(directive + "\np('caf\u00e9').\n"));
        e.ConsultFile(f);
        Assert.True(e.Query("p(X), atom_codes(X, [99, 97, 102, 233]).").Success);
    }

    [Fact]
    public void AParenthesisLeftOpen_IsNotTheDirective()
    {
        string decoded = Shumway.Core.TextFile.DecodeSource(
            Latin1(":-(encoding(iso_latin_1).\np('caf\u00e9').\n"));
        Assert.DoesNotContain("caf\u00e9", decoded);
    }

    private (PrologEngine Engine, string Warnings) ConsultLatin1(string text)
    {
        var warnings = new StringWriter();
        var e = new PrologEngine { Warnings = warnings };
        e.ConsultFile(Write("bytes.pl", Latin1(text)));
        return (e, warnings.ToString());
    }

    [Theory]
    [InlineData("first.\np('caf\u00e9').\nlast.\n")]
    [InlineData("first.\np(\"caf\u00e9\").\nlast.\n")]
    [InlineData("first.\np(caf\u00e9).\nlast.\n")]
    [InlineData("first.\np(0'\u00e9).\nlast.\n")]
    public void BytesNotValidInATerm_AreReported_AndOnlyThatClauseIsSkipped(string text)
    {
        var (e, warnings) = ConsultLatin1(text);
        Assert.Contains("Bytes not valid in the file's encoding", warnings);
        Assert.Contains("bytes.pl:2:", warnings);
        Assert.True(e.Query("first, last.").Success);
        Assert.False(e.Query("catch(p(_), _, fail).").Success);
    }

    [Theory]
    [InlineData("p('caf\u00e9').")]
    [InlineData("p($caf\u00e9$).")]
    [InlineData("p(`\u00e9).")]
    public void BytesNotValid_AreReportedInAritysTokensToo(string clause)
    {
        var (e, warnings) = ConsultLatin1(
            ":- set_prolog_flag(arity_compat, true).\nfirst.\n" + clause + "\nlast.\n");
        Assert.Contains("Bytes not valid in the file's encoding", warnings);
        Assert.Contains("bytes.pl:3:", warnings);
        Assert.True(e.Query("first, last.").Success);
    }

    [Theory]
    [InlineData("% caf\u00e9\np(a).\n")]
    [InlineData("/* caf\u00e9 */\np(a).\n")]
    [InlineData("p(a). % caf\u00e9\n")]
    public void BytesNotValidInAComment_DoNoHarm(string text)
    {
        var (e, warnings) = ConsultLatin1(text);
        Assert.Equal("", warnings);
        Assert.True(e.Query("p(a).").Success);
    }

    [Fact]
    public void AReplacementCharacterWrittenOnPurpose_IsText()
    {
        var warnings = new StringWriter();
        var e = new PrologEngine { Warnings = warnings };
        e.ConsultFile(Write("fffd.pl", Encoding.UTF8.GetBytes("p('\uFFFD').\n")));
        Assert.Equal("", warnings.ToString());
        Assert.True(e.Query("p(X), atom_codes(X, [65533]).").Success);
    }

    [Fact]
    public void ADeclaredEncoding_MakesTheBytesValid()
    {
        var (e, warnings) = ConsultLatin1(":- encoding(iso_latin_1).\np('caf\u00e9').\n");
        Assert.Equal("", warnings);
        Assert.True(e.Query("p(X), atom_codes(X, [99, 97, 102, 233]).").Success);
    }

    [Fact]
    public void AByteAboveAsciiInADeclaredAsciiFile_IsReported()
    {
        var (_, warnings) = ConsultLatin1(":- encoding(ascii).\np('caf\u00e9').\n");
        Assert.Contains("Bytes not valid in the file's encoding", warnings);
    }

    [Fact]
    public void BomWins_AndDirectiveIsThenANoOp()
    {
        var e = new PrologEngine();
        string f = Write("lebom.pl", Concat(
            new byte[] { 0xFF, 0xFE },
            Encoding.Unicode.GetBytes(":- encoding(utf16le).\nr('Γειά').\n")));
        e.ConsultFile(f);
        Assert.True(e.Query("r(X), atom_length(X, 4).").Success);
    }

    [Fact]
    public void Open4_DetectsBoms_AndBomBeatsTheEncodingOption()
    {
        var e = new PrologEngine();
        string f = Write("bom16le.txt", Concat(
            new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes("abc")));
        var sol = e.Query(
            $"open('{f}', read, S), stream_property(S, encoding(E)), "
            + "stream_property(S, bom(B)), close(S).");
        Assert.True(sol.Success);
        Assert.Equal("utf16le", ((AtomTerm)sol["E"]!).Name);
        Assert.Equal("true", ((AtomTerm)sol["B"]!).Name);
        // encoding(utf8) requested, but the BOM takes precedence.
        var sol2 = e.Query(
            $"open('{f}', read, S, [encoding(utf8)]), "
            + "stream_property(S, encoding(E)), get_char(S, C), close(S).");
        Assert.True(sol2.Success);
        Assert.Equal("utf16le", ((AtomTerm)sol2["E"]!).Name);
        Assert.Equal("a", ((AtomTerm)sol2["C"]!).Name);
    }

    [Fact]
    public void Open4_BomFalse_DeliversTheMarkAsData()
    {
        var e = new PrologEngine();
        string f = Write("bom16le2.txt", Concat(
            new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes("x")));
        var sol = e.Query(
            $"open('{f}', read, S, [encoding(utf16le), bom(false)]), "
            + "get_code(S, C), close(S).");
        Assert.True(sol.Success);
        Assert.Equal(0xFEFFL, ((IntTerm)sol["C"]!).Value);
    }

    [Fact]
    public void EncodingFlag_SetsTheDefaultForOpenAndConsult()
    {
        var e = new PrologEngine();
        string f = Write("plain16.txt", Utf16Be("hola"));
        Assert.True(e.Query("set_prolog_flag(encoding, 'UTF-16BE'), "
            + "current_prolog_flag(encoding, utf16be).").Success);
        var sol = e.Query($"open('{f}', read, S), get_char(S, C), close(S), "
            + "set_prolog_flag(encoding, utf8).");
        Assert.True(sol.Success);
        Assert.Equal("h", ((AtomTerm)sol["C"]!).Name);
    }

    [Fact]
    public void Open4_WritesUtf16WithExplicitBom_AndReadsItBack()
    {
        var e = new PrologEngine();
        string f = Path.Combine(_dir, "out16.txt").Replace('\\', '/');
        Assert.True(e.Query(
            $"open('{f}', write, W, [encoding(utf16le), bom(true)]), "
            + "write(W, 'hola.'), nl(W), close(W), "
            + $"open('{f}', read, R), stream_property(R, encoding(utf16le)), "
            + "stream_property(R, bom(true)), read_term(R, T, []), close(R), "
            + "T == hola.").Success);
    }

    [Fact]
    public void LenientEscapesFlag_GatesTheFixedWidthUnicodeEscapes()
    {
        var e = new PrologEngine();
        // Strict default: the escape is unknown (the conformance suites
        // check). The flag flip and its use must be separate reads — a
        // query is parsed whole before it runs.
        Assert.ThrowsAny<Exception>(() => e.Query("X = 'a\\u0041b'."));
        Assert.True(e.Query("set_prolog_flag(lenient_escapes, true).").Success);
        Assert.True(e.Query("X = 'a\\u0041b', X == 'aAb'.").Success);
        Assert.True(e.Query("set_prolog_flag(lenient_escapes, false).").Success);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }
}
