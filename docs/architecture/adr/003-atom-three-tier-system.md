# ADR-003: Atom Three-Tier System

## Status

Accepted.

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

Shumway interns atoms into one process-wide table with stable ids, in three
tiers. Transient atoms are kept alive by claims that each activation and each
engine compute when they mark, so engines on different threads collect on
their own, without stopping one another.

### The three tiers

1. **Permanent**: never collected. The pre-registered atoms, the names the
   engine itself fixes (builtin names, atom ids C# keeps in static caches),
   and the atoms of a linked bundle's code. A bundle is static and never
   unloaded, and its persisted IL and wasm modules bake atom ids and are
   shared by the process, so every loader of a bundle interns permanent.

2. **Transient**: every other atom, the atoms of code compiled by a consult,
   by `assert` and for a query included. An atom named only by clauses that
   were retracted, abolished or replaced by a reconsult is garbage once
   nothing else holds it, so a system that never stops and works by `assert`
   and `retract` keeps a bounded table. The compilers intern transient.

3. **TransientWeak**: a transient atom no engine claims while C# still holds
   it through the embedding API (`RegisterForeignHold`). The table keeps only a
   `WeakReference<Atom>`, and the .NET GC decides: if C# lets go, the atom is
   gone; if an engine interns it again first, it is transient again.

### Ids

One id per live name in the process. A collected atom's id is free, and a new
atom takes a free id before a fresh one, the last freed first: the ids, the
array indexed by them and a mark's bitmap stay bounded by the most atoms alive
at once, not by every atom the process made. A name interned again after its
atom died is a new atom, with whatever id is free. What is kept per activation
and per engine is liveness, never the name: two engines that intern the same
name get the same atom.

A stale id therefore names another atom. Every holder of an atom id is a
claimant or is reached by a claimant's mark. A cache keyed by an atom or
functor id keeps permanent names only, checks on a hit that its ids still name
the atoms it was made from, or is emptied when a collection runs. For tests the
table can poison dead ids instead of freeing them: a poisoned id is not handed
out again and looking it up throws, so a holder a mark misses fails where it
reads.

The hot path writes the id. `put_atom` is a store and atom comparison is
integer equality; no instruction counts references.

### Lookup tables

- `_byName: ConcurrentDictionary<string, WeakReference<Atom>>`: name to atom,
  weak so that a TransientWeak atom has no strong table-side reference. Used by
  `Intern(string)`. A dead atom's entry is removed only while it still names
  that atom.
- `_permanentById`: strong references to the permanent atoms, mirrored by a
  copy-on-write array for lock-free `GetById`.
- `_transientById`: strong references to the transient atoms.
- `_transientWeak`: the TransientWeak atoms, each a `WeakReference<Atom>` with
  its name.
- `_foreignWeakRefs`: weak references to the atoms handed to C#, which tell a
  collection which unclaimed atoms C# still holds.

### Claims

Each transient atom carries a claim count and an epoch: the value of a global
epoch counter when it was last interned or last lost a claim. Interning writes
the epoch; nothing else happens on the hot path or in an intern.

The claimants hold sorted lists of transient atom ids:

- **Activations.** Each activation holds the transient atoms its last mark
  found. A nested activation, a query run inside a builtin of another on the
  same thread (the complete evaluation of tabled negation, a consult from
  Prolog, a foreign predicate that queries back, the debugger's evaluation),
  marks with the activation it runs in and shares its list. The last
  activation of a domain to end releases its list.
- **Engines.** Each engine holds the transient atoms of its state beyond any
  activation: its code (the persistent program buffer, the static link, the
  literal pools and images, the dynamic clause store, and the atoms each IL or
  wasm artifact compiled from that code bakes, recorded with the artifact and
  held while it is reachable, delegates retired for resumes included), its
  global variables, its recorded database and its `flag/3` values. A
  retracted clause's code stays in the persistent buffer, and holds its atoms,
  until the buffer compacts (ADR-015).

Compiled code holds its atoms as long as an activation can still run it. An
IL artifact (a compile's delegates and continuation methods) claims the atom
operands of every predicate the compile read and the atoms of the images its
methods load; a wasm module claims those of the predicate it was compiled
from. The claim holds while the code is installed and, once it is evicted or
replaced, while an activation of the engine reaches it: through a resume
marker of its predicate among the activation's control words, through a
compiled choice point of its code, or through a method of its code on the
stack. Only a builtin that may change code (assert, retract, abolish, the
consult family, the clause collectors, the tier switch, a foreign predicate)
can evict the code of a method that is running, so a method stores its
artifact's id into the activation before it calls one, and the dispatch loop
clears it when compiled code returns to it; every other call stores nothing. A
wasm module's running method is named by the resume marker its builtin's
return address carries. Retired code is released when no activation reaches
it: when code is evicted, when an activation ends, and before a new install.
A resume marker names a predicate and a cursor, not a version, so a new
install for a predicate waits until its retired code is gone, and the
predicate runs on bytecode until then. A bundle's code names no artifact and
claims nothing, its atoms being permanent; retired, it is kept.

### Collection

A claimant's mark replaces its list with what it finds and adjusts the counts
by the difference, merging the two sorted lists: an atom it found and did not
hold gains a claim; an atom it held and did not find loses one, which writes
the atom's epoch. An atom dies when its count is zero and every live claimant
has marked since its epoch; the registry of claimants publishes each one's
last mark epoch. So an atom another claimant took, and has not marked yet,
cannot die. Counts and the dead state change atomically: engines on different
threads collect on their own, and an intern that meets a dead atom makes a new
one for the name. An unclaimed atom C# still holds goes to TransientWeak
instead of dying.

A collection starts when the transient atoms interned since the last one pass
a threshold. It waits for every claimant to mark at or after the epoch it
began at, and the mark that completes it, or the release of the last claimant
it waited for, sweeps the table. A claimant's owner runs it between taking
and giving back a busy state: an activation's domain from the outermost step
of its query (each run, the building of each answer, the teardown), an engine
from the outermost step of any of its queries and while the host changes it.
The collection takes each claimant idle if it can, without waiting:

- A claimant it takes is marked there, on the collection's thread. One that
  has not run since its last mark made that way holds what it held: its last
  mark moves to now and nothing is scanned.
- A running claimant is asked to mark at its next safe point, through the
  request word the safe points already read for the debugger, so the hot path
  reads nothing more. A heap collection due at that safe point marks in its
  own trace (ADR-016); otherwise the activation traces without moving
  anything. A failure-driven loop that crosses no call boundary is reached at
  the throttled safe point of the retries the engine makes itself, the one that
  serves cancellation; the backtrack polls of compiled code do not mark, since
  they keep heap indices and registers in locals no trace sees. A running
  engine marks at the safe points of the domain its outermost step runs in,
  which holds the queries its builtins run, on other engines too. A claimant
  that stops before a safe point marks as it stops, unless an exception
  stopped it.

So a claimant that does not run, an engine kept idle or a query whose answers
the host stopped asking for, never holds a collection back.

The mark takes every cell the trace reaches (an `Atom` cell, and the name atom
of a `Functor` cell) and the atom ids the activation keeps outside cells:
attribute module keys and their trail log, the wakeup queue, the
global-variable keys of the external trail, the findall, bagof and setof
buffers in progress, and the objects of builtin choice points and of the
foreign table, through a visitor they implement. An activation joins its
domain at its first run, once its setup wrote what it writes: from there a
collection may mark it from another thread.

### Atom terms held by the host

An `AtomTerm` caches the id it resolved, and the trees the host holds (an
answer, an exception, a term handed to a foreign predicate) cannot be marked.
The cached id carries the number of sweeps run when it was taken; after a
sweep the tree resolves the atom again by the name it keeps.

### Functors

The functor table maps a functor id to its name atom and arity, and is not a
claimant. An entry stays when its name atom dies and serves the atom that
takes the id next, so the table is bounded by the ids in use and the arities
each was used with.

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

### One mark over every engine

**Rejected.** A sweep that marks every engine's state needs every engine stopped at a safe point at the same time, a handshake across threads. Claims computed per activation and per engine let each collect alone.

### A namespace per activation

**Rejected.** Activations meet through shared code and stores: a name in a bundle's code, a fact asserted by one query and read by another. One name must have one id everywhere it can be compared.

### Claims kept on every intern

**Rejected.** A set per claimant updated on each intern costs a hash lookup per intern and some twenty bytes per atom per claimant, as much as a short atom itself. Computing claims when marking costs a store per intern and the merge of two sorted lists per mark.

## Consequences

### Positive

- **Long-running systems are safe**: atoms created dynamically, and those of clauses that were retracted or replaced, are reclaimed once nothing holds them.
- **No stop across threads**: each activation and each engine collects on its own.
- **A claimant that does not run holds nothing back**: idle engines and suspended queries are marked by the collection.
- **Bounded ids**: a dead atom's id is reused, so the ids and the tables indexed by them follow the atoms alive.
- **C# retention works naturally**: as long as C# holds the atom object, it stays alive.
- **Hot path is unaffected**: `put_atom` is the same as in a non-GC system.
- **Atom comparison stays O(1)**: ids are stable; comparison is integer equality.
- **GC is amortized**: atoms are marked only past a threshold, inside a heap collection or at a query setup.

### Negative

- **A holder the mark misses lets a live atom die**: its id goes to the next new atom, and the cells that still hold it read that atom. Every structure that keeps atom ids outside cells has to be part of a mark; tests poison dead ids to find one that is not.
- **Memory per atom**: a count and an epoch on each transient atom, and four bytes per atom per claimant held between marks.
- **A claimant blocked inside a builtin** (a read waiting for input) holds a collection back until it reaches a safe point.
- **Retracted code holds its atoms until the persistent buffer compacts.**

## Implementation Notes

### Atom object layout

An `Atom` holds its id, its name, whether it is permanent, its claim count,
its epoch and whether it died. Only `AtomTable` constructs atoms.

### Pre-registered atoms

At engine construction, the system pre-registers common atoms with fixed ids:

- `[]` (empty list) → id 0
- `{}` → id 1
- `.` (cons functor) → id 2
- `true` → id 3
- `false` → id 4

This lets the interpreter and compiler reference these atoms as compile-time constants without lookup.

## Test Strategy

- **A test per kind of claim**: an atom reachable only from that claimant survives a forced collection, and the test fails with that claimant left out of the mark.
- **Id reuse**: a dead atom's id is handed out again, the ids stay bounded while atoms come and go, a reused id reuses its functor entries, and a cache keyed by ids answers for the atom that has the id now.
- **Poisoned ids**: with dead ids poisoned, reading one throws; the collection tests run poisoned.
- **Idle and running claimants**: an idle claimant is marked by the collection, and scanned again only if it ran; a running one marks at its next safe point, at a backtrack safe point, inside a heap collection due there, or as it stops; a run waits while the collection marks its claimant; a running engine marks at the safe points of its own query and of a query its builtin runs on another engine.
- **Reclamation**: transient atoms created and dropped in a loop are reclaimed and the table stays bounded, over many queries and within one long query.
- **Retracted clauses**: an atom named only by retracted or abolished clauses is reclaimed after the buffer compacts.
- **Bundles**: the atoms of a linked bundle stay permanent.
- **Host trees**: a term the host held across a sweep materializes with its name.
- **C# retention**: an atom handed to C# and unclaimed survives while C# holds it.
- **Threads**: two engines intern and collect in parallel, and `Intern` gives one id per name.

## Related ADRs

- ADR-001 (Engines and Global Tables): the atom table is global, but operates over engine state.
- ADR-002 (Cell Layout): atoms in cells are encoded by id.
- ADR-010 (Embedding API): foreign holds are registered when atoms are exposed to C#; an engine runs on one thread at a time.
- ADR-015 (Persistent code space): retracted code is reclaimed when the buffer compacts.
- ADR-016 (Heap garbage collection): an activation marks atoms inside its heap collection.
