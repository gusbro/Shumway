# PSTR Design: Partial Strings for Grammar Processing

This document specifies the design of PSTR (partial strings), a specialized cell type for efficient representation of character sequences. PSTRs are central to Shumway's grammar processing capabilities and enable DCGs to operate on large inputs without the prohibitive cost of materializing cons cells.

## Motivation

A traditional list-of-codes representation of a string is `[H1, H2, ..., Hn]`, which in WAM heap occupies `2n + 1` cells (`n` LIS cells, `n` value cells, and one terminal `[]`). For a 1 MB input, this is over 2 million heap cells.

For DCG-based grammar processing, this is prohibitive. A parser that consumes characters one at a time would build up enormous intermediate structures.

PSTRs solve this by representing a string of `n` characters in roughly `n / 4 + 2` cells (one header, `⌈n/4⌉` buffer cells, one tail cell). For 1 MB of input, that's ~131,072 cells, a 16× improvement.

More importantly, PSTRs support **lazy decomposition**: `[H|T] = pstr("hello")` does not materialize cons cells. `T` is another PSTR header pointing into the same buffer at the next position. The parser's per-character operations are O(1) and allocate nothing.

This design follows the approach pioneered by Scryer Prolog, adapted to use UTF-16 (.NET's native encoding).

## Layout

### Header cell

```
Bits 63..60: 0xB (PSTR tag)
Bits 59..32: length in UTF-16 code units (28 bits, max 2^28 - 1 = 268M code units)
Bits 31..2:  heap index of the first buffer cell (30 bits, max 1G heap cells)
Bits 1..0:   offset within the first buffer cell (0..3)
```

- **Length** is in **UTF-16 code units**, not codepoints. This makes the tail position computable in O(1): `tail_index = buffer_idx + ⌈(length + offset) / 4⌉`. The actual codepoint count requires iteration to detect surrogate pairs; the cost is paid only when needed (e.g., `pstr_length/2`).
- **Heap index** points to the first buffer cell.
- **Offset** allows the PSTR to start mid-cell. This is essential for lazy decomposition: when `[H|T] = Pstr` consumes one character, `T`'s header has an incremented offset.

### Buffer cells

Buffer cells follow the header on the heap. Each cell holds **4 UTF-16 code units packed into the 60-bit payload**:

```
Bits 63..60: tag value (see note below)
Bits 59..45: code unit 0 (15 bits used; UTF-16 code units are 16 bits, but the high bit fits the same way)
Bits 44..30: code unit 1
Bits 29..15: code unit 2
Bits 14..0:  code unit 3
```

Wait, 15 bits is not enough for UTF-16. Let me revise:

```
Bits 63..60: tag (let's reserve 0xC for "PSTR buffer", or reuse INT/raw)
Bits 59..0:  60 bits for 4 code units = 15 bits each? Not enough.
```

UTF-16 code units are 16 bits. We need 64 bits for 4 of them, but we only have 60 in the payload. This requires a compromise.

**Solution adopted**: buffer cells use 15 bits per code unit, with the high bit packed separately. A character with bit 15 set (rare in practice except for surrogate pairs) requires special handling: the buffer cell stores 15 bits, and a separate high-bits buffer holds the 16th bit per code unit.

This is awkward. A cleaner alternative: use 16 bits per code unit, but pack only 3 code units per cell (using 48 bits of the payload). Wastes ~25% of each cell but is dramatically simpler.

**Decision: 3 UTF-16 code units per buffer cell**. The slight memory overhead is worth the simplicity:

```
Bits 63..60: tag = 0xC (PSTR_BUFFER, a new tag reserved for this purpose)
            (Alternatively, reuse INT tag; the iterator treats it as opaque
             when reached via a PSTR header.)
Bits 59..48: code unit 0 (12 bits) — no, still not enough.
```

Let me reconsider. 60 bits / 16 bits per code unit = 3.75, so **3 code units fit cleanly with 12 bits to spare**. The 12 bits can be used as a small marker, padding, or reserved.

**Final layout for buffer cells**:

```
Bits 63..60: tag = 0xC (PSTR_BUFFER, new tag) or reuse 0x5 (INT) for simplicity
Bits 59..48: reserved (12 bits, zero)
Bits 47..32: code unit 0 (16 bits)
Bits 31..16: code unit 1 (16 bits)
Bits 15..0:  code unit 2 (16 bits)
```

This packs **3 UTF-16 code units per cell**. For a string of `n` code units, the buffer occupies `⌈n / 3⌉` cells.

For a 1 MB string (524288 code units), the buffer is ~175K cells + 1 header + 1 tail = ~175K cells total. Still excellent vs. the 2M+ for cons cells.

**Updated header layout to match**:

```
Bits 63..60: 0xB (PSTR tag)
Bits 59..32: length in UTF-16 code units (28 bits)
Bits 31..2:  heap index of the first buffer cell (30 bits)
Bits 1..0:   offset within that buffer cell (0..2, since 3 code units per cell)
```

The offset field uses values 0, 1, or 2 to indicate which of the 3 code units in the first buffer cell is the start. Value 3 is reserved/unused.

### Tail cell

After the buffer cells, a single tail cell terminates the PSTR. Its value can be:

- `Atom([])`: the PSTR is a complete proper list (a "string").
- `Ref(unbound)`: the PSTR is a partial list; the tail is open.
- `Lis(...)`: the PSTR was extended by appending a regular cons cell (a fallback case).
- `Pstr(...)`: another PSTR (lazy concatenation, Phase 2 feature).

### Buffer cell tag choice

The decision of which tag to use for buffer cells affects iteration:

**Option A**: dedicated tag `0xC` (PSTR_BUFFER). The heap iterator sees the tag and knows it's part of a PSTR. Cleaner semantically.

**Option B**: reuse `0x5` (INT). The cell is structurally a valid INT (with garbage value as int), but the iterator must follow the PSTR header to know it's buffer data.

Option A is cleaner. Phase 1 reserves tag `0xC` for PSTR_BUFFER. The heap iterator and the atom GC simply skip cells with tag 0xC (no atom references inside).

```
Tag values (updated):
0xA  ATTVAR (reserved)
0xB  PSTR (header)
0xC  PSTR_BUFFER (buffer cell of a PSTR)
0xD..0xF reserved
```

## Code unit extraction

Reading the `i`-th code unit from a PSTR (where `i` is 0-based within the PSTR):

```csharp
public static int GetCodeUnit(Cell header, int i, Cell[] heap)
{
    long payload = header.Data & Cell.PayloadMask;
    int offset = (int)(payload & 0x3);
    int bufferIdx = (int)((payload >> 2) & 0x3FFFFFFF);
    
    int absolutePos = offset + i;
    int cellIdx = bufferIdx + absolutePos / 3;
    int positionInCell = absolutePos % 3;
    
    Cell bufferCell = heap[cellIdx];
    long bufferPayload = bufferCell.Data;
    
    switch (positionInCell)
    {
        case 0: return (int)((bufferPayload >> 32) & 0xFFFF);
        case 1: return (int)((bufferPayload >> 16) & 0xFFFF);
        case 2: return (int)(bufferPayload & 0xFFFF);
        default: throw new InvalidOperationException();
    }
}
```

## Construction from .NET string

```csharp
public Cell MakePstr(string s)
{
    int codeUnits = s.Length;
    int bufferCellCount = (codeUnits + 2) / 3;  // ceil(codeUnits / 3)
    int totalCells = 1 + bufferCellCount + 1;   // header + buffer + tail
    
    int headerIdx = AllocateHeapCells(totalCells);
    int bufferIdx = headerIdx + 1;
    int tailIdx = bufferIdx + bufferCellCount;
    
    // Write buffer cells (3 code units per cell, packed)
    for (int i = 0; i < bufferCellCount; i++)
    {
        long cellData = (long)Tag.PstrBuffer << 60;
        for (int j = 0; j < 3; j++)
        {
            int cuIdx = i * 3 + j;
            if (cuIdx < codeUnits)
            {
                int cu = s[cuIdx];
                int shift = 32 - j * 16;  // 32 for j=0, 16 for j=1, 0 for j=2
                cellData |= ((long)cu) << shift;
            }
        }
        _heap[bufferIdx + i] = new Cell(cellData);
    }
    
    // Write tail (default: [])
    _heap[tailIdx] = Cell.Atom(AtomTable.EmptyListId);
    
    // Write header
    long headerPayload = ((long)codeUnits << 32) | ((uint)bufferIdx << 2) | 0;  // offset 0
    _heap[headerIdx] = new Cell(((long)Tag.Pstr << 60) | headerPayload);
    
    return _heap[headerIdx];
}
```

## Conversion to .NET string

```csharp
public string PstrToString(Cell header, Cell[] heap)
{
    long payload = header.Data & Cell.PayloadMask;
    int codeUnits = (int)((payload >> 32) & 0xFFFFFFF);
    int bufferIdx = (int)((payload >> 2) & 0x3FFFFFFF);
    int offset = (int)(payload & 0x3);
    
    var sb = new StringBuilder(codeUnits);
    for (int i = 0; i < codeUnits; i++)
    {
        int cu = GetCodeUnit(header, i, heap);
        sb.Append((char)cu);
    }
    return sb.ToString();
}
```

## Lazy decomposition

The key feature of PSTR is that decomposition does not allocate cons cells.

When the engine encounters `[H|T] = Pstr` (i.e., unifying a LIS or a `[H|T]` pattern with a PSTR):

```csharp
public (Cell head, Cell tail) DecomposePstr(Cell pstrHeader, Cell[] heap, Engine engine)
{
    long payload = pstrHeader.Data & Cell.PayloadMask;
    int codeUnits = (int)((payload >> 32) & 0xFFFFFFF);
    int bufferIdx = (int)((payload >> 2) & 0x3FFFFFFF);
    int offset = (int)(payload & 0x3);
    
    if (codeUnits == 0)
    {
        // The PSTR is empty; its decomposition fails (no head).
        // The caller treats this as a failure or as matching with the tail cell.
        throw new InvalidOperationException("Cannot decompose empty PSTR");
    }
    
    // Read the first code unit (possibly part of a surrogate pair)
    int unit0 = GetCodeUnit(pstrHeader, 0, heap);
    
    int codepoint;
    int unitsConsumed;
    if (unit0 >= 0xD800 && unit0 <= 0xDBFF && codeUnits >= 2)
    {
        // High surrogate, look at the next unit
        int unit1 = GetCodeUnit(pstrHeader, 1, heap);
        if (unit1 >= 0xDC00 && unit1 <= 0xDFFF)
        {
            codepoint = 0x10000 + ((unit0 - 0xD800) << 10) + (unit1 - 0xDC00);
            unitsConsumed = 2;
        }
        else
        {
            // Unpaired high surrogate; treat as a lone code point
            codepoint = unit0;
            unitsConsumed = 1;
        }
    }
    else
    {
        codepoint = unit0;
        unitsConsumed = 1;
    }
    
    // Build the head value depending on the double_quotes flag
    Cell headCell;
    switch (engine.Flags.DoubleQuotes)
    {
        case DoubleQuotesMode.Codes:
            headCell = Cell.Int(codepoint);
            break;
        case DoubleQuotesMode.Chars:
            string charStr = char.ConvertFromUtf32(codepoint);
            int atomId = AtomTable.Intern(charStr, permanent: false);
            headCell = Cell.Atom(atomId);
            break;
        default:
            // For other modes, default to chars
            string charStr2 = char.ConvertFromUtf32(codepoint);
            int atomId2 = AtomTable.Intern(charStr2, permanent: false);
            headCell = Cell.Atom(atomId2);
            break;
    }
    
    // Build the tail value
    Cell tailCell;
    int newCodeUnits = codeUnits - unitsConsumed;
    int newAbsolutePos = offset + unitsConsumed;
    int newBufferIdx = bufferIdx + newAbsolutePos / 3;
    int newOffset = newAbsolutePos % 3;
    
    if (newCodeUnits == 0)
    {
        // The PSTR is now empty; the tail is the original tail cell
        int tailIdx = bufferIdx + (codeUnits + offset + 2) / 3;
        tailCell = heap[tailIdx];
    }
    else
    {
        // Build a new PSTR header for the remaining content
        long newPayload = ((long)newCodeUnits << 32) | ((uint)newBufferIdx << 2) | (uint)newOffset;
        tailCell = new Cell(((long)Tag.Pstr << 60) | newPayload);
    }
    
    return (headCell, tailCell);
}
```

Notable: the new PSTR header **does not allocate any new heap cells**. It's just a value computation. The same buffer is referenced with an incremented offset.

## Unification with PSTR

When the engine unifies a value with a PSTR, the strategy depends on the other operand's tag.

### PSTR vs PSTR

Compare lengths first. If different, fail.

If lengths match, compare code units pairwise:

```csharp
public bool UnifyPstrPstr(Cell pstrA, Cell pstrB, Cell[] heap)
{
    long payloadA = pstrA.Data & Cell.PayloadMask;
    long payloadB = pstrB.Data & Cell.PayloadMask;
    int lengthA = (int)((payloadA >> 32) & 0xFFFFFFF);
    int lengthB = (int)((payloadB >> 32) & 0xFFFFFFF);
    
    if (lengthA != lengthB) return false;
    
    for (int i = 0; i < lengthA; i++)
    {
        if (GetCodeUnit(pstrA, i, heap) != GetCodeUnit(pstrB, i, heap))
            return false;
    }
    
    // Lengths and content match; now unify the tails
    Cell tailA = GetPstrTail(pstrA, heap);
    Cell tailB = GetPstrTail(pstrB, heap);
    return Unify(tailA, tailB);
}
```

Optimization: when the buffers are at the same heap address with the same offset, the lengths-match check is enough (they share the same data).

### PSTR vs LIS (cons cell)

If the LIS pattern is `[H|T]`:

```csharp
public bool UnifyPstrLis(Cell pstr, Cell lis, Cell[] heap, Engine engine)
{
    long payload = pstr.Data & Cell.PayloadMask;
    int codeUnits = (int)((payload >> 32) & 0xFFFFFFF);
    
    if (codeUnits == 0)
    {
        // PSTR is empty; unify with tail (which is the cons cell's tail)
        // Actually if PSTR is empty, it should be [] effectively;
        // unifying with non-empty LIS fails.
        return false;
    }
    
    // Decompose PSTR
    var (head, tail) = DecomposePstr(pstr, heap, engine);
    
    // Unify head with LIS head, tail with LIS tail
    int lisIdx = (int)(lis.Data & 0xFFFFFFFFL);
    Cell lisHead = heap[lisIdx];
    Cell lisTail = heap[lisIdx + 1];
    
    return Unify(head, lisHead) && Unify(tail, lisTail);
}
```

### PSTR vs ATOM([])

A PSTR with length 0 and tail `[]` represents the empty list, which is the atom `[]`. Unify succeeds.

A non-empty PSTR does not unify with `[]`. Fail.

```csharp
public bool UnifyPstrAtomEmpty(Cell pstr, Cell[] heap)
{
    long payload = pstr.Data & Cell.PayloadMask;
    int codeUnits = (int)((payload >> 32) & 0xFFFFFFF);
    
    if (codeUnits != 0) return false;
    
    Cell tail = GetPstrTail(pstr, heap);
    int emptyListAtomId = AtomTable.EmptyListId;
    return tail.Tag == Tag.Atom && (int)(tail.Data & 0xFFFFFFFFL) == emptyListAtomId;
}
```

### PSTR vs REF (unbound variable)

The variable is bound to the PSTR (via the standard bind mechanism: write a REF cell pointing to the PSTR header, or copy the header cell). Since the PSTR header is essentially atomic from the binding perspective, copying is preferred (per the binding policy in ADR-002).

### PSTR vs other tags

Unifying a PSTR with anything other than PSTR, LIS, `[]` atom, or a variable fails.

## When PSTRs become regular lists ("fallback to cons")

In rare cases, operations cannot preserve the PSTR representation. For example, if a program does:

```prolog
[H|T] = Pstr, T = [foo | T2].
```

The tail of the PSTR (after decomposing one character) is unified with `[foo | T2]`, which has an atom `foo` as head. Since PSTR can only contain characters, this requires falling back: the next position of what was the PSTR becomes a regular LIS cell.

The mechanism: when a tail position of a PSTR is unified with a LIS, **the unification proceeds normally** (the LIS is bound to the variable or compared with the existing tail). The PSTR's buffer remains unchanged; future references through this path will see a LIS.

If the PSTR's data is needed AFTER unification has changed a referenced cell, the engine must re-materialize: walk the PSTR and create LIS cells for the part that's now mixed.

In practice, this is rare and the overhead is acceptable. Phase 2 may add optimizations.

## Builtins for PSTR

The following builtins operate on PSTRs with fast paths.

### `pstr_codes/2`

```prolog
pstr_codes(+Pstr, -Codes).   % decompose PSTR to list of codes
pstr_codes(-Pstr, +Codes).   % build PSTR from list of codes
```

Fast path: when the PSTR is fully ground and we're computing codes, allocate the list of cons cells once. When building a PSTR from a code list, traverse the list once, packing into buffer cells.

### `pstr_chars/2`

Analogous, with atoms of one char instead of codes.

### `pstr_length/2`

```prolog
pstr_length(+Pstr, -Length).
```

Returns the number of **codepoints** (not code units). Requires walking the PSTR to detect surrogate pairs.

Optimization: a header flag bit (one of the reserved bits) can mark "BMP-only" PSTRs, where length-in-codepoints equals length-in-code-units. For these (the common case), `pstr_length` is O(1).

### `pstr_concat/3`

```prolog
pstr_concat(+A, +B, -C).
```

In v1: eager. Allocates a new buffer combining A and B's contents.

In Phase 2: lazy. Returns a PSTR whose tail is another PSTR (effectively a chained reference).

### `sub_pstr/5`

```prolog
sub_pstr(+Pstr, +Before, +Length, +After, ?SubPstr).
```

Extracts a substring. Zero-copy when possible (the SubPstr shares Pstr's buffer with a different offset and length).

### `string_to_pstr/2`, `atom_to_pstr/2`

Conversion to and from STRING (opaque) and ATOM types.

### `is_pstr/1`

Type test.

## Behavior under `double_quotes` flag

The `double_quotes` flag controls how source-code string literals (`"..."` in Prolog) are represented and how PSTRs decompose.

| Flag value | Source literal `"hello"` produces | PSTR `[H|T]` decomposition produces H as... |
|-----------|-----------------------------------|---------------------------------------------|
| `codes` (default, ISO) | List `[104, 101, 108, 108, 111]` (cons cells of INT) | `Int(codepoint)` |
| `chars` | List `[h, e, l, l, o]` (cons cells of single-char atoms) | `Atom(char_atom)` |
| `atom` | Atom `'hello'` | (PSTR decomposition still uses `codes` behavior) |
| `string` | STRING cell (opaque) | (PSTR decomposition still uses `codes` behavior) |
| `pstr` (Shumway extension) | PSTR cell | `Atom(char_atom)` (chars-as-atoms by convention) |

In Phase 1, `codes` is the default. PSTRs are not created from source literals; they appear via:

- Reading from streams (`read_pstr/2`, file/network I/O).
- The embedding API from C# (`engine.MakePstr(string)`).
- Explicit conversion builtins (`atom_to_pstr/2`, etc.).

Users who want PSTR-by-default for performance can opt in via `:- set_prolog_flag(double_quotes, pstr).` at the top of their module.

## Memory characteristics

For a string of `n` code units (no surrogates, BMP-only):

- Header: 1 cell
- Buffer: `⌈n/3⌉` cells
- Tail: 1 cell
- **Total**: `⌈n/3⌉ + 2` cells

For `n = 1,000,000`: 333,335 cells = ~2.7 MB. Compare to cons cell list: ~16 MB.

Decomposition is O(1) per character (just compute a new header).

Concatenation in v1: O(n) eager. Phase 2: O(1) lazy.

Indexed access (e.g., `nth0`): O(1) by computing the buffer cell index from the position.

## Integration with the atom GC

The atom GC's mark phase encounters PSTR headers and buffer cells:

- **PSTR header**: contains a heap index to the buffer. The buffer cells contain UTF-16 code units, not atom ids. The mark phase does not add anything to the marked set from PSTR cells **except** for the tail cell.
- **PSTR_BUFFER cells**: contain only character data. The mark phase skips them (no atom references inside).
- **Tail cell**: if it's an ATOM (like `[]`), mark normally.

```csharp
case Tag.Pstr:
    // The header doesn't directly contain atom references.
    // The tail cell will be visited separately during the heap walk.
    break;
case Tag.PstrBuffer:
    // Buffer cells don't contain atom references.
    break;
```

When decomposing a PSTR with `double_quotes = chars`, the engine creates **transient atoms** for single characters. These flow through the atom GC normally (interned in the atom table, included in mark phase when referenced from heap cells).

For programs that decompose long PSTRs as chars, the transient atom table can grow significantly with character atoms. The atom GC cleans them up between queries when no longer referenced.

## Open questions for Phase 2

- **Lazy concatenation**: `pstr_concat(A, B, C)` returns C whose tail is B. Operations on C transparently traverse both buffers. Adds complexity to unification.

- **Shared prefix recognition**: if multiple PSTRs share a prefix (e.g., from parsing alternatives), the buffer can be shared. Requires reference counting or a more complex memory model.

- **Mutable buffer for parser performance**: a parser building output character-by-character could append to a mutable PSTR buffer. Requires the buffer to be exclusively owned (no other PSTR references it). Phase 2 feature.

## Test strategy

- **Roundtrip**: construct a PSTR from a .NET string, convert back, verify equality.
- **Empty PSTR**: construct, decompose (should yield tail directly).
- **Single character PSTR**: decompose, verify head and tail (where tail should be the [] atom).
- **Long PSTR (10K characters)**: construct, decompose all characters one by one, verify content.
- **PSTR with surrogate pairs**: construct from a string with emojis, decompose, verify codepoints are correctly reconstructed (one codepoint per surrogate pair, not two).
- **Unify PSTR vs LIS**: pattern `[H|T] = "abc"`, verify H = 'a' (or 97) and T = "bc" (another PSTR).
- **Unify PSTR vs PSTR**: same content (different buffers) succeeds; different content fails.
- **`pstr_length/2`**: matches the actual codepoint count (with surrogates correctly handled).
- **Concatenation**: `pstr_concat("hello", " world", R)`, verify R is "hello world".
- **Memory**: build a 1 MB PSTR, verify heap usage is < 4 MB (vs. ~16 MB for cons cells).
- **`double_quotes` flag**: decompose a PSTR under each flag value, verify H is the correct representation.
- **Fallback to cons**: deliberately trigger fallback (mix PSTR and atom heads in a list), verify correctness.

## See also

- ADR-002 (Cell Layout): cell encoding rationale.
- `cell-layout-detail.md`: bit-level cell specifications.
- `wam-instruction-set.md`: PSTR-specific bytecode instructions.
- `builtins-catalog.md`: full list of PSTR builtins.
