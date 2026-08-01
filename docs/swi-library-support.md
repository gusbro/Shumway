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

## How a `use_module(library(X))` resolves — and what "no-op" means

`use_module(library(X))` resolves in this order:

1. **baked C# library** (`clpfd`, `clpr`, `coroutining`) — calls the engine's
   native implementation (`UseClpfd()` …). The SWI `.pl` is **never** consulted.
2. **native-override marker** — for a name on the override candidate list (`when`),
   the `.pl` is opened and scanned for a marker (`$eval_when_condition/2`); if
   present, the SWI file is **discarded** and the engine's native implementation is
   used instead. A user's own unmarked `when.pl` still loads normally.
3. **file on the library search path** — `X.pl` / `X.shum` is consulted and runs on
   our engine (the ordinary path for a genuine third-party source).
4. **dialect pack fallback** — a name our prelude/engine already covers
   (`lists`, `apply`, `pairs`, `ordsets`, `error`, `debug`, `aggregate`, `assoc`,
   `yall`, `apply_macros`) resolves to a **prelude-backed no-op** when no file is on
   the path: the predicates are already global, so importing just marks them
   available.

So a **"no-op"** load is one where the SWI library file is not what provides the
behaviour. There are two kinds, and this doc labels every entry with which:

- **no-op (native)** — we intercept and use **our own** implementation (steps 1, 2,
  or 4). The library *works*; the SWI source is redundant. This is the good kind.
- **no-op (unsupported)** — the library is inert because we **cannot** support what
  it needs (real threads, foreign C, dict syntax). It neither loads meaningfully nor
  runs. This is the bad kind, and is called out explicitly below.

The **Mechanism** column: `native` = no-op (native), our code is authoritative;
`file` = the SWI `.pl` loads and runs on our engine; `shim` = an SWI-specific
predicate supplied by our SWI compat shim; `unsupported` = no-op (unsupported).

## ✅ Supported (loads + representative predicates work)

| library | mechanism | exercised |
|---|---|---|
| `lists` | native (no-op) | `last/2`, `sum_list/2`, `max_list/2`, … (prelude-backed) |
| `apply` | native (no-op) | `foldl/4`, `include/3`, … with **named** goals (lambdas need `library(yall)`) |
| `pairs` | native (no-op) | `pairs_keys_values/3`, `pairs_keys/2`, `pairs_values/2` |
| `ordsets` | native (no-op) | `ord_union/3`, `ord_intersection/3` |
| `assoc` | native (no-op) | `list_to_assoc/2`, `get_assoc/3`, `put_assoc/4` (AVL trees) |
| `aggregate` | native (no-op) | `aggregate_all(count/sum, …)` |
| `error` | native (no-op) + shim | `is_of_type/2`, `must_be/2` (incl. `acyclic`), via the shim's `$is_char*` cluster |
| `dif` | native (no-op) | `dif/2` (Shumway's coroutining `dif`) |
| `when` | native (no-op, marker) | `when/2` — the SWI file is discarded, routed to our coroutining |
| `yall` | native (no-op) | `[X]>>Goal` lambdas via `call/N`, incl. 3-parameter (`>>/5`) |
| `gensym` | file + shim | `gensym/2` (via the engine's `flag/3` + shim) |
| `heaps` | file | `list_to_heap/2`, `get_from_heap/4` (priority queues) |
| `rbtrees` | file | `list_to_rbtree/2`, `rb_lookup/3` (red-black trees) |
| `charsio` | file + shim | `with_output_to(string(S), …)` |
| `sort` | native (no-op) | `msort/2`, `predsort/3` with a named comparator |
| `random` | file + shim | `random/1`, `random_between/3` via the `random(N)`/`random_float` arithmetic evaluables |
| `varnumbers` | file + shim | `numbervars/3`, `varnumbers/2`; `must_be(acyclic, _)` now shimmed |
| `ansi_term` | shim | `ansi_format/3` (format applied, colour attributes ignored) |
| `terms` | file | `term_variables/2` (a re-exported builtin) now resolves bare-global |

## 🟡 Mostly supported (loads, core works, a specific feature/predicate missing)

| library | mechanism | what fails | cause / fix |
|---|---|---|---|
| `option` | file | `option/3` on **dict** option-sets | uses `is_dict/1` (SWI dicts — structural) |
| `debug` | native (no-op) | `debug/3` print path | message system present; debug's own plumbing incomplete |
| `solution_sequences` | file | `distinct/1` | a dependency failed to load, so `distinct` is unresolved |
| `occurs` | file | `contains_term/2` raises `type_error(integer, _)` | internal traversal hits a numeric expectation — needs investigation |
| `nb_set` | file + shim | `$filled_array/4` | SWI functor-array kernel primitive — shim candidate |
| `nb_rbtrees` | file + shim | (API exercised was wrong) | loads; the non-backtrackable RB API needs a correct smoke, likely works given `nb_setarg` is shimmed |

## ❌ Not supported — no-op (unsupported): needs a language feature or backend we don't have

These do **not** work. They are inert because the underlying capability is absent —
not because we intercept them.

| library | why (unsupported) |
|---|---|
| `dicts` | SWI **dicts** — the `Tag{k:v}` term syntax is a language feature, not a predicate |
| `thread`, `thread_pool` | real **threads** — we have single-threaded shims only (`with_mutex`, message queues as FIFO) |
| `shlib` | loads a **foreign C** shared library |
| `prolog_stack` | prints the C-level **backtrace** (we stub `backtrace/1` as a no-op — it returns, but reports nothing) |
| `csv` | needs the stream-I/O + DCG plumbing exercised end-to-end (loads; unverified) |
| `intercept` | loads; signal/intercept machinery unverified |

## Status of the previously-recommended fixes (all landed)

1. **`random` arithmetic function** (`X is random(N)`, `random_float`) → `random`
   now supported. ✅
2. **`must_be(acyclic, _)`** in the prelude → `varnumbers` now supported. ✅
3. **`when` native override** via the `$eval_when_condition/2` marker → `when` now
   routed to our coroutining. ✅
4. **`ansi_format/3`** in the shim (format + ignore colour) → `ansi_term` now
   supported. ✅
5. **`terms` re-export** — a library that lists an export it does not define (the
   builtin `term_variables/2`) no longer maps to a dangling `terms$term_variables`;
   the import falls through to the bare-global builtin. ✅

Remaining investigation: `occurs`' `contains_term/2` numeric error.

Regenerate: `SHUMWAY_SWI_LIB=<dir> SHUMWAY_TRIAGE_OUT=<file> dotnet test
tests/Shumway.Tests.DialectInterop/ --filter FullyQualifiedName~SwiEndToEndValidation`.
