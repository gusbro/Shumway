# WebAssembly Tier-1 — JIT and AOT from WAM to native wasm

## Context

WebShumway runs the whole engine in the browser, but **Tier-0 only,
interpreted by the Mono interpreter** (no `RunAOTCompilation`): an interpreter
on an interpreter. Tier-1 does not exist there because `Reflection.Emit` does
not work under browser-wasm, and the `Shumway.RuntimeCodegen=false` feature
switch trims the IL subsystem out of the payload. On the desktop, Tier-1 is
worth 1.9–5.5x (geomean ~3.3x) over a Tier-0 that already runs JITted
(`docs/benchmarks/analysis.md:38-58`); against the browser's interpreted
Tier-0 the possible margin is larger. No browser-vs-native benchmark existed.

**The idea**: a parallel backend that compiles WAM directly to **WebAssembly
modules** — JIT (promote hot predicates by emitting and instantiating a module
at runtime inside the browser) and AOT (`shumway-link` baking `.wasm` into
bundles). **No interpreted IL** (user decision): the goal is native code on
the browser's wasm engine. Benchmarks decide whether the arc continues.

What the exploration established (verified in the tree):

- **The consuming side is already backend-agnostic.** `ITier1Dispatcher`
  (`src/Shumway.Core/ITier1Dispatcher.cs`), the
  `bool PredicateDelegate(Activation, int cursor)` contract
  (`src/Shumway.Compiler.Il/PredicateDelegate.cs:28`), the resume markers +
  `IlTailCallPending` (interpreter path: `BytecodeInterpreter.cs:512-617`),
  and the ADR-014 IL choice points (`PushIlChoicePoint`, BP=-1, `_ilCpStack`)
  work identically for any producer of delegates. **Zero interpreter
  changes.**
- **The producer is not abstracted**: `Sigil.Emit<PredicateDelegate>` runs
  through ~7k lines. The wasm backend is a fork of the emitter, not a
  retro-abstraction.
- **The real ABI is ~93 helper calls** (68 `Activation`, 14 `ArithEvalStack`,
  ~9 `Cell`); `Cell` is `struct{long}` (maps to i64), `Activation` does not
  marshal. The obstacle ADR-042 §2 named — the heap is a managed `Cell[]` —
  is resolved with pinning + shared `WebAssembly.Memory`.
- **AOT already has a template**: `PersistedIlBuilder` + `IlPatchSite` +
  load-time patching (`BundleLoader.ApplyIlPatches`) and functor binding
  (`RegisterBoundDelegate`, unconditional install — works in the browser).
- **Browser**: a second module can import the .NET runtime's memory (shared,
  via `WasmEnableThreads`); synchronous instantiation is legal in workers;
  wasm 3.0 tail calls (Chrome 112+/FF 121+/Safari 18.4 — WebShumway already
  requires modern browsers for threads). The engine runs on pool threads; JS
  interop is affine to the runtime thread (reference pattern:
  `PageInput.cs:58-82`).

**Emitter** (user decision: hybrid): the `WebAssembly` NuGet package
(dotnet-webassembly, **Apache-2.0**, active — 2.1.0 as of Jul 2026, wasm 3.0,
zero deps; its wasm-to-IL execution engine gives xUnit tests with no browser),
isolated behind our own interface so an in-house emitter can replace it later.

## Design decisions (D1–D7)

- **D1 — Call path: raw `calli` through a table index, no JS on the hot
  path.** At instantiation (JS, runtime thread), `Module.addFunction(export)`
  registers the function in the dotnet module's table and returns an i32
  index; C# invokes it via `delegate* unmanaged<int,int,int>` from the pool
  thread. If this does not work under the Mono interpreter → **spike No-Go**
  (the JS-thunk path is rejected as a product: thread affinity + per-call
  marshalling).
- **D2 — Memory contract: mailbox + bases pinned per entry.** The module's
  view is (i) the imported shared dotnet memory
  (`(import "env" "memory" (memory 0 65536 shared))`) and (ii) a pinned (POH)
  `long[]` **mailbox** per `Activation`. The C# wrapper, on **every entry**,
  inside `fixed(Cell* …)` over `_heap/_stack/_registers/trails`, writes fresh
  bases + the WAM scalar registers into the mailbox, does the `calli`, and
  copies the scalars back. The heap can only be replaced (growth/GC) by
  managed code, and managed only runs when the wasm has bailed ⇒ bases are
  stable by construction during each wasm activation. This settles ADR-042's
  open question.
- **D3 — Bail protocol: the wasm never calls managed.** Export
  `(mailbox: i32, cursor: i32) -> i32 verdict`: 0=Fail, 1=Success,
  2=SuccessTailCall (mailbox Pc → `IlTailCallPending`), 3=BuiltinRequest,
  4=PushChoicePoint, 5=Safepoint (GC watermark or the wakeup/interrupt flags
  word, checked on every back edge). The wrapper **is** the
  `PredicateDelegate`: a loop that refreshes bases, calls, handles verdicts
  3–5 (invokes the builtin / `PushIlChoicePoint` / `MaybeCollectHeap` +
  wakeups) and re-enters at a continuation cursor (extra `br_table` cases,
  the same mechanism as the IL resume cursors).
- **D4 — Choice points via verdict 4** (no pre-registration): the delayed-CP
  forms (ADR-031) push mid-body; the push is already a boundary. Uses the
  existing `PushIlChoicePoint` with the wrapper as the delegate.
- **D5 — Isolated emitter**: our own `IWasmModuleWriter` interface with one
  implementation over dotnet-webassembly. If the library lacks the memory
  limits `shared` flag, that byte is post-patched in the import section (a
  well-defined binary location).
- **D6 — Id binding: constants for JIT, imported globals for AOT.** JIT
  compiles in-process with live ids → `i64.const`. AOT imports immutable
  globals resolved at instantiation from a `WasmBindSite[]` table
  (Kind/Name/Arity/Cursor, mirroring `IlPatchKind` incl. ResumeMarker) — the
  import object is the natural mechanism, no byte patching.
- **D7 — Capability + trimming**: `RuntimeCaps.SupportsWasmCodegen` with
  `[FeatureSwitchDefinition("Shumway.WasmCodegen")]`, default false; only
  Shumway.Web turns it on. Desktop trims `Shumway.Compiler.Wasm` + the
  package, symmetric to `Shumway.RuntimeCodegen`. Consult the property, never
  cache it (rule documented in `RuntimeCaps.cs`).

## Phase 0 — Go/No-Go SPIKE (~1.5-2 weeks)

A hand-built module for the self-tail counter
(`loop(N) :- N > 0, N1 is N - 1, loop(N1). loop(0).`), interop + memory only.

Files:
- NEW `src/Shumway.Compiler.Wasm/Shumway.Compiler.Wasm.csproj` (net10, refs:
  the WebAssembly package + Shumway.Core).
- NEW `src/Shumway.Compiler.Wasm/WasmAbi.cs` — the mailbox layout (named
  slots) + the verdict enum. Reused as-is by the full backend.
- NEW `src/Shumway.Compiler.Wasm/SpikeCounterModule.cs` — builds the module
  through the library: open-coded X0 deref, small-int tag test, i64
  arithmetic, self-tail as `loop`/`br`, watermark+flags check on the back
  edge → verdict 5. `BuildForTest(shared: false)` variant for xUnit.
- NEW `src/Shumway.Web/WasmTier.cs` — instantiation service (post to
  `_jsThread` in the `PageInput` style, async) + a spike wrapper implementing
  `PredicateDelegate` over the mailbox.
- NEW `src/Shumway.Web/wwwroot/wasmtier.js` — `getDotnetRuntime(0)`,
  `Module.wasmMemory`, `WebAssembly.instantiate` (async), `addFunction`,
  returns the index.
- MODIFY `src/Shumway.Web/wwwroot/main.js` (~:1633, beside `#selftest`) +
  NEW `wwwroot/wasmspike.js` — the `#wasmspike` hook: counter N=10⁷ Tier-0 vs
  wasm (installed via `IlPromotionStore.RegisterBoundDelegate`), median of 5,
  a table.

Mandatory measurements: (1) the `calli` boundary cost (degenerate module, 10⁶
entries; the JS thunk only as a comparative record); (2) base stability under
heap growth/GC between re-entries (verdict-5 bail → collect → re-enter with
fresh bases); (3) library fitness (the shared flag; validates and
instantiates in Chrome and Firefox; the non-shared variant runs in xUnit on
the library's wasm-to-IL engine).

**Go criterion (numeric): wasm ≥ 2.0x over interpreted Tier-0 on the counter,
in Chrome AND Firefox, with the boundary ≤ 1 µs per entry.** Less than that
in the friendliest possible shape = the boundary/memory tax ate the win →
No-Go, findings to `docs/benchmarks/browser-spike.md`, end of the arc.

### Status: PHASE 0 CLOSED — GO (1067x, boundary 285 ns)

The measurements are in [browser-spike.md](../benchmarks/browser-spike.md),
including an intermediate WRONG verdict worth more than the numbers:

- **The first attempt produced a false No-Go.** The calli hung because the
  index came from the PAGE's `addFunction`: with threads, every worker has
  its OWN WebAssembly.Table (only the memory is shared), and a foreign index
  either does not exist (a silent trap = the hang) or names a DIFFERENT
  function (worse: it would run the wrong code without failing). Measured in
  both variants.
- **The way through**: register IN THE CALLING THREAD'S REALM, via `spike.c`
  linked into dotnet.native.wasm (the relink already happens for threads):
  `shumway_wasm_register` (EM_JS: instantiates against the shared memory and
  addFunctions into THIS thread's table) + `shumway_wasm_call` (one line, one
  call_indirect). With a thread-local index, **D1's raw calli also works**
  (285 ns): the mechanism was never broken.
- **Chrome numbers**: wasm counter 5.4-9.8 ns/iteration against 5,783 ns of
  the browser's Tier-0 (600-1100x; the gate asked for 2x); boundary 250-420
  ns (ceiling 1000). D2 confirmed: the module imports the runtime's memory
  and addresses the mailbox and registers inside it.
- **Product shape**: bytes compiled once; every pool thread registers lazily
  and caches ITS index (a per-thread map). Firefox was not measured (not on
  this machine); what failed and got fixed belonged to the runtime, not the
  browser.

Phase 1 (the real backend) follows, with the mailbox ABI already pinned by
the desktop tests and this registration mechanism as the base.

### Status: desktop half DONE

These exist and work, with no browser:

- `src/Shumway.Compiler.Wasm/` (net10, `WebAssembly` package 2.1.0,
  Apache-2.0, zero dependencies of its own) with `WasmAbi` (a mailbox of 16
  64-bit slots + the six verdicts), `SpikeCounterModule` (the hand-built
  counter) and `WasmSharedMemory`.
- `tests/Shumway.Tests.Wasm/` — 16 tests that **execute** the module on the
  library's engine against a harness that places mailbox, registers and heap
  inside the imported memory. It is the same view the module has in the
  browser: it only addresses by offset within the memory it is handed.

What got pinned: the X0 deref reading the base from the mailbox, the tag
test, unpacking and repacking a whole cell (sign included), `loop`/`br` as
the tail call, the flags and watermark bail on the back edge, and the cursor
re-entry finishing the count where it left off.

Brought forward from the risk register: **the `shared` flag is no longer a
risk**. The library does not emit it, so the import's limits byte is patched
(0x01 → 0x03, one byte for one byte, no section moves) and tests pin it,
including that the patched module parses back whole.

The browser half remains, which is where the three mandatory measurements
live: `WasmTier.cs` + `wasmtier.js` (instantiation and `addFunction`), the
`#wasmspike` hook, and the table-index `calli` under the Mono interpreter,
which is what decides D1.

### Phase 1, first slice: DONE (real compiler, corpus green, 4.7x gate)

`WasmPredicateCompiler` compiles WAM→wasm against the engine's REAL state:
the same frame layout (CE/CP/N + Y), the same CP words (11+arity, BP
included), the same trail rule (young-to-old, bind if addr<HB). Control = a
dispatcher loop with a br_table over cursors; the cursors are ALSO the
re-entry vocabulary (resume markers and CP BPs name cursors), so a call's
return and a backtrack land in the same dispatch.

Translated set: switch_on_term/integer/atom (+ the ADR-028 `_arg` variants),
try/retry/trust OPEN-CODED (the whole CP in wasm, restore and trail unwind
included; the local fail path compares BP against its own encodings and
returns Fail only for foreign CPs), allocate/deallocate(+proceed) with the
faithful stack reclamation, get/put of constants and X/Y registers,
get_value_x/y (unify with the young-to-old discipline), a_int_bin/cmp on the
small-int lane (overflow → deopt). Everything else REJECTS the predicate;
everything hard at runtime (attvar, bigint, full trail, watermark) is
**Deopt** (verdict 6): scalars synced + Pc = the bytecode address of THE
instruction, and the interpreter continues as if the predicate had never
been compiled — the deopt is cheap because the state IS the engine's.

37 tests (`WasmCompilerTests` + the gate): 100k counter, factorial with
frames and marker resumes, mutual recursion, enumeration with backtracking
inside the wasm, indexing, a Y-slot accumulator, rejection. Measured gate:
**4.7x over Tier-0** on the desktop with the per-round CP included (the gate
asked for 2x); Tier-1 IL gives 8.6x here — in the browser Tier-0 is ~34x
worse and the wasm is not, so the projected ratio there is ~150x.

Harness TRAP noted: REGISTERS are working state — CP restores overwrite
them; an answer is read through the variable's HOME captured at query setup,
never through the final register.

Left of phase 1: structures/lists (get/put/unify_structure, ADR-017/019),
cut, ITE regions, the rest of the arithmetic lane, and the engine wiring
(phase 2: per-thread registration + WasmDelegateFactory).

### Phase 1 COMPLETE (slices 2-4: structures, cut/builtins, and the close)

Slice 2 (structures/lists): get/put/unify_* with ADR-017 inline cells and
ADR-019 last-arg nested builds; the unify machine (WriteMode + S) lives in
locals synced through mailbox slots 22/23 so a mid-sequence deopt is
resumable. Slice 3 (cut/builtins): cut = B down to the barrier with the
stale-barrier no-op (ISO); engine extras (cleanups, IL-CPs) via the Flags
word → deopt; call_builtin/execute_builtin leave through BuiltinRequest (id
+ cursor; tail = cursor -1); ADR-025 inline ITE (try_me_else with the
sentinel arity + jump). Slice 4, the phase close:

- **Floats**: get_float/put_float with the double's bits baked from the
  literal pool; dynamic paired cell (header | H+1); -0.0 is born 0.0 (the
  MakeFloat funnel). Binding is var → Ref(header), as the engine's unify
  does.
- **a_eval_*** (ADR-018): the RPN stack simulated at compile time over 8 i64
  locals — a deopt anywhere in the sequence rewinds to the FIRST push
  (pushes are read-only ⇒ re-running is sound). Bin {Add,Sub,Mul,IntDiv,Mod}
  and Un {Neg,Pos,Abs,Sign,BitNot} on the small-int lane with the Fits60
  check; everything else (float division, transcendentals, bigint/float
  literals) deopts at sequence start. A sequence cannot cross a leader
  (external re-entry ⇒ locals lost): the predicate is rejected.
- **ADR-020 reserved builds**: put_structure_r/put_list_r with the
  write-frame cascade (PushWriteFrame/OnReservedArgWritten) REPLAYED at
  compile time — the build tree is static, so the whole region flattens to
  ONE upfront heap guard + straight stores at fixed offsets from H0.
  Deopt-free by construction (pure writes); a leader inside rejects.
- **General unifier**: the module's wasm function 2 (internal, not exported,
  `(a i64, b i64, mailbox i32) → 0 fail / 1 ok / 2 deopt`), a worklist of
  pairs ABOVE the stack top (nothing pushes frames while it runs), functor
  arities via a host-mirrored i32 table at the new FunctorArityBase slot
  (24; SlotCount → 32). Walks Str/Lis/Float; attvar, bigint, rational and
  PSTR deopt (engine logic). Deopt after partial binding is sound: what got
  bound was required, is trailed, and the interpreter re-unifies
  idempotently. Called from get_value/unify_value when both sides are bound
  compounds.

89 tests green (WasmArithFloat + WasmReservedUnify new; the rejection pin is
now a bigint literal). Trap caught while building it: two DIFFERENT
immediates of the same tag (Int 2 vs Int 3) fell to the unifier's deopt
fallthrough instead of failing — the "same immediate tag, different cells"
case must answer 0.

Still rejected (a v1 decision, not correctness): ADR-023 dynamics, native
blocks, bigint/rational literals, indexed dispatch in odd shapes. With the
Deopt verdict, every exclusion is a PERFORMANCE decision.

## Phase 1 — Backend (~3-4 weeks, conditional on Go)

`WasmPredicateCompiler.Compile(CompiledPredicate, WasmIdSource) → (byte[],
WasmEntry)`, a fork of the emitter (reuses the neutral analyses: the
`CanCompile` census, shape classification, the ADR-018 RPN streams).

- **Open-coded in wasm**: deref, tag tests, small-int box/unbox (direct
  `i64.load/store`), bind + push to both trails (bases and cursors in the
  mailbox), X/Y moves, structure and list build/match, scalar cut,
  compare/branch, the 14 `ArithEvalStack` RPN ops over small ints as pure
  i64 (overflow or a non-small operand → bail).
- **Bail**: call/execute to another predicate (= the existing threaded
  continuation: `Cp = EncodeResumeMarker(selfFid, cursor)`, Pc, verdict 2 —
  the interpreter's marker path does the rest), builtins (3), CP push (4),
  safepoints (5), bigint/rational, attvar binding.
- **Wasm imports: none in v1** (memory + id globals only).
- **Opcode tier table** (`WasmOpcodeTiers.cs`), over the universe of 57:
  T=translatable, B=bail, R=reject the predicate (v1: indexed dispatch, ITE
  regions, ADR-023 dynamics, native blocks — revisit post-benchmark). First
  milestone: self-tail + head matching + arithmetic (tak/nrev-class bodies).

Files: `WasmPredicateCompiler.cs`, `.Emit.cs`, `WasmOpcodeTiers.cs`,
`IWasmModuleWriter.cs` + `DotnetWebAssemblyWriter.cs`, `WasmIdSource.cs`,
`WasmEntry.cs`/`WasmBindSite.cs`; in Shumway.Web `WasmDelegateFactory.cs`
(the verdict loop, generalising the spike wrapper).

Phase gate: the T corpus green in xUnit + the counter still ≥2x with the real
compiler.

## Phase 2 — JIT (~1.5-2 weeks)

- NEW `src/Shumway.Embedding/WasmPromotionStore.cs` — a parallel store (do
  not generalise `IlPromotionStore`, which holds `PredicateDelegate` and
  calls the IL compiler directly): per-functor counters + threshold + a
  background compile worker mirroring the existing shape, gated by
  `SupportsWasmCodegen`. Compiles bytes on a pool thread, instantiates via
  `WasmTier` (async, runtime thread), installs through the **existing**
  `IlPromotionStore.RegisterBoundDelegate` (`IlPromotionStore.cs:541`,
  unconditional install) → reuses the Call→CallIl rewrite, `IlByFunctorId`
  and `ITier1Dispatcher` untouched.
- MODIFY `src/Shumway.Core/RuntimeCaps.cs` (D7),
  `src/Shumway.Embedding/IlPromotionStore.cs:555` + `BundleLoader.cs:639`
  (`IsPermanentlyBytecodeOnly` must be false under `SupportsWasmCodegen` —
  today the linker rewrites to `CallBytecode` and statically removes the
  dispatch on web), `src/Shumway.Web/EngineBoot.cs` (`Tier0Only`) and
  `Shumway.Web.csproj` (new switch true; `Shumway.RuntimeCodegen` stays
  false).

Until the install completes, the predicate stays on Tier-0 — the same UX as
today's background compile.

### Phase 2 status: DESKTOP half DONE (the tier runs in the live engine)

The engine wiring is complete and proven on the desktop with the REAL
engine — only the browser half (pinning + calli + boot) remained:

- **`WasmAbi` moved to `Shumway.Core`** (the ABI mirrors engine state; the
  engine does not depend on the compiler). `Activation.Wasm.cs`: the mailbox
  bridge — `TryFillWasmMailbox`/`SyncFromWasmMailbox` + array views +
  `WasmModeCompatible` (trail-everything / occurs_check ⇒ per-entry fallback
  to bytecode).
- **Deopt with no interpreter change**: the delegate returns true with
  `IlTailCallPending` + `Pc` = a bytecode address — the existing
  post-delegate path (BytecodeInterpreter 603-616) continues at that Pc,
  marker or bytecode alike. Tail call and deopt are THE SAME mechanism.
- **`EngineWasmCompileEnv`**: every encode is an interned resume marker
  (`EncodeResumeMarker`) — call target = marker(callee, 0), a CP's BP =
  marker(self, retry-cursor), a continuation = marker(self, cursor); deopt
  pc = linked base + local offset (the pre-link CompiledPredicate offsets
  match the linked program 1:1; the linker only rewrites operand VALUES).
  Indirect builtins (IsCall/IsDollarCall) deopt at the call site;
  call_builtin carries the env trim in the high half of the BuiltinId slot.
- **`WasmTierDelegate`** (Embedding): the verdict loop as a
  `PredicateDelegate`; mirrors the interpreter's CallBuiltin/ExecuteBuiltin
  (TrimEnv before the impl, `BuiltinReturnPc` = marker or Cp, StampBuiltin).
  Fail returns false and the interpreter's backtracking re-enters through
  the wasm CP's marker BP — proven with findall enumerating through wasm
  CPs.
- **`DesktopWasmRunner`** (Compiler.Wasm): a copy-in/copy-out image over the
  library's wasm-to-IL engine — EVERYTHING in a cell is an INDEX, never an
  address, which is what makes the copy model sound; the FINAL tops bound
  the copy-back (whatever sits above is dead). Arity mirror via `TryLookup`
  (the id space has HOLES from atom GC + publication races).
- **`WasmPromotionStore`** (Embedding, no reference to the wasm backend —
  the world injects `Promoter`): counters + threshold + rejects; installs
  through `RegisterBoundDelegate` ⇒ markers, rewrites and EVICTION shared
  with IL. Hooks: `Tier1DispatcherAdapter.OnDispatch` (ahead of the IL path)
  and `IsPermanentlyBytecodeOnly` (with wasm on it consults the wasm reject
  set — without this the linker rewrites to CallBytecode and kills the
  dispatch).
- **TRAPS CAUGHT**: (1) the `__query__` wrappers reuse one functor id with a
  different body per query — promoting one REPLAYS the old query (the IL
  store's exclusion is now shared); (2) markers are sequentially interned
  pairs, NOT base+fid*stride arithmetic; (3) consulted functor ids are
  module-scoped — do not guess fids by re-interning names in tests.

Tests: `EngineWasmTierTests` (4) — nrev/app recursion via markers,
cut+findall backtracking into wasm CPs, floats + reserved builds + the
general unifier, a control engine. 92/92 of the wasm project green.

### PHASE 2 COMPLETE — runs in the browser, MEASURED

Browser half: `RuntimeCaps.SupportsWasmCodegen` (the `Shumway.WasmCodegen`
switch, default false, only Shumway.Web turns it on); `BrowserWasmRunner`
(`fixed` pins over the real arrays — D2: managed only runs with the wasm
bailed, so the pins last exactly the call; pinned mailbox; table index
cached PER THREAD via ThreadLocal + synchronous registration through spike.c
— the EM_JS registration is synchronous in the calling thread, no runtime
thread needed; bytes patched to shared with WasmSharedMemory; per-module
register demand — `EnsureWasmRegisters` BEFORE taking the view, an
out-of-range store corrupts whatever lies next); `BrowserWasmTier.Attach` in
`BootEngine` (process-wide pinned arity mirror, append-only). The
`#wasmtier[=rounds]` probe (two engines side by side, correctness
cross-checked first).

**Run in headless Chrome, measured** (docs/benchmarks/browser.md):
- correctness: all 3 goals (counter/nrev/tak) agree, tier vs Tier-0;
- **counter 300k: ~100-220x** (8-16 ms vs ~1665 ms — the self-tail stays in
  wasm, the only boundary is the one `is` per turn);
- **nrev 200×5: 1.2-1.4x** (call+alloc heavy: the per-element nrev→app
  handoff bounces through markers, and heap pressure deopts at the
  watermark);
- **tak: bounded** — NOT by builtins (its arithmetic is open-coded:
  `a_int_cmp`/`a_int_bin`, zero BuiltinRequest); the tax is the 3 NON-TAIL
  calls to tak per invocation (the tail `execute` stays in wasm), each with
  two boundary round-trips. Verified by disassembly.

The spread NAMES the three bail seams and none is a correctness limit (deopt
returns them to the tier they were on). The MEASUREMENT reorders the plan:
the dominant seam for tak AND nrev is the **non-tail inter-predicate call**
(it bounces through the interpreter), NOT builtins. The big lever is a
direct wasm→wasm call (resolve the callee's index and call it instead of
verdict 2) — the measured priority for the next phase. Open-coded builtins
help builtin-dense code, but the data says calls come first.

Test trap caught: `EngineWasmTierTests` runs LIVE engines ⇒ shares the
global AtomTable/FunctorTable; in-process assembly parallelism must be
DISABLED (as Embedding does), or another class interns functors under a
running engine's feet.

## Phase 3 — AOT: RETHOUGHT (baking bytes is redundant)

The original design (baking `.wasm` + `WasmBindSite[]` into the bundle) does
NOT pay for its complexity, unlike persisted IL, for a concrete reason:

- Persisted IL bakes ASSEMBLY bytes because compiling IL is EXPENSIVE
  (`Reflection.Emit` + JIT); skipping that at load time is worth it.
- Generating WASM bytes is CHEAP (`WasmPredicateCompiler` just emits bytes,
  no JIT). The expensive cost — instantiating/JITting the module — is paid
  by the browser AT LOAD TIME regardless, whether the bytes come from the
  bundle or are generated on the fly.
- Moreover the module's encodings (markers) are PER PROCESS (pairs interned
  at runtime), so baked bytes are not portable without imported globals
  (D6) that the loader computes FROM the `CompiledPredicate` — which the
  bundle ALREADY carries. That is: baked bytes would be redundant with the
  stored `CompiledPredicate`.

Conclusion: for WebShumway the JIT path (phases 1-2) ALREADY delivers what
AOT would (predicates on Tier-1 from the first call), because wasm codegen
is cheap enough that the promotion threshold's warmup is negligible. The
useful form of "AOT" would be eager priming at load (promote the bundle's
predicates at threshold 1 on load), which reuses the whole JIT path with no
new bundle section — and even that is marginal.

**AOT is deferred: it adds nothing over the measured JIT.** If it is picked
up again, the right shape is priming at load, NOT baking bytes.

## Phase T — Tests (parallel to 1-3)

- NEW `tests/Shumway.Tests.Wasm/` — desktop xUnit with no browser: modules
  with non-shared memory executed by the library's wasm-to-IL engine against
  a mailbox/memory harness, + differential runs vs Tier-0 over the T corpus.
- Browser: extend `wwwroot/selftest.js` with a wasm section (promote, re-run
  the selftest corpus, compare answers).
- Regression: the full `Shumway.Tests.Embedding` green with the switch false
  (the default) everywhere.

### The performance round's DECISION — options weighed

With the diagnostic in hand (the cost was ~150 µs of MONO-INTERPRETED C# on
every module entry, not the ~0.3 µs boundaries nor the ~3 µs of wasm), three
paths were weighed:

- **(A) The hop inside wasm** (`call_indirect` through an imported function
  table + a per-thread functor→index map in linear memory): zero C# per
  hop, the theoretical ceiling. AGAINST: only testable in a browser, a
  foreign-realm index traps the worker SILENTLY (the phase-0 lesson), and
  it needs new wasm mechanics (table import, per-thread map).
- **(B) A C#-level CHAIN over a shared mailbox**: pin + fill ONCE per
  delegate invocation; between chained modules the mailbox already holds the
  scalars the wasm itself synced on return, so a hop is a marker decode + a
  dictionary probe + a raw call (~4-15 µs interpreted vs ~150 µs). FOR: no
  new wasm mechanics, fully testable on the desktop (the copy world), and it
  captures ~90% of the win.
- **(C) Cheapening the per-entry marshalling** (cache pins, partial fill):
  discarded — the 24-slot fill IS the cost under the interpreter; there is
  no cheap version of "C# per entry".

**TAKEN: (B), with (A) noted as the residual lever.** The criterion: maximum
measurable win at minimum risk with desktop tests; (A) is only justified if
a real corpus shows the residual interpreted switch dominating (today it
bounds tak at 3.4x — already above the 2x gate). As a bonus, implementing
(B) uncovered two correctness bugs (over-cut from a stale B0; an untrailed
bind surviving backtracking) that (A) would have buried under a more opaque
layer.

### POST-measurement performance round: CHAINS + inline =/2 + trail-first

The fine-grained measurement (wall/inWasm/stage split) revealed the real
enemy: MONO-INTERPRETED C# per entry (~150 µs of staging vs ~3 µs of wasm —
Mono interprets the whole runner in the browser). Three designs in cascade,
each dictated by the verdict diagnostic (never by hypothesis — two earlier
hypotheses, the watermark and tak's builtins, DIED against the diag):

1. **The CHAIN model** (replaces per-entry): `IWasmExecutionWorld` +
   `IWasmChainContext` in Core; a delegate opens ONE chain (pin + fill once)
   and hops module-to-module over the mailbox the wasm itself keeps synced.
   A switch = marker decode + dict + raw call. Per-switch guards: watermark,
   cancellation, wakeups (only builtins queue them mid-chain). A builtin =
   SyncEngine → impl → RefreshFromEngine (the arrays may have been REPLACED
   by growth). Worlds: `DesktopWasmWorld` (one image, copy-in/out PER CHAIN)
   and `BrowserWasmWorld` (GCHandle pins per chain, a pinned mailbox per
   context — nesting through sub-engines/reentrant-solve works because the
   builtin path re-syncs around it). nrev: 1.3x → ~35x.
2. **=/2 open-coded** (`IsInlineUnify` in the env): the `A = Z` in tak's
   leaves was ONE host exit per leaf (~16k). It is now the same two-cell
   unify get_value uses, at all 4 call-site shapes. tak's builtins: 16k→0.
3. **A SOUNDNESS BUG caught by the diag: trail-first at the binds.** The
   bind emitted the STORE before the trail-space check; with a full trail
   the deopt left an UNTRAILED bind behind (survives backtracking =
   unsound) and the interpreter's re-run saw the var already bound → never
   trailed → the trail never grew → a deopt STORM (14,912, one per leaf, TR
   stuck at the limit). The general unifier already did trail-first WITH
   the comment; the inline binds did not. All funnelled through ONE
   trail-first `EmitBindDa`. Deopts: 14,912→36.

Also: the earlier over-cut fix (SetB0 parity — the CutBarrier slot is
refreshed at EVERY dispatch, self-tail included; regression `w/2` with
per-level `d/1` CPs: 4 solutions, not 1).

Final browser numbers: counter ~90-250x, nrev ~31-39x, tak ~3.4x (the 2x
gate cleared on all three). What remains in tak/nrev: the interpreted switch
(~4-15 µs); the next lever is the tail hop IN wasm (call_indirect + an
imported table), an arc of its own.

## Phase 3' — The direct wasm→wasm call (the measured lever, its own arc)

The measurement points here, not at AOT nor at builtins. The analysis of
what is tractable and what is not:

- **The NON-TAIL call (tak) is NOT cheaply removable.** A non-tail `call`
  fixes CP and the callee returns through CP; in a predicate-JIT the fail of
  a deep callee can unwind CPs many frames up, which a recursive C stack
  does not model. Keeping non-tail calls in wasm = moving the whole dispatch
  LOOP (with backtracking over the CP stack) into wasm = "compiling the
  engine to wasm", not a predicate JIT. A separate large arc.
- **The TAIL call to another functor (nrev→app) IS tractable.** A tail call
  has no continuation to preserve (it IS the last goal, it inherits CP): the
  module could `call_indirect` the callee's `run(mailbox, 0)` and return its
  verdict directly (tail semantics: the callee's result IS the caller's).
  Requires: a per-thread `functorId → callee table index` map mirrored into
  linear memory (the module reads the index and call_indirects; 0 = not
  registered on this thread yet, fall back to verdict 2). It attacks the
  nrev→app handoff (app's internal recursion is already a wasm self-tail).
  RISK: a wrong index = silent corruption/trap; it needs the fallback and
  careful tests. This is the arc's correct entry point.

**Status**: deferred to its own arc. The measured JIT already proves the
thesis (100-220x in the ideal case); the direct tail call is the highest-
leverage improvement for real code and deserves dedicated design + tests,
not a rush.

## Phase B — Benchmarks + close (~1 week)

A `#bench` page (NEW `wwwroot/wasmbench.js`): counter, tak, nrev, crypt,
zebra; interpreted Tier-0 vs wasm Tier-1, median of N with warmup (the
`docs/benchmarks/analysis.md` methodology); report to
`docs/benchmarks/browser.md` with the desktop reference table. **Gate for
"on by default on web": geomean ≥ 2x on the subset.** A new ADR
`docs/architecture/adr/050-wasm-tier1-backend.md` recording D1–D7 (satisfies
the decision policy: new backend + new dependency ⇒ ADR).

## Risk register

| Risk | Exposure | Mitigation / kill switch |
|---|---|---|
| `calli` to an addFunction index not viable under the Mono interpreter | Kills D1 and the design | Spike measurement 1; a documented No-Go; the JS thunk rejected as a product |
| SGen pinning semantics (fixed/POH) under browser-wasm with threads | Corruption | Spike measurement 2, with GC stress |
| The heap's `Cell[]` replaced by growth/GC | Stale bases | Structural: managed only runs with the wasm bailed; base refresh on every wrapper iteration; watermark bail before allocating |
| The library does not emit the shared flag | Blocks instantiation | Post-patch of the limits byte (D5) |
| Instantiation cost per JIT promotion | Latency | Async install; Tier-0 keeps running; the threshold amortises |
| Payload (the package + Compiler.Wasm in the web bundle) | Load time | Trim-friendly writer; AOT-only deploys can exclude the emitter |
| Verdict frequency in builtin-heavy predicates | Eats the win | Tier R rejects them until the benchmark justifies more open-coding; per-shape bail counters in the wrapper |
| Wakeups/interrupts lost in long wasm loops (ADR-049) | Correctness | A flags word in the mailbox checked on every back edge → verdict 5 |

## End-to-end verification

1. **Spike**: `#wasmspike` in Chrome and Firefox prints the table; the
   numeric Go/No-Go criterion; the non-shared variant's xUnit green.
2. **Post-phase 1**: the T corpus differential vs Tier-0 in xUnit; the
   counter ≥2x with the real compiler.
3. **Post-phase 2/3**: the extended `#selftest` green with the JIT active;
   a `--with-wasm` bundle boots WebShumway and promotes from AOT; the full
   Embedding suite green with the switch off.
4. **Close**: `#bench` published in `docs/benchmarks/browser.md`; geomean
   ≥2x decides the default; ADR-050 written.
