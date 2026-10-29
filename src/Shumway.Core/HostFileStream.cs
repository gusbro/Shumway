using System.IO;

namespace Shumway.Core;

/// <summary>
/// The bytes of a file a Prolog stream reads or writes. A failure the host
/// reports after the file opened (a device that refuses the write, a full
/// disk, a locked region) arrives as <c>system_error(Message)</c>, which
/// <c>catch/3</c> catches: a .NET exception would leave the query. The
/// writer's buffers meet the host only when they empty, so the error surfaces
/// at <c>flush_output</c>, at <c>close</c>, or at a write that fills them.
/// </summary>
public sealed class HostFileStream : Stream
{
    private readonly FileStream _file;

    public HostFileStream(FileStream file) => _file = file;

    /// <summary>Opens <paramref name="path"/>; a failure to open is the
    /// caller's to map, as a permission or existence error.</summary>
    public static HostFileStream Open(string path, FileMode mode, FileAccess access, FileShare share)
        => new(new FileStream(path, mode, access, share));

    public override bool CanRead => _file.CanRead;
    public override bool CanSeek => _file.CanSeek;
    public override bool CanWrite => _file.CanWrite;

    public override long Length
    {
        get
        {
            try { return _file.Length; }
            catch (IOException ex) { throw Failed(ex); }
            catch (UnauthorizedAccessException ex) { throw Failed(ex); }
        }
    }

    public override long Position
    {
        get
        {
            try { return _file.Position; }
            catch (IOException ex) { throw Failed(ex); }
            catch (UnauthorizedAccessException ex) { throw Failed(ex); }
        }
        set
        {
            try { _file.Position = value; }
            catch (IOException ex) { throw Failed(ex); }
            catch (UnauthorizedAccessException ex) { throw Failed(ex); }
        }
    }

    public override void Flush()
    {
        try { _file.Flush(); }
        catch (IOException ex) { throw Failed(ex); }
        catch (UnauthorizedAccessException ex) { throw Failed(ex); }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try { return _file.Read(buffer, offset, count); }
        catch (IOException ex) { throw Failed(ex); }
        catch (UnauthorizedAccessException ex) { throw Failed(ex); }
    }

    public override int ReadByte()
    {
        try { return _file.ReadByte(); }
        catch (IOException ex) { throw Failed(ex); }
        catch (UnauthorizedAccessException ex) { throw Failed(ex); }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        try { _file.Write(buffer, offset, count); }
        catch (IOException ex) { throw Failed(ex); }
        catch (UnauthorizedAccessException ex) { throw Failed(ex); }
    }

    public override void WriteByte(byte value)
    {
        try { _file.WriteByte(value); }
        catch (IOException ex) { throw Failed(ex); }
        catch (UnauthorizedAccessException ex) { throw Failed(ex); }
    }

#if !NETFRAMEWORK
    // The readers and writers call these: the base versions copy through a
    // rented array.
    public override int Read(Span<byte> buffer)
    {
        try { return _file.Read(buffer); }
        catch (IOException ex) { throw Failed(ex); }
        catch (UnauthorizedAccessException ex) { throw Failed(ex); }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        try { _file.Write(buffer); }
        catch (IOException ex) { throw Failed(ex); }
        catch (UnauthorizedAccessException ex) { throw Failed(ex); }
    }
#endif

    public override long Seek(long offset, SeekOrigin origin)
    {
        try { return _file.Seek(offset, origin); }
        catch (IOException ex) { throw Failed(ex); }
        catch (UnauthorizedAccessException ex) { throw Failed(ex); }
    }

    public override void SetLength(long value)
    {
        try { _file.SetLength(value); }
        catch (IOException ex) { throw Failed(ex); }
        catch (UnauthorizedAccessException ex) { throw Failed(ex); }
    }

    // Disposing writes what the file still buffers; the handle is released
    // whether that write fails or not.
    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing) _file.Dispose();
        }
        catch (IOException ex) { throw Failed(ex); }
        catch (UnauthorizedAccessException ex) { throw Failed(ex); }
        finally { base.Dispose(disposing); }
    }

    private static PrologRuntimeException Failed(Exception ex) => new("system_error", ex.Message);
}
