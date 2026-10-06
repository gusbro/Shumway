using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A resume row that does not resolve is a miss, and the caller
/// carries on as it would without a table: a partition's fail case hands
/// FAIL on, to the resolver and then the host. The probe once wrote the
/// row's cursor field before it knew the row resolved, so a miss continued
/// at cursor 0, the first leader of the module, and the query never ended.
///
/// <para>A miss on a live choice point: the first answer leaves choice
/// points in e/1 and f/1, e/1 leaves the tier between answers, and the next
/// answer exhausts f/1 inside wasm and fails into e/1's choice point, whose
/// row is gone.</para></summary>
public sealed class ResumeProbeMissTests : IDisposable
{
    private readonly bool _savedCps = WasmPredicateCompiler.CpsMode;

    public void Dispose() => WasmPredicateCompiler.CpsMode = _savedCps;

    private const string Corpus = """
        e(1). e(2).
        f(a). f(b).
        ok(1, a).
        q(X-Y) :- e(X), f(Y), ok(X, Y).
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailIntoAnEvictedModulesChoicePointGoesToTheHost(bool cps)
    {
        WasmPredicateCompiler.CpsMode = cps;
        var (e, members, world) = TieredEngine.BuildWithWorld(Corpus);
        for (int i = 0; i < 3; i++) Assert.True(e.Query("findall(P, q(P), L).").Success);
        int efid = -1;
        foreach (var m in members)
        {
            var (aid, ar) = FunctorTable.Lookup(m.Predicate.FunctorId);
            if (ar == 1 && (AtomTable.GetById(aid)?.Name ?? "").EndsWith("e")) efid = m.Predicate.FunctorId;
        }
        // ANTI-VACUITY: e/1 is on the tier, and the eviction takes it off.
        Assert.True(efid >= 0, "e/1 was not promoted");

        var answers = new List<string>();
        var run = Task.Run(() =>
        {
            foreach (var s in e.QueryAll("q(P)."))
            {
                answers.Add(s["P"]!.ToString()!);
                if (answers.Count == 1) Assert.Contains(efid, world.Evict(new[] { efid }));
            }
        });
        // A bound, not a measure: the miss used to loop for good.
        await run.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(new[] { "-(1, a)" }, answers);
    }
}
