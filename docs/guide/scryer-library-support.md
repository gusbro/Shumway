# Scryer Prolog library support

Status of running Scryer Prolog's libraries **unmodified** on Shumway.
Measured against a Scryer checkout's `lib/`: all 46 top-level libraries are
loaded on a fresh engine under the `scryer` dialect, and 33 are exercised at
runtime with representative queries (the opt-in `ScryerEndToEndValidation`
test — see the end of this document). The `http/`, `numerics/`,
`serialization/` subdirectories are not covered.

**Bottom line: 46 of 46 libraries load clean — zero warnings, zero failures —
and everything whose functionality can exist on Shumway works, including
clp(Z), which is certified byte-identical to Scryer's own answers. What does
not work needs a capability the engine does not have (delimited continuations,
Rust FFI/network bindings, crypto natives).**

## How to use Scryer libraries

Point the engine at a Scryer `lib/` tree with the `scryer` dialect tag:

- REPL / CLI: `shumway -L "scryer:C:/Scryer/lib" myprogram.pl`
- Embedding: `engine.AddLibraryDirectory(dir, "scryer")`

Then `:- use_module(library(X)).` works as in Scryer. Libraries load with
`double_quotes = chars` (Scryer's default) scoped to the load; text is chars
lists throughout, as their APIs expect.

A Scryer compat shim auto-loads with the first `scryer`-dialect module. Where
the SWI shim supplies SWI system predicates, this one mostly supplies
**emulations of Scryer's Rust-VM native instructions** (the `'$...'` calls its
libraries bottom out in) as bare-global predicates, so the libraries' own pure
Prolog runs unmodified: `$random_integer`, `$crypto_random_byte`, `$getenv`,
`$file_exists` and the file-system family, `$char_type` with Scryer's full
category vocabulary, plus `builtins.pl` helpers (`must_be_number/2`,
`can_be_number/2`) that Scryer's bootstrap makes implicitly visible on their
VM.

Two libraries route to Shumway equivalents instead (native override — the file
is recognised by a marker and the load discarded): `format` (its rendering
core needs `builtins:parse_write_options`; the pack's `format/2,3` shim
serves) and `time` (wraps `$cpu_now`; Shumway's native `time/1` + `sleep/1`
serve — `current_time`/`format_time//2` are not available).

## ✅ Supported — loads clean and runtime-validated

| library | exercised |
|---|---|
| `lists` | `member/2`, `append/3`, `length/2` |
| `assoc` | `empty_assoc` → `put_assoc` → `get_assoc` |
| `between` | `between/3`, `numlist/3` |
| `clpb` | `taut(X + ~X, 1)` (boolean constraints) |
| `clpz` | `X #= 3+4` — **certified byte-identical answers vs Scryer** (queens/permutations oracle) |
| `csv` | `phrase(parse_csv(Rows), Cs)` → `frame/2` |
| `dcgs` | `seq//1` over chars |
| `debug` | `* Goal` (goal generalization), `$`/`$-` ops |
| `dif` | `dif/2` posting + failing |
| `error` | `must_be/2` with ISO error terms |
| `freeze` | `freeze/2` firing on binding |
| `gensym` | `gensym/2` |
| `iso_ext` | `bb_put/2` + `bb_get/2` (blackboard) |
| `lambda` | `\X^Y^Goal` lambdas via `maplist/3` |
| `ordsets` | `ord_union/3` |
| `pairs` | `pairs_keys_values/3` |
| `queues` | `list_queue/2`, `queue_length/2` |
| `reif` | `if_/3`, `tfilter/3` (reified control) |
| `si` | `atom_si/1`, `integer_si/1` (sound type tests) |
| `simplex` | `gen_state/1` + `constraint/3` |
| `terms` | `numbervars/3` |
| `ugraphs` | `add_vertices/3` |
| `xpath` | `xpath/3` over a term DOM (pure — no sgml needed) |
| `arithmetic` | `lcm/3`, `msb/2` |
| `charsio` | `char_type/2` incl. the `lower(L)`/`upper(U)` string forms |
| `format` | `format/2,3` (via the pack shim) |
| `files` | `file_exists/1` and the FS family, chars↔atom converted |
| `os` | `getenv/2` |
| `random` | `random/1`, `random_integer/3` (upper bound exclusive, as in Scryer) |
| `time` | `time/1`, `sleep/1` |
| `uuid` | `uuidv4_string/1` — valid v4 UUIDs. **NOT cryptographically secure** (seedable PRNG source) |
| `when` | `when/2` post + fire |
| `crypto` | the pure parts (`hex_bytes/2`, `crypto_n_random_bytes/2`†) |

† random bytes come from a PRNG — fine for ids and simulation, **not for key
material**. `atts` is validated indirectly: it is the foundation the whole
clpz/dif/freeze stack runs on.

## ❌ Not supported — needs a capability Shumway does not have

These load (harmlessly) but their purpose cannot be served:

| group | libraries | why |
|---|---|---|
| **delimited continuations** | `cont` (`reset/3`, `shift/1`) | a VM execution-model feature |
| `tabling` | Scryer's tabling is built on `cont` | **Shumway's own native `:- table` covers the feature** — `:- table p/2` works through our tabling engine |
| **native bindings** | `ffi`, `sockets`, `tls`, `wasm`, `sgml` (`load_html`), `process` | Rust-side FFI / network / OS |
| **crypto natives** | `crypto` hashes, HKDF, curves | Rust crypto backend (the pure parts DO work, see above) |
| **VM introspection** | `diag` (`wam_instructions/2`) | decompiles Scryer's WAM; Shumway has `shumway-disasm` for its own |
| **bootstrap internals** | `builtins`, `loader`, `ops_and_meta_predicates` | Scryer-internal modules; load as inert data |
| `pio` | `phrase_from_file` needs their stream layer | unverified; plain `phrase/2,3` is native here |

## Regenerate the validation

```
SHUMWAY_SCRYER_LIB=C:/Scryer/lib SHUMWAY_TRIAGE_OUT=<file> dotnet test
tests/Shumway.Tests.DialectInterop/ --filter FullyQualifiedName~ScryerEndToEndValidation
```
