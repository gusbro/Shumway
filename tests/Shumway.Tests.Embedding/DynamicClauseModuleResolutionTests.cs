using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A clause in the dynamic store belongs to a module as much as a
/// static one: a call in it to an imported predicate reaches the import, and
/// the goal of a meta-predicate in it resolves in its module. Dynamic clauses
/// were rewritten without the module's import table, and a multifile clause
/// of a named module, rewritten before the clause pipeline, kept the goal of
/// a findall bare; both raised existence_error.</summary>
public sealed class DynamicClauseModuleResolutionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_dynres_" + Guid.NewGuid().ToString("N"));

    public DynamicClauseModuleResolutionTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "rlib.pl"), @"
:- module(rlib, [r/1]).
r(c).
");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private PrologEngine NewEngine()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.AddLibraryDirectory(_dir);
        return e;
    }

    [Fact]
    public void ADynamicClauseInUserCallsAnImport()
    {
        var e = NewEngine();
        e.ConsultString(@"
:- use_module(library(rlib)).
:- dynamic(d/1).
d(X) :- r(X).
s(X) :- r(X).
");
        Assert.True(e.Query("s(X), X == c.").Success);
        Assert.True(e.Query("d(X), X == c.").Success);
    }

    [Fact]
    public void AnAssertedClauseCallsAnImport()
    {
        var e = NewEngine();
        e.ConsultString(@"
:- use_module(library(rlib)).
:- dynamic(e/1).
");
        // Compiled in place, inside the query that asserts it.
        Assert.True(e.Query("assertz((e(X) :- r(X))), e(Y), Y == c.").Success);
        // Compiled by the next query's setup.
        Assert.True(e.Query("e(Y), Y == c.").Success);
    }

    [Fact]
    public void AClauseAssertedBeforeTheImportSeesIt()
    {
        var e = NewEngine();
        Assert.True(e.Query("assertz((e(X) :- r(X))).").Success);
        Assert.True(e.Query("use_module(library(rlib)).").Success);
        Assert.True(e.Query("e(Y), Y == c.").Success);
    }

    [Fact]
    public void AMultifileClauseOfAModuleCallsItsImport()
    {
        var e = NewEngine();
        e.ConsultString(@"
:- module(mfi, []).
:- use_module(library(rlib)).
:- multifile(user:hook/1).
user:hook(X) :- r(X).
");
        // The global predicate is named user:hook/1 (ADR-038, amendment); its
        // body runs in the module that wrote it.
        Assert.True(e.Query("hook(X), X == c.").Success);
    }

    [Fact]
    public void AMultifileClauseOfAModuleMetaCallsItsLocals()
    {
        var e = NewEngine();
        e.ConsultString(@"
:- module(mfm, []).
:- multifile(user:hook/1).
user:hook(all(L)) :- findall(X, q(X), L).
user:hook(set(S)) :- setof(X-Y, Z^(q(X), w(Y, Z)), S).
user:hook(clo(L)) :- findall(Y, call(w, Y, _), L).
user:hook(rec(R)) :- catch(boom, _, q(R)).
user:hook(neg) :- \+ q(zz).
user:hook(agg(N)) :- aggregate_all(count, q(_), N).
q(b).
w(1, a). w(2, b).
boom :- throw(oops).
");
        // q/1 is the module's own: not callable bare from user.
        Assert.True(e.Query(
            "catch(q(_), error(existence_error(procedure, q/1), _), true).").Success);
        Assert.True(e.Query(
            "findall(H, hook(H), Hs), " +
            "Hs == [all([b]), set([b-1, b-2]), clo([1, 2]), rec(b), neg, agg(1)].").Success);
    }

    [Fact]
    public void AVariableInAMetaGoalIsLeftForTheCallToConvert()
    {
        var e = NewEngine();
        e.ConsultString(@"
:- module(cutm, [t/1]).
t(L) :- X = !, findall(Y, call(((Y = 1 ; Y = 2), X)), L).
");
        // X is ! when call/1 converts its goal: a real cut, one solution.
        Assert.True(e.Query("t(L), L == [1].").Success);
    }

    [Fact]
    public void AggregateAllCallsAModuleLocal()
    {
        var e = NewEngine();
        e.ConsultString(@"
:- module(agg, [count/1]).
q(1).
q(2).
count(N) :- aggregate_all(count, q(_), N).
");
        Assert.True(e.Query("count(N), N == 2.").Success);
    }
}
