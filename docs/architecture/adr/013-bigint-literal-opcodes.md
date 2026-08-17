# ADR-013: BigInteger Literal Opcodes

## Status

Accepted ([Phase 1](../../history/phase-1-closure.md)).

## Context

The WAM bytecode encoding (ADR-006) uses 32-bit operands. The Phase-1 opcodes
`put_integer`, `get_integer`, and `unify_integer` carry their integer literal
inline as an `OperandKind.IntValue` (32-bit signed) operand. This works for the
vast majority of source-level integer literals, but breaks down for:

1. **Integer literals that don't fit in `int32` but fit in `long`** — e.g.
   `1099511627776` (2^40). The runtime supports them (`Cell.Int` accepts up
   to the 60-bit inline range), but the operand encoding can't carry them.
2. **Integer literals that don't fit in `long`** — e.g. `10^21`. The runtime
   already supports these via the `Tag.BigInt` side table and the BigInteger
   arithmetic promotion path delivered in chunk 38 (overflow during `is/2`
   automatically lifts the result), but source-level literals had no compile
   path.

The clause compiler used to reject both categories with a `NotSupportedException`
out of `CheckInt32`:

```text
Integer literal 1000000000000000000 doesn't fit in a 32-bit operand.
BigInt support lands later.
```

This ADR lifts that restriction so source-level BigInteger literals
work the same way runtime-promoted BigInteger results already do.

ADR-006 marks "adding a new top-level opcode" as a major decision that needs an
ADR. This is that ADR.

## Decision

Three new opcodes are added to the WAM instruction set. Each mirrors the
existing float-literal opcode of the same shape, but uses the engine's
BigInteger side table instead of the float heap representation:

| Opcode        | Hex   | Size | Operands                              |
|---------------|-------|------|----------------------------------------|
| `GetBigInt`   | 0x0C  | 9    | `LiteralId`, `Reg`                     |
| `PutBigInt`   | 0x18  | 9    | `LiteralId`, `Reg`                     |
| `UnifyBigInt` | 0x25  | 5    | `LiteralId`                            |

These reuse the existing get/put/unify machinery. The ids above are the current
ones: opcodes were later renumbered into one contiguous block (ADR-006), so the
original per-category bands no longer apply — `Opcode.cs` is authoritative. The
names and 9/9/5 sizes are the stable part.

The `LiteralId` operand is an index into a per-module
`LiteralPool<BigInteger>` that the compiler builds alongside the existing
`LiteralPool<string>` (for PSTR) and `LiteralPool<double>` (for floats).
The pool surfaces as `CompiledModule.BigIntLiterals` and rides through
the same compile-link-interpret path as the float pool.

### Compile-time routing

The clause compiler routes integer literals based on their range:

- Value fits in `int32` → `put_integer` / `get_integer` / `unify_integer`
  (fast path, inline operand).
- Value fits in `long` but not `int32` → `put_bigint` / `get_bigint` /
  `unify_bigint` (pool lookup yields a `BigInteger(longValue)`).
- Value doesn't fit in `long` (came from a `BigIntTerm` AST node) →
  `put_bigint` / `get_bigint` / `unify_bigint`.

This means the compiler is type-agnostic at the call site — `IntTerm` and
`BigIntTerm` both compile via the bigint pool when needed.

### Runtime semantics

The three opcodes match their float-literal cousins almost exactly. The
interpreter calls `Engine.MakeBigInt(pool[literalId])` to materialise the
value, then performs the same register / heap / unify-pointer dance as the
float path. The only structural difference is that `Cell.BigInt` is a
single-cell representation (table id in the payload), so `unify_bigint`
in write mode allocates one heap slot and writes directly — no
PreEmitMultiCellLiterals dance is needed (that's a float-specific workaround
for the 2-cell heap layout of `Tag.Float`).

### MakeBigInt canonicalisation

`Engine.MakeBigInt` was changed to **auto-collapse** values that fit in the
60-bit inline range to `Cell.Int(...)` instead of allocating a side-table
slot. The invariant is now:

> A given integer value has exactly one canonical cell representation:
> `Tag.Int` if it fits the 60-bit inline range, `Tag.BigInt` otherwise.

This means `Cell.Int(5)` and `Engine.MakeBigInt(new BigInteger(5))` yield
the same cell, and unification doesn't have to cross tag boundaries to
recognise that they represent the same value. Without this collapse,
`X = 5` in source (compiled as `put_integer 5`) and `X = 5` in a context
that routed through the bigint pool (e.g. emitted from a foreign-API
materialisation) would not unify.

### Persistence

`CompiledModule.BigIntLiterals` is persisted by `CompiledModuleCodec`
alongside the string and float literal tables. Each value is serialised as
its `BigInteger.ToByteArray()` byte sequence, length-prefixed. Bundles
written before chunk 38 (which had no bigint pool) round-trip unchanged
because the new section is just a count-prefixed list that's empty in those
cases.

### Lexer / parser

The lexer's integer reader now falls back to `BigInteger.Parse` when
`long.TryParse` overflows. Tokens carry a `BigValue` field and a
`HasBigValue` flag; the parser produces a `BigIntTerm` when set, an
`IntTerm` otherwise. The negative-literal collapse (chunk 37) handles
BigInts the same way it handles longs.

## Consequences

**Positive**:

- Source-level BigInteger literals work everywhere `IntTerm` works: in
  clause heads, body args, compound sub-args, arithmetic expressions,
  `is/2`, and bundle round-trips.
- No special syntax — `1000000000000000000000` is a valid integer literal
  just like `42`.
- Existing tests using `1000000 * 1000000 * 1000000 * 1000000` to *build*
  large values via arithmetic chains still work (the runtime path is
  unchanged), but tests can now use literals directly.
- The `Cell.Int` / `Cell.BigInt` canonicalisation invariant simplifies
  unification (no cross-tag value comparison needed).

**Negative**:

- Three more opcodes consumed. Opcode ids come from a single contiguous block
  (ADR-006) with ample room, so there is no scarcity concern.
- The bigint side table grows monotonically — every `MakeBigInt(value)`
  with a side-table-bound value appends a fresh slot. A future
  optimisation could deduplicate, but the current behaviour matches what
  the float path does and isn't a Phase-1 bottleneck.

**Out of scope for this ADR** (both since delivered):

- Tier-1 IL compiler support for the new opcodes — at the time the IL
  compiler handled a small opcode subset; full coverage arrived with the
  Phase-20 Tier-1 completeness work.
- Trail-aware BigInteger allocation — later shipped as the
  `TrailType.BigIntAlloc` entries (rationals mirror it with `RationalAlloc`),
  so backtracked-over BigIntegers are reclaimed.

## References

- ADR-002: Cell Layout (`Tag.BigInt`, side-table convention).
- ADR-006: Bytecode Encoding (opcode space, operand encoding, `OperandKind`).
- `src/Shumway.Compiler/Wam/ClauseCompiler.cs` — `FitsInt32` plus the three
  IntTerm / BigIntTerm switch cases that route to the new opcodes.
- `src/Shumway.Interpreter/BytecodeInterpreter.cs` — the three new dispatch
  arms near the float-literal handlers.
