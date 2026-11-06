using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Shumway.Core;

/// <summary>
/// A stream over a pipe or a socket, where a read can wait for as long as the other end
/// takes. It waits in steps through <see cref="StreamRegistry.Wait"/>, so the running query
/// can be cancelled or run out of time. A read left waiting stays pending: what it gets
/// goes to the next read, and no bytes are lost.
///
/// <para>It holds what is written until the buffer fills, a flush asks, or a read is about
/// to wait: the other end may be waiting for those bytes before it answers.</para>
/// </summary>
public sealed class LiveStream : Stream
{
    /// <summary>How many bytes a stream holds before it sends them, unless told otherwise.</summary>
    public const int DefaultBufferSize = 4096;

    private readonly Stream _inner;
    private readonly StreamRegistry _registry;
    // A pipe's calls block, its ReadAsync on a thread of the pool, and a pool with all its
    // threads taken may not add one while the machine is busy. A socket's calls do not block.
    private readonly bool _asynchronous;
    private readonly byte[] _buffer = new byte[4096];
    private byte[] _held;
    private int _heldCount;
    private Task? _writeLeftWaiting;
    private int _pos;
    private int _len;
    private Task<int>? _pendingRead;
    // An input that ended stays ended, and a pipe or socket is not asked again.
    private bool _ended;
    private bool _detached;

    /// <param name="bufferSize">The bytes it holds before sending them; zero sends each write.</param>
    /// <param name="asynchronous">Whether the stream's own asynchronous calls do not block a thread.</param>
    public LiveStream(Stream inner, StreamRegistry registry, int bufferSize = DefaultBufferSize, bool asynchronous = false)
    {
        _inner = inner;
        _registry = registry;
        _asynchronous = asynchronous;
        BufferSize = bufferSize;
        _held = new byte[inner.CanWrite ? bufferSize : 0];
    }

    public int BufferSize { get; }

    /// <summary>What must reach this stream before a read waits: what a writer above it holds.</summary>
    public Action? BeforeWait { get; set; }

    /// <summary>How long a read or a write may wait, in milliseconds, before it raises
    /// a timeout error. Zero waits for as long as it takes.</summary>
    public int TimeoutMs { get; set; }

    public override bool CanRead => _inner.CanRead;
    public override bool CanWrite => _inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Flush()
    {
        lock (_writeGate)
        {
            SendHeld();
            FinishEarlierWrite();
            if (_asynchronous && _inner.CanWrite) _registry.Wait(_inner.FlushAsync(), TimeoutMs, "write");
        }
    }

    private static Task<int> OnItsOwnThread(Func<int> call) => Task.Factory.StartNew(call,
        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private Task<int> StartRead() => _asynchronous
        ? _inner.ReadAsync(_buffer, 0, _buffer.Length)
        : OnItsOwnThread(() => _inner.Read(_buffer, 0, _buffer.Length));

    // Before a wait: the other end may be waiting for this to answer.
    private void SendHeldBeforeWaiting()
    {
        if (_pendingRead!.IsCompleted) return;
        // If the other end has gone, the read says so, after what it left.
        try
        {
            BeforeWait?.Invoke();
            lock (_writeGate) SendHeld();
        }
        catch (PrologRuntimeException ex) when (ex.Kind == "system_error") { }
    }

    /// <summary>Whether it holds no input and is waiting for none.</summary>
    public bool IsIdle => _pos == _len && _pendingRead is null;

    /// <summary>The connection, for something else to read and write it from now on.</summary>
    public Stream Detach()
    {
        Stream inner = _inner;
        _detached = true;
        return inner;
    }

    /// <summary>The read still waiting for input, leaving one started if there was none;
    /// null when a read would not wait.</summary>
    public Task? InputPending()
    {
        if (_pos < _len || _ended) return null;
        _pendingRead ??= StartRead();
        SendHeldBeforeWaiting();
        return _pendingRead.IsCompleted ? null : _pendingRead;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_pos == _len)
        {
            if (_ended) return 0;
            _pendingRead ??= StartRead();
            SendHeldBeforeWaiting();
            _registry.Wait(_pendingRead, TimeoutMs);
            int got = _pendingRead.Result;
            _pendingRead = null;
            if (got <= 0)
            {
                _ended = true;
                return 0;
            }
            _pos = 0;
            _len = got;
        }
        int n = Math.Min(count, _len - _pos);
        Buffer.BlockCopy(_buffer, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_writeGate)
        {
            if (count >= _held.Length)
            {
                SendHeld();
                if (count == 0) return;
                // A copy: a write that runs out of time goes on with these bytes.
                var own = new byte[count];
                Buffer.BlockCopy(buffer, offset, own, 0, count);
                Send(own, count);
                return;
            }
            if (count > _held.Length - _heldCount) SendHeld();
            Buffer.BlockCopy(buffer, offset, _held, _heldCount, count);
            _heldCount += count;
            if (_heldCount == _held.Length) SendHeld();
        }
    }

    public override void WriteByte(byte value)
    {
        lock (_writeGate)
        {
            if (_held.Length == 0)
            {
                Send(new[] { value }, 1);
                return;
            }
            _held[_heldCount++] = value;
            if (_heldCount == _held.Length) SendHeld();
        }
    }

    private void SendHeld()
    {
        if (_heldCount == 0) return;
        int count = _heldCount;
        _heldCount = 0;
        Send(_held, count);
    }

    private void Send(byte[] bytes, int count)
    {
        FinishEarlierWrite();
        Task writing = _asynchronous
            ? _inner.WriteAsync(bytes, 0, count)
            : OnItsOwnThread(() =>
            {
                _inner.Write(bytes, 0, count);
                _inner.Flush();
                return count;
            });
        try { _registry.Wait(writing, TimeoutMs, "write"); }
        catch when (!writing.IsCompleted)
        {
            // Out of time or cancelled, it goes on: the next write waits its turn,
            // and these bytes are not to be written over.
            _writeLeftWaiting = writing;
            if (ReferenceEquals(bytes, _held)) _held = new byte[_held.Length];
            throw;
        }
    }

    private void FinishEarlierWrite()
    {
        if (_writeLeftWaiting is null) return;
        _registry.Wait(_writeLeftWaiting, TimeoutMs, "write");
        _writeLeftWaiting = null;
    }

    private readonly object _writeGate = new();

    /// <summary>Writes from a copy on another thread: no query to answer to, and it takes
    /// its turn with what the program writes.</summary>
    public void WriteFromCopy(byte[] buffer, int count)
    {
        lock (_writeGate)
        {
            if (_heldCount > 0)
            {
                _inner.Write(_held, 0, _heldCount);
                _heldCount = 0;
            }
            _inner.Write(buffer, 0, count);
            _inner.Flush();
        }
    }

    /// <summary>Reads for a copy on another thread, which owns the input from now on: what
    /// was already here comes first, then the read left waiting, then the connection.</summary>
    public int ReadForCopy(byte[] buffer)
    {
        if (_pos < _len)
        {
            int held = Math.Min(buffer.Length, _len - _pos);
            Buffer.BlockCopy(_buffer, _pos, buffer, 0, held);
            _pos += held;
            return held;
        }
        if (_ended) return 0;
        if (_pendingRead is not null)
        {
            int got = _pendingRead.GetAwaiter().GetResult();
            _pendingRead = null;
            int n = Math.Min(buffer.Length, Math.Max(got, 0));
            Buffer.BlockCopy(_buffer, 0, buffer, 0, n);
            _pos = n;
            _len = Math.Max(got, 0);
            return n;
        }
        return _inner.Read(buffer, 0, buffer.Length);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_detached) _inner.Dispose();
        base.Dispose(disposing);
    }
}
