# ADR-005: Stack Layout

## Status

Accepted (Phase 1).

## Context

The WAM stack holds two kinds of frames that must coexist:

1. **Environment frames (E-frames)**: hold permanent variables (`Y1`, `Y2`, ..., `Yn`) for a clause invocation, plus the continuation environment (`CE`) and continuation point (`CP`) needed to return.

2. **Choice points (B-frames)**: hold the state needed to retry an alternative clause: snapshots of arguments, registers, heap top, trail tops, the previous choice point, the address of the next clause to try.

The WAM literature presents two main approaches:

- **Unified stack** (classical WAM): both frame types live in the same stack, interleaved according to creation order. Last call optimization (LCO) and environment trimming benefit from this layout.
- **Separate stacks**: one for environments, one for choice points. Simpler reasoning but loses some optimizations and complicates LCO.

Real-world implementations like SWI-Prolog and GNU Prolog use the unified stack. The pattern is well-documented and the optimizations matter for production performance.

Several lower-level decisions also need to be made:

- Stack as `Cell[]` (consistent with heap) vs. typed `struct[]` (more compile-time type safety).
- Layout of `CE`/`CP` and other control words: raw `long` or tagged cells?
- How many registers are snapshotted in a choice point (just arguments, or also live X registers)?
- How the stack is scanned by the atom GC.

These decisions affect every part of the engine that touches the stack: call/return, choice point creation, backtracking, cut, and the atom GC's mark phase.

## Decision

Shumway uses a **single unified stack as a `Cell[]`**, with environments and choice points interleaved.

### Stack representation

```csharp
private Cell[] _stack;
private int _stackTop;

// Engine registers
private int _e;     // current environment base index
private int _b;     // current choice point base index (-1 if none)
private int _p;     // program counter (index into bytecode)
private int _cp;    // continuation point (next instruction after current call)
private int _hb;    // heap boundary at the last choice point creation

// Argument and temporary registers (X1..Xn, A1..An share this array)
private Cell[] _registers;
```

The same array stores environment frames and choice point frames, distinguished by which engine register points at them.

### Environment frame layout

When `allocate N` is executed for a clause with `N` permanent variables:

```
Stack offset (from envBase = _e):
  envBase + 0: CE (long, index of caller's environment)
  envBase + 1: CP (long, code address to return to)
  envBase + 2: Y1 (Cell)
  envBase + 3: Y2 (Cell)
  ...
  envBase + 1 + N: Yn (Cell)
```

`CE` and `CP` are stored as raw `long` values in cells. The interpreter knows the layout and accesses these slots without consulting tags. (They are not interpreted as Prolog values; their tag bits, if examined, would look like ATOM or some other meaningless tag, but the interpreter never reads them as cells.)

The variable slots `Y1..Yn` start unbound (REF self-pointing). They are written by `get_variable Y i, Aj` or similar instructions in the clause body.

### Choice point frame layout

When `try_me_else <next>` creates a choice point for a predicate with `n` arguments:

```
Stack offset (from cpBase = _b):
  cpBase + 0:           n (number of args, long)
  cpBase + 1..n:        A1..An (saved argument registers)
  cpBase + n + 1:       CE (saved continuation environment)
  cpBase + n + 2:       CP (saved continuation point)
  cpBase + n + 3:       B (previous choice point index)
  cpBase + n + 4:       BP (address of next clause to try)
  cpBase + n + 5:       BindingTrailTop (snapshot)
  cpBase + n + 6:       ExtraTrailTop (snapshot)
  cpBase + n + 7:       HeapTop (snapshot)
  cpBase + n + 8:       HB (saved heap boundary)
```

Total size: 9 + n cells for an n-argument predicate.

Only **arguments** are snapshotted, not all live X registers. The compiler is responsible for ensuring that any X register needed across the boundary is also written to a Y permanent.

### CE, CP, and similar slots as raw values

Control slots (`CE`, `CP`, `B`, `BP`, trail tops, heap top, `HB`) are stored as raw `long` values, not as tagged cells. The interpreter reads them via:

```csharp
long ceValue = _stack[envBase + 0].Data;  // Cell.Data is a long
int previousE = (int)ceValue;
```

The tag bits of such slots are not meaningful. Setting the tag to zero is recommended for clarity, but the engine does not rely on it.

### Operations

**Allocate** (`allocate N`):

```csharp
public void Allocate(int numPermanents)
{
    int newE = _stackTop;
    _stack[newE + 0] = new Cell(_e);     // CE = current E
    _stack[newE + 1] = new Cell(_cp);    // CP = current CP
    // Y1..Yn left as unbound; they're set by subsequent get_variable instructions
    for (int i = 0; i < numPermanents; i++)
        _stack[newE + 2 + i] = Cell.UnboundVar(newE + 2 + i);
    _stackTop = newE + 2 + numPermanents;
    _e = newE;
}
```

**Deallocate** (`deallocate`):

```csharp
public void Deallocate()
{
    _cp = (int)_stack[_e + 1].Data;
    _e = (int)_stack[_e + 0].Data;
    // _stackTop is NOT reduced here. The space remains allocated until the next
    // operation determines it can be reclaimed (typically after the next try_me_else
    // or proceed). This is the WAM convention.
}
```

**Call vs Execute** (call with continuation vs last call optimization):

```csharp
public void Call(int newPC, int newCP)
{
    _cp = newCP;  // Where to return after the callee finishes
    _p = newPC;
}

public void Execute(int newPC)
{
    _p = newPC;
    // _cp is inherited; no change
}
```

`Execute` is LCO: the last goal in a body is called without saving the return address, allowing tail-recursion without unbounded stack growth.

**Try / Retry / Trust**:

```csharp
public void TryMeElse(int nextClauseAddr, int arity)
{
    int newB = _stackTop;
    _stack[newB + 0] = new Cell(arity);
    for (int i = 0; i < arity; i++)
        _stack[newB + 1 + i] = _registers[i];
    int offset = newB + 1 + arity;
    _stack[offset + 0] = new Cell(_e);
    _stack[offset + 1] = new Cell(_cp);
    _stack[offset + 2] = new Cell(_b);
    _stack[offset + 3] = new Cell(nextClauseAddr);
    _stack[offset + 4] = new Cell(_bindingTrailTop);
    _stack[offset + 5] = new Cell(_extraTrailTop);
    _stack[offset + 6] = new Cell(_heapTop);
    _stack[offset + 7] = new Cell(_hb);
    _stackTop = offset + 8;
    _b = newB;
    _hb = _heapTop;
}

public void RetryMeElse(int nextClauseAddr)
{
    // Restore state, then update BP and continue
    RestoreFromCP();
    int arity = (int)_stack[_b].Data;
    _stack[_b + 1 + arity + 3] = new Cell(nextClauseAddr);
}

public void TrustMe()
{
    // Restore state, then discard the CP
    RestoreFromCP();
    int arity = (int)_stack[_b].Data;
    int offset = _b + 1 + arity;
    _b = (int)_stack[offset + 2].Data;  // previous B
    _stackTop = _b;  // shrink stack (subject to E being lower; see note)
}
```

### Stack space reclamation

After `deallocate` or `trust_me`, the stack top may shrink. The rule:

- The new `_stackTop` is set to the maximum of `_b + size(B)` (if any CP exists) and the position just past the current environment.
- This ensures that CPs and environments above the current frame are preserved while reclaiming space above them.

In practice, this is `_stackTop = max(_e + envSize(_e), _b + cpSize(_b))` if both exist, otherwise the one that exists.

### Cut implementation

Cut needs to know the choice point that existed when the current predicate was entered (the "cut barrier"). This is captured at compile time:

```
foo(X) :- bar(X), !, baz(X).
```

Compiles to (approximately):

```
foo/1:
  allocate 1
  get_level Y1            ; save current _b in Y1
  get_variable X1, A1
  put_value X1, A1
  call bar/1, 1
  cut Y1                  ; cut to the level saved in Y1
  put_value X1, A1
  deallocate
  execute baz/1
```

`get_level` saves the current `_b` register in a permanent variable. `cut Yi` later restores `_b` to that saved value, discarding any CPs created in between.

```csharp
public void Cut(int barrier)
{
    if (_b > barrier)
    {
        _b = barrier;
        // Compact both trails (see ADR-004)
        if (_b >= 0)
        {
            int arity = (int)_stack[_b].Data;
            int offset = _b + 1 + arity;
            int parentBindingTop = (int)_stack[offset + 4].Data;
            int parentExtraTop = (int)_stack[offset + 5].Data;
            int parentHeapTop = (int)_stack[offset + 6].Data;
            CompactBindingTrail(parentBindingTop, parentHeapTop);
            CompactExtraTrail(parentExtraTop, parentHeapTop);
        }
        // Shrink stack if no CPs above _e
        ReclaimStackSpace();
    }
}
```

### Neck cut optimization

When a cut appears immediately after the head match (no body goals before the `!`), the compiler can emit a simpler `neck_cut` instruction that uses the choice point register without needing a saved level in a permanent variable. This saves one Y slot.

### Atom GC interaction

The atom GC's mark phase scans the stack looking for cells with tag ATOM. The scan is linear over `_stackTop` cells:

```csharp
for (int i = 0; i < _stackTop; i++)
{
    Cell c = _stack[i];
    if (c.Tag == Tag.Atom)
        marked.Add(c.AsAtomId);
}
```

Control slots (`CE`, `CP`, etc.) typically have tags that are not `Atom` (they store raw indices in the low bits, with high bits zero). They are not mistaken for atom cells. If for some reason a control slot's value happens to encode bits that match the `Atom` tag, the GC would mark a spurious id; this is harmless (just a redundant mark) and rare enough to ignore.

If exactness is required, the GC can be made layout-aware (skip control slots based on frame layout), but this complicates the code significantly. As shipped, the linear scan with the tag check is sufficient (ADR-016 later tagged control words RawInt for exactly this discrimination).

## Alternatives Considered

### Separate stacks for environments and choice points

**Rejected.** Loses LCO efficiency (the environment of the caller can be reused in tail calls only when CPs created after it are accounted for in the unified layout). Also complicates space reclamation logic.

### Stack as `struct[]` of typed frames

**Rejected.** Environments and CPs have different sizes (depending on the number of permanents or arguments). A typed array would need either:

- A union type with the largest possible size: wastes space.
- A polymorphic array of references: defeats the purpose of a stack.

A flat `Cell[]` accommodates variable-sized frames trivially.

### Snapshot all live X registers in CPs

**Rejected.** The compiler already produces code that arranges arguments and live temporaries correctly when entering a clause. Snapshotting all X registers (which could be many) would inflate CP size unnecessarily. The classical WAM convention of snapshotting only arguments is well-proven.

### Tagged cells for CE/CP

**Rejected.** Adds no value. The interpreter knows the layout; it accesses control slots by offset, not by tag dispatch. Using raw `long` values makes the layout cleaner.

### Stack frames as managed objects

**Rejected.** Every frame as a managed object would mean millions of allocations during normal execution. The .NET GC would collapse under the load. The flat `Cell[]` approach has zero per-frame allocation.

## Consequences

### Positive

- **LCO works**: tail-recursive predicates use bounded stack space.
- **Compact frames**: only the necessary state is stored. No padding for typing.
- **Consistent with heap**: same `Cell[]` model, same blittability guarantees.
- **Fast frame creation**: writing N cells is a tight loop with no per-cell overhead.

### Negative

- **Layout knowledge is hardcoded**: the offsets of `CE`, `CP`, etc., within frames are constants in the interpreter. Changes require touching many places.
- **Stack space reclamation is subtle**: the rule for setting `_stackTop` after `deallocate` or `trust_me` requires care.
- **Atom GC may mark spurious ids**: theoretical issue, no practical impact.

### Mitigations

- **Define frame layouts in a single place** (constants and helper methods in `Engine`). All other code accesses frames through these helpers.
- **Tests for stack space reclamation** verify that `_stackTop` is correctly set after each operation that modifies the stack.
- **Documentation in the source** explains the layout in detail.

## Implementation Notes

### Frame layout constants

```csharp
internal static class FrameLayout
{
    // Environment frame
    public const int EnvCeOffset = 0;
    public const int EnvCpOffset = 1;
    public const int EnvY1Offset = 2;
    
    public static int EnvSize(int numPermanents) => 2 + numPermanents;
    
    // Choice point frame
    public const int CpArityOffset = 0;
    public const int CpArg1Offset = 1;
    // CE at offset 1 + arity, etc.
    
    public static int CpSize(int arity) => 10 + arity;          // ADR-015: +1 for ViewGen
    public static int CpCeOffset(int arity) => 1 + arity;
    public static int CpCpOffset(int arity) => 1 + arity + 1;
    public static int CpBOffset(int arity) => 1 + arity + 2;
    public static int CpBpOffset(int arity) => 1 + arity + 3;
    public static int CpBindingTrailOffset(int arity) => 1 + arity + 4;
    public static int CpExtraTrailOffset(int arity) => 1 + arity + 5;
    public static int CpHeapTopOffset(int arity) => 1 + arity + 6;
    public static int CpHbOffset(int arity) => 1 + arity + 7;
    public static int CpViewGenOffset(int arity) => 1 + arity + 8;   // ADR-015 chunk C
}
```

#### ViewGen slot (ADR-015 chunk C)

The trailing `ViewGen` slot carries the dynamic-database generation the
calling goal saw when it entered — its logical-update-view timestamp.
`PushChoicePoint` captures `engine.CurrentViewGen`; `RetryMeElse` and
`TrustMe` restore it via `RestoreCommonFromCurrentCp`. The slot is
uniform across all CPs (zero for static-predicate dispatch, which never
samples it); the tiny per-CP cost buys one save/restore path instead of
two parallel CP shapes. The upcoming `CheckVisible` opcode reads
`CurrentViewGen` against a clause's `born` / `died` to honour the ISO
logical update view at the bytecode level (no builtin indirection).

### Stack growth

The stack grows geometrically when full. Initial capacity is 8 KB (1024 cells). Maximum can be configured.

Out-of-stack throws `resource_error(stack_overflow)`.

### Registers vs Stack

X registers (X1..Xn) are temporary, live only between WAM instructions within a clause. They are stored in a flat array `Cell[] _registers`, separate from the stack.

A registers (A1..An) are conventionally the first N of X (they are the same physical registers; "A" denotes their role as arguments to a call). In Shumway, A_i is simply X_i.

### Saved registers in CPs are A registers

When a CP is created, the arguments to the current call (A1..An) are saved. On `retry_me_else` or `trust_me`, these are restored to the registers so the next alternative receives the same arguments.

### Stack and the atom GC mark phase

The mark phase scans `_stack[0.._stackTop-1]` linearly. It also scans `_registers[0..arity-1]` where `arity` is the maximum arity ever used (or simply the array length, since unused slots are unbound or zero).

## Test Strategy

- **Frame layout**: write known values, read back via the offsets, verify correctness.
- **Allocate/deallocate**: round-trip multiple times, verify stack top is correctly managed.
- **Call/return**: simulate a sequence of calls and returns, verify `_e`, `_cp`, `_p` are correctly maintained.
- **LCO**: tail-recursive predicate runs with bounded stack.
- **CP create/restore/discard**: create a CP, modify state, retry (state restored), trust (CP discarded).
- **Cut**: cut to various levels, verify CPs above are discarded and trail is compacted.
- **Stack growth**: allocate enough frames to trigger growth, verify state is preserved.
- **Atom GC over stack**: place atom cells in stack frames, run GC, verify atoms are marked.

## Related ADRs

- ADR-002 (Cell Layout): the stack uses the same `Cell` type as the heap.
- ADR-004 (Two Trails): choice points snapshot both trail tops.
- ADR-006 (Bytecode Encoding): instructions like `allocate`, `deallocate`, `call`, `execute`, `try_me_else` operate on the stack.

## Related Design Docs

- `design/wam-instruction-set.md`: full specification of stack-manipulating instructions.
