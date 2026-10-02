using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Only the interpreter counts inferences (ADR-061). With Tier-1 off,
/// time/1 reports them and Lips; with compiled code it reports seconds and heap
/// cells, the cells those of Tier-0, and statistics(inferences, _) counts each
/// reading, so it is never 0 and two readings differ.</summary>
public sealed class IlInferenceReportTests
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

    private static string Report(PrologEngine e, System.IO.StringWriter sw, string goal)
    {
        sw.GetStringBuilder().Clear();
        Assert.True(e.Query($"time(({goal})).").Success, goal);
        return sw.ToString();
    }

    private static long HeapCells(string report)
    {
        var m = System.Text.RegularExpressions.Regex.Match(report, @"([0-9,]+) heap cells");
        Assert.True(m.Success, report);
        return long.Parse(m.Groups[1].Value.Replace(",", ""));
    }

    private static long Int(Solution s, string name) => long.Parse(s[name]!.ToString()!);

    private static string Name(int fid)
    {
        var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
        string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
        return $"{name[(name.LastIndexOf('$') + 1)..]}/{arity}";
    }

    [Fact]
    public void WithTierOneOffTimeReportsInferencesAndLips()
    {
        var sw = new System.IO.StringWriter();
        var e = new PrologEngine { Out = sw };
        e.IlPromotion.Threshold = 0;
        e.ConsultString(Corpus);
        foreach (string g in Goals)
            Assert.Matches(@"% [0-9,]+ inferences, [0-9.]+ seconds, [0-9,]+ heap cells \([0-9,]+ Lips\)",
                Report(e, sw, g));

        var s = e.Query("statistics(inferences, A), count(10), statistics(inferences, B), D is B - A.");
        Assert.True(s.Success);
        // count(10) is 11 calls of count/1, and the second reading is one more.
        Assert.Equal(12, Int(s, "D"));
    }

    [Fact]
    public void WithTierOneOnTimeReportsTheHeapCellsOfTierZero()
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
            foreach (string g in Goals) Report(tiered, sw1, g);
            tiered.IlPromotion.WaitForPendingPromotions();
            promoted = tiered.IlPromotion.PromotedFunctorIds().Select(Name).ToHashSet();
        }
        // ANTI-VACUITY: the program runs promoted.
        foreach (string pi in expected)
            Assert.True(promoted.Contains(pi), $"{pi} did not promote");

        foreach (string g in Goals)
        {
            string r0 = Report(plain, sw0, g), r1 = Report(tiered, sw1, g);
            Assert.DoesNotContain("inferences", r1);
            Assert.DoesNotContain("Lips", r1);
            Assert.True(HeapCells(r0) == HeapCells(r1), $"{g}: Tier-0 {r0} Tier-1 {r1}");
        }

        var s = tiered.Query("statistics(inferences, A), count(10), statistics(inferences, B).");
        Assert.True(s.Success);
        Assert.True(Int(s, "A") > 0, $"A = {Int(s, "A")}");
        Assert.True(Int(s, "B") > Int(s, "A"), $"A = {Int(s, "A")}, B = {Int(s, "B")}");
    }
}
