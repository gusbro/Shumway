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
    public void BarIsNotAnOperatorUntilDeclared()
    {
        // ISO Cor.2: `|` may be declared infix with priority > 1000, and then
        // `a|b` denotes '|'(a,b) — NOT ';'(a,b). Undeclared it is a syntax
        // error, which is what the conformity suite pins (and what Scryer
        // does); GNU and SWI declare it up front as an extension.
        Succeeds("catch(atom_to_term('(a|b)', _, _), error(syntax_error(_), _), true).");
        Succeeds("catch(op(999, xfy, '|'), "
            + "error(permission_error(create, operator, '|'), _), true).");
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "op(1105, xfy, '|'), atom_to_term('(a|b;c)', T, _), "
            + "T = '|'(a, ';'(b, c)).").Success);
    }

    [Fact]
    public void BarIsTheAlternationConnectiveInsideDcgRules()
    {
        // TS 13211-3: inside a DCG rule `|` is alternation, whether or not it
        // is a declared operator — and it stays out of ordinary bodies.
        var engine = new PrologEngine();
        engine.ConsultString(
            "greeting --> [hello] | [hi].\n"
            + "pair([A,B]) --> ( [A], [B] | [A] ), { B = none }.\n"
            + "opt --> [] | [q].\n");
        Assert.True(engine.Query("phrase(greeting, [hi]).").Success);
        Assert.True(engine.Query("phrase(greeting, [hello]).").Success);
        Assert.True(engine.Query("phrase(opt, []).").Success);
        Assert.True(engine.Query("phrase(pair(_), [x]).").Success);
        // Not in a plain body: `|` there is still a syntax error.
        Assert.True(engine.Query(
            "catch(atom_to_term('(p :- q | r)', _, _), "
            + "error(syntax_error(_), _), true).").Success);
    }
}
