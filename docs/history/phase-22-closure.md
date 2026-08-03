# Phase 22 — Closure

**Status**: complete.

**Tagged**: `phase-22`.

Phase 22 takes the chunk-237-to-245 foreign-predicate machinery
from "works for in-process embedding" to "works across the
compile / link / run toolchain". Three chunks:

| # | Chunk | What it enables |
|---|---|---|
| 246 | mode-aware `[PrologPredicate]` | `out T` / `ref T?` parameters → `-` / `?` Prolog modes |
| 247 | `--foreign-dll` linker support | shumway-link reflects DLLs, records them in bundle, runtime auto-loads |
| 248 | linker fully resolves foreign calls | new `ExecuteBuiltin` opcode + linker rewrites — no compile-time hint needed |

End-state user workflow:

```bash
# C# side
public partial class Geo {
    [PrologPredicate("distance/2")]
    public static int Distance(int a, int b) => Math.Abs(a - b);

    [PrologPredicate("clamp/3")]
    public static void Clamp(out int result, int value, ref int? bound) {
        bound ??= 100;
        result = Math.Min(value, bound.Value);
    }
}

# Toolchain
shumway-compile my_app.pl                     # plain — no foreign-dll hint
shumway-link --foreign-dll Geo.dll \
             --exe my_app.exe my_app.shmo     # foreigns resolved here
./my_app.exe                                  # runs, with foreigns wired up
```

## Chunk 246 — mode-aware `[PrologPredicate]`

The chunks 237 / 242 typed signature mapped only "all `+` plus one
implicit `-`" (the return value). Functional shape — but Prolog
is about relations, with multi-output predicates and bidirectional
arguments commonplace. Chunk 246 adds the missing two modes by
piggybacking on standard C# parameter modifiers:

| C# modifier | Prolog mode | Generator emits |
|---|---|---|
| `T x` | `+` | `instantiation_error` check, then `FromTerm<T>` |
| `out T x` | `-` | declare local, user assigns, post-call `Unify(reg, ToTerm(x))` |
| `ref T? x` | `?` | if `VarTerm` then `null`, else `FromTerm<T>`; post-call unify when non-null |

A non-trivial generator bug surfaced and was fixed: the original
`?` decode used a `?:` ternary —
`int? __arg = __t is VarTerm ? default : FromTerm<int>(__t)` —
which the C# compiler types the ternary as `int` (from the
`FromTerm<int>` branch), boxing `default` (which becomes `0`)
back to `Nullable<int>(0)`. Replaced with an explicit `if`/`else`
so each branch assigns at the nullable type directly.

Validation: `out`/`ref` are incompatible with non-bool / non-void
return (return-as-output would conflict with parameter-as-output)
and with non-determinism (per-solution out-binding needs a
separate design pass).

10 new Chunk246Tests cover the `(-, +, ?)`, `(-, -, +, +)`,
`(+, ?, -)` and reference-type-? combinations, including the
input-bound-wrong → predicate-fails case.

## Chunks 247 + 248 — `--foreign-dll` across the toolchain

These two chunks landed together as one feature with two stages.

### The end state (chunk 248)

The compiler does NOT need to know about foreign DLLs at compile
time. It emits unresolved external references with generic
`Call` / `Execute` opcodes; the linker resolves them and rewrites
the opcode in place. Standard separation-of-concerns: compiler
emits objects with external references, linker decides how to
materialise each.

Mechanism: every call-site opcode now has a same-size builtin
variant the linker can swap to in place.

| Compiler emits | Linker swaps to (when target is a builtin) |
|---|---|
| `Call` (9 bytes: opcode + addr + perms) | `CallBuiltin` (9 bytes: opcode + builtinId + perms) |
| `Execute` (5 bytes: opcode + addr) | `ExecuteBuiltin` (5 bytes: opcode + builtinId) — **new in chunk 248** |

The new `Opcode.ExecuteBuiltin` (0x5B) is the tail-call counterpart
of `CallBuiltin`. Semantically equivalent to `CallBuiltin + Proceed`
but in 5 bytes instead of 10 — so the linker swap is one opcode
byte + one operand patch with no `Nop` padding. The interpreter
dispatches the builtin (no `TrimEnv` — we're returning), then
sets `Pc = Cp` for the tail return. `BuiltinReturnPc` is set to
`engine.Cp` so a backtrackable builtin's `ResumeAtReturnPc` lands
at the caller's continuation rather than looping back to its own
site.

The runtime side also gains:

- `PrologEngine.RegisterPredicates(Type, bool staticOnly)` — new
  overload. When `staticOnly` is `true`, instance methods and
  builtin-collisions are silently skipped instead of throwing.
  Used by the foreign-DLL auto-loader (which can't construct
  instances and shouldn't fail a whole DLL load because one type
  happens to collide with `assertz/1`).
- `PrologEngine.RegisterForeignAssembly(path)` — public; loads
  via `Assembly.LoadFrom`, walks types, registers each
  `[PrologPredicate]` static method.

### Bundle format V5

Bundle file format gains a foreign-assemblies trailer at the end
(after the per-entry payloads): a `uint32` count followed by
filename-only entries. Pre-V5 bundles read by a V5 runtime have
an empty list; pre-V5 readers stop at the trailer and don't see
it. `BundleWriter`, `BundleReader`, and the linker's
`SerialiseBundle` all updated in lockstep.

### Linker side (chunk 247)

`ShmoLinker.LinkConfig.ForeignAssemblies` accepts paths to .NET
DLLs. At link time the linker reflects each (defensively
handling `ReflectionTypeLoadException` partial-loader failures
via `SafeGetTypes`), collects every `[PrologPredicate]` indicator
into the resolved-reachability set, and records each assembly's
filename in the resulting `Bundle.ForeignAssemblies`. Empty / load-
failure surface as warning / error diagnostics.

`shumway-link` CLI: repeatable `--foreign-dll <path>` with the
same pre-existence check and full-path normalisation as `--output`.

### Runtime side (chunks 247 + 248)

`PrologEngine.LoadBundle` auto-registers every assembly the
bundle's `ForeignAssemblies` lists. Path resolution: adjacent to
the .shum file (the typical layout), then `AppContext.BaseDirectory`
(for `--exe` deployments), then the runtime's default
`Assembly.Load` probe. Missing DLL throws `FileNotFoundException`
with a clear message.

`ExecutableEmitter.Emit` gains an optional `foreignDllPaths`
parameter; when supplied, copies each DLL next to the produced
executable so the runtime's `AppContext.BaseDirectory` probe
finds them.

### What was rejected

Chunk 247 originally added a parallel `--foreign-dll` flag to
`shumway-compile` so the compiler could emit `CallBuiltin`
directly. The user pushed back on the architecture: a compiler
shouldn't need to know whether an external reference will be
linked to a native predicate or a foreign one. Chunk 248 removed
that flag entirely — the `ExecuteBuiltin` opcode closes the
size-asymmetry gap that forced the compile-time hint.

## Stats

- 3 chunks (246, 247, 248).
- 1 new opcode (`Opcode.ExecuteBuiltin`).
- 1 new bundle format version (V5).
- 1 new CLI flag (`shumway-link --foreign-dll`).
- 16 new Chunk246/247Tests in `tests/Shumway.Tests.Embedding/`.
- Full suite at phase close: 1837 embedding + 275 ISO conformance
  + 248 compiler + 105 interpreter + 423 core = 2888 tests, 0
  failures, 3 long-standing skips.
- No ADRs touched; no invariants modified.

## What's next

Phase 22 cleanly closes the foreign-predicate story across the
toolchain. Open threads if the next phase wants to continue the
embedding axis: `EnginePool`, async `IAsyncEnumerable<Solution>`,
SWI-style `ForeignContext.IsFirstCall` for non-`IEnumerable<T>`-
shaped non-det predicates. Or a return to engine internals:
ISO gap-closing, the recorded `retract/1` Blint bug, performance
work.
