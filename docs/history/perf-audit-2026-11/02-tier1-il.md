# Tier-1 (WAM → IL) — unexplored performance opportunities

Static review of `src/Shumway.Compiler.Il/` and the `Activation` helpers it calls
(`src/Shumway.Core/`), against what the project already tried: the 2026-06
efficiency audit, `docs/design/il-*.md`, `region-jit-limits.md`, ADR-011/014/
019/020/023/025/029–034/037/057/058/060/061/062/063, the rejected ADR-021/026/032,
and the phase 16–43 closures. No build or benchmark was possible in this
environment; every claim below was checked by reading the code at the cited
lines. Git history in this checkout is shallow (179 commits), so the
"novelty check" leans on the docs and on the code itself; where the git grep
was inconclusive it says so.

Ranking is by expected value = (how hot the path is) × (how much the change
removes) × (confidence), highest first. Gains are honest guesses; the project's
own measurement discipline applies (one process, ABBA over copies, min and
median of ≥ 8 rounds, heap cells identical, `SHUMWAY_IL_DUMP` for the IL,
the JIT's `MethodJitInliningFailed` events for inlines, `IlRegionCost.Log`
for the planner's counts).

---

## 1. Standalone multi-clause predicates never get ADR-057/058/060: allow a one-member region

**Where**
- `src/Shumway.Compiler.Il/IlPredicateCompiler.Regions.cs:249` —
  `if (region.MemberCount < 2) { reason = "members<2 (no local closure)"; return false; }`
- `src/Shumway.Compiler.Il/IlPredicateCompiler.cs:787-836` — with no region the
  predicate falls to `CompileTryMeElseChain` / `CompileIndexedDispatch` /
  `CompileIndexedAtomPredicate`.
- `src/Shumway.Compiler.Il/IlPredicateCompiler.Registers.cs:235-249` —
  `AttachRegisterFile`: a non-region method gets a register file that holds
  `MachineRegs.None`.
- `src/Shumway.Compiler.Il/IlPredicateCompiler.Dispatch.cs:204-208, 229-230`
  and `IlPredicateCompiler.Cps.cs:1065-1070, 1091-1111` — the clause choice
  point is `call Activation.PushIlChoicePoint(self, cursor, arity)` and a
  failure is `ldc 0; ret`.
- `src/Shumway.Core/Activation.Tier1.cs:1433-1445` (push: a full `PushChoicePoint`
  plus a side-stack struct holding a delegate reference), `:1886-1918`
  (`PopIlChoicePointAndRestore`), and
  `src/Shumway.Interpreter/BytecodeInterpreter.Runs.cs:458-493` (the
  interpreter's `TryBacktrack` resumes it: `RetryOrPopIlChoicePoint` then
  `del(_engine, cursor)`).
- `src/Shumway.Compiler.Il/IlPredicateCompiler.Registers.Control.cs:291-300`
  (`EmitAllocateOp` → helper call when the control registers are not held),
  `:21-152` (every intrinsic — cut, deallocate, Y-slot access, `SetB0`,
  `SetCp` — requires held registers).

**What happens today.** The whole arc ADR-057 (local backtracking), ADR-058
(WAM choice points with a marker BP, inline push ladder and inline restore)
and ADR-060 (control registers in locals, inline allocate/deallocate/cut) is
gated on *being a region*, and a region needs ≥ 2 members. A hot predicate
with no module-local callee — exactly the shape of library utilities
(`member/2`, `append/3`, `select/3`, `memberchk/2`, `nth0/3`, `between/3`-style
enumerators, DCG terminals, any multi-clause fact table called from several
places and therefore "owned" by nobody, see `region-jit-limits.md` "What a
region copies") — compiles standalone and runs the Phase-14-era protocol:

- every non-last clause pays a *helper* `PushIlChoicePoint`: `PushChoicePoint`
  (11 control cells) **plus** a side-stack entry with three reference fields
  (`Del`, `OnPrune`, `CpsAlt`) — i.e. a GC write barrier per push — and
  `_cutQuickFloor` maintenance;
- every clause failure returns to the dispatch loop, which runs
  `TryBacktrack` → `RetrySafePoint` → `RetryOrPopIlChoicePoint` (restore via
  helper, side-stack pop, `ResetCutQuickFloor`) → a delegate invocation
  (double hop, item 4) → the method's cursor `switch`;
- the method holds no registers, so `allocate`, `deallocate`, `neck_cut`,
  `cut`, `get_level` and the Y-slot reads/writes are all helper calls, and
  `get_list`/`=/2` inline paths reload `_registers`/`_heap` from fields.

ADR-060 (Results, stage 4, first step) records the symptom: "`select/3` and
`no_attack/3` also compile as methods outside any region, which had no
register file" — fixed only by giving them an *empty* register file so the
`get_list` emission could run. ADR-061 ("Tried in the engine and rejected",
*A region of one predicate as the continuation method*) measured what inline
choice points give a one-predicate unit: **queens 13% faster** — and
rejected it only because, in the continuation-method form, the cold method
cannot resume inside a region. That constraint does not exist in the
default (region) form; a one-member region is simply a region.

**Proposal.** Drop the `MemberCount < 2` gate for a root that is multi-clause
(pushes choice points) or has a frame (`allocate`), and let `EmitRegionInto`
emit it with one member. Everything else exists: the planner, the cursor
plan (`ClauseAlt`/`IndexNode` cursors), `EmitRegionChoicePoint` with marker
BP, `EmitResumeOwnFast`, the ladder, the restore, the held control
registers, the member-entry aliasing for by-fid callers. Keep the standalone
shapes only for single-clause leaf predicates (no CP, no frame), where a
region buys nothing. The persisted path (`EmitPersistedBody`, `.cs:1112-1158`)
takes the same change; the prune's membership accounting is unaffected
(a one-member region absorbs nothing).

**Expected gain.** On the hot standalone shapes: a clause push goes from a
helper with ~11 cell stores + a struct with 3 references to the inline ladder
(ADR-060 item 6: ~180 IL ops shared per region, no references); a backtrack
goes from loop → helper restore → delegate to an in-method branch. ADR-060
stage 2b measured −8/−11% on queens(10) and −3/−5% on bc3 for the ladder
alone, and ADR-061 measured +13% on queens for inline CPs in a one-predicate
unit. For a program whose hot predicates are shared utilities (clpz over
Scryer's library, Logtalk's, Blint's parsing helpers) I would expect 5–15%;
for the Van Roy programs whose loop is one owned chain, nothing. Count the
standalone methods first: `SHUMWAY_IL_SHAPE=1` prints `[region-skip] …
members<2`, and `Activation.DiagLocalResumes[2]` / the interpreter's IL
backtrack path count how many backtracks cross the loop.

**Risk / invariants.** None new: the region machinery is already the
default for ≥ 2 members and is validated by the ADR-057/058 equivalence
suites. Code size: a one-member region carries the ladder, the restore and
the resume entries (+~200 IL ops) — negligible against the JIT limits for a
small predicate, and the planner's cost model applies unchanged. Watch
`CalleeRecorder`/eviction: a one-member region records no copied callees.

**Novelty check.** `IsRegionEmittable`'s gate is unchanged since Stage 3
(`il-region-compilation.md`, "IsStage3RegionEmittable gates the minimal
subset (≥2 members …)"); `region-jit-limits.md` states "a root alone is a
method, not a region" as a given while tuning the budget; ADR-061's
one-predicate experiment is the CPS form only. No closure doc or ADR records
trying a one-member region in the default form. `git log --grep` found
nothing (shallow history).

---

## 2. Body if-then-else choice points inside regions still go through `PushIlChoicePoint`

**Where**
- `src/Shumway.Compiler.Il/IlPredicateCompiler.Emit.cs:887-955` — the
  `try_me_else` of an inline ITE (ADR-025): in a region the else cursor comes
  from the plan (`regionCtx.CursorBySite`), but the push is
  `ldarg.0; <self delegate>; ldc elseCursor; ldc 0; call PushIlChoicePoint`
  (lines 925-947) — not `EmitRegionChoicePoint`.
- `src/Shumway.Compiler.Il/IlPredicateCompiler.Regions.cs:1025-1041`
  (`EmitRegionChoicePoint`, marker BP, inline ladder) is called only from
  `:1172` (clause alternatives) and `:1259` (index nodes); `:799-804` gives
  resume entries (`resumeCursor[...]`) only to `ClauseAlt`/`IndexNode`
  cursors.
- Backtracking into the ITE CP: `Regions.cs:962-977` — `EmitResumeOwnFast`
  bails to the call (`bp` is the IL sentinel −1, not a marker, line 654-656),
  then `Activation.TryResumeOwnChoicePoint` (`Activation.Tier1.cs:1789-1816`)
  → `TryResumeOwnIlChoicePoint` (`:1768-1782`) → `PopIlChoicePointAndRestore`
  (`:1886-1918`): a helper restore, a side-stack pop, two diagnostic hooks.
- Soft cut: `Activation.Tier1.cs:1462-1475` (`NeutralizeIlChoicePoint` swaps
  the side-stack delegate); the Tier-0 form uses the `SoftCutDeadBp` BP
  sentinel (`BytecodeInterpreter.Runs.cs:497-505`).

**What it costs.** Every `( C -> T ; E )` and `( A ; B )` in a region body
pays the helper push (11 cells + a reference-bearing side-stack entry with a
write barrier), and every condition failure pays a helper restore instead of
the region's inline restore. ADR-025's own bring-up note (Emit.cs:931-935)
measured the *previous* indirection (resume through the loop) at +40% on
boyer — "a call-cond ITE per rewrite/2 entry" — so ITE choice points are
demonstrably on boyer's hot path; Blint and any real program are ITE-dense
(the Blint survey in `il-local-inlining.md` found 109 multi-clause-rule and
121 helper call sites, most of them `(C->T;E)` lowerings before ADR-025).

**Proposal.** Treat the ITE else-CP exactly like a clause alternative: the
plan already gives the `try_me_else` pc a cursor; add it to `resumeCursor`
so it gets an ADR-058 resume entry (trust form: restore, pop, branch to the
`trust_me` label), and push it with `EmitRegionChoicePoint(ctx, elseCursor,
arity: 0)`. The `cut` after the condition already goes through the held-`B`
intrinsic. For `*->` keep the IL path, or implement the soft cut on the WAM
frame by writing `SoftCutDeadBp` into the frame's BP (what Tier-0 does), with
the region's fail handler treating that BP as "trust and keep failing".

**Expected gain.** boyer: the ITE CP is per rewrite step; 5–10% is plausible
given that the loop-resume form cost 40%. Blint/clpz: a few percent. Measure
with `SHUMWAY_IL_DUMP` (count `PushIlChoicePoint` sites in region dumps —
the Stage-11 survey counted 1470 push sites bundle-wide on Blint before
ADR-058; the ITE ones are what is left) and ABBA on boyer.

**Risk.** Soundness of the barrier: unchanged (B captured by `get_level_b`
before the push, ADR-025). The side-stack invariants
(`TopChoicePointIsIl`, `_cutQuickFloor`) get *simpler* — fewer IL CPs. The
debugger path is excluded as today (`DebugMode` holds nothing).

**Novelty check.** ADR-058 ("A region's choice points are WAM choice
points") and phase-42 closure describe clause alternatives and index nodes;
nothing mentions the ITE CP, and the emit for `Opcode.TryMeElse` still
carries the ADR-025 follow-up comment about direct re-entry via the
delegate. Not in the 2026-06 audit.

---

## 3. CP-free inline if-then-else for binding-free deterministic conditions

**Where**
- `src/Shumway.Compiler/Wam/ClauseCompiler.cs:548-600` — the lowering is
  always `get_level_b; try_me_else ELSE; Cond; cut; Then; jump; ELSE:
  trust_me; Else`; `src/Shumway.Compiler/InlineIte.cs:23-40` admits any
  conjunction of plain goals as the condition.
- `src/Shumway.Compiler.Il/IlPredicateCompiler.Emit.cs:887-955` — the IL
  emit always pushes the else CP.
- ADR-031 machinery that already does the same thing for *clause* guards:
  `IlPredicateCompiler.Dispatch.cs:41-59` (`TryGetCpFreeGuard`, cases A/B/G),
  `IlPredicateCompiler.Guards.cs` (recognizer, whitelist, stats),
  `Activation.Tier1.cs:1647-1687` (`BeginIlGuard`/`CommitIlGuard`/`FailIlGuard`
  for a guard that binds).

**What it costs.** A condition that is a type test, an arithmetic
comparison, `==`/`\==`, `@<`… creates no choice point and binds nothing, yet
the ITE pushes a full choice point (item 2's cost), then the `cut` pops it.
With item 2 the push is inline, but it is still ~11 stores + the restore on
the else path; the Tier-0 interpreter pays the same.

**Proposal.** Reuse ADR-031's recognizer on the ITE's condition range
(`try_me_else` … `cut`): case A (no bindings: type tests, compares, `==`) →
emit no CP; condition failure branches to the `trust_me` label directly,
the `cut` is a no-op, `get_level_b` is dropped. Case B (bindings via
`get_*`/`=/2` only) → `BeginIlGuard` + the existing commit/fail pair, the
same lazy-CP rule as the clause guard. The structural identity
`( C -> T ; E ) ≡ p :- C, !, T.  p :- E.` is exact, so the whitelist and the
soundness argument carry over; only the barrier bookkeeping (none needed
when no CP exists) differs.

**Expected gain.** For the common `( X == Y -> … ; … )`, `( integer(X) -> …)`,
`( N > 0 -> …)` shapes: the ITE drops from ~30 instructions + a restore on
the else path to a compare and a branch. Program-level 2–8% on ITE-dense
code (boyer, Blint, Logtalk's dispatch); nothing on the Van Roy search
programs. `CpFreeGuardStats` (`Guards.cs:25-60`) can be extended to count
how many ITE conditions pass the whitelist on a real program before building.

**Risk.** The whitelist is the same one ADR-031 validated; a wake pending at
the condition (an attributed variable bound by `=/2` in case B) must take the
lazy CP exactly as `EmitCpFreeGuardClause` does. Extension, not a redesign:
ADR-025 stays the lowering, ADR-031 the commit rule — I would still add a
line to ADR-031's scope.

**Novelty check.** ADR-031 is explicitly about clause guards ("the
pre-cut prefix of a non-last clause"); ADR-025 lowers every eligible ITE with
a CP; `InlineIte.cs` has no notion of a test-only condition; grep for
`binding-free`/`SafeCondition`/`CpFreeIte` in the compiler and emitter finds
nothing. Phase-33 closure (ADR-031 round) does not mention ITEs.

---

## 4. The transfer protocol: a double delegate hop and two helper calls per trampoline

**Where**
- `src/Shumway.Embedding/IlPromotionStore.cs:56-66` —
  `TableEntry(fid, del)` returns `del.Invoke` for a predicate without
  continuation methods: a `Func<Activation,int,bool>` whose target is
  `PredicateDelegate.Invoke`, i.e. two delegate dispatches per invocation.
  Installed in `BundleLoader.cs:593-595` and `:763` into `IlByFunctorId`.
- `src/Shumway.Compiler.Il/IlPredicateCompiler.Runtime.cs:76-94` —
  `IndexedDelegateHolder.Register` wraps the same way
  (`new Func<Activation,int,bool>(del)`); this is the delegate every IL
  choice point carries (`EmitPushIlChoicePoint`, Cps.cs:1065-1070) and that
  `TryBacktrack` invokes (`BytecodeInterpreter.Runs.cs:467`).
- `src/Shumway.Compiler.Il/IlEmit.cs:710-717` — the DynamicMethod's delegate
  is created once as `PredicateDelegate`, closed over the literal table.
- The dispatch loop invokes through the table at
  `BytecodeInterpreter.cs:697` (markers), `:1021` (`CallIl`), `:1120`
  (`ExecuteIl`); `Activation.Continuation` (`Activation.Tier1.cs:1099-1116`)
  hands the same wrapped delegate to the region's cross-region tail return.
- `src/Shumway.Compiler.Il/IlPredicateCompiler.Emit.cs:2098-2107`,
  `:2176-2183`, `:2300-2307` — every threaded Call/Execute ends with
  `call Activation.SetPc` and `call set_IlTailCallPending`.
  `src/Shumway.Core/Activation.cs:910-914` (`SetPc` tests the diagnostic
  `TrapPc`); `IlInlineTable.Generated.cs:125` records the JIT's verdict:
  **`SetPc` = 0, "unprofitable inline"** — it is a real call at every
  transfer; `:168` `set_IlTailCallPending` = 1 inline.
- `Activation.Tier1.cs:830` (`_ilTailCallPending` is a public byte on the
  reserved surface); `Activation.Frames.cs:19` (`_p` is private).

**What it costs.** ADR-061's profile of the inter-region transfer (~80 ns)
attributes "the rest" to "a non-inlined helper, a chain of delegates (`Func`
to `PredicateDelegate` to a shuffle thunk), and the prologue and epilogue of
a large region method". The prologue/epilogue is what ADR-061 is about; the
delegate chain and the non-inlined helper are independent of it and are
still there in the default form: every cross-method call, every return and
every backtrack into an IL choice point pays `Func.Invoke` →
`PredicateDelegate.Invoke` → closed-delegate shuffle → method, plus (per
Call/Execute) a real call to `SetPc` and an inlined property setter.

**Proposal.**
(a) Create the `Func<Activation,int,bool>` directly from the `DynamicMethod`
(`dm.CreateDelegate(typeof(Func<…>), table)` — a second delegate over the
same method), hand it to `IndexedDelegateHolder.Register` and `TableEntry`,
and keep `PredicateDelegate` only as the API-facing type (or make it the
`Func`). One delegate dispatch instead of two, everywhere.
(b) Emit the transfer as two field stores: `stfld _p` (expose `_p` on the
reserved public surface like `_ilTailCallPending`, ADR-058 item 6 pattern)
and `stfld _ilTailCallPending`; emit the `SetPc` call only when `TrapPc >= 0`
at emit time (it is a process-wide static read from the environment, so
the decision is exact).
(c) Minor, same path: `ContinueIlTailCall` (`BytecodeInterpreter.cs:3114-3123`)
calls `TakeIlDeopt` and tests `IsResumeMarker` on every tail transfer;
folding the deopt flag into the pending byte (`_ilTailCallPending == 3`)
saves a load and a store per transfer.

**Expected gain.** Per transfer ~3–6 ns of ~80 ns (ADR-061's figures: a
slot call costs ~0.35 ns over a direct call; a delegate hop with a shuffle
thunk is several ns). On `xr2`-like call-heavy code between regions 3–6%;
on backtracking into standalone IL choice points (item 1 makes this
rarer) similar; nothing inside a region. Cheap to verify on `xr2.pl`
(ADR-061 Context) in one process.

**Risk.** (a) is mechanical; (b) widens the reserved surface by one field,
as ADR-058/060 did; the debugger and `TrapPc` paths keep the call. No
invariant touched (compiled IL stays engine-agnostic: it writes the
activation it was given).

**Novelty check.** ADR-061 names the delegate chain as a cost but fixes it
only in the CPS form (native code pointers, `calli`). `TableEntry`'s own
comment ("Method-group conversion: del.Invoke creates a Func that calls
through to del", `BundleLoader.cs:593-594`) shows the double hop was seen as
one-time link cost, not per-call cost. The 2026-06 audit's Tier-A item 1
hoisted the holder probe but kept the wrapper. `SetPc` as a non-inlined
call is visible only in the generated inline table, which nobody read for
this purpose.

---

## 5. Stop spending the JIT's inline budget on constants, register reads, the trail push and `put_variable`

**Where** (all in `src/Shumway.Compiler.Il/`)
- Constant cells: `IlPredicateCompiler.Emit.cs:155-171` (`EmitAtomId` → `ldc.i4`),
  then `EmitHelperCall(emit, CellAtomMethod)` / `CellIntMethod` at
  `Emit.cs:496-507, 527-537, 639-663` (get_integer, get_nil, put_atom,
  put_integer, put_nil) and 12 more sites in `FactInline.cs`, `ReadStream.cs`,
  `Unify.Window.cs` (18 sites by grep). `Cell.cs:69-83`: `Atom` is a
  shift-or, `Int` adds a range check. `IlInlineTable.Generated.cs:170-171`:
  **`Cell::Atom` = 2 inlines, `Cell::Int` = 2 inlines per site.**
- Register reads: `Emit.cs:567-572` (get_variable_x), `:592-597`
  (get_variable_y), `:695-700` (put_value_x), `:3103`, `FactInline.cs:244,366`,
  `Guards.cs:1491,1732`, `IntArith.cs:608`, `Runtime.cs:568` call
  `Activation.GetRegister` (`Activation.UnifyOps.cs:13`, no inlining
  attribute; `IlInlineTable.Generated.cs:94`: 1 inline). The emitter already
  has the direct form, `EmitLoadRegisterConst` (`Unify.cs:540-545`:
  `ldfld _registers; ldc; ldelem`), used only by the `=/2`/`get_value`
  paths; `TryEmitIntrinsic` (`Registers.Control.cs:35-44`) replaces the call
  only when `RegisterArray` is held, which the default `HeldByDefault =
  ControlRegs` never is.
- Trail push: `Unify.cs:303-314` (`EmitBindBits`: `call TrailBind` when
  `home < HB`) is the region form; the inline form `EmitHotTrail`
  (`Unify.cs:67-89`) is used only under `CpsHotExit` (`:439, :451, :853`).
  `Activation.Terms.cs:1124-1135`: `TrailBind` → `TrailBinding`
  (AggressiveInlining) → `EnsureBindingTrailCapacity` (FixedInline): the
  inline table says **4 inlines per site** (`Generated.cs:134`).
- `put_variable_x/y`: `Emit.cs:749-753, 777-781` call `AllocateHeapUnbound`
  (`Activation.Frames.cs:193-201`, 4 inlines per site, `Generated.cs:68`);
  the inline emission `TryEmitHotAllocateHeapUnbound` (`Cps.cs:761-792`) is
  gated on `CpsHotExit` (`:763`).

**Why it costs.** `region-jit-limits.md` ("The inliner's cap") measured that
69 of Blint's 121 regions reach the JIT's 1,024-locals cap, after which
"the JIT declines every remaining call site of the method … the hottest
among them are the wake check after a builtin call, the register reads, the
atom cells, the trail push." The project fixed the first (the wake check is
now a field read, −3 to −5% on sendmore/flatten/crypt/qsort) and built a
planner that *budgets* the inlines; the other three are still emitted as
calls that the JIT has to inline, each costing 1–4 of the method's
temporaries (`1.19 locals per inline`) and, past the cap, becoming real
calls in the region's tail. A `put_atom` is then `call Cell.Atom` → a
function call to build a constant.

**Proposal.** Emit them as IL, no call to inline:
(a) atom/int/nil literals as `ldc.i8 <tag<<60 | id>` at run time; for
persisted bundles a new `IlPatchKind.AtomCell` whose 8-byte `ldc.i8`
operand the PE scan patches (today `IlPatchSite.cs:55-60` scans only the
4-byte `ldc.i4` operand; the sentinel scheme extends to 8-byte operands
with a reserved high word). `Cell.Int`'s range check folds at emit time.
(b) `EmitLoadRegisterConst` at every constant-index register read.
(c) `EmitHotTrail`'s body (compare, capacity test, store, increment, with the
helper on the capacity slow path) in region mode too — it already works on
fields when nothing is held (`EmitLoadEngineField` falls back to the field).
(d) `TryEmitHotAllocateHeapUnbound`'s body with a helper slow path instead of
`EmitColdExit`, enabled in regions.

**Expected gain.** Same class as the wake-check change the doc measured
(2–5% on the four programs whose regions sit at the cap, even elsewhere),
cumulative over three more sources, and it lets the planner's locals model
admit more members per region (fewer trampolines). The honest number needs
the JIT events (`JitTracing`) before/after on Blint and the clpz puzzles:
inlines declined per region, then ABBA timing. Zero cost where the cap is
not reached, since the emitted IL is what the inline would have produced.

**Risk.** Low; (a) touches the persisted patch format (a bundle version
bump, `IlPatchKind` is `byte`). `IlInlineTable` and `IlRegionCost`'s opcode
weights must be refitted (the doc describes the procedure: "An opcode whose
weight was fitted is fitted again when its emission changes").

**Novelty check.** `region-jit-limits.md` names the three remaining hot
declined inlines and the fix pattern (the wake check) but records no work
on them; git log (shallow) has "get_blit's test of the register inline" and
the `=/2` inline, nothing on atom cells / register reads / trail push in
regions. The hot-exit forms exist only under ADR-061's CPS flag.

---

## 6. `_cellsAllocated` is a read-modify-write on every heap allocation — count by heap-top deltas instead

**Where**
- `src/Shumway.Core/Activation.Frames.cs:181` (`AllocateHeap`: `_cellsAllocated += count`),
  `:199` (`AllocateHeapUnbound`: `_cellsAllocated++`).
- Emitted inline: `IlPredicateCompiler.Unify.cs:457-462` (fused `get_list`
  write: `ldfld; ldc 2; add; stfld`), `IlPredicateCompiler.Cps.cs:785-790`
  (hot `put_variable`); every `put_structure`/`put_list`/`unify_*` write goes
  through `AllocateHeap`.
- Consumers: `src/Shumway.Builtins/ControlBuiltins.cs:88,111` (`time/1`,
  `statistics`), `Diagnostics/AreaTrace`, `BuiltinTally`,
  `src/Shumway.Embedding/HeapBufferPool.cs:63` (a sizing proxy),
  `Activation.Wasm.cs:523`.
- Points where the heap top moves backwards:
  `Activation.Frames.cs:1037` (`RestoreCommonFromCurrentCp`), the inline
  restore `IlPredicateCompiler.Frames.cs:568-570`, `FailIlGuard`/
  `TryFailIlGuardQuick` (`Activation.Tier1.cs:1194-1203, 1675-1687`), the
  heap GC's compaction.

**What it costs.** One load + add + store to an activation field per
allocation, on both tiers and in every inline write path the IL tier
emits. On nreverse-class code (the one loss against GNU Prolog in
`cross-engine-comparison.md`) allocation *is* the loop.

**Proposal.** Keep the count exact without touching it per allocation:
`CellsAllocated = _heapTop + _cellsDiscarded`, where `_cellsDiscarded +=
oldTop − newTop` at every point the top is lowered (the five sites above;
each already reads both values) and `+= reclaimed` in the collector. The
`cells` figure `time/1` prints stays identical (the gate's "heap cells
identical" check is the regression test). The per-query `AreaTrace` /
`BuiltinTally` diagnostics read the property, so they keep working.

**Expected gain.** Small per site, universal: 1–3% on allocation-bound loops
(nreverse, serialize, flatten); it also removes two IL instructions and a
field reference from every inline write site, which counts against the
JIT's reference limit. Cross-tier change (Core); listed here because the
IL tier emits the increment inline.

**Risk.** Every path that lowers the heap top must be found: the restore,
the guards, the GC, `DetachHeapBuffer`, the wasm world's `CellsClaimed`
merge. Mechanical; a missed site shows as a changed `time/1` cell count.

**Novelty check.** The 2026-06 audit's "verified non-findings" cover the
Cell factories and `AllocateHeap`'s inlining, not the counter. Phase-42
closure: "Tier-1 allocates the heap cells Tier-0 does, and `time/1` counts
its …" — the counter was made *exact*, not cheap. No ADR or design doc
mentions it.

---

## 7. A `cursor == 0` test before the entry `switch`

**Where**
- `src/Shumway.Compiler.Il/IlPredicateCompiler.Cps.cs:817-852` (`EmitCursorSwitch`),
  used at `Dispatch.cs:97, :504`, `Emit.cs:2868`; the region entry at
  `Regions.cs:844-852` (`ldloc cur; switch; …`).

**What it costs.** Every fresh call enters the method with cursor 0 and goes
through the jump table: a bounds compare and an *indirect* jump. The same
indirect-jump site also serves every resume cursor (call-site returns,
clause alternatives, builtin resumes), so its target history is mixed and
the predictor is on its own. ADR-061 ("Tried and rejected", *The
alternatives method as the predicate's entry*) found that entering through
a method with "an unfolded cursor" cost 6–20% across the Van Roy set in
the CPS form, where the entry method otherwise has no switch at all.

**Proposal.** `ldloc cur; brfalse entry_label` immediately before the
`switch` (two instructions): the dominant entry becomes a direct,
well-predicted branch; the table serves only the resumes. For regions,
the member-entry cursors (`MemberEntry` kind) could take the same
treatment when the region is entered from outside at a member.

**Expected gain.** 0–3% on call-heavy code; probably invisible inside a
region whose calls are `br`. Trivially cheap to try; measure on `xr2`,
tak, crypt.

**Risk.** None.

**Novelty check.** The chunk-363 work replaced the linear compare chain by
the `switch` and the 2026-06 audit asked for the same in the remaining
shapes; a fast path for the 0 cursor is not mentioned anywhere.

---

## 8. Size the register bank at method entry, then hold `_registers` in a local and drop the capacity checks

**Where**
- `src/Shumway.Core/Activation.UnifyOps.cs:9-27` — `MinRegisterCount = 64`,
  `SetRegister` grows on demand, `EnsureRegisters(count)` exists (used by
  ADR-063's read-only stream "before its first temporary").
- `src/Shumway.Compiler/Wam/CompiledClause.cs:27` — `RegisterCount` per
  clause is known at compile time.
- Checks emitted per site: `IlPredicateCompiler.Unify.cs:349-357` (window
  registers must exist), `:696-730` (`TryEmitSetRegister`: `idx >= Length`
  test + slow path), `IlPredicateCompiler.Frames.cs:358-366` (push site,
  arity > 64), `EmitSetRegisterConst` (`Unify.cs:551-565`) falls back to the
  helper for index ≥ 64.
- Reloads of the array: `EmitLoadEngineField(emit, EngRegisters)` at every
  register access (`Unify.cs:540-545, 553-558, 345-346, 708-709`, …) because
  `RegisterArray` is not held; `Registers.cs:330-336, 384-390, 404-411, 439,
  455` list the helpers whose rows say they *write* `RegisterArray` (they may
  grow it).

**What it costs.** Each register access reloads `this._registers` (the JIT
can CSE it only between calls), and each generic store or window pays a
length compare. ADR-060's queens listing counted 438 field loads per 4,662
native instructions; the register array is the most frequent of them after
the heap.

**Proposal.** Emit `EnsureRegisters(maxRegisterOfThisMethod)` once at the
method's entry (the region's maximum over members). Then, within the
method, no helper the method calls can grow the bank *for this method's
registers* except the `All`-row helpers (meta-call, builtins, wakes), so:
(a) every `idx >= Length` check for a constant index below the max goes;
(b) `RegisterArray` can be held (ADR-060 item 1 already lists it) with the
table rows of the register-growing helpers narrowed to "writes
RegisterArray only when asked for a slot ≥ the bank" — which this method
never does — so the reload after `GetListVarXVarX`, `PutStructure`,
`UnifyVariableX`, `SetRegister`… disappears. Only `All` rows reload.

**Expected gain.** Uncertain: ADR-060 stage 3 held the heap top, heap array
and register array together and lost 9% on queens(10) to the reloads after
every unify helper — but those were the *heap* reloads (every unify helper
writes `_heap`), which stage 4 then inlined. The register array alone, with
the no-grow guarantee, has far fewer writers. 0–5%; measure as stage 3 did,
in one process on queens/nreverse/zebra.

**Risk.** The helper table (`Registers.cs:304-492`) is verified by a test
against each method's IL; narrowing a row needs that test to accept the
"never grows below N" argument — or keep the rows and accept reloads only
after the few helpers that can touch a slot the method did not name
(`IlMetaCallHelper.Dispatch`, `PushChoicePoint` with arity > bank).
Meta-call `call/N` writes registers 0..arity−1 of the *callee*, not of this
method, so the invariant holds for the method's own registers.

**Novelty check.** ADR-060 stage 3 ("not kept") held the register array
only in combination with the heap registers; the entry-time sizing that
makes it hold-able cheaply is ADR-063's trick for the read stream, not
applied to the method as a whole. Partially explored; flagged as such.

---

## 9. Zero only the Y slots that can be read by the collector before they are written

**Where**
- `src/Shumway.Core/Activation.Frames.cs:657-669` — `Allocate` stores
  `RawInt(0)` into every Y slot "so the heap GC's conservative stack scan
  skips it"; the IL form `IlPredicateCompiler.Registers.Control.cs:327-341`
  emits one store per slot (`for i < n`).
- `src/Shumway.Core/Engine.HeapGc.cs:1056-1072` — the scan is conservative
  and bounds-guarded: "The worst a stale-but-plausible ref can do is
  over-retain … never crash and never corrupt." So the zeroing prevents
  retention, not corruption.
- Safe points at which a collection can run are goal boundaries
  (`MaybeCollectHeapAtCall`, the marker path, the region's intra-call safe
  point `Regions.cs:1633-1638`); a slot's first write is a
  `get_variable_y`/`put_variable_y`/`unify_variable_y`/`a_eval_is` the
  clause compiler emits at a known pc.

**Proposal.** At emit time, compute per clause which permanents are first
written before the first safe point (the first `call`/`execute`/
`call_builtin`-with-GC/intra-region `br`). Those slots need no zeroing:
no collector can see them stale. Typically every head variable is written
before the first body goal, so most frames zero nothing. The interpreter's
`Allocate` can take the same information from a per-clause mask the WAM
compiler records (an `allocate` operand variant), or keep zeroing.

**Expected gain.** n stores per `allocate`; for tak (`bench(5)`: 400k
allocates of 3–4 slots) a few percent of the allocate cost, ~1% of the
program. Low, but it is on every framed call and the analysis is one pass
over the clause.

**Risk.** A slot not written before a *later* safe point but read by the
collector then would over-retain, never corrupt (HeapGc comment above); a
wrong mask is a leak-class bug, visible as "heap cells identical" is not.
An `allocate` operand change is a bytecode format change (ADR-006
territory, "fixed-size encoding"): keep the mask in the clause metadata the
IL tier already reads instead, and leave Tier-0 alone.

**Novelty check.** The `Allocate` comment records the previous design
(eager heap variables) and its replacement (RawInt(0)); nothing records a
liveness-based skip. The 2026-06 audit does not mention it.

---

## 10. Inline the choice point push at the site when the region has headroom

**Where**
- `src/Shumway.Compiler.Il/IlPredicateCompiler.Frames.cs:325-374`
  (`EmitFramePushSite`: a site stores a return index, branches to the shared
  ladder, and comes back through `switch l.Returns` at `:462-465`).
- ADR-060 Context/Decision item 6 and Risks: the per-site inline form "was
  the fastest form measured, by 3 to 5% on `queens(9)`", chosen against for
  code size; "The shared sequence returns through an indirect jump".
- `IlRegionCost` (`IlRegionCost.cs`) already knows each region's distance
  from the JIT limits.

**Proposal.** A policy, not a mechanism: after planning, if the region's
reference/locals estimate leaves room for `pushSites × ~180` references,
emit the push inline at the site. The per-site form of ADR-058 item 6 was
removed when the ladder landed (ADR-060: "the per-site inline form of
ADR-058 item 6 is gone"), so it would be re-derived from the ladder body
(`EmitFramePushLadder`, `Frames.cs:379-466`) with the arity a constant,
which also folds the arity-dependent control-word offsets. The Van Roy
regions are small (`region-jit-limits.md`: "The Van Roy programs keep their
regions to the member") and would take the fast form; Blint's and clpz's
large regions keep the ladder.

**Expected gain.** 3–5% on CP-push-bound programs (queens is the largest
remaining gap against GNU Prolog, 4.4× at the last baseline, 2.3 M pushes
per `queens(9)`); nothing elsewhere.

**Risk.** Two emission forms to keep equivalent; the cost model must count
the inline form's weight.

**Novelty check.** Both extremes were measured (ADR-060); the
size-conditional choice was not tried. Low rank because the ceiling is known.

---

## 11. Minor, same family (verified, small)

- **`EmitDerefA0` and the indexed-atom entry** (`Runtime.cs:559-587`,
  `Dispatch.cs:508-541`) read the register, the tag and the atom id through
  `GetRegister`, `Cell.Tag`, `Cell.AsAtomId` helper calls (3–4 inlines per
  entry); the raw-bits forms used by `EmitUnifyTwoCells` (`Unify.cs:251-279`,
  `EmitTagIs`) do the same with no call. Same fix as item 5.
- **The marker path of the dispatch loop** (`BytecodeInterpreter.cs:590-718`)
  runs `MaybeCollectHeapAtReturn` (five field tests), `DecodeResumeMarker`
  (a `Volatile.Read` of the pairs array and a tuple load) and
  `ResolveMarkerDelegate` for every marker, forward call or return. The safe
  point must stay (a threaded IL Call emits no `MaybeCollectHeapAtCall` of
  its own, `Emit.cs:2067-2107`; the loop is its safe point), but
  `ContinueIlTailCall` (`:3114-3123`) decodes and tests the same marker a
  second time (`TakeIlDeopt`, `IsResumeMarker`) before the loop decodes it
  again. One decode per transfer is enough. Noted, not ranked.
- **`Inferences`** is not counted in compiled code (ADR-061), fine; but
  `CallIl`/`ExecuteIl` still do `_engine.Inferences++` and
  `Tier1Dispatcher?.CreditCaller(pc)` (an interface call) per bytecode→IL
  call (`BytecodeInterpreter.cs:997-998, 1102-1103`). Tier-0 agent's area.

---

## Considered — already done, measured, or rejected (do not re-propose)

- **Call-site inline caching** — never built, superseded by threaded
  continuation (`inline-caching.md`).
- **Linear cursor compare chains → `switch`** — done (chunk 363 and the
  2026-06 audit's Tier-A item 1 for the remaining shapes;
  `EmitCursorSwitch` everywhere now).
- **`IndexedDelegateHolder` ConcurrentDictionary probe** — replaced by a
  slot array + per-invocation hoist (`Runtime.cs:51-97`, Dispatch.cs:74-82).
- **Region compilation, budgets, JIT-limit planning, the locals model,
  copy-vs-call of installed callees, root selection, dead-region prune** —
  all shipped (`il-region-compilation.md`, `region-jit-limits.md`).
- **Registers in locals**: control registers held (ADR-060 stages 1–2b);
  heap top/array held (stage 3) **measured and not kept**; `get_list`, the
  fused windows, `=/2`, `get_value_*`, `a_int_bin/cmp` on locals, frames
  through a `ldelema` reference — done (stage 4). Passing held registers by
  ref to helpers — rejected (ADR-060 alternatives).
- **Choice point push/restore**: shared ladder vs inline — both measured
  (ADR-060 item 6); variable-width CP frames — rejected (ADR-026);
  fail-handler fast path with the marker-run subtraction — done.
- **Wake check as a field read** — done (`EmitHasPendingWakeups`); hoisting
  provably-redundant `FlushWakeupsForIlCut` — considered and left alone
  (Stage 11, soundness-delicate).
- **Self-tail recursion as an in-method loop**, **leaf/rule/fact
  inlining**, **CP-free guard commit (ADR-031)**, **guard continuation stack
  (ADR-033)**, **inline ITE lowering (ADR-025)**, **soft cut (ADR-037)** — all
  shipped; ADR-032 soft-rejected; ADR-021 register allocator rejected.
- **Builtins called directly**, **callee markers as constants**, **the
  integer lane on method locals**, **cold/hot `FusedBin` split** — done
  (phase 43, #151/#166, ADR-060 results).
- **Tail-calling the continuation instead of returning to the loop** —
  measured at 7% (ADR-061 Context) and shipped for cross-region returns
  (`CrossRegionTailReturn`, `Activation.Continuation` with the 64-hop
  bound); the full form is ADR-061 (CPS), implemented behind
  `SHUMWAY_IL_CPS=1`, 18–43% faster than regions on the Van Roy set once
  compiled, default still undecided (phase-43 "Deferred"). Deciding it is the
  largest single lever and is explicitly *not* a new idea; items 1, 2, 4 and
  5 above are worth having in either form (item 1 and 2 only in the region
  form).
- **ADR-061's own rejected list** (alternatives method as entry, registers
  read from fields, smaller held mask, self-recursion as a transfer,
  rotate-built constants, byref frame writes under Sigil, volatile array
  loads, per-cursor methods, inline restore in the failing method, local
  resumption in every method, shared push checks per arity) — recorded
  there "so that they are not retried without a new reason".
- **Per-register lattice analysis and pre-resolved heads in guards** —
  measured (a census and a spike) and closed within noise (phase-43
  "Deferred").
- **PGO / tiering interactions**: engine helpers compiled-code inlines are
  `HelperImpl.Fixed` (no instrumentation, stable layout); persisted methods
  compiled on the worker get `AggressiveOptimization`
  (`IlEmit.OptimizeAtOnce`, `Emit.cs:138`); DynamicMethods are not tiered by
  design. Nothing left there that the code does not already handle.
