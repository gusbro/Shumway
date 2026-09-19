# ADR-053: The foreign table is swept, not truncated

## Status

Proposed (2026-09-19). Changes what the heap collector does at the end of a
collection and the lifetime of a managed object the engine holds on a
Prolog program's behalf, which `docs/architecture/decision-policy.md` names
a major decision. Companion to ADR-052, which did the same for the
attribute table; ADR-051 removed this table's highest-rate producer.

## Context

`Activation._foreignTable` is a `List<object?>`. `MakeForeign` appends and
returns a `FOREIGN` cell whose payload is the index. **Nothing ever removes
an entry**: not backtracking, not the heap collector, not a cut. Every
object put there is retained until the activation dies.

### The engine already reclaims two of its three side tables

This is not a missing mechanism, it is an unapplied one. `TrailType`
carries two entries whose whole purpose is reclaiming a side-table slot:

```
BigIntAlloc  = 2    one slot appended to the BigInteger side table; HeapIdx
                    carries the table size BEFORE the append, so unwind
                    truncates back to it and reclaims the slot
RationalAlloc = 3   same contract for the rational side table (ADR-039)
```

`MakeBigInt` trails its allocation. `MakeForeign` does not. Measured, in a
single query, with the live activation's tables read directly:

| after | foreign | bigint |
|---|---|---|
| 5,000 `'$new_reftype_slot'/1` | 5,000 | 0 |
| 5,000 `call_residue_vars/2` | 5,000 | 0 |
| 5,000 transient big integers | 0 | 5,000 |
| 5,000 slots, then `garbage_collect` | **5,000** | 0 |
| 5,000 slots, then backtracking over them | **5,000** | 0 |
| 5,000 big integers, then backtracking over them | 0 | **0** |

The bottom two rows are the decision. Backtracking over five thousand big
integers returns every slot; backtracking over five thousand foreign
objects returns none, and neither does a full collection.

### The scope is one query, and smaller than it was

Two facts bound this, and both are worth stating because the earlier
backlog note overstated the severity.

**A query gets a fresh `Activation`**, so the table dies with the query.
This is not a session-lifetime leak: it is unbounded growth *inside one
long-running query*, which is exactly the shape an embedded rules engine or
a long search has, and not a problem for a top level.

**ADR-051 removed the high-rate producer.** `ClpfdDomain` used to enter the
table once per propagation step (4,656 for `queens_fd(8)`). With domains on
the heap, a real clpfd search touches the table *zero* times:
`queens_fd(7)` run under `call_residue_vars/2` leaves the table at **1**
entry, which is the snapshot itself.

What is left, per execution rather than per program site:

- **`TermSlot`** (ADR-024): one per `'$new_reftype_slot'/1`, and one per
  `reftype` output every time a native `:- c` block runs
  (`NativeBlockRunner` calls `MakeForeign` inside the outputs loop). A loop
  around a native block with a reftype output fills the table at the speed
  of the loop. This is the unbounded case.
- **`AttrSnapshot`**: one per `call_residue_vars/2`.
- The debugger's attvar transplant, once per re-registered object.

The objects are not small. A `TermSlot` holds a whole AST `Term` (and a
`TermSlot[]` of sub-slots for a compound); an `AttrSnapshot` holds a
`HashSet<int>` with one entry per live attributed variable. The retained
slot is 8 bytes; the retained *object* is the cost.

### What the collector knows about a foreign cell today

Nothing. `Tag.Foreign` has no case in `GcMarkReferents` and falls through
to the leaf default, so a foreign cell is traced as an opaque value. The
collector walks `_foreignTable` exactly once, to relocate `AttrSnapshot`
homes, and never to decide whether an entry is still referenced.

## Decision

**A foreign table entry lives as long as a `FOREIGN` cell that names it is
reachable, and no longer.** The collector that already proves reachability
decides, in the same pass, with the same shape ADR-052 used:

### 1. A traced foreign cell marks its id

`GcMarkReferents` gains a `Tag.Foreign` case that records the id in a
per-collection bitmap. Like ADR-052's `AttVar` case, this puts **zero**
work on the mark path for every cell that is not a foreign cell.

### 2. Dead entries are nulled

After the trace, an entry whose id was not seen is set to `null`. The
managed object becomes collectable by the .NET GC, which is what actually
holds the memory. Nulling rather than removing keeps ids **positional and
stable**: every surviving id still means what it meant, so there is no
reuse hazard at all for the entries that remain.

### 3. The tail shrinks while its last entry is dead

Nulling leaves an 8-byte hole per dead entry. Where the dead entries are at
the END of the list, which is the common append-then-die shape, the list is
truncated while its last entry is dead. This reclaims the slot itself, and
it is liveness-driven rather than position-driven: an id is only released
once the collector has proved that nothing reachable names it.

## Consequences

### What gets better

A query that loops over a native block with a reftype output, or over
`call_residue_vars/2`, stops growing without bound. The objects go at the
first collection after they die, rather than at the end of the query.

### The hazard, named precisely

An id that lives **outside the heap** is invisible to the trace, so the
sweep can free a slot something still intends to use. There are exactly
two such places, and both are narrow:

- **`'$foreign'(N)` as a term.** `TermReader` renders a foreign cell as the
  compound `'$foreign'(N)`, and `DbgFixForeign` reads that form back and
  resolves `N` against a source activation. Inside a term, `N` is an
  ordinary integer and the collector cannot tell it from any other. This is
  the one respect in which foreign ids differ from BigInt and rational
  ids, whose raw indices never escape into terms.
- **`ForeignById` on a suspended activation**, which is how the debugger's
  attvar transplant reads the object. A suspended activation is not
  collecting, so its table is not being swept while it is read.

Both are debugger paths, cross-activation, and outside any window in which
the source activation collects. The sweep must nonetheless be tested
against the transplant, because "narrow" is not "impossible".

### What gets slower

One bitmap set per foreign cell the trace marks, and one pass over the
table per collection. Both are proportional to the foreign table, and a
program with an empty table pays nothing. The table is empty for every
program that does not use native reftypes or `call_residue_vars/2`, which
after ADR-051 is nearly all of them.

### Verification

- The measurement table above, re-run: the foreign rows must fall to the
  live count after a collection, where today they stay flat.
- **Red first**: a foreign object held only in a Y slot across a collection
  must still resolve. Without step 1 it must fail.
- A `TermSlot` written through a native block, collected over, then read
  back through `reftype_term`.
- `call_residue_vars/2` across a forced collection (ADR-052 already added
  this test; it must keep passing with the snapshot's slot swept).
- The debugger's attvar transplant over a collection, which is the hazard
  above.

## Alternatives considered

**Trail the allocation, like `BigIntAlloc`.** The obvious move, and it is
what the two sibling tables do. Rejected as the primary mechanism for two
reasons. It reclaims by POSITION rather than by liveness, so it releases an
id the moment the allocation is backtracked over, which is precisely when a
`'$foreign'(N)` integer captured elsewhere would go stale. And it puts a
trail entry on every `MakeForeign`, i.e. on the native interop path, to
save 8 bytes per slot, while the sweep frees the object itself for nothing
on that path. It remains available and compatible if a measurement ever
shows the slot count matters on its own; it is not needed to fix what is
measured here.

**Make the table a weak-reference list.** `WeakReference<object>` per entry
would let the .NET GC decide, with no engine change at all. Rejected: the
.NET GC has no idea whether a `FOREIGN` cell on the Prolog heap names the
object, so every entry would be collectable immediately and the engine
would have to resurrect from nothing. The reachability that matters is the
Prolog heap's, and only the Prolog collector can see it.

**Move the producers off the table**, as ADR-051 did for domains.
Applicable to neither survivor: a `TermSlot` and an `AttrSnapshot` are
managed objects with managed interiors, not term shapes that a heap cell
can hold.

**Do nothing.** Defensible on the numbers, and worth stating: ADR-051 took
the rate from thousands per query to zero for the common case, and what
remains needs a native-interop loop to bite. It is rejected because the
remaining case is not exotic for this engine, whose stated purpose includes
embedded rules engines over .NET interop, and because the fix is now the
second application of a shape the codebase just proved out.
