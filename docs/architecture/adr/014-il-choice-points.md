# ADR-014: IL Choice Points (Tier-1 Multi-clause ABI)

## Status

Accepted (Phase 1) — and long since built on: Tier-1 IL emits multi-clause
predicates over this ABI (including fully indexed dispatch, Phase 20), region
compilation composes whole predicate closures over it (Phase 29), and
backtrackable builtins and the debugger's clause re-enter use the same IL
choice points. This ADR is the ABI those features stand on.

## Context

The Tier-1 IL compiler (ADR-011) emits a `PredicateDelegate` whose
shape used to be:

```csharp
public delegate bool PredicateDelegate(Activation engine);
```

This is fine for single-clause facts — the delegate either succeeds
(returns `true`, the engine continues at `CP`) or fails (returns
`false`, the engine backtracks). Multi-clause predicates, though, need
a way to expose alternative clauses to the engine's choice-point
machinery so that an external `;/2`, `findall/3`, or simple Prolog
backtracker can enumerate every solution.

The WAM does this with `try_me_else` / `retry_me_else` / `trust_me`
instructions backed by stack-allocated choice-point frames whose `BP`
field is a bytecode address. The engine's `TryBacktrack` reads `BP`,
sets `PC := BP`, and the instruction at that address (a `retry_me_else`
or `trust_me`) restores engine state and dispatches the next clause.

IL-compiled predicates don't live in the bytecode address space, so
storing a bytecode `BP` in their CP would be a lie — the engine has
nothing to jump to. Two alternatives:

1. **Re-emit every IL predicate's "next clause" entry as a tiny
   bytecode stub.** Each retry would jump to a stub, call the IL
   delegate at the right cursor, and return. Doubles the number of
   bytecode + IL artifacts the runtime juggles and adds an opcode
   (which would in turn need its own ADR per the ADR-006 process).

2. **Re-enterable IL delegates.** Change the delegate signature to
   accept a "clause cursor" that names which alternative to run; teach
   the engine to keep IL CPs in a side table so backtrack pops the
   frame, restores state, and re-invokes the delegate at the cursor
   from the side table. The "next clause" logic stays inside the IL.

Option 2 keeps everything in IL — no new opcodes, no bytecode stubs —
and the side table is one Dictionary on the engine.

## Decision

### ABI

The delegate becomes:

```csharp
public delegate bool PredicateDelegate(Activation engine, int clauseCursor);
```

| `clauseCursor` | Meaning |
|----------------|---------|
| `0` | Fresh call from a `Call` / `Execute` opcode. The IL runs the first clause and, if there are more, pushes an IL CP for cursor `1` before its head-match. |
| `N > 0` | Re-entry after a backtrack popped an IL CP. The IL switches on the cursor to find the right clause body, pushes a CP for `N+1` if there are still more clauses, and runs the selected clause. |

Each clause's IL body is responsible for *its own* head-match-then-body
sequence. Failure in the head-match returns `false` so the engine
backtracks (which may re-enter the same delegate with the next
cursor).

### Activation API

Three new public surfaces on `Activation` (the per-query execution object;
`Engine` is its informal name in older text):

```csharp
public const int IlChoicePointSentinelBp = -1;
public void PushIlChoicePoint(Func<Activation, int, bool> del, int nextCursor, int arity);
public bool TopChoicePointIsIl { get; }
public (Func<Activation, int, bool> Del, int Cursor) PopIlChoicePointAndRestore();
```

`PushIlChoicePoint` calls the existing `PushChoicePoint` with the
sentinel BP, then records `(delegate, nextCursor)` in a private
`Dictionary<int, …>` keyed by the new CP frame's stack index. The CP
frame layout is identical to a bytecode CP — same offsets, same trail
markers — so the existing trail-unwind path keeps working unchanged.

`PopIlChoicePointAndRestore` does what `TrustMe` does for bytecode
CPs: restores registers / `_e` / `_cp` / heap top / trails, frees the
frame, and removes the side-table entry. The caller (the
interpreter's `TryBacktrack`) then runs the returned delegate at the
returned cursor.

### Interpreter integration

`TryBacktrack` is now a small loop:

```csharp
while (_engine.B >= 0)
{
    if (_engine.TopChoicePointIsIl)
    {
        var (del, cursor) = _engine.PopIlChoicePointAndRestore();
        if (del(_engine, cursor))
        {
            _engine.SetPc(_engine.Cp);
            return true;
        }
        continue;   // IL clause failed, look for the next CP.
    }
    // standard bytecode CP path: read BP, set PC, return true.
    ...
}
return false;
```

The bytecode path is unchanged for non-IL CPs. The hot path adds one
dictionary-presence check per backtrack — negligible compared to the
heap / trail restoration that runs alongside it.

### Why the side table instead of inlining

The CP frame is a stack of `Cell`s — value types only, no managed
references. Stashing a `Func<Activation, int, bool>` on the stack would
mean GC-rooting the delegate through a managed array, which the
heap / register / stack are deliberately *not*. The side table keeps
managed references where managed references belong (a regular
`Dictionary`) without polluting the value-only stack representation.

The dictionary key is the CP frame's stack index, which is unique for
the lifetime of the frame (the frame can't move) and naturally cleans
up when the entry is removed on pop. Even on engine reset / abrupt
backtrack-to-zero, the side table sits next to the stack and gets
cleared if the engine instance is discarded.

## Consequences

### Positive

- Multi-clause IL predicates can use the same choice-point semantics
  the bytecode side already has. External `findall/3`, `;/2`,
  user-driven backtracking all keep working uniformly.
- Zero new opcodes. ADR-006's "adding a new top-level opcode is a
  major decision" budget is preserved for genuinely new bytecode-level
  features.
- The CP frame layout is unchanged — bytecode and IL CPs share the
  same trail-marker conventions, so trail unwind keeps working
  without case analysis.
- The ABI change is small and mechanical: every existing IL call site
  passes `0` (fresh entry), and the IL emission for single-clause
  predicates is otherwise untouched.

### Negative

- The dictionary lookup on every backtrack is hot-path overhead, even
  for programs that never compile any IL. Profiling on real workloads
  will tell us whether to swap in a faster check (sentinel-BP check
  inline; flag bit in the CP itself; etc.).
- The "the IL emits its own retry logic via cursor" pattern is harder
  to reason about than the bytecode's "retry_me_else updates BP".
  Newcomers reading the IL source for a 5-clause predicate will see a
  5-way switch and need to trace the cursor / CP interactions to
  understand which alternative runs next.

### Out of scope for this ADR (since delivered)

At decision time the IL compiler did not yet emit multi-clause predicates —
only the engine + interpreter + ABI were wired. The emission arrived as
planned and grew far past it: `try_me_else` chains, fully indexed
`switch_on_*` dispatch with O(1) key lookup (Phase 20), and region
compilation (Phase 29) all emit over this ABI. It was never a major decision
in the decision-policy sense — no opcode space, cell layout, or engine state
changed; just IL text.

## References

- ADR-005: Stack Layout (CP frame layout that IL CPs reuse verbatim).
- ADR-011: IL Compiler Architecture (Tier-1 promotion path).
- `src/Shumway.Core/Activation.Tier1.cs` — `PushIlChoicePoint`,
  `PopIlChoicePointAndRestore`, `TopChoicePointIsIl`.
- `src/Shumway.Interpreter/BytecodeInterpreter.cs` — `TryBacktrack`
  loop with the IL-CP dispatch.
- `src/Shumway.Compiler.Il/PredicateDelegate.cs` — the new
  `(Activation, int) → bool` shape.
