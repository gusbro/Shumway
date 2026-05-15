# Inline Caching at Call Sites

This document specifies the inline caching mechanism used at call sites in IL-compiled code. Inline caching is one of the key optimizations that makes Tier 1 IL competitive with native code.

## Motivation

When a compiled predicate calls another predicate, the call target must be resolved at runtime in certain cases:

- **Unresolved cross-module references** that were not patched at link time.
- **Dynamic predicates** whose clauses can change via `assertz`/`retract`.
- **Indirect calls** via `call/1` with a runtime-determined goal.

Each call has cost: looking up the target in a dictionary, validating the result, then invoking it. For a hot call site (executed millions of times), this cost dominates.

**Inline caching** caches the resolved target at each call site. If the cache is valid (the predicate table hasn't changed), the cached result is used directly. If invalid, a full lookup is performed and the cache is updated.

The pattern is well-known in dynamic language runtimes (V8, .NET DLR, etc.). It's particularly effective when the call target rarely changes after the first call.

## Design

### CallSiteCache structure

Each call site has an associated `CallSiteCache`:

```csharp
internal class CallSiteCache
{
    public PredicateDelegate? CachedDelegate;
    public int CachedTableVersion;
    public FunctorId TargetFunctor;
    public int MissCount;       // for diagnostics and future poly-IC
    public long LastInvalidatedTicks;
}
```

These caches are stored in **static fields** of the dynamically-generated assembly (or in a separate per-compilation table for `DynamicMethod`).

### Engine state for invalidation

The engine maintains a version counter:

```csharp
public partial class Engine
{
    private int _predicateTableVersion;
    
    public int PredicateTableVersion => _predicateTableVersion;
    
    internal void InvalidatePredicateTable()
    {
        Interlocked.Increment(ref _predicateTableVersion);
    }
}
```

This counter is **incremented whenever any predicate table changes**:

- A new predicate is registered.
- An existing predicate's clauses change (assertz, retract, retractall on dynamic).
- A predicate is `abolish`ed.
- A new module is loaded that may shadow existing predicates.

Static predicates don't trigger invalidation once they're registered (they're immutable). Dynamic predicates trigger on every modification.

### Cache validation

A cache entry is valid if `CachedDelegate != null` and `CachedTableVersion == engine.PredicateTableVersion`.

### Call site IL pattern

For each call site that needs caching, the IL emitter generates:

```il
// Static field for the cache
.field private static class CallSiteCache _callSite_NNN

// Initialize once at type construction:
.cctor:
newobj CallSiteCache::.ctor
stsfld _callSite_NNN

// At the call site:
ldsfld _callSite_NNN
ldfld CallSiteCache::CachedDelegate
brfalse SlowPath                    // null = miss

ldsfld _callSite_NNN
ldfld CallSiteCache::CachedTableVersion
ldarg.0                              // engine
ldfld Engine::_predicateTableVersion
bne.un SlowPath                     // version mismatch = miss

// Fast path: invoke cached delegate
ldsfld _callSite_NNN
ldfld CallSiteCache::CachedDelegate
ldarg.0
ldarg.1                              // argBase, or computed new argBase
call PredicateDelegate::Invoke
br.s AfterCall

SlowPath:
ldarg.0
ldc.i4 <call_site_id>                // identifies this call site for re-cache
ldarg.1                              // argBase
call instance Engine::ResolveAndCache  // returns bool result of the call

AfterCall:
brfalse FailLabel
// Continue execution
```

### `Engine.ResolveAndCache`

The slow path is implemented in C# for clarity:

```csharp
internal bool ResolveAndCache(int callSiteId, int argBase)
{
    var cache = _callSites[callSiteId];
    var pred = _publicPredicates.TryGetValue(cache.TargetFunctor, out var p)
        ? p
        : ResolveByFunctor(cache.TargetFunctor);
    
    if (pred == null)
        return RaiseExistenceError(cache.TargetFunctor);
    
    PredicateDelegate del;
    if (pred is CompiledIlPredicate compiled)
    {
        del = compiled.Compiled;
    }
    else if (pred is InterpretedPredicate interp)
    {
        // The predicate is in Tier 0; create a delegate that invokes the interpreter.
        del = (engine, ab) => engine.RunBytecode(interp.Bytecode, ab);
    }
    else
    {
        del = pred.Invoke;
    }
    
    // Update cache (atomic write of multi-field state is ordered carefully)
    cache.CachedDelegate = del;
    cache.CachedTableVersion = _predicateTableVersion;
    
    return del(this, argBase);
}
```

### Initialization

When the IL-compiled assembly is loaded, the `.cctor` of the type initializes all `CallSiteCache` instances. The number of call sites per predicate is small (typically 0–20), so this is fast.

For DynamicMethod-based runtime compilation, the call sites are stored in a per-method `CallSiteCache[]` array, accessed via the compiled method's closure or via a method-specific helper.

## Behavior for static predicates

Static predicates are immutable. Once a call site caches a static predicate's delegate, the cache is **never invalidated** (because the version counter only changes for dynamic operations and module changes).

In practice:

- First call to a static predicate: cache miss → resolve → update cache.
- All subsequent calls: cache hit. Just one indirection (the `Invoke` call), no lookup.

This is essentially as fast as a direct method call after the first invocation.

## Behavior for dynamic predicates

Dynamic predicates can change between calls. Every `assertz`/`retract` increments the version counter, invalidating all call site caches for dynamic predicate references.

The trade-off:

- For dynamic predicates that change infrequently, the cache provides most of the benefit.
- For dynamic predicates that change on every call (rare), the cache provides no benefit but adds the version check cost.

The cost of the version check is one int comparison plus one branch. With branch prediction, this is essentially free in the cache-hit case.

## Polymorphic inline caching (Phase 2+)

The basic mechanism is **monomorphic**: each call site caches one target. If the call site's target varies (e.g., due to changing module state), the cache is invalidated and re-resolved.

For call sites that genuinely target multiple predicates (rare in Prolog, more common in OO languages), polymorphic inline caching (PIC) caches multiple targets:

```csharp
internal class PolyCallSiteCache
{
    public CacheEntry[] Entries;
    public int EntryCount;
}

internal struct CacheEntry
{
    public FunctorId TargetFunctor;
    public PredicateDelegate Delegate;
    public int TableVersion;
}
```

The fast path scans up to N entries (typically 2–4). For Shumway v1, PIC is not implemented; all call sites are monomorphic. Phase 2+ may add PIC if profiling shows it's beneficial.

## Megamorphic fallback

If a call site's cache is invalidated repeatedly (e.g., a dynamic predicate that changes every call, or `call/1` with varying goals), the cache becomes a liability rather than a benefit.

The diagnostic counter `MissCount` tracks invalidations. When it exceeds a threshold (e.g., 100), the call site is marked "megamorphic" and the IL skips caching entirely, going straight to the slow path:

```csharp
internal bool ResolveAndCache(int callSiteId, int argBase)
{
    var cache = _callSites[callSiteId];
    cache.MissCount++;
    
    if (cache.MissCount > MegamorphicThreshold)
    {
        // Don't bother caching; just resolve and invoke
        return ResolveAndInvoke(cache.TargetFunctor, argBase);
    }
    
    // Normal slow path with caching
    // ...
}
```

This adaptive behavior is a Phase 2 optimization. v1 uses simple monomorphic caching without megamorphic detection.

## Cache distribution

For a typical Shumway program with ~5,000 predicates and ~20,000 call sites:

- 5,000 CallSiteCache instances × ~24 bytes each = ~120 KB.
- 20,000 call sites × ~50 bytes of IL each = ~1 MB.

Total overhead: ~1.1 MB for the inline caching infrastructure in a large program. Acceptable.

## Thread safety

Since engines are single-threaded, individual call site updates are not subject to races. However:

- The predicate table version is read by all engines that share the same global tables. The version counter is `volatile` and updated via `Interlocked.Increment`.
- A cache entry is updated by one engine at a time, but read by potentially multiple engines (if the IL is in the global IL cache).

For the cache fields:

- `CachedDelegate` (reference): atomic read/write on .NET.
- `CachedTableVersion` (int): atomic read/write.

The read order matters: the IL reads `CachedDelegate` first, then `CachedTableVersion`. If the writer updates in reverse order (version first, then delegate), the reader either sees:

- Old delegate + old version: cache hit (valid).
- Old delegate + new version: cache miss (correct).
- New delegate + new version: cache hit (correct).
- New delegate + old version: never happens if writer updates version first.

To prevent the impossible state, the cache update follows this order:

```csharp
cache.CachedDelegate = newDelegate;
Thread.MemoryBarrier();         // ensure delegate is visible before version
cache.CachedTableVersion = newVersion;
```

Most x86/x64 CPUs have strong memory ordering, making `MemoryBarrier` mostly unnecessary, but it's correct for ARM and future architectures.

## Diagnostics

The engine exposes call site statistics for performance analysis:

```csharp
public class CallSiteStats
{
    public int CallSiteId;
    public FunctorId Target;
    public int TotalCalls;
    public int CacheHits;
    public int CacheMisses;
    public double HitRate => (double)CacheHits / TotalCalls;
}

public partial class Engine
{
    public CallSiteStats[] GetCallSiteStats();
}
```

Profiling tools can use these to identify problematic call sites.

In production, statistics collection is disabled by default (the IL doesn't update counters). It can be enabled via `EngineConfig.CollectCallSiteStats`.

## Test strategy

- **Basic hit/miss**: invoke a predicate at a call site twice; verify first is miss, second is hit.
- **Invalidation on assertz**: call a dynamic predicate, modify it, call again, verify cache miss.
- **Static predicate stability**: call a static predicate many times, verify all subsequent calls are hits.
- **Concurrent access**: two engines call the same compiled predicate concurrently; verify correctness.
- **Megamorphic fallback** (Phase 2): hammer a call site with changing targets, verify fallback after threshold.
- **Memory overhead**: load a large program, measure call site cache memory usage.
- **Cache update ordering**: stress test concurrent modifications, verify no impossible states.

## See also

- ADR-011 (IL Compiler Architecture): high-level strategy.
- `il-emission-patterns.md`: detailed IL patterns including call sites.
- V8's inline caching documentation: https://v8.dev/blog/short-builtin-calls
