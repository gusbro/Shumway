using System.Diagnostics.Tracing;

namespace Shumway.Tests.Embedding;

#if !NETFRAMEWORK   // .NET Framework has no event source for its JIT: the planner's own counts stand alone there
/// <summary>The optimization tier of every method the runtime JIT
/// reports, by name, from its MethodLoadVerbose events; and the methods
/// it stopped inlining into for too many locals, from its inlining
/// events.</summary>
internal sealed class JitTiers : EventListener
{
    private readonly Dictionary<string, string> _tiers = new();
    private readonly Dictionary<string, int> _names = new();
    private readonly Dictionary<string, int> _tooManyLocals = new();
    public int Events;
    public string Seen { get { lock (_names) return string.Join(", ", _names.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key}x{kv.Value}")); } }
    public string? FirstPayload;
    public IReadOnlyDictionary<string, string> Tiers { get { lock (_tiers) return new Dictionary<string, string>(_tiers); } }

    /// <summary>The runtime delivers its events on a thread of its own, a
    /// little after the JIT: waits for a method named with the prefix to
    /// show up.</summary>
    public IReadOnlyDictionary<string, string> WaitFor(string prefix)
    {
        for (int i = 0; i < 100; i++)
        {
            var t = Tiers;
            if (t.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))) return t;
            Thread.Sleep(50);
        }
        return Tiers;
    }

    public IReadOnlyDictionary<string, string> RegionTiers() => WaitFor("ShumwayIlRegion_");

    /// <summary>The inlines the JIT declined for too many locals into the
    /// region methods of <paramref name="rootFids"/>, once their load
    /// events are in (the inlining events precede the load).</summary>
    public (int Declines, string Detail) TooManyLocals(IEnumerable<int> rootFids)
    {
        var names = rootFids.Select(f => $"ShumwayIlRegion_{f}_").ToList();
        for (int i = 0; i < 100; i++)
        {
            var t = Tiers;
            if (names.All(n => t.Keys.Any(k => k.StartsWith(n, StringComparison.Ordinal)))) break;
            Thread.Sleep(50);
        }
        lock (_tooManyLocals)
        {
            var mine = _tooManyLocals.Where(kv => names.Any(n => kv.Key.StartsWith(n, StringComparison.Ordinal))).ToList();
            return (mine.Sum(kv => kv.Value), string.Join(", ", mine.Select(kv => $"{kv.Key} x{kv.Value}")));
        }
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)(0x10 | 0x1000));   // JIT, JitTracing
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        Interlocked.Increment(ref Events);
        lock (_names) _names[e.EventName ?? $"#{e.EventId}"] = _names.TryGetValue(e.EventName ?? $"#{e.EventId}", out int c) ? c + 1 : 1;
        if (e.EventName is null || e.PayloadNames is null || e.Payload is null) return;
        if (e.EventName.StartsWith("MethodJitInliningFailed", StringComparison.Ordinal))
        {
            int root = e.PayloadNames.IndexOf("MethodBeingCompiledName"), reason = e.PayloadNames.IndexOf("FailReason");
            if (root < 0 || reason < 0 || e.Payload[reason] as string != "too many locals") return;
            string rootName = (string)e.Payload[root]!;
            if (!rootName.StartsWith("ShumwayIlRegion_", StringComparison.Ordinal)) return;
            lock (_tooManyLocals) _tooManyLocals[rootName] = _tooManyLocals.TryGetValue(rootName, out int n) ? n + 1 : 1;
            return;
        }
        if (!e.EventName.StartsWith("MethodLoadVerbose", StringComparison.Ordinal)) return;
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
