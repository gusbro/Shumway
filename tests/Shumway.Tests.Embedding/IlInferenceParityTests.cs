using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>time/1 reports the inferences of Tier-0 whatever runs promoted.
/// Tier-1 counted only the calls that crossed the interpreter, so a fully
/// promoted program reported 1 where Tier-0 reported millions, and a partly
/// promoted one any number in between: the count said how much had promoted,
/// not how much work was done.</summary>
public sealed class IlInferenceParityTests
{
    private const string Corpus = """
        queens(N, Qs) :- numlist(1, N, Ns), permutation(Ns, Qs), safe(Qs).
        safe([]).
        safe([Q|Qs]) :- no_attack(Q, Qs, 1), safe(Qs).
        no_attack(_, [], _).
        no_attack(Q, [R|Rs], D) :- Q =\= R + D, Q =\= R - D, D1 is D + 1, no_attack(Q, Rs, D1).
        count(0) :- !.
        count(N) :- N1 is N - 1, count(N1).
        twice(G) :- call(G), call(G).
        pick(_, 1).
        pick(G, X) :- call(G, X).
        seven(7).
        """;

    private static readonly string[] Goals =
    {
        "findall(Q, queens(6, Q), _)",
        "findall(Q, (permutation([1,2,3,4,5], Q), safe(Q)), _)",
        "findall(x, (between(1, 300, _), select(_, [1,2,3], _)), _)",
        "count(2000)",
        "twice(count(50))",
        "findall(X, pick(seven, X), _)",
        "forall(member(X, [a,b,c]), atom(X))",
    };

    private static long Inferences(PrologEngine e, System.IO.StringWriter sw, string goal)
    {
        sw.GetStringBuilder().Clear();
        Assert.True(e.Query($"time(({goal})).").Success, goal);
        var m = System.Text.RegularExpressions.Regex.Match(sw.ToString(), @"% ([0-9,]+) inferences");
        Assert.True(m.Success, sw.ToString());
        return long.Parse(m.Groups[1].Value.Replace(",", ""));
    }

    private static string Name(int fid)
    {
        var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
        string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
        return $"{name[(name.LastIndexOf('$') + 1)..]}/{arity}";
    }

    [Fact]
    public void APromotedProgramReportsTheInferencesOfTierZero()
    {
        var sw0 = new System.IO.StringWriter();
        var plain = new PrologEngine { Out = sw0 };
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Corpus);

        var sw1 = new System.IO.StringWriter();
        var tiered = new PrologEngine { Out = sw1 };
        tiered.IlPromotion.Threshold = 1;
        tiered.ConsultString(Corpus);
        string[] expected = { "queens/2", "safe/1", "no_attack/3", "count/1", "twice/1", "pick/2" };
        var promoted = new HashSet<string>();
        for (int round = 0; round < 5 && !expected.All(promoted.Contains); round++)
        {
            foreach (string g in Goals) Inferences(tiered, sw1, g);
            tiered.IlPromotion.WaitForPendingPromotions();
            promoted = tiered.IlPromotion.PromotedFunctorIds().Select(Name).ToHashSet();
        }
        // ANTI-VACUITY: the program runs promoted, so the calls are IL's own.
        foreach (string pi in expected)
            Assert.True(promoted.Contains(pi), $"{pi} did not promote");

        foreach (string g in Goals)
        {
            long t0 = Inferences(plain, sw0, g);
            long t1 = Inferences(tiered, sw1, g);
            Assert.True(t0 == t1, $"{g}: Tier-0 {t0} inferences, Tier-1 {t1}");
        }
    }
}
