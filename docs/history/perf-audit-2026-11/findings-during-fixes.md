# Findings while implementing the 2026-11 audit fixes

Issues met while fixing audit items on branch `perf-audit-2026-11`, left alone
because they are outside the item being fixed. All reproduce on `main`
(7536c3c) unless noted.

## Pre-existing bugs

1. **Logical update view miss under Tier-1 promotion (timing-dependent).**
   ```prolog
   :- dynamic(d/2).
   d(1, a). d(2, b). d(k, c). d([x], d). d(f(1), e). d(_, anything).
   ?- findall(K, (clause(d(K,_), true), (K == 1 -> retract(d(2,b)) ; true)), Ks).
   ```
   Sometimes answers `Ks = [1]` instead of every clause, when preceded by
   certain query sequences (a long probe script of atomic_list_concat /
   clause/2 / predsort goals before it). Reproduced 2/10 runs on `main`,
   2/5 on the branch base; never with `SHUMWAY_IL_PROMOTE=0`, so it is in
   the Tier-1 path, not in `clause/2`'s candidate copy.

2. **Uncatchable crash on a non-atom module qualifier in `clause/2`.**
   `clause(1:q(_), _)` — and the next goal after
   `catch(clause(_:q(_), _), E, true)` — kills the engine with an
   `ArgumentOutOfRangeException` (`extraTarget`) instead of raising a
   type/instantiation error.

3. **`AssertHelperReuseTests` (added on this branch, item W5) were
   order-dependent.** They passed alone and failed 1–3 tests when run together
   with other classes: their permanent-atom growth figure varied run to run
   because other tests intern atoms concurrently in the same process. Fixed
   on this branch: the tests read the engine's own helper-id sequence and
   helper registry, which no other test touches.

## Residual of item W5 (needs an ADR before fixing)

The W5 fix bounds asserted-rule helper growth by the number of *distinct*
rules an engine sees. An engine that keeps asserting ever-new distinct rules
with control constructs still grows by one helper set (permanent atoms,
functors, compiled helper predicates) per distinct rule. Reclaiming them
needs transient predicate-name atoms plus a helper-retire protocol across the
registry, every activation's address map and the IL/wasm artifacts — an
ADR-003 addendum.

## Test environment notes (Linux cloud container, 4 cores)

- .NET SDK from Ubuntu packages: `apt-get install dotnet-sdk-10.0` (10.0.114).
- `Adr036ProxyTests.Launch_RunInTerminalHandshake_ThenDebugs` hangs (no
  terminal); exclude `Adr036ProxyTests`.
- Fail on clean `main` here too: Core
  `DebugSiteTableTests.AFileIsIdentifiedByItsName_NotByThePathItWasReachedThrough`;
  Embedding `QualifiedGoalsInBundlesTests` ×3 (`cells_allocated/1` builtin
  collision), `StreamNewlineTests.Consult_AndRead_AgreeOnACrLfSource`; Wasm
  `WasmPhaseGateTests.TheCompiledCounterStaysAheadOfTier0` (wall clock: the
  desktop wasm world runs at 0.8× Tier-0 in this container) and
  `RelinkMovesPromotedCodeTests.AConsultThatMovesPromotedCode_LeavesItRunnable`
  (its own anti-vacuity check: "the consult no longer moves code").
- Order-dependent / load-sensitive in a full Embedding run (pass alone, on
  base and branch alike): `Tier1ArtifactAtomTests`, `NestedConditionTests`,
  `EngineCodeAtomTests`, `EngineAtomClaimTests`, `TermAtomMemoTests`,
  `Chunk217Tests`, `IlCompileWorkerTests`, `PreludeIlBakeTests`,
  `QuadsSuccessiveOutputsTests`, `RegionMemberEntryTests`,
  `RegionPruneCrossEntryTests`, `BigIntHeapTests`, `CpsHotPathTests`.
