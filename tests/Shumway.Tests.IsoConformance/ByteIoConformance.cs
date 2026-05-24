using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// ISO 13211-1, §8.13 Byte I/O.
///
/// Covers <c>get_byte/1,2</c> (§8.13.1), <c>peek_byte/1,2</c>
/// (§8.13.2) and <c>put_byte/1,2</c> (§8.13.3). Bytes are integers
/// in [0, 255]; reading EOF returns -1. The stream must be opened
/// with <c>type(binary)</c> — chunk 142 added real binary-mode
/// streams (<see cref="Shumway.Core.StreamHandle.BinaryStream"/>),
/// since the chunk-140 text-only wrappers couldn't preserve raw
/// bytes.
/// </summary>
public class ByteIoConformance : IDisposable
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    private readonly string _tempPath;

    public ByteIoConformance()
    {
        _tempPath = Path.GetTempFileName();
    }

    public void Dispose()
    {
        try { File.Delete(_tempPath); } catch { }
    }

    // ---------- get_byte/2 ----------

    [Fact]
    public void GetByte2_ReadsRawByte()
    {
        File.WriteAllBytes(_tempPath, new byte[] { 0x41 });  // 'A'
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S, [type(binary)]), get_byte(S, B), close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Int(0x41), sol["B"]);
    }

    [Fact]
    public void GetByte2_AtEnd_ReturnsMinusOne()
    {
        File.WriteAllBytes(_tempPath, Array.Empty<byte>());
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S, [type(binary)]), get_byte(S, B), close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Int(-1), sol["B"]);
    }

    [Fact]
    public void GetByte2_ConsumesAcrossCalls()
    {
        File.WriteAllBytes(_tempPath, new byte[] { 0xAA, 0xBB });
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S, [type(binary)]), "
            + "get_byte(S, X), get_byte(S, Y), close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Int(0xAA), sol["X"]);
        Assert.Equal(Int(0xBB), sol["Y"]);
    }

    [Fact]
    public void GetByte2_OnTextStream_RaisesPermissionError()
    {
        File.WriteAllText(_tempPath, "x");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S), "
            + "catch(get_byte(S, _B), error(permission_error(_, T, _), _), Caught = ok), "
            + "close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("text_stream"), sol["T"]);
        Assert.Equal(Atom("ok"), sol["Caught"]);
    }

    // ---------- peek_byte/2 ----------

    [Fact]
    public void PeekByte2_DoesNotConsume()
    {
        File.WriteAllBytes(_tempPath, new byte[] { 0x42 });
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S, [type(binary)]), "
            + "peek_byte(S, P), get_byte(S, G), close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Int(0x42), sol["P"]);
        Assert.Equal(Int(0x42), sol["G"]);
    }

    // ---------- put_byte/2 ----------

    [Fact]
    public void PutByte2_WritesRawBytes()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', write, S, [type(binary)]), "
            + "put_byte(S, 0), put_byte(S, 255), put_byte(S, 127), close(S).").Success);
        Assert.Equal(new byte[] { 0, 255, 127 }, File.ReadAllBytes(_tempPath));
    }

    [Fact]
    public void PutByte2_OutOfRange_RaisesTypeError()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', write, S, [type(binary)]), "
            + "catch(put_byte(S, 256), error(type_error(T, _), _), Caught = ok), "
            + "close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("byte"), sol["T"]);
        Assert.Equal(Atom("ok"), sol["Caught"]);
    }

    [Fact]
    public void PutByte2_NegativeByte_RaisesTypeError()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', write, S, [type(binary)]), "
            + "catch(put_byte(S, -1), error(type_error(T, _), _), Caught = ok), "
            + "close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("byte"), sol["T"]);
    }

    [Fact]
    public void PutByte2_VarByte_RaisesInstantiationError()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', write, S, [type(binary)]), "
            + "catch(put_byte(S, _B), error(E, _), Caught = ok), "
            + "close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void PutByte2_OnTextStream_RaisesPermissionError()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', write, S), "
            + "catch(put_byte(S, 65), error(permission_error(_, T, _), _), Caught = ok), "
            + "close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("text_stream"), sol["T"]);
    }

    // ---------- Cross-mode permission errors ----------

    [Fact]
    public void GetChar2_OnBinaryStream_RaisesPermissionError()
    {
        File.WriteAllBytes(_tempPath, new byte[] { 0x41 });
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S, [type(binary)]), "
            + "catch(get_char(S, _C), error(permission_error(_, T, _), _), Caught = ok), "
            + "close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("binary_stream"), sol["T"]);
    }

    [Fact]
    public void Write2_OnBinaryStream_RaisesPermissionError()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        // write/2 should fail on a binary stream — it tries to render
        // a term as text.
        var sol = e.Query(
            $"open('{path}', write, S, [type(binary)]), "
            + "catch(write(S, hello), error(permission_error(_, _, _), _), Caught = ok), "
            + "close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("ok"), sol["Caught"]);
    }

    // ---------- Round-trip ----------

    [Fact]
    public void Bytes_Roundtrip_PreservesRawValues()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        // Write three bytes, then read them back.
        Assert.True(e.Query(
            $"open('{path}', write, S, [type(binary)]), "
            + "put_byte(S, 1), put_byte(S, 254), put_byte(S, 128), close(S).").Success);

        var sol = e.Query(
            $"open('{path}', read, S, [type(binary)]), "
            + "get_byte(S, A), get_byte(S, B), get_byte(S, C), close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Int(1), sol["A"]);
        Assert.Equal(Int(254), sol["B"]);
        Assert.Equal(Int(128), sol["C"]);
    }
}
