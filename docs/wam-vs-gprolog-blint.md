# WAM codegen: Shumway vs GProlog (Blint)

Tracking doc for a per-predicate comparison of the WAM we generate against
GProlog's `pl2wam`, on `Blint.pl` (a real ~2570-line vanilla-Prolog program).
Goal: find where GProlog optimises the WAM and we don't, to drive a WAM codegen
optimisation pass.

## Method / caveats

- **GProlog**: `GLOBALSZ=400000 pl2wam --no-redef-error -w` on a copy with the
  7 multi-line `:- dynamic a/0, b/1, …` directives wrapped as
  `:- dynamic((a/0, b/1, …)).` (GProlog is ISO-strict: `dynamic(a/0)` at prio
  1150 can't be the left operand of `,` at 1000 — our parser is lenient and
  accepts the bare form). pl2wam **infinite-loops / overflows the global stack
  after 89 predicates** (a pl2wam limit on some later predicate, not ours), so
  **89 predicates** are compared; all 89 are also in our set.
- **Shumway**: `shumway-disasm --release` — **256 predicates**, clean, 0 errors.
- Blint expresses conditionals with **user predicates `ifthen/2` / `ifthenelse/3`
  (47 call sites)** rather than `;`/`->`, so real control-construct codegen
  (the `$disj`/`$neg` helper lowering) is barely exercised here.

## Where we MATCH or BEAT GProlog

- **Guard-before-cut recursion** (`p :- guard, !, recur`, e.g. `blint_params/2`
  cl.2, `parse_args/2` cl.2): **identical** Y-slot allocation and put sequence —
  and we additionally **fuse** `allocate`+`get_level` → `allocate_get_level` and
  `deallocate`+`proceed` → `deallocate_proceed`, so we emit *fewer* instructions
  than GProlog.
- **Arithmetic**: we emit inline `a_int_*` / `a_eval_*` (zero heap, zero call);
  GProlog emits `call((=<)/2)` / `execute((is)/2)` and builds the expression
  compound on the heap. Big win for us on arithmetic-heavy code.
- **Indexing**: explicit `switch_on_term` / `switch_on_arg` first- and sub-arg
  dispatch; competitive (GProlog's `-w` dump hides its index, so not line-diffed,
  but ours is present and tight).

## Optimisation opportunities (ranked)

| # | Gap | Pervasiveness (Blint) | Effort | Status |
|---|-----|----------------------|--------|--------|
| **A** | Argument-register **preferencing bails on a leading neck cut** (`p :- !, recur(Args)`): we extract head vars into high temps then `put_value` them into the call's arg registers; GProlog extracts straight into x0/x1 (zero `put_value`). | every `!, recur` clause (e.g. `trim_all_spaces/2` cl.3, `parse_args/2` cl.3) | **low** — mirror chunk-309's `FirstScheduledCallIndex` inside `ComputePreferredArgRegisters`. | **DONE (chunk 312)** — `trim_all_spaces/2` cl.3 now byte-matches GProlog; Blint put_value 1132→1113. |
| **B** | **Empty environment frame** `allocate [0]` / `deallocate` not elided when there are 0 permanents, no deep cut, and ≤1 real call. GProlog emits no frame. | **26** predicates carry an `allocate [0]` | **low** — base `needFrame` on a real CALL before the last goal, not goal count (a neck cut / inline arith goal needs no frame). | **DONE (chunk 312)** — Blint empty frames 26→4 (remaining 4 are genuine). |
| **C** | **Nested compound/list construction**: GProlog continues the write-mode stream into the nested term with `unify_structure` / `unify_list` (no temp). We used the chunk-8b two-pass BFS: a fresh temp var + a separate `get_structure` / `get_list` per nesting level. | **HIGHEST** — every nested term build. | done | **DONE (chunk 314, ADR-019)** — `unify_structure` / `unify_list` for a nested compound in the LAST argument position (covers all lists + last-arg compounds; linear, no write-stack). Blint get_list/get_structure 2812→1387 (−51%), total WAM 16008→14582 (−8.9%). Non-last nested keeps the BFS. |
| **D** | `unify_local_value` **globalisation** when a permanent (Y) value is written into a heap structure (GProlog: `unify_local_value(y0)`); we emit plain `unify_value_y`. | term-building clauses | n/a | **DONE (chunk 313) — already safe, no change.** We **heap-allocate** permanents (`put_variable_y` → `AllocateHeapUnbound`); a Y-slot only ever holds a Ref to a heap cell or an immediate, never a stack-resident unbound. So `unify_value_y` into a heap structure is always a heap ref that survives env deallocation — classical WAM's local-value problem can't arise. Pinned by `Phase26PermanentEscapeTests`. |

## Per-predicate sample (the shapes; 89 cluster into these)

| Predicate | Shape | Verdict vs GProlog |
|-----------|-------|--------------------|
| `blint_params/2` cl.1, `parse_args/2` cl.1, `trim_all_spaces/2` cl.1 | `p([],[]) :- !.` | parity (we use `get_atom <nil>` vs GProlog `get_nil` — equivalent) |
| `blint_params/2` cl.2 | `[H\|T],[H2\|T2] :- guard_call, !, recur` (cut after a call) | **parity / slightly ahead** (identical Y-slots; we fuse allocate+get_level) |
| `parse_args/2` cl.2 | multi-goal guard chain `…, parse_arg, !, recur` | parity (same permanents, same calls); list-arg build shows **C** |
| `trim_all_spaces/2` cl.3, `parse_args/2` cl.3 | `[H\|T],[H\|T2] :- !, recur` (neck cut then call) | **behind: A + B** (we add `allocate[0]` + 2× `put_value_x`; GProlog: no frame, args direct) |
| `assert_default_extension/1` | `retractif(...), ifthen(...), !` (term args) | parity on calls; nested-build of `assert_once(default_extension_i(Ext))` and `(Ext \= '')` shows **C** + **D** |
| `bling_file_header/2` | builds `['Blint: ', Name]`, several `writeln` | parity on calls; the list build is **C** (6 instr + temp vs GProlog 5 inline) + **D** |
| `tokenize_one_pred/3` cl.1 | structured first arg `token(L,eof)`, output `[token(L,eof)]` | **parity** — the nested `token(...)` is the list HEAD (non-last) so both BFS it; structured first-arg index + shared `L` identical |
| `parse_pred_errors/3` cl.1 | deep body (`length`, `is`, recur, `concat`, `!`) + nested-list head | head matching **parity** (same instr, BFS order vs GProlog depth-first; shared `Type` via a temp); arithmetic **we WIN** — `Loc is LPred-RLoc` is one inline `a_int_bin` (0 heap) vs GProlog's `put_structure (-)/2` + 2 unify + `call((is)/2)` (4 instr + a 3-cell heap term) |

**Cataloguing converged.** Across the shapes above (facts/asserts, list-recursion
with a guard, neck-cut recursion, term building, deep body + arithmetic,
structured-first-arg indexing) **no optimisation gap beyond A–D was found**.
After A+B+C (+ the D verification) we MATCH GProlog on frame/permanent allocation
and nested build, and BEAT it on arithmetic (inline `a_int_*`) and prologue
fusion (`allocate_get_level` / `deallocate_proceed`). Blint uses
`ifthen`/`ifthenelse` rather than `;`/`->`, so real control-construct codegen is
out of scope here (covered by Chunk86/88).

**CSE — DONE (chunk 315), beats GProlog.** The one optimisation neither engine
did: a clause that rebuilds a head subterm in its output
(`tokenize_one_pred`'s `[token(L,eof)]`) now SHARES the matched input structure
via `unify_value` instead of rebuilding it — we now emit fewer instructions
than GProlog on that shape. Scoped to head matching (stable arg registers),
top-level head-arg compounds, variable-name-precise key. Rare in Blint but
real where it applies.

**Final aggregate (89 GProlog-compiled predicates, non-index instructions):
GProlog 3769 vs Shumway 3319 (−12%).** We emit fewer instructions overall and
are ahead or at parity on every shape; the only 4 predicates where we emit more
do so by 1–2 instructions (register-allocation noise, not a pattern). The Blint
WAM-comparison exercise is closed.

## Recommended order of attack

1. **A + B together** — cheap, related, and a direct continuation of chunk 309
   (neck-cut transparency). They turn `p :- !, recur` into GProlog's frameless,
   put_value-free form. Low risk (the runtime correctness of the neck-cut shape
   is already proven by `Phase26CutTransparencyTests`).
2. **D** — confirm our `unify_value_y`-into-heap path can't dangle; if it can,
   add the globalising variant (and we'd want it before C).
3. **C** — the biggest structural win but the biggest change: inline nested
   write-mode building (`unify_structure` / `unify_list`) to drop the per-level
   temp + `get_*`. Pervasive, so the payoff is large; scope as its own ADR.
