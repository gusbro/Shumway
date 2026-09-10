# ADR-050: A WebAssembly Tier-1 backend

## Status

Accepted, phases 0–2 shipped. The engine compiles a hot predicate to a
WebAssembly module and runs it natively; phase 0 (the Go/No-Go spike) and
phases 1–2 (the backend and the live-engine wiring, desktop and browser) are
in the tree with measurements. Phase 3 (AOT bundles) and phase B (open-coded
builtins, decided by the bail-frequency data) remain. The full design and the
running record live in [`docs/design/wasm-tier1-plan.md`](../../design/wasm-tier1-plan.md);
this ADR records the decisions the policy calls major — a new backend and a
new external dependency.

## Context

WebShumway runs the whole engine in the browser, but only Tier-0, interpreted
by Mono's own wasm interpreter: an interpreter on an interpreter. Tier-1 (IL
emission) is unavailable there — `Reflection.Emit` throws under browser-wasm,
and the `Shumway.RuntimeCodegen` feature switch trims the IL subsystem out of
the payload. On the desktop, Tier-1 buys 1.9–5.5x over a Tier-0 that already
JITs; against the browser's interpreted Tier-0 the ceiling is far higher, and
no measurement existed.

The browser IS a WebAssembly engine. The opening is to compile WAM to
WebAssembly modules and run them natively — the browser's Tier-1 — rather than
interpret bytecode with the interpreter that is itself interpreted.

The consuming side is already backend-agnostic: `ITier1Dispatcher`, the
`PredicateDelegate` contract, the phase-16 resume markers, and the ADR-014 IL
choice points work for any producer of delegates. What was not abstracted is
the producer (`Sigil.Emit<PredicateDelegate>`), so the wasm backend is a fork
of the emitter, not a retrofit of the IL one. The heap is a managed `Cell[]`,
which ADR-042 §2 named as the obstacle to a second module touching engine
memory; the resolution is that a cell holds only indices, never addresses, so
the engine's own arrays ARE the module's working memory.

## Decision

Seven decisions, D1–D7 (the plan carries them in full):

**D1 — the call path is a raw `calli` through the thread's function table, no
JavaScript on the hot path.** The phase-0 spike proved this, after a false
No-Go: with threads on, every worker has its OWN `WebAssembly.Table` (only the
memory is shared), so an `addFunction` index from one realm is invalid in
another and calling through it traps the worker silently. The fix is to
register the module in the CALLING thread's realm, through a C shim's EM_JS
(`spike.c`, linked into the native relink that threads already force). With a
thread-local index the raw managed `calli` works — boundary ~285 ns, measured.

**D2 — the memory contract is a mailbox plus the engine's own pinned arrays.**
The module imports the shared runtime memory and reads a mailbox (`WasmAbi`,
`Shumway.Core`) of 8-byte slots: the base addresses of heap/stack/registers/
trail plus every scalar WAM register (H, B, HB, TR, E, CP, write-mode, the
unify pointer, the cut barrier, ViewGen). The runner pins the arrays with
`fixed`, writes their real addresses and the scalars, calls, and syncs the
scalars back. The arrays are only ever replaced (growth, GC) by managed code,
and managed code only runs while the wasm has bailed, so the pins are stable
for exactly the duration of each call. This settles ADR-042's open question.

**D3 — the module never calls managed; it returns a verdict.** The export is
`(mailbox, cursor) -> verdict`: Success, SuccessTailCall (Pc names the callee),
BuiltinRequest (id + return cursor), Fail, or **Deopt** — the scalars are
synced and Pc is the bytecode address of the very instruction, so the
interpreter resumes as if the predicate had never been compiled. Deopt is
cheap because the state IS the engine's; it makes every compiler exclusion a
PERFORMANCE decision, never a correctness one. The verdict loop
(`WasmTierDelegate`) is the `PredicateDelegate`, and it drives builtins and
deopt through the interpreter's existing post-delegate path (`IlTailCallPending`
+ Pc) with no interpreter change.

**D4 — the compiler mirrors the engine's state cell-for-cell.** Env frames, the
11+arity choice-point control words with the BP field, the young-to-old trail
rule, the RawInt encoding — all identical, so a wasm-pushed choice point is a
real one the interpreter backtracks into, and a callee's proceed resumes the
wasm caller. The control flow is a dispatcher `br_table` over CURSORS, which
are also the re-entry vocabulary: resume markers and choice-point BPs name
cursors, so a call return and a backtrack land in the same dispatch. `try`/
`retry`/`trust` are open-coded (the full choice point in wasm); the general
unifier is a second wasm function over a worklist above the stack top; ADR-020
reserved builds are the engine's write-frame cascade replayed at compile time.

**D5 — all encodings are interned resume markers.** In the live engine
(`EngineWasmCompileEnv`) a call target is `marker(callee, 0)`, a choice
point's BP is `marker(self, retry-cursor)`, a call continuation is
`marker(self, cursor)`, and a deopt Pc is the linked base plus the local
offset. Markers are interned pairs, not arithmetic, so there is no cursor
range cap. The test harness bakes its own small encodings behind the same
`IWasmCompileEnv` interface, which is what lets the backend have desktop tests
with no browser.

**D6 — the emitter is the `WebAssembly` NuGet package (dotnet-webassembly,
Apache-2.0), isolated behind our own types.** It builds and validates a module
in memory AND executes one without a browser through its wasm-to-IL engine,
which is what gives the backend xUnit coverage. Isolated so an in-house emitter
can replace it later. Where the package omits the shared-memory limits flag,
one byte is post-patched (`WasmSharedMemory`).

**D7 — a capability behind a feature switch.** `RuntimeCaps.SupportsWasmCodegen`
(`Shumway.WasmCodegen`, default false); only Shumway.Web turns it on. Desktop
builds trim the whole `Shumway.Compiler.Wasm` subtree and the package, mirror
of `Shumway.RuntimeCodegen`. Consult the property, never cache it, so the
trimmer can fold it.

## Consequences

The tier runs in the live engine, desktop and browser, measured
([`docs/benchmarks/browser.md`](../../benchmarks/browser.md)). A tight
self-tail arithmetic loop stays inside the module and wins ~100–220x over the
interpreted Tier-0; call-and-allocate-heavy code (nrev) barely moves, and
recursion-heavy code (tak) is dominated by the non-tail-call boundary — its
arithmetic is open-coded, so the tax is the three non-tail self-calls per
invocation round-tripping the interpreter, not builtins. Those last two are
not correctness limits — deopt returns them to the tier they were on — and the
measurement points the next work at the inter-predicate call boundary: a
direct wasm-to-wasm call that resolves the callee's table index instead of
returning to the interpreter. Open-coded wasm builtins help builtin-dense
predicates too, but the data puts calls first.

The new dependency is permissive (Apache-2.0), executes in-process for tests,
and is trimmed out of every non-browser build. No invariant in
`docs/architecture/invariants.md` changes: the module manipulates the engine's
own heap/stack/trail under the same rules the interpreter uses, activations
stay single-threaded internally and thread-agile (the function-table index is
cached per thread, the state is not), and the atom/functor tables stay global
and shared (mirrored read-only into the module for the general unifier).
