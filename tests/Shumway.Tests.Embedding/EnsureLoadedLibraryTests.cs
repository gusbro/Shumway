using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary><c>ensure_loaded(library(Name))</c> loads a library as
/// <c>use_module/1</c> does, as on the reference systems: the goal form
/// imports into <c>user</c>, the directive where it stands, and separate
/// compilation records the dependency. It raised <c>type_error(atom, ...)</c>,
/// so a program that loads clpfd this way (Logtalk's constraint examples
/// among them) never had the library. A file name is unchanged.</summary>
public sealed class EnsureLoadedLibraryTests
{
    private static bool Holds(PrologEngine e, string goal) => e.Query(goal).Success;

    [Fact]
    public void TheGoalForm_LoadsTheLibraryIntoUser()
    {
        var e = new PrologEngine();
        Assert.True(Holds(e, "ensure_loaded(library(clpfd))."));
        Assert.True(Holds(e, "X #= 1 + 2, X == 3."));
        Assert.True(Holds(e, "predicate_property(label(_), imported_from(clpfd))."));
        // Loaded once: asking again succeeds and changes nothing.
        Assert.True(Holds(e, "ensure_loaded(library(clpfd))."));
        Assert.True(Holds(e, "X #= 2 * 3, X == 6."));
    }

    [Fact]
    public void TheDirective_LoadsTheLibraryForTheRestOfTheFile()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- ensure_loaded(library(clpfd)).
            p(X) :- X #= 1 + 2.
            """);
        Assert.True(Holds(e, "p(X), X == 3."));
    }

    [Fact]
    public void InAModule_TheImportsAreTheModulesOwn()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(elm, [q/1]).
            :- ensure_loaded(library(reif)).
            q(R) :- if_(1 = 1, R = yes, R = no).
            """);
        Assert.True(Holds(e, "q(R), R == yes."));
        // The directive imported into elm, not into user.
        Assert.True(Holds(e,
            "catch(if_(1 = 1, _, _), error(existence_error(procedure, _), _), true)."));
    }

    [Fact]
    public void AFileName_IsStillAFile()
    {
        var e = new PrologEngine();
        Assert.True(Holds(e,
            "catch(ensure_loaded(no_such_file_for_ensure_loaded), "
            + "error(existence_error(source_sink, no_such_file_for_ensure_loaded), _), true)."));
    }

    [Fact]
    public void SeparateCompilation_RecordsTheLibraryDependency()
    {
        var obj = ShmoCompiler.CompileSource(
            ":- ensure_loaded(library(reif)).\np(R) :- if_(1 = 1, R = yes, R = no).\n", "elprog");
        Assert.Contains(obj.LibraryDeps, d => d.LibName == "reif");
    }
}
