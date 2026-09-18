# ADR-051: CLP(FD) domains live on the heap

## Status

Proposed.

## Context

A finite-domain variable's domain is a `ClpfdDomain`: a managed object holding
a sorted `long[]` of interval bounds. A Prolog term names one through a
`Foreign` cell, whose payload is an index into the activation's foreign table,
and every domain operation is a native builtin that reads those objects
(`$dom_del`, `$dom_same`, `$dom_contains`, `$dom_isect`, and a dozen more).
That layer was built in phase 28 to keep interval walking out of interpreted
Prolog, and it did its job.

Two things it cannot do have since become the limiting factor.

**A wasm module cannot read a domain.** The Tier-1 WebAssembly backend
(ADR-050) executes inside a linear memory into which the engine's heap, stack,
registers and trail are staged. Managed objects are not there, so every domain
operation is a builtin exit: out of the module, into C#, and back. Measured in
a browser on `queens_fd(9)`, 465,029 builtin exits in one run, of which
380,061 were `$dom_del` and `$dom_same` alone. The same program with the tier
off takes 69.8 s against 17.1 s with it, so the tier does help, but the
comparison that matters is with a program the tier can actually compile:
`queens(9)`, ordinary Prolog over the same board, makes **6** builtin exits in
its whole run and goes 60x faster under the tier while `queens_fd` goes 4x.
The difference is not the constraint solving. It is that one program runs
inside the module and the other is a sequence of round trips.

**The foreign table never shrinks.** `MakeForeign` appends and nothing ever
truncates: not backtracking, not the heap collector. Every domain an engine
ever built stays reachable from the table, and therefore alive, until the
activation dies. A domain is immutable, so each narrowing that removes
anything allocates a new one and leaves the old behind, which is correct
(backtracking has to restore it) and unbounded (nothing reclaims it once no
choice point can). Measured: `queens_fd(8)` builds 4,656 domains for its 92
solutions, `queens_fd(7)` 1,247 for 40. A long-running session accumulates
without a ceiling.

Two narrower fixes were considered and rejected, both of them mirrors of the
domain store in linear memory, in the shape of the attribute-table image that
already exists for `get_attr/3`. Mirroring every domain adds bookkeeping to
the hot path of the solver; mirroring only the domains reachable from live
attributes avoids that but is more machinery. Either one buys the reads and
neither buys the writes: `$dom_del` that really removes a value still has to
build a domain, and a module cannot allocate a managed object. Both also leave
the foreign table growing.

A domain that lives ON THE HEAP answers all of it at once. The heap is already
in linear memory, so the module reads a domain with no image to keep in step,
and the module already allocates on the heap when it builds terms, so it can
write one too. The heap is truncated on backtracking, so the lifetime question
stops being a question. This is the same move ADR-047 made for text: a
structure with its own layout, inside the heap, rather than beside it.

## Decision

**D1 — a domain is a term.** `'$fd_dom'(L1, H1, L2, H2, ..., Lk, Hk)`: an
ordinary `Str` cell over a reserved functor of arity 2k, bounds ascending and
non-adjacent, exactly the invariant `ClpfdDomain` keeps today. The empty
domain is the atom `'$fd_dom_empty'` rather than a zero-arity structure, so
the common emptiness test is a cell comparison.

No new cell tag. The tag space is four bits with two values left, a new one is
a major decision in its own right, and nothing here needs one: the module
already reads `Str`, the collector already traces it, unification and
`copy_term/2` and assert already handle it, and the debugger already prints
it.

A packed form in the style of PSTR was considered and buys nothing here.
PSTR is compact because it puts three 16-bit code units in one 60-bit
payload; a bound needs the whole payload, so a packed domain is one header
plus 2k data cells against a term's one functor plus 2k argument cells. The
same size. Nor is it faster to read: a structure's arguments are contiguous,
so argument i is at `base + 1 + i`, the same arithmetic an array index is.
The one thing a tag would buy is that a domain could not be forged -- with a
reserved functor, `'$fd_dom'(3, 1)` is a term anyone can write and the
builtins have to keep rejecting bad shapes, which they do today for
non-domains anyway. That is not worth a quarter of the remaining tag space.

An arity of 2k means one interned functor per interval count, so domains
that fragment into many intervals grow the functor table by a handful of
entries. A domain of k intervals needs the k-th functor, not one per domain,
so the count is bounded by the widest fragmentation a program reaches. This
is the one real cost of D1 and it is small.

**D2 — the operations move, the interface does not.** `ClpfdDomain`'s methods
become operations over heap cells, keeping their names and contracts, and the
`$dom_*` builtins keep their argument shapes. clpfd.pl does not change. This
is what makes the change reversible and what lets D1 be revisited: the
representation is behind one boundary.

**D3 — `inf` and `sup` are atoms, not bounds.** Today they are `long.MinValue`
and `long.MaxValue`, which do not fit in a cell's 60-bit payload. A bound cell
is therefore an `Int` for a finite bound and the atom `inf` or `sup` for an
open one, which is already how the builtins read and write bounds at the
Prolog boundary (`ReadBound`, `WriteBound`).

Finite values keep the range the solver has today, which is the range of
`Tag.Int` and nothing wider. Measured on the current engine, not assumed:
`X in 1..576460752303423487` (2^59 - 1) holds and labels, `X in
1..576460752303423488` raises `type_error(fd_bound)` out of `$dom_new/3`,
`X in -576460752303423488..0` holds, and a 61-bit value raises out of
whichever `$dom_*` first reads it. A domain wider than a cell is not
something this ADR takes away, because it is not something the solver has;
widening it is separate work and no easier in the representation being
replaced.

**D4 — what the module open-codes, and in what order.** Reads first, because
they are most of the exits and none of them allocate: `$dom_same` by contents,
`$dom_contains`, `$dom_empty`, `$dom_singleton`, `$dom_min`, `$dom_max`. Then
`$dom_del`, the single largest, which allocates only when it removes something
and otherwise returns its argument. The rest (`$dom_isect`, `$dom_union`,
`$dom_above`, `$dom_below`, `$fd_hall`) stay builtins until a measurement asks
for them; they are a small share of the exits and the most code to emit.

**D5 — the foreign table keeps its other tenants, and stops being a leak in
practice.** This ADR moves domains, not every foreign object. Giving foreign
entries the heap's lifetime is a change to the cell and backtracking model
and belongs in its own ADR, and it is left open deliberately.

What that ADR would be worth changes here, but less than it first looks.
Every producer appends per EXECUTION, not per program site: a `TermSlot` each
time a native block runs with a reftype output (`NativeBlockRunner` calls
`MakeForeign` inside the loop that binds outputs) and each time
`new_reftype_slot` is called, an `AttrSnapshot` on each `copy_term/3`. None
of them is bounded by the program's text. What separates domains is
frequency, not kind: they were appended per propagation STEP, thousands per
query -- 4,656 in `queens_fd(8)` -- where the others are appended per
interop call or per copy.

So this removes the leak along the path that reaches it fastest, and the one
every constraint program walks. It does not remove the leak. A loop around a
native block with a reftype output still grows the table without a ceiling,
and it does so at whatever rate the loop runs. That is the separate ADR, and
what this one changes is which programs hit the problem, not whether the
problem exists.

## Consequences

The tier can compile finite-domain code instead of wrapping calls to C#. What
that is worth is not predicted here: the counts say 82% of the exits in
`queens_fd` are the two operations D4 attacks first, and the browser is where
the time is measured (CONTRIBUTING.md).

Domains become heap cells and so become the collector's business. Reported on
its own that reads as a cost -- `queens_fd(7)` goes from 416,920 heap cells to
422,645, and a run whose domains fragment into 200 intervals goes up by 11.5%
-- but it is the same memory in a different place, and the comparison has to
include what leaves. A domain of k intervals cost a list entry, an object
header and a `long[]` in the foreign table, about 56 + 16k bytes, retained for
the life of the activation. As a term it is 8 + 16k bytes, reclaimable. It is
SMALLER the moment it is built, by roughly the object overhead, and then it
goes away. The 5,725 extra heap cells in `queens_fd(7)` are a transient peak;
the 1,247 domains that run built are what stops being retained.

And the reclamation is real, not theoretical: 100 and 400 successively
abandoned domains retain the same 40 bytes after a collection (phase 1's
`AbandonedDomainsAreReclaimed`).

Tier-0 domain operations become C# over `Cell[]` instead of C# over `long[]`.
The expectation is that they are comparable; the requirement is only that they
are not grossly worse, and that is a check at the end rather than a gate at
the start. What is being bought here is a memory bound and a compilable
representation, and a few percent of interpreted interval walking does not
outweigh either.

The limits become user-facing documentation. The solver's bounds are the
range of a cell's integer payload, and today that is discoverable only by
provoking a `type_error(fd_bound)`. The guide has to say what a finite domain
can hold, that `inf` and `sup` are the open bounds, and that a value outside
the range is an error rather than a silently widened domain. That is not a
consequence of this change -- the limit is already there -- but the change is
when it gets written down.

Phase 1 ran the audit and it came out clean for this arc, with one finding
that belongs to someone else. The attribute table is a STRONG GC root, so an
attributed variable nothing can reach after a cut keeps its propagator list
alive: thirty rounds of 200 propagators retain 27,570 heap cells that no
`garbage_collect` recovers. That is not this change -- the same program
retained 27,480 cells before it, the 90-cell difference being the collapsed
final domains -- and it is recorded separately. It is worth knowing here
because it is what a naive measurement of "does the heap stay flat after a
cut" actually measures.

What was audited, each with a test:

- **The collector**, on a live domain: it survives a collection and still
  says what it said, fragmented or not.
- **The trail as a root**: a domain the current attribute no longer points at
  is still restorable while a choice point exists, and a collection taken
  mid-search must not take it. Two narrowings deep, so what comes back is an
  intermediate domain rather than the original.
- **The attribute store.** A variable's attribute is `fd(Domain, Props)` and
  those terms are already heap terms, so the domain is reachable from a root
  that the collector knows. Worth confirming rather than assuming, because the
  attr log has its own hygiene rules.
- **`copy_term/2`, `assertz`, `findall/3`.** A `Foreign` cell survives a copy
  by sharing the object and a term is copied structurally, so this looks like
  a semantic change. Measured, it is not one: a domain never reaches a term a
  user can copy. `copy_term(A, B)` with `A in 1..9` yields a plain variable,
  because a copy drops attributes and the domain lives inside one;
  `copy_term/3` yields residual goals (`_G in 1..9`), which are ordinary
  terms and not domains; `findall/3` yields a fresh variable. The audit is to
  confirm that this stays true, not to manage a change.

  `copy_term/2` is public semantics and is not to be touched. If copying a
  domain while keeping its identity is ever needed, it goes in a private
  predicate that does what copy_term does plus that, never in copy_term
  itself.
- **The debugger's attvar transplant**, which re-registers foreign ids across
  activations. With domains as terms there is nothing to re-register, which
  removes a special case rather than adding one.

## Phases

0. DONE. The representation and D2's boundary, Tier-0 only: `ClpfdDomain`'s
   operations over heap cells, builtins unchanged, clpfd.pl untouched. The
   existing clpfd and clpz suites are the oracle, and they are large. Nothing
   about wasm yet.
1. DONE. The audit above, each item with a test.
2. PARTLY DONE. $dom_same on identical cells and $dom_empty are answered
   in the module. Desktop counts on queens_fd(7): builtin exits 23,262 ->
   14,448, $dom_same 8,814 -> 1,233 (the calls where the domain really
   changed, which the host owes), $dom_empty 1,233 -> 0. The rest of D4's
   reads ($dom_contains, $dom_singleton, $dom_min, $dom_max) need a walk
   over the intervals, which is the same machinery phase 3 needs.
3. PARTLY DONE. The module walks a domain's intervals, which answers
   $dom_contains, $dom_singleton, and $dom_del for the case that removes
   NOTHING (most of them: clpfd posts a disequality by removing a value
   and asking whether anything changed, and by the time a propagator
   re-fires the value is usually already gone). A removal that DOES
   remove has to build a domain and still steps aside, as do unbounded
   domains, whose bounds are atoms.
   Desktop counts on queens_fd(7): builtin exits 14,448 -> 5,115,
   $dom_del 8,807 -> 1,226, $dom_contains 519 -> 0, $dom_singleton
   1,233 -> 0, chains 3,587 -> 2,692. Against the 23,262 the arc started
   from, that is -78%.
4. PARTLY DONE. $dom_same compares CONTENTS when the cells differ, bound
   for bound as cells, which needs no knowledge of what a bound means and
   so covers inf and sup as well as integers. Desktop counts on
   queens_fd(7): builtin exits 5,115 -> 3,882, $dom_same 1,233 -> 0,
   chains 2,692 -> 1,459. Against the 23,262 the arc started from, -83%.
5. What is left of the domain exits is $dom_del where the value IS
   present (1,226), which has to build a domain. The module can allocate
   on the heap, but a domain's functor depends on the resulting interval
   count and the module cannot intern one, so this needs a small table of
   '$fd_dom' functors by arity in the mailbox. That is new ABI, which is
   why it is its own step rather than part of phase 3.
