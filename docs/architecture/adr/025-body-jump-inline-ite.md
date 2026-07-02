# ADR-025: Body `jump` opcode + inline deterministic if-then-else (Tier-0)

**Status:** Proposed (Phase 33, audit item W4).

## Context

At Tier-0, `(Cond -> Then ; Else)` and plain disjunction `(A ; B)` lower via
`MetaTransform` to a **synthesized helper predicate** (`$disj_N`) reached by a
full `Call`: the helper's 2-clause dispatch pushes a choice point
(`try_me_else`/`trust_me`), the host's free variables are marshalled through
the helper's head, and a deterministic if-then-else pays a call + CP + return
per execution.

Phase 29's region compilation fixed exactly this in **Tier-1 IL** — the
chunk-418 validation found the `(C->T;E)` lowering was the real lever (~2× on
ITE-recursion, qsort −22%, boyer −15%). The Tier-0 interpreter still pays the
helper shape, and the Arity-compat workload uses `;`/`->` directly (Blint hid
it behind user-defined `ifthen/2`, which is why the Phase-26 Blint comparison
declared it out of scope).

The classical inline lowering needs an **unconditional intra-predicate branch**
in body position — an opcode Shumway's WAM does not have (adding a top-level
opcode is a stop-and-propose decision per CLAUDE.md; hence this ADR).

## Decision (proposed)

### 1. One new opcode

- **`jump <target:int32>`** — unconditional branch to an absolute code address
  (same operand convention as `try_me_else`). Numbered at the end of the dense
  dispatch block per the chunk-429 contiguity policy. No engine state touched:
  `Pc = target`.

### 2. Inline lowering in `ClauseCompiler`

For a body `(Cond -> Then ; Else)` whose parts are conjunctions of plain goals
(no internal `!` needing the chunk-408 barrier threading, no nested
disjunctions — those keep the helper), emit **in the host clause**:

```
get_level Yk          ; capture the barrier for the ITE's own CP
try_me_else ELSE      ; arity-0 CP — the else-branch resume point
<Cond>                ; compiled inline (real WAM, real indexing)
cut Yk                ; commit: pop the ITE CP (ISO ->/2 semantics)
<Then>
jump END
ELSE: trust_me        ; pop the CP on the else path
<Else>
END:  ...rest of the clause body
```

Plain `(A ; B)` is the same without `get_level`/`cut` — both branches stay
reachable on backtracking, `trust_me` pops the CP on entry to `B`.

**Variable discipline (the key simplification):** any variable the `Else`
branch (or the post-ITE continuation) needs is classified as crossing a chunk
boundary at the ITE — exactly as it is today when the branch is a helper call —
so it lives in a **Y slot**, and the ITE's `try_me_else` CP can be pushed with
**arity 0** (no X registers to save/restore). No new CP machinery.

### 3. Both tiers must understand the shape

The same bytecode feeds Tier-1, so `IlPredicateCompiler` must describe + emit
the new shape **before** the compiler starts emitting it (otherwise predicates
with body-ITE would LOSE IL promotion — a regression against today's
helper form, which IL compiles and regions optimize):

- `jump` → a Sigil unconditional `br` to the target's label (trivial);
- mid-body `try_me_else`/`trust_me` → the existing IL choice-point machinery
  (`PushIlChoicePoint` + a resume cursor at ELSE), the same pattern the clause
  chains already use;
- `cut Yk` → existing deep-cut emission.

Rollout order: (a) opcode + interpreter case + disassembler; (b) IL
describe/emit for the shape, gated OFF in the WAM compiler; (c) flip the
compiler lowering ON behind a flag; (d) A/B against the helper form (`--alloc`
+ interleaved wall-clock per the measurement discipline); (e) default ON,
remove the flag.

## Consequences

- Deterministic ITE at Tier-0 drops a Call + helper dispatch + head
  unification per execution; the CP push remains (semantically required) but is
  arity-0 and popped by `cut`/`trust_me` without the retry chain.
- `MetaTransform` keeps the helper path for: branch cuts (chunk-408 barrier),
  var/non-callable conditions, and nested disjunctions — the inline form is an
  opportunistic fast shape, not a replacement.
- One-time consumers to update: interpreter dispatch (one case), OpcodeTable,
  `shumway-disasm`, IL describe/emit, region-member eligibility (a body-ITE
  member is still region-eligible — the IL side lowers it natively anyway).
- Risk: the permanents/chunk-model interaction (Y-classification at the ITE
  boundary) must match what the helper call implies today — the discipline in
  §2 keeps it identical by construction. The Phase-25 chunk-model failure arc
  does NOT apply (no X-register lifetime extension is attempted).

## Alternatives considered

- **Mandatory link-time unfold of the helpers** (extend `MetaWrapperUnfold`):
  removes the cross-module call but still leaves the helper's own clause
  dispatch + CP chain; doesn't reach consult-time-only programs (REPL, plain
  `ConsultString` embedding). Weaker win, similar IL-side work.
- **Superinstruction fusing Call+helper**: does not remove the head
  marshalling or the second clause's retry chain.
