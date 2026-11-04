using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>set_stream/2 with alias(A): an alias names one stream at a time
/// and a stream may have several; user_input, user_output and user_error move
/// to a stream of their direction and come back to their terminal streams
/// when it closes; current_input and current_output set the current streams.
/// The terminal user_output writes to the engine's Out, which each test
/// captures; user_error is only ever written while it is redirected.</summary>
public sealed class SetStreamTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "shumway_setstream_" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _console = new();
    private readonly PrologEngine _engine;

    public SetStreamTests()
    {
        Directory.CreateDirectory(_dir);
        _engine = new PrologEngine { Out = _console };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string P(string name) => Path.Combine(_dir, name).Replace('\\', '/');

    private string Text(string name) =>
        File.ReadAllText(Path.Combine(_dir, name)).Replace("\r\n", "\n");

    private void True(string goal) =>
        Assert.True(_engine.Query(goal).Success, goal + "\nconsole: " + _console);

    [Fact]
    public void AnAliasNamesOneStream_AndAStreamCanHaveSeveral()
    {
        True($@"
            open('{P("s.txt")}', write, S), open('{P("s2.txt")}', write, S2),
            set_stream(S, alias(foo)), set_stream(S, alias(bar)),
            write(foo, a), write(bar, b),
            findall(A, stream_property(S, alias(A)), [foo, bar]),
            set_stream(S2, alias(bar)),
            findall(A, stream_property(S, alias(A)), [foo]),
            stream_property(S2, alias(bar)),
            write(bar, c),
            close(S), close(S2).");
        Assert.Equal("ab", Text("s.txt"));
        Assert.Equal("c", Text("s2.txt"));
    }

    // The terminal stream loses the alias while it is moved, and gets it back
    // when the stream that took it is closed. The current output stays put.
    [Fact]
    public void UserOutputMovesToTheStream_UntilItIsClosed()
    {
        True($@"
            stream_property(T, alias(user_output)),
            open('{P("u.txt")}', write, S),
            set_stream(S, alias(user_output)),
            \+ stream_property(T, alias(user_output)),
            stream_property(S, alias(user_output)),
            write(user_output, to_file), nl(user_output),
            write(to_console), nl,
            close(S),
            stream_property(T, alias(user_output)),
            write(user_output, back), nl(user_output).");
        Assert.Equal("to_file\n", Text("u.txt"));
        Assert.Contains("to_console", _console.ToString());
        Assert.Contains("back", _console.ToString());
        Assert.DoesNotContain("to_file", _console.ToString());
    }

    // What the Jupyter kernel for Logtalk does for a backend with set_stream/2.
    [Fact]
    public void TheKernelsRedirection_SendsAllOutputToTheStream_AndCloseRestoresIt()
    {
        True($@"
            current_output(C0),
            stream_property(U0, alias(user_output)),
            stream_property(E0, alias(user_error)),
            open('{P("k.txt")}', write, S),
            set_stream(S, alias(current_output)),
            set_stream(S, alias(user_output)),
            set_stream(S, alias(user_error)),
            write(via_write), nl,
            write(user_output, via_user_output), nl(user_output),
            write(user_error, via_user_error), nl(user_error),
            print_message(error, format(""a message"", [])),
            close(S),
            current_output(C1), C1 == C0,
            stream_property(U1, alias(user_output)), U1 == U0,
            stream_property(E1, alias(user_error)), E1 == E0.");
        Assert.Equal("via_write\nvia_user_output\nvia_user_error\nerror: a message\n",
            Text("k.txt"));
        Assert.Equal("", _console.ToString());
    }

    // No stack of redirections: closing the second goes back to the terminal.
    [Fact]
    public void ClosingGoesBackToTheTerminal_NotToAnEarlierRedirection()
    {
        True($@"
            open('{P("n1.txt")}', write, S1), set_stream(S1, alias(user_output)),
            open('{P("n2.txt")}', write, S2), set_stream(S2, alias(user_output)),
            write(user_output, second), close(S2),
            write(user_output, terminal), close(S1).");
        Assert.Equal("", Text("n1.txt"));
        Assert.Equal("second", Text("n2.txt"));
        Assert.Contains("terminal", _console.ToString());
    }

    [Fact]
    public void GivingTheAliasBackToTheTerminal_RestoresWithoutClosing()
    {
        True($@"
            stream_property(T, alias(user_output)),
            open('{P("r.txt")}', write, S), set_stream(S, alias(user_output)),
            write(user_output, redirected),
            set_stream(T, alias(user_output)),
            write(user_output, terminal),
            write(S, still_open), close(S).");
        Assert.Equal("redirectedstill_open", Text("r.txt"));
        Assert.Contains("terminal", _console.ToString());
    }

    // close(user_output) closes the stream the alias names; the terminal
    // stream itself is never closed.
    [Fact]
    public void ClosingUserOutput_ClosesTheStreamItNames()
    {
        True($@"
            stream_property(T, alias(user_output)),
            open('{P("c.txt")}', write, S), set_stream(S, alias(user_output)),
            close(user_output),
            \+ is_stream(S),
            stream_property(U, alias(user_output)), U == T,
            close(user_output),
            write(user_output, still_there).");
        Assert.Contains("still_there", _console.ToString());
    }

    // A closed current output moves to the stream user_output names then.
    [Fact]
    public void ClosingTheCurrentOutput_MovesItToTheStreamUserOutputNames()
    {
        True($@"
            open('{P("h.txt")}', write, H), set_stream(H, alias(user_output)),
            open('{P("o.txt")}', write, O), set_output(O),
            close(O),
            current_output(C), C == H,
            write(after), close(H).");
        Assert.Equal("after", Text("h.txt"));
    }

    [Fact]
    public void CurrentOutputAndCurrentInput_SetTheCurrentStreams_AndAreNotAliases()
    {
        File.WriteAllText(Path.Combine(_dir, "in.txt"), "term(one).\n");
        True($@"
            open('{P("co.txt")}', write, S),
            set_stream(S, alias(current_output)),
            current_output(S), \+ stream_property(S, alias(_)),
            write(to_current), close(S),
            open('{P("in.txt")}', read, R),
            set_stream(R, alias(current_input)),
            current_input(R), read(T), T == term(one), close(R),
            write(after_close).");
        Assert.Equal("to_current", Text("co.txt"));
        Assert.Contains("after_close", _console.ToString());
    }

    [Fact]
    public void UserInputMovesToAReadStream_UntilItIsClosed()
    {
        File.WriteAllText(Path.Combine(_dir, "ui.txt"), "hello(world).\n");
        True($@"
            stream_property(T, alias(user_input)),
            open('{P("ui.txt")}', read, R),
            set_stream(R, alias(user_input)),
            read(user_input, X), X == hello(world),
            close(R),
            stream_property(U, alias(user_input)), U == T.");
    }

    [Theory]
    [InlineData("set_stream(_, alias(a))", "instantiation_error")]
    [InlineData("set_stream(user_output, _)", "instantiation_error")]
    [InlineData("set_stream(user_output, alias(_))", "instantiation_error")]
    [InlineData("set_stream(user_output, alias(1))", "type_error(atom, 1)")]
    [InlineData("set_stream(user_output, foo)", "domain_error(stream_property, foo)")]
    [InlineData("set_stream(user_output, eof_action(eof_code))",
        "domain_error(stream_property, eof_action(eof_code))")]
    [InlineData("set_stream(nosuchstream, alias(a))", "existence_error(stream, nosuchstream)")]
    [InlineData("set_stream(42, alias(a))", "domain_error(stream_or_alias, 42)")]
    [InlineData("set_stream(user_input, alias(user_output))", "permission_error(output, stream, user_input)")]
    [InlineData("set_stream(user_input, alias(user_error))", "permission_error(output, stream, user_input)")]
    [InlineData("set_stream(user_input, alias(current_output))", "permission_error(output, stream, user_input)")]
    [InlineData("set_stream(user_output, alias(user_input))", "permission_error(input, stream, user_output)")]
    [InlineData("set_stream(user_output, alias(current_input))", "permission_error(input, stream, user_output)")]
    public void Errors(string goal, string error)
    {
        True($"catch({goal}, error(E, _), true), nonvar(E), E = {error}.");
        // A refused call changes nothing: each standard alias still names the
        // terminal stream it started on.
        True("stream_property(O, alias(user_output)), current_output(O), "
             + "stream_property(I, alias(user_input)), current_input(I), "
             + "stream_property(E, alias(user_error)), E \\== O, E \\== I.");
    }

    [Fact]
    public void AClosedStreamIsAnExistenceError()
    {
        True($@"
            open('{P("x.txt")}', write, S), close(S),
            catch(set_stream(S, alias(a)), error(existence_error(stream, S1), _), true),
            S1 == S.");
    }

    [Fact]
    public void SetStreamIsBuiltIn() =>
        True("predicate_property(set_stream(_, _), built_in).");
}
