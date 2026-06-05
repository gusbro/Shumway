# ADR-020: Inline non-last nested compound build (reserve-upfront write mode)

**Status:** Accepted — implementing (Phase 27, theme 3). Extends ADR-019 (which
inlined nested compounds only in LAST-argument position) to **non-last**
positions. Measurement: Blint defers 1119 non-last nested compounds to the BFS
(temp + `get_structure`), all inlinable — a register/instruction win of the same
order as ADR-019's last-arg win (`unify_list` 1401×).

## Context

ADR-019 inlines a nested compound into the write stream (`unify_structure` /
`unify_list`) only when it is the LAST argument of its parent, because the
write model is **allocate-on-demand**: each `unify_*` allocates one heap cell at
the top as it runs, so the parent's argument cells are laid down contiguously
one per `unify_*`. A nested compound in a non-last position would advance the
heap top past where the parent's *remaining* args must sit, breaking the
parent's contiguity. So `f(g(X), Y)` keeps the chunk-8b BFS: write a temp var
into `g`'s slot, finish the parent, then build `g` into the temp via a deferred
`get_structure` (+1 instruction, +1 temp register per non-last nesting level).

GProlog also BFSs non-last nested compounds (`docs/wam-vs-gprolog-blint.md`,
`tokenize_one_pred/3`: "the nested `token(...)` is the list HEAD (non-last) so
both BFS it"), so this is a **beat-GProlog** optimisation, not a parity gap.

## Decision

For a structure whose term tree contains **any** non-last nested compound, build
the whole tree in **reserve-upfront** write mode with an auto-popping
write-pointer stack. Structures with no nesting, or only last-arg nesting (every
list literal), keep the existing zero-overhead allocate-on-demand path
unchanged — so the hot common path is untouched.

### Two new opcodes (carry their reserve size at compile time — no runtime lookup)

- **`put_structure_r <functorId> <regIdx> <argCount>`** (13 bytes): allocate
  `argCount + 1` contiguous cells (functor + args), write the functor, store an
  inline `Str` ref in `X[regIdx]`, enter **reserved** write mode, push a base
  frame `(remaining = argCount)`, set `UnifyPointer` to the first arg. `argCount`
  is the parent arity — known at WAM-emit time, so it is baked as an operand;
  the runtime never does `FunctorTable.Lookup` for the size.
- **`put_list_r <regIdx>`** (5 bytes): reserve 2 cells (cons), `Lis` ref into
  `X[regIdx]`, reserved write mode, base frame `(remaining = 2)`, `UnifyPointer`
  at the head.

### Reserved write mode (a runtime flag `_reservedWrite`)

- Set true by `put_structure_r` / `put_list_r`. Set false by `put_structure` /
  `put_list` / `get_structure` / `get_list` (the on-demand / read entries) and
  when the base frame pops (build complete). So a stale flag can never reach an
  on-demand build.
- A scalar `unify_*` (atom/integer/nil/variable/value/void) in reserved mode
  WRITES at the pre-reserved `UnifyPointer` cell (no `AllocateHeap`), advances,
  and calls `OnArgWritten` — decrement the top frame's `remaining`, and
  cascade-pop every frame that reaches 0, restoring `UnifyPointer` to the popped
  frame's saved parent-resume pointer.
- `unify_structure` / `unify_list` in reserved mode write the nested `Str`/`Lis`
  ref into the current parent slot, decrement the parent frame (this slot is
  filled), allocate the nested cells at the heap top, and push a frame
  `(resume = parentSlot + 1, remaining = nestedArity)`. When the nested's last
  scalar pops it, the cascade resumes the parent — and pops the parent too if
  the nested was its last arg. So last-arg and non-last nesting are handled
  uniformly; the per-level temp + deferred `get_*` are both dropped (−1 temp,
  −1 instruction per non-last level).

### Scope: body (build) only for now

The reserve frame stack is WRITE-mode only. Body-goal argument building is pure
write mode (rooted at `put_structure` / `put_list`, never matching an existing
term), so the compiler emits the `_r` opcodes there. **Head matching** (read
mode, where read/write interleave on a partially-bound term) keeps the BFS for
non-last nesting — the same bytecode can run read or write at runtime depending
on the caller's bindings, and read-mode resume is out of scope here. A future
ADR may extend reserved mode to head matching if measurement justifies it.

## Consequences

- **New opcodes** — a Major Decision per `CLAUDE.md`; this ADR is the proposal.
  Ids `0x2C` / `0x2D` in the put family.
- Touches: `Opcode` / `OpcodeTable` (ids + sizes), `Engine` (reserved-mode
  helpers + the write-pointer frame stack), the Tier-0 interpreter dispatch
  (reserved branch in each `unify_*` + the two new put opcodes), the Tier-1 IL
  emit (the two new put opcodes; the existing `unify_*` helper calls reach the
  reserved branch inside the shared `Engine` methods), the disassembler, and
  `ClauseCompiler` (emit `_r` + inline non-last nested in body building when the
  tree has non-last nesting).
- **GC**: reserved-but-unwritten cells are transient — no safe point (goal
  boundary) occurs between a `put_*_r` and its `unify_*`, so the order-preserving
  collector (ADR-016) never observes them. The frame stack is ephemeral within a
  single structure build (no choice point spans it), so it is not trailed.
- **Hot path**: untouched — only terms with non-last nesting take the reserved
  path. No `FunctorTable.Lookup` added anywhere (the size is baked).
- **Correctness**: equivalent heap shape to the BFS build; validated against the
  full suite + the Blint WAM diff (the temp + `get_*` pair drops for every
  non-last nested level).

## Future work: head matching (measured, deliberately not done)

Head matching keeps the BFS for non-last nested compounds. Extending reserve-
upfront to the head would require the frame stack to work in **read** mode and to
handle the WAM **read/write mode flip per argument** (a head structure matches an
existing term, but any unbound sub-arg flips to build mode mid-match) — the
hottest path in the engine and exactly where read/write toggling bugs hide.

Measured ceiling on Blint: of the 337 `get_structure`/`get_list` remaining after
this ADR, **266 (79%) are top-level head-arg matches** — intrinsic (decoding the
caller's argument; no inline can remove them) — and only **71 (21%) are nested
deferrals** (head non-last; the head last-arg case is already inlined read-mode
by ADR-019, and body inlinable nesting by this ADR). So the head extension's
upper bound is ~71 instructions vs this ADR's 1048 in the body (~15× smaller) for
a far harder, riskier change. Not worth it; recorded here should a future
measurement on a match-heavy program change the calculus.
