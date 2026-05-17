using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Coverage for the module system (ADR-008): :- module/1 names a module,
/// :- public/1 marks a functor as globally visible, locals get mangled so
/// two modules can use the same private name, and public uniqueness is
/// enforced across the engine.
/// </summary>
public class ModuleSystemTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    // ---------- Default module backward compat ----------

    [Fact]
    public void NoDirective_LoadsIntoUserModule()
    {
        var engine = new PrologEngine();
        engine.ConsultString("p(a).");
        Assert.True(engine.Modules.ContainsKey(PrologEngine.DefaultModuleName));
        Assert.True(engine.Query("p(a).").Success);
    }

    [Fact]
    public void NoDirective_MultipleConsults_AppendToUser()
    {
        // The 'user' module is special — it accumulates across consults to
        // match the pre-modules behaviour rather than replace on every call.
        var engine = new PrologEngine();
        engine.ConsultString("p(a).");
        engine.ConsultString("p(b).");
        var solutions = engine.QueryAll("p(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Atom("a"), Atom("b") }, solutions);
    }

    // ---------- Explicit module ----------

    [Fact]
    public void ExplicitModule_NameIsRegistered()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- module(parser).\np(a).");
        Assert.True(engine.Modules.ContainsKey("parser"));
    }

    [Fact]
    public void ExplicitModule_LocalPredicateNotCallableFromUser()
    {
        // p/1 is local to 'parser', so a query in the user context can't
        // reach it — it tries to call the bare 'p/1' which doesn't exist
        // outside parser.
        var engine = new PrologEngine();
        engine.ConsultString(":- module(parser).\np(a).");
        Assert.Throws<InvalidOperationException>(() => engine.Query("p(X)."));
    }

    [Fact]
    public void PublicPredicate_CallableFromUser()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- module(parser).\n" +
            ":- public token/1.\n" +
            "token(hello).\n" +
            "token(world).\n");

        Assert.True(engine.Query("token(hello).").Success);
        Assert.True(engine.Query("token(world).").Success);
        Assert.False(engine.Query("token(missing).").Success);
    }

    [Fact]
    public void PublicPredicate_EnumeratesAllClauses()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- module(items).\n" +
            ":- public item/1.\n" +
            "item(apple).\n" +
            "item(banana).\n" +
            "item(cherry).\n");

        var items = engine.QueryAll("item(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Atom("apple"), Atom("banana"), Atom("cherry") }, items);
    }

    // ---------- Public-list form ----------

    [Fact]
    public void PublicListForm_ExportsAllNamed()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- module(lib).\n" +
            ":- public [a/0, b/1, c/2].\n" +
            "a.\n" +
            "b(x).\n" +
            "c(1, 2).\n");

        Assert.True(engine.Query("a.").Success);
        Assert.True(engine.Query("b(x).").Success);
        Assert.True(engine.Query("c(1, 2).").Success);
    }

    // ---------- Multi-module with cross-call ----------

    [Fact]
    public void TwoModules_PublicCallsAcrossWorks()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- module(lib).\n" +
            ":- public double/2.\n" +
            "double(X, Y) :- Y is X * 2.\n");
        engine.ConsultString(
            ":- module(client).\n" +
            ":- public describe/2.\n" +
            "describe(X, R) :- double(X, R).\n");

        Assert.Equal(Int(14), engine.Query("describe(7, R).")["R"]);
    }

    [Fact]
    public void TwoModules_SameLocalNameDoesntCollide()
    {
        // Both modules use a local 'helper/1' — without mangling these would
        // clash in the linker. The mangling makes each module own its own
        // helper.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- module(mod_a).\n" +
            ":- public ka/1.\n" +
            "helper(a_value).\n" +
            "ka(X) :- helper(X).\n");
        engine.ConsultString(
            ":- module(mod_b).\n" +
            ":- public kb/1.\n" +
            "helper(b_value).\n" +
            "kb(X) :- helper(X).\n");

        Assert.Equal(Atom("a_value"), engine.Query("ka(X).")["X"]);
        Assert.Equal(Atom("b_value"), engine.Query("kb(X).")["X"]);
    }

    // ---------- Public uniqueness ----------

    [Fact]
    public void TwoModules_SamePublicFunctor_RejectedAtQuery()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- module(a).\n" +
            ":- public shared/1.\n" +
            "shared(from_a).\n");
        engine.ConsultString(
            ":- module(b).\n" +
            ":- public shared/1.\n" +
            "shared(from_b).\n");

        // Validation happens at query time — that's the first opportunity to
        // notice the conflict in the simpler ConsultString flow.
        var ex = Assert.Throws<InvalidOperationException>(
            () => engine.Query("shared(X)."));
        Assert.Contains("shared/1", ex.Message);
        Assert.Contains("public", ex.Message);
    }

    // ---------- Reload semantics ----------

    [Fact]
    public void ExplicitModule_Reload_ReplacesPreviousContents()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- module(counter).\n" +
            ":- public value/1.\n" +
            "value(1).\n");
        Assert.Equal(Int(1), engine.Query("value(N).")["N"]);

        // Re-consult the same module — the old value/1 disappears entirely.
        engine.ConsultString(
            ":- module(counter).\n" +
            ":- public value/1.\n" +
            "value(99).\n");
        Assert.Equal(Int(99), engine.Query("value(N).")["N"]);
    }

    // ---------- Builtins still work everywhere ----------

    [Fact]
    public void Builtins_CallableFromAnyModule()
    {
        // is/2, write/1, etc. live in a global "system" namespace and are
        // never mangled.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- module(calc).\n" +
            ":- public square/2.\n" +
            "square(X, Y) :- Y is X * X.\n");
        Assert.Equal(Int(49), engine.Query("square(7, Y).")["Y"]);
    }

    // ---------- Local recursion still works ----------

    [Fact]
    public void LocalRecursion_WithinModule_Works()
    {
        // A local helper that recursively calls itself — mangling must be
        // consistent between head and body.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- module(arith).\n" +
            ":- public sum_to/2.\n" +
            "sum_to(N, S) :- sum_to(N, 0, S).\n" +
            "sum_to(0, Acc, Acc) :- !.\n" +
            "sum_to(N, Acc, S) :- N > 0, N1 is N - 1, Acc1 is Acc + N, sum_to(N1, Acc1, S).\n");

        Assert.Equal(Int(55), engine.Query("sum_to(10, S).")["S"]);
    }
}
