# ADR-019: Inline nested structure construction (`unify_structure` / `unify_list`)

**Status:** Accepted — implemented chunk 314. Blint: get_list/get_structure
2812 → 1387 (−51%), total WAM instructions 16008 → 14582 (−8.9%); `unify_list`
emitted 1401×. Green across Tier-0, in-process Tier-1 IL, and cross-process
persisted IL.

## Context

Comparing our generated WAM to GProlog's `pl2wam` on Blint
(`docs/wam-vs-gprolog-blint.md`, gap **C**) showed our biggest pervasive codegen
disadvantage is **nested compound / list construction**.

When a clause builds a term with a nested compound argument — `Out = f(g(X))`,
`L = [a, Name, c]`, `assert_once(default_extension_i(Ext))` — we use the
chunk-8b two-pass BFS: the outer `put_structure` / `put_list` writes a fresh
**temporary variable** for the nested arg, and a deferred `get_structure` /
`get_list` later builds the nested part into that temp. GProlog instead
continues the write-mode stream straight into the nested structure with
`unify_structure` / `unify_list`. Cost of our approach: **+1 instruction and +1
temporary register per nesting level**. GProlog emits `unify_structure` /
`unify_list` 221× across its 89 Blint predicates; we emit zero (we have no such
opcode) and lean on `get_list` / `get_structure` 2812× across 256 predicates.

Lists are the worst case: every list literal `[a, b, c]` =
`[a | [b | [c | []]]]` nests once per element, so an N-element list costs N−1
temps + N−1 `get_list`s today.

## Decision

Add two write-mode opcodes that allocate a nested compound at the current unify
pointer and redirect the write stream into it:

- **`unify_structure <functorId>`** (write mode): allocate the nested FUNCTOR
  cell, write a `Str` ref to it into the current outer arg slot, set
  `UnifyPointer` to the nested functor's first arg. (Read mode: like
  `get_structure` against the cell at `UnifyPointer`.)
- **`unify_list`** (write mode): allocate the 2-cell `[head, tail]` cons,
  write a `Lis` ref to it into the current outer arg slot, set `UnifyPointer`
  to the cons head. (Read mode: like `get_list` against the cell at
  `UnifyPointer`.)

Both leverage the **contiguous heap allocation** that `PutStructure` /
`PutList` already rely on (ADR-017 inline cons: no separate header cell), so no
new machinery is needed in write mode — the pointer simply advances into the
nested structure's cells, which sit immediately after the outer arg slot.

### Scope: last-argument position only (linear, no write-stack)

Inline nested building is emitted **only when the nested compound is the LAST
argument of its parent** — which is *always* true for a list tail, and true for
single-arg wrappers and last-position compounds. In that case the build is
purely linear: after the nested structure there are no more parent args to
return to, so `UnifyPointer` never needs to "resume" the parent and **no
write-pointer stack is required**.

A nested compound in a **non-last** argument position (`f(g(X), Y)`) keeps the
existing BFS (temp + deferred `get_structure`), because resuming `Y` after
`g(X)` would need a write-pointer stack. This is the minority case; last-arg /
list nesting is the pervasive one and captures GProlog's win. (A future ADR may
add the write-pointer stack for full generality if measurement justifies it.)

### Read mode

In read mode (head matching against an existing term), `unify_structure` /
`unify_list` dereference the cell at `UnifyPointer` and match the functor / cons,
positioning `UnifyPointer` at its first argument — the same nesting the build
side produces, so head matching of a nested last-arg compound can also drop its
temp. (Head-side adoption is optional / a follow-up; the build side is the win.)

## Consequences

- **New opcodes** — a Major Decision per [the decision policy](../decision-policy.md); this ADR is the proposal.
  Opcode ids in the `0x40` unify family (next free after `UnifyVoid = 0x48`).
- Touches: `Opcode` / `OpcodeInfo` (ids + sizes — `unify_structure` carries a
  4-byte functor id = 5 bytes; `unify_list` = 1 byte), `Engine` (two write-mode
  helpers), the Tier-0 interpreter dispatch, the Tier-1 IL emit, the
  disassembler, and `ClauseCompiler` (emit them for last-arg nested compounds
  instead of the BFS defer).
- **GC**: the nested cells are ordinary heap cells reachable through the parent;
  the order-preserving mark-compact collector (ADR-016) handles them unchanged.
- **Correctness**: equivalent to the BFS build (same heap shape), just fewer
  instructions and no temp. Validated against the full suite + the Blint WAM
  diff (the temp+get pattern should drop for every last-arg / list build).
- **Win**: −1 instruction and −1 temporary register per inlined nesting level;
  pervasive (every list literal, every last-arg wrapper).
