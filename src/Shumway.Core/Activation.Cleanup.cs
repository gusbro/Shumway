using System.Collections.Generic;

namespace Shumway.Core;

// setup_call_cleanup/3 support (ADR-040 Tier-1). A "cleanup handler" ties a
// Cleanup goal (stored stably as a '$cleanup_pending'/2 dynamic fact, keyed by an
// integer Ref) to the choice-point level at which setup_call_cleanup registered
// it. When that scope is discarded WITHOUT the prelude's own synchronous fire
// having run — an external cut past it, an exception unwinding from below, or the
// query being torn down — the engine enqueues the Ref so the interpreter runs the
// cleanup at its next safe point (modelled on the wakeup drain). The dynamic-fact
// retract inside '$scc_fire'/1 is the exactly-once guard, so a redundant enqueue
// is harmless. The prelude's deterministic-success / failure / error paths call
// '$scc_fire'/1 directly and FORGET the handler first, so only genuinely-leftover
// handlers ever fire asynchronously.
public sealed partial class Activation
{
    private struct CleanupHandler
    {
        public int Level;      // the choice-point pointer (_b) at registration
        public int Ref;        // key into the '$cleanup_pending'/2 dynamic store
        public bool Enqueued;  // already moved to the pending-run queue
        public Cell Live;      // the LIVE Cleanup term (dereffed cell) — an
                               // async fire runs THIS, so its bindings reach
                               // the caller (test: scc(true, scc(...), Y=3), !
                               // must leave Y=3). A GC root — see
                               // MarkCleanupRoots.
    }

    private List<CleanupHandler>? _cleanupHandlers;
    private List<(int Ref, Cell Live, bool UseLive)>? _pendingCleanupRefs;
    private int _nextCleanupRef = 1;

    /// <summary>Marks the LIVE Cleanup terms as heap roots. A handler holds its
    /// goal so an async fire runs the real term and its bindings reach the
    /// caller; nothing else need reference it, so without this the collector
    /// frees the goal out from under a handler that has not fired yet.</summary>
    internal void MarkCleanupRoots()
    {
        if (_cleanupHandlers is { } hs)
            foreach (var h in hs) GcMarkReferents(h.Live);
        if (_pendingCleanupRefs is { } ps)
            foreach (var p in ps) GcMarkReferents(p.Live);
    }

    /// <summary>Relocates those same Cleanup terms.</summary>
    internal void RelocateCleanupRoots(System.Func<Cell, Cell> relocate)
    {
        if (_cleanupHandlers is { } hs)
            for (int i = 0; i < hs.Count; i++)
            {
                var h = hs[i];
                h.Live = relocate(h.Live);
                hs[i] = h;
            }
        if (_pendingCleanupRefs is { } ps)
            for (int i = 0; i < ps.Count; i++)
            {
                var (r, live, useLive) = ps[i];
                ps[i] = (r, relocate(live), useLive);
            }
    }

    /// <summary>Registers a cleanup handler at the current choice-point level and
    /// returns its Ref (the key the caller stored the Cleanup goal under).
    /// <paramref name="liveCleanup"/> is the cleanup term's dereffed cell,
    /// used by the ASYNC fire paths so bindings survive.</summary>
    public int RegisterCleanupHandler(Cell liveCleanup)
    {
        _cleanupHandlers ??= new List<CleanupHandler>();
        // Registration order is usually level order too, and saying so lets
        // the cut hook below find its range instead of walking everyone.
        if (_cleanupHandlers.Count == 0) _cleanupLevelsSorted = true;
        else if (_b < _cleanupHandlers[^1].Level) _cleanupLevelsSorted = false;
        int r = _nextCleanupRef++;
        _cleanupHandlers.Add(new CleanupHandler
            { Level = _b, Ref = r, Enqueued = false, Live = liveCleanup });
        return r;
    }

    /// <summary>Drops a handler once the prelude has fired it synchronously
    /// (deterministic success / failure / error), so it can never fire again.</summary>
    public void ForgetCleanupHandler(int refId)
    {
        if (_cleanupHandlers is null) return;
        for (int i = _cleanupHandlers.Count - 1; i >= 0; i--)
            if (_cleanupHandlers[i].Ref == refId)
            {
                _cleanupHandlers.RemoveAt(i);
                if (_cleanupHandlers.Count == 0) _cleanupLevelsSorted = true;
                return;
            }
    }

    /// <summary>Cut hook: enqueue every handler whose registration level is AT OR
    /// ABOVE the cut barrier — Goal's choice points sit strictly above the
    /// registration level, so a cut to <c>barrier &lt;= Level</c> discards the
    /// whole setup_call_cleanup continuation without a backtrack into it. Cheap
    /// no-op when no handlers are live.</summary>
    /// <summary><paramref name="heapIntact"/> discriminates the trigger: a
    /// CUT discards choice points but leaves the heap alone, so the fire may
    /// run the LIVE Cleanup cell (bindings reach the caller); an EXCEPTION
    /// unwind truncates the heap below the catcher, so the live cell may
    /// point at reclaimed memory and the fire must use the stable
    /// '$cleanup_pending' copy instead.</summary>
    public void FireCleanupsAbove(int barrier, bool heapIntact = true)
    {
        if (_cleanupHandlers is null || _cleanupHandlers.Count == 0) return;
        // Only handlers at or above the barrier can fire, and a cut asks that
        // question far more often than it gets a yes. Walking every live
        // handler each time made a NEST of setup_call_cleanup quadratic:
        // 4,000 deep spent 25,749,670 steps in this loop, about 1.6n^2.
        //
        // Registration level rises with registration order unless something
        // registers after backtracking below an older handler, which is
        // noticed as it happens; while that holds, the handlers that can fire
        // are a SUFFIX and binary search finds where it starts. The scan then
        // runs forward from there, so the order they are enqueued in -- which
        // is the order they will run in -- is exactly what it was.
        for (int i = FirstCleanupAtOrAbove(barrier); i < _cleanupHandlers.Count; i++)
        {
            CleanupHandler h = _cleanupHandlers[i];
            if (!h.Enqueued && h.Level >= barrier)
            {
                h.Enqueued = true;
                _cleanupHandlers[i] = h;
                (_pendingCleanupRefs ??= new()).Add((h.Ref, h.Live, heapIntact));
            }
        }
    }

    /// <summary>The first handler that could match a barrier: the start of the
    /// suffix when levels are in order, and the whole list when they are
    /// not.</summary>
    private int FirstCleanupAtOrAbove(int barrier)
    {
        if (!_cleanupLevelsSorted) return 0;
        int lo = 0, hi = _cleanupHandlers!.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_cleanupHandlers[mid].Level < barrier) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>True while the handlers' levels are non-decreasing, so the
    /// ones a barrier can reach form a suffix. Cleared when a registration
    /// breaks it and restored when the list empties.</summary>
    private bool _cleanupLevelsSorted = true;

    /// <summary>Teardown hook: enqueue every remaining handler (the query ended,
    /// or the caller stopped asking with choice points still live).</summary>
    public void FireAllRemainingCleanups()
    {
        if (_cleanupHandlers is null) return;
        for (int i = 0; i < _cleanupHandlers.Count; i++)
        {
            CleanupHandler h = _cleanupHandlers[i];
            if (!h.Enqueued)
            {
                h.Enqueued = true;
                _cleanupHandlers[i] = h;
                // Teardown: the query is over — engine state may be
                // arbitrary, run the stable copy.
                (_pendingCleanupRefs ??= new()).Add((h.Ref, h.Live, false));
            }
        }
    }

    public bool HasCleanupHandlers => _cleanupHandlers is { Count: > 0 };
    public bool HasPendingCleanups => _pendingCleanupRefs is { Count: > 0 };

    /// <summary>Pops one pending cleanup in QUEUE order — a single cut
    /// discarding nested scc scopes enqueues inside-out, so FIFO fires the
    /// inner cleanup before the outer (WG17 `innerouter`). Fails when the
    /// queue is empty; the interpreter's safe-point drain loops on this.</summary>
    public bool TryPopPendingCleanup(
        out int refId, out Cell liveCleanup, out bool useLive)
    {
        if (_pendingCleanupRefs is null || _pendingCleanupRefs.Count == 0)
        {
            refId = 0;
            liveCleanup = default;
            useLive = false;
            return false;
        }
        (refId, liveCleanup, useLive) = _pendingCleanupRefs[0];
        _pendingCleanupRefs.RemoveAt(0);
        return true;
    }
}
