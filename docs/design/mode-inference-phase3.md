# Mode-Aware Compilation (Phase 3 Design)

This document specifies the design intent for Phase 3 of Shumway's mode-aware compilation. It is a **forward-looking design** for code that will be written in Phase 3; it does not describe v1 behavior. The directive `:- mode/1` is accepted and stored in v1 (per ADR-012), but no code generation exploits it until Phase 3.

This document exists in v1 so that:

1. Source code written with mode declarations today is informed by what they will mean.
2. Phase 1 implementation choices remain compatible with Phase 3 (e.g., the IL emitter architecture supports specialization).
3. Phase 3 implementors have a starting design to refine.

## Goals of Phase 3

The performance target is to make deterministic predicates run within **2-3×** of equivalent hand-written C# code. For non-deterministic predicates, the goal is to be **within 50%** of the v1 baseline (no regression).

Mode-aware compilation achieves this by:

- Eliminating choice point allocation for `det` and `semidet` modes.
- Eliminating trail entries for bindings that won't survive failure.
- Specializing on argument types when declared (e.g., `+integer`).
- Inlining the entire body of a `det` predicate into the caller's IL.

## Mode declarations recap

```prolog
:- mode foo(+, -) is det.
:- mode foo(+, +) is semidet.
:- mode foo(-, +) is nondet.

:- mode bar(+integer, -integer) is det.   % typed input (Phase 3 extension)
```

Indicators (v1):
- `+`: bound at call.
- `-`: unbound at call.
- `?`: either.

Indicators (Phase 3 additions):
- `+atom`, `+integer`, `+list`, `+ground`, `+nonvar`: typed input.
- `-atom`, `-integer`, ...: typed output (rare; usually inferred).

Determinism (v1 + Phase 3):
- `det`: exactly one solution.
- `semidet`: zero or one.
- `multi`: one or more.
- `nondet`: zero or more.

## Specialized compilation per mode

For each mode declaration, the compiler generates a **separate IL method**. The original generic predicate remains as a fallback for unspecified-mode calls.

```csharp
// Original (v1, mode-agnostic):
public static bool Foo_2(Engine engine, int argBase);

// Phase 3 specializations:
public static bool Foo_2_PlusMinus_Det(Engine engine, int argBase);
public static bool Foo_2_PlusPlus_SemiDet(Engine engine, int argBase);
public static bool Foo_2_MinusPlus_NonDet(Engine engine, int argBase);
```

The specialized methods:

- Take the same signature (Engine, argBase, return bool).
- Implement the predicate's body assuming the mode pattern holds.
- Are not robust to mode violations: if called in a different mode, behavior is undefined (debug builds may check; release builds skip).

## Call site dispatch

At each call site, the compiler determines which specialization to invoke:

### Static dispatch (compile-time)

If the compiler can prove the call's argument modes statically (via dataflow analysis or the caller's own mode declarations), it directly emits a call to the matching specialization:

```prolog
:- mode caller(+) is det.
caller(X) :- foo(X, Y), bar(Y).

% Compiler reasoning:
%   X is bound at call entry.
%   foo(X, Y) with X bound, Y not yet seen → mode (+, -).
%   Compile call to: Foo_2_PlusMinus_Det
```

### Dynamic dispatch (runtime)

When the mode can't be determined statically, the compiler emits a dispatcher that checks at runtime and invokes the appropriate specialization:

```csharp
public static bool Foo_2_Dispatcher(Engine engine, int argBase)
{
    var arg0 = engine.Deref(engine.Registers[argBase + 0]);
    var arg1 = engine.Deref(engine.Registers[argBase + 1]);
    
    bool arg0Bound = arg0.Tag != Tag.Ref;
    bool arg1Bound = arg1.Tag != Tag.Ref;
    
    if (arg0Bound && !arg1Bound)
        return Foo_2_PlusMinus_Det(engine, argBase);
    if (arg0Bound && arg1Bound)
        return Foo_2_PlusPlus_SemiDet(engine, argBase);
    if (!arg0Bound && arg1Bound)
        return Foo_2_MinusPlus_NonDet(engine, argBase);
    
    return Foo_2_Generic(engine, argBase);  // fallback
}
```

The dispatch overhead is a few branches; specialized bodies more than make up for it.

For predicates with many mode declarations, the dispatcher can use a more efficient lookup (precomputed branches based on a hash of the mode signature).

## Optimizations enabled by each determinism

### Det optimizations

A `det` predicate has exactly one solution. Implications:

- **No choice point allocation**: the `try_me_else` family is skipped.
- **No trail entries**: bindings made by the predicate body don't need to be reversible (the predicate either succeeds, returning bindings, or never returns).

  Wait, this is wrong. If the *caller* fails after a `det` predicate returns successfully, the caller's backtracking must undo the `det` predicate's bindings.
  
  Correction: the trail entries are still needed (so the caller can undo). But within the `det` predicate's body, intermediate bindings that don't survive to the predicate's output don't need trailing.

- **Aggressive inlining**: a `det` call can be inlined into the caller, eliminating the call overhead entirely.
- **No CP cleanup**: no need to compact trail after the call.

The combined effect: a `det` predicate runs almost like a function call in C#.

### Semidet optimizations

A `semidet` predicate has zero or one solution. Implications:

- **No CP allocation** (like det): only one clause body runs; if it fails, no alternatives.
- **Failure path is a single branch**: on failure, jump directly to the caller's fail handler.
- **Trail entries still needed**: even if zero solutions, intermediate failures must unwind bindings.

### Multi optimizations

A `multi` predicate has one or more solutions. Implications:

- **Some CP optimization**: the first solution doesn't need a CP if we know there are more (subsequent solutions are produced on backtrack). However, the CP must be created before returning the first solution.
- **Trail compaction can be specialized**: knowing that all alternatives produce at least one solution, certain bookkeeping can be elided.

In practice, `multi` is less common; the optimizations are less impactful than for det/semidet.

### Nondet (no special optimization)

Standard WAM execution. This is the default for predicates without declarations.

## Type specialization

For typed input indicators (e.g., `+integer`), the body is compiled assuming the argument is of the declared type:

```prolog
:- mode add(+integer, +integer, -integer) is det.
add(X, Y, Z) :- Z is X + Y.
```

Compiled body for `Add_3_PlusPlusMinus_Det`:

```il
// Skip type check; assume X[0] and X[1] are integers.
// X[2] is unbound (by mode).

// Read X[0] as int directly
ldarg.0
ldfld Engine::_registers
ldarg.1
ldelem Cell
call Cell::get_AsInt   // assume INT tag

// Read X[1] as int
ldarg.0
ldfld Engine::_registers
ldarg.1
ldc.i4 1
add
ldelem Cell
call Cell::get_AsInt

// Add
add

// Build INT cell
call Cell::Int

// Bind X[2]
ldarg.0
ldarg.0
ldfld Engine::_registers
ldarg.1
ldc.i4 2
add
ldelem Cell
call Cell::get_AsHeapIndex     // X[2] is REF, get its heap index
// ... bind ...
```

Compare with the generic version: skips type checking, skips boxing/unboxing, skips dispatch.

### Runtime mode checking (strict mode)

In `strict_mode_checking` configuration, the entry to a specialized method verifies the mode:

```csharp
public static bool Add_3_PlusPlusMinus_Det(Engine engine, int argBase)
{
    if (!engine.Registers[argBase + 0].IsInt)
        throw new ModeViolationException();
    if (!engine.Registers[argBase + 1].IsInt)
        throw new ModeViolationException();
    if (!engine.Registers[argBase + 2].IsRef)
        throw new ModeViolationException();
    
    // ... fast body ...
}
```

This is enabled in development/testing. Disabled in production (the type/mode is assumed).

## Mode inference (Phase 3+)

For predicates without `:- mode` declarations, the compiler can attempt to **infer** modes from usage patterns:

### Static inference

Analyze call sites: if `foo/2` is always called with the first argument bound, infer `(+, ?)`.

Limitations:
- Cross-module calls may not be visible.
- Dynamic calls (via `call/1`) escape inference.

### Profile-guided inference

In long-running applications, the engine can record observed modes at runtime:

```csharp
public class ModeObservation
{
    public int CallCount;
    public Dictionary<ModePattern, int> ObservedModes;
}
```

After a threshold of observations, the engine compiles specialized versions for the most common modes.

This is more aggressive than declaration-based specialization but adds complexity. Phase 4+ feature.

## Interaction with indexing

Phase 1's first-argument indexing already specializes dispatch by the first argument's type. Phase 3's mode-aware compilation generalizes this to all arguments.

When both are active:
- Indexing dispatches based on the runtime value of the first argument.
- Within an indexed branch, the body is compiled with the mode information.

For example, `:- mode foo(+atom, -) is det` and an indexed dispatch on `foo(specific_atom, X)`:

- Indexing routes to the appropriate clause based on the atom.
- The clause body is the mode-specialized version (no type checks, no choice points for det).

## Failure handling in det code

`det` predicates declare they always succeed. If the body actually fails (e.g., a unification fails internally), this is a programmer error or a violation of the declaration.

Behavior:
- **Debug/strict mode**: raises `det_mode_violation_error`.
- **Production**: undefined behavior (may produce wrong results or crash).

This places trust in the declaration. Linters and testing should validate that det predicates are actually deterministic.

## Det predicate inlining

The biggest win from det specialization is inlining. A predicate like:

```prolog
:- mode add(+, +, -) is det.
add(X, Y, Z) :- Z is X + Y.
```

When called from:

```prolog
:- mode compute(+, +, -) is det.
compute(X, Y, R) :- add(X, Y, S), S2 is S * 2, R = S2.
```

The compiler can inline `add/3` into `compute/3`:

```il
// compute body, with add inlined:

// add(X, Y, S):
//   read X as int, read Y as int, add, store in S as int

// S2 is S * 2:
//   read S as int (already in a local), multiply by 2, store in S2

// R = S2:
//   bind R to S2's value
```

The entire body of compute becomes a tight sequence of arithmetic operations and one bind, indistinguishable from hand-written C#.

The inlining decision is based on heuristics:
- The callee is det.
- The callee's body is small (< 50 IL instructions, configurable).
- The call site is not in a megamorphic context.

## Backwards compatibility

Phase 3 code coexists with v1:

- Predicates without mode declarations: compiled with v1 strategy.
- Predicates with mode declarations: compiled with mode-aware strategy.
- Calls between them: dispatcher mediates.

Programs run correctly regardless of when mode declarations were added.

## Test strategy

Phase 3 tests:

- **Det predicate execution**: verify a det predicate's body runs without CP creation or trail entries beyond what's needed for the caller.
- **Det predicate inlining**: verify that a det callee is inlined into a det caller; check IL output.
- **Type specialization**: verify that `+integer` arguments are accessed as ints directly, no type dispatch.
- **Mode dispatch**: call a multi-mode predicate in various modes, verify the correct specialization is invoked.
- **Mode violation detection (strict)**: call a predicate in an undeclared mode, verify error.
- **Mode violation tolerance (production)**: with strict mode off, undeclared mode falls back to generic.
- **Cross-validation**: every Prolog test runs with Phase 3 enabled and disabled, comparing results.
- **Benchmark**: measure speedup of det predicates vs v1. Target: 5-10× faster on hot code.

## See also

- ADR-012 (Mode Inference Roadmap): v1 acceptance and storage of mode declarations.
- ADR-011 (IL Compiler Architecture): the IL emission infrastructure.
- Mercury language documentation: the inspiration for many of these ideas.
