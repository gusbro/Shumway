using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 24 chunk 266 — Arity-Prolog recorded database. A second
/// in-memory store separate from dynamic predicates, indexed by an
/// arbitrary key term, with stable integer references.
/// </summary>
public class RecordedDbTests
{
    [Fact]
    public void Recordz_Then_Recorded_Enumerates_In_Insertion_Order()
    {
        var engine = new PrologEngine();
        engine.Query("recordz(items, a, _).");
        engine.Query("recordz(items, b, _).");
        engine.Query("recordz(items, c, _).");
        var sol = engine.Query("findall(X, recorded(items, X, _), L).");
        Assert.True(sol.Success);
        Assert.Equal("[a, b, c]", AstTermRenderer.Render(sol["L"]!));
    }

    [Fact]
    public void Recorda_Prepends_To_Chain()
    {
        var engine = new PrologEngine();
        engine.Query("recordz(items, b, _).");
        engine.Query("recorda(items, a, _).");
        engine.Query("recordz(items, c, _).");
        var sol = engine.Query("findall(X, recorded(items, X, _), L).");
        Assert.Equal("[a, b, c]", AstTermRenderer.Render(sol["L"]!));
    }

    [Fact]
    public void Recorded_Returns_Stable_Ref_That_Instance_Resolves()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("recordz(k, hello(world), R).");
        Assert.True(sol.Success);
        Assert.IsType<IntTerm>(sol["R"]);
        long refVal = ((IntTerm)sol["R"]!).Value;

        var sol2 = engine.Query($"instance({refVal}, X).");
        Assert.True(sol2.Success);
        Assert.Equal("hello(world)", AstTermRenderer.Render(sol2["X"]!));
    }

    [Fact]
    public void Erase_RemovesEntry_And_LaterInstance_Fails()
    {
        var engine = new PrologEngine();
        var addSol = engine.Query("recordz(k, val, R).");
        long refVal = ((IntTerm)addSol["R"]!).Value;
        Assert.True(engine.Query($"erase({refVal}).").Success);
        Assert.False(engine.Query($"instance({refVal}, _).").Success);
    }

    [Fact]
    public void Erase_OnUnknownRef_Fails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("erase(99999).").Success);
    }

    [Fact]
    public void Eraseall_RemovesEveryEntryUnderKey()
    {
        var engine = new PrologEngine();
        engine.Query("recordz(k, a, _).");
        engine.Query("recordz(k, b, _).");
        engine.Query("recordz(k, c, _).");
        Assert.True(engine.Query("eraseall(k).").Success);
        var sol = engine.Query("findall(X, recorded(k, X, _), L).");
        Assert.Equal("[]", AstTermRenderer.Render(sol["L"]!));
    }

    [Fact]
    public void Compound_KeyTerm_Works()
    {
        // Key can be any term, including compounds.
        var engine = new PrologEngine();
        engine.Query("recordz(person(alice), age(30), _).");
        engine.Query("recordz(person(alice), city(boston), _).");
        engine.Query("recordz(person(bob), age(25), _).");
        var sol = engine.Query("findall(F, recorded(person(alice), F, _), L).");
        Assert.Equal("[age(30), city(boston)]", AstTermRenderer.Render(sol["L"]!));
    }

    [Fact]
    public void KeyCount_ReturnsExactCount()
    {
        var engine = new PrologEngine();
        engine.Query("recordz(k, a, _).");
        engine.Query("recordz(k, b, _).");
        var sol = engine.Query("key_count(k, N).");
        Assert.Equal(2L, ((IntTerm)sol["N"]!).Value);
    }

    [Fact]
    public void Keys_EnumeratesAllKeys()
    {
        var engine = new PrologEngine();
        engine.Query("recordz(k1, a, _).");
        engine.Query("recordz(k2, b, _).");
        engine.Query("recordz(k3, c, _).");
        var sol = engine.Query("findall(K, keys(K), L), sort(L, S).");
        Assert.Equal("[k1, k2, k3]", AstTermRenderer.Render(sol["S"]!));
    }

    [Fact]
    public void Ref_TestsIfArgIsLiveRef()
    {
        var engine = new PrologEngine();
        var add = engine.Query("recordz(k, v, R).");
        long live = ((IntTerm)add["R"]!).Value;
        Assert.True(engine.Query($"ref({live}).").Success);
        Assert.False(engine.Query("ref(99999).").Success);
        Assert.False(engine.Query("ref(foo).").Success);
        engine.Query($"erase({live}).");
        Assert.False(engine.Query($"ref({live}).").Success);
    }

    [Fact]
    public void Replace_ChangesTerm_KeepsRefAndPosition()
    {
        var engine = new PrologEngine();
        engine.Query("recordz(k, a, _).");
        var add = engine.Query("recordz(k, b, R).");
        long midRef = ((IntTerm)add["R"]!).Value;
        engine.Query("recordz(k, c, _).");
        Assert.True(engine.Query($"replace({midRef}, b_replaced).").Success);
        var sol = engine.Query("findall(X, recorded(k, X, _), L).");
        Assert.Equal("[a, b_replaced, c]", AstTermRenderer.Render(sol["L"]!));
    }

    [Fact]
    public void Nref_Pref_WalkTheChain()
    {
        var engine = new PrologEngine();
        engine.Query("recordz(k, a, _).");
        var midAdd = engine.Query("recordz(k, b, R).");
        engine.Query("recordz(k, c, _).");
        long mid = ((IntTerm)midAdd["R"]!).Value;

        var next = engine.Query($"nref({mid}, N).");
        Assert.True(next.Success);
        Assert.Equal("c", AstTermRenderer.Render(
            engine.Query($"instance({((IntTerm)next["N"]!).Value}, X).")["X"]!));

        var prev = engine.Query($"pref({mid}, P).");
        Assert.True(prev.Success);
        Assert.Equal("a", AstTermRenderer.Render(
            engine.Query($"instance({((IntTerm)prev["P"]!).Value}, X).")["X"]!));
    }

    [Fact]
    public void RecordBefore_And_RecordAfter_InsertInChain()
    {
        var engine = new PrologEngine();
        var add = engine.Query("recordz(k, b, R).");
        long mid = ((IntTerm)add["R"]!).Value;
        engine.Query($"record_before({mid}, a, _).");
        engine.Query($"record_after({mid}, c, _).");
        var sol = engine.Query("findall(X, recorded(k, X, _), L).");
        Assert.Equal("[a, b, c]", AstTermRenderer.Render(sol["L"]!));
    }

    [Fact]
    public void Recorded_OnUnknownKey_Fails()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Query("recorded(missing, _, _).").Success);
    }

    [Fact]
    public void Recorded_DoesNotInterferWithDynamicPredicates()
    {
        // The recorded DB is a completely separate store.
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic foo/1.");
        engine.Query("assertz(foo(dyn)).");
        engine.Query("recordz(foo, rec, _).");
        Assert.True(engine.Query("foo(dyn).").Success);
        var sol = engine.Query("recorded(foo, X, _).");
        Assert.Equal("rec", AstTermRenderer.Render(sol["X"]!));
    }
}
