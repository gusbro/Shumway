using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Xunit;

namespace Shumway.Tests.Compiler.Parsing;

public class ClauseReaderTests
{
    private static List<Clause> ReadAll(string source) =>
        new ClauseReader(source).ReadAll().ToList();

    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Var(string n) => new VarTerm(n);
    private static Term Int(long v) => new IntTerm(v);
    private static Term Cmp(string f, params Term[] args) => new CompoundTerm(f, args);

    // ---------- Empty / trivial ----------

    [Fact]
    public void Empty_Source_YieldsNoClauses()
    {
        Assert.Empty(ReadAll(""));
    }

    [Fact]
    public void WhitespaceAndComments_OnlySource_YieldsNoClauses()
    {
        Assert.Empty(ReadAll("  % nothing\n  /* still nothing */  "));
    }

    // ---------- Classification ----------

    [Fact]
    public void Fact_IsClassifiedAsFact()
    {
        var clauses = ReadAll("foo.");
        Assert.Single(clauses);
        Assert.Equal(ClauseKind.Fact, clauses[0].Kind);
        Assert.Equal(Atom("foo"), clauses[0].Term);
    }

    [Fact]
    public void Compound_WithoutDirectiveFunctor_IsFact()
    {
        var clauses = ReadAll("p(a, 1).");
        Assert.Equal(ClauseKind.Fact, clauses[0].Kind);
    }

    [Fact]
    public void Rule_IsClassifiedAsRule()
    {
        var clauses = ReadAll("p(X) :- q(X).");
        Assert.Single(clauses);
        Assert.Equal(ClauseKind.Rule, clauses[0].Kind);
        Assert.Equal(
            Cmp(":-", Cmp("p", Var("X")), Cmp("q", Var("X"))),
            clauses[0].Term);
    }

    [Fact]
    public void Directive_PrefixIsClassifiedAsDirective()
    {
        var clauses = ReadAll(":- dynamic foo/2.");
        Assert.Single(clauses);
        Assert.Equal(ClauseKind.Directive, clauses[0].Kind);
    }

    [Fact]
    public void DcgRule_IsClassifiedAsDcgRule()
    {
        var clauses = ReadAll("greeting --> [hello], [world].");
        Assert.Single(clauses);
        Assert.Equal(ClauseKind.DcgRule, clauses[0].Kind);
    }

    // ---------- Multi-clause sources ----------

    [Fact]
    public void MultipleClauses_AreYieldedInOrder()
    {
        var clauses = ReadAll(
            "p(a).\n" +
            "p(b).\n" +
            "p(X) :- q(X).\n");

        Assert.Equal(3, clauses.Count);
        Assert.Equal(Cmp("p", Atom("a")), clauses[0].Term);
        Assert.Equal(Cmp("p", Atom("b")), clauses[1].Term);
        Assert.Equal(ClauseKind.Rule, clauses[2].Kind);
    }

    [Fact]
    public void MultipleClauses_WithCommentsAndBlankLines()
    {
        var clauses = ReadAll(
            "% header\n\n" +
            "first.\n" +
            "/* mid-file block\n   comment */\n\n" +
            "second(X) :- X > 0.\n");
        Assert.Equal(2, clauses.Count);
        Assert.Equal(Atom("first"), clauses[0].Term);
        Assert.Equal(ClauseKind.Rule, clauses[1].Kind);
    }

    // ---------- Operator directive ----------

    [Fact]
    public void OpDirective_DefinesNewInfixOperator_AffectsLaterClauses()
    {
        // Declare ===> as a new infix operator at precedence 700, xfx.
        // Then use it in the next clause — must parse without error.
        var clauses = ReadAll(
            ":- op(700, xfx, ===>).\n" +
            "a ===> b.\n");

        Assert.Equal(2, clauses.Count);
        Assert.Equal(ClauseKind.Directive, clauses[0].Kind);
        Assert.Equal(ClauseKind.Fact, clauses[1].Kind);
        Assert.Equal(Cmp("===>", Atom("a"), Atom("b")), clauses[1].Term);
    }

    [Fact]
    public void OpDirective_DefinesPrefixOperator_AffectsLaterClauses()
    {
        var clauses = ReadAll(
            ":- op(900, fy, maybe).\n" +
            "maybe foo.\n");

        Assert.Equal(Cmp("maybe", Atom("foo")), clauses[1].Term);
    }

    [Fact]
    public void OpDirective_ListOfNames_DefinesAllOfThem()
    {
        var clauses = ReadAll(
            ":- op(700, xfx, [<:, :>, <:>]).\n" +
            "a <: b.\n" +
            "a :> b.\n" +
            "a <:> b.\n");

        Assert.Equal(4, clauses.Count);
        Assert.Equal(Cmp("<:", Atom("a"), Atom("b")), clauses[1].Term);
        Assert.Equal(Cmp(":>", Atom("a"), Atom("b")), clauses[2].Term);
        Assert.Equal(Cmp("<:>", Atom("a"), Atom("b")), clauses[3].Term);
    }

    [Fact]
    public void OpDirective_PrecedenceZero_RemovesPriorDefinition()
    {
        // Define a custom op, use it, then remove the definition and
        // the same source no longer parses it as an operator.
        var firstReader = new ClauseReader(":- op(700, xfx, ===>).\n :- op(0, xfx, ===>).\na ===> b.\n");
        Assert.Throws<ParseException>(() => firstReader.ReadAll().ToList());
    }

    // ---------- Op directive error cases ----------

    [Fact]
    public void OpDirective_NonIntegerPrecedence_Throws()
    {
        Assert.Throws<ParseException>(() =>
            ReadAll(":- op(seven, xfx, foo).\n"));
    }

    [Fact]
    public void OpDirective_BadType_Throws()
    {
        Assert.Throws<ParseException>(() =>
            ReadAll(":- op(700, wibble, foo).\n"));
    }

    [Fact]
    public void OpDirective_NameNotAtomOrList_Throws()
    {
        Assert.Throws<ParseException>(() =>
            ReadAll(":- op(700, xfx, 42).\n"));
    }

    // ---------- Position tracking ----------

    [Fact]
    public void Clause_CarriesPositionOfFirstToken()
    {
        var clauses = ReadAll("\n  foo.\n");
        Assert.Equal(2, clauses[0].Position.Line);
        Assert.Equal(3, clauses[0].Position.Column);
    }

    [Fact]
    public void MultipleClauses_PositionsAdvance()
    {
        var clauses = ReadAll("a.\nb.\n");
        Assert.Equal(1, clauses[0].Position.Line);
        Assert.Equal(2, clauses[1].Position.Line);
    }

    // ---------- Realistic short program ----------

    [Fact]
    public void ShortProgram_RoundTripsAllKinds()
    {
        var clauses = ReadAll(
            ":- dynamic counter/1.\n" +
            "counter(0).\n" +
            "increment(N) :- counter(C), retract(counter(C)), N is C + 1, assertz(counter(N)).\n" +
            "list_sum([], 0).\n" +
            "list_sum([H|T], S) :- list_sum(T, R), S is H + R.\n");
        Assert.Equal(5, clauses.Count);
        Assert.Equal(ClauseKind.Directive, clauses[0].Kind);
        Assert.Equal(ClauseKind.Fact, clauses[1].Kind);
        Assert.Equal(ClauseKind.Rule, clauses[2].Kind);
        Assert.Equal(ClauseKind.Fact, clauses[3].Kind);
        Assert.Equal(ClauseKind.Rule, clauses[4].Kind);
    }
}
