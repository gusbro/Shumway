using Shumway.Compiler.Il;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-060 stage 1: with every register the activation exposes held
/// in locals, and the checked mode on, a region computes what Tier-0
/// computes through choice points, cuts, exceptions, stack growth and
/// builtin choice points; and a row that hides a write is caught at the
/// call. The stage converts no operation, so this exercises the load, spill
/// and reload around the helpers alone.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class IlRegisterFileTests
{
    private const MachineRegs Exposed =
        MachineRegs.E | MachineRegs.Cp | MachineRegs.B | MachineRegs.B0 | MachineRegs.StackTop
        | MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop
        | MachineRegs.StackArray | MachineRegs.RegisterArray;

    private const string Corpus = """
        sel(X, [X|T], T).
        sel(X, [H|T], [H|R]) :- sel(X, T, R).
        perm([], []).
        perm(L, [X|P]) :- sel(X, L, R), perm(R, P).
        safe([]).
        safe([Q|Qs]) :- noattack(Q, Qs, 1), safe(Qs).
        noattack(_, [], _).
        noattack(Q, [Q1|Qs], D) :- Q =\= Q1 + D, Q =\= Q1 - D, D1 is D + 1, noattack(Q, Qs, D1).
        queens(N, Qs) :- numlist(1, N, Ns), perm(Ns, Qs), safe(Qs).
        deep(0, []) :- !.
        deep(N, [N|T]) :- N1 is N - 1, deep(N1, T).
        len([], 0).
        len([_|T], N) :- len(T, N0), N is N0 + 1.
        deep_len(N, C) :- deep(N, L), len(L, C).
        guarded(X, Y) :- catch(risky(X), error(type_error(_, _), _), Y = caught), !.
        guarded(_, none).
        risky(X) :- Y is X + 1, Y > 0.
        first(X, L) :- member(X, L), !.
        count(N, C) :- findall(X, (between(1, N, X), X mod 3 =:= 0), L), len(L, C).
        """;

    // Each goal binds Out; the two tiers must agree on it.
    private static readonly string[] Goals =
    {
        "findall(Qs, queens(6, Qs), Out).",
        "deep_len(20000, Out).",
        "guarded(3, Out).",
        "guarded(a, Out).",
        "first(Out, [x, y, z]).",
        "count(3000, Out).",
        "findall(X-R, sel(X, [a, b, c, d], R), Out).",
    };

    private static readonly string[] Expected =
        { "queens/2", "perm/2", "sel/3", "safe/1", "noattack/3", "deep/2", "len/2", "deep_len/2", "guarded/2", "count/2" };

    private static string Name(int fid)
    {
        var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
        string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
        return $"{name[(name.LastIndexOf('$') + 1)..]}/{arity}";
    }

    private static PrologEngine Tiered()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 1;
        engine.ConsultString(Corpus);
        var promoted = new HashSet<string>();
        for (int round = 0; round < 6 && !Expected.All(promoted.Contains); round++)
        {
            foreach (string g in Goals) engine.Query(g);
            engine.IlPromotion.WaitForPendingPromotions();
            promoted = engine.IlPromotion.PromotedFunctorIds().Select(Name).ToHashSet();
        }
        foreach (string pi in Expected)
            Assert.True(promoted.Contains(pi), $"{pi} did not promote");
        return engine;
    }

    private static string Answer(PrologEngine engine, string goal)
    {
        var a = engine.Query(goal);
        return a.Success
            ? string.Join(", ", a.Bindings.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} = {kv.Value}"))
            : "no";
    }

    [Fact]
    public void HoldingEveryExposedRegisterUnderCheckIsTransparent()
    {
        var plain = new PrologEngine();
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Corpus);

        var held = IlPredicateCompiler.HeldByDefault;
        bool checkedBefore = IlPredicateCompiler.CheckedRegisters;
        int checksBefore = IlPredicateCompiler.RegisterChecksEmitted;
        IlPredicateCompiler.HeldByDefault = Exposed;
        IlPredicateCompiler.CheckedRegisters = true;
        try
        {
            var tiered = Tiered();
            foreach (string g in Goals)
                Assert.Equal(Answer(plain, g), Answer(tiered, g));
            Assert.Equal("Out = 20000", Answer(tiered, "deep_len(20000, Out)."));
        }
        finally
        {
            IlPredicateCompiler.HeldByDefault = held;
            IlPredicateCompiler.CheckedRegisters = checkedBefore;
        }
        // ANTI-VACUITY: the regions carried the checks.
        Assert.True(IlPredicateCompiler.RegisterChecksEmitted - checksBefore > 1000,
            $"only {IlPredicateCompiler.RegisterChecksEmitted - checksBefore} checks emitted");
    }

    [Fact]
    public void ARowThatHidesAWriteIsCaughtAtTheCall()
    {
        // Every push writes B; a row that says it does not leaves the local
        // stale, and the check after the call sees the field move. Which push
        // a region takes depends on its indexing, so both rows are wrong here.
        var pushes = new[] { "EnginePushChoicePointMethod", "EnginePushIlCpMethod" }
            .Select(IlPredicateCompiler.HelperHandle).ToArray();
        var rows = pushes.Select(m => IlPredicateCompiler.HelperTable[m]).ToArray();
        foreach (var row in rows) Assert.True((row.Writes & MachineRegs.B) != 0);

        var held = IlPredicateCompiler.HeldByDefault;
        bool checkedBefore = IlPredicateCompiler.CheckedRegisters;
        IlPredicateCompiler.HeldByDefault = Exposed;
        IlPredicateCompiler.CheckedRegisters = true;
        for (int i = 0; i < pushes.Length; i++)
            IlPredicateCompiler.OverrideHelperRow(pushes[i], new(rows[i].Reads, rows[i].Writes & ~MachineRegs.B));
        try
        {
            // A region emits its own clause pushes inline; the push of an
            // inline disjunction is still a call.
            var engine = new PrologEngine { EnableInlineIte = true };
            engine.IlPromotion.Threshold = 1;
            engine.ConsultString("""
                pick(X, Y) :- (X = a ; X = b), Y = X.
                walk(Y) :- pick(_, Y), atom(Y).
                """);
            var ex = Assert.ThrowsAny<Exception>(() =>
            {
                for (int round = 0; round < 6; round++)
                {
                    engine.Query("findall(Y, walk(Y), Out).");
                    engine.IlPromotion.WaitForPendingPromotions();
                }
            });
            Assert.Contains("ADR-060", ex.ToString());
            Assert.Contains("B changed by Push", ex.ToString());
        }
        finally
        {
            for (int i = 0; i < pushes.Length; i++)
                IlPredicateCompiler.OverrideHelperRow(pushes[i], rows[i]);
            IlPredicateCompiler.HeldByDefault = held;
            IlPredicateCompiler.CheckedRegisters = checkedBefore;
        }
    }
}
