using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary><c>read_term/3</c>'s <c>line_counts(Begin, End)</c> option and
/// <c>line_count/2</c>: the lines a term read starts and ends on, and the line
/// a text input stream is on. Begin is the line of the term's first token,
/// past the layout and comments before it; End is the line of its end dot.
/// Logtalk reports source lines with them.</summary>
public class ReadTermLineCountsTests
{
    // Line 1 is a comment, a block comment runs from line 4 into line 5 where
    // bar( starts, and two terms share line 7. The text ends with a newline,
    // so end of file is on line 8.
    private const string Source =
        "% a comment\n" +
        "foo(a).\n" +
        "\n" +
        "/* a block\n" +
        "   comment */ bar(\n" +
        "  b).\n" +
        "baz.  qux.\n";

    private static string WriteTemp(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), "lines_" + Guid.NewGuid().ToString("N") + ".pl");
        File.WriteAllText(path, text);
        return path.Replace("\\", "/");
    }

    private static void WithFile(string text, Action<PrologEngine, string> body)
    {
        string path = WriteTemp(text);
        try { body(new PrologEngine(), path); }
        finally { File.Delete(path); }
    }

    private const string ReadAll = @"
        read_all(S, Ls) :-
            read_term(S, T, [line_counts(B, E)]),
            (   T == end_of_file -> Ls = [eof-B-E]
            ;   Ls = [T-B-E|Rest], read_all(S, Rest)
            ).
        ";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void EachTermGetsTheLinesOfItsFirstTokenAndItsEndDot(string newline)
    {
        WithFile(Source.Replace("\n", newline), (engine, path) =>
        {
            engine.ConsultString(ReadAll);

            Assert.True(engine.Query($@"
                open('{path}', read, S), read_all(S, Ls), close(S),
                Ls == [foo(a)-2-2, bar(b)-5-6, baz-7-7, qux-7-7, eof-8-8].").Success);
        });
    }

    [Fact]
    public void LineCountFollowsTheReadsAndARepositioning()
    {
        WithFile(Source, (engine, path) =>
        {
            Assert.True(engine.Query($@"
                open('{path}', read, S),
                line_count(S, L0), L0 == 1,
                read(S, _), line_count(S, L1), L1 == 2,
                stream_property(S, position(P)),
                read(S, _), read(S, _), line_count(S, L2), L2 == 7,
                set_stream_position(S, P), line_count(S, L3), L3 == 2,
                read_term(S, T, [line_counts(B, E)]), T == bar(b), B-E == 5-6,
                close(S).").Success);
        });
    }

    [Fact]
    public void LineCountsHasTwoArguments()
    {
        WithFile(Source, (engine, path) =>
        {
            Assert.True(engine.Query($@"
                open('{path}', read, S),
                catch(read_term(S, _, [line_counts(_)]), error(E, _), true),
                close(S),
                E = domain_error(read_option, line_counts(_)).").Success);
        });
    }

    [Fact]
    public void AnOutputStreamHasNoLineCount()
    {
        string path = WriteTemp("");
        try
        {
            Assert.True(new PrologEngine().Query($@"
                open('{path}', write, S), \+ line_count(S, _), close(S).").Success);
        }
        finally { File.Delete(path); }
    }

    // The adapter shipped in logtalk/ reports each term's lines and the
    // current line to Logtalk, which used to get 0 for both.
    [Fact]
    public void TheLogtalkAdapterReportsTheLines()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent!;
        string adapter = Path.Combine(dir!.FullName, "logtalk", "adapters", "shumway.pl").Replace("\\", "/");

        WithFile(Source, (engine, path) =>
        {
            engine.ConsultFile(adapter);

            Assert.True(engine.Query($@"
                open('{path}', read, S),
                '$lgt_read_term'(S, T1, [], L1), T1 == foo(a), L1 == 2-2,
                '$lgt_read_term'(S, T2, [], L2), T2 == bar(b), L2 == 5-6,
                '$lgt_stream_current_line_number'(S, N), N == 6,
                close(S).").Success);
        });
    }
}
