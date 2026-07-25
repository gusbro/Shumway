# Phase 35 — ISO conformance (Neumerkel) + soft cut (ADR-037) + module-local meta-calls + REPL polish

**Status: complete** (tagged `phase-35`). 61 commits over `phase-34..phase-35`.
Four threads, all driven by real conformance/compatibility gaps rather than a
pre-planned feature list.

## 1. ISO reader/writer conformance — Ulrich Neumerkel's conformity suites

Drove conformance from Neumerkel's conformity-testing pages (syntax, number_chars,
variable_names, dif), keeping the test DATA out of the repo (no license) but the
fixes in. End scores: **number_chars 67/67, variable_names 63/63, dif 26/26,
syntax 201/202** — the single divergence is `#106` (a quoted operator-atom as an
operand, deliberately left because supporting it would break the prelude's operator
rendering). Work landed:

- **Writer (`writeq`/`write_term`):** token-adjacency (insert a space where a tight
  infix/postfix operator would fuse with its operand on re-read), list-element and
  numeric/operator-operand parenthesisation (incl. a prefix operator applied to an
  equal-priority operator operand — `- (X^2)`), quoted-atom escaping, `0'''`,
  `variable_names` option validation with last-option-wins semantics.
- **Reader:** ISO §6.3.1.3 (a bare operator-atom cannot be an operator's operand,
  except an `op/N` indicator), radix literals lowercase-only (`0x`/`0o`/`0b`, reject
  `0X`/`0O`/`0B`), the line-continuation escape (backslash before a newline elided),
  raw control characters rejected in quoted atoms and `0'` literals.
- **`number_chars/2` + `number_codes/2`:** ISO §8.16.8 direction (read the chars as a
  TERM that must be a number) via a term-reader fallback, full number syntax
  (post-sign layout `- /**/1`, partial-list `instantiation_error`, cyclic-argument
  bound so it errors rather than OOMs), `-0.0`/`0.0` unify.
- Directives: an unrecognised directive runs as a goal (ISO §7.4.2) instead of being
  dropped silently. Coroutining Tier 1+2 (`when/2`, `?=/2`, `unifiable/3`) and a
  `dif/2` refinement (retire superseded suspensions, project each constraint once).

## 2. ADR-037 — soft cut (`*->/2`), end to end

`*->` was parsed and recognised by every analysis but never lowered to execution
(`existence_error(*->/2)`). Implemented against GNU Prolog's `pl2wam` shape (verified
by disassembly — `soft_cut(y(0))` captured *after* the `try_me_else`, vs `cut` for
`->`):

- **`soft_cut` opcode** + `Activation.SoftCut`: neutralises the ELSE choice point,
  keeping the condition's CPs (its non-determinism) intact. Discards the ELSE CP
  when it is the top (deterministic condition — so `time(true)` is determinate),
  marks the middle case dead otherwise. Tier-0 (dead-`BP` sentinel) and **Tier-1 IL**
  (`SoftCutToLevel`; the ELSE IL choice point neutralised by swapping its resume
  delegate to a fail-delegate).
- **Inline lowering** for the eligible `( Cond *-> Then ; Else )`, forced on
  regardless of the inline-ITE flag; indexed `*->`/`->` promote (runtime + persisted).
- **Non-eligible `*->`** (cut in a branch, nested control, standalone, runtime-built):
  a synthesized soft-cut helper (`'$choice_level'(K), Cond, '$soft_cut'(K), Then` /
  `Else`), `HasTransparentBranchCut`/`ReplaceTransparentCuts` descending into `*->`,
  standalone → `( … ; fail )`, the runtime `'$call_disj'` `*->` clause + a bare-`*->`
  route to `'$call_softarrow'`.
- **`time/1`** uses `( call(Goal) *-> report ; report, fail )` — deterministic now.

Fell out along the way: a crash from a cut-transparent `!` nested in a `->`/`;`
branch (`ReplaceTransparentCuts` not descending into a nested `;`/`->` dropped the
host barrier); a **latent `->` bug** — a runtime-built `( true -> a ; b )` ran BOTH
branches because `DistributeMqual` (the module tag for variable meta-calls) wrapped a
`;`'s `->`/`*->` left-arg whole, hiding it from `$call_disj`'s if-then-else match —
fixed by distributing the module INTO the construct (`WrapGoal`, interpreter + IL).

## 3. Module-local meta-calls in linked bundles

A module-local predicate meta-called by name in a `--exe`/linked bundle raised
`existence_error`. Fixed with module-relative resolution: variable meta-goals are
tagged `'$mqual'(Module, Goal)` (`ModuleRewrite`), unwrapped at the meta-dispatch
sites (interpreter + IL); `findall`/`bagof`/`setof` variable-goal fallbacks run in
the LIVE engine (prelude clauses) instead of a sub-engine that lacked the bundle's
precompiled definitions. A two-module local `pepe/2` collision was fixed too.

## 4. REPL polish

- The top-level answer starts on a fresh line when a goal left the cursor mid-line,
  tracked on the output column (correct under redirection too, SWI-style); `time/1`'s
  report follows the same rule.
- **Default Tier-1 IL auto-promotion at threshold 32** (`SHUMWAY_IL_PROMOTE=N`
  overrides; `N <= 0` disables) — interactive / `--goal` runs get compiled code for
  hot predicates with no flag.

## Verification / gate at close

Full five-project gate: **Core 444 · Interpreter 105 · Compiler 360 · ISO 298 ·
Embedding 3424** (the 4 initial Embedding cross-process failures were a stale Release
REPL — green after `-c Release` rebuild). Zero compiler warnings.

A closing ISO §7.8/§8 audit (this phase) confirmed **every ISO control construct and
built-in predicate executes** — `*->` was the only "parses but doesn't run" gap, now
closed. (A `|/2`-as-goal-disjunction suspicion was checked against `pl2wam` and
retracted: GNU Prolog also compiles it to a call to `|/2`; `|`-as-alternative is
DCG-only, already handled.)
