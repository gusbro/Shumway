# Cell Layout: Detailed Specification

This document is the authoritative bit-level specification of Shumway's cell encoding. It complements ADR-002 by providing exact layouts, encoding/decoding code patterns, and worked examples for every cell type.

## Cell as a value type

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct Cell : IEquatable<Cell>
{
    public readonly long Data;

    public Cell(long data) { Data = data; }
    
    public Tag Tag => (Tag)((int)(Data >> 60) & 0xF);
    public long Payload => Data & PayloadMask;
    
    public const long PayloadMask = (1L << 60) - 1;
    public const int TagShift = 60;
    
    // Equality is bitwise comparison
    public bool Equals(Cell other) => Data == other.Data;
    public override int GetHashCode() => Data.GetHashCode();
    public static bool operator ==(Cell a, Cell b) => a.Data == b.Data;
    public static bool operator !=(Cell a, Cell b) => a.Data != b.Data;
}
```

## Tag enumeration

```csharp
public enum Tag : byte
{
    Ref     = 0x0,
    Str     = 0x1,
    Lis     = 0x2,
    Functor = 0x3,
    Atom    = 0x4,
    Int     = 0x5,
    Float   = 0x6,
    BigInt  = 0x7,
    // 0x8 free (was String; removed by ADR-047)
    Foreign = 0x9,
    AttVar  = 0xA,   // attributed variable (Phase 4; CLP(FD)/CLP(R) build on it)
    Pstr    = 0xB,   // packed list header (ADR-047)
    PstrBuffer = 0xC, // PSTR buffer cell: 3 UTF-16 code units
    RawInt  = 0xD,   // non-heap-ref control word (env/CP fields) — ADR-016 heap-GC scan
    Rational = 0xE,  // rational table id (ADR-039)
    // 0xF free
}
```

> **Later note.** This document predates changes that refine (without
> breaking) the layout described below: the three extra tags above
> (`PstrBuffer`, `RawInt`, `Rational`), and
> **ADR-017 inline compound references** — a `Lis`/`Str` cell may now sit
> *inline in a referring slot* (a register, an argument, a structure argument)
> rather than always behind an on-heap header, and unification is cell-based
> accordingly. The on-heap layouts described here remain valid; ADR-017 is the
> authority on where a compound reference may appear.

## Bit layout overview

```
Bit position:  63 62 61 60 | 59 ............................. 0
Field:         |   tag    | |          payload (60 bits)        |
```

The tag occupies the four high bits (63..60). The payload occupies the low 60 bits (59..0).

## Per-tag layout

### REF (0x0) — Variable

```
Bits 63..60: 0x0
Bits 59..32: 0 (reserved, must be zero)
Bits 31..0:  heap index (signed int, but always treated as non-negative)
```

A REF cell points to another cell in the heap. If it points to itself (the heap index equals its own position), the variable is **unbound**. Any other value means it is bound (perhaps transitively) to whatever is at the target.

**Construction**:

```csharp
public static Cell Ref(int heapIdx)
    => new Cell(((long)Tag.Ref << 60) | (uint)heapIdx);

public static Cell UnboundVar(int heapIdx)
    => Ref(heapIdx);  // pointing to self = unbound
```

**Extraction**:

```csharp
public static int AsHeapIndex(Cell c) => (int)(c.Data & 0xFFFFFFFFL);
```

**Example**: an unbound variable at heap position 100 is `0x0000_0000_0000_0064` (tag=0, payload=100).

### STR (0x1) — Compound term head

```
Bits 63..60: 0x1
Bits 59..32: 0 (reserved)
Bits 31..0:  heap index to the FUNCTOR cell that follows
```

A STR cell points to a FUNCTOR cell, which is immediately followed by the structure's arguments. For example, the term `foo(a, b)` occupies four cells:

```
heap[N+0]: STR     pointing to N+1
heap[N+1]: FUNCTOR functor of foo/2
heap[N+2]: ATOM    a (argument 1)
heap[N+3]: ATOM    b (argument 2)
```

**Construction**:

```csharp
public static Cell Str(int heapIdx)
    => new Cell(((long)Tag.Str << 60) | (uint)heapIdx);
```

**Convention**: the payload always points to the FUNCTOR cell, never to the first argument directly.

### LIS (0x2) — List (cons cell)

```
Bits 63..60: 0x2
Bits 59..32: 0 (reserved)
Bits 31..0:  heap index to the head cell; head+1 is the tail cell
```

A LIS cell points to two consecutive cells: the head at the indexed position, and the tail immediately after. The list `[a, b]` occupies five cells:

```
heap[N+0]: LIS     pointing to N+1
heap[N+1]: ATOM    a (head of [a, b])
heap[N+2]: LIS     pointing to N+3
heap[N+3]: ATOM    b (head of [b])
heap[N+4]: ATOM    [] (tail of [b], empty list)
```

The empty list `[]` is a regular atom (pre-registered with a known atom id at engine initialization).

**Construction**:

```csharp
public static Cell Lis(int heapIdx)
    => new Cell(((long)Tag.Lis << 60) | (uint)heapIdx);
```

### FUNCTOR (0x3) — Structure functor

```
Bits 63..60: 0x3
Bits 59..32: 0 (reserved)
Bits 31..0:  functor table id (global)
```

A FUNCTOR cell appears immediately after a STR cell. It identifies the structure's name and arity via a global functor table id. The functor table maps `FunctorId` to `(atom_id, arity)`.

**Construction**:

```csharp
public static Cell Functor(int functorId)
    => new Cell(((long)Tag.Functor << 60) | (uint)functorId);
```

**Note**: FUNCTOR cells are not meant to appear in isolation. They are part of the STR-FUNCTOR-args triplet pattern. Code that dereferences a value should not encounter a FUNCTOR cell directly; if it does, that's a bug.

### ATOM (0x4) — Atom

```
Bits 63..60: 0x4
Bits 59..32: 0 (reserved)
Bits 31..0:  atom id (global)
```

An atom is identified by a global integer id. The atom table provides the mapping `id ↔ string`.

**Construction**:

```csharp
public static Cell Atom(int atomId)
    => new Cell(((long)Tag.Atom << 60) | (uint)atomId);
```

**Extraction**:

```csharp
public static int AsAtomId(Cell c) => (int)(c.Data & 0xFFFFFFFFL);
```

**Pre-registered atoms** (assigned fixed ids at engine initialization):

| Atom | Id |
|------|----|
| `[]` | 0 |
| `{}` | 1 |
| `.` (cons functor) | 2 |
| `true` | 3 |
| `false` | 4 |
| (more reserved 5..15 for future use) |

User-created atoms get ids 16 and above.

### INT (0x5) — Inline integer

```
Bits 63..60: 0x5
Bits 59..0:  signed 60-bit integer (two's complement)
```

Integers in the range `[-2^59, 2^59 - 1]` are stored inline. Values outside this range use BIGINT.

**Construction**:

```csharp
public static Cell Int(long value)
{
    // Caller must check range; throws if out of range or promotes to BigInt
    if (value < MinInt60 || value > MaxInt60)
        throw new ArgumentOutOfRangeException(nameof(value));
    return new Cell(((long)Tag.Int << 60) | (value & PayloadMask));
}

public const long MinInt60 = -(1L << 59);
public const long MaxInt60 = (1L << 59) - 1;
```

**Extraction with sign extension**:

```csharp
public static long AsInt(Cell c)
{
    long payload = c.Data & PayloadMask;
    // Sign-extend from bit 59
    if ((payload & (1L << 59)) != 0)
        payload |= unchecked((long)0xF000000000000000UL);
    return payload;
}
```

**Examples**:

| Value | Hex encoding |
|-------|--------------|
| 0     | `0x5000_0000_0000_0000` |
| 1     | `0x5000_0000_0000_0001` |
| -1    | `0x5FFF_FFFF_FFFF_FFFF` |
| 100   | `0x5000_0000_0000_0064` |
| -100  | `0x5FFF_FFFF_FFFF_FF9C` |

### FLOAT (0x6) — Floating-point header

```
Bits 63..60: 0x6
Bits 59..56: 4 high bits of the double
Bits 55..32: 0 (reserved)
Bits 31..0:  heap index to a paired INT cell containing the 60 low bits of the double
```

A double is 64 bits. It does not fit in a 60-bit payload. Shumway encodes it in two cells: a FLOAT header carrying the 4 high bits + a heap index, and an INT cell at the indexed position carrying the 60 low bits.

The paired cell has tag INT and is structurally valid (an iterator can process it as INT without special handling). Its numeric value as an integer is meaningless (it's just the 60 low bits of the double, not a real integer value), but code that follows the FLOAT header to reconstruct the double does the right thing.

**Construction**:

```csharp
public static (Cell header, Cell paired) MakeFloat(double value, int pairedHeapIdx)
{
    long bits = BitConverter.DoubleToInt64Bits(value);
    long highBits = (bits >> 60) & 0xFL;            // 4 high bits
    long lowBits = bits & PayloadMask;              // 60 low bits
    
    var paired = new Cell(((long)Tag.Int << 60) | lowBits);
    long headerPayload = (highBits << 56) | (uint)pairedHeapIdx;
    var header = new Cell(((long)Tag.Float << 60) | headerPayload);
    
    return (header, paired);
}
```

**Reconstruction**:

```csharp
public static double AsFloat(Cell header, Cell[] heap)
{
    long payload = header.Data & PayloadMask;
    long highBits = (payload >> 56) & 0xFL;
    int idx = (int)(payload & 0xFFFFFFFFL);
    long lowBits = heap[idx].Data & PayloadMask;
    long bits = (highBits << 60) | lowBits;
    return BitConverter.Int64BitsToDouble(bits);
}
```

**Heap allocation pattern**: when creating a FLOAT, allocate two consecutive cells. The header goes in the first, the paired INT in the second. The header points to the second.

### BIGINT (0x7) — Reference to BigInteger

```
Bits 63..60: 0x7
Bits 59..32: 0 (reserved)
Bits 31..0:  id in the engine's BigInteger table
```

When an integer exceeds the range of INT (60 bits signed), it's stored in a per-engine `List<BigInteger>` (the BigInt table). The cell holds the index into this list.

**Construction**:

```csharp
public Cell MakeBigInt(BigInteger value)
{
    int id = _bigIntTable.Count;
    _bigIntTable.Add(value);
    return new Cell(((long)Tag.BigInt << 60) | (uint)id);
}
```

**Extraction**:

```csharp
public BigInteger AsBigInt(Cell c)
{
    int id = (int)(c.Data & 0xFFFFFFFFL);
    return _bigIntTable[id];
}
```

The BigInt table grows during a query. It is **not** trail-reversed (truncating it would be expensive). At end-of-query, the table is cleared.

### 0x8 — free

Freed by ADR-047: there is no opaque string type. Text as a value is an atom;
text as a sequence is a list, packed or not.

The tag space is 4 bits and cannot grow, so 0x8 and 0xF are the last two slots.
Claiming either is a major decision (`decision-policy.md`).

### FOREIGN (0x9) — Reference to .NET object

```
Bits 63..60: 0x9
Bits 59..32: 0 (reserved)
Bits 31..0:  id in the engine's foreign object table
```

A reference to a managed .NET object passed from C# code. The object lives in a per-engine `List<object?>`. The cell holds the index.

**Construction**:

```csharp
public Cell MakeForeign(object obj)
{
    int id = _foreignTable.Count;
    _foreignTable.Add(obj);
    return new Cell(((long)Tag.Foreign << 60) | (uint)id);
}
```

**Notes**:
- The foreign table holds strong references to objects, keeping them alive while the engine references them.
- The table is cleared at engine reset/disposal.
- Backtracking does not unwind foreign object additions (similar to the BigInt and Rational tables).

### ATTVAR (0xA) — Attributed variable

Implemented in Phase 4. The payload is a heap index to the variable's own home
cell (a self-referencing variable, like REF); its attributes live in a
per-activation side table. Backs `attvar/1`, `put_attr`/`get_attr`, the
`attr_unify_hook` / `verify_attributes/4` wakeup, and CLP(FD)/CLP(R).

### PSTR (0xB) — Packed list header

A **list**, stored packed — not a string type (ADR-047). Described in full in
`pstr-design.md`; the summary:

```
Bits 63..60: 0xB (PSTR header)
Bit  59:     presentation — 1 = chars, 0 = codes
Bits 58..32: length in UTF-16 code units (27 bits)
Bits 31..2:  heap index of the first buffer cell (30 bits)
Bits 1..0:   offset within that buffer cell (0..2, 2 bits)
```

The header is followed by buffer cells (3 UTF-16 code units each) and a tail
cell. The presentation bit sits at 59 so that the buffer index and offset keep
the bit positions they had before it existed, leaving the GC's relocation of a
PSTR header untouched — losing that bit during a collection would silently turn
a list of chars into a list of codes.

See `pstr-design.md` for the complete specification.

## Heap iteration

The atom GC, debugger, and other tools iterate cells linearly. The iteration must handle the FLOAT case (the paired INT cell is structurally valid but should not be misinterpreted).

```csharp
public IEnumerable<(int Index, Cell Cell)> IterateHeap(Cell[] heap, int top)
{
    for (int i = 0; i < top; i++)
    {
        yield return (i, heap[i]);
        // Note: the paired INT for FLOAT is yielded as-is; readers that need
        // semantic interpretation should detect FLOAT and skip its paired cell.
    }
}
```

For semantic iteration (one logical value at a time, skipping FLOAT pairs):

```csharp
public IEnumerable<(int Index, Cell Cell)> IterateLogical(Cell[] heap, int top)
{
    int i = 0;
    while (i < top)
    {
        Cell c = heap[i];
        yield return (i, c);
        if (c.Tag == Tag.Float)
            i += 2;  // skip the paired INT
        else
            i += 1;
        // Note: structures (STR + FUNCTOR + args) are emitted as separate cells;
        // semantic interpretation higher up the stack handles this.
    }
}
```

## Atom GC marking

The atom GC's mark phase scans the heap for ATOM cells and adds their ids to the marked set:

```csharp
public void MarkAtomsInHeap(Cell[] heap, int top, HashSet<int> marked)
{
    for (int i = 0; i < top; i++)
    {
        Cell c = heap[i];
        if (c.Tag == Tag.Atom)
        {
            marked.Add((int)(c.Data & 0xFFFFFFFFL));
        }
    }
}
```

FUNCTOR cells contain functor ids, not atom ids directly. The mark phase must also scan FUNCTOR cells and resolve their atom names:

```csharp
public void MarkAtomsInHeap(Cell[] heap, int top, HashSet<int> markedAtoms)
{
    for (int i = 0; i < top; i++)
    {
        Cell c = heap[i];
        switch (c.Tag)
        {
            case Tag.Atom:
                markedAtoms.Add((int)(c.Data & 0xFFFFFFFFL));
                break;
            case Tag.Functor:
                int functorId = (int)(c.Data & 0xFFFFFFFFL);
                var (atomId, _) = FunctorTable.Lookup(functorId);
                markedAtoms.Add(atomId);
                break;
            // Other tags don't directly contain atom references
        }
    }
}
```

The same logic applies to stack scanning, register scanning, and predicate bytecode scanning (the last requires walking the bytecode to extract atom operands).

## Binding policy (mixed)

When binding an unbound variable to a value, the WAM has two patterns:

- **For atomic values** (ATOM, INT, FLOAT, BIGINT, RATIONAL, FOREIGN): copy the value cell into the variable's cell. Future dereferences see the value directly, no indirection.

```csharp
public void BindVarToAtomic(int varIdx, Cell value)
{
    _heap[varIdx] = value;  // overwrite
    if (varIdx < _hb)
        TrailBinding(varIdx);
}
```

- **For compound values** (STR, LIS, PSTR): write a REF cell pointing to the value's heap location. The dereferenced cell becomes the target.

```csharp
public void BindVarToCompound(int varIdx, int targetIdx)
{
    _heap[varIdx] = Cell.Ref(targetIdx);
    if (varIdx < _hb)
        TrailBinding(varIdx);
}
```

This optimization (copy for atomic, REF for compound) is the classical WAM choice.

## Dereferencing

Dereferencing follows REF cells until reaching a non-REF cell or a REF cell that points to itself (unbound):

```csharp
public int Deref(int heapIdx)
{
    while (true)
    {
        Cell c = _heap[heapIdx];
        if (c.Tag != Tag.Ref) return heapIdx;
        int target = (int)(c.Data & 0xFFFFFFFFL);
        if (target == heapIdx) return heapIdx;  // unbound
        heapIdx = target;
    }
}
```

**Path compression** (an optimization that updates intermediate REF cells to point directly to the target) is **not implemented in v1**. The cost of path compression includes trail entries for the modified cells, which complicates backtracking. Phase 2+ may add it as an optimization for deep dereference chains.

## Equality of cells

**Bitwise equality** (`==` on `Cell`):
- Two REFs are bitwise equal if they have the same heap index.
- Two ATOMs are bitwise equal if they have the same atom id.
- Two INTs are bitwise equal if they have the same value (within the 60-bit range).
- Two FLOATs are bitwise equal if their headers are identical (which implies same encoded double).
- Two BIGINTs at different table ids are NOT bitwise equal even if the BigInteger values are equal. Semantic equality requires a table lookup.

For Prolog semantic equality (`==/2`), the engine performs structural comparison:

- For inline tags (ATOM, INT, REF): bitwise comparison is sufficient.
- For BIGINT: compare the actual BigInteger values from the table.
- For FOREIGN: reference equality (`ReferenceEquals`).
- For STR/LIS/PSTR: recursive structural comparison.

## Common bit patterns

Quick reference for common cells:

| Cell | Hex | Description |
|------|-----|-------------|
| Unbound var at heap[0] | `0x0000_0000_0000_0000` | tag=REF, payload=0 |
| Unbound var at heap[100] | `0x0000_0000_0000_0064` | tag=REF, payload=100 |
| `[]` atom (id=0) | `0x4000_0000_0000_0000` | tag=ATOM, payload=0 |
| `true` atom (id=3) | `0x4000_0000_0000_0003` | tag=ATOM, payload=3 |
| Int 0 | `0x5000_0000_0000_0000` | tag=INT, payload=0 |
| Int 42 | `0x5000_0000_0000_002A` | tag=INT, payload=42 |
| Int -1 | `0x5FFF_FFFF_FFFF_FFFF` | tag=INT, payload=-1 (sign extended) |
| STR pointing to heap[10] | `0x1000_0000_0000_000A` | tag=STR, payload=10 |
| LIS pointing to heap[20] | `0x2000_0000_0000_0014` | tag=LIS, payload=20 |
| FUNCTOR id=5 | `0x3000_0000_0000_0005` | tag=FUNCTOR, payload=5 |

## Validation rules

Several invariants must hold for the heap to be well-formed:

1. **REF cells point within the heap**: payload < heap top. Violation indicates corruption.
2. **STR cells point to FUNCTOR cells**: the cell at the STR's payload index must have tag FUNCTOR. Violation indicates corruption.
3. **LIS cells point to a head**: the cell at the LIS's payload index is the head; payload+1 must be within heap bounds.
4. **FLOAT cells point to INT cells**: the paired cell must have tag INT.
5. **FUNCTOR cells are reachable only via STR**: a FUNCTOR cell should not be the target of a REF or a top-level dereference path.
6. **Unbound REF cells point to themselves**: the payload equals the cell's heap index.

These invariants are not checked at runtime in the hot path. Debug-build assertions catch violations during testing. Test suites verify them after each operation.

## Reserved values

The following bit patterns are reserved and should never appear in valid cells:

- Tags 0x8 and 0xF (no semantics defined). Tags 0xC (`PstrBuffer`), 0xD (`RawInt`) and 0xE (`Rational`) are in use.
- The all-zero cell (`0x0000_0000_0000_0000`) is technically a valid REF cell pointing to heap[0], but in practice, the cell at heap[0] is either an unbound REF to itself or part of an allocated structure. Code should be careful when interpreting zero-initialized memory as cells.

## See also

- ADR-002 (Cell Layout): high-level rationale.
- `pstr-design.md`: detailed PSTR encoding.
- `wam-instruction-set.md`: how instructions read and write cells.
