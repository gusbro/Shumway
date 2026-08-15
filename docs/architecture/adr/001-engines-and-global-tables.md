# ADR-001: Engines and Global Tables

## Status

Accepted ([Phase 1](../../history/phase-1-closure.md)).

## Context

Shumway is intended for embedding in .NET applications, where typical scenarios include:

- Server applications handling multiple concurrent requests, each requiring isolated Prolog execution state.
- Long-running services that load Prolog rules once and execute queries many times.
- Applications with multiple independent rule sets that should not share state.

The runtime needs to define how state is partitioned: what is shared across the entire process, and what is isolated per execution context.

Two extremes are possible:

1. **Single global engine**: simple model but requires synchronization between concurrent users; one user's state pollutes another's.
2. **Fully isolated engines**: each engine duplicates everything (atom table, code, etc.), wasting memory and preventing optimizations like cross-engine code sharing.

Neither extreme fits the target use cases. A middle ground is required.

## Decision

Shumway uses a **multi-engine model** with the following partitioning:

### Engine: the unit of execution

The `Engine` is the primary runtime object. Each engine encapsulates:

- The heap (`Cell[]`).
- The stack (`Cell[]`) — environments and choice points interleaved.
- Two trails: `BindingTrail` (`int[]`) and `ExtraTrail` (`struct[]`).
- Registers (X1..Xn and A1..An).
- The predicate tables visible to this engine (module-local predicates).
- Per-engine auxiliary tables (bigints, strings, foreign objects).
- The TransientWeak atom hold list (for atoms retained from C# code).
- Engine flags (`double_quotes`, `unknown`, etc.).

Multiple engines can coexist in the same process. Each one is independent: heap of engine A is never accessed from engine B; bindings in A do not affect B.

### Engines are single-threaded internally

No locks are placed within engine state. The hot paths of the interpreter, unification, trail management, etc., assume exclusive access.

**The caller is responsible** for ensuring that only one thread accesses a given engine at any moment.

### Engines are thread-agile

The engine state does not use `[ThreadStatic]` or any per-thread mechanism. The same engine can be used from different threads, **as long as access is serialized**.

This enables async scenarios:

```csharp
async Task ProcessAsync()
{
    using var pooled = pool.Rent();
    var engine = pooled.Engine;       // possibly on thread A
    var query = engine.Query(...);
    await SomeOtherWork();             // possibly resumes on thread B
    foreach (var sol in query.Solutions())  // continues on thread B, still valid
    {
        // ...
    }
}
```

This works because no per-thread state is captured by the engine.

### Shared global tables

Some state is genuinely global across the process:

| Table | Purpose | Thread safety |
|-------|---------|---------------|
| Atom table | Global atom ids (atoms are immutable identifiers) | `ConcurrentDictionary` |
| Functor table | Global functor ids (functor = atom + arity) | `ConcurrentDictionary` |
| Compiled IL cache | Predicate code keyed by bytecode hash | `ConcurrentDictionary` |

These are shared because:

- Atoms and functors are conceptually identifiers. Two engines using the atom `foo` should agree on its id.
- Compiled code for a static predicate is engine-agnostic (it takes `Engine` as a parameter), so sharing it across engines avoids duplicate compilation.

Global tables use thread-safe data structures because multiple engines may modify them concurrently.

### Predicate tables: per-engine but referencing global resources

Each engine has its own predicate tables:

- **Module-local predicates**: visible only within their module in this engine.
- **Public predicates visible to this engine**: a flat namespace of predicates exported by any loaded module.

The predicate objects themselves can reference globally cached compiled code (when the same module is loaded in multiple engines).

## Alternatives Considered

### Single global engine with thread-local query state

**Rejected.** This was a tempting model: one engine, multiple threads, each thread has its own "query context" (registers, heap region, etc.). The issue is that backtracking and binding interact across the entire heap. Partitioning the heap by thread would require complex bookkeeping and would invalidate the cell layout assumptions. The complexity is not worth the trade-off.

### Fully isolated engines (no shared global state)

**Rejected.** Without a global atom table, two engines using the same atom `foo` would have different ids for it. Comparison across engines would require name lookup. Cross-engine code sharing would be impossible. Memory usage would balloon with the number of engines (each duplicating atom and functor tables).

### Per-thread engines (thread affinity)

**Rejected.** Modern .NET applications use `async`/`await` heavily, with thread hopping between awaits. Engines bound to a thread would prevent this pattern. Furthermore, server scenarios using engine pools require engines to be reusable across threads.

### Lock-based shared engine

**Rejected.** Adding internal locking to the engine would add overhead to every hot-path operation (unification, deref, trail). For a system targeting single-thread-per-engine semantics, this overhead is pure waste. The right place for synchronization is the application-level pool, not the engine itself.

## Consequences

### Positive

- **Scalability via pooling.** Server applications can maintain a pool of engines, renting one per request. Concurrency scales linearly with the number of engines.
- **Async-friendly.** Engines work seamlessly with `async`/`await` because they don't pin to threads.
- **Cross-engine code sharing.** A program loaded in multiple engines compiles to IL once. Subsequent engines reuse the cached code.
- **Hot paths stay fast.** No locks, no thread-local lookups inside the engine. Unification, deref, and trail operations are simple memory operations.
- **Atoms are comparable across engines** (same id everywhere).

### Negative

- **The caller bears responsibility.** Using the same engine from two threads concurrently is undefined behavior. The engine doesn't detect this in release builds.
- **Cross-engine term passing requires copying.** A term in engine A's heap cannot be directly used in engine B (the heap indices differ). The embedding API exposes explicit copy operations.

### Mitigations

- **Debug builds detect concurrent access.** In debug builds, the engine records the thread id of the current operation and throws if a concurrent access is detected. Release builds skip this check for zero overhead.
- **Engine pool abstraction.** The embedding API provides `EnginePool` to make the pattern of "rent engine, use exclusively, return" trivial for callers.

## Implementation Notes

### Engine state initialization

A new engine starts with:

- Empty heap (allocated with initial capacity, grows on demand).
- Empty stack.
- Empty trails.
- Predicate table contains only built-ins (registered at construction).
- Auxiliary tables empty.
- Module list empty (modules are added by `Consult` or `LoadBundle`).

### Engine.Reset

The engine exposes `Reset()` that clears execution state (heap, stack, trails, registers, auxiliary tables) while preserving loaded predicates and modules. This is useful for engine pool implementations that want to recycle engines between requests.

### Global table lifecycle

Global tables are static, lazily initialized. They live for the lifetime of the process. There is no "unload module" operation that removes atoms from the global table (atoms are immutable; their lifecycle is managed by the atom GC, not by module loading).

### Capacity and growth

- Heap, stack, and trails grow geometrically (×2 when full).
- Maximum sizes are configurable via `ActivationConfig`. A value of 0 means unlimited (default).
- Out-of-memory at the engine level throws a Prolog `resource_error/1` exception.

## Test Strategy

- Construct multiple engines in parallel. Verify atoms used in both have the same id.
- Use the same engine from two threads in a debug build. Verify the concurrent-access exception is thrown.
- Allocate a heap large enough to trigger growth. Verify state is preserved across growth.
- Verify `Reset()` preserves loaded modules but clears execution state.
- Cross-engine term copy: build a complex term in engine A, copy to engine B, verify structural equality.

## Related ADRs

- ADR-002 (Cell Layout): defines the blittable cell that lives in engine heaps.
- ADR-003 (Atom Three-Tier System): explains how the global atom table interacts with per-engine state.
- ADR-008 (Module Visibility): explains how per-engine predicate tables organize public vs local.
- ADR-010 (Embedding API): explains `EnginePool`, async APIs, and how clients interact with engines.
