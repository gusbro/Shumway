# ADR-003: Atom Three-Tier System

## Status

Accepted (Phase 1).

## Context

Atoms are fundamental to Prolog. They appear as functor names, constants, and identifiers. A typical Prolog program may use thousands of distinct atoms; long-running programs may create many more dynamically (via `atom_concat`, `read_term`, conversions from input data, etc.).

The atom system has three concerns that pull in different directions:

1. **Identity and comparison performance**. Atoms must compare in O(1). The standard solution is to intern atoms into a table and compare by integer id.

2. **Memory management**. Dynamically created atoms can pile up indefinitely. GNU Prolog is famously known to be unsuitable for long-running production systems precisely because its closed atom table fills up and is never garbage-collected.

3. **Interop with .NET**. C# code may obtain a reference to an atom (via the embedding API) and retain it. As long as C# holds the atom, it must remain valid; if C# lets go and no engine references it, it should be collectable.

Several models exist:

- **No GC** (GNU Prolog): atoms are permanent. Simple but unusable for long-running systems.
- **Reference counting** (some implementations): tracks references on every operation. Costly in the hot path of `put_atom` etc.
- **Mark-and-sweep** (SWI-Prolog): periodic GC over the atom table. Better than RC for hot path but requires careful integration with engine state.

A subtle problem arises when implementing mark-and-sweep with a strongly-referenced atom table:

- If the table keeps strong references to atoms, the .NET GC cannot tell from a `WeakReference` whether C# is also holding the atom (the table's strong reference keeps it alive regardless).
- This breaks the natural mechanism of using `WeakReference` to detect "C# is the only holder".

A solution is required that lets the table participate in GC while still allowing the .NET GC to handle the C#-retention case naturally.

## Decision

Shumway uses a **three-tier atom table** with explicit movement between tiers driven by a custom atom GC.

### The three tiers

1. **Permanent**: atoms originating from source code literals, builtin names, or explicit promotion. Strong references in `_permanentById` (a `Dictionary<int, Atom>`, mirrored by a copy-on-write `_permanentByIdArray` for lock-free `GetById`). **Never collected** by the custom GC. Two sub-categories:
   - Atoms from source code (created during `Consult`).
   - Atoms promoted from Transient (e.g., when a transient atom appears in a source file loaded later).

2. **Transient**: atoms created dynamically at runtime (via `atom_concat`, `read_term`, etc.). Strong references in `_transientById` (a `Dictionary<int, Atom>`). **Kept alive by the table itself** during normal execution. The custom atom GC moves them to TransientWeak or removes them entirely.

3. **TransientWeak**: atoms that have no strong reference from any engine but are still held by C# via the embedding API. The table holds only a `WeakReference<Atom>`. The .NET GC handles the actual collection: if C# lets go, the atom is collected; if reused by an engine, the atom is promoted back to Transient.

### Lookup tables

- `_byName: ConcurrentDictionary<string, WeakReference<Atom>>`: maps atom name to a **weak** reference to the atom. Weak so that demoting an atom to TransientWeak truly releases every table-side strong reference — a strong `_byName` would make the weak tier pointless. Used by `Intern(string)`.
- `_permanentById: Dictionary<int, Atom>`: strong references that keep Permanent atoms alive.
- `_transientById: Dictionary<int, Atom>`: strong references for Transient atoms.
- `_transientWeak: Dictionary<int, TransientWeakEntry>`: for TransientWeak atoms — each entry bundles the `WeakReference<Atom>` with a cached name (the name is needed after the atom is gone).
- `_foreignWeakRefs: List<WeakReference<Atom>>`: weak references corresponding to atoms exposed to C# via the embedding API. Used by the GC to detect C# retention.

### Atom GC algorithm

The GC runs at safe points (between queries, or when the transient table grows past a threshold). Stop-the-world for engines is brief (milliseconds).

**Phase 1 — Mark**: scan reachable engine state for atom ids.

```
marked = empty set
for each engine in all engines:
    for each cell in engine.heap, engine.stack, engine.registers:
        if cell.tag == ATOM:
            marked.add(cell.AsAtomId)
    for each cell in engine's trails (if applicable):
        if cell.tag == ATOM: marked.add(cell.AsAtomId)
for each predicate in all loaded predicates:
    for each atom id in predicate's atom references:
        marked.add(id)
```

**Phase 2 — Compute foreignAlive**: walk `_foreignWeakRefs`, retain those whose target is still alive.

```
foreignAlive = empty set
newForeignRefs = empty list
for each weak in _foreignWeakRefs:
    if weak.TryGetTarget(out atom):
        foreignAlive.add(atom.Id)
        newForeignRefs.add(weak)
_foreignWeakRefs = newForeignRefs   // compact
```

**Phase 3 — Process Transient table**: for each transient, decide between three outcomes.

```
for each (id, atom) in _transient:
    if marked.contains(id):
        # still in use, keep in Transient
        pass
    elif foreignAlive.contains(id):
        # not used by any engine, but C# holds it; move to TransientWeak
        _transient.remove(id)
        _transientWeak[id] = new WeakReference<Atom>(atom)
    else:
        # nobody uses it, remove (drop the strong reference)
        _transient.remove(id)
        _byName.remove(atom.Name)
        # .NET GC will eventually collect the Atom object
```

**Phase 4 — Process TransientWeak table**: clean up dead weak refs, promote back to Transient if reused.

```
for each (id, weak) in _transientWeak:
    if not weak.TryGetTarget(out atom):
        # C# also released, remove the entry
        _transientWeak.remove(id)
        _byName.remove(<atom's name; needs caching since atom is gone>)
    elif marked.contains(id):
        # an engine used it again; promote back to Transient
        _transient[id] = atom
        _transientWeak.remove(id)
    else:
        # still only held by C#, keep in TransientWeak
        pass
```

### Atom ids are stable

Once an atom is assigned an id, that id never changes, even as the atom moves between tiers. Cells in heaps that contain `id=42` remain valid as long as the atom with id 42 is alive (in any tier).

### Hot path is trivial

The hot path of `put_atom` is just:

```csharp
heap[idx] = new Cell((long)Tag.Atom << 60 | (uint)atomId);
```

No reference counting. No anchor list updates. No bookkeeping. The atom remains alive because the Transient table holds it.

### Promotion to Permanent

When a transient atom appears in a source file being loaded, it is promoted to Permanent:

```csharp
public static Atom Intern(string name, bool permanent)
{
    var atom = LookupOrCreate(name);
    if (permanent && !atom.IsPermanent)
    {
        lock (_lock)
        {
            if (!atom.IsPermanent)
            {
                _permanentAnchors.Add(atom);
                atom.IsPermanent = true;
                if (_transient.Remove(atom.Id))
                {
                    // moved from Transient to Permanent
                }
                else if (_transientWeak.Remove(atom.Id))
                {
                    // moved from TransientWeak to Permanent
                }
            }
        }
    }
    return atom;
}
```

### Foreign exposure registers a weak ref

When the embedding API returns an `Atom` to C#:

```csharp
public Atom InternForApi(string name)
{
    var atom = AtomTable.Intern(name, permanent: false);
    if (!atom.IsPermanent)
        AtomTable.RegisterForeignHold(atom);
    return atom;
}

internal static void RegisterForeignHold(Atom atom)
{
    lock (_foreignHoldsLock)
    {
        _foreignWeakRefs.Add(new WeakReference<Atom>(atom));
    }
}
```

This registration is what lets the GC distinguish "atom used by engine" from "atom retained only by C#".

## Alternatives Considered

### No atom GC (GNU Prolog approach)

**Rejected.** Long-running production systems would leak atoms indefinitely. This is precisely the use case Shumway targets.

### Reference counting on every cell write

**Rejected.** The hot path of writing an atom id to a cell would become: increment refcount, write cell, possibly decrement old cell's atom refcount. The cost per operation is small but adds up over millions of operations per query. Mark-and-sweep amortizes much better.

### Single-tier table with WeakReference and external anchor list

**Considered, rejected.** If the table itself uses weak references and engines hold strong references through anchor lists, the system must update anchor lists on every `put_atom`. This is essentially RC under a different name and was previously discussed and rejected.

### Single-tier table with strong references and no GC for foreign holds

**Rejected.** If the table is strongly referenced and there's no special handling for C# retention, the GC would have no way to distinguish "atom used by engine" from "atom only held by table". Foreign-held atoms would either leak (never collected) or be collected while C# still holds them.

### Conditional weak references (`ConditionalWeakTable`)

**Considered.** .NET has `ConditionalWeakTable<TKey, TValue>` for this kind of "weak-keyed" mapping. However, its semantics (the table never enumerates keys, values are kept alive while keys are) don't quite match the atom use case. The three-tier approach is more explicit and clear.

## Consequences

### Positive

- **Long-running systems are safe**: atoms created dynamically can be reclaimed when no longer in use.
- **C# retention works naturally**: as long as C# holds the atom object, it stays alive. When C# lets go, it's eligible for collection on the next GC.
- **Hot path is unaffected**: `put_atom` is the same as in a non-GC system. No per-operation cost.
- **Atom comparison stays O(1)**: ids are stable; comparison is integer equality.
- **GC is amortized**: runs only when needed, not on every operation.

### Negative

- **Complexity**: the system has three tiers and explicit transitions between them.
- **Safe-point coordination**: the GC must coordinate with engines via safe points; this adds protocol around multi-threaded scenarios.
- **`_byName` removal complexity**: when an atom is collected, its entry in `_byName` must also be removed. This requires either caching the name before removal or scanning the dictionary.

### Mitigations

- The atom GC code is isolated in a single module (`Shumway.Core.AtomTable`). Other parts of the system interact only through `Intern`, `GetById`, and `RegisterForeignHold`.
- Documentation in the `AtomTable` source explains the three tiers and their transitions in detail.
- Tests cover each transition path explicitly.

## Implementation Notes

### Atom object layout

```csharp
public sealed class Atom
{
    internal int Id;
    internal string Name;
    internal bool IsPermanent;
    
    // Constructor only used internally by AtomTable
    internal Atom(int id, string name) { Id = id; Name = name; }
}
```

The `Atom` class is sealed and immutable from the outside. Only `AtomTable` can construct atoms.

### Safe points and thread coordination

When the GC runs, all engines must be at a safe point (not in the middle of a hot path that would observe inconsistent state). The mechanism:

- Each engine checks a `volatile int _gcRequested` flag periodically (in its dispatch loop, every N instructions).
- When the flag is set, the engine enters a safe point and waits.
- When all engines are at safe points, the GC runs.
- After the GC, engines resume.

For single-threaded use (one engine per process), this is trivial: the GC runs between queries.

`design/atom-gc-coordination.md` describes the atom table and its tiers as
built; a multi-engine stop-the-world coordination protocol was considered but
not built, and the sweep is currently exercised only by tests (that document
covers the current status).

### Pre-registered atoms

At engine construction, the system pre-registers common atoms with fixed ids:

- `[]` (empty list) → id 0
- `{}` → id 1
- `.` (cons functor) → id 2
- `true` → id 3
- `false` → id 4

This lets the interpreter and compiler reference these atoms as compile-time constants without lookup.

### Ids never reused

When an atom is collected, its id is not reused for new atoms. The id counter advances monotonically. With 32-bit ids, the space (~2 billion) is far larger than any realistic Prolog system needs. Avoiding id reuse simplifies invariants (no risk of stale cell references coincidentally matching a new atom).

### Per-engine TransientWeak tracking is not required

The decision is to use a global `_transientWeak` map. The atom GC operates globally over all engines. Per-engine atom tracking would add complexity without clear benefit.

## Test Strategy

- **Sweep of unreached transient atoms**: create transients, drop all references, run GC, verify they are removed from the table.
- **C# retention prevents collection**: create transient via embedding API, retain in C#, run GC, verify atom remains accessible.
- **Promotion back to Transient**: create transient, run GC (moves to TransientWeak), use it again from an engine, run GC, verify it's back in Transient.
- **Permanent promotion**: create transient, then load a source file that mentions the same atom, verify it's promoted to Permanent.
- **Stability of ids across transitions**: verify an atom's id doesn't change as it moves between tiers.
- **Concurrent atom creation from multiple engines**: stress test that `Intern` is thread-safe and produces consistent ids.

## Related ADRs

- ADR-001 (Engines and Global Tables): the atom table is global, but operates over engine state.
- ADR-002 (Cell Layout): atoms in cells are encoded by id.
- ADR-010 (Embedding API): foreign holds are registered when atoms are exposed to C#.
