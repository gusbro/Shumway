# ADR-041 — Dynamic-chain clause selection at dispatch (tier-uniform determinism)

## Status

Accepted (2026-08-01). v1 SHIPPED: flat consult-compiled chains (the two-line repro reports det on first call, all tiers, all thresholds). PENDING: the mid-query LIVE-LINK chain path (Logtalk) does not yet route through the selector - linear_algebra unchanged; same design, needs the live-linked trampolines registered for selection.

## Context

An **unindexed** dynamic predicate compiles to a `try_me_else` /
`retry_me_else` chain. Dispatch pushes a choice point blindly, so a call that
matches any non-last clause reports **non-deterministic** even when no later
clause could possibly match:

```prolog
:- dynamic(t/1).  t(a). t(b). t(c).
?- call_det(t(b), D).   % D = false — GNU/SWI report true
```

Once the predicate crosses `JitIndexProfile.Threshold` and recompiles indexed,
the same call reports det. That makes **observable semantics depend on a
performance knob and on call history** — unacceptable: determinism must be
uniform across Tier-0 WAM and Tier-1 IL (whose ADR-023/031 machinery is
already CP-disciplined) and independent of hotness. This is the dominant
failure of the Logtalk library sweep (lgtunit `deterministic` tests over the
multifile-as-dynamic type-check library).

Raising/lowering the JIT threshold is explicitly rejected as a fix (it
conflates JIT with semantics, and was measured to break nothing less than the
bundle dynamic-seeds path while not even covering Logtalk's live-link chains,
which bypass the JIT profile).

## Decision

Select candidate clauses **at dispatch time, in the Tier-0 chain machinery
itself**, keyed by the call's first argument — uniformly for cold, hot,
live-linked and mid-query-consulted chains:

1. **Per-entry first-arg key.** Every chain entry record in the per-engine
   `DynChainTable` gains the clause's first-argument key, known at clause
   compile time: `Atom(id)` / `Int(v)` / `Struct(fid)` / `List` / `Var`
   (catch-all). The key rides the SAME registration/mutation paths the table
   already maintains (append / prepend / retract / in-place extend), so it
   stays correct under every mutation the chunk-155/156 machinery supports.

2. **Selection in `enter_dynamic`.** At the trampoline, dereference the
   call's first argument:
   - argument **unbound**, or predicate arity 0 → no selection (chain runs
     exactly as today);
   - bound → candidates are entries whose key equals the call key or is
     `Var`;
   - **0 candidates** → fail immediately (no chain walk, no CP);
   - **exactly 1 candidate** → jump straight to that entry's clause code
     (past its `try_me_else`, landing on `check_visible`, which still
     enforces the logical update view — a dead clause then fails the call,
     which is correct because nothing else could match) — **no choice point
     is created**;
   - **2+ candidates** → v1 runs the chain from its head unchanged (the CP
     is semantically justified: another clause may match). A later
     refinement may add last-candidate-as-trust.

The lookup is keyed by the chain-head address the trampoline's `execute`
targets (the trampoline carries no functor id), via a per-engine map
maintained alongside `DynChainTable`.

## Consequences

- `call_det`/`deterministic` answers become a property of the program, not
  of temperature: the two-line repro reports det on the FIRST call, matching
  GNU/SWI, and keeps reporting det at every tier and hotness.
- Logtalk's live-link chains get the same discipline (same table, same
  dispatch), which the JIT-threshold approach could never reach.
- Compiled shapes do not change: no new opcodes, no bundle-format impact,
  the dynamic-seeds path untouched.
- Cost: one deref + one small table lookup per dynamic dispatch of a
  bound-first-arg call; the selection scan is O(entries) over an in-memory
  array (the same order the chain walk itself would pay in failure cases —
  and strictly less work when it prunes).
- The JIT indexed compile remains purely a performance upgrade (O(1)
  dispatch), as it always should have been.

## Conformance case

The repro above joins the regression suite: first-call `call_det(t(b), D)`
must yield `D = true` with the JIT threshold at its default, and equally
with the threshold forced high (never-hot) and low (always-hot).
