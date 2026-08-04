# Atom table and GC

The atom table backs Shumway's global, stable atom ids. This documents what
exists in `src/Shumway.Core/AtomTable.cs`; ADR-003 is the design rationale.

## Three tiers

Every atom is in one of three tiers (see ADR-003):

- **Permanent** — source literals, builtin names, promoted atoms. Held by strong
  references in `_permanentById` (`Dictionary<int, Atom>`, mirrored by a
  copy-on-write `_permanentByIdArray` for lock-free `GetById`). Never collected.
- **Transient** — atoms created at runtime (`atom_concat`, `read_term`, …). Held
  strongly in `_transientById`. Eligible for collection.
- **TransientWeak** — a transient atom with no engine-side strong reference but
  still reachable from C# via the embedding API. Held as a
  `Dictionary<int, TransientWeakEntry>` (each entry bundles a
  `WeakReference<Atom>` with a cached name, since the name is needed after the
  atom is gone). The .NET GC decides its fate.

The name index `_byName` is a `ConcurrentDictionary<string, WeakReference<Atom>>`
— **weak on purpose**: a strong by-name reference would pin an atom the weak
tier is meant to release. `_foreignWeakRefs` tracks atoms exposed to C# so the
sweep can detect host retention.

## Sweep

Collection is mark-then-sweep. The **mark** phase is performed **externally** —
the engine subsystem gathers the set of reachable atom ids — and handed to
`AtomTable.Sweep(HashSet<int> reachable)`, which demotes or removes transient
atoms not in the set (a foreign-held atom moves to TransientWeak rather than
being dropped). `RegisterForeignHold(Atom)` registers a host-side hold.

## Current status

The tiering, `Sweep`, and the weak/foreign-retention machinery are implemented
and unit-tested (`tests/Shumway.Tests.Core/AtomTableTests.cs`). They are **not
currently driven at runtime**: no engine code triggers a sweep, so in practice
transient atoms accumulate for the life of the process. There is no
multi-engine stop-the-world GC coordinator — atom ids are global and stable, and
a sweep, when invoked, runs against a caller-supplied reachable set. A
concurrent multi-engine collection protocol (safe-point handshake across
running engines) was considered but not built; if atom reclamation becomes
necessary it would be added here.
