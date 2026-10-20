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
    /// inlining into a method once the count reaches
    /// <see cref="IlJitLimits.InlineLocals"/>, and from there on every
    /// helper of the method is a call. The target is the cap less the
    /// estimate's slack: measured on Blint, 1,000 runs 5% ahead of 1,100
    /// and 4% ahead of 900. A program whose members declare few locals and
    /// call few helpers keeps its regions whole: boyer's largest comes to
    /// 1,041 and stays, being under the floor of references.</summary>
    public static int LocalsTarget { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("SHUMWAY_REGION_LOCALS"), out var s) && s >= 0
            ? s : DefaultLocalsTarget;

    public const int DefaultLocalsTarget = 1000;

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
    public const int LocalsFloorRefs = 4000;

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
        void Walk(int start, int end)
        {
            for (int pc = start; pc < end;)
            {
                var op = (Opcode)code[pc];
                int size = op == Opcode.Meta ? 6 : OpcodeTable.Get((byte)op).Size;
                if (size <= 0) return;
                sum16 += LocalsWeight16(op);
                bytes += size;
                pc += size;
            }
        }
        if (clauseRanges is null) Walk(0, code.Length);
        else foreach (var (s, e) in clauseRanges) Walk(s, e);
        return (int)Math.Max((sum16 + 8) / 16, bytes * 4 / 16);
    }

    /// <summary>JIT locals per opcode in sixteenths, fitted; the opcodes not
    /// listed fitted at zero.</summary>
    private static int LocalsWeight16(Opcode op) => op switch
    {
        Opcode.AIntBin => 1002,
        Opcode.AIntCmp => 671,
        Opcode.AEvalIs => 418,
        Opcode.AEvalPush => 351,
        Opcode.AllocateGetLevel => 285,
        Opcode.PutInteger => 278,
        Opcode.GetList => 264,
        Opcode.GetAtom => 242,
        Opcode.DeallocateProceed => 194,
        Opcode.GetInteger => 193,
        Opcode.UnifyNil => 176,
        Opcode.UnifyInteger => 158,
        Opcode.UnifyValueX => 147,
        Opcode.NeckCut => 147,
        Opcode.GetStructure => 142,
        Opcode.PutValueX => 126,
        Opcode.UnifyVariableX => 125,
        Opcode.UnifyValueY => 114,
        Opcode.UnifyAtom => 110,
        Opcode.PutVariableX => 104,
        Opcode.Call => 94,
        Opcode.UnifyVariableY => 83,
        Opcode.CallBuiltin => 82,
        Opcode.PutNil => 81,
        Opcode.AEvalBin => 73,
        Opcode.AEvalUn => 62,
        Opcode.PutValueY => 53,
        Opcode.UnifyVoid => 40,
        Opcode.Deallocate => 30,
        Opcode.GetVariableY => 30,
        Opcode.PutVariableY => 26,
        Opcode.PutBlit => 13,
        Opcode.UnifyList => 10,
        Opcode.UnifyStructure => 9,
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
        int Instructions, int Locals, int Attempt, bool Persisted, int Inlines)
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
