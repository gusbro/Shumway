# Efficiency audit — 2026-06 (4-agent sweep, post-phase-29)

Verified-by-reading findings, ranked. Each item: location, cost, fix. The
detailed per-subsystem reports were produced in-session; this is the
consolidated working list. Tier-A = attack first.

## Tier A

1. **Tier-1 IL: cursor dispatch is a LINEAR compare chain in the three
   dominant multi-clause shapes.** `IlPredicateCompiler.cs:4268` (indexed),
   `:4578` (try-me-else chain), `:4859` (indexed-atom — which tests cursor 0
   LAST, so the fresh-call path is the worst case). Every Tier-1 call and
   every backtrack re-entry pays it. The chunk-363 O(1) `emit.Switch` lesson
   was applied to single-clause (`:2467`) and regions (`:1337`) but never to
   these. Fix: jump tables, same pattern. Also hoist the
   `IndexedDelegateHolder.Get` ConcurrentDictionary probe out of CP-push
   sites (`:5008`; regions already CSE it at `:1279`), and consider a dense
   array instead of the ConcurrentDictionary.

2. **Assert path: 3 full literal-pool `ToArray()` snapshots per assertz**
   (`PrologEngine.cs:6199` → `LiteralPool.Snapshot()`), plus a full 4-pass
   ClausePipeline + fresh ModuleRewrite.Context/HashSet/ClauseCompiler/
   BytecodeEmitter per assert (`:6131-6155`). This is the measured 24µs /
   4.3KB per assert. Fix: skip the pool refresh when pool counts didn't
   change; fact fast-path that bypasses DCG/Meta/Phrase; reuse compiler
   objects per engine.

3. **`FunctorTable.Lookup` = ConcurrentDictionary probe on the unify hot
   path + GC mark** (`Engine.cs:3011/3153/3185/3713`, `Engine.HeapGc.cs:201`).
   Functor ids are dense. Fix: volatile grow-copy-on-write `(int atomId,
   int arity)[]` by id — the `AtomTable._permanentByIdArray` precedent.

4. **Interpreter: the opcode switch is 11 disjoint clusters → cluster
   search, not one jump table** (`BytecodeInterpreter.cs:361`; opcode map in
   `Opcode.cs`). ~28M dispatches/run. Fix: renumber opcodes contiguously
   (legal pre-release) or fill the gaps with explicit throw cases so Roslyn
   emits one dense 256-way table. Measure with deterministic counters +
   interleaved wall.

5. **Per-QUERY setup re-transforms everything**: `SetupQueryFromTerm` re-runs
   MetaWrapperUnfold + 4-pass pipeline + ModuleRewrite over EVERY module
   (prelude included) each query (`PrologEngine.cs:5401-5459`), rescans
   cached predicates' bytecode for literal operands per query
   (`ModuleCompiler.cs:137`), rebuilds merged address/switch maps and the
   bare-alias loop with two Substring allocs per functor (`:5550/5726/5794`).
   Fix: cache per consult-generation; store `IsPoolFree` on
   CompiledPredicate; cache merged maps alongside `_staticLink`.

6. **retract + Materializer string/pinning issues**: RetractStep copies the
   remaining-candidates tail + closure per successful retract even when a
   cut kills the CP (`MetaBuiltins.cs:3189`); Materializer re-interns every
   atom BY STRING with `permanent: true` (`Materializer.cs:39,116`) —
   defeating the three-tier atom GC for anything transiting a meta-builtin
   (quiet memory-class issue, not just speed); DefiniteMismatch re-interns
   per trial (`MetaBuiltins.cs:3284`). Fix: lazily-cached AtomId/FunctorId
   on AtomTerm/CompoundTerm; permanent:false; generation-guarded lazy
   snapshot.

## Tier B

- Capacity checks via non-inlinable `GrowIfNeeded` on every trail/CP/env
  push (`Engine.cs:3599`); inline fast compare + cold grow.
- `GetStructure` lacks the chunk-353 hot/cold split (`Engine.cs:1146`);
  `UnifyLis` recurses per list element (`:3024`) — loop the tail (also
  stack-overflow robustness); same for `AreLisStructurallyEqual`.
- Operand reads re-test `Overflow` + double bounds-check per read
  (`BytecodeIO.cs:26`); peel via `codeArr` in hot handlers (worst:
  `TryInlineCheckVisible` ReadInt64 ×2 per chain entry).
- Chunk-415 run fusion breaks at `UnifyVariableY`/`UnifyValueY`/
  `UnifyStructure` (not in the run switch, cases don't chain).
- `FlushPendingWakeups` (12 sites) and `MaybeCollectHeap` need
  AggressiveInlining guard + NoInlining slow body; fold the GC diag knobs
  into one `_gcDiagActive` bool.
- Loop preamble: 3 abnormal-case branches + per-tick ProgramGeneration
  probe could fold into one unsigned compare + cold disambiguator.
- Bundle-build costs: `TryDescribe*` recomputed ~8×/predicate (memoize per
  CompiledPredicate); `RegionRootSelector` fixpoint rebuilds all regions
  per iteration (cache `IsRegionMemberEligible`, recompute only affected);
  `FindCallSiteFunctorId` linear scan per opcode → dict/binary search;
  builtin classification via name strings during emit → precomputed
  `IsBacktrackable` flag on BuiltinEntry.
- `Engine.GetY/SetY` not inlinable (inline throw + no attribute) — called
  from all emitted Y-slot IL.
- findall: heap→AST→heap round trip per solution (`MetaBuiltins.cs:3498`,
  TermReader/Materializer) → snapshot to scratch Cell[] instead.
- `new Engine` per query = ~512KB LOH heap alloc (`EngineConfig` 65536
  cells) — pool/reset for many-small-queries embedding workloads.
- Transient `AtomTable.Intern` double string hash (`AtomTable.cs:173`).
- `_attrTrailLog` grows unboundedly under clpfd labeling (`Engine.cs:70`);
  truncate on unwind.
- `EnterDynamic` invokes `Func<long>` DbGenerationProvider per dynamic call
  (`BytecodeInterpreter.cs:752`) → direct field/holder.
- `append/3` det path allocates a List<Cell> per call
  (`AtomListBuiltins.cs:94`) → two-pass spine walk.
- Heap GC mark phase: closure delegate per register/stack slot
  (`Engine.HeapGc.cs:163`); `bool[]` mark bitmap vs ulong[] bits.
- `DiagnoseRegion` not `[Conditional]` — env probe per Compile
  (`IlPredicateCompiler.cs:2147`) — policy violation of chunk 414.
- AssertImpl extracts the head fid twice via string intern
  (`MetaBuiltins.cs:3026`).

## Verified non-findings
Profiler/[Conditional] diags zero-cost; chunk-234 CP span treatment, Cell
factory inlining, chunk-416 meta route cache, nb_* atom-id keys, GetById
array read, char-I/O single-char atom cache: all already optimal.
