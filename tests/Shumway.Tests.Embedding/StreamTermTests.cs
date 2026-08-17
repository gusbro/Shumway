using System;
using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The stream-term is the ordinary ground compound
/// <c>'$stream'(Id)</c> (GNU Prolog's shape), not an opaque handle. That is
/// what makes a stream survive everything a term survives — copy_term,
/// findall, and above all assertz followed by a later call/retract, ACROSS
/// queries. A foreign-cell stream-term did not: the clause compiler had no
/// representation for the payload, so `assertz(s(S)), s(X)` handed back a
/// bare `'$foreign'(0)` compound while `retract` still yielded a live
/// stream — two paths disagreeing, silently.</summary>
public sealed class StreamTermTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(),
            "shumway_streamterm_" + Guid.NewGuid().ToString("N") + ".txt");

    private static string Esc(string p) => p.Replace("\\", "\\\\");

    [Fact]
    public void StreamTerm_IsAGroundCompound()
    {
        string f = TempPath();
        try
        {
            var e = new PrologEngine();
            var sol = e.Query(
                $"open('{Esc(f)}', write, S), ground(S), compound(S), "
                + "S = '$stream'(Id), integer(Id), close(S).");
            Assert.True(sol.Success);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void AssertedStream_IsUsableFromACallAndFromRetract()
    {
        string f = TempPath();
        try
        {
            var e = new PrologEngine();
            // Both retrieval paths — the compiled dynamic-predicate call and
            // retract/1 — must hand back the SAME usable stream.
            e.ConsultString(
                ":- dynamic(st/1).\n"
                + $"go :- open('{Esc(f)}', write, S), assertz(st(S)),\n"
                + "  st(S1), write(S1, viacall), nl(S1),\n"
                + "  retract(st(S2)), write(S2, viaretract), nl(S2),\n"
                + "  S == S1, S == S2, close(S2).\n");
            Assert.True(e.Query("go.").Success);
            Assert.Equal("viacall\nviaretract\n",
                File.ReadAllText(f).Replace("\r\n", "\n"));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void AssertedStream_SurvivesAcrossQueries()
    {
        // The point of the '$stream'(Id) design: the registry lives on the
        // ENGINE, so an id asserted in one query still names the stream in
        // the next. A per-activation payload table could not do this.
        string f = TempPath();
        try
        {
            var e = new PrologEngine();
            e.ConsultString(":- dynamic(saved/1).");
            Assert.True(e.Query($"open('{Esc(f)}', write, S), assertz(saved(S)).").Success);
            Assert.True(e.Query("saved(S), write(S, across), nl(S).").Success);
            Assert.True(e.Query("saved(S), close(S).").Success);
            Assert.Equal("across\n", File.ReadAllText(f).Replace("\r\n", "\n"));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void CopyTermAndFindall_PreserveTheStream()
    {
        string f = TempPath();
        try
        {
            var e = new PrologEngine();
            Assert.True(e.Query(
                $"open('{Esc(f)}', write, S), copy_term(S, C), S == C, "
                + "findall(X, member(X, [S]), [G]), S == G, "
                + "write(C, viacopy), write(G, viafindall), close(S).").Success);
            Assert.Equal("viacopyviafindall", File.ReadAllText(f));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void ClosedStream_TermRaisesExistenceError()
    {
        string f = TempPath();
        try
        {
            var e = new PrologEngine();
            var sol = e.Query(
                $"open('{Esc(f)}', write, S), close(S), "
                + "catch(write(S, x), error(E, _), true), E = existence_error(stream, _).");
            Assert.True(sol.Success);
        }
        finally { File.Delete(f); }
    }

    // The error contract, verified byte-for-byte against GNU Prolog 1.5 and
    // SWI 10 (both agree): a well-formed stream-term naming no open stream is
    // existence_error(stream, Culprit); anything that is not a stream-term or
    // alias at all is domain_error(stream_or_alias, Culprit) — `stream_or_alias`
    // names a DOMAIN, not a type.
    [Theory]
    [InlineData("'$stream'(999999)", "existence_error")]
    [InlineData("'$stream'(foo)", "domain_error")]
    [InlineData("'$stream'(1,2)", "domain_error")]
    [InlineData("no_such_alias", "existence_error")]
    [InlineData("42", "domain_error")]
    [InlineData("f(x)", "domain_error")]
    public void BogusStreamArg_RaisesTheSameErrorAsGnuAndSwi(string arg, string kind)
    {
        var e = new PrologEngine();
        var sol = e.Query(
            $"catch(write({arg}, x), error(E, _), true), functor(E, K, _).");
        Assert.True(sol.Success);
        Assert.Equal(kind, ((AtomTerm)sol["K"]!).Name);
    }

    [Fact]
    public void IsStream_TracksTheRegistry()
    {
        string f = TempPath();
        try
        {
            var e = new PrologEngine();
            Assert.True(e.Query(
                $"open('{Esc(f)}', write, S), is_stream(S), close(S), "
                + "\\+ is_stream(S).").Success);
            Assert.True(e.Query("is_stream(user_output).").Success);
            Assert.False(e.Query("is_stream('$stream'(999999)).").Success);
            Assert.False(e.Query("is_stream(foo).").Success);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void CurrentInputOutputAndEnumeration_UseTheSameShape()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("current_input(S), S = '$stream'(_).").Success);
        Assert.True(e.Query("current_output(S), S = '$stream'(_).").Success);
        // stream_property/2 and current_stream/3 enumerate the same term.
        Assert.True(e.Query(
            "current_output(S), stream_property(S, mode(_)).").Success);
        Assert.True(e.Query(
            "current_output(S), set_output(S), current_output(S2), S == S2.").Success);
    }
}
