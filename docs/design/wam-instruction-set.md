# WAM Instruction Set

This document specifies the core WAM instruction set used by Shumway (as of
Phase 25 / 2026-05). It complements ADR-006 by providing the exact semantics,
operands, and bytecode encoding of each instruction it covers.

> **Not exhaustive — `Opcode.cs` is the authority.** Many opcodes were added
> after this document was written and are specified by their ADRs rather than
> here: the arithmetic instruction set (`a_eval_*`, `a_int_bin`/`a_int_cmp` —
> ADR-018), inline nested compound build (`unify_structure`/`unify_list`,
> reserve-upfront forms — ADR-019/020), body `jump` (ADR-025), second-level and
> structure-keyed indexing (`switch_on_*_sub`, `switch_on_structure_sub` —
> ADR-027/028), epilogue fusions (`deallocate_execute`,
> `cut_deallocate_proceed`, `cut_proceed` — ADR-029), baked tier dispatch
> (`call_il`/`execute_il`/`call_bytecode`/`execute_bytecode`,
> `execute_builtin`), inline comparisons (`unify_eq` family), `get_level_b`,
> and the debugger trio (`break`, `debug_lastcall`, `debug_port` — ADR-035).
> Numeric opcode values cited below may have shifted (opcodes are renumbered to
> keep the dense dispatch block contiguous); trust `shumway-disasm` output and
> `src/Shumway.Core/Opcode.cs` for live values.

## Conventions

For each instruction, the spec provides:

- **Opcode**: the byte value (in hex).
- **Mnemonic**: the symbolic name used in disassembly.
- **Operands**: each operand, its type, and its meaning.
- **Total size**: bytecode size in bytes (including opcode).
- **Semantics**: what the instruction does.
- **Side effects**: writes to heap, trail, stack, registers.

Operand types:

- `int`: 32-bit signed integer (unaligned, little-endian).
- `address`: 32-bit signed code offset (relative to start of CodeArea bytecode).
- `reg`: 32-bit signed register index (`X1=0`, `X2=1`, ...).
- `perm`: 32-bit signed permanent variable index (`Y1=0`, `Y2=1`, ...).
- `atom_id`: 32-bit atom id.
- `functor_id`: 32-bit functor id.
- `int_value`: 32-bit signed value (for small constants embedded in code).

## Opcode space organization

```
0x00          Reserved (Invalid)
0x01..0x1F    Get instructions
0x20..0x3F    Put instructions
0x40..0x4F    Unify instructions
0x50..0x5F    Control instructions
0x60..0x6F    Choice point instructions
0x70..0x7F    Indexing instructions
0x80..0x8F    Cut instructions
0x90..0x9F    Builtin call instructions
0xA0..0xBF    Consolidated patterns (v1)
0xC0..0xCF    PSTR instructions
0xD0..0xFD    Reserved for future extensions
0xFE          Meta opcode (sub-byte for kind)
0xFF          Reserved (Extension escape)
```

**These per-category bands are the original scheme and no longer describe the
encoding.** Opcodes were renumbered into one dense contiguous block (from 0x00
upward) so dispatch is a single jump table; the ranges above survive only as a
historical grouping. `src/Shumway.Core/Opcode.cs` is authoritative for every id,
and the hex ids in the per-opcode tables below are frequently stale — the
opcode **names and sizes** are the current part.

---

## Get instructions (clause head matching, "read mode")

These instructions appear at the beginning of a clause body to match the head against the arguments of the call.

### get_variable_x (0x01)

Operands: `reg dest`, `reg arg`
Total size: 9 bytes

Copies the argument register to a temporary variable.

```
X[dest] := X[arg]
```

No deref, no unification. Just register copy. The destination becomes whatever the argument is (a variable, an atom, etc.).

### get_variable_y (0x02)

Operands: `perm dest`, `reg arg`
Total size: 9 bytes

Copies the argument register to a permanent variable in the current environment.

```
Y[dest] := X[arg]
```

### get_value_x (0x03)

Operands: `reg src`, `reg arg`
Total size: 9 bytes

Unifies a previously-set temporary variable with the argument.

```
unify(X[src], X[arg])
```

If unification fails, the instruction triggers backtracking.

### get_value_y (0x04)

Operands: `perm src`, `reg arg`
Total size: 9 bytes

Unifies a permanent variable with the argument.

```
unify(Y[src], X[arg])
```

### get_constant (0x05)

Operands: `atom_id const`, `reg arg`
Total size: 9 bytes

Unifies the argument with a constant atom.

```
unify(X[arg], Atom(const))
```

For non-atom constants (integers, etc.), use specialized opcodes (`get_integer`, etc.).

### get_integer (0x06)

Operands: `int_value value`, `reg arg`
Total size: 9 bytes

Unifies the argument with a 32-bit integer constant.

```
unify(X[arg], Int(value))
```

For integers outside the 32-bit range, the compiler uses `get_bigint` (with a BigInt table index) or a longer encoding.

### get_atom (0x07)

Operands: `atom_id atom`, `reg arg`
Total size: 9 bytes

Same as `get_constant` but explicitly named for clarity. The compiler may use this for atom-specific paths.

### get_nil (0x08)

Operands: `reg arg`
Total size: 5 bytes

Unifies the argument with `[]`.

```
unify(X[arg], Atom([])) 
```

This is a specialization of `get_atom` for the very common case.

### get_structure (0x09)

Operands: `functor_id functor`, `reg arg`
Total size: 9 bytes

Unifies the argument with a structure of the given functor. Sets the engine's mode:

- If `X[arg]` derefs to an unbound REF: **write mode**. A new STR+FUNCTOR cell pair is allocated on the heap, and the variable is bound to it. Subsequent `unify_*` instructions write arguments.
- If `X[arg]` derefs to a STR cell with matching functor: **read mode**. Subsequent `unify_*` instructions read and unify with the existing arguments.
- Otherwise: fail.

The engine maintains a `_writeMode` flag and a `_unifyPointer` cursor for the subsequent `unify_*` instructions.

### get_list (0x0A)

Operands: `reg arg`
Total size: 5 bytes

Unifies the argument with a non-empty list (cons cell).

- If `X[arg]` derefs to an unbound REF: **write mode**. A LIS cell is allocated, and the variable is bound. Subsequent `unify_*` write head and tail.
- If `X[arg]` derefs to a LIS cell: **read mode**.
- Otherwise: fail.

---

## Put instructions (preparing call arguments, "write mode")

These instructions appear before a `call` or `execute` to set up the argument registers for the callee.

### put_variable_x (0x20)

Operands: `reg dest_perm_and_arg`, `reg arg`
Total size: 9 bytes

Creates a new unbound variable on the heap, stores it in both `X[dest_perm_and_arg]` (typically same as arg) and `X[arg]`.

```
new_var := allocate_heap_unbound()
X[dest_perm_and_arg] := new_var
X[arg] := new_var
```

### put_variable_y (0x21)

Operands: `perm dest`, `reg arg`
Total size: 9 bytes

Creates a new unbound variable, stores it in `Y[dest]` and `X[arg]`.

### put_value_x (0x22)

Operands: `reg src`, `reg arg`
Total size: 9 bytes

Copies a temporary variable to the argument register.

```
X[arg] := X[src]
```

### put_value_y (0x23)

Operands: `perm src`, `reg arg`
Total size: 9 bytes

Copies a permanent variable to the argument register.

```
X[arg] := Y[src]
```

### put_constant (0x24)

Operands: `atom_id const`, `reg arg`
Total size: 9 bytes

Sets the argument register to a constant atom.

```
X[arg] := Atom(const)
```

### put_integer (0x25)

Operands: `int_value value`, `reg arg`
Total size: 9 bytes

Sets the argument register to an integer.

```
X[arg] := Int(value)
```

### put_atom (0x26)

Operands: `atom_id atom`, `reg arg`
Total size: 9 bytes

Same as `put_constant`. Provided for symmetry with `get_atom`.

### put_nil (0x27)

Operands: `reg arg`
Total size: 5 bytes

Sets the argument register to `[]`.

```
X[arg] := Atom([])
```

### put_structure (0x28)

Operands: `functor_id functor`, `reg arg`
Total size: 9 bytes

Begins constructing a structure on the heap. Allocates a STR cell pointing to a FUNCTOR cell, sets `X[arg]` to the STR.

```
str_addr := heap_top
allocate_heap(STR pointing to heap_top+1, FUNCTOR(functor))
X[arg] := Str(str_addr)
```

Subsequent `unify_*` instructions in write mode populate the arguments.

### put_list (0x29)

Operands: `reg arg`
Total size: 5 bytes

Begins constructing a cons cell. Allocates a LIS cell, sets `X[arg]` to it.

```
lis_addr := heap_top
allocate_heap(LIS pointing to heap_top+1)
X[arg] := Lis(lis_addr)
```

Subsequent `unify_*` write head and tail.

---

## Unify instructions (read/write mode-sensitive)

These instructions follow `get_structure`, `get_list`, `put_structure`, or `put_list`. Their behavior depends on the mode (read vs write).

### unify_variable_x (0x40)

Operands: `reg target`
Total size: 5 bytes

- Read mode: copies the cell at the unify pointer to `X[target]`, advances the pointer.
- Write mode: creates a new unbound variable on the heap and stores it both in the structure (at the unify pointer) and in `X[target]`.

### unify_variable_y (0x41)

Operands: `perm target`
Total size: 5 bytes

Same as `unify_variable_x` but the target is a permanent variable.

### unify_value_x (0x42)

Operands: `reg src`
Total size: 5 bytes

- Read mode: unifies `X[src]` with the cell at the unify pointer.
- Write mode: writes `X[src]` to the heap at the unify pointer.

### unify_value_y (0x43)

Operands: `perm src`
Total size: 5 bytes

Same as `unify_value_x` but reading from a permanent.

### unify_constant (0x44)

Operands: `atom_id const`
Total size: 5 bytes

- Read mode: unifies the cell at the unify pointer with `Atom(const)`.
- Write mode: writes `Atom(const)` to the heap.

### unify_integer (0x45)

Operands: `int_value value`
Total size: 5 bytes

Like `unify_constant` but for integer literals.

### unify_atom (0x46)

Operands: `atom_id atom`
Total size: 5 bytes

Same as `unify_constant`. Symmetry with `get_atom`/`put_atom`.

### unify_nil (0x47)

Operands: (none)
Total size: 1 byte

Equivalent to `unify_atom` with the `[]` atom id. Common pattern.

### unify_void (0x48)

Operands: `int count`
Total size: 5 bytes

- Read mode: advances the unify pointer by `count` (skipping arguments that are not bound to anything).
- Write mode: creates `count` unbound variables on the heap.

Used for anonymous variables (`_`) in clause heads and structures.

---

## Control instructions

### allocate (0x50)

Operands: `int num_perms`
Total size: 5 bytes

Allocates an environment frame on the stack with `num_perms` permanent variable slots.

```
new_e := stack_top
stack[new_e + 0] := CE = current_e
stack[new_e + 1] := CP = current_cp
for i in 0..num_perms-1:
    stack[new_e + 2 + i] := UnboundVar(new_e + 2 + i)
stack_top := new_e + 2 + num_perms
current_e := new_e
```

See ADR-005 for stack layout.

### deallocate (0x51)

Operands: (none)
Total size: 1 byte

Removes the current environment.

```
current_cp := stack[current_e + 1].Data
current_e := stack[current_e + 0].Data
```

Note: stack top is not reduced here; that's handled later (typically at trust_me or when the predicate fully returns).

### call (0x52)

Operands: `address target`, `int num_live_perms`
Total size: 9 bytes

Calls a predicate. Saves the continuation point.

```
current_cp := pc_after_this_call
pc := target
```

`num_live_perms` is informational (for environment trimming, a future optimization). Currently unused.

### execute (0x53)

Operands: `address target`
Total size: 5 bytes

Last-call optimization: calls a predicate without saving the continuation point (the current continuation is inherited).

```
pc := target
// CP is NOT updated
```

The compiler emits `execute` instead of `call` for the last goal in a clause body.

### proceed (0x54)

Operands: (none)
Total size: 1 byte

Returns from the current predicate.

```
pc := current_cp
```

### halt (0x55)

Operands: (none)
Total size: 1 byte

Halts the engine. Used at the top level when a query completes.

---

## Choice point instructions

### try_me_else (0x60)

Operands: `address next_clause`, `int arity`
Total size: 9 bytes

Creates a choice point pointing to the next alternative clause, then proceeds to the first clause.

```
new_b := stack_top
// Save arity and arguments
stack[new_b + 0] := Cell(arity)
for i in 0..arity-1:
    stack[new_b + 1 + i] := X[i]
int offset = new_b + 1 + arity
// Save state
stack[offset + 0] := CE
stack[offset + 1] := CP
stack[offset + 2] := current_b
stack[offset + 3] := next_clause
stack[offset + 4] := binding_trail_top
stack[offset + 5] := extra_trail_top
stack[offset + 6] := heap_top
stack[offset + 7] := hb
stack_top := offset + 8
current_b := new_b
hb := heap_top
```

See ADR-005 for CP layout.

### retry_me_else (0x61)

Operands: `address next_clause`
Total size: 5 bytes

On backtrack, this instruction restores the engine state from the current CP and updates the CP's next-clause pointer.

```
restore_state_from_b(current_b)
int arity = stack[current_b].Data
int offset = current_b + 1 + arity
stack[offset + 3] := next_clause  // update BP for next backtrack
```

### trust_me (0x62)

Operands: (none)
Total size: 1 byte

On backtrack, this instruction restores the engine state and discards the current CP (no more alternatives).

```
restore_state_from_b(current_b)
current_b := stack[offset + 2].Data  // previous CP
stack_top := current_b  // (subject to E being lower)
```

### try (0x63)

Operands: `address clause_addr`
Total size: 5 bytes

Used in indexed dispatch. Creates a CP pointing to the next alternative (the instruction following this `try`), and jumps to `clause_addr`.

Similar to `try_me_else` but used when several alternatives are listed.

### retry (0x64)

Operands: `address clause_addr`
Total size: 5 bytes

Used in indexed dispatch. On backtrack, restores state from CP and jumps to `clause_addr`. The CP is retained for further alternatives.

### trust (0x65)

Operands: `address clause_addr`
Total size: 5 bytes

Used in indexed dispatch. On backtrack, restores state, discards the CP, and jumps to `clause_addr` (the last alternative).

### enter_dynamic (0x66) — ADR-015 chunk C

Operands: none
Total size: 1 byte

Emitted at the entry of every dynamic predicate. Samples the host's
`DbGeneration` into `engine.CurrentViewGen`. The surrounding
`try_me_else` captures that into the choice point so every clause's
`check_visible` reads the call's stable view-generation throughout the
chain's enumeration — the ISO logical update view.

```
engine.CurrentViewGen := host.DbGeneration
```

### check_visible (0x67) — ADR-015 chunk C

Operands: `long_value born`, `long_value died`
Total size: 17 bytes

Per-clause visibility filter for dynamic predicates. Fails (triggers
backtrack) if the calling goal's captured view-generation is outside
`[born, died)` — i.e., the clause did not yet exist when the goal began
(`born > G`) or had been retracted before it began (`died ≤ G`).
`retract` patches `died` in place; everything else stays immutable.

```
g := engine.CurrentViewGen
if born > g || died <= g:
    backtrack
else:
    pc += 17
```

The two operands are 64-bit signed integers — the only opcode in v1
with `LongValue` operands. The generation counter (`assertz` / `retract`
bump count) needs more than 32 bits for a long-running engine.

---

## Indexing instructions

### switch_on_term (0x70)

Operands: `address var_label`, `address const_label`, `address list_label`, `address struct_label`
Total size: 17 bytes

Dispatches based on the type of A1 (the first argument register).

```
c := deref(X[0])
switch (c.Tag):
    case REF: goto var_label
    case ATOM, INT, FLOAT, BIGINT, STRING: goto const_label
    case LIS: goto list_label
    case STR: goto struct_label
    default (FOREIGN, PSTR, etc.): goto var_label
```

### switch_on_atom (0x71)

Operands: `int table_id`
Total size: 5 bytes

Looks up A1's atom id in `CodeArea.SwitchTables[table_id]` and jumps to the matching address or to the default.

### switch_on_integer (0x72)

Operands: `int table_id`
Total size: 5 bytes

Looks up A1's integer value in the switch table.

### switch_on_structure (0x73)

Operands: `int table_id`
Total size: 5 bytes

Looks up A1's structure's functor id in the switch table.

---

## Cut instructions

### neck_cut (0x80)

Operands: (none)
Total size: 1 byte

A cut placed immediately after the head match (no body goals before `!`). Discards all CPs created since the predicate was entered.

```
current_b := saved_b_at_predicate_entry
compact_trails()
```

The "saved b" is implicit (it's the value of `current_b` at the moment of clause entry, which the compiler tracks).

### get_level (0x81)

Operands: `perm dest`
Total size: 5 bytes

Saves the current `current_b` to a permanent variable, for use by a deep cut.

```
Y[dest] := current_b
```

### cut (0x82)

Operands: `perm src`
Total size: 5 bytes

A deep cut: discards CPs up to the level saved in `Y[src]`.

```
target := Y[src].Data
if (current_b > target):
    current_b := target
    compact_trails()
```

---

## Builtin call instructions

### call_builtin (0x90)

Operands: `int builtin_id`, `int arity`
Total size: 9 bytes

Invokes a registered builtin predicate. Arguments are in `X[0..arity-1]`.

```
result := _builtins[builtin_id](this)
if not result: fail
```

### Specialized builtin opcodes (v1)

For the most frequent builtins, dedicated opcodes avoid the `call_builtin` dispatch overhead.

| Opcode | Mnemonic | Operands | Description |
|--------|----------|----------|-------------|
| 0x91   | unify_eq | (none) | Equivalent to `=/2`. Unifies X[0] and X[1]. |
| 0x92   | is_op | (none) | Evaluates X[1] arithmetically and unifies with X[0]. |
| 0x93   | less_than | (none) | `<`: arithmetic less-than. |
| 0x94   | greater_than | (none) | `>`: arithmetic greater-than. |
| 0x95   | less_eq | (none) | `=<`: arithmetic less-or-equal. |
| 0x96   | greater_eq | (none) | `>=`: arithmetic greater-or-equal. |
| 0x97   | arith_eq | (none) | `=:=`: arithmetic equality. |
| 0x98   | arith_not_eq | (none) | `=\=`: arithmetic inequality. |
| 0x99   | struct_eq | (none) | `==`: structural equality (no unification). |
| 0x9A   | struct_not_eq | (none) | `\==`: structural inequality. |

---

## Consolidated patterns (v1)

The most frequent patterns get dedicated opcodes for performance.

### get_constant_a1 (0xA0)

Operands: `atom_id const`
Total size: 5 bytes

Equivalent to `get_constant const, X[0]`.

### get_constant_a2 (0xA1)

Operands: `atom_id const`
Total size: 5 bytes

Equivalent to `get_constant const, X[1]`.

### put_constant_a1 (0xA2)

Operands: `atom_id const`
Total size: 5 bytes

Equivalent to `put_constant const, X[0]`.

### put_constant_a2 (0xA3)

Operands: `atom_id const`
Total size: 5 bytes

### get_list_a1 (0xA4)

Operands: (none)
Total size: 1 byte

Equivalent to `get_list X[0]`.

### get_list_a2 (0xA5)

Operands: (none)
Total size: 1 byte

(Additional consolidations are added based on frequency analysis during implementation.)

---

## PSTR instructions

These are specific to partial-string handling for grammar processing.

### get_pstr (0xC0)

Operands: `int pstr_literal_id`, `reg arg`
Total size: 9 bytes

Unifies the argument with a PSTR literal. The literal is in `CodeArea.StringLiterals[pstr_literal_id]`.

### put_pstr (0xC1)

Operands: `int pstr_literal_id`, `reg arg`
Total size: 9 bytes

Sets the argument register to a PSTR literal.

### unify_pstr_head (0xC2)

Operands: `reg head_dest`
Total size: 5 bytes

Decomposes a PSTR in the unify cursor: gets the first character (as an atom or code, per the `double_quotes` flag), stores it in `head_dest`, and advances the cursor to the rest of the PSTR.

Used for PSTR-to-list pattern matching like `[H|T] = Pstr`.

(More PSTR instructions are defined in `pstr-design.md`.)

---

## Meta opcode

### meta (0xFE)

Operands: `byte sub_opcode`, then sub-specific operands
Total size: variable

The Meta opcode is used for non-execution information embedded in the bytecode.

#### Sub-opcode 0x00: dbg_info

Operands: `byte sub_opcode = 0x00`, `int entry_id`
Total size: 6 bytes

References a debug info entry in `CodeArea.DebugEntries[entry_id]`. At runtime:

- If a debugger is attached: the debugger is notified with the entry's metadata.
- Otherwise: no-op (the interpreter skips it).

The IL compiler may treat `dbg_info` instructions as no-ops or as boundaries (in Full debug level).

---

## Halt and reserved opcodes

| Opcode | Mnemonic | Note |
|--------|----------|------|
| 0x00 | `reserved_invalid` | Reserved; trapping if encountered. |
| 0xFE | `meta` | Meta opcode with sub-byte. |
| 0xFF | `reserved_extension` | Reserved for future escape mechanism. |

---

## Encoding of operands

All multi-byte operands are little-endian. The compiler emits operands using `Unsafe.WriteUnaligned<int>` and the interpreter reads them with `Unsafe.ReadUnaligned<int>`.

The cross-platform .NET runtime guarantees little-endian for `Unsafe.WriteUnaligned<int>` and `Unsafe.ReadUnaligned<int>` regardless of the CPU's native endianness. This makes bytecode portable across x86, x64, ARM, etc.

---

## Instruction sizing table

The interpreter uses a precomputed table to advance the program counter after each instruction:

```csharp
public static class OpcodeInfo
{
    public struct Info
    {
        public byte Size;
        public byte NumOperands;
        public string Mnemonic;
    }
    
    public static readonly Info[] Table = new Info[256];
    
    static OpcodeInfo()
    {
        Table[0x00] = new Info { Size = 1, NumOperands = 0, Mnemonic = "reserved_invalid" };
        Table[0x01] = new Info { Size = 9, NumOperands = 2, Mnemonic = "get_variable_x" };
        Table[0x02] = new Info { Size = 9, NumOperands = 2, Mnemonic = "get_variable_y" };
        // ... full table
        Table[0xFE] = new Info { Size = 6, NumOperands = 1, Mnemonic = "meta" };  // assumes dbg_info sub
        Table[0xFF] = new Info { Size = 1, NumOperands = 0, Mnemonic = "reserved_extension" };
    }
}
```

The actual size of `meta` instructions depends on the sub-opcode; the table above is a default. The interpreter must handle this case specifically.

---

## Compilation patterns

The compiler emits these instructions to translate Prolog. Examples:

### Simple fact

```prolog
parent(tom, bob).
```

Compiles to:

```
get_atom_a1   <atom_id of 'tom'>
get_atom_a2   <atom_id of 'bob'>
proceed
```

### Recursive predicate

```prolog
length([], 0).
length([_|T], N) :- length(T, N1), N is N1 + 1.
```

Compiles to (approximate):

```
length/2:
  try_me_else <c2_addr>, 2
c1:
  get_nil X[0]                      ; A1 must be []
  get_integer 0, X[1]               ; A2 must be 0
  proceed
c2:
  trust_me
  allocate 1                         ; Y1 = N1
  get_list X[0]                      ; A1 must be cons
  unify_void 1                       ; skip H (anonymous _)
  unify_variable_x X[2]              ; T -> X[2]
  put_value_x X[2], X[0]             ; A1 = T
  put_variable_y Y[0], X[1]          ; Y[0] = N1, A2 = N1
  call length/2, 1                   ; call recursively
  put_value_y Y[0], X[0]             ; A1 = N1
  put_integer 1, X[1]                ; A2 = 1
  is_op                              ; X[0] = X[0] + X[1]; bound to original N
  deallocate
  proceed
```

### Cut

```prolog
foo(X) :- bar(X), !, baz(X).
```

Compiles to:

```
allocate 1
get_variable_y Y[0], X[0]            ; Y[0] = X (X is permanent since used after cut)
get_level Y[1]                       ; not really needed; use neck_cut for performance
put_value_y Y[0], X[0]
call bar/1, 2
neck_cut                              ; assuming compiler emits neck_cut for this position
put_value_y Y[0], X[0]
deallocate
execute baz/1
```

---

## Stability and versioning

The opcode assignments above are stable for v1. Future versions may add new opcodes in the reserved ranges. Removing or changing an opcode requires bumping the bytecode format version (see ADR-009).

---

## See also

- ADR-006 (Bytecode Encoding): high-level rationale.
- ADR-005 (Stack Layout): how control instructions interact with the stack.
- ADR-007 (Indexing): how indexing instructions work.
- `cell-layout-detail.md`: how instructions read/write cells.
- `pstr-design.md`: detailed PSTR instruction semantics.
- `debug-info.md`: details of the meta/dbg_info opcode.
