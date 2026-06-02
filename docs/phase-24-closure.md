# Phase 24 — Closure

**Status**: complete.

**Tagged**: `phase-24`.

Phase 24 brings Arity-Prolog compatibility primitives so programs
written against Arity/Prolog32 (and its surface conventions —
snips, the recorded database, Edinburgh-style I/O, `:- visible`)
port to Shumway with minimal source edits. The selection of which
predicates to implement was driven by inspection of Arity's actual
predicate listing (`ARITY.HLP.txt`) — not generic Prolog folklore.
Each chunk closes a specific gap.

Ten chunks (263–274, with 269/270 dropped — see "Items NOT in this
phase" below):

| # | Chunk | What it adds |
|---|---|---|
| 263 | Snips `[! ... !]` | Parser-level desugar to `once((G))`; Arity's scoped commit |
| 264 | `save_state/1,2`, `restore_state/1` | V6 bundle format with snapshot trailer; full or dynamic-only |
| 265 | `:- visible foo/N` alias | Same semantics as `:- dynamic foo/N` |
| 266 | Recorded database | `recorda/recordz/recorded/erase/instance/replace/nref/pref/record_after/record_before/key_count/keys/ref/eraseall` |
| 267 | Edinburgh-style I/O | `see/seen/seeing/tell/told/telling/get/get0/put/skip/tab` |
| 268 | Misc Arity utilities | `string_term/2`, `string_termq/2`, `string_search/3` (backtrackable) |
| 271 | File-system ops | `mkdir/rmdir/delete/rename/directory/exists_file/exists_directory/chdir` |
| 272 | Pseudo-random | `randomize/1`, `random/1`, `random_between/3` |
| 273 | `expand_term/2` | DCG expansion hook exposed to user code |
| 274 | `file_list/1,2` | Plain-text database dump (re-consultable) |

## Chunk 263 — Snips `[! G !]`

Arity's "scoped soft cut" desugars to `once((G))` at parse time:
internal backtracking is permitted while finding G's first solution,
but a successful exit prunes the snip's choice points so a later
failure skips back past the snip rather than re-entering it. Cut
inside a snip is scoped to the snip boundary (inherited from
`once/1`'s call barrier).

Parser-only change (`Parser.cs:346`); engine sees `once/1` and
nothing knows about the syntax. Trade-off: `[!, a, b]` no longer
parses as a list-with-cut-as-first-element — write `[(!), a, b]`
for that (vanishingly rare pattern).

## Chunk 264 — `save_state/1,2`, `restore_state/1`

Snapshots an engine's user-visible state to a Shumway V6 bundle.
Full mode (default) captures every source previously consulted
plus every asserted dynamic clause; `restore_state/1` on a fresh
engine resets and replays. Dynamic-only mode (`dynamic_only(true)`)
skips the consult history and just persists facts; `restore_state/1`
then merges via `assertz` instead of resetting.

Bundle format V6: optional trailer after V5's foreign-assemblies
section, carrying consult history (UTF-8 strings) and dynamic
clauses (TermCodec-encoded). `BundleSnapshot` type, reader/writer
extended; the LoadBundle path ignores the trailer.

`_consultHistory` log field on PrologEngine, populated by
`ConsultString` via a new private `ConsultStringInner` overload so
the prelude's auto-consult stays out of snapshots.

## Chunk 265 — `:- visible foo/N` alias

Arity uses `:- visible` where SWI/SICStus use `:- dynamic`. Same
semantics. `OperatorTable.Default` registers `visible` as
`fx 1150` alongside `dynamic`; `TryReadDynamicDirective` accepts
both functor names. ShmoCompiler mirrors the change so
separate-compilation respects the alias.

Supports all forms: indicator (`:- visible foo/1.`), list
(`:- visible [a/0, b/1].`), and GNU comma-separated
(`:- visible a/0, b/1.`).

## Chunk 266 — Recorded database

A second in-memory store separate from dynamic predicates: keys
are arbitrary terms (not `functor/arity`), each `recorda/3` /
`recordz/3` returns a fresh stable integer reference,
`erase/1` takes a reference and removes precisely that entry,
`recorded/3` enumerates the chain on backtracking.

Refs never reused (monotonic counter); a stale `instance/1` on
an erased ref simply fails rather than resurrecting another
entry. `RecordedDatabase` class with two dictionaries —
`Term → LinkedList<RecordEntry>` for chain order, `int →
RecordEntry` for O(1) ref lookup. Lazily constructed on first
`PrologEngine.Records` access.

Full builtin family: `recorda/3`, `recordz/3`, `recorded/3`
(backtrackable), `erase/1`, `eraseall/1`, `instance/2`,
`key_count/2`, `keys/1` (backtrackable when arg is unbound),
`ref/1` (live-ref type test), `replace/2`, `nref/2`, `pref/2`,
`record_after/3`, `record_before/3`.

## Chunk 267 — Edinburgh-style I/O

Layer of Arity / Edinburgh I/O primitives over the chunk-140
ISO stream registry:

- `see/1` / `seen/0` / `seeing/1` — set current input to a file,
  close + revert to user_input, report current input filename.
- `tell/1` / `told/0` / `telling/1` — same for current output.
- `get/1,2` (printable code, skips control chars < 32),
  `get0/1,2` (any code), `put/1,2` (write code), `skip/1,2`
  (read-and-discard until target code), `tab/2` (N spaces to
  stream).

All wrap the StreamRegistry; no engine state changes. Closing
tell-/see-opened streams is automatic on the next `tell`/`see`
or on `told`/`seen`.

## Chunk 268 — `string_term/2`, `string_termq/2`, `string_search/3`

Originally scoped wider (counters, gc, argrep, ifthen,
ifthenelse, ...). Narrowed during chunk discussion: `ifthen/2` and
`ifthenelse/3` were proposed as prelude predicates but discarded
because real Arity programs (and Blint specifically) define their
own `ifthen/2` — providing one in the prelude collides via
`ValidatePublicUniqueness`. The remaining items shipped:

- `string_term(?Atom, ?Term)` — bidirectional parse/render with
  write-style (unquoted). In Arity "string" means atom.
- `string_termq(?Atom, ?Term)` — writeq-style with quoting;
  equivalent to the existing `term_to_atom/2`.
- `string_search(+Sub, +Atom, ?Location)` — backtrackable
  substring search, 0-based offsets, left-to-right order,
  overlapping matches reported.

## Chunk 271 — File-system operations

Arity's file-system family on top of `System.IO`:

- `mkdir/1` — `Directory.CreateDirectory` (idempotent).
- `rmdir/1` — `Directory.Delete` non-recursive; fails on non-
  empty, `existence_error` on missing.
- `delete/1` — `File.Delete`; `existence_error` /
  `permission_error`.
- `rename/2` — `File.Move`; `existence_error` on missing source,
  `permission_error` if target exists.
- `directory/6` — backtrackable enumeration of `(Name, Mode,
  Time, Date, Size)`. Mode is Arity's bitfield (1=ReadOnly,
  2=Hidden, 4=System, 16=Directory, 32=Archive), Time as
  `HH:MM:SS` atom, Date as `YYYY-MM-DD` atom, Size in bytes
  (0 for directories).
- `exists_file/1`, `exists_directory/1` — SWI-style existence
  tests.
- `chdir/1` — 1-arg alias of `working_directory/2` (prelude).

Errors are ISO-shaped (`ShumwayPrologException` carrying
`error/2` terms) so `catch/3` can match them.

## Chunk 272 — Pseudo-random

Per-engine `System.Random` instance with seedable Randomize:

- `randomize(+Seed)` — reseed with an integer.
- `random(-X)` — fresh float in `[0.0, 1.0)`.
- `random_between(+L, +H, -X)` — fresh integer in `[L, H]`
  inclusive (matching SWI semantics).

The arithmetic-function form `X is random(N)` is NOT included —
would require cross-project access from
`Shumway.Builtins.ArithmeticEvaluator` into the engine host's
RNG state; deferred until there's a clear need.

## Chunk 273 — `expand_term/2`

Exposes the same `DcgTransform` that consult applies internally.
A `--> /2` rule expands to its difference-list clause; any other
term passes through unchanged. Useful for inspecting / reapplying
the DCG transformation without running consult.

## Chunk 274 — `file_list/1,2`

Plain-text database dump:

- `file_list(+File)` — save every listable predicate.
- `file_list(+File, +Spec)` — `Spec` is `Name/Arity` or a list of
  predicate indicators.

Output is re-consultable Prolog source. Each dynamic predicate
gets a `:- dynamic Name/Arity.` directive at the top, then every
clause is rendered via `ClausePortrayer` (head + indented body).
Round-trip-tested: a `file_list` dump consults back into a fresh
engine and reproduces facts AND rules (a test exercises
`doubled(X, Y) :- Y is X * 2`).

## Stats

- 10 chunks (263–274 with 269/270 dropped).
- ~70 new tests across the phase.
- Full suite at phase close: 2007 embedding + 275 ISO
  conformance + 256 compiler + 105 interpreter + 423 core =
  **3066 tests**, 0 failures, 5 long-standing skips.
- No ADRs touched. Bundle format bumped to V6 (chunk 264) with
  backward-compatible snapshot trailer. New public embedding
  surface: `PrologEngine.SaveState/RestoreState[FromBytes]`,
  `PrologEngine.Records`, `PrologEngine.Randomize`,
  `BundleSnapshot`, `RecordedDatabase`.

## Items NOT in this phase

**Chunk 269 (dropped)** — broader string-conversion family
(`int_text/2`, `float_text/3` with format specs, `list_text/2`,
`substring/4`, `string_lower/2`, `string_upper/2`). Items that
weren't already covered by existing builtins (`atom_codes/2`,
`atom_chars/2`, `downcase_atom/1`, `upcase_atom/1`,
`sub_atom/5`) were postponed. The two `string_term` variants
and `string_search/3` shipped under chunk 268.

**Chunk 270 (skipped)** — binary I/O (`read_int8/16/32`,
`write_int8/16/32`, `read_asciz/2`, `read_line/2`). User
descoped during planning.

**Counters and trivial helpers** (`inc/2`, `dec/2`, `ctr_inc/2`,
`ctr_dec/2`, `ctr_set/2`, `ctr_is/2`, `case/1`, `ifthen/2`,
`ifthenelse/3`, `gc/0`, `argrep/4`, `arg0/3`, `term_concat/3`)
— user descoped. `ctr_*/2` flagged as extra-logical (per-engine
mutable state) — if added in a future phase, should live with
the SWI-style `nb_setval`/`nb_getval` and GNU-style
`g_assign`/`g_value` family as a coherent "global state" module.

**Arity GUI / DOS-specific primitives** (`create_popup/N`,
`define_window/N`, `dialog_run/N`, `current_window/2`, `cls/0`,
`tmove/2`, `wc/2`, `wa/2`, `tchar/2`, `tget/2`, `keyb/2`,
`set_cursor/2`, `recolor_window/2`, `in/2`, `out/2`, `lock/0`,
`unlock/0`, `abort/1`) — not applicable to .NET. Equivalent
modern surface would be a `Shumway.WinForms` or
`Shumway.SpectreConsole` module; out of phase scope.

**Arity DLL/native loading** (`dll_load/3`, `dll_free/1`,
`dll_handle/2`, `dll_address/3`, `dll_visi/2`) — superseded by
the `[PrologPredicate]` attribute + `--foreign-dll` toolchain
(Phase 21–22).

**Arity B-trees / hash tables as native types** (`defineb/4,5`,
`recordb/3`, `retrieveb/N`, `betweenb/N`, `defineh/2`,
`recordh/3`, ...). Specialised disk-backed data structures;
SQLite/LiteDB modules would be the modern equivalent. Out of
phase scope.

**Arity worlds** (`code_world/2`, `data_world/2`,
`create_world/1`, `what_worlds/1`, `delete_world/1`) — alternative
code/data namespacing. Shumway has flat modules; adding worlds
is complexity without clear ROI.

**Arity debugger** (`trace/0,1`, `debug/0,1`, `notrace/0,1`,
`spy/1`, `nospy/1`, `leash/1`, `resetspy/0`, `debugger/0`) —
Byrd-box procedural debugger. Substantial work (instrumented
interpreter with call/exit/redo/fail ports), deserves its own
phase.

**Arity time/date** (`time/1`, `date/1`, `date_day/2`) — partial:
we already have `get_time/1` (Unix epoch) and `stamp_date_time/2`
(human-readable). Arity's `time/1` reads/writes the system clock
in a `time(H, M, S, Hs)` term, which is rarely useful in modern
embedded usage.

**Arity arithmetic `random(N)` function in `is/2`** — see
chunk 272 — needs an arithmetic-evaluator hook from
`Shumway.Builtins` into the engine host's RNG state. Deferred
until needed.
