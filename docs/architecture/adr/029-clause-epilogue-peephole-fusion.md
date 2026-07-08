# ADR-029: Clause-epilogue peephole fusion (`deallocate;execute`, `cut;deallocate_proceed`, `cut;proceed`)

**Status:** Accepted — **`CutDeallocateProceed` implemented and shipped**
(the deep-cut deterministic-clause epilogue). Tier-0 dispatch reduction; Tier-1
reads the un-fused bytecode, so promotion is preserved and IL codegen is
byte-for-byte the pre-fusion shape. `DeallocateExecute` and the neck-cut variants
are **deferred** (see Implementation notes). Extends chunk 220's `AllocateGetLevel`
/ `DeallocateProceed` fusion with the clause-epilogue pairs a corpus census showed
remain.

## Implementation notes (2026-07-08)

- **Shipped: `CutDeallocateProceed`** (`Cut` + `DeallocateProceed` → 7 bytes,
  slot operand at +1, two trailing Nops). Fires **30 223×** across the 556-file
  Arity corpus (matching the census's 30 233 `cut→deallocate_proceed` pairs; the
  ~10 difference is `neck_cut`-headed pairs the fusion leaves alone). This is the
  `Head :- …, call, !.` deterministic-clause epilogue — a deep cut always has a
  frame, so its epilogue is always `deallocate_proceed`.
- **The Tier-1 integration is `CompiledPredicate.BytecodeUnfused`**, not per-site
  opcode cases. The IL project's describe/emit walks all read `BytecodeUnfused`
  (a lazily-built, same-length copy with each fused opcode expanded back to its
  two components), so no IL describer/emitter/inliner needs a new case and none
  can silently lose promotion on a fused opcode. Same-length ⇒ every recorded
  offset (CallSites, DispatchSites, clause ranges) stays valid. Discovered via the
  gate: fusion-on surfaced exactly 6 Embedding failures (fact-inline, deep-cut IL
  emit, once/1 differential, `IsInlinableRule`); `BytecodeUnfused` fixed all 6 at
  the source. Gate green: Core 436 / Compiler 322 / Interpreter 105 /
  Embedding 2878 / ISO 277.
- **`CutProceed` is defined + handled but never fires** on the corpus (a deep
  `Cut` is always framed, so it is never immediately followed by a bare
  `proceed`). Kept for completeness / a theoretical frameless deep cut; harmless.
- **Deferred — `DeallocateExecute`** (defined + interpreter handler ready, but the
  peephole does **not** emit it): `execute` is a link-time dispatch site the engine
  rewrites in place to `ExecuteIl` / `ExecuteBuiltin` (PrologEngine link paths);
  fusing `deallocate;execute` hides that swap. Enabling it needs
  `DeallocateExecute{Il,Bytecode,Builtin}` variants the linker also rewrites — a
  combinatorial follow-up weighed against its 12 164 static (but Tier-0-only) sites.
- **Deferred — neck-cut variants** (`NeckCutProceed` / `NeckCutDeallocateProceed`):
  the census's 11 606 `cut→proceed` pairs are all `neck_cut→proceed` (the frameless
  `Head :- guard, !.` shape), which the current fusion (keyed on the `Cut` opcode,
  not `NeckCut`) leaves alone. A clean, high-frequency follow-up now that the
  mechanism (peephole + `BytecodeUnfused`) is proven.

## Context

Chunk 220 fused the two hottest Blint clause-boundary pairs into single opcodes
that keep the same total byte width (second slot overwritten with `Nop`, so no
operand-address shifts cascade through `try_me_else` / switch tables):
`Allocate`+`GetLevel` → `AllocateGetLevel`, `Deallocate`+`Proceed` →
`DeallocateProceed`. A fused opcode saves one iteration of the Tier-0
fetch-decode-dispatch loop (`op = code[pc]; switch(op){…}; pc += size`).

A static census over the full Arity corpus (`shumway-disasm --census --arity`,
`C:\temp\test` + `C:\temp\testGen`, 556 files: 30 820 predicates, 1.56M opcodes,
1.53M adjacent pairs, 71 686 clause bodies) measured which clause-epilogue pairs
remain, and — crucially — which are actually *fusable*. A peephole fusion is only
sound between two **straight-line** opcodes; an opcode that transfers control
cannot be fused with its continuation.

| Pair | count | % of pairs | fusable? |
|------|------:|-----------:|----------|
| `deallocate → execute*` | 12 164 | 0.79% | **yes** — LCO epilogue, both straight-line |
| `cut → deallocate_proceed` | 30 233 | 1.98% | **yes** — cut only prunes CPs then continues |
| `cut → proceed` | 11 606 | 0.76% | **yes** — frameless variant |
| `cut → deallocate` / `cut → execute*` | 4 766 | 0.31% | yes but marginal counts |
| `call* → cut` | 35 915 | 2.35% | **NO** — `call` transfers control; the cut runs when the callee *returns* (via `Cp`), not in line, so the two cannot collapse into one opcode |

Two structural facts drive the decision:

- **`deallocate;execute` is the LCO epilogue** — the tail-call sequence emitted
  for every frame-allocated last call, i.e. it fires **once per iteration** of
  every tail-recursive predicate (`!, tailCall` recursion is 36% of all tail-call
  clauses, 25.5% of all clause bodies are tail calls). Chunk 220 fused
  `deallocate;proceed` but **not** `deallocate;execute` — an obvious asymmetry.
  This is the highest *dynamic* frequency of any epilogue pair.
- **`cut;<terminator>` is 58.4% of all clause bodies** — the deterministic-clause
  epilogue `Head :- Body, !.`, the pervasive Arity idiom.

`call*→cut` (2.35%, the largest static count and the pair the earlier
"minor-fusions" memory listed) is **rejected as non-fusable** by the control-flow
rule above.

## Decision

Add three fused Tier-0 opcodes, emitted as an emit-time peephole in
`ClauseCompiler` exactly as chunk 220 did (same total width, `Nop`-padded second
slot):

- **`DeallocateExecute <target:int32>`** (6 bytes = `Deallocate`(1) +
  `Execute`(5)) — the missing LCO-epilogue sibling of `DeallocateProceed`.
- **`CutDeallocateProceed <slot:int32>`** (7 bytes = `Cut`(5) +
  `DeallocateProceed`(2)) — the frame-allocated deterministic-clause epilogue.
- **`CutProceed <slot:int32>`** (6 bytes = `Cut`(5) + `Proceed`(1)) — the
  frameless variant.

`cut→deallocate` and `cut→execute` (0.31%) are **not** given their own opcodes —
their counts do not justify the machinery; they remain two dispatches.

### Encoding

New opcodes go at the **end of the dense dispatch block** (before `Meta`),
renumbering the reserved tail — free pre-release
([[no-format-version-bumps-prerelease]]). `OpcodeInfo` gets a `Set(...)` line per
opcode; the data-driven disassembler and `BytecodeIO` read them uniformly. The
`Nop`-pad keeps every downstream operand at its original offset, so no
`try_me_else` / switch-table / chain-patcher address shifts.

### Tier-1: understood, not accelerated

The same bytecode feeds both tiers. Tier-1 does **not** dispatch opcodes — it
*describes* the WAM then emits native IL (ADR-011), so a fused opcode has no
per-dispatch cost to save there. The requirement is **promotion eligibility**:
the IL describer must **recognise** the fused opcodes or every predicate using
them silently loses Tier-1 promotion (the W6 lesson). The describer *un-fuses*
them — emits exactly the IL the two component opcodes would (`DeallocateExecute`
→ the existing `deallocate` env-trim IL + the existing tail-call emit;
`CutDeallocateProceed` → `engine.Cut(slot)` + the deallocate/proceed epilogue).
So Tier-1 codegen is byte-identical to today; the opcodes are transparent.

The recognition sites are the same ~7 that chunk 220 already touches for
`DeallocateProceed` / `AllocateGetLevel` in `IlPredicateCompiler.cs`: the two
eligibility gates (`IsClauseBodyOpcode`, the structural-dispatch adjacent), and
the emit cases in the `EmitClauseBody` variants (the terminator-emit special
case, and the per-opcode `switch`). Bounded and mechanical.

## Scope of the win

Tier-0 interpreter and Native-AOT (Tier-0-only) execution. Beneficiaries: the
default `--exe` without `--with-compiled-il`, the AOT REPL, and any cold/warming
code before promotion. Precedent that this lever is real: Phase-33 **I1**
(interpreter loop-top-check skip) took ~8–16% on `nreverse` purely by cutting
per-opcode Tier-0 overhead. `DeallocateExecute` removes one dispatch per
tail-recursion iteration; the `cut;terminator` pair removes one per
deterministic-clause exit (58% of clause bodies).

Not claimed: any Tier-1 speedup. Hot predicates promote to IL where these
opcodes are un-fused to identical code.

## Soundness

Each fused pair's first element (`deallocate`, `cut`) is a straight-line body
opcode that is never a clause terminator and never a jump/switch target, so
textual adjacency equals control-flow fall-through — the fusion is a local
rewrite with no reachability hazard. `execute`/`proceed`/`deallocate_proceed`
are emitted only as the *unit's* second half (the LCO / epilogue sequence), so
the emitter fuses exactly where the two were already going to be adjacent.

## Implementation plan

1. `Opcode.cs` — add the three opcodes at the dense-block tail; renumber the
   reserved tail. `OpcodeInfo.cs` — sizes + operand kinds + mnemonics.
2. `BytecodeInterpreter.cs` — three dispatch cases (each does what the two
   component cases did, back-to-back).
3. `BytecodeEmitter.cs` — `EmitDeallocateExecute` / `EmitCutDeallocateProceed` /
   `EmitCutProceed`.
4. `ClauseCompiler.cs` — emit the fused form at the existing epilogue sites
   (the `deallocate;execute` LCO path, and the `cut` immediately before a
   terminator).
5. `IlPredicateCompiler.cs` — the ~7 recognition/un-fuse sites (chunk-220
   mirror).
6. Peephole/chain-patcher/disassembler: `Nop`-pad keeps offsets stable; confirm
   the disassembler renders the new mnemonics.

## Verification

- Interpreter unit tests: each fused opcode executes identically to its
  component pair (hand-assembled bytecode + a compiled predicate that emits it).
- IL promotion: a predicate using each fused opcode still promotes and runs
  correctly (Warm-promoted, and `--strip-wam` persisted).
- Determinism/backtracking unchanged (the fused ops are semantically identical).
- A/B Tier-0 wall-clock, **back-to-back same session** on a tail-recursive
  benchmark ([[wallclock-ab-must-be-back-to-back]]); the deterministic
  `SHUMWAY_PROFILE` dispatch-count drop is the primary metric.
- Full five-project gate.

## Alternatives considered

- **Fuse `call*→cut`** (largest static count): rejected — `call` is a control
  transfer; the cut runs on callee return, not in line, so no linear fusion
  exists (this corrects the earlier "minor safe fusions" framing).
- **A general super-instruction pass** over arbitrary pairs: rejected — the W6
  census established single-dispatch-saving Tier-0 fusions ripple through every
  body-shape consumer and buy nothing once hot code is Tier-1; this ADR limits
  itself to the three highest-frequency, structurally-clean epilogue pairs in
  the established chunk-220 family.

## Related

Chunk 220 (`AllocateGetLevel`/`DeallocateProceed`); Phase-33 W6 (execute_builtin
fusion — rejected on census); I1 (Tier-0 dispatch overhead is a real lever);
[[logtalk-benchmark-comparison]] (the pair census); ADR-011 (IL describe-then-
compile — why Tier-1 has no dispatch to fuse).
