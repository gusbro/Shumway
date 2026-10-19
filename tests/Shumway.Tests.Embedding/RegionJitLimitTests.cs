using System.Diagnostics.Tracing;
using System.Text;
using Shumway.Compiler.Il;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A region method stays inside the sizes past which the JIT stops
/// optimizing (<see cref="IlJitLimits"/>): the planner keeps the members
/// under the local-reference target, and a region the estimate let past a
/// limit is emitted again under a smaller plan. The runtime's JIT events say
/// what tier each region method got.</summary>
public sealed class RegionJitLimitTests
{
    // A program whose local closure, absorbed whole, is a region far past the
    // JIT's limit: a chain of predicates, each with clauses that build
    // structures, test arithmetic and call the next.
    private static string Program(int preds)
    {
        var sb = new StringBuilder();
        sb.AppendLine(":- public run/1.");
        sb.AppendLine("run(N) :- p1(N, f(N, g(N)), R), R = [_|_].");
        for (int i = 1; i <= preds; i++)
        {
            string me = $"p{i}", next = i < preds ? $"p{i + 1}" : "stop";
            sb.AppendLine($"{me}(0, _, []) :- !.");
            sb.AppendLine($"{me}(N, f(A, g(B)), [A-B, [A, B|N]|T]) :- N > 0, N1 is N - 1, M is N1 * 2 + 1, {next}(N1, f(B, g(M)), T).");
            sb.AppendLine($"{me}(N, h(A), [A, h(A, N)|T]) :- N > 0, N1 is N - 1, M is N1 - A, {next}(N1, f(M, g(A)), T).");
            sb.AppendLine($"{me}(N, k([A|B]), [A, B|T]) :- N > 0, N1 is N - 1, {next}(N1, f(A, g(B)), T).");
            sb.AppendLine($"{me}(N, X, [X, X|T]) :- N > 0, N1 is N - 1, {next}(N1, X, T).");
        }
        sb.AppendLine("stop(_, _, []).");
        return sb.ToString();
    }

    private static PrologEngine Engine(string program)
    {
        IlRegionBuilder.DefaultBudgetBytes = 1 << 16;   // the byte budget out of the way: the planner is under test
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.IlPromotion.BackgroundCompilation = false;
        e.ConsultString(program);
        return e;
    }

#if !NETFRAMEWORK   // .NET Framework has no event source for its JIT: the planner's own counts stand alone there
    /// <summary>The optimization tier of every method the runtime JIT
    /// reports, by name, from its MethodLoadVerbose events.</summary>
    private sealed class JitTiers : EventListener
    {
        private readonly Dictionary<string, string> _tiers = new();
        private readonly Dictionary<string, int> _names = new();
        public int Events;
        public string Seen { get { lock (_names) return string.Join(", ", _names.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key}x{kv.Value}")); } }
        public string? FirstPayload;
        public IReadOnlyDictionary<string, string> Tiers { get { lock (_tiers) return new Dictionary<string, string>(_tiers); } }

        /// <summary>The runtime delivers its events on a thread of its own, a
        /// little after the JIT: waits for a region method to show up.</summary>
        public IReadOnlyDictionary<string, string> RegionTiers()
        {
            for (int i = 0; i < 100; i++)
            {
                var t = Tiers;
                if (t.Keys.Any(k => k.StartsWith("ShumwayIlRegion_", StringComparison.Ordinal))) return t;
                Thread.Sleep(50);
            }
            return Tiers;
        }

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x10);   // JIT
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            Interlocked.Increment(ref Events);
            lock (_names) _names[e.EventName ?? $"#{e.EventId}"] = _names.TryGetValue(e.EventName ?? $"#{e.EventId}", out int c) ? c + 1 : 1;
            if (e.EventName is null || !e.EventName.StartsWith("MethodLoadVerbose", StringComparison.Ordinal)) return;
            if (e.PayloadNames is null || e.Payload is null) return;
            FirstPayload ??= string.Join("|", e.PayloadNames);
            // The tier rides in MethodFlags, bits 7 and up: 1 MinOptJitted,
            // 2 Optimized, 3 QuickJitted, 4 OptimizedTier1, ...
            int name = e.PayloadNames.IndexOf("MethodName"), flags = e.PayloadNames.IndexOf("MethodFlags");
            if (name < 0 || flags < 0) return;
            int tier = (Convert.ToInt32(e.Payload[flags], System.Globalization.CultureInfo.InvariantCulture) >> 7) & 0xF;
            string tierName = tier switch { 1 => "MinOptJitted", 2 => "Optimized", 3 => "QuickJitted", 4 => "OptimizedTier1", _ => $"tier{tier}" };
            lock (_tiers) _tiers[(string)e.Payload[name]!] = tierName;
        }
    }
#endif

    /// <summary>The regions of this program's predicates, the last emission
    /// of each root: the log is the process's, other classes compile regions
    /// on their own threads meanwhile (under the byte budget raised here), and
    /// a first plan past a limit is logged before it is emitted again.</summary>
    private static IReadOnlyList<IlRegionCost.Emission> RegionsOf(PrologEngine e, string goal)
    {
        IlRegionCost.ResetDiagnostics();
        Assert.True(e.Query(goal).Success);
        var last = new Dictionary<int, IlRegionCost.Emission>();
        foreach (var r in IlRegionCost.Emissions)
        {
            var (atomId, _) = Shumway.Core.FunctorTable.Lookup(r.RootFid);
            string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
            name = name[(name.LastIndexOf('$') + 1)..];
            if (name is "run" or "urun" || (name.Length > 1 && name[0] is 'p' or 'u' && name[1..].All(char.IsDigit)))
                last[r.RootFid] = r;
        }
        return last.Values.ToList();
    }

    [Fact]
    public void EveryRegionStaysUnderTheJitLimits()
    {
        int target = IlRegionCost.Target, budget = IlRegionBuilder.DefaultBudgetBytes;
        try
        {
            IlRegionCost.Target = 7200;
#if !NETFRAMEWORK
            using var tiers = new JitTiers();
#endif
            var e = Engine(Program(40));
            var regions = RegionsOf(e, "run(30).");
            Assert.True(regions.Count >= 1, "no region compiled");
            Assert.Contains(regions, r => r.Members >= 2 && r.LocalRefs >= 2000);   // a real region, not a leaf
            foreach (var r in regions)
            {
                Assert.True(r.LocalRefs <= IlJitLimits.LocalRefs, $"root {r.RootFid}: {r.LocalRefs} local refs");
                Assert.True(r.IlBytes <= IlJitLimits.IlBytes, $"root {r.RootFid}: {r.IlBytes} IL bytes");
                Assert.True(r.Instructions <= IlJitLimits.Instructions, $"root {r.RootFid}: {r.Instructions} instructions");
            }
            Assert.True(e.Query("run(30).").Success);
#if !NETFRAMEWORK
            // The JIT agrees: no region method compiled without optimization.
            var reported = tiers.RegionTiers();
            var minOpts = reported.Where(kv => kv.Key.StartsWith("ShumwayIlRegion_", StringComparison.Ordinal)
                                            && kv.Value.Contains("MinOpt", StringComparison.Ordinal)).ToList();
            Assert.True(reported.Keys.Any(k => k.StartsWith("ShumwayIlRegion_", StringComparison.Ordinal)),
                $"the JIT reported no region method ({tiers.Events} events [{tiers.Seen}] payload [{tiers.FirstPayload}], {reported.Count} methods: "
                + string.Join(", ", reported.Keys.Where(k => k.Contains("Shumway")).Take(5)) + ")");
            Assert.Empty(minOpts);
#endif
        }
        finally
        {
            IlRegionCost.Target = target;
            IlRegionBuilder.DefaultBudgetBytes = budget;
        }
    }

    [Fact]
    public void WithoutTheTargetTheSameProgramGivesTheJitARegionItWillNotOptimize()
    {
        int target = IlRegionCost.Target, budget = IlRegionBuilder.DefaultBudgetBytes;
        try
        {
            IlRegionCost.Target = 0;
#if !NETFRAMEWORK
            using var tiers = new JitTiers();
#endif
            var e = Engine(Program(40));
            var regions = RegionsOf(e, "run(30).");
            Assert.True(regions.Any(r => r.LocalRefs > IlJitLimits.LocalRefs),
                "largest region: " + regions.Max(r => r.LocalRefs) + " local refs, members " + regions.Max(r => r.Members));
            Assert.True(e.Query("run(30).").Success);
#if !NETFRAMEWORK
            var reported = tiers.RegionTiers();
            Assert.Contains(reported, kv => kv.Key.StartsWith("ShumwayIlRegion_", StringComparison.Ordinal)
                                            && kv.Value.Contains("MinOpt", StringComparison.Ordinal));
#endif
        }
        finally
        {
            IlRegionCost.Target = target;
            IlRegionBuilder.DefaultBudgetBytes = budget;
        }
    }

    [Fact]
    public void ARegionTheEstimateLetPastTheLimitIsEmittedAgainUnderASmallerPlan()
    {
        // A fresh set of predicates: nothing measured, every member estimated.
        int target = IlRegionCost.Target, budget = IlRegionBuilder.DefaultBudgetBytes;
        try
        {
            IlRegionCost.Target = 7200;
            var e = Engine(Program(40).Replace("p", "u").Replace("stop", "ustop").Replace("run", "urun"));
            var regions = RegionsOf(e, "urun(30).");
            foreach (var r in regions)
                Assert.True(r.LocalRefs <= IlJitLimits.LocalRefs, $"root {r.RootFid}: {r.LocalRefs} local refs after attempt {r.Attempt}");
        }
        finally
        {
            IlRegionCost.Target = target;
            IlRegionBuilder.DefaultBudgetBytes = budget;
        }
    }

    [Fact]
    public void TheAnswersDoNotDependOnThePlan()
    {
        int target = IlRegionCost.Target, budget = IlRegionBuilder.DefaultBudgetBytes;
        try
        {
            var expected = new List<string>();
            foreach (int t in new[] { 0, 7200, 3000 })
            {
                IlRegionCost.Target = t;
                var e = Engine(Program(12));
                var r = e.Query("run(7), R = done.");
                Assert.True(r.Success);
                var q = e.Query("p1(5, f(1, g(2)), L).");
                Assert.True(q.Success);
                expected.Add(q["L"]!.ToString()!);
            }
            Assert.Equal(expected[0], expected[1]);
            Assert.Equal(expected[0], expected[2]);
        }
        finally
        {
            IlRegionCost.Target = target;
            IlRegionBuilder.DefaultBudgetBytes = budget;
        }
    }
}
