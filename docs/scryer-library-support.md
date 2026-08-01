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

**46/46 load clean — zero warnings, zero failures. After round 2, 33 of them
are runtime-validated by smoke queries** (the 24 below plus the nine former
gaps in the round-2 table). The clpz arc (ADR-040, rounds 1–8) paved the
Scryer path: `atts`, export-qualified modules, dialect parsing, per-module
attribute hooks. What distinguished the rest was one axis — **does the library
bottom out in pure Prolog, or in a Rust-VM native (`'$...'`)?** — and round 2
closed the native-gated set by emulating those natives in the Scryer shim.

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

## ✅ Round 2 — the former gaps, now served by the Scryer shim

The round-1 gaps split on one axis — a call into `builtins.pl` (Scryer's
bootstrap, implicitly visible in every module on their VM) or a Rust-VM native
(`'$...'`). Round 2 closed ALL of them with the **Scryer shim**
(`ScryerShim.cs`, auto-loaded on the first scryer-dialect load): bare-global
**emulations of the `'$...'` natives** — an unresolved bare or
`builtins:`-qualified call falls through to the bare-global namespace, so
providing the native's contract there lets the library's own pure Prolog run
unmodified.

| library | was blocked on | now |
|---|---|---|
| `arithmetic` | `builtins:must_be_number/2`, `can_be_number/2` | shim helpers → `lcm/3`, `msb/2`, … work |
| `format` | `builtins:parse_write_options/3` (via charsio) | marker-override (`format_args_cells`) → the scryer pack's `Format` shim; `format/2,3` work |
| `charsio` | `$char_type` | shim ctype dispatch (Scryer's full category vocabulary, ASCII + non-ASCII-as-alphabetic); `char_type/2` incl. `lower(L)`/`upper(U)` string forms |
| `files` | `$file_exists`, `$directory_files`, … | shim over our FS builtins (exists/delete/rename/mkdir/rmdir/mkpath/working_directory/directory_files), chars↔atom converted |
| `random` | `$maybe`, `$random_integer` | shim over `random_between/3` (Upper exclusive preserved) |
| `time` | `$cpu_now` | marker-override → native `time/1` + `sleep/1` (current_time/format_time lost) |
| `uuid` | `$crypto_random_byte` (via crypto) | shim byte source → `uuidv4_string/1` yields valid v4 UUIDs. **NOT cryptographically secure** — fine for ids, not for key material |
| `os` | `$getenv` | shim over the `$sys_getenv` builtin alias; `getenv/2` works |
| `when` | (collateral of the round-1 shim state) | works — post + fire validated |

**The import-shadow loop trap** (twice bitten, now designed around): a shim
emulation must NOT call a builtin by a name the library it serves EXPORTS —
imports win over builtins, so after `os.pl` loads, a shim call to `getenv/2`
resolves back into `os$getenv` → infinite loop (same for `charsio`'s
`char_type`, `files`' `working_directory`). Emulations therefore use either
names no Scryer library exports, self-contained code, or the `$sys_getenv` /
`$sys_working_directory` C# builtin aliases added for exactly this.

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
