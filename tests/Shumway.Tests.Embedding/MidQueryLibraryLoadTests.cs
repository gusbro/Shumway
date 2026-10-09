using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A goal that loads a library can call it right away, in the same
/// query or clause body, as in Scryer and SICStus: an engine library loaded
/// by use_module/1, a module file found on the library path, a module file
/// consulted. What the load defines links into the running query's code; a
/// module's private predicates stay private.</summary>
public sealed class MidQueryLibraryLoadTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_midquery_" + Guid.NewGuid().ToString("N"));

    public MidQueryLibraryLoadTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "midlib.pl"), """
            :- module(midlib, [hello/1]).
            hello(X) :- secret(X).
            secret(world).
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.AddLibraryDirectory(_dir);
        return e;
    }

    [Theory]
    [InlineData("use_module(library(reif)), tfilter(=(a), [a,b,a], L), L == [a,a]")]
    [InlineData("use_module(library(reif)), G = if_(=(a, b), R = y, R = n), call(G), R == n")]
    // reif's undecided answer is coroutining's dif/2, loaded by reif's load.
    [InlineData(@"use_module(library(reif)), =(X, a, false), \+ X = a, X = b")]
    [InlineData("use_module(library(coroutining)), when(nonvar(V), W = woke), V = 1, W == woke")]
    [InlineData("use_module(library(midlib)), hello(X), X == world")]
    [InlineData("use_module(library(midlib)), G = hello(X), call(G), X == world")]
    public void AGoalCallsWhatItJustLoaded(string goal)
        => Assert.True(Engine().Query(goal + ".").Success, goal);

    [Fact]
    public void AClauseBodyCallsWhatItJustLoaded()
    {
        var e = Engine();
        e.ConsultString("t(R) :- use_module(library(reif)), if_(a = a, R = y, R = n).");
        Assert.True(e.Query("t(R), R == y.").Success);
    }

    [Fact]
    public void AModuleFileConsultedByAGoalIsImported()
    {
        string path = Path.Combine(_dir, "midlib.pl").Replace('\\', '/');
        Assert.True(Engine().Query($"consult('{path}'), hello(X), X == world.").Success);
    }

    [Fact]
    public void APrivatePredicateStaysPrivate()
    {
        var e = Engine();
        Assert.True(e.Query("use_module(library(midlib)), "
            + "catch(secret(_), error(existence_error(procedure, secret/1), _), true).").Success);
        Assert.True(e.Query(
            "catch(secret(_), error(existence_error(procedure, secret/1), _), true).").Success);
    }
}
