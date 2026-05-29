using Shumway.Compiler.Il;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 216 stage 1: validates the indexed-dispatch recogniser
/// (<see cref="IlIndexedDispatch.TryDescribe"/>) parses the WAM switch
/// machinery into correct chain nodes — in particular that a var-head
/// clause joins every key's bucket, and that each key's bucket lists
/// exactly the clauses whose head can match in source order. The runtime
/// resolver is exercised end-to-end (Tier-1 == Tier-0) by the emit tests;
/// here we check the static structure the emit will rely on.
/// </summary>
public class Chunk216ModelTests
{
    private static IlIndexedDispatchInfo Describe(string src)
    {
        var clauses = new ClauseReader(src).ReadAll().ToList();
        var module = new ModuleCompiler().Compile(clauses);
        var pred = module.Predicates.Single(p => p.ClauseCount > 1);
        // Permissive body check — we're validating chain structure, not the
        // IL subset (these fact bodies are trivially emittable).
        Assert.True(IlIndexedDispatch.TryDescribe(pred, (_, _) => true, out var info));
        return info!;
    }

    /// <summary>Walks the chain from <paramref name="entryCursor"/> via
    /// NextCursor, returning the clause-index sequence it runs.</summary>
    private static List<int> Walk(IlIndexedDispatchInfo info, int entryCursor)
    {
        var seq = new List<int>();
        int c = entryCursor;
        while (c >= 0)
        {
            var node = info.Nodes[c];
            seq.Add(node.ClauseIndex);
            c = node.NextCursor;
        }
        return seq;
    }

    /// <summary>All clause sequences reachable as dispatch entries (every
    /// switch-table value/default that lands on a chain head or a
    /// deterministic clause body).</summary>
    private static List<List<int>> AllEntrySequences(IlIndexedDispatchInfo info)
    {
        var result = new List<List<int>>();
        foreach (var (_, cursor) in info.AddrToEntryCursor)
            result.Add(Walk(info, cursor));
        return result;
    }

    [Fact]
    public void Bucket_VarHeadClause_JoinsEveryKeysBucket()
    {
        // p(a,1). p(X,2). p(b,3).
        // key a -> {clause0 (a), clause1 (var)} ; key b -> {clause1 (var), clause2 (b)}.
        var info = Describe("p(a, 1).\np(X, 2).\np(b, 3).\n");
        Assert.Equal(3, info.Clauses.Count);

        var seqs = AllEntrySequences(info);
        // The two buckets (var-head clause 1 shared) must both be present.
        Assert.Contains(seqs, s => s.SequenceEqual(new[] { 0, 1 }));
        Assert.Contains(seqs, s => s.SequenceEqual(new[] { 1, 2 }));
        // The full var chain (all clauses in source order) is the default.
        Assert.Contains(seqs, s => s.SequenceEqual(new[] { 0, 1, 2 }));
    }

    [Fact]
    public void PureDiscrimination_EachKeyOneClause()
    {
        // q(a,1). q(b,2). q(c,3). — distinct keys, no var head.
        var info = Describe("q(a, 1).\nq(b, 2).\nq(c, 3).\n");
        Assert.Equal(3, info.Clauses.Count);

        var seqs = AllEntrySequences(info);
        // Deterministic single-clause entries for each key.
        Assert.Contains(seqs, s => s.SequenceEqual(new[] { 0 }));
        Assert.Contains(seqs, s => s.SequenceEqual(new[] { 1 }));
        Assert.Contains(seqs, s => s.SequenceEqual(new[] { 2 }));
        // Var chain still present as the fallback.
        Assert.Contains(seqs, s => s.SequenceEqual(new[] { 0, 1, 2 }));
    }

    [Fact]
    public void SharedKey_BucketHasBothClauses()
    {
        // r(a,1). r(a,2). r(b,3). — key a matches clauses 0 and 1.
        var info = Describe("r(a, 1).\nr(a, 2).\nr(b, 3).\n");
        Assert.Equal(3, info.Clauses.Count);

        var seqs = AllEntrySequences(info);
        Assert.Contains(seqs, s => s.SequenceEqual(new[] { 0, 1 }));   // key a
        Assert.Contains(seqs, s => s.SequenceEqual(new[] { 0, 1, 2 })); // var chain
    }

    [Fact]
    public void EveryNode_NextCursorFormsAcyclicChain()
    {
        // Sanity: no node points to itself or forms a cycle; every chain
        // terminates at -1.
        var info = Describe("p(a, 1).\np(X, 2).\np(b, 3).\n");
        for (int start = 0; start < info.Nodes.Count; start++)
        {
            var seen = new HashSet<int>();
            int c = start;
            while (c >= 0)
            {
                Assert.True(seen.Add(c), $"cycle at node {c} from {start}");
                Assert.InRange(info.Nodes[c].ClauseIndex, 0, info.Clauses.Count - 1);
                c = info.Nodes[c].NextCursor;
            }
        }
    }
}
