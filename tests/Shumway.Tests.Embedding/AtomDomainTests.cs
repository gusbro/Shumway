using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shumway.Builtins;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-003: a query run inside a builtin of a running query claims atoms
/// with it, as one domain, whether it runs on the same engine or on an engine that
/// builtin made; a query that runs on its own after another has its own. A domain
/// holds its claimant while it runs, and not between solutions.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class AtomDomainTests
{
    private static readonly List<AtomDomain?> Seen = new();
    private static readonly List<bool> SeenRunning = new();
    // The engine of the test running: the registry keeps a builtin's first
    // registration, so a builtin cannot close over a test's own engine.
    private static PrologEngine? Current;

    static AtomDomainTests()
    {
        BuiltinsRegistry.Register("$atom_domain_probe", 0, a =>
        {
            Seen.Add(a.AtomDomain);
            SeenRunning.Add(a.AtomDomain?.Claimant.IsRunning ?? false);
            return true;
        });
        BuiltinsRegistry.Register("$atom_domain_nested", 0, _ =>
            Current!.Query("'$atom_domain_probe'.").Success);
    }

    private static List<AtomDomain?> Run(System.Action<PrologEngine> body)
    {
        bool was = AtomTable.CollectionEnabled;
        AtomTable.CollectionEnabled = true;
        Seen.Clear();
        SeenRunning.Clear();
        try
        {
            var e = new PrologEngine { Out = new StringWriter() };
            Current = e;
            body(e);
            return Seen.ToList();
        }
        finally
        {
            Current = null;
            AtomTable.CollectionEnabled = was;
        }
    }

    [Fact]
    public void AQueryABuiltinRunsOnTheSameEngineSharesTheDomain()
    {
        var seen = Run(e =>
            Assert.True(e.Query("'$atom_domain_probe', '$atom_domain_nested'.").Success));

        Assert.Equal(2, seen.Count);
        Assert.NotNull(seen[0]);
        Assert.Same(seen[0], seen[1]);
    }

    [Fact]
    public void AQueryOnAnEngineABuiltinMadeSharesTheDomain()
    {
        var seen = Run(e => Assert.True(
            e.Query("'$atom_domain_probe', '$tbl_solve_complete'('$atom_domain_probe').").Success));

        Assert.Equal(2, seen.Count);
        Assert.NotNull(seen[0]);
        Assert.Same(seen[0], seen[1]);
    }

    [Fact]
    public void AQueryHoldsItsClaimantWhileItRunsAndNotBetweenSolutions()
    {
        var seen = Run(e =>
        {
            using var solutions = e.QueryAll("member(_, [1, 2]), '$atom_domain_probe'.").GetEnumerator();
            Assert.True(solutions.MoveNext());
            Assert.False(Seen[0]!.Claimant.IsRunning);
            Assert.True(solutions.MoveNext());
        });

        Assert.Equal(new[] { true, true }, SeenRunning);
        Assert.Same(seen[0], seen[1]);
        Assert.False(seen[0]!.Claimant.IsRunning);
    }

    [Fact]
    public void ANestedQueryLeavesTheDomainRunningWhenItReturns()
    {
        Run(e => Assert.True(e.Query("'$atom_domain_nested', '$atom_domain_probe'.").Success));

        Assert.Equal(new[] { true, true }, SeenRunning);
    }

    [Fact]
    public void QueriesOneAfterAnotherHaveTheirOwnDomains()
    {
        var seen = Run(e =>
        {
            Assert.True(e.Query("'$atom_domain_probe'.").Success);
            Assert.True(e.Query("'$atom_domain_probe'.").Success);
        });

        Assert.Equal(2, seen.Count);
        Assert.NotNull(seen[0]);
        Assert.NotNull(seen[1]);
        Assert.NotSame(seen[0], seen[1]);
    }
}
