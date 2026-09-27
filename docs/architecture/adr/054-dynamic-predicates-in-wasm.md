# ADR-054: Dynamic Predicates in the wasm Tier (shadow snapshot + retire-on-mutation)

## Status

Accepted (2026-09-26). Extends [ADR-023](023-dynamic-predicates-in-il.md) to
the wasm backend of [ADR-050](050-wasm-tier1-backend.md).

## Context

The wasm tier compiles static predicates only. A `:- dynamic` predicate's
bytecode opens with `enter_dynamic` and is patched in place by every
`assert`/`retract` (ADR-015), so a module compiled from it would go stale;
the compiler has no lowering for that opcode and the promotion store marks
the predicate unpromotable. Every call to one from a promoted caller is a
foreign exit: the chain closes, the interpreter runs the callee, a fresh
chain reopens for the continuation.

That is the largest thing left in clp(Z) on the tier. `clpz_neq/2` is
declared dynamic and has source clauses generated at load; it is never
mutated afterwards. On queens24 x2 it is 2,760 of 2,904 foreign exits, 95%,
and `#wasmexits` prices a dynamic call from the module at 18 to 23 us
against 2 to 6 on Tier-0 (`docs/benchmarks/wasm-split-spike.md`). Every
library that declares a predicate dynamic for `goal_expansion` or
`multifile` convenience and then only reads it has the same shape.

The IL tier solved this in ADR-023: a dynamic predicate is compiled as a
static-style SNAPSHOT of its visible clauses, guarded by a mutation stamp,
evicted by `InvalidateDynamicCache` on every mutation, and pinned to Tier-0
after too many evictions. IL needs no bytecode of the snapshot: the delegate
is the whole implementation.

A wasm module is not. A chain can leave the module at any guard (a deopt),
and the interpreter continues at the deopt's pc in the live program. A
snapshot compiled to wasm therefore needs its bytecode LINKED at a real
address, or every deopt inside it lands nowhere.

## Decision

A dynamic predicate promotes to the wasm tier as a snapshot, the way ADR-023
promotes it to IL, with two additions: the snapshot's bytecode is linked
into the persistent program as a SHADOW region, so the interpreter can run
it, and a mutation RETIRES the member rather than evicting it, so the call
in flight can finish in it.

1. **Candidate.** `WasmPromotionStore.RecordDispatch` receives the live
   `enter_dynamic` predicate and the running activation (the dispatcher
   adapter hands both over). When its bytecode opens with `enter_dynamic`
   and the host can retire a snapshot (it wires `ShadowRetired` and
   `StaleEvicted`, as the browser and the test harness do), the store asks
   the engine for a shadow snapshot instead of compiling the chain. No
   visible clause is a retry, as in ADR-023, and so is ADR-023's churn pin,
   shared with the IL store (`DynamicChurnPinned`). A host that wires
   neither keeps the old behaviour: the compiler refuses the chain.
2. **The clauses.** The query setup compiles a dynamic predicate's chain
   from two sources, the module's clauses first and the dynamic store's
   after them, and `BuildDynamicSnapshot` read the store only. The two
   disagreed for `clpz_neq/2`: its 18 clauses, produced by a
   `term_expansion` hook of clp(Z)'s own file, were left among the module's
   clauses by the consult's re-expansion pass, where the chain ran them but
   `retract/1` and a module-qualified `clause/2` never saw them. That pass
   now stores them in the dynamic store like any other clause of a dynamic
   predicate (`ExpandedDynamicClausesTests`). The snapshot still reads both
   sources, the chain's own order, so it is the chain whatever the consult
   does. A mutation leaves those sources stale until the next setup, so a
   functor mutated since then gets no snapshot until it runs.
3. **Shadow region.** `PrologEngine.BuildShadowSnapshot` takes that
   snapshot, links it alone,
   `Linker.Link([clone], loadOffset: end of the code space, externalSymbols:
   the running address map)`, and appends the bytes with
   `Activation.AppendCode`, the path a mid-query `assertz` uses; its switch
   tables join the program's. The clone carries a functor of its own
   (`$snapshot$name/arity`), so a call inside it to the predicate itself
   links to the live `enter_dynamic` chain and not to the shadow: after a
   deopt, a recursive call from the shadow's bytecode reaches the predicate
   as it is then. No name maps to the shadow's address. The snapshot handed
   to the wasm compiler keeps the predicate's own functor, which is what the
   member is installed under, and the shadow's address is its bias.
4. **Install.** The member is a module of its own, never part of a batch,
   and no module bakes a direct jump to it, itself included: every call to
   it goes through its entry row. The store keeps it apart from the
   relink bookkeeping (`ReconcileWithLink` never sees it); the translation
   of a build pc leaves a shadow address as it is, since a shadow never
   moves.
5. **Retire on mutation.** `PrologEngine.InvalidateIlForFunctor`, which
   every mutation reaches, evicts the IL store's delegate and then tells the
   wasm store, which RETIRES the member (`WasmModuleRegistry.Retire`): its
   entry row and call markers are cleared, so a new call from a module or
   from the interpreter goes to the `enter_dynamic` chain, and its resume
   rows stay, so a choice point or a continuation inside the old module
   still lands there. The next promotion waits for the threshold again.
6. **The call in flight.** A call that began before the mutation finishes
   on the clauses it began with, which is the logical update view. Inside a
   chain its resumes resolve through the rows the retirement kept. From the
   interpreter, a resume marker needs a delegate for its functor, and the
   IL store's is gone: `EvictDelegate` now keeps the evicted delegate for
   RESUMES only (`ITier1Dispatcher.ResolveRetiredResume`), which the
   interpreter asks for when a marker's cursor is past the entry and no
   delegate is bound. A fresh call never takes it. Before this, that case
   threw ("a Tier-1 promotion must have unwired itself mid-query").
7. **Code space rebuilt.** A consult or an abolish builds the persistent
   program again, and the shadow regions go with the old buffer: at that
   point every snapshot, retired ones included, is evicted from the world
   (its rows cleared) and promotes again on demand. That eviction does not
   count toward the churn pin.

## Consequences

- A `:- dynamic` predicate with source clauses runs in the wasm tier between
  mutations, in-module, reached through its entry row like any member: the
  foreign exit and the two chain boundaries it cost are gone.
- A shadow costs its bytecode once per promotion, appended and never
  reclaimed until the code space is rebuilt; a mutation costs a retirement
  and, on demand, a re-promotion; the churn pin bounds how often that
  repeats.
- A retired module's resume rows stay until the next rebuild, so the calls
  running in it can finish; they are unreachable once those calls end.
- The interpreter's resume path gains one fallback, taken only where it
  used to throw; it also covers an IL delegate evicted under an open
  continuation.
- The IL tier promotes a module's dynamic predicates too, under ADR-023's
  rules, since it reads the same snapshot.

## Tests

`DynamicShadowTests`, every answer checked against Tier-0: the snapshot is
promoted; a mutation reaches the next call; the call in flight finishes on
the clauses it began with; a call that starts after the mutation sees it,
from inside the snapshot's own recursion; a deopt continues in the shadow's
bytecode, and a recursive call from there reaches the live predicate; a
mutation-heavy predicate is pinned; a rebuilt code space evicts the
snapshot and the next call promotes it again; and, with the diagnostic
counters, a promoted caller reaches the snapshot with no foreign exit
where a host that cannot retire leaves on every call. A predicate declared
dynamic in a module promotes and sees an assert and a retract of its source
clauses, on the wasm tier (`DynamicShadowTests`) and on IL
(`DynamicIlPromotionTests`). Three of these fail
when their fix is undone: without the retired resume the in-flight call
throws, with a baked self-jump the new call misses the new clause, and a
shadow linked under the predicate's own functor breaks the recursion after
a deopt. The staleness guard of point 2 turns no test red: neither tier
promotes a predicate again inside the query that mutated it. It stays,
because a snapshot of stale sources would be a silent wrong answer.

## Out of scope

Dynamic predicates with no source clauses (runtime-assert-only) stay
Tier-0, as in ADR-023. Reclaiming retired shadow regions is the dead-region
reuse question ADR-015's layout already carries, unchanged here.
