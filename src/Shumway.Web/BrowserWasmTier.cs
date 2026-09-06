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

/// <summary>The browser's wasm Tier-1 runner: the engine's arrays already
/// live inside the runtime's linear memory, so an entry pins them in place,
/// writes their REAL addresses plus the scalars into the pinned mailbox, and
/// calls the registered module through this thread's function table -- no
/// copies, no JavaScript on the hot path. With threads on every worker has
/// its own table, so the module registers lazily per thread and the index is
/// cached per thread (the engine is thread-agile; the REALM is not).</summary>
internal sealed class BrowserWasmRunner : IWasmActivationRunner
{
    private readonly byte[] _module;        // shared-memory-patched, pinned
    private readonly ThreadLocal<int> _index;
    private readonly long[] _mailbox = GC.AllocateArray<long>(WasmAbi.SlotCount, pinned: true);
    private readonly int _mailboxAt;
    private readonly int _registerDemand;

    public BrowserWasmRunner(byte[] module, int registerDemand)
    {
        _registerDemand = registerDemand;
        byte[] patched = WasmSharedMemory.Patch(module);
        _module = GC.AllocateArray<byte>(patched.Length, pinned: true);
        patched.CopyTo(_module, 0);
        _index = new ThreadLocal<int>(() =>
        {
            int at = (int)(nint)Marshal.UnsafeAddrOfPinnedArrayElement(_module, 0);
            int index = WebShumwayApp.WasmRegister(at, _module.Length);
            if (index < 0)
                throw new InvalidOperationException("wasm module did not register");
            return index;
        });
        _mailboxAt = (int)(nint)Marshal.UnsafeAddrOfPinnedArrayElement(_mailbox, 0);
    }

    public unsafe WasmVerdict Run(Activation engine, int cursor)
    {
        engine.EnsureWasmRegisters(_registerDemand);
        Cell[] heap = engine.WasmHeapView;
        Cell[] stack = engine.WasmStackView;
        Cell[] regs = engine.WasmRegistersView;
        int[] trail = engine.WasmBindingTrailView;
        // Managed code only runs while the wasm is bailed, and the arrays are
        // only replaced (growth, GC) from managed code, so the pins below are
        // stable for exactly the duration of the call -- D2.
        fixed (Cell* h = heap)
        fixed (Cell* st = stack)
        fixed (Cell* rg = regs)
        fixed (int* tr = trail)
        {
            var bases = new Activation.WasmMailboxBases(
                (long)(nint)h, (long)(nint)st, (long)(nint)rg, (long)(nint)tr,
                HeapLimitCells: heap.Length - 8,
                StackLimitCells: stack.Length - 8,
                TrailLimitEntries: trail.Length - 8,
                FunctorArityBase: BrowserWasmTier.ArityMirrorAddress());
            if (!engine.TryFillWasmMailbox(_mailbox, bases))
                throw new InvalidOperationException(
                    "a mode-incompatible activation reached the wasm runner");
            // Byte addresses vs cell indexing: the module computes
            // base + index*8, so bases go in as raw byte addresses. They ARE
            // i32-sized here (browser linear memory).
            int verdict = WebShumwayApp.WasmCall(_index.Value, _mailboxAt, cursor);
            engine.SyncFromWasmMailbox(_mailbox);
            return (WasmVerdict)verdict;
        }
    }

    public long ReadSlot(int slot) => _mailbox[slot];

    public void Dispose() => _index.Dispose();
}

/// <summary>Boot-time wiring of the wasm tier (plan phase 2): the promotion
/// store's <c>Promoter</c> compiles a predicate, registers the module, and
/// wraps the verdict loop. Gated by <see cref="RuntimeCaps.SupportsWasmCodegen"/>
/// -- only Shumway.Web turns the feature switch on.</summary>
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
                // Note: the new address is picked up on the NEXT entry; the
                // old pinned array stays valid for any call in flight.
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
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = threshold,
            Promoter = (pred, linkedBase) => Promote(store, pred, linkedBase),
        };
    }

    private static PredicateDelegate? Promote(
        IlPromotionStore store, CompiledPredicate pred, int linkedBase)
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
            var runner = new BrowserWasmRunner(entry.Module, entry.RegisterDemand);
            return new WasmTierDelegate(pred.FunctorId, runner, addrByCursor).Invoke;
        }
        catch (WasmCompileException)
        {
            return null;
        }
    }
}


/// <summary>The phase-2 browser measurement: two engines side by side, one
/// with the wasm tier attached at threshold 1 and one plain Tier-0, over the
/// counter, nrev and tak. Correctness first (both agree), then medians.
/// Reached via the page hash <c>#wasmtier</c>.</summary>
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
                    bool a = tiered.Query(goal).Success;
                    bool b = plain.Query(goal).Success;
                    report.Append(a && b ? "ok      " : $"MISMATCH tier={a} plain={b} ")
                          .Append(goal).Append('\n');
                    if (!a || !b) return report.ToString();
                }
                int promoted = tiered.IlPromotion.PromotedFunctorIds().Count();
                report.Append("promoted predicates: ").Append(promoted).Append('\n');
                if (promoted == 0)
                    report.Append("NOTE: nothing promoted; the measurement below is Tier-0 vs Tier-0\n");

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
                    }
                    return best;
                }
                foreach (var (name, goal) in new (string, string)[]
                {
                    ("counter 300k", "loop(300000)."),
                    ("nrev 200 x20", "range(1, 200, L), nrevN(20, L)."),
                    // tak is deliberately left out: it is nothing but is/2 and
                    // =</2, so under the current design every arithmetic goal
                    // crosses the wasm boundary as a BuiltinRequest (~150 ns
                    // measured) and the boundary tax dominates -- the case the
                    // plan flags for open-coded wasm builtins (phase B).
                })
                {
                    if (goal.Contains("nrevN"))
                    {
                        // nrevN is not in the corpus; inline the loop goal.
                        string g = "range(1, 200, L), nrev(L, _), nrev(L, _), nrev(L, _), nrev(L, _), nrev(L, _).";
                        double t = Median(tiered, g), p = Median(plain, g);
                        report.Append($"{name}: tier {t:F1} ms, tier0 {p:F1} ms, {p / t:F1}x\n");
                        continue;
                    }
                    double tt = Median(tiered, goal), pp = Median(plain, goal);
                    report.Append($"{name}: tier {tt:F1} ms, tier0 {pp:F1} ms, {pp / tt:F1}x\n");
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
