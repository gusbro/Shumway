using System.Linq;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>The shared conformance corpus (wasm-conformance.pl) run under
/// three tier configurations, and required to agree. none keeps every
/// predicate on Tier-0; all promotes each on its first call; jit leaves the
/// rare ones on Tier-0 and promotes the hot ones mid-run. A case that holds
/// on Tier-0 and breaks promoted is a tier bug -- the tabling false-success
/// and the carried-cut mispruning both had exactly that shape, and both
/// would surface here.
///
/// <para>The corpus is a plain Prolog file, so the same cases run in the
/// browser through the #wasmtests hook: this is the desktop half, in the
/// gate.</para></summary>
public sealed class WasmConformanceTests(ITestOutputHelper o)
{
    private static string Corpus()
    {
        string path = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "wasm-conformance.pl");
        return System.IO.File.ReadAllText(path);
    }

    /// <summary>Each case as (name, goal-text). Pulled once from a Tier-0
    /// engine, NOT from the engine under test: a case is run as its own
    /// top-level query, the way a user asks it and the way that agrees
    /// across configs (wrapping it in once(G) with G fetched from wc_case
    /// routed it through a meta-call that diverged under full promotion). And
    /// the writeq that extracts the text is kept off the measured engine --
    /// interleaving it with the runs left state that broke the next case.
    /// </summary>
    private static List<(string Name, string Goal)> Cases()
    {
        var src = new PrologEngine();
        src.ConsultString(Corpus());
        var names = src.Query(
            "findall(N, wc_case(N, _), L), with_output_to(atom(A), writeq(L)).");
        string list = names.Bindings["A"].ToString()!.Trim('[', ']');
        var result = new List<(string, string)>();
        foreach (var name in list.Split(',', System.StringSplitOptions.TrimEntries))
        {
            var g = src.Query(
                $"wc_case({name}, G), with_output_to(atom(A), writeq(G)).");
            result.Add((name, g.Bindings["A"].ToString()!));
        }
        return result;
    }

    /// <summary>The failing case names, or "" for a clean run -- the goal
    /// texts run on the engine, nothing else.</summary>
    private static string Run(PrologEngine e, List<(string Name, string Goal)> cases)
    {
        var failed = new List<string>();
        foreach (var (name, goal) in cases)
        {
            var r = e.Query($"catch(({goal}), _E, fail).");
            if (!r.Success) failed.Add(name);
        }
        return string.Join(",", failed);
    }

    [Fact]
    public void NoneJitAllAgreeAndAllPass()
    {
        string corpus = Corpus();
        var cases = Cases();
        int total = cases.Count;
        Assert.True(total >= 30, $"only {total} cases: the corpus did not load in full");

        // none: Tier-0, no wasm store attached.
        var none = new PrologEngine();
        none.ConsultString(corpus);
        var noneFailed = Run(none, cases);
        o.WriteLine($"none: {total} cases, failed=[{noneFailed}]");

        // jit: promote the hot predicates mid-run, leave the rare ones behind.
        var (jit, jitMembers, _) = TieredEngine.BuildWithWorld(corpus, wasmThreshold: 8);
        var jitFailed = Run(jit, cases);
        o.WriteLine($"jit : {jitMembers.Count} promoted, failed=[{jitFailed}]");

        // all: every predicate on the tier from its first call.
        var (all, allMembers, _) = TieredEngine.BuildWithWorld(corpus, wasmThreshold: 1);
        var allFailed = Run(all, cases);
        o.WriteLine($"all : {allMembers.Count} promoted, failed=[{allFailed}]");

        // Anti-vacuity: "all" must actually have put predicates on the tier,
        // or this is three Tier-0 runs wearing different names.
        Assert.True(allMembers.Count > 5,
            $"only {allMembers.Count} predicates promoted: the tier was not exercised");

        // Tier-0 must pass everything: the corpus itself is correct.
        Assert.Equal("", noneFailed);

        // The tier answers what Tier-0 answers, case for case. The list is
        // EMPTY on purpose: a divergence here is a tier bug, and the two it
        // caught (the meta-called cut barrier read after the arguments had
        // overwritten X1, and get_attr/3's form spending the local the
        // meta-call was keeping the goal's heap base in) both presented as a
        // single name on this line.
        foreach (var (config, failed) in new[] { ("jit", jitFailed), ("all", allFailed) })
            Assert.Equal("", $"{failed}".Length == 0 ? "" : $"{config}: {failed}");
    }
}
