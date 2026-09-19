# ADR-052: The attribute table observes, it does not retain

## Status

Proposed (2026-09-19). Changes what the heap collector treats as a root,
which `docs/architecture/decision-policy.md` names a major decision, so it
is written before any of it is implemented. Builds on ADR-016 (the heap
collector) and on the bounded-memory arc of phase 40, which closed the
other two leaks of this family (orphan attr-log registers, dead choice
points under LCO frames).

## Context

The attributed-variable store maps a variable's home heap index to its
attributes: `home -> { moduleId -> attributeValueHeapIndex }`. Both halves
are heap indices, so the collector cannot skip the store. Today
`MarkExternalHolders` marks both:

```csharp
foreach (var (home, _, attrValueIdx) in AttrAll())
{
    if ((uint)home < (uint)oldTop) GcMarkCell(home);
    if ((uint)attrValueIdx < (uint)oldTop) GcMarkCell(attrValueIdx);
}
```

Marking the *value* from a live home is right: the attribute term is
reachable only through the store, and a live attributed variable must keep
its attributes. Marking the *home* is the defect. It says "this variable is
alive because the table has a row for it", and the table has a row for it
because the variable was once alive. **The row is its own justification, so
it can never be collected.**

### What it costs, measured

The shape is the one the user proposed while auditing ADR-051 phase 1: give
a variable a domain, fragment it with many propagators, then collapse it to
a singleton and cut.

```prolog
frag(_, 0) :- !.
frag(X, N) :- X #\= N, M is N - 1, frag(X, M).
round      :- X in 1..400, frag(X, 200), X #> 398, !.
```

After the cut nothing in the program can reach `X`. Thirty rounds, then
`garbage_collect`, then `'$heap_root_diag'`:

```
[gc-roots] holders breakdown: attrTable=54570 (n=30) attrTrailLog=0 (n=12120)
                              wakeups=0 (n=0) cleanups=0
[gc-roots] baseline breakdown: bindingTrail=0 (entries=0) extraTrail=0 (entries=0)
                               catchFrames=0 (n=0) ... markHook=4
[gc-roots] heapTop=54574 total-live=54574 stackTop=10 E=7 B=-1
```

Of 54,574 cells that survive a full collection, **54,570 are held by the
attribute table alone** and 4 by everything else in the engine. There are no
choice points (`B=-1`), an empty binding trail, and a two-frame environment
chain. Nothing can name those variables again, and the heap grows in
proportion to the number of rounds for the life of the query.

The attribute trail log is not an accomplice. Its 12,120 entries mark zero
cells even when the log is charged FIRST (measured by swapping the two
blocks): every one of them is a dead record, dropped by the cut. The
attribute table is the sole root.

This is **not** ADR-051's doing and not the domains': the same corpus
measured 27,480 cells before the domains moved to the heap and 27,570 after,
the 90-cell difference being the thirty collapsed singletons. Intermediate
domains are already reclaimed (`AbandonedDomainsAreReclaimed` pins that).
What is retained here is the *propagator lists*, and they were retained
before ADR-051 exactly as they are now.

### The engine already knows the distinction

`AttrSnapshot`, which backs `call_residue_vars/2`, holds homes as raw
addresses and its doc-comment states the principle outright:

> It holds raw HEAP ADDRESSES, deliberately: an integer observes without
> retaining, so a variable that becomes garbage during the goal is not
> pinned by having been recorded.

The collector relocates those addresses and never marks them. So the engine
already separates observing from retaining, and already implements the side
this ADR wants, in the one holder that thought about it. The attribute
table is the holder that did not.

## Decision

**A row in the attribute table does not keep its variable alive.** The home
is a weak key; the attribute value stays a strong reference *from* a live
home. A row whose home is unreachable from the real roots is garbage and is
dropped by the collector that proved it unreachable.

That is ephemeron marking, and the three parts are:

### 1. The home stops being a root

`MarkExternalHolders` stops marking homes and stops marking values. The
attribute trail log, the pending-wakeup queue and the cleanup roots are
unchanged: those are genuine roots, because backtracking will read them.

### 2. A marked attributed variable pulls in its attributes

The edge moves from the table into the trace, where `GcMarkReferents`
already has a case for the tag:

```csharp
case Tag.Ref:
case Tag.AttVar:
    GcMarkCell(c.AsHeapIndex);
    // ADR-052: reaching an attributed variable reaches its attributes.
    // The table is a weak key: this is the only edge INTO a row.
    if (c.Tag == Tag.AttVar) AttrEnqueueValues(c.AsHeapIndex, GcMarkCell);
    break;
```

This is deliberately *not* a fixpoint loop over the table. The work list
already runs to fixpoint, so an attribute term that reaches another
attributed variable pulls that one's attributes in on the same pass, at no
extra cost and with no quadratic corner. It also puts **zero** work on the
mark path for cells that are not attributed variables, which matters: the
mark loop visits every live cell, and the rule against unproposed hot-path
work applies to it.

A conservatively-scanned stack slot holding a stale cell that happens to
read as `AttVar` probes a home that may no longer be that variable's. The
result is over-retention, never a wrong answer, which is the direction
`GcMarkReferents` already documents for every other stale-but-plausible
payload.

### 3. The collector sweeps the rows it disproved

After the trace and before relocation, a row whose home is unmarked is
dropped through `AttrDropRecord`, which is one of the five writers in the
`Activation.Attrs.cs` funnel, so the wasm tier's linear-memory mirror stays
in step by construction. Dropping before relocation matters: afterwards the
index is meaningless.

This also removes a hazard rather than adding one. `AttrCreateRecord`
carries code to evict an "orphan record" left by a backtracked-then-reused
heap slot; with the sweep, a collection can no longer leave one behind. The
defensive code stays (backtracking still makes orphans between
collections), but the window it covers shrinks.

### 4. `call_residue_vars` snapshots drop what died

A snapshot's homes are relocated today and never filtered. `RelocIndex` on
an *unmarked* index returns where the next live cell landed, so a dead home
silently becomes an unrelated live variable's address, and
`'$attv_new_since'` then reads a genuinely new attributed variable as "not
new". The trap is documented in `AttrSnapshot` itself and exists today; it
is latent only because the table pins nearly every home. Making the table
weak makes it reachable, so it is fixed here: an entry whose home is
unmarked is dropped from the snapshot instead of relocated.

## Consequences

### What gets better

The corpus above drops from 54,570 retained cells to the handful the roots
genuinely reach, and stops growing with the number of rounds. Every program
that constrains a variable and then abandons it gets the same treatment, and
that is most of what a solver does: a labelling search abandons attributed
variables at every failed node.

The foreign-table leak shrinks with it. `ClpfdDomain` entries no longer
enter the table after ADR-051, but every other producer is unaffected;
see the separate backlog item, which needs its own ADR because it changes
cell lifetime rather than root policy.

### What gets slower

One extra dictionary probe per *attributed variable* the trace marks, and
one pass over the table to sweep. Both are proportional to the number of
attributed variables, not to the heap, and the table is small precisely in
the programs that collect often. The measurement to take before accepting:
a clpfd labelling benchmark, back to back, expecting the collection itself
to get faster because it has less to mark and less to slide.

### What could break, and how it is caught

**An attributed variable reachable only through a path the collector does
not walk would now be collected.** This is the whole risk, and it is the
same risk the stack-roots arc already took: the answer is that every such
path is already enumerated in `MarkExternalHolders`, `MarkExternalTrailRoots`
and the `OnGcMark` hook, because the collector would already be freeing the
attribute *value* under a live reference if one were missing. The change
makes the home obey the same rule the value has obeyed since attributed
variables learned to survive a collection.

The counter-proof has to be in both directions, which the solver campaigns
make cheap:

- **Red first**: a test that constrains a variable, keeps it reachable from a
  Y slot only, collects, and asserts the constraint still holds. Without
  step 2 it must fail.
- Retention: the corpus above, asserting the retained-cell count stops
  growing with rounds, plus `'$heap_root_diag'` attribution naming the table.
- Soundness at scale: the clpz/clpb certification suites and the Neumerkel
  set, which are the engine's existing oracles for "a constraint quietly
  disappeared", run with a collection forced at a low watermark.
- `call_residue_vars/2` and `'$attv_new_since'` around a forced collection,
  which is the snapshot path of part 4.

### What is deliberately not done

The table stays keyed by heap index, and stays a managed dictionary. Making
attribute rows heap-resident is the shape ADR-051 used for domains and would
subsume this, but it changes the cell model, and the leak does not need it.

## Alternatives considered

**Drop rows when the variable is cut away.** There is no such moment: a cut
removes choice points, not bindings, and nothing tells the store that the
last reference to a variable just went out of scope. That is what a
collector is for.

**A periodic scavenge of rows whose home cell no longer reads `AttVar`.**
Cheap, and it catches a real subset (a home whose cell was overwritten),
but not this one: the cells in the measurement above all still read `AttVar`.
Reachability is the question, and only the collector answers it.

**Keep the table strong and cap it.** A cap turns a leak into a wrong
answer.
