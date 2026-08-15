# ADR-028: Sibling-argument and structure-keyed indexing inside value buckets

## Status

Shipped ([Phase 33](../../history/phase-33-closure.md)).

Tier-0 (WAM interpreter) and Tier-1 (IL, incl.
`--strip-wam` persisted bundles). One new opcode, `switch_on_structure_sub`; the
rest reuses the existing `switch_on_{atom,integer,structure}_arg` and ADR-027's
`switch_on_{atom,integer}_sub`. Completes the indexing work ADR-007 (first/multi-arg)
and ADR-027 (second-level sub-arg, atom/int) began.

Implementation notes (decisions that emerged while building it):

- **Fixed a shipped ADR-027 soundness bug.** A sub-switch whose discriminator is
  **unbound** (a `Ref`, or an unfollowable hop) routed to the *wildcards-only*
  default, which dropped the ground-key clauses whenever a var-headed clause was
  also in the bucket — `p(f(a),1). p(f(b),2). p(X,3).` answered `p(f(Y),R)` with
  only `R=3`. An unbound discriminator can unify with **every** clause in the
  bucket, so the default is now the **full-bucket chain** (matching ADR-027's
  documented "Ref → full group chain" intent). The nested `SubSwitch` carries
  `AllClauses` for this; `LayoutSubSwitch` / `BuildSubTable` / `EmitSubSwitch` use
  it. This is why the per-key buckets and a full default chain are emitted
  separately (they share clause bodies, so only the try/retry/trust dispatch
  duplicates).
- **Sibling set = later cascade args only.** A value bucket at cascade level `li`
  is reached with the *earlier* cascade args unbound (their var-fallthrough led
  here); only args of levels `> li` can be bound at the call, so only those are
  offered as sibling discriminators. (Fully var-arg0 predicates already make
  their first concrete arg the primary index, so they need nothing new.)
- **Atom/int sibling gated at ≥ 3 clauses.** A 2-clause bucket ends in `trust`
  (no leftover choice point), so nesting saves only one head-unify — not worth the
  switch's code size. The audit's own model treats worst-bucket ≤ 2 as
  already-indexed. (List/struct buckets keep ADR-027's ≥ 2, unchanged.)
- **`switch_on_structure_sub` keys a nested-list terminal as the cons functor.**
  The interpreter and the Tier-1 runtime resolver / graph handle it; the Tier-1
  *inline* fast path routes a list terminal to the sound full-bucket default
  instead (correct, just not fast-pathed — a documented first-cut corner).
- **Verified**: all 556 Arity corpus files compile clean (0 errors, 42
  `switch_on_structure_sub` emitted); djota 32/32; the audit's top targets emit
  the nested indexing (`control_has_property`/`object_has_method` nest
  `switch_on_atom_arg` on arg 1, `addlay_p` uses `switch_on_structure_sub`);
  `BucketIndexingTests` (compiler + embedding, all three tiers). Full gate:
  Core 436 / Interpreter 105 / Compiler 322 / Embedding 2878 / ISO 277.

## Context

Shumway's indexed dispatch (`PredicateCompiler.CompileIndexed`) builds a **cascade**
of typed switches, one per indexable argument, chained head-to-tail by their
*var-fallthrough* labels: `switch_on_term` on arg 0, whose var label jumps to a
`switch_on_arg` on the next indexable arg, and so on. Each switch's **value buckets**
(`switch_on_atom` / `switch_on_integer` / `switch_on_structure`, plus the single list
label) jump to a `try`/`retry`/`trust` chain over the clauses that share that key.

The cascade indexes the **first bound argument** of a call. Once a call's arg 0 (or
the first indexable arg) is bound to a ground value, dispatch enters that value
bucket's chain and **no later argument is ever consulted** — the sibling switches
live only on the var-fallthrough path, reachable only when the earlier arg is
*unbound*. So a bucket whose clauses share their arg-0 key but differ at a later
argument is scanned **linearly**, leaving a choice point. Disassembled:

```
h(a,x,1). h(a,y,2). h(a,z,3). h(b,w,9).
   0: switch_on_term  [17, 83, 17, 17]
  17: switch_on_arg   [1, 38, 107, ...]   % VAR-arg0 path: DOES cascade to arg 1
  ...
  83: switch_on_atom  [0]                  % arg0='a' bucket
  88: try[125,3] retry[153] trust[181]     % LINEAR over (a,x),(a,y),(a,z) — arg1 NOT consulted
 107: switch_on_atom_arg [1, 1]            % only on the var path
```

A call `h(a, y, V)` scans `(a,x)` — arg-1 head-unify fails — then `(a,y)` matches,
leaving a CP to `(a,z)`. The `switch_on_atom_arg` that *would* pick `(a,y)` directly
sits on the var-arg0 path and is never reached. First-argument indexing already
isolated the `a` bucket; the missing capability is a **second dimension inside the
bucket**.

Two flavours of the same gap:

1. **Sibling argument** — the bucket's clauses differ at a *later argument* `j`
   (`h/3` above; the Arity property/method/type tables `control_has_property/3`,
   `object_has_method/2`, `define_property_type/4`, the type-mapping families,
   `sql_*` tables — `f(Category, Key, Value)` called Category+Key bound). The keys
   are atoms/ints (mostly) or **compound functors**.
2. **Structure-keyed sub-argument** — the bucket is a list/struct bucket whose
   clauses differ in the **functor** of the list head / a struct sub-arg
   (`addlay_p/2`: `[parse(_)|_]` / `[&(_)|_]` / `[lit(_)|_]` …). ADR-027 handles
   atom/int sub-keys but **explicitly deferred** structure-*keyed* sub-args.

ADR-027 already introduced the machine for "replace a bucket's linear chain with a
nested switch" — but only for the **sub-arg** dimension with **atom/int** keys, and
only on list/struct buckets. This ADR generalises that machine to the **sibling**
dimension and to **structure** keys, and applies it to **all** value buckets.

### Corpus impact (measured, `shumway-disasm --audit`, cut-aware)

The audit models Shumway's real dispatch and categorises every ≥2-clause static
predicate in the Arity corpora (`C:\temp\test` + `C:\temp\testGen`, 556 files,
20 790 predicates). The **net-new** work — value buckets whose arg-0 (or earlier
indexable arg) is a **ground** value, so the cascade will not reach a sibling — is:

| dimension / key | preds | mechanism | notes |
|---|---|---|---|
| **sibling arg, atom/int key** | 447 | reuse `switch_on_{atom,integer}_arg` | avoidable scan (cut-aware) ≈ 2 790 clause-visits; 269 become deterministic |
| **structure-keyed, sub-path** (list head / struct sub-arg functor) | 92 | **new** `switch_on_structure_sub` | the `addlay_p` family; arg0=list/struct |
| **structure-keyed, sibling arg** | 57 | reuse `switch_on_structure_arg` (nested) | arg0=atom/int bucket, functor-valued sibling |

≈ **596 predicates**. Predicates whose arg 0 is *fully* variable are **excluded** —
the cascade already makes their first *concrete* argument the primary index
(`f(_,a,1)…` → `switch_on_atom_arg`; `g(_,p(1))…` → `switch_on_structure_arg`), so
they are already served.

The realised win is **determinism and instruction/CP count, not heap cells** (as in
ADR-027 — the ground call's argument is already on the heap, head matching is
read-mode). Two facts sharpen the value:

- **79 % of these predicates commit via a cut** (a `!` in the body's top-level
  `,`-chain; measured `cut% ≥ 90` on 925 of 1 166). A committing target **prunes the
  trailing match-all/var clauses**, so the realistic residual is the matched
  key-group, not key-group + wildcards. For a *cutting* clause the cut already
  removes the post-match CP, so the index's win there is **skipping the pre-match
  failed head-unifications** (on both the success and the failure path). For the
  ~21 % *non-cutting* predicates the index additionally removes the residual CP
  (true determinism).
- Short chains are **not** marginal: of 714 indexable predicates with a worst-case
  bucket ≤ 4, 654 (92 %) become deterministic or near — skipping 1–2 failed
  head-unifications *per call* on a hot lookup/rewrite predicate, plus the CP.

## Decision

Generalise ADR-027's `SubSwitch` into a **`BucketSwitch`**: a nested typed switch
that replaces any ≥2-clause value-bucket chain, discriminating the bucket by a chosen
**(dimension, key-kind)**:

- **Dimension** — either a **sibling** argument `j` (read `X[j]`) or a **sub-path**
  `(argIdx, sub0, sub1)` into the bucketed argument (ADR-027's bounded 2-hop walk).
- **Key-kind** — `Atom`, `Int`, or **`Structure`** (functor id).

The opcode is picked by (dimension × key-kind); all but one already exist:

| | Atom | Int | Structure |
|---|---|---|---|
| **sibling arg `j`** | `switch_on_atom_arg` | `switch_on_integer_arg` | `switch_on_structure_arg` |
| **sub-path** | `switch_on_atom_sub` | `switch_on_integer_sub` | **`switch_on_structure_sub`** *(new)* |

One new opcode, at the end of the dense dispatch block (after `switch_on_integer_sub`,
before `Meta`; renumber the tail — no released bundles, per the pre-release format
policy):

```
switch_on_structure_sub <argIdx:4> <sub0:4> <sub1:4> <tableId:4>   (17 bytes)
```

**Semantics.** Identical path walk to ADR-027's `_sub` opcodes — hop `sub0`, then
(if `sub1 ≥ 0`) hop `sub1` from `X[argIdx]` via `SubCell` (a list cell exposes
head 0 / tail 1 through the ADR-017 inline cons; a struct exposes `arg[idx]`,
bounds-checked) — but the terminal is keyed by **functor id**: `Tag.Str` → look up in
the structure table (the `switch_on_structure` table format), a list terminal keys as
`'.'/2`, anything else (or a hop miss) → default. `sub1 = -1` is the depth-1 sentinel.

**When it fires.** In `CompileIndexed`, **every** value bucket with ≥ 2 clauses
(`AtomBuckets[k]`, `IntBuckets[k]`, `StructBuckets[k]`, `ListBucket`) becomes eligible
for a `BucketSwitch` — today only `ListBucket` and `StructBuckets` get one (ADR-027,
sub-only). A single **discriminator search** picks the best partition over:

- every sibling arg `j` not already indexed above this bucket, and
- (list/struct buckets only) every sub-path `(0,-1)…(0,K)` / `(0,-1)…(K,-1)`
  (ADR-027's candidate paths),

ranked by the resulting **worst key-group size** (the audit's metric), tie-broken
toward the cheaper mechanism (sibling before sub — no path walk) then the lower
position. The partition must yield ≥ 2 distinct keys of a single kind; otherwise the
bucket keeps its linear chain (no regression). A clause whose chosen
sibling/sub-position is a **variable** — or an unfollowable sub-path — is a
**wildcard**: it merges into every keyed group **and** forms the switch's default
chain (the existing `MergeWithVar` / ADR-027 rule, one dimension wider).

### Why this is sound and never a regression

- **The default label is today's behaviour.** A `BucketSwitch`'s var/default target is
  the full bucket chain. A call whose discriminator argument is **bound** routes to
  the keyed sub-group (skip); a call whose discriminator is **unbound** — the
  enumeration mode, e.g. `control_has_property(subfile, P, V)` with `P` unbound —
  takes the default and enumerates the whole bucket exactly as today. Bound ⇒ faster,
  unbound ⇒ identical. Strictly ≥ current.
- **Solutions and their order are preserved.** Keyed groups and the wildcard default
  keep clauses in source order; the wildcard-merge is a sound over-approximation (it
  only ever adds tried-but-failing clauses, never drops a valid one). This is
  ADR-027's argument, now over the sibling dimension and functor keys.
- **Cut and determinism are unchanged.** Indexing only removes provably
  non-unifiable clauses before they are tried; a `!` commits exactly as before. The
  win *interacts* with cut (a committing target prunes the wildcard tail) but the
  semantics do not.

## Tier-0 implementation

1. **`Opcode.cs` / `OpcodeInfo.cs`** — add `SwitchOnStructureSub` (17 bytes,
   operands `Reg, Reg, Reg, TableId`), renumber the tail. Data-driven disasm renders
   it for free (`sub1 = -1` prints as the depth-1 sentinel, as for the atom/int subs).
2. **`BytecodeEmitter.cs`** — `EmitSwitchOnStructureSub(argIdx, sub0, sub1, tableId)`,
   mirroring `EmitSwitchOnIntegerSub`; tableId relocation site at the same offset,
   tracked in `switchTableIdSites`.
3. **`BytecodeInterpreter.cs`** — one dispatch case beside `SwitchOnIntegerSub`,
   reusing the ADR-027 `SubCell` hop for the walk, then the `switch_on_structure`
   functor-table lookup for the terminal.
4. **`PredicateCompiler.cs`** — the core generalisation:
   - Rename/extend `SubSwitch` → `BucketSwitch` with a `Dimension`
     (`Sibling(j)` | `Sub(argIdx, sub0, sub1)`) and a `KeyKind` (`Atom`/`Int`/`Struct`).
   - `TryBuildSubSwitch` → `TryBuildBucketSwitch(bucket, clauses, availableSiblings)`:
     probe the sibling args (via `ClassifyArg`, already used for the top-level
     buckets) **and** the ADR-027 sub-paths (via `ClassifySubPath`, extended to
     return a `Struct` functor key), rank by worst-group size, build the winner.
   - Apply it to `AtomBuckets` / `IntBuckets` too (not just `ListBucket` /
     `StructBuckets`), in the pass-1 layout and the pass-3 emit — the emit already
     branches `ListSub != null ? EmitSubSwitch : EmitChain`; extend that to every
     bucket, choosing the emitter by (dimension, key-kind).
   - Emit reuses `EmitChain`, `BuildTable`, the `SwitchTable` type and the
     `switchTableIdSites` relocation. Sibling switches use the existing
     `EmitSwitchOn{Atom,Integer,Structure}Arg`; sub switches use the `_sub` family.

The dynamic-predicate path (`CompileIndexedDynamic`) mirrors the same change or, in the
first cut, keeps its current chain (dynamic predicates are Tier-0-only and mutation-heavy;
the win there is smaller — a follow-up).

## Tier-1 (IL) implementation

The IL index re-encodes the WAM switch cascade; ADR-027 already taught both resolvers
the atom/int sub opcodes and nested-switch-from-a-value-table shape:

- **`switch_on_structure_sub`** — add to `IlIndexedDispatch` (`IsDispatchSwitch`,
  `OpcodeSize`, a `ResolveEntryCursor` case with a `StructureSubTarget` that walks
  `Sub0`/`Sub1` via the shared `WalkSubOrMiss` then keys by functor) and to the
  WAM-independent `IlIndexGraph` (a `Structure` node gains `Sub0`/`Sub1`), persisted
  via `IndexGraphCodec`. Mechanical — the functor table and clause-body entry cursors
  already exist for `switch_on_structure`.
- **Nested `switch_on_*_arg` reached from a value-table entry** — ADR-027's
  sub-switches are already reached from list/struct table targets and handled by both
  IL modes, so a `switch_on_atom_arg` reached from an *atom* table entry is the same
  shape; verify the chain-collection (`TryDescribeBytes` / `ResolveEntryCursor`)
  follows it and add coverage. No new IL emit — the targets are ordinary body cursors.

A bucket-indexed predicate promotes to Tier-1 like any other; verify in-process and
cross-process (`--strip-wam` and full-WAM), as ADR-027 did.

## Consequences

- **Initial coverage**: one nested `BucketSwitch` per value bucket, discriminating on a
  single best position. This matches the audit's single-discriminator model and
  captures the measured wins.
- **Deferred** (symmetric follow-ups): recursively re-partitioning a keyed sub-group
  that is itself ≥ 2 (deeper nesting); mixed atom+int keys at one position (as
  ADR-027); sub-paths deeper than 2 hops; and the same treatment for **dynamic**
  indexed predicates (`CompileIndexedDynamic`).
- **Code size**: a `BucketSwitch` adds ~17–21 bytes + a table per partitioned bucket.
  Bounded and strictly runtime-positive; if size becomes a concern, gate on bucket
  size (e.g. only ≥ 3) or reuse the chunk-75 JIT-indexing hotness gate. Static
  predicates pay it once at compile time.
- **Relationship to ADR-021** (register allocator, rejected): orthogonal — this is a
  dispatch-shape change, not a register concern.

## Verification

- **Compiler unit** (`BucketIndexingTests`): `h(a,x,1). h(a,y,2). h(a,z,3). h(b,w,9).`
  → a `switch_on_atom_arg` nested inside the `a` bucket, its linear chain gone;
  `addlay_p`-shape distinct list-head functors → `switch_on_structure_sub`;
  functor-valued sibling in an atom bucket → nested `switch_on_structure_arg`;
  all-same-key / no-discriminator / mixed-kind → **no** fire (plain chain kept).
- **Interpreter + Embedding, both tiers**: a bound-discriminator call yields the one
  right answer with **no** choice point (determinism probe / `LastQueryCellsAllocated`
  delta); the **unbound-discriminator** call still enumerates the whole bucket
  (enumeration mode unaffected); wildcard-merge and cut-interaction cases verified;
  re-run under `SHUMWAY_IL_PROMOTE=1` for Tier-1 parity.
- **Corpus regression**: `C:\temp\test` + `C:\temp\testGen` produce **identical
  output**; djota **32/32**; back-to-back `--alloc`/opcode A/B on
  `control_has_property` / `object_has_method` / `addlay_p` shows the CP + opcode drop
  with flat cells.
- **Full five-project gate**: Core / Interpreter / Compiler / IsoConformance /
  Embedding.
- **ADR-027** cross-reference updated (its deferred "structure-keyed sub-arg" is now
  delivered here); the repository-root CLAUDE.md decision table gains an ADR-028 row.
```
