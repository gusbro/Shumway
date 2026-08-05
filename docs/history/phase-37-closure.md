# Phase 37 closure — the documentation-truth, licensing and interop phase

43 commits after `phase-36`; 149 files, +3976 / −6083 — the first phase whose
diff is a net **deletion**. Phase 36 shipped a large ecosystem campaign and left
the documentation describing a project that no longer quite existed: ADRs
written in the future tense about work long since shipped, design docs
describing surfaces that were never built, invariants scattered across a dozen
files, and a repo with no license. This phase made the documentation *true*,
gave the project a license, and then — with the ground solid — closed two real
correctness bugs and benchmarked the interop story the project exists for.

## 1. Documentation structure

`docs/` went from ~60 flat files mixing three audiences to a structure with an
index per audience: `guide/` (user-facing), `history/` (phase closures, audits,
point-in-time records), alongside the existing `architecture/`, `design/` and
`benchmarks/`. New `docs/README.md` and a root `README.md`. The generated
`predicates.md` moved with the guides — generator path, canary test and
doc-comment updated together. The mode-inference spec was archived to
`history/` and lost its `-phase3` name.

Two consolidations came out of it:

- **`docs/architecture/invariants.md`** — the consolidated invariant catalog.
  `CLAUDE.md`'s embedded "Non-Negotiable Invariants" section became a short
  summary plus a reference; the catalog is now the single home, harvested from
  the scattered mentions across design docs and ADRs (including the "chunk 159
  invariant" that had never been written down anywhere: mutation-driven dynamic
  dispatch stays on Tier-0).
- **`docs/architecture/decision-policy.md`** — what counts as a major decision
  is a document, not a paragraph in `CLAUDE.md`.

## 2. The documentation truth pass

Every ADR and design doc was audited against the code. The defect classes that
kept recurring (recorded in the audit memory) were: status/body seams (a
`Status: Proposed` header over a shipped design), deferred items that had in
fact been delivered, stale symbols (`Engine` → `Activation`, renamed opcodes,
drifted opcode ids), the project-owner "the user" voice leaking into design
prose, and internal contradictions.

- **ADRs**: every `Status:` line brought to the truth; ADR-041's title
  normalized; the "v1" framing retired (there are no versions — phases are the
  timeline); since-delivered scope sections stopped reading as current gaps;
  foundational 001–008, 010/011/015/031, 013/014/019/020 and 023–040 each got a
  pass. ADR-012 gained the trust boundary it had always implied: declarations
  restrict, they never license removal.
- **Design docs**: the fictional ones were rewritten to be concise and correct
  (IL family — retired an inline-caching design that was never built;
  encoding/format — opcode-band scheme, cell tags, PSTR packing, bundle format;
  API/atom/debug — bannered the pre-rename and unbuilt surfaces).
- **Guides**: a current-state pass dropped internal round/phase markers, fixed
  the `native-aot` `PublishAot` claim, a broken ADR link, a stale `.shmo`
  version and a `--region-prune` flag that does not exist.
- **`overview.md`**: corrected the `double_quotes` default, PSTR packing, the
  missing rational tag, the embedding surface and scoped modules; gained a phase
  chronology table.
- The standard itself was codified in `invariants.md`: what belongs in an ADR
  versus a reference doc, so the drift has a rule to be measured against.

## 3. Licensing

The repo has a license: **MIT**, with third-party notices and a contributing
guide. The one piece of non-Shumway code in the tree — the Logtalk adapter —
was **rewritten from scratch as Shumway's own code**, its equivalence validated
by a 240-suite sweep against the original. A new `version_data` prolog flag lets
the adapter derive `prolog_version` properly instead of hardcoding it.

## 4. Toolchain

- **`shumway-compile --consult`** — one engine for all inputs, incremental
  (skip by timestamp + dependency mode), emitting **self-contained per-module
  objects** with seeds and operators attributed per module.
- **`shumway-link --consult`** for `.pl` inputs, with a hint when a source needs
  it.
- **`-L dialect:path`** on both, so a link can pull another Prolog system's
  libraries (the ADR-040 dialect mechanism reaching the offline toolchain).
- **`statistics/2`** (`runtime` / `walltime` / `cputime`) — self-timing that is
  portable across engines, which is what made the benchmark work below
  measurable at all.

## 5. Two bundle correctness bugs

Both were the same root cause seen twice: **meta-helper id collisions** between
the offline compiler's per-module numbering and the engine's runtime counter.

- `MetaTransform` synthesises `$disj_N` / `$neg_N` / `$once_N` / `$catchgoal_N`
  helpers. Offline (`ShmoCompiler`) numbers them 0-based per module; at runtime
  `NextMetaHelperId` also starts low. With clpz's ~253 helpers the two ranges
  overlapped, a dynamic clause's re-transform minted the *same* mangled fid as a
  static helper with a different body, and the query-setup partition dropped the
  static one — so **every non-singleton domain narrowing failed** in a compiled
  bundle (`label/1`, `queens`) while the same source worked live. Fixed in
  `LoadBundle` (`ObserveBundleHelperId`), not in the compiler: the collision is
  offline-vs-runtime, and per-module stable numbering is what keeps incremental
  `.shmo` byte-identical.
- Fixing that exposed the second: `-i --exe` / `--stdlib` raised
  `existence_error($disj_NNNN/2)` for dynamic-heavy clpz. The persisted-IL build
  warms an engine (`Query("true.")`), re-transforming dynamic clauses with
  link-time helper numbers that the ADR-023 snapshots then baked in — but those
  helpers were not in `emitOnly`, leaving the snapshots dangling. `CompileEntryToIl`
  now BFSes from each snapshot's call sites and bakes the reachable helpers.

Both were violations of the same invariant, stated by the project owner during
the work and worth recording: **being on Tier-0, JIT Tier-1 or persisted Tier-1
may change efficiency, never behaviour.** A tier-dependent answer is a
correctness bug, not a configuration caveat.

## 6. The interop arc

The project's stated reason to exist — beating a native engine on C# ↔ Prolog
interop — had never been measured. It is now, against **GNU Prolog embedded in
the same C# host via P/Invoke** (a C shim DLL that embeds GProlog, exercising
both directions: host→Prolog `Pl_Query_*` and Prolog→C# `:- foreign` +
reverse-P/Invoke).

- **`PrologEngine.SolveOnce`** — the re-entrant host→Prolog solve that was
  missing. A foreign predicate holding the live `Activation` can call a Prolog
  goal back **on that activation**, reusing the linked program instead of
  building a top-level query: ~630 ns per crossing against `QueryAll`'s ~59 µs
  of per-query setup. Three overloads (lean typed single-output, full
  `Solution`, semidet check); the lean form allocates 282 B against 858 B. The
  subtle part is register transparency — a typed-return bridge reads its output
  register *after* the user method returns, so the nested solve snapshots and
  restores the argument-register bank, per-invocation so nesting stays correct.
- **The honest result**, oracle-verified: the *convenience* API (typed args,
  `FromTerm`/`ToTerm`) loses to P/Invoke marshalling by 3–34× on composites,
  because it decodes through an intermediate Term-AST. The **zero-copy** path —
  a raw `bool(Activation)` foreign walking and building the engine's heap cells
  directly — wins by 3–180×, and list/term traversal is essentially free. A
  native engine embedded over P/Invoke *structurally cannot* match that: its
  cells are unmanaged, so it must copy where Shumway does not.
- Documented in `docs/guide/interop.md` (the four mechanisms, with worked
  zero-copy traverse/build examples) and `docs/benchmarks/cross-engine-comparison.md`
  (Shumway against GNU Prolog, Scryer and SWI across Van Roy, clp(Z), Logtalk
  and now interop). `ZeroCopyInteropTests` pins the claim in the repo:
  correctness oracle plus a deterministic allocation guardrail, gated
  `Category=Slow` for the pre-phase-close run.

## Gate at close

Core 444 / Interpreter 105 / Compiler 364 / ISO 298 / Embedding 3752 /
DialectInterop 9 — all green. The Embedding run is the **full** one
(`test-embedding-parallel.ps1 -Full`, no `Category!=Slow` filter), so the new
`ZeroCopyInteropTests` allocation guardrail is included.

## Deferred

- The `--consult` path does not yet cover baked-C# libraries through separate
  compilation (inherited from ADR-038).
- The convenience interop tier still decodes composites through a Term-AST; a
  direct heap ↔ `List<long>` / `long[]` fast path would close most of the 3–34×
  gap without giving up ergonomics.
- `docs/guide/interop.md` documents the zero-copy mechanism but the engine
  exposes it only as raw cell access; a safer typed cursor over the live heap
  (the C# counterpart of the ADR-024 reftype tier) is unbuilt.
