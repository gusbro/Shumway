# IL Emission Patterns

This document specifies how the Shumway IL compiler translates WAM bytecode instructions to .NET CIL. It complements ADR-011 by providing concrete IL patterns for each opcode.

The patterns assume the compiled method signature:

```csharp
public static bool CompiledPred(Engine engine, int argBase)
```

Where:
- `engine`: the engine instance (argument 0).
- `argBase`: the offset in the engine's register array where arguments are stored (argument 1).
- Return: `true` on success, `false` on failure (backtracks to nearest CP).

## Common helpers

The compiled code calls back into engine methods for complex operations. These methods are exposed via reflection-cached `MethodInfo` references.

### Cached method handles

```csharp
internal static class EngineMethods
{
    public static readonly MethodInfo Deref;
    public static readonly MethodInfo Unify;
    public static readonly MethodInfo Bind;
    public static readonly MethodInfo AllocateHeap;
    public static readonly MethodInfo GetCell;
    public static readonly MethodInfo SetCell;
    public static readonly MethodInfo TrailBinding;
    public static readonly MethodInfo PushChoicePoint;
    public static readonly MethodInfo PopChoicePoint;
    public static readonly MethodInfo Backtrack;
    public static readonly MethodInfo CallPredicate;
    public static readonly MethodInfo CallBuiltin;
    // ... etc.
    
    static EngineMethods()
    {
        var t = typeof(Engine);
        Deref = t.GetMethod(nameof(Engine.Deref))!;
        Unify = t.GetMethod(nameof(Engine.Unify))!;
        // ... etc.
    }
}
```

### Local variables in IL

Each compiled method declares locals for:

- `Cell[] _heap`: cached reference to engine.Heap.
- `Cell[] _registers`: cached reference to engine.Registers.
- Temporary cells, indices, etc.

These are loaded once at method entry:

```il
.locals init (
    [0] class Cell[] heap,
    [1] class Cell[] regs,
    [2] int32 tmp1,
    [3] valuetype Cell tmpCell
)

ldarg.0       // engine
ldfld Engine::_heap
stloc.0       // heap

ldarg.0       // engine
ldfld Engine::_registers
stloc.1       // regs
```

## Get instructions

### get_variable_x (X[dest] := X[arg])

```il
// regs[argBase + dest] = regs[argBase + arg]
ldloc.1                  // regs
ldarg.1                  // argBase
ldc.i4 <dest>
add
ldloc.1                  // regs
ldarg.1                  // argBase
ldc.i4 <arg>
add
ldelem Cell
stelem Cell
```

For small register indices (the common case), constant folding may apply. The C# JIT optimizes this further.

### get_variable_y (Y[dest] := X[arg])

```il
// engine.Stack[engine._e + 2 + dest] = regs[argBase + arg]
ldarg.0                  // engine
ldfld Engine::_stack
ldarg.0
ldfld Engine::_e
ldc.i4 2
add
ldc.i4 <dest>
add

ldloc.1                  // regs
ldarg.1
ldc.i4 <arg>
add
ldelem Cell

stelem Cell
```

### get_value_x (unify X[src] with X[arg])

```il
// if (!engine.Unify(regs[argBase + src], regs[argBase + arg])) return false;

ldarg.0                  // engine
ldloc.1                  // regs
ldarg.1
ldc.i4 <src>
add
ldelem Cell
ldloc.1
ldarg.1
ldc.i4 <arg>
add
ldelem Cell
call instance Engine::Unify

brfalse FailLabel
```

`FailLabel` is the standard failure label that returns `false`.

### get_constant (unify X[arg] with Atom(const))

```il
// Cell argCell = engine.Deref(regs[argBase + arg]);
// if (argCell.Tag == Tag.Ref) { engine.Bind(argCell.heap_idx, Cell.Atom(const)); }
// else if (argCell.Tag == Tag.Atom && argCell.atom_id == const) { /* match */ }
// else return false;

ldarg.0
ldloc.1
ldarg.1
ldc.i4 <arg>
add
ldelem Cell
call instance Engine::Deref       // returns dereferenced Cell

stloc.3                            // tmpCell

ldloc.3
call instance Cell::get_Tag        // get tag
ldc.i4 0                           // Tag.Ref
beq.s BindCase

ldloc.3
call instance Cell::get_Tag
ldc.i4 4                           // Tag.Atom
bne.un FailLabel

ldloc.3
call instance Cell::get_AsAtomId
ldc.i4 <const>
bne.un FailLabel

br.s NextInstruction

BindCase:
ldarg.0
ldloc.3
call instance Cell::get_AsHeapIndex
ldc.i4 <const>
call Cell::Atom                    // static factory
call instance Engine::Bind

NextInstruction:
```

The inline form above is for `Level 2 inlining` (the top opcodes are inlined into IL directly). For other opcodes, the IL emitter just calls back to a single helper method on the engine.

### get_structure (unify X[arg] with structure of given functor)

```il
// Cell argCell = engine.Deref(regs[argBase + arg]);
// if (argCell.Tag == Tag.Ref) {
//     // Write mode: allocate STR+FUNCTOR
//     int heapTop = engine.HeapTop;
//     engine.AllocateHeap(2);
//     engine.SetHeap(heapTop, Cell.Str(heapTop + 1));
//     engine.SetHeap(heapTop + 1, Cell.Functor(functorId));
//     engine.Bind(argCell.AsHeapIndex, Cell.Str(heapTop));
//     engine._writeMode = true;
//     engine._unifyPointer = heapTop + 2;
// } else if (argCell.Tag == Tag.Str) {
//     int strIdx = argCell.AsHeapIndex;
//     Cell functorCell = engine.GetHeap(strIdx);
//     if (functorCell.AsFunctorId != functorId) return false;
//     engine._writeMode = false;
//     engine._unifyPointer = strIdx + 1;
// } else return false;
```

In IL, this is verbose. The pattern is delegated to a helper method:

```il
ldarg.0
ldloc.1
ldarg.1
ldc.i4 <arg>
add
ldelem Cell
ldc.i4 <functorId>
call instance Engine::TryGetStructure  // helper, returns bool

brfalse FailLabel
```

`TryGetStructure` handles both modes and updates `engine._writeMode` and `engine._unifyPointer`.

## Put instructions

### put_constant (X[arg] := Atom(const))

```il
// regs[argBase + arg] = Cell.Atom(const);

ldloc.1                  // regs
ldarg.1
ldc.i4 <arg>
add
ldc.i4 <const>
call Cell::Atom          // static factory
stelem Cell
```

### put_variable_x (new heap var, store in two registers)

```il
// int idx = engine.AllocateHeapUnboundVar();
// regs[argBase + dest] = Cell.Ref(idx);
// regs[argBase + arg] = Cell.Ref(idx);

ldarg.0
call instance Engine::AllocateHeapUnboundVar  // returns int
dup
stloc.2                  // tmp1

ldloc.1
ldarg.1
ldc.i4 <dest>
add
ldloc.2
call Cell::Ref
stelem Cell

ldloc.1
ldarg.1
ldc.i4 <arg>
add
ldloc.2
call Cell::Ref
stelem Cell
```

### put_structure (begin building a structure)

```il
// int heapTop = engine.HeapTop;
// engine.AllocateHeap(2);
// engine.SetHeap(heapTop, Cell.Str(heapTop + 1));
// engine.SetHeap(heapTop + 1, Cell.Functor(functorId));
// regs[argBase + arg] = Cell.Str(heapTop);
// engine._writeMode = true;
// engine._unifyPointer = heapTop + 2;

// Delegated to helper
ldarg.0
ldc.i4 <functorId>
ldloc.1                  // regs
ldarg.1
ldc.i4 <arg>
add
call instance Engine::PutStructure  // helper
```

## Unify instructions (mode-sensitive)

These have different behavior in read mode vs write mode. The IL emits both paths and dispatches on `engine._writeMode`.

### unify_variable_x

```il
// if (engine._writeMode) {
//     int idx = engine.AllocateHeapUnboundVar();
//     regs[argBase + target] = engine.GetHeap(idx);  // unbound REF
// } else {
//     regs[argBase + target] = engine.GetHeap(engine._unifyPointer);
//     engine._unifyPointer++;
// }

ldarg.0
ldfld Engine::_writeMode
brtrue WriteMode

// Read mode
ldloc.1
ldarg.1
ldc.i4 <target>
add
ldloc.0                  // heap
ldarg.0
ldfld Engine::_unifyPointer
dup
ldc.i4 1
add
ldarg.0
ldarg.0
ldfld Engine::_unifyPointer
ldc.i4 1
add
stfld Engine::_unifyPointer   // increment
ldelem Cell
stelem Cell

br.s Done

WriteMode:
ldarg.0
call instance Engine::AllocateHeapUnboundVar  // returns int
stloc.2

ldloc.1
ldarg.1
ldc.i4 <target>
add
ldloc.0                  // heap
ldloc.2
ldelem Cell
stelem Cell

Done:
```

The above is verbose. Helper methods can reduce this:

```il
ldarg.0
ldc.i4 <target>
ldarg.1
call instance Engine::UnifyVariableX  // helper handles mode dispatch
```

## Control instructions

### allocate (allocate stack frame with N permanents)

```il
// engine.Allocate(N);

ldarg.0
ldc.i4 <N>
callvirt instance Engine::Allocate
```

### deallocate

```il
// engine.Deallocate();

ldarg.0
callvirt instance Engine::Deallocate
```

### call (call predicate at address)

The compiler resolves the target predicate to a delegate at compile time (for static predicates) or uses inline caching (for dynamic / unresolved):

```il
// Static call (resolved):
// engine.PrepareCall(<argBase + num_args>, <new_argBase>);
// if (!CompiledTarget(engine, new_argBase)) return false;

ldarg.0
ldc.i4 <num_args>
call instance Engine::PrepareCall
ldarg.0
ldarg.1
ldc.i4 <num_args>
add
call <CompiledTarget>
brfalse FailLabel
```

For unresolved/dynamic calls, the inline caching pattern (see ADR-011) is used:

```il
// Inline-cached call:
ldsfld _callSite_42                            // CallSiteCache struct
ldfld CallSiteCache::CachedDelegate
brfalse SlowPath

ldsfld _callSite_42
ldfld CallSiteCache::CachedVersion
ldarg.0
ldfld Engine::_predicateTableVersion
bne.un SlowPath

// Fast path: invoke cached delegate
ldsfld _callSite_42
ldfld CallSiteCache::CachedDelegate
ldarg.0
ldarg.1
ldc.i4 <num_args>
add
callvirt PredicateDelegate::Invoke
brfalse FailLabel
br.s Done

SlowPath:
ldarg.0
ldc.i4 <functor_id>
ldarg.1
ldc.i4 <num_args>
add
call instance Engine::CallAndCacheCallSite_42
brfalse FailLabel

Done:
```

### execute (last call, no save CP)

Same as call, but with no return address management:

```il
// For inlined execute (LCO):
// return CompiledTarget(engine, new_argBase);

ldarg.0
ldarg.1
ldc.i4 <num_args>
add
call <CompiledTarget>
ret    // tail call (with .tail prefix if supported)
```

The `.tail` prefix lets the JIT optimize this to a true tail call, avoiding stack growth in recursive predicates.

### proceed

```il
// Return success.
ldc.i4.1
ret
```

## Choice point instructions

### try_me_else

```il
// engine.PushChoicePoint(<arity>, <next_clause_addr>);
// (then continue with current clause body)

ldarg.0
ldc.i4 <arity>
ldc.i4 <next_clause_addr>  // bytecode offset; for IL, a code label
call instance Engine::PushChoicePoint
```

For IL-compiled predicates, the "next clause address" is encoded as a delegate reference:

```il
ldarg.0
ldc.i4 <arity>
ldftn <NextClauseMethod>      // pointer to method
newobj PredicateDelegate::.ctor(object, native int)
call instance Engine::PushChoicePointIl   // overload for IL
```

### retry_me_else

This instruction is only reached on backtracking. In IL, retry/trust instructions are entry points to alternative bodies, not directly executed.

The pattern: the compiled method has multiple entry points (labels), and on backtrack, the engine invokes the method with a hint (via a re-entry parameter) that tells it which label to jump to.

Alternative: each clause is a separate compiled method, and the choice point holds a pointer to the next method.

The latter is simpler. Adopted as the v1 strategy:

```il
// Clause 1 method:
public static bool Clause1(Engine engine, int argBase)
{
    // Body of clause 1
    // ... ends with ret
}

// Clause 2 method:
public static bool Clause2(Engine engine, int argBase)
{
    // Body of clause 2
}

// Predicate dispatcher:
public static bool MyPred(Engine engine, int argBase)
{
    // Optional: indexing dispatch
    engine.PushChoicePoint(arity, Clause2);
    return Clause1(engine, argBase);
}
```

When `Clause1` fails, the engine pops back to the CP, invokes `Clause2`, and so on.

### trust_me

For the last clause (no more alternatives), no CP is created:

```il
// Just execute the body; failure returns false directly.
```

## Cut instructions

### neck_cut

```il
// engine.Cut(savedB);
// where savedB is the value of _b at predicate entry.

ldarg.0
ldarg.0
ldfld Engine::_savedB      // engine pushed this on entry
call instance Engine::Cut
```

The "saved B" can be captured at the start of the predicate:

```il
// Method prologue:
ldarg.0
ldfld Engine::_b
stloc.4         // savedB

// At neck_cut:
ldarg.0
ldloc.4
call instance Engine::Cut
```

### get_level Y[dest]

```il
// engine.Stack[engine._e + 2 + dest] = new Cell(engine._b);

ldarg.0
ldfld Engine::_stack
ldarg.0
ldfld Engine::_e
ldc.i4 2
add
ldc.i4 <dest>
add
ldarg.0
ldfld Engine::_b
conv.i8
newobj Cell::.ctor(int64)
stelem Cell
```

### cut Y[src]

```il
// int target = (int)engine.Stack[engine._e + 2 + src].Data;
// engine.Cut(target);

ldarg.0
ldarg.0
ldfld Engine::_stack
ldarg.0
ldfld Engine::_e
ldc.i4 2
add
ldc.i4 <src>
add
ldelem Cell
ldfld Cell::Data
conv.i4
call instance Engine::Cut
```

## Indexing instructions

### switch_on_term

```il
// Cell c = engine.Deref(regs[argBase + 0]);
// switch (c.Tag) {
//     case Tag.Ref: goto VarLabel;
//     case Tag.Atom: case Tag.Int: ...: goto ConstLabel;
//     case Tag.Lis: goto ListLabel;
//     case Tag.Str: goto StructLabel;
//     default: goto VarLabel;
// }

ldarg.0
ldloc.1
ldarg.1
ldelem Cell
call instance Engine::Deref
stloc.3

ldloc.3
call instance Cell::get_Tag
switch (
    VarLabel,    // 0: Ref
    StructLabel, // 1: Str
    ListLabel,   // 2: Lis
    VarLabel,    // 3: Functor (shouldn't happen in well-formed code)
    ConstLabel,  // 4: Atom
    ConstLabel,  // 5: Int
    ConstLabel,  // 6: Float
    ConstLabel,  // 7: BigInt
    ConstLabel,  // 8: String
    VarLabel,    // 9: Foreign
    VarLabel,    // 10: AttVar
    VarLabel     // 11: Pstr
)
br VarLabel  // default
```

CIL `switch` instruction handles dispatch in O(1).

### switch_on_atom

```il
// Cell c = engine.Deref(regs[argBase + 0]);
// int atomId = c.AsAtomId;
// int target = SwitchTable[<id>].Lookup(atomId);
// goto BytecodeAddress(target);

// In IL, the table is precomputed as a Dictionary<int, MethodInfo> or a sorted array.
// The IL calls a helper:

ldarg.0
ldc.i4 <switch_table_id>
ldarg.0
ldloc.1
ldarg.1
ldelem Cell
call instance Engine::Deref
call instance Cell::get_AsAtomId
call instance Engine::LookupSwitchAtom   // returns a delegate
ldarg.0
ldarg.1
callvirt PredicateDelegate::Invoke
```

## Builtin opcodes

### is_op (is/2)

```il
// X[0] := evaluate(X[1])

ldarg.0
ldloc.1
ldarg.1
ldc.i4 1
add
ldelem Cell
call instance Engine::ArithEvaluate   // returns Cell

stloc.3

ldarg.0
ldloc.1
ldarg.1
ldelem Cell
ldloc.3
call instance Engine::Unify

brfalse FailLabel
```

For simple cases (X[1] is a single integer constant), the IL emitter can inline the arithmetic:

```il
// X[0] := Cell.Int(<const_value>)

ldloc.1
ldarg.1
ldelem Cell
ldc.i4 <const_value>
conv.i8
call Cell::Int
... unify ...
```

### less_than (</2 with integers)

For arithmetic comparisons with integer operands, the IL can inline the comparison:

```il
// Cell a = engine.Deref(regs[argBase + 0]);
// Cell b = engine.Deref(regs[argBase + 1]);
// long aVal = a.AsInt;
// long bVal = b.AsInt;
// return aVal < bVal;

ldarg.0
ldloc.1
ldarg.1
ldelem Cell
call instance Engine::Deref
call instance Cell::get_AsInt

ldarg.0
ldloc.1
ldarg.1
ldc.i4 1
add
ldelem Cell
call instance Engine::Deref
call instance Cell::get_AsInt

blt FailLabel   // if NOT less, go to fail
// continue successfully
```

(Note: the comparison direction matters; `blt` jumps to fail if NOT less; we want the inverse.)

For floats or BigIntegers, the IL calls a helper. The JIT's type specialization handles the int case efficiently.

## PSTR instructions

PSTR-specific opcodes are delegated to engine helpers, since the logic is complex:

```il
// get_pstr <literal_id>, <arg>
ldarg.0
ldc.i4 <literal_id>
ldc.i4 <arg>
ldarg.1
call instance Engine::GetPstr
brfalse FailLabel
```

## Meta opcodes (dbg_info)

```il
// dbg_info <entry_id>
// if (engine._debugger != null) engine._debugger.OnDebugPoint(engine, <entry_id>);

ldarg.0
ldfld Engine::_debugger
brfalse SkipDbg
ldarg.0
ldfld Engine::_debugger
ldarg.0
ldc.i4 <entry_id>
callvirt IDebugger::OnDebugPoint

SkipDbg:
```

For PersistedAssemblyBuilder (build-time IL), this also emits a sequence point for the .NET debugger.

## Failure handling

The label `FailLabel` is the standard failure exit:

```il
FailLabel:
ldc.i4.0   // false
ret
```

For predicates with multiple clauses, FailLabel can instead branch to the next clause's body if appropriate (instead of returning false). The compiler chooses based on the control flow.

## Optimization opportunities

The Phase 1 IL emitter performs basic optimizations:

1. **Constant folding**: when both operands of an operation are constants, fold at compile time.
2. **Dead code elimination**: instructions whose result is never used are dropped.
3. **Branch simplification**: empty blocks are removed; redundant branches collapsed.
4. **Inline small helpers**: very small engine methods can be inlined (the JIT does this anyway).

Phase 2 adds:

5. **Type specialization**: when bytecode shows a register always holds an INT in some path, generate int-specific code.
6. **Inline caching for indirect calls**: documented above.
7. **Escape analysis**: terms that don't escape can stay in stack-allocated buffers.

Phase 3 adds:

8. **Mode-aware compilation**: for predicates with `:- mode` declarations.

## Verifying generated IL

Sigil validates IL during emission. For PersistedAssemblyBuilder code, a verification pass after generation uses `PEVerify` or similar tools.

In CI, generated assemblies are validated to catch regressions.

## See also

- ADR-011 (IL Compiler Architecture): high-level strategy.
- ADR-006 (Bytecode Encoding): the source bytecode format.
- `inline-caching.md`: detailed inline caching mechanism.
- Sigil documentation: https://github.com/kevin-montrose/Sigil
