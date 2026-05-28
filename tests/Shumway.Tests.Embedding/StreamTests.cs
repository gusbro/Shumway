using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 35: file-based output streams. open/3 with write / append
/// mode, close/1, write/2, and nl/1 (stream variant). Phase 1 doesn't
/// yet support reading streams.
/// </summary>
public class StreamTests
{
    private static string TempPath() =>
        // Use forward slashes so the path embeds cleanly inside a
        // single-quoted Prolog atom — backslashes would otherwise be
        // interpreted as escape sequences by the lexer.
        Path.Combine(Path.GetTempPath(), $"shumway-stream-{Guid.NewGuid():N}.txt")
            .Replace('\\', '/');

    [Fact]
    public void Open_Write_CreatesFileAndCloseFlushes()
    {
        string path = TempPath();
        try
        {
            var engine = new PrologEngine();
            engine.Query($"open('{path}', write, S), write(S, hello), close(S).");

            // After close the file should contain exactly the written text.
            string content = File.ReadAllText(path);
            Assert.Equal("hello", content);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Open_Append_AddsToExistingFile()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "first\n");
            var engine = new PrologEngine();
            engine.Query($"open('{path}', append, S), write(S, second), close(S).");

            string content = File.ReadAllText(path);
            Assert.Contains("first", content);
            Assert.Contains("second", content);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WriteAndNl_TogetherProduceFormattedOutput()
    {
        string path = TempPath();
        try
        {
            var engine = new PrologEngine();
            engine.Query(
                $"open('{path}', write, S), "
                + "write(S, line1), nl(S), "
                + "write(S, line2), nl(S), "
                + "close(S).");

            string[] lines = File.ReadAllText(path)
                .Split(new[] { System.Environment.NewLine },
                       StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.Equal("line1", lines[0]);
            Assert.Equal("line2", lines[1]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Write_Compound_RendersInOperatorForm()
    {
        string path = TempPath();
        try
        {
            var engine = new PrologEngine();
            engine.Query(
                $"open('{path}', write, S), write(S, a + b), close(S).");

            string content = File.ReadAllText(path);
            Assert.Equal("a+b", content);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Open_UnknownMode_Throws()
    {
        var engine = new PrologEngine();
        // 'read' is now supported; an unknown mode (gibberish) raises.
        Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => engine.Query("open('/tmp/whatever.txt', purple, _)."));
    }

    [Fact]
    public void Close_NonStreamHandle_Throws()
    {
        var engine = new PrologEngine();
        Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => engine.Query("close(not_a_stream)."));
    }
}
