using Shumway.Core;
using System.Runtime.CompilerServices;
using Shumway.Compiler.Wam;

namespace Shumway.Compiler.Il;

/// <summary>The region planner's cost model, in the measure the JIT applies
/// first (<see cref="IlJitLimits.LocalRefs"/>): what a member's block and the
/// shared tail will cost before they are emitted. A member emitted once in
/// any region is measured, and the measure holds in the next region to within
/// a few percent; an unmeasured member is estimated from its opcodes, with
/// weights fitted on Blint, clpz and the Van Roy suite (median error 21%, a
/// heavy tail where guards inline their callees). The tail is the resume
/// entries and the push ladder, fitted the same way. The runtime re-plans a
/// region the estimate let past the limit; the linker measures every member
/// first (<c>WarmRegionCosts</c>), so its plans and its emissions agree.</summary>
internal static class IlRegionCost
{
    /// <summary>The local references a region is planned to stay under, the
    /// JIT's limit less a margin for the estimate; 0 plans by bytecode bytes
    /// alone, as before the model. <c>SHUMWAY_REGION_LVREFS</c>.</summary>
    public static int Target { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("SHUMWAY_REGION_LVREFS"), out var t) && t >= 0
            ? t : 7200;

    /// <summary>The prologue of a region method (registers into locals).</summary>
    public const int Prologue = 20;
    /// <summary>The tail's fixed part: return and fail handlers, wake dispatch.</summary>
    public const int TailBase = 181;
    /// <summary>Per resume entry (a clause alternative or an index node).</summary>
    public const int PerResumeCursor = 23;
    /// <summary>Per entry of the push ladder, one per arity up to the largest.</summary>
    public const int PerLadderEntry = 5;
    private const int LadderReserve = 8 * PerLadderEntry;

    /// <summary>What the members may add up to under <see cref="Target"/>.</summary>
    public static int PlanBudget => Target - Prologue - TailBase - LadderReserve;

    /// <summary>The locals a region method is planned to stay under, in the
    /// JIT's count: the locals it declares and the temporaries of the
    /// inlines the JIT would make into it (<see cref="Locals"/>); 0 plans by
    /// local references alone. <c>SHUMWAY_REGION_LOCALS</c>. The JIT stops
    /// inlining into a method once its own count reaches nine tenths of
    /// <see cref="IlJitLimits.InlineLocals"/>
    /// (<see cref="IlJitLimits.InliningCutoff"/>), and from there on every
    /// helper of the method is a call. The target is in the planner's
    /// count (<see cref="Locals"/>), which is not the JIT's, and fitted to
    /// the cutoff: measured on Blint, 1,000 runs 5% ahead of 1,100 and 4%
    /// ahead of 900. A program whose members declare few locals and
    /// call few helpers keeps its regions whole: boyer's largest comes to
    /// 1,041 and stays, being under the floor of references.</summary>
    public static int LocalsTarget { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("SHUMWAY_REGION_LOCALS"), out var s) && s >= 0
            ? s : DefaultLocalsTarget;

    /// <summary>The cap the JIT runs with less <see cref="CapSlack"/>: 1,000
    /// at the JIT's own 1,024, and as much more as a host raised it.</summary>
    public static int DefaultLocalsTarget => IlJitLimits.InlineLocals - CapSlack;

    public const int CapSlack = 24;

    /// <summary>Temporaries per inline the table predicts, in sixteenths:
    /// 1.19. The JIT creates 1.4 to 1.8 per inline it makes, and makes 80
    /// to 95% of the ones the table predicts, the rest judged unprofitable
    /// at their site; the regions that reach the cap and the largest that
    /// do not (boyer, 804 predicted inlines and 84 locals) pin the product
    /// between 1.17 and 1.24.</summary>
    public const int TempsPerInline16 = 19;

    /// <summary>What a method's locals come to in the JIT's count: the
    /// arguments, the locals declared and the temporaries of the inlines.</summary>
    public static int Locals(int declared, int inlines) => 2 + declared + (inlines * TempsPerInline16 + 8) / 16;

    /// <summary>The prologue's and the shared tail's part: the arguments,
    /// the locals they declare (17 and up to 4) and the tail's one inline.</summary>
    public const int TailLocals = 25;

    /// <summary>What the members may add up to under <see cref="LocalsTarget"/>.</summary>
    public static int LocalsPlanBudget => LocalsTarget - TailLocals;

    /// <summary>The local references under which the locals target leaves
    /// a region as its references alone plan it. A small region is one hot
    /// path, and a trampoline cut into it costs more than its last sites
    /// run as calls: flatten's largest, 3,493 references and a third past
    /// the cap, loses 5% cut; sendmore's hold a single member each past the
    /// cap, which a cut cannot mend. Blint's and the clpz puzzles' regions,
    /// at the reference target, gain from the cut.</summary>
    public static int LocalsFloorRefs { get; set; } = 4000;

    /// <summary>The shared tail of a region with that many resume entries and
    /// ladder entries.</summary>
    public static int TailCost(int resumeCursors, int ladderEntries)
        => TailBase + PerResumeCursor * resumeCursors + PerLadderEntry * ladderEntries;

    private sealed class Box { public int LocalRefs, Inlines, Locals; }
    private static readonly ConditionalWeakTable<CompiledPredicate, Box> Measured = new();

    /// <summary>Records what a member's block cost; the first measure stays,
    /// so a plan made before an emission and one made after it agree.</summary>
    public static void Record(CompiledPredicate member, int localRefs, int inlines, int locals)
    {
        if (localRefs <= 0) return;
        var box = Measured.GetValue(member, _ => new Box { LocalRefs = -1 });
        if (box.LocalRefs < 0) { box.LocalRefs = localRefs; box.Inlines = inlines; box.Locals = locals; }
    }

    public static bool IsMeasured(CompiledPredicate member)
        => Measured.TryGetValue(member, out var box) && box.LocalRefs > 0;

    /// <summary>What the member would add to a region in the JIT's locals:
    /// the locals its block declares and the temporaries of its inlines,
    /// measured; or estimated from its opcodes (<see cref="EstimateLocals"/>).</summary>
    public static int MemberLocals(CompiledPredicate member, IReadOnlyList<(int Start, int End)>? clauseRanges)
        => Measured.TryGetValue(member, out var box) && box.LocalRefs > 0
            ? box.Locals + (box.Inlines * TempsPerInline16 + 8) / 16
            : EstimateLocals(member.BytecodeUnfused, clauseRanges);

    /// <summary>The opcode model of a member's JIT locals, fitted like
    /// <see cref="Estimate"/> (median error 20%, against 36% for a ratio to
    /// the local references: the arithmetic opcodes inline far more per
    /// reference than the rest). Floored at a per-byte rate.</summary>
    public static int EstimateLocals(byte[] code, IReadOnlyList<(int Start, int End)>? clauseRanges)
    {
        long sum16 = 0, bytes = 0;
        var matched = MatcherSites(code);
        void Walk(int start, int end)
        {
            for (int pc = start; pc < end;)
            {
                var op = (Opcode)code[pc];
                int size = op == Opcode.Meta ? 6 : OpcodeTable.Get((byte)op).Size;
                if (size <= 0) return;
                sum16 += LocalsWeight16(op);
                bytes += size;
                // ADR-063: a stream the matcher stands in for is not emitted.
                pc = op == Opcode.UnifyBlit && matched.Contains(pc) ? BytecodeIO.ReadInt32(code, pc + 9) : pc + size;
            }
        }
        if (clauseRanges is null) Walk(0, code.Length);
        else foreach (var (s, e) in clauseRanges) Walk(s, e);
        return (int)Math.Max((sum16 + 8) / 16, bytes * 4 / 16);
    }

    /// <summary>ADR-063: the <c>unify_blit</c> sites of a code whose streams
    /// the IL tier leaves to the matcher (<c>Activation.UnifyBlit</c>). None
    /// at first: a stream's code, inlined by the JIT, is faster than the
    /// matcher's pass. A region measured past the cap on locals sends the
    /// largest streams it emitted there (<see cref="CompactStreams"/>), and
    /// they stay there for the code's later emissions. The set is replaced,
    /// never changed: a reader holds a consistent one.</summary>
    public static HashSet<int> MatcherSites(byte[] code)
    {
        if (MatchEveryPart) return AllSites.GetValue(code, EverySite);
        return Sites.TryGetValue(code, out var s) ? s : NoSites;
    }

    private static readonly ConditionalWeakTable<byte[], HashSet<int>> Sites = new();
    private static readonly ConditionalWeakTable<byte[], HashSet<int>> AllSites = new();
    private static readonly HashSet<int> NoSites = new();

    /// <summary>Every part to the matcher, whatever the region: the
    /// measurement's switch, <c>SHUMWAY_IMAGE_MATCH_ALL=1</c>.</summary>
    public static bool MatchEveryPart { get; set; } =
        Environment.GetEnvironmentVariable("SHUMWAY_IMAGE_MATCH_ALL") == "1";

    /// <summary>The locals a part's stream must cost for a compaction to
    /// take it; a smaller stream stays. <c>SHUMWAY_IMAGE_MATCH_LOCALS</c>.</summary>
    public static int MatcherStreamMin { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("SHUMWAY_IMAGE_MATCH_LOCALS"), out var m) && m >= 0
            ? m : 32;

    /// <summary>Streams sent to the matcher by compactions, over the process.</summary>
    public static int Compactions => _compactions;
    private static int _compactions;

    /// <summary>A region measured past a limit of the JIT: of the streams it
    /// emitted (<paramref name="streams"/>, a code and the site in it), those
    /// at or past <see cref="MatcherStreamMin"/> whose argument every local
    /// caller passes fresh (<see cref="IlColdArguments"/>) go to the matcher.
    /// True when any did. Every one of them, not an overshoot's worth: the
    /// measure that let the region past the limit is not one to meter them
    /// by. A stream of an argument some caller may pass bound stays: read in
    /// a loop, the stream's code beats the matcher's pass.</summary>
    public static bool CompactStreams(IReadOnlyList<(byte[] Code, int Pc)> streams,
        IReadOnlyDictionary<int, CompiledPredicate>? callers)
    {
        if (streams is null || streams.Count == 0 || callers is null) return false;
        var fresh = IlColdArguments.FreshArguments(callers);
        if (fresh.Count == 0) return false;
        var byCode = new Dictionary<byte[], CompiledPredicate>(ReferenceEqualityComparer.Instance);
        foreach (var p in callers.Values) byCode[p.BytecodeUnfused] = p;
        var parts = new Dictionary<byte[], Dictionary<int, int>>(ReferenceEqualityComparer.Instance);
        long min16 = (long)MatcherStreamMin * 16;
        int taken = 0;
        lock (Sites)
        {
            foreach (var (code, pc) in streams)
            {
                if (MatcherSites(code).Contains(pc) || StreamWeight16(code, pc) < min16) continue;
                if (!byCode.TryGetValue(code, out var pred) || !fresh.TryGetValue(pred.FunctorId, out long mask)) continue;
                if (!parts.TryGetValue(code, out var owners))
                    parts[code] = owners = IlColdArguments.PartArguments(code, pred.Arity);
                if (!owners.TryGetValue(pc, out int arg) || arg < 0 || arg >= 63 || ((mask >> arg) & 1) == 0) continue;
                var next = Sites.TryGetValue(code, out var current) ? new HashSet<int>(current) : new HashSet<int>();
                next.Add(pc);
                Sites.Remove(code);
                Sites.Add(code, next);
                taken++;
            }
        }
        if (taken > 0) Interlocked.Add(ref _compactions, taken);
        return taken > 0;
    }

    /// <summary>The locals of the stream after the <c>unify_blit</c> at
    /// <paramref name="pc"/>, in sixteenths.</summary>
    private static long StreamWeight16(byte[] code, int pc)
    {
        int target = BytecodeIO.ReadInt32(code, pc + 9);
        long w = 0;
        for (int q = pc + OpcodeTable.Get((byte)Opcode.UnifyBlit).Size; q < target;)
        {
            var o = (Opcode)code[q];
            int sz = o == Opcode.Meta ? 6 : OpcodeTable.Get((byte)o).Size;
            if (sz <= 0) break;
            w += LocalsWeight16(o);
            q += sz;
        }
        return w;
    }

    private static HashSet<int> EverySite(byte[] code)
    {
        var sites = new HashSet<int>();
        for (int pc = 0; pc < code.Length;)
        {
            var op = (Opcode)code[pc];
            int size = op == Opcode.Meta ? 6 : OpcodeTable.Get((byte)op).Size;
            if (size <= 0) break;
            if (op == Opcode.UnifyBlit) sites.Add(pc);
            pc += size;
        }
        return sites;
    }

    /// <summary>JIT locals per opcode in sixteenths, fitted on the corpus
    /// with the head streams in their read-only form (ADR-063); the opcodes
    /// not listed fitted at zero. Median error 21%.</summary>
    private static int LocalsWeight16(Opcode op) => op switch
    {
        Opcode.AIntBin => 981,
        Opcode.AIntCmp => 632,
        Opcode.AEvalIs => 391,
        Opcode.AEvalPush => 314,
        Opcode.PutInteger => 284,
        Opcode.AllocateGetLevel => 276,
        Opcode.GetAtom => 236,
        Opcode.GetList => 226,
        Opcode.DeallocateProceed => 211,
        Opcode.UnifyNil => 203,
        Opcode.UnifyInteger => 186,
        Opcode.AEvalBin => 170,
        Opcode.GetInteger => 170,
        Opcode.UnifyValueX => 164,
        Opcode.NeckCut => 145,
        Opcode.UnifyVariableX => 133,
        Opcode.PutValueX => 127,
        Opcode.UnifyVariableY => 121,
        Opcode.PutVariableX => 105,
        Opcode.AEvalUn => 105,
        Opcode.UnifyAtom => 103,
        Opcode.UnifyValueY => 94,
        Opcode.Call => 90,
        Opcode.GetStructure => 84,
        Opcode.PutNil => 63,
        Opcode.UnifyVoid => 63,
        Opcode.CallBuiltin => 51,
        Opcode.PutValueY => 49,
        Opcode.GetVariableY => 40,
        Opcode.PutVariableY => 27,
        Opcode.Deallocate => 26,
        Opcode.UnifyStructure => 17,
        Opcode.Proceed => 13,
        _ => 0,
    };

    /// <summary>What the member would add to a region: its block, measured or
    /// estimated, and the tail's resume entries its clauses bring.</summary>
    public static int MemberCost(CompiledPredicate member,
        IReadOnlyList<(int Start, int End)>? clauseRanges, int resumeCursors)
    {
        int block = Measured.TryGetValue(member, out var box) && box.LocalRefs > 0
            ? box.LocalRefs
            : Estimate(member.BytecodeUnfused, clauseRanges);
        return block + PerResumeCursor * resumeCursors;
    }

    /// <summary>The opcode model: local references per opcode, fitted; the
    /// opcodes not listed fitted at zero. Floored at a per-byte rate, so a
    /// body of unlisted opcodes is not free.</summary>
    public static int Estimate(byte[] code, IReadOnlyList<(int Start, int End)>? clauseRanges)
    {
        long sum = 0, bytes = 0;
        void Walk(int start, int end)
        {
            for (int pc = start; pc < end;)
            {
                var op = (Opcode)code[pc];
                int size = op == Opcode.Meta ? 6 : OpcodeTable.Get((byte)op).Size;
                if (size <= 0) return;
                sum += Weight(op);
                bytes += size;
                pc += size;
            }
        }
        if (clauseRanges is null) Walk(0, code.Length);
        else foreach (var (s, e) in clauseRanges) Walk(s, e);
        return (int)Math.Max(sum, bytes * 12 / 10);
    }

    private static int Weight(Opcode op) => op switch
    {
        Opcode.AllocateGetLevel => 153,
        Opcode.AEvalIs => 148,
        Opcode.DeallocateProceed => 115,
        Opcode.PutList => 88,
        Opcode.Deallocate => 85,
        Opcode.AEvalCmp => 80,
        Opcode.GetList => 73,
        Opcode.Allocate => 47,
        Opcode.UnifyStructure => 45,
        Opcode.GetInteger => 40,
        Opcode.NeckCut => 39,
        Opcode.GetAtom => 36,
        Opcode.PutInteger => 33,
        Opcode.GetStructure => 30,
        Opcode.AIntCmp => 27,
        Opcode.Execute => 21,
        Opcode.PutValueX => 21,
        Opcode.PutValueY => 17,
        Opcode.UnifyVoid => 14,
        Opcode.Proceed => 10,
        Opcode.PutAtom => 9,
        Opcode.AEvalPush => 9,
        Opcode.UnifyList => 2,
        _ => 0,
    };

    /// <summary>One region emission, for diagnostics and tests.</summary>
    public readonly record struct Emission(int RootFid, int Members, int LocalRefs, int IlBytes,
        int Instructions, int Locals, int Attempt, bool Persisted, int Inlines, int Blocks)
    {
        /// <summary>The method's locals in the JIT's count (<see cref="IlRegionCost.Locals"/>).</summary>
        public int JitLocals => IlRegionCost.Locals(Locals, Inlines);
    }

    private static readonly List<Emission> _log = new();
    private static int _replans, _persistedOverflows;

    /// <summary>The region emissions of this process, newest last; at most
    /// the last 1024.</summary>
    public static IReadOnlyList<Emission> Emissions { get { lock (_log) return _log.ToArray(); } }
    /// <summary>Regions the runtime emitted again under a smaller plan.</summary>
    public static int Replans => _replans;
    /// <summary>Persisted regions emitted past a limit (the linker does not
    /// re-plan: its membership was fixed when the roots were selected).</summary>
    public static int PersistedOverflows => _persistedOverflows;

    internal static void Log(Emission e)
    {
        lock (_log)
        {
            if (_log.Count >= 1024) _log.RemoveRange(0, 512);
            _log.Add(e);
        }
    }
    internal static void NoteReplan() => Interlocked.Increment(ref _replans);
    internal static void NotePersistedOverflow() => Interlocked.Increment(ref _persistedOverflows);

    /// <summary>Tests: forget the log and the counters (not the measures).</summary>
    internal static void ResetDiagnostics()
    {
        lock (_log) _log.Clear();
        _replans = 0;
        _persistedOverflows = 0;
    }
}
