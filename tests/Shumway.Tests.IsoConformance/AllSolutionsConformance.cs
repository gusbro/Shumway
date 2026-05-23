using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// ISO 13211-1, §8.10 All solutions.
///
/// Covers <c>findall/3</c> (§8.10.1), <c>bagof/3</c> (§8.10.2) and
/// <c>setof/3</c> (§8.10.3). All three meta-call their Goal argument
/// and collect Template instances; they differ in what to do when
/// the goal fails (findall: empty list; bagof/setof: outer call fails)
/// and in how free variables in Goal are handled (findall: ignored;
/// bagof/setof: each free variable's binding splits the solution set
/// into a separate witnessing answer — suppressible with the
/// <c>^/2</c> existential operator).
///
/// <para>Shumway runs all three in-engine (Phase-4 chunks 82-86) so
/// side effects from inside Goal persist after the call.</para>
/// </summary>
public class AllSolutionsConformance
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    /// <summary>Walk a Prolog list (built from <c>./2</c> cons-cells)
    /// and return the head terms in order. Hardcoding our own walker
    /// avoids the <c>Term.ToString()</c> shape (which renders as
    /// <c>.(a, .(b, []))</c> rather than <c>[a, b]</c>).</summary>
    private static List<Term> ListElements(Term? list)
    {
        var elements = new List<Term>();
        while (list is CompoundTerm { Functor: ".", Args.Length: 2 } cons)
        {
            elements.Add(cons.Args[0]);
            list = cons.Args[1];
        }
        return elements;
    }

    private static List<string> ListAsStrings(Term? list) =>
        ListElements(list).Select(t => t.ToString()!).ToList();

    // ---------- §8.10.1 findall/3 ----------

    [Fact]
    public void Findall_OverMember_CollectsAll()
    {
        var e = new PrologEngine();
        var sol = e.Query("findall(X, member(X, [1,2,3]), L).");
        Assert.True(sol.Success);
        Assert.Equal(new[] { Int(1), Int(2), Int(3) }, ListElements(sol["L"]));
    }

    [Fact]
    public void Findall_NoSolutions_EmptyList()
    {
        // ISO §8.10.1.1: findall(_, Goal, []) when Goal has no solutions.
        var e = new PrologEngine();
        var sol = e.Query("findall(_X, fail, L).");
        Assert.True(sol.Success);
        Assert.Empty(ListElements(sol["L"]));
        Assert.Equal(Atom("[]"), sol["L"]);
    }

    [Fact]
    public void Findall_TemplateIsExpression()
    {
        // The template is instantiated per solution; ISO §8.10.1.4(a).
        var e = new PrologEngine();
        var sol = e.Query("findall(X+Y, member(X-Y, [a-1, b-2]), L).");
        Assert.True(sol.Success);
        var elements = ListElements(sol["L"]);
        Assert.Equal(2, elements.Count);
        Assert.IsType<CompoundTerm>(elements[0]);
        Assert.Equal("+", ((CompoundTerm)elements[0]).Functor);
    }

    [Fact]
    public void Findall_DuplicatesPreserved()
    {
        // Unlike setof, findall keeps duplicates.
        var e = new PrologEngine();
        var sol = e.Query("findall(X, member(X, [1,2,1]), L).");
        Assert.True(sol.Success);
        Assert.Equal(new[] { Int(1), Int(2), Int(1) }, ListElements(sol["L"]));
    }

    [Fact]
    public void Findall_VarGoal_RaisesInstantiationError()
    {
        // §8.10.1.3(a): Goal is var → instantiation_error.
        var e = new PrologEngine();
        var sol = e.Query("catch(findall(_, _G, _), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void Findall_NonCallableGoal_RaisesTypeError()
    {
        // §8.10.1.3(b): Goal not callable → type_error(callable, Goal).
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(findall(_, 123, _), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("callable"), sol["T"]);
    }

    // ---------- §8.10.2 bagof/3 ----------

    [Fact]
    public void Bagof_NoSolutions_Fails()
    {
        // Unlike findall, bagof fails when Goal has no solutions.
        var e = new PrologEngine();
        Assert.False(e.Query("bagof(_X, fail, _L).").Success);
    }

    [Fact]
    public void Bagof_Simple_CollectsInOrder()
    {
        var e = new PrologEngine();
        var sol = e.Query("bagof(X, member(X, [1,2,3]), L).");
        Assert.True(sol.Success);
        Assert.Equal(new[] { Int(1), Int(2), Int(3) }, ListElements(sol["L"]));
    }

    [Fact]
    public void Bagof_PreservesDuplicates()
    {
        var e = new PrologEngine();
        var sol = e.Query("bagof(X, member(X, [1,2,1]), L).");
        Assert.True(sol.Success);
        Assert.Equal(new[] { Int(1), Int(2), Int(1) }, ListElements(sol["L"]));
    }

    [Fact]
    public void Bagof_ExistentialQuantifier_SuppressesWitness()
    {
        // Y^Goal makes Y existential — bagof shouldn't split solutions
        // by each Y. ISO §8.10.2.4(d).
        var e = new PrologEngine();
        var sol = e.Query(
            "bagof(X, Y^member(X-Y, [a-1, b-2, a-3]), L).");
        Assert.True(sol.Success);
        Assert.Equal(new[] { Atom("a"), Atom("b"), Atom("a") },
            ListElements(sol["L"]));
    }

    [Fact]
    public void Bagof_FreeVarSplitsSolutions()
    {
        // Without ^, free variable Y splits the solution set: bagof
        // enumerates one binding of Y per backtrack.
        var e = new PrologEngine();
        var solutions = e.QueryAll(
            "bagof(X, member(X-Y, [a-1, b-2, a-3]), L).")
            .Select(s => (Y: s["Y"]!, L: ListAsStrings(s["L"])))
            .ToList();
        Assert.Contains(solutions, p => p.Y.Equals(Int(1)) && p.L.SequenceEqual(new[] { "a" }));
        Assert.Contains(solutions, p => p.Y.Equals(Int(2)) && p.L.SequenceEqual(new[] { "b" }));
        Assert.Contains(solutions, p => p.Y.Equals(Int(3)) && p.L.SequenceEqual(new[] { "a" }));
    }

    [Fact]
    public void Bagof_NonCallableGoal_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(bagof(_, 123, _), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("callable"), sol["T"]);
    }

    // ---------- §8.10.3 setof/3 ----------

    [Fact]
    public void Setof_NoSolutions_Fails()
    {
        var e = new PrologEngine();
        Assert.False(e.Query("setof(_X, fail, _L).").Success);
    }

    [Fact]
    public void Setof_SortsAndDedupes()
    {
        // §8.10.3 — sorted by standard order, no duplicates.
        var e = new PrologEngine();
        var sol = e.Query("setof(X, member(X, [3,1,2,1,3]), L).");
        Assert.True(sol.Success);
        Assert.Equal(new[] { Int(1), Int(2), Int(3) }, ListElements(sol["L"]));
    }

    [Fact]
    public void Setof_AcrossDisjunction()
    {
        var e = new PrologEngine();
        var sol = e.Query("setof(X, (X=2 ; X=1 ; X=3 ; X=1), L).");
        Assert.True(sol.Success);
        Assert.Equal(new[] { Int(1), Int(2), Int(3) }, ListElements(sol["L"]));
    }

    [Fact]
    public void Setof_ExistentialQuantifier_SuppressesWitness()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "setof(X, Y^member(X-Y, [a-1, b-2, a-3]), L).");
        Assert.True(sol.Success);
        Assert.Equal(new[] { Atom("a"), Atom("b") }, ListElements(sol["L"]));
    }

    [Fact]
    public void Setof_FreeVarSplitsAndSortsEach()
    {
        var e = new PrologEngine();
        // Each Y enumerates a separate sorted/dedup result list.
        var solutions = e.QueryAll(
            "setof(X, member(X-Y, [a-2, b-2, a-1, c-1, a-1]), L).")
            .Select(s => (Y: s["Y"]!, L: ListAsStrings(s["L"])))
            .ToList();
        Assert.Contains(solutions, p => p.Y.Equals(Int(1)) && p.L.SequenceEqual(new[] { "a", "c" }));
        Assert.Contains(solutions, p => p.Y.Equals(Int(2)) && p.L.SequenceEqual(new[] { "a", "b" }));
    }

    [Fact]
    public void Setof_NonCallableGoal_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(setof(_, 123, _), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("callable"), sol["T"]);
    }
}
