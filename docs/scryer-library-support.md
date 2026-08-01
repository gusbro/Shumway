# Scryer library support — full-corpus sweep + end-to-end validation

What actually works when a real Scryer-Prolog library is loaded on Shumway (under
the `scryer` dialect — `double_quotes = chars`, the `atts` attribute machinery)
and its predicates are **exercised at runtime**. Covers the full top-level corpus
of a Scryer checkout's `lib/` (46 libraries; the `http/`, `numerics/`,
`serialization/`, `tabling/` subdirectories are not swept), loading each on a
fresh engine plus a runtime-smoke set. Companion to
[`swi-library-support.md`](swi-library-support.md) — same method, same
categories.

## Sweep result

**46/46 load clean — zero warnings, zero failures.** The clpz arc (ADR-040,
rounds 1–8) paved the Scryer path: `atts`, export-qualified modules, dialect
parsing, per-module attribute hooks. Loading is a solved problem for this
corpus; what distinguishes libraries is whether they run, which splits on one
axis: **does the library bottom out in pure Prolog, or in a Rust-VM native
(`'$...'`) instruction we don't provide?**

## ✅ Runtime-validated (smoke queries pass) — 24

| library | exercised |
|---|---|
| `lists` | `member/2`, `append/3`, `length/2` |
| `assoc` | `empty_assoc` → `put_assoc` → `get_assoc` |
| `between` | `between/3`, `numlist/3` |
| `clpb` | `taut(X + ~X, 1)` (boolean constraints) |
| `clpz` | `X #= 3+4` — **certified byte-identical to Scryer** (see `clpz-vs-scryer` memory) |
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
| `crypto` | `hex_bytes/2` (the pure part; hashes are native, see ❌) |

`atts` is validated indirectly — it is the foundation the whole clpz/dif/freeze
stack runs on.

## 🟡 Loads; runtime gap — round-2 shim candidates

These load fine but their exercised predicates hit one of two walls:
**(a)** a call into `builtins.pl` — Scryer's bootstrap module, implicitly
visible in every module on their VM, not on ours; or **(b)** a Rust-VM native
(`'$...'`) with a Shumway-native equivalent we could route to via the
marker-override mechanism (as done for SWI's `when`/`arithmetic`/`listing`/
`prolog_stack`).

| library | wall | round-2 route |
|---|---|---|
| `arithmetic` | (a) `builtins:must_be_number/2` | tiny bare-global shim helper → the rest (`lcm/3`, `msb/2`, …) is pure |
| `format` | (a) `builtins:parse_write_options/3` | marker-override → the scryer pack's own `Format` shim (works today when no file dir is on the path) |
| `charsio` | (b) `$char_type` | marker-override → pack no-op (our native `char_type/2` serves) |
| `files` | (b) `$file_exists`, … | marker-override → shim over our FS builtins (`exists_file/1`, `delete/1`, `rename/2`, …) |
| `random` | (b) `$maybe`, `$random_integer` | marker-override → our native `random/1` / `random_between/3` |
| `time` | (b) `$cpu_now` | marker-override → our `time/1` + `statistics/2` |
| `uuid` | (b) `$crypto_random_byte` | shim `uuidv4` over our random source |
| `os` | (b) `$getenv` chain | partial shim over our env access |
| `when` | ? | loads; `when/2` posting **fails silently** — needs diagnosis (its impl pulls lambda/format/debug) |

Note the FILE-FIRST subtlety: `charsio`/`format` **work today on an engine with
no Scryer dir on the search path** (the dialect pack's shim/no-op resolves);
with the real tree on the path the native-gated file wins and breaks. That is
exactly what the marker-override mechanism is for.

## ❌ No-op (unsupported) — needs a capability Shumway does not have

These load (harmlessly) but their purpose is a VM/OS capability we don't provide
— inert, not intercepted:

| group | libraries | why |
|---|---|---|
| **delimited continuations** | `cont` (`reset/3`, `shift/1` via `$reset_cont_marker`) | a VM execution-model feature |
| `tabling` | Scryer's tabling is built ON `cont` | **Shumway's own native `:- table` covers the feature** — a program using `:- table p/2` works through our tabling engine |
| **native bindings** | `ffi`, `sockets`, `tls`, `wasm`, `sgml` (`load_html`), `process` | Rust-side FFI / network / OS |
| **crypto natives** | `crypto` (hashes, HKDF, curves) | Rust crypto backend (`hex_bytes` and other pure parts DO work) |
| **VM introspection** | `diag` (`wam_instructions/2`) | decompiles Scryer's WAM; Shumway has `shumway-disasm` for its own |
| **bootstrap internals** | `builtins`, `loader`, `ops_and_meta_predicates` | Scryer-internal modules; load as inert data |
| `pio` | `phrase_from_file` needs their stream layer | unverified; plain `phrase/2,3` is native here |

## Method / regenerate

`ScryerEndToEndValidation` (opt-in): loads each library on a fresh engine with
`AddLibraryDirectory(dir, "scryer")` and runs the smoke query. The hard
assertion is the load sweep (46/46 must load).

```
SHUMWAY_SCRYER_LIB=C:/Scryer/lib SHUMWAY_TRIAGE_OUT=<file> dotnet test
tests/Shumway.Tests.DialectInterop/ --filter FullyQualifiedName~ScryerEndToEndValidation
```
