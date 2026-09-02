using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shumway.Builtins;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Builtin registration is a one-time, process-wide affair that any
/// number of threads may ask for. It used to claim its "already done" flag
/// BEFORE doing the work (<c>Interlocked.Exchange</c> on the way in), so a
/// second caller arriving mid-registration was told the registry was ready and
/// went on to look up a builtin that had not been registered yet. The net48
/// gate caught it as <c>call/1 is not a registered builtin</c> — a test whose
/// own first line had just called EnsureRegistered.
///
/// <para>These cannot recreate the original window: by the time any test runs,
/// this process has long since registered, so EnsureRegistered returns on its
/// fast path. What they pin is the invariant that made the bug possible —
/// that a caller which has been told registration is done can rely on every
/// builtin being there — and they would catch a fresh process regressing to
/// flag-first, which is the shape to guard against.</para></summary>
public sealed class BuiltinRegistrationRaceTests
{
    /// <summary>Builtins every caller must see the moment EnsureRegistered
    /// returns. call/1 is the one the gate actually caught missing. All are
    /// registered builtins — findall/3 deliberately is not one (it is a
    /// prelude predicate, a collect loop over call/1) and so has no place
    /// here.</summary>
    private static readonly (string Name, int Arity)[] MustResolve =
    {
        ("call", 1), ("=", 2), ("is", 2), ("atom", 1), ("functor", 3),
        ("assertz", 1), ("throw", 1), ("write", 1), ("between", 3),
    };

    private static bool Resolves(string name, int arity)
    {
        int fid = FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);
        return BuiltinsRegistry.TryGetByFunctor(fid, out _);
    }

    [Fact]
    public async Task EveryCallerThatIsToldRegistrationIsDoneSeesEveryBuiltin()
    {
        // Whoever gets there first does the work; everyone else waits for it.
        // Nobody may be waved through into a half-populated registry.
        var missing = new ConcurrentBag<string>();
        using var start = new ManualResetEventSlim(false);
        var threads = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            start.Wait();
            StandardBuiltins.EnsureRegistered();
            MetaBuiltins.EnsureRegistered();
            foreach (var (name, arity) in MustResolve)
                if (!Resolves(name, arity)) missing.Add($"{name}/{arity}");
        })).ToArray();

        start.Set();
        await Task.WhenAll(threads).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(missing.Distinct());
    }

    [Fact]
    public void RegistrationIsIdempotentAndKeepsIdsStable()
    {
        // Ids are handed out by a counter and BAKED INTO PERSISTED IL as patch
        // targets, so a second registration pass that renumbered anything would
        // be far worse than a slow one.
        StandardBuiltins.EnsureRegistered();
        MetaBuiltins.EnsureRegistered();
        var before = MustResolve.Select(m =>
        {
            int fid = FunctorTable.Intern(
                AtomTable.Intern(m.Name, permanent: true).Id, m.Arity);
            Assert.True(BuiltinsRegistry.TryGetByFunctor(fid, out int id), $"{m.Name}/{m.Arity}");
            return id;
        }).ToArray();

        for (int i = 0; i < 5; i++)
        {
            StandardBuiltins.EnsureRegistered();
            MetaBuiltins.EnsureRegistered();
        }

        var after = MustResolve.Select(m =>
        {
            int fid = FunctorTable.Intern(
                AtomTable.Intern(m.Name, permanent: true).Id, m.Arity);
            BuiltinsRegistry.TryGetByFunctor(fid, out int id);
            return id;
        }).ToArray();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task EnginesBuiltFromManyThreadsAllGetAWorkingRegistry()
    {
        // The way the race actually arrived: several threads constructing
        // engines at once, each of which registers on the way up.
        var failures = new ConcurrentBag<string>();
        var threads = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try
            {
                var engine = new PrologEngine { Out = new System.IO.StringWriter() };
                if (!engine.Query("X = 1, call(atom(a)), X == 1.").Success)
                    failures.Add("a fresh engine could not run a basic goal");
            }
            catch (Exception ex) { failures.Add(ex.Message); }
        })).ToArray();
        await Task.WhenAll(threads).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Empty(failures.Distinct());
    }
}
