# ADR-035: Source-level debugger (Visual Studio / Concord, interpreter-aware)

**Status:** ACCEPTED — phased implementation in progress (phases D0–D4; see §9).

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
  unverified bonus).
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
2. **`Break` opcode** — takes the reserved `ReservedExtension` 1-byte slot (no
   renumbering). Side table id→(pc, original byte); on hit the DebugService
   decides (breakpoint / step spec), notifies, and on resume restores the
   byte, single-dispatches, re-patches. Patching is done by the engine
   in-process (commands arrive via the channel), so there are no remote-write
   or GC-array-move hazards.
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
5. **`:- disable_debug.` module directive** — the Prolog equivalent of
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

- **New opcodes**: `Break` in the `ReservedExtension` slot; `debug_lastcall`
  appended at the end of the dense dispatch block (contiguity preserved).
  Both are this ADR's sanctioned additions per the CLAUDE.md major-decision
  rule.
- **Debug-mode codegen differs** from release (trimming off, cut-elision off,
  LCO toggleable, named vars forced permanent) — debug bytecode is a
  correctness-equivalent, slower compilation of the same program.
- **Release mode is byte-identical and time-identical**: no debug metadata, no
  hooks on any hot path beyond the session-flag branches, A/B-verified.

## Phases (detail in the phase plan)

- **D0** — Concord spike: five de-risk legs (func-eval context matrix + pinned
  ReadMemory/WriteMemory; managed notify breakpoint on CoreCLR; `.pl` F9
  binding; stack filter + EE routing on VS 2026; step arbitration) before any
  engine work.
- **D1** — Engine debug core (metadata, `Break`, `debug_lastcall`, ports,
  DebugService + channel, `disable_debug`, tests, REPL tracer).
- **D2** — Concord read side (stack, locals, watches).
- **D3** — Concord control side (F9 binding, stepping).
- **D4** — VSIX + F5 command (`IVsDebugger4.LaunchDebugTargets4`) + the
  arity-compat interop E2E gate + `docs/debugger.md`.
- **D5 (deferred)** — conditional breakpoints, side-effect watches (opt-in),
  debug tables in stripped bundles, VS Code/vsdbg exploration, project system.
