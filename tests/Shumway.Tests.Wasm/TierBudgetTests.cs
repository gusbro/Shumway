using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>Exact tallies for a few fixed goals: how many times the tier was
/// entered, how many times it stepped aside, and how many times it left because
/// the target lived elsewhere.
///
/// <para>These exist because of one failure mode nothing else can see. When the
/// backend translates something wrongly it steps aside, the interpreter redoes
/// the instruction, and the ANSWER comes out right — the run is simply slower.
/// A differential against Tier-0 is blind to it by construction. It has already
/// happened once: a choice-point restore that scaled a cell index by four
/// instead of eight made every retry and trust step aside, and
/// <c>once(queens(10,_))</c> staged the whole image 379,661 times while
/// answering correctly throughout. Only a count catches that.</para>
///
/// <para><b>When one of these fails.</b> The number moving is not by itself a
/// bug — during an arc it is usually the point. The failure prints the old and
/// the new value so the change can be read at a glance, and the rule is that
/// the commit which updates a number says in words why it moved. A number that
/// went DOWN with no work that explains it is as suspicious as one that went
/// up.</para></summary>
public sealed class TierBudgetTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- public app/3.
        :- public rev/2.
        :- public pick/2.
        :- public pairs/3.
        :- public sumto/2.
        app([], L, L).
        app([H|T], L, [H|R]) :- app(T, L, R).
        rev([], []).
        rev([H|T], R) :- rev(T, S), app(S, [H], R).
        pick([H|_], H).
        pick([_|T], X) :- pick(T, X).
        pairs(L, X, Y) :- pick(L, X), pick(L, Y), X @< Y.
        sumto(0, 0) :- !.
        sumto(N, S) :- N > 0, N1 is N - 1, sumto(N1, S1), S is S1 + N.
        """;

    /// <summary>One goal's budget. Entries and foreign exits are counts of
    /// boundary crossings; deopts are step-asides. All three are properties of
    /// the emitted code, not of the machine, so they are reproducible.</summary>
    public sealed record Budget(string Goal, long Entries, long Deopts, long ForeignExits);

    public static TheoryData<string, long, long, long> Budgets() => new()
    {
        // goal, entries, deopts, foreign exits
        //
        // Three of these fell by exactly 2 when the one-argument type tests
        // became inline (var/1, integer/1, number/1 and friends answered by a
        // tag comparison instead of a host exit). Every goal below that ends
        // in `length(_, N)` with N unbound pays for the prelude's own
        // length/2 deciding its mode: two type tests, two chain exits, two
        // re-entries. They are the same two in each goal, which is why the
        // three moved by the same amount. sumto is unchanged: it tests
        // nothing.
        { "numlist(1, 60, L), rev(L, R), length(R, N), N == 60.", 4, 0, 0 },
        // 1604 entries for 780 solutions: findall re-enters the tier once per
        // solution, and each re-entry stages the whole image. Recorded rather
        // than rounded off -- it is the largest boundary cost in this file and
        // the number that should move if that ever gets addressed.
        { "numlist(1, 40, L), findall(P, pairs(L, X, Y), Ps), length(Ps, N).", 1604, 0, 0 },
        // Deep recursion under a cut: one entry for 300 frames, which is what
        // a chain is supposed to buy.
        { "sumto(300, S), S == 45150.", 1, 0, 0 },
        { "numlist(1, 200, A), app(A, [x], B), length(B, N), N == 201.", 4, 0, 0 },
    };

    [DiagTheory]
    [MemberData(nameof(Budgets))]
    public void TheTierSpendsExactlyThis(string goal, long entries, long deopts,
                                         long foreignExits)
    {
        var (e, members) = TieredEngine.Build(Corpus);
        // Warm: the first run promotes what the goal touches, and promotion
        // itself enters the tier. The budget is for a run on a warm tier.
        e.Query(goal);
        WasmTierDelegate.ResetDiag();
        Assert.True(e.Query(goal).Success, "the goal failed");

        long gotEntries = WasmTierDelegate.DiagEntries;
        long gotDeopts = WasmTierDelegate.DiagDeopts;
        long gotForeign = WasmTierDelegate.DiagForeignExits;
        o.WriteLine($"{goal}\n  entries={gotEntries} deopts={gotDeopts} "
            + $"foreignExits={gotForeign}");

        // ANTI-VACUITY: a budget of zero everywhere would be met by a tier
        // that never ran.
        Assert.NotEmpty(members);
        Assert.True(gotEntries > 0, "nothing entered the tier: the budget is vacuous");

        var moved = new List<string>();
        if (gotEntries != entries) moved.Add($"entries {entries} -> {gotEntries}");
        if (gotDeopts != deopts) moved.Add($"deopts {deopts} -> {gotDeopts}");
        if (gotForeign != foreignExits)
            moved.Add($"foreignExits {foreignExits} -> {gotForeign}");
        Assert.True(moved.Count == 0,
            $"the tier's budget for `{goal}` moved: {string.Join(", ", moved)}. "
            + "That is not automatically wrong -- during an arc it is usually the "
            + "point -- but the commit that updates these numbers has to say why "
            + "each one moved. A number that fell with no work behind it deserves "
            + "the same suspicion as one that rose.");
    }

    /// <summary>The budget above is per goal; this is the property that holds
    /// for ALL of them and is worth stating on its own, because it is the one
    /// the arc must not lose: with a single module, nothing leaves the tier
    /// looking for a target elsewhere.</summary>
    [DiagFact]
    public void OneModuleNeverExitsLookingForAnother()
    {
        var (e, members) = TieredEngine.Build(Corpus);
        foreach (var row in Budgets()) e.Query((string)row[0]);
        WasmTierDelegate.ResetDiag();
        foreach (var row in Budgets()) Assert.True(e.Query((string)row[0]).Success);

        o.WriteLine($"entries={WasmTierDelegate.DiagEntries} "
            + $"switches={WasmTierDelegate.DiagSwitches} "
            + $"foreignExits={WasmTierDelegate.DiagForeignExits} "
            + $"boundaryExits={WasmTierDelegate.DiagBoundaryExits}");
        Assert.NotEmpty(members);
        Assert.True(WasmTierDelegate.DiagEntries > 0);
        Assert.Equal(0, WasmTierDelegate.DiagForeignExits);
    }
}
