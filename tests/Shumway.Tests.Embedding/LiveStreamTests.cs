using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Streams over a pipe or a socket: they cannot seek, a read can wait on the other
/// end, and that other end can send anything. Real anonymous pipes stand in for both.</summary>
public sealed class LiveStreamTests : IDisposable
{
    private readonly PrologEngine _engine = new();
    private readonly AnonymousPipeServerStream _toEngine = new(PipeDirection.Out);
    private readonly AnonymousPipeClientStream _engineReads;
    private readonly AnonymousPipeServerStream _fromEngine = new(PipeDirection.In);
    private readonly AnonymousPipeClientStream _engineWrites;

    public LiveStreamTests()
    {
        _engineReads = new AnonymousPipeClientStream(PipeDirection.In, _toEngine.ClientSafePipeHandle);
        _engineWrites = new AnonymousPipeClientStream(PipeDirection.Out, _fromEngine.ClientSafePipeHandle);
    }

    public void Dispose()
    {
        _toEngine.Dispose();
        _engineReads.Dispose();
        _fromEngine.Dispose();
        _engineWrites.Dispose();
    }

    private sealed class EndedInput : Stream
    {
        public int Reads;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) { Interlocked.Increment(ref Reads); return 0; }
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }

    private sealed class TwoWays : Stream
    {
        private readonly Stream _in, _out;
        public TwoWays(Stream input, Stream output) { _in = input; _out = output; }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _out.Flush();
        public override int Read(byte[] b, int o, int c) => _in.Read(b, o, c);
        public override void Write(byte[] b, int o, int c) => _out.Write(b, o, c);
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }

    private string Reading(bool binary = false, string? encoding = null, string? eofAction = null)
    {
        StreamHandle h = StreamBuiltins.OpenLiveStream(_engine.Streams, _engineReads, "read", binary,
            encoding, alias: null, eofAction, translateNewlines: false);
        return $"'$stream'({h.Id})";
    }

    private string Writing(bool binary = false, string? encoding = null)
    {
        StreamHandle h = StreamBuiltins.OpenLiveStream(_engine.Streams, _engineWrites, "write", binary,
            encoding, alias: null, null, translateNewlines: false);
        return $"'$stream'({h.Id})";
    }

    private string BothWays(bool binary = false)
    {
        StreamHandle h = StreamBuiltins.OpenLiveStream(_engine.Streams, new TwoWays(_engineReads, _engineWrites),
            "readwrite", binary, null, alias: null, null, translateNewlines: false);
        return $"'$stream'({h.Id})";
    }

    private void Send(params byte[] bytes)
    {
        _toEngine.Write(bytes, 0, bytes.Length);
        _toEngine.Flush();
    }

    private byte[] Received(int count)
    {
        var got = new byte[count];
        int have = 0;
        while (have < count)
        {
            int n = _fromEngine.Read(got, have, count - have);
            if (n <= 0) break;
            have += n;
        }
        return got.Take(have).ToArray();
    }

    private static T WithinSeconds<T>(int seconds, Func<T> work) => Finished(OwnThread.Run(work), seconds);

    private static T Finished<T>(Task<T> task, int seconds = 60)
    {
        Assert.True(task.Wait(TimeSpan.FromSeconds(seconds)), "it hung");
        return task.Result;
    }

    // ---------- bytes

    [Fact]
    public void PeekByte_WorksWithoutSeeking_AndTheByteIsStillThere()
    {
        string s = Reading(binary: true);
        Send(97, 98);
        Assert.True(_engine.Query($@"
            peek_byte({s}, 97), peek_byte({s}, 97), get_byte({s}, 97),
            peek_byte({s}, 98), get_byte({s}, 98).").Success);
    }

    [Fact]
    public void AtEndOfStream_OfBytes_AnswersByReading()
    {
        string s = Reading(binary: true, eofAction: "eof_code");
        Send(97);
        Assert.True(_engine.Query($@"\+ at_end_of_stream({s}), get_byte({s}, 97).").Success);
        _toEngine.Dispose();
        Assert.True(_engine.Query($@"at_end_of_stream({s}), get_byte({s}, -1).").Success);
    }

    [Fact]
    public void PeekByte_AtTheEnd_LeavesTheEndForTheRead()
    {
        string s = Reading(binary: true);
        _toEngine.Dispose();
        Assert.True(_engine.Query($@"
            peek_byte({s}, -1), get_byte({s}, -1),
            catch(get_byte({s}, _), error(permission_error(input, past_end_of_stream, _), _), true).").Success);
    }

    [Fact]
    public void ABinaryWriter_DeliversOnFlush()
    {
        string s = Writing(binary: true);
        Assert.True(_engine.Query($@"put_byte({s}, 97), put_byte({s}, 200), flush_output({s}).").Success);
        Assert.Equal(new byte[] { 97, 200 }, WithinSeconds(10, () => Received(2)));
    }

    // ---------- what the other end sends

    [Fact]
    public void Text_InTheDefaultEncoding_ReadsAnythingWithoutAnError()
    {
        string s = Reading();
        Send(0x61, 0x82, 0xC3, 0xA9, 0xE9, 0x62);
        _toEngine.Dispose();
        var sol = _engine.Query($@"
            get_code({s}, A), peek_code({s}, B0), get_code({s}, B), get_code({s}, C),
            get_code({s}, D), get_code({s}, E), get_code({s}, F).");
        Assert.True(sol.Success);
        long[] codes = new[] { "A", "B0", "B", "C", "D", "E", "F" }.Select(v => ((IntTerm)sol[v]!).Value).ToArray();
        Assert.Equal(new long[] { 0x61, 0xFFFD, 0xFFFD, 0xE9, 0xFFFD, 0x62, -1 }, codes);
    }

    [Fact]
    public void Text_CutShortAtTheEnd_ReadsWithoutAnError()
    {
        string s = Reading();
        Send(0x61, 0xE2, 0x82);
        _toEngine.Dispose();
        Assert.True(_engine.Query($@"
            get_code({s}, 0x61), get_code({s}, 0xFFFD), get_code({s}, 0xFFFD), get_code({s}, -1).").Success);
    }

    [Fact]
    public void Text_InADefaultWideEncoding_MovesPastWhatIsNotValid()
    {
        Assert.True(_engine.Query("set_prolog_flag(encoding, 'UTF-16LE').").Success);
        string s = Reading();
        Send(0x61, 0, 0x00, 0xDC, 0x62, 0);
        _toEngine.Dispose();
        Assert.True(_engine.Query($@"
            get_code({s}, 0x61), get_code({s}, 0xFFFD), get_code({s}, 0x62), get_code({s}, -1).").Success);
    }

    [Fact]
    public void Text_InANamedEncoding_RaisesWhatDoesNotFit()
    {
        string s = Reading(encoding: "utf8");
        Send(0x61, 0x82, 0x62);
        Assert.True(_engine.Query($@"
            get_char({s}, a),
            catch(get_code({s}, _), error(representation_error(character), _), true),
            get_char({s}, b).").Success);
    }

    [Fact]
    public void Text_InANamedNarrowEncoding_WritesAnErrorForWhatDoesNotFit()
    {
        string s = Writing(encoding: "iso_latin_1");
        Assert.True(_engine.Query($@"
            catch((put_code({s}, 0x3042), fail), error(representation_error(character), _), true),
            put_code({s}, 0xE9), flush_output({s}).").Success);
        Assert.Equal(new byte[] { 0xE9 }, WithinSeconds(10, () => Received(1)));
    }

    [Fact]
    public void Text_AstralAcrossTwoWrites_ReadsAsOneCharacter()
    {
        string s = Reading();
        Send(0xF0, 0x9D);
        var read = OwnThread.Run(() => _engine.Query($"get_code({s}, C)."));
        Thread.Sleep(100);
        Assert.False(read.IsCompleted);
        Send(0x84, 0x9E);
        Assert.Equal(0x1D11EL, ((IntTerm)Finished(read)["C"]!).Value);
    }

    // ---------- waiting

    [Fact]
    public void PeekChar_WaitsForTheData_AndDoesNotSayEndOfFile()
    {
        string s = Reading();
        Send(0x61);
        Assert.True(_engine.Query($"get_char({s}, a).").Success);
        var peek = OwnThread.Run(() => _engine.Query($"peek_char({s}, C)."));
        Thread.Sleep(150);
        Assert.False(peek.IsCompleted);
        Send(0x62);
        Assert.Equal("b", ((AtomTerm)Finished(peek)["C"]!).Name);
    }

    [Fact]
    public void AQuestionAboutTheStream_DoesNotWaitForData()
    {
        string s = Reading();
        var sol = WithinSeconds(10, () => _engine.Query($@"
            stream_property({s}, end_of_stream(E)),
            stream_property({s}, reposition(false)),
            stream_property({s}, position(0)),
            findall(P, stream_property({s}, P), _)."));
        Assert.True(sol.Success);
        Assert.Equal("not", ((AtomTerm)sol["E"]!).Name);
    }

    [Fact]
    public void AReadThatWaits_IsCancelled()
    {
        string s = Reading();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var ex = Assert.ThrowsAny<AggregateException>(() =>
            Finished(OwnThread.Run(() => _engine.QueryAll($"get_char({s}, _).", cts.Token).ToList())));
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public void AReadThatRunsOutOfTime_LosesNoBytes()
    {
        string s = Reading();
        var late = WithinSeconds(20, () => _engine.Query($"time_out(get_char({s}, _), 200, R)."));
        Assert.True(late.Success);
        Assert.Equal("time_out", ((AtomTerm)late["R"]!).Name);
        Send(0x61, 0x62);
        Assert.True(WithinSeconds(10, () => _engine.Query($"get_char({s}, a), get_char({s}, b).")).Success);
    }

    [Fact]
    public void ATimeoutOnTheStream_IsAnError_AndLosesNoBytes()
    {
        StreamHandle h = StreamBuiltins.OpenLiveStream(_engine.Streams, _engineReads, "read", true,
            null, alias: null, null, translateNewlines: false);
        h.LiveConnection!.TimeoutMs = 100;
        string s = $"'$stream'({h.Id})";
        Assert.True(WithinSeconds(10, () => _engine.Query($@"
            catch(get_byte({s}, _), error(timeout_error(read), _), true).")).Success);
        Send(97);
        Assert.True(WithinSeconds(10, () => _engine.Query($"get_byte({s}, 97).")).Success);
    }

    [Fact]
    public void AFailedConnection_IsASystemError()
    {
        StreamHandle h = StreamBuiltins.OpenLiveStream(_engine.Streams, _engineWrites, "write", true,
            null, alias: null, null, translateNewlines: false);
        _fromEngine.Dispose();
        string s = $"'$stream'({h.Id})";
        Assert.True(WithinSeconds(10, () => _engine.Query($@"
            catch((put_byte({s}, 97), flush_output({s}), fail), error(system_error(_), _), true).")).Success);
    }

    // ---------- two directions

    [Fact]
    public void AStreamOfTwoDirections_ReportsBoth()
    {
        string s = BothWays();
        Assert.True(_engine.Query($@"
            stream_property({s}, mode(readwrite)),
            stream_property({s}, input),
            stream_property({s}, output),
            stream_property({s}, type(text)),
            stream_property({s}, encoding('UTF-8')),
            stream_property({s}, eof_action(error)).").Success);
    }

    [Fact]
    public void AnInputStream_ReportsOnlyInput()
    {
        string s = Reading();
        Assert.True(_engine.Query($@"stream_property({s}, input), \+ stream_property({s}, output).").Success);
    }

    [Fact]
    public void Text_GoesBothWaysOnOneStream()
    {
        string s = BothWays();
        Send(0x61);
        Assert.True(_engine.Query($@"get_char({s}, a), put_char({s}, b), flush_output({s}).").Success);
        Assert.Equal(new byte[] { 0x62 }, WithinSeconds(10, () => Received(1)));
    }

    [Fact]
    public void Bytes_GoBothWaysOnOneStream()
    {
        string s = BothWays(binary: true);
        Send(97);
        Assert.True(_engine.Query($@"
            stream_property({s}, type(binary)), stream_property({s}, mode(readwrite)),
            peek_byte({s}, 97), get_byte({s}, 97), put_byte({s}, 98), flush_output({s}).").Success);
        Assert.Equal(new byte[] { 98 }, WithinSeconds(10, () => Received(1)));
    }

    [Fact]
    public void Closing_DeliversWhatIsBuffered_AndEndsTheConnection()
    {
        string s = BothWays();
        Assert.True(_engine.Query($@"put_char({s}, z), close({s}).").Success);
        Assert.Equal(new byte[] { 0x7A }, WithinSeconds(10, () => Received(1)));
    }

    [Fact]
    public void BothDirectionsAreCurrentStreams()
    {
        string s = BothWays();
        Send(0x61);
        Assert.True(_engine.Query($@"
            set_input({s}), set_output({s}),
            get_char(a), put_char(b), flush_output,
            set_input(user_input), set_output(user_output).").Success);
        Assert.Equal(new byte[] { 0x62 }, WithinSeconds(10, () => Received(1)));
    }

    [Fact]
    public void AStreamOfTwoDirections_CannotBeRepositioned()
    {
        string s = BothWays();
        Assert.True(_engine.Query($@"
            catch(set_stream_position({s}, 0), error(permission_error(reposition, stream, _), _), true).").Success);
    }

    [Fact]
    public void ALiveStream_CannotReposition()
    {
        Assert.True(_engine.Query($"stream_property({Reading()}, reposition(false)).").Success);
    }

    [Fact]
    public void WhatWasDecodedAhead_GoesToACopy_AsBytes()
    {
        StreamHandle h = StreamBuiltins.OpenLiveStream(_engine.Streams, _engineReads, "read", binary: false,
            null, alias: null, null, translateNewlines: false);
        Send(0xF0, 0x9F, 0x98, 0x80, 0x78);
        _toEngine.Dispose();
        Assert.True(_engine.Query($"peek_code('$stream'({h.Id}), 0x1F600).").Success);
        Assert.Equal(new byte[] { 0xF0, 0x9F, 0x98, 0x80, 0x78 }, h.TakeInputBuffered());
        Assert.Empty(h.TakeInputBuffered());
    }

    [Fact]
    public void ALineEndPeekedAt_GoesToACopy_AsTheProgramSawIt()
    {
        StreamHandle h = StreamBuiltins.OpenLiveStream(_engine.Streams, _engineReads, "read", binary: false,
            null, alias: null, null, translateNewlines: true);
        Send(0x0D, 0x0A, 0x78);
        _toEngine.Dispose();
        Assert.True(_engine.Query($"peek_code('$stream'({h.Id}), 0x0A).").Success);
        Assert.Equal(new byte[] { 0x0A, 0x78 }, h.TakeInputBuffered());
    }

    [Fact]
    public void AnEndedInput_IsNotAskedAgain()
    {
        var input = new EndedInput();
        StreamHandle h = StreamBuiltins.OpenLiveStream(_engine.Streams, input, "read", binary: true,
            null, alias: null, "eof_code", translateNewlines: false);
        string s = $"'$stream'({h.Id})";
        Assert.True(_engine.Query("use_module(library(sockets)).").Success);
        Assert.True(_engine.Query($@"
            get_byte({s}, -1), get_byte({s}, -1),
            socket_select([], [], [{s}], [{s}], [], [], off),
            peek_byte({s}, -1).").Success);
        Assert.Equal(1, input.Reads);
    }

    [Fact]
    public void EofAction_IsTheOneAskedFor()
    {
        string s = Reading(binary: true, eofAction: "eof_code");
        _toEngine.Dispose();
        Assert.True(_engine.Query($@"
            stream_property({s}, eof_action(eof_code)),
            get_byte({s}, -1), get_byte({s}, -1).").Success);
    }
}
