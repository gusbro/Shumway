using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 40: multi-solution enumeration for the predicates we used to
/// fake first-solution (member/2, clause/2, current_predicate/1) plus
/// the string/atom builtins needed by grammar-style code
/// (split_string/4, string_concat/3, …).
///
/// <para>The multi-solution predicates moved into the engine's
/// <c>'$prelude'</c> module so they can lean on the standard WAM
/// choice-point machinery rather than carrying a builtin-internal
/// state machine. The C# side is reduced to two enumeration-style
/// helper builtins (<c>'$all_clauses_of'/2</c>,
/// <c>'$all_predicate_indicators'/1</c>) that materialise candidate
/// lists; Prolog's own <c>member/2</c> does the iteration with
/// natural backtracking.</para>
/// </summary>
public class Chunk40Tests
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    // A double-quoted literal reaches C# as the LIST it is (ADR-047 decision 6):
    // the representation is not observable at the boundary, so what arrives is
    // the same whether or not the engine stored it packed.
    private static Term Pstr(string s)
    {
        Term t = new AtomTerm("[]");
        for (int i = s.Length - 1; i >= 0; i--)
            t = new CompoundTerm(".", new Term[] { new IntTerm(s[i]), t });
        return t;
    }

    // ============================================================================
    // Multi-solution member/2
    // ============================================================================

    [Fact]
    public void Member_EnumeratesAllSolutions()
    {
        var engine = new PrologEngine();
        var sols = engine.QueryAll("member(X, [a, b, c]).").ToList();
        Assert.Equal(3, sols.Count);
        Assert.Equal(Atom("a"), sols[0]["X"]);
        Assert.Equal(Atom("b"), sols[1]["X"]);
        Assert.Equal(Atom("c"), sols[2]["X"]);
    }

    [Fact]
    public void Member_GroundSucceeds()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("member(b, [a, b, c]).").Success);
        Assert.False(engine.Query("member(z, [a, b, c]).").Success);
    }

    [Fact]
    public void Member_InsideFindallCollectsAll()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("findall(X, member(X, [1, 2, 3]), L).");
        Assert.True(sol.Success);
        // L = [1, 2, 3]
        Assert.Equal(
            new CompoundTerm(".", new Term[] {
                Int(1),
                new CompoundTerm(".", new Term[] {
                    Int(2),
                    new CompoundTerm(".", new Term[] { Int(3), Atom("[]") })
                })
            }),
            sol["L"]);
    }

    [Fact]
    public void Member_InsideForallStylePatternFailsCorrectly()
    {
        // Classic Prolog idiom: member(X, List), \+ p(X) — succeeds if any X
        // doesn't satisfy p. With first-solution member this used to be
        // unusable; with multi-sol it works.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public small/1.
            small(1). small(2). small(3).
            """);
        // Find any member of [1,2,3,10] that is NOT a small/1.
        var sol = engine.Query("member(X, [1, 2, 3, 10]), \\+ small(X).");
        Assert.True(sol.Success);
        Assert.Equal(Int(10), sol["X"]);
    }

    // ============================================================================
    // Multi-solution clause/2
    // ============================================================================

    [Fact]
    public void Clause_EnumeratesAllStaticClauses()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public colour/1.
            colour(red). colour(green). colour(blue).
            """);
        var sols = engine.QueryAll("clause(colour(X), true).").ToList();
        Assert.Equal(3, sols.Count);
        Assert.Equal(Atom("red"), sols[0]["X"]);
        Assert.Equal(Atom("green"), sols[1]["X"]);
        Assert.Equal(Atom("blue"), sols[2]["X"]);
    }

    [Fact]
    public void Clause_EnumeratesDynamicClausesInAssertionOrder()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic note/1.");
        engine.Query("assertz(note(first)).");
        engine.Query("assertz(note(second)).");
        engine.Query("assertz(note(third)).");

        var sols = engine.QueryAll("clause(note(X), true).").ToList();
        Assert.Equal(3, sols.Count);
        Assert.Equal(Atom("first"), sols[0]["X"]);
        Assert.Equal(Atom("second"), sols[1]["X"]);
        Assert.Equal(Atom("third"), sols[2]["X"]);
    }

    [Fact]
    public void Clause_BindsBodyForRules()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public greet/1.
            greet(X) :- write(X), nl.
            """);
        var sol = engine.Query("clause(greet(_), B).");
        Assert.True(sol.Success);
        var body = Assert.IsType<CompoundTerm>(sol["B"]);
        Assert.Equal(",", body.Functor);
    }

    // ============================================================================
    // Multi-solution current_predicate/1
    // ============================================================================

    [Fact]
    public void CurrentPredicate_GroundIndicator_StillSucceedsForKnown()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public foo/1.
            foo(a).
            """);
        Assert.True(engine.Query("current_predicate(foo/1).").Success);
        // A builtin is NOT a current_predicate (§8.8.2, GNU-verified);
        // predicate_property/2 is the way to ask about one.
        Assert.True(engine.Query("\\+ current_predicate(is/2).").Success);
        Assert.True(engine.Query("predicate_property(is(_, _), built_in).").Success);
    }

    [Fact]
    public void CurrentPredicate_EnumeratesUserPredicatesOnly()
    {
        // §8.8.2 (GNU-verified): current_predicate/1 enumerates
        // USER-DEFINED procedures. Builtins and prelude library
        // predicates are excluded — predicate_property/2 answers for
        // those.
        var engine = new PrologEngine();
        engine.ConsultString("c40_user(1). c40_other(a, b).");
        var names = engine.QueryAll("current_predicate(X).")
            .Select(s => s["X"]).ToHashSet();
        Assert.Contains(new CompoundTerm("/", new Term[] { Atom("c40_user"), Int(1) }), names);
        Assert.Contains(new CompoundTerm("/", new Term[] { Atom("c40_other"), Int(2) }), names);
        Assert.DoesNotContain(new CompoundTerm("/", new Term[] { Atom("is"), Int(2) }), names);
        Assert.DoesNotContain(new CompoundTerm("/", new Term[] { Atom("member"), Int(2) }), names);
        Assert.True(engine.Query("\\+ current_predicate(atom/1).").Success);
        Assert.True(engine.Query("predicate_property(atom(_), built_in).").Success);
    }

    [Fact]
    public void CurrentPredicate_TypeErrorOnNonIndicator()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<ShumwayPrologException>(
            () => engine.Query("current_predicate(not_a_slash)."));
        var ct = Assert.IsType<CompoundTerm>(ex.Term);
        Assert.Equal("error", ct.Functor);
        var inner = Assert.IsType<CompoundTerm>(ct.Args[0]);
        Assert.Equal("type_error", inner.Functor);
    }

    // ============================================================================
    // String builtins
    // ============================================================================

    [Fact]
    public void StringLength_AcceptsPstrAndAtom()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(5), engine.Query("string_length(\"hello\", L).")["L"]);
        Assert.Equal(Int(5), engine.Query("string_length(hello, L).")["L"]);
        Assert.Equal(Int(0), engine.Query("string_length(\"\", L).")["L"]);
    }

    [Fact]
    public void StringConcat_JoinsTwoInputs()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("string_concat(\"foo\", \"bar\", X).");
        Assert.True(sol.Success);
        Assert.Equal(Pstr("foobar"), sol["X"]);
    }

    [Fact]
    public void StringConcat_AtomInputsCoerceToString()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("string_concat(hello, ' world', X).");
        Assert.True(sol.Success);
        Assert.Equal(Pstr("hello world"), sol["X"]);
    }

    [Fact]
    public void StringChars_StringToChars()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("string_chars(\"abc\", L).");
        Assert.True(sol.Success);
        // L = [a, b, c]
        var lt = sol["L"];
        Assert.Equal(
            new CompoundTerm(".", new Term[] {
                Atom("a"),
                new CompoundTerm(".", new Term[] {
                    Atom("b"),
                    new CompoundTerm(".", new Term[] { Atom("c"), Atom("[]") })
                })
            }), lt);
    }

    [Fact]
    public void StringChars_CharsToString()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("string_chars(S, [h, i]).");
        Assert.True(sol.Success);
        Assert.Equal(Pstr("hi"), sol["S"]);
    }

    [Fact]
    public void StringCodes_StringToCodes()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("string_codes(\"AB\", L).");
        Assert.True(sol.Success);
        Assert.Equal(
            new CompoundTerm(".", new Term[] {
                Int('A'),
                new CompoundTerm(".", new Term[] {
                    Int('B'), Atom("[]")
                })
            }), sol["L"]);
    }

    [Fact]
    public void SplitString_SimpleSplit()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("split_string(\"a,b,c\", \",\", \"\", L).");
        Assert.True(sol.Success);
        Assert.Equal(
            new CompoundTerm(".", new Term[] {
                Pstr("a"),
                new CompoundTerm(".", new Term[] {
                    Pstr("b"),
                    new CompoundTerm(".", new Term[] { Pstr("c"), Atom("[]") })
                })
            }), sol["L"]);
    }

    [Fact]
    public void SplitString_TrimsPadChars()
    {
        // Split on comma, then trim spaces.
        var engine = new PrologEngine();
        var sol = engine.Query("split_string(\" a , b , c \", \",\", \" \", L).");
        Assert.True(sol.Success);
        Assert.Equal(
            new CompoundTerm(".", new Term[] {
                Pstr("a"),
                new CompoundTerm(".", new Term[] {
                    Pstr("b"),
                    new CompoundTerm(".", new Term[] { Pstr("c"), Atom("[]") })
                })
            }), sol["L"]);
    }

    [Fact]
    public void SplitString_EmptySeps_TreatsAsWholeStringTrimmed()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("split_string(\"  hello  \", \"\", \" \", L).");
        Assert.True(sol.Success);
        Assert.Equal(
            new CompoundTerm(".", new Term[] { Pstr("hello"), Atom("[]") }),
            sol["L"]);
    }

    [Fact]
    public void UpcaseAtom_ConvertsCase()
    {
        var engine = new PrologEngine();
        Assert.Equal(Atom("HELLO"), engine.Query("upcase_atom(hello, X).")["X"]);
    }

    [Fact]
    public void DowncaseAtom_ConvertsCase()
    {
        var engine = new PrologEngine();
        Assert.Equal(Atom("world"), engine.Query("downcase_atom('World', X).")["X"]);
    }
}
