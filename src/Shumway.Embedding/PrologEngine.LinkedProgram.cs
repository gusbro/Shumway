using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Embedding;

public sealed partial class PrologEngine
{
    /// <summary>What the host keeps of the program one activation runs: its address
    /// map, the debugger's tables derived from it (ADR-035), the query it was linked
    /// for, and the breakpoint record of the persistent buffer it runs on. A query
    /// nested in a running one links a program of its own; the host's current program
    /// follows the activation that runs (<see cref="EnterRun"/>).</summary>
    internal sealed class LinkedProgram
    {
        public LinkedProgram(BreakpointRecord breakpoints) => Breakpoints = breakpoints;

        // One per query setup: the empty tables are shared, never mutated
        // (RebuildDebugTables builds new ones).
        private static readonly HashSet<int> NoSites = new();
        private static readonly List<(int, int, int, int)> NoClauseLines = new();

        public IReadOnlyDictionary<int, CompiledPredicate>? Map;
        public Activation? Engine;
        public string? QueryText;

        /// <summary>The predicates' entry addresses, sorted; built on first use.</summary>
        public int[]? SortedEntries;

        // Every stop site in the program, by address, sorted, and the source sites
        // they stop at; empty unless something was compiled debuggable.
        public HashSet<int> CompiledSites = NoSites;
        public int[] StopPcs = Array.Empty<int>();
        public int[] StopSiteIds = Array.Empty<int>();

        // Every debuggable clause by address, sorted, with its frame map; and the
        // source span of each: where its head is written, and the first and last
        // lines it can be stopped at.
        public int[] ClauseStarts = Array.Empty<int>();
        public DebugClauseFrame[] ClauseFrames = Array.Empty<DebugClauseFrame>();
        public List<(int FileId, int HeadLine, int FirstLine, int LastLine)> ClauseLines = NoClauseLines;

        // The last predicate range resolved, a one-entry memo: ports ask about the
        // same few addresses in a loop. Engine-thread only, like every port-path
        // structure (func-evals hijack the same thread).
        public int FidMemoLo = int.MaxValue;
        public int FidMemoHi = int.MaxValue;
        public int FidMemoFid;

        public BreakpointRecord Breakpoints;

        /// <summary>Predicates linked into the activation's code while it runs (a consult
        /// or a library load in the query). Setup builds the map per query, a layer over
        /// the persistent base, so they go into it.</summary>
        public void AddLinked(IReadOnlyDictionary<int, CompiledPredicate> preds)
        {
            if (Map is not LayeredIntMap<CompiledPredicate> map) return;
            foreach (var (addr, pred) in preds) map[addr] = pred;
            SortedEntries = null;
            FidMemoLo = FidMemoHi = int.MaxValue;
        }
    }

    /// <summary>The Break bytes patched into one code array and the bytes they replaced,
    /// shared by every activation that runs on the array. A rebuilt buffer is a new array
    /// with a record of its own; an array an activation outgrows is copied, and the copy
    /// gets a copy of the record (<see cref="OnProgramMoved"/>), so two arrays never share
    /// one.</summary>
    internal sealed class BreakpointRecord
    {
        public BreakpointRecord(byte[]? program) => Program = program;

        public readonly Dictionary<int, byte> Patches = new();
        public readonly byte[]? Program;
        public int SitesVersion;

        /// <summary>The debuggable predicates linked into the array while queries ran, by
        /// entry address. Each activation on the array linked its own; they share the
        /// bytes, so a sync patches all of them.</summary>
        public List<KeyValuePair<int, CompiledPredicate>>? LiveCode;
    }

    private LinkedProgram _cur = new(new BreakpointRecord(null));

    /// <summary>The program whose debugger tables were built last: the next setup
    /// starts from them, and rebuilds them only when the layout changed.</summary>
    private LinkedProgram? _debugTablesOwner;

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], BreakpointRecord>
        _breakpointRecords = new();

    private Action<Activation, int>? _onProgramMoved;

    /// <summary>The record of <paramref name="program"/>'s Break bytes; a new, empty one for
    /// an array not seen before (a rebuilt buffer).</summary>
    private BreakpointRecord RecordFor(byte[] program)
        => _breakpointRecords.GetValue(program, static a => new BreakpointRecord(a));

    /// <summary>An activation's code moved to a larger array holding a copy of the old one's
    /// first <paramref name="copied"/> bytes. The new array gets a copy of the old record,
    /// the patches and the live code inside what was copied, before anything runs on it;
    /// an activation still on the old array keeps the old record.</summary>
    private void OnProgramMoved(Activation engine, int copied)
    {
        byte[] program = engine.CurrentProgram!;
        var old = (engine.HostProgram as LinkedProgram)?.Breakpoints;
        var moved = new BreakpointRecord(program) { SitesVersion = old?.SitesVersion ?? 0 };
        if (old is not null)
        {
            foreach (var (pc, original) in old.Patches)
                if (pc < copied) moved.Patches[pc] = original;
            if (old.LiveCode is { } live)
                foreach (var entry in live)
                    if (entry.Key < copied) (moved.LiveCode ??= new()).Add(entry);
        }
        _breakpointRecords.Remove(program);
        _breakpointRecords.Add(program, moved);
        if (engine.HostProgram is LinkedProgram p) p.Breakpoints = moved;
        if (engine.BreakpointOriginals is not null) engine.BreakpointOriginals = moved.Patches;
    }

    /// <summary>Bumped whenever the armed breakpoint sites change, so a program whose
    /// buffer was last synced before knows it is behind.</summary>
    private int _breakpointSitesVersion;

    /// <summary>The activations whose run is in progress on this thread's stack, the
    /// innermost last: a query nested in another runs inside one of the outer one's
    /// builtins.</summary>
    private readonly List<Activation> _running = new();

    /// <summary>An activation starts or resumes running: its program is the current
    /// one until it stops (<see cref="LeaveRun"/>).</summary>
    private void EnterRun(Activation engine)
    {
        _running.Add(engine);
        if (engine.HostProgram is LinkedProgram p && !ReferenceEquals(p, _cur)) SwitchTo(p);
    }

    /// <summary>The run <see cref="EnterRun"/> began has stopped: the activation it
    /// ran inside of, if any, is current again. With none, the program last linked
    /// stays current, as it always did between queries.</summary>
    private void LeaveRun()
    {
        _running.RemoveAt(_running.Count - 1);
        if (_running.Count > 0 && _running[^1].HostProgram is LinkedProgram p
            && !ReferenceEquals(p, _cur))
            SwitchTo(p);
    }

    /// <summary>Makes <paramref name="p"/> the current program and its activation the
    /// live one. Its array's breakpoints are brought up to date when the armed sites
    /// changed since they were synced (while another program was current), unless a
    /// debug evaluation is running (its setup leaves them alone, ADR-035 D5); and the
    /// activation is armed if the debugger armed in the meantime. A sync patches all the
    /// code in the array, so a sync for one program on it is right for the others.</summary>
    private void SwitchTo(LinkedProgram p)
    {
        _cur = p;
        _lastQueryEngine = p.Engine;
        var record = p.Breakpoints;
        if (_debugEvalDepth == 0 && record.SitesVersion != _breakpointSitesVersion
            && p.Engine?.CurrentProgram is { } live && ReferenceEquals(live, record.Program))
            SyncBreakpoints(p, live);
        // ADR-035 D5+: the arm-publish pair, as at query setup. The activation applies
        // the arm at its next goal, on its own thread.
        System.Threading.Interlocked.MemoryBarrier();
        if (DebugFullyArmed && DebugSession is { } session && p.Engine is { Debug: null } a)
        {
            bool lco = DebugLcoWhenArmed;
            a.RequestDebugArm(x =>
            {
                x.Debug = session;
                x.TrailEverything = true;
                x.LastCallOptimisation = lco;
            });
        }
    }
}
