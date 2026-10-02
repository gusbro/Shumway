using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// A dynamic predicate whose clauses a term_expansion hook of the same file
/// produces keeps them where every other clause of a dynamic predicate is
/// kept: the dynamic store, at the place of the source line they replace.
/// The in-file re-expansion pass used to leave them among the module's
/// static clauses, where the predicate still ran them but retract/1 could
/// not remove them and a module-qualified clause/2 could not see them.
/// clp(Z)'s clpz_neq/2 is such a predicate.
/// </summary>
public class ExpandedDynamicClausesTests
{
    private const string ModuleText = """
        :- module(mx, [touch/0]).
        :- dynamic(gen/1).
        term_expansion(make_gen, [gen(a), gen(b), gen(c)]).
        make_gen.
        touch.
        """;

    private static void Succeeds(PrologEngine e, string goal)
        => Assert.True(e.Query(goal).Success, goal);

    [Fact]
    public void InAModule_EveryAccessSeesTheExpandedClauses()
    {
        var e = new PrologEngine();
        e.ConsultString(ModuleText);
        Succeeds(e, "findall(X, clause(mx:gen(X), true), L), L == [a, b, c].");
        Succeeds(e, "findall(X, clause(gen(X), true), L), L == [a, b, c].");
        Succeeds(e, "findall(X, mx:gen(X), L), L == [a, b, c].");
        Succeeds(e, "retract(mx:gen(a)).");
        Succeeds(e, "\\+ mx:gen(a).");
        Succeeds(e, "retract(gen(b)).");
        Succeeds(e, "findall(X, mx:gen(X), L), L == [c].");
        Succeeds(e, "assertz(mx:gen(z)), findall(X, clause(mx:gen(X), true), L), L == [c, z].");
    }

    [Fact]
    public void InAUserFile_TheExpandedClausesCanBeRetracted()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- dynamic(ug/1).
            term_expansion(make_ug, [ug(1), ug(2)]).
            make_ug.
            """);
        Succeeds(e, "findall(X, clause(user:ug(X), true), L), L == [1, 2].");
        Succeeds(e, "retract(ug(1)).");
        Succeeds(e, "\\+ ug(1).");
        Succeeds(e, "findall(X, ug(X), L), L == [2].");
    }

    [Fact]
    public void TheExpandedClausesTakeThePlaceOfTheirSourceLine()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(mo, [touch/0]).
            :- dynamic(mix/1).
            term_expansion(make_mix, [mix(a), mix(b)]).
            mix(0).
            make_mix.
            mix(9).
            touch.
            """);
        Succeeds(e, "findall(X, mo:mix(X), L), L == [0, a, b, 9].");
        Succeeds(e, "findall(X, clause(mo:mix(X), true), L), L == [0, a, b, 9].");
        Succeeds(e, "retract(mo:mix(a)), findall(X, mo:mix(X), L), L == [0, b, 9].");
    }
}
