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
        int RegisterDemand,
        WasmBuildAddressIndex AddrIndex);

    // (fid -> live linked address) after a relink; null until one happens.
    // Reference-swapped at a boundary tick, read lock-free by chains.
    private volatile IReadOnlyDictionary<int, int>? _liveByFid;

    public void RefreshLiveAddresses(IReadOnlyDictionary<int, int> liveByFid)
        => _liveByFid = liveByFid;

    public int LiveEntryAddressOf(int functorId)
        => _liveByFid is { } live && live.TryGetValue(functorId, out int at)
            ? at : EntryAddressOf(functorId);

    public long TranslatePcToLive(long buildPc)
        => _current is { } b ? b.AddrIndex.Translate(buildPc, _liveByFid) : buildPc;

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
            long t0 = Stopwatch.GetTimestamp();
            int i = WebShumwayApp.WasmRegister(at, pinned.Length);
            DiagRegisterTicks += Stopwatch.GetTimestamp() - t0;
            if (i < 0)
                throw new WasmRegisterException(
                    $"the group module ({pinned.Length} bytes) did not register"
                    + " — see the browser console for the engine's reason");
            return i;
        });
        // EAGERLY on the installing thread: a module the browser refuses (a
        // V8 size limit, say — a giant wasm_compile(all) group) must fail
        // HERE, where the caller can fall back to bytecode and report,
        // never inside some later user query's first chain call. Every pool
        // thread is the same kind of worker, so this thread's verdict
        // stands for the others.
        _ = index.Value;
        _current = new Build(pinned, index, entryCursorByFid, cursorByAddress,
                             entryAddressByFid, registerDemand,
                             new WasmBuildAddressIndex(entryAddressByFid));
    }

    /// <summary>Wall time inside the per-thread module registration
    /// (compile + instantiate + addFunction). Diagnostic.</summary>
    internal static long DiagRegisterTicks;

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
        => new Chain(this,
                     _current ?? throw new InvalidOperationException("no group installed"),
                     engine);

    private sealed class Chain : IWasmChainContext
    {
        private readonly BrowserWasmWorld _w;
        private readonly Build _build;
        private readonly Activation _engine;
        private readonly long[] _mailbox = GC.AllocateArray<long>(WasmAbi.SlotCount, pinned: true);
        private readonly int _mailboxAt;
        private GCHandle _heapPin, _stackPin, _regsPin, _trailPin;
        private Cell[] _heap = null!, _stack = null!, _regs = null!;
        private int[] _trail = null!;
        private bool _engineAuthoritative;

        public Chain(BrowserWasmWorld w, Build build, Activation engine)
        {
            _w = w;
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

        public long TranslatePcToLive(long buildPc)
            => _build.AddrIndex.Translate(buildPc, _w._liveByFid);

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

    // Every world holding installed builds for the CURRENT engine — the
    // dynamic group's and the baked prelude's — so a relink's live-address
    // refresh reaches them all. Reset when a new engine attaches.
    private static readonly List<BrowserWasmWorld> _worlds = new();

    /// <summary>Attaches the wasm promotion store to an engine. No-op when
    /// the capability is off.</summary>
    internal static void Attach(PrologEngine engine, int threshold = 16)
    {
        if (!RuntimeCaps.SupportsWasmCodegen) return;
        var store = engine.IlPromotion;
        var world = new BrowserWasmWorld();
        _worlds.Clear();
        _worlds.Add(world);
        BakedFids.Clear();
        var members = new List<WasmGroupMember>();
        var env = new EngineWasmCompileEnv();
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = threshold,
            Promoter = (pred, linkedBase) =>
                Promote(store, world, members, env, pred, linkedBase),
            BatchPromoter = candidates =>
                PromoteBatch(store, world, members, env, candidates),
        };
        // A relink moved predicates out from under their modules: the
        // evicted members leave the dynamic group (their biases are dead —
        // compiling them again would poison the whole build), and evicted
        // baked members leave the baked bookkeeping. What remains valid is
        // reinstalled so the group module carries no dead code.
        store.Wasm.StaleEvicted = staleFids =>
        {
            var gone = new HashSet<int>(staleFids);
            int removed = members.RemoveAll(m => gone.Contains(m.Predicate.FunctorId));
            BakedFids.RemoveWhere(gone.Contains);
            if (removed > 0 && members.Count > 0) InstallCurrent(world, members, env);
        };
        // A relink moved the code: the worlds translate at their boundaries,
        // and the member list REBASES so the next promotion compiles against
        // live addresses instead of poisoning the group with dead biases.
        store.Wasm.LiveRefreshed = liveByFid =>
        {
            foreach (var w in _worlds) w.RefreshLiveAddresses(liveByFid);
            for (int i = 0; i < members.Count; i++)
                if (liveByFid.TryGetValue(members[i].Predicate.FunctorId, out int at)
                    && members[i].Bias != at)
                    members[i] = members[i] with { Bias = at };
        };
    }

    /// <summary>The wasm_compile(all) path: the whole candidate set in ONE
    /// group build — O(n), where promoting the same set one dispatch at a
    /// time rebuilds the group per member. A candidate the compiler refuses
    /// must not take the batch down: on a failed build each candidate is
    /// test-compiled alone (still O(n): builds of one) and the refusals are
    /// marked unpromotable; the survivors build together.</summary>
    private static int PromoteBatch(IlPromotionStore store, BrowserWasmWorld world,
        List<WasmGroupMember> members, EngineWasmCompileEnv env,
        List<(CompiledPredicate Pred, int Addr)> candidates)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            var added = new List<WasmGroupMember>();
            foreach (var (pred, addr) in candidates)
                added.Add(new WasmGroupMember(pred, addr,
                    store.FloatPoolProvider?.Invoke(pred.FunctorId)));
            members.AddRange(added);
            try
            {
                InstallCurrent(world, members, env);
            }
            catch (WasmRegisterException)
            {
                // The BROWSER refused the module — too big, most likely.
                // The members are individually fine; back the whole batch
                // out and reinstall what worked before.
                members.RemoveRange(members.Count - added.Count, added.Count);
                if (members.Count > 0) InstallCurrent(world, members, env);
                return 0;
            }
            catch (WasmCompileException)
            {
                // Sort the poisoners out one by one, then build the rest.
                members.RemoveRange(members.Count - added.Count, added.Count);
                var good = new List<WasmGroupMember>();
                foreach (var m in added)
                {
                    try
                    {
                        WasmPredicateCompiler.CompileGroup(
                            new List<WasmGroupMember> { m }, env);
                        good.Add(m);
                    }
                    catch (WasmCompileException)
                    {
                        store.Wasm?.MarkUnpromotable(m.Predicate.FunctorId);
                    }
                }
                if (good.Count == 0) return 0;
                members.AddRange(good);
                try { InstallCurrent(world, members, env); }
                catch (WasmCompileException)
                {
                    // Individually fine but jointly refused — should not
                    // happen; back out rather than leave a broken group.
                    members.RemoveRange(members.Count - good.Count, good.Count);
                    if (members.Count > 0) InstallCurrent(world, members, env);
                    return 0;
                }
                added = good;
            }
            foreach (var m in added)
            {
                store.RegisterBoundDelegate(m.Predicate.FunctorId,
                    new WasmTierDelegate(m.Predicate.FunctorId, world).Invoke);
                store.Wasm?.NoteInstalled(m.Predicate.FunctorId, m.Bias,
                    m.Predicate);
            }
            return added.Count;
        }
        finally
        {
            DiagCompileTicks += Stopwatch.GetTimestamp() - t0;
            DiagCompileBuilds++;
        }
    }

    /// <summary>Wall time spent COMPILING group modules (each promotion
    /// rebuilds the whole group), and how many builds — the cost side of the
    /// tier, reported by wasm_compile(status). Mono-interpreted C#, so this
    /// is the dominant promotion cost in the browser.</summary>
    internal static long DiagCompileTicks;
    internal static int DiagCompileBuilds;

    private static PredicateDelegate? Promote(IlPromotionStore store,
        BrowserWasmWorld world, List<WasmGroupMember> members,
        EngineWasmCompileEnv env, CompiledPredicate pred, int linkedBase)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            return PromoteCore(store, world, members, env, pred, linkedBase);
        }
        finally
        {
            DiagCompileTicks += Stopwatch.GetTimestamp() - t0;
            DiagCompileBuilds++;
        }
    }

    private static PredicateDelegate? PromoteCore(IlPromotionStore store,
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

    /// <summary>Installs the build-time-baked prelude group without
    /// compiling anything, after replaying the bake's evidence against this
    /// process (see <see cref="WasmBakedGroup.Validate"/>). The baked group
    /// lives in its OWN world, frozen: later promotions build a second,
    /// user-code group from empty — extending the baked one would make the
    /// first lazy promotion recompile the whole prelude. A call between the
    /// two groups is an ordinary chain switch. On any mismatch nothing is
    /// installed and the tier compiles as before.</summary>
    /// <summary>What became of the baked prelude at boot — surfaced by
    /// wasm_compile(status), because boot-time page writes predate the
    /// console.</summary>
    internal static string BakedInstallNote = "no asset";

    /// <summary>The baked prelude's members: status folds these into one
    /// count so the promoted list shows the USER's predicates, not five
    /// hundred prelude internals burying them.</summary>
    internal static readonly HashSet<int> BakedFids = new();

    internal static bool TryInstallBaked(PrologEngine engine, byte[] asset,
        out string reason)
    {
        var store = engine.IlPromotion;
        if (store.Wasm is null) { reason = "tier not attached"; return false; }
        WasmBakedGroup baked;
        try { baked = WasmBakedGroup.Read(new MemoryStream(asset)); }
        catch (Exception e) { reason = $"unreadable asset: {e.Message}"; return false; }
        // The same throwaway goal the bake ran to materialise the static
        // link — intern parity with the bake.
        engine.Query("true.");
        var byAddress = new Dictionary<int, CompiledPredicate>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
            byAddress[addr] = pred;
        if (byAddress.Count == 0) { reason = "no static link"; return false; }
        if (!baked.Validate(new EngineWasmCompileEnv(), byAddress,
                fid => store.FloatPoolProvider?.Invoke(fid), out reason))
            return false;
        var world = new BrowserWasmWorld();
        var entryCursors = new Dictionary<int, int>(baked.Members.Count);
        var entryAddr = new Dictionary<int, int>(baked.Members.Count);
        foreach (var m in baked.Members)
        {
            entryCursors[m.FunctorId] = m.EntryCursor;
            entryAddr[m.FunctorId] = m.Bias;
        }
        var cursorByAddress = new Dictionary<int, int>(baked.CursorByAddress.Count);
        foreach (var kv in baked.CursorByAddress) cursorByAddress[kv.Key] = kv.Value;
        try
        {
            world.InstallGroup(baked.Module, entryCursors, cursorByAddress,
                entryAddr, baked.RegisterDemand);
        }
        catch (WasmRegisterException e) { reason = e.Message; return false; }
        _worlds.Add(world);
        foreach (var m in baked.Members)
        {
            store.RegisterBoundDelegate(m.FunctorId,
                new WasmTierDelegate(m.FunctorId, world).Invoke);
            store.Wasm?.NoteInstalled(m.FunctorId, m.Bias, byAddress[m.Bias]);
            BakedFids.Add(m.FunctorId);
        }
        reason = $"{baked.Members.Count} predicates";
        return true;
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

/// <summary>Phase B of the wasm-tier plan: the benchmark page. Five programs
/// — the three of the tier probe plus crypt and zebra from the Van Roy suite
/// — each in its OWN pair of engines (the group module is the program's), a
/// correctness cross-check first, then best-of-rounds wall time for tiered
/// against plain Tier-0 and the geometric mean over the set. Reached via the
/// page hash <c>#wasmbench</c>; the report feeds
/// docs/benchmarks/browser.md.</summary>
internal static partial class WebShumwayApp
{
    private const string CryptSource = """
        crypt([O, N, E, T, W]) :-
            digit(O), O > 0,
            digit(N), N \== O,
            digit(E), E \== O, E \== N,
            digit(T), T > 0, T \== O, T \== N, T \== E,
            digit(W), W \== O, W \== N, W \== E, W \== T,
            100*O + 10*N + E
          + 100*O + 10*N + E
          =:= 100*T + 10*W + O.
        digit(0). digit(1). digit(2). digit(3). digit(4).
        digit(5). digit(6). digit(7). digit(8). digit(9).
        cbench(0) :- !.
        cbench(N) :- crypt(_), !, N1 is N - 1, cbench(N1).
        """;

    private const string ZebraSource = """
        zebra(Houses, Zebra, Water) :-
            Houses = [house(_, _, _, _, _), house(_, _, _, _, _),
                      house(_, _, _, _, _), house(_, _, _, _, _),
                      house(_, _, _, _, _)],
            member_(house(red, english, _, _, _), Houses),
            member_(house(_, spanish, dog, _, _), Houses),
            member_(house(green, _, _, coffee, _), Houses),
            member_(house(_, ukrainian, _, tea, _), Houses),
            right_of(house(green, _, _, _, _), house(ivory, _, _, _, _), Houses),
            member_(house(_, _, snails, _, winston), Houses),
            member_(house(yellow, _, _, _, kools), Houses),
            middle(house(_, _, _, milk, _), Houses),
            first(house(_, norwegian, _, _, _), Houses),
            next_to(house(_, _, _, _, chesterfield), house(_, _, fox, _, _), Houses),
            next_to(house(_, _, _, _, kools), house(_, _, horse, _, _), Houses),
            member_(house(_, _, _, orange_juice, lucky_strike), Houses),
            member_(house(_, japanese, _, _, parliaments), Houses),
            next_to(house(_, norwegian, _, _, _), house(blue, _, _, _, _), Houses),
            member_(house(_, Zebra, zebra, _, _), Houses),
            member_(house(_, Water, _, water, _), Houses).
        member_(X, [X|_]).
        member_(X, [_|T]) :- member_(X, T).
        right_of(A, B, [B, A | _]).
        right_of(A, B, [_|T]) :- right_of(A, B, T).
        next_to(A, B, [A, B | _]).
        next_to(A, B, [B, A | _]).
        next_to(A, B, [_|T]) :- next_to(A, B, T).
        first(X, [X|_]).
        middle(X, [_, _, X, _, _]).
        zbench(0) :- !.
        zbench(N) :- zebra(_, _, _), !, N1 is N - 1, zbench(N1).
        """;

    private sealed record BenchProgram(
        string Name, string Source, string CheckGoal, string BenchGoal);

    private static readonly BenchProgram[] BenchPrograms =
    {
        new("counter 300k", TierProbeCorpus,
            "loop(1000).", "loop(300000)."),
        new("nrev 200 x5", TierProbeCorpus,
            "range(1, 30, L), nrev(L, R), R = [30|_], length(R, 30).",
            "range(1, 200, L), nrev(L, _), nrev(L, _), nrev(L, _), nrev(L, _), nrev(L, _)."),
        new("tak 18,12,6", TierProbeCorpus,
            "tak(18, 12, 6, 7).", "tak(18, 12, 6, _)."),
        new("crypt x10", CryptSource,
            "crypt([O, N, E, T, W]), 200*O + 20*N + 2*E =:= 100*T + 10*W + O.",
            "cbench(10)."),
        new("zebra x10", ZebraSource,
            "zebra(_, Z, W), Z == japanese, W == norwegian.",
            "zbench(10)."),
    };

    [JSExport]
    internal static async Task<string> WasmBenchProbe(int rounds)
        => await Task.Run(() =>
        {
            var report = new StringBuilder();
            rounds = Math.Max(1, rounds);
            var speedups = new List<double>();
            try
            {
                foreach (var prog in BenchPrograms)
                {
                    WriteToPage($"[bench] {prog.Name}: engines up\n");
                    var tiered = new PrologEngine();
                    tiered.ConsultString(prog.Source);
                    tiered.IlPromotion.Threshold = 0;
                    BrowserWasmTier.Attach(tiered, threshold: 1);
                    if (tiered.IlPromotion.Wasm is null)
                        return "wasm tier NOT attached: the capability is off\n";
                    var plain = new PrologEngine();
                    plain.ConsultString(prog.Source);

                    // Correctness on both, and it doubles as the warmup that
                    // promotes the tiered engine's group.
                    bool a = tiered.Query(prog.CheckGoal).Success;
                    bool b = plain.Query(prog.CheckGoal).Success;
                    if (!a || !b)
                    {
                        report.Append($"MISMATCH {prog.Name}: tier={a} plain={b} "
                            + prog.CheckGoal).Append('\n');
                        continue;
                    }

                    WasmTierDelegate.ResetDiag();
                    double tt = BenchMedian(tiered, prog.BenchGoal, rounds);
                    long entries = WasmTierDelegate.DiagEntries;
                    long deopts = WasmTierDelegate.DiagDeopts;
                    long builtins = WasmTierDelegate.DiagBuiltins;
                    long tails = WasmTierDelegate.DiagTailExits;
                    double pp = BenchMedian(plain, prog.BenchGoal, rounds);
                    int promoted = tiered.IlPromotion.PromotedFunctorIds().Count();
                    double speedup = pp / tt;
                    speedups.Add(speedup);
                    string line = $"{prog.Name}: tier {tt:F1} ms, tier0 {pp:F1} ms, "
                        + $"{speedup:F1}x  (promoted={promoted} entries={entries} "
                        + $"deopts={deopts} builtinExits={builtins} tailExits={tails})";
                    WriteToPage($"[bench] {line}\n");
                    report.Append(line).Append('\n');
                }
                if (speedups.Count > 0)
                {
                    double geo = Math.Exp(speedups.Sum(Math.Log) / speedups.Count);
                    report.Append($"geomean over {speedups.Count}: {geo:F1}x\n");
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

    private static double BenchMedian(PrologEngine e, string goal, int rounds)
    {
        double best = double.MaxValue;
        for (int r = 0; r < rounds; r++)
        {
            var sw = Stopwatch.StartNew();
            if (!e.Query(goal).Success) throw new InvalidOperationException(goal);
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            if (best > 20_000) break;   // one slow round is answer enough
        }
        return best;
    }
}

/// <summary>The REPL's runtime switch for the wasm tier: the page answers the
/// pseudo-goal <c>wasm_compile.</c> (and its variants) by calling here, the
/// way <c>restart.</c> is answered by the page. Attaching to the LIVE engine
/// is safe between queries — nothing already running changes, the next
/// dispatches start counting. "off" stops further promotion; what already
/// promoted keeps running as wasm (detaching delegates under a live wasm
/// choice point would break its redo), and <c>restart.</c> is the full off:
/// a fresh engine has no tier attached.</summary>
internal static partial class WebShumwayApp
{
    [JSExport]
    internal static Task<string> WasmCompileControl(string command)
        => OnEngine(() =>
        {
            if (!Shumway.Core.RuntimeCaps.SupportsWasmCodegen)
                return "% wasm_compile: the capability is off in this build\n";
            var engine = _session?.Engine;
            if (engine is null) return "% wasm_compile: no engine\n";
            var store = engine.IlPromotion;

            if (command == "status")
            {
                if (store.Wasm is not { } w)
                    return "% wasm_compile: not attached (wasm_compile. to attach)\n";
                string Name(int f)
                {
                    var (aid, ar) = Shumway.Core.FunctorTable.Lookup(f);
                    return $"{Shumway.Core.AtomTable.GetById(aid)?.Name}/{ar}";
                }
                // Status is read to find the USER's predicates. Everything
                // else folds into counts: the baked prelude's members, and
                // library modules (a use_module(library(clpfd)) promotes
                // hundreds of clpfd$... internals under `all`). A name's
                // module is its prefix up to the scope '$' — one more '$'
                // along when it starts with '$' ($q$..., $prelude$$...).
                static string PrefixModuleOf(string name)
                {
                    int at = name.IndexOf('$', name.StartsWith('$') ? 1 : 0);
                    if (at <= 0) return "";
                    int scope = name.StartsWith('$') ? name.IndexOf('$', at + 1) : at;
                    return scope > 0 ? name[..at] : "";
                }
                // A library's EXPORTS carry no module prefix (clpfd's in/2,
                // #=/2, label/1 look exactly like user predicates), so the
                // qualified-name rule alone leaves dozens of them in the
                // list. The engine's module manifests name them.
                var exporter = new Dictionary<int, string>();
                foreach (var (modName, manifest) in engine.Modules)
                    foreach (int pf in manifest.PublicFunctors)
                        exporter[pf] = modName;
                var allPromoted = store.PromotedFunctorIds().ToList();
                var promoted = new List<string>();
                var byModule = new SortedDictionary<string, int>();
                int baked = 0;
                foreach (int f in allPromoted)
                {
                    if (BrowserWasmTier.BakedFids.Contains(f)) { baked++; continue; }
                    string name = Name(f);
                    string mod = PrefixModuleOf(name);
                    if (mod is "" && exporter.TryGetValue(f, out string? owner)) mod = owner;
                    // Compiler-generated helpers ($disj_N, $neg_N, the $q$
                    // query wrappers) are nobody's source predicate: they
                    // belong to whatever clause spawned them.
                    if (mod is "" or "user" && name.StartsWith('$')) mod = "generated";
                    if (mod is "" or "user") promoted.Add(name);
                    else byModule[mod] = byModule.GetValueOrDefault(mod) + 1;
                }
                var folded = byModule.Select(kv => $"{kv.Value} {kv.Key}").ToList();
                if (baked > 0) folded.Add($"{baked} baked prelude");
                var refused = w.UnpromotableFunctorIds().Select(Name).ToList();
                return $"% wasm_compile: threshold={w.Threshold}\n"
                    + $"%   baked prelude: {BrowserWasmTier.BakedInstallNote}\n"
                    + (w.RelinkEvictions > 0
                        ? $"%   relink evictions: {w.RelinkEvictions} (a library "
                          + "load moved the code; evicted predicates re-promote)\n"
                        : "")
                    + $"%   promoted ({promoted.Count}"
                    + (folded.Count > 0 ? " + " + string.Join(" + ", folded) : "")
                    + $"): {string.Join(" ", promoted)}\n"
                    + $"%   refused ({refused.Count}): {string.Join(" ", refused)}\n"
                    + $"%   chains={WasmTierDelegate.DiagEntries} "
                    + $"switches={WasmTierDelegate.DiagSwitches} "
                    + $"deopts={WasmTierDelegate.DiagDeopts} "
                    + $"builtinExits={WasmTierDelegate.DiagBuiltins} "
                    + $"tailExits={WasmTierDelegate.DiagTailExits}\n"
                    + $"%   compile: {BrowserWasmTier.DiagCompileBuilds} group builds, "
                    + $"{BrowserWasmTier.DiagCompileTicks * 1000.0 / Stopwatch.Frequency:F0} ms total\n";
            }
            if (command == "off")
            {
                if (store.Wasm is { } w)
                {
                    w.Threshold = 0;
                    w.CompileAllOnConsult = false;
                }
                return "% wasm_compile: promotion off — already-promoted "
                    + "predicates keep running as wasm (restart. for a clean engine)\n";
            }
            if (command == "all")
            {
                // Compile the whole static program NOW and again after every
                // consult — never on the user's first real query, which would
                // otherwise be billed for all of it at once.
                if (store.Wasm is null) BrowserWasmTier.Attach(engine, threshold: 16);
                if (store.Wasm is not { } wa)
                    return "% wasm_compile: could not attach\n";
                wa.CompileAllOnConsult = true;
                long b0 = Stopwatch.GetTimestamp();
                int batched = wa.CompileAllTick(engine);
                double ms = (Stopwatch.GetTimestamp() - b0) * 1000.0 / Stopwatch.Frequency;
                // Compiling the whole prelude here means the baked group is
                // NOT carrying it — say why right where the cost shows up,
                // not only in status.
                string bakedNote = BrowserWasmTier.BakedFids.Count == 0 && batched > 100
                    ? $"% (the baked prelude is not installed — {BrowserWasmTier.BakedInstallNote})\n"
                    : "";
                return $"% wasm_compile: all — {batched} predicates compiled now "
                    + $"({ms:F0} ms); every consult recompiles the new ones\n"
                    + bakedNote
                    + "% (experimental)\n";
            }
            // "on", or a numeric threshold. Attach once; afterwards only the
            // threshold moves (re-attaching would abandon the group's members
            // while their delegates live on).
            int threshold = command == "on" ? 16
                : int.TryParse(command, out int n) && n > 0 ? n : -1;
            if (threshold < 0)
                return "% wasm_compile: on | off | status | <threshold>\n";
            if (store.Wasm is { } existing)
            {
                existing.Threshold = threshold;
                return $"% wasm_compile: threshold={threshold}\n";
            }
            BrowserWasmTier.Attach(engine, threshold);
            return store.Wasm is null
                ? "% wasm_compile: could not attach\n"
                : $"% wasm_compile: attached, threshold={threshold} — hot "
                  + "predicates now compile to WebAssembly\n";
        });

    /// <summary>Called by the page after a consult and after each completed
    /// query: under wasm_compile(all) it re-runs the batch when the program
    /// changed — a consult mid-query included — so the compile always lands
    /// here, on the boundary, never inside the user's next real query. One
    /// int compare when nothing changed.</summary>
    [JSExport]
    internal static Task<int> WasmCompileAllTick()
        => OnEngine(() =>
        {
            var engine = _session?.Engine;
            if (engine?.IlPromotion.Wasm is not { CompileAllOnConsult: true } w)
                return 0;
            return w.CompileAllTick(engine);
        });
}

/// <summary>The BROWSER refused the compiled group module (a V8 limit, an
/// instantiation failure) — the members are individually fine, so retrying
/// them one by one, as a compile refusal warrants, would burn minutes to
/// learn nothing. Callers back the group out instead.</summary>
internal sealed class WasmRegisterException(string message)
    : WasmCompileException(message);
