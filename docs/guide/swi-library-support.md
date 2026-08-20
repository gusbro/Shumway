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

## Writing code against these libraries

**`double_quotes` is `chars` by default** (ADR-047), which is what most
modern SWI code expects. It is a parse-time flag and the literal is stored
packed either way, so choosing `codes` costs nothing: set it explicitly when
a library is written against code lists. (While a swi-dialect library itself
loads, the flag is whatever that dialect pack declares — what those library
sources are written against.)

**A native Shumway builtin wins over an imported library predicate of the same
name.** Importing a library never shadows a builtin. This is what makes the
"no-op (native)" resolutions below work, and it is also why a library's
alternative argument vocabulary for a name Shumway already has (`char_type/2`
is the usual one) is not reachable — you get Shumway's, which follows SWI's
conventions anyway.

### Worked examples

All verified on Shumway with `-L "swi:c:/Program Files/swipl/library"`.

```prolog
:- use_module(library(csv)).
:- use_module(library(dcg/basics)).
:- use_module(library(solution_sequences)).
:- use_module(library(url)).
:- use_module(library(rbtrees)).
:- use_module(library(heaps)).
:- use_module(library(yall)).
:- use_module(library(error)).

dato(uno). dato(dos). dato(uno). dato(tres).

%  csv — typed rows
%  ?- phrase(csv(Rows), "ana,33\nluis,41\n").
%     Rows = [row(ana,33), row(luis,41)].

%  dcg/basics — real parsers without hand-rolling the lexer
%  ?- phrase((integer(A), ",", integer(B)), "12,34").      A = 12, B = 34.

%  solution_sequences
%  ?- findall(X, distinct(dato(X)), L).     L = [uno,dos,tres].
%  ?- findall(X, limit(2, dato(X)), L).     L = [uno,dos].

%  url
%  ?- parse_url('http://example.org:8080/a/b?x=1', P).
%     P = [protocol(http),host(example.org),port(8080),path(/a/b),search([x=1])].

%  rbtrees / heaps — real O(log n) structures
%  ?- list_to_rbtree([b-2,a-1,c-3], T), rb_lookup(b, V, T).      V = 2.
%  ?- list_to_heap([3-c,1-a,2-b], H), get_from_heap(H, K, V, _). K = 1, V = a.

%  yall lambdas
%  ?- maplist([X,Y]>>(Y is X*X), [1,2,3,4], Sq).                 Sq = [1,4,9,16].

%  error
%  ?- catch(must_be(integer, foo), error(E, _), true).  E = type_error(integer,foo).
```

`library(record)` is worth its own mention — it generates code from a
declaration:

```prolog
:- use_module(library(record)).
:- record point(x:integer=0, y:integer=0, label:atom=unnamed).

?- make_point([x(3), y(4)], P).        % P = point(3,4,unnamed)
?- point_x(P, X).                      % X = 3
?- set_x_of_point(10, P, P2).          % P2 = point(10,4,unnamed)
```

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

## Supported — loads clean and runtime-validated

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

## Not supported — needs a capability Shumway does not have

The engine capabilities behind these gaps, so it is clear what is missing and
that none of it is a defect to report:

| capability | status in Shumway | what it blocks here |
|---|---|---|
| **dict syntax** (`Tag{k:v}`, `X.key`, `#{...}`) | not implemented — a language/reader feature, so the files do not even parse | `dicts`, `pprint`, `statistics`, `strings`, `prolog_source`, `prolog_trace`, `prolog_jiti`, `prolog_deps`, `prolog_profile`, `tableutil`, `zip`, `shell` |
| **real OS threads** | engines are thread-AGILE but single-threaded inside (an invariant, see `docs/architecture/invariants.md`); `with_mutex/2` and the message queues are single-threaded stubs | `thread`, `thread_pool`, `threadutil` |
| **loading foreign shared libraries the SWI way** | Shumway's foreign interface is .NET (`[PrologPredicate]`, `--foreign-dll`) and C (`:- native` + P/Invoke); it does not load SWI `.so`/`.dll` plugins or save SWI states | `shlib`, `qsave`, `progman` |
| **SWI VM / IDE internals** | not applicable — Shumway has its own clause store, debugger (ADR-035/036) and toolchain | `check`, `edit`, `make`, `prolog_autoload`, `prolog_breakpoints`, `prolog_clause`, `prolog_codewalk`, `prolog_colour`, `prolog_coverage`, `prolog_pack`, `prolog_qlfmake`, `prolog_xref`, `sandbox`, `check_installation` |
| **cryptographic randomness** | the PRNG is seedable, **not a CSPRNG** | anything deriving key material; ordinary `random/1` and id generation are fine |
| **user-defined evaluable functions** | `is/2` evaluation is not user-extensible (it needs global goal_expansion + module introspection) | `arithmetic`'s `arithmetic_function/1` (accepted and ignored) |

With those in mind, the libraries below load (only warning about their
unsupported dependencies) but their purpose cannot be served — **no-op
(unsupported)**:

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
