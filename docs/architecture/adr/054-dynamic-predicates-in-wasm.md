# ADR-054: Dynamic Predicates in the wasm Tier (shadow snapshot + evict-on-mutation)

## Status

Proposed (2026-09-26). Extends [ADR-023](023-dynamic-predicates-in-il.md) to
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
promotes it to IL, with one addition: the snapshot's bytecode is linked into
the persistent program as a SHADOW region, so the interpreter can run it.

1. **Candidate.** `WasmPromotionStore.RecordDispatch` receives the live
   `enter_dynamic` predicate (the dispatcher adapter hands every dispatch
   over). When its bytecode opens with `enter_dynamic`, the store asks
   `IlPromotionStore.DynamicSnapshotProvider` (that is
   `PrologEngine.BuildDynamicSnapshot`) for the snapshot instead of compiling
   the chain. No visible clauses is a retry, as in ADR-023; the ADR-023
   churn limit applies unchanged, through the IL store's eviction count.
2. **Shadow region.** The engine links the snapshot alone,
   `Linker.Link([clone], loadOffset: end of the persistent program,
   externalSymbols: the persistent address map)`, and appends the bytes with
   `Activation.AppendCode`, the path mid-query `assertz` already uses. The
   clone carries a synthetic functor (`name$snapshot/arity`) so the region
   claims no name a call could resolve to: calls keep going to the
   `enter_dynamic` chain, which is the logical update view. The snapshot
   handed to the wasm compiler keeps the REAL functor id, so the member's
   entry marker is the one callers resolve. The region's address is the
   member's bias; deopt pcs translate into it.
3. **Install.** The member installs as any static member does; the store
   records `fid -> (synthetic fid, shadow address)`. `ReconcileWithLink`
   compares a shadow member against its clone's address and code hash, not
   against the real functor's (which the static link never lists), and the
   live-address map handed to the world carries the real fid at the shadow
   address.
4. **Evict on mutation.** `PrologEngine.InvalidateIlForFunctor` (every
   mutation funnels through it) also tells the wasm store. The store evicts
   the member from the world through the registry's cascade (baked callers
   included), retires the shadow (dead, kept in place: the layout is
   append-only), and drops its record. New calls dispatch the
   `enter_dynamic` chain from that moment: the delegate table entry is gone,
   the per-query `IlByFunctorId` slots are cleared, and the resume table
   has no entry marker for the functor.
5. **Open chains.** A chain suspended in a builtin request (the `assertz`
   itself, or anything the mutation runs under) resumes after the eviction
   with rows it can no longer resolve. The delegate treats that exactly as
   it already treats an entry evicted under an open marker: it closes the
   chain and continues in bytecode at the translated return address. For a
   chain inside the snapshot, that address is in the shadow region, which
   is linked and live, so the call that began before the mutation finishes
   on the clauses it began with. That is the logical update view, and it is
   why the shadow has to be real bytecode.
6. **Persistent rebuild.** A consult or abolish rebuilds the persistent
   program, and the shadows with it; every shadow member is evicted then and
   promotes again on demand. The static-layout stability of ADR-015's
   append-only layout is what keeps a shadow's address valid between
   rebuilds, relinks included.

## Consequences

- A `:- dynamic` predicate with source clauses runs in the wasm tier between
  mutations, in-module, with markers like any static member: the foreign
  exit and the two chain boundaries it cost are gone.
- The shadow costs its bytecode once per promotion, and a mutation under a
  hot snapshot costs a cascade eviction plus a re-promotion; the churn limit
  bounds how often that repeats, as it does for IL.
- The delegate's resume-after-builtin path stops throwing on an unresolved
  return address and falls back to bytecode instead; the diag counters
  report those closes, so a silent slow path stays visible.
- Nothing changes for the interpreter, the IL tier, or a program that never
  promotes a dynamic predicate.

## Tests

- Answers under mutation: a snapshot promoted, then `assertz`/`retract`
  from inside a promoted caller and from the top level, with the answers
  of Tier-0 in every order, including the in-flight call finishing on the
  old view.
- Counters: `clpz_neq/2`'s foreign exits go to zero on queens; a mutation
  evicts exactly the member and its baked callers; a re-promotion follows;
  the churn limit pins.
- Relink and rebuild: a consult after a shadow promotion evicts it and the
  next call re-promotes; a relink that moves nothing keeps it installed.
- Two engines in one process, same program, one mutating: the other's
  shadow is untouched.

## Out of scope

Dynamic predicates with no source clauses (runtime-assert-only) stay
Tier-0, as in ADR-023. Reclaiming retired shadow regions is the dead-region
reuse question ADR-015's layout already carries, unchanged here.
