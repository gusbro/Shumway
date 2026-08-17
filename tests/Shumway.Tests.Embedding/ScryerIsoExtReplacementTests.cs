using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The scryer-dialect definition replacement: Scryer's iso_ext.pl
/// implements setup_call_cleanup/3 over its VM's choice-point natives
/// ('$get_b_value', the scc cleaner and ball stacks) that no emulation can
/// honor — the consult pipeline DROPS those definitions at load, so every
/// resolution falls through to Shumway's own builtin of the same ISO
/// contract: the importer's call, the module's internal callers, and
/// call_cleanup/2 (whose one clause rides setup_call_cleanup).</summary>
public sealed class ScryerIsoExtReplacementTests : IDisposable
{
    private readonly string _dir;
    private readonly PrologEngine _e;

    public ScryerIsoExtReplacementTests()
    {
        _dir = Path.Combine(Path.GetTempPath(),
            "shumway-isoext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        // A stand-in with the REAL library's shape: the exports, the
        // native-riding definitions (which must be dropped), and an internal
        // caller of setup_call_cleanup.
        File.WriteAllText(Path.Combine(_dir, "iso_ext.pl"), """
            :- module(iso_ext, [setup_call_cleanup/3,
                                call_cleanup/2,
                                with_note/2]).
            setup_call_cleanup(S, G, C) :-
                '$get_b_value'(B),
                '$no_such_native'(S, G, C, B).
            call_cleanup(G, C) :- setup_call_cleanup(true, G, C).
            with_note(G, N) :-
                setup_call_cleanup(true, G, assertz(note(N))).
            """);
        _e = new PrologEngine();
        _e.AddLibraryDirectorySpec("scryer:" + _dir.Replace('\\', '/'));
        _e.ConsultString(":- use_module(library(iso_ext)).");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ImportedSetupCallCleanup_IsShumways()
    {
        // Scryer's definition would die in '$get_b_value'; the drop makes the
        // import fall through to the prelude's — which runs the cleanup.
        Assert.True(_e.Query(
            "setup_call_cleanup(assertz(s1), X = 1, assertz(c1)), X == 1, s1, c1.")
            .Success);
    }

    [Fact]
    public void CallCleanup_RidesTheSameReplacement()
    {
        Assert.True(_e.Query(
            "call_cleanup(X = 2, assertz(c2)), X == 2, c2.").Success);
    }

    [Fact]
    public void TheModulesInternalCallers_FallThroughToo()
    {
        // with_note/2 calls setup_call_cleanup from INSIDE iso_ext: with the
        // definition dropped before locals are computed, that body call
        // compiles bare and reaches the builtin.
        Assert.True(_e.Query("with_note(Y = 3, done), Y == 3, note(done).").Success);
    }
}
