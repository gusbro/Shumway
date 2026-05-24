using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// ISO 13211-1, §8.12 Character I/O.
///
/// Covers <c>get_char/1,2</c> (§8.12.1), <c>peek_char/1,2</c>
/// (§8.12.2), <c>put_char/1,2</c> (§8.12.3), <c>get_code/1,2</c>
/// (§8.12.4), <c>peek_code/1,2</c> (§8.12.5), <c>put_code/1,2</c>
/// (§8.12.6). All built on the chunk-140 stream registry so the
/// 1-arg forms honour the current input/output cursors set by
/// <c>set_input/1</c> and <c>set_output/1</c>.
/// </summary>
public class CharacterIoConformance : IDisposable
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    private readonly string _tempPath;

    public CharacterIoConformance()
    {
        _tempPath = Path.GetTempFileName();
    }

    public void Dispose()
    {
        try { File.Delete(_tempPath); } catch { }
    }

    // ---------- get_char / peek_char on a file stream ----------

    [Fact]
    public void GetChar2_ReadsFirstCharacter()
    {
        File.WriteAllText(_tempPath, "abc");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S), get_char(S, C), close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("a"), sol["C"]);
    }

    [Fact]
    public void GetChar2_ConsumesAcrossCalls()
    {
        File.WriteAllText(_tempPath, "xy");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S), get_char(S, A), get_char(S, B), close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("x"), sol["A"]);
        Assert.Equal(Atom("y"), sol["B"]);
    }

    [Fact]
    public void PeekChar2_DoesNotConsume()
    {
        File.WriteAllText(_tempPath, "z");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S), peek_char(S, P), get_char(S, G), close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("z"), sol["P"]);
        Assert.Equal(Atom("z"), sol["G"]);
    }

    [Fact]
    public void GetChar2_AtEnd_ReturnsEndOfFile()
    {
        File.WriteAllText(_tempPath, "");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S), get_char(S, C), close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("end_of_file"), sol["C"]);
    }

    // ---------- get_char/1, peek_char/1 honour set_input ----------

    [Fact]
    public void GetChar1_UsesCurrentInput()
    {
        File.WriteAllText(_tempPath, "k");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S), set_input(S), get_char(C), "
            + "set_input(user_input), close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("k"), sol["C"]);
    }

    // ---------- put_char ----------

    [Fact]
    public void PutChar2_WritesToStream()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', write, S), put_char(S, h), put_char(S, i), close(S).").Success);
        Assert.Equal("hi", File.ReadAllText(_tempPath));
    }

    [Fact]
    public void PutChar2_NonChar_RaisesTypeError()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', write, S), "
            + "catch(put_char(S, abc), error(type_error(T, _), _), Caught = ok), "
            + "close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("character"), sol["T"]);
    }

    [Fact]
    public void PutChar2_Unbound_RaisesInstantiationError()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', write, S), "
            + "catch(put_char(S, _C), error(E, _), Caught = ok), "
            + "close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void PutChar1_HonoursCurrentOutput()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', write, S), set_output(S), "
            + "put_char(a), put_char(b), set_output(user_output), close(S).").Success);
        Assert.Equal("ab", File.ReadAllText(_tempPath));
    }

    // ---------- get_code / peek_code / put_code ----------

    [Fact]
    public void GetCode2_ReadsAsInteger()
    {
        File.WriteAllText(_tempPath, "A");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S), get_code(S, C), close(S).");
        Assert.True(sol.Success);
        // 'A' is 65.
        Assert.Equal(Int(65), sol["C"]);
    }

    [Fact]
    public void GetCode2_AtEnd_ReturnsMinusOne()
    {
        File.WriteAllText(_tempPath, "");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S), get_code(S, C), close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Int(-1), sol["C"]);
    }

    [Fact]
    public void PeekCode2_DoesNotConsume()
    {
        File.WriteAllText(_tempPath, "X");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S), peek_code(S, P), get_code(S, G), close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Int(88), sol["P"]);
        Assert.Equal(Int(88), sol["G"]);
    }

    [Fact]
    public void PutCode2_WritesIntegerAsChar()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        // 65 = 'A', 66 = 'B'.
        Assert.True(e.Query(
            $"open('{path}', write, S), put_code(S, 65), put_code(S, 66), close(S).").Success);
        Assert.Equal("AB", File.ReadAllText(_tempPath));
    }

    [Fact]
    public void PutCode2_OutOfRange_RaisesRepresentationError()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', write, S), "
            + "catch(put_code(S, 16777216), "
            + "error(representation_error(F), _), Caught = ok), "
            + "close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("character_code"), sol["F"]);
    }

    [Fact]
    public void PutCode2_NonInteger_RaisesTypeError()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', write, S), "
            + "catch(put_code(S, foo), error(type_error(T, _), _), Caught = ok), "
            + "close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("integer"), sol["T"]);
    }

    // ---------- nl/0 honours current_output ----------

    [Fact]
    public void Nl0_HonoursCurrentOutput()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', write, S), set_output(S), "
            + "put_char(x), nl, put_char(y), "
            + "set_output(user_output), close(S).").Success);
        var content = File.ReadAllText(_tempPath);
        // Two chars plus a newline of some shape (\r\n on Windows,
        // \n elsewhere). Pin: starts with 'x', ends with 'y', has a
        // newline in between.
        Assert.StartsWith("x", content);
        Assert.EndsWith("y", content);
        Assert.Contains("\n", content);
    }
}
