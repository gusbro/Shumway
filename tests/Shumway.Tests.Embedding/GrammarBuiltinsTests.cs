using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Coverage for the chunk-20 grammar / meta-programming builtins:
/// phrase/2, phrase/3, atom_length, atom_chars, char_code, number_codes,
/// number_chars, copy_term.
/// </summary>
public class GrammarBuiltinsTests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term Flt(double v) => new FloatTerm(v);
    private static Term Nil() => new AtomTerm("[]");
    private static Term Cons(Term h, Term t) => new CompoundTerm(".", new[] { h, t });
    private static Term List(params Term[] items)
    {
        Term acc = Nil();
        for (int i = items.Length - 1; i >= 0; i--) acc = Cons(items[i], acc);
        return acc;
    }

    // ---------- phrase/2 + phrase/3 ----------

    [Fact]
    public void Phrase2_OverDcgRule_Succeeds()
    {
        // phrase(noun, [dog]) → noun([dog], []), DCG rule consumes the whole list.
        var engine = new PrologEngine();
        engine.ConsultString(
            "noun --> [dog].\n" +
            "noun --> [cat].\n");
        Assert.True(engine.Query("phrase(noun, [dog]).").Success);
        Assert.True(engine.Query("phrase(noun, [cat]).").Success);
        Assert.False(engine.Query("phrase(noun, [fish]).").Success);
    }

    [Fact]
    public void Phrase3_BindsRemainingTokens()
    {
        // phrase(noun, Tokens, Rest) → noun(Tokens, Rest); Rest is whatever's
        // left after noun consumes its prefix.
        var engine = new PrologEngine();
        engine.ConsultString("noun --> [dog].\n");
        var sol = engine.Query("phrase(noun, [dog, runs], Rest).");
        Assert.True(sol.Success);
        Assert.Equal(List(Atom("runs")), sol["Rest"]);
    }

    [Fact]
    public void Phrase_CompoundNonTerminal_AppendsTwoArgs()
    {
        // phrase(greet(X), [hello, world]) → greet(X, [hello, world], []).
        var engine = new PrologEngine();
        engine.ConsultString("greet(X) --> [hello, X].\n");
        var sol = engine.Query("phrase(greet(X), [hello, world]).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("world"), sol["X"]);
    }

    [Fact]
    public void Phrase_ListBody_LeavesAsUserPredicate()
    {
        // If the body argument is a list-shaped term, phrase isn't expanded
        // — the call goes to whatever the user defined as phrase/2. Here
        // that's the DCG rule for the non-terminal called 'phrase'.
        var engine = new PrologEngine();
        engine.ConsultString(
            "verb --> [runs].\n" +
            "phrase --> [the, dog], verb.\n");
        Assert.True(engine.Query("phrase([the, dog, runs], []).").Success);
    }

    // ---------- atom_length/2 ----------

    [Fact]
    public void AtomLength_GroundAtom_ReturnsCodeUnitCount()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(5), engine.Query("atom_length(hello, N).")["N"]);
        Assert.Equal(Int(0), engine.Query("atom_length('', N).")["N"]);
        Assert.Equal(Int(3), engine.Query("atom_length(cat, N).")["N"]);
    }

    // ---------- atom_chars/2 ----------

    [Fact]
    public void AtomChars_DecomposesAtomIntoSingleCharAtoms()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("atom_chars(abc, L).");
        Assert.True(sol.Success);
        Assert.Equal(List(Atom("a"), Atom("b"), Atom("c")), sol["L"]);
    }

    [Fact]
    public void AtomChars_BuildsAtomFromCharList()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("atom_chars(A, [h, e, l, l, o]).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("hello"), sol["A"]);
    }

    // ---------- char_code/2 ----------

    [Fact]
    public void CharCode_AtomToCode()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int('a'), engine.Query("char_code(a, C).")["C"]);
        Assert.Equal(Int('Z'), engine.Query("char_code('Z', C).")["C"]);
    }

    [Fact]
    public void CharCode_CodeToAtom()
    {
        var engine = new PrologEngine();
        Assert.Equal(Atom("a"), engine.Query("char_code(C, 97).")["C"]);
    }

    // ---------- number_codes/2 ----------

    [Fact]
    public void NumberCodes_IntegerToCodes()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("number_codes(42, L).");
        Assert.True(sol.Success);
        Assert.Equal(List(Int('4'), Int('2')), sol["L"]);
    }

    [Fact]
    public void NumberCodes_CodesToInteger()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(123), engine.Query("number_codes(N, [0'1, 0'2, 0'3]).")["N"]);
    }

    [Fact]
    public void NumberCodes_FloatRoundTrip()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("number_codes(3.14, L).");
        Assert.True(sol.Success);
        // 3.14 → "3.14" → codes for '3', '.', '1', '4'.
        Assert.Equal(
            List(Int('3'), Int('.'), Int('1'), Int('4')),
            sol["L"]);
    }

    // ---------- number_chars/2 ----------

    [Fact]
    public void NumberChars_IntegerToChars()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("number_chars(99, L).");
        Assert.True(sol.Success);
        Assert.Equal(List(Atom("9"), Atom("9")), sol["L"]);
    }

    [Fact]
    public void NumberChars_CharsToInteger()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(-25), engine.Query("number_chars(N, ['-', '2', '5']).")["N"]);
    }

    // ---------- copy_term/2 ----------

    [Fact]
    public void CopyTerm_GroundTerm_StructurallyEqualButFresh()
    {
        var engine = new PrologEngine();
        // Ground terms copy by value.
        var sol = engine.Query("copy_term(foo(a, b, c), C).");
        Assert.True(sol.Success);
        Assert.Equal(
            new CompoundTerm("foo", new[] { Atom("a"), Atom("b"), Atom("c") }),
            sol["C"]);
    }

    [Fact]
    public void CopyTerm_TermWithVars_GivesFreshVars()
    {
        // copy_term(f(X, X, Y), C) → C is f(_A, _A, _B) — sharing of X
        // preserved, but neither var is the same heap variable as the
        // original X / Y.
        var engine = new PrologEngine();
        var sol = engine.Query("copy_term(f(X, X, Y), C), Y = bound.");
        Assert.True(sol.Success);
        // Y becomes 'bound' in the caller. Inside C, the second var should
        // remain unbound (a fresh variable not equal to the caller's Y).
        Term? c = sol["C"];
        var ct = Assert.IsType<CompoundTerm>(c);
        Assert.Equal("f", ct.Functor);
        Assert.Equal(3, ct.Args.Length);
        // First two args share — must be the same VarTerm name.
        var v0 = Assert.IsType<VarTerm>(ct.Args[0]);
        var v1 = Assert.IsType<VarTerm>(ct.Args[1]);
        Assert.Equal(v0.Name, v1.Name);
        // Third arg is a different fresh var (and definitely not 'bound').
        var v2 = Assert.IsType<VarTerm>(ct.Args[2]);
        Assert.NotEqual(v0.Name, v2.Name);
    }
}
