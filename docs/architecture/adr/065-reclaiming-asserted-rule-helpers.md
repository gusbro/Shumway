# ADR-065: Reclaiming the Helpers of Runtime-Asserted Rules

## Status

Proposed (2026-11-04). Design only; nothing is implemented. Stage 3 below
changes what "permanent" means for a predicate's name atom, which
`docs/architecture/decision-policy.md` names a major decision (the atom GC
strategy, ADR-003); stages 1 and 2 stay inside ADR-015, ADR-025 and ADR-063
and are recorded here so the whole lifetime of a helper is decided in one
place. Follows audit item W5 (`docs/history/perf-audit-2026-11/03-wam-compiler.md`
item 5) and its residual in `findings-during-fixes.md`.

## Context

A rule asserted at run time whose body has a control construct is not
compiled as written. `MetaTransform` lifts each construct into a helper
predicate and the clause calls it: `( C -> T ; E )` and `( A ; B )` become
a two-clause `'$disj_N'`, `\+ G` a `'$neg_N'`, `once/1` and `ignore/1` a
`'$once_N'` / `'$ign_N'`, a cut-barrier `call/1` a `'$cutb_N'`, `catch/3` a
`'$catchgoal_N'` and `'$catchrec_N'`, and the collect loops of
`findall/3`, `bagof/3`, `setof/3` and `forall/2` a disjunction or negation
stamped with the goal's name (`'$findall_N'`, …, ADR-035). `N` comes from
the engine's monotonic `NextMetaHelperId`, so two helpers of one engine
never share a name (phase 33, E12). `ClauseCompiler.InternPredicate`
interns every predicate name `permanent`, helpers included: the engine's
tables are keyed by functor id, and a permanent atom is the simplest way
to keep an id stable.

The assert path (`CompileRuntimeAssertClause`) transforms the rule through
`TransformClauses`, links the helpers into the asserting activation
(`LinkRuntimeAssertHelpers`) and registers each compiled helper in
`_runtimeAssertHelperPreds` (`RegisterLateHelpers`), from which any other
live activation materializes it on demand (`TryMaterializeAssertHelper`,
reached through `Activation.ResolveLateHelper` right before an
existence error). The registry exists because an asserted clause is
visible to every activation through the shared chains of ADR-015, so its
helpers must be reachable from every activation too.

Before the W5 fix every assert minted a fresh helper set: 1000 rounds of
assert and retract of a one-construct rule left +1000 permanent atoms,
+1000 functor entries and +1000 registered helper predicates, never
reclaimed. The fix memoizes a rule's transform per module on its variant
key (`ClausePipeline.VariantKey`, `_ruleTransforms`), so a rule asserted
again takes the helpers its earlier variant took, and growth is bounded by
the number of distinct rules the engine has seen. That is the right bound
for a program that cycles a fixed rule set. It is no bound at all for the
use case this engine names first, a rules engine that derives ever-new
rules from data: one helper set per distinct rule, for the engine's life,
retract or not.

### What one distinct rule keeps, verified on this branch

Per helper (one or two per construct; the test rule of
`AssertHelperReuseTests`, with `->`, `\+`, `once` and `findall`, takes
four to five):

1. **A permanent atom** for the name, and a second one for the bare alias
   when the rule's module mangles (`RegisterLateHelpers` interns
   `name[cut..]` permanent so a `'$catchrec_N'(RecVars)` recovery term, a
   goal as data, resolves). A permanent atom is never collected (ADR-003).
2. **A functor entry** per such atom. The functor table has no removal:
   an entry stays when its name atom dies and serves the atom that takes
   the id next (ADR-003, Functors); for a permanent name it just stays.
3. **The compiled helper** in `_runtimeAssertHelperPreds`: bytecode, call
   sites, switch tables and atom memo, "kept for the engine's life, to be
   linked into any activation". The registry's own comment says it grows
   monotonically.
4. **Its literals, pinned.** `RegisterLateHelpers` calls
   `_literalPools.Pin(p.Bytecode)`: a packed string, a float or a ground
   term image (ADR-063) in a lifted branch is never counted and never
   freed, where the owner clause's own literals are counted and released
   with its chunk.
5. **Its data atoms, claimed.** `RetainCode(CodeAtoms.Of(p).Ids)` with no
   matching `DropCode`: the transient atoms the helper's body names
   (`pos`, `neg`, …) are held by the engine's claimant forever.
6. **The memo entry**: the transformed clause ASTs under the rule's
   variant key. `PruneRuleTransforms` prunes only the keys a consulted
   module's last transform did not take; a runtime-asserted rule "has no
   position and so no key a module's transform takes; its entry stays".
7. **Once promoted, the Tier-1 artifact.** A helper is a static predicate
   under a plain name, so `IlPromotionStore.IsExcludedFromPromotion`
   (which excludes only `__query__` and the `$q` stub helpers) lets it
   promote; the artifact claims its atoms (`InstallDelegate`), and query
   setup deliberately does not evict a delegate on a derivation bump
   because its helper ids "stay resolvable forever through the late-helper
   registry". The resume markers it took are `(fid, cursor)` pairs in a
   process-wide table that is never emptied. The wasm tier takes the same
   filter and bakes the helper's body into its group module.
8. **Under the debugger, all of the above per assert**, not per distinct
   rule: `TransformClauses` bypasses the memo under `EmitDebugInfo` or
   `DebugCodegen`, because each clause keeps its own stop sites and
   variable names.

Not leaked, and the design must keep it so: the helper's bytes in an
activation's buffer and its overlay entry in the `LayeredIntMap` die with
the activation; the asserted clause's own chunk is given back to the free
list, its atoms dropped and its literals released, once no activation
reaches it (`ReleaseUnreached`); `clause/2` and `listing/1` read the
store's clause, so a helper never shows; a bundle's helpers are permanent
by design (`ObserveBundleHelperId`, ADR-003 tier 1) and stay so.

The static region sees the helpers too: the next query setup derives a
live rule's helpers from the store again into the static link
(`_programStamp++` at assert), and the region's layout is append-only
(ADR-015 addendum), so a predicate once laid out keeps its bytes there.
That is ADR-015's existing behaviour for anything a program stops
defining, not specific to helpers, and this ADR does not change it.

### The atom collection is off in a shipped engine

`AtomTable.CollectionEnabled` is false unless the host sets it ("Off until
every holder of atom ids is a claimant"); `PrologEngine` registers its
claimants only when it is on, and nothing under `src/` turns it on. Only
the atom tests do. So today no transient atom is reclaimed either, and a
transient helper name would reclaim nothing by itself. This bounds what
option (a) buys and orders the plan below.

## Options

### (a) Transient helper names, claimed by the code that calls them

An ADR-003 addendum: a predicate's name is permanent for a builtin, a
bundle and a consulted static; the name of a helper the engine mints for
an asserted rule is transient, claimed while code that calls it can run.

- `InternPredicate` interns transient for names the assert path mints.
  A `call` operand is an address with a count, not a functor, so
  `CodeAtoms` does not see the callee's name: the claim comes from the
  clause's call-site list (`CompiledClause.CallSites[].CalleeFunctorId`,
  name atom of) added to the owner chunk's atom unit, and from the
  registry entry for the helper's own recursion and its callees.
- The claim follows the chunk: `NoteCodeUnit` retains, `ReleaseUnreached`
  drops. A retracted rule's chunk holds its atoms until no activation
  reaches it, so an older call under the logical update view keeps its
  helpers alive by construction, in every activation.
- A retire protocol on the atom's death (the sweep's callback): drop the
  registry entry, release what `Pin` pinned (counted instead), drop the
  data-atom claim, drop the memo entry, evict the IL and wasm artifacts
  through the ADR-003 artifact retire protocol (a live resume keeps the
  retired code until released). ADR-003's rule for caches keyed by id
  applies to every fid-keyed table a helper enters: the registry, the
  promotion stores, the process-wide resume-marker pairs (a reused fid with
  a stale pair resolves to a predicate with no delegate: must evict or
  check).

Touches: ADR-003 (tiers, Functors, the cache rule), `invariants.md` Atom
management. Risk: a holder the mark misses lets a live helper name die and
its id name another predicate; the poisoned-id tests of `EngineCodeAtomTests`
and `Tier1ArtifactAtomTests` are the net. Cost: nothing on the hot path;
one more sorted merge per chunk. Effort: medium. **Reclaims nothing while
the collection is off**, and ids reused by other engines' helpers of the
same number already share fids today.

### (b) Do not lift: compile the construct inline in the asserted clause

No helper, so nothing to reclaim. What exists: the ADR-025 inline
if-then-else and disjunction on Tier-0 (`get_level; try_me_else; C; cut; T;
jump; trust_me; E`), behind `EnableInlineIte` (default off) and only on
the consult paths: `CompileRuntimeAssertClause` passes `inlineIte: false`.
The ADR-037 soft cut always inlines, and it already lives in dynamic
chunks (`DynamicCodePatcher.RelocateDispatchSites` relocates a clause's
mid-body dispatch sites), so the mechanics in the dynamic buffer are
proven. Eligibility is strict (`InlineIte.IsEligible`): every arm a plain
conjunction, no cut, no nested control, no meta goal, no variable goal.

The phase-33 verdict that kept the default off (Tier-0 inline wins 3–13 %
of opcodes, Tier-1 promoted loses 17 % on boyer because the region-lowered
helper is CP-free for the det commit) does not reach asserted rules: a
dynamic predicate runs on Tier-0 (invariant), the ADR-023 snapshot
excepted, and stage (b) of ADR-025 emits the inline shape in IL for that
case. So the assert path can take the inline lowering on its own.

Extension: `\+ G` is `( G -> fail ; true )` and `once(G)` / `ignore(G)`
are `( G -> true ; fail )` / `( G -> true ; true )`; when `G` is plain
they are the same shape and inline too. That covers the common
rules-engine body. Left over, still helpers: `catch/3` (its recovery is
resolved by fid), the four collect loops (a goal the debugger must show,
ADR-035), a cut inside an arm, nested control, a variable in goal
position, the `call/N` barrier. Touches ADR-025 (the assert path and the
three new shapes); `clause/2` and `listing/1` unchanged (the store);
user `;`/`->`/`\+` frames are transparent to the debugger already.
Effort: small to medium. Narrows the problem; does not close it.

### (c) Anonymous helpers: a sub-procedure of the owner's chunk

The `call` operand is already an address, so a helper laid out inside its
owner clause's chunk needs no functor to be called, and it would be
compiled, placed, claimed and freed with the chunk: no atom, no functor,
no registry, no memo, and nothing to retire, with the collection on or
off. That is the attraction. What needs a predicate identity today, and
would need a substitute: error context and the existence check, the
debugger's frames (`DebugConstructName` maps the name back to the
construct), the promotion unit of both tiers and the ADR-023 snapshot's
eviction key, the resume-marker pair, the `'$catchrec_N'` recovery
resolved by fid, and the cross-activation materializer (an older
activation could no longer link a helper it never saw: the chunk would
have to carry the helper, which it does). A reserved per-engine id space
outside the functor table is (a) under another name; an address-keyed
identity is ADR-006 and ADR-011 territory, plus multi-entry chunks in
`DynamicCodePatcher`. Effort: high. Rejected unless (a) is blocked for
good (see Rejection below).

### (d) Status quo, bounded and visible

Expose the engine's helper sequence and registry count (a `statistics/2`
key and a host property), warn past a threshold under a flag, and key the
memo per lifted construct rather than per rule (phase-33 W9(d)'s
structural key, scoped to the assert path): two rules sharing
`( X > 0 -> S = pos ; S = neg )` would share the helper, bounding growth
by distinct constructs. Cheapest; reclaims nothing. The diagnostic is
taken into every stage below; the finer key is a measured follow-up.

### (e) Retire the registry entry with the owner's last chunk, keep the name

The registry entry is what weighs (items 3 to 7 above); the name atom and
functor entry are two small table rows. Count, per memo entry, the chunks
of the rule's variants alive in the buffer; when the last is released
(`ReleaseUnreached`, the point at which the engine already proves no
activation reaches the chunk), retire the helper set: drop the registry
entries, release the literals (`AddRefs`/`Release` in place of `Pin`),
drop the data-atom claims, evict the IL and wasm artifacts through the
existing retire protocol, drop the memo entry. The name and functor stay;
a later assert of the same rule mints a fresh number, since the memo is
gone, so no fid-keyed table ever sees two bodies under one id (the `$q`
replay trap). ADR-053's shape, applied to the registry: swept on proven
liveness, never truncated by position. Works with the collection off.
Touches ADR-015 (the registry's lifetime) and ADR-063 (counted literals
in helper code), no invariant. Residual: one atom and one functor entry
(about two hundred bytes) per distinct rule, for the engine's life.
Effort: small to medium.

## Recommendation

**(e) first, (b) second, (a) third and conditional; (c) rejected; (d)'s
diagnostic in every stage.**

(e) removes nearly all of the weight for an ordinary change, with the
release point the engine already computes for the clause's own chunk, so
the logical update view holds for the helpers exactly as it holds for the
clause. (b) then keeps most rules from minting a helper at all, and gains
Tier-0 opcodes on the way. (a) is the coherent end state under ADR-003 and
what the audit asked for, but it is worth taking only with the collection
on in a shipped engine, which is a decision of its own (every holder of an
atom id a claimant) and not this one.

### Plan

0. **Diagnostic.** `statistics(assert_helpers, [Minted, Registered])` and
   `PrologEngine.AssertHelperCount`; a test in the `AssertHelperReuseTests`
   style reads them, and the Embedding gate stays green with the counters
   logged for the suites that assert rules in bulk (Logtalk, lgtunit).
1. **Retire with the chunk (e).** A reference from a memo entry to its
   helper fids and a live-chunk count; `ReleaseUnreached` decrements and
   retires at zero; `RegisterLateHelpers` counts literals instead of
   pinning; the promotion stores take the retire through their eviction
   path with the ADR-003 artifact protocol. Tests, both tiers (threshold 0
   and 1) and the wasm lane:
   - N distinct rules asserted, run and retracted, then a query boundary:
     registry count, image and string pool counts (`Images.Count` after a
     sweep) and `AtomTable.ClaimsOf` a data atom back to baseline; the
     sequence advanced by N helper sets and no more.
   - LUV on both tiers: `AnOlderCallKeepsRunningTheRuleItStartedOn` with a
     *distinct* rule asserted in the middle and a `garbage_collect/0`, and
     with the helper promoted; a suspended outer query that first reaches
     the rule after the inner query retracted it, which must still
     materialize the helper (the registry entry outlives the retract
     until the chunk is released, never shorter).
   - A helper under a scoped module (bare alias) and a `catch/3` recovery
     term retired and re-asserted.
   - `clause/2`, `listing/1` and the debugger (`EmitDebugInfo`) unchanged.
   - Throughput: the assert/retract loop of the interop benchmark
     (`docs/benchmarks/`) within noise, ABBA.
2. **Inline on the assert path (b).** `CompileRuntimeAssertClause` passes
   the inline lowering; `\+`, `once/1` and `ignore/1` over a plain goal
   join `InlineIte.IsEligible`. Tests: the ADR-025 and ADR-037 suites run
   over asserted rules on both tiers, the ADR-023 snapshot of such a rule,
   cut semantics in every arm (`AReassertedRuleBehavesAsItDid`), the
   debugger's step over an inline `\+`, and the helper counters of stage 0
   at zero for the covered shapes.
3. **Transient names (a), only with the collection enabled.** The ADR-003
   addendum, the call-site claim, the retire on atom death, the fid-keyed
   caches under the ADR-003 rule. Tests: `EngineCodeAtomTests` and
   `Tier1ArtifactAtomTests` gain a helper case each, poisoned ids on; a
   helper name reused by another engine's sequence; a stale resume-marker
   pair under a reused fid.

### What would make me reject it

- Stage 1 cannot find a release point that sees every layout: the indexed
  and bucket layouts rebuild wholesale and "what the layout was built with
  goes with the buffer". If the helper's owner cannot be counted there, the
  retire ties to the buffer's life instead; if that is most of the real
  programs, (e) is not worth its bookkeeping and (c) is the design.
- Any LUV test above fails: a helper retired while an activation can still
  reach its owner. No partial credit.
- The assert/retract loop slows measurably: the rules-engine path is hot.
- Stage 3: the collection cannot be enabled in a shipped engine (a holder
  the mark misses that cannot be made a claimant). Then stage 3 is dropped
  and the residual of (e) is the documented bound.

## Consequences

- An engine that asserts ever-new rules stops growing by compiled code,
  literals, claims and artifacts per rule; after stage 1 the residual is
  two table rows per distinct rule, after stage 3 none while the
  collection runs.
- The registry is no longer "for the engine's life": a retire point enters
  `ReleaseUnreached`, and the cross-activation materializer can meet a
  retired fid only through a chunk no activation reaches, which it never
  asks for.
- Helper literals become counted like the clause's own; a sweep can free
  a lifted branch's image, which it could not before.
- Stage 2 changes the bytecode of asserted rules with control constructs
  (fewer calls, one `get_level`/`try_me_else`/`jump` per construct) and
  their Tier-1 form under the ADR-023 snapshot; the debugger sees one
  frame fewer for user `;`, `->`, `\+`, `once` and `ignore`, which it
  renders transparent today.
- Documents amended if accepted: stage 1, ADR-015 (the late-helper
  registry's lifetime) and ADR-063 (helper literals counted, not pinned);
  stage 2, ADR-025 (the assert path and the three new shapes) and the
  `EnableInlineIte` note in `PrologEngine`; stage 3, ADR-003 (tier 1's
  list of names the engine fixes, the Functors paragraph, the cache rule
  for fid-keyed tables), `invariants.md` Atom management (a predicate's
  name atom is permanent only for builtins, bundles and consulted
  statics), and `docs/design/atom-gc-coordination.md`, which still says no
  engine drives a sweep. The quick-reference row in `CLAUDE.md` and the
  W5 residual in `findings-during-fixes.md` point here now.
