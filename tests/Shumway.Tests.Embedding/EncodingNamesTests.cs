using System;
using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Encoding names: each encoding has an official name, which
/// stream_property/2 and the encoding flag report, and aliases, which every place
/// that takes a name accepts. A question about a specific name passes with any name
/// of the encoding, and a module loaded as an SWI library sees the engine's own
/// names.</summary>
public sealed class EncodingNamesTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "shumway-encnames-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name).Replace('\\', '/');

    private static string Name(Solution solution, string variable) =>
        ((AtomTerm)solution[variable]!).Name;

    [Theory]
    [InlineData(@"'UTF-8'", "UTF-8")]
    [InlineData(@"utf8", "UTF-8")]
    [InlineData(@"'ISO-8859-1'", "ISO-8859-1")]
    [InlineData(@"iso_latin_1", "ISO-8859-1")]
    [InlineData(@"'US-ASCII'", "US-ASCII")]
    [InlineData(@"ascii", "US-ASCII")]
    [InlineData(@"'UTF-16LE'", "UTF-16LE")]
    [InlineData(@"utf16le", "UTF-16LE")]
    [InlineData(@"'UCS-2LE'", "UTF-16LE")]
    [InlineData(@"'UTF-16BE'", "UTF-16BE")]
    [InlineData(@"utf16be", "UTF-16BE")]
    [InlineData(@"'UCS-2BE'", "UTF-16BE")]
    [InlineData(@"'UTF-32LE'", "UTF-32LE")]
    [InlineData(@"utf32le", "UTF-32LE")]
    [InlineData(@"'UTF-32BE'", "UTF-32BE")]
    [InlineData(@"utf32be", "UTF-32BE")]
    [InlineData(@"'UTF-16'", "UTF-16")]
    [InlineData(@"'UCS-2'", "UTF-16")]
    [InlineData(@"'UTF-32'", "UTF-32")]
    public void EveryNameOpensAStream_ThatReportsTheOfficialName(string option, string official)
    {
        var sol = new PrologEngine().Query($@"
            open('{P("o.txt")}', write, S, [encoding({option})]),
            stream_property(S, encoding(E)),
            close(S).");
        Assert.True(sol.Success);
        Assert.Equal(official, Name(sol, "E"));
    }

    [Fact]
    public void AStreamOpenedWithNoOption_ReportsTheOfficialName()
    {
        var sol = new PrologEngine().Query($@"
            open('{P("o.txt")}', write, S), stream_property(S, encoding(E)), close(S).");
        Assert.Equal("UTF-8", Name(sol, "E"));
    }

    [Theory]
    [InlineData(@"utf8")]
    [InlineData(@"'UTF-8'")]
    public void AQuestionAboutAStream_PassesWithAnyNameOfItsEncoding(string asked)
    {
        var e = new PrologEngine();
        Assert.True(e.Query($@"
            open('{P("o.txt")}', write, S),
            stream_property(S, encoding({asked})),
            close(S).").Success);
    }

    [Theory]
    [InlineData(@"'UTF-16'", @"'UTF-16BE'")]
    [InlineData(@"'UTF-16'", @"utf16be")]
    [InlineData(@"'UTF-16BE'", @"'UTF-16'")]
    [InlineData(@"'UTF-32'", @"'UTF-32BE'")]
    [InlineData(@"'UTF-32BE'", @"'UTF-32'")]
    public void AQuestionAboutAStream_PassesWithANameThatGivesTheSameBytes(string opened, string asked)
    {
        var e = new PrologEngine();
        Assert.True(e.Query($@"
            open('{P("o.txt")}', write, S, [encoding({opened})]),
            stream_property(S, encoding({asked})),
            close(S).").Success);
    }

    [Theory]
    [InlineData(@"utf16le")]
    [InlineData(@"'UTF-16LE'")]
    [InlineData(@"not_an_encoding")]
    public void AQuestionAboutAStream_FailsWithTheNameOfAnotherEncoding(string asked)
    {
        var e = new PrologEngine();
        Assert.True(e.Query($@"
            open('{P("o.txt")}', write, S),
            \+ stream_property(S, encoding({asked})),
            close(S).").Success);
    }

    [Fact]
    public void TheFlag_ReportsTheOfficialName()
    {
        var e = new PrologEngine();
        Assert.Equal("UTF-8", Name(e.Query("current_prolog_flag(encoding, E)."), "E"));
        Assert.True(e.Query("set_prolog_flag(encoding, iso_latin_1).").Success);
        Assert.Equal("ISO-8859-1", Name(e.Query("current_prolog_flag(encoding, E)."), "E"));
    }

    [Theory]
    [InlineData(@"utf8", true)]
    [InlineData(@"'UTF-8'", true)]
    [InlineData(@"utf16le", false)]
    public void AQuestionAboutTheFlag_PassesWithAnyNameOfItsEncoding(string asked, bool passes)
    {
        Assert.Equal(passes,
            new PrologEngine().Query($"current_prolog_flag(encoding, {asked}).").Success);
    }

    [Fact]
    public void EnumeratingTheFlags_GivesTheOfficialName()
    {
        var sol = new PrologEngine().Query("current_prolog_flag(F, V), F == encoding.");
        Assert.Equal("UTF-8", Name(sol, "V"));
    }

    [Theory]
    [InlineData(@"'UTF-16'", new byte[] { 0xFE, 0xFF, 0x00, 0x61 })]
    [InlineData(@"'UCS-2'", new byte[] { 0xFE, 0xFF, 0x00, 0x61 })]
    [InlineData(@"'UTF-32'", new byte[] { 0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x61 })]
    public void NoByteOrderInTheName_WritesBigEndianWithAMark(string name, byte[] bytes)
    {
        var e = new PrologEngine();
        Assert.True(e.Query($@"
            open('{P("o.bin")}', write, S, [encoding({name})]),
            stream_property(S, bom(true)),
            put_char(S, a), close(S).").Success);
        Assert.Equal(bytes, File.ReadAllBytes(P("o.bin")));
    }

    [Fact]
    public void Bom_False_LeavesTheMarkOut()
    {
        var e = new PrologEngine();
        Assert.True(e.Query($@"
            open('{P("o.bin")}', write, S, [encoding('UTF-16'), bom(false)]),
            put_char(S, a), close(S).").Success);
        Assert.Equal(new byte[] { 0x00, 0x61 }, File.ReadAllBytes(P("o.bin")));
    }

    [Fact]
    public void NoByteOrderInTheName_AppendingWritesNoMark()
    {
        File.WriteAllBytes(P("o.bin"), new byte[] { 0xFE, 0xFF, 0x00, 0x61 });
        var e = new PrologEngine();
        Assert.True(e.Query($@"
            open('{P("o.bin")}', append, S, [encoding('UTF-16')]),
            stream_property(S, bom(false)),
            put_char(S, b), close(S).").Success);
        Assert.Equal(new byte[] { 0xFE, 0xFF, 0x00, 0x61, 0x00, 0x62 }, File.ReadAllBytes(P("o.bin")));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x61, 0x00 }, "UTF-16LE")]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x61 }, "UTF-16BE")]
    [InlineData(new byte[] { 0x00, 0x61 }, "UTF-16")]
    public void NoByteOrderInTheName_ReadsByTheMark_OrBigEndianWithoutOne(byte[] bytes, string official)
    {
        File.WriteAllBytes(P("i.bin"), bytes);
        var sol = new PrologEngine().Query($@"
            open('{P("i.bin")}', read, S, [encoding('UTF-16')]),
            stream_property(S, encoding(E)), get_char(S, C), close(S).");
        Assert.Equal(official, Name(sol, "E"));
        Assert.Equal("a", Name(sol, "C"));
    }

    [Fact]
    public void AnUnknownName_IsAnError()
    {
        Assert.True(new PrologEngine().Query($@"
            catch(open('{P("o.txt")}', write, _, [encoding(klingon)]),
                  error(domain_error(stream_option, encoding(klingon)), _), true).").Success);
    }

    private PrologEngine EngineWithSwiModule()
    {
        string lib = Path.Combine(_dir, "swilib");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(lib, "swimod.pl"), @"
:- module(swimod, [flag_name/1, stream_name/2, stream_asked/2, flag_asked/1]).
flag_name(E) :- current_prolog_flag(encoding, E).
flag_asked(E) :- current_prolog_flag(encoding, E).
stream_name(F, E) :- open(F, write, S), stream_property(S, encoding(E)), close(S).
stream_asked(F, E) :- open(F, write, S), stream_property(S, encoding(E)), close(S).
");
        var e = new PrologEngine();
        e.AddLibraryDirectory(lib, "swi");
        e.ConsultString(":- use_module(library(swimod)).");
        return e;
    }

    [Fact]
    public void AModuleLoadedAsAnSwiLibrary_SeesTheEngineNames()
    {
        var e = EngineWithSwiModule();
        Assert.Equal("utf8", Name(e.Query("flag_name(E)."), "E"));
        Assert.Equal("utf8", Name(e.Query($"stream_name('{P("o.txt")}', E)."), "E"));
    }

    [Fact]
    public void AnSwiLibraryInTheEngine_DoesNotChangeWhatTheRestSees()
    {
        var e = EngineWithSwiModule();
        Assert.Equal("UTF-8", Name(e.Query("current_prolog_flag(encoding, E)."), "E"));
        Assert.Equal("UTF-8", Name(e.Query($@"
            open('{P("o.txt")}', write, S), stream_property(S, encoding(E)), close(S)."), "E"));
    }

    [Theory]
    [InlineData(@"utf8")]
    [InlineData(@"'UTF-8'")]
    public void InAnSwiLibrary_AQuestionPassesWithAnyName(string asked)
    {
        var e = EngineWithSwiModule();
        Assert.True(e.Query($"flag_asked({asked}).").Success);
        Assert.True(e.Query($"stream_asked('{P("o.txt")}', {asked}).").Success);
    }
}
