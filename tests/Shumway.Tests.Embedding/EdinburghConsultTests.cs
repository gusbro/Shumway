using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The Edinburgh loading shapes — not ISO (13211-1 defines no load
/// syntax) but universal practice: `?- [file1, file2].` consults each element,
/// `?- [user].` reads clauses interactively from current input until
/// end_of_file, and consult/1 itself accepts the same list / user specs.</summary>
public sealed class EdinburghConsultTests
{
    private static string WriteTemp(string name, string source)
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway-edin-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, source);
        return path.Replace('\\', '/');
    }

    [Fact]
    public void ListGoal_ConsultsEachElement()
    {
        string f1 = WriteTemp("f1.pl", "p1(a).\n");
        string f2 = WriteTemp("f2.pl", "p2(b).\n");
        var e = new PrologEngine();
        Assert.True(e.Query($"['{f1}', '{f2}'].").Success);
        Assert.True(e.Query("p1(a), p2(b).").Success);
    }

    [Fact]
    public void EmptyList_IsTrue()
    {
        Assert.True(new PrologEngine().Query("[].").Success);
    }

    [Fact]
    public void ExtensionlessElement_ResolvesToPl()
    {
        string f = WriteTemp("noext.pl", "q3(c).\n");
        var e = new PrologEngine();
        Assert.True(e.Query($"['{f[..^3]}'].").Success);
        Assert.True(e.Query("q3(c).").Success);
    }

    [Fact]
    public void ConsultUser_ReadsUntilEndOfFileLine()
    {
        // No "|: " in the transcript on purpose: the prompt belongs to the
        // INTERACTIVE input source (the REPL's user_input reader prints it per
        // refilled line, as for read/1) — writing it here too doubled it, and
        // a piped script correctly sees none.
        var output = new StringWriter();
        var e = new PrologEngine
        {
            In = new StringReader("gato(tom).\n\ngato(felix).\nend_of_file.\n"),
            Out = output,
        };
        Assert.True(e.Query("[user].").Success);
        Assert.True(e.Query("gato(tom), gato(felix).").Success);
        Assert.DoesNotContain("|: ", output.ToString());
    }

    [Fact]
    public void ConsultUser_CtrlD_EndsTheEntry()
    {
        // A cooked Windows console delivers Ctrl-D as an in-line \x04.
        var e = new PrologEngine
        {
            In = new StringReader("pez(blub).\n\u0004\nno_llega(x).\n"),
            Out = new StringWriter(),
        };
        Assert.True(e.Query("[user].").Success);
        Assert.True(e.Query("pez(blub).").Success);
        Assert.False(e.Query("catch(no_llega(_), _, fail).").Success);
    }

    [Fact]
    public void ConsultUser_EndsAtEndOfInput()
    {
        var e = new PrologEngine
        {
            In = new StringReader("perro(rex)."),   // no trailing newline, no end_of_file
            Out = new StringWriter(),
        };
        Assert.True(e.Query("consult(user).").Success);
        Assert.True(e.Query("perro(rex).").Success);
    }

    [Fact]
    public void ConsultOfAList_TakesTheSameSpecs()
    {
        string f = WriteTemp("f3.pl", "p4(d).\n");
        var e = new PrologEngine();
        Assert.True(e.Query($"consult(['{f}']).").Success);
        Assert.True(e.Query("p4(d).").Success);
    }

    [Fact]
    public void SyntaxError_IsACatchableIsoBall_AndNothingLoads()
    {
        // The GNU-documented model: on a failed compilation a message is
        // displayed and NOTHING is loaded. The message crosses into Prolog as
        // a proper syntax_error ball (it used to escape as the raw .NET parse
        // exception, uncatchable from Prolog).
        string f = WriteTemp("bad.pl", "buena(a).\nrota(b :- .\nbuena2(c).\n");
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"catch(consult('{f}'), error(syntax_error(_), _), true).").Success);
        Assert.False(e.Query("catch(buena(_), _, fail).").Success);

        var e2 = new PrologEngine
        {
            In = new StringReader("ok(1).\nrota(b :- .\nend_of_file.\n"),
            Out = new StringWriter(),
        };
        Assert.True(e2.Query(
            "catch([user], error(syntax_error(_), _), true).").Success);
        Assert.False(e2.Query("catch(ok(_), _, fail).").Success);
    }

    [Fact]
    public void VariableSpec_IsAnInstantiationError()
    {
        Assert.True(new PrologEngine().Query(
            "catch(consult([_]), error(instantiation_error, _), true).").Success);
    }
}
