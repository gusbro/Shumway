using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// The default operator table, measured against GNU Prolog, SWI and Scryer.
/// ISO 13211-1 Table 7 is the floor; where the standard is silent the
/// de-facto table is the whole authority, so these pin the places the three
/// systems agree and Shumway once did not.
/// </summary>
public class OperatorTableAlignmentConformance
{
    private static void Succeeds(string query)
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(query).Success, $"Query failed: {query}");
    }

    [Fact]
    public void ModuleQualifierIsLooserThanSlash()
    {
        // GNU, SWI and Scryer all put `:` at 600 xfy — LOOSER than `/` (400),
        // so a qualified indicator reads as :(Module, /(Name, Arity)).
        Succeeds("current_op(600, xfy, :).");
        Succeeds("X = (m:f/0), X = :(m, /(f, 0)).");
        Succeeds("X = (lists:append/3), X = :(lists, /(append, 3)).");
        // A qualified goal is unaffected either way.
        Succeeds("X = (m:foo(1)), X = :(m, foo(1)).");
        // xfy: right-associative.
        Succeeds("X = (a:b:c), X = :(a, :(b, c)).");
    }

    [Fact]
    public void QualifiedIndicatorDirectivesTakeBothGroupings()
    {
        // The reader accepts the looser grouping AND the tighter one, so a
        // source written for a table that puts `:` below `/` still loads.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- multifile user:mf_both/1.\n:- discontiguous user:dc_both/2.\n");
        Assert.True(engine.Query("current_predicate(mf_both/1).").Success);
        Assert.True(engine.Query("current_predicate(dc_both/2).").Success);
    }

    [Fact]
    public void XorSitsWithTheMultiplicativeOperators()
    {
        // Not an ISO operator: GNU and Scryer do not declare it at all, and
        // SWI — the only system that does — has it at 400 yfx.
        Succeeds("current_op(400, yfx, xor).");
        Succeeds("X is 5 xor 3, X == 6.");
    }

    [Fact]
    public void NonIsoDialectOperatorsAreNotInTheDefaultTable()
    {
        // A non-ISO operator in the INITIAL table changes how a strictly
        // conforming program reads. Scryer's own library declares this one
        // (lib/ops_and_meta_predicates.pl); Shumway's scryer shim does the
        // same, globally, instead of building it in.
        var engine = new PrologEngine();
        Assert.False(engine.Query("current_op(_, _, non_counted_backtracking).").Success);
    }

    [Fact]
    public void BarIsTheDcgOperatorOfTheDefaultTable()
    {
        // TS 13211-3 puts `op(1105, xfy, '|')` in the table, above `;`, and
        // `a|b` denotes '|'(a,b), never ';'(a,b) (Cor.2). The Part 1 table
        // alone has no bar, and the Neumerkel Part 1 suite's row #285
        // (`X=[(a|b)]` a syntax error by default) is knowingly lost, as GNU
        // and SWI lose it. Cor.2 still governs op/3: infix above 1000 only.
        Succeeds("current_op(1105, xfy, '|').");
        Succeeds("atom_to_term('(a|b;c)', T, _), T == '|'(a, ';'(b, c)).");
        Succeeds("atom_to_term('(a|b)', T, _), T \\== (a ; b).");
        // `(G, fail)` under the catch: the query succeeds only when the
        // error is raised, never because G quietly succeeded.
        Succeeds("catch((op(999, xfy, '|'), fail), "
            + "error(permission_error(create, operator, '|'), _), true).");
        // Removed, the bar is a syntax error everywhere, DCG bodies included.
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "op(0, xfy, '|'), "
            + "catch((atom_to_term('(a|b)', _, _), fail), error(syntax_error(_), _), true), "
            + "catch((atom_to_term('(a --> b | c)', _, _), fail), error(syntax_error(_), _), true), "
            + "op(1105, xfy, '|'), atom_to_term('(a --> b | c)', T, _), T = (_ --> '|'(b, c)).").Success);
    }

    [Fact]
    public void BarAsAGoalIsAnUndefinedProcedure()
    {
        // The operator makes `a :- b | c.` read, as the term '|'(b, c) in
        // the body; nothing makes it run. ISO has no '|'/2 control
        // construct, so calling it is the existence error any undefined
        // procedure raises, and not a disjunction.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic(a/0).\nb.\nc.\na :- b | c.\n");
        Assert.True(engine.Query("clause(a, B), B == '|'(b, c).").Success);
        // ('|')/2: an operator atom as an operand takes parentheses (6.3.1.3).
        Assert.True(engine.Query(
            "catch(a, error(existence_error(procedure, ('|')/2), _), true).").Success);
    }

    [Fact]
    public void BarIsTheAlternationConnectiveInsideDcgRules()
    {
        // TS 13211-3: inside a DCG rule `|` is alternation, through the
        // declared operator; in a plain body the same operator reads the
        // same term, which nothing there runs (see the test above).
        var engine = new PrologEngine();
        engine.ConsultString(
            "greeting --> [hello] | [hi].\n"
            + "pair([A,B]) --> ( [A], [B] | [A] ), { B = none }.\n"
            + "opt --> [] | [q].\n");
        Assert.True(engine.Query("phrase(greeting, [hi]).").Success);
        Assert.True(engine.Query("phrase(greeting, [hello]).").Success);
        Assert.True(engine.Query("phrase(opt, []).").Success);
        Assert.True(engine.Query("phrase(pair(_), [x]).").Success);
        Assert.True(engine.Query(
            "atom_to_term('(p :- q | r)', T, _), T == (p :- '|'(q, r)).").Success);
    }
}
