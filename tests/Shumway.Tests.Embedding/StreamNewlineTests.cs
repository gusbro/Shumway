using System;
using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-045 end to end: a CR-LF file read as TEXT yields <c>\n</c>
/// (on Windows, matching GNU/C stdio), and the same file read as BINARY
/// still yields both bytes. The pair is what the ISO text/binary
/// distinction is for, so both halves are pinned together.</summary>
public sealed class StreamNewlineTests
{
    /// <summary>What a text read of a CR-LF file must produce here: the
    /// translated newline on Windows, the untouched CR anywhere else —
    /// which is also what GNU does on each platform.</summary>
    private static readonly string ExpectedFourthChar =
        OperatingSystem.IsWindows() ? "\n" : "\r";

    private static string CrLfFile()
    {
        string p = Path.Combine(Path.GetTempPath(),
            "shumway_nl_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllBytes(p, "abc\r\ndef\r\n"u8.ToArray());
        return p;
    }

    private static string Quoted(string path) => path.Replace("\\", "\\\\");

    [Fact]
    public void TextRead_CollapsesCrLf()
    {
        string file = CrLfFile();
        try
        {
            var e = new PrologEngine();
            var sol = e.Query(
                $"open('{Quoted(file)}', read, S), "
                + "get_char(S, _), get_char(S, _), get_char(S, _), "
                + "get_char(S, C4), get_char(S, C5), close(S).");
            Assert.True(sol.Success);
            Assert.Equal(ExpectedFourthChar, ((AtomTerm)sol["C4"]!).Name);
            Assert.Equal(OperatingSystem.IsWindows() ? "d" : "\n",
                         ((AtomTerm)sol["C5"]!).Name);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void BinaryRead_KeepsBothBytes()
    {
        string file = CrLfFile();
        try
        {
            var e = new PrologEngine();
            var sol = e.Query(
                $"open('{Quoted(file)}', read, S, [type(binary)]), "
                + "get_byte(S, _), get_byte(S, _), get_byte(S, _), "
                + "get_byte(S, B4), get_byte(S, B5), close(S).");
            Assert.True(sol.Success);
            Assert.Equal(13L, ((IntTerm)sol["B4"]!).Value);
            Assert.Equal(10L, ((IntTerm)sol["B5"]!).Value);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Consult_AndRead_AgreeOnACrLfSource()
    {
        // A RAW newline inside a quoted atom is an ISO error, so the construct
        // that legally spans lines is the continuation escape. What this pins
        // is that the two load routes agree on one file: consult/1 slurps it
        // with File.ReadAllText while open/3 + read/2 goes through a stream
        // handle, and those must not diverge.
        string p = Path.Combine(Path.GetTempPath(),
            "shumway_nl_" + Guid.NewGuid().ToString("N") + ".pl");
        File.WriteAllBytes(p, "spans('a\\\r\nb').\r\n"u8.ToArray());
        try
        {
            var e = new PrologEngine();
            e.ConsultFile(p);
            var consulted = e.Query("spans(A).");
            Assert.True(consulted.Success);

            var streamed = e.Query(
                $"open('{Quoted(p)}', read, S), read(S, T), close(S), arg(1, T, B).");
            Assert.True(streamed.Success);

            Assert.Equal("ab", ((AtomTerm)consulted["A"]!).Name);
            Assert.Equal(((AtomTerm)streamed["B"]!).Name,
                         ((AtomTerm)consulted["A"]!).Name);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Position_AdvancesByOnePerTranslatedNewline()
    {
        string file = CrLfFile();
        try
        {
            var e = new PrologEngine();
            var sol = e.Query(
                $"open('{Quoted(file)}', read, S), "
                + "get_char(S, _), get_char(S, _), get_char(S, _), get_char(S, _), "
                + "stream_property(S, position(P)), close(S).");
            Assert.True(sol.Success);
            Assert.Equal(4L, ((IntTerm)sol["P"]!).Value);
        }
        finally { File.Delete(file); }
    }
}
