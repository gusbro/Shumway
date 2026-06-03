# ADR-017: Inline compound cell references (2-cell cons)

## Status

**Accepted; phase 1 (lists) implemented** (Phase 25). List (and, by the
same mechanism, structure) construction is core to a Prolog engine and
currently allocates one more heap cell per compound than the standard
WAM. Changing the on-heap shape of every list and structure is a "major
decision" under CLAUDE.md (coherence-critical, comparable to the trail
format or the heap-GC strategy), so the design was settled here before
code landed.

**Status of implementation.** Phase 1 (inline 2-cell lists) landed in
chunk 289: `PutList` and `GetList`'s plain-variable write branch now
store an inline `Lis` cell instead of a `Ref` to an on-heap header. The
heap-GC root scan needed no change — it is fully conservative (every
register / stack slot is fed to the tag-dispatching `MarkReferents` /
`RelocateCell`, which already handle `Lis`/`Str`/`Float`/`Pstr`), so
inline compounds in roots were already relocated correctly. The attvar
sub-case of `GetList` keeps the on-heap header (it binds via
`BindAttVarToValue`, which stores a `Ref` to a heap home; the heap GC
bails while attvars are live anyway). Measured by `--alloc`: nreverse
−28.8 %, flatten −18.7 %, qsort −13.8 %, boyer −5.9 % cells/iter;
arithmetic-only benches (tak, crypt) unchanged. All suites green (Core
423, Compiler 256, ISO 275, Embedding 2007).

**Phase 2 (structures) — investigated and deferred.** Inlining `Str`
(`PutStructure` / `GetStructure`) the same way was prototyped and
measured: it wins big on structure-*building* code (sendmore −25 %,
crypt −25 %, queens −15 %, serialize −9 %, tak −7.5 % cells/iter) but
**regresses zebra by +45 %** — and that regression is *not* fixable by
the obvious means. Root cause: a structure unified as a whole via
`get_value` needs a heap address, so Shumway's address-based `Unify`
calls `MaterializeRegister`. With the old `Ref`→on-heap-`Str` shape the
structure already lived on the heap, so materialisation was free and
survived backtracking. With an inline `Str` the structure is anchored in
the register, so each `get_value` re-copies the `Str` header to the heap
(+1 cell) — and "globalising" the register (rewriting it to a `Ref` after
the first copy) does **not** help, because backtracking restores the
register to its saved inline value and the next forward pass re-copies.
zebra unifies whole `house/5` terms under massive `member/3` backtracking,
so the per-unification copy dominates. Lists never hit this: they are
walked element-by-element by `get_list` in *read* mode, which never
materialises. The proper fix is a **cell-based unification path** that
unifies a register-held inline compound directly, without first copying
it to a heap address — a larger change than phase 2 itself. Until that
exists, inline structures are a net loss for unify-heavy + backtracking
workloads, so phase 2 stays **deferred**. Phase 1 (lists) stands on its
own: lists are the project's primary use case (DCGs / grammar / parsing)
and the list win has no such counter-case.

This ADR does **not** change the cell layout of ADR-002 (still 8 bytes,
4-bit tag + 60-bit payload, same `Lis` / `Str` tags). It changes only
*where* a `Lis` / `Str` cell is stored — inline in the slot that refers
to the compound, instead of in a separate heap cell reached through a
`Ref`.

## Context

### The current representation: 3 cells per cons

Shumway represents a compound as a tagged header cell that lives **on the
heap**, reached from its referrer through a `Ref`. For a list cell
`[H|R]`, `Engine.PutList` does:

```csharp
int h = AllocateHeap(1);
_heap[h] = Cell.Lis(h + 1);   // the LIS header cell, on the heap
_registers[regIdx] = Cell.Ref(h);
_writeMode = true;
_unifyPointer = h + 1;        // head goes to h+1, tail to h+2
```

The following two `unify_*` opcodes (write mode) each `AllocateHeap(1)`
for the head and the tail. So a single cons occupies **three** heap
cells:

```
h     : Cell.Lis(h+1)     <- the header, referenced by Ref(h)
h+1   : head
h+2   : tail
```

`PutStructure` / `GetStructure` use the identical pattern for `f(...)`: a
`Str` header cell on the heap plus a `Functor` cell plus the argument
cells, reached through `Ref(h)`.

### The standard WAM representation: 2 cells per cons

In the canonical WAM a list value is a `LIST`-tagged **pointer** carried
in the referring slot (a register, an environment Y-slot, a structure
argument, or a parent cons's tail). The pointer addresses a 2-cell pair
`[head, tail]`. There is **no separate on-heap header cell** — the `Lis`
tag rides in the referrer. A cons is **two** heap cells:

```
p     : head
p+1   : tail
```

and the thing that names the list (register / var / parent tail) holds
`Cell.Lis(p)` directly.

### Why this matters (measured)

The Phase-25 deterministic allocation counter (`--alloc`, chunk 287)
reports WAM cells reserved per iteration — a noise-free metric. On the
Van Roy suite the figure is dominated by structure building, not by
unification or arithmetic:

| Benchmark | cells/iter | character |
|---|---:|---|
| nreverse | 1615 | almost entirely cons building |
| qsort | 2877 | list partition / append |
| flatten | 1232 | list building |
| zebra | 29597 | list-heavy constraint search |

The separate `Lis` header is **one of the three cells of every cons**, so
eliminating it removes ~⅓ of the heap cells these programs allocate —
directly, deterministically, and provably via `--alloc`. Heap allocation
is the dominant cost in list-heavy Prolog; it also drives heap-GC
frequency (ADR-016), so fewer cells means fewer collections too.

A Prolog engine that builds every cons 50 % larger than GNU Prolog cannot
meet the project's "comparable to or better than GNU Prolog" performance
target on grammar / list workloads — the primary use case (DCGs, parsing).

### What other engines do (verified)

The classic WAM (Aït-Kaci, §2) special-cases lists precisely to avoid a
header: `put_list` writes `<LIS, H>` directly into the referring slot and
the heap holds only the 2-cell pair `[head, tail]`. There is no list
header cell and no functor cell, because `./2` is the one fixed functor.
(Structures, by contrast, do carry a functor cell — `f(t1..tn)` is
functor + n args — because the functor varies.)

| Engine | cons `[H|T]` | Mechanism |
|---|---:|---|
| **GNU Prolog** | **2 cells** | classic WAM `LST` tag → `[head, tail]` pair, tag inline in the referrer ("a List Cell points to the Head Cell") |
| **SICStus** (Quintus WAM lineage) | **2 cells** | the standard WAM list optimisation |
| **Scryer Prolog** | **2 cells** | `Lis` heap-cell tag → `[head, tail]`; plus compact *partial strings* for char/code lists (an up-to-24× win on strings specifically) |
| **YAP** | **2 cells** | classic WAM list pair |
| **SWI-Prolog** | **3 cells** | lists are ordinary compounds `'[|]'/2` (functor word + head + tail) — chosen for representational uniformity, **not** compactness |
| **Shumway (today)** | **3 cells** | `Ref` → on-heap `Lis` header → `[head, tail]` (a header *and* an indirection) |
| **Shumway (this ADR)** | **2 cells** | inline `Lis` tag → `[head, tail]` — i.e. the GNU/SICStus/Scryer scheme |

So 2-cell cons is **the** efficient standard, used by every
performance-oriented WAM (GNU Prolog, SICStus, Scryer, YAP). SWI is the
outlier at 3 cells, and is *less* compact than this proposal — it is not
a model to follow here. Nothing mainstream is more compact than 2-cell
for *general* lists; the only more-compact techniques are narrower:

- **CDR-coding** (BinProlog) — folds a tail's functor cell into the
  preceding argument's slot. More compact for *proper, ground* lists, but
  it complicates the GC and partial-list handling and breaks the
  "every cons is uniform" property. See Alternative B; a possible future
  ADR, not this one.
- **Packed / partial strings** — a large win, but only for the special
  case of character / code lists. Shumway **already has this** (PSTR,
  `docs/design/pstr-design.md`), as do Scryer and SWI. It is orthogonal
  to and composes with 2-cell cons for the general case.

## Decision

Adopt the standard WAM representation: **a compound is named by a `Lis`
/ `Str` cell stored inline in the referring slot**, addressing the
argument cells directly. Drop the separate on-heap header cell.

- A cons `[H|R]` becomes **2 cells** (`head`, `tail`); the referrer holds
  `Cell.Lis(headIndex)`.
- A structure `f/n` becomes **functor + n args** (the `Functor` cell is
  still required to carry the functor id); the referrer holds
  `Cell.Str(functorIndex)`. The standalone `Str` header cell is dropped.
- `Deref` is unchanged: it follows `Ref` and stops at any non-`Ref` cell,
  so it already lands on an inline `Lis` / `Str` exactly as it lands on an
  `Atom` / `Int` today.

### Why this is a smaller change than it looks

The representation is **already hybrid**. Registers and environment slots
hold *value* cells directly today — an `Atom`, an `Int`, the `nil` atom —
and only fall back to a heap home when one is genuinely required.
`MaterializeRegister` is the existing bridge:

```csharp
private int MaterializeRegister(int regIdx)
{
    Cell c = _registers[regIdx];
    if (c.Tag is Tag.Ref or Tag.AttVar) return c.AsHeapIndex;
    int slot = AllocateHeap(1);   // copy a direct value cell to the heap
    _heap[slot] = c;
    return slot;
}
```

Extending "a slot may hold a value cell directly" from `Atom`/`Int` to
`Lis`/`Str` is consistent with this design, not a new concept. And the
hot **consumers already tag-dispatch on the slot's own cell** rather than
assuming a `Ref`:

- `GetStructure` / `GetList`: `if (regCell.Tag is Tag.Ref or Tag.AttVar)
  { deref; } else { finalCell = regCell; }` — an inline `Lis` / `Str`
  register already takes the non-`Ref` branch and matches via the
  `Tag.Lis` / `Tag.Str` case.
- `Unify`, structural equality, `==/2`, the materialisers: all switch on
  the dereferenced cell's tag and read `AsHeapIndex` as the argument
  pointer — identical whether the `Lis` cell sat inline or on the heap.

So the change is concentrated in the **producers** plus the **GC root
scan**, not spread across every consumer.

## Blast radius

| Area | Change | Risk |
|---|---|---|
| `PutList` | store `Cell.Lis(pairStart)` in the target slot; allocate only head+tail | low |
| `GetList` write-mode branch | bind the var to an inline `Cell.Lis(pairStart)`; allocate head+tail | low |
| `PutStructure` / `GetStructure` write-mode | store `Cell.Str(functorIdx)` inline (phase 2) | low |
| Var→compound binding in `Unify` | bind the unbound var to the inline `Lis`/`Str` cell, not a `Ref` to a heap copy | medium |
| **Heap GC root scan (ADR-016)** | the conservative scan of registers / Y-slots / CP-protected slots must treat an inline `Lis`/`Str`/`Pstr` payload as a relocatable heap index (today only `Ref`/`AttVar` in those slots point into the heap) | **high — the one correctness-critical item** |
| First-arg indexing (ADR-007, `switch_on_term`) | the arg-tag read must classify an inline `Lis`/`Str` the same as a deref'd one | medium |
| Materialiser / `TermReader` | already tag-dispatch + walk the list spine iteratively (chunk 111); audit for any `Ref`-assuming path | medium |
| List-building builtins (~31 `Cell.Lis(` sites: `findall`, `sort`, `atom_codes`, `=..`, …) | return the compound inline instead of `AllocateHeap; _heap[h]=Cell.Lis(h+1); Ref(h)` | medium (mechanical, many sites) |
| PSTR ↔ list interop (`UnifyPstrLis`) | audit the list-cell construction it shares | low |
| `MaterializeRegister` / `MaterializePermanent` | unchanged — already copy a direct value cell to a heap slot when an address is forced (re-adds one cell only in that rare case, equal to today) | none |

The heap-GC root scan is the item that must be gotten exactly right: a
root holding an inline `Lis`/`Str` that the scan fails to recognise as a
heap pointer would be neither marked nor relocated, corrupting the term
after a collection. ADR-016's precise heap walk already relocates `Lis`/
`Str` cells *on the heap*; the new work is recognising them *in roots*.
The `Tag.RawInt` control-word tagging (Phase 20 chunk 213) already lets
the scan distinguish heap pointers from control words, so the predicate
"is this slot a heap pointer" gains `Lis`/`Str`/`Pstr` alongside `Ref`/
`AttVar` — a localized, testable extension.

## Migration plan (phased, each step `--alloc`-validated)

1. **Lists first** (the priority and the bigger win): `PutList`,
   `GetList` write-mode, the var→list bind path, the GC root scan for
   inline `Lis`, `switch_on_term` arg classification, and the
   list-building builtins. Gate: `--alloc` shows ≈⅓ fewer cells on
   nreverse / qsort / flatten / zebra; full suite green (Core, Compiler,
   ISO); the heap-GC stress paths (the tabling fixpoint, deep list
   materialisation, Blint) stay sound.
2. **Structures second** (same mechanism for `Str`): `PutStructure`,
   `GetStructure` write-mode, GC for inline `Str`, structure-building
   builtins. Lists and structures may legitimately run mixed between
   steps 1 and 2 because every consumer tag-dispatches; there is no
   correctness coupling, only a transient representational asymmetry.
3. **Optional follow-up** (separate, *not* part of this ADR): fuse
   `put_list`+`unify`+`unify` into a single opcode to cut dispatch
   overhead. That is a *time* optimisation (same cell count), so it is
   measured by wall-clock, not `--alloc`.

## Alternatives considered

- **A. Keep the 3-cell layout, fuse the allocation** (`AllocateHeap(3)`
  once instead of three calls). Saves dispatch / bounds-check overhead
  but **zero cells** — it does not touch the actual overhead this ADR
  targets. Rejected as the primary fix (it is the step-3 follow-up).
- **B. CDR-coding** (a proper list of *k* ground elements in *k*+1
  contiguous cells). A larger saving than 2-cell cons, but it
  special-cases proper vs partial lists, complicates unification and the
  GC, and breaks the "every cons is uniform" property that keeps the
  interpreter simple. Out of scope; a possible future ADR once 2-cell
  cons is in and measured.
- **C. Inline compound references (this ADR).** Standard WAM, ~⅓ fewer
  cells on list-heavy code, `--alloc`-provable, blast radius concentrated
  in producers + the GC root scan. Chosen.

## Consequences

**Positive**

- ~⅓ fewer heap cells on list/structure-heavy programs (deterministic,
  measured by `--alloc`), the dominant cost in the project's primary
  grammar / list use case.
- Fewer heap-GC cycles (ADR-016) as a second-order effect of allocating
  less.
- Brings list representation in line with the efficient WAM standard used
  by every performance-oriented engine we compare against — GNU Prolog,
  SICStus, Scryer, YAP (see "What other engines do"). SWI's 3-cell
  `'[|]'/2` lists are *less* compact, so matching SWI is not the goal;
  matching GNU Prolog / Scryer is.

**Negative / cost**

- A coherence-critical change to the heap-GC root scan, which must be
  proven with the existing GC stress suites before the change is trusted.
- ~31 builtin list-construction sites to convert (mechanical but
  numerous).
- A transient mixed representation (inline lists, `Ref`-based structures)
  between migration steps 1 and 2 — safe because consumers tag-dispatch,
  but worth noting for anyone reading the code mid-migration.

**Invariants preserved**

- ADR-002 cell layout is untouched (no new tag, same 8-byte cell).
- ADR-004 trailing is unchanged: binding a var to an inline compound
  trails the var address exactly as before; the head/tail cells sit above
  HB and are reclaimed by the normal heap-top restore on backtracking.
- The young-to-old binding rule and the HB trailing check are unaffected.

## Validation

- `--alloc` before/after on every Van Roy benchmark: lists must drop ≈⅓;
  arithmetic-only benches (tak, crypt) must be unchanged (a non-zero
  delta there would signal an unintended path change).
- Full test suites green: `Shumway.Tests.Core`, `Shumway.Tests.Compiler`,
  `Shumway.Tests.IsoConformance`.
- Heap-GC soundness: the ADR-016 stress paths — the tabling alternating
  fixpoint, deep list materialisation (chunk 111), `garbage_collect/0`
  under a half-built list, and a full Blint run — must produce identical
  answers with the GC watermark forced low.
- Wall-clock (`--vanroy`, hyperfine for the externals) as a secondary
  cross-engine positioning check, read with the `tot_sd%` noise column in
  mind.
