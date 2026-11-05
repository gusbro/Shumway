using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The predicates that read and write text, in every encoding and with
/// characters of every width: the bytes in the file are the ones the encoding says,
/// what comes back is what went in, and what the encoding cannot hold is an error.</summary>
public sealed class EncodingAwareBuiltinsTests : IDisposable
{
    private const string Wide = "aéあ\U0001D11E\nb";
    private const string Latin = "aé\nb";
    private const string Plain = "a\nb";

    private const string Helpers = @"
by_put_char(_, []).
by_put_char(S, [C|Cs]) :- char_code(Ch, C), put_char(S, Ch), by_put_char(S, Cs).
by_put_code(_, []).
by_put_code(S, [C|Cs]) :- put_code(S, C), by_put_code(S, Cs).
each_put_char([]).
each_put_char([C|Cs]) :- char_code(Ch, C), put_char(Ch), each_put_char(Cs).
each_put_code([]).
each_put_code([C|Cs]) :- put_code(C), each_put_code(Cs).
by_put_char_1(S, Cs) :- set_output(S), each_put_char(Cs), set_output(user_output).
by_put_code_1(S, Cs) :- set_output(S), each_put_code(Cs), set_output(user_output).
by_write_term(S, Cs) :- atom_codes(A, Cs), write_term(S, A, []).
by_write(S, Cs) :- atom_codes(A, Cs), write(S, A).
by_format_a(S, Cs) :- atom_codes(A, Cs), format(S, '~a', [A]).
by_format_s(S, Cs) :- format(S, '~s', [Cs]).

by_get_char(S, Cs) :-
    get_char(S, Ch),
    ( Ch == end_of_file -> Cs = [] ; char_code(Ch, C), Cs = [C|T], by_get_char(S, T) ).
by_get_code(S, Cs) :-
    get_code(S, C),
    ( C =:= -1 -> Cs = [] ; Cs = [C|T], by_get_code(S, T) ).
by_peek_char(S, Cs) :-
    peek_char(S, Ch),
    ( Ch == end_of_file -> get_char(S, _), Cs = []
    ; get_char(S, Ch), char_code(Ch, C), Cs = [C|T], by_peek_char(S, T) ).
by_peek_code(S, Cs) :-
    peek_code(S, C),
    ( C =:= -1 -> get_code(S, _), Cs = []
    ; get_code(S, C), Cs = [C|T], by_peek_code(S, T) ).
by_get_char_1(S, Cs) :- set_input(S), each_get_char(Cs), set_input(user_input).
by_get_code_1(S, Cs) :- set_input(S), each_get_code(Cs), set_input(user_input).
by_peek_char_1(S, Cs) :- set_input(S), each_peek_char(Cs), set_input(user_input).
by_peek_code_1(S, Cs) :- set_input(S), each_peek_code(Cs), set_input(user_input).
each_get_char(Cs) :-
    get_char(Ch),
    ( Ch == end_of_file -> Cs = [] ; char_code(Ch, C), Cs = [C|T], each_get_char(T) ).
each_get_code(Cs) :-
    get_code(C),
    ( C =:= -1 -> Cs = [] ; Cs = [C|T], each_get_code(T) ).
each_peek_char(Cs) :-
    peek_char(Ch),
    ( Ch == end_of_file -> get_char(_), Cs = []
    ; get_char(Ch), char_code(Ch, C), Cs = [C|T], each_peek_char(T) ).
each_peek_code(Cs) :-
    peek_code(C),
    ( C =:= -1 -> get_code(_), Cs = []
    ; get_code(C), Cs = [C|T], each_peek_code(T) ).

is_representation_error(G) :-
    catch((G, fail), error(representation_error(character), _), true).
";

    private readonly string _dir = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "shumway-encbuiltins-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name).Replace('\\', '/');

    private static PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.ConsultString(Helpers);
        return e;
    }

    private static Encoding NoMark(string official) => official switch
    {
        "UTF-8" => new UTF8Encoding(false),
        "ISO-8859-1" => Encoding.GetEncoding("ISO-8859-1"),
        "US-ASCII" => Encoding.ASCII,
        "UTF-16LE" => new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
        "UTF-16BE" or "UTF-16" => new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
        "UTF-32LE" => new UTF32Encoding(bigEndian: false, byteOrderMark: false),
        "UTF-32BE" or "UTF-32" => new UTF32Encoding(bigEndian: true, byteOrderMark: false),
        _ => throw new ArgumentException(official),
    };

    private static byte[] Bytes(string official, string text, bool mark = false)
    {
        Encoding enc = NoMark(official);
        return (mark ? enc.GetBytes("﻿") : Array.Empty<byte>()).Concat(enc.GetBytes(text)).ToArray();
    }

    private static string CodeList(string text)
    {
        var codes = new List<int>();
        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
            codes.Add(char.ConvertToUtf32(text, i));
        return "[" + string.Join(",", codes) + "]";
    }

    private static string TextFor(string official) => official switch
    {
        "ISO-8859-1" => Latin,
        "US-ASCII" => Plain,
        _ => Wide,
    };

    private static readonly string[] Officials =
        { "UTF-8", "UTF-16", "UTF-16LE", "UTF-16BE", "UTF-32", "UTF-32LE", "UTF-32BE", "ISO-8859-1", "US-ASCII" };

    public static IEnumerable<object[]> Writers() =>
        from official in Officials
        from writer in new[]
        {
            "by_put_char", "by_put_code", "by_put_char_1", "by_put_code_1",
            "by_write_term", "by_write", "by_format_a", "by_format_s",
        }
        select new object[] { official, writer };

    public static IEnumerable<object[]> Readers() =>
        from official in Officials
        from reader in new[]
        {
            "by_get_char", "by_get_code", "by_peek_char", "by_peek_code",
            "by_get_char_1", "by_get_code_1", "by_peek_char_1", "by_peek_code_1",
        }
        select new object[] { official, reader };

    [Theory]
    [MemberData(nameof(Writers))]
    public void AWriter_PutsTheBytesOfTheEncoding(string official, string writer)
    {
        string text = TextFor(official);
        // A name with no byte order gets a mark, by the rule for writing.
        byte[] expected = Bytes(official, text, mark: official is "UTF-16" or "UTF-32");
        Assert.NotEmpty(expected);
        Assert.True(Engine().Query($@"
            open('{P("o.bin")}', write, S, [encoding('{official}')]),
            {writer}(S, {CodeList(text)}),
            close(S).").Success);
        Assert.Equal(expected, File.ReadAllBytes(P("o.bin")));
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void AReader_GivesTheCharactersOfTheEncoding(string official, string reader)
    {
        string text = TextFor(official);
        File.WriteAllBytes(P("i.bin"), Bytes(official, text));
        Assert.True(Engine().Query($@"
            open('{P("i.bin")}', read, S, [encoding('{official}')]),
            {reader}(S, Cs),
            close(S),
            Cs == {CodeList(text)}.").Success);
    }

    [Theory]
    [InlineData("UTF-8")]
    [InlineData("UTF-16LE")]
    [InlineData("UTF-16BE")]
    [InlineData("UTF-32LE")]
    [InlineData("UTF-32BE")]
    public void AReader_FindsTheEncodingInTheMark_WithAnotherOption(string official)
    {
        File.WriteAllBytes(P("i.bin"), Bytes(official, Wide, mark: true));
        Assert.True(Engine().Query($@"
            open('{P("i.bin")}', read, S, [encoding('ISO-8859-1')]),
            stream_property(S, encoding('{official}')),
            stream_property(S, bom(true)),
            by_get_code(S, Cs),
            close(S),
            Cs == {CodeList(Wide)}.").Success);
    }

    [Theory]
    [InlineData("UTF-8")]
    [InlineData("UTF-16LE")]
    [InlineData("UTF-32BE")]
    [InlineData("UTF-16")]
    public void GettingACharacter_ChecksAGivenAstralOne(string official)
    {
        string clef = "\U0001D11E";
        File.WriteAllBytes(P("i.bin"), Bytes(official, clef + clef + clef));
        Assert.True(Engine().Query($@"
            char_code(C, 0x1D11E),
            open('{P("i.bin")}', read, S, [encoding('{official}')]),
            peek_char(S, C),
            get_char(S, C),
            get_code(S, 0x1D11E),
            peek_code(S, 0x1D11E),
            close(S).").Success);
    }

    [Theory]
    [InlineData("UTF-8", false)]
    [InlineData("UTF-8", true)]
    [InlineData("UTF-16LE", false)]
    [InlineData("UTF-16LE", true)]
    [InlineData("UTF-16BE", false)]
    [InlineData("UTF-16BE", true)]
    [InlineData("UTF-32LE", false)]
    [InlineData("UTF-32LE", true)]
    [InlineData("UTF-32BE", false)]
    [InlineData("UTF-32BE", true)]
    [InlineData("UTF-16", false)]
    [InlineData("UTF-32", false)]
    [InlineData("ISO-8859-1", false)]
    public void AReposition_LandsOnTheCharacter_WithOrWithoutAMark(string official, bool mark)
    {
        // Each read gives the code after the last, whatever its width in bytes.
        string first = official == "ISO-8859-1" ? "é" : "\U0001D11E";
        File.WriteAllBytes(P("i.bin"), Bytes(official, first + "bc", mark));
        int code = char.ConvertToUtf32(first, 0);
        Assert.True(Engine().Query($@"
            open('{P("i.bin")}', read, S, [encoding('{official}'), reposition(true)]),
            stream_property(S, position(P0)),
            get_code(S, {code}),
            stream_property(S, position(P1)),
            peek_code(S, 98),
            get_code(S, 98),
            set_stream_position(S, P1),
            get_code(S, 98),
            get_code(S, 99),
            set_stream_position(S, P0),
            get_code(S, {code}),
            peek_code(S, 98),
            close(S).").Success);
    }

    [Theory]
    [InlineData("UTF-8")]
    [InlineData("UTF-16LE")]
    [InlineData("UTF-16BE")]
    [InlineData("UTF-32LE")]
    [InlineData("UTF-32BE")]
    public void AFileWithOnlyAMark_IsAtItsEnd(string official)
    {
        File.WriteAllBytes(P("i.bin"), Bytes(official, "", mark: true));
        Assert.True(Engine().Query($@"
            open('{P("i.bin")}', read, S, [eof_action(eof_code)]),
            stream_property(S, encoding('{official}')),
            stream_property(S, end_of_stream(at)),
            at_end_of_stream(S),
            peek_code(S, -1),
            get_char(S, end_of_file),
            close(S).").Success);
    }

    [Theory]
    [InlineData("UTF-8")]
    [InlineData("UTF-16")]
    [InlineData("UTF-16LE")]
    [InlineData("UTF-16BE")]
    [InlineData("UTF-32")]
    [InlineData("UTF-32LE")]
    [InlineData("UTF-32BE")]
    public void ATerm_RoundTrips_ThroughWriteTermAndReadTerm(string official)
    {
        Assert.True(Engine().Query($@"
            atom_codes(A, {CodeList("aéあ\U0001D11E")}),
            open('{P("t.txt")}', write, W, [encoding('{official}')]),
            write_term(W, f(A, 'é', X, X), [quoted(true), variable_names(['X'=X])]),
            put_char(W, '.'), nl(W),
            close(W),
            open('{P("t.txt")}', read, R, [encoding('{official}')]),
            read_term(R, f(B, C, Y, Z), [variable_names(['X'=Y])]),
            read_term(R, E, []),
            close(R),
            B == A, C == 'é', Y == Z, E == end_of_file.").Success);
    }

    [Theory]
    [InlineData("UTF-8")]
    [InlineData("UTF-16LE")]
    [InlineData("UTF-32BE")]
    [InlineData("ISO-8859-1")]
    public void ALine_IsCountedInEveryEncoding(string official)
    {
        File.WriteAllBytes(P("i.bin"), Bytes(official, "ab\ncd\n"));
        Assert.True(Engine().Query($@"
            open('{P("i.bin")}', read, S, [encoding('{official}')]),
            line_count(S, 1),
            get_char(S, a), get_char(S, b), get_char(S, _),
            line_count(S, 2),
            close(S).").Success);
    }

    // ---------- bytes the encoding does not allow

    [Theory]
    [InlineData("UTF-8", new byte[] { 0x61, 0xE9, 0x62 })]
    [InlineData("US-ASCII", new byte[] { 0x61, 0xE9, 0x62 })]
    [InlineData("UTF-16LE", new byte[] { 0x61, 0, 0x00, 0xD8, 0x62, 0 })]
    [InlineData("UTF-16LE", new byte[] { 0x61, 0, 0x00, 0xDC, 0x62, 0 })]
    [InlineData("UTF-16BE", new byte[] { 0, 0x61, 0xD8, 0x00, 0, 0x62 })]
    [InlineData("UTF-32LE", new byte[] { 0x61, 0, 0, 0, 0, 0, 0x11, 0, 0x62, 0, 0, 0 })]
    [InlineData("UTF-32LE", new byte[] { 0x61, 0, 0, 0, 0, 0xD8, 0, 0, 0x62, 0, 0, 0 })]
    [InlineData("UTF-32BE", new byte[] { 0, 0, 0, 0x61, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0x62 })]
    public void AnIllFormedSequence_IsAnError_ThatAPeekLeavesAndAReadPasses(string official, byte[] bytes)
    {
        File.WriteAllBytes(P("i.bin"), bytes);
        Assert.True(Engine().Query($@"
            open('{P("i.bin")}', read, S, [encoding('{official}'), bom(false)]),
            get_char(S, a),
            is_representation_error(peek_code(S, _)),
            is_representation_error(peek_char(S, _)),
            is_representation_error(get_code(S, _)),
            get_char(S, b),
            close(S).").Success);
    }

    [Fact]
    public void TwoLowSurrogatesInARow_AreTwoErrors()
    {
        File.WriteAllBytes(P("i.bin"), new byte[] { 0x61, 0, 0x00, 0xDC, 0x00, 0xDC, 0x62, 0 });
        Assert.True(Engine().Query($@"
            open('{P("i.bin")}', read, S, [encoding('UTF-16LE'), bom(false)]),
            get_char(S, a),
            is_representation_error(get_code(S, _)),
            is_representation_error(get_code(S, _)),
            get_char(S, b),
            close(S).").Success);
    }

    [Theory]
    [InlineData("UTF-16LE", new byte[] { 0x61, 0, 0x62 })]
    [InlineData("UTF-16LE", new byte[] { 0x61, 0, 0x00, 0xD8 })]
    [InlineData("UTF-32LE", new byte[] { 0x61, 0, 0, 0, 0x62, 0 })]
    public void ACutSequenceAtTheEnd_IsAnError(string official, byte[] bytes)
    {
        File.WriteAllBytes(P("i.bin"), bytes);
        Assert.True(Engine().Query($@"
            open('{P("i.bin")}', read, S, [encoding('{official}'), bom(false), eof_action(eof_code)]),
            get_char(S, a),
            is_representation_error(get_code(S, _)),
            get_code(S, -1),
            close(S).").Success);
    }

    [Fact]
    public void Latin1_HasNoIllFormedByte()
    {
        File.WriteAllBytes(P("i.bin"), Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        Assert.True(Engine().Query($@"
            open('{P("i.bin")}', read, S, [type(text), encoding('ISO-8859-1'), bom(false)]),
            by_get_code(S, Cs),
            close(S),
            length(Cs, N), N >= 255,
            last(Cs, 255).").Success);
    }

    // ---------- characters the encoding cannot hold

    [Theory]
    [InlineData("put_char(S, Hi)")]
    [InlineData("put_code(S, 0x3042)")]
    [InlineData("put_code(S, 0x1D11E)")]
    [InlineData("write(S, Hi)")]
    [InlineData("writeq(S, Hi)")]
    [InlineData("print(S, Hi)")]
    [InlineData("write_canonical(S, Hi)")]
    [InlineData("write_term(S, Hi, [quoted(true)])")]
    [InlineData("format(S, '~w', [Hi])")]
    [InlineData("format(S, '~a', [Hi])")]
    [InlineData("format(S, '~c', [0x3042])")]
    [InlineData("write(S, Late)")]
    [InlineData("format(S, '~a', [Late])")]
    public void Latin1_RaisesAnError_ForAWiderCharacter_AndKeepsTheStream(string goal)
    {
        Assert.True(Engine().Query($@"
            char_code(Hi, 0x3042), atom_concat(x, Hi, Late),
            open('{P("o.bin")}', write, S, [encoding('ISO-8859-1')]),
            put_char(S, a),
            is_representation_error({goal}),
            put_code(S, 0xE9),
            close(S).").Success);
        Assert.Equal(new byte[] { 0x61, 0xE9 }, File.ReadAllBytes(P("o.bin")));
    }

    [Theory]
    [InlineData("put_char(S, E)")]
    [InlineData("put_code(S, 0xE9)")]
    [InlineData("write(S, E)")]
    [InlineData("format(S, '~a', [E])")]
    public void Ascii_RaisesAnError_ForAWiderCharacter_AndKeepsTheStream(string goal)
    {
        Assert.True(Engine().Query($@"
            char_code(E, 0xE9),
            open('{P("o.bin")}', write, S, [encoding('US-ASCII')]),
            put_char(S, a),
            is_representation_error({goal}),
            put_char(S, b),
            close(S).").Success);
        Assert.Equal(new byte[] { 0x61, 0x62 }, File.ReadAllBytes(P("o.bin")));
    }

    [Theory]
    [InlineData("ISO-8859-1", 0xFF)]
    [InlineData("US-ASCII", 0x7F)]
    public void TheLastCharacterOfAnEncoding_IsWritten(string official, int last)
    {
        Assert.True(Engine().Query($@"
            open('{P("o.bin")}', write, S, [encoding('{official}')]),
            put_code(S, {last}),
            is_representation_error(put_code(S, {last + 1})),
            close(S).").Success);
        Assert.Equal(new[] { (byte)last }, File.ReadAllBytes(P("o.bin")));
    }

    // ---------- when each stream gets a byte order mark

    [Theory]
    [InlineData("'UTF-8'", "[bom(true)]", new byte[] { 0xEF, 0xBB, 0xBF, 0x61 }, true)]
    [InlineData("'UTF-8'", "[bom(false)]", new byte[] { 0x61 }, false)]
    [InlineData("'UTF-8'", "[]", new byte[] { 0x61 }, false)]
    [InlineData("'UTF-16LE'", "[bom(true)]", new byte[] { 0xFF, 0xFE, 0x61, 0 }, true)]
    [InlineData("'UTF-16LE'", "[]", new byte[] { 0x61, 0 }, false)]
    [InlineData("'UTF-16BE'", "[bom(true)]", new byte[] { 0xFE, 0xFF, 0, 0x61 }, true)]
    [InlineData("'UTF-16BE'", "[]", new byte[] { 0, 0x61 }, false)]
    [InlineData("'UTF-32LE'", "[bom(true)]", new byte[] { 0xFF, 0xFE, 0, 0, 0x61, 0, 0, 0 }, true)]
    [InlineData("'UTF-32LE'", "[]", new byte[] { 0x61, 0, 0, 0 }, false)]
    [InlineData("'UTF-32BE'", "[bom(true)]", new byte[] { 0, 0, 0xFE, 0xFF, 0, 0, 0, 0x61 }, true)]
    [InlineData("'UTF-32BE'", "[]", new byte[] { 0, 0, 0, 0x61 }, false)]
    [InlineData("'UTF-16'", "[]", new byte[] { 0xFE, 0xFF, 0, 0x61 }, true)]
    [InlineData("'UTF-16'", "[bom(true)]", new byte[] { 0xFE, 0xFF, 0, 0x61 }, true)]
    [InlineData("'UTF-16'", "[bom(false)]", new byte[] { 0, 0x61 }, false)]
    [InlineData("'UTF-32'", "[]", new byte[] { 0, 0, 0xFE, 0xFF, 0, 0, 0, 0x61 }, true)]
    [InlineData("'UTF-32'", "[bom(false)]", new byte[] { 0, 0, 0, 0x61 }, false)]
    [InlineData("'ISO-8859-1'", "[bom(true)]", new byte[] { 0x61 }, false)]
    [InlineData("'US-ASCII'", "[bom(true)]", new byte[] { 0x61 }, false)]
    public void Writing_PutsAMark_ByTheEncodingAndTheOption(
        string encoding, string options, byte[] bytes, bool mark)
    {
        Assert.True(Engine().Query($@"
            append([encoding({encoding})], {options}, Options),
            open('{P("o.bin")}', write, S, Options),
            stream_property(S, bom({(mark ? "true" : "false")})),
            put_char(S, a),
            close(S).").Success);
        Assert.Equal(bytes, File.ReadAllBytes(P("o.bin")));
    }

    [Theory]
    [InlineData("'UTF-8'", "[bom(true)]", new byte[] { 0x62 })]
    [InlineData("'UTF-16'", "[]", new byte[] { 0, 0x62 })]
    [InlineData("'UTF-16'", "[bom(true)]", new byte[] { 0, 0x62 })]
    [InlineData("'UTF-16LE'", "[bom(true)]", new byte[] { 0x62, 0 })]
    [InlineData("'UTF-32'", "[]", new byte[] { 0, 0, 0, 0x62 })]
    public void Appending_NeverPutsAMark(string encoding, string options, byte[] added)
    {
        byte[] before = { 0x7E };
        File.WriteAllBytes(P("o.bin"), before);
        Assert.True(Engine().Query($@"
            append([encoding({encoding})], {options}, Options),
            open('{P("o.bin")}', append, S, Options),
            stream_property(S, bom(false)),
            put_char(S, b),
            close(S).").Success);
        Assert.Equal(before.Concat(added).ToArray(), File.ReadAllBytes(P("o.bin")));
    }

    [Theory]
    [InlineData("UTF-8", new byte[] { 0xEF, 0xBB, 0xBF, 0x61 })]
    [InlineData("UTF-16BE", new byte[] { 0xFE, 0xFF, 0, 0x61 })]
    [InlineData("UTF-16LE", new byte[] { 0xFF, 0xFE, 0x61, 0 })]
    [InlineData("UTF-32BE", new byte[] { 0, 0, 0xFE, 0xFF, 0, 0, 0, 0x61 })]
    [InlineData("UTF-32LE", new byte[] { 0xFF, 0xFE, 0, 0, 0x61, 0, 0, 0 })]
    public void BomFalse_ReadsTheMarkAsACharacter(string official, byte[] bytes)
    {
        File.WriteAllBytes(P("i.bin"), bytes);
        Assert.True(Engine().Query($@"
            open('{P("i.bin")}', read, S, [encoding('{official}'), bom(false)]),
            stream_property(S, bom(false)),
            get_code(S, 0xFEFF),
            get_code(S, 97),
            close(S).").Success);
    }

    [Fact]
    public void BomFalse_ReadsTheMarkAsACharacter_AfterAReposition()
    {
        File.WriteAllBytes(P("i.bin"), new byte[] { 0xEF, 0xBB, 0xBF, 0x61 });
        Assert.True(Engine().Query($@"
            open('{P("i.bin")}', read, S, [encoding('UTF-8'), bom(false), reposition(true)]),
            stream_property(S, position(P)),
            get_code(S, 0xFEFF),
            get_code(S, 97),
            set_stream_position(S, P),
            get_code(S, 0xFEFF),
            close(S).").Success);
    }

    [Fact]
    public void BomFalse_WithANarrowEncoding_ReadsTheBytes()
    {
        File.WriteAllBytes(P("i.bin"), new byte[] { 0xEF, 0xBB, 0xBF, 0x61 });
        Assert.True(Engine().Query($@"
            open('{P("i.bin")}', read, S, [encoding('ISO-8859-1'), bom(false)]),
            stream_property(S, encoding('ISO-8859-1')),
            by_get_code(S, [0xEF, 0xBB, 0xBF, 0x61]),
            close(S).").Success);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("append")]
    public void ABinaryStream_IgnoresEncodingAndBom(string mode)
    {
        Assert.True(Engine().Query($@"
            open('{P("o.bin")}', {mode}, S, [type(binary), encoding('UTF-16'), bom(true)]),
            \+ stream_property(S, encoding(_)),
            \+ stream_property(S, bom(_)),
            put_byte(S, 0x61),
            close(S).").Success);
        Assert.Equal(new byte[] { 0x61 }, File.ReadAllBytes(P("o.bin")));
    }

    [Fact]
    public void ABinaryStream_ReadsAMarkAsBytes()
    {
        File.WriteAllBytes(P("i.bin"), new byte[] { 0xEF, 0xBB, 0xBF });
        Assert.True(Engine().Query($@"
            open('{P("i.bin")}', read, S, [type(binary), encoding('UTF-8'), bom(true)]),
            get_byte(S, 0xEF),
            close(S).").Success);
    }

    // ---------- the default encoding

    [Theory]
    [InlineData("'UTF-8'", "UTF-8", new byte[] { 0x61 }, false)]
    [InlineData("'UTF-16'", "UTF-16", new byte[] { 0xFE, 0xFF, 0, 0x61 }, true)]
    [InlineData("'UCS-2'", "UTF-16", new byte[] { 0xFE, 0xFF, 0, 0x61 }, true)]
    [InlineData("'UTF-32'", "UTF-32", new byte[] { 0, 0, 0xFE, 0xFF, 0, 0, 0, 0x61 }, true)]
    [InlineData("'UTF-16LE'", "UTF-16LE", new byte[] { 0x61, 0 }, false)]
    [InlineData("'UTF-16BE'", "UTF-16BE", new byte[] { 0, 0x61 }, false)]
    [InlineData("'UTF-32LE'", "UTF-32LE", new byte[] { 0x61, 0, 0, 0 }, false)]
    [InlineData("'UTF-32BE'", "UTF-32BE", new byte[] { 0, 0, 0, 0x61 }, false)]
    [InlineData("iso_latin_1", "ISO-8859-1", new byte[] { 0x61 }, false)]
    public void TheDefaultEncoding_RulesOpen3AndOpen4(string flag, string official, byte[] bytes, bool mark)
    {
        var e = Engine();
        Assert.True(e.Query($"set_prolog_flag(encoding, {flag}).").Success);
        Assert.Equal(official, ((Shumway.Compiler.Ast.AtomTerm)
            e.Query("current_prolog_flag(encoding, F).")["F"]!).Name);
        string b = mark ? "true" : "false";
        foreach (string open in new[]
        {
            $"open('{P("o.bin")}', write, S)",
            $"open('{P("o.bin")}', write, S, [])",
            $"open('{P("o.bin")}', write, S, [alias(out)])",
        })
        {
            File.Delete(P("o.bin"));
            Assert.True(e.Query($@"
                {open},
                stream_property(S, encoding('{official}')),
                stream_property(S, bom({b})),
                put_char(S, a),
                close(S).").Success, open);
            Assert.Equal(bytes, File.ReadAllBytes(P("o.bin")));
        }
    }

    [Theory]
    [InlineData("'UTF-16'", new byte[] { 0, 0x62 })]
    [InlineData("'UTF-32'", new byte[] { 0, 0, 0, 0x62 })]
    [InlineData("'UTF-16LE'", new byte[] { 0x62, 0 })]
    public void TheDefaultEncoding_RulesAppending_WithNoMark(string flag, byte[] added)
    {
        foreach (string open in new[]
        {
            $"open('{P("o.bin")}', append, S)",
            $"open('{P("o.bin")}', append, S, [])",
        })
        {
            byte[] before = { 0x7E };
            File.WriteAllBytes(P("o.bin"), before);
            var e = Engine();
            Assert.True(e.Query($"set_prolog_flag(encoding, {flag}).").Success);
            Assert.True(e.Query($@"
                {open}, stream_property(S, bom(false)), put_char(S, b), close(S).").Success);
            Assert.Equal(before.Concat(added).ToArray(), File.ReadAllBytes(P("o.bin")));
        }
    }

    [Theory]
    [InlineData("'UTF-16'", new byte[] { 0, 0x61 }, "UTF-16", false)]
    [InlineData("'UTF-16'", new byte[] { 0xFE, 0xFF, 0, 0x61 }, "UTF-16BE", true)]
    [InlineData("'UTF-16'", new byte[] { 0xFF, 0xFE, 0x61, 0 }, "UTF-16LE", true)]
    [InlineData("'UTF-32'", new byte[] { 0, 0, 0, 0x61 }, "UTF-32", false)]
    [InlineData("'UTF-32'", new byte[] { 0xFF, 0xFE, 0, 0, 0x61, 0, 0, 0 }, "UTF-32LE", true)]
    [InlineData("'UTF-16LE'", new byte[] { 0x61, 0 }, "UTF-16LE", false)]
    [InlineData("'UTF-16LE'", new byte[] { 0xEF, 0xBB, 0xBF, 0x61 }, "UTF-8", true)]
    public void TheDefaultEncoding_RulesReading_WhenThereIsNoMark(string flag, byte[] bytes, string official, bool mark)
    {
        File.WriteAllBytes(P("i.bin"), bytes);
        var e = Engine();
        Assert.True(e.Query($"set_prolog_flag(encoding, {flag}).").Success);
        string b = mark ? "true" : "false";
        foreach (string open in new[]
        {
            $"open('{P("i.bin")}', read, S)",
            $"open('{P("i.bin")}', read, S, [])",
        })
        {
            Assert.True(e.Query($@"
                {open},
                stream_property(S, encoding(E)), E == '{official}',
                stream_property(S, bom({b})),
                get_char(S, a),
                close(S).").Success, open);
        }
    }

    [Fact]
    public void TheEncodingOption_RulesOverTheDefaultEncoding()
    {
        var e = Engine();
        Assert.True(e.Query("set_prolog_flag(encoding, 'UTF-16').").Success);
        Assert.True(e.Query($@"
            open('{P("o.bin")}', write, S, [encoding('UTF-8')]),
            stream_property(S, bom(false)),
            put_char(S, a),
            close(S).").Success);
        Assert.Equal(new byte[] { 0x61 }, File.ReadAllBytes(P("o.bin")));
    }

    [Theory]
    [InlineData("'UTF-16'", new byte[] { 0xFE, 0xFF, 0, 0x61 })]
    [InlineData("'UTF-16LE'", new byte[] { 0x61, 0 })]
    public void Tell_FollowsTheDefaultEncoding(string flag, byte[] bytes)
    {
        var e = Engine();
        Assert.True(e.Query($"set_prolog_flag(encoding, {flag}).").Success);
        Assert.True(e.Query($"tell('{P("o.bin")}'), put_char(a), told.").Success);
        Assert.Equal(bytes, File.ReadAllBytes(P("o.bin")));
    }

    [Theory]
    [InlineData("'UTF-16'", new byte[] { 0, 0x61 })]
    [InlineData("'UTF-16'", new byte[] { 0xFF, 0xFE, 0x61, 0 })]
    [InlineData("'UTF-16LE'", new byte[] { 0x61, 0 })]
    [InlineData("iso_latin_1", new byte[] { 0x61 })]
    public void See_FollowsTheDefaultEncoding(string flag, byte[] bytes)
    {
        File.WriteAllBytes(P("i.bin"), bytes);
        var e = Engine();
        Assert.True(e.Query($"set_prolog_flag(encoding, {flag}).").Success);
        Assert.True(e.Query($"see('{P("i.bin")}'), get_char(a), seen.").Success);
    }

    [Fact]
    public void AStreamFromSeeOrTell_ReportsItsEncoding()
    {
        File.WriteAllBytes(P("i.bin"), new byte[] { 0x61 });
        var e = Engine();
        Assert.True(e.Query($@"
            see('{P("i.bin")}'), current_input(I),
            stream_property(I, encoding('UTF-8')), seen,
            tell('{P("o.bin")}'), current_output(O),
            stream_property(O, encoding('UTF-8')), told.").Success);
    }

    [Theory]
    [InlineData("'UTF-16LE'", "UTF-16LE")]
    [InlineData("'UTF-16'", "UTF-16")]
    [InlineData("'UTF-32'", "UTF-32")]
    public void ASource_IsReadInTheDefaultEncoding(string flag, string official)
    {
        File.WriteAllBytes(P("src.pl"), Bytes(official, "fact('é\U0001D11E').\n"));
        var e = Engine();
        Assert.True(e.Query($"set_prolog_flag(encoding, {flag}).").Success);
        e.ConsultFile(P("src.pl"));
        Assert.True(e.Query("fact(A), atom_codes(A, [0xE9, 0x1D11E]).").Success);
    }

    [Theory]
    [InlineData("'UTF-16'", "UTF-16", true)]
    [InlineData("'UTF-16LE'", "UTF-16LE", false)]
    [InlineData("'UTF-8'", "UTF-8", false)]
    public void FileList_WritesInTheDefaultEncoding_SoAConsultReadsItBack(string flag, string official, bool mark)
    {
        string file = P("dump.pl");
        var e = Engine();
        Assert.True(e.Query($"set_prolog_flag(encoding, {flag}).").Success);
        e.ConsultString(":- dynamic(fact/1).");
        Assert.True(e.Query("atom_codes(A, [0xE9, 0x1D11E]), assertz(fact(A)).").Success);
        Assert.True(e.Query($"file_list('{file}', [fact/1]).").Success);

        byte[] bytes = File.ReadAllBytes(file);
        byte[] start = Bytes(official, "", mark);
        Assert.Equal(start, bytes.Take(start.Length).ToArray());
        string text = NoMark(official).GetString(bytes);
        Assert.Contains("fact(", text);
        Assert.Contains("\U0001D11E", text);

        var again = Engine();
        Assert.True(again.Query($"set_prolog_flag(encoding, {flag}).").Success);
        again.ConsultFile(file);
        Assert.True(again.Query("fact(A), atom_codes(A, [0xE9, 0x1D11E]).").Success);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("append")]
    [InlineData("read")]
    public void TheNullDevice_ReportsAnEncoding(string mode)
    {
        string device = OperatingSystem.IsWindows() ? "nul" : "/dev/null";
        Assert.True(Engine().Query($@"
            open('{device}', {mode}, S, [encoding('UTF-16LE')]),
            stream_property(S, encoding('UTF-16LE')),
            stream_property(S, bom(false)),
            close(S).").Success);
    }

    [Theory]
    [InlineData("UTF-8", false)]
    [InlineData("UTF-16LE", true)]
    [InlineData("UTF-16BE", true)]
    [InlineData("UTF-32LE", true)]
    [InlineData("UTF-32BE", true)]
    public void PhraseFromFile_ReadsAstralText_InTheEncodingOfTheMark(string official, bool mark)
    {
        string text = string.Concat(Enumerable.Repeat("a\U0001D11E", 3000));
        File.WriteAllBytes(P("i.bin"), Bytes(official, text, mark));
        var e = Engine();
        e.ConsultString(@"
pairs(_, 0) --> [].
pairs(C, N) --> [a, C], pairs(C, N0), { N is N0 + 1 }.
");
        Assert.True(e.Query($@"
            char_code(C, 0x1D11E),
            phrase_from_file(pairs(C, N), '{P("i.bin")}'),
            N == 3000.").Success);
    }
}
