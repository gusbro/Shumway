using System;
using System.Collections.Generic;
using System.IO;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>get_unbuffered_char/1 and get_unbuffered_code/1: a key on a
/// terminal, one character of the current input anywhere else. A real console
/// cannot be driven from a test: the host's keyboard stands in for it, and the
/// translation of console keys is checked on its own.</summary>
public sealed class UnbufferedInputTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "shumway_unbuffered_" + Guid.NewGuid().ToString("N"));

    public UnbufferedInputTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string File_(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path.Replace('\\', '/');
    }

    private sealed class Keyboard : StringReader, IKeyReader
    {
        private readonly Queue<int> _keys;
        public Action? BeforeWaiting;

        public Keyboard(string text, params int[] keys) : base(text) =>
            _keys = new Queue<int>(keys);

        public bool TryReadKey(out int codePoint)
        {
            BeforeWaiting?.Invoke();
            codePoint = -1;
            if (_keys.Count == 0) return false;
            codePoint = _keys.Dequeue();
            return true;
        }
    }

    private sealed class FlushRecorder : StringWriter
    {
        public string Flushed = "";
        public override void Flush() => Flushed = ToString();
    }

    private static PrologEngine Engine(TextReader input) =>
        new() { In = input, Out = new StringWriter() };

    private static void True(PrologEngine engine, string goal) =>
        Assert.True(engine.Query(goal).Success, goal);

    [Fact]
    public void TheCharForm_ReadsTheCharactersOfTheInput()
    {
        True(Engine(new StringReader("ab\n")), @"
            get_unbuffered_char(A), get_unbuffered_char(B),
            get_unbuffered_char(C), get_unbuffered_char(D),
            A == a, B == b, C == '\n', D == end_of_file.");
    }

    [Fact]
    public void TheCodeForm_ReadsTheCodesOfTheInput()
    {
        True(Engine(new StringReader("ab\n")), @"
            get_unbuffered_code(A), get_unbuffered_code(B),
            get_unbuffered_code(C), get_unbuffered_code(D),
            A == 97, B == 98, C == 10, D == -1.");
    }

    [Theory]
    [InlineData(@"get_unbuffered_char(C), char_code(C, 0x1F600).")]
    [InlineData(@"get_unbuffered_code(C), C =:= 0x1F600.")]
    public void ACharacterOutsideTheBmp_IsOneCodePoint(string goal)
    {
        True(Engine(new StringReader("\U0001F600")), goal);
    }

    [Fact]
    public void TheyReadTheCurrentInput_NotUserInput()
    {
        string file = File_("in.txt", "q");
        True(Engine(new StringReader("z")), $@"
            open('{file}', read, S), set_input(S),
            get_unbuffered_char(C),
            set_input(user_input), close(S),
            C == q.");
    }

    [Theory]
    [InlineData("error", "raised")]
    [InlineData("eof_code", "-1")]
    public void ASecondReadAtTheEnd_FollowsTheEofAction(string action, string expected)
    {
        string file = File_("empty.txt", "");
        True(Engine(new StringReader("")), $@"
            open('{file}', read, S, [eof_action({action})]), set_input(S),
            get_unbuffered_code(First),
            catch(get_unbuffered_code(Second),
                  error(permission_error(input, past_end_of_stream, _), _),
                  Second = raised),
            set_input(user_input), close(S),
            First == -1, Second == {expected}.");
    }

    [Theory]
    [InlineData(@"get_unbuffered_char(1)", @"type_error(in_character, 1)")]
    [InlineData(@"get_unbuffered_code(a)", @"type_error(integer, a)")]
    [InlineData(@"get_unbuffered_code(-2)", @"representation_error(in_character_code)")]
    public void AnArgumentNoReadCouldGive_IsAnError(string goal, string error)
    {
        True(Engine(new StringReader("x")),
            $@"catch({goal}, error(E, _), true), E = {error}.");
    }

    [Theory]
    [InlineData("get_unbuffered_char(_)")]
    [InlineData("get_unbuffered_code(_)")]
    public void ABinaryInput_IsAPermissionError(string goal)
    {
        string file = File_("bin.dat", "x");
        True(Engine(new StringReader("")), $@"
            open('{file}', read, S, [type(binary)]), set_input(S),
            catch({goal}, error(E, _), true),
            set_input(user_input), close(S),
            E = permission_error(input, binary_stream, _).");
    }

    [Theory]
    [InlineData("get_unbuffered_char(_)")]
    [InlineData("get_unbuffered_code(_)")]
    public void TheyAreBuiltIns_WhichIsWhatPortableCodeAsks(string head)
    {
        True(Engine(new StringReader("")), $"predicate_property({head}, built_in).");
    }

    [Fact]
    public void AKeyOfTheHost_ComesInsteadOfItsText()
    {
        True(Engine(new Keyboard("text", 'x', 'y')), @"
            get_unbuffered_char(A), get_unbuffered_code(B), get_char(C),
            A == x, B == 121, C == t.");
    }

    [Fact]
    public void WithNoKeyToWaitFor_TheTextIsRead()
    {
        True(Engine(new Keyboard("t")), "get_unbuffered_char(C), C == t.");
    }

    [Fact]
    public void TheEndOfInputAtTheTerminal_DoesNotEndTheTerminal()
    {
        True(Engine(new Keyboard("", -1, 'x')), @"
            get_unbuffered_char(A), get_unbuffered_char(B),
            A == end_of_file, B == x.");
    }

    [Fact]
    public void WhatAPeekPushedBack_ComesBeforeAnyKey()
    {
        // A peek at a character outside the BMP holds both of its halves.
        True(Engine(new Keyboard("\U0001F600", 'k')), @"
            peek_code(_), get_unbuffered_code(A), get_unbuffered_code(B),
            A =:= 0x1F600, B == 0'k.");
    }

    [Fact]
    public void AKey_CountsInTheLineOfTheStream()
    {
        True(Engine(new Keyboard("", '\n')), @"
            get_unbuffered_code(_), line_count(user_input, Line), Line == 2.");
    }

    [Theory]
    [InlineData("get_unbuffered_char(_)")]
    [InlineData("get_unbuffered_code(_)")]
    public void WhatWasWrittenBefore_IsFlushedBeforeTheWait(string goal)
    {
        var output = new FlushRecorder();
        string seen = "";
        var keyboard = new Keyboard("", 'k');
        keyboard.BeforeWaiting = () => seen = output.Flushed;
        var engine = new PrologEngine { In = keyboard, Out = output };
        True(engine, $"write(prompt), {goal}.");
        Assert.Equal("prompt", seen);
    }

    private static ConsoleKeyInfo Key(char c, ConsoleKey key = ConsoleKey.A) =>
        new(c, key, shift: false, alt: false, control: false);

    private static int Translate(params ConsoleKeyInfo[] keys)
    {
        int next = 0;
        return TerminalKeys.Next(() => keys[next++]);
    }

    [Fact]
    public void Enter_IsANewline() =>
        Assert.Equal('\n', Translate(Key('\r', ConsoleKey.Enter)));

    [Fact]
    public void AKeyWithNoCharacter_IsSkipped() =>
        Assert.Equal('a', Translate(Key('\0', ConsoleKey.UpArrow), Key('a')));

    [Theory]
    [InlineData('\u0004')]
    [InlineData('\u001A')]
    public void CtrlDAndCtrlZ_AreTheEndOfInput(char c) =>
        Assert.Equal(-1, Translate(Key(c)));

    [Theory]
    [InlineData(ConsoleKey.D)]
    [InlineData(ConsoleKey.Z)]
    public void CtrlDAndCtrlZ_AreTheEndOfInput_WhenTheConsoleGivesNoCharacter(ConsoleKey key) =>
        Assert.Equal(-1, Translate(new ConsoleKeyInfo('\0', key, shift: false, alt: false, control: true)));

    [Fact]
    public void Escape_BelongsToTheGoal() =>
        Assert.Equal(27, Translate(Key('\u001B', ConsoleKey.Escape)));

    [Fact]
    public void TwoSurrogateKeys_AreOneCodePoint() =>
        Assert.Equal(0x1F600, Translate(Key('\uD83D'), Key('\uDE00')));

    [Fact]
    public void AnOrdinaryKey_IsItsCharacter() =>
        Assert.Equal('z', Translate(Key('z')));
}
