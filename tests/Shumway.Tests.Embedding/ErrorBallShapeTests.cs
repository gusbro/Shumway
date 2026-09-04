using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>An error a program cannot catch, or catches only by matching a
/// shape nobody would write, is not an error report. Two families are pinned
/// here.
///
/// <para>The first is the ball's SHAPE: several loading predicates built the
/// whole formal as one string, so the ball carried the ATOM
/// <c>'existence_error(source_sink, \'f\')'</c> where a catcher expects the
/// compound <c>existence_error(source_sink, f)</c>. Every such catcher
/// silently missed.</para>
///
/// <para>The second is what escapes at all: opening a directory let a host
/// exception through the engine, past <c>catch/3</c>, and out of the
/// query.</para></summary>
public sealed class ErrorBallShapeTests
{
    private static PrologEngine Engine() => new();

    /// <summary>True when Goal raises a ball the pattern matches.</summary>
    private static bool Raises(PrologEngine e, string goal, string pattern)
        => e.Query($"catch(({goal}), {pattern}, true).").Success;

    [Theory]
    // Every loading predicate that reports a missing file reports it the same
    // way, and names the file it was asked for.
    [InlineData("consult(no_such_file_zz)")]
    [InlineData("ensure_loaded(no_such_file_zz)")]
    [InlineData("reconsult(no_such_file_zz)")]
    [InlineData("see(no_such_file_zz)")]
    [InlineData("restore(no_such_file_zz)")]
    [InlineData("restore_state(no_such_file_zz)")]
    public void AMissingFileIsAnExistenceError(string goal)
    {
        var e = Engine();
        Assert.True(Raises(e, goal,
            "error(existence_error(source_sink, no_such_file_zz), _)"));
    }

    [Fact]
    public void TheFormalIsACompoundAndNotAnAtomSpellingOne()
    {
        // The defect stated directly: the formal used to be an atom whose NAME
        // read like the compound, so functor/3 saw arity 0 and every catcher
        // written against the ISO shape missed.
        var e = Engine();
        Assert.True(e.Query(
            "catch(consult(no_such_file_zz), error(F, _), true), "
            + "functor(F, existence_error, 2), arg(1, F, source_sink), "
            + "arg(2, F, no_such_file_zz).").Success);
    }

    [Fact]
    public void ALibraryThatDoesNotExistSaysSo()
    {
        var e = Engine();
        Assert.True(Raises(e, "use_module(library(no_such_library_zz))",
            "error(existence_error(library, no_such_library_zz), _)"));
    }

    [Theory]
    // A file argument of the wrong type is a type error naming the culprit,
    // not an atom that spells one and leaves the culprit unbound.
    [InlineData("consult(f(x))", "f(x)")]
    [InlineData("ensure_loaded(1)", "1")]
    [InlineData("reconsult(1)", "1")]
    public void ANonAtomFileNamesTheCulprit(string goal, string culprit)
    {
        var e = Engine();
        Assert.True(Raises(e, goal, $"error(type_error(atom, {culprit}), _)"));
    }

    [Fact]
    public void OpeningADirectoryIsAPermissionErrorAndIsCatchable()
    {
        // The host refuses to open a directory as a file. That refusal used to
        // travel as a host exception: it crossed catch/3 untouched and came
        // out of the query, which no Prolog program can defend against.
        string dir = System.IO.Path.GetTempPath().TrimEnd('\\', '/').Replace('\\', '/');
        var e = Engine();
        Assert.True(e.Query(
            $"catch(open('{dir}', write, _), "
            + "error(permission_error(open, source_sink, _), _), true).").Success);
        Assert.True(e.Query(
            $"catch(open('{dir}', read, _, []), "
            + "error(permission_error(open, source_sink, _), _), true).").Success);
    }

    [Fact]
    public void AMissingFileIsStillDistinctFromOneThatCannotBeOpened()
    {
        // The two answers must not collapse into each other: a file that is
        // not there and a file the host will not give us are different fixes.
        var e = Engine();
        Assert.True(e.Query(
            "catch(open(no_such_file_zz, read, _), "
            + "error(existence_error(source_sink, _), _), true).").Success);
    }
}
