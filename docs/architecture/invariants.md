# Shumway invariants

The consolidated catalog of the project's non-negotiable invariants. Each entry
states the rule, why it exists, and where it is enforced. **If a change requires
breaking one of these, stop and write/amend an ADR before proceeding.**

Scattered restatements of these rules in other docs are informative; THIS file
is the authority. ADR references give the full rationale.

## Memory and concurrency

- **`Shumway.Core.Activation` is the per-query WAM machine** — heap, stacks,
  trails, registers, choice points — born at every `SetupQueryFromTerm`, alive
  exactly as long as its solution enumeration. The durable instance (dynamic
  store, code space, consult history) is `Shumway.Embedding.PrologEngine`.
  Several activations may coexist over one engine (a suspended `QueryAll` plus
  a nested query).
- **Activations are single-threaded internally.** No locks inside activation
  state; the caller serializes access.
- **Activations are thread-agile.** No `[ThreadStatic]` engine state; an
  activation may hop threads as long as access is serialized.
- **Global tables (atom table, functor table, code cache) are thread-safe**
  (`ConcurrentDictionary` / fine-grained locks) — engines share them.
  (ADR-001.)
- **The heap is a `Cell[]` of 8-byte blittable values. Never put managed
  object references inside cells.** Managed payloads (BigInteger, string,
  foreign object, rational) live in per-activation side tables, reached by an
  integer id in the cell. Enforced by the `Cell` type itself (a struct over a
  `ulong`). (ADR-002.)
- **Cells are 8 bytes: 4-bit tag + payload.** Do not change this layout.
  (ADR-002; field-level detail in `../design/cell-layout-detail.md`.)

## Heap well-formedness

Not checked in the hot path; debug assertions and the test suites keep them
honest (`../design/cell-layout-detail.md` §Validation rules):

- REF cells point within the heap; an unbound REF points to itself.
- STR cells point at FUNCTOR cells; FUNCTOR cells are reached only via STR.
- LIS cells point at a head cell with the tail adjacent.
- FLOAT header cells pair with an adjacent INT payload cell.
- CP and environment control words are tagged `RawInt` so the conservative GC
  scan can never mistake them for heap references. (ADR-016.)

## Atom management

- **Atoms have global integer ids; atom comparison is int comparison.**
- **Three tiers**: Permanent (eternal strong refs), Transient (strong refs in
  the table, swept by the custom GC), TransientWeak (kept alive only by C#
  retention via `WeakReference`). (ADR-003.)
- **The atom GC runs at safe points only** — the hot path just writes the id.
- **Atom ids are stable for the atom's lifetime**, across tier promotion.

## Modules and visibility

- **Each Prolog source file is one module.** Predicates are local by default;
  `:- public foo/N` exports to the flat global namespace. (ADR-008.)
- **`:- module(Name, [Exports])` (2-arg) is the sole trigger for scoped
  qualification** — all its predicates mangle `Name$x`; resolution is
  local → imports → bare-global, identically at compile time and runtime.
  (ADR-038.)
- **Static predicates are immutable once compiled.** `assertz`/`retract` on a
  static predicate is an error.
- **Dynamic predicates** are declared with `:- dynamic foo/N` or auto-promoted
  on first assert when the `implicit_dynamic` flag is true (the default);
  auto-promotion never applies over existing static clauses or builtins.
- **Public predicates are globally unique** (`ValidatePublicUniqueness`); two
  modules cannot both export the same bare-global `foo/N`. Export-qualified
  (`:- module/2`) modules coexist by construction.

## Bytecode

- **Opcode 0x00 is reserved as Invalid** — hitting it during dispatch means
  corruption; fail loudly.
- **Opcodes are numbered contiguously** so the interpreter's switch compiles to
  one dense jump table; new opcodes go at the end of the dense block; never
  cite numeric opcode values in docs (`Opcode.cs` is the truth). (ADR-006.)
- **All dispatched opcodes use fixed-size encoding** with operands as unaligned
  ints, sizes from the per-opcode table.

## Trails and backtracking

- **Two separate trails**: `BindingTrail` (int[], the hot path) and
  `ExtraTrail` (struct[], other reversible state — attribute changes, BigInt
  allocations, rational allocations). (ADR-004.)
- **HB check**: bindings to variables younger than the newest choice point are
  not trailed.
- **Young-to-old**: unifying two unbound variables binds the younger (higher
  heap index) to the older.
- **`assertz`/`retract` and global-state changes are NOT trailed** — they are
  permanent. Consequence: **exploring more clauses than ISO mandates is a
  correctness bug, not a cosmetic one** — extra backtracking re-runs side
  effects.
- **Trail compaction must preserve live `AttrModify`/`BigIntAlloc` entries**
  (a dropped attribute-restore entry corrupts constraint stores on backtrack).

## Logical update view

- A call to a dynamic predicate runs against the database **as of when its
  goal began**: choice points carry `ViewGen`; clauses carry born/died
  generations checked per clause (`check_visible`). Mid-query
  `assertz`/`retract` is visible to LATER goals of the same query, never to
  the in-flight call. (ADR-015.)

## Compilation tiers

- **Tier 0 (bytecode interpreter) is always available** and is the only tier
  under Native AOT.
- **Dynamic predicates execute on Tier 0, always.** Their mutation-driven
  in-place dispatch (patchable chains, JIT index tables) cannot be observed by
  a cached IL delegate. Enforced at promotion: a predicate whose bytecode
  opens with `enter_dynamic` is permanently excluded
  (`IlPromotionStore.IsExcludedByLayout`). The ONE sanctioned exception is
  ADR-023's snapshot model: a STATIC-style IL snapshot of a dynamic predicate
  may run only under eviction-on-mutation plus clause-entry staleness tests
  (ADR-034); anything else must decline to Tier 0. (Historically "the
  chunk-159 invariant".)
- **Compiled IL is engine-agnostic**: it takes the activation as a parameter;
  the code cache is shared across engines. Persisted IL is name-relative
  (sentinel ids patched at load). (ADR-011, Phase 17.)
- **Promotion swaps are atomic**; a stale delegate must never be reachable
  after eviction (`EvictionStamp`, self-guarding snapshots).

## Region compilation (Tier-1 IL regions)

(`../design/il-region-compilation.md`.)

- **Dynamic predicates are never region members** (see the tier invariant
  above) — cross-region calls reach them through a trampoline.
- **Public predicates are never region members** (callable from anywhere);
  trampoline likewise.
- **Resume-cursor accounting must be exact**: every continuation a region
  member can suspend at has exactly one cursor, and the method-top dispatch
  must cover all of them — a missed cursor is silent solution loss.
- **Regions are bounded by the .NET 64 KB method-body limit**; a member that
  would exceed the budget stays outside (the region is just smaller).

## Garbage collection (heap)

- **The collector is an order-preserving sliding mark-compact** — relative
  heap order is what the young-to-old rule and the HB check rely on.
- The stack scan is conservative but must be **precise enough that tabling
  and attributed variables stay sound**: env Y-slots, CP-protected slots,
  query vars, global vars, debugger-held roots (`MarkHeapRoots` /
  `RelocateHeapRoots` seams) are all roots. (ADR-016.)

## Debugger

- **`DebugWire.FormatVersion` moves engine and debugger together**: the wire
  definition is one file compiled into both sides; a version bump obligates a
  VSIX/extension release in the same change. A mismatched reader must refuse,
  never guess.
- **Debug codegen may add information, never behavior**: code compiled with
  debug metadata and no armed breakpoints executes exactly the instructions
  release code would (Break bytes are patched in by the debugger, not
  emitted). (ADR-035.)

## File formats

- **No `.shmo`/`.shum`/codec version bumps or compat readers before the first
  official release** — pre-release format changes rewrite the format in
  place and rebuild the tools.
- The two `.shum` writers (linker and librarian) must stay byte-identical in
  the sections they share.

## Build

- **Zero warnings** — enforced by `<TreatWarningsAsErrors>` in the root
  `Directory.Build.props`. A genuinely unavoidable case is silenced narrowly
  (targeted pragma with a comment), never by relaxing the invariant.

## Documentation

- **ADRs are decision records; design docs, guides and the architecture
  overview are reference for how the system works now — and the two are held to
  different standards.** An ADR (`architecture/adr/`) may keep a superseded or
  never-built design in place, clearly marked (a "superseded"/"not built"
  banner pointing at what shipped), because its value is the record of the
  decision and its evolution. A reference doc (`design/`, `guide/`,
  `architecture/overview.md`) must instead **state what is true now**: it is
  verified against the code, carries no banner over incorrect content, and when
  a mechanism changes it is rewritten to describe the real one — concise,
  pointing at the authoritative source — rather than annotated as stale. A
  reference doc that describes a mechanism that was renamed or never built is a
  defect to fix, not to caveat.
