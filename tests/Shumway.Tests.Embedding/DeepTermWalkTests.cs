using System.Text;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Engine walks over USER DATA must not run on the C# call stack.
///
/// <para>A term a program builds has no depth limit the engine controls, and
/// a .NET stack overflow is not a <c>resource_error</c> a program can catch —
/// it kills the process, with no goal to unwind and nothing to report. Every
/// walk over a term therefore carries its pending work in an explicit list on
/// the managed heap. Recursive versions of the walks below died at some ten
/// thousand list elements — an ordinary size for a code list, a findall
/// result, or a generated fact.</para>
///
/// <para>The lists here are deliberately far past that threshold. A
/// regression does not fail these tests politely: it takes the test run down
/// with it, which is the honest signal.</para>
/// </summary>
public class DeepTermWalkTests
{
    private const int Long = 50_000;

    private static PrologEngine EngineWithList()
    {
        var e = new PrologEngine();
        e.ConsultString($"mklist(L) :- numlist(1, {Long}, L).");
        return e;
    }

    [Fact]
    public void TermVariables_OverALongList()
    {
        var e = new PrologEngine();
        var sol = e.Query($"length(L, {Long}), term_variables(L, Vs), length(Vs, N).");
        Assert.True(sol.Success);
        Assert.Equal((long)Long, ((IntTerm)sol["N"]!).Value);
    }

    [Fact]
    public void AcyclicTerm_OverALongList()
    {
        var e = EngineWithList();
        Assert.True(e.Query("mklist(L), acyclic_term(L).").Success);
    }

    [Fact]
    public void NumberVars_OverALongList()
    {
        var e = new PrologEngine();
        var sol = e.Query($"length(L, {Long}), numbervars(L, 0, End).");
        Assert.True(sol.Success);
        Assert.Equal((long)Long, ((IntTerm)sol["End"]!).Value);
    }

    [Fact]
    public void Listing_OfAClauseHoldingALongList()
    {
        // listing/1 demangles and renames variables over the whole clause
        // before laying it out.
        var e = EngineWithList();
        e.ConsultString(":- dynamic(stored/1).");
        var sol = e.Query(
            "mklist(L), assertz(stored(L)), "
            + "with_output_to(atom(A), listing(stored/1)), atom_length(A, N).");
        Assert.True(sol.Success);
        Assert.True(((IntTerm)sol["N"]!).Value > Long);
    }

    [Fact]
    public void AtomToTerm_OverALongList()
    {
        // Reads the term back and collects its variable names — the walk that
        // read_term/3's variable_names option shares.
        var e = EngineWithList();
        var sol = e.Query(
            "mklist(L), term_to_atom(L, A), atom_to_term(A, T, Bindings), "
            + "length(T, N), Bindings = [].");
        Assert.True(sol.Success);
        Assert.Equal((long)Long, ((IntTerm)sol["N"]!).Value);
    }

    [Fact]
    public void TabledAnswer_HoldingALongList()
    {
        // Every tabled answer is canonicalised into a dedup key.
        var e = new PrologEngine();
        e.ConsultString($"""
            :- table t/1.
            t(L) :- numlist(1, {Long}, L).
            """);
        var sol = e.Query("t(L), length(L, N).");
        Assert.True(sol.Success);
        Assert.Equal((long)Long, ((IntTerm)sol["N"]!).Value);
    }

    [Fact]
    public void Bagof_WitnessHoldingALongList()
    {
        // The witness's grouping key is built by walking the witness.
        var e = new PrologEngine();
        e.ConsultString($"""
            anything(_).
            groups(N) :- length(L, {Long}),
                         findall(x, bagof(t, anything(L), _), Xs),
                         length(Xs, N).
            """);
        var sol = e.Query("groups(N).");
        Assert.True(sol.Success);
        Assert.Equal(1L, ((IntTerm)sol["N"]!).Value);
    }

    [Fact]
    public void Consult_OfASourceFileHoldingALongListLiteral()
    {
        // Every consulted clause is scanned for embedded native-C blocks, and
        // that scan walks whatever the source holds.
        var text = new StringBuilder("big([");
        for (int i = 0; i < Long; i++)
        {
            if (i > 0) text.Append(',');
            text.Append(i);
        }
        text.Append("]).");
        var e = new PrologEngine();
        e.ConsultString(text.ToString());
        var sol = e.Query("big(L), length(L, N).");
        Assert.True(sol.Success);
        Assert.Equal((long)Long, ((IntTerm)sol["N"]!).Value);
    }

    [Fact]
    public void TermCodec_RoundTripsAClauseHoldingALongList()
    {
        // The .shmo dynamic-seed codec: encode at compile time, decode at
        // load time — the least diagnosable place to die.
        var args = new Term[Long];
        for (int i = 0; i < Long; i++) args[i] = new IntTerm(i);
        Term list = new AtomTerm("[]");
        for (int i = Long - 1; i >= 0; i--)
            list = new CompoundTerm(".", new[] { args[i], list });
        var clause = Clause.From(new CompoundTerm("big", new[] { list }));

        byte[] bytes = TermCodec.EncodeClause(clause);
        Clause back = TermCodec.DecodeClause(bytes);

        // Walk both spines and compare, iteratively — the test must not
        // recurse either.
        Term a = ((CompoundTerm)clause.Term).Args[0];
        Term b = ((CompoundTerm)back.Term).Args[0];
        int count = 0;
        while (a is CompoundTerm ca && ca.Functor == ".")
        {
            var cb = Assert.IsType<CompoundTerm>(b);
            Assert.Equal(".", cb.Functor);
            Assert.Equal(((IntTerm)ca.Args[0]).Value, ((IntTerm)cb.Args[0]).Value);
            a = ca.Args[1];
            b = cb.Args[1];
            count++;
        }
        Assert.Equal(Long, count);
        Assert.Equal("[]", Assert.IsType<AtomTerm>(b).Name);
    }
}
