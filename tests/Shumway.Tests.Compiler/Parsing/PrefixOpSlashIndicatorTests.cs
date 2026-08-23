using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Xunit;
using SourceLexer = Shumway.Compiler.Lexer.Lexer;

namespace Shumway.Tests.Compiler.Parsing;

/// <summary>
/// ISO §6.3.4 disambiguation: an atom that is both a prefix operator
/// (e.g. <c>not</c> fy 900, <c>dynamic</c> fx 1150) AND a valid plain
/// atom collides with the predicate-indicator notation <c>name/N</c>.
/// Without the disambiguation, <c>[not/1]</c> parses as the prefix
/// operator <c>not</c> applied to the standalone atom <c>'/'</c>,
/// stranding the integer arity behind it and producing
/// <c>"Expected ']' got integer 1"</c>. With it, <c>not/1</c> resolves
/// as <c>'/'(not, 1)</c> — the indicator the user wrote. SWI / GNU
/// both do this disambiguation.
/// </summary>
public class PrefixOpSlashIndicatorTests
{
    private static Term Parse(string source)
    {
        var parser = new Parser(new SourceLexer(source), OperatorTable.Default());
        return parser.ReadClauseTerm();
    }

    [Fact]
    public void NotSlashOne_InList_ParsesAsIndicator()
    {
        // [not/1] — the prefix-op + indicator pattern from Blint.pl.
        var t = Parse("[not/1].");
        var cons = Assert.IsType<CompoundTerm>(t);
        Assert.Equal(".", cons.Functor);
        var slash = Assert.IsType<CompoundTerm>(cons.Args[0]);
        Assert.Equal("/", slash.Functor);
        Assert.Equal(2, slash.Args.Length);
        Assert.Equal(new AtomTerm("not"), slash.Args[0]);
        Assert.Equal(new IntTerm(1), slash.Args[1]);
    }

    [Fact]
    public void IndicatorListMixedPrefixOps_AllResolve()
    {
        // [not/1, catch/3, ifthen/2, dynamic/2].
        // dynamic and ifthen are NOT prefix operators in the default
        // table; not and catch (no, catch is not prefix either). Only
        // 'not' triggers the disambiguation here, but the others
        // exercise the indicator-list shape end to end.
        var t = Parse("[not/1, catch/3, ifthen/2].");
        var cons = Assert.IsType<CompoundTerm>(t);
        // Walk the list, collect each Name/Arity.
        var names = new List<string>();
        var arities = new List<long>();
        Term cursor = t;
        while (cursor is CompoundTerm c && c.Functor == "." && c.Args.Length == 2)
        {
            var slash = Assert.IsType<CompoundTerm>(c.Args[0]);
            Assert.Equal("/", slash.Functor);
            names.Add(((AtomTerm)slash.Args[0]).Name);
            arities.Add(((IntTerm)slash.Args[1]).Value);
            cursor = c.Args[1];
        }
        Assert.Equal(new[] { "not", "catch", "ifthen" }, names);
        Assert.Equal(new long[] { 1, 3, 2 }, arities);
    }

    [Fact]
    public void DynamicAtomSlashArity_AsArgument_ResolvesToIndicator()
    {
        // dynamic/0 written as a bare argument — the same pattern as
        // not/1 but with the fx-1150 directive-head operator.
        // Wrap it in a structural compound to avoid the operator-as-
        // clause-prefix interpretation that ":-" expects.
        var t = Parse("p(dynamic/0).");
        var c = Assert.IsType<CompoundTerm>(t);
        var slash = Assert.IsType<CompoundTerm>(c.Args[0]);
        Assert.Equal("/", slash.Functor);
        Assert.Equal(new AtomTerm("dynamic"), slash.Args[0]);
        Assert.Equal(new IntTerm(0), slash.Args[1]);
    }

    [Fact]
    public void PrefixOpFollowedByQuotedSymbolic_StillTakesPrefix()
    {
        // :- public '#='/2. — the clpfd shape. '#=' is a quoted
        // atom (TokenKind.Atom text "#="), registered as xfx 700
        // infix elsewhere. The disambiguation must NOT trip here:
        // 'public' is acting as the fx-1150 prefix operator and its
        // operand is '#='/2.
        var t = Parse(":- public '#='/2.");
        var dir = Assert.IsType<CompoundTerm>(t);
        Assert.Equal(":-", dir.Functor);
        Assert.Single(dir.Args);
        var pub = Assert.IsType<CompoundTerm>(dir.Args[0]);
        Assert.Equal("public", pub.Functor);
        Assert.Single(pub.Args);
        var slash = Assert.IsType<CompoundTerm>(pub.Args[0]);
        Assert.Equal("/", slash.Functor);
        Assert.Equal(new AtomTerm("#="), slash.Args[0]);
        Assert.Equal(new IntTerm(2), slash.Args[1]);
    }

    [Fact]
    public void PrefixOpNotFollowedByCompound_StillTakesPrefix()
    {
        // 'not member(X, L)' — the operand starts with an alpha atom
        // (member), the / disambiguation does not engage, and the
        // prefix form binds the conjunction. `not` must be DECLARED an
        // operator first (the default table keeps it a plain atom, like
        // ISO/GNU/SWI; Arity sources get it via arity_compat).
        var ops = OperatorTable.Default();
        ops.Define("not", 900, OperatorType.Fy);
        var t = new Parser(new SourceLexer("not member(X, L)."), ops)
            .ReadClauseTerm();
        var c = Assert.IsType<CompoundTerm>(t);
        Assert.Equal("not", c.Functor);
        Assert.Single(c.Args);
        var inner = Assert.IsType<CompoundTerm>(c.Args[0]);
        Assert.Equal("member", inner.Functor);
    }

    [Fact]
    public void GroupedDynamicDirective_CommaSeparatedSpecs_Parses()
    {
        // :- dynamic a/0, b/1, c/2. — GNU Prolog's grouped form.
        // Should parse as the conjunction (a/0, (b/1, c/2)) — each
        // leaf is an indicator. The ShmoCompiler / engine then
        // walks the conjunction to extract the list.
        var t = Parse(":- dynamic a/0, b/1, c/2.");
        var dir = Assert.IsType<CompoundTerm>(t);
        Assert.Equal(":-", dir.Functor);
        var dyn = Assert.IsType<CompoundTerm>(dir.Args[0]);
        Assert.Equal("dynamic", dyn.Functor);
        // dyn's single argument is the conjunction.
        var conj = Assert.IsType<CompoundTerm>(dyn.Args[0]);
        Assert.Equal(",", conj.Functor);
        // The leftmost leaf is a/0.
        var a = Assert.IsType<CompoundTerm>(conj.Args[0]);
        Assert.Equal("/", a.Functor);
        Assert.Equal(new AtomTerm("a"), a.Args[0]);
        Assert.Equal(new IntTerm(0), a.Args[1]);
    }
}
