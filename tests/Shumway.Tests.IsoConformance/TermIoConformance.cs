using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// ISO 13211-1, §8.14 Term I/O. Covers <c>read/1,2</c> (§8.14.2),
/// <c>read_term/2,3</c> (§8.14.1), <c>write/1,2</c> (§8.14.4),
/// <c>writeq/1,2</c> (§8.14.5), <c>write_canonical/1,2</c>
/// (§8.14.6), and <c>write_term/2,3</c> (§8.14.3). All built on the
/// chunk-140 stream registry so the 1-arg forms honour
/// <c>set_input/1</c> / <c>set_output/1</c>.
/// </summary>
public class TermIoConformance : IDisposable
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    private readonly string _tempPath;

    public TermIoConformance()
    {
        _tempPath = Path.GetTempFileName();
    }

    public void Dispose()
    {
        try { File.Delete(_tempPath); } catch { }
    }

    // ---------- read/2 ----------

    [Fact]
    public void Read2_OnFile_ParsesTerm()
    {
        File.WriteAllText(_tempPath, "foo(1, 2, 3).\n");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S), read(S, T), close(S).");
        Assert.True(sol.Success);
        var t = Assert.IsType<CompoundTerm>(sol["T"]);
        Assert.Equal("foo", t.Functor);
        Assert.Equal(3, t.Args.Length);
    }

    [Fact]
    public void Read2_AtEnd_ReturnsEndOfFile()
    {
        File.WriteAllText(_tempPath, "");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S), read(S, T), close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("end_of_file"), sol["T"]);
    }

    [Fact]
    public void Read1_UsesCurrentInput()
    {
        File.WriteAllText(_tempPath, "marker.\n");
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S), set_input(S), read(T), "
            + "set_input(user_input), close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("marker"), sol["T"]);
    }

    [Fact]
    public void Read2_OnBinaryStream_RaisesPermissionError()
    {
        File.WriteAllBytes(_tempPath, new byte[] { 0x41 });
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', read, S, [type(binary)]), "
            + "catch(read(S, _T), error(permission_error(_, T, _), _), Caught = ok), "
            + "close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("binary_stream"), sol["T"]);
    }

    // ---------- write/1, write/2 ----------

    [Fact]
    public void Write2_RendersTerm()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', write, S), write(S, foo(1, 2)), close(S).").Success);
        Assert.Equal("foo(1, 2)", File.ReadAllText(_tempPath));
    }

    [Fact]
    public void Write1_HonoursCurrentOutput()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', write, S), set_output(S), write(hello), "
            + "set_output(user_output), close(S).").Success);
        Assert.Equal("hello", File.ReadAllText(_tempPath));
    }

    // ---------- writeq/1, writeq/2 ----------

    [Fact]
    public void Writeq2_QuotesNonAlphaAtoms()
    {
        // 'foo bar' has whitespace, so writeq must quote it; write
        // would render it bare.
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', write, S), writeq(S, 'foo bar'), close(S).").Success);
        Assert.Equal("'foo bar'", File.ReadAllText(_tempPath));
    }

    [Fact]
    public void Writeq1_NoQuotingNeededForAlphaAtom()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', write, S), set_output(S), writeq(plain_atom), "
            + "set_output(user_output), close(S).").Success);
        Assert.Equal("plain_atom", File.ReadAllText(_tempPath));
    }

    // ---------- write_canonical/1, write_canonical/2 ----------

    [Fact]
    public void WriteCanonical2_UsesFunctionalForm()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        // 1 + 2 in canonical form is +(1, 2), not "1 + 2".
        Assert.True(e.Query(
            $"open('{path}', write, S), write_canonical(S, 1 + 2), close(S).").Success);
        // Either "+(1, 2)" or "'+'(1, 2)" is valid canonical form —
        // both read back the same term. Pin the functional-vs-operator
        // distinction without baking the renderer's quoting choice in.
        var written = File.ReadAllText(_tempPath);
        Assert.Contains("(1, 2)", written);
        Assert.DoesNotContain(" + ", written);
    }

    [Fact]
    public void Write_UsesOperatorForm_ByDefault()
    {
        // For contrast: write/2 keeps operator syntax. Symbolic infix
        // operators render tight (no surrounding spaces), matching
        // SWI / GNU / SICStus.
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', write, S), write(S, 1 + 2), close(S).").Success);
        Assert.Equal("1+2", File.ReadAllText(_tempPath));
    }

    // ---------- write_term/3 ----------

    [Fact]
    public void WriteTerm3_QuotedOption_QuotesAtoms()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', write, S), write_term(S, 'has space', [quoted(true)]), close(S).").Success);
        Assert.Equal("'has space'", File.ReadAllText(_tempPath));
    }

    [Fact]
    public void WriteTerm3_IgnoreOpsOption_ForcesFunctional()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', write, S), write_term(S, 1 + 2, [ignore_ops(true)]), close(S).").Success);
        Assert.Equal("+(1, 2)", File.ReadAllText(_tempPath));
    }

    [Fact]
    public void WriteTerm3_NumbervarsOption_RendersVARCompounds()
    {
        // '$VAR'(0) renders as A, '$VAR'(1) as B, etc., when
        // numbervars(true) is on.
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"open('{path}', write, S), write_term(S, '$VAR'(0), [numbervars(true)]), close(S).").Success);
        Assert.Equal("A", File.ReadAllText(_tempPath));
    }

    // ---------- write/2 on binary stream — permission error ----------

    [Fact]
    public void Write2_OnBinaryStream_RaisesPermissionError()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        var sol = e.Query(
            $"open('{path}', write, S, [type(binary)]), "
            + "catch(write(S, hello), error(permission_error(_, T, _), _), Caught = ok), "
            + "close(S).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("binary_stream"), sol["T"]);
    }

    // ---------- Round-trip: write then read ----------

    [Fact]
    public void WriteThenRead_PreservesTerm()
    {
        var path = _tempPath.Replace("\\", "\\\\");
        var e = new PrologEngine();
        // writeq + nl, read back.
        Assert.True(e.Query(
            $"open('{path}', write, W), writeq(W, foo(1, 'bar baz', [a,b,c])), "
            + "write(W, '.'), nl(W), close(W).").Success);

        var sol = e.Query(
            $"open('{path}', read, R), read(R, T), close(R).");
        Assert.True(sol.Success);
        var t = Assert.IsType<CompoundTerm>(sol["T"]);
        Assert.Equal("foo", t.Functor);
        Assert.Equal(3, t.Args.Length);
        Assert.Equal(Int(1), t.Args[0]);
        Assert.Equal(Atom("bar baz"), t.Args[1]);
    }
}
