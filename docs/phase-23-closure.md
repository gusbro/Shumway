# Phase 23 — Closure

**Status**: complete.

**Tagged**: `phase-23`.

Phase 23 polishes the REPL — the day-to-day surface a developer
sits in front of when interactively driving Shumway. The phase
opened scoped to four chunks (249–252) but expanded once each
landed exposed something else worth fixing while the surface was
still fresh; closes at chunk 262.

| # | Chunk | What it adds |
|---|---|---|
| 249 | REPL line editor + persistent history | Cursor editing, arrow-key history, `~/.shumway_history` |
| 250 | Tab completion for predicate names | Completes builtins + user predicates; multi-column listing |
| 251 | Error display + source positions | Clean error message + Prolog stack trace with `file:line:col` |
| 252 | Pretty-print bindings on overflow | Long compounds / lists break across lines with indentation |
| 253 | Line-editor horizontal scroll | No more mid-token wrap on a query wider than the terminal |
| 254 | `listing/1` preserves source variable names | Walks AST clauses instead of round-tripping through the heap |
| 255 | `listing/1` for source-stripped bundles | Shows the bytecode-only signature with an explanatory comment |
| 256 | Listing diagnostics + local-predicate demangle | `no predicate matches X` / `X/N not defined`; strips `<module>$` prefix |
| 257 | `portray_clause/1,2` + use in listing | SWI/SICStus head + indented body printing |
| 258 | `portray_clause` width-aware multi-line layout | `,`-chains always break; args align past open paren |
| 259 | Delete `shumway-bundler` (obsolete) | Compile + link path supersedes it; removes ~847 lines |
| 260 | `shumway-link` short flags + complete help | `-E`/`-u`/`-i`/`-c`/`-f`; help lists `--with-compiled-il` and `--foreign-dll` |
| 261 | Zero out compilation warnings | Clean build at 0 warnings (was ~196); source-generator nullable polish |
| 262 | `use_module/1` + REPL residual constraint display | `use_module(library(clpfd))`; `?- A #> 5, A #< 10.` prints `A in 6..9.` |

Before / after a typical session:

```
# Before
?- L = [aaaaa_one, bbbbb_two, ccccc_three, ddddd_four, eeeee_five, fffff_six, ggggg_seven].
                                                                              ^ wraps mid-token
?- 1/0 is X.
% PrologRuntimeException: evaluation_error: zero_divisor

# After
?- L = [
      aaaaa_one,
      bbbbb_two,
      ccccc_three,
      ddddd_four,
      eeeee_five,
      fffff_six,
      ggggg_seven
    ].
?- 1/0 is X.
% error: evaluation_error(zero_divisor) in is/2

# Plus arrow-key history, Tab completion, Ctrl-A/E/U/K editing.
```

Phase opened with a different plan — "engine robustness" focused
on the `retract/1` Blint bug recorded in memory. Verification at
the start of the phase found the bug **no longer reproduces**
(verified 2026-05-31): Blint linting itself runs cleanly in 3.6s,
no `type_error(character, next_char_i(a))`, no crash. The fix
landed incidentally somewhere across the chunks 235-248
foreign-predicate work. Memory was updated to record the
resolution. With the obvious correctness target gone, the phase
pivoted to REPL UX.

## Chunk 249 — line editor + persistent history

The REPL used plain `Console.ReadLine` — no cursor editing, no
in-session history, no recall across sessions. Replaces it with
a custom `Console.ReadKey`-based line editor plus a persistent
`HistoryStore` at `~/.shumway_history` (override via
`SHUMWAY_HISTORY`).

Keybindings: Left/Right, Home/End/Ctrl-A/Ctrl-E, Backspace,
Delete, Up/Down (with in-progress draft preserved when stepping
back into it), Ctrl-U / Ctrl-K (kill to start / end), Ctrl-D
(EOF), Enter.

Detects `Console.IsInputRedirected` and falls back to plain
`ReadLine` for scripted / piped invocations — the chunk-215
PePatchEndToEnd cross-process test keeps working unchanged.

HistoryStore: load / append / cap (1000 entries default),
consecutive-duplicate dedup, best-effort I/O (history loss is
non-fatal).

10 unit tests cover the HistoryStore (the testable half). The
interactive line editor itself depends on a real terminal and
relies on manual smoke testing.

## Chunk 250 — tab completion

Tab on a partial atom completes against the union of every
registered builtin and every user predicate the engine knows
about (each module's clauses, `PublicFunctors`, `DynamicFunctors`,
and `PrecompiledStaticPredicates` from any loaded bundle).

Three behaviours:

- Unique match → replaces the prefix.
- Multiple matches → extends to the longest common prefix, lists
  the alternatives below in multi-column format sized to
  `Console.WindowWidth`.
- No match → silent (no terminal bell).

Word boundary: walks back from the cursor while the character is
identifier-class (`[a-zA-Z0-9_]`), so Tab in `assertz(asse|`
sees just `asse` — the preceding `(` is a boundary.

Capped to 200 results so a Tab on an empty / very short prefix
doesn't dump the entire builtins universe.

8 tests cover the pure helpers (`FindWordStart`,
`LongestCommonPrefix`).

## Chunk 251 — error display + stack traces

`PrintError` distinguishes three exception families:

- `ShumwayPrologException` (user `throw/1`) — renders the
  carried term cleanly: `% error: <term>`. No more
  `Prolog throw/1:` prefix.
- `PrologRuntimeException` (ISO-shaped) — composes
  `kind(detail)` + builtin context:
  `% error: evaluation_error(zero_divisor) in is/2`.
- Anything else (parse errors, internal bugs) — type name +
  message.

Both Prolog families surface the engine's
`LastErrorStackTraceWithPositions` (chunk 144+), filtering out
synthetic `$`-prefixed helpers:

```
%   at bar/1 (rules.pl:34:5)
%   at main/0
```

`SHUMWAY_DEBUG_TRACE=1` still adds the .NET stack on top — useful
when an engine bug surfaces as an
`InvalidOperationException` deep in the interpreter.

`ErrorRendering` is a separate public static class for
testability; 5 tests cover `FormatRuntimeError`.

## Chunk 252 — pretty-print bindings

`Solution.ToString(int width)` overload that breaks long
compounds and lists across lines with indented arguments.
`Solution.ToString()` (no arg) keeps compact single-line for
embedding-API consumers that log to a file.

Algorithm: try the compact `Render` first; if it fits in
`width - indent` columns, emit as-is. Otherwise break the term —
compounds open with `(\n` and indent two past the opening, lists
open with `[\n`. Recursive; an outer compound that broke can
contain inner compounds that stay compact (and vice versa).

Operator compounds (`a + b`, `foo:bar`) stay compact even when
they don't fit — breaking at the operator is notation-fragile.
Only compounds and lists break.

Narrow-budget fallback: when the indent has eaten so much of the
width that breaking won't help (< 16 columns of room), the
printer emits compact regardless.

REPL uses `Console.WindowWidth` with an 80-column fallback for
piped environments. 9 tests cover the pretty-print path.

## Chunk 253 — line-editor horizontal scroll

Chunk 249's editor stopped at one terminal-line worth of input —
a long query past the right edge wrapped at the wrong place and
re-rendered garbled. Chunk 253 tracks a horizontal scroll offset
on the visible window, scrolls left/right as the cursor moves
past either edge, and only re-renders the visible slice. Long
DCG/CLP queries now edit cleanly without wrap glitches.

## Chunk 254 — `listing/1` preserves source variable names

`listing(foo)` was printing `foo(_G1004, _G1005)` because the
clause body was being round-tripped through the heap via
`clause/2`, which loses the parser's variable names. Chunk 254
adds `'$listing_pred_source'/1` that walks the AST clauses
directly — `head :- body.` prints with the names the user wrote.

## Chunk 255 — listing for source-stripped bundles

A release `.shum` (built with `--strip`) has no source clauses,
so `listing/1` had nothing to walk. Chunk 255 falls back to
`PrecompiledStaticPredicates`: prints the head signature plus an
explanatory comment `% (compiled — source stripped)`. The user's
guidance was explicit: "si tenés el fuente disponible usalo, pero
si no tenés el fuente disponible algo tenés que mostrar".

## Chunk 256 — listing diagnostics + local-predicate demangle

Two correctness fixes the user surfaced trying the REPL:

- `listing(pepe).` on an undefined predicate printed `true.`
  instead of telling the user the predicate doesn't exist. Now
  prints `no predicate matches pepe` (no `/N` given) or
  `pepe/N not defined`.
- `listing(main).` on a local predicate (no `:- public main/0`
  directive) found nothing because `ModuleRewrite` had stored it
  as `user$main`. `listing/1` now demangles the `<module>$` prefix
  for display and lookup so the user's `main` matches `user$main`.

## Chunk 257 — `portray_clause/1,2` + use in listing

`portray_clause(Clause)` prints the SWI/SICStus way: head on its
own line; rule body as indented goals one per line. `listing`
now routes every clause through `portray_clause/2` so its output
matches what user code calling `portray_clause` directly would
produce.

## Chunk 258 — `portray_clause` width-aware multi-line layout

The chunk-257 layout was simple "always one goal per line" — fine
for short bodies, ugly for ones like
`catch((a, b, c), _, findall(z, (g, h, i), X))`. Chunk 258
rewrites it as a true pretty-printer: a `,`-chain anywhere in the
body breaks across lines; compound arguments align their args
past the opening paren; sub-bodies recurse with deeper indents.
Width-aware: a compact rendering that fits stays on one line.

## Chunk 259 — delete `shumway-bundler` (obsolete)

`shumway-bundler` predated the `.shmo → .shum` separate-compilation
toolchain. Its `--with-bytecode` path failed on DCG (no
`DcgTransform` application), and "compile + link" via the
new tools covered every other case. ~847 lines of code deleted —
the entire `Shumway.Bundler` project, the embedding-API `Bundler`
helper, `BundleConfig` / `BundleResult`, and `Chunk72Tests`. No
test regressions because the bundler had no current callers.

## Chunk 260 — `shumway-link` short flags + complete help

Every `--xxx` long flag in `shumway-link` gained a `-x` short
form: `-o`, `-E` (entry), `-u` (allow-undefined), `-m` (map),
`-s` (strip), `-x` (exe), `-i` (with-compiled-il), `-c`
(self-contained), `-f` (foreign-dll). Help text rewritten to
list `--with-compiled-il` and `--foreign-dll` (which were
implemented but undocumented).

## Chunk 261 — zero out compilation warnings

Clean build at 0 warnings / 0 errors (was ~196: 82 CS8600 + 66
CS8602 + 22 xUnit2013 + 16 CS8604 + 2 CA2264). Two main strands:

- Source generator (`PrologPredicateGenerator`):
  `(PrologEngine)engine.Host!` and `call!.GetEnumerator()` —
  removes ~38 CS86xx warnings across every generated
  `[PrologPredicate]` bridge.
- Test files (~50 sites): `(IntTerm)s["X"]` casts gain `!`
  suffix; `Assert.Equal(1, x.Count())` / `.Length` switched to
  `Assert.Single(x)` to silence xUnit2013.

Engine fixes: `_persistentProgram` / `engine.CurrentProgram`
dereferences in the dynamic-chain mutation paths,
`ArgumentNullException.ThrowIfNull` on a struct in
`BytecodeInterpreter.Backtrack` (CA2264), one null-forgiving in
`IlPredicateCompiler`'s `resumeLabels` path.

## Chunk 262 — `use_module/1` + REPL residual constraint display

Two related top-level features. The first is a SWI-style Prolog-
level library loader; the second uses it to render attribute-
constrained answers like a human-readable Prolog top-level.

**`use_module/1` builtin.** `use_module(library(clpfd))` /
`use_module(library(clpr))` consult the library that
`PrologEngine` previously only exposed through the
`engine.UseClpfd()` / `engine.UseClpr()` embedding API. With an
atom argument, behaves like `consult/1`. Unknown library raises
`existence_error(library, _)`.

**Residual constraint display.** The top-level wraps each query
with `copy_term/3` over its named variables to collect residual
attribute goals. An unground answer like `?- A #> 5, A #< 10.`
now prints `A in 6..9.` instead of leaving a bare unbound
variable. Copy variables in the projected goals are renamed back
to the originals; a variable with residuals replaces its
(uninformative) `X = _G123` line with the residual goals,
matching SWI / SICStus convention.

Plumbing required two new public surfaces:

- `PrologEngine.ParseGoal(string)` — returns the parsed `Term`
  and its named variables, so the REPL can construct the wrap.
- `PrologEngine.Operators` — exposes the runtime operator table
  so `AstTermRenderer.Render(Term, int, OperatorTable)` can
  render library-defined operators (`in`, `..`, `#=`, `#<`, ...)
  in operator form instead of canonical `in(A, ..(6, 9))`.

**Propagator projection.** `clpfd_attr_goals/3` now emits not
only each variable's domain but also every suspended propagator
translated to its source-level form: `$fd_lt`/`$fd_le`/`$fd_neq`
between two vars become `X #< Y` etc; `$fd_plus`/`$fd_times`/etc
become `A + B #= C`; `$fd_alldiff` becomes `all_distinct/1`. An
owner-first-var rule emits each propagator exactly once (across
all the variables it watches). Binary comparisons against an
integer constant are *skipped* because the resulting domain
already captures them — so `A #> 5, A #< 10` prints just
`A in 6..9.`, not `5 #< A, A in 6..9, A #< 10` (matches SWI).

Tests: `UseModuleTests` (4), `ResidualGoalsDisplayTests` (9) —
parse-goal, operator-aware render, domain + propagator
projection across the arithmetic / comparison / global-constraint
propagators.

## Stats

- 14 chunks (249–262).
- ~60 new tests across the phase.
- Full suite at phase close: 1918 embedding + 275 ISO conformance
  + 248 compiler + 105 interpreter + 423 core = **2969 tests, 0
  failures**, 5 long-standing skips.
- Clean build: **0 warnings, 0 errors**.
- One ADR touched: none (no architectural shift). Engine
  invariants unchanged. New public embedding API:
  `Solution.ToString(int)`, `PrologEngine.ParseGoal`,
  `PrologEngine.Operators`, `AstTermRenderer.Render(Term, int,
  OperatorTable)`.

## Pivot from the original Phase 23 plan

Phase 23 was originally scoped as "engine robustness / correctness"
focused on the recorded `retract/1` Blint bug. Verification at the
start of the phase showed the bug had been fixed incidentally
(memory updated to reflect this — see
[`retract-blint-bug.md`](../../Users/gbrow/.claude/projects/C--claude-Shumway/memory/retract-blint-bug.md)).
With the obvious correctness target gone and the ISO conformance
suite already clean at 275/275, the phase pivoted to REPL UX —
which had real day-to-day pain (no history, no editing, no
completion, terse error display).

## Items NOT in this phase

Backlog of robustness items still open for a future phase:

- Cyclic-term materializer overflow (Phase 10 deferred).
- Parser `\+ (a, b)` ambiguity (Phase 10 deferred).
- 3 skipped tests (Chunk53/55 — Phase 14 stale `PrecompiledClauseCache`).

REPL features deferred:

- Spy / trace primitives (real engine work, not just polish).
- Multi-line history (each physical line in a continuation entry
  is a separate history record; SWI / GProlog convention is one
  entry per full goal).
- Syntax highlighting (terminal-portability cost too high for the
  benefit).
- `shumway-link --map` enriched with per-predicate IL emit status
  / dispatch path.
