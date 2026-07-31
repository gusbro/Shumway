# SWI library support — end-to-end validation

What actually works when a real SWI-Prolog library is loaded on Shumway (under the
`swi` dialect, so the SWI compat shim auto-loads) and its predicates are
**exercised at runtime** — not merely parsed/loaded. This is the truthful measure;
the static missing-predicate survey (`library-missing-predicates-swi.md`)
over-counts gaps (module-local false positives) and cannot tell whether a loaded
library actually runs.

**Method.** `SwiEndToEndValidation` (opt-in, `SHUMWAY_SWI_LIB`) loads each library
and runs a representative smoke query. `SHUMWAY_TRIAGE_OUT=<file>` writes the raw
table. Measured against SWI-Prolog x64's `library/` (v9.x). This is a **curated
sample** of commonly-used libraries, not all 129 — for the rest, "loads" is from
the load sweep and structural categorisation.

Note on "loads (dep!)": the library itself loaded and works; a *dependency* printed
a `use_module … failed` warning (often another library we don't fully support),
which does not stop the library from functioning.

## ✅ Supported (loads + representative predicates work)

| library | exercised |
|---|---|
| `lists` | `last/2`, `sum_list/2`, `max_list/2`, … (prelude-backed) |
| `apply` | `foldl/4`, `include/3`, … with **named** goals (lambdas need `library(yall)`) |
| `pairs` | `pairs_keys_values/3`, `pairs_keys/2` |
| `assoc` | `list_to_assoc/2`, `get_assoc/3`, `put_assoc/4` (AVL trees) |
| `ordsets` | `ord_union/3`, `ord_intersection/3` |
| `error` | `is_of_type/2`, `must_be/2` (the shim's `$is_char*` cluster) |
| `aggregate` | `aggregate_all(count/sum, …)` |
| `gensym` | `gensym/2` (via the engine's `flag/3` + shim) |
| `heaps` | `list_to_heap/2`, `get_from_heap/4` (priority queues) |
| `rbtrees` | `list_to_rbtree/2`, `rb_lookup/3` (red-black trees) |
| `dif` | `dif/2` (Shumway's coroutining `dif`) |
| `yall` | `[X]>>Goal` lambdas via `call/N`, incl. 3-parameter (`>>/5`) |
| `charsio` | `with_output_to(string(S), …)` |
| `sort` | `msort/2`, `predsort/3` with a named comparator |

## 🟡 Mostly supported (loads, core works, a specific feature/predicate missing)

| library | what fails | cause / fix |
|---|---|---|
| `option` | `option/3` on **dict** option-sets | uses `is_dict/1` (SWI dicts — structural) |
| `random` | `random_between/3`, `random_permutation/2` | needs `random` as an **evaluable arithmetic function** (`X is random(N)`) — a small addition |
| `varnumbers` | `must_be(acyclic, _)` | our `must_be/2` lacks the `acyclic` type — **one-line shim fix** |
| `when` | `when/2` condition eval | needs the `$eval_when_condition/2` kernel helper — shim candidate |
| `ansi_term` | `ansi_format/3` | not defined — shim candidate (format, colour ignored) |
| `debug` | `debug/3` (the print path) | a dependency doesn't fully resolve; message system present but debug's own plumbing incomplete |
| `solution_sequences` | `distinct/1` | a dependency failed to load, so `distinct` is unresolved |
| `occurs` | `contains_term/2` raises `type_error(integer, _)` | internal traversal hits a numeric expectation — needs investigation |
| `nb_set` | `$filled_array/4` | SWI functor-array kernel primitive — shim candidate |
| `nb_rbtrees` | (API exercised was wrong) | loads; the non-backtrackable RB API needs a correct smoke, likely works given `nb_setarg` is shimmed |
| `terms` | `term_variables/2` resolves to `terms$term_variables` | a module re-export quirk (the library exports the builtin) |

## ❌ Not supported (structural — needs a language feature or backend we don't have)

| library | why |
|---|---|
| `dicts` | SWI **dicts** — the `Tag{k:v}` term syntax is a language feature, not a predicate |
| `thread`, `thread_pool` | real **threads** — we have single-threaded shims only (`with_mutex`, message queues as FIFO) |
| `shlib` | loads a **foreign C** shared library |
| `prolog_stack` | prints the C-level **backtrace** (we stub `backtrace/1` as a no-op) |
| `csv` | needs the stream-I/O + DCG plumbing exercised end-to-end (loads; unverified) |
| `intercept` | loads; signal/intercept machinery unverified |

## Recommended next fixes (small, promote a library each)

1. **`random` arithmetic function** (`X is random(N)`, `random_float`) → promotes
   `random` to fully supported.
2. **`must_be(acyclic, _)`** in the shim → promotes `varnumbers`.
3. **`$eval_when_condition/2`** kernel helper → promotes `when`.
4. **`ansi_format/3`** in the shim (format + ignore colour) → promotes `ansi_term`.
5. Investigate `occurs`' `contains_term/2` numeric error.

Regenerate: `SHUMWAY_SWI_LIB=<dir> SHUMWAY_TRIAGE_OUT=<file> dotnet test
tests/Shumway.Tests.DialectInterop/ --filter FullyQualifiedName~SwiEndToEndValidation`.
