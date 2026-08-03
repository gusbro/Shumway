# ADR-035: Source-level debugger (Visual Studio / Concord, interpreter-aware)

**Status:** ACCEPTED and **IMPLEMENTED** — the arc is closed (D0–D4 delivered in
full; every D5 deferred item except the project system shipped too; see *Final
state* at the end). The VS Code / DAP frontend is **ADR-036**, which reuses this
ADR's engine-side core unchanged.

## Context

Shumway needs a **source-level debugger**: breakpoints in `.pl` files, a call
stack, per-frame variable inspection, watches, and stepping — hosted in
**Visual Studio 2026**, and able to show a **single mixed call stack** that
interleaves Prolog, C#, and native C frames. The driving scenario is the
project's key workload: **arity-compat modules with interop** (`:- c` embedded
blocks, `:- native` P/Invoke, `[PrologPredicate]` foreign predicates) debugged
end-to-end.

Two facts shape the whole design:

1. **The logical Prolog stack does not live on the C# stack.** At Tier-0 the
   entire query runs inside ONE C# frame (`BytecodeInterpreter.Dispatch`,
   threaded dispatch since Phase 16); frames, choice points and variables live
   in the `Activation`'s arrays (`[CE|CP|N|Y1..Yn]` environment chain). Any
   faithful debugger must therefore *recompose* the call stack from engine
   state — which the engine already knows how to walk
   (`EnumerateCallReturnAddresses`).
2. **Prolog's execution model breaks frame-based debugging.** The *redo* port
   re-enters a frame the debugger saw "return"; backtracking is not expressible
   in the return-address stepping model that native/managed debuggers use. So
   stepping must be **port-based** (call/exit/redo/fail), implemented by the
   engine, with the IDE mapping step-in/over/out onto port+depth predicates.

## Decision

Integrate with Visual Studio through **Concord** (the VS debug-engine
extensibility API, `Microsoft.VisualStudio.Debugger.Engine`), as an
**interpreter-aware** debugger modeled on PTVS mixed-mode (Python/native):

- A **call stack filter** (`IDkmCallStackFilter`, IDE-level component) replaces
  the physical `Dispatch` frame(s) with N synthesized Prolog frames built from
  a stop snapshot of the `Activation`'s environment chain. Every other frame —
  C# interop bridges (`_X_PrologBridge`, `NativeBlockRunner.*`,
  `shumway_native_calli`) and real native frames — passes through untouched.
  The mixed Prolog + C# + native stack falls out of this by construction.
- A **server-level runtime component** owns breakpoint enable/disable
  (`IDkmRuntimeMonitorBreakpointHandler`), stepping arbitration
  (`IDkmRuntimeStepper`), and the notify-breakpoint receiver.
- An **engine-side debug core** (cross-platform, zero VS dependencies) does all
  the real work: breakpoint table with in-process bytecode patching, 4-port
  step controller, frame/variable enumeration, term rendering
  (`TermReader.Materialize` → `AstTermRenderer.Render`).
- Debugger ⇄ engine communication uses a **pinned-memory channel as the
  primary path**: `DebugService` owns a GCHandle-pinned snapshot buffer and
  command region; on every stop the engine pre-serializes the snapshot BEFORE
  calling the no-inline notify method `ShumwayDebugHelper.Notify`, on which the
  Concord component holds a hidden CLR breakpoint; the component reads the
  snapshot with `DkmProcess.ReadMemory` and writes commands (breakpoints, step
  specs) with `WriteMemory`, drained by the engine before resume. **Func-eval
  is reserved for user-initiated actions** (watch evaluation, deep subterm
  expansion) from the IDE-side EE — the explicitly supported context.
  Rationale: ConcordExtensibilitySamples issue #61 documents func-eval hanging
  in breakpoint-notification context; PTVS uses the same memory-channel shape.

### Alternatives rejected

- **Classic AD7 custom engine.** 5–10× the implementation surface (the
  IDebug*2 COM family) for *no added capability*: Concord is the modern engine
  underneath VS's own managed+native debugging, and only a Concord component
  can interleave synthesized frames into the SAME stack walk (PTVS proves it).
- **DAP-first (VS Code).** No mixed call stack — VS Code composes separate
  sessions side-by-side. Remains possible later as a thin adapter over the
  same engine-side debug core (and Concord components can load in vsdbg —
  unverified bonus). *This rejection is scoped to the VS frontend:* **ADR-036**
  later adopts DAP as the *second* frontend, for VS Code and cross-platform
  (Linux) debugging, over this same engine core.
- **IL + PDB sequence points (the Iris model).** Only buys the *stock*
  frame-based stepping/breakpoints, which (a) cannot express redo/fail and
  (b) apply to Tier-1 IL — but debugging runs Tier-0 where the `.pl` source
  never becomes IL. The stack is recomposed from engine state either way.

## Engine-side design

All debug behavior is gated on **debug compile mode** (`compile_mode=debug` /
`shumway-compile --debug`) plus an **active debug session** (per-engine flag,
predicted-not-taken branch — the ESC-cancel pattern). **Zero release-mode
impact** is a hard requirement, verified by A/B.

1. **Debug metadata** (debug mode only): a file table (consult records the
   file id; `ConsultString` gets a pseudo-name); **per-goal source positions**
   propagated through every `ClausePipeline` transform (DCG, MetaTransform
   helpers, snips→`once`, `{...}`→`$native_run`) into a side table
   PC→position on `CompiledPredicate` (side table, NOT bytecode markers: no
   size/dispatch impact); per-clause **name → Y-slot maps** (captured from
   `ClassifyPermanents`/`state.Ys`, which today are discarded after compile).
   Debug codegen forces named source variables into Y slots, disables
   environment trimming and redundant-cut elision. `.shmo`/`.shum` Debug build
   mode carries the tables.
2. **`Break` opcode** — takes the reserved `ReservedExtension` slot.

   > **As implemented (D1-c).** The sketch above is right, and the shape it
   > describes is what shipped — but the *resume* mechanism it proposed is not
   > needed, and dropping it is what makes the whole thing sound.
   >
   > **No restore-step-repatch.** On reaching a `Break`, the interpreter does
   > **not** put the original byte back and single-step over it. It reads the
   > original opcode out of the engine's breakpoint table and **dispatches that
   > opcode at the same pc**, with the operands untouched — `Break` overwrites
   > only the opcode byte, so the original instruction's operands are still
   > exactly where it left them. The patched byte is never disturbed.
   >
   > This matters because **the code space is shared**: several activations
   > coexist over one engine's code. A restore-step-repatch sequence is a window
   > in which a second activation runs the *un*-patched instruction and misses
   > the breakpoint. Re-dispatching from the table has no such window. (This is
   > the JVM's model — its `breakpoint` bytecode stands in for the original,
   > which the method keeps — and it is why the "always-emit a stop instruction
   > instead" alternative was rejected: a dispatch per goal in every debug
   > program is a cost no real VM pays.)
   >
   > **Debug codegen emits nothing extra.** It *records*, per clause, the
   > bytecode **offsets** a debugger may stop at (the clause entry, and the first
   > instruction of each body goal) paired with the interned source site
   > (`DebugSiteTable`: file/line/column) each one corresponds to. Those offsets
   > ride the same clause → predicate → program relocation as the clause's call
   > sites. So debug-compiled code with no breakpoints armed runs exactly the
   > instructions release code would.
   >
   > **The armed source site is the truth; the byte patches are derived.** They
   > are re-applied whenever the code space changes (relink, compaction,
   > consult), so a breakpoint set once keeps working across queries rather than
   > pointing at whatever moved into its old address. Binding is decided against
   > *this engine's* compiled code, not against the process-wide site table:
   > `PrologEngine.AddBreakpoint(file, line)` returns how many sites bound, and
   > zero is how a debugger learns to draw a hollow breakpoint (a blank line, a
   > comment, or a `:- disable_debug.` region).
   >
   > Prerequisite discovered here: **source positions did not survive the
   > pipeline.** `ModuleRewrite` rebuilds every term it mangles and
   > `Term.Position` is init-only, so a module's goals reached the compiler at
   > 0:0 — which is every goal in every program, since predicates are
   > module-local by default. Mangling changes a name, not a place; the rebuilt
   > terms now carry the position of what they replace.
3. **`debug_lastcall` opcode** (appended at the dense-block end) — **LCO is
   runtime-toggleable under debug**: reads the per-engine LCO flag at
   dispatch; on → classic deallocate+execute; off → a normal call that
   retains the frame (+ emitted return stub), so tail frames appear in the
   stack. Surfaces: `prolog_flag(debug_lco, on|off)`, a `PrologEngine`
   property, the `SHUMWAY_DEBUG_LCO=on|off` environment pin, and the
   Immediate-window command `:lco on|off|status`. Default under debug: off
   (full stack). Toggling affects future calls only.
4. **Port instrumentation**: call/exit/redo/fail hooks at the
   Call/Execute/Proceed/backtrack dispatch sites, active only with a session.
   Step depth is recomputed from the environment chain at each port, never
   incrementally counted.
5. **`:- disable_debug.` / `:- enable_debug.`** — the Prolog equivalent of
   Just-My-Code "external code". A marked module keeps FULL release
   compilation under debug (Tier-1 IL, regions, LCO, CP-free guards) and
   appears in the call stack as ONE collapsed "semi-native" frame (its entry
   predicate, opaque inside) without losing coherence: a call from inside it
   into a debuggable module resumes normal frame-by-frame display (the env
   chain stays intact; Tier-1 resume-marker return addresses decode via
   `IsResumeMarker`/`ResolveByFunctorId`). Step-into a semi-native call
   behaves as step-through (opaque modules emit no ports). F9 there does not
   bind. Under a debug session, Tier-1 promotion exclusion is therefore
   **per-module**, not global. **The prelude is implicitly `disable_debug`.**

   > **Amended in implementation (D1-c).** The two
   > directives are **positional**, not file-scoped declarations: each sets the
   > debuggability of the clauses that *follow* it, until the next one or the
   > end of the file. Debuggability is therefore a property of a **predicate**,
   > not of a module — a file can hand the debugger the predicates worth
   > stepping through and keep the rest compiled for speed. A non-debuggable
   > predicate gets no stop sites, no forced frames and no `debug_lastcall`;
   > it is release code.
   >
   > The coherence requirement falls out rather than being engineered: an opaque
   > predicate that calls a debuggable one resumes normal frame-by-frame
   > debugging, because the two compile independently and the environment chain
   > runs through both either way. That is precisely why the design is
   > per-predicate rather than a mode the whole engine is in.
6. **REPL `trace/0` port tracer** over the same DebugService — the
   intermediate deliverable that validates ports/stepping with no VS in the
   loop, and a useful feature in itself.

## Licensing

| Component | License | Use |
|---|---|---|
| ConcordExtensibilitySamples (Iris, HelloWorld) | MIT | adapt freely (vsdconfig/VSIX build plumbing) |
| PTVS `Debugger.Concord` | Apache-2.0 | adapt freely (the architecture reference) |
| `Microsoft.VisualStudio.Debugger.Engine`, VSSDK packages | VS SDK EULA | reference assemblies whose licensed purpose is building VS extensions; nothing is redistributed (the Concord runtime ships inside VS); referenced ONLY by the opt-in `vs\` projects — Shumway core keeps its permissive-only dependency policy |

## Build layout (opt-in, Linux unaffected)

The Concord extension references **no Shumway project** (it communicates by
method name + memory), so the VS pieces live outside the main solution:

- `vs\Shumway.Debugger.sln` — Windows-only, desktop MSBuild (VSSDK targets):
  `Shumway.Debugger.Concord` (netstandard2.0; Dkm components + two
  `.vsdconfigxml` compiled to one `.vsdconfig` by
  `Microsoft.VSSDK.Debugger.VSDConfigTool`) and `Shumway.Debugger.Vsix`
  (classic VSIX csproj; `DebuggerEngineExtension` asset; installation target
  `[17.0,)` — VS 2026 loads VS2022-era VSIXes unchanged).
- Engine-side code (`src\Shumway.Embedding\Debugging\`, opcodes, port hooks)
  is ordinary net10.0 cross-platform code in the main solution.
- `Shumway.slnx` is untouched; `dotnet build` on Linux never sees `vs\`.

## Invariants touched

- **New opcodes** (all emitted ONLY under `compile_mode=debug`; release bytecode
  contains none of them, and the deterministic `--alloc` metric is unchanged on all
  ten Van Roy benchmarks): `Break` in the `ReservedExtension` slot; `debug_lastcall`
  appended at the end of the dense dispatch block (contiguity preserved); and
  `debug_port` — one byte in front of each INLINE body goal (`!`, `is/2`, `=/2`,
  the comparisons), which emits no call and therefore raises no port of its own:
  without it a step walked straight over the `!` the user wanted to stand at,
  variables in hand, before it commits. Dispatch is a null check when no session
  is attached. All three are this ADR's sanctioned additions per the
  [decision policy](../decision-policy.md) major-decision rule.
- **Debug-mode codegen differs** from release (trimming off, cut-elision off,
  LCO toggleable, named vars forced permanent) — debug bytecode is a
  correctness-equivalent, slower compilation of the same program.
- **Release mode is byte-identical and time-identical**: no debug metadata, no
  hooks on any hot path beyond the session-flag branches, A/B-verified.

## Phases (detail in the phase plan)

- **D0** — Concord spike: five de-risk legs (func-eval context matrix + pinned
  ReadMemory/WriteMemory; managed notify breakpoint on CoreCLR; `.pl` F9
  binding; stack filter + EE routing on VS 2026; step arbitration) before any
  engine work. ✅ **Done.**
- **D1** — Engine debug core (metadata, `Break`, `debug_lastcall`, ports,
  DebugService + channel, `disable_debug`, tests, REPL tracer). ✅ **Done** —
  see *What D1 settled* below.
- **D2** — Concord read side (stack, locals, watches). ✅ **Done.**
- **D3** — Concord control side (F9 binding, stepping). ✅ **Done.**
- **D4** — VSIX + F5 command (`IVsDebugger4.LaunchDebugTargets4`) + the
  arity-compat interop E2E gate + `docs/debugger.md`. ✅ **Done.**
- **D5** — everything on the deferred list except the project system shipped,
  plus a body of work the list never imagined. ✅ **Done** — see *Final state*.

## What D1 settled

Six things were decided by building them, and each is a place where the obvious
design was wrong.

**Stepping is by PORT, and depth is read, never counted.** A step runs until the
next port satisfying the step's condition — into: the next port, however deep;
over: the next port no deeper than the goal we were on; out: shallower than it.
The condition is stated in the machine's logical call depth, and that depth is
recomputed from the environment chain at every port rather than incremented and
decremented, because counting drifts the moment anything changes the depth without
going through a port: last-call optimisation reusing a frame, a cut discarding
choice points, an opaque predicate running goals that report nothing. Reading the
chain cannot drift, because the chain IS the depth.

The rule is `EnvDepth + 1` at every port, and it is one fact seen from either end:
at a call port the callee has not allocated its frame yet, and at an exit port the
frame is already gone. LCO falls out for free — it reclaims the caller's frame
*before* the call, so the callee reads the caller's own depth, which is exactly
right, since it has taken the caller's place.

**A step over lands on the exit port of the goal stepped over**, as it does in
SWI, and this is not a compromise: in a port model there is no depth that
separates "this goal exited" from "the next goal is called". They are siblings.

**Two ports need the machine asked properly.** At the *redo* port the machine is
still standing in the computation that FAILED — `P`, the environment chain and
`Cp` all describe it — while what the user must see is the one about to be
retried, which the choice point carries (`Activation.PendingRedoEnvDepth` /
`TopChoicePointContext`). And a retry address does not point at a clause: it points
at a chain link (`trust 72`), which in an indexed predicate sits ahead of every
clause body, so it precedes all the predicate's source sites. Two hops get to the
clause and then to its site.

**A clause's stop site sits AFTER head unification.** That is what makes the
variables readable (the frame exists, the arguments are matched), and it means a
clause whose head does not match is never stopped in — the user asked to stop when
this clause RUNS, not when it is tried and rejected. A *rule* gets no entry site at
all: with the head matched, the next instruction is its first goal's, and one point
in the machine deserves one stop. A breakpoint on a rule's head line snaps forward
to it — but only within the clause it lands in, so a breakpoint in a
`:- disable_debug.` region stays hollow rather than silently arming a line the user
was not looking at.

**Debug codegen initialises the frame.** Making every named variable permanent and
never trimming is what lets a debugger show them; *initialising* them is what stops
it from lying. A Y slot the machine has not written yet holds stack garbage, and
garbage can look exactly like a valid heap reference — so an uninitialised slot
would not fail loudly, it would print a plausible value. Every slot the head did
not bind gets a fresh unbound variable.

**The prelude and the CLP libraries are implicitly `:- disable_debug.`** — and
this must be *recorded*, not inferred from the flag at compile time, because a
library's clauses are re-compiled at query setup, by which point `compile_mode` is
whatever the user's program set. Marking the library's own predicates is not
enough either: MetaTransform lowers its control constructs into generated helpers
that are not in the clause list but do carry the library's source positions.
Compiling a module is what makes its predicates — if the module is not debuggable,
neither is anything it made.

**The channel writes before it notifies.** Every stop is: serialise the whole stop
into pinned memory; call `ShumwayDebugHelper.Notify` (where the debugger stops the
process and reads that memory); drain the commands it wrote back. Nothing runs in
the debuggee while it is stopped, because the answer was already there before the
question could be asked — which is what keeps a func-eval out of
breakpoint-notification context, where it deadlocks.

## Final state (arc closed 2026-07-20)

Delivered end to end, user-verified on real programs (Blint, ~2570 lines) across
all three deployment shapes — REPL `--debug`, embedded `EnableDebugging`, and
linked `--exe --debug` (`--debug-wait` blocks for attach and stops at the entry
point). Wire format v7, VSIX 0.27. Gate at close: Core 444 / Interpreter 105 /
Compiler 351 / Embedding 3275 / ISO 277. Beyond the D0–D4 sketch, the arc grew:

- **Conditional breakpoints** (D5 item): the engine evaluates the condition goal
  at the `Break`, BEFORE notifying — a failing condition is a silent resume that
  never wakes the debugger; an erroring one stops with the error surfaced.
- **Evaluation in the live engine**: Immediate-window goals run in the suspended
  engine; solution bindings can be **unified into the suspended frame**
  (bind-into-frame — trailed as if executed there, aliasing real). Watch/Locals
  edits are **destructive by design** (`SetFrameVariable`: rebind is trailed so
  backtracking restores it; `_` un-instantiates; Immediate deliberately stays
  pure). Displays are writeq-quoted so values round-trip. DataTips honour
  NoSideEffects — hovering a predicate name refuses the implicit eval instead of
  running it.
- **Set Next Statement** (never planned): a no-replay design — under a session
  the engine **trails everything** (Hb pinned) and records **port marks**
  (trail/heap/B tops) at debuggable call ports; forward moves skip, backward
  moves unwind the trail to the mark (undoing all bindings since), refusal for
  moves the model cannot honour (dead cut barriers, redo). Cross-frame SNS (pop
  to the selected frame), **sibling-clause head SNS** with standard Prolog
  fall-through (an IL-CP cursor over the following clauses; cut discards it
  naturally), and head→restart-body. The heap GC **relocates marks** rather than
  being suspended; cut-time trail compaction stands down under trail-everything.
- **Control constructs are transparent** (`,`/`;`/`->`/`$call_*` raise no
  stops/frames; `catch`/`once`/`\+`/`findall`-family stay visible), positions
  survive every transform (DCG, phrase, native, ITE — pinned by a per-shape
  tripwire suite), stepping stops at library calls by call site.
- **Detach is clean**: a hit with no debugger attached disarms the Break bytes
  and runs free; re-attach re-sends the full state.
- **Lazy full debug**: `DebugOptions.ActivateOnAttach` /
  `SHUMWAY_DEBUG_ACTIVATION=attach` opens the session with the runtime machinery
  off (no ports, no trail-everything, LCO on) at near-release speed; attaching
  arms it — mid-query, at a safe point — and detaching disarms. Measured on
  Blint: full debug ≈ 2.2× release, lazy unattached ≈ 1.37× (the residual is
  debug *codegen*: +11% dispatches). The armed-mode cost was then cut ~16% by
  caching (per-port address→predicate range memo, transparency cache, and a
  reference-identity guard that stops the per-query debug-table rebuild from
  being O(program) — the hundreds-of-modules hazard).
- **Func-eval works against Release-built engines**: the CLR refused to hijack a
  thread stopped in optimized code (`NoOptimization` yields only MinOpts, whose
  GC-safe points are call sites — `Notify` had none), so VS fell back to IL
  interpretation, which dies on the first FCall. A zero-iteration call-free loop
  in `Notify` forces fully-interruptible GC info, making the stop hijackable in
  any build — the natural pairing of the lazy workflow (debug-compiled Prolog on
  release engine bits).
- **Not done**: the VS project system (Open-Folder launch profiles) — the one
  D5 item left, unpursued for lack of demand. The VS Code exploration became
  **ADR-036**.
