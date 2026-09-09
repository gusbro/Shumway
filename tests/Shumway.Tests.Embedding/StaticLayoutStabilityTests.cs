using Shumway.Compiler.Wam;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Consulting a source must not move the code that was already
/// compiled. The static region is laid out append-only: a predicate keeps
/// the ordinal it was first laid out with, and anything new — or CHANGED by
/// a reconsult — takes a fresh ordinal at the end.
///
/// <para>The layout used to be "whatever the module compiler produced, then
/// the precompiled prelude appended", so consulting one fact pushed the
/// whole prelude down by that fact's size. Every address baked against the
/// old layout then pointed into different code: the wasm tier's modules bake
/// deopt pcs, resume markers and BP encodings, and that is how a
/// library load turned into "reserved_invalid opcode … bytecode
/// corruption".</para></summary>
public sealed class StaticLayoutStabilityTests
{
    private static Dictionary<int, (int Addr, string Name)> Layout(PrologEngine e)
    {
        var d = new Dictionary<int, (int, string)>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(e))
        {
            var (aid, ar) = Shumway.Core.FunctorTable.Lookup(pred.FunctorId);
            d[pred.FunctorId] =
                (addr, $"{Shumway.Core.AtomTable.GetById(aid)?.Name}/{ar}");
        }
        return d;
    }

    private static (int Same, int Moved, List<string> MovedNames) Compare(
        Dictionary<int, (int Addr, string Name)> before,
        Dictionary<int, (int Addr, string Name)> after)
    {
        int same = 0, moved = 0;
        var names = new List<string>();
        foreach (var (fid, (addr, name)) in before)
        {
            if (!after.TryGetValue(fid, out var now)) continue;
            if (now.Addr == addr) same++;
            else { moved++; if (names.Count < 5) names.Add($"{name} {addr}->{now.Addr}"); }
        }
        return (same, moved, names);
    }

    [Fact]
    public void ConsultingNewCodeDoesNotMoveWhatWasAlreadyThere()
    {
        var e = new PrologEngine();
        e.Query("true.");                       // materialise the static link
        var before = Layout(e);
        Assert.True(before.Count > 100, $"expected the prelude, saw {before.Count}");

        e.ConsultString("newfact(1).\nnewfact(2).\nnewrule(X) :- newfact(X).\n");
        e.Query("true.");
        var after = Layout(e);
        Assert.True(after.Count > before.Count, "the new predicates should be linked");

        var (same, moved, names) = Compare(before, after);
        Assert.Equal(0, moved);
        Assert.Equal(before.Count, same);
        Assert.True(e.Query("newrule(2).").Success);
        Assert.True(e.Query("member(b, [a,b,c]).").Success);
        Assert.Empty(names);
    }

    [Fact]
    public void ALibraryLoadDoesNotMoveThePreludeEither()
    {
        // The reported shape: use_module(library(clpfd)) brings hundreds of
        // predicates in one go.
        var e = new PrologEngine();
        e.Query("true.");
        var before = Layout(e);

        e.ConsultString(":- use_module(library(clpfd)).\nsmall(X) :- X in 1..3, X #> 1.\n");
        e.Query("true.");
        var after = Layout(e);
        Assert.True(after.Count > before.Count,
            $"expected the library to link, {before.Count} -> {after.Count}");

        var (same, moved, _) = Compare(before, after);
        Assert.Equal(0, moved);
        Assert.Equal(before.Count, same);
        Assert.True(e.Query("findall(X, (small(X), label([X])), [2,3]).").Success);
    }

    /// <summary>A RECONSULT still shifts code after the predicate it
    /// changed: the changed predicate takes a fresh ordinal at the end and
    /// leaves a hole where it was, so everything that followed moves up by
    /// its old size. Closing that needs the linker to pad the hole (dead
    /// bytes until a compaction), which is a design decision of its own.
    /// What is pinned here is that the program stays CORRECT across a
    /// reconsult — the address residue is named, not asserted.</summary>
    [Fact]
    public void AReconsultKeepsTheProgramCorrect()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            p(1).
            q(X) :- p(X).
            """);
        Assert.True(e.Query("findall(X, q(X), [1]).").Success);

        e.ReconsultString("""
            p(1).
            p(2).
            p(3).
            q(X) :- p(X).
            """);
        Assert.True(e.Query("findall(X, q(X), [1,2,3]).").Success);
        Assert.True(e.Query("member(b, [a,b,c]).").Success);
        Assert.True(e.Query("msort([3,1,2], [1,2,3]).").Success);
    }
}
