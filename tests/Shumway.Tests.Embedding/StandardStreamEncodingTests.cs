using System;
using System.IO;
using System.Text;
using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The encoding property of user_input, user_output and user_error: what stands
/// behind each one, the console or what the host gave. stream_property/2 takes a stream
/// and not an alias (ISO 8.11.8), hence the alias(_) goal in these queries.</summary>
public sealed class StandardStreamEncodingTests
{
    private static string Name(Solution solution, string variable) =>
        ((AtomTerm)solution[variable]!).Name;

    private static StreamWriter WriterIn(Encoding encoding) => new(new MemoryStream(), encoding);

    private static StreamReader ReaderIn(Encoding encoding) => new(new MemoryStream(), encoding);

    private sealed class Windows1252 : UTF8Encoding
    {
        public override int CodePage => 1252;
        public override string WebName => "windows-1252";
    }

    private sealed class Declared : StringReader, IEncodedReader
    {
        public Declared(Encoding encoding) : base("") => Encoding = encoding;
        public Encoding Encoding { get; }
    }

    private sealed class NoName : StringWriter
    {
        public override Encoding Encoding => throw new NotImplementedException();
    }

    public static TheoryData<Encoding, string> Writers() => new()
    {
        { new UTF8Encoding(false), "UTF-8" },
        { Encoding.GetEncoding("ISO-8859-1"), "ISO-8859-1" },
        { Encoding.ASCII, "US-ASCII" },
        { new UnicodeEncoding(false, false), "UTF-16LE" },
        { new UnicodeEncoding(true, false), "UTF-16BE" },
        { new UTF32Encoding(false, false), "UTF-32LE" },
        { new UTF32Encoding(true, false), "UTF-32BE" },
        { new Windows1252(), "windows-1252" },
    };

    [Theory]
    [MemberData(nameof(Writers))]
    public void UserOutput_ReportsTheEncodingOfItsWriter(Encoding encoding, string official)
    {
        var e = new PrologEngine { Out = WriterIn(encoding) };
        Assert.Equal(official, Name(e.Query("stream_property(X, alias(user_output)), stream_property(X, encoding(E))."), "E"));
        Assert.Equal(official, Name(e.Query("current_output(S), stream_property(S, encoding(E))."), "E"));
    }

    [Fact]
    public void UserOutput_FollowsTheWriterTheHostGivesLater()
    {
        var e = new PrologEngine { Out = WriterIn(new UTF8Encoding(false)) };
        Assert.Equal("UTF-8", Name(e.Query("stream_property(X, alias(user_output)), stream_property(X, encoding(E))."), "E"));
        e.Out = WriterIn(Encoding.ASCII);
        Assert.Equal("US-ASCII", Name(e.Query("stream_property(X, alias(user_output)), stream_property(X, encoding(E))."), "E"));
    }

    [Fact]
    public void UserOutput_WithAWriterThatNamesNoEncoding_HasNone()
    {
        var e = new PrologEngine { Out = new NoName() };
        Assert.True(e.Query(@"stream_property(X, alias(user_output)), \+ stream_property(X, encoding(_)).").Success);
    }

    [Fact]
    public void UserError_ReportsTheEncodingOfTheConsole()
    {
        string expected = TextEncodings.OfficialName(TextEncodings.NameOf(Console.Error.Encoding));
        Assert.Equal(expected,
            Name(new PrologEngine().Query("stream_property(X, alias(user_error)), stream_property(X, encoding(E))."), "E"));
    }

    [Fact]
    public void UserInput_ReportsTheEncodingOfItsStreamReader()
    {
        var e = new PrologEngine { In = ReaderIn(Encoding.GetEncoding("ISO-8859-1")) };
        Assert.Equal("ISO-8859-1", Name(e.Query("stream_property(X, alias(user_input)), stream_property(X, encoding(E))."), "E"));
        Assert.Equal("ISO-8859-1", Name(e.Query("current_input(S), stream_property(S, encoding(E))."), "E"));
    }

    [Fact]
    public void UserInput_AsksAReaderThatKnowsItsEncoding()
    {
        var e = new PrologEngine { In = new Declared(new UnicodeEncoding(true, false)) };
        Assert.Equal("UTF-16BE", Name(e.Query("stream_property(X, alias(user_input)), stream_property(X, encoding(E))."), "E"));
    }

    [Fact]
    public void UserInput_WithAPlainTextReader_HasNoEncoding()
    {
        var e = new PrologEngine { In = new StringReader("") };
        Assert.True(e.Query(@"stream_property(X, alias(user_input)), \+ stream_property(X, encoding(_)).").Success);
    }

    [Fact]
    public void UserInput_WithNothingGiven_ReportsTheConsole()
    {
        string expected = TextEncodings.OfficialName(TextEncodings.NameOf(Console.InputEncoding));
        Assert.Equal(expected,
            Name(new PrologEngine().Query("stream_property(X, alias(user_input)), stream_property(X, encoding(E))."), "E"));
    }

    [Theory]
    [InlineData(@"utf8", true)]
    [InlineData(@"'UTF-8'", true)]
    [InlineData(@"'UTF-16LE'", false)]
    public void AQuestionAboutAStandardStream_PassesWithAnyNameOfItsEncoding(string asked, bool passes)
    {
        var e = new PrologEngine { Out = WriterIn(new UTF8Encoding(false)) };
        Assert.Equal(passes, e.Query($"stream_property(X, alias(user_output)), stream_property(X, encoding({asked})).").Success);
    }

    [Fact]
    public void AStandardStream_ReportsNoMark()
    {
        var e = new PrologEngine { Out = WriterIn(new UTF8Encoding(false)) };
        Assert.True(e.Query(@"stream_property(X, alias(user_output)), \+ stream_property(X, bom(_)).").Success);
        Assert.True(e.Query(@"stream_property(X, alias(user_error)), \+ stream_property(X, bom(_)).").Success);
        Assert.True(e.Query(@"stream_property(X, alias(user_input)), \+ stream_property(X, bom(_)).").Success);
    }

    [Fact]
    public void EnumeratingTheStreams_IncludesTheStandardOnes()
    {
        var e = new PrologEngine { Out = WriterIn(Encoding.ASCII) };
        Assert.True(e.Query(@"
            current_output(O),
            findall(S, stream_property(S, encoding(_)), Ss),
            member(X, Ss), X == O.").Success);
    }

    [Fact]
    public void AnAliasMovedToAnotherStream_ReportsThatStream()
    {
        string file = Path.Combine(Path.GetTempPath(),
            "shumway-stdenc-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');
        try
        {
            var e = new PrologEngine { Out = WriterIn(new UTF8Encoding(false)) };
            var sol = e.Query($@"
                open('{file}', write, S, [encoding('UTF-16LE')]),
                set_stream(S, alias(user_output)),
                stream_property(X, alias(user_output)), stream_property(X, encoding(E)),
                close(S).");
            Assert.Equal("UTF-16LE", Name(sol, "E"));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void ACapturedOutput_HasNoEncoding()
    {
        var e = new PrologEngine { Out = WriterIn(new UTF8Encoding(false)) };
        Assert.True(e.Query(@"
            with_output_to(atom(_), (current_output(S), \+ stream_property(S, encoding(_)))).").Success);
    }
}
