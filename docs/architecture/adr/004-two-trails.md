# ADR-004: Two Separate Trails

## Status

Accepted (Phase 1).

## Context

The trail is the WAM mechanism that allows backtracking to undo changes made since the most recent choice point. Whenever the engine makes a reversible modification, it records an entry on the trail. Backtracking pops entries from the trail and reverses them.

In the classic WAM described in Aït-Kaci's book, only variable bindings are trailed. But a real Prolog implementation needs to handle additional reversible state:

1. **Variable bindings** (the common case, 95%+ of trail entries).
2. **Value changes** on heap cells (rare but possible in some operations).
3. **Attributed variable modifications** (future, when attvars are added).
4. **Backtrackable global variables** (`b_setval/2` in SWI, optional feature).

Several encoding strategies exist:

- **Single trail with type-tagged entries**: every entry carries a type field; unwind dispatches on type.
- **Single trail with implicit type for the common case**: the most frequent type (binding) uses a smaller encoding; rare types use multi-word entries.
- **Two separate trails**: one for the common case (bindings), one for rare cases. Each is optimized for its workload.

The choice has performance implications because:

- The trail is a hot data structure in cut-heavy code (which is one of Shumway's target workloads).
- Trail compaction after cuts processes potentially many entries.
- Memory locality during unwind matters for cache performance.

The user's target workload includes intensive use of cut and backtracking. This shifts the balance toward maximizing performance of the common case (binding trail) at the cost of slightly more complex code for the rare case.

## Decision

Shumway uses **two separate trails**:

### BindingTrail: `int[]`

- One entry per binding of an unbound variable.
- Each entry is a single `int` containing the heap index of the bound variable.
- Hot path: 4 bytes per entry, no dispatch, no allocation.
- Unwind: read the int, write `Cell.UnboundVar(idx)` to `_heap[idx]`.

```csharp
private int[] _bindingTrail;
private int _bindingTrailTop;

public void TrailBinding(int heapIdx)
{
    if (heapIdx >= _hb) return;  // HB check: skip if "young"
    EnsureBindingTrailCapacity(1);
    _bindingTrail[_bindingTrailTop++] = heapIdx;
}

public void UnwindBindingTrail(int targetTop)
{
    while (_bindingTrailTop > targetTop)
    {
        int idx = _bindingTrail[--_bindingTrailTop];
        _heap[idx] = Cell.UnboundVar(idx);
    }
}
```

### ExtraTrail: `ExtraTrailEntry[]`

- One entry per non-binding reversible change.
- Each entry is a struct with type, heap index, old value, and a marker into BindingTrail (to preserve interleaving order during unwind).
- Used for value changes (replacing an already-bound value with another), and (future) attvar modifications, mutable globals, etc.

```csharp
public struct ExtraTrailEntry
{
    public TrailType Type;       // discriminator
    public int HeapIdx;
    public Cell OldValue;
    public int BindingTrailMarker;  // position in _bindingTrail at the time this entry was added
}

public enum TrailType : byte
{
    ValueChange = 1,
    AttrAdd = 16,        // reserved for attvars
    AttrModify = 17,     // reserved for attvars
    AttrRemove = 18,     // reserved for attvars
    MutableSet = 32,     // reserved for b_setval/2 if implemented
    // additional types reserved per category
}

private ExtraTrailEntry[] _extraTrail;
private int _extraTrailTop;
```

### Choice points snapshot both tops

The choice point structure includes:

```csharp
struct ChoicePoint
{
    public int HeapTop;
    public int BindingTrailTop;
    public int ExtraTrailTop;
    public int Hb;
    // ... other fields (saved registers, CE, CP, etc.)
}
```

On backtracking, the engine restores both trail tops:

```csharp
public void Backtrack(ChoicePoint cp)
{
    UnwindTrails(cp.BindingTrailTop, cp.ExtraTrailTop);
    _heapTop = cp.HeapTop;
    _hb = cp.Hb;
    // ... restore registers, etc.
}

public void UnwindTrails(int bindingTarget, int extraTarget)
{
    // Process extra entries in reverse order, interleaved with bindings
    while (_extraTrailTop > extraTarget)
    {
        ref var entry = ref _extraTrail[_extraTrailTop - 1];
        // First, unwind bindings up to the marker
        while (_bindingTrailTop > entry.BindingTrailMarker)
        {
            int idx = _bindingTrail[--_bindingTrailTop];
            _heap[idx] = Cell.UnboundVar(idx);
        }
        // Then process the extra entry itself
        ProcessExtraUnwind(entry);
        _extraTrailTop--;
    }
    // Remaining bindings
    while (_bindingTrailTop > bindingTarget)
    {
        int idx = _bindingTrail[--_bindingTrailTop];
        _heap[idx] = Cell.UnboundVar(idx);
    }
}
```

### HB check filters unnecessary entries

The HB (Heap Boundary) is the heap top at the time of the most recent choice point. Variables created after this point are "young" and would be discarded when the heap is truncated on backtrack. Trailing their bindings is wasted work.

```csharp
public void Bind(int heapIdx, Cell value)
{
    _heap[heapIdx] = value;
    if (heapIdx < _hb)
        TrailBinding(heapIdx);
}
```

### Young-to-old binding rule

When unifying two unbound variables, always bind the younger (higher heap index) to the older (lower heap index). This ensures that the bound variable doesn't reference a region of heap that will be truncated by backtracking.

```csharp
public void UnifyVariables(int idx1, int idx2)
{
    if (idx1 > idx2)
        (idx1, idx2) = (idx2, idx1);  // ensure idx1 is older
    _heap[idx2] = Cell.Ref(idx1);     // bind younger to older
    if (idx2 < _hb)
        TrailBinding(idx2);
}
```

### Trail compaction after cut

When a cut eliminates choice points, entries in the trail beyond the new top that bind variables created after the parent's heap top are no longer needed. The Warren trail-compaction algorithm:

```csharp
public void CompactBindingTrail(int parentTrailTop, int parentHeapTop)
{
    int writeIdx = parentTrailTop;
    for (int readIdx = parentTrailTop; readIdx < _bindingTrailTop; readIdx++)
    {
        int idx = _bindingTrail[readIdx];
        if (idx < parentHeapTop)
            _bindingTrail[writeIdx++] = idx;  // still needed
    }
    _bindingTrailTop = writeIdx;
}
```

Cut applies the compaction to both BindingTrail and ExtraTrail. This is important for cut-heavy workloads to keep the trail bounded.

### What is NOT trailed

The following are **not** reversible by backtracking and are not trailed:

- `assertz/1`, `asserta/1`, `retract/1`: modifications to the dynamic predicate database are permanent. This is the standard Prolog semantics (and what GNU Prolog and SWI-Prolog do).
- Modifications to the atom table, functor table, global tables.
- Auxiliary table growth (bigints, strings, foreign objects, floats): these grow during a query and are cleaned up at query end, not by trail.
- I/O side effects (`write`, `read`).
- `set_prolog_flag/2`.
- `nb_setval/2` (non-backtrackable globals, if implemented).

## Alternatives Considered

### Single trail with type-tagged entries (16 bytes per entry)

**Considered, rejected.** Every entry would carry a type discriminator and have padding for the largest case. For 95%+ of entries that are simple bindings, this is 4× the memory of the optimal encoding. Trail compaction and unwind would also be slower because of the larger working set.

### Single trail with implicit type for binding (8 bytes per entry)

**Considered, rejected.** This was an intermediate option: a `long[]` trail where type 0 (binding) uses just the heap index in the low bits, and rare types use a discriminator in the high bits. Better than the 16-byte option, but still 2× the memory of `int[]` for the binding case, and the dispatch branch (even if well predicted) costs ILP.

For workloads with cut compaction running frequently, the cost of copying twice the data during compaction is real. The user's emphasis on cut-heavy workloads tilted the decision toward the two-trail design.

### Three or more specialized trails

**Rejected.** Splitting further (e.g., one trail per type of extra entry) would complicate the data structure without significant benefit. ValueChange and future attvar operations are rare enough that a single ExtraTrail is sufficient.

### No trail compaction

**Rejected.** Without compaction, the trail grows monotonically within a query. In cut-heavy code, this means trail entries from cut branches sit in memory until the end of the query, and unwinds process them even though they refer to long-dead heap regions. Compaction is essential for the target workloads.

## Consequences

### Positive

- **Hot path is minimal**: writing a binding entry is 1 store of 4 bytes, no dispatch, no allocation.
- **Unwind is tight loops**: the BindingTrail unwind is a simple loop with no branch on type. The JIT can vectorize or unroll it.
- **Cache density**: 8 binding entries per 64-byte cache line, vs. 4 or 2 for larger encodings.
- **Compaction is fast**: scanning an `int[]` with simple comparison and copy is one of the most cache-friendly patterns possible.
- **Extensibility for the future**: when attvars are added, they fit into ExtraTrail without disturbing the binding hot path.

### Negative

- **Two separate data structures**: more state in the engine, more state in choice points (two tops to snapshot).
- **Interleaving complexity in unwind**: when both trails have entries, the unwind must process them in interleaved order. This is non-trivial logic.
- **The BindingTrailMarker in ExtraTrail adds 4 bytes per extra entry**: but extras are rare, so the overhead is negligible.

### Mitigations

- **Encapsulate the trail logic in the engine**. External code never directly manipulates trails.
- **Document the unwind algorithm clearly**. The interleaving logic must be correct or backtracking misbehaves.

## Implementation Notes

### Initial capacity and growth

- `_bindingTrail` starts at 1024 entries (4 KB).
- `_extraTrail` starts at 64 entries (~1 KB).
- Both grow geometrically (×2) when full.
- Maximum sizes can be configured via `EngineConfig`.

### Trail and engine.Reset

`Reset()` truncates both trails to size 0. This is part of clearing execution state.

### Trail in serialization

When an engine is paused (e.g., between queries or for diagnostics), the trail must remain consistent with the heap. Snapshots of engine state for debugging include both trails.

### Thread safety

The trails are part of engine state, which is single-threaded. No locks.

### Performance characteristics

For a typical Prolog program with backtracking:

- **Per binding**: 1 int write to BindingTrail. ~1 ns on modern hardware.
- **Per unwind step**: 1 int read, 1 cell write to heap. ~2 ns.
- **Per compaction entry**: 1 read, 1 compare, possibly 1 write. ~1 ns.

For 1 million bindings followed by full unwind: total trail work ~5 ms. The dominant cost is the heap cell writes (which involve cache traffic), not the trail operations themselves.

## Test Strategy

- **Round-trip binding and unwind**: create N bindings, backtrack, verify all variables are unbound.
- **Mixed bindings and value changes**: interleave both types, verify unwind restores state correctly in order.
- **Cut compaction**: bind variables both below and above the parent's heap top, perform cut, verify compaction keeps only the necessary entries.
- **HB check**: bind a young variable (above HB), verify no trail entry is added.
- **Young-to-old rule**: unify two unbound variables, verify the younger one was bound, not the older.
- **Stress test**: deep recursion with backtracking, verify trail behavior is correct and memory doesn't blow up.

## Related ADRs

- ADR-002 (Cell Layout): trail entries refer to heap indices that point to cells.
- ADR-005 (Stack Layout): choice points snapshot trail tops.
- ADR-007 (Indexing): indexing affects how many choice points are created.

## Related Design Docs

- `design/wam-instruction-set.md`: specifies which instructions can cause bindings and value changes (and thus generate trail entries).
