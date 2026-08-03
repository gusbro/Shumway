# Phase 19 — Closure

**Status**: complete.

**Tagged**: `phase-19`.

Phase 19 closes the last gap in Tier-1 IL coverage: `call/N` and
`'$call'/2` are now IL-emittable. Before this phase the chunk-201
gate kept any predicate body containing a `CallBuiltin call/N` or
`CallBuiltin '$call'/2` on Tier-0 because the IL emit invoked the
builtin's `Impl` directly, which for `'$call'/2` throws
`InvalidOperationException` ("must be dispatched by the interpreter,
not invoked directly") and for `call/N` would miss the bytecode
interpreter's cut-barrier-aware dispatch.

## The helper

`IlMetaCallHelper.Dispatch(engine, callArity, cutBarrier)` is the
runtime mirror of the bytecode interpreter's `DispatchCall` (chunks
86, 88). It:

- Derefs X[0] for the runtime goal. Throws
  `instantiation_error` / `type_error(callable, _)` per ISO.
- Moves goal args into X[0..goalArity-1], appends `call/N`'s extra
  args at X[goalArity..]
- Routes the chunk-88 control constructs (`,`/`;`/`->`/`\+`/`not`)
  to their `$call_conj/$call_disj/$call_arrow/$call_neg` helpers
  with the cut barrier on X[2].
- Intercepts `!` / `true` / `fail` inline and returns
  `SyncSuccess` / `SyncFail` so the IL caller can fall through or
  jump to fail without spinning up a real dispatch.
- Recurses for builtin-as-goal `call(call(...))` and
  `'$call'('$call'(...))`.
- For all other functors: sets `B0 = cutBarrier` and returns the
  callee's bytecode address; the IL caller threads the dispatch
  via chunk-182.

`IlMetaCallHelper.ReadIntRegister(engine, reg)` is the partner the
`'$call'/2` emit uses to lift the barrier int out of X[1] without
the IL inlining the deref-then-AsInt sequence.

## The emit

`EmitClauseBody`'s `CallBuiltin` handler grew a special case for
`call` and `$call` builtins. The previous chunk-201
`IsClauseBodyOpcode` gate is gone; `CountNonTailCallOpcodes` now
counts these CallBuiltins as non-tail call sites so the chunk-188
chain emit reserves a resume cursor for each.

The emit invokes `Dispatch` with `(arity, barrier)` derived from
which builtin fired (`call/N` uses `engine.B`; `'$call'/2` reads
X[1].AsInt). It then branches on the return:
- `SyncFail` (-1) → `goto failLabel`.
- `SyncSuccess` (-2) → fall through to the resume label, which is
  positioned just after the IL emit for the CallBuiltin so the
  next opcode (typically Proceed) runs.
- target ≥ 0 → chunk-182 threaded dispatch: `SetCp(resume marker),
  SetPc(target), IlTailCallPending = true, return true`.

## The last-call subtlety

When `CallBuiltin call/N` or `CallBuiltin '$call'/2` is immediately
followed by `Proceed` (the meta-call is the clause's final goal),
the bytecode interpreter's `DispatchCall` does NOT set Cp — Cp
stays as the outer caller's so the meta-called goal's proceed
returns straight to the outer caller. This is the standard WAM
last-call optimisation.

A first cut of Phase 19 missed this and always set `Cp = resume
marker`. The result was a tight loop: outer caller dispatches IL
predicate → IL meta-calls into a leaf goal → leaf's Proceed sets
`Pc = Cp = resume marker` → bytecode interpreter decodes marker →
re-enters IL at resume cursor → IL hits Proceed → `return true` →
bytecode interpreter's resume handler does `SetPc(_engine.Cp)` →
Pc = same marker → loop forever.

The chunk-202 `OnDispatch` cache hid the per-iteration overhead
but didn't break the loop — Blint just hung. Bisected to
`'$call_disj'/3`'s clause-2 body (the canonical `$call(A, K)`
shape that exercises this pattern), then traced via
`SHUMWAY_TRACE_METACALL` env-var to the resume-marker re-decode
spin.

Fix: the emit checks whether the next opcode after the CallBuiltin
is Proceed and skips `SetCp` in that case. Subsequent
`Deallocate` (post-Allocate) cases route through the non-tail
path because Deallocate restores Cp from the env frame —
correct end-state either way.

## Tests

`Phase19MetaCallTests` covers ten scenarios for the IL meta-call
path, run in-process so the persisted IL builds and executes
without leaving the test runner:

- `call(Var)` bound to an atom / compound / known compound + extra args
- `call(true)` / `call(fail)` — synchronous success / failure
- `call((A,B))` / `call((A;B))` — conjunction / disjunction routing
- `call(\\+ fail)` — negation-as-failure routing through `$call_neg`
- `call(call(...))` — recursive meta-call
- `call(P, Ord, X, Y)` — the comparator-passing pattern
  `predsort_ins/4` uses

All 10 pass. The other Phase 17 / 18 e2e tests (Blint probe,
PePatch end-to-end, local-entry-point, Phase 18 bisect) stay green.

## Measurements (Blint cross-process, 3-run median)

| Configuration | Time | Correct? |
|---------------|------|----------|
| Direct REPL consult (Tier-0) | 15s | ✓ |
| Bundle Tier-0 | 9.2s | ✓ |
| **Bundle Tier-1 persisted IL** | **7.9s** | ✓ |

Persisted IL count: 144 → 152. The 8 predicates Phase 18 chunk 201
kept on Tier-0 (4 prelude `$disj_*` helpers, `predsort_ins/4`,
`$call_disj/3`, plus 2 Blint helpers) are now IL.

The Tier-1 IL margin over Tier-0 is similar to Phase 18 (~15%)
because the 8 newly-IL-eligible predicates aren't hot enough on
Blint to dominate; the meta-call route they now take has a
slightly higher per-dispatch cost (the runtime functor lookup
inside `IlMetaCallHelper.Dispatch`) than the static functor IDs
chunk-182 uses for regular Call sites, so the IL/Tier-0 ratio
doesn't shift much. The architectural win is that Tier-1 IL is
now *complete* — no fallback path, no special exclusions, and the
remaining Phase-16 follow-ups (chunk-202's dispatch fast path, the
OnDispatch cache) cover every dispatch unconditionally.

## Suite

417 + 250 + 105 + 275 + 1646. Same 3 pre-existing Chunk45 PreWarm
failures (inherited from chunk 192).
