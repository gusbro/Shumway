# ADR-016: Heap Garbage Collection

## Status

Shipped ([Phase 20](../../history/phase-20-closure.md)).

Adding a heap GC
is a "major decision" under [the decision policy](../decision-policy.md) (comparable to the atom-GC
strategy in ADR-003), so the design was settled here before code landed;
see "Status of implementation" below for the as-built notes, including
the chunk-213 correction (conservative scan + `Tag.RawInt` control
words) and the measured Blint heap drop.

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

1. **X (argument/temporary) registers** — a conservative scan of the
   whole register bank. Sound because every slot is a tagged `Cell`: a
   stale slot can only over-retain a still-addressable cell, never
   corrupt.
2. **The entire control stack `[0, _stackTop)`** — scanned
   *conservatively*, slot by slot, rather than by precisely walking the
   `E`/`B` chains and each frame's live-permanent count. This is both
   safe and complete: a genuine heap reference (a frame Y-slot, a CP
   saved argument) is a tagged reference cell and gets marked no matter
   which frame owns it, while every **control word** (CE, CP, B, BP,
   arity, trail tops, HeapTop, Hb, ViewGen, B0, perm-count, and the
   captured cut barrier from `get_level`) is stored as a
   `Tag.RawInt` cell (see below) so the marker treats it as a leaf and
   never follows or relocates it. This replaces the original precise
   frame-liveness walk, which **under-counted roots** in the tabling
   fixpoint's reused stack (see the chunk-213 resolution) — a missing
   root silently corrupts the heap, so completeness is non-negotiable
   and the conservative scan buys it cheaply.
3. **Both trails** (ADR-004). The binding trail holds heap indices; the
   extra trail's struct entries carry heap indices and a `CatchFrame`
   snapshots a heap top. All are roots *and* must be relocated.
4. **Attributed-variable tables** and any per-engine auxiliary tables
   that hold heap indices.
5. The **current goal / in-flight structure** being built at the safe
   point (held in registers, covered by 1).

#### `Tag.RawInt` — distinctly-tagged control words

The conservative stack scan only works if a control word can never be
mistaken for a heap reference. Before this design, control words were
written as `new Cell(value)`, which for a small positive value yields
`Tag.Ref` (tag `0x0`) — *indistinguishable* from a genuine heap
self-pointer. A conservative scan would relocate such a word as if it
were a heap index, corrupting (e.g.) a saved choice-point address or a
cut barrier. `Tag.RawInt` (`0xD`, ADR-002 reserved-tag space) tags every
control word distinctly: the marker and relocator treat it as a leaf.
The stored value occupies the 60-bit payload; integer slots round-trip
through a plain `(int)Data` cast (the tag lives above bit 31, so the
cast is unaffected, including the `-1` sentinel), and the one 60-bit
slot (ViewGen) reads via `Cell.Payload`. There is **zero hot-path cost**
— writing a `RawInt` is the same single store as writing any cell. The
two heap-top *boundaries* a CP saves (`HeapTop`/`Hb`) are RawInt-tagged
too, and are relocated specially by a CP-chain walk that maps them
through the forwarding table (they are heap-top counts, not cell
references).

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

### IL audit result (chunk 212)

Audited and **clear**: the Tier-1 IL compiler keeps all WAM state — X
registers and Y slots — in the engine arrays via
`engine.GetRegister`/`SetRegister`/`GetY`/`SetY`. Its CLR locals are
intra-instruction temporaries that hold no heap index across an
instruction boundary. Both IL Call and Execute return to the bytecode
dispatch loop (Phase 16 threaded dispatch); IL tail-call chains loop in
`DispatchToTier1OrBytecode`; non-tail returns come back through the
resume-marker path. At every one of those points all live heap
references are in the engine (registers / Y slots / CPs / trails), and
values live across a Prolog call are in Y slots by WAM convention — so
the GC root set is **identical for Tier-0 and Tier-1**, with no
IL-specific scanning and no extra cost. The watermark check is therefore
placed at those dispatch points and is correct for both tiers.

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

## Status of implementation

- **Chunk 210** — env-frame live-permanent count (`EnvNOffset`) so the
  collector scans Y slots precisely.
- **Chunk 211** — the mark-compact collector (`Engine.HeapGc.cs`) and
  `garbage_collect/0`. Validated by mechanism tests and end-to-end
  explicit-GC tests.
- **Chunk 212** — IL audit (above) + watermark wiring at the dispatch
  safe points + `SHUMWAY_GC_STRESS` fuzz mode. The stress fuzz (collect
  at every safe point) passed plain execution but surfaced a **missing
  root in the tabling / meta-call machinery**: a ground query against a
  tabled predicate corrupted a goal into a `-/2` answer-table pair
  (`existence_error: -/2`); CLP reification, `bagof`/`setof` with `^`,
  and `listing` failed the same way. Auto-collection was held off
  (`GcThreshold = 0`) while the root was hunted.

- **Chunk 213 — root found and fixed; auto-collection re-enabled.** The
  "missing root" was not missing data — it was the *precise* frame-
  liveness root scan **under-counting** in the tabling fixpoint's reused
  stack, compounded by **control words stored as `Tag.Ref`**. The
  `get_level` WAM instruction captured the cut barrier (`B0`, a
  choice-point stack index) into a Y-slot as a plain `new Cell(B0)` —
  which is `Tag.Ref` for a small index, indistinguishable from a heap
  self-pointer. Any GC that scanned that slot relocated the barrier as
  if it were a heap reference; the next `!` then cut to a garbage
  barrier (`IndexOutOfRange` in `CompactTrails`). The fix (chosen for
  correctness over a heuristic, per the project's "must be right for any
  program" bar):
    1. **`Tag.RawInt` (0xD)** — every environment / choice-point control
       word *and* the `get_level` cut barrier is now a distinctly-tagged
       control cell that the marker/relocator treat as a leaf. Zero
       hot-path cost (same single store).
    2. **Conservative full-stack scan** replaces the precise frame-
       liveness walk in `MarkRoots` / `RelocateRoots`: scan every slot
       in `[0, _stackTop)`. RawInt control words are skipped; every
       genuine reference cell is marked/relocated regardless of which
       frame owns it. Complete by construction — no under-count
       possible. `MarkReferents` bounds-guards every payload and only
       follows a `Str` whose target is really a `Functor`, so a stale
       ref in a dead slot over-retains at worst, never crashes.
  Validated: the tabling closure repro and `bagof`/`findall`/`listing`
  run correctly under `SHUMWAY_GC_STRESS=1`; the full test suite is
  green with auto-GC enabled by default. **`EngineConfig.GcThreshold`
  now defaults to `1<<18`** (256 K cells). `SHUMWAY_GC_THRESHOLD=N`
  overrides it at runtime (`0` disables) for measurement.

  **Measured (Blint linting its own 78 KB source, same build):**
  GC off → heap grows to 8,388,608 cells (**64 MB**); GC on → bounded at
  1,048,576 cells (**8 MB**), same lint output. ~8× high-water drop, and
  the 2 M/4 M/8 M doubling-realloc churn is eliminated.

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
