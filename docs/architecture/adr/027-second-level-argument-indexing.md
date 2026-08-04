# ADR-027: Second-level (sub-argument) argument indexing

## Status

**Accepted — implemented (Phase 33).** Tier-0 (WAM interpreter) and Tier-1 (IL,
including `--strip-wam` persisted bundles). Two new opcodes, `switch_on_atom_sub`
and `switch_on_integer_sub`.

## Context

Shumway indexes clauses by the **type/value of an argument register**: a
`switch_on_term` on arg 0, chained to `switch_on_arg` on later positions, plus the
typed value tables `switch_on_atom` / `switch_on_integer` / `switch_on_structure`
(and their `_arg` multi-argument variants). This is first-/multi-*argument*
indexing (ADR-007 + chunk 67).

It does not index one level **deeper** — on a *sub-term* of an argument. When a
group of clauses shares the same top-level shape at the indexed position but
differs in the **head of a list** or the **argument of a compound**, the compiler
falls back to a linear `try`/`retry`/`trust` scan and leaves a spurious choice
point. Two shapes, disassembled:

```
% list head — djota DCG input, tokenizers
tok([a|T],T). tok([b|T],T). tok([c|T],T). tok([d|T],T).
   switch_on_term routes a list arg to ONE label -> linear try/retry/retry/trust

% compound sub-argument — Arity SQL compiler evalsql.pl
expression_operand(e(1,_)). expression_operand(e(29,_)). ...
   switch_on_structure picks functor e/2 -> linear scan; the OpCode is NOT indexed
```

A three-corpus survey (djota; the Arity `C:\temp\test` and `C:\temp\testGen`
generator sources) established that the highest-value shapes for the Arity-compat
workload are:

- **Compound sub-argument** — `expression_operand(e(OpCode,…))`, `sql_case/9`
  (the recursive SQL expression/condition compiler), keyed on an integer/atom
  sub-argument of a struct already selected by `switch_on_structure`.
- **Token stream** — `pred([t(Sym,Code)|Tail], …)`, the characteristic Arity
  parser idiom (166 predicates across the two corpora). The list head is a
  compound `t/N`; the discriminator is a **sub-argument of the head** — a
  *depth-2* path. The single largest genuine dispatch predicate in any corpus is
  `action/32` (dispatch on the first token's symbol atom); the hottest is
  `print_cmd/41` (dispatch on the token's **integer** code at sub-arg 1, with the
  symbol written `_`).
- **List head (car)** — `[Type|_]`, `[char|_]` (djota `list_type//1`, Arity
  `bigger_type/4`). Modest but real.

None of these are reachable by first-argument indexing: the list head is uniformly
`[_|_]`, the token functor uniformly `t`, the struct functor uniformly `e`.

## Decision

Add a **bounded 2-hop sub-argument switch**. Both list-head and struct-sub-arg are
the same operation — *index on sub-argument `j` of the compound in register `k`* —
so one generic opcode family covers all three shapes, with a `<subIdx>` path that
picks the best-discriminating position (crucially **not** hardwired to the token
*symbol*: `print_cmd` keys on the integer at sub-arg 1).

Two opcodes, added at the end of the dense dispatch block (before `Meta`;
renumbered the tail — no released bundles, per the pre-release format policy):

```
switch_on_atom_sub    <argIdx:4> <sub0:4> <sub1:4> <tableId:4>   (17 bytes)
switch_on_integer_sub <argIdx:4> <sub0:4> <sub1:4> <tableId:4>   (17 bytes)
```

**Semantics.** Walk a path from `X[argIdx]`: hop `sub0`, then (if `sub1 ≥ 0`) hop
`sub1`; deref the terminal; an atom / integer keys the table, anything else — or a
hop that lands on a non-compound / out-of-range position — takes the default.
`sub1 = -1` is the depth-1 sentinel. Each hop `SubCell(cell, idx)`: a list cell
exposes head (0) / tail (1) via the ADR-017 inline cons layout; a struct exposes
`arg[idx]` (bounds-checked against the functor arity). One tag-dispatched hop
covers both, so the three shapes map to:

- `[atom|_]` → `sub0=0, sub1=-1` (list head).
- `e(Op,_)`  → `sub0=j, sub1=-1` (struct arg `j`; the arg *is* the struct).
- `[t(Sym,_)|_]` → `sub0=0, sub1=j` (list head → token's arg `j`).

**When it fires.** In `PredicateCompiler.CompileIndexed`, two hooks each pick the
shortest partitioning path:

1. **List bucket** — probe path `(0,-1)` (head), then `(0,0)…(0,K)` (into the head
   compound). Fires when the ground clauses partition into ≥ 2 distinct keys of a
   single kind (all atoms → `atom_sub`, all integers → `integer_sub`).
2. **Struct functor group** — probe `(0,-1)…(K,-1)` (each struct arg). Same
   condition.

A clause whose chosen sub-position is a **variable** — or whose path can't be
followed at compile time (a differently-shaped head) — becomes a **wildcard**: it
merges into every keyed bucket **and** forms the table's default chain. This is a
sound over-approximation (it only ever adds tried-but-failing clauses; it never
removes a valid clause or changes solutions), which is why no "all heads share a
functor" precondition is needed. Mixed atom+integer keys at one position, a single
distinct key, or no partitioning position → keep the plain chain (no regression).

The sub-switch region is a structural copy of a top-level typed switch: the 17-byte
switch, then a `try`/`retry`/`trust` group chain per key with ≥ 2 clauses, then a
default chain over the wildcards — reusing `SwitchTable`, `EmitChain`, and the
`switchTableIdSites` relocation.

**Win.** A distinct-key call with no wildcard clause collapses an N-way scan to one
table jump with **no leftover choice point** (`action` 32 → ~1–4; `print_cmd` 41 →
lookup; `tok` 4 → 1); with wildcard fallbacks it prunes to {matching ∪ wildcard}.

The win is **determinism and instruction count, not heap cells.** For the realistic
*ground* parser call — the argument is already built on the heap — head matching is
read-mode, so the failed clause attempts a linear scan makes allocate **nothing**;
the heap-cell count is identical with and without the index. What the index removes
is the scan itself. Measured Tier-0 A/B (SHUMWAY_PROFILE, sub-index on vs off, hot
call hitting the *last* key so the linear baseline tries every earlier clause):

| predicate (call) | clauses | opcodes | backtracks | choice points | cells |
|---|---|---|---|---|---|
| `print_cmd([t(x,112,a,b)｜_],W)` — token stream, depth-2 int | 12 | 110 → **22** (−80%) | 11 → **0** | 1 → **0** | 8 → 8 |
| `expression_operand(e(49,foo))` — struct sub-arg int | 7 | 31 → **13** (−58%) | 6 → **0** | 1 → **0** | 3 → 3 |

Dispatched opcodes fall 58–80%, backtracking is eliminated, and the residual choice
point is gone (the call is now deterministic). Heap cells are flat — the correct
metric here is opcodes/CPs, not the `--alloc` cell count that measures the var-arg
(write-mode, structure-building) case.

## Tier-1 (IL)

The IL index re-encodes the WAM switch cascade. Both the runtime bytecode-walking
resolver (`IlIndexedDispatch.ResolveEntryCursor`, used when WAM is present) and the
WAM-independent `IlIndexGraph` (used by `--strip-wam` bundles, persisted via
`IndexGraphCodec`) learn the two opcodes: an `Atom`/`Int` node gains `Sub0`/`Sub1`
fields and walks the path before the table lookup. The sub-switch reuses
`SwitchTable` and its targets are ordinary clause-body entry cursors, so no new IL
emit is required — a sub-indexed predicate promotes to Tier-1 like any other. This
was verified in-process and cross-process (both `--strip-wam` and full-WAM
bundles).

One emit subtlety was load-bearing: the **compiled inline resolver**
(`TryEmitInlineIndexResolve`, the fast path both Tier-1 modes take) must walk the
`Sub0`/`Sub1` path before keying — via `IlIndexedDispatch.WalkSubOrMiss`. Without it
the resolver keyed on the *argument register's own cell*, so a list-headed call saw
tag `Lis`, fell to the table default, and (with a wildcard present) ran only the
default chain — dropping the bucket clause. The runtime graph walk
(`IlIndexGraph.TargetFor`) always handled `Sub0`; the inline emit is the one that
had to learn it.

**Tier-1 win — measured.** Same A/B, but the predicate runs as baked Tier-1 IL (IL
bundle + `LoadBundle`, `IsPromoted` asserted); the meaningful metric is IL choice
points pushed (`PushIlChoicePoint` → `PushChoicePoint`), not WAM opcodes (the bodies
run as IL, so the interpreter dispatches only query-setup opcodes):

| predicate (call) | clauses | IL choice points | backtracks | cells |
|---|---|---|---|---|
| `print_cmd([t(x,112,a,b)｜_],W)` | 12 | 11 → **0** | 1 → 0 | 8 → 8 |
| `expression_operand(e(49,foo))` | 7 | 6 → **0** | 1 → 0 | 3 → 3 |

Under Tier-1 the linear scan pushes one IL choice point per clause boundary walked
(each an `IlChoicePointEntry` + a CP-stack frame + a delegate reference) — 11 and 6
for these last-key hits. The sub-index eliminates **all** of them: the call is fully
deterministic (0 CPs, 0 backtracks). This is the Tier-1 counterpart of the Tier-0
opcode drop, concentrated in CP-stack allocation / delegate-ref churn.

**Wall-clock — where the tiers diverge sharply.** A hot failure-driven loop
(`between(1,2e6,_), p(X,_), fail`, `X` built once, 2 M calls, min of 15 interleaved
in-process A/B, Release):

| loop | Tier-0 (WAM interp) | Tier-1 (compiled IL) |
|---|---|---|
| `print_cmd/2` (12 clauses) | 672 → 662 ms (**1.02×**) | 2438 → 372 ms (**6.55×**) |
| `expression_operand/1` (7 clauses) | 506 → 481 ms (**1.05×**) | 1148 → 353 ms (**3.25×**) |

The win is **modest in Tier-0, dramatic in Tier-1** — and for the same reason the
CP counts predicted. In Tier-0 an avoided clause is a cheap `get_integer` compare +
an in-place `retry_me_else` (~1 ns), so the ~5–13 ns/call saved is swamped by the
`between`/`fail` loop control. In Tier-1 an avoided clause is an IL choice-point
*push* — a heap `IlChoicePointEntry` + CP frame — at ~66–94 ns each; removing 11
(resp. 6) per call across 2 M calls sheds ~22 M allocations of GC pressure. Note
Tier-1-*on* (372 ms) beats Tier-0-*on* (662 ms) while Tier-1-*off* (2438 ms) is far
slower than Tier-0-*off*: IL is fast when deterministic but pays heavily for choice
points — exactly what the sub-index removes on the hot dispatch predicates. So the
feature's real value lands on the Tier-1 bundles that ship a program, precisely
where the Arity parser/compiler families (`print_cmd`, `action`, `expression_operand`)
are hottest.

## Consequences

- **Initial coverage**: atom / integer sub-keys, path depth ≤ 2. The interpreter hop is
  generic (any depth), but the compiler probes and the opcode encoding cap the path
  at 2 hops.
- **Delivered by ADR-028**: structure-*keyed* sub-arg (a functor table on the
  sub-value — djota `ast_html_rows_//2`, Arity `sql_q_pcondition/9`
  function-name dispatch) via `switch_on_structure_sub`.
- **Deferred** (symmetric follow-ups, recorded here): paths deeper than 2; mixed
  atom+integer sub-keys at one position; and the second-token axis
  (`[t(';'),t(')')|_]`), which `heading_line//2`'s all-`#` heads also need.
- **Impact is narrow but hot**: a concentrated determinism/scan win on the Arity
  parser/compiler families (`print_cmd`, `action`, `expression_operand`) and a
  handful of djota rules; strictly no regression elsewhere.

## Verification

`SubArgIndexingTests` (Compiler: the right opcode + path operands appear and the
plain chain is gone; all-same-head and mixed-kind cases correctly do **not** fire).
`SubArgIndexingTests` (Embedding: correctness + determinism across list-head,
token-stream and struct-sub-arg in **both** tiers, plus the wildcard-merge and
cells-drop checks). djota 32/32; the Arity corpora produce identical output.
