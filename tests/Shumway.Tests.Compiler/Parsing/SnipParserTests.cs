using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Xunit;

namespace Shumway.Tests.Compiler.Parsing;

/// <summary>
/// Snip syntax — <c>[! Goal !]</c>. An Arity-Prolog construct that
/// commits to the first solution of <c>Goal</c>: backtracking is
/// permitted internally, but once the snip exits successfully its
/// internal choice points are pruned, so a later failure skips past
/// the snip entirely rather than re-entering it.
///
/// <para>Implemented as parser-level desugaring to <c>once((Goal))</c>
/// so the rest of the engine (transforms, compiler, interpreter)
/// sees a plain Prolog goal and needs no awareness of the source
/// syntax.</para>
///
/// <para>A real snip always has a goal after the opening <c>!</c>. A bare
/// <c>!</c> that immediately closes or continues the list — <c>[!]</c>,
/// <c>[!, X]</c>, <c>[! | T]</c> — is an ordinary list with the cut atom as an
/// element (ISO-valid; Scryer's clpz uses it), not a snip.</para>
/// </summary>
public class SnipParserTests
{
    private static Term Parse(string source) =>
        new Parser(new global::Shumway.Compiler.Lexer.Lexer(source)).ReadTerm();

    private static Term List(params Term[] items)
    {
        Term t = new AtomTerm("[]");
        for (int i = items.Length - 1; i >= 0; i--)
            t = new CompoundTerm(".", new[] { items[i], t });
        return t;
    }

    [Fact]
    public void BareCut_AsSoleListElement_IsAList()
    {
        // [!] -> '.'(!, []), NOT a snip.
        Assert.Equal(List(new AtomTerm("!")), Parse("[!]"));
    }

    [Fact]
    public void BareCut_AsFirstListElement_IsAList()
    {
        // [!, b] -> [!, b]
        Assert.Equal(List(new AtomTerm("!"), new AtomTerm("b")), Parse("[!, b]"));
    }

    [Fact]
    public void BareCut_AsListHeadWithTail_IsAList()
    {
        // [! | T] -> '.'(!, T)
        Assert.Equal(
            new CompoundTerm(".", new Term[] { new AtomTerm("!"), new VarTerm("T") }),
            Parse("[! | T]"));
    }

    [Fact]
    public void EmptyBodyless_SimpleGoal_DesugarsToOnce()
    {
        // [! p(X) !] -> once(p(X))
        var actual = Parse("[! p(X) !]");
        var expected = new CompoundTerm("once",
            new Term[] { new CompoundTerm("p", new Term[] { new VarTerm("X") }) });
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Conjunction_InsideSnip_IsTheOnceArgument()
    {
        // [! a, b !] -> once((a, b))
        var actual = Parse("[! a, b !]");
        var inner = new CompoundTerm(",",
            new Term[] { new AtomTerm("a"), new AtomTerm("b") });
        Assert.Equal(new CompoundTerm("once", new Term[] { inner }), actual);
    }

    [Fact]
    public void NoWhitespace_AroundBangs_StillParses()
    {
        // [!a, b!] -> once((a, b))   — the bracket-bang pair is greedy
        // regardless of whitespace, matching the user's chosen option.
        var actual = Parse("[!a, b!]");
        var inner = new CompoundTerm(",",
            new Term[] { new AtomTerm("a"), new AtomTerm("b") });
        Assert.Equal(new CompoundTerm("once", new Term[] { inner }), actual);
    }

    [Fact]
    public void Nested_Snips_DesugarRecursively()
    {
        // [! [! a !], b !] -> once((once(a), b))
        var actual = Parse("[! [! a !], b !]");
        var inner = new CompoundTerm("once", new Term[] { new AtomTerm("a") });
        var middle = new CompoundTerm(",", new Term[] { inner, new AtomTerm("b") });
        var expected = new CompoundTerm("once", new Term[] { middle });
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CutInside_Snip_IsLiteralCutAtom()
    {
        // [! a, !, b !] -> once((a, (!, b)))   — the `!` between commas
        // is the cut atom, which once/1 will scope to the snip boundary.
        var actual = Parse("[! a, !, b !]");
        // ,/2 is right-associative (xfy 1000), so a, !, b parses as
        // ,(a, ,(!, b)).
        var inner = new CompoundTerm(",", new Term[]
        {
            new AtomTerm("a"),
            new CompoundTerm(",", new Term[] { new AtomTerm("!"), new AtomTerm("b") }),
        });
        Assert.Equal(new CompoundTerm("once", new Term[] { inner }), actual);
    }

    [Fact]
    public void PlainList_StillParsesNormally()
    {
        // [a, b, c] is unaffected.
        var actual = Parse("[a, b, c]");
        var expected = new CompoundTerm(".", new Term[]
        {
            new AtomTerm("a"),
            new CompoundTerm(".", new Term[]
            {
                new AtomTerm("b"),
                new CompoundTerm(".", new Term[]
                {
                    new AtomTerm("c"),
                    new AtomTerm("[]"),
                }),
            }),
        });
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EmptyList_StillParsesAsNil()
    {
        Assert.Equal(new AtomTerm("[]"), Parse("[]"));
    }

    [Fact]
    public void MissingClosingBang_IsParseError()
    {
        Assert.Throws<ParseException>(() => Parse("[! a, b ]"));
    }

    [Fact]
    public void CutAsOperandOfInfix_IsAList()
    {
        // [!-1, !-2] — the token after the '!' is an infix operator (or a
        // sign-folded number), so the '!' is the LEFT OPERAND of a list
        // element, not a snip opener (ISO pairs with a cut key are legal;
        // the Logtalk conformity suite writes them).
        var t = Parse("[!-1, !-2]");
        var cons = Assert.IsType<CompoundTerm>(t);
        var first = Assert.IsType<CompoundTerm>(cons.Args[0]);
        Assert.Equal("-", first.Functor);
        Assert.Equal(new AtomTerm("!"), first.Args[0]);
        Assert.Equal(new IntTerm(1), first.Args[1]);
    }
}
