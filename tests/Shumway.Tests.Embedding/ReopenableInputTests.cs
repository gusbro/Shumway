using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// <see cref="IReopenableInput"/>: <c>user_input</c> has <c>eof_action(reset)</c>,
/// so an end of input a person gave ends one read, and the program reading
/// past it is asked again, as at a terminal. The reader here is that terminal:
/// lines in turn, null for a Ctrl-D, which lasts until the stream reopens it.
/// </summary>
public sealed class ReopenableInputTests
{
    private sealed class Terminal : TextReader, IReopenableInput
    {
        private readonly Queue<string?> _lines;
        private string _line = "";
        private int _at;
        private bool _ended;
        public int Reopened;

        public Terminal(params string?[] lines) => _lines = new(lines);

        public override int Peek() => More() ? _line[_at] : -1;
        public override int Read() => More() ? _line[_at++] : -1;

        private bool More()
        {
            while (_at >= _line.Length)
            {
                if (_ended || _lines.Count == 0) return false;
                string? next = _lines.Dequeue();
                if (next is null) { _ended = true; return false; }
                _line = next + "\n";
                _at = 0;
            }
            return true;
        }

        public void Reopen() { Reopened++; _ended = false; }
    }

    private static string Text(Solution s, string name, PrologEngine e)
        => AstTermRenderer.Render(s[name]!, 1200, e.Operators);

    [Fact]
    public void AReadAfterAnEndOfFile_ReadsWhatIsTypedNext()
    {
        var input = new Terminal(null, "bob.");
        var e = new PrologEngine { In = input, Out = new StringWriter() };
        var s = e.Query(@"read(A), read(B).");
        Assert.True(s.Success);
        Assert.Equal("end_of_file", Text(s, "A", e));
        Assert.Equal("bob", Text(s, "B", e));
        Assert.Equal(1, input.Reopened);
    }

    [Fact]
    public void AGetCharAfterAnEndOfFile_ReadsWhatIsTypedNext()
    {
        var input = new Terminal(null, "x");
        var e = new PrologEngine { In = input, Out = new StringWriter() };
        var s = e.Query(@"get_char(A), get_char(B).");
        Assert.True(s.Success);
        Assert.Equal("end_of_file", Text(s, "A", e));
        Assert.Equal("x", Text(s, "B", e));
    }

    // A peek does not read past the end: the read after it gets the same end.
    [Fact]
    public void APeekAtTheEnd_LeavesTheEndForTheReadAfterIt()
    {
        var input = new Terminal(null, "x");
        var e = new PrologEngine { In = input, Out = new StringWriter() };
        var s = e.Query(@"peek_char(P), get_char(A), get_char(B).");
        Assert.True(s.Success);
        Assert.Equal("end_of_file", Text(s, "P", e));
        Assert.Equal("end_of_file", Text(s, "A", e));
        Assert.Equal("x", Text(s, "B", e));
    }
}
