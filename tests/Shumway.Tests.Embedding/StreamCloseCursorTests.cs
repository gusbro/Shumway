using System;
using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ISO §8.11.6 — closing the CURRENT input or output moves that
/// cursor back to <c>user_input</c> / <c>user_output</c>.
///
/// <para>Without it, <c>current_output/1</c> hands the program a stream term
/// naming a handle that is no longer registered — a dangling reference it can
/// only discover by failing to use it. That is not hypothetical: it broke the
/// save-redirect-restore idiom (<c>current_output(O), …, set_output(O)</c>)
/// that Logtalk's test framework runs around every suite.</para></summary>
public sealed class StreamCloseCursorTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(),
            "shumway_cur_" + Guid.NewGuid().ToString("N") + ".txt");

    private static string Q(string p) => p.Replace("\\", "\\\\");

    [Fact]
    public void ClosingCurrentOutput_RestoresUserOutput()
    {
        string f = TempPath();
        try
        {
            var e = new PrologEngine();
            var sol = e.Query(
                $"open('{Q(f)}', write, S), set_output(S), close(S), "
                + "current_output(Now), stream_property(Now, alias(A)).");
            Assert.True(sol.Success);
            Assert.Equal("user_output", ((AtomTerm)sol["A"]!).Name);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void ClosingCurrentInput_RestoresUserInput()
    {
        string f = TempPath();
        File.WriteAllText(f, "x.\n");
        try
        {
            var e = new PrologEngine();
            var sol = e.Query(
                $"open('{Q(f)}', read, S), set_input(S), close(S), "
                + "current_input(Now), stream_property(Now, alias(A)).");
            Assert.True(sol.Success);
            Assert.Equal("user_input", ((AtomTerm)sol["A"]!).Name);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void SaveRedirectRestore_RoundTrips()
    {
        // The idiom the regression actually broke.
        string f = TempPath();
        try
        {
            var e = new PrologEngine();
            var sol = e.Query(
                "current_output(Saved), "
                + $"open('{Q(f)}', write, S), set_output(S), close(S), "
                + "set_output(Saved).");
            Assert.True(sol.Success);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void ClosingANonCurrentStream_LeavesTheCursorAlone()
    {
        string a = TempPath(), b = TempPath();
        try
        {
            var e = new PrologEngine();
            var sol = e.Query(
                $"open('{Q(a)}', write, S1), open('{Q(b)}', write, S2), "
                + "set_output(S1), close(S2), current_output(Now), "
                + "(Now == S1 -> R = same ; R = moved), close(S1), set_output(user_output).");
            Assert.True(sol.Success);
            Assert.Equal("same", ((AtomTerm)sol["R"]!).Name);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void AClosedStreamTerm_IsStillAnExistenceError()
    {
        // The cursor moving is not a licence to keep using the closed stream:
        // a term naming it must still be rejected (ISO §8.11).
        string f = TempPath();
        try
        {
            var e = new PrologEngine();
            var ex = Assert.ThrowsAny<Exception>(() => e.Query(
                $"open('{Q(f)}', write, S), close(S), set_output(S)."));
            Assert.Contains("existence_error", ex.Message);
        }
        finally { File.Delete(f); }
    }
}
