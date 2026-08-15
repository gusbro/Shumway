# ADR-002: Cell Layout

## Status

Accepted ([Phase 1](../../history/phase-1-closure.md)).

## Context

The cell is the most fundamental unit of memory in a WAM-based Prolog implementation. Every value on the heap, in registers, and in stack frames is represented as a cell. The cell's layout has cascading consequences for:

- **Memory footprint**: a 1 MB Prolog program may use millions of cells; small changes in cell size produce significant memory differences.
- **Cache locality**: cells are accessed sequentially during unification, deref, GC mark phase, and many other operations.
- **Performance of dereferencing**: every Prolog operation involves multiple deref steps, each of which reads cells.
- **GC interactions**: the .NET GC must not scan the heap looking for references; this means cells must be blittable.
- **Compatibility with the rest of the runtime**: the cell layout affects every component, from the interpreter to the IL compiler to the embedding API.

Several encoding strategies exist in the WAM literature and in real implementations:

- **Tagged words** (the classical approach from Aït-Kaci's book): a single machine word with low or high bits as tag.
- **Multi-word cells**: separate type tag and value, two machine words per cell.
- **NaN-boxing**: aggressive single-word encoding that uses the NaN space of IEEE 754 floats for non-float values. Used by LuaJIT and some JavaScript engines.
- **Struct-based with managed references**: cells as C# structs that hold both blittable data and `object?` references.

The choice affects nearly every other ADR.

## Decision

Shumway uses **8-byte (64-bit) cells encoded as a single `long`**, with a 4-bit tag in the high bits and a 60-bit payload in the low bits.

### Cell encoding

```
Bits 63..60: tag (4 bits, 16 possible types)
Bits 59..0:  payload (60 bits, interpretation depends on tag)
```

### Tag values

| Hex | Mnemonic | Payload interpretation |
|-----|----------|------------------------|
| 0x0 | REF      | Heap index to target cell. If equal to the cell's own index, the variable is unbound. |
| 0x1 | STR      | Heap index to a FUNCTOR cell; the structure's arguments follow it. |
| 0x2 | LIS      | Heap index to the head cell; head+1 is the tail cell. |
| 0x3 | FUNCTOR  | Id in the global functor table. |
| 0x4 | ATOM     | Atom id (global). |
| 0x5 | INT      | Signed 60-bit integer (inline). |
| 0x6 | FLOAT    | 4 high bits of double + heap index to INT cell with 60 low bits. |
| 0x7 | BIGINT   | Id in the per-engine BigInteger table. |
| 0x8 | STRING   | Id in the per-engine string table (opaque, non-list). |
| 0x9 | FOREIGN  | Id in the per-engine foreign object table. |
| 0xA | ATTVAR   | Heap index to the variable's own home cell (a self-referencing variable, like REF). Implemented in Phase 4 — see chunk 77. |
| 0xB | PSTR     | Partial string header (see PSTR design doc). |
| 0xC | PSTRBUF  | Partial-string buffer (`PstrBuffer`). |
| 0xD | RAWINT   | Untagged control word (`RawInt`) in environment / choice-point slots — lets the conservative GC scan tell control data from heap references. |
| 0xE | RATIONAL | Id in the per-engine rational table (`Rational`, ADR-039). |
| 0xF | (reserved) | Available for future extensions. |

### The heap is fully blittable

The heap is `Cell[]`, where `Cell` is a `readonly struct` wrapping a `long`. **The .NET GC never scans cells for managed references.** All managed-reference data (BigInteger, string, foreign object) lives in per-engine auxiliary tables. The cell holds an integer id into the appropriate table.

### Cell construction and inspection

```csharp
public readonly struct Cell : IEquatable<Cell>
{
    public readonly long Data;

    public Cell(long data) { Data = data; }

    public Tag Tag => (Tag)((Data >> 60) & 0xF);
    public long Payload => Data & ((1L << 60) - 1);

    public int AsHeapIndex => (int)Payload;
    public int AsAtomId => (int)Payload;
    public long AsInt
    {
        get
        {
            long p = Payload;
            // Sign-extend from bit 59
            if ((p & (1L << 59)) != 0)
                p |= unchecked((long)0xF000000000000000UL);
            return p;
        }
    }

    public static Cell Ref(int heapIdx) =>
        new Cell(((long)Tag.Ref << 60) | (uint)heapIdx);
    public static Cell Atom(int atomId) =>
        new Cell(((long)Tag.Atom << 60) | (uint)atomId);
    public static Cell Int(long value) =>
        new Cell(((long)Tag.Int << 60) | (value & ((1L << 60) - 1)));
    // ... and so on for other tags
}
```

### Special cell representations

#### FLOAT spans two cells

A double occupies 64 bits, which does not fit in a 60-bit payload. The encoding:

- **Header cell** with tag FLOAT: payload contains 4 high bits of the double (bits 59..56 of payload) and a 32-bit heap index (bits 31..0 of payload) pointing to a second cell.
- **Second cell** with tag INT: payload contains the 60 low bits of the double.

The second cell is a structurally valid INT cell (the iterator does not need a special case to skip it). Its numeric value as an integer is meaningless, but as long as no code dereferences it directly (only through the FLOAT header), correctness is maintained.

This costs 2 cells per float. For our target workloads (grammar processing, embedded rules), floats are not on the hot path, so this is acceptable.

#### Unbound variables: REF self-pointing

A variable is represented as a REF cell whose payload points to its own heap index. When the variable is bound, the cell is overwritten to point to the bound target (REF) or to contain the bound value directly (for atomic constants).

#### Binding policy (mixed)

When binding an unbound variable:

- **To a constant (ATOM, INT, FLOAT, BIGINT, STRING, FOREIGN)**: copy the constant cell into the variable's cell. Dereferencing later is one step.
- **To a STR or LIS**: write a REF cell pointing to the STR/LIS cell. Dereferencing later goes through one indirection.

This optimization (copy for constants, REF for compounds) is the classical WAM choice. The trail entry is the same in both cases (just the heap index of the modified cell).

### Indices use 32 bits

Heap indices, atom ids, functor ids, and similar indices use 32 bits, even though the cell payload has 60 bits available. The unused 28 bits are reserved (must be zero) for forward compatibility.

This decision is consistent with `Span<Cell>` and array indexing in .NET, which use `int`. A 32-bit index allows heaps of up to 2^31 cells (16 GB at 8 bytes per cell), far beyond practical sizes.

## Alternatives Considered

### Tagged words with low-bit tags (Aït-Kaci book style)

**Rejected.** The book uses low-bit tags (because the payloads were typically pointer-aligned and had spare low bits). On modern .NET, payloads are not pointers but managed indices; there is no alignment benefit. High-bit tags are simpler and equally fast.

### Multi-word cells (separate tag and value)

**Rejected.** Doubles the memory footprint of the heap. Cache locality suffers. For workloads with large heaps (parsing 1 MB inputs to PSTR + cons cells), the cost is prohibitive.

### NaN-boxing for FLOAT inline

**Considered, deferred.** NaN-boxing would allow doubles to be encoded inline without spanning two cells. The complexity of correctly handling all NaN patterns and integrating with the rest of the tag system is significant. For our target workloads, floats have not been hot enough to justify the complexity. Could be revisited if profiling ever shows float-heavy code as a bottleneck.

### Object references inside cells

**Rejected.** Mixing managed references and blittable data in cells would force the .NET GC to scan the entire heap (millions of cells) on every collection, looking for references. This would dominate runtime cost. Keeping the heap fully blittable means the GC never touches it.

### Smaller cells (4 bytes)

**Rejected.** A 4-byte cell could not represent both a 32-bit heap index and a tag, much less inline integers of reasonable range. The savings in memory would be lost to additional indirection.

### Larger cells (16 bytes)

**Considered briefly.** A 16-byte cell could store doubles inline and provide larger inline integers. However, the cost in cache locality and memory footprint is severe. Most cells in real Prolog programs are REF, STR, LIS, ATOM, or small INT, none of which benefit from more than 8 bytes.

## Consequences

### Positive

- **Compact heap**: 8 bytes per cell is the optimum for cache locality.
- **Blittable**: the .NET GC never scans the heap. Marking and copying of millions of cells during a `Gen2` collection would otherwise dominate runtime cost.
- **Fast operations**: tag extraction is a shift; payload extraction is a mask. Both are single-cycle operations.
- **Inline integers cover the common case**: 60-bit signed integers cover ±5.76×10^17, more than enough for nearly all practical arithmetic.
- **Snapshots and serialization are trivial**: copying the heap is `Buffer.BlockCopy` over a `byte[]` view.

### Negative

- **Doubles cost 2 cells**: minor cost for non-float-heavy workloads.
- **Large integers require BigInteger table**: integers outside ±2^59 need a per-engine table lookup. This is acceptable because such values are rare in typical Prolog code.
- **Cell payload is not aligned**: when reading the payload as a typed value (e.g., `AsInt`), masking is required. The masking cost is negligible.

### Mitigations

- **Helpers for typed access** (`AsAtomId`, `AsHeapIndex`, `AsInt`, etc.) are inlined and constant-folded by RyuJIT.
- **Documentation of the layout** in the `Cell` struct is mandatory; future contributors must understand the encoding before modifying.

## Implementation Notes

### Cell as a `readonly struct`

The cell is declared `readonly struct` to ensure value semantics and prevent accidental mutation. Methods like `Ref(...)`, `Atom(...)` are factory methods returning new cells.

### Sign extension for negative integers

When extracting an inline integer, the high bit of the payload (bit 59) is checked. If set, the upper bits of the result are filled with 1s (sign extension). This is masked out by the helper getter and is invisible to callers.

### Reserved bits in payload

For tags where the payload uses fewer than 60 bits (e.g., REF with a 32-bit heap index), the unused high bits of the payload must be zero. This is a soft invariant: code that creates cells should not set spurious bits, and code that reads them should mask correctly. Violations are not detected at runtime but can cause subtle bugs.

### Tag enum

```csharp
public enum Tag : byte
{
    Ref = 0x0,
    Str = 0x1,
    Lis = 0x2,
    Functor = 0x3,
    Atom = 0x4,
    Int = 0x5,
    Float = 0x6,
    BigInt = 0x7,
    String = 0x8,
    Foreign = 0x9,
    AttVar = 0xA,  // attributed variable (Phase 4, chunk 77): payload = own home index
    Pstr = 0xB,
    PstrBuffer = 0xC,
    RawInt = 0xD,      // untagged control word (env / CP slots)
    Rational = 0xE,    // rational table id (ADR-039)
    // 0xF reserved
}
```

## Test Strategy

- Round-trip tests for every tag: construct, inspect, verify the same value.
- Sign-extension test for `AsInt` with negative values near the boundaries.
- FLOAT encoding: store IEEE special values (NaN, infinity, denormals) and verify round-trip.
- Heap operations preserve cell layout (no accidental modification of tag bits during binding).
- Verify that the .NET GC does not touch the heap (use `GC.GetTotalAllocatedBytes` before and after operations on a large heap).

## Related ADRs

- ADR-001 (Engines and Global Tables): the heap is per-engine.
- ADR-003 (Atom Three-Tier System): atom ids in cells correspond to entries in the global atom table.
- ADR-004 (Two Trails): trail entries refer to heap indices that point to cells.
- ADR-005 (Stack Layout): stack frames are also cells.

## Related Design Docs

- `design/cell-layout-detail.md`: complete bit-level specification, including FLOAT encoding and PSTR layout.
- `design/pstr-design.md`: PSTR cell encoding for partial strings.
