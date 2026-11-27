using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-054, amendment: with a compiler aside, a dynamic predicate's
/// snapshot is compiled away from the engine's thread. The predicate runs
/// its bytecode until the module is there, a mutation while it compiles
/// drops the module, and the module is installed again after the code space
/// is built again, with no second compile. Every answer is checked against
/// Tier-0.</summary>
public sealed class SnapshotCompiledAsideTests
{
    private const string Corpus = """
        :- dynamic(d/1).
        d(1).
        d(2).
        d(3).
        run(0) :- !.
        run(N) :- d(2), N1 is N - 1, run(N1).
        all(L) :- findall(X, d(X), L).
        :- dynamic(c/1).
        c(1).
        c(2) :- d(3).
        every_c(L) :- findall(X, c(X), L).
        """;

    private static int Fid(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    private sealed class Rig
    {
        public required PrologEngine Plain;
        public required PrologEngine Tiered;
        public required WasmPromotionStore Wasm;
        // The compiles handed over and not run yet: the test is the worker.
        public readonly List<Action> Handed = new();

        public void Agree(string goal)
        {
            Assert.True(Plain.Query(goal).Success, goal + " on Tier-0");
            Assert.True(Tiered.Query(goal).Success, goal + " on the tier");
        }

        public void Both(Action<PrologEngine> change)
        {
            change(Plain);
            change(Tiered);
        }

        public void RunHanded()
        {
            var work = Handed.ToArray();
            Handed.Clear();
            foreach (var w in work) w();
        }
    }

    private static Rig Build(Func<Rig, Action<Action>>? runner = null)
    {
        var plain = new PrologEngine();
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Corpus);
        var (tiered, _, world) = TieredEngine.BuildWithWorld(Corpus);
        var rig = new Rig { Plain = plain, Tiered = tiered, Wasm = tiered.IlPromotion.Wasm! };
        var run = runner?.Invoke(rig) ?? rig.Handed.Add;
        rig.Wasm.SnapshotCompiler = (snapshot, digest, done) =>
            WasmBundleTier.CompileSnapshotAside(tiered, world, snapshot, digest, done, run);
        return rig;
    }

    [Fact]
    public void WhileItCompiles_ThePredicateRunsItsBytecode_ThenTheModuleIsInstalled()
    {
        var rig = Build();
        int d = Fid("d", 1);

        rig.Agree("run(50).");
        Assert.Equal(1, rig.Wasm.SnapshotCompilesRequested);
        Assert.Single(rig.Handed);
        Assert.True(rig.Wasm.SnapshotPending(d));
        Assert.False(rig.Wasm.HasShadow(d), "the snapshot was compiled where it was called");
        rig.Agree("all(L), L == [1, 2, 3].");
        Assert.False(rig.Wasm.HasShadow(d));

        rig.RunHanded();
        rig.Agree("run(5).");
        Assert.True(rig.Wasm.HasShadow(d), "the compiled snapshot was not installed");
        Assert.Equal(1, rig.Wasm.CompiledSnapshotInstalls);
        Assert.Equal(1, rig.Wasm.SnapshotCompilesRequested);
        Assert.False(rig.Wasm.SnapshotPending(d));
        rig.Agree("all(L), L == [1, 2, 3].");
        rig.Agree(@"\+ d(4), d(X), X == 1.");
    }

    [Fact]
    public void AMutationWhileItCompiles_DropsTheModule_AndTheNextOneIsCompiledFromTheNewClauses()
    {
        var rig = Build();
        int d = Fid("d", 1);

        rig.Agree("run(5).");
        Assert.True(rig.Wasm.SnapshotPending(d));
        rig.Agree("assertz(d(4)).");
        Assert.False(rig.Wasm.SnapshotPending(d));

        rig.RunHanded();
        rig.Agree("all(L), L == [1, 2, 3, 4].");
        Assert.Equal(0, rig.Wasm.CompiledSnapshotInstalls);
        Assert.Equal(2, rig.Wasm.SnapshotCompilesRequested);

        rig.RunHanded();
        rig.Agree("all(L), L == [1, 2, 3, 4].");
        Assert.True(rig.Wasm.HasShadow(d));
        Assert.Equal(1, rig.Wasm.CompiledSnapshotInstalls);
        rig.Agree("retract(d(1)).");
        rig.Agree("all(L), L == [2, 3, 4].");
    }

    [Fact]
    public void AfterTheCodeSpaceIsBuiltAgain_TheModuleIsInstalledWithNoSecondCompile()
    {
        var rig = Build();
        int d = Fid("d", 1);
        rig.Agree("run(5).");
        rig.RunHanded();
        rig.Agree("run(5).");
        Assert.True(rig.Wasm.HasShadow(d));
        int requested = rig.Wasm.SnapshotCompilesRequested;

        rig.Both(e => e.ConsultString("consulted_later(1)."));
        rig.Agree("consulted_later(1), all(L), L == [1, 2, 3].");
        rig.Agree("run(5).");

        Assert.True(rig.Wasm.HasShadow(d), "the snapshot was not installed over the new code space");
        Assert.Equal(2, rig.Wasm.CompiledSnapshotInstalls);
        Assert.Equal(requested, rig.Wasm.SnapshotCompilesRequested);
        Assert.Empty(rig.Handed);
    }

    [Fact]
    public void APredicateWithARule_IsCompiledAsideToo()
    {
        var rig = Build();
        int c = Fid("c", 1);
        rig.Agree("every_c(L), L == [1, 2].");
        Assert.True(rig.Wasm.SnapshotPending(c));
        rig.RunHanded();
        rig.Agree("every_c(L), L == [1, 2].");
        rig.Agree("every_c(L), L == [1, 2].");
        Assert.True(rig.Wasm.HasShadow(c));
        rig.Agree("retract(d(3)).");
        rig.Agree("every_c(L), L == [1].");
    }

    // The same with the compile on a pool thread, as a page runs it.
    [Fact]
    public void CompiledOnAPoolThread_TheModuleIsInstalledAtTheNextCall()
    {
        using var compiled = new System.Threading.CountdownEvent(1);
        var rig = Build(_ => work => System.Threading.Tasks.Task.Run(() =>
        {
            work();
            compiled.Signal();
        }));
        int d = Fid("d", 1);
        rig.Agree("run(5).");
        Assert.Equal(1, rig.Wasm.SnapshotCompilesRequested);
        Assert.True(compiled.Wait(60_000), "the snapshot did not compile within the timeout");

        rig.Agree("run(5).");
        Assert.True(rig.Wasm.HasShadow(d));
        rig.Agree("all(L), L == [1, 2, 3].");
    }
}
