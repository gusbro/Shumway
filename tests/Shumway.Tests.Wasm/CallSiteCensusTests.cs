using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>The compiler counts call sites per (caller, callee) while it
/// walks the instructions. It is the evidence for whether the one group
/// module could be split along some boundary: inside the group a call is a
/// branch, across groups it closes the chain and re-enters through the
/// interpreter, so an edge that crosses a proposed boundary is a proposed
/// host round trip.</summary>
public sealed class CallSiteCensusTests(ITestOutputHelper o)
{
    // Hand-countable: leaf/1 calls nothing; mid/1 calls leaf twice; top/1
    // calls mid twice and leaf once. Body calls only -- a fact has none.
    private const string Corpus = """
        :- public leaf/1.
        :- public mid/1.
        :- public top/1.
        leaf(0).
        mid(X) :- leaf(X), leaf(X).
        top(X) :- mid(X), mid(X), leaf(X).
        """;

    [Fact]
    public void CallSitesAreCountedPerCallerCalleePair()
    {
        var e = new PrologEngine();
        e.ConsultString(Corpus);
        e.Query("true.");
        var env = new EngineWasmCompileEnv();
        var members = new List<WasmGroupMember>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(e))
        {
            var (aid, _) = FunctorTable.Lookup(pred.FunctorId);
            string n = AtomTable.GetById(aid)?.Name ?? "";
            if (n is "leaf" or "mid" or "top")
                members.Add(new WasmGroupMember(pred, addr, null));
        }
        Assert.Equal(3, members.Count);

        var entry = WasmPredicateCompiler.CompileGroup(members, env);
        int Fid(string name, int arity)
            => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);
        int Sites(string from, string to)
            => entry.CallSites.TryGetValue((Fid(from, 1), Fid(to, 1)), out int n) ? n : 0;

        foreach (var ((a, b), n) in entry.CallSites)
        {
            var (aa, ar) = FunctorTable.Lookup(a);
            var (ba, br) = FunctorTable.Lookup(b);
            o.WriteLine($"{AtomTable.GetById(aa)?.Name}/{ar} -> "
                + $"{AtomTable.GetById(ba)?.Name}/{br} = {n}");
        }

        Assert.Equal(2, Sites("mid", "leaf"));
        Assert.Equal(2, Sites("top", "mid"));
        Assert.Equal(1, Sites("top", "leaf"));
        // leaf/1 is a fact: it calls nothing, and nothing invents an edge.
        Assert.Equal(0, Sites("leaf", "leaf"));
        Assert.Equal(0, Sites("leaf", "mid"));
        Assert.Equal(0, Sites("mid", "top"));
    }
}
