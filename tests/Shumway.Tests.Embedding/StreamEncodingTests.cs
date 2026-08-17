using System;
using System.IO;
using System.Text;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The <c>encoding/1</c> open/4 option (SWI-style): a Latin-1 file
/// must read back with its BYTE VALUES as codes (Latin-1 is the first 256
/// Unicode code points) — the default UTF-8 reader turns every 0x80–0xFF
/// byte into U+FFFD, which silently corrupts ISO-8859-1 sources (the
/// Neumerkel conformity pages).</summary>
public sealed class StreamEncodingTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(),
            "shumway_enc_" + Guid.NewGuid().ToString("N") + ".txt");

    [Fact]
    public void OpenWithLatin1_ReadsHighBytesAsTheirCodePoints()
    {
        string file = TempPath();
        File.WriteAllBytes(file, new byte[] { 0x63, 0x61, 0x66, 0xE9 });   // "café"
        try
        {
            var e = new PrologEngine();
            string f = file.Replace("\\", "\\\\");
            var sol = e.Query(
                $"open('{f}', read, S, [encoding(iso_latin_1)]), "
                + "get_code(S, A), get_code(S, B), get_code(S, C), get_code(S, D), "
                + "close(S).");
            Assert.True(sol.Success);
            Assert.Equal(0x63L, ((IntTerm)sol["A"]!).Value);
            Assert.Equal(0xE9L, ((IntTerm)sol["D"]!).Value);   // é, not U+FFFD
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void OpenDefault_IsStillUtf8()
    {
        string file = TempPath();
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes("café"));
        try
        {
            var e = new PrologEngine();
            string f = file.Replace("\\", "\\\\");
            var sol = e.Query(
                $"open('{f}', read, S, []), "
                + "get_code(S, A), get_code(S, B), get_code(S, C), get_code(S, D), "
                + "close(S).");
            Assert.True(sol.Success);
            Assert.Equal(0xE9L, ((IntTerm)sol["D"]!).Value);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void WriteWithLatin1_EmitsSingleBytes()
    {
        string file = TempPath();
        try
        {
            var e = new PrologEngine();
            string f = file.Replace("\\", "\\\\");
            Assert.True(e.Query(
                $"open('{f}', write, S, [encoding(iso_latin_1)]), "
                + "put_code(S, 233), close(S).").Success);
            Assert.Equal(new byte[] { 0xE9 }, File.ReadAllBytes(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void UnknownEncoding_IsAStreamOptionDomainError()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(open('x.tmp', read, _, [encoding(klingon)]), "
            + "error(domain_error(_, _), _), true).").Success);
    }
}
