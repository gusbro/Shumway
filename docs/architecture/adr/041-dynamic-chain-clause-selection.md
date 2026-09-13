# ADR-041: Dynamic-chain clause selection at dispatch (tier-uniform determinism)

## Status

Shipped ([Phase 36](../../history/phase-36-closure.md), 2026-08-01).

Includes live-link coverage: mid-query trampolines register their address in the chain table (TrampolineFids), so the selector serves consult-compiled AND live-linked chains. Measured: Logtalk linear_algebra 27/72 -> 72/72, types 148/149 -> 149/149 (the long-standing determinism edge closed).

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
   - **2+ candidates** → the selection declines and the chain runs from its head unchanged (the CP
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
  bound-first-arg call.
- The JIT indexed compile remains purely a performance upgrade (O(1)
  dispatch), as it always should have been.

## Amendment (2026-09-11): the key really is per entry now

As shipped, the selection did NOT do what point 1 above says. The key was
re-derived from the clause's AST on every dispatch — interning the head atom
per clause, per call — and the candidates were found by walking every entry.
The original cost note excused that as "the same order the chain walk itself
would pay", which does not hold: the walk stops at its first match, while the
selection must see every entry to prove there is no SECOND candidate. So it
always paid the full O(entries), including on the path it was meant to speed
up.

That is invisible while the JIT's indexed recompile is doing the real work,
because that recompile happens at query SETUP. It is not invisible for a
predicate built and used inside ONE query, which never reaches it — which is
what `setup_call_cleanup/3` does, asserting a `'$cleanup_pending'` clause per
level. A nest of it was quadratic for this reason.

Point 1 is now true: the key is decided once in the entry's constructor, and
`DynChainState` keeps buckets beside its list — one per key, plus the entries
that match anything — maintained by the only four operations that change the
list. 20,000 calls on one key, varying only the predicate's size: 0.86s ->
0.20s over 2,000 clauses, and 20.0s -> 0.08s over 32,000.

Ordering is unaffected BY CONSTRUCTION, which is what makes this safe: the
buckets answer only "exactly one candidate" or "none". Two or more still
declines and the chain runs from its head, so nothing here can reorder a
solution.

`retract/1` is NOT covered: it walks the clause list itself rather than
dispatching, so it stays O(clauses) and the `setup_call_cleanup` nest stays
quadratic until that is addressed separately.

## Conformance case

The repro above joins the regression suite: first-call `call_det(t(b), D)`
must yield `D = true` with the JIT threshold at its default, and equally
with the threshold forced high (never-hot) and low (always-hot).
