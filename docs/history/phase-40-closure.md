# Phase 40 — version 1.0, the ecosystem campaigns, and bounded memory

**Closed 2026-08-24**, tagged `phase-40`. 82 commits, 516 files,
+29,290/−7,366 since `phase-39`, merged to main as three PRs: the web-debug
arc (#2), the module-conformity mega-branch (#3), and the heap-GC arc (#4);
the branches are kept on origin as the detailed history.

The phase's question: is the engine **1.0** — text as first-class data, a
module system that answers qualified questions, third-party constraint
solvers running certified from their own trees, and memory that stays
bounded when a program runs long? The answer to each is yes, and each
"yes" was earned against an external oracle: the Neumerkel conformity
suites, Logtalk's ISO battery, Trealla's test corpus, Markus Triska's
solvers, and a 4 MB lazy parse.

The design records are ADR-044 through ADR-047; the campaign state lives in
the guide's per-engine support pages.

---

## Version 1.0.0 and the web debugger (PR #2, branch `web-debug`)

The version number became real: `1.0.0` reported everywhere, generator
stamps in emitted artifacts, and the first user-visible conventions frozen —
[ADR-044](../architecture/adr/044-canonical-path-separator.md) (canonical
`/` path separator) and
[ADR-045](../architecture/adr/045-text-stream-newline-translation.md)
(CR-LF → `\n` on text reads only), both following GNU. `'$stream'(Id)`
stream terms, `ensure_loaded/1`, `time_out/3`, `http_download/2`, and
`open/4`'s `encoding(...)` option landed alongside.

**WebShumway gained debug mode**: the full debugger core — conditional
breakpoints, port stepping, the real call stack with residual constraints,
Set Next Statement — behind an embedded web frontend with dockable panes,
running in the same static-site wasm build. The REPL grew a
sentence-at-a-time top level with shared `user_input` type-ahead.

**ISO conformance went to the full Neumerkel suites**: syntax 365/365
(Codex reads 362, GNU 360), number_chars 67/67, variable_names 63/63,
dif 26/26 — self-hosted in `tests/conformity/` with a multi-engine runner.

**The Logtalk campaign closed at 100%**: the whole structurally supported
library set green on the swi dialect — no patches to the Logtalk tree —
and the engine capabilities it forced (predicate_property meta templates,
`unicode_property/2`, shell de-dup, a BMP truncation fix) stayed.

## The module-conformity mega-branch (PR #3, 74 commits)

**A packed string is a list** —
[ADR-047](../architecture/adr/047-packed-string-is-a-list.md). The PSTR
representation stopped being a stranded literal-only optimization and
became *the* text representation: `is_list` true, `==`/`compare/3` against
cons element-wise, both chars and codes packed (a presentation bit travels
with the data), default `double_quotes = chars`, producers pack
(`atom_chars` of 4,000 characters: 8,002 → 1,341 cells), and
`phrase_from_file/2,3` parses lazily through freeze-chunked partial
strings. `Tag.String` is gone; `term_cells/2` answers what text costs.

**Modules answer qualified questions.** `assert`/`retract`/`retractall`/
`abolish`/`clause`/`predicate_property`/`current_predicate`/`listing` all
accept `M:` forms; consult-direct bare calls reach an explicit module's
locals; module-scoped operator tables
([ADR-046](../architecture/adr/046-module-scoped-operators.md)) serve
`op(...)` export lists. Logtalk's `tests/prolog` ISO battery went
2,796 passed / 499 failed → **3,219 / 70** across three rounds (§7.6.2
body conversion, format directives, output-list checks).

**The Trealla campaign** — their `tests/` corpus with `.expected` oracles,
loaded from their tree under a `trealla` dialect pack: core 85 → **102/111**,
issues 83 → **139/168**, issues-OLD **49/54** (cold-run 48), slow **1/1**,
the remainder classified (their VM's tasks/sockets, the quads test
framework, message formats, accepted divergences). The campaign's engine
yield: strict UTF-8 text streams (`representation_error(character)`, peek
does not consume), the attvar-preserving blackboard (`bb_get` residualizes
via `copy_term/3`), `\=`/2 running attvar hooks (dif can veto, freeze
fires), DCG rules and in-file `goal_expansion` reaching dynamic
predicates, rational-tree `unify_with_occurs_check`, `length(L,L)`
failing instead of looping, UTF-8 console output, and the compat-name
policy: dialect names live in shims — `limit/2` → `call_with_limit/2`,
`offset/2` → `call_with_offset/2`, `load_text/2` → `consult_text/1` —
while Arity's `ifthen/2`/`ifthenelse/3` (chunk 268's old debt) joined the
engine with `arity_compat` parse leniency so Blint consults again.

**The Triska solvers certified**: Trealla's real `clpz.pl` and `clpb.pl` —
their code strictly as specification, all fixes engine-side — serve from
the mounted tree. The atts hProlog bridge, expansion re-walk of control
constructs, and the `$prelude$$` catch-helper bare-alias fix got them
there; SEND+MORE=MONEY, `#<==>` reification, labeling min/max, and
Triska's CLP(B) consistency test (`slow/test0360`) all pass. That last
one found a real engine bug: the nested-driver failure path truncated
catch frames whose trail records were still live — frames are now
deactivated trailed, one shared driver for both tiers.

**SSU aligned with SWI** (the only mainstream Prolog implementing `=>`):
single-sided head matching through `'$ssu_match'` (a pattern never binds a
caller variable) and `existence_error(matching_rule, Goal)` when no rule
applies.

## Bounded memory (PR #4, branch `heap-gc-stack-roots`)

The heap GC had already learned to run with attributed variables live
(previously one resolved `dif/2` disabled collection for the rest of the
consult). The remaining goal — bounded memory on long deterministic runs —
was reached, and the long-suspected culprit (conservative stack scanning
needing a precise frame-liveness walk) turned out to be wrong. A new
root-attribution diagnostic (`'$heap_root_diag'`) charged retained cells
to individual roots and found two concrete bugs: **orphaned attr-trail-log
records** (a cut's trail compaction dropped young `AttrModify` entries but
left their side-log records GC-rooted — one orphan per lazy chunk retained
a parse's entire consumed input) and **dead choice points under
LCO-reused frames** (a `try_me_else` CP discarded by the clause's cut
leaked its slots below the frame forever; `Deallocate` now reclaims down
to max(E-chain top, B-chain top), the WAM invariant).

Result, lazy DCG over 4.1 MB / 120k lines: mid-parse live cells
814k → ~1k, control stack 900,067 → 68 slots, end state 18 cells —
identical under pure Tier-0 and default Tier-1, and ~14% *faster* on a
framed Tier-0 loop (hot frame-reuse locality). The conservative root scan
stays: with no dead regions it matches the precise walk in practice,
without the fragility that reverted phase 20's attempt. Validated by the
five-project gate, 230 tests under `SHUMWAY_GC_STRESS=1`, and the Trealla
sweep with zero regressions.

---

## The gate at close

Embedding 4,096 · ISO conformance 416 · Core 457 · Interpreter 102 ·
Compiler 371 — zero failures, zero warnings. CI green on all three lanes
(net10, net48-x64, net48-x86). External oracles: Neumerkel 365/365 + 67 +
63 + 26, Logtalk battery 3,219/70, Logtalk libraries 100%, Trealla
102/111 + 139/168 + 49/54 + 1/1, Triska solvers 8/8.
