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
/// <para>Trade-off: a list whose first element is the cut atom now
/// must be written <c>[(!), ...]</c> instead of <c>[!, ...]</c> —
/// a pattern that virtually never appears in real code.</para>
/// </summary>
public class SnipParserTests
{
    private static Term Parse(string source) =>
        new Parser(new global::Shumway.Compiler.Lexer.Lexer(source)).ReadTerm();

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
}
