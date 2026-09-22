using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>A closure built in one module and called from another.
///
/// <para><c>maplist(double(2), L, D)</c> is written here and the
/// <c>call(G, X, Y)</c> that runs it lives in library(lists), so the goal is
/// looked up relative to LISTS and lands on the bare <c>double/3</c> -- a
/// functor that names no predicate, because the one with the code is
/// <c>user$double/3</c>. The interpreter follows the address map and runs
/// the right thing. The module cannot: its jump target is a marker, markers
/// are minted per functor, and the bare one has none. So the chain closed,
/// every time.</para>
///
/// <para>Measured on clp(Z) in a browser this was the whole cost:
/// 2,891,187 of 2,892,755 chain exits, all naming <c>unwrap_with/3</c>
/// against a compiled <c>clpz$unwrap_with/3</c>.</para></summary>
public sealed class ClosureAcrossModulesTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- use_module(library(lists)).
        double(K, X, Y) :- Y is X * K.
        run(N, S) :- numlist(1, N, L), maplist(double(2), L, D), sum_list(D, S).
        """;

    [DiagFact]
    public void AClosureCalledFromAnotherModuleStaysInTheChain()
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query("run(200, S), S == 40200.").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        // The first call is what teaches the host, so measure after one.
        Assert.True(tiered.Query("run(200, _).").Success);
        WasmTierDelegate.ResetDiag();

        Assert.True(tiered.Query("run(200, S), S == 40200.").Success,
            "the mapping answer moved on the tier");
        o.WriteLine($"chains={WasmTierDelegate.DiagEntries} "
            + $"foreign={WasmTierDelegate.DiagForeignExits} "
            + $"deopts={WasmTierDelegate.DiagDeopts}");
        foreach (var (fid, hits) in WasmTierDelegate.ForeignRanking())
        {
            var (aid, ar) = Shumway.Core.FunctorTable.Lookup(fid);
            o.WriteLine($"  {hits} {Shumway.Core.AtomTable.GetById(aid)?.Name}/{ar}");
        }

        // 200 elements, one closure call each. A chain that cannot continue
        // into the callee closes and reopens per element, so the count is
        // the measurement: it was ~200 and it has to be none.
        Assert.Equal(0L, WasmTierDelegate.DiagForeignExits);
    }

    /// <summary>The renaming is by ADDRESS, so it must not fire when the
    /// names disagree about which code they mean. A predicate that is NOT
    /// installed and shares no entry with one that is still answers, the
    /// long way round.</summary>
    [Fact]
    public void AGenuinelyUncompiledCalleeStillAnswers()
    {
        const string Program = """
            :- use_module(library(lists)).
            :- dynamic(pick/3).
            pick(k, a, 1).
            pick(k, b, 2).
            run(L) :- findall(X-N, (member(X, [a,b]), call(pick(k), X, N)), L).
            """;
        var plain = new PrologEngine();
        plain.ConsultString(Program);
        Assert.True(plain.Query("run(L), L == [a-1, b-2].").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Program);
        Assert.True(tiered.Query("run(_).").Success);
        Assert.True(tiered.Query("run(L), L == [a-1, b-2].").Success,
            "a callee nothing compiled stopped answering");
    }
}
