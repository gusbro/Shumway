using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Xunit;

namespace Shumway.Tests.Compiler.Parsing;

public class ParserTests
{
    // ---------- Helpers ----------

    private static Term Parse(string source) =>
        new Parser(new global::Shumway.Compiler.Lexer.Lexer(source)).ReadTerm();

    private static Term ParseClause(string source) =>
        new Parser(new global::Shumway.Compiler.Lexer.Lexer(source)).ReadClauseTerm();

    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Var(string n) => new VarTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term Cmp(string f, params Term[] args) => new CompoundTerm(f, args);

    // ---------- Primaries ----------

    [Fact]
    public void Atom_Bare()
    {
        Assert.Equal(Atom("foo"), Parse("foo"));
    }

    [Fact]
    public void Variable_NamedAndAnonymous()
    {
        Assert.Equal(Var("X"), Parse("X"));
        Assert.Equal(Var("_"), Parse("_"));
        Assert.Equal(Var("_a"), Parse("_a"));
    }

    [Fact]
    public void Integer_PositiveValue()
    {
        Assert.Equal(Int(42), Parse("42"));
    }

    [Fact]
    public void Integer_NegativeLiteral_ParsesAsIntegerCell()
    {
        // Chunk 37: `-3` collapses to Integer(-3) instead of the compound
        // `-(3)`. This matches what almost every Prolog system does in
        // practice — the strict ISO reading produces a compound, but every
        // built-in we ship (integer/1, arg/3, between/3, …) expects the
        // numeric literal here.
        Assert.Equal(Int(-3), Parse("-3"));
    }

    [Fact]
    public void Integer_NegativeWithSpace_AlsoCollapses()
    {
        // `- 3` (with a space) is the same — the parser collapses the
        // minus + numeric pair regardless of intervening whitespace.
        Assert.Equal(Int(-3), Parse("- 3"));
    }

    [Fact]
    public void Integer_ExplicitMinusCompound_StaysCompound()
    {
        // Explicit `-(3)` (parens after the minus) is still the unary-minus
        // compound — the prefix-op disambiguation rule blocks the collapse
        // when '(' follows.
        Assert.Equal(Cmp("-", Int(3)), Parse("-(3)"));
    }

    [Fact]
    public void Float_Literal()
    {
        Assert.Equal(new FloatTerm(3.14), Parse("3.14"));
    }

    [Fact]
    public void String_Literal()
    {
        Assert.Equal(new StringTerm("hello"), Parse("\"hello\""));
    }

    // ---------- Compound terms ----------

    [Fact]
    public void Compound_NoArgsIsAnError()
    {
        // `foo()` is NOT valid Prolog — atoms with zero args have no paren form.
        Assert.Throws<ParseException>(() => Parse("foo()"));
    }

    [Fact]
    public void Compound_SingleArg()
    {
        Assert.Equal(Cmp("foo", Atom("a")), Parse("foo(a)"));
    }

    [Fact]
    public void Compound_MultipleArgs()
    {
        Assert.Equal(Cmp("foo", Atom("a"), Atom("b"), Atom("c")), Parse("foo(a, b, c)"));
    }

    [Fact]
    public void Compound_NestedArgs()
    {
        Assert.Equal(
            Cmp("foo", Cmp("bar", Atom("x")), Atom("y")),
            Parse("foo(bar(x), y)"));
    }

    [Fact]
    public void Compound_CommaInsideArgIsSeparator_NotOperator()
    {
        // foo(a, b) — three tokens, two args; the comma is a separator.
        Term t = Parse("foo(a, b)");
        Assert.Equal(Cmp("foo", Atom("a"), Atom("b")), t);
    }

    [Fact]
    public void Compound_CommaAsOperatorRequiresParens()
    {
        // foo((a, b)) — outer compound has one arg, which is the comma compound.
        Assert.Equal(
            Cmp("foo", Cmp(",", Atom("a"), Atom("b"))),
            Parse("foo((a, b))"));
    }

    // ---------- Lists ----------

    [Fact]
    public void List_Empty()
    {
        Assert.Equal(Atom("[]"), Parse("[]"));
    }

    [Fact]
    public void List_SingleElement()
    {
        Assert.Equal(Cmp(".", Atom("a"), Atom("[]")), Parse("[a]"));
    }

    [Fact]
    public void List_ThreeElements()
    {
        Term expected =
            Cmp(".", Atom("a"),
                Cmp(".", Atom("b"),
                    Cmp(".", Atom("c"), Atom("[]"))));
        Assert.Equal(expected, Parse("[a, b, c]"));
    }

    [Fact]
    public void List_WithBarTail()
    {
        Assert.Equal(Cmp(".", Var("H"), Var("T")), Parse("[H | T]"));
    }

    [Fact]
    public void List_MultiHeadWithBarTail()
    {
        Term expected =
            Cmp(".", Atom("a"),
                Cmp(".", Atom("b"), Var("T")));
        Assert.Equal(expected, Parse("[a, b | T]"));
    }

    // ---------- Braces ----------

    [Fact]
    public void Brace_Empty()
    {
        Assert.Equal(Atom("{}"), Parse("{}"));
    }

    [Fact]
    public void Brace_WithBody()
    {
        Assert.Equal(Cmp("{}", Cmp(",", Atom("a"), Atom("b"))), Parse("{a, b}"));
    }

    // ---------- Operator precedence ----------

    [Fact]
    public void Operator_Plus_LeftAssociative()
    {
        // a + b + c → +(+(a, b), c)
        Assert.Equal(
            Cmp("+", Cmp("+", Atom("a"), Atom("b")), Atom("c")),
            Parse("a + b + c"));
    }

    [Fact]
    public void Operator_PlusTimes_PrecedenceCorrect()
    {
        // a + b * c → +(a, *(b, c))   (* binds tighter)
        Assert.Equal(
            Cmp("+", Atom("a"), Cmp("*", Atom("b"), Atom("c"))),
            Parse("a + b * c"));
    }

    [Fact]
    public void Operator_TimesPlus_PrecedenceCorrect()
    {
        // a * b + c → +(*(a, b), c)
        Assert.Equal(
            Cmp("+", Cmp("*", Atom("a"), Atom("b")), Atom("c")),
            Parse("a * b + c"));
    }

    [Fact]
    public void Operator_Comma_RightAssociative()
    {
        // (a, b, c) → ,(a, ,(b, c))
        Assert.Equal(
            Cmp(",", Atom("a"), Cmp(",", Atom("b"), Atom("c"))),
            Parse("(a, b, c)"));
    }

    [Fact]
    public void Operator_XfxNonAssociative_Rejects()
    {
        // a = b = c is a syntax error because = is xfx.
        // The parser builds (a = b), then sees `=` but can't combine (700 > 699
        // for the left arg of an xfx). The leftover `= c` errors out at
        // ReadClauseTerm (expecting '.').
        Assert.Throws<ParseException>(() => ParseClause("a = b = c."));
    }

    [Fact]
    public void Operator_DirectivePrefix()
    {
        // :- dynamic foo/2.
        Term expected = Cmp(":-", Cmp("dynamic", Cmp("/", Atom("foo"), Int(2))));
        Assert.Equal(expected, ParseClause(":- dynamic foo/2."));
    }

    [Fact]
    public void Operator_Clause_Rule()
    {
        // p(X) :- q(X), r(X).
        Term expected =
            Cmp(":-",
                Cmp("p", Var("X")),
                Cmp(",", Cmp("q", Var("X")), Cmp("r", Var("X"))));
        Assert.Equal(expected, ParseClause("p(X) :- q(X), r(X)."));
    }

    [Fact]
    public void Operator_Unification()
    {
        Assert.Equal(Cmp("=", Var("X"), Atom("foo")), Parse("X = foo"));
    }

    [Fact]
    public void Operator_IsArithmetic()
    {
        // X is 1 + 2 * 3 → is(X, +(1, *(2, 3)))
        Assert.Equal(
            Cmp("is", Var("X"),
                Cmp("+", Int(1), Cmp("*", Int(2), Int(3)))),
            Parse("X is 1 + 2 * 3"));
    }

    // ---------- Prefix / postfix ----------

    [Fact]
    public void Prefix_UnaryMinus_AppliesToFollowingTerm()
    {
        Assert.Equal(Cmp("-", Atom("a")), Parse("- a"));
    }

    [Fact]
    public void Prefix_AfterInfix_OperandIsTheRightTerm()
    {
        // a + -b → +(a, -(b))
        Assert.Equal(
            Cmp("+", Atom("a"), Cmp("-", Atom("b"))),
            Parse("a + -b"));
    }

    [Fact]
    public void Prefix_Not()
    {
        // \+ foo → \+(foo)
        Assert.Equal(Cmp("\\+", Atom("foo")), Parse("\\+ foo"));
    }

    [Fact]
    public void Atom_FollowedByCommaIsStandaloneAtom_NotPrefix()
    {
        // foo(-, X) — the first arg is the atom `-`, not a prefix op missing operand.
        Assert.Equal(Cmp("foo", Atom("-"), Var("X")), Parse("foo(-, X)"));
    }

    // ---------- Errors ----------

    [Fact]
    public void Error_UnclosedParen_Throws()
    {
        Assert.Throws<ParseException>(() => Parse("foo(a, b"));
    }

    [Fact]
    public void Error_UnclosedBracket_Throws()
    {
        Assert.Throws<ParseException>(() => Parse("[a, b"));
    }

    [Fact]
    public void Error_MissingDotOnClause_Throws()
    {
        Assert.Throws<ParseException>(() => ParseClause("foo(a)"));
    }

    [Fact]
    public void Error_UnexpectedClosingParen_Throws()
    {
        Assert.Throws<ParseException>(() => Parse(")"));
    }

    // ---------- Realistic clauses ----------

    [Fact]
    public void Clause_FactWithList()
    {
        // numbers([1, 2, 3]).
        Term expected =
            Cmp("numbers",
                Cmp(".", Int(1),
                    Cmp(".", Int(2),
                        Cmp(".", Int(3), Atom("[]")))));
        Assert.Equal(expected, ParseClause("numbers([1, 2, 3])."));
    }

    [Fact]
    public void Clause_RuleWithCut()
    {
        // p(X) :- q(X), !, r(X).
        Term expected =
            Cmp(":-",
                Cmp("p", Var("X")),
                Cmp(",",
                    Cmp("q", Var("X")),
                    Cmp(",",
                        Atom("!"),
                        Cmp("r", Var("X")))));
        Assert.Equal(expected, ParseClause("p(X) :- q(X), !, r(X)."));
    }

    [Fact]
    public void Clause_RuleWithDisjunction()
    {
        // p(X) :- (q(X) ; r(X)).
        Term expected =
            Cmp(":-",
                Cmp("p", Var("X")),
                Cmp(";", Cmp("q", Var("X")), Cmp("r", Var("X"))));
        Assert.Equal(expected, ParseClause("p(X) :- (q(X) ; r(X))."));
    }

    [Fact]
    public void Clause_DcgArrow()
    {
        // foo --> bar.
        Assert.Equal(Cmp("-->", Atom("foo"), Atom("bar")), ParseClause("foo --> bar."));
    }
}
