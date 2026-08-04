# Bundle Format Specification

> **This describes an earlier proposed container that is NOT the implemented
> `.shum` format.** The byte-level layout below (a 32-byte header with CRC32, a
> typed-section table, an `"END!"` footer with a whole-file CRC) was never
> built. The real format (`src/Shumway.Embedding/BundleFormat.cs`, authoritative)
> is: magic `SHUM`, a `uint32` version, a one-byte compression flag, then a
> module-list body (optionally Brotli-compressed) — no section table, no CRC, no
> footer. The current version is **6**, and the format is **frozen pre-release**:
> the reader requires *exactly* the current version and rejects anything else
> (there is no `Min`/`MaxSupportedFormatVersion` range). Treat the spec below as
> a historical design sketch; for the shipped layout read `BundleFormat.cs` /
> `BundleReader.cs` / `BundleWriter.cs`.

This document specifies the on-disk binary format of Shumway bundles (`.shum` files). It complements ADR-009 by providing exact byte-level layout, encoding rules, and validation requirements.

## Conventions

- All integers are **little-endian** unless explicitly noted.
- All offsets are byte offsets from the start of the file.
- All strings are **UTF-8** with a 32-bit length prefix (in bytes, not codepoints).
- `int32` is a 32-bit signed integer. `uint32` is unsigned. `int64` is 64-bit signed.
- Section lengths are inclusive of the length prefix itself.

## File structure

```
[Header: 32 bytes]
[Section: AtomTable]
[Section: FunctorTable]
[Section: BigIntTable]
[Section: StringTable]
[Section: FloatTable]
[Section: Modules]
[Section: Bytecode]
[Section: SwitchTables]
[Section: PstrLiterals]
[Section: OperatorDeclarations]
[Section: ModeDeclarations]
[Section: DebugInfo]      (optional, present if header flag is set)
[Section: PredicateEntries]
[Section: EntryPoints]
[Footer: 8 bytes]
```

Sections appear in the order above. Section presence is mandatory except where noted as optional.

## Header (32 bytes)

```
Offset  Size  Field
0       4     Magic: ASCII bytes "SHUM" (0x53 0x48 0x55 0x4D)
4       4     Format version (int32): currently 1
8       4     Engine version targeted (int32): the Shumway runtime version this bundle expects
12      4     Flags (uint32):
                bit 0: HasDebugInfo
                bit 1: HasCompiledIl (Phase 2)
                bit 2: HasPstrLiterals
                bits 3..31: reserved (zero)
16      4     Number of sections (int32): for validation
20      4     File size (int32): total size in bytes, for quick validation
24      4     Header CRC32 (uint32): CRC of bytes 0..23
28      4     Reserved (zero)
```

If magic, format version, or header CRC fail, the bundle is rejected with a clear error.

## Section header (8 bytes per section)

Each section begins with:

```
Offset  Size  Field
0       4     Section type (int32): identifier of the section
4       4     Section length (int32): total bytes including this header
```

Section types:

```
0x0001  AtomTable
0x0002  FunctorTable
0x0003  BigIntTable
0x0004  StringTable
0x0005  FloatTable
0x0010  Modules
0x0011  Bytecode
0x0012  SwitchTables
0x0013  PstrLiterals
0x0020  OperatorDeclarations
0x0021  ModeDeclarations
0x0030  DebugInfo
0x0040  PredicateEntries
0x0041  EntryPoints
0x00FF  (reserved)
0x0100..0xFFFF  reserved for future use
```

Unknown section types in a future bundle version are **skipped** (forward compatibility for additive changes). The reader uses the section length to skip the section's payload.

## AtomTable (section 0x0001)

```
Offset  Size  Field
0       4     Section type
4       4     Section length
8       4     Number of atom entries (int32)
12      N     Entries (variable length)
```

Each entry:

```
4 bytes    Atom id (int32, as used in the bundle's bytecode)
4 bytes    Name length in bytes (int32)
N bytes    UTF-8 name
0..3 bytes Padding to 4-byte alignment
```

The bundle's atom ids are local to the bundle. When loading, the engine maps each bundle atom id to a global atom id (interning the names). The bytecode is then patched (see "Atom remapping on load").

The first few atom ids may correspond to **pre-registered atoms** (`[]`, `{}`, `.`, `true`, `false`). The loader verifies these match the engine's pre-registered atoms.

## FunctorTable (section 0x0002)

```
Offset  Size  Field
0       4     Section type
4       4     Section length
8       4     Number of functor entries (int32)
12      N     Entries
```

Each entry:

```
4 bytes    Functor id (int32, in bundle's id space)
4 bytes    Name atom id (int32, refers to AtomTable)
4 bytes    Arity (int32)
```

## BigIntTable (section 0x0003)

```
Offset  Size  Field
0       4     Section type
4       4     Section length
8       4     Number of entries (int32)
12      N     Entries
```

Each entry:

```
4 bytes    BigInt id (int32, in bundle's id space)
4 bytes    Byte length of the BigInteger encoding (int32)
N bytes    BigInteger value (System.Numerics.BigInteger.ToByteArray() format)
0..3 bytes Padding to 4-byte alignment
```

## StringTable (section 0x0004)

```
Offset  Size  Field
0       4     Section type
4       4     Section length
8       4     Number of entries (int32)
12      N     Entries
```

Each entry:

```
4 bytes    String id (int32)
4 bytes    Length in bytes (int32)
N bytes    UTF-8 string content
0..3 bytes Padding
```

## FloatTable (section 0x0005)

```
Offset  Size  Field
0       4     Section type
4       4     Section length
8       4     Number of entries (int32)
12      N     Entries (8 bytes each: float id + double value)
```

Each entry:

```
4 bytes    Float id (int32)
4 bytes    Padding/reserved (zero)
8 bytes    Double value (IEEE 754, little-endian)
```

This table holds float literals referenced by bytecode (e.g., from `is/2` with constant floats).

## Modules (section 0x0010)

```
Offset  Size  Field
0       4     Section type
4       4     Section length
8       4     Number of modules (int32)
12      N     Module entries
```

Each module entry:

```
4 bytes    Module id (int32, in bundle's id space)
4 bytes    Name atom id (int32, refers to AtomTable)
4 bytes    Source file id (int32, refers to SourceFiles array in DebugInfo; -1 if no debug info)
4 bytes    Number of local predicates (int32)
4 bytes    Number of public predicates (int32)
4 bytes    Module flags (uint32):
             bit 0: HasUnresolvedReferences (should be 0 in a valid bundle)
             bit 1: HasDynamicPredicates
             bits 2..31: reserved
N bytes    Local predicate functor ids (int32 each)
M bytes    Public predicate functor ids (int32 each)
```

## Bytecode (section 0x0011)

```
Offset  Size  Field
0       4     Section type
4       4     Section length
8       4     Bytecode length (int32, in bytes)
12      N     Raw bytecode bytes
0..3 bytes Padding to section boundary
```

This is the contiguous bytecode for all included predicates. Predicates point into this byte array via offsets (in PredicateEntries).

## SwitchTables (section 0x0012)

```
Offset  Size  Field
0       4     Section type
4       4     Section length
8       4     Number of switch tables (int32)
12      N     Table entries
```

Each switch table entry:

```
4 bytes    Switch table id (int32)
4 bytes    Number of cases (int32)
4 bytes    Default address (int32, bytecode offset)
4 bytes    Use dictionary flag (int32, 0 or 1)
N bytes    Cases: pairs of (key, address), each 8 bytes
```

The "use dictionary" flag indicates whether the engine should build a `Dictionary<int, int>` for fast lookup (when N > 16) or use the linear array form.

## PstrLiterals (section 0x0013)

```
Offset  Size  Field
0       4     Section type
4       4     Section length
8       4     Number of entries (int32)
12      N     Entries
```

Each entry:

```
4 bytes    PSTR literal id (int32)
4 bytes    Length in UTF-16 code units (int32)
N bytes    UTF-16 code units (length * 2 bytes), little-endian
0..3 bytes Padding to 4-byte alignment
```

PSTR literals appear in the bytecode via `get_pstr` / `put_pstr` instructions, which reference them by id.

## OperatorDeclarations (section 0x0020)

```
Offset  Size  Field
0       4     Section type
4       4     Section length
8       4     Number of operators (int32)
12      N     Operator entries
```

Each operator entry:

```
4 bytes    Priority (int32, 0..1200)
4 bytes    Operator type (int32):
             0 = xfx, 1 = xfy, 2 = yfx
             3 = xf, 4 = yf
             5 = fx, 6 = fy
4 bytes    Atom id of the operator (int32)
```

These are applied globally when the bundle is loaded. Conflicts with already-registered operators are warnings (the bundle's declaration overrides).

## ModeDeclarations (section 0x0021)

```
Offset  Size  Field
0       4     Section type
4       4     Section length
8       4     Number of mode declarations (int32)
12      N     Mode entries
```

Each mode entry:

```
4 bytes    Functor id (int32)
4 bytes    Number of argument mode indicators (int32, equals the predicate arity)
4 bytes    Determinism (int32): 0=Det, 1=SemiDet, 2=Multi, 3=NonDet, 4=NoneDeclared
N bytes    Argument indicators (1 byte each): 0='+', 1='-', 2='?'
0..3 bytes Padding to 4-byte alignment
```

In v1, these are stored but not used by the code generator. Phase 3 will exploit them.

## DebugInfo (section 0x0030, optional)

Present only if header flag `HasDebugInfo` is set.

```
Offset  Size  Field
0       4     Section type
4       4     Section length
8       4     Debug level (int32): 0=None, 1=Basic, 2=Full
12      4     Number of source files (int32)
16      N     Source file paths (string entries: 4 bytes length + UTF-8)
M       4     Number of debug entries (int32)
M+4     P     Debug entries
```

Each debug entry:

```
4 bytes    PC start (int32)
4 bytes    PC end (int32)
4 bytes    Source file id (int32)
4 bytes    Line (int32)
4 bytes    Column (int32)
4 bytes    Kind (int32)
4 bytes    Annotation length (int32, 0 if no annotation)
N bytes    Annotation UTF-8 (if length > 0)
0..3 bytes Padding
4 bytes    Number of variable infos (int32)
M bytes    Variable info entries (variable size)
```

Each variable info:

```
4 bytes    Name length (int32)
N bytes    Name UTF-8
0..3 bytes Padding
1 byte     IsPermanent (0 or 1)
4 bytes    Index (int32)
4 bytes    Type hint (int32, 0 = none, reserved values for future hints)
3 bytes    Padding
```

At Basic level, the `Variables` field is empty (zero count).

## PredicateEntries (section 0x0040)

```
Offset  Size  Field
0       4     Section type
4       4     Section length
8       4     Number of entries (int32)
12      N     Entries
```

Each entry:

```
4 bytes    Functor id (int32)
4 bytes    Module id (int32, where defined)
4 bytes    Bytecode address (int32, offset into Bytecode section's data)
4 bytes    Flags (uint32):
             bit 0: IsPublic
             bit 1: IsDynamic
             bit 2: HasIndex
             bit 3: IsCompiledIl (Phase 2, indicates IL is available)
             bits 4..31: reserved
4 bytes    Number of atom references (int32)
N bytes    Atom reference ids (4 bytes each, for GC marking)
```

The atom references list is the set of atoms referenced by this predicate's bytecode. Used by the atom GC to mark atoms reachable from predicate metadata (separate from heap scanning).

## EntryPoints (section 0x0041)

```
Offset  Size  Field
0       4     Section type
4       4     Section length
8       4     Number of entry points (int32)
12      N     Functor ids (int32 each)
```

Lists the functor ids of public predicates designated as entry points when the bundle was built. The runtime can query this to validate that expected entries are available.

## Footer (8 bytes)

```
Offset  Size  Field
0       4     Total file CRC32 (uint32): CRC of all preceding bytes
4       4     Magic: ASCII "END!" (0x45 0x4E 0x44 0x21)
```

The footer CRC validates the entire file. The "END!" magic provides an additional sanity check.

## Atom remapping on load

The bundle's atom ids are local (assigned at bundle build time). The engine's atom table has its own global ids. When loading:

1. Read the AtomTable section.
2. For each atom name, intern it in the engine's global atom table, getting a global id.
3. Build a remap array: `bundle_id → global_id`.
4. Walk the bytecode and rewrite atom operands using the remap array.
5. Similarly remap functor ids (which themselves reference atom ids).

```csharp
public int[] BuildAtomRemap(BundleAtomTable bundleAtoms)
{
    var remap = new int[bundleAtoms.MaxId + 1];
    foreach (var (bundleId, name) in bundleAtoms.Entries)
    {
        int globalId = AtomTable.Intern(name, permanent: true);
        remap[bundleId] = globalId;
    }
    return remap;
}

public void PatchBytecode(byte[] bytecode, int[] atomRemap, int[] functorRemap)
{
    int p = 0;
    while (p < bytecode.Length)
    {
        byte op = bytecode[p];
        var info = OpcodeInfo.Table[op];
        
        // For each operand that's an atom id or functor id, remap it
        for (int i = 0; i < info.NumOperands; i++)
        {
            int operandOffset = p + 1 + i * 4;
            int operandKind = info.OperandKinds[i];  // metadata about each operand
            
            if (operandKind == OperandKind.AtomId)
            {
                int bundleId = BytecodeIO.ReadInt(bytecode, operandOffset);
                int globalId = atomRemap[bundleId];
                BytecodeIO.WriteInt(bytecode, operandOffset, globalId);
            }
            else if (operandKind == OperandKind.FunctorId)
            {
                int bundleId = BytecodeIO.ReadInt(bytecode, operandOffset);
                int globalId = functorRemap[bundleId];
                BytecodeIO.WriteInt(bytecode, operandOffset, globalId);
            }
            // Other operands (heap indices, addresses) are not remapped
        }
        
        p += info.Size;
    }
}
```

The `OpcodeInfo.Table` is augmented with per-operand kind metadata so the patcher knows what to remap.

## Determinism

The bundle is byte-deterministic given identical inputs:

- Atoms are interned in source-order (first occurrence wins for id assignment).
- Sections are written in fixed order.
- CRC is computed on the deterministic byte stream.

This enables build caching: identical inputs produce identical outputs, so a build system can skip rebuilding if the input hash hasn't changed.

## Version compatibility

When the engine loads a bundle:

```csharp
public void LoadBundle(string path)
{
    using var stream = File.OpenRead(path);
    var header = ReadHeader(stream);
    
    if (header.Magic != "SHUM")
        throw new BundleFormatException("Invalid magic bytes");
    
    if (header.FormatVersion > MaxSupportedFormatVersion)
        throw new BundleFormatException(
            $"Bundle format version {header.FormatVersion} not supported. " +
            $"Engine supports up to version {MaxSupportedFormatVersion}.");
    
    if (header.FormatVersion < MinSupportedFormatVersion)
        throw new BundleFormatException(
            $"Bundle format version {header.FormatVersion} too old. " +
            $"Engine requires at least version {MinSupportedFormatVersion}.");
    
    // Validate engine version
    if (header.EngineVersionTargeted > CurrentEngineVersion)
    {
        // Warning: bundle was built for a newer engine, may have compatibility issues
        Logger?.Log(LogLevel.Warning, $"Bundle targets engine version {header.EngineVersionTargeted}");
    }
    
    // ... load sections
}
```

## Size estimates

For typical inputs:

- **Small program** (100 LOC Prolog, 10 predicates): ~10–20 KB bundle.
- **Medium program** (5,000 LOC, 500 predicates): ~500 KB – 1 MB.
- **Large program** (50,000 LOC, 5,000 predicates): ~5–10 MB.

Debug info adds 20-50% to size at Basic level, 100% at Full level.

## Validation

The loader performs the following checks:

1. Magic bytes and format version (header).
2. Header CRC matches.
3. File size matches header field.
4. Footer magic ("END!") and total CRC match.
5. Section types are in expected order (or are skip-safe unknown sections).
6. All references resolve: predicate addresses are within bytecode bounds, atom ids are valid, functor ids reference valid atoms, etc.

Failures produce specific error messages identifying the issue.

## See also

- ADR-009 (Bundler Design): high-level rationale and CLI usage.
- ADR-006 (Bytecode Encoding): the bytecode format itself.
- ADR-002 (Cell Layout): cell encoding (referenced from auxiliary tables).
