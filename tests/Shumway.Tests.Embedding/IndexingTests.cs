using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// End-to-end coverage for first-argument indexing (ADR-007). The unit tests
/// in Shumway.Tests.Compiler verify the emitted bytecode shape; these tests
/// verify the runtime semantics: indexed predicates still produce the right
/// solutions, in the right order, with backtracking preserved.
/// </summary>
public class IndexingTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    [Fact]
    public void Atoms_SpecificCall_FindsMatchingClause()
    {
        // Fact table dispatching on atom — the bread-and-butter indexed case.
        var engine = new PrologEngine();
        engine.ConsultString(
            "colour(red).\n" +
            "colour(green).\n" +
            "colour(blue).\n");

        Assert.True(engine.Query("colour(red).").Success);
        Assert.True(engine.Query("colour(green).").Success);
        Assert.True(engine.Query("colour(blue).").Success);
        Assert.False(engine.Query("colour(purple).").Success);
    }

    [Fact]
    public void Atoms_VariableCall_EnumeratesEveryClauseInOrder()
    {
        // A variable A1 routes through VarLbl and tries every clause.
        var engine = new PrologEngine();
        engine.ConsultString(
            "colour(red).\n" +
            "colour(green).\n" +
            "colour(blue).\n");

        var all = engine.QueryAll("colour(X).").Select(s => s["X"]).ToList();
        Assert.Equal(
            new[] { Atom("red"), Atom("green"), Atom("blue") },
            all);
    }

    [Fact]
    public void Atoms_MultipleClausesPerKey_BacktrackingWorks()
    {
        // Two clauses share the same atom key — indexing dispatches the
        // try/retry/trust chain inside the matching group.
        var engine = new PrologEngine();
        engine.ConsultString(
            "kind(cat,  pet).\n" +
            "kind(dog,  pet).\n" +
            "kind(cat,  feline).\n" +
            "kind(lion, feline).\n");

        var pets = engine.QueryAll("kind(cat, K).").Select(s => s["K"]).ToList();
        Assert.Equal(new[] { Atom("pet"), Atom("feline") }, pets);
    }

    [Fact]
    public void Integers_SpecificCall_FindsMatchingClause()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            "square(1, 1).\n" +
            "square(2, 4).\n" +
            "square(3, 9).\n" +
            "square(4, 16).\n");

        Assert.Equal(Int(9), engine.Query("square(3, X).")["X"]);
        Assert.Equal(Int(16), engine.Query("square(4, X).")["X"]);
        Assert.False(engine.Query("square(99, _).").Success);
    }

    [Fact]
    public void VarClause_AppearsInAtomBucket()
    {
        // A var-first-arg clause matches every atom call. With indexing it
        // must still be reached when dispatched through an atom bucket.
        var engine = new PrologEngine();
        engine.ConsultString(
            "tag(red,    colour).\n" +
            "tag(_,      thing).\n" +     // var first arg — matches any atom
            "tag(circle, shape).\n");

        // ?- tag(red, X) — should give 'colour' then 'thing' on backtrack.
        var tags = engine.QueryAll("tag(red, X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Atom("colour"), Atom("thing") }, tags);

        // ?- tag(square, X) — only the var-arg clause matches.
        var squareTags = engine.QueryAll("tag(square, X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Atom("thing") }, squareTags);
    }

    [Fact]
    public void MixedAtomAndIntegerFirstArgs_BothDispatch()
    {
        // A predicate whose clauses have a mix of atom and integer first args.
        // Atom-headed calls go through switch_on_atom, integer-headed calls
        // through switch_on_integer, both chained via switch_on_term.
        var engine = new PrologEngine();
        engine.ConsultString(
            "labelled(red, colour).\n" +
            "labelled(1,   number).\n" +
            "labelled(green, colour).\n" +
            "labelled(42,  number).\n");

        Assert.Equal(Atom("colour"), engine.Query("labelled(red, L).")["L"]);
        Assert.Equal(Atom("number"), engine.Query("labelled(42, L).")["L"]);
        Assert.False(engine.Query("labelled(purple, _).").Success);
        Assert.False(engine.Query("labelled(999, _).").Success);
    }

    [Fact]
    public void StructureFirstArg_DispatchesByFunctor()
    {
        // Discrimination by the functor of a compound first arg.
        var engine = new PrologEngine();
        engine.ConsultString(
            "describe(point(_, _),  twoD).\n" +
            "describe(box(_, _, _), threeD).\n" +
            "describe(line(_, _),   geometry).\n");

        Assert.Equal(Atom("twoD"), engine.Query("describe(point(1, 2), L).")["L"]);
        Assert.Equal(Atom("threeD"), engine.Query("describe(box(1, 2, 3), L).")["L"]);
        Assert.Equal(Atom("geometry"), engine.Query("describe(line(1, 2), L).")["L"]);
    }

    [Fact]
    public void ListFirstArg_DispatchesAsList()
    {
        // [] is an atom but a cons cell ([H|T]) is a list — they end up in
        // different buckets. The indexing path routes correctly to each.
        var engine = new PrologEngine();
        engine.ConsultString(
            "shape([],    empty).\n" +
            "shape([_|_], nonempty).\n");

        Assert.Equal(Atom("empty"), engine.Query("shape([], X).")["X"]);
        Assert.Equal(Atom("nonempty"), engine.Query("shape([1, 2, 3], X).")["X"]);
    }

    [Fact]
    public void LargeIndexedTable_LookupBeyondLinearScanThreshold()
    {
        // The switch table flips from linear scan to Dictionary when there are
        // more than 16 entries. Exercise the Dictionary path with 20 clauses.
        var clauses = new System.Text.StringBuilder();
        for (int i = 0; i < 20; i++) clauses.Append($"f({i}, {i * 10}).\n");
        var engine = new PrologEngine();
        engine.ConsultString(clauses.ToString());

        Assert.Equal(Int(0),  engine.Query("f(0, Y).")["Y"]);
        Assert.Equal(Int(50), engine.Query("f(5, Y).")["Y"]);
        Assert.Equal(Int(190), engine.Query("f(19, Y).")["Y"]);
        Assert.False(engine.Query("f(100, _).").Success);
    }

    [Fact]
    public void IndexedPredicate_PreservesCutSemantics()
    {
        // Cut inside an indexed clause should still discard the predicate's
        // own choice point. Even though we entered through switch_on_atom,
        // the bucket's try/retry created a CP; cut must remove it.
        var engine = new PrologEngine();
        engine.ConsultString(
            "lookup(a, first) :- !.\n" +
            "lookup(a, second).\n" +
            "lookup(b, third).\n");

        // Cut commits to clause 1; clause 2 isn't tried.
        var bindings = engine.QueryAll("lookup(a, X).").Select(s => s["X"]).ToList();
        Assert.Single(bindings);
        Assert.Equal(Atom("first"), bindings[0]);
    }
}
