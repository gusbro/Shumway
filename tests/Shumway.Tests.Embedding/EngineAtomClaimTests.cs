using System;
using System.Collections.Generic;
using System.IO;
using Shumway.Builtins;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-003: what an engine keeps beyond its queries (its global variables,
/// its recorded database, its flag values) claims the atoms it names, marked by the
/// collection between queries and at the safe points of a query that runs. Dead ids
/// are poisoned: an atom the engine's mark missed is not handed out again, so the
/// name built anew is another atom.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class EngineAtomClaimTests
{
    static EngineAtomClaimTests()
    {
        BuiltinsRegistry.Register("$atom_collect", 0, _ =>
        {
            AtomTable.RequestCollection();
            return true;
        });
        BuiltinsRegistry.Register("$atom_collected", 0, _ => !AtomTable.CollectionPending);
        BuiltinsRegistry.Register("$engine_claim_probe", 0, a =>
        {
            Claims.Add(a.HostAtomClaim);
            Running.Add(a.HostAtomClaim?.Claimant.IsRunning ?? false);
            return true;
        });
    }

    private static readonly List<HostAtomClaim?> Claims = new();
    private static readonly List<bool> Running = new();

    private const string Countdown = """
        countdown(0) :- !.
        countdown(N) :- M is N - 1, countdown(M).
        """;

    private static void Run(Action<PrologEngine> body)
    {
        bool enabled = AtomTable.CollectionEnabled, poisoned = AtomTable.PoisonDeadIds;
        AtomTable.CollectionEnabled = true;
        AtomTable.PoisonDeadIds = true;
        try
        {
            var e = new PrologEngine { Out = new StringWriter() };
            e.ConsultString(Countdown);
            body(e);
        }
        finally
        {
            AtomTable.PoisonDeadIds = poisoned;
            AtomTable.CollectionEnabled = enabled;
        }
    }

    // A suffix no code interns: the atoms built from it at run time are transient.
    private static string Suffix() => Guid.NewGuid().ToString("N");

    private static int IdOf(string name) => AtomTable.Intern(name).Id;

    private static bool Alive(int id, string name) => AtomTable.ProbeById(id)?.Name == name;

    [Fact]
    public void AGlobalVariableClaimsItsKeyAndItsValueBetweenQueries()
    {
        string s = Suffix();
        Run(e =>
        {
            Assert.True(e.Query($"atom_concat(gk_, '{s}', K), atom_concat(gv_, '{s}', V), "
                + $"atom_concat(gl_, '{s}', _), nb_setval(K, V).").Success);
            int key = IdOf("gk_" + s), value = IdOf("gv_" + s), loose = IdOf("gl_" + s);

            AtomTable.RequestCollection();

            Assert.False(AtomTable.CollectionPending);
            Assert.False(Alive(loose, "gl_" + s));
            Assert.True(Alive(key, "gk_" + s));
            Assert.True(Alive(value, "gv_" + s));
            Assert.True(e.Query($"atom_concat(gk_, '{s}', K), nb_getval(K, V), "
                + $"atom_concat(gv_, '{s}', V).").Success);
        });
    }

    [Fact]
    public void AGlobalVariableClaimsTheAtomsOfACompoundValue()
    {
        string s = Suffix();
        Run(e =>
        {
            Assert.True(e.Query($"atom_concat(gk_, '{s}', K), atom_concat(gv_, '{s}', V), "
                + "nb_setval(K, f([V])).").Success);
            int value = IdOf("gv_" + s);

            AtomTable.RequestCollection();

            Assert.True(Alive(value, "gv_" + s));
            Assert.True(e.Query($"atom_concat(gk_, '{s}', K), nb_getval(K, f([V])), "
                + $"atom_concat(gv_, '{s}', V).").Success);
        });
    }

    [Fact]
    public void AFlagClaimsItsAtomValue()
    {
        string s = Suffix();
        Run(e =>
        {
            Assert.True(e.Query($"atom_concat(fv_, '{s}', V), flag(atom_claim_flag, _, V).").Success);
            int value = IdOf("fv_" + s);

            AtomTable.RequestCollection();

            Assert.True(Alive(value, "fv_" + s));
            Assert.True(e.Query($"flag(atom_claim_flag, V, V), atom_concat(fv_, '{s}', V).").Success);
        });
    }

    [Fact]
    public void ARecordClaimsItsKeyAndItsValue()
    {
        string s = Suffix();
        Run(e =>
        {
            Assert.True(e.Query($"atom_concat(rk_, '{s}', K), atom_concat(rv_, '{s}', V), "
                + "recordz(K, g(V), _).").Success);
            int key = IdOf("rk_" + s), value = IdOf("rv_" + s);

            AtomTable.RequestCollection();

            Assert.True(Alive(key, "rk_" + s));
            Assert.True(Alive(value, "rv_" + s));
            Assert.True(e.Query($"atom_concat(rk_, '{s}', K), recorded(K, g(V), _), "
                + $"atom_concat(rv_, '{s}', V).").Success);
        });
    }

    [Fact]
    public void AnEngineHoldsItsClaimantWhileAQueryRunsAndNotBetweenSolutions()
    {
        Claims.Clear();
        Running.Clear();
        Run(e =>
        {
            using var solutions = e.QueryAll("member(_, [1, 2]), '$engine_claim_probe'.").GetEnumerator();
            Assert.True(solutions.MoveNext());
            Assert.NotNull(Claims[0]);
            Assert.False(Claims[0]!.Claimant.IsRunning);
            Assert.True(e.Query("'$engine_claim_probe'.").Success);
            Assert.Same(Claims[0], Claims[1]);
            Assert.True(solutions.MoveNext());
        });
        Assert.Same(Claims[0], Claims[2]);
        Assert.Equal(new[] { true, true, true }, Running);
        Assert.False(Claims[0]!.Claimant.IsRunning);
    }

    [Fact]
    public void AnEngineWhoseBuiltinRunsAnotherHasItsOwnClaimant()
    {
        Claims.Clear();
        Running.Clear();
        Run(e => Assert.True(e.Query(
            "'$engine_claim_probe', '$tbl_solve_complete'('$engine_claim_probe').").Success));

        Assert.Equal(2, Claims.Count);
        Assert.NotNull(Claims[1]);
        Assert.NotSame(Claims[0], Claims[1]);
        Assert.Equal(new[] { true, true }, Running);
    }

    [Fact]
    public void ARunningEngineMarksAtTheSafePointsOfItsQuery()
    {
        string s = Suffix();
        Run(e => Assert.True(e.Query($"atom_concat(gk_, '{s}', K), nb_setval(K, 1), "
            + "'$atom_collect', countdown(10), '$atom_collected'.").Success));
    }

    [Fact]
    public void AnEngineMarksAtTheSafePointsOfAQueryItsBuiltinRuns()
    {
        string s = Suffix();
        Run(e => Assert.True(e.Query($"atom_concat(gk_, '{s}', K), nb_setval(K, 1), "
            + "'$tbl_solve_complete'(('$atom_collect', countdown(10), '$atom_collected')).").Success));
    }
}
