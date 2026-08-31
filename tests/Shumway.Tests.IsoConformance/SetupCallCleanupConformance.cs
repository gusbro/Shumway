using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.IsoConformance;

/// <summary>Neumerkel's setup_call_cleanup/3 battery
/// (complang.tuwien.ac.at/ulrich/iso-prolog/cleanup, the N215 line): setup
/// runs protected, the cleanup is checked callable BEFORE the goal runs
/// (cases 3 and 7 — the throw stays unthrown), runs exactly once via
/// once/1, and its failure or bindings never affect the answer.</summary>
public sealed class SetupCallCleanupConformance
{
    private static void True(string q) =>
        Assert.True(new PrologEngine().Query(q).Success, q);
    private static void False(string q) =>
        Assert.False(new PrologEngine().Query(q).Success, q);
    private static void Raises(string goal, string pattern) =>
        True($"catch(({goal}), error({pattern}, _), true).");

    [Fact] public void S01_SetupFails() => False("setup_call_cleanup(fail, _, _).");

    [Fact] public void S02_SetupThrows() =>
        True("catch(setup_call_cleanup(throw(ex), _, _), ex, true).");

    [Fact] public void S03_VarCleanup_CheckedBeforeGoal() =>
        // The goal would throw `unthrown` — but the unbound cleanup is
        // rejected first, so it never runs.
        Raises("setup_call_cleanup(true, throw(unthrown), _)", "instantiation_error");

    [Fact] public void S04_CleanupRunsOnce() =>
        True("setup_call_cleanup(true, true, ( true ; throw(x) )).");

    [Fact] public void S05_CleanupCannotRebindTheAnswer() =>
        True("setup_call_cleanup(true, X = 1, X = 2), X == 1.");

    [Fact] public void S06_CleanupBindingIsImplementationDefined() =>
        True("setup_call_cleanup(true, true, _ = 2).");

    [Fact] public void S07_CleanupVarAtCallTime() =>
        // X would be `true` by cleanup time; what counts is call time.
        Raises("setup_call_cleanup(true, X = true, X)", "instantiation_error");

    [Fact] public void S08_SetupBoundCleanupThrows() =>
        True("catch(setup_call_cleanup(X = throw(ex), true, X), ex, true).");

    [Fact] public void S09_CleanupFailureIsIgnored() =>
        True("setup_call_cleanup(true, true, fail).");

    [Fact]
    public void S10_FileRoundTrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway-scc-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "f.pl").Replace('\\', '/');
        File.WriteAllText(f, "hello(world).\n");
        try
        {
            True($"setup_call_cleanup(open('{f}', read, S), read(S, X), close(S)), "
               + "X == hello(world).");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
