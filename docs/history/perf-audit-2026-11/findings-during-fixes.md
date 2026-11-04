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
   Re-run with the full probe script (`repro/luv-probe.pl`, run as `shumway luv-probe.pl -g "main, halt"`) (each goal wrapped in
   `catch(G, _, fail) -> ...`), 10 runs each: `main` 7536c3c 3/10 wrong;
   538b202 (index commit, before the catch-frame fix 25ea400) 0/10;
   branch head 0/10. So the catch-frame fix is not what hides it; the most
   likely change is 34b0fbb (clause/2 through indexes), which rebuilt the
   path the failing goal takes. Not yet known whether 34b0fbb fixes the
   cause or only shifts the timing.
   **It only shifts the timing: the bug is still there.** With
   `SHUMWAY_IL_PROMOTE=1000000` (user code effectively never promoted, the
   prelude's baked IL still running) the probe script answers `[1]` 6/6 on
   `main` and 4/6 on the branch head; `SHUMWAY_IL_PROMOTE=1` answers
   correctly 6/6 on both, and `=0` (everything on Tier-0) is always correct.
   So the failing mix is Tier-0 user code with Tier-1 library code, not
   promotion itself. Both clause/2 versions copy the candidate list before
   enumerating with the same `IndexEnumCursor`, so the likely loss is the
   cursor's choice point after the `retract`, not the candidate snapshot.
   **Deterministic synthetic repro: `repro/luv-min.pl`** — answers `[1]`
   every run on both `main` and the branch with
   `SHUMWAY_IL_PROMOTE=1000000 shumway luv-min.pl -g "main, halt"`, and the
   correct `[1,2,k,[x],f(1),_]` with `=0` or `=1`. Bisection found that it
   needs all of: one long straight-line clause body of `show/1` calls,
   where `show(G) :- ( catch(G, E, (write(caught(E)), nl, fail)) ->
   write(yes(G)), nl ; write(no(G)), nl )` (the plain
   `( catch(G, _, fail) -> true ; true )` does NOT reproduce); about 33
   `findall` enumerations over `clause/2`; about 33 caught exceptions
   (`show(clause(_, _))`); a `d(_, anything)` clause with a variable first
   argument; and the failing `findall` itself inside `show/1`. A recursive
   loop of the same calls does not reproduce it. The quantity matters more
   than the exact goals (any one block removed → correct), which suggests a
   threshold-triggered event in the catch-frame, trail-compaction or
   cursor-choice-point machinery; not yet traced.

2. **Uncatchable crash on a non-atom module qualifier in `clause/2`.**
   `clause(1:q(_), _)` — and the next goal after
   `catch(clause(_:q(_), _), E, true)` — kills the engine with an
   `ArgumentOutOfRangeException` (`extraTarget`) instead of raising a
   type/instantiation error.

   **Fixed on this branch** (commit "Core: a catch frame given back late
   leaves the trail where a later mark points"). Not a `clause/2` bug: the
   probe ran the goals through `show(G) :- ( catch(G, _, fail) -> ... )`,
   and any catch whose goal leaves a choice point, cut by `->`, followed by
   any catch whose goal fails or throws, crashed the same way on both
   tiers (`t5 :- ( catch(q(_), _, fail) -> true ; true ),
   ( catch(fail, _, fail) -> true ; true ).`). `ReclaimDeadCatchFrames`
   popped the dead frame's two trail records off the extra trail while a
   choice point pushed since (at an address the cut had freed) or a CP-free
   guard (ADR-031, marks in IL locals) held a mark above them; their
   restore then unwound to a position past the trail top. The records are
   now neutered in place and dropped by the next compaction (the watermark
   is lowered to them); only a frame's own records go at its deterministic
   exit. The qualified `clause/2` also checks every qualifier layer and the
   head/body as `call/1` and the unqualified form do (`a:1:q(_)` →
   `type_error(atom, 1)`, `user:_` → `instantiation_error`,
   `user:q(_), 1` → `type_error(callable, 1)`), where it peeled or failed
   quietly before. Pinned by `CatchFrameLifetimeTests` and
   `QualifiedReflectionTests`. Seen on the way, not fixed: `retract/1` and
   `assertz/1` report `type_error(atom, _)` with an unbound culprit for
   `1:q(_)` (a C# path of its own); a `$`-helper's name reaches error
   contexts (`$clause_enum/2`, `$module_clause_enum/3`).

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

Proposed as [ADR-065](../../architecture/adr/065-reclaiming-asserted-rule-helpers.md)
(design only): what one distinct rule keeps today, verified item by item
(the registry entry, its pinned literals and claimed data atoms, the memo,
the promoted artifacts, and per assert rather than per rule under the
debugger), four options plus a fifth, and a staged plan — retire the
registry entry with the owner's last chunk first (no ADR-003 change), inline
`\+`/`once`/`ignore`/if-then-else on the assert path second, transient
helper names third and only once the atom collection runs in a shipped
engine (`AtomTable.CollectionEnabled` is off outside the tests).

## Test environment notes (Linux cloud container, 4 cores)

- .NET SDK from Ubuntu packages: `apt-get install dotnet-sdk-10.0` (10.0.114).
- `Adr036ProxyTests.Launch_RunInTerminalHandshake_ThenDebugs` hangs (no
  terminal); exclude `Adr036ProxyTests`.
- Fail on the branch base (25ea400) too, under the diag build the Wasm suite
  runs with (seen while gating item I1, lever on and off alike, and on a
  worktree at the base): `TierBudgetTests.TheTierSpendsExactlyThis` ×3 (the
  tier's entries 4 → 3 for the two `length(_, N)` goals and 1604 → 1603 for
  the findall one, deopts and foreign exits unchanged — one re-entry fewer
  per goal) and
  `WasmRelinkEvictionTests.AConsultAfterALibraryLoadCompactsAndTheTierTranslates`
  (its line 181). Whether `main` shows them was not checked.
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
  `RegionPruneCrossEntryTests`, `BigIntHeapTests`, `CpsHotPathTests`,
  `SubArgIndexingTests`, `BucketIndexingTests`, `Adr029FusionTests`,
  `IlInlineTableTests`, `QuadsInputTests`, `Adr037IndexedIlTests` ("must
  bake to IL in a persisted bundle"). Two mechanisms seen in the logs: a
  plain `dotnet test` run lets the exclusive classes run beside everyone
  else, so `BundlePromotionPolicyTests`' static
  `IlPromotionStore.DefaultPersistedThreshold` reaches every bundle-IL engine
  built meanwhile ("p/2 must be Tier-1 IL" in a cluster), and the atom tests'
  process-wide collection with poisoned ids kills atoms other engines name
  ("Atom id N is not registered"). The script gate runs the exclusive
  classes in their own pass and sees neither.
