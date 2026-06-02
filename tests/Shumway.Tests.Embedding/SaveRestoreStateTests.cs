using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Save-state chunk 264 — <c>save_state/1,2</c> and
/// <c>restore_state/1</c>. Snapshots the engine's user-visible state
/// (consult history + dynamic clauses) to a V6 .shum bundle; the
/// counterpart resets a fresh engine and replays.
/// </summary>
public class SaveRestoreStateTests
{
    [Fact]
    public void Roundtrip_FullSnapshot_ReplaysConsultedSource()
    {
        var src = new PrologEngine();
        src.ConsultString("greet(world).  fav(blue).");
        byte[] snap = src.SaveStateToBytes();

        var dst = new PrologEngine();
        dst.RestoreStateFromBytes(snap);
        Assert.True(dst.Query("greet(world).").Success);
        Assert.True(dst.Query("fav(blue).").Success);
        Assert.False(dst.Query("greet(nobody).").Success);
    }

    [Fact]
    public void Roundtrip_FullSnapshot_CarriesDynamicClauses()
    {
        var src = new PrologEngine();
        src.ConsultString(":- dynamic counter/1.");
        src.Query("assertz(counter(1)).");
        src.Query("assertz(counter(2)).");
        src.Query("assertz(counter(3)).");
        byte[] snap = src.SaveStateToBytes();

        var dst = new PrologEngine();
        dst.RestoreStateFromBytes(snap);
        var sol = dst.Query("findall(X, counter(X), L).");
        Assert.True(sol.Success);
        Assert.Equal("[1, 2, 3]", AstTermRenderer.Render(sol["L"]!));
    }

    [Fact]
    public void Roundtrip_FullSnapshot_RestoreResetsExistingState()
    {
        // A full restore drops whatever was in the target engine first.
        var src = new PrologEngine();
        src.ConsultString("p(a).");
        byte[] snap = src.SaveStateToBytes();

        var dst = new PrologEngine();
        dst.ConsultString("q(z).");
        Assert.True(dst.Query("q(z).").Success);
        dst.RestoreStateFromBytes(snap);
        // q/1 from the pre-restore consult is gone; p/1 from the
        // snapshot is in.
        Assert.True(dst.Query("p(a).").Success);
        Assert.Throws<Shumway.Core.PrologRuntimeException>(() => dst.Query("q(z)."));
    }

    [Fact]
    public void DynamicOnly_OmitsConsultedSource()
    {
        // dynamic_only snapshot captures only dynamic clauses; the
        // restore engine doesn't get the source consulted with the
        // facts.
        var src = new PrologEngine();
        src.ConsultString(":- dynamic fact/1.");
        src.Query("assertz(fact(7)).");
        Assert.True(src.Query("fact(7).").Success);
        byte[] snap = src.SaveStateToBytes(dynamicOnly: true);

        var dst = new PrologEngine();
        // Target needs the predicate declared dynamic before assertz can
        // merge into it cleanly (matches plain assertz semantics).
        dst.ConsultString(":- dynamic fact/1.");
        dst.RestoreStateFromBytes(snap);
        Assert.True(dst.Query("fact(7).").Success);
    }

    [Fact]
    public void DynamicOnly_MergesIntoExistingClauses()
    {
        var src = new PrologEngine();
        src.ConsultString(":- dynamic d/1.");
        src.Query("assertz(d(b)).");
        byte[] snap = src.SaveStateToBytes(dynamicOnly: true);

        var dst = new PrologEngine();
        dst.ConsultString(":- dynamic d/1.");
        dst.Query("assertz(d(a)).");
        dst.RestoreStateFromBytes(snap);
        var sol = dst.Query("findall(X, d(X), L).");
        Assert.Equal("[a, b]", AstTermRenderer.Render(sol["L"]!));
    }

    [Fact]
    public void RestoreState_OnNonSnapshotBundle_ThrowsInvalidDataException()
    {
        // A bundle written without a snapshot trailer (e.g. by
        // shumway-link) can't drive RestoreState.
        var ordinary = new Bundle(
            new[] { new BundleEntry("user", "fact(x).") },
            foreignAssemblies: null);
        byte[] bytes = BundleWriter.ToBytes(ordinary);

        var engine = new PrologEngine();
        Assert.Throws<InvalidDataException>(() => engine.RestoreStateFromBytes(bytes));
    }

    [Fact]
    public void SaveState_OnFreshEngine_ProducesLoadableSnapshot()
    {
        var src = new PrologEngine();
        byte[] snap = src.SaveStateToBytes();
        var dst = new PrologEngine();
        dst.RestoreStateFromBytes(snap);
        // A no-op restore should leave the engine functional.
        Assert.True(dst.Query("true.").Success);
    }

    [Fact]
    public void SaveState_PrologBuiltin_RoundTrips()
    {
        // Exercise the save_state/1 + restore_state/1 builtins through
        // a temp file.
        string path = Path.Combine(Path.GetTempPath(),
            "shumway_savestate_test_" + Guid.NewGuid().ToString("N") + ".shum");
        try
        {
            var src = new PrologEngine();
            src.ConsultString("colour(red). colour(green). colour(blue).");
            var pathEsc = path.Replace('\\', '/');
            Assert.True(src.Query($"save_state('{pathEsc}').").Success);

            var dst = new PrologEngine();
            Assert.True(dst.Query($"restore_state('{pathEsc}').").Success);
            var sol = dst.Query("findall(C, colour(C), L).");
            Assert.Equal("[red, green, blue]", AstTermRenderer.Render(sol["L"]!));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveState2_DynamicOnlyOption_Builtin()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "shumway_savestate_test_" + Guid.NewGuid().ToString("N") + ".shum");
        try
        {
            var src = new PrologEngine();
            src.ConsultString(":- dynamic kv/2.");
            src.Query("assertz(kv(a, 1)).");
            src.Query("assertz(kv(b, 2)).");
            var pathEsc = path.Replace('\\', '/');
            Assert.True(src.Query(
                $"save_state('{pathEsc}', [dynamic_only(true)]).").Success);

            var dst = new PrologEngine();
            dst.ConsultString(":- dynamic kv/2.");
            Assert.True(dst.Query($"restore_state('{pathEsc}').").Success);
            var sol = dst.Query("findall(K-V, kv(K, V), L).");
            Assert.Equal("[a-1, b-2]", AstTermRenderer.Render(sol["L"]!));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RestoreState_MissingFile_RaisesExistenceError()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => engine.Query("restore_state('does/not/exist.shum')."));
        Assert.Contains("existence_error", ex.Message);
    }
}
