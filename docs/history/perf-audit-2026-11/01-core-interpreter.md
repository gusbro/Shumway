# Performance review — core engine (`Shumway.Core`) and the Tier-0 interpreter

Scope: `src/Shumway.Core/` (Activation*, Cell, trails, choice points, environments, heap GC, atom/functor tables, attvars/wakeups) and `src/Shumway.Interpreter/`. Static analysis only (no `dotnet` in the container): every claim below was checked by reading the cited code at the cited lines. Line numbers are against the working tree at HEAD (2026-11-04).

What was checked first, to avoid re-proposing: `docs/history/efficiency-audit-2026-06.md` (every Tier-A/B item re-verified in code — see the last section), `docs/benchmarks/{analysis,cross-engine-comparison,baseline}.md`, `docs/architecture/invariants.md`, ADR-002/004/005/006/016/017/026/049/052/053/063/064 and the rejected 021/026/032, the phase-25…43 closure docs and `wam-vs-gprolog-*.md`, and `git log --grep` for every idea (the exact greps are listed per item under "Novelty check").

Where Tier-0 matters today (so the ranking is honest): Tier-1 is on by default since phase 43, so the interpreter's steady-state customers are (a) **dynamic predicates** (invariant: they execute on Tier 0), i.e. assert/retract-heavy programs like Blint and Logtalk's dynamic tables; (b) code before promotion / under `SHUMWAY_IL_PROMOTE=0`, `compile_mode=debug`, and the browser's Tier-0 fallback; (c) the engine primitives (unify/bind/trail/CP/GC) that **both tiers** call. Items are ranked by expected value under that mix.

---

## Ranked findings

### 1. The dispatch loop round-trips `pc` through `_engine._p` on every instruction (and re-derives the register/heap arrays per access)

**Where.** `src/Shumway.Interpreter/BytecodeInterpreter.cs:529` (`int pc = _engine.P;` at the loop head), every straight-line case ends with `_engine.SetPc(pc + N); inClause = true;` (e.g. `:778`, `:1906`, `:2315`, `:2443`, `:2973`, `:3046`), `RunUnifySequence` exits with `_engine.SetPc(pc)` (`BytecodeInterpreter.Runs.cs:340,344`). `SetPc` is `Activation.cs:909-914`: a `TrapPc` test plus a field store; `AdvancePc` (`:918-922`) likewise.

**What it costs.** Per dispatched instruction: a store to `_engine._p`, then at the loop head a dependent load of the same field (store-to-load forwarding, ~4–5 cycles on the loop's critical path on x64, more on ARM64), plus the `TrapPc >= 0` compare (a `static readonly` that tiered-JIT folds only once the method reaches Tier-1/OSR — `Dispatch` carries no `MethodImpl`, so it is tiered and OSR-compiled; fine, but the field round trip itself is never removed). The I1 `inClause` flag (`:525-537,724`) already removed the generation/marker/bounds checks for straight-line ops but kept the field round trip. Likewise every register access is `_engine._registers[i]` (load field → load length → check → load) and every heap access `_engine._heap[i]`; the loop never caches the arrays because `EnsureRegisterCapacity`/`EnsureHeapCapacity` may replace them.

**Proposal.** Keep `pc` in a local across the straight-line regime: straight-line cases do `pc += N; goto fetch;` (a label just above `byte opByte = codeArr[pc]`, `:726`) instead of `SetPc + break`; publish `_engine.SetPc(pc)` only on the paths that leave the regime (any Call/Execute/CallBuiltin/backtrack/IL/marker/return — exactly the cases that today set `inClause = false`) and before any engine call that can observe `P` (builtins, `MaybeCollectHeap*`, debug ports). The readers of `P` outside the interpreter are few and all cold: `PrologEngine.Debug.cs:586,657,2133` (frame capture) and `PrologEngine.Libraries.cs:183` (dialect-at-address for an error), plus `TrapPcHit`. To keep error positions exact when a helper throws mid-clause (e.g. `resource_error` from `AllocateHeap`), add an exception filter on `Dispatch`'s try: `catch (Exception) when (PublishPc(pc))` — a filter runs before unwinding and sees the local. Second step, same shape: cache `Cell[] regs = _engine._registers` / `Cell[] heap = _engine._heap` in locals and refresh them at the same regime exits (and after any helper that can grow them: `SetRegister` with a high index, `AllocateHeap*`, `PushChoicePoint`'s `EnsureRegisterCapacity`); the public `_registers`/`_heap` fields exist precisely so compiled code can do this (ADR-058/060 did it for Tier-1 regions: commit `213e885` "A region addresses its frames through a reference, one bounds check per frame").

**Expected gain.** Tier-0 only. On dispatch-bound shapes (tak/nreverse/qsort at T0, Blint's chain code) I would expect 3–8 % — the field round trip is a fixed per-instruction tax on top of ~10–20 instructions of real work. Honest uncertainty: high; the JIT may already keep `_engine` enregistered and the store/load pair may be partly hidden by OoO execution. Measure with the Van Roy harness under `SHUMWAY_IL_PROMOTE=0`, interleaved min-of-5, and confirm the codegen change with `DOTNET_JitDisasm=Dispatch DOTNET_JitStdOutFile=…` (look for the `mov [rcx+off], eax` / `mov eax, [rcx+off]` pair at the loop head disappearing).

**Risk / invariants.** None of the invariants is touched. The risk is a stale `_p` observed by something that reads it mid-clause; the enumeration above says those readers are all on cold paths (debug, error rendering, forensics), and the filter covers exceptions. The debugger's `RedirectPc` (`Activation.cs:964`) already forces `inClause = false; continue;` so it re-reads the field — keep that.

**Novelty check.** `git log -i --grep` for "pc in a local", "local pc", "pc local", "registers local", "store forward", "field round", "Dispatch method", "OSR", "jump table": 0 hits. The I1 preamble (audit Tier B "loop preamble") shipped the check-skipping but not the local-pc form; nothing in the closure docs mentions it.

### 2. Half of the operand reads still go through `in ProgramView` with two bounds checks per operand; a padded code array would allow one check per instruction

**Where.** `ReadI32`/`ReadI64` (`BytecodeInterpreter.cs:3235-3264`) peel the array but still call `BytecodeIO.ReadInt32(byte[], int)` = `BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(offset))` (`BytecodeIO.cs:13-14`): `AsSpan(offset)` checks `offset <= Length`, `ReadInt32LittleEndian` checks `span.Length >= 4` — two checks per operand. The handlers that were *not* converted to `ReadI32` read `BytecodeIO.ReadInt32(code, pc + k)` through the `in ProgramView` struct (a `Primary` field load per operand on top): `Try`/`Retry`/`Trust` (`:1558-1559,1569,1577`), all nine `SwitchOn*` (`:1587-1590,1614,1626,1650,1675-1679,1697-1698,1710-1711,1732-1733,1755-1758,1769-1772,1788-1791`), `GetPstr`/`PutPstr`/`PutBlit` (`:2655-2656,2670-2671,2680-2681`), the float/bigint/rational family (`:2757-2758,2771-2772,2787,2811-2812,2825-2826,2834,2887-2889,2900-2903,2911-2912`), and — the hot ones — the ADR-018 arithmetic opcodes `AEvalPush` (`:2945-2946`), `AEvalBin`/`AEvalUn` (`:2978,2983`), `AEvalIs` (`:2989-2990`), `AEvalCmp` (`:3008`), `AIntBin` (`:3019,3040-3044`: five reads → ten checks + five `Primary` loads) and `AIntCmp` (`:3053,3072-3074`).

**What it costs.** `a_int_bin`/`a_int_cmp` are the arithmetic workhorses (tak, queens, crypt, sendmore at T0); `switch_on_term` runs on every indexed call. 8–10 redundant compares+branches per such instruction.

**Proposal.** (a) Mechanical: route the listed sites through `ReadI32(code, codeArr, …)`. (b) Make `ReadI32` a single-check read: `MemoryMarshal.GetArrayDataReference(codeArr)` + `Unsafe.ReadUnaligned<int>` guarded by one `(uint)(offset + 4) <= (uint)codeArr.Length` test — or no per-operand test at all if the code array is guaranteed to carry ≥ 24 bytes of `Invalid` (0x00) slack past the live code (the widest operand span is `SwitchOnArg`, 21 bytes). `AppendCode` (`Activation.Tier1.cs:380`) already doubles capacity, and `ProgramView.cs:27-29` documents that the slack is zero bytes = `Invalid`; reserving `+32` in the grow formula (and in the per-query rewire in `PrologEngine.Query.cs`) makes the loop-head `pc >= codeLen` check (`:720`) sufficient for every operand read. Document it as an invariant of the code buffer (ADR-006 fixed-size encoding already gives the per-opcode width).

**Expected gain.** 1–4 % on arithmetic-heavy T0; sub-1 % elsewhere. Also simplifies the dynamic-chain `CheckVisible` peel (`Runs.cs:47-48`, two `ReadI64`). Measure on tak/crypt T0.

**Risk.** Memory safety: with the padding form, a corrupt `pc` that passes `pc < codeLen` can read into the slack but never past the array, so the reads stay in bounds by construction; without the padding keep the single explicit check. The `Break` opcode path re-dispatches from a byte table and never reads operands differently — unaffected.

**Novelty check.** Audit Tier B listed "operand reads re-test Overflow + double bounds-check per read; peel via `codeArr` in hot handlers" — the peel shipped for the unify/get/put/call family only (the `ReadI32` helper); the ~35 sites above still take the old path, and neither the single-check form nor the padding idea appears in git (`--grep` "operand read", "ReadI32", "peel", "padding": only `80facfb`, unrelated).

### 3. `_cellsAllocated` is a read-modify-write on every heap allocation, in both tiers — derive it from the (rare) truncation events instead

**Where.** `Activation.Frames.cs:181` (`AllocateHeap`) and `:199` (`AllocateHeapUnbound`), both `FixedInline`, so the RMW is inlined at every allocation site in Core and in the interpreter; the IL tier emits the same `ldfld/add/stfld` triple inline into generated code (`IlPredicateCompiler.Unify.cs:90,459-462`, seven emit sites); the wasm world adds its claimed cells on exit (`Activation.Wasm.cs:523`); `MakeBigIntProduct` corrects it by hand (`Activation.Terms.cs:69,75,79`). The field is documented (`Activation.cs:43-53`) as the deterministic `--alloc` benchmark metric and is "present in every build".

**What it costs.** One 64-bit load+add+store per allocation on the hottest path the engine has (every `put_variable`, every write-mode `unify_*`, every cons/structure build). It is not free: it is a second memory RMW next to `_heapTop` on the same cache line, and in the IL tier it costs three IL instructions per emitted allocation (inlining budget, ADR-060's concern).

**Proposal.** Maintain `_cellsReclaimed` instead and define `CellsAllocated => _cellsReclaimed + _heapTop`. Every site that *lowers* `_heapTop` adds `(old − new)` to `_cellsReclaimed`; those sites are few and already enumerated by grep: `RestoreCommonFromCurrentCp` (`Frames.cs:1037`), `UnwindToCatchFrame` (`:601`), `SetHeapTop` (`Activation.cs:466`), `MakeBigIntProduct` rollbacks (`Terms.cs:68,74,78`), the guard-stack restore (`Tier1.cs:1197`), the region restore (`Tier1.cs:1680`), the wasm sync (`Wasm.cs:484`), and the collector's compaction (`Engine.HeapGc.cs:717`, where the reclaimed count is already computed). The two allocation primitives and the seven IL emit sites then drop the RMW entirely. The value stays bit-identical to today's counter (every allocation raises `_heapTop`, every lowering is accounted), so `--alloc` A/B history remains comparable.

**Expected gain.** Small but universal: ~1 instruction of 3 per allocation; I'd expect 1–3 % on allocation-heavy shapes (nreverse/qsort/serialize) at both tiers, and a measurable IL-size reduction for regions. Measure with `--alloc` (must be exactly unchanged) and wall time.

**Risk.** Missing one lowering site would under-count; the grep above is exhaustive for `_heapTop =`/`-=`, and a unit test can assert `CellsAllocated` equality against the old formula over the Van Roy set. No invariant involved.

**Novelty check.** `git log -i --grep` "cellsAllocated"/"CellsAllocated": 0 hits; the field's own comment says it was kept because it "cancels exactly when comparing two builds" — true, but it still costs in absolute terms, and the derived form cancels just as exactly.

### 4. The unify-run fusion re-reads `_reservedWrite`, `_writeMode` and `_unifyPointer` from the engine on every opcode of the run

**Where.** `BytecodeInterpreter.Runs.cs:102-346` (`RunUnifySequence`): each case does `_engine.ReservedWrite` (`:113,139,167,201,236,260,284,323`), `_engine.UnifyPointer` (`:119,145,173,207,242,266,290,329`), `_engine.WriteMode` (`:120,146,174,208,243,267,291,330`) and `_engine.SetUnifyPointer(ptr + 1)` (`:131,159,193,227,252,276,300,335`) — three field loads and one field store per operand, for a mode that is invariant across the run (it only changes inside `UnifyStructure`/`UnifyList`, `:304-318`, and the ADR-020 reserved cascade).

**Proposal.** Hoist `bool write = _engine._writeMode, reserved = _engine._reservedWrite; int ptr = _engine._unifyPointer;` before the loop (the fields are public for exactly this purpose, ADR-058), keep `ptr` in the local, write `_engine._unifyPointer = ptr` once at run exit and before/after the two nested-compound ops and any reserved-mode helper (which mutate it). The write-mode arm can go further: a run of k write-mode scalar `unify_*` ops is k consecutive `AllocateHeap(1)`; with the mode hoisted, the loop can bump `_heapTop` once per op without the per-op capacity check after a single `EnsureHeapCapacity(runLength)` — the run length is computable by a lookahead over the fixed-width opcodes (the same lookahead the Y-run fusions already do).

**Expected gain.** List/structure-heavy T0 (nreverse, flatten, serialize, Blint's term walks): 2–5 % — the run is already the dominant fused path there (the Runs.cs header cites 945 K/782 K/666 K pairs on Blint).

**Risk.** Keeping `ptr` local is only valid while no helper in the run touches `_unifyPointer`: `UnifyHeapWithCell`, `Bind`, `SetRegister`, `AllocateHeap*` do not; `UnifyStructure`/`UnifyList`/`UnifyArgCell` (reserved) do — reload after them. Attribute wakeups never run inside a run (goal boundary only, ADR-049).

**Novelty check.** The fusion itself is the audit's "chunk-415 run fusion" (its Tier-B gap — Y forms and `unify_structure` not chaining — is fixed: `:135,197,311`). `git log -i --grep` "hoist", "UnifyPointer", "unify run": the only hits are unrelated (`733d82a` wake-queue test hoisting; `5645f66`/`1ba58be` images). Not explored.

### 5. Every dynamic-predicate call pays a ConditionalWeakTable probe and three Dictionary probes before the chain runs (ADR-041 selection)

**Where.** `BytecodeInterpreter.cs:1519-1533` (`EnterDynamic` invokes `_engine.DynChainSelect(e, pc)` per call) → `PrologEngine.Query.cs:1966` (closure) → `DynamicCodePatcher.SelectDynChainCandidate` (`:2040-2135`): `GetChainTable(engine)` is a `ConditionalWeakTable<Activation, DynChainTable>.TryGetValue` (`:1980-1984`), then `predsByAddr.TryGetValue(trampolinePc)` (`:2046`), then `table.Chains.TryGetValue(fid)` (`:2049`), then for the keyed path `DynFirstArgKey.OfCall` + `state.NoCandidate` + `state.TrySoleCandidate` — two more probes of `_byKey` (`:2582,2571`), plus three opcode sanity reads of the program (`:2091-2098`) that are constant per trampoline.

**What it costs.** Dynamic predicates are Tier-0's primary workload by invariant. A CWT lookup is a hashed probe over a dependent-handle table (noticeably slower than a `Dictionary` probe); together ~4–6 hash probes per call — comparable to or larger than the cost of the try_me_else/check_visible chain step the selection is meant to shortcut, for the common "selection declines" outcome (unbound A1, multi-candidate: `:2117-2127`).

**Proposal.** Resolve once per activation, not per call: the `DynChainSelect` closure is created at query setup (`Query.cs:1966`), so capture the `DynChainTable` there (or store it in a field on `Activation`, like `DbGenerationBox` replaced the `Func<long>` provider — the audit's item that fixed `EnterDynamic`'s first delegate cost). Merge `TrampolineFids` + `Chains` + the per-trampoline shape checks into one `Dictionary<int /*trampolinePc*/, DynChainState>` (or an `int[]`-indexed side table keyed by the dense trampoline index the linker already knows), so the per-call path is one probe + the A1 deref. Cache the "shape ok" verdict (`:2091-2098`) on the state object and invalidate on the in-place patches that change the chain head (all funnel through `DynamicCodePatcher`).

**Expected gain.** Assert/retract-heavy programs (Blint-class, Logtalk send cache): 2–6 % of total, more where dynamic calls dominate. Measure with the Blint self-lint and `SHUMWAY_DYNSEL_DIAG=1` tallies to confirm verdict distribution is unchanged.

**Risk.** The selection's correctness contract (ADR-041: determinism independent of JIT hotness, LUV via check_visible at the jump target) is untouched; only the lookup path changes. Crosses into `Shumway.Embedding` — coordinate with the owner of that scope.

**Novelty check.** `git log -i --grep "DynChainSelect"`: 0; the audit's dynamic-path items were the `Func<long>` generation provider (fixed, `GenerationBox`) and the `TryInlineCheckVisible` peel (present). The selection hook post-dates the audit (ADR-041, phase 36) and has not had a cost pass.

### 6. Read-mode `unify_value_x/y` and `get_value_x/y` bypass the atomic fast path that `UnifyHeapWithCell` already has

**Where.** Interpreter read-mode `UnifyValueX` (`BytecodeInterpreter.cs:2083`, `Runs.cs:189`) → `UnifyRegisterWithHeapAt` (`Activation.UnifyOps.cs:123-124`) → `UnifyCells(_registers[regIdx], Cell.Ref(heapIdx))` (`Activation.Terms.cs:1752-1796`); `UnifyValueY` → `UnifyPermanentWithHeapAt` (`UnifyOps.cs:141-145`) likewise; `GetValueX/Y` (`BytecodeInterpreter.cs:2332,2345`) → `UnifyRegisters`/`UnifyPermanentWithRegister` → `UnifyCells`. `UnifyCells` is the general funnel: `OccursMode` test, two `ResolveOperand` tuple returns (`:1803-1811`), the address-equality test, the AttVar/Pstr test, two Ref tests, a tag compare and a `switch` — and it is not inlined. By contrast `UnifyHeapWithCell` (`UnifyOps.cs:150-199`) decides "register holds Atom/Int vs heap cell unbound-or-same-atomic" in four instructions, and the Tier-1 fused helper `GetListValXVarX` (`:676`) routes through it.

**Proposal.** In `UnifyRegisterWithHeapAt` / `UnifyPermanentWithHeapAt`: `Cell r = …; if (r.Tag is Tag.Atom or Tag.Int) return UnifyHeapWithCell(heapIdx, r);` — `UnifyHeapWithCell`'s fallthrough is exactly `UnifyCells(value, Cell.Ref(heapIdx))`, so semantics are identical by construction. A second fast shape worth adding to the same two entry points: register holds a `Ref` whose deref is an unbound var and the heap cell is bound atomic/compound → `Bind` directly (that is `TryUnifyLeaf`'s logic, `Terms.cs:1921-1946`, which already exists for compound arguments but is not reachable from the register entry points). The `get_value` pair (`UnifyRegisters`, `UnifyPermanentWithRegister`) gets the same leaf test before falling into `UnifyCells`.

**Expected gain.** 1–3 % on head-matching-heavy T0 (DCG/parser clauses with `[a|T]`-style tails, Blint's token matching). Trivial to land; zero risk (pure fast-path in front of an unchanged general path).

**Novelty check.** The audit's "verified non-findings" cover `Cell` factory inlining and the chunk-353 `GetList`/`GetStructure` split; `UnifyHeapWithCell`'s fast path exists (its own comment notes it saves nothing on Van Roy because head literals go through `UnifyRegisterWithCell`) but was never extended to the value-register entry points. `git log -i --grep "UnifyRegisterWithHeapAt"`: 0. Commit `6d19f14` ("get_value unifies its two cells inline") is the Tier-1 emit, not the Tier-0 helper.

### 7. PSTR-versus-cons unification allocates two cells per element and recurses with the depth guard reset — a performance and a stack-safety issue

**Where.** `Activation.Terms.cs:2475-2502` (`UnifyPstrLis`): per element it allocates `headSlot` (1 cell) and a `sliceSlot` virtual header (1 cell), then calls the **public two-argument** `Unify(sliceSlot, lisTailIdx)` (`:2501`), which re-enters `Unify(a, b, 0, null)` (`:1569-1572`) at depth 0 — so the `UnifyRecursionLimit` (`:1642`) never engages, and the C# recursion is three frames per list element. `UnifyPstrPstr` (`:2420-2464`) does the same per chain segment (`:2450,2463`), and `UnifyPstr`'s empty-slice case (`:2401`). No `RecursionGuard.EnsureRoom()` on these paths (grep: none in `Activation.Terms.cs`).

**What it costs.** Unifying a packed text with an equal-length cons list (e.g. a user-built code list from `findall`/`numlist`/DCG output against a `"…"` literal or `atom_codes` result) costs 2 n heap cells plus n³ frames of C# stack; the phase-41 closure records the project's policy that deep walks must come off the C# stack ("deep walks off the C# stack, linear catch/cleanup/retract scans"), and this walk was missed — a long enough list (hundreds of thousands of elements on a 1 MB stack) is an uncatchable `StackOverflow`.

**Proposal.** Rewrite `UnifyPstrLis` as a loop that walks the cons spine and the packed buffer in lockstep: for each position, deref the list head; if unbound, `Bind` it to `PstrHeadCell(kind, cp)` (no slot needed — the cell is atomic); if bound atomic, compare `Data`; if attvar, fall back to the existing slot path for that element only. Continue while the deref'd tail is `Lis`; when the packed text runs out, unify its stored tail with the remaining cons tail through `UnifyCells` (one call); when the cons list runs out first, allocate the one remaining virtual slice (one cell total, as today's final step). This allocates nothing for the ground case, is O(1) in C# stack, and keeps the mixed `UnifyPstrPstr` path as is (segments, not elements). Also worth adding `RecursionGuard.EnsureRoom()` to `UnifyPstrPstr`'s segment recursion (bounded by the lazy-concat chain length, but that is user data too).

**Expected gain.** Niche for Van Roy; real for text-processing programs that mix packed and unpacked lists (ADR-047 says the representation must not be observable, so programs do mix them). The robustness fix is the main value; measure with `length(L, 200000), maplist(=(0'a), L), atom_codes(A, L), atom_codes(A, L2), L = L2`.

**Risk.** None to invariants (ADR-047: "a PSTR is a list" — this makes the two representations unify at the same cost class). Keep the `PstrHeadCodePoint` surrogate handling (`:996-1011`).

**Novelty check.** `git log -i --grep "UnifyPstrLis|pstr.*unif"`: 0; `tests/Shumway.Tests.Embedding/PstrHeadMatchTests.cs` covers head matching (`GetListSlow` → `UnconsPstrToPair`, which is fine: 2 cells per uncons is the lazy representation), not the general-unify path.

### 8. Attributed-variable storage and the wake drain allocate managed objects per variable / per flush

**Where.** `Activation.cs:95` — `_attrStore` is `Dictionary<int, Dictionary<int,int>>`; `AttrCreateRecord` (`Activation.Attrs.cs:37-47`) allocates a fresh inner `Dictionary` per attributed variable (plus its bucket/entry arrays on first `AttrSet`, `:51-58`); `AttrValueAt` (`:126-128`) is two hash probes; `AttrQueueWakeups` (`:158-163`) runs per binding of an attvar — one probe plus a `Dictionary` enumerator. On the drain side: `TakePendingWakeups` allocates a `List` per batch (`Activation.Terms.cs:2855`); `TryWakeInterrupt` builds the batch term on the heap (fine) but first calls that allocating `TakePendingWakeups` (`Activation.Wakeups.cs:49`); the fallback drain allocates `new Cell[regCount]` (`BytecodeInterpreter.MetaCall.cs:215`, and again `:65` for cleanups) and `goalLists` (`:258`) per flush. `_attrTrailLog` is a `List<(int,int,int)>` written per attribute change (`Terms.cs:1298-1314`).

**What it costs.** For clp(fd)/clpz/dif/freeze workloads every constrained variable costs ~200 bytes of managed allocation and every attribute read is two probes; the docs record Shumway losing to Scryer on the most propagation-heavy clp(Z) model (`cross-engine-comparison.md`). The wasm tier already needed a flat image because the Dictionary-of-Dictionary is "managed state a module cannot reach" (`Activation.AttrMirror.cs:3-20`: 12,600 exits in clpr).

**Proposal.** (a) Make the flat open-addressed `(home, module) → value` table that `AttrMirror` already implements **the** store (it is currently a derived copy kept in step by the five writers), with a small per-home row count; the five-writer funnel (`Attrs.cs`) makes this a representation swap behind an unchanged API, and the wasm world then reads the store itself instead of an image. Most attvars carry one module, so a single-row fast path avoids all per-variable allocation. (b) Pool the drain's scratch: `ArrayPool<Cell>` for the register snapshot (the pattern `ReentrantSolveTransparent` already uses, `BytecodeInterpreter.cs:389-396`), a reusable batch list in `WakeQueue` (give `TakePendingWakeups` a `Span`/struct-enumerator form), and reuse `goalLists`.

**Expected gain.** Only on attvar programs; there, plausibly 5–15 % of attribute-path time and a large cut in .NET GC pressure. Uncertain — measure with the clp(Z) suite from `cross-engine-comparison.md` (sendmore/queens via `clpz.pl`) and `dotnet-counters` allocation rate.

**Risk / ADRs.** ADR-052 (the table observes, it does not retain — the GC sweep/relocate funnels `AttrSweepUnmarked`/`AttrRekeyAll`, `Attrs.cs:109-124,211-223`) is a semantic contract over the funnel, unaffected by the representation; `AttrMirrorVerify` becomes redundant. Moderate blast radius (one file's five writers + the mirror file).

**Novelty check.** `git log -i --grep` "flat attr", "attr record", "pooled wake", "wake list", "TakePendingWakeups": 0. The audit's attr item was the unbounded `_attrTrailLog` growth (fixed: truncation at `Terms.cs:1492-1498` and the cut's orphan handling `Frames.cs:1471-1487`).

### 9. `Allocate` writes every Y slot although the conservative GC already tolerates stale slots

**Where.** `Activation.Frames.cs:668-669`: `for (i < numPermanents) _stack[newE + EnvY1Offset + i] = Cell.RawInt(0);` on every environment push. The comment (`:657-667`) explains it as "left uninitialised — tagged RawInt(0), which the heap GC's conservative stack scan skips". But `MarkRoots` (`Engine.HeapGc.cs:1198-1262`) scans the **entire** stack `[0, _stackTop)` conservatively, including dead frames, and its contract (`:1181-1193`, `GcMarkReferents` `:1057-1072`) is that any stale-but-plausible cell can only over-retain, never corrupt or crash; `RelocIndex` (`:1152-1153`) leaves out-of-range indices alone. So the zeroing buys only reduced over-retention for the window between `allocate` and the slot's first write, at the cost of n stores per frame.

**Proposal.** Drop the loop (both tiers' allocate paths, the IL tier's frame emit included if it mirrors this), keeping `EnvNOffset`. If the retention window matters for lazy DCGs, write only the slots the compiler knows are first-written *after* a call (a bitmask operand) — but measure first; the retention is bounded by one clause body.

**Expected gain.** Micro: ≤ 1 % on call-heavy T0 (frames are typically 2–6 permanents, the stores are sequential and cache-hot). Included because it is free and the current comment's justification is not what the GC actually requires.

**Risk.** The debugger displays uninitialised Y slots (`PrologEngine.Debug.cs` reads `GetY`); a stale cell would render as a stale value rather than "uninitialised". Tier-1 regions that read Y slots before writing them would be a compiler bug today already. GC fuzz (`SHUMWAY_GC_STRESS`) is the test that proves it.

**Novelty check.** `git log -i --grep` "Y slot", "RawInt(0)", "uninitialised": one unrelated hit (`9af94c9`). The lazy-allocation change (no heap var per Y) is recorded in the comment; the zeroing itself was never costed.

### 10. Store the functor's arity in the Functor cell — drops `FunctorTable.Lookup` from every compound unify/compare/GC step (needs ADR)

**Where.** `FunctorTable.Lookup` (`FunctorTable.cs:103-116`: volatile array load + bounds check + unpack) is called per compound in `UnifyStr` (`Terms.cs:1900`), `StructuralCompareIterative` (`Activation.cs:658`), `OccursIn`/`SlotOccursInValue` (`Terms.cs:2300,2336`), `CollectUnboundVars` (`:2826`), the GC trace (`Engine.HeapGc.cs:1093-1098`), `TryHop` for sub-argument indexing (`Runs.cs:602`), `UnifyStructure`'s reserved path (`UnifyOps.cs:969`), the wake arity (`BytecodeInterpreter.cs:1001,1106`), and `MaybeCollectHeapSlowAtCall`. A `Functor` cell's payload uses 32 of 60 bits (`Cell.cs:72-74`); arity fits comfortably in the upper 28.

**Proposal.** `Cell.Functor(functorId, arity)` packing `arity << 32 | id`, `Cell.AsFunctorArity`; keep `AsFunctorId` as the low 32 bits (unchanged). Every reader above becomes a field extract from a cell it already holds.

**Expected gain.** Small per site (the dense array is already one cache-hot load), but it removes a dependent load from the compound-unify inner loop and the GC trace; 1–2 % on structure-heavy workloads is my guess. Low confidence.

**Risk / ADR.** This changes the Functor cell's payload interpretation: term images (ADR-063 `TermImage`/`TermImageCodec`), persisted `.shmo`/`.shum` literal pools, the wasm module's functor mirror (`CopyPackedFrom`) and the heap GC's cell rewriting all encode Functor cells → **needs an ADR** (cell-layout detail, `docs/design/cell-layout-detail.md`) and a format version bump. Not a tag change (ADR-002's 4-bit tag space is untouched), but a payload-format change is a "major decision" by the decision policy. Flagged, not recommended ahead of items 1–8.

**Novelty check.** `git log -i --grep` "arity in the functor", "functor cell": 0; the audit's functor item was the ConcurrentDictionary probe (fixed by the dense array).

### 11. The giant `Dispatch` method: exception-handling regions and locals pressure (measure before acting)

**Where.** `BytecodeInterpreter.cs:509-3106` is one ~2,600-line method with 111 cases, two `try/catch` regions inside the switch (`:1238-1249` ExecuteBuiltin, `:2614-2629` CallBuiltin), dozens of `FixedInline` helpers inlined into it (`Deref`, `GetList`, `GetStructure`, `UnifyVariableX`, `UnifyRegisterWithCell`'s callees, `MaybeCollectHeap*`, `NeckCut`, `TryInlineCheckVisible`, the three `Run*` fusions are separate but large), and no `MethodImpl` (tiered + OSR).

**Why it may cost.** RyuJIT's register allocator and tracked-locals limits degrade on very large methods; locals live across an EH region are written through to the stack (EH write-thru) — here `builtinId`/`entry` only, but the EH regions also split the method into more basic blocks; and Dynamic PGO's hot/cold layout is driven by the first calls (ADR-061's "what a promotion costs" lesson applies to the interpreter too: the layout follows whatever program ran during warm-up). None of this is verifiable statically.

**Proposal (measurement first).** (a) Dump `Dispatch`'s Tier-1/OSR codegen (`DOTNET_JitDisasm=Dispatch`, `DOTNET_JitDisasmSummary=1`) and look at spills of `pc`, `codeArr`, `codeLen`, `_engine` across the hot cases; (b) if present, move the two builtin-invocation bodies into `NoInlining` helpers (keeping the loop EH-free), and consider `[MethodImpl(AggressiveOptimization)]` on `Dispatch` only if the OSR'd code is measurably worse than a straight full-opt compile (it loses PGO, so it can go either way); (c) consider splitting cold cases (float/bigint/rational/pstr/meta/debug) into a `DispatchCold(opByte, pc)` call so the hot switch is smaller. Gain unknown until (a) is done — could be 0 or 10 %.

**Novelty check.** `git log -i --grep` "Dispatch method", "OSR", "AggressiveOptimization", "jump table": 0; the audit's opcode-clustering item (Tier A #4) is fixed (`Opcode.cs` is contiguous 0x00–0x6D, 111 values — verified by extracting every `= 0x` literal), which is the only JIT-shape work recorded on the interpreter.

### 12. Micro-items (each ≤ 1 %, all safe)

- `Cell.Int(long)` range-checks (`Cell.cs:88-93`); `PutInteger`/`GetInteger`/`UnifyInteger` pass a 32-bit operand (`BytecodeInterpreter.cs:2187,2372,2451`, `Runs.cs:259`). Add `Cell.Int(int)` (no check) and use it at those sites.
- `UnwindTrails` validates both targets with throwing range checks on every CP restore (`Terms.cs:1369-1372`); `RestoreCommonFromCurrentCp` (`Frames.cs:1035`) passes values it just read from the CP. Add an internal unchecked overload for the CP path.
- `TrimEnv` (`Frames.cs:260-286`) is called on every `Call`/`CallBuiltin` and rewrites `_stack[_e + EnvNOffset]` even when the count did not change; cheap, but a compare-before-store saves a store on the common unchanged case.
- `TryBacktrack` (`Runs.cs:432-555`) resolves the CP's BP and `SetPc`s it, then the loop head re-checks generation/marker/bounds and dispatches `RetryMeElse`/`TrustMe` which read the arity again. A direct call into the retry/trust handler from `TryBacktrack` for the plain-bytecode CP shape would save a dispatch round per backtrack; worth it only on backtracking-dominated shapes (zebra, queens) — measure first.
- `CallBuiltin` reads `BuiltinsRegistry.GetById` (array, fine) but then tests `IsFail`, `IsCall`, `IsDollarCall` and `Debug` twice per call (`BytecodeInterpreter.cs:2526-2574,2606,2631`); a single `Kind` byte switch would shrink the common path. Marginal.

---

## How to measure (common to all items)

- Tier-0: `dotnet run -c Release --project tests/Shumway.Tests.Benchmarks/ -- --vanroy --runs 5` with `SHUMWAY_IL_PROMOTE=0`; interleaved min-of-N is the project's discipline (`docs/benchmarks/analysis.md`). Dynamic path: the Blint self-lint (`docs/history/wam-vs-gprolog-blint.md`).
- Allocation-neutral changes (items 2, 3, 4, 6, 9): `--alloc` must read identically before/after (item 3 by definition; item 7 should *drop*).
- Codegen-level claims (items 1, 2, 4, 11): `DOTNET_JitDisasm=Dispatch` / `RunUnifySequence` with `DOTNET_TieredPGO=1`, compare the hot-case bodies; `dotnet-trace` for the share of `Dispatch` vs helpers.
- Attvar items (8): the clp(Z) suite in `cross-engine-comparison.md`, plus `dotnet-counters` allocation rate.

## Considered but already done / rejected (verified in code, so not proposed)

From `docs/history/efficiency-audit-2026-06.md`, every item touching this scope:
- Tier A #3 **FunctorTable.Lookup ConcurrentDictionary probe** — fixed: dense volatile `long[]` by id (`FunctorTable.cs:16-45,103-116`).
- Tier A #4 **opcode switch in 11 clusters** — fixed: `Opcode.cs` values are contiguous 0x00–0x6D (111 entries); invariant recorded in `invariants.md` ("Opcodes are numbered contiguously").
- Tier B **`GrowIfNeeded` non-inlinable on every push** — fixed: inline capacity compares in `EnsureStackCapacity`/`EnsureBindingTrailCapacity`/`EnsureExtraTrailCapacity` (`Activation.cs:363-383`) and in `AllocateHeap*`.
- Tier B **`GetStructure` hot/cold split; `UnifyLis` recursion** — fixed (`UnifyOps.cs:302-372`; `Terms.cs:1953-1993` iterative spine with guard).
- Tier B **operand reads double-check** — partially fixed (the `ReadI32` peel); the remainder is item 2.
- Tier B **run fusion breaks at `UnifyVariableY`/`UnifyValueY`/`UnifyStructure`** — fixed (`Runs.cs:135,197,311`).
- Tier B **`FlushPendingWakeups`/`MaybeCollectHeap` guard split, `_gcDiagActive`** — fixed (`MetaCall.cs:22-25`, `Engine.HeapGc.cs:338-361`).
- Tier B **loop preamble** — fixed in the I1 `inClause` form (`BytecodeInterpreter.cs:525-724`); item 1 is the next step.
- Tier B **`GetY`/`SetY` not inlinable** — fixed (`Frames.cs:729-734,824-829`, `ThrowNoEnv` hoisted).
- Tier B **`new Engine` per query = 512 KB** — unchanged (`ActivationConfig.InitialHeapSize = 65536`), but it belongs to the embedding/pooling scope (EnginePool exists since phase 27), not this one.
- Tier B **`_attrTrailLog` unbounded** — fixed (`Terms.cs:1492-1498`).
- Tier B **`EnterDynamic` `Func<long>` per call** — fixed (`GenerationBox`, `BytecodeInterpreter.cs:1501-1505`).
- Tier B **GC mark closures, `bool[]` bitmap** — fixed (`Engine.HeapGc.cs:105-112` de-closured, `ulong[]` bits `:23,64-70`).
- Verified non-findings there (Profiler/`[Conditional]` diagnostics zero-cost, `Cell` factories inlined, CP span treatment, meta-route cache, `GetById` array) — re-verified: `Profiler`, `CommitTrace`, `AreaTrace`, `BuiltinTally` are all `[Conditional]`; `PushChoicePoint`/`RestoreCommonFromCurrentCp` use a single `Span` over the control block.

Rejected / settled by ADR, not re-proposed:
- **ADR-026** variable-width choice points (drop ViewGen for static CPs): rejected on a measured ≤ 1 % ceiling; the uniform 11-word frame stays.
- **ADR-021** register allocator (Tier-1), **ADR-032** guard fail-continuation (soft-rejected), **ADR-033** guard continuation stack (opt-in prototype) — Tier-1 scope.
- **Heap GC watermark** was already tuned 256 K → 1 M cells after profiling (`ActivationConfig.cs:31-44`); the adaptive 2×-live re-arm (`Engine.HeapGc.cs:560-569`) is sound; no proposal.
- **Trail compaction on cut**: the watermark (`Frames.cs:1349-1358`) and the I5 no-op fast path (`:1367-1368`) already remove the quadratic cases; ADR-061's quick-cut floor handles the hot-method cut.
- **Catch-frame reclamation** in deterministic loops (`Frames.cs:473-518`) — done.
- **Wake-check density**: compiled code tests the wake queue once per stretch (`733d82a`); Tier-0's per-boundary test is a single field read of an inline struct (`WakeQueue.cs`), nothing to gain.
- **Dynamic `Call` → `Tier1Dispatcher.OnDispatch` dictionary probe per call** (`Tier1DispatcherAdapter.cs:155-166`): already bypassed in steady state by the link-time `CallIl`/`CallBytecode` rewrites and the in-place `OnCalleePromoted` rewrite; the residual cost is warm-up only.
- **Fused cons/struct helpers** (`UnifyOps.cs:589-825`) exist for Tier-1; the interpreter's equivalent is the unify-run (item 4), not another set of opcodes (the project explicitly avoided new opcodes there).
- **`PushChoicePoint` arg copy via loop, not `Span.CopyTo`** — measured choice, kept.
- **`Allocate` zeroing** was *not* found in any prior measurement — hence item 9 rather than this list.
