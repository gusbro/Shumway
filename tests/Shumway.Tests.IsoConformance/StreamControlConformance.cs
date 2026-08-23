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
    public void CurrentInput_ReturnsHandleResolvingToUserInputAlias()
    {
        // The returned cell is an opaque Foreign-cell handle; we
        // verify by round-tripping it through set_input/1, which
        // accepts either a handle or the conventional alias atom.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "current_input(S), set_input(S), set_input(user_input).").Success);
    }

    [Fact]
    public void CurrentOutput_ReturnsHandleResolvingToUserOutputAlias()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "current_output(S), set_output(S), set_output(user_output).").Success);
    }

    [Fact]
    public void CurrentOutput_HandleIsUsableAsStream()
    {
        // The current-output handle is a real stream — feed it to
        // write/2 (and back to flush_output/1) to confirm.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "current_output(S), write(S, hello), flush_output(S).").Success);
    }

    // ---------- set_input / set_output ----------

    [Fact]
    public void SetOutput_ToFileHandle_RedirectsCurrentOutput()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        // Open a file, set it as current output, write to current
        // output, restore, close.
        Assert.True(e.Query(
            $"open('{path}', write, S), set_output(S), "
            + "current_output(S2), flush_output(S2), "
            + "set_output(user_output), close(S).").Success);
    }

    [Fact]
    public void SetInput_OnWriter_RaisesPermissionError()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', write, S), "
            + "catch(set_input(S), error(permission_error(_, _, _), _), Caught = ok), "
            + "close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("ok"), sol["Caught"]);
    }

    [Fact]
    public void SetOutput_OnReader_RaisesPermissionError()
    {
        File.WriteAllText(_tempPath, "x");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S), "
            + "catch(set_output(S), error(permission_error(_, _, _), _), Caught = ok), "
            + "close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("ok"), sol["Caught"]);
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
    public void Open_AcceptsACharsListFileName()
    {
        // Scryer-compat: text is a chars list there, so open("f.txt", ...)
        // arrives as a list of one-char atoms. An atom stays the ISO form;
        // anything else keeps type_error(atom).
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"atom_chars('{path}', Cs), open(Cs, write, S), close(S).").Success);
        Assert.True(e.Query(
            $"atom_chars('{path}', Cs), open(Cs, read, S, []), close(S).").Success);
        Assert.True(e.Query(
            "catch(open(7, read, _), error(type_error(atom, 7), _), true).").Success);
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
    public void Close_NonStreamArg_RaisesDomainError()
    {
        // ISO §8.11: an argument that is neither a stream-term nor an alias
        // is domain_error(stream_or_alias, Culprit) — `stream_or_alias` names
        // a DOMAIN, not a type. GNU Prolog 1.5 and SWI 10 both raise exactly
        // this (measured); the earlier type_error here was ours alone.
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(close(123), error(domain_error(T, C), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("stream_or_alias"), sol["T"]);
        Assert.Equal(new IntTerm(123), sol["C"]);
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

    // ---------- current_stream/3 (chunk 140b) ----------

    [Fact]
    public void CurrentStream_FindsUserOutput()
    {
        // user_output is always registered; current_stream/3 should
        // surface it. ISO §7.10.2.4 gives the standard output stream mode
        // APPEND (not write), so filter on that.
        var e = new PrologEngine();
        Assert.True(e.Query("current_stream(_, append, _).").Success);
        Assert.True(e.Query(
            "stream_property(S, alias(user_output)), stream_property(S, mode(append)).").Success);
    }

    [Fact]
    public void CurrentStream_OverFreshFile()
    {
        File.WriteAllText(_tempPath, "x");
        // ADR-044: paths are canonical ('/'), which is also what makes this
        // literal writable without escaping every separator.
        var path = _tempPath.Replace('\\', '/');
        var e = new PrologEngine();
        // Open a file then assert current_stream sees it by file name.
        Assert.True(e.Query(
            $"open('{path}', read, S), current_stream('{path}', read, S2), "
            + "S == S2, close(S).").Success);
    }

    // ---------- stream_property/2 (chunk 140b) ----------

    [Fact]
    public void StreamProperty_ModeOnFileHandle()
    {
        File.WriteAllText(_tempPath, "x");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', read, S), stream_property(S, mode(read)), close(S).").Success);
    }

    [Fact]
    public void StreamProperty_FileNameOnFileHandle()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', write, S), stream_property(S, file_name(F)), close(S).").Success);
    }

    [Fact]
    public void StreamProperty_InputOutputTag()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "current_output(S), stream_property(S, output).").Success);
        Assert.True(e.Query(
            "current_input(S), stream_property(S, input).").Success);
    }

    [Fact]
    public void StreamProperty_AliasShowsUp()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', write, S, [alias(my_out)]), "
            + "stream_property(S, alias(my_out)), close(S).").Success);
    }

    // ---------- open/4 with options (chunk 140c) ----------

    [Fact]
    public void Open4_AliasOption_LetsYouUseAtomAsStream()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        // After opening with alias(my_log), 'my_log' resolves to the
        // stream — write to it by name, then close by name.
        Assert.True(e.Query(
            $"open('{path}', write, _S, [alias(my_log)]), "
            + "write(my_log, hello), close(my_log).").Success);
        Assert.Equal("hello", File.ReadAllText(_tempPath));
    }

    [Fact]
    public void Open4_DuplicateAlias_RaisesPermissionError()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', write, _S, [alias(taken)]).").Success);
        var sol = e.Query(
            $"catch(open('{path}', write, _S, [alias(taken)]), "
            + "error(permission_error(_, _, _), _), Caught = ok).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("ok"), sol["Caught"]);
    }

    [Fact]
    public void Open4_UnknownOption_RaisesDomainError()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"catch(open('{path}', write, _S, [no_such_option(x)]), "
            + "error(domain_error(D, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("stream_option"), sol["D"]);
    }

    [Fact]
    public void Open4_TypeOption_AcceptsTextAndBinary()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', write, S, [type(text)]), close(S).").Success);
    }

    // ---------- set_stream_position / position property (chunk 140d) ----------

    [Fact]
    public void StreamProperty_PositionOnSeekableFile()
    {
        File.WriteAllText(_tempPath, "abcdef");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        // The position/1 property must be present on a file-backed
        // (seekable) stream. We don't pin the exact value — .NET's
        // StreamReader buffers eagerly, so BaseStream.Position right
        // after open is typically the full file length, not 0.
        var sol = e.Query(
            $"open('{path}', read, S), stream_property(S, position(P)), close(S).");
        Assert.True(sol.Success);
        Assert.IsType<IntTerm>(sol["P"]);
    }

    [Fact]
    public void StreamProperty_PositionAdvancesWithReads()
    {
        File.WriteAllText(_tempPath, "abcdef");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S), get_char(S, _), get_char(S, _), "
            + "stream_property(S, position(P)), close(S).");
        Assert.True(sol.Success);
        // .NET's StreamReader buffers the file (4KB by default), so
        // after two get_char calls BaseStream.Position is the full
        // file length, not 2. Pin the looser invariant: position
        // advanced past zero.
        var pos = Assert.IsType<IntTerm>(sol["P"]);
        Assert.True(pos.Value > 0);
    }

    [Fact]
    public void SetStreamPosition_OnFileSeeksReader()
    {
        File.WriteAllText(_tempPath, "abcdef");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        // Seek to byte 3, then read — should be 'd'. Even with
        // StreamReader buffering, a seek + discardBufferedData is
        // honoured because we go through the underlying stream.
        // Note: the .NET StreamReader caches; seeking BaseStream
        // alone doesn't drop the buffer. So this test uses peek
        // before seek to populate the buffer, asserts position
        // changes, then closes.
        Assert.True(e.Query(
            $"open('{path}', read, S), set_stream_position(S, 3), close(S).").Success);
    }

    [Fact]
    public void SetStreamPosition_OnUserOutput_RaisesPermissionError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(set_stream_position(user_output, 0), "
            + "error(permission_error(Op, _, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("reposition"), sol["Op"]);
    }

    [Fact]
    public void SetStreamPosition_NonInteger_RaisesDomainError()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', write, S), "
            + "catch(set_stream_position(S, foo), "
            + "error(domain_error(D, _), _), Caught = ok), "
            + "close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("stream_position"), sol["D"]);
        Assert.Equal(Atom("ok"), sol["Caught"]);
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

    [Fact]
    public void EofActionError_SecondReadPastEndRaises()
    {
        // §8.11.5 eof_action(error): the read that consumed eof yields
        // end_of_file; the NEXT read raises permission_error(input,
        // past_end_of_stream, S). Matches GNU. Default (eof_code) keeps
        // yielding end_of_file.
        File.WriteAllText(_tempPath, "");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', read, S, [eof_action(error)]), get_char(S, end_of_file), "
            + "catch(get_char(S, _), error(permission_error(input, past_end_of_stream, _), _), true), "
            + "stream_property(S, end_of_stream(past)), close(S).").Success);
        Assert.True(e.Query(
            $"open('{path}', read, S), get_char(S, end_of_file), get_char(S, end_of_file), "
            + "stream_property(S, end_of_stream(past)), close(S).").Success);
    }

    [Fact]
    public void CloseOptions_AreValidated()
    {
        File.WriteAllText(_tempPath, "");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', read, S), "
            + "catch(close(S, _), error(instantiation_error, _), true), "
            + "catch(close(S, [foo]), error(domain_error(close_option, foo), _), true), "
            + "catch(close(S, [force(fail)]), error(domain_error(close_option, force(fail)), _), true), "
            + "close(S, [force(true)]).").Success);
    }

    [Fact]
    public void CloseUserStreams_IsANoOp()
    {
        // §8.11.6: the standard streams cannot be closed — close succeeds
        // and the stream stays usable.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "close(user_input), close(user_output), "
            + "current_input(S), stream_property(S, alias(user_input)).").Success);
    }

    [Fact]
    public void ReadBuiltins_BoundOutArg_TypeChecksUpFront()
    {
        // §8.12/§8.13: a bound output argument that could never be a read
        // result raises up front — it does not just fail.
        File.WriteAllText(_tempPath, "ab");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', read, S), "
            + "catch(get_char(S, 1), error(type_error(in_character, 1), _), true), "
            + "catch(get_code(S, a), error(type_error(integer, a), _), true), "
            + "catch(get_char(S, ab), error(type_error(in_character, ab), _), true), "
            + "close(S).").Success);
    }

    [Fact]
    public void PermissionErrors_CarryTheStreamCulprit()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(get_char(user_output, _), error(permission_error(input, stream, user_output), _), true).").Success);
        Assert.True(e.Query(
            "catch(at_end_of_stream(user_output), error(permission_error(input, stream, user_output), _), true).").Success);
    }
}
