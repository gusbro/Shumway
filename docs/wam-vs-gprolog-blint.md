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
| **A** | Argument-register **preferencing bails on a leading neck cut** (`p :- !, recur(Args)`): we extract head vars into high temps then `put_value` them into the call's arg registers; GProlog extracts straight into x0/x1 (zero `put_value`). | every `!, recur` clause (e.g. `trim_all_spaces/2` cl.3, `parse_args/2` cl.3) | **low** — mirror chunk-309's `FirstScheduledCallIndex` inside `ComputePreferredArgRegisters` (it currently `return`s when `goals[0]` isn't a compound, i.e. on `!`). | open |
| **B** | **Empty environment frame** `allocate [0]` / `deallocate` not elided when there are 0 permanents, no deep cut, and ≤1 real call. GProlog emits no frame. | **26** predicates carry an `allocate [0]` | **low** — base `needFrame` on the count of real *calls*, not goals (a neck cut / inline arith goal needs no frame). | open |
| **C** | **Nested compound/list construction**: GProlog continues the write-mode stream into the nested term with `unify_structure` / `unify_list` (no temp). We use the chunk-8b two-pass BFS: a fresh temp var (`unify_variable_x`) + a separate `get_structure` / `get_list` to build the nested part — **+1 instruction + 1 temp register per nesting level**. | **HIGHEST** — every nested term build. GProlog uses `unify_list`+`unify_structure` **221×** across 89 preds; we have **no such opcode** (0×) and lean on `get_list`/`get_structure` (2812× across 256). | **high** — a codegen-strategy change (inline nested write-mode build), likely new `unify_structure`/`unify_list` opcodes. | open |
| **D** | `unify_local_value` **globalisation** when a permanent (Y) value is written into a heap structure (GProlog: `unify_local_value(y0)`); we emit plain `unify_value_y`. | term-building clauses | **verify first** — likely already safe under our heap/GC model (Blint runs correctly; conservative GC scans Y-slots), but confirm no dangling-reference path. | verify |

## Per-predicate sample (the shapes; 89 cluster into these)

| Predicate | Shape | Verdict vs GProlog |
|-----------|-------|--------------------|
| `blint_params/2` cl.1, `parse_args/2` cl.1, `trim_all_spaces/2` cl.1 | `p([],[]) :- !.` | parity (we use `get_atom <nil>` vs GProlog `get_nil` — equivalent) |
| `blint_params/2` cl.2 | `[H\|T],[H2\|T2] :- guard_call, !, recur` (cut after a call) | **parity / slightly ahead** (identical Y-slots; we fuse allocate+get_level) |
| `parse_args/2` cl.2 | multi-goal guard chain `…, parse_arg, !, recur` | parity (same permanents, same calls); list-arg build shows **C** |
| `trim_all_spaces/2` cl.3, `parse_args/2` cl.3 | `[H\|T],[H\|T2] :- !, recur` (neck cut then call) | **behind: A + B** (we add `allocate[0]` + 2× `put_value_x`; GProlog: no frame, args direct) |
| `assert_default_extension/1` | `retractif(...), ifthen(...), !` (term args) | parity on calls; nested-build of `assert_once(default_extension_i(Ext))` and `(Ext \= '')` shows **C** + **D** |
| `bling_file_header/2` | builds `['Blint: ', Name]`, several `writeln` | parity on calls; the list build is **C** (6 instr + temp vs GProlog 5 inline) + **D** |

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
