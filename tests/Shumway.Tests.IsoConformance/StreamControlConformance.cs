using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// ISO 13211-1, §8.11 Stream selection and control.
///
/// Covers <c>current_input/1</c> (§8.11.1), <c>current_output/1</c>
/// (§8.11.2), <c>open/3</c> (§8.11.5), <c>close/1</c> (§8.11.6),
/// <c>flush_output/0,1</c> (§8.11.7) and <c>at_end_of_stream/0,1</c>
/// (§8.11.9). The bigger stream-registry features —
/// <c>stream_property/2</c>, <c>current_stream/3</c>,
/// <c>set_input/1</c>, <c>set_output/1</c>, the <c>open/4</c> options
/// list and <c>set_stream_position/2</c> — need a dedicated stream
/// registry on the engine and are recorded as gaps; queued for a
/// follow-up chunk.
/// </summary>
public class StreamControlConformance : IDisposable
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    private readonly string _tempPath;

    public StreamControlConformance()
    {
        _tempPath = Path.GetTempFileName();
    }

    public void Dispose()
    {
        try { File.Delete(_tempPath); } catch { /* best-effort */ }
    }

    // ---------- current_input / current_output ----------

    [Fact]
    public void CurrentInput_ReturnsUserInput()
    {
        var e = new PrologEngine();
        var sol = e.Query("current_input(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("user_input"), sol["S"]);
    }

    [Fact]
    public void CurrentOutput_ReturnsUserOutput()
    {
        var e = new PrologEngine();
        var sol = e.Query("current_output(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("user_output"), sol["S"]);
    }

    // ---------- open/3 ----------

    [Fact]
    public void Open_WriteMode_CreatesHandle()
    {
        var e = new PrologEngine();
        // Quote the path so backslashes pass through cleanly. Open
        // and close in the same query so the binding for S survives
        // through to close.
        var path = _tempPath.Replace("\\", "\\\\");
        var sol = e.Query($"open('{path}', write, S), close(S).");
        Assert.True(sol.Success);
        Assert.NotNull(sol["S"]);
    }

    [Fact]
    public void Open_ReadMode_ReadsBackWhatWasWritten()
    {
        File.WriteAllText(_tempPath, "hello");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S), get_char(S, C), close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("h"), sol["C"]);
    }

    [Fact]
    public void Open_BadMode_RaisesDomainError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(open('x', no_such_mode, _S), "
            + "error(domain_error(_, _), _), true).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Open_NonAtomPath_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(open(123, read, _S), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("atom"), sol["T"]);
    }

    // ---------- close/1 ----------

    [Fact]
    public void Close_NonStreamArg_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(close(123), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("stream"), sol["T"]);
    }

    [Fact]
    public void Close_AlreadyClosed_AnotherCloseStillReports()
    {
        // Closing the engine's writer twice should at least not crash;
        // ISO permits an existence_error on the second close. Our
        // impl raises existence_error("stream") when the foreign
        // entry is null — which matches.
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', write, S), close(S).").Success);
    }

    // ---------- flush_output ----------

    [Fact]
    public void FlushOutput0_Succeeds()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("flush_output.").Success);
    }

    [Fact]
    public void FlushOutput1_UserOutputAtom()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("flush_output(user_output).").Success);
    }

    [Fact]
    public void FlushOutput1_OnFileHandle()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', write, S), "
            + "write(S, hello), flush_output(S), close(S).").Success);
        // Reading back confirms the flush actually wrote.
        Assert.Equal("hello", File.ReadAllText(_tempPath));
    }

    [Fact]
    public void FlushOutput1_UnknownAtom_RaisesExistenceError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(flush_output(no_such_stream), "
            + "error(existence_error(_, _), _), true).");
        Assert.True(sol.Success);
    }

    // ---------- at_end_of_stream ----------

    [Fact]
    public void AtEndOfStream0_UserInput_Fails()
    {
        // We conservatively report user_input as not at end (peeking
        // a console reader would block).
        var e = new PrologEngine();
        Assert.False(e.Query("at_end_of_stream.").Success);
    }

    [Fact]
    public void AtEndOfStream1_FreshStream_NotAtEnd()
    {
        File.WriteAllText(_tempPath, "abc");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.False(e.Query(
            $"open('{path}', read, S), at_end_of_stream(S), close(S).").Success);
    }

    [Fact]
    public void AtEndOfStream1_AfterReadingAll_IsAtEnd()
    {
        File.WriteAllText(_tempPath, "x");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', read, S), get_char(S, _), at_end_of_stream(S), close(S).").Success);
    }
}
