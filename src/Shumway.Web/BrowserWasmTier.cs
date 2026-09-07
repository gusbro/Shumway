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

/// <summary>The browser's wasm execution world: the engine's arrays already
/// live inside the runtime's linear memory, so a chain pins them in place
/// once, writes their REAL addresses plus the scalars into a pinned mailbox
/// once, and then every module hop is a raw call through this thread's
/// function table -- no per-entry marshalling, which matters doubly here
/// because all C# in this file runs MONO-INTERPRETED (~150 us for the old
/// per-entry staging against ~3 us of wasm work). With threads on every
/// worker has its own table, so each module registers lazily per thread and
/// caches its index per thread (the engine is thread-agile; the REALM is
/// not).</summary>
internal sealed class BrowserWasmWorld : IWasmExecutionWorld
{
    private readonly List<(int Fid, byte[] PinnedModule, int Demand, ThreadLocal<int> Index)>
        _modules = new();
    private readonly Dictionary<int, int> _handleByFid = new();
    private int _maxDemand;

    // Timing split for the probe: ticks inside raw calls vs staging
    // (BeginChain + RefreshFromEngine). Process-wide diagnostics.
    internal static long DiagCallTicks, DiagStageTicks;

    public int RegisterModule(int functorId, byte[] module, int registerDemand)
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
        int handle = _modules.Count;
        _modules.Add((functorId, pinned, registerDemand, index));
        _handleByFid[functorId] = handle;
        if (registerDemand > _maxDemand) _maxDemand = registerDemand;
        return handle;
    }

    public bool TryGetHandle(int functorId, out int handle)
        => _handleByFid.TryGetValue(functorId, out handle);

    public int FunctorOfHandle(int handle) => _modules[handle].Fid;

    public IWasmChainContext BeginChain(Activation engine) => new Chain(this, engine);

    private sealed class Chain : IWasmChainContext
    {
        private readonly BrowserWasmWorld _w;
        private readonly Activation _engine;
        private readonly long[] _mailbox = GC.AllocateArray<long>(WasmAbi.SlotCount, pinned: true);
        private readonly int _mailboxAt;
        private GCHandle _heapPin, _stackPin, _regsPin, _trailPin;
        private Cell[] _heap = null!, _stack = null!, _regs = null!;
        private int[] _trail = null!;
        private bool _engineAuthoritative;

        public Chain(BrowserWasmWorld w, Activation engine)
        {
            _w = w;
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
            _engine.EnsureWasmRegisters(_w._maxDemand);
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

        public WasmVerdict Call(int handle, int cursor)
        {
            long t0 = Stopwatch.GetTimestamp();
            int v = WebShumwayApp.WasmCall(
                _w._modules[handle].Index.Value, _mailboxAt, cursor);
            DiagCallTicks += Stopwatch.GetTimestamp() - t0;
            return (WasmVerdict)v;
        }

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

/// <summary>Boot-time wiring of the wasm tier (plan phase 2): the promotion
/// store's <c>Promoter</c> compiles a predicate, registers its module in the
/// store's world, and wraps the chain-driving verdict loop. Gated by
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
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = threshold,
            Promoter = (pred, linkedBase) => Promote(store, world, pred, linkedBase),
        };
    }

    private static PredicateDelegate? Promote(IlPromotionStore store,
        BrowserWasmWorld world, CompiledPredicate pred, int linkedBase)
    {
        try
        {
            var env = new EngineWasmCompileEnv(pred.FunctorId, linkedBase);
            var entry = WasmPredicateCompiler.Compile(pred, env,
                floatLiterals: store.FloatPoolProvider?.Invoke(pred.FunctorId));
            int maxCursor = 0;
            foreach (var (_, c) in entry.CursorByAddress)
                if (c > maxCursor) maxCursor = c;
            var addrByCursor = new int[maxCursor + 1];
            foreach (var (addr, c) in entry.CursorByAddress)
                addrByCursor[c] = linkedBase + addr;
            WebShumwayApp.WriteToPage($"[tier] promoting fid={pred.FunctorId}\n");
            int handle = world.RegisterModule(
                pred.FunctorId, entry.Module, entry.RegisterDemand);
            WebShumwayApp.WriteToPage($"[tier] promoted fid={pred.FunctorId} h={handle}\n");
            return new WasmTierDelegate(pred.FunctorId, world, handle, addrByCursor).Invoke;
        }
        catch (WasmCompileException)
        {
            return null;
        }
    }
}

/// <summary>The phase-2 browser measurement: two engines side by side, one
/// with the wasm tier attached at threshold 1 and one plain Tier-0, over the
/// counter and nrev, correctness cross-checked first. Reached via the page
/// hash <c>#wasmtier</c>.</summary>
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
                WriteToPage("[tier] correctness done, measuring\n");
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
