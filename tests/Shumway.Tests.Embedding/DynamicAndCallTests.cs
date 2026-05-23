using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Coverage for chunk 21: <c>:- dynamic</c> declarations, the
/// <c>assertz/asserta/retract</c> built-in family, and runtime meta-call
/// via <c>call/N</c>.
/// </summary>
public class DynamicAndCallTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    // ---------- :- dynamic declaration ----------

    [Fact]
    public void DynamicDeclared_NoAssertions_CallFails()
    {
        // A declared-but-empty dynamic predicate exists at link time but
        // every call to it fails — it has no clauses yet.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic colour/1.\n");
        Assert.False(engine.Query("colour(red).").Success);
    }

    [Fact]
    public void Assertz_AppendsClauseToDynamicStore()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic fact/1.\n");
        engine.Query("assertz(fact(1)).");
        engine.Query("assertz(fact(2)).");
        engine.Query("assertz(fact(3)).");

        var solutions = engine.QueryAll("fact(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(1), Int(2), Int(3) }, solutions);
    }

    [Fact]
    public void Asserta_PrependsClauseToDynamicStore()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic v/1.\n");
        engine.Query("assertz(v(2)).");
        engine.Query("assertz(v(3)).");
        engine.Query("asserta(v(1)).");
        // Asserta puts v(1) at the front, so the order is 1, 2, 3.
        var solutions = engine.QueryAll("v(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(1), Int(2), Int(3) }, solutions);
    }

    [Fact]
    public void Retract_FirstMatchingClause_Removes()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic n/1.\n");
        engine.Query("assertz(n(10)).");
        engine.Query("assertz(n(20)).");
        engine.Query("assertz(n(30)).");

        // Remove the middle one.
        Assert.True(engine.Query("retract(n(20)).").Success);
        var remaining = engine.QueryAll("n(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(10), Int(30) }, remaining);
    }

    [Fact]
    public void Retract_NonexistentClause_Fails()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic v/1.\n");
        engine.Query("assertz(v(1)).");

        Assert.False(engine.Query("retract(v(99)).").Success);
    }

    [Fact]
    public void Retract_WithVariablePattern_BindsAndRemoves()
    {
        // retract(v(X)) — finds the first clause for v/1, removes it, and
        // binds X to that clause's head argument.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic v/1.\n");
        engine.Query("assertz(v(first)).");
        engine.Query("assertz(v(second)).");

        var sol = engine.Query("retract(v(X)).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("first"), sol["X"]);
        // The remaining clause is the second one.
        var leftover = engine.Query("v(Y).");
        Assert.Equal(Atom("second"), leftover["Y"]);
    }

    [Fact]
    public void Assertz_NonDynamicPredicate_Throws()
    {
        // Without :- dynamic, assertz refuses to add a clause.
        // Phase-9 chunk 131e: this now raises a catchable ISO
        // permission_error(modify, static_procedure, _) rather than the
        // uncatchable InvalidOperationException earlier phases used.
        var engine = new PrologEngine();
        var ex = Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => engine.Query("assertz(p(1))."));
        Assert.Equal("permission_error", ex.Kind);
    }

    [Fact]
    public void DynamicList_Form_Accepted()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic [a/0, b/1, c/2].\n");
        engine.Query("assertz(a).");
        engine.Query("assertz(b(x)).");
        engine.Query("assertz(c(1, 2)).");

        Assert.True(engine.Query("a.").Success);
        Assert.True(engine.Query("b(x).").Success);
        Assert.True(engine.Query("c(1, 2).").Success);
    }

    [Fact]
    public void DynamicRule_Asserted_BodyExecutes()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic double/2.\n");
        engine.Query("assertz((double(X, Y) :- Y is X * 2)).");
        Assert.Equal(Int(14), engine.Query("double(7, R).")["R"]);
    }

    // ---------- call/N ----------

    [Fact]
    public void Call1_Atom_InvokesPredicate()
    {
        var engine = new PrologEngine();
        engine.ConsultString("greet :- true.\n");
        Assert.True(engine.Query("call(greet).").Success);
    }

    [Fact]
    public void Call1_Compound_InvokesAndBinds()
    {
        var engine = new PrologEngine();
        engine.ConsultString("colour(red).\ncolour(green).\n");
        var sol = engine.Query("call(colour(X)).");
        Assert.True(sol.Success);
        // Query/1 reports the first solution; call/N is backtrackable for
        // the rest (chunk 86 — see Chunk86Tests).
        Assert.Equal(Atom("red"), sol["X"]);
    }

    [Fact]
    public void Call2_AppendsOneExtraArg()
    {
        // call(greet, world) → greet(world).
        var engine = new PrologEngine();
        engine.ConsultString("greet(X) :- X = world.\n");
        var sol = engine.Query("call(greet, R).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("world"), sol["R"]);
    }

    [Fact]
    public void Call3_AppendsTwoExtraArgs()
    {
        // call(append, [a, b], [c, d], R) → append([a, b], [c, d], R).
        var engine = new PrologEngine();
        var sol = engine.Query("call(append, [a, b], [c, d], R).");
        Assert.True(sol.Success);
        Assert.Equal(
            new CompoundTerm(".",
                new[] { Atom("a"),
                    new CompoundTerm(".",
                        new[] { Atom("b"),
                            new CompoundTerm(".",
                                new[] { Atom("c"),
                                    new CompoundTerm(".",
                                        new[] { Atom("d"), Atom("[]") }) }) }) }),
            sol["R"]);
    }

    [Fact]
    public void Call_GoalFails_CallFails()
    {
        var engine = new PrologEngine();
        engine.ConsultString("p(1).\n");
        Assert.False(engine.Query("call(p(2)).").Success);
    }

    [Fact]
    public void Call_OnBuiltin_Works()
    {
        // call(is(X, 1+2)) → is(X, 1+2). Hits the builtin like a direct call.
        var engine = new PrologEngine();
        var sol = engine.Query("call(is(X, 1+2)).");
        Assert.True(sol.Success);
        Assert.Equal(Int(3), sol["X"]);
    }

    // ---------- Combined ----------

    [Fact]
    public void Assertz_ThenCall_Roundtrip()
    {
        // Assertz a clause, then invoke it via call/1.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic kind/2.\n");
        engine.Query("assertz(kind(cat, mammal)).");
        engine.Query("assertz(kind(snake, reptile)).");

        var sol = engine.Query("call(kind(snake, K)).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("reptile"), sol["K"]);
    }
}
