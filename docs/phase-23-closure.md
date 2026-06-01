# Phase 23 — Closure

**Status**: complete.

**Tagged**: `phase-23`.

Phase 23 polishes the REPL — the day-to-day surface a developer
sits in front of when interactively driving Shumway. Four
chunks (249–252):

| # | Chunk | What it adds |
|---|---|---|
| 249 | REPL line editor + persistent history | Cursor editing, arrow-key history, `~/.shumway_history` |
| 250 | Tab completion for predicate names | Completes builtins + user predicates; multi-column listing |
| 251 | Error display + source positions | Clean error message + Prolog stack trace with `file:line:col` |
| 252 | Pretty-print bindings on overflow | Long compounds / lists break across lines with indentation |

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

## Stats

- 4 chunks (249–252).
- 32 new tests (10 + 8 + 5 + 9).
- Full suite at phase close: 1875 embedding + 275 ISO conformance
  + 248 compiler + 105 interpreter + 423 core = 2926 tests, 0
  failures, 3 long-standing skips.
- No ADRs touched. No engine invariants modified — Phase 23 is
  pure REPL surface + a single `Solution.ToString(int)`
  embedding-API addition.

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
