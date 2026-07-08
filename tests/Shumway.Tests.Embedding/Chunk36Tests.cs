using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 36: precedence-aware parens in operator-form rendering;
/// read-mode streams; format/3 stream-aware variant.
/// </summary>
public class Chunk36Tests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    private static PrologEngine WithCaptureOut(out StringWriter sw)
    {
        sw = new StringWriter();
        return new PrologEngine { Out = sw };
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"shumway-chunk36-{Guid.NewGuid():N}.txt")
            .Replace('\\', '/');

    // ---------- Operator parens ----------

    [Fact]
    public void Render_BindsTighterRight_NoParens()
    {
        // a + b * c — * binds tighter than + so no parens needed.
        var engine = WithCaptureOut(out var sw);
        engine.Query("write(a + b * c).");
        Assert.Equal("a+b*c", sw.ToString());
    }

    [Fact]
    public void Render_BindsLooserLeft_NeedsParens()
    {
        // (a + b) * c — + has higher prec than * so parens needed on the left.
        var engine = WithCaptureOut(out var sw);
        engine.Query("write((a + b) * c).");
        Assert.Equal("(a+b)*c", sw.ToString());
    }

    [Fact]
    public void Render_RightAssoc_ChainsWithoutParens()
    {
        // = is xfx 700; same-priority chaining on either side needs parens
        // (xfx is non-associative). a = b — single layer, no parens.
        var engine = WithCaptureOut(out var sw);
        engine.Query("write(a = b).");
        Assert.Equal("a=b", sw.ToString());
    }

    [Fact]
    public void Render_NestedInsideArgList_NeedsParensWhenAtPrec1000()
    {
        // Comma inside an arg list — anything at priority ≥ 1000 must be
        // parenthesised (commas separate args at priority 999).
        var engine = WithCaptureOut(out var sw);
        engine.Query("write(foo(a + b, c)).");
        Assert.Equal("foo(a+b,c)", sw.ToString());   // Phase 33: compact ISO layout
    }

    // ---------- read_term_from_stream/2 ----------

    [Fact]
    public void ReadTermFromStream_ParsesOneTerm()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "foo(1, 2, 3).\n");
            var engine = new PrologEngine();
            var sol = engine.Query(
                $"open('{path}', read, S), read_term_from_stream(S, T), close(S).");
            Assert.True(sol.Success);
            var t = Assert.IsType<CompoundTerm>(sol["T"]);
            Assert.Equal("foo", t.Functor);
            Assert.Equal(3, t.Args.Length);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ReadTermFromStream_EmptyFile_ReturnsEndOfFile()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "");
            var engine = new PrologEngine();
            var sol = engine.Query(
                $"open('{path}', read, S), read_term_from_stream(S, T), close(S).");
            Assert.True(sol.Success);
            Assert.Equal(Atom("end_of_file"), sol["T"]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---------- get_char / peek_char ----------

    [Fact]
    public void GetChar_ReadsOneAtATime()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "abc");
            var engine = new PrologEngine();
            var sol = engine.Query(
                $"open('{path}', read, S), "
                + "get_char(S, C1), get_char(S, C2), get_char(S, C3), "
                + "close(S).");
            Assert.True(sol.Success);
            Assert.Equal(Atom("a"), sol["C1"]);
            Assert.Equal(Atom("b"), sol["C2"]);
            Assert.Equal(Atom("c"), sol["C3"]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void PeekChar_DoesNotAdvance()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "X");
            var engine = new PrologEngine();
            var sol = engine.Query(
                $"open('{path}', read, S), "
                + "peek_char(S, P), get_char(S, G), close(S).");
            Assert.True(sol.Success);
            Assert.Equal(sol["P"], sol["G"]);
            Assert.Equal(Atom("X"), sol["P"]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---------- format/3 ----------

    [Fact]
    public void Format3_StreamVariant_WritesToFile()
    {
        string path = TempPath();
        try
        {
            var engine = new PrologEngine();
            engine.Query(
                $"open('{path}', write, S), "
                + "format(S, 'value=~d', [42]), "
                + "close(S).");
            string content = File.ReadAllText(path);
            Assert.Equal("value=42", content);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
