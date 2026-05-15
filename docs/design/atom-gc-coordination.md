# Atom GC Coordination

This document specifies how the custom atom GC coordinates with multiple engines running concurrently in the same process. It complements ADR-003 by providing the synchronization protocol, safe-point mechanism, and edge cases.

## Context

The atom GC is a global operation that scans engine state to determine which atoms are still reachable. For correctness:

- While the GC scans an engine's heap, that engine must not modify its heap (no concurrent writes).
- All engines must reach a "safe point" before the GC proceeds.
- After the GC completes, engines resume normally.

This is conceptually similar to .NET's own GC, which performs stop-the-world collections (with concurrent variants). Shumway's atom GC is simpler because it only collects atoms (not the heap), but the synchronization principle is the same.

## Safe points

A safe point is a moment in the engine's execution where:

1. The engine's state (heap, stack, registers) is fully consistent.
2. No in-progress operation needs heap access.
3. The engine can wait without holding any lock.

In Shumway, safe points occur:

- **Between WAM instructions** in the bytecode dispatch loop, every N instructions (default: 1024).
- **At the start of each builtin call**.
- **Between query iterations** (after returning a solution to the caller).
- **In compiled IL code**, at points marked by the IL emitter as safe (typically the same intervals as the interpreter).

The engine has a `_safePointCounter` that decrements on each instruction. When it reaches zero:

```csharp
public void CheckSafePoint()
{
    if (--_safePointCounter <= 0)
    {
        _safePointCounter = _config.SafePointInstructionInterval;
        if (_safePointRequested)
        {
            EnterSafePoint();
        }
        if (_cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException();
        }
    }
}
```

The check is a few instructions and is very fast in the common case (no safe point requested).

## GC request protocol

When the atom GC needs to run, it requests safe points from all engines and waits:

```csharp
internal static class AtomGcCoordinator
{
    private static readonly object _gcLock = new();
    private static readonly List<WeakReference<Engine>> _registeredEngines = new();
    
    public static void RegisterEngine(Engine engine)
    {
        lock (_gcLock)
            _registeredEngines.Add(new WeakReference<Engine>(engine));
    }
    
    public static void RunGc()
    {
        lock (_gcLock)
        {
            // Determine which engines are still alive
            var liveEngines = new List<Engine>();
            for (int i = _registeredEngines.Count - 1; i >= 0; i--)
            {
                if (_registeredEngines[i].TryGetTarget(out var e))
                    liveEngines.Add(e);
                else
                    _registeredEngines.RemoveAt(i);
            }
            
            if (liveEngines.Count == 0)
            {
                // No engines, nothing to scan from
                return;
            }
            
            // Request safe points
            var safePointReached = new CountdownEvent(liveEngines.Count);
            foreach (var e in liveEngines)
                e.RequestSafePoint(safePointReached);
            
            // Wait for all engines to reach safe points
            // Timeout in case of bugs to avoid hanging indefinitely
            if (!safePointReached.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new InvalidOperationException("Atom GC timeout: not all engines reached safe point.");
            }
            
            // All engines are at safe points; run the GC
            try
            {
                AtomTable.Mark(liveEngines);
                AtomTable.Sweep();
            }
            finally
            {
                // Release all engines
                foreach (var e in liveEngines)
                    e.ResumeFromSafePoint();
            }
        }
    }
}
```

## Engine safe-point handling

When an engine receives a safe point request:

```csharp
public partial class Engine
{
    private volatile bool _safePointRequested;
    private CountdownEvent? _safePointReachedSignal;
    private ManualResetEventSlim? _resumeSignal;
    private readonly object _safePointLock = new();
    
    public void RequestSafePoint(CountdownEvent reachedSignal)
    {
        lock (_safePointLock)
        {
            _safePointReachedSignal = reachedSignal;
            _resumeSignal = new ManualResetEventSlim();
            _safePointRequested = true;
        }
    }
    
    private void EnterSafePoint()
    {
        CountdownEvent? signal;
        ManualResetEventSlim? resume;
        
        lock (_safePointLock)
        {
            signal = _safePointReachedSignal;
            resume = _resumeSignal;
            _safePointRequested = false;
        }
        
        if (signal != null && resume != null)
        {
            // Signal that this engine has reached a safe point
            signal.Signal();
            // Wait for the GC to complete
            resume.Wait();
            resume.Dispose();
            
            lock (_safePointLock)
            {
                _safePointReachedSignal = null;
                _resumeSignal = null;
            }
        }
    }
    
    public void ResumeFromSafePoint()
    {
        lock (_safePointLock)
        {
            _resumeSignal?.Set();
        }
    }
}
```

## Idle engines

If an engine is idle (no query in progress), it doesn't poll for safe points. Two cases:

**Case 1**: idle and the user is not actively using it. The GC scans its state directly (the engine is not modifying anything).

**Case 2**: idle but about to start a query. The query start path must check for safe-point requests before executing.

```csharp
public Query Query(string text)
{
    if (_safePointRequested)
        EnterSafePoint();
    
    return new Query(this, text);
}
```

This ensures that an engine just transitioning from idle to active waits if a GC is in progress.

## Single-engine case

For applications using a single engine, the coordination is trivial: the GC runs in the same thread as the engine (between queries), no synchronization needed.

```csharp
public void TriggerGcIfNeeded()
{
    // Single-engine optimization: if we're the only engine, run inline
    if (AtomGcCoordinator.IsSoloEngine(this))
    {
        AtomTable.Mark(this);
        AtomTable.Sweep();
    }
    else
    {
        AtomGcCoordinator.RunGc();
    }
}
```

## When GC is triggered

The GC runs at three trigger points:

### 1. Manual: `engine.RunAtomGc()`

The embedding API exposes a method to trigger GC explicitly. Useful for long-running servers that want to clean up after a known idle moment.

### 2. Threshold: when the Transient atom table grows past a limit

```csharp
public int TransientCount => _transientById.Count;

internal void CheckTransientThreshold()
{
    if (_transientById.Count > _config.AtomGcThreshold)
    {
        AtomGcCoordinator.RunGc();
    }
}
```

The threshold is checked at safe points. Default: 100,000 transient atoms.

### 3. Between queries

The engine optionally runs GC between queries when:

- The number of newly-created transients during the last query exceeds a threshold.
- A configurable interval has passed since the last GC.

The configuration:

```csharp
public class EngineConfig
{
    // ...
    public int AtomGcThreshold { get; set; } = 100_000;
    public TimeSpan AtomGcInterval { get; set; } = TimeSpan.FromMinutes(5);
    public bool AtomGcAfterEachQuery { get; set; } = false;
}
```

## Marking phase details

The GC's mark phase walks each engine's state:

```csharp
public static class AtomTable
{
    public static void Mark(IReadOnlyList<Engine> engines)
    {
        var marked = new HashSet<int>();
        
        // 1. Mark pre-registered atoms (always alive)
        for (int i = 0; i < PreRegisteredCount; i++)
            marked.Add(i);
        
        // 2. Scan all engines
        foreach (var engine in engines)
        {
            // Heap
            for (int i = 0; i < engine.HeapTop; i++)
            {
                Cell c = engine.Heap[i];
                if (c.Tag == Tag.Atom)
                    marked.Add(c.AsAtomId);
                else if (c.Tag == Tag.Functor)
                {
                    var (atomId, _) = FunctorTable.Lookup(c.AsFunctorId);
                    marked.Add(atomId);
                }
            }
            
            // Stack
            for (int i = 0; i < engine.StackTop; i++)
            {
                Cell c = engine.Stack[i];
                if (c.Tag == Tag.Atom)
                    marked.Add(c.AsAtomId);
                else if (c.Tag == Tag.Functor)
                {
                    var (atomId, _) = FunctorTable.Lookup(c.AsFunctorId);
                    marked.Add(atomId);
                }
            }
            
            // Registers
            for (int i = 0; i < engine.MaxRegistersUsed; i++)
            {
                Cell c = engine.Registers[i];
                if (c.Tag == Tag.Atom)
                    marked.Add(c.AsAtomId);
                else if (c.Tag == Tag.Functor)
                {
                    var (atomId, _) = FunctorTable.Lookup(c.AsFunctorId);
                    marked.Add(atomId);
                }
            }
            
            // Predicate metadata (atom references in bytecode)
            foreach (var pred in engine.AllPredicates)
                foreach (var atomId in pred.AtomReferences)
                    marked.Add(atomId);
        }
        
        // 3. Foreign holds (atoms retained by C# code)
        var newForeignRefs = new List<WeakReference<Atom>>();
        foreach (var weak in _foreignWeakRefs)
        {
            if (weak.TryGetTarget(out var atom))
            {
                marked.Add(atom.Id);
                newForeignRefs.Add(weak);
            }
        }
        _foreignWeakRefs = newForeignRefs;
        
        _lastMarkedSet = marked;
    }
}
```

## Sweep phase details

```csharp
public static void Sweep()
{
    var marked = _lastMarkedSet;
    
    var toRemove = new List<int>();
    foreach (var (id, atom) in _transient)
    {
        if (!marked.Contains(id))
        {
            // Not reachable from any engine, check if C# holds it
            // (already determined during Mark phase by checking _foreignWeakRefs)
            // If marked, it's in marked set; otherwise, move to TransientWeak.
            
            // Check if the foreign weak refs still hold this atom
            bool stillForeignHeld = _foreignWeakRefs.Any(w => 
                w.TryGetTarget(out var a) && a.Id == id);
            
            if (stillForeignHeld)
            {
                _transientWeak[id] = new WeakReference<Atom>(atom);
                toRemove.Add(id);
            }
            else
            {
                _byName.TryRemove(atom.Name, out _);
                toRemove.Add(id);
            }
        }
    }
    
    foreach (var id in toRemove)
        _transient.Remove(id);
    
    // Process TransientWeak: clean dead, promote re-used
    var weakToRemove = new List<int>();
    var toPromote = new List<int>();
    foreach (var (id, weak) in _transientWeak)
    {
        if (!weak.TryGetTarget(out var atom))
        {
            weakToRemove.Add(id);
            // _byName entry was already removed when atom became unreachable
        }
        else if (marked.Contains(id))
        {
            toPromote.Add(id);
            _transient[id] = atom;
            weakToRemove.Add(id);
        }
        // else: still only held by C#, keep in TransientWeak
    }
    
    foreach (var id in weakToRemove)
        _transientWeak.Remove(id);
}
```

## Interaction with concurrent operations

### Atom creation during GC

If an engine is at a safe point, it can't create atoms. So no new atoms appear during the GC scan.

The atom table itself uses `ConcurrentDictionary` for `_byName`, so concurrent reads (by engines not at safe points but not yet at safe points either—wait, all engines must be at safe points before GC starts) are safe. In practice, by the time the GC mutates the table, all engines are blocked.

### Atom intern from C# (embedding API)

`PrologEngine.MakeAtom(name)` can be called by C# code at any time. If a GC is in progress, the call blocks until GC completes (the call enters its own "safe point" wait).

Actually, a cleaner approach: the embedding API's atom creation goes through the engine's safe-point check. If a GC is in progress, the call waits. Otherwise, it proceeds.

### Engine creation during GC

A new engine being created during a GC must wait for the GC to complete before being registered. Engine registration:

```csharp
public PrologEngine(EngineConfig config)
{
    // Initialize state
    // ...
    
    // Register with the coordinator (may block if a GC is in progress)
    AtomGcCoordinator.RegisterEngine(this);
}
```

The coordinator's `RegisterEngine` uses the same lock as the GC, so it queues behind any in-progress GC.

### Engine disposal during GC

If an engine is disposed (its lifetime ends) during a GC, the GC may already have a reference to it. The WeakReference cleanup happens at the start of the next GC, so disposed engines naturally drop out.

## Timeout and bug detection

If a GC times out (some engine doesn't reach a safe point within the timeout), the GC throws an exception. This is a bug indicator: either an infinite loop in the interpreter, or a missing safe-point check in compiled IL.

In production, the timeout should be high enough to handle slow safe-point arrival in legitimate scenarios (e.g., a long-running builtin), but short enough to detect bugs. Default: 30 seconds.

## Configurability

```csharp
public class EngineConfig
{
    // Safe point check frequency
    public int SafePointInstructionInterval { get; set; } = 1024;
    
    // GC threshold
    public int AtomGcThreshold { get; set; } = 100_000;
    
    // GC interval
    public TimeSpan AtomGcInterval { get; set; } = TimeSpan.FromMinutes(5);
    
    // Run GC after each query (for diagnostic purposes)
    public bool AtomGcAfterEachQuery { get; set; } = false;
    
    // GC timeout
    public TimeSpan AtomGcTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
```

## Test strategy

- **Single engine, basic GC**: create transient atoms, drop references, run GC, verify cleanup.
- **Multi-engine, coordinated GC**: two engines running concurrent queries, trigger GC, verify both reach safe points and resume correctly.
- **Foreign hold**: create atom via embedding API, retain in C#, run GC, verify atom is not removed.
- **Concurrent atom creation**: stress test atom interning from multiple engines while GC is requested.
- **GC during engine creation**: trigger GC while creating an engine; verify ordering.
- **GC during engine disposal**: dispose an engine; verify it's correctly removed from the registered list at next GC.
- **Timeout detection**: simulate a non-responsive engine; verify GC raises a clear exception.
- **Bench**: measure GC overhead per atom collected; aim for under 1 microsecond per atom on average.

## See also

- ADR-003 (Atom Three-Tier System): high-level rationale.
- ADR-001 (Engines and Global Tables): single-thread-per-engine model.
