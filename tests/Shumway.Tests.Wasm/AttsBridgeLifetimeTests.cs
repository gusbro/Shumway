using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>Attributes put through the <c>atts</c> bridge, and what
/// backtracking leaves of them.
///
/// <para>Every earlier replica of the clp(Z) runaway used this engine's own
/// clpfd, whose attributes are the engine's native put_attr/3, and none of
/// them reproduced. clp(Z) does not use those: it goes through
/// <c>put_atts/2</c>, which stores a module's attribute as a LIST through
/// <c>'$put_to_attr_list'/3</c>. The tallies say so -- 315 calls to that on
/// both tiers -- and it is the one mechanism the replicas never touched.
/// </para>
///
/// <para>What is being asked: after a goal that attributed a variable is
/// undone, is the CELL still an attributed variable? That is the question
/// <c>'$live_attvars'/1</c> answers, and on clp(Z) the tier answers it with
/// six more variables than the interpreter, from a point where every
/// builtin call and every allocation still agree exactly.</para></summary>
public sealed class AttsBridgeLifetimeTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- use_module(library(atts)).
        :- attribute mine/1.
        live(N) :- '$live_attvars'(L), length(L, N).
        mark(X) :- put_atts(X, mine(1)).
        % Attributed, then undone.
        undone(N) :- \+ \+ mark(_X), live(N).
        % Attributed, then undone under the failure-driven loop the browser
        % stage uses.
        loop(N) :- ( between(1, 1, _), \+ \+ mark(_X), fail ; true ), live(N).
        % Attributed and then bound, which is what labelling does.
        bound(N) :- \+ \+ (mark(X), X = 7), live(N).
        % Several at once, reached from outside the negation.
        many(N) :- Vs = [_,_,_], \+ \+ maplist(mark, Vs), live(N).
        % Anti-vacuity: still standing, so a probe that never sees one fails.
        standing(N) :- mark(_X), live(N).
        """;

    [Theory]
    [InlineData("undone")]
    [InlineData("loop")]
    [InlineData("bound")]
    [InlineData("many")]
    [InlineData("standing")]
    public void BothTiersHoldTheSameAttributedVariables(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        string p0 = Count(plain, goal);

        var (tiered, _) = TieredEngine.Build(Corpus);
        string t = Count(tiered, goal);
        o.WriteLine($"{goal}: tier0={p0} tier={t}");
        Assert.Equal(p0, t);
    }

    [Fact]
    public void TheProbeSeesAStandingAttributedVariable()
    {
        var e = new PrologEngine();
        e.ConsultString(Corpus);
        Assert.Equal("1", Count(e, "standing"));
    }

    private static string Count(PrologEngine e, string goal)
    {
        try
        {
            var r = e.Query($"{goal}(N).");
            if (!r.Success) return "failed";
            foreach (var b in r.Bindings) if (b.Key == "N") return b.Value.ToString()!;
            return "?";
        }
        catch (System.Exception ex) { return ex.Message; }
    }
}
