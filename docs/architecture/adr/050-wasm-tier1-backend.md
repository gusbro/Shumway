# ADR-050: A WebAssembly Tier-1 backend

## Status

Accepted and shipped. The engine compiles a hot predicate to a WebAssembly
module and runs it natively. In the tree, with measurements: phase 0 (the
Go/No-Go spike), phases 1 and 2 (the backend and the live-engine wiring,
desktop and browser), phase B (open-coded builtins, its gate cleared at about
50x), the direct call between compiled predicates, group modules, the
relocatable module baked at build time (what phase 3 became), and the hop
between the modules of one engine inside wasm. The cleanups of the
many-modules arc remain open. The full design and the running record live in
[`docs/design/wasm-tier1-plan.md`](../../design/wasm-tier1-plan.md); this ADR
records the decisions the policy calls major: a new backend and a new
external dependency. Amended 2026-11-11: the module writes state the host
owns only through rings the host drains in order. Amended 2026-11-22:
`jit_compile(off)` evicts this backend's builds at the goal, and the helpers
of a bundle's dynamic rules are baked.

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
the producer (the IL emitter), so the wasm backend is a fork
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
A failure resumes a choice point in-chain only above `Activation.BacktrackFloor`,
staged in the mailbox: the rule ADR-057 sets for IL. At or below it the choice
point belongs to the computation outside a nested driver's sub-goal (a wakeup
drained before a cut), and the failure goes back to the host.

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

**D8 — the tier's setting is a predicate, not a page command.**
`jit_compile(off | all | N)` is a builtin of the engine, so a consulted file
can set it with a directive and a harness written in Prolog can ask for a
configuration without knowing which product it is running in: a build has
exactly one Tier-1, the IL compiler in Shumway and this backend in
WebShumway, and the same goal names whichever it is. The page's
`wasm_compile/1` was renamed to it rather than kept as an alias: the tier
had never shipped, so nothing was owed compatibility. `jit_compile(status)`
stays page-side, being a report of this backend rather than a setting, and
the top level answers it the way it answers `restart.`.

`off` evicts what already promoted, deferred to the next query setup. It
cannot happen where it is asked: a choice point created inside tier code has
to be able to redo there, and query setup is the existing safe point for work
of this shape (the dynamic-buffer compaction is deferred to the same place).
So `off` governs the goals after it, not the one it appears in. For this
backend that changed: see the amendment of 2026-11-22.

The browser keeps the mode across `restart.`, which builds a fresh engine:
the setting is what the session is working under, and clearing the database
is not a reason to change it.

## Consequences

The tier runs in the live engine, desktop and browser, measured
([`docs/benchmarks/browser.md`](../../benchmarks/browser.md)). The first
measurement put the cost at the boundary between predicates: a tight
self-tail arithmetic loop stayed inside the module and won 100 to 220x over
the interpreted Tier-0, while `nrev` and `tak` paid a round trip through the
interpreter for every non-tail call. That boundary is gone. A call between
the members of a module is a jump inside it, and a call or a backtrack into
another module of the engine is a tail call inside wasm, so in a headless
browser `nrev`, `tak` and `queens` run 15 to 25x over Tier-0. What leaves the
module now is a builtin it does not answer itself, and the open-coded
builtins of phase B took the frequent ones.

The new dependency is permissive (Apache-2.0), executes in-process for tests,
and is trimmed out of every non-browser build. No invariant in
`docs/architecture/invariants.md` changes: the module manipulates the engine's
own heap/stack/trail under the same rules the interpreter uses, activations
stay single-threaded internally and thread-agile (the function-table index is
cached per thread, the state is not), and the atom/functor tables stay global
and shared (mirrored read-only into the module for the general unifier).

## Amendment (2026-11-11): the module writes host state through rings

D3 keeps the module from calling managed code. Some builtins it now answers
change state the host owns: the wakeup queue (ADR-049, point 12), findall's
solution buffers, and the global-variable store with its external trail log.
The module does not reach into any of them. It does the part that lives in
linear memory (the heap, the trails, the images the host stages) and parks
the rest in a ring of the mailbox (`WasmAbi.WakeRingBase`,
`FindallRingBase`, `GlobalWriteBase`, each with a top and a limit). When the
chain hands control back, the host takes the entries in order before
anything reads that state, so it reads as if the builtin had run where the
module ran it. A ring that is not staged, or is full, makes the module
request the builtin as before: each ring is a performance decision, as D3
wants every exclusion to be.

A backtrackable global write is the one with a trail. The module writes the
image row and the `MutableSet` entry on the extra trail, with the log index
the host will give the record (the staged log length plus the entry's place
in the ring), and the host appends the log record and then sets the store.
The first write of a key stays the host's, since the image has no row for
it. A findall collect runs in the module only when the whole call is still
parked and the list goes to a free variable.

No invariant changes. The trails keep their formats and rules (an entry the
module writes is the one the host would have written), and the managed side
tables are written only by managed code.

## Amendment (2026-11-22): `off` evicts at the goal

D8 deferred the eviction of `jit_compile(off)` to the next query setup,
because a choice point created inside tier code had to redo there. That
holds for the IL compiler, and it no longer holds here. A choice point of a
module is a WAM choice point whose alternative names a bytecode address, and
since ADR-054 a build can leave in the middle of a query: its rows go, a
resume that names it resolves nowhere and lands on the bytecode, and a
snapshot of a dynamic predicate is retired for new calls and kept for the
calls running in it. A consult that defines a compiled predicate again
already evicts it that way, and so does `jit_compile(bundles_off)`.

The deferral also had a cost. A notebook's session is one query for its
whole life (ADR-067), so the next query setup never came: after
`jit_compile(off)` its program stayed compiled, and a change of form under a
threshold never took effect.

So with this backend `off`, and a change of form, evict every build at the
goal (`WasmPromotionStore.LeaveNow`). The rest of the goal that asked runs
on the interpreter, and a choice point left inside a build gives its next
solution from the bytecode. The IL compiler keeps the deferral.

## Amendment (2026-11-22): the helpers of a bundle's dynamic rules are baked

A rule of a dynamic predicate takes helper predicates when an engine first
links it: one for a disjunction, one for a negation, one for the goal of a
`catch/3`. Each is named with a number of that engine's counter, so the same
rule's helper is `'$disj_41'` in one process and `'$disj_97'` in the next. A
relocatable module names its members, and the bake left these out: a member
the loading engine names differently refuses the whole module. A host then
compiled them itself at every load. For the largest library measured that was
311 predicates and 1.5 to 4.3 seconds of each visit to the page.

The IL tier's bake of a bundle already takes them. It compiles each dynamic
predicate that has clauses as a snapshot (ADR-023), with the helpers that
snapshot reaches, and loads them as delegates. A delegate is the whole
implementation of its predicate, so the names of the baking engine do no
harm: nothing in the loading engine has to match them.

A module of this backend is not the whole implementation. It hands any
instruction back to the interpreter, which runs the bytecode at a linked
address, and the loading engine has that bytecode under its own helper's
name. So the bake names a helper by something every process agrees on: the
rule that took it and its place among that rule's helpers
(`PrologEngine.PortableHelperNames`, `WasmPortableNames`). The rule is the
key the engine already memoizes a rule's transform under, which is its
module, its place in its file and its term up to a renaming of variables,
less the file's id, which a process hands out. The module carries a digest of
that key. At install each such name resolves to the loading engine's helper
of the same rule, and the check of a member's code against the code it was
compiled from compares call sites under the same names.

These helpers go in a module of their own, the bundle's second
(`WasmBundleTier.BakeLoaded`). An engine may not have one of the rules as it
was baked: a clause retracted before the first goal, a rule transformed under
debug code generation, which keeps no memo. A module installs whole or not at
all, and such an engine refuses the helpers' module alone and compiles those
helpers as it did before. The module of the static predicates names none of
them and installs as it always did.

A module also carries the predicates its bake refused, each with the
compiler's reason (`WasmRelocatableModule.Refused`). A host that installs
the module marks them refused: its batch offered each of them to the
compiler again at every load, to be refused again. A refusal depends on the
forms the builtins have, which the install has checked by then.

A rule two files hold at the same place gives its helpers no portable name,
and they stay out of the bake. The snapshots themselves are baked too, each
in a module of its own: the amendment of the same date to
[ADR-054](054-dynamic-predicates-in-wasm.md).
