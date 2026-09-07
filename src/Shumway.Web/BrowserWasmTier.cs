using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using Shumway.Compiler.Il;
using Shumway.Compiler.Wam;
using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;

namespace Shumway.Web;

/// <summary>The browser's wasm execution world: ONE group module whose bytes
/// are pinned and registered lazily per thread (with threads on, every worker
/// has its own function table; only the memory is shared). A chain pins the
/// engine arrays in place once, fills the pinned mailbox once, and every call
/// is a raw hop through this thread's table -- and with group compilation the
/// in-group calls never even leave the module. All C# here runs
/// MONO-INTERPRETED, which is why per-entry work is hoisted into the chain
/// open/close.</summary>
internal sealed class BrowserWasmWorld : IWasmExecutionWorld
{
    private Build? _current;

    // Timing split for the probe: ticks inside raw calls vs staging
    // (BeginChain + RefreshFromEngine). Process-wide diagnostics.
    internal static long DiagCallTicks, DiagStageTicks;

    /// <summary>One installed group compile: pinned patched bytes, the
    /// per-thread table index, and the maps a chain captures. Old builds stay
    /// referenced by their open chains; their thread-local indices remain
    /// valid (registered functions are never unregistered).</summary>
    private sealed record Build(
        byte[] PinnedModule,
        ThreadLocal<int> Index,
        IReadOnlyDictionary<int, int> EntryCursorByFid,
        IReadOnlyDictionary<int, int> CursorByAddress,
        IReadOnlyDictionary<int, int> EntryAddressByFid,
        int RegisterDemand);

    public void InstallGroup(byte[] module,
        IReadOnlyDictionary<int, int> entryCursorByFid,
        IReadOnlyDictionary<int, int> cursorByAddress,
        IReadOnlyDictionary<int, int> entryAddressByFid,
        int registerDemand)
    {
        byte[] patched = WasmSharedMemory.Patch(module);
        byte[] pinned = GC.AllocateArray<byte>(patched.Length, pinned: true);
        patched.CopyTo(pinned, 0);
        var index = new ThreadLocal<int>(() =>
        {
            int at = (int)(nint)Marshal.UnsafeAddrOfPinnedArrayElement(pinned, 0);
            int i = WebShumwayApp.WasmRegister(at, pinned.Length);
            if (i < 0)
                throw new InvalidOperationException("wasm module did not register");
            return i;
        });
        _current = new Build(pinned, index, entryCursorByFid, cursorByAddress,
                             entryAddressByFid, registerDemand);
    }

    public bool Contains(int functorId)
        => _current?.EntryCursorByFid.ContainsKey(functorId) == true;

    public bool TryResolve(int functorId, int address, out int cursor)
        => TryResolveIn(_current, functorId, address, out cursor);

    private static bool TryResolveIn(Build? b, int functorId, int address, out int cursor)
    {
        cursor = 0;
        if (b is null) return false;
        if (address == 0) return b.EntryCursorByFid.TryGetValue(functorId, out cursor);
        return b.EntryCursorByFid.ContainsKey(functorId)
            && b.CursorByAddress.TryGetValue(address, out cursor);
    }

    public int EntryAddressOf(int functorId)
        => _current!.EntryAddressByFid[functorId];

    public IWasmChainContext BeginChain(Activation engine)
        => new Chain(_current ?? throw new InvalidOperationException("no group installed"),
                     engine);

    private sealed class Chain : IWasmChainContext
    {
        private readonly Build _build;
        private readonly Activation _engine;
        private readonly long[] _mailbox = GC.AllocateArray<long>(WasmAbi.SlotCount, pinned: true);
        private readonly int _mailboxAt;
        private GCHandle _heapPin, _stackPin, _regsPin, _trailPin;
        private Cell[] _heap = null!, _stack = null!, _regs = null!;
        private int[] _trail = null!;
        private bool _engineAuthoritative;

        public Chain(Build build, Activation engine)
        {
            _build = build;
            _engine = engine;
            _mailboxAt = (int)(nint)Marshal.UnsafeAddrOfPinnedArrayElement(_mailbox, 0);
            Stage();
        }

        /// <summary>Pins the engine areas and fills the mailbox with their
        /// real addresses plus the scalars. The arrays are only replaced
        /// (growth, GC) by managed code, and managed code only runs with the
        /// chain synced back to the engine, so the pins are stable for the
        /// life of the staging -- D2.</summary>
        private void Stage()
        {
            long t0 = Stopwatch.GetTimestamp();
            _engine.EnsureWasmRegisters(_build.RegisterDemand);
            var heap = _engine.WasmHeapView;
            var stack = _engine.WasmStackView;
            var regs = _engine.WasmRegistersView;
            var trail = _engine.WasmBindingTrailView;
            RepinIfChanged(ref _heapPin, ref _heap, heap);
            RepinIfChanged(ref _stackPin, ref _stack, stack);
            RepinIfChanged(ref _regsPin, ref _regs, regs);
            if (!ReferenceEquals(_trail, trail))
            {
                if (_trailPin.IsAllocated) _trailPin.Free();
                _trailPin = GCHandle.Alloc(trail, GCHandleType.Pinned);
                _trail = trail;
            }
            var bases = new Activation.WasmMailboxBases(
                (long)_heapPin.AddrOfPinnedObject(),
                (long)_stackPin.AddrOfPinnedObject(),
                (long)_regsPin.AddrOfPinnedObject(),
                (long)_trailPin.AddrOfPinnedObject(),
                HeapLimitCells: heap.Length - 8,
                StackLimitCells: stack.Length - 8,
                TrailLimitEntries: trail.Length - 8,
                FunctorArityBase: BrowserWasmTier.ArityMirrorAddress());
            if (!_engine.TryFillWasmMailbox(_mailbox, bases))
                throw new InvalidOperationException(
                    "a mode-incompatible activation reached the wasm world");
            _engineAuthoritative = false;
            DiagStageTicks += Stopwatch.GetTimestamp() - t0;
        }

        private static void RepinIfChanged(ref GCHandle pin, ref Cell[] cached, Cell[] current)
        {
            if (ReferenceEquals(cached, current)) return;
            if (pin.IsAllocated) pin.Free();
            pin = GCHandle.Alloc(current, GCHandleType.Pinned);
            cached = current;
        }

        public WasmVerdict Call(int cursor)
        {
            long t0 = Stopwatch.GetTimestamp();
            int v = WebShumwayApp.WasmCall(_build.Index.Value, _mailboxAt, cursor);
            DiagCallTicks += Stopwatch.GetTimestamp() - t0;
            return (WasmVerdict)v;
        }

        public bool TryResolve(int functorId, int address, out int cursor)
            => TryResolveIn(_build, functorId, address, out cursor);

        public long ReadSlot(int slot) => _mailbox[slot];

        public void SyncEngine()
        {
            if (_engineAuthoritative) return;
            _engine.SyncFromWasmMailbox(_mailbox);
            _engineAuthoritative = true;
        }

        public void RefreshFromEngine()
        {
            if (!_engineAuthoritative)
                throw new InvalidOperationException("refresh without a preceding sync");
            Stage();
        }

        public void Dispose()
        {
            SyncEngine();
            if (_heapPin.IsAllocated) _heapPin.Free();
            if (_stackPin.IsAllocated) _stackPin.Free();
            if (_regsPin.IsAllocated) _regsPin.Free();
            if (_trailPin.IsAllocated) _trailPin.Free();
        }
    }
}

/// <summary>Boot-time wiring of the wasm tier: the promotion store's
/// <c>Promoter</c> accumulates the promoted set, recompiles the GROUP module
/// on each promotion (cross-member calls become internal jumps), installs
/// the fresh build, and wraps the chain-driving verdict loop. Gated by
/// <see cref="RuntimeCaps.SupportsWasmCodegen"/> -- only Shumway.Web turns
/// the feature switch on.</summary>
internal static class BrowserWasmTier
{
    // The functor-arity mirror the general unifier reads, pinned and
    // process-wide: append-only under the lock, read lock-free by the wasm.
    private static int[] _arityMirror = GC.AllocateArray<int>(4096, pinned: true);
    private static int _aritySynced;
    private static readonly object _arityLock = new();

    internal static long ArityMirrorAddress()
    {
        SyncArityMirror();
        return (long)(nint)Marshal.UnsafeAddrOfPinnedArrayElement(_arityMirror, 0);
    }

    private static void SyncArityMirror()
    {
        int count = FunctorTable.Count;
        if (count <= Volatile.Read(ref _aritySynced)) return;
        lock (_arityLock)
        {
            if (count > _arityMirror.Length)
            {
                int grown = _arityMirror.Length;
                while (grown < count) grown *= 2;
                var next = GC.AllocateArray<int>(grown, pinned: true);
                Array.Copy(_arityMirror, next, _arityMirror.Length);
                _arityMirror = next;
                // The new address is picked up at the NEXT staging; the old
                // pinned array stays valid for any chain in flight.
            }
            for (int fid = _aritySynced; fid < count; fid++)
                _arityMirror[fid] = FunctorTable.TryLookup(fid, out var fe) ? fe.Arity : 0;
            Volatile.Write(ref _aritySynced, count);
        }
    }

    /// <summary>Attaches the wasm promotion store to an engine. No-op when
    /// the capability is off.</summary>
    internal static void Attach(PrologEngine engine, int threshold = 16)
    {
        if (!RuntimeCaps.SupportsWasmCodegen) return;
        var store = engine.IlPromotion;
        var world = new BrowserWasmWorld();
        var members = new List<WasmGroupMember>();
        var env = new EngineWasmCompileEnv();
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = threshold,
            Promoter = (pred, linkedBase) =>
                Promote(store, world, members, env, pred, linkedBase),
        };
    }

    private static PredicateDelegate? Promote(IlPromotionStore store,
        BrowserWasmWorld world, List<WasmGroupMember> members,
        EngineWasmCompileEnv env, CompiledPredicate pred, int linkedBase)
    {
        var candidate = new WasmGroupMember(pred, linkedBase,
            store.FloatPoolProvider?.Invoke(pred.FunctorId));
        members.Add(candidate);
        try
        {
            InstallCurrent(world, members, env);
            return new WasmTierDelegate(pred.FunctorId, world).Invoke;
        }
        catch (WasmCompileException)
        {
            // The candidate poisoned the group: reinstall without it.
            members.Remove(candidate);
            if (members.Count > 0) InstallCurrent(world, members, env);
            return null;
        }
    }

    private static void InstallCurrent(BrowserWasmWorld world,
        List<WasmGroupMember> members, EngineWasmCompileEnv env)
    {
        var entry = WasmPredicateCompiler.CompileGroup(members, env);
        var entryAddr = new Dictionary<int, int>(members.Count);
        foreach (var m in members)
            entryAddr[m.Predicate.FunctorId] = m.Bias;
        world.InstallGroup(entry.Module, entry.EntryCursorByFid,
            entry.CursorByAddress, entryAddr, entry.RegisterDemand);
    }
}

/// <summary>The browser measurement: two engines side by side, one with the
/// wasm tier attached at threshold 1 and one plain Tier-0, over the counter,
/// nrev and tak, correctness cross-checked first. Reached via the page hash
/// <c>#wasmtier</c>.</summary>
internal static partial class WebShumwayApp
{
    private const string TierProbeCorpus = """
        loop(0).
        loop(N) :- N > 0, N1 is N - 1, loop(N1).
        app([], L, L).
        app([H|T], L, [H|R]) :- app(T, L, R).
        nrev([], []).
        nrev([H|T], R) :- nrev(T, RT), app(RT, [H], R).
        range(N, N, [N]) :- !.
        range(I, N, [I|T]) :- I < N, I1 is I + 1, range(I1, N, T).
        tak(X, Y, Z, A) :- X =< Y, !, A = Z.
        tak(X, Y, Z, A) :-
            X1 is X - 1, tak(X1, Y, Z, A1),
            Y1 is Y - 1, tak(Y1, Z, X, A2),
            Z1 is Z - 1, tak(Z1, X, Y, A3),
            tak(A1, A2, A3, A).
        """;

    [JSExport]
    internal static async Task<string> WasmTierProbe(int rounds)
        => await Task.Run(() =>
        {
            var report = new StringBuilder();
            try
            {
                var tiered = new PrologEngine();
                tiered.ConsultString(TierProbeCorpus);
                tiered.IlPromotion.Threshold = 0;
                BrowserWasmTier.Attach(tiered, threshold: 1);
                if (tiered.IlPromotion.Wasm is null)
                    return "wasm tier NOT attached: the capability is off\n";

                var plain = new PrologEngine();
                plain.ConsultString(TierProbeCorpus);

                // Correctness first, on both.
                foreach (var goal in new[]
                {
                    "loop(1000).",
                    "range(1, 30, L), nrev(L, R), R = [30|_], length(R, 30).",
                    "tak(18, 12, 6, 7).",
                })
                {
                    WriteToPage($"[tier] goal {goal}\n");
                    bool a = tiered.Query(goal).Success;
                    WriteToPage($"[tier]   tiered => {a}\n");
                    bool b = plain.Query(goal).Success;
                    report.Append(a && b ? "ok      " : $"MISMATCH tier={a} plain={b} ")
                          .Append(goal).Append('\n');
                    if (!a || !b) return report.ToString();
                }
                int promoted = tiered.IlPromotion.PromotedFunctorIds().Count();
                report.Append("promoted predicates: ").Append(promoted).Append('\n');

                // Attribute nrev: verdict tally + a wall/call/stage time split.
                WasmTierDelegate.ResetDiag();
                BrowserWasmWorld.DiagCallTicks = 0;
                BrowserWasmWorld.DiagStageTicks = 0;
                var dsw = Stopwatch.StartNew();
                tiered.Query("range(1, 200, L), nrev(L, _).");
                dsw.Stop();
                double callMs = BrowserWasmWorld.DiagCallTicks * 1000.0 / Stopwatch.Frequency;
                double stageMs = BrowserWasmWorld.DiagStageTicks * 1000.0 / Stopwatch.Frequency;
                report.Append($"nrev diag: chains={WasmTierDelegate.DiagEntries} "
                    + $"switches={WasmTierDelegate.DiagSwitches} "
                    + $"deopts={WasmTierDelegate.DiagDeopts} "
                    + $"builtins={WasmTierDelegate.DiagBuiltins} "
                    + $"tailexits={WasmTierDelegate.DiagTailExits}\n");
                report.Append($"nrev time: wall={dsw.Elapsed.TotalMilliseconds:F1} ms, "
                    + $"inWasm={callMs:F1} ms, stage={stageMs:F1} ms, "
                    + $"glue={dsw.Elapsed.TotalMilliseconds - callMs - stageMs:F1} ms\n");

                WasmTierDelegate.ResetDiag();
                BrowserWasmWorld.DiagCallTicks = 0;
                BrowserWasmWorld.DiagStageTicks = 0;
                var tsw = Stopwatch.StartNew();
                tiered.Query("tak(14, 10, 4, _).");
                tsw.Stop();
                double tcallMs = BrowserWasmWorld.DiagCallTicks * 1000.0 / Stopwatch.Frequency;
                double tstageMs = BrowserWasmWorld.DiagStageTicks * 1000.0 / Stopwatch.Frequency;
                report.Append($"tak14 diag: chains={WasmTierDelegate.DiagEntries} "
                    + $"switches={WasmTierDelegate.DiagSwitches} "
                    + $"deopts={WasmTierDelegate.DiagDeopts} "
                    + $"builtins={WasmTierDelegate.DiagBuiltins} "
                    + $"tailexits={WasmTierDelegate.DiagTailExits}\n");
                report.Append($"tak14 time: wall={tsw.Elapsed.TotalMilliseconds:F1} ms, "
                    + $"inWasm={tcallMs:F1} ms, stage={tstageMs:F1} ms, "
                    + $"glue={tsw.Elapsed.TotalMilliseconds - tcallMs - tstageMs:F1} ms\n");

                rounds = Math.Max(1, rounds);
                double Median(PrologEngine e, string goal)
                {
                    double best = double.MaxValue;
                    for (int r = 0; r < rounds; r++)
                    {
                        var sw = Stopwatch.StartNew();
                        if (!e.Query(goal).Success) throw new InvalidOperationException(goal);
                        sw.Stop();
                        best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                        // One slow round is answer enough; do not multiply it.
                        if (best > 15_000) break;
                    }
                    return best;
                }
                foreach (var (name, goal) in new (string, string)[]
                {
                    ("counter 300k", "loop(300000)."),
                    ("nrev 200 x5",
                     "range(1, 200, L), nrev(L, _), nrev(L, _), nrev(L, _), nrev(L, _), nrev(L, _)."),
                    ("tak 18,12,6", "tak(18, 12, 6, _)."),
                })
                {
                    WriteToPage($"[tier] measuring {name}\n");
                    double tt = Median(tiered, goal), pp = Median(plain, goal);
                    string line = $"{name}: tier {tt:F1} ms, tier0 {pp:F1} ms, {pp / tt:F1}x";
                    WriteToPage($"[tier] {line}\n");
                    report.Append(line).Append('\n');
                }
            }
            catch (Exception ex)
            {
                report.Append("STOPPED: ").Append(ex.GetType().Name)
                      .Append(": ").Append(ex.Message).Append('\n')
                      .Append(ex.StackTrace).Append('\n');
            }
            return report.ToString();
        }).ConfigureAwait(false);
}
