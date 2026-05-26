using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// <c>absolute_file_name/2</c> and <c>working_directory/2</c> —
/// the minimum file-path stdlib surface Blint.pl (and most ports
/// of SWI / SICStus programs) reach for.
/// </summary>
public class AbsoluteFileNameTests
{
    [Fact]
    public void AbsoluteFileName_AlreadyAbsolute_RoundTrips()
    {
        var e = new PrologEngine();
        // Use a vanilla relative path here so we don't have to worry
        // about Windows-vs-Unix path escaping for the parser. The
        // round-trip-when-already-absolute behaviour is exercised by
        // a second call that feeds the result back in.
        var sol = e.Query("absolute_file_name('relative.pl', A), absolute_file_name(A, B).");
        Assert.True(sol.Success);
        var a = Assert.IsType<AtomTerm>(sol["A"]);
        var b = Assert.IsType<AtomTerm>(sol["B"]);
        // Calling absolute_file_name on an already-absolute path
        // must yield the same path back (no double prefix).
        Assert.Equal(a.Name, b.Name);
    }

    [Fact]
    public void AbsoluteFileName_RelativePath_Resolved()
    {
        var e = new PrologEngine();
        var sol = e.Query("absolute_file_name('foo.txt', A).");
        Assert.True(sol.Success);
        var a = Assert.IsType<AtomTerm>(sol["A"]);
        Assert.True(Path.IsPathRooted(a.Name),
            $"absolute_file_name should produce an absolute path, got '{a.Name}'");
        Assert.EndsWith("foo.txt", a.Name);
    }

    [Fact]
    public void AbsoluteFileName_Unbound_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(absolute_file_name(_, _), error(instantiation_error, _), true).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void AbsoluteFileName_NonAtom_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(absolute_file_name(42, _), error(type_error(_, _), _), true).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void WorkingDirectory_ReadOnlyForm_Returns_CurrentCwd()
    {
        var e = new PrologEngine();
        var sol = e.Query("working_directory(D, D).");
        Assert.True(sol.Success);
        var d = Assert.IsType<AtomTerm>(sol["D"]);
        Assert.False(string.IsNullOrEmpty(d.Name));
    }

    [Fact]
    public void AbsoluteFileName_BlintShape_BlintsAbsoluteResolution()
    {
        // The exact pattern Blint.pl uses:
        //   absolute_file_name(FileSpec, File), blint(File, _).
        // The point: when FileSpec is a bound atom and File is
        // unbound, the result is the absolute path as an atom.
        var e = new PrologEngine();
        var sol = e.Query("absolute_file_name('./somefile.pl', F).");
        Assert.True(sol.Success);
        var f = Assert.IsType<AtomTerm>(sol["F"]);
        Assert.True(Path.IsPathRooted(f.Name));
    }
}
