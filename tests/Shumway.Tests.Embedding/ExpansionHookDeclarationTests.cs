using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A <c>:- dynamic</c> or <c>:- multifile</c> declaration of
/// term_expansion/2 or goal_expansion/2: while nothing defines the hook it is a
/// current, empty dynamic predicate (Logtalk's user object declares both
/// multifile and checks them with current_predicate/1). Hook clauses from a
/// source still take the static pipeline, which resolves a module's own
/// helpers in their bodies.</summary>
public class ExpansionHookDeclarationTests
{
    private const string Declarations = @"
        :- dynamic(term_expansion/2).
        :- dynamic(goal_expansion/2).
        ";

    [Theory]
    [InlineData(@"dynamic", @"current_predicate(term_expansion/2)")]
    [InlineData(@"dynamic", @"current_predicate(goal_expansion/2)")]
    [InlineData(@"dynamic", @"predicate_property(term_expansion(_, _), dynamic)")]
    [InlineData(@"dynamic", @"predicate_property(goal_expansion(_, _), dynamic)")]
    [InlineData(@"dynamic", @"\+ term_expansion(_, _)")]
    [InlineData(@"dynamic", @"\+ goal_expansion(_, _)")]
    [InlineData(@"multifile", @"current_predicate(term_expansion/2)")]
    [InlineData(@"multifile", @"current_predicate(goal_expansion/2)")]
    [InlineData(@"multifile", @"\+ term_expansion(_, _)")]
    [InlineData(@"multifile", @"\+ goal_expansion(_, _)")]
    public void ADeclaredHookIsAnEmptyDynamicPredicate(string directive, string goal)
    {
        var engine = new PrologEngine();
        engine.ConsultString($@"
            :- {directive}(term_expansion/2).
            :- {directive}(goal_expansion/2).
            ");

        Assert.True(engine.Query(goal + ".").Success, goal);
    }

    [Fact]
    public void ADeclaredHookIsNotQueriedForEveryConsultedTerm()
    {
        var engine = new PrologEngine();
        engine.ConsultString(Declarations);
        int te = FunctorTable.Intern(AtomTable.Intern("term_expansion").Id, 2);
        int ge = FunctorTable.Intern(AtomTable.Intern("goal_expansion").Id, 2);

        Assert.False(engine.HookCouldMatch(te, new AtomTerm("f")));
        Assert.False(engine.HookCouldMatch(ge, new CompoundTerm("g", new Term[] { new IntTerm(1) })));

        Assert.True(engine.Query("assertz(term_expansion(f, g)).").Success);
        Assert.True(engine.HookCouldMatch(te, new AtomTerm("f")));
    }

    [Fact]
    public void AnAssertedHookExpandsALaterConsult()
    {
        var engine = new PrologEngine();
        engine.ConsultString(Declarations);

        Assert.True(engine.Query("assertz(term_expansion(old, new)).").Success);
        engine.ConsultString("old.");
        Assert.True(engine.Query("new.").Success);
        Assert.True(engine.Query("retract(term_expansion(old, new)).").Success);
        engine.ConsultString("old.");
        Assert.True(engine.Query("old.").Success);
    }

    [Fact]
    public void AModuleHookDefinedAfterTheDeclarationCallsItsOwnHelpers()
    {
        var engine = new PrologEngine();
        engine.ConsultString(Declarations);
        Assert.True(engine.Query(@"\+ term_expansion(x, _).").Success);

        engine.ConsultString(@"
            :- module(hooks, []).
            user:term_expansion(x, Y) :- once(helper(Y)).
            helper(y).
            ");
        engine.ConsultString("x.");

        Assert.True(engine.Query("y.").Success);
        Assert.True(engine.Query("term_expansion(x, Y), Y == y.").Success);
    }

    // The same, consulted from inside a running query, the way a program loads
    // its libraries: a hook clause put in the dynamic store there reached its
    // module's helper unqualified, and a direct call raised existence_error.
    [Fact]
    public void AModuleHookConsultedInsideAQueryCallsItsOwnHelpers()
    {
        string hooks = WriteTemp(@"
            :- module(hooks2, []).
            user:term_expansion(x, Y) :- once(helper(Y)).
            helper(y).
            ");
        string user = WriteTemp("x.\n");
        try
        {
            var engine = new PrologEngine();
            engine.ConsultString(Declarations);

            Assert.True(engine.Query($@"\+ term_expansion(x, _),
                consult('{hooks}'), consult('{user}'), y,
                term_expansion(x, Y), Y == y.").Success);
        }
        finally { System.IO.File.Delete(hooks); System.IO.File.Delete(user); }
    }

    private static string WriteTemp(string text)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "hookdecl_" + System.Guid.NewGuid().ToString("N") + ".pl");
        System.IO.File.WriteAllText(path, text);
        return path.Replace("\\", "/");
    }

    [Theory]
    [InlineData(@"dynamic")]
    [InlineData(@"multifile")]
    public void AModuleThatDeclaresAndDefinesTheHookKeepsItAcrossAReload(string directive)
    {
        string hooks = $@"
            :- module(twice_hook, []).
            :- {directive} user:goal_expansion/2.
            user:goal_expansion(twice(G), (G, G)) :- once(ok(G)).
            ok(_).
            ";
        const string user = @"
            count(N) :- nb_setval(k, 0), twice(inc), nb_getval(k, N).
            inc :- nb_getval(k, V), V1 is V + 1, nb_setval(k, V1).
            ";
        var engine = new PrologEngine();

        engine.ConsultString(hooks);
        engine.ConsultString(user);
        Assert.True(engine.Query("count(2).").Success);
        engine.ConsultString(hooks);
        engine.ConsultString(user);
        Assert.True(engine.Query("count(2).").Success);
    }
}
