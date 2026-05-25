using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Surface fixes that landed while compiling a real-world Blint.pl
/// via the engine's consult path (not just the ShmoCompiler):
///
/// <list type="bullet">
/// <item>PrologEngine.ConsultString now accepts the GNU-style
/// comma-separated form of <c>:- dynamic / :- public /
/// :- discontiguous / :- multifile / :- table</c> directives:
/// <c>:- dynamic a/0, b/1, c/2.</c></item>
///
/// <item>The discontiguity check classifies DCG-rule heads by their
/// *expanded* arity (Name/(Arity+2)) instead of the raw <c>-->/2</c>
/// compound. Without this fix a file containing only DCG rules
/// (every clause's head reads as <c>-->/2</c> through the
/// pre-DCG-transform classifier) triggers a false-positive
/// "Clauses for -->/2 are not contiguous" error.</item>
/// </list>
/// </summary>
public class ConsultDirectiveFormsTests
{
    [Fact]
    public void Dynamic_CommaSeparatedForm_Accepted()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic a/0, b/1, c/2.\n");
        // a/0 and b/1 and c/2 are now valid call targets that
        // assertz can populate at runtime.
        Assert.True(e.Query("assertz(a).").Success);
        Assert.True(e.Query("assertz(b(1)).").Success);
        Assert.True(e.Query("assertz(c(x, y)).").Success);
        Assert.True(e.Query("a.").Success);
        Assert.True(e.Query("b(1).").Success);
        Assert.True(e.Query("c(x, y).").Success);
    }

    [Fact]
    public void Public_CommaSeparatedForm_Accepted()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- module(m).\n"
            + ":- public foo/0, bar/1.\n"
            + "foo. bar(_).\n");
        Assert.True(e.Query("foo.").Success);
        Assert.True(e.Query("bar(_).").Success);
    }

    [Fact]
    public void Discontiguous_CommaSeparatedForm_Accepted()
    {
        var e = new PrologEngine();
        // Without :- discontiguous the engine would throw on the
        // re-interleaved p/q clauses. The comma form must work.
        e.ConsultString(
            ":- discontiguous p/1, q/1.\n"
            + "p(1).\nq(1).\np(2).\nq(2).\n");
        Assert.True(e.Query("p(1).").Success);
        Assert.True(e.Query("q(2).").Success);
    }

    [Fact]
    public void DcgRules_Contiguity_ClassifiedByExpandedArity()
    {
        // A file of only DCG rules — the contiguity check has to
        // treat each head as its (expanded) Name/(Arity+2) form,
        // not as -->/2. Pre-fix this threw "Clauses for -->/2 are
        // not contiguous" because every DcgRule's head term was
        // the `-->`/2 compound.
        var e = new PrologEngine();
        e.ConsultString(
            ":- module(g).\n"
            + ":- public sentence/2.\n"
            + "sentence --> noun, verb.\n"
            + "noun --> [the], [dog].\n"
            + "verb --> [runs].\n");
        Assert.True(e.Query("sentence([the, dog, runs], []).").Success);
    }

    [Fact]
    public void DcgRules_InterleavedWithRegularRules_DiscontiguityRespected()
    {
        // Regression guard: two non-contiguous DCG-rule blocks of
        // the same expanded name/arity should be flagged just like
        // regular Rule clauses are.
        var e = new PrologEngine();
        Assert.ThrowsAny<Exception>(() => e.ConsultString(
            "a --> [x].\n"
            + "b --> [y].\n"
            + "a --> [z].\n"));   // a/2 split by b/2 — must throw.
    }

    [Fact]
    public void DcgRules_InterleavedWithDiscontiguousDeclared_Allowed()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- discontiguous a/2.\n"
            + "a --> [x].\n"
            + "b --> [y].\n"
            + "a --> [z].\n");
        // Just a smoke check — if consult didn't throw, the
        // discontiguous declaration with the expanded arity was
        // honoured.
        var sol = e.Query("a([x], []).");
        Assert.True(sol.Success);
    }
}
