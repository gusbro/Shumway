# WAM codegen vs GNU Prolog — SWI/van-Roy benchmark set

> **Measurement snapshot (2026-06, ~ADR-026).** Instruction counts predate the
> later indexing and peephole ADRs (027–034); the method remains reusable, the
> numbers are historical.

Companion to [`wam-vs-gprolog-blint.md`](wam-vs-gprolog-blint.md). Where that
doc drove a predicate-by-predicate comparison on one large real program
(Blint), this one widens the oracle to the **canonical Prolog benchmark set**
— the SWI-Prolog `bench` suite (the "van Roy" programs: `nreverse`, `boyer`,
`tak`, `zebra`, `chat_parser`, …). The goal is unchanged: use GNU Prolog's
`pl2wam` as a WAM-quality oracle and close any codegen gap where Shumway emits
materially more instructions than GProlog for the same clause.

## Method

- Oracle: `pl2wam --no-redef-error -w prog.pl` → `prog.wbc`, a text dump of
  one `clause(Head, [instr, …])` per clause. **GProlog's byte-code WAM has no
  explicit indexing instructions** — no `switch_on_*`, no
  `try_me`/`retry_me`/`trust_me`, no `try`/`retry`/`trust` chain ops. Indexing
  is implicit in its dispatcher.
- Shumway: `shumway-disasm prog.pl` → per-predicate WAM listing.
- **Fair comparison = non-index instructions only.** Because GProlog emits zero
  indexing instructions, counting Shumway's `switch_on_*` /
  `try_me`/`retry_me`/`trust_me` / `try`/`retry`/`trust` / `check_visible` /
  `enter_dynamic` / `nop` against it is apples-to-oranges. Excluding them on the
  Shumway side compares *clause-body codegen density*, which is the thing
  `pl2wam` is an oracle for. (Same discipline as the Blint comparison.)
- 27 of the 35 bench programs compile under `pl2wam`. The 8 excluded —
  `det`, `fib`, `moded_path`, `nand`, `perfect`, `pingpong`, `queens_8`,
  `queens_clpfd` — use syntax or directives `pl2wam` rejects, so there is no
  oracle dump to compare against.

## Result (non-index WAM instruction count)

**Total: Shumway 15533 vs GProlog 16156 = 0.96×** — competitive with or ahead
of GProlog on the whole set, and **no program exceeds 1.10×** (the previous
outlier, `zebra`, was closed — see below).

| program          | Shumway | GProlog | ratio |
|------------------|--------:|--------:|------:|
| boyer            |    1601 |    1550 | 1.03 |
| browse           |     488 |     552 | 0.88 |
| chat_parser      |    4631 |    4603 | 1.01 |
| crypt            |     252 |     303 | 0.83 |
| derive           |     190 |     204 | 0.93 |
| divide10         |     154 |     164 | 0.94 |
| eval             |      52 |      58 | 0.90 |
| fast_mu          |     244 |     316 | 0.77 |
| flatten          |     703 |     739 | 0.95 |
| log10            |     146 |     148 | 0.99 |
| meta_qsort       |     339 |     344 | 0.99 |
| mu               |     133 |     143 | 0.93 |
| nreverse         |      91 |      91 | 1.00 |
| ops8             |     150 |     156 | 0.96 |
| poly_10          |     370 |     400 | 0.93 |
| prover           |     420 |     418 | 1.00 |
| qsort            |     157 |     159 | 0.99 |
| query            |     193 |     206 | 0.94 |
| reducer          |    1430 |    1459 | 0.98 |
| sendmore         |     140 |     200 | 0.70 |
| serialise        |     134 |     137 | 0.98 |
| sieve            |      98 |     109 | 0.90 |
| simple_analyzer  |    1821 |    1972 | 0.92 |
| tak              |      44 |      58 | 0.76 |
| times10          |     154 |     164 | 0.94 |
| unify            |    1199 |    1303 | 0.92 |
| zebra            |     199 |     200 | 0.99 |

The two programs slightly above parity (`boyer` 1.03, `chat_parser` 1.01) are
within 3% — body codegen at parity. The wins below 1.00 are mostly the
fused arithmetic (`a_int_*`, `sendmore`/`tak`/`crypt`) and clause-prologue
fusion already delivered in Phases 25–27.

## Gap found and closed: consecutive void batching (chunk 347)

`zebra` was the one outlier at **1.29×** (259 vs 200). Root cause:
`house(red, english, _, _, _)` and similar facts have runs of consecutive
anonymous (singleton) arguments. GProlog emits a single `unify_void(3)` for the
three-void run; Shumway emitted three separate `unify_void 1` instructions.

Fix (`BytecodeEmitter.EmitUnifyVoid`): coalesce a new `unify_void` into the
immediately-preceding one when it sits at the current write head, bumping the
count operand in place. The interpreter already decoded the operand as a count
(`UnifyVoid(n)`), so this is a pure codegen-density change — no opcode, ABI, or
interpreter change. Batching never merges across an intervening real
unification (`unify_constant`/`unify_value`/…), only adjacent voids.

Result: `zebra` 259 → 199 (1.29× → **0.99×**); the set total moved 0.97 → 0.96
and the last >1.10× outlier disappeared. Regression coverage in
`Chunk347Tests` (batched run → one `unify_void(3)`; void runs split by a real
arg stay separate).

## Remaining gap analysis (aggregate opcode histogram)

After the void batch, the per-predicate sweep across all 27 programs shows the
worst remaining ratios are small (`flatten/extract_disj/4` 1.87 = 28 vs 15;
the recurring set-ops `intersectv`/`unionv`/`diffv`/3 at 1.10 = 11 vs 10). An
aggregate opcode histogram (Shumway vs GProlog, normalized for spelling, set
total) explains them and, more importantly, separates real gaps from
non-gaps:

**The one real gap — register movement (`put_value` +512).** Shumway emits 512
more `put_value` than GProlog, partly offset by GProlog emitting 299 more
`get_variable`. GProlog's register allocator **preferences** a head sub-argument
(extracted by `unify_variable`) directly into the argument register of the
following call, and **pre-saves** the conflicting incoming args with
`get_variable`; Shumway extracts sub-args into safe high registers and then
shuffles them into argument position with `put_value`. Example
(`intersectv([A|S1], S2, S) :- intersectv_2(S2, A, S1, S)`): GProlog 7
instructions (unify A→x1, S1→x2 *in place*, pre-save S2/S), Shumway 8 (unify
A→x3, S1→x4, then four `put_value`). The avoidable part is the temporary /
single-call (leaf) clauses; genuine permanents crossing a call require
`put_value_y` on both sides regardless (GProlog too). This is the deferred
**GProlog-style register allocator** — a substantial, previously-attempted
effort (see the `chunk-model-refinement-failed` note: it needs a proper
allocator plus unsafe-variable handling, and a naive version broke CLP and
collided body-cons registers). No safe shortcut.

**Non-gaps (look like gaps in a raw diff, aren't):**
- `put_unsafe_value` 0 vs **350** — Shumway heap-allocates permanents
  (chunk 313), so a permanent variable never dangles when its environment is
  deallocated; GProlog stack-allocates environments and must globalize unsafe
  vars. Different design, same instruction count (Shumway's `put_value`
  subsumes the role).
- `put_void` 0 vs 127, `get_nil` → `get_atom([])` — naming only, parity count.

**Where Shumway wins (do not regress):**
- Fused arithmetic `a_int_bin`/`a_int_cmp`/`a_eval_*` (216 instrs) — GProlog
  compiles `is`/comparisons as **calls** (its `call` is +117 for this reason).
- Fused control: `allocate_get_level`, `neck_cut`, `deallocate_proceed`.
- Inline nested compounds (ADR-019/020): Shumway `unify_structure` +
  `put_structure` = 512 vs GProlog's 599 (`put_structure` + intermediate var).

Bottom line: the void batch was the last *safe, fusion-style* count gap. The
remaining ~0.96× → parity-everywhere headroom is the register allocator, which
is a scheduled major effort rather than an incremental codegen tweak.
