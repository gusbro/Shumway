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

    /// <summary>The shared tail of a region with that many resume entries and
    /// ladder entries.</summary>
    public static int TailCost(int resumeCursors, int ladderEntries)
        => TailBase + PerResumeCursor * resumeCursors + PerLadderEntry * ladderEntries;

    private sealed class Box { public int LocalRefs; }
    private static readonly ConditionalWeakTable<CompiledPredicate, Box> Measured = new();

    /// <summary>Records what a member's block cost; the first measure stays,
    /// so a plan made before an emission and one made after it agree.</summary>
    public static void Record(CompiledPredicate member, int localRefs)
    {
        if (localRefs <= 0) return;
        var box = Measured.GetValue(member, _ => new Box { LocalRefs = -1 });
        if (box.LocalRefs < 0) box.LocalRefs = localRefs;
    }

    public static bool IsMeasured(CompiledPredicate member)
        => Measured.TryGetValue(member, out var box) && box.LocalRefs > 0;

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
        int Instructions, int Locals, int Attempt, bool Persisted);

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
