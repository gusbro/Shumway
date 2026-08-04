# IL emission

How the Tier-1 compiler lowers WAM bytecode to .NET CIL. The authoritative
source is `src/Shumway.Compiler.Il/` (`IlPredicateCompiler` and its partials);
[`il-region-compilation.md`](il-region-compilation.md) documents the dispatch
and region model this emission sits inside. It complements ADR-011.

## The compiled method

A promoted predicate compiles to a delegate:

```csharp
public delegate bool PredicateDelegate(Activation engine, int clauseCursor);
```

- **`engine`** is the per-query `Activation` (there is no `Engine` class). Heap,
  registers, trail, unification and the choice-point stack are all methods /
  state on it (`Activation.UnifyOps.cs`, `Activation.Frames.cs`,
  `Activation.Tier1.cs`).
- **`clauseCursor`** is a resume cursor, **not** a register base. Arguments are
  read from the argument registers via `engine.GetRegister(n)` /
  the deref+unify helpers; they are not passed as an `argBase` offset.

Clauses of a multi-clause predicate are emitted as **cursor-labelled blocks
within the one delegate**, not as one method per clause. A choice point is
pushed with `PushIlChoicePoint(delegate, cursor, arity)`; on backtracking the
dispatch loop re-invokes the delegate at the saved cursor.

## Calls

A non-tail Call is **threaded continuation**, not an inline cache: the emitter
sets `Cp = EncodeResumeMarker(functorId, cursor)` and `IlTailCallPending`, then
returns to the interpreter loop, which invokes the callee and — when it proceeds
— re-enters this method at the resume cursor. A tail Call just sets `Pc` and
returns. This keeps the C# stack O(1) regardless of Prolog depth. (The
call-site inline cache once sketched in [`inline-caching.md`](inline-caching.md)
was never built.)

## Per-opcode lowering

Each WAM opcode lowers to a short CIL sequence calling the corresponding
`Activation` primitive:

- **get / put / unify** → `Deref`, `Bind`, the `Unify*` family, and the
  read/write-mode structure/list builders (`Activation.UnifyOps.cs`), including
  the inline compound (ADR-017) and reserved-write nested-build paths
  (ADR-019/020).
- **arithmetic** → the `a_eval_*` RPN evaluator and the fused integer fast lane
  (ADR-018), emitted with try/catch-free fast paths where the operands are known
  integers.
- **control / choice / cut** → `Allocate`/`Deallocate`, `PushIlChoicePoint` and
  the cursor machinery, `Cut`/`SoftCut`/`GetLevel`.
- **indexed dispatch** → `switch_on_term` / `switch_on_arg` lower to an O(1) key
  lookup over the IL index graph with bucket backtracking
  (`IlIndexedDispatch.cs`, `IlIndexGraph.cs`).
- **backtrackable builtins** and **`call/N` meta-calls** are IL-emittable via
  `BuiltinReturnPc` and `IlMetaCallHelper`.

For the exact CIL per opcode, read the emit partials in
`src/Shumway.Compiler.Il/` — and dump what the compiler actually generates with
`shumway-compile --dump-il` (see the user guide).
