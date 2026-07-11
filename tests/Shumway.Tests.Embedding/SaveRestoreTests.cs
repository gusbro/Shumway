using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Arity <c>save/0</c>, <c>save/1</c>, <c>restore/0</c>, <c>restore/1</c> —
/// dynamic-database snapshots with destructive REPLACE semantics: restore
/// wipes every user dynamic predicate's clauses and re-installs the
/// snapshot (in-memory for /0, from a file for /1). Declarations survive;
/// static predicates and engine-internal ($-prefixed) dynamics are never
/// touched. Distinct from the save_state/restore_state merge/replay family.
/// </summary>
public class SaveRestoreTests
{
    private static PrologEngine Engine(string program)
    {
        var e = new PrologEngine();
        e.ConsultString(program);
        return e;
    }

    private const string Program =
        ":- public go/0.\n"
        + ":- dynamic f/1.\n"
        + ":- dynamic g/2.\n"
        + "f(1).\n"
        + "f(2).\n"
        + "s(static_one).\n"
        + "go.\n";

    [Fact]
    public void SaveRestore_InMemory_RoundTrip()
    {
        var e = Engine(Program);
        Assert.True(e.Query("assertz(g(a, 1)), save.").Success);
        // Mutate after the snapshot: add, remove, add to another predicate.
        Assert.True(e.Query("assertz(f(3)), retract(f(1)), assertz(g(b, 2)).").Success);
        Assert.True(e.Query("findall(X, f(X), L), L == [2, 3].").Success);
        // Restore: exactly the snapshot state — f(1), f(2), g(a,1).
        Assert.True(e.Query("restore.").Success);
        Assert.True(e.Query("findall(X, f(X), L), L == [1, 2].").Success);
        Assert.True(e.Query("findall(K-V, g(K, V), L), L == [a-1].").Success);
        // Restore is repeatable (the snapshot is not consumed).
        Assert.True(e.Query("retract(f(2)), restore, findall(X, f(X), L), L == [1, 2].").Success);
    }

    [Fact]
    public void Restore_WithoutSave_WipesAllUserDynamics()
    {
        var e = Engine(Program);
        Assert.True(e.Query("assertz(g(k, 9)).").Success);
        Assert.True(e.Query("restore.").Success);
        // Every user dynamic is empty now — calls FAIL (no existence_error:
        // the declarations survive).
        Assert.False(e.Query("f(_).").Success);
        Assert.False(e.Query("g(_, _).").Success);
        // And a later assert works normally.
        Assert.True(e.Query("assertz(f(7)), f(7).").Success);
    }

    [Fact]
    public void Restore_MidQuery_LogicalUpdateView()
    {
        var e = Engine(Program);
        // One query: snapshot, mutate, restore, and the SAME query's later
        // goals see the restored state.
        Assert.True(e.Query(
            "save, assertz(f(99)), retract(f(1)), restore, "
            + "findall(X, f(X), L), L == [1, 2].").Success);
    }

    [Fact]
    public void Restore_DoesNotTouchStatics()
    {
        var e = Engine(Program);
        Assert.True(e.Query("restore.").Success);
        Assert.True(e.Query("s(static_one).").Success);
        Assert.True(e.Query("go.").Success);
    }

    [Fact]
    public void Restore_KeepsInternalDollarDynamics()
    {
        var e = Engine(Program);
        // A $-prefixed dynamic is engine/library-internal by convention:
        // excluded from BOTH the snapshot and the restore wipe.
        Assert.True(e.Query("assertz('$mine'(1)).").Success);
        Assert.True(e.Query("save.").Success);
        Assert.True(e.Query("assertz('$mine'(2)).").Success);
        Assert.True(e.Query("restore.").Success);
        Assert.True(e.Query("findall(X, '$mine'(X), L), L == [1, 2].").Success);
    }

    [Fact]
    public void SaveRestore_File_RoundTrip()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"shumway_saverestore_{System.Guid.NewGuid():N}.sav");
        try
        {
            var e = Engine(Program);
            Assert.True(e.Query("assertz(g(x, 5)).").Success);
            Assert.True(e.Query($"save('{path.Replace("\\", "\\\\")}').").Success);
            Assert.True(e.Query("assertz(f(42)), retract(f(2)), retract(g(x, 5)).").Success);
            Assert.True(e.Query($"restore('{path.Replace("\\", "\\\\")}').").Success);
            Assert.True(e.Query("findall(X, f(X), L), L == [1, 2].").Success);
            Assert.True(e.Query("g(x, 5).").Success);

            // A FRESH engine (same static program) restores the same file.
            var e2 = Engine(Program);
            Assert.True(e2.Query("assertz(f(777)).").Success);
            Assert.True(e2.Query($"restore('{path.Replace("\\", "\\\\")}').").Success);
            Assert.True(e2.Query("findall(X, f(X), L), L == [1, 2].").Success);
            Assert.True(e2.Query("g(x, 5).").Success);
            Assert.False(e2.Query("f(777).").Success);
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Restore_MissingFile_RaisesCatchableError()
    {
        var e = Engine(Program);
        Assert.True(e.Query(
            "catch(restore(no_such_snapshot_file_xyz), _, true).").Success);
        // And the database was NOT wiped by the failed restore.
        Assert.True(e.Query("f(1).").Success);
    }

    [Fact]
    public void Restore_ModuleLocalDynamic_RestoresToMangledSlot()
    {
        // A module-local dynamic's storage name is mangled (m$p) — the
        // snapshot must restore into the SAME slot, reachable through the
        // module's public accessor.
        var e = new PrologEngine();
        e.ConsultString(
            ":- module(m).\n"
            + ":- public padd/1, pget/1.\n"
            + ":- dynamic p/1.\n"
            + "p(0).\n"
            + "padd(X) :- assertz(p(X)).\n"
            + "pget(X) :- p(X).\n");
        Assert.True(e.Query("save.").Success);
        Assert.True(e.Query("padd(5), pget(5).").Success);
        Assert.True(e.Query("restore.").Success);
        Assert.True(e.Query("pget(0).").Success);
        Assert.False(e.Query("pget(5).").Success);
    }
}
