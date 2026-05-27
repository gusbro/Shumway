# Phase 17 — Closure

**Status**: complete.

**Tagged**: `phase-17` (this commit).

Phase 17 makes persisted Tier-1 IL bundles **cross-process correct**.
Before Phase 17 the persisted IL baked each functor / atom id as an
inline `ldc.i4` constant, and those ids drifted between processes —
the `shumway-link` LINK process accumulates `AtomTable` / `FunctorTable`
interns the `shumway` REPL doesn't, so the integer in the IL pointed
at the wrong functor at runtime. Symptom on Blint: persisted-IL bundle
loaded fine, ran ~10× faster than Tier-0, produced a wrong answer
(`type_error: evaluable` or `instantiation_error` depending on what
the misresolved id happened to land on).

## Why not just stabilise the ids?

The first instinct was to make the global atom/functor tables drift-
proof — either reset them in the linker before the IL emit sub-engine
runs, or have both processes intern in lockstep. That works *for the
moment* but is fragile: it requires the linker and the REPL to execute
exactly the same operations in exactly the same order, an invariant
maintained by hand. Any future code that interns an atom in one path
and not the other silently breaks every bundle that ships.

## The PE-patch approach

Persisted IL is name-relative, not id-relative:

1. **Emit-time** (`IlPredicateCompiler`): the per-emit batch runs in
   "persist mode". Every functor-id, atom-id, and resume-marker
   constant the emit pipeline would have written as `ldc.i4 <buildId>`
   gets a unique sentinel int from a reserved range
   (`0x7E000000+`) and an `IlPatchSite` recording the build-process
   `(name, arity)` plus, for resume markers, the cursor. Sentinels are
   always emitted via the 5-byte `ldc.i4` long form (so the patch
   target is always 4 bytes wide).

2. **Post-Save** (`PersistedIlBuilder.LocatePatchSites`): after
   `PersistedAssemblyBuilder.Save` writes the PE, the locator scans
   every method body's IL stream restricted to `ldc.i4` operand
   positions (preceding byte == `0x20`) looking for each sentinel.
   Each must occur exactly once; duplicates or no-shows fail the
   build loudly. The sentinel's absolute byte offset within the PE
   goes back into the corresponding patch site.

3. **Bundle V3** carries the patch table and a per-method `(slot,
   name, arity, methodName)` entries table alongside the .dll bytes.
   See `BundleFormat`.

4. **Load-time** (`PrologEngine.LoadBundle` →
   `ApplyIlPatches`): for each patch site, intern `(name, arity)` in
   the current process, compute the runtime value (atom id / functor
   id / `Engine.EncodeResumeMarker(runtimeFid, cursor)`), and
   overwrite the four bytes at the recorded offset on a copy of the
   .dll bytes. Then `Assembly.Load(patchedBytes)` — the JIT compiles
   the IL from the patched bytes, so every formerly-sentinel
   `ldc.i4` is a runtime-correct inline constant.

   The per-method entries table additionally lets LoadBundle register
   each delegate under the *runtime* functor id rather than the
   build-time id baked into the method name. Without this the
   dispatcher's `ResolveByFunctorId(runtimeFid)` would miss and
   silently fall back to Tier-0 — bundles would still execute
   correctly because the bundled bytecode is name-encoded, but the
   persisted IL would be dead weight, defeating the purpose.

## Cost

Zero per-dispatch overhead. The JIT sees a normal `ldc.i4
<runtimeId>` immediate — same machine code it would have generated
for an in-process compile.

LoadBundle pays:

- One PE byte scan per bundle to verify sentinel offsets (already
  paid at build time, not at load).
- One pass through the patch table to write runtime values
  (~hundreds of entries for Blint-scale bundles, microseconds).
- One pass through the entries table to intern names and register
  delegates (already happening pre-Phase 17, just with a different
  key source).

## Chunks

- **193** — `IlPatchSite` / `IlPersistedEntry` types + codecs. The
  on-disk side channels each Phase-17 site needs.

- **194** — `IlPredicateCompiler` persist-mode emit helpers
  (`EmitAtomId` / `EmitFunctorId` / `EmitResumeMarker`). Routes
  every functor / atom / resume-marker emit through a sentinel +
  patch-site record when in persist mode; runtime
  `DynamicMethod` path is unchanged.

- **195** — `PersistedIlBuilder.LocatePatchSites`: post-Save PE
  scan that maps sentinels to absolute byte offsets and verifies
  each occurs exactly once.

- **196** — Bundle V3 format with per-entry IL patch table +
  entries table. `BundleWriter` / `BundleReader` /
  `ShmoLinker.SerialiseBundle` emit / parse them; `BundleEntry`
  carries the new fields.

- **197** — `PrologEngine.LoadBundle.ApplyIlPatches` + runtime-fid
  delegate registration. Patches bytes before `Assembly.Load`;
  registers delegates under runtime functor ids using the entries
  table.

- **198** — Test harness: `PePatchPrototype` (the PE-patch
  feasibility check), `PersistedIlBlintProbe` (in-process repro of
  the retractall divergence), and `PePatchEndToEnd` (six tests
  exercising single + multi-dynamic, nested compound, and
  retractall through both in-process and cross-process paths).

- **199** — Closure summary + tag.

## Out of scope (deferred)

A pre-existing IL emit bug surfaces when a predicate body has
**2+ assertz of one dynamic predicate + another `:- dynamic`
declaration**: `retractall` on the first dynamic raises
`instantiation_error` inside `retract`. Reproduces independently of
Phase 17 (with the runtime DynamicMethod path too); the patcher
hands the runtime correct constants — the IL itself is wrong for
that shape. Tracked separately for a Phase 18 fix.

The five Sigil-verifier "Unreachable code detected" predicates
(`$prelude$$listing_all/1` etc.) that
`PersistedIlBuilder.Build`'s per-pred try/catch already skips are
the same set the runtime DynamicMethod path would reject; they
fall back to Tier-0. Out of Phase 17 scope.
