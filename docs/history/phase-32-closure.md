# Phase 32 — Closure

**Status**: complete.

**Tagged**: `phase-32`.

Phase 32 delivers ADR-024's deferred **materializer ↔ dematerializer tier** — whole-term
interop for the case the Phase-30 cursor tier doesn't cover: when C# is only a
**trampoline to a native C function** (P/Invoke, can't touch the Shumway heap) or a .NET
method that wants a struct **snapshot**. The work was driven by the `testProc` reference
corpus, whose uniform pattern is `fill_par(Term,&parNref)` → `ret='native_fn'(…,parNref)`
→ `reftype_term(Term,&parNref)`. Eighteen commits (`384d5f8` … `cd15130`).

The settled design: a **`:- native fn/N`** directive marks a function as
materializer-protocol; at the call site Shumway decides **once and caches** whether it
resolves to a registered C# interop method (→ managed `Reftype` snapshot) or to a native
library export (→ P/Invoke into a physical `t_reftype`). `fill_par` / `reftype_term` stay
the Phase-30 cursor builtins; materialize/dematerialize **wrap** the `:- native` call.

| # | Area | What it adds |
|---|------|--------------|
| 1 | **Managed snapshot core** | `Shumway.Embedding.Reftype` — the managed snapshot + the `Reftype.Codes` ntype contract; `Materialize(Term)→Reftype` (recursive over functor args) and `Dematerialize(Reftype)→Term` (atom/string→atom, undef→fresh var). |
| 2 | **Blittable native form** | `NativeReftype` — `Materialize(Term, Encoding?)→IntPtr` builds the 32-byte `t_reftype` graph (`AllocHGlobal`; cint 32-bit; `pars` = `t_reftype*` array), `Dematerialize`, and `Free` (the `freepar` walk). Layout: `int64 ntype; int64 nelem; t_reftype** pars; union crep`. *(Correction 2026-08: originally recorded as "identical to Arity", but the Arity material we hold does not declare the struct at all, and the reference corpus's `:- c` prototypes treat `reftype` as opaque. The layout is Shumway's own contract; native C interops by recompiling against our declaration.)* |
| 3 | **Configurable text encoding** | `char*` text uses `PrologEngine.NativeTextEncoding` (default **UTF-8**, set per engine; byte-oriented), threaded through both native backends. |
| 4 | **`:- native` directive + managed-snapshot backend** | A `:- native` fn whose C# interop method takes a `Reftype` gets a materialized snapshot of the reftype global, mutated in place, dematerialized back after the call. New `fx 1150` prefix operator; `_nativeFunctions`/`IsNativeFunction`. |
| 5 | **P/Invoke backend** | A `:- native` fn that does **not** resolve to a C# method is a real native export of a registered library (`UseNativeLibrary`). Resolution is **cached per functor**; `NativeCall` derives the marshalling signature from the `:- c` prototype and invokes by pointer via a cached **cdecl `calli`** (`DynamicMethod`). |
| 6 | **Native-allocator mode ("C builds a list")** | When the library exports the reftype allocator API (`newreftype`/`freepar`/`getargp`/`setcflt`), Shumway materializes + frees **through it** so a native function that allocates sub-nodes (builds a list/term) and the materializer share one heap. Auto-detected by `UseNativeLibrary`. |
| 7 | **Parameter / return marshalling** | `int`/`short`/`long`/`double` by value; `reftype`/`preftype` materialized; **`char*` input** (Prolog string → NUL-terminated native memory, freed after); **`char*` return** (raw pointer integer, read with `make_prolog_string`, which now reads native memory from an integer source); **out-scalar** `short*`/`int*`/`long*`/`double*` (`&local`, written back into the block-local); **`char**` out-string** (`&local`, native side writes a borrowed `char*`, decoded into the block-local). |
| 8 | **IL emit for both backends** | Both `:- native` backends compile to IL instead of bailing the block to the interpreter. Managed-snapshot calls emit inline (materialize → call → write-back). P/Invoke calls dispatch through the cached `calli` invoker over pre-evaluated args (`PInvokeFromIl`, since Expression trees can't emit `calli`), so scalar work *around* a native call runs as IL too. Out-scalar and `char**` write-backs thread through a read-back array the emitted IL stores into its block-locals. All proven via `NativeBlockCompiler.CompiledCount`. |
| 9 | **`--native-dll` + bundle serialization** | `shumway-link --native-dll <path>` records the library in the bundle; `LoadBundle` auto-loads it (probing adjacent to the bundle / exe), and the library is copied next to the output for **both** `--exe` (`ExecutableEmitter`) and `--dll` (`LibraryEmitter` — a Phase-close fix; it had dropped native DLLs). The `:- native` indicators + `:- c` prototypes travel in the `.shmo`/`.shum`, so a **source-stripped** Release bundle resolves them with no source. |
| 10 | **Memory ownership model** | Documented in [generic-term-interop §10c](../guide/generic-term-interop.md). Shumway owns + frees (call-scoped): char* inputs, reftype materializations, out-scalar slots, the `char**` cell. **Borrowed** (native-owned, copied out, never freed): char* returns and the char* written into a `char**` — matching Arity's static/internal-buffer convention. A `malloc`'d return leaks by design; caller-owns would need an explicit paired-free annotation. |
| 11 | **Native-library lifetime + thread-safety** | A native library is loaded **once per path for the process** (a static lock-guarded table) and shared across engines, instead of one `NativeLibrary.Load` per engine (which leaked an OS refcount per engine). Documented in [§10e](../guide/generic-term-interop.md): `:- native` calls are **not serialized** (parallel multi-engine callers need a reentrant library; borrowed static-buffer returns race), and native global state is process-global and not reset between engines. |

## End state

- A `:- native fn/N` function marshals whole Prolog terms to/from a physical Arity
  `t_reftype` (native P/Invoke) or a managed `Reftype` snapshot (.NET interop), with the
  call-site mechanism chosen once and cached.
- Full parameter/return coverage — scalar, reftype, char* in, char* return, out-scalar,
  char** out-string — in **both** the interpreter and Tier-1 IL.
- The deployment chain is verified end-to-end in a shipped binary: a source-stripped
  Release bundle over a real native DLL, linked `--native-dll`, runs correctly as both a
  native **`--exe`** (`app.exe` prints `42`) and a **`--dll`** consumed by a .NET app
  (`Nrt.Bundle.CreateEngine()` → `DLL-OK:42`).
- The memory-ownership and native-library lifetime / thread-safety contracts are written
  down in `generic-term-interop.md` §10.
- **Full 5-project gate green**: Embedding 2614 / Compiler 302 / Core 432 /
  Interpreter 105 / ISO 277.

## Deliberately deferred (out of scope, not blocking)

- **Caller-owns return strings.** A native function that returns `malloc`'d memory the
  caller must free leaks today — return strings must be *borrowed* (static / internal /
  pooled on the native side). Supporting caller-owns would need an explicit paired-free
  annotation (e.g. `:- native_free fn/1`), not added until a real case requires it.
- **Deeper pointer params.** `int**` / pointer-to-pointer of non-char types are rejected
  (loud error); `char**` is the only depth-2 form supported (out-string).
- **No native-library unload.** The mapping lives to process exit (no `Dispose`/`Free`
  hook). With load-once-per-process the per-engine refcount leak is gone, so an explicit
  resource-close was judged unnecessary.
