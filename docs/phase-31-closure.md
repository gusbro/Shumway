# Phase 31 — Closure

**Status**: complete.

**Tagged**: `phase-31`.

Phase 31 opened with two user-named themes — the REPL "no anda muy bien" (line
editing) and a linker `--dll` to embed a `.shum` in a .NET app — and grew, through
review, into a native-interop correctness arc (a stale-doc audit that surfaced real
behavior gaps) plus the test-discipline fix the phase is partly remembered for.

| # | Area | What it adds |
|---|------|--------------|
| 1 | **`--dll` loadable class library** | `shumway-link --dll <path>` emits a .NET class-library DLL embedding the `.shum` + a generated factory `<Ns>.<Class>.CreateEngine()` returning a ready `PrologEngine` (via `FromBundle`). For a .NET app that *uses* Shumway, the counterpart of `--exe`. Namespace inferred from the DLL filename (`Greeter.dll`→`Greeter`), class `Bundle`; `--dll-namespace`/`--dll-class` override. Copies every Shumway/foreign dependency next to the output. `LibraryEmitter.Emit` is the API; verified end-to-end (a consumer app called the factory and ran a query). |
| 2 | **REPL line editing** | (a) **long-line wrapping** — replaced the chunk-253 horizontal-scroll-on-one-row with real multi-row wrapping (`LineView` repaints from a captured origin, cursor tracked across rows via the pure `CellRowCol`; scroll detected by `BufferHeight`); (b) **flicker fix** — hide the cursor across the repaint so it no longer flashes to column 0 each keystroke; (c) **ESC-cancel** — a background watcher fires a `CancellationTokenSource`; the engine aborts at its next safe point → `% Execution aborted.`. New `PrologEngine.QueryAll(Term, CancellationToken)`. |
| 3 | **ESC-cancel reaches backtrackable-builtin loops** | `between(0,BIG,X),fail` / `repeat,fail` re-satisfy through a builtin choice point and never cross a call-boundary `MaybeCollectHeap`, so they were uncancellable. Added `Engine.BacktrackSafePoint()` — a counter-throttled poll (interval 4096) — called in `TryBacktrack`'s **IL-choice-point branch only** (the resume path those builtins take via `PushBuiltinChoicePoint`). Clause-backtracking loops re-satisfy via `Call` and were already cancellable, so they pay nothing — back-to-back zebra 0.6% (within noise). |
| 4 | **Fixed 7 stale Interpreter tests + gate discipline** | 6 `CompoundOpcodeTests` asserted the pre-ADR-017 representation (Ref→on-heap-STR header) — corrected to the inline STR/LIS-in-register form; 1 `BytecodeInterpreterTests` predated Phase-28 deallocate frame reclaim. They had failed on baseline since Phase 25/28 because the routine "gate" lines omitted `Shumway.Tests.Interpreter`. Lesson recorded: the gate is **five** projects, all run before any phase close. |
| 5 | **embedded-native-c.md audit** | Four stale claims corrected against the code: (a) a native block in a `:- dynamic`/`:- visible` predicate **works** (the transform runs before dynamic routing) — was "rejected/inert"; (b) the **Status banner** "IL emission in progress" — IL shipped (§10 contradicted it), reftype tier is a separate shipped page; (c) §7 "deferred term/reftype tier" reworded (the `->`/`..`/`preftype` C member-access *syntax* is unsupported, not the tier); (d) §2/§5 "globals become static fields" — `char*`/reftype globals are holder slots (ADR-024), there is no static-field path. Each fix backed by a new test. |
| 6 | **Persistent scalar `:- c` globals** | A plain scalar global (`int counter;` / `double acc;`) had silently become a zero-init per-call local. Now it has Arity **static-storage** semantics: per-engine persistent storage (`_nativeGlobal{Int,Float}`), seeded on block entry and **written through** on every assignment (a later failure doesn't roll back). Threaded `NativeScalarGlobal(name, isFloat)` through all three backends (interpreter / runtime delegate / Tier-1 IL) and serialized in the `.shmo`/`.shum`, so persistence survives a source-stripped Release bundle / `--exe` and the Tier-0→Tier-1 promotion boundary. |
| 7 | **Undeclared-global is a consult error; `extern` = declared** | A scalar name in a block that is neither a Prolog var, a block-local, nor a declared `:- c` global is now a hard consult error (was a silent zero-init local). `extern int g;` counts as declared (CParser folds `extern`), the way to reference a global **defined in another module** — per-engine storage is keyed by the bare C name, so an `extern` reference and the defining module share storage (C-linkage), verified cross-module. |

## End state

- `--dll` produces a runnable class library used by a consumer .NET app.
- The REPL wraps long lines, doesn't flicker, and ESC cancels a runaway query —
  including `between/fail` / `repeat/fail`.
- Persistent scalar native globals with Arity semantics, error-on-undeclared, and
  `extern` cross-module sharing, across consult + bundle + `--exe` + all three
  native backends.
- `embedded-native-c.md` matches the code.
- **Full 5-project gate green**: Embedding 2572 / Compiler 302 / Core 432 /
  Interpreter 105 / ISO 277.

## Deliberately deferred (out of scope, not blocking)

- An `extern` scalar global that **no** module defines reads 0 (lazy default, like
  a C extern with no definition) rather than erroring — a link/load-time "undefined
  extern" check would need per-module global-definition tracking, not serialized
  today.
- ADR-024's **materializer ↔ dematerializer tier** (the physical blittable
  `Reftype` struct for the native-C P/Invoke trampoline case) remains the ADR's
  designed-for future tier — the target of the next phase.
