using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>listing/1, portray_clause/1, statistics/0 and time/1 write to the
/// current output stream: set_output/1 and set_stream/2's
/// alias(current_output) send them to a file, and nothing reaches the
/// terminal stream, which writes to the engine's Out.</summary>
public sealed class OutputToCurrentStreamTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "shumway_curout_" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _console = new();
    private readonly PrologEngine _engine;

    public OutputToCurrentStreamTests()
    {
        Directory.CreateDirectory(_dir);
        _engine = new PrologEngine { Out = _console };
        _engine.ConsultString(@"
:- dynamic(fact/1).
fact(1).
fact(2).
");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string P(string name) => Path.Combine(_dir, name).Replace('\\', '/');

    private string Text(string name) =>
        File.ReadAllText(Path.Combine(_dir, name)).Replace("\r\n", "\n");

    [Theory]
    [InlineData("listing(fact/1)", "fact(1).")]
    [InlineData("portray_clause(foo(X, bar))", "foo(A, bar).")]
    [InlineData("statistics", "Heap:")]
    [InlineData("time(true)", "seconds")]
    public void SetOutput_SendsTheReportToTheStream(string goal, string expected)
    {
        Assert.True(_engine.Query($@"
            open('{P("out.txt")}', write, S), set_output(S),
            {goal},
            set_output(user_output), close(S).").Success);
        Assert.Contains(expected, Text("out.txt"));
        Assert.DoesNotContain(expected, _console.ToString());
    }

    [Theory]
    [InlineData("listing(fact/1)", "fact(2).")]
    [InlineData("portray_clause(foo(X, bar))", "foo(A, bar).")]
    [InlineData("statistics", "Heap:")]
    [InlineData("time(true)", "seconds")]
    public void ARedirectionWithSetStream_TakesTheReportToo(string goal, string expected)
    {
        Assert.True(_engine.Query($@"
            open('{P("red.txt")}', write, S),
            set_stream(S, alias(current_output)),
            set_stream(S, alias(user_output)),
            set_stream(S, alias(user_error)),
            {goal},
            close(S).").Success);
        Assert.Contains(expected, Text("red.txt"));
        Assert.DoesNotContain(expected, _console.ToString());
    }

    // Anti-vacuity: without a redirection the report reaches the terminal.
    [Theory]
    [InlineData("listing(fact/1)", "fact(1).")]
    [InlineData("time(true)", "seconds")]
    public void WithoutARedirection_TheReportReachesTheTerminal(string goal, string expected)
    {
        Assert.True(_engine.Query(goal + ".").Success);
        Assert.Contains(expected, _console.ToString());
    }

    // PrologEngine.Out is where the program's output goes, also when the host
    // sets it after the first query has built the streams.
    [Fact]
    public void AnOutSetAfterTheFirstQuery_ReceivesTheOutput()
    {
        var engine = new PrologEngine();
        engine.ConsultString(@"
:- dynamic(late/1).
late(1).
");
        Assert.True(engine.Query("true.").Success);
        var late = new StringWriter();
        engine.Out = late;
        Assert.True(engine.Query("write(hello), nl, listing(late/1).").Success);
        Assert.Contains("hello", late.ToString());
        Assert.Contains("late(1).", late.ToString());
    }

    private sealed class LineTracking : StringWriter, Shumway.Core.ILineStartAware
    {
        public bool AtLineStart { get; private set; } = true;

        public override void Write(char value)
        {
            base.Write(value);
            AtLineStart = value == '\n';
        }

        public override void Write(string? value)
        {
            base.Write(value);
            if (value is { Length: > 0 }) AtLineStart = value[value.Length - 1] == '\n';
        }

        public override void Write(char[] buffer, int index, int count)
        {
            base.Write(buffer, index, count);
            if (count > 0) AtLineStart = buffer[index + count - 1] == '\n';
        }
    }

    // time/1 starts its report on a fresh line when the terminal says the goal
    // left it mid-line, as the top level's column tracker does.
    [Fact]
    public void TimeStartsItsReportOnAFreshLine()
    {
        var tracking = new LineTracking();
        var engine = new PrologEngine { Out = tracking };
        Assert.True(engine.Query("time((write(x), true)).").Success);
        Assert.StartsWith("x\n%", tracking.ToString().Replace("\r\n", "\n"));
    }

    [Fact]
    public void WithOutputTo_StillCapturesTheReport()
    {
        var sol = _engine.Query("with_output_to(atom(A), listing(fact/1)).");
        Assert.True(sol.Success);
        Assert.Contains("fact(2).", sol["A"]?.ToString() ?? "");
    }
}
