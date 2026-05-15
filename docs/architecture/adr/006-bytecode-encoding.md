# ADR-006: Bytecode Encoding

## Status

Accepted (Phase 1).

## Context

The bytecode is the intermediate representation produced by the WAM compiler from Prolog source. It is consumed by two backends:

1. **Tier 0 interpreter**: dispatches instruction by instruction over the bytecode.
2. **Tier 1 IL compiler**: reads the bytecode and emits .NET IL.

The encoding choice affects:

- **Interpreter performance**: dispatch loop overhead, cache utilization of bytecode.
- **Memory footprint**: large programs (the user mentioned 50,000+ LOC Prolog as a target) produce significant bytecode.
- **IL compiler complexity**: how easy it is to decode and translate.
- **Extensibility**: ability to add new opcodes without breaking existing bytecode.
- **Debuggability**: ability to inspect and disassemble bytecode.

The encoding also intersects with the debug info system. Debug info can be inlined in the bytecode (slowing the interpreter slightly but enabling fine-grained source-level debugging) or kept in a side table (faster but coarser).

Several encoding styles exist in the literature and in real implementations:

- **Dense binary encoding**: 1-byte opcodes, operands packed tightly. Used by JVM, CLR, most modern VMs.
- **Word-aligned instructions**: each instruction is a fixed-size struct. Simpler to decode, but wastes space.
- **Threaded code**: each instruction contains a pointer to its handler. Fast dispatch but harder to serialize.
- **Tree-based IR**: instructions as objects in memory. Most flexible but worst memory footprint.

For Shumway, the IR has two consumers (interpreter and IL compiler) that need different things, but both benefit from a compact and well-defined binary format.

## Decision

Shumway uses a **dense binary encoding** as a `byte[]` with the following structure.

### Opcode = 1 byte

Each instruction starts with a single opcode byte. The instruction's total size (including operands) is determined by a per-opcode table.

### Operands = unaligned ints

Operands follow the opcode as little-endian 32-bit signed integers (`int`). They are not aligned; reading uses `Unsafe.ReadUnaligned<int>` (or equivalent). Modern x86/x64 CPUs handle unaligned reads at full speed; ARM may pay a small penalty but it remains negligible.

```
Byte layout:
  byte 0:        opcode
  bytes 1..4:    operand 0 (if any)
  bytes 5..8:    operand 1 (if any)
  bytes 9..12:   operand 2 (if any)
  ... up to N operands
```

Most instructions have 0–3 operands. Special instructions (e.g., `switch_on_term`) have more, encoded as additional ints.

### Opcode space

256 opcodes are available (0x00..0xFF).

**Reserved values**:

| Opcode | Purpose |
|--------|---------|
| 0x00   | **Invalid** (Reserved). Encountering this at runtime indicates corruption or a PC misdirection. The interpreter fails loudly. |
| 0xFE   | **Meta opcode**. The next byte indicates the kind of meta info. v1 defines only `DbgInfo` (sub-opcode 0x00). |
| 0xFF   | **Extension**. Reserved for future use if 256 opcodes become insufficient (an "escape" mechanism). Unused in v1. |

**Usable values**:

| Range | Use |
|-------|-----|
| 0x01..0x7F | Core opcodes: get, put, unify, control, choice, indexing, cut, builtins. ~80 opcodes in v1 (including consolidations). |
| 0x80..0xFD | Reserved for future extensions (PSTR-specific, attvar-specific, optimization variants). |

The reserved ranges leave ample room for new opcodes without restructuring the encoding.

### Meta opcode (0xFE)

The Meta opcode has a sub-byte structure:

```
byte 0:    0xFE (Meta)
byte 1:    sub-opcode (kind of meta information)
bytes 2+:  operands depending on sub-opcode
```

Sub-opcodes defined in v1:

| Sub-opcode | Name | Operands |
|------------|------|----------|
| 0x00 | DbgInfo | int (index into `CodeArea.DebugEntries`) |

Future Meta sub-opcodes could include ProfilePoint, TypeAssertion, etc.

### Per-opcode size table

A precomputed table `OpcodeInfo.Table[256]` provides the size of each instruction:

```csharp
public static class OpcodeInfo
{
    public struct Info
    {
        public byte Size;        // total bytes including opcode
        public byte NumOperands;
        public string Mnemonic;  // for disassembler
    }
    
    public static readonly Info[] Table = new Info[256];
    
    static OpcodeInfo()
    {
        Table[(byte)Opcode.GetVariableX] = new Info { Size = 9, NumOperands = 2, Mnemonic = "get_variable_x" };
        Table[(byte)Opcode.GetConstant]   = new Info { Size = 9, NumOperands = 2, Mnemonic = "get_constant" };
        Table[(byte)Opcode.Allocate]      = new Info { Size = 5, NumOperands = 1, Mnemonic = "allocate" };
        Table[(byte)Opcode.Proceed]       = new Info { Size = 1, NumOperands = 0, Mnemonic = "proceed" };
        Table[(byte)Opcode.Call]          = new Info { Size = 9, NumOperands = 2, Mnemonic = "call" };
        // ... full table
    }
}
```

The interpreter advances `_p` by the size of the just-executed instruction (or jumps to a new PC for branches).

### Operand interpretation

The interpretation of each operand depends on the opcode. Documentation accompanies each opcode. Examples:

- `GetVariableX dest_reg, arg_idx`: copy `A[arg_idx]` to `X[dest_reg]`.
- `GetConstant const_id, arg_idx`: unify `A[arg_idx]` with the constant identified by `const_id` (atom id or int value, depending on context).
- `Call address, num_live_perms`: call the predicate at `address`, with `num_live_perms` permanent variables still live (for environment trimming).
- `TryMeElse next_clause_addr, arity`: create a choice point for a predicate with `arity` arguments; the next alternative is at `next_clause_addr`.

For some operands (e.g., literals), the value is a direct integer (atom id, code address). For larger constants (BigInteger, string literal), the operand is an index into a side table in the `CodeArea`.

### CodeArea: bytecode plus auxiliary tables

The bytecode alone is not enough. Some operations need auxiliary data that doesn't fit in the bytecode itself:

```csharp
public class CodeArea
{
    public byte[] Bytes;
    public int Length;
    
    // Switch tables for indexing instructions
    public List<SwitchTable> SwitchTables;
    
    // Large literals that don't fit in int operands
    public List<BigInteger> BigIntLiterals;
    public List<string> StringLiterals;
    public List<double> FloatLiterals;  // for FLOAT cells that may need to be reconstructed
    
    // Predicate entry points
    public Dictionary<FunctorId, int> PredicateEntries;
    
    // Debug info
    public List<DebugEntry> DebugEntries;
    public List<string> SourceFiles;
}
```

A switch table is referenced by index from a `switch_on_*` instruction. The switch table itself contains the mapping from constant value (atom id, int, functor id) to bytecode address.

### Consolidated opcodes (v1)

Beyond the base WAM instruction set, v1 includes consolidations for the most frequent patterns. These reduce dispatch overhead in the interpreter and produce more compact bytecode.

Examples:

- `GetConstantA1`, `GetConstantA2`: specialization for arguments 1 and 2 (very common in clause heads).
- `PutConstantA1`, `PutConstantA2`: similar for argument preparation.
- `GetListA1`, `GetListA2`: specialization of list matching.
- Builtin opcodes for hot builtins: `UnifyEq` (=/2), `Is` (is/2), `LessThan` (</2), `GreaterThan` (>/2), `LessEq` (=</2), `GreaterEq` (>=/2), `ArithEq` (=:=/2), `ArithNotEq` (=\=/2). These skip the general `CallBuiltin` dispatch.

The full set is ~80 opcodes in v1.

### Debug info storage

Debug info lives in a side table (`CodeArea.DebugEntries`), referenced by index from `DbgInfo` meta opcodes embedded in the bytecode. Three levels of detail:

- **None**: no `DbgInfo` opcodes, no debug entries. Smallest bytecode.
- **Basic**: a table with PC → source location mapping, but no inlined `DbgInfo` opcodes. The runtime can map a PC to a source location without invoking debug hooks.
- **Full**: inlined `DbgInfo` opcodes at significant points (clause start, before each goal, etc.). Enables fine-grained stepping and inspection. Adds overhead to interpretation.

The level is configurable per-module via `EngineConfig.DebugLevel`. The IL compiler respects the level (in Full mode, it does not inline across debug points; in None or Basic, it inlines freely).

## Alternatives Considered

### Structured `Instruction[]` (not binary)

**Considered, rejected for v1.** A typed array of structs would be easier to inspect and debug, but uses more memory (a struct is at least the size of its largest case, with padding). For a 50,000-LOC Prolog program, this would be 2–4× the memory of a binary encoding. The IL compiler doesn't need the structured form (a disassembler iterating the binary works fine).

### Word-aligned instructions

**Rejected.** Adds padding for alignment, increasing memory. The performance benefit of aligned reads is negligible on modern hardware.

### Variable-length operands (varint encoding)

**Considered, rejected.** Variable-length operands (e.g., 1–5 bytes for ints) would save memory at the cost of decode complexity. The current encoding already pays only what's needed (most instructions have 1–2 int operands, totaling 5–9 bytes). Varint would save perhaps 30% memory at the cost of slower decoding and complex compiler logic. Not worth it.

### Threaded code (handler pointers in bytecode)

**Rejected.** Cross-process portability of bytecode is desired (for bundles). Threaded code with function pointers cannot be serialized or shared between runs. Furthermore, modern JIT-compiled switch statements are nearly as fast as threaded code in practice.

### Inline debug info in every instruction

**Rejected.** Inflates the bytecode significantly. The side-table approach with optional inlined `DbgInfo` opcodes (only at Full level) is more flexible.

## Consequences

### Positive

- **Compact**: average instruction size ~5–9 bytes. A 50,000-LOC program produces a few MB of bytecode at most.
- **Cache-friendly**: dispatch loop reads sequential bytes, good for branch prediction and instruction cache.
- **Portable**: bytecode can be serialized to disk (in bundles) and loaded into any engine.
- **Extensible**: large reserved opcode ranges and the Meta opcode allow future additions without breaking existing bytecode.
- **Two-consumer design**: both interpreter and IL compiler work from the same binary input.
- **Detectable corruption**: opcode 0x00 catches PC misdirection immediately.

### Negative

- **Not human-readable**: a disassembler is needed for inspection.
- **Unaligned reads**: in theory slightly slower on some architectures, in practice negligible on x86/x64.
- **Hard to evolve format**: if the encoding itself needs to change (e.g., different operand sizes), bytecode is not forward-compatible. Mitigated by including a version field in bundles.

### Mitigations

- **Disassembler**: ship a disassembler tool from day one, both for debugging and as a library API.
- **Bytecode format versioning**: bundles include a version number; the engine rejects bundles with incompatible versions.
- **Documentation**: each opcode is documented with its operand semantics in `design/wam-instruction-set.md`.

## Implementation Notes

### Reading and writing operands

```csharp
public static class BytecodeIO
{
    public static int ReadInt(byte[] code, int offset)
    {
        return Unsafe.ReadUnaligned<int>(ref code[offset]);
    }
    
    public static void WriteInt(byte[] code, int offset, int value)
    {
        Unsafe.WriteUnaligned(ref code[offset], value);
    }
}
```

For performance-critical paths in the interpreter, `unsafe` pointer arithmetic with `fixed` is preferred:

```csharp
public unsafe void Dispatch()
{
    fixed (byte* codePtr = _code.Bytes)
    {
        while (true)
        {
            byte op = codePtr[_p];
            switch ((Opcode)op)
            {
                case Opcode.GetConstant:
                {
                    int constId = Unsafe.ReadUnaligned<int>(codePtr + _p + 1);
                    int argIdx = Unsafe.ReadUnaligned<int>(codePtr + _p + 5);
                    // ... execute
                    _p += 9;
                    break;
                }
                // ... other cases
            }
        }
    }
}
```

### Disassembler

```csharp
public class Disassembler
{
    public IEnumerable<DisassembledInstruction> Iterate(byte[] code, int start, int end)
    {
        int p = start;
        while (p < end)
        {
            Opcode op = (Opcode)code[p];
            var info = OpcodeInfo.Table[(byte)op];
            var inst = new DisassembledInstruction
            {
                Address = p,
                Op = op,
                Mnemonic = info.Mnemonic,
                Operands = new int[info.NumOperands],
            };
            for (int i = 0; i < info.NumOperands; i++)
                inst.Operands[i] = BytecodeIO.ReadInt(code, p + 1 + i * 4);
            yield return inst;
            p += info.Size;
        }
    }
}

public struct DisassembledInstruction
{
    public int Address;
    public Opcode Op;
    public string Mnemonic;
    public int[] Operands;
}
```

The disassembler is used by the IL compiler, by debugging tools, and by tests that verify compiler output.

### Bytecode emission

The WAM compiler emits bytecode using a `CodeAreaBuilder`:

```csharp
public class CodeAreaBuilder
{
    public void EmitOpcode(Opcode op) { _bytes.Add((byte)op); }
    public void EmitInt(int value) { /* append 4 bytes */ }
    public Label DefineLabel() { /* ... */ }
    public void MarkLabel(Label label) { /* fixup later */ }
    public void EmitOpcodeWithLabel(Opcode op, Label target) { /* deferred patching */ }
    
    public CodeArea Build() { /* resolve labels, return CodeArea */ }
}
```

Forward references (e.g., `try_me_else <future_addr>`) are handled by deferred label patching.

## Test Strategy

- **Round-trip**: emit each opcode with various operands, disassemble, verify match.
- **Size table consistency**: for every opcode, verify the size in `OpcodeInfo.Table` matches the actual bytes emitted.
- **Disassembler termination**: disassembling a well-formed bytecode segment terminates exactly at the end.
- **0x00 detection**: a bytecode segment containing 0x00 causes a fail during dispatch (in debug builds, with diagnostic info).
- **Bytecode growth**: emit a large bytecode, verify the byte array grows correctly.
- **Cross-platform endianness**: serialize a bundle on x86 (little-endian), load on ARM, verify operands are correctly read. (.NET specifies little-endian for `Unsafe.ReadUnaligned<int>` regardless of platform, so this should be invariant.)

## Related ADRs

- ADR-007 (Indexing): switch instructions reference switch tables in `CodeArea`.
- ADR-009 (Bundler): bundles contain bytecode + auxiliary tables.
- ADR-011 (IL Compiler): consumes bytecode via the disassembler.

## Related Design Docs

- `design/wam-instruction-set.md`: complete instruction set with operand semantics, sizes, and effects.
- `design/debug-info.md`: details of the DbgInfo meta opcode and the side table.
