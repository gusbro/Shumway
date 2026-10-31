using System;
using System.IO;
using System.Runtime.InteropServices;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A file that opens and then refuses its bytes (a device that takes
/// no writes, a full device, a region another handle locked) reports the
/// refusal as <c>system_error(Message)</c> that <c>catch/3</c> catches, where
/// the host's exception used to leave the query (#172). Writes meet the host
/// when the buffers empty: at <c>flush_output</c>, at <c>close</c>, or at a
/// write that fills them.</summary>
public sealed class HostIoErrorTests
{
    // A file that opens and fails when its bytes reach the host: on Windows a
    // region another handle locked, elsewhere a device that refuses writes.
    private sealed class FailingSink : IDisposable
    {
        private readonly FileStream? _holder;
        private readonly string? _dir;
        private readonly long _lockFrom;

        public string Path { get; }

        /// <summary>A file of ours, which a released handle lets us delete.</summary>
        public bool IsOwnFile => _dir is not null;

        public FailingSink(long lockFrom = 0, string content = "")
        {
            _lockFrom = lockFrom;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shumway-hostio-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_dir);
                Path = System.IO.Path.Combine(_dir, "locked.txt");
                File.WriteAllText(Path, content);
                // Read access is enough to lock, and lets tell/1 (which shares
                // only reading) open the file too.
                _holder = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                _holder.Lock(lockFrom, 1 << 20);
            }
            else
                Path = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "/dev/urandom" : "/dev/full";
            // Not vacuous: the host refuses a plain write here.
            Assert.ThrowsAny<Exception>(() =>
            {
                using var raw = new FileStream(Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                raw.Position = lockFrom;
                raw.Write(new byte[] { 1, 2, 3 }, 0, 3);
                raw.Flush();
            });
        }

        public string Atom => "'" + Path.Replace('\\', '/') + "'";

        public void Dispose()
        {
            if (_holder is not null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _holder.Unlock(_lockFrom, 1 << 20);
                _holder.Dispose();
            }
            // Best effort: a stream the test failed to close keeps the file,
            // and this must not hide the test's own failure.
            if (_dir is not null)
                try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }

    private static PrologEngine Engine() => new() { Out = new StringWriter() };

    [Fact]
    public void FlushingWhatTheHostRefusesRaisesASystemError()
    {
        using var sink = new FailingSink();
        var e = Engine();

        var answer = e.Query($@"open({sink.Atom}, write, S), write(S, abc),
            catch(flush_output(S), error(system_error(M), C), true),
            close(S, [force(true)]), atom(M), C == flush_output/1.");

        Assert.True(answer.Success);
    }

    [Fact]
    public void AWriteThatFillsTheBuffersRaisesAtTheWrite()
    {
        using var sink = new FailingSink();
        var e = Engine();

        Assert.True(e.Query($@"length(Cs, 20000), maplist(=(a), Cs), atom_chars(A, Cs),
            open({sink.Atom}, write, S),
            catch(write(S, A), error(system_error(_), write/2), Raised = true),
            close(S, [force(true)]), Raised == true.").Success);
    }

    [Fact]
    public void ClosingAStreamWhoseBytesTheHostRefusesRaisesAndReleasesIt()
    {
        using var sink = new FailingSink();
        var e = Engine();

        Assert.True(e.Query($@"open({sink.Atom}, write, S), write(S, abc),
            catch(close(S), error(system_error(_), close/1), Raised = true),
            Raised == true, \+ is_stream(S).").Success);
        // The file is no longer open: a handle still holding it would keep it.
        if (sink.IsOwnFile) File.Delete(sink.Path);
    }

    [Fact]
    public void AForcedCloseSwallowsTheFailureAndReleasesTheStream()
    {
        using var sink = new FailingSink();
        var e = Engine();

        Assert.True(e.Query($@"open({sink.Atom}, write, S), write(S, abc),
            close(S, [force(true)]), \+ is_stream(S).").Success);
    }

    [Fact]
    public void ABinaryStreamRaisesTheSameError()
    {
        using var sink = new FailingSink();
        var e = Engine();

        Assert.True(e.Query($@"open({sink.Atom}, write, S, [type(binary)]), put_byte(S, 1),
            catch(flush_output(S), error(system_error(_), flush_output/1), Raised = true),
            close(S, [force(true)]), Raised == true.").Success);
    }

    [Fact]
    public void FlushingABinaryStreamWritesItsBytes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway-hostio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "bytes.bin");
            var e = Engine();

            // The bytes are on disk at the flush, before the close.
            Assert.True(e.Query($@"open('{file.Replace('\\', '/')}', write, S, [type(binary)]),
                put_byte(S, 65), flush_output(S), nb_setval(hostio_stream, S).").Success);
            using (var read = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                Assert.Equal(65, read.ReadByte());
            Assert.True(e.Query("nb_getval(hostio_stream, S), close(S).").Success);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TheEdinburghOutputRaisesTheSameError()
    {
        using var sink = new FailingSink();
        var e = Engine();

        Assert.True(e.Query($@"tell({sink.Atom}), write(abc),
            catch(flush_output, error(system_error(_), flush_output/0), Raised = true),
            told, Raised == true.").Success);
        // told let the file go, though its flush failed.
        if (sink.IsOwnFile) File.Delete(sink.Path);
    }

    [Fact]
    public void TellingADirectoryIsAPermissionError()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway-hostio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string atom = "'" + dir.Replace('\\', '/') + "'";
            var e = Engine();

            Assert.True(e.Query($@"catch(tell({atom}),
                error(permission_error(open, source_sink, {atom}), tell/1), Raised = true),
                Raised == true.").Success);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AReadTheHostRefusesRaisesASystemErrorEachTime()
    {
        // A region locked past the first 8 KB: the stream opens and reads its
        // start, and the read that reaches the region fails, and fails again.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        using var sink = new FailingSink(lockFrom: 8192, content: new string('a', 20000));
        var e = Engine();

        Assert.True(e.Query($@"open({sink.Atom}, read, S),
            catch(( between(1, 20000, _), get_char(S, _), fail ; true ),
                  error(system_error(_), get_char/2), First = true),
            catch(get_char(S, _), error(system_error(_), get_char/2), Second = true),
            close(S), First == true, Second == true.").Success);
    }

    [Fact]
    public void AStreamWhoseBytesTheHostRefusedLeavesStreamPropertyAnswering()
    {
        // Left open after its flush failed, with the bytes still buffered (a
        // test that expects the error and moves on): a later query about the
        // streams answers, and this one has no position.
        using var sink = new FailingSink();
        var e = Engine();

        Assert.True(e.Query($@"open({sink.Atom}, write, S), write(S, abc),
            catch(flush_output(S), error(system_error(_), _), true),
            stream_property(_, alias(user_output)),
            findall(P, stream_property(S, P), Ps), \+ memberchk(position(_), Ps),
            memberchk(mode(write), Ps),
            close(S, [force(true)]).").Success);
    }

    [Fact]
    public void AQueryAboutAnotherPropertyWritesNothing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway-hostio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "pending.txt");
            var e = Engine();

            // The text stays in the writer's buffer: no query but position
            // flushes it.
            Assert.True(e.Query($@"open('{file.Replace('\\', '/')}', write, S), write(S, abc),
                stream_property(S, file_name(_)), stream_property(S, mode(write)),
                nb_setval(hostio_pending, S).").Success);
            Assert.Equal(0, new FileInfo(file).Length);
            Assert.True(e.Query("nb_getval(hostio_pending, S), close(S).").Success);
            Assert.Equal(3, new FileInfo(file).Length);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void AReadTheHostRefusedLeavesStreamPropertyAnswering()
    {
        // The input side: end_of_stream peeks, and a refused read leaves the
        // state unknown, not an error.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        using var sink = new FailingSink(lockFrom: 8192, content: new string('a', 20000));
        var e = Engine();

        Assert.True(e.Query($@"open({sink.Atom}, read, S),
            catch(( between(1, 20000, _), get_char(S, _), fail ; true ), error(system_error(_), _), true),
            findall(P, stream_property(S, P), Ps), \+ memberchk(end_of_stream(_), Ps),
            close(S).").Success);
    }
}
