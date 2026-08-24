# Trealla Prolog library support

Status of running Trealla Prolog's libraries **unmodified** on Shumway.
Measured against a Trealla checkout's `library/`: all 40 libraries are loaded
on a fresh engine under the `trealla` dialect, and 27 are exercised at runtime
with representative queries (the opt-in `TreallaEndToEndValidation` test —
see the end of this document).

**Bottom line: 39 of 40 libraries load clean and everything whose
functionality can exist on Shumway works — including clp(Z) and clp(B), which
are certified against Markus Triska's own test suites running from the
Trealla tree. What does not run needs a capability the engine does not have
(C FFI, network sockets, their VM's task engine). The one library that does
not load, `rbtrees`, depends on a `library('dialect/commons')` that is not
present in the Trealla tree.**

## How to use Trealla libraries

Point the engine at a Trealla `library/` tree with the `trealla` dialect tag:

- REPL / CLI: `shumway -L "trealla:C:/Trealla/library" myprogram.pl`
- Embedding: `engine.AddLibraryDirectory(dir, "trealla")`

Then `:- use_module(library(X)).` works as in Trealla. Libraries load with
`double_quotes = chars` (Trealla's default, and Shumway's) scoped to the
load; text is chars lists throughout, as their APIs expect.

Trealla's library sources are pure Prolog over ordinary builtins — unlike
Scryer's, they do not bottom out in `'$...'` VM instructions — so most of the
tree just runs. A small Trealla compat shim auto-loads with the first
`trealla`-dialect module and supplies what their sources take from the VM:
the `:- help(Signature, Meta)` documentation directives (accepted, ignored),
their `'$memberchk'/3` partial-list core, the 4-argument
`must_be(Value, Type, Context, _)` validator, the `crypto_n_random_bytes/2`
entropy source and `hex_bytes/2` codec that `uuid` rides (the shim's bytes
are pseudo-random, not cryptographically strong), and the compat-name
mappings onto engine predicates: `limit/2` → `call_with_limit/2`,
`offset/2` → `call_with_offset/2`, `load_text/2` → `consult_text/1`,
`srandom/1` → `set_seed/1`.

Without a configured tree, a handful of `use_module(library(X))` names are
still honoured by the dialect pack itself: `lists`, `error`, `iso_ext` and
friends no-op onto the engine's own predicates, `freeze`/`when` route to
`library(coroutining)`, and `clpz` routes to the native `library(clpfd)`
(`#=`, `in`/`ins`, `label`). With the tree configured, the files win — that
is what runs their real `clpz.pl`.

## The sweep

| library | load | exercised | notes |
|---|---|---|---|
| abnf | ✓ | ✓ | RFC 5234 core rules as DCGs |
| aggregate | ✓ | ✓ | `aggregate_all/3` |
| arithmetic | ✓ | ✓ | `lsb`/`msb`/`popcount` over the shim's `must_be/4` |
| assoc | ✓ | ✓ | AVL trees |
| atts | ✓ | certified | the hProlog-style attribute interface `clpz`/`clpb` are built on — certified end to end by the Triska-solver campaign |
| builtins | ✓ | — | their bootstrap layer; loads inert |
| charsio | ✓ | ✓ | `char_type/2` (full Unicode on Shumway), `get_n_chars/3` |
| clpb | ✓ | ✓ | CLP(B) — certified from the tree (`taut/2`, `sat/1`, consistency suite) |
| clpz | ✓ | ✓ | CLP(Z) — certified from the tree (SEND+MORE=MONEY, reification, labeling options) |
| concurrent | ✓ | — | their VM's task engine (`spawn`/`await`); no counterpart |
| curl | ✓ | — | C FFI (`use_foreign_module`); `http_download/2` is the native alternative |
| debug | ✓ | ✓ | the `$`/`*` debug operators |
| dif | ✓ | ✓ | over the engine's native `dif/2` machinery |
| error | ✓ | ✓ | `must_be/2`, `instantiation_error/1`, ... |
| format | ✓ | ✓ | their `format_//2` nonterminal and `format/2,3` |
| freeze | ✓ | ✓ | routes to `library(coroutining)` |
| gensym | ✓ | ✓ | |
| gsl | ✓ | — | GNU Scientific Library FFI |
| http | ✓ | — | pure Prolog, but over network sockets |
| iso_ext | ✓ | ✓ | `bb_put`/`bb_get`/`bb_update` (the attvar-preserving blackboard), `setup_call_cleanup/3`, ... |
| json | ✓ | ✓ | full parse round trip |
| lambda | ✓ | ✓ | `\X^...` lambdas |
| lists | ✓ | ✓ | |
| ordsets | ✓ | ✓ | |
| pairs | ✓ | ✓ | |
| pio | ✓ | — | the native `phrase_from_file/2,3` (lazy, bounded memory) serves this ground |
| quads | ✓ | — | their quad-store test framework |
| random | ✓ | ✓ | `random_integer/3`, `maybe/0,1,2` |
| raylib | ✓ | — | C FFI (game/graphics bindings) |
| rbtrees | ✗ | — | YAP-heritage source: `:- hmtype ... ---> ...` directives Shumway's reader rejects, and a `library('dialect/commons')` dependency not present in the Trealla tree. `library(assoc)` covers the ground |
| reif | ✓ | ✓ | `if_/3`, `tfilter/3` |
| si | ✓ | ✓ | the safe-inference type tests |
| sockets | ✓ | — | network sockets |
| sqlite3 | ✓ | — | C FFI |
| tabling | ✓ | — | the native `:- table` (variant tabling, well-founded negation) serves this ground |
| time | ✓ | ✓ | `sleep/1`; `current_time/1`/`format_time` load |
| ugraphs | ✓ | ✓ | |
| uuid | ✓ | ✓ | `uuidv4/1`, `uuid_string/2` over the shim's entropy + hex codec |
| when | ✓ | ✓ | routes to `library(coroutining)` |
| yall | ✓ | ✓ | `[X,Y]>>Goal` lambdas |

The FFI libraries (`curl`, `gsl`, `raylib`, `sqlite3`) consult — their
`use_foreign_module`/`foreign_struct` directives raise warnings and the
bindings stay undefined. Loading them is harmless; calling them is not
possible without their C libraries and FFI machinery.

## Reproduce

```
SHUMWAY_TREALLA_LIB=C:/Trealla/library dotnet test tests/Shumway.Tests.DialectInterop/ \
    --filter "FullyQualifiedName~TreallaEndToEndValidation"
```

The test loads each library on a fresh engine, runs its representative
query, prints the table above, and asserts zero unexpected load failures
(`rbtrees` is the one documented exception). Set `SHUMWAY_TRIAGE_OUT` to
also write the report to a file. The deeper certification — Trealla's own
`tests/` corpus against their `.expected` oracles, and the Triska solver
suites — is recorded in the phase 40 closure document.
