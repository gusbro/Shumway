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
    }

    private List<CleanupHandler>? _cleanupHandlers;
    private List<int>? _pendingCleanupRefs;
    private int _nextCleanupRef = 1;

    /// <summary>Registers a cleanup handler at the current choice-point level and
    /// returns its Ref (the key the caller stored the Cleanup goal under).</summary>
    public int RegisterCleanupHandler()
    {
        _cleanupHandlers ??= new List<CleanupHandler>();
        int r = _nextCleanupRef++;
        _cleanupHandlers.Add(new CleanupHandler { Level = _b, Ref = r, Enqueued = false });
        return r;
    }

    /// <summary>Drops a handler once the prelude has fired it synchronously
    /// (deterministic success / failure / error), so it can never fire again.</summary>
    public void ForgetCleanupHandler(int refId)
    {
        if (_cleanupHandlers is null) return;
        for (int i = _cleanupHandlers.Count - 1; i >= 0; i--)
            if (_cleanupHandlers[i].Ref == refId) { _cleanupHandlers.RemoveAt(i); return; }
    }

    /// <summary>Cut hook: enqueue every handler whose registration level is AT OR
    /// ABOVE the cut barrier — Goal's choice points sit strictly above the
    /// registration level, so a cut to <c>barrier &lt;= Level</c> discards the
    /// whole setup_call_cleanup continuation without a backtrack into it. Cheap
    /// no-op when no handlers are live.</summary>
    public void FireCleanupsAbove(int barrier)
    {
        if (_cleanupHandlers is null || _cleanupHandlers.Count == 0) return;
        for (int i = 0; i < _cleanupHandlers.Count; i++)
        {
            CleanupHandler h = _cleanupHandlers[i];
            if (!h.Enqueued && h.Level >= barrier)
            {
                h.Enqueued = true;
                _cleanupHandlers[i] = h;
                (_pendingCleanupRefs ??= new List<int>()).Add(h.Ref);
            }
        }
    }

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
                (_pendingCleanupRefs ??= new List<int>()).Add(h.Ref);
            }
        }
    }

    public bool HasCleanupHandlers => _cleanupHandlers is { Count: > 0 };
    public bool HasPendingCleanups => _pendingCleanupRefs is { Count: > 0 };

    /// <summary>Pops one pending cleanup Ref (LIFO), or fails when the queue is
    /// empty. The interpreter's safe-point drain loops on this.</summary>
    public bool TryPopPendingCleanup(out int refId)
    {
        if (_pendingCleanupRefs is null || _pendingCleanupRefs.Count == 0)
        {
            refId = 0;
            return false;
        }
        int last = _pendingCleanupRefs.Count - 1;
        refId = _pendingCleanupRefs[last];
        _pendingCleanupRefs.RemoveAt(last);
        return true;
    }
}
