# SWI library support — full-corpus sweep + end-to-end validation

What actually works when a real SWI-Prolog library is loaded on Shumway (under the
`swi` dialect, so the SWI compat shim auto-loads) and its predicates are
**exercised at runtime** — not merely parsed/loaded. Covers the FULL top-level
corpus of SWI-Prolog x64 v9.x's `library/` (129 libraries), swept by loading every
one on a fresh engine, plus a curated runtime-smoke set.

## How a `use_module(library(X))` resolves — and what "no-op" means

`use_module(library(X))` resolves in this order:

1. **baked C# library** (`clpfd`, `clpr`, `coroutining`) — the engine's native
   implementation. The SWI `.pl` is **never** consulted.
2. **native-override marker** — for a name on the override candidate list, the
   resolved `.pl` is opened and scanned for a marker; if present, the SWI file is
   **discarded** and Shumway's equivalent is used. A user's own unmarked same-named
   file loads normally. Current overrides:
   | name | marker | routed to |
   |---|---|---|
   | `when` | `$eval_when_condition` | native coroutining `when/2` |
   | `arithmetic` | `math_goal_expansion` | shim stubs (see below) |
   | `listing` | `do_portray_clause` | native `listing/0,1` + `portray_clause/1,2` |
   | `prolog_stack` | `prolog_frame_attribute` | shim `backtrace/1` no-op |

   `arithmetic` deserves its note: SWI implements user-defined evaluable functions
   via a GLOBAL `goal_expansion` hook + module introspection we don't have. Loading
   the real file mis-expanded the arithmetic of **every later consult** (a poison
   pill that made unrelated libraries fail with `type_error`). The override accepts
   `arithmetic_function/1` (unregistered) and evaluates
   `arithmetic_expression_value/2` over the builtin evaluables only.
3. **file on the library search path** — `X.pl` / `X.shum` is consulted and runs on
   our engine. Subdirectory libraries (`library(dcg/basics)`) resolve too.
4. **dialect pack fallback** — a name our prelude/engine covers natively (`lists`,
   `apply`, `pairs`, `ordsets`, `error`, `debug`, `aggregate`, `assoc`, `yall`,
   `apply_macros`) is a **prelude-backed no-op** when no file is on the path. With a
   real SWI tree on the path those load from the file (and work either way).

So a **"no-op"** load is one where the SWI file is not what provides the behaviour:
- **no-op (native)** — Shumway intercepts and uses **its own** implementation
  (steps 1, 2, 4). The library *works*; the SWI source is redundant. The good kind.
- **no-op (unsupported)** — inert because the capability is absent (threads,
  foreign C, dict syntax). Neither loads meaningfully nor runs. The bad kind,
  called out explicitly below.

`:- autoload(Lib, Imports)` is honoured as an eager `use_module` (import lists
included), so a library's bare calls to autoloaded predicates resolve.

## Full-corpus sweep result (129 top-level libraries)

**90 load completely clean; 39 load with warnings; 0 hard failures.** A
"warning" load means the library itself consulted — usually a *dependency*
(tooling it autoloads) failed; the library's own predicates work unless listed
under ❌.

### ✅ Load clean (90)

aggregate, ansi_term, apply, apply_macros, arithmetic°, assoc, atom, backcomp,
base32, base64, broadcast, charsio, codesio, coinduction, console_input, csv,
ctypes, date, dde, dialect, dif, edinburgh, error, exceptions, explain, fastrw,
files, gensym, git, heaps, help°°, hotfix°°, increval, intercept, iostream,
lazy_lists†, listing°, lists, macros, modules, nb_rbtrees, nb_set††, obfuscate,
occurs††, operators, option††, optparse, ordsets, oset, pairs, persistency†,
pio, portray_text, prolog_code, prolog_config, prolog_debug, prolog_evaluable,
prolog_format, prolog_history, prolog_locale, prolog_metainference,
prolog_versions, prolog_wrap, pure_input, qpforeign, quasi_quotations, quintus,
random, rbtrees, readln, readutil, record, rwlocks, settings, solution_sequences,
sort, streams, system, tables, tabling, terms, tty, ugraphs, url††, utf8,
varnumbers, wfs, when°, writef, www_browser, yall
— plus subdirectory libraries exercised: **dcg/basics** (and `dcg/high_order` as
a dependency).

° = native override / native no-op (Shumway's implementation serves).
°° = loads; useful content is SWI-IDE-oriented.
† = loads, but a specific construct inside is inert (see 🟡).
†† = loads; one exercised predicate has a known gap (see 🟡).

### ✅ Runtime-validated (smoke queries pass)

lists, apply (incl. yall lambdas), pairs, assoc, ordsets, error (`must_be`,
`is_of_type`, incl. `acyclic`), aggregate, gensym, heaps, rbtrees, random,
terms, dif, when°, yall, **solution_sequences** (`distinct/1` via the shim's
trie), charsio, sort (`predsort` + lambdas), **csv** (`phrase(csv(Rows), Cs)` →
typed `row/N` terms), **record** (`:- record` generates constructors/accessors
with defaults + types), **dcg/basics** (`integer//1`, `string_without//2`),
**arithmetic**° (builtin evaluation), broadcast, debug, ansi_term
(`ansi_format/3`), varnumbers (needed the `msb` evaluable — added), gensym.

### 🟡 Loads, with a known gap

| library | gap |
|---|---|
| `option` | `option/3` default-branch touches `is_dict/1` (dict-dependent) |
| `occurs` | `contains_term/2` needs SWI's *enumerating* `arg/3` (ISO `arg/3` raises on unbound index) |
| `nb_set` | needs the `$filled_array/4` kernel primitive |
| `url` | loads; `parse_url/2` trips inside its `schema//1` DCG (legacy lib — SWI itself superseded it with `uri`, which is foreign-backed) |
| `lazy_lists` | its `:- lazy_list_iterator(...)` macro directives don't expand (in-file hook applied to same-file directives); the plain lazy-list core loads |
| `persistency` | parse: directives inside list literals (`[ :- dynamic(D), … ]`) |
| `main` | parse: `A|B` as a term argument (`opt_convert/3`) |
| `hashtable` | parse: SWI's `[](_)` zero-name compound syntax |
| `settings` | loads clean; runtime `setting/4` machinery unverified |

### ❌ No-op (unsupported) — needs a capability Shumway does not have

These do **not** work, and are inert because the capability is absent — not
because we intercept them:

| group | libraries | why |
|---|---|---|
| **dict syntax** (`Tag{k:v}`, `X.key`, `#{...}`) | `dicts`, `pprint`, `statistics`, `strings`, `prolog_source`, `prolog_trace`, `prolog_jiti`, `prolog_deps`, `prolog_profile`, `tableutil` | dicts are a language feature, not predicates; the files fail to parse |
| **real threads** | `thread`, `thread_pool`, `threadutil`, `rwlocks`* | single-threaded engines; only `with_mutex`/message-queue shims exist (*rwlocks parses now, its runtime needs threads) |
| **foreign C / OS binding** | `shlib`, `zip`, `qsave`, `dde`, `progman` (`missing_feature('DDE')`), `shell` (also dicts) | load a shared library / talk to the OS |
| **SWI-IDE / VM tooling** | `check`, `check_installation`, `edit`, `make`, `prolog_autoload`, `prolog_breakpoints`, `prolog_clause`, `prolog_codewalk`, `prolog_colour`, `prolog_coverage`, `prolog_pack`, `prolog_qlfmake`, `prolog_xref`, `vm`, `sandbox` | introspect SWI's clause/VM internals (`==>` SSU-DCG sources, dicts, `$vm` hooks); Shumway has its own debugger (ADR-035/036) and toolchain |

The tooling group still *loads* (only warns about its unsupported deps) but its
useful behaviour targets SWI's internals — treat as not applicable.

## Engine/parser features this sweep added (swi dialect load scope only)

- `:- autoload/1,2` = eager `use_module` (imports honoured).
- Module-qualified clause heads (`prolog:message(...)`) exempt from the
  contiguity check (multifile hook idiom).
- `:- dynamic system:term_expansion/2.` no longer hides hook clauses in the
  dynamic store (hooks stay on the static hook pipeline).
- Hook-emitted **directives** are not re-expanded (SWI single-pass), so
  `record`'s `(:- record('<compiled>'))` xref marker stays inert.
- Digit-group separators (`10_000`), lenient bare-operator operands
  (`:- dynamic X as volatile.`), `0''` = quote char — each scoped to the swi
  dialect load; ISO strictness holds everywhere else.
- `as` (xfx 700) + `thread_local` (fx 1150, = dynamic) operators; `as`
  decorations stripped in indicator directives.
- `library(dcg/basics)`-style subdirectory resolution (dialect inherited from
  the tagged root).
- Arithmetic: `msb`/`lsb` evaluables.
- Shim additions: `$clausable/1`, `noprofile/1`, `$hide/1`, `$notransact/1`,
  `create_prolog_flag/3` (accept, no store), `record('<compiled>')`,
  plain-`digit`/`xdigit(W)`/`csym`/`csymf` in `code_type/2`,
  `current_arithmetic_function/1` enumeration mode, minimal tries
  (`trie_new/1`, `trie_insert/2`, `trie_destroy/1`),
  `arithmetic_function/1` + `arithmetic_expression_value/2` stubs.

## Remaining candidates (not done)

1. SWI-enumerating `arg/3` under the caller-dialect walk → promotes `occurs`.
2. `$filled_array/4` (functor-array) → promotes `nb_set`.
3. `url`'s `schema//1` failure → promotes `parse_url/2`.
4. In-file expansion of same-file directives → promotes `lazy_lists` macros.

Regenerate: `SHUMWAY_SWI_LIB=<dir> SHUMWAY_TRIAGE_OUT=<file> dotnet test
tests/Shumway.Tests.DialectInterop/ --filter FullyQualifiedName~SwiEndToEndValidation`.
