using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>The type tests on an ATTRIBUTED variable.
///
/// <para>An attributed variable is still a variable, so <c>var/1</c> holds
/// of it and <c>nonvar/1</c> does not. The tier open-codes these, which is
/// why it matters here and not in the interpreter: an open-coded test that
/// treats the ATTVAR tag as "not a variable" changes which clause commits,
/// and a predicate whose first clause is <c>p(V) :- var(V), !</c> then
/// falls through to whatever comes after it.</para>
///
/// <para>The tally that brought this here: on clp(Z)'s projection the tier
/// calls <c>=../2</c> 2,178,087 times and Tier 0 does not call it at all,
/// while every other builtin agrees to within a percent. <c>=../2</c> is
/// reached only from the LAST clause of unwrap_with/3, whose first two are
/// a var test and a shape test.</para></summary>
public sealed class VarOnAttributedTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- use_module(library(clpfd)).
        % Committing on the var test, the shape unwrap_with/3 has.
        first(V, R) :- ( var(V) -> R = yes ; R = no ).
        firstcut(V, R) :- vcut(V, R).
        vcut(V, yes) :- var(V), !.
        vcut(_, no).
        nv(V, R) :- ( nonvar(V) -> R = yes ; R = no ).
        atomicp(V, R) :- ( atomic(V) -> R = yes ; R = no ).
        compoundp(V, R) :- ( compound(V) -> R = yes ; R = no ).
        % An attributed variable, made the way a solver makes one.
        probe(Goal, R) :- X in 1..9, call(Goal, X, R).
        """;

    [Theory]
    [InlineData("first", "yes")]
    [InlineData("firstcut", "yes")]
    [InlineData("nv", "no")]
    [InlineData("atomicp", "no")]
    [InlineData("compoundp", "no")]
    public void ATypeTestSeesAnAttributedVariableAsAVariable(string test, string expected)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        string p0 = Answer(plain, test);
        Assert.Equal(expected, p0);          // the standard says so, first

        var (tiered, _) = TieredEngine.Build(Corpus);
        string t = Answer(tiered, test);
        o.WriteLine($"{test}: tier0={p0} tier={t}");
        Assert.Equal(p0, t);
    }

    private static string Answer(PrologEngine e, string test)
    {
        try
        {
            var r = e.Query($"probe({test}, R).");
            if (!r.Success) return "failed";
            foreach (var b in r.Bindings) if (b.Key == "R") return b.Value.ToString()!;
            return "?";
        }
        catch (System.Exception ex) { return ex.Message; }
    }
}
