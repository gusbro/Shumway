# ADR-037: `soft_cut` opcode + inline `( Cond *-> Then ; Else )` lowering

**Status:** Accepted — the `soft_cut` opcode, `Activation.SoftCut`, the
`TryBacktrack` dead-sentinel handling, and the inline lowering for the *eligible*
`( Cond *-> Then ; Else )` (plain Then/Else, plain-or-`call` condition) are
implemented and default-on; `time/1` uses `*->`. The disassembly matches
`pl2wam`'s shape (`try_me_else; get_level_b; Cond; soft_cut; Then; ELSE:
trust_me; Else`). Refinement over the design below: `soft_cut` DISCARDS the ELSE
CP (cuts to its parent, keeping bindings) when it is the current top — a
deterministic condition leaves no lingering dead frame, so `time(true)` is
determinate at the top level; the dead-`BP` neutralisation applies only when the
condition left choice points above it. **Tier-1 IL now implemented:** `soft_cut`
is whitelisted in `IsSupportedOpcode` and emits `engine.SoftCutToLevel`; the ELSE
choice point is an IL choice point, so the deterministic case discards it through
`Cut` (which also drops the `_ilCpStack` entry) and the non-deterministic middle
case swaps that entry's resume delegate to a fail-delegate
(`NeutralizeIlChoicePoint`), the IL analogue of the dead `BP`.

**Non-eligible `*->` also handled** (a cut in a branch, nested control in a part,
a standalone `*->`, or one built at runtime): `SynthesizeDisjunctionHelper` gains a
soft-cut-helper case (`'$choice_level'(K), Cond, '$soft_cut'(K), Then` + `Else`,
with `'$soft_cut'/1` = `Activation.SoftCut`); `HasTransparentBranchCut` /
`ReplaceTransparentCuts` descend into `*->` branches; standalone rewrites to
`( … ; fail )`; the runtime `'$call_disj'` gets a `*->` clause and a bare `*->`
routes to `'$call_softarrow'` (= `'$call_arrow'` minus the commit). Fixing the
runtime-built case also fixed a **latent `->` bug**: `DistributeMqual` (the module
tag for variable meta-calls) wrapped a `;`'s `->`/`*->` left-arg whole, hiding it
from `$call_disj`'s if-then-else match, so a runtime-built `( true -> a ; b )` ran
BOTH branches — `WrapGoal` now distributes the module INTO the construct instead
(interpreter + IL). ADR-037 is fully implemented; nothing deferred.

## Context

`*->/2` (soft cut, a.k.a. "if" without commitment) is a widely-supported
non-ISO control construct: `( Cond *-> Then ; Else )` runs `Then` for **every**
solution of `Cond` when `Cond` succeeds at least once (Else is pruned), and runs
`Else` only when `Cond` has no solution. It differs from `->/2` in that it does
**not** commit to `Cond`'s first solution — `Cond`'s non-determinism is
preserved.

Shumway parses `*->` (operator `1050 xfy`) and every static analysis descends
through it (call-graph collection, `MetaTransform`, `PhraseTransform`,
`DeterminismAnalysis`, `ModuleRewrite`, the debugger frame walker,
`ShmoCompiler`, `InlineIte` eligibility). But **nothing lowers it to
execution**: `CompileInlineIte` recognises only `->`, so a `( C *-> T ; E )`
compiles as a plain disjunction `( X ; E )` whose then-part `X = *->(C,T)` is
emitted as an ordinary body goal — a call to a non-existent `*->/2`. Runtime
meta-dispatch (`DispatchCall`) routes `,`/`;`/`->`/`\+` to their `$call_*`
helpers but has no `*->` arm. The net effect is `existence_error(*->/2)` at run
time. The operator and analysis plumbing make it *look* supported; the
execution tier was never wired.

The immediate motivation: the clean fix for `time/1`'s spurious choice point
(`time(true)` leaves a CP) is `( call(Goal) *-> report ; report, fail )`, which
needs a working `*->`.

**Why an opcode rather than a source rewrite.** The hard part of soft cut is
that on `Cond`'s first success you must remove **only** the `Else`
choice point while leaving `Cond`'s own choice points — which sit *above* the
`Else` CP on the stack. A source-to-source lowering (a fresh non-backtrackable
flag: `( C, commit(R), T ; pending(R), E )`) is correct and tier-agnostic, but
costs ~2 extra builtin dispatches + a small allocation per execution and
produces WAM visibly worse than GProlog's. Shumway's standing goal is WAM
parity-or-better with GNU Prolog (the Phase-26 `wam-vs-gprolog` arc), and
`pl2wam` compiles `*->` to a **single dedicated opcode**:

```
% ( member(X,[1,2,3]) *-> true ; X = none )   — GNU Prolog 1.5.0 pl2wam
try_me_else(1),
get_current_choice(y(0)),   % capture B AFTER the try_me_else → the ELSE CP
...
call(member/2),
soft_cut(y(0)),             % neutralise ONLY the ELSE CP; member's CPs survive
label(1),
trust_me_else_fail,
get_atom(none,0), ...
```

versus the hard cut it emits for `->` (`get_current_choice` *before*
`try_me_else`, then `cut(y(0))`, which pops the ELSE CP **and** the condition's
CPs). So soft cut is a first-class primitive in the reference engine, not an
exotic it decomposes. This ADR mirrors that: capture the barrier *after* the
`try_me_else`, and commit with a new `soft_cut` instead of `cut`.

This reuses ADR-025's inline-ITE machinery almost verbatim (`get_level_b`,
`try_me_else`/`trust_me`, `jump`, the arity-0 CP, the Y-slot barrier discipline);
the only genuinely new piece is the CP-neutralisation semantics of `soft_cut`.

## Decision (proposed)

### 1. One new opcode

- **`soft_cut <slot:int32>`** — reads the barrier level `B` from `Y[slot]`
  (same operand shape as `cut`) and **neutralises the single choice point at
  that level** without touching any CP above it. Numbered at the end of the
  dense dispatch block per the contiguity policy (see `Opcode.cs`; no numeric
  value cited here per the comment policy).

Neutralisation, not removal: the ELSE CP is a *middle* frame (the condition's
CPs are above it on the contiguous stack), so it cannot be popped in place.
`Activation.SoftCut(int barrier)` reads the frame's saved arity at
`barrier + CpArityOffset`, then overwrites its `BP` slot
(`barrier + CpBpOffset(arity)`) with a dedicated **dead sentinel**
(`SoftCutDeadBp`, distinct from the fail sentinel `-1`). Backtracking reaches
this CP only after the condition's CPs above it are exhausted; `TryBacktrack`,
on reading `BP == SoftCutDeadBp`, does a `TrustMe` (pops the frame, reverts `B`
to the previous CP) and **continues backtracking** — so `Else` never runs and
control falls through to the CP that preceded the whole construct. If `barrier`
is stale (`> B`, the CP already gone via a surrounding `catch/3` unwind) the op
is a no-op, exactly as `Cut` treats a stale barrier.

The IL tier's inline ITE uses an IL choice point (`PushIlChoicePoint` + a resume
cursor at the ELSE label, per ADR-025 stage b). `SoftCut` marks the matching
`_ilCpStack` entry (keyed by its frame's stack-`B`) so its resume pops-and-fails
instead of re-entering the ELSE cursor — the IL analogue of the dead `BP`.

### 2. Inline lowering in `ClauseCompiler`

`CompileInlineIte` generalises to `disj.Args[0]` being **either** `->` or `*->`.
The emitted shape for `( C *-> T ; E )`:

```
try_me_else ELSE      ; arity-0 CP — the ELSE alternative
get_level_b Yk        ; AFTER try_me_else → Yk = the ELSE CP's level
<C>                   ; compiled inline (real WAM, real indexing)
soft_cut Yk           ; neutralise the ELSE CP; C's CPs survive
<T>
jump END
ELSE: trust_me        ; pop the CP on the else path
<E>
END:  ...
```

The **only** differences from the existing `->` lowering are (a) `get_level_b`
is emitted *after* the `try_me_else` (so the barrier names the ELSE CP, not the
parent — mirroring GProlog's `get_current_choice` placement), and (b) `soft_cut`
replaces `cut`. Everything else — the arity-0 CP, the branch-tail LCO, the
Y-classification of branch/continuation variables, the dispatch-site recording,
the `ForceIteVarsPermanent` discipline — is unchanged and correct by
construction. Eligibility is the same as `->` (parts are conjunctions of plain
goals; branch cuts, var/non-callable conditions, and nested disjunctions keep
the helper/runtime path).

### 3. Runtime meta-call path

For a dynamically-built goal (`call((C *-> T ; E))`, or a `*->` reached through a
variable), `DispatchCall`'s disjunction arm gains a `*->` case beside its `->`
case, reached through the same `$call_*` helper family. The helper body is
written with a **literal** `( call(C) *-> call(T) ; call(E) )`, which lowers
through §2 to the `soft_cut` opcode — so the runtime path costs one helper call
and then runs the native primitive, with no second implementation of the
semantics. Cut transparency of `Then`/`Else` and cut-opacity of `Cond` match the
`->` helper (the condition runs under a call barrier).

### 4. Both tiers understand the shape

As with ADR-025, the shared bytecode feeds Tier-1, so `IlPredicateCompiler` must
**describe + emit** `soft_cut` (and the `get_level_b`-after-`try_me_else`
variant) before the WAM compiler starts emitting it — otherwise a body-`*->`
predicate would lose IL promotion. `soft_cut` in IL marks the ITE's IL choice
point dead (see §1); the rest reuses the ADR-025 ITE emit.

### Rollout order

(a) `soft_cut` opcode + `Opcode.cs` + interpreter case + `Activation.SoftCut` +
`TryBacktrack` dead-sentinel handling + `shumway-disasm`; (b) IL describe/emit
for the shape (gated OFF in the WAM compiler); (c) `CompileInlineIte`
generalisation + the runtime `$call_*` arm, behind the existing inline-ITE
enablement; (d) tests — `time(true)` det, `member` soft cut preserves
non-determinism + prunes Else, `Cond` failure runs Else, standalone `A *-> B`,
findall / once / `\+\+` / cut-in-branch, interpreter + IL parity; (e) close.

## Consequences

- `*->` becomes executable at both tiers with one opcode, WAM shape matching
  GProlog's (one `soft_cut` per construct, no builtin-dispatch tax, no
  allocation).
- The `time/1` spurious-CP fix reduces to
  `( call(Goal) *-> '$time_report'(Mark) ; '$time_report'(Mark), fail )`.
- New CP semantics (`soft_cut`'s middle-CP neutralisation) is confined to
  `Activation.SoftCut` + one `TryBacktrack` sentinel check; the CP **frame
  layout is unchanged** (a new BP *value*, not a new field — so ADR-005 / ADR-026
  are untouched, unlike a per-CP "dead" flag).
- One-time consumers to update: interpreter dispatch (one case), `OpcodeTable`
  sizing, `shumway-disasm`, IL describe/emit, and the region/inline detectors
  that already special-case `jump`-bearing bodies (a body-`*->` follows the same
  rule).
- Risk: the IL-side dead-CP marking is the least-trodden path; the `time/1`
  regression suite plus a findall/`*->` interpreter-vs-IL parity test pin it.

## Alternatives considered

- **Source-to-source flag lowering** (`'$sc_cell'(R), ( C, '$sc_commit'(R), T ;
  '$sc_pending'(R), E )`, R a fresh non-backtrackable boolean): correct,
  reentrant, tier-agnostic with a single `MetaTransform` rewrite, no opcode, no
  CP change. **Rejected as the primary path** because it produces WAM strictly
  worse than GProlog's single `soft_cut` (2 extra builtin dispatches + a bounded
  per-execution flag-cell allocation), against the project's WAM-parity goal.
  Retained as the mental model for the *runtime* helper only, where the extra
  cost is already paid by the meta-call itself — though §3 avoids even that by
  having the helper body lower to the opcode.
- **Per-CP "dead" flag** in the choice-point frame: cleaner to read at
  `TryBacktrack` than a sentinel `BP`, but widens the CP frame — an ADR-005 /
  ADR-026 change (measured not worth its cost for other features). The sentinel
  `BP` value achieves the same with zero layout change.
