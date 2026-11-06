using System;
using System.IO;
using System.Threading.Tasks;

namespace Shumway.Core;

/// <summary>
/// A stream over a pipe or a socket, where a read can wait for as long as the other end
/// takes. It waits in steps through <see cref="StreamRegistry.Wait"/>, so the running query
/// can be cancelled or run out of time. A read left waiting stays pending: what it gets
/// goes to the next read, and no bytes are lost.
/// </summary>
public sealed class LiveStream : Stream
{
    private readonly Stream _inner;
    private readonly StreamRegistry _registry;
    private readonly byte[] _buffer = new byte[4096];
    private int _pos;
    private int _len;
    private Task<int>? _pendingRead;
    // Not asked again once ended: on .NET Framework a PipeStream fails the ReadAsync after
    // the one that found its other end gone.
    private bool _ended;
    private bool _detached;

    public LiveStream(Stream inner, StreamRegistry registry)
    {
        _inner = inner;
        _registry = registry;
    }

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

    public override void Flush() => _registry.Wait(_inner.FlushAsync(), TimeoutMs, "write");

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
        _pendingRead ??= _inner.ReadAsync(_buffer, 0, _buffer.Length);
        return _pendingRead.IsCompleted ? null : _pendingRead;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_pos == _len)
        {
            if (_ended) return 0;
            _pendingRead ??= _inner.ReadAsync(_buffer, 0, _buffer.Length);
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
            _registry.Wait(_inner.WriteAsync(buffer, offset, count), TimeoutMs, "write");
    }

    private readonly object _writeGate = new();

    /// <summary>Writes from a copy on another thread: no query to answer to, and it takes
    /// its turn with what the program writes.</summary>
    public void WriteFromCopy(byte[] buffer, int count)
    {
        lock (_writeGate)
        {
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
