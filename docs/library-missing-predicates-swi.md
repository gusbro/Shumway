# SWI libraries — missing-predicate survey

What the SWI libraries **reference but nothing defines** — the predicates to
implement in the engine / SWI shim. The load-triage (`library-triage-swi.md`)
measures whether a library *parses and loads*; this measures whether the code it
loaded can actually *run* — a referenced-but-undefined predicate only errors when
CALLED, so it hides behind a clean load.

## Method

`SwiMissingPredicateSurvey` (opt-in, in `Shumway.Tests.Embedding`, gated on
`SHUMWAY_SWI_LIB`; optional `SHUMWAY_TRIAGE_OUT=<file>`). For **each** library:
compile it + its `use_module` dependencies through the consult pipeline in the
`swi` dialect (`ShmoViaConsult`, dialect-aware), LINK it (`ShmoLinker`,
`AllowUndefined`), and collect the linker's `missing_predicate` diagnostics from
its reachability walk. Per-library isolation (a fresh engine each) — loading all
129 into one engine cascades (125/129 fail on shared operator/collision state).

**Real gap = referenced ∧ undefined ∧ not defined by *any* SWI library.** A
predicate one library defines (e.g. `gensym/2` from `library(gensym)`) is not a
gap even when another references it without importing — 81 such names were
filtered out. Ranked by how many libraries reference each gap.

**Snapshot: 226 real gaps, 0 parse-blocked** (every library now compiles through
`ShmoViaConsult` — the `SsuTransform`-in-`CompileFromParts` fix below). 2182
distinct predicates are defined across the 129 libraries.

## Tier 1 — standard, high-impact, straightforward (recommended shim targets)

| predicate | #libs | note |
|---|---:|---|
| `setup_call_cleanup/3` | 37 | ISO/SWI cleanup-on-exit control. The single biggest win. |
| `numbervars/4` | 30 | SWI variant of our `numbervars/3` (extra end-var / options arg). |
| `sub_atom_icasechk/3` | 30 | case-insensitive substring find. |
| `is_stream/1` | 29 | is the term a stream handle. |
| `term_string/3` | 28 | term ↔ string with options (have `term_to_atom/2`). |
| `sequence/5` | 23 | `solution_sequences` helper. |
| `compound_name_arity/3` | 17 | `functor/3` for compounds (SWI introspection). |
| `code_type/2` | 10 | code variant of our `char_type/2`. |
| `cyclic_term/1` | 10 | negation of our `acyclic_term/1` — trivial. |
| `current_arithmetic_function/1` | 7 | query the arithmetic-function table (declared no-op today; here CALLED). |
| `compound_name_arguments/3` | 6 | `X =.. [F|Args]` split for compounds. |
| `nb_setarg/3` | 6 | non-backtrackable destructive `setarg`. |
| `sub_string/5` | 5 | `sub_atom/5` for strings. |
| `nb_linkarg/3` | 5 | non-backtrackable link-arg. |
| `copy_term_nat/2` | 4 | `copy_term` dropping attributes. |
| `absolute_file_name/3` | 9 | option form (have `/2`). |
| `file_base_name/2` | 4 | path basename. |
| `open_string/2` | 3 | read a string as an input stream. |
| `format_time/3` | 3 | strftime-style time formatting. |
| `functor/4` | 2 | SWI `functor/4` (type-aware). |
| `duplicate_term/2` | 2 | deep copy. |
| `flag/3` | 2 | **unblocks `library(gensym)`** — the `gensym/2` you flagged. `set_flag/2` (1) pairs with it. |
| `read_string/5` | 2 | bounded string read. |
| `same_term/2` | 1 | term identity. |
| `setarg/3` | 1 | backtrackable destructive arg set. |

## Tier 2 — SWI runtime internals (message system / debugger / tabling)

Lower priority and less portable — SWI-specific plumbing. Many are `$`-prefixed
or `Module$`-mangled (module-locals leaking cross-module). Notable clusters:

- **Message system**: `translate_message/3` (28), `message_property/2` (2),
  `print_message`-family — SWI's `message//1` hook infrastructure. (We ship a
  best-effort `print_message/2` already.)
- **Debug / backtrace**: `backtrace/1` (28), `prolog_debug$assertion_rethrow/1`
  (28), `prolog_frame_attribute/3` (4), `clause_property/2`, `nth_clause/3`,
  spy/trace internals.
- **library(error) internals**: `error$current_type/3`, `error$is_of_type/2`,
  `error$text/1`, `$is_char/1`, `$is_code_list/2`, … (6 each). These back
  `must_be`/`is_of_type` — our `must_be/2` shim sidesteps them.
- **Tabling internals**: `$tbl_answer/3,4`, `$tbl_table_status/4`, `trie_gen/2,3`,
  `abolish_table_subgoals/1`, `tnot/1`, `current_table/2` (increval/tables/wfs).
  Shumway's tabling uses different internals; these are SWI's WAM-level trie API.
- **`$skip_list/3`** (14), `$seek_list/4` (5) — SWI's partial-list length helpers.

## Tier 3 — structural / backend (defer, per by-hand analysis)

- **SWI dicts**: `is_dict/1` (8), `dict_pairs/3` (6), `dict_create/3`,
  `get_dict/3`, `put_dict/3`, `del_dict/4` — a language feature (own arc).
- **Threads**: `thread_self/1`, `thread_property/2`, `thread_create/3`,
  `thread_join/2`, `thread_get_message/1` — beyond the single-threaded shim
  (`with_mutex`, message queues) already added.
- **Foreign / blobs**: `load_foreign_library/1`, `use_foreign_library_noi/1`,
  `blob/2`, `$wrap_predicate/5` — native backend.
- **DDE / Windows**: `dde_*`, `open_dde_conversation/3`, `prompt/2` (progman).
- **TTY**: `tty_get_capability/3`, `tty_goto/2`, `tty_put/2`, `get_single_char/1`.
- **fastrw**: `fast_read/2`, `fast_write/2`, `fast_term_serialized/2`.

## Regenerating

```
SHUMWAY_SWI_LIB="C:/Program Files/swipl/library" \
SHUMWAY_TRIAGE_OUT=/path/report.txt \
dotnet test tests/Shumway.Tests.Embedding/ --filter FullyQualifiedName~SwiMissingPredicateSurvey
```

The report lists all 226 gaps ranked, the 81 referenced-but-provided-elsewhere
names, and any parse/load-blocked libraries (0 today).

## Note — a real bug this surfaced

`ShmoCompiler.CompileFromParts` (used by BOTH `shumway-compile` and
`ShmoViaConsult`) ran its own clause sub-pipeline (`DcgTransform → MetaTransform
→ PhraseTransform`) that never got `SsuTransform` when ADR-037 (`Head => Body`)
landed. So **separate compilation of any `=>`-using program** threw
`Unknown clause kind: SsuRule` at `ClauseCompiler`. Fixed by running
`SsuTransform` first in `CompileFromParts` (no-op on non-`=>` clauses).
Regression: `SsuSeparateCompilationTests`.
