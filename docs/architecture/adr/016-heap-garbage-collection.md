# ADR-016: Heap Garbage Collection

## Status

Proposed (Phase 20). Not yet implemented — this ADR records the design
to be built. Adding a heap GC is a "major decision" under CLAUDE.md
(comparable to the atom-GC strategy in ADR-003), so it is settled here
before any code lands.

## Context

Shumway's heap is a flat `Cell[]` (ADR-002). The engine allocates cells
by bumping `_heapTop`; it **never** allocates a managed object per cell.
The only mechanism that ever lowers `_heapTop` — i.e. the only heap
*reclamation* — is **backtracking**: when the engine backtracks into a
choice point it restores `_heapTop` to the value the CP saved at
`CpHeapTopOffset` (ADR-005). `SetHeapTop` (used by builtins that do a
trial allocation and roll it back) is the same coarse mechanism under a
different name.

This has a sharp consequence: **Shumway reclaims heap only at
choice-point boundaries, never by reachability.** Two cells can be
provably unreachable — no register, no environment slot, no choice
point, and no trail entry refers to them — and they will still sit on
the heap until execution backtracks past the point where they were
allocated. During a long stretch of deterministic, forward execution
(the common shape of real programs, which commit with `!` rather than
backtrack) nothing is reclaimed at all.

The pin is total in practice because **the top-level always holds a
choice point at the very bottom of the stack.** A query is run by
taking the first solution and leaving the remaining choice points in
place so `;` can re-satisfy it. That bottom CP is created at query entry
(heap top ≈ 5) and is never released while the first solution is
computed. Every cell allocated during the whole query lives *above* its
saved heap top, so backtracking — even Blint's millions of internal
backtracks into *younger* CPs — can never bring `_heapTop` back down to
it.

### Measured evidence (Blint linting a 72 KB source)

Instrumenting every heap grow (`SHUMWAY_PROFILE`):

```
[heapgrow] cap=131,072   heapTop=65,536    cps=6   bottomCpHeapTop=5  trappedAboveFloor=65,531
[heapgrow] cap=262,144   heapTop=131,071   cps=7   bottomCpHeapTop=5  trappedAboveFloor=131,066
[heapgrow] cap=524,288   heapTop=262,144   cps=52  bottomCpHeapTop=5  trappedAboveFloor=262,139
[heapgrow] cap=1,048,576 heapTop=524,288   cps=7   bottomCpHeapTop=5  trappedAboveFloor=524,283
[heapgrow] cap=2,097,152 heapTop=1,048,576 cps=17  bottomCpHeapTop=5  trappedAboveFloor=1,048,571
[heapgrow] cap=4,194,304 heapTop=2,097,152 cps=739 bottomCpHeapTop=5  trappedAboveFloor=2,097,147
[heapgrow] cap=8,388,608 heapTop=4,194,304 cps=7   bottomCpHeapTop=5  trappedAboveFloor=4,194,299
```

The oldest choice point's saved heap top is **5** at every grow, while
`_heapTop` balloons to 4.2 M cells. The heap reaches **8 M cells
(64 MB)** to lint a 72 KB file; essentially all of it is garbage trapped
above the bottom CP. The doubling re-growth allocates **~127 MB** of
intermediate `Cell[]` arrays (the dominant slice of the ~250 MB total
.NET allocation per query). For larger inputs this grows without bound.

### Why this is the absence of a GC, not a fixable point-leak

We investigated for a discrete leak first. There is none: the trapped
cells are genuine deterministic garbage, and the bottom CP is the
legitimate top-level enumeration CP. The behaviour is the textbook
consequence of a WAM with no reachability-based heap GC. Larger programs
will need it regardless; Blint just makes it visible.

## Decision

Add a **reachability-based, order-preserving (sliding) mark-compact
collector for the `Cell[]` heap**, run at engine safe points, triggered
by a heap-occupancy watermark and via an explicit `garbage_collect/0`
builtin (SWI-compatible). It reclaims unreachable cells **independently
of the choice-point stack** — open CPs no longer pin garbage, only
*reachable* state survives.

### Roots

The collector marks from every place a live heap index can be held:

1. **X (argument/temporary) registers** — the live set at the safe
   point. (At a Call/Execute boundary the live-X set is known from the
   call's arity; a conservative scan of the whole register bank is also
   sound since every slot is a tagged `Cell`.)
2. **Y-slots in every environment frame** — walk the `E` chain via
   `EnvCeOffset` (ADR-005), scanning each frame's permanent variables.
3. **Every choice point's saved argument registers and saved state** —
   walk the full CP chain via `CpBOffset`. This is the crux of the win:
   because we mark from *all* CPs (not just the youngest), a cell stays
   live iff some CP could still reach it, but unreachable cells under an
   open CP are collected.
4. **Both trails** (ADR-004). The binding trail holds heap indices; the
   extra trail's struct entries carry heap indices and a `CatchFrame`
   snapshots a heap top. All are roots *and* must be relocated.
5. **Attributed-variable tables** and any per-engine auxiliary tables
   that hold heap indices.
6. The **current goal / in-flight structure** being built at the safe
   point.

Managed-object side tables (BigInteger, string, foreign refs — ADR-002
keeps these out of cells, addressed by integer id) are *not* scanned as
heap roots; their lifetime is a separate concern (ref-count or a
parallel sweep keyed by surviving id cells) and is out of scope for the
first cut.

### Relocation

Cells reference other cells **by heap index** (REF self-pointers, STR →
functor cell, LIS → pair). A compacting collector must rewrite those
payloads to the post-compaction addresses. Sliding compaction preserves
the relative order and the **contiguity** WAM structures rely on (a STR
cell's functor and argument cells, a list pair) because a live structure
is marked and moved as a unit and order is preserved. After compaction
the collector fixes up, to the new addresses:

- payloads inside surviving cells (REF/STR/LIS),
- every heap index on both trails,
- each CP's saved `HeapTop` / `Hb` and saved argument cells,
- environment Y-slots and X-registers,
- `_heapTop`, `_hb`, and any `CatchFrame.SnapHeapTop`.

### Invariants preserved

- **ADR-002 cell layout** unchanged — the collector only reads tags and
  rewrites heap-index payloads; cells stay 8-byte blittable values with
  no managed refs.
- **Young-to-old binding rule / `Hb`** — order-preserving compaction
  keeps the age ordering of variables (a cell that was older stays at a
  lower index), so the rule and the trailing decision still hold; `Hb`
  is recomputed from the post-compaction CP boundary.
- **ADR-004 two trails** — both are relocated; their structure is
  unchanged.
- **Single-threaded engine (ADR-001)** — the GC runs inline on the
  engine thread at a safe point; no locking. Like the atom GC (ADR-003)
  it runs at safe points, **never** in the middle of a partially-built
  structure or between an STR cell and its functor.

### Safe points and trigger

- A safe point is an instruction boundary where engine state is
  consistent: the natural choice is the top of the dispatch loop
  (between WAM instructions) and the Call/Execute entry, mirroring where
  the atom GC already runs.
- **Watermark trigger**: when `_heapTop` crosses a configurable fraction
  of `HeapCapacity`, request a GC at the next safe point (cf. the
  chunk-158 dynamic-buffer compaction watermark). If a GC does not
  recover enough, *then* grow the array.
- **Explicit**: `garbage_collect/0`.
- **Tier-1 IL**: IL-compiled code allocates heap cells too. Its safe
  points are the Call boundaries where it already re-enters the engine
  (Phase 16 threaded dispatch); a GC request is honoured there. IL code
  must not cache raw heap indices across a safe point in CLR locals
  without re-reading post-GC — to be audited as part of implementation.

## Consequences

### Positive

- Heap stays proportional to *live* data, not to total work done.
  Blint's 64 MB high-water collapses to whatever is actually reachable
  (small — the dynamic error DB plus the current parse state).
- Eliminates most of the 127 MB doubling-realloc churn as a side effect
  (the array rarely needs to grow once collection keeps occupancy low),
  cutting per-query .NET allocation and GC pressure.
- Unblocks programs whose deterministic forward computation currently
  has no upper memory bound.

### Negative / risks

- Relocation correctness is the hard part: **every** holder of a heap
  index must be found and rewritten, or the heap corrupts silently. The
  blittable-cell design (no managed refs to chase) and the existing
  trail/CP layout make the root set enumerable, but the test burden is
  high — dedicated suites for each root kind, for cyclic terms, attvars,
  partial strings (PSTR), and GC-during-IL.
- Pause time: a stop-the-world mark-compact pauses the engine. For an
  embedded rules engine this is usually fine; a generational refinement
  (collect a young region first) is a possible later optimisation, not
  part of this ADR.
- PSTR (partial strings) and attvars have their own heap shapes that the
  marker/relocator must understand.

## Alternatives considered

1. **Status quo (grow only).** Rejected — unbounded heap for
   deterministic programs; the measured Blint behaviour is the
   motivation.
2. **Reference counting.** Rejected — cannot collect cyclic terms,
   per-mutation cost on the hot path, and cells holding indices make
   decrement bookkeeping intrusive.
3. **Segmented heap (list of fixed `Cell[]` blocks) to avoid the
   realloc copy.** Addresses only the 127 MB *re-growth churn*, not the
   64 MB *retained garbage*, and taxes the hottest path (`_heap[idx]`
   becomes `_blocks[idx>>k][idx&mask]`). Orthogonal; not a substitute
   for GC. May still be worth doing independently.
4. **Copying (Cheney semi-space) collector.** Viable and simpler to make
   correct than in-place sliding, but needs 2× heap reservation and
   reorders cells, complicating the young-to-old age invariant. Sliding
   mark-compact preserves order and peak footprint; chosen for that.
5. **Generational GC.** Deferred — a refinement on top of the basic
   collector once it exists.

## Implementation sketch (for the follow-up chunks)

1. Mark phase: tri-state mark bits (side array, not in the cell) seeded
   from the root enumeration above; iterative (no C# recursion — long
   list/structure spines must not overflow the CLR stack, cf. chunk-111).
2. Compute forwarding addresses (slide live cells left, order-preserving).
3. Relocate: rewrite cell payloads, trails, CP state, env frames,
   registers, `_heapTop`/`_hb`/catch snapshots.
4. Watermark hook in `EnsureHeapCapacity`; `garbage_collect/0` builtin.
5. Tests: structure/list/cyclic/attvar/PSTR survival and address
   rewrite; GC under deep CP stacks; GC mid-IL; Blint high-water drop.
