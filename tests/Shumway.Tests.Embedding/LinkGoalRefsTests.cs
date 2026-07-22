using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// --goal accepts any REPL-acceptable query, not just a direct call to a
/// user predicate: the call-position functors may be builtins or prelude
/// predicates (<c>time(main)</c>), and a user predicate named inside the
/// goal (a meta-call argument) becomes a reachability root.
/// </summary>
public class LinkGoalRefsTests
{
    private static LinkResult LinkWithGoal(string source, string goal,
        string module = "m")
    {
        Assert.True(ExecutableEmitter.TryCollectGoalRefs(goal,
            out var callRefs, out var termRefs, out string? err), err);
        var obj = ShmoCompiler.CompileSource(source, module);
        return ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            GoalCallRefs = callRefs,
            GoalTermRefs = termRefs,
        });
    }

    // ----- TryCollectGoalRefs -----

    [Fact]
    public void CollectRefs_BuiltinHeadWithUserArgument_SplitsCallAndTerm()
    {
        Assert.True(ExecutableEmitter.TryCollectGoalRefs("time(mi_predicado)",
            out var callRefs, out var termRefs, out _));
        Assert.Contains(new PredicateRef("time", 1), callRefs);
        Assert.Contains(new PredicateRef("mi_predicado", 0), termRefs);
    }

    [Fact]
    public void CollectRefs_ControlConstructsStayInCallPosition()
    {
        Assert.True(ExecutableEmitter.TryCollectGoalRefs(
            "(setup, \\+ broken -> main ; fallback)",
            out var callRefs, out var termRefs, out _));
        Assert.Contains(new PredicateRef("setup", 0), callRefs);
        Assert.Contains(new PredicateRef("broken", 0), callRefs);
        Assert.Contains(new PredicateRef("main", 0), callRefs);
        Assert.Contains(new PredicateRef("fallback", 0), callRefs);
        Assert.Empty(termRefs);
    }

    [Fact]
    public void CollectRefs_TrueFailCutAndVariablesAreNoRefs()
    {
        Assert.True(ExecutableEmitter.TryCollectGoalRefs(
            "(true, ! ; fail)", out var callRefs, out var termRefs, out _));
        Assert.Empty(callRefs);
        Assert.Empty(termRefs);
    }

    [Fact]
    public void CollectRefs_NestedSubterms_AreTermRefs()
    {
        Assert.True(ExecutableEmitter.TryCollectGoalRefs(
            "findall(X, gen(X), L)", out var callRefs, out var termRefs, out _));
        Assert.Contains(new PredicateRef("findall", 3), callRefs);
        Assert.Contains(new PredicateRef("gen", 1), termRefs);
    }

    // ----- Link-level behaviour -----

    private const string PublicSource =
        ":- module(m).\n:- public mi_predicado/0.\nmi_predicado :- writeln(hola).\n";

    [Fact]
    public void Link_BuiltinHeadGoal_KeepsUserPredicateReachable()
    {
        var result = LinkWithGoal(PublicSource, "time(mi_predicado)");
        Assert.True(result.Success,
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        // The user predicate survived reachability and the bundle runs the goal.
        var engine = new PrologEngine();
        engine.LoadBundle(result.Bundle!);
        var solutions = engine.QueryAll("time(mi_predicado).").ToList();
        Assert.Single(solutions);
    }

    [Fact]
    public void Link_LocalPredicateInsideGoal_LinksAndRuns()
    {
        var result = LinkWithGoal(
            ":- module(m).\nmi_local :- writeln(chau).\n", "time(mi_local)");
        Assert.True(result.Success,
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var engine = new PrologEngine();
        engine.LoadBundle(result.Bundle!);
        var solutions = engine.QueryAll("time(mi_local).").ToList();
        Assert.Single(solutions);
    }

    [Fact]
    public void Link_BuiltinOnlyGoal_Succeeds()
    {
        var result = LinkWithGoal(PublicSource, "writeln(hola)");
        Assert.True(result.Success,
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
    }

    [Fact]
    public void Link_TypoInCallPosition_FailsTheLink()
    {
        var result = LinkWithGoal(PublicSource, "tmie(mi_predicado)");
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics,
            d => d.Code == "goal_not_found" && d.Message.Contains("tmie/1"));
    }

    [Fact]
    public void Link_DataAtomInsideGoal_IsIgnoredNotAnError()
    {
        // `hola` resolves to nothing — it is data, not a missing predicate.
        var result = LinkWithGoal(PublicSource, "writeln(hola)");
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "goal_not_found");
    }

    [Fact]
    public void Link_DirectUserGoal_StillWorks()
    {
        var result = LinkWithGoal(PublicSource, "mi_predicado");
        Assert.True(result.Success,
            string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var engine = new PrologEngine();
        engine.LoadBundle(result.Bundle!);
        Assert.Single(engine.QueryAll("mi_predicado.").ToList());
    }
}
