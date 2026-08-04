# SWI-Prolog library support

Status of running SWI-Prolog's standard libraries **unmodified** on Shumway.
Measured against SWI-Prolog x64 v9.x's `library/` directory: all 129 top-level
libraries are loaded on a fresh engine under the `swi` dialect, and a curated
set is exercised at runtime with representative queries (the opt-in
`SwiEndToEndValidation` test — see the end of this document).

**Bottom line: 94 of 129 libraries load completely clean; the remaining 35 load
with warnings about unsupported dependencies. Zero hard failures. Every library
whose functionality can exist on Shumway works; what does not work needs a
capability the engine does not have (SWI dict syntax, real threads, foreign C
libraries, SWI's IDE/VM internals).**

## How to use SWI libraries

Point the engine at an SWI library tree with the `swi` dialect tag:

- REPL / CLI: `shumway -L "swi:c:/Program Files/swipl/library" myprogram.pl`
- Embedding: `engine.AddLibraryDirectory(dir, "swi")`

Then `:- use_module(library(X)).` works as in SWI, including subdirectory
libraries (`library(dcg/basics)`). While a library from a `swi`-tagged
directory loads, the parser accepts SWI's syntax extensions (digit separators
`10_000`, `:- dynamic X as volatile.`, `0''`, arguments at full operator
priority — `f(a :- b)`, `[ :- D1, :- D2 ]`, `f(a|b)` — and `[](Args)`
compounds, plus the `as` and `thread_local` operators). Outside such a load the
engine stays strictly ISO.

An SWI compat shim auto-loads with the first `swi`-dialect module, supplying
SWI system predicates the libraries rely on (`nb_setarg/3`, `copy_term_nat/2`,
`code_type/2`, tries for `distinct/1`, `variant_hash/2`, `format`/message
plumbing, …). It can also be loaded explicitly with
`use_module(library(swi))`. `:- autoload(Lib, Imports)` is honoured as an
eager `use_module` with its import list.

## How a `use_module(library(X))` resolves — and what "no-op" means

1. **Baked native library** (`clpfd`, `clpr`, `coroutining`) — Shumway's own
   implementation; the SWI `.pl` is never consulted.
2. **Native override** — for a known name, the resolved file is scanned for a
   marker identifying it as the SWI version; if found, the load is discarded
   and Shumway's equivalent serves. A user's own same-named library without
   the marker loads normally. Current overrides:

   | name | routed to |
   |---|---|
   | `when` | native coroutining `when/2` |
   | `arithmetic` | shim stubs — user-defined evaluable functions are unsupported (they need SWI's global goal_expansion + module introspection); `arithmetic_function/1` is accepted and ignored, `arithmetic_expression_value/2` evaluates the builtin functions |
   | `listing` | native `listing/0,1` + `portray_clause/1,2` |
   | `prolog_stack` | shim `backtrace/1` no-op (there is no SWI VM backtrace) |

3. **File on the library search path** — consulted and run on Shumway.
4. **Dialect-pack fallback** — names the prelude covers natively (`lists`,
   `apply`, `pairs`, `ordsets`, `error`, `debug`, `aggregate`, `assoc`,
   `yall`, `apply_macros`) resolve as no-ops when no file is on the path.

A **no-op (native)** load means Shumway intercepts and its own implementation
serves — the library *works*. A **no-op (unsupported)** load means the library
is inert because the capability is absent — called out explicitly below.

## ✅ Supported — loads clean and runtime-validated

lists, apply (incl. yall lambdas), pairs, assoc, ordsets, **error** (`must_be`,
`is_of_type`, incl. `acyclic`), aggregate, gensym, heaps, rbtrees, nb_rbtrees,
random, **occurs** (`contains_term/2` — `arg/3` enumerates for SWI callers),
terms, dif, when°, yall, **solution_sequences** (`distinct/1`, `limit/2`),
charsio, sort (`predsort` + lambdas), **csv** (`phrase(csv(Rows), Cs)` → typed
`row/N`), **record** (`:- record` generates constructors/accessors with
defaults + types), **dcg/basics** (`integer//1`, `string_without//2`),
**lazy_lists** (its `:- lazy_list_iterator(...)` macro directives expand;
iterators are generated), **url** (`parse_url/2` → protocol/host/port/path/
search), **nb_set** (add/dedup/to_list), arithmetic°, broadcast, debug,
ansi_term (`ansi_format/3` — format applied, colour ignored), varnumbers,
**persistency**, **main**, **hashtable** (all three parse), listing°,
prolog_stack°, codesio, date, base32, base64, utf8, readutil, readln,
intercept, increval, iostream, pure_input, pio, coinduction, wfs, tabling
(via Shumway's native `:- table`), quasi_quotations, operators, oset, ugraphs,
writef, atom, backcomp, quintus, edinburgh, dialect, macros, modules, streams,
system, tables, portray_text, prolog_code, prolog_config, prolog_debug,
prolog_evaluable, prolog_format, prolog_history, prolog_locale,
prolog_metainference, prolog_versions, prolog_wrap, optparse,
predicate_options, files, fastrw, hotfix, help, ctypes, console_input, dde,
git, explain, obfuscate, rwlocks, www_browser, tty, vm.

° = native no-op / override — Shumway's implementation serves.

## 🟡 Loads, with a known limitation

| library | limitation |
|---|---|
| `option` | `option/3`'s default branch touches `is_dict/1` (dict-dependent); the non-dict forms work |
| `settings` | loads clean; the runtime `setting/4` machinery is unverified |
| `crypto`-adjacent uses | random-derived values come from a seedable PRNG, **not cryptographically secure** |

## ❌ Not supported — needs a capability Shumway does not have

These load (only warning about their unsupported dependencies) but their
purpose cannot be served — **no-op (unsupported)**:

| group | libraries | why |
|---|---|---|
| **dict syntax** (`Tag{k:v}`, `X.key`, `#{...}`) | `dicts`, `pprint`, `statistics`, `strings`, `prolog_source`, `prolog_trace`, `prolog_jiti`, `prolog_deps`, `prolog_profile`, `tableutil`, `zip`, `shell` | dicts are a language feature, not predicates; the files fail to parse |
| **real threads** | `thread`, `thread_pool`, `threadutil` | single-threaded engines; only `with_mutex`/message-queue shims exist |
| **foreign C / OS binding** | `shlib`, `qsave`, `progman` (DDE) | load shared libraries / talk to Windows services |
| **SWI IDE / VM tooling** | `check`, `check_installation`, `edit`, `make`, `prolog_autoload`, `prolog_breakpoints`, `prolog_clause`, `prolog_codewalk`, `prolog_colour`, `prolog_coverage`, `prolog_pack`, `prolog_qlfmake`, `prolog_xref`, `sandbox` | introspect SWI's clause/VM internals (`==>` sources, dicts, `$vm` hooks); Shumway has its own debugger and toolchain |

## Regenerate the validation

```
SHUMWAY_SWI_LIB=<dir> SHUMWAY_TRIAGE_OUT=<file> dotnet test
tests/Shumway.Tests.DialectInterop/ --filter FullyQualifiedName~SwiEndToEndValidation
```
