using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Tier-1 SWI-library gap predicates (docs/library-missing-predicates-swi.md):
/// numbervars/4, is_stream/1, term_string/2,3, compound_name_arity/3,
/// cyclic_term/1.</summary>
public sealed class Tier1PredicatesTests
{
    // ---------- numbervars/4 ----------

    [Fact]
    public void NumberVars4_NumbersVariables_WithOptionList()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "numbervars(f(X,Y,X), 0, End, []), X == '$VAR'(0), Y == '$VAR'(1), End == 2.").Success);
        // arity-3 still works unchanged.
        Assert.True(e.Query("numbervars(g(A,B), 5, E), A == '$VAR'(5), E == 7.").Success);
    }

    // ---------- is_stream/1 ----------

    [Fact]
    public void IsStream_TrueForStreamsAndAliases_FalseOtherwise()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("current_output(S), is_stream(S).").Success);
        Assert.True(e.Query("is_stream(user_output).").Success);
        Assert.True(e.Query("is_stream(user_input).").Success);
        Assert.False(e.Query("is_stream(not_a_stream).").Success);
        Assert.False(e.Query("is_stream(42).").Success);
        Assert.False(e.Query("is_stream(_).").Success);           // unbound → fail, no throw
    }

    // ---------- term_string/2,3 ----------

    [Fact]
    public void TermString_TermToString()
    {
        var e = new PrologEngine();
        // Render a term to a string, then read it back as an atom to compare.
        Assert.True(e.Query("term_string(foo(1,2), S), atom_string(A, S), A == 'foo(1,2)'.").Success);
        // operator notation, like term_to_atom.
        Assert.True(e.Query("term_string(1+2, S), atom_string(A, S), A == '1+2'.").Success);
    }

    [Fact]
    public void TermString_StringToTerm()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("atom_string('bar(3, X)', S), term_string(T, S), T = bar(3, _).").Success);
    }

    [Fact]
    public void TermString3_AcceptsOptions()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("term_string(foo(a), S, []), atom_string(A, S), A == 'foo(a)'.").Success);
    }

    // ---------- compound_name_arity/3 ----------

    [Fact]
    public void CompoundNameArity_Decompose()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("compound_name_arity(foo(a,b,c), N, A), N == foo, A == 3.").Success);
        // A list is a compound too.
        Assert.True(e.Query("compound_name_arity([a,b], _, A), A == 2.").Success);
    }

    [Fact]
    public void CompoundNameArity_Construct()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("compound_name_arity(C, foo, 2), C = foo(_, _).").Success);
        Assert.True(e.Query("compound_name_arity(C, bar, 1), functor(C, bar, 1).").Success);
    }

    [Fact]
    public void CompoundNameArity_AtomicIsTypeError()
    {
        var e = new PrologEngine();
        // An atom / number is not a compound.
        Assert.True(e.Query(
            "catch(compound_name_arity(foo, _, _), error(type_error(compound, _), _), true).").Success);
        Assert.True(e.Query(
            "catch(compound_name_arity(42, _, _), error(type_error(compound, _), _), true).").Success);
        // Construct with arity 0 would be atomic, not a compound.
        Assert.True(e.Query(
            "catch(compound_name_arity(_, foo, 0), error(type_error(compound, _), _), true).").Success);
    }

    // ---------- cyclic_term/1 ----------

    [Fact]
    public void CyclicTerm_TrueForCyclic_FalseForFinite()
    {
        var e = new PrologEngine();
        Assert.False(e.Query("cyclic_term(f(a, b)).").Success);
        Assert.False(e.Query("cyclic_term(foo).").Success);
        Assert.False(e.Query("cyclic_term(_).").Success);
        // X = f(X) builds a cyclic term (no occurs check by default).
        Assert.True(e.Query("X = f(X), cyclic_term(X).").Success);
        // Exact complement of acyclic_term/1.
        Assert.True(e.Query("X = f(X), \\+ acyclic_term(X).").Success);
    }
}
