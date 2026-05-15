# Debug Info Design

This document specifies the design of Shumway's debug information system: the data structures, the encoding in bytecode, the runtime behavior, and the integration points with debugging tools.

## Goals

The debug info system serves four use cases:

1. **Source-level error reporting**: when an exception or warning occurs, report the source file, line, and column.
2. **Tracing**: log each step of execution, mapping back to source locations.
3. **Interactive debugging**: a debugger can set breakpoints, step through execution, inspect variables.
4. **Profiling**: associate runtime samples with source locations.

These use cases have different cost tolerances. Source-level error reporting is cheap and always-on. Tracing is moderately expensive. Interactive debugging is heaviest.

The design uses **three configurable levels** to balance information richness against runtime cost.

## Levels

### None

- No debug info embedded in bytecode.
- No debug entries in `CodeArea.DebugEntries`.
- Exceptions report only the predicate functor (no line/column).
- Smallest bytecode, fastest interpretation.

Used in production when source-level diagnostics are not needed.

### Basic (default)

- A side table `CodeArea.DebugEntries` maps bytecode addresses (PCs) to source locations.
- No `dbg_info` opcodes embedded in bytecode (so no per-instruction overhead in the interpreter).
- The mapping is at **clause granularity**: each clause has an entry pointing to its source position.
- Optionally: each goal within a clause has an entry. This is granular enough for stack traces but doesn't slow the interpreter.

Used by default. Enables stack traces and error messages with source positions. The IL compiler may use these entries to emit sequence points in the IL (for .NET debugger integration).

### Full

- The Basic mapping plus inlined `dbg_info` opcodes (`0xFE 0x00`) at every meaningful source position (before each goal, at variable assignments, at choice point creation, etc.).
- The interpreter notifies an attached debugger at each `dbg_info`.
- Enables fine-grained stepping, breakpoint at any source line, variable inspection.

Used during interactive debugging. Adds noticeable overhead to interpretation (the `dbg_info` opcode is fast but still adds instruction count).

## Data structures

### DebugEntry

```csharp
public class DebugEntry
{
    public int PcStart;            // Inclusive: PC where this entry applies from
    public int PcEnd;              // Exclusive: PC where this entry no longer applies
    public int SourceFileId;       // Index into CodeArea.SourceFiles
    public int Line;               // 1-based
    public int Column;             // 1-based
    public DebugEntryKind Kind;
    public string? Annotation;     // Optional text (e.g., "clause start", "before call to foo/2")
    public DebugVariableInfo[]? Variables;  // Live variables at this point, if Full level
}

public enum DebugEntryKind
{
    ClauseStart,           // Entry to a clause body
    GoalStart,             // Before a goal in the body
    GoalEnd,               // After a goal
    CutPoint,              // At a !
    ChoicePointCreate,     // Where a try_me_else is emitted
    LiteralUnification,    // get_constant, get_atom, etc.
    Custom,                // Annotation provided by tooling
}

public struct DebugVariableInfo
{
    public string Name;             // Source-level variable name
    public bool IsPermanent;        // True if it's a Y variable
    public int Index;               // X[i] or Y[i] index
    public TypeHint? TypeHint;      // Optional mode hint
}
```

### DebugEntries in CodeArea

```csharp
public class CodeArea
{
    // ... other fields
    
    public List<DebugEntry> DebugEntries;
    public List<string> SourceFiles;       // file paths
    
    // Sorted index for fast PC → entry lookup (used at Basic level)
    public int[] DebugEntryIndexByPc;
}
```

The list is **sorted by `PcStart`** to enable binary search.

## Lookup: PC → debug entry

```csharp
public DebugEntry? GetDebugEntry(int pc)
{
    var list = _codeArea.DebugEntries;
    if (list.Count == 0) return null;
    
    int lo = 0, hi = list.Count - 1;
    while (lo <= hi)
    {
        int mid = (lo + hi) / 2;
        var entry = list[mid];
        if (pc < entry.PcStart) hi = mid - 1;
        else if (pc >= entry.PcEnd) lo = mid + 1;
        else return entry;
    }
    return null;
}
```

This is used by exception machinery and stack trace generation.

## The dbg_info meta opcode

When debug level is Full, the compiler emits `dbg_info` opcodes at relevant points:

```
Bytecode pattern:
  0xFE (Meta)
  0x00 (sub-opcode: DbgInfo)
  int32: index into CodeArea.DebugEntries
```

Total size: 6 bytes.

### Interpreter handling

The interpreter has a `_debugger` field of type `IDebugger?`. The dispatch for `dbg_info`:

```csharp
case 0xFE:  // Meta opcode
{
    byte sub = _code.Bytes[_p + 1];
    switch (sub)
    {
        case 0x00:  // DbgInfo
            int entryId = Unsafe.ReadUnaligned<int>(ref _code.Bytes[_p + 2]);
            if (_debugger != null)
                _debugger.OnDebugPoint(_engine, entryId);
            _p += 6;
            break;
        default:
            throw new InvalidOperationException($"Unknown meta sub-opcode: {sub:X2}");
    }
    break;
}
```

When no debugger is attached, the cost is one branch prediction (the `_debugger != null` check) and the PC advance. Modern CPUs handle this well; the branch predictor learns that the debugger is usually null.

### IDebugger interface

```csharp
public interface IDebugger
{
    void OnDebugPoint(Engine engine, int debugEntryId);
    void OnException(Engine engine, PrologException ex, int pc);
    void OnPredicateEnter(Engine engine, FunctorId functor);
    void OnPredicateExit(Engine engine, FunctorId functor, bool succeeded);
}

public partial class Engine
{
    public void AttachDebugger(IDebugger debugger);
    public void DetachDebugger();
}
```

A debugger receives notifications and can:

- Pause execution (block in the callback until a signal).
- Inspect engine state (heap, stack, registers, trails).
- Set breakpoints (by recording PC ranges or predicate names).
- Step instruction by instruction.

The implementation of a specific debugger (interactive console, IDE integration, etc.) is outside this document's scope.

## Variable name tracking

For each clause, the compiler records the mapping from source variable names (`X`, `Y`, `Result`) to register slots (`X[i]`, `Y[i]`). This mapping is part of `DebugEntry.Variables`.

```csharp
// Source: foo(A, B) :- bar(A, X), baz(X, B).
// 
// Compiled (simplified):
//   allocate 1
//   get_variable_x X[2], X[0]        ; A → X[2]
//   get_variable_y Y[0], X[1]        ; B → Y[0]
//   put_value_x X[2], X[0]            ; X[0] = A
//   put_variable_x X[3], X[1]         ; new var → X (source); X[1] = X (source)
//   call bar/2, 1
//   put_value_x X[3], X[0]            ; X[0] = X (source)
//   put_value_y Y[0], X[1]            ; X[1] = B
//   deallocate
//   execute baz/2
//
// DebugEntry for clause start:
//   Variables = [
//     { Name = "A", IsPermanent = false, Index = 2 },  // X[2]
//     { Name = "B", IsPermanent = true,  Index = 0 },  // Y[0]
//     { Name = "X", IsPermanent = false, Index = 3 },  // X[3]
//   ]
```

The debugger uses this to map names like "X" back to the actual register slot when the user requests inspection.

Variables that go out of scope (e.g., a temporary X register reused later in the clause) are handled by having multiple DebugEntries with different variable lists, valid for different PC ranges.

## Stack trace generation

When a Prolog exception propagates upward, the engine generates a stack trace from the active environments and CPs.

```csharp
public List<StackFrame> GenerateStackTrace()
{
    var frames = new List<StackFrame>();
    
    // Current frame
    var current = new StackFrame
    {
        Functor = _currentFunctor,
        Pc = _p,
        DebugEntry = GetDebugEntry(_p),
    };
    frames.Add(current);
    
    // Walk environments
    int e = _e;
    while (e >= 0)
    {
        int ceValue = (int)_stack[e + 0].Data;
        int cpValue = (int)_stack[e + 1].Data;
        
        if (cpValue == 0) break;
        
        var debugEntry = GetDebugEntry(cpValue);
        frames.Add(new StackFrame
        {
            Functor = debugEntry?.OwningFunctor,
            Pc = cpValue,
            DebugEntry = debugEntry,
        });
        
        e = ceValue;
    }
    
    return frames;
}

public class StackFrame
{
    public FunctorId Functor;
    public int Pc;
    public DebugEntry? DebugEntry;
    
    public string SourceFile => DebugEntry?.SourceFileId is int id 
        ? _codeArea.SourceFiles[id] : "(no debug info)";
    public int Line => DebugEntry?.Line ?? 0;
    public int Column => DebugEntry?.Column ?? 0;
}
```

The stack trace is presented in `PrologException.StackTrace`.

## Source line table

A separate, compact representation for the most common debug query (PC → line) lives alongside `DebugEntries`:

```csharp
public class CompactSourceLineTable
{
    public int[] PcStarts;
    public ushort[] Lines;
    public ushort[] FileIds;
    
    public (int file, int line) Lookup(int pc)
    {
        int idx = BinarySearchPcStart(pc);
        return (FileIds[idx], Lines[idx]);
    }
}
```

This is built from `DebugEntries` at module load time. It's compact (8 bytes per entry vs. dozens for full `DebugEntry`), cache-friendly, and fast.

For Basic-level debug info, `CompactSourceLineTable` is the primary mechanism. `DebugEntries` provides richer information when explicitly requested.

## Source file paths

Source files are stored in `CodeArea.SourceFiles` as strings. Paths can be:

- **Absolute**: `/home/user/project/parser.pl`. Used during development.
- **Relative to a base**: when bundles are deployed, paths are usually relative to a project root. The bundler has options `--source-root <path>` to control this.
- **Stripped**: only the file name (no directory). For deployment where source isn't shipped.

The bundler defaults to relative paths from the project root. A `--strip-paths` option keeps only the file name.

## Debug info in bundles

When the bundler produces a bundle:

- At Basic level: includes `CompactSourceLineTable` and `SourceFiles` array.
- At Full level: also includes `DebugEntries` (full structures).
- At None level: omits the debug sections entirely.

Bundles can have different debug levels for different modules (e.g., framework code at None, application code at Full), though this is not common.

## Adapting source-level debug info to IL-compiled code

When a predicate is promoted to Tier 1 (IL), the debug info from the bytecode must map to the generated IL for the .NET debugger to work properly.

The IL emitter, when receiving a `dbg_info` opcode, marks a **sequence point** at the corresponding IL position:

```csharp
public void EmitDbgInfo(int debugEntryId)
{
    var entry = _codeArea.DebugEntries[debugEntryId];
    var fileName = _codeArea.SourceFiles[entry.SourceFileId];
    
    // For DynamicMethod: ISymbolWriter is not available; cannot embed sequence points.
    // For PersistedAssemblyBuilder: ISymbolDocumentWriter is available.
    
    if (_emitter is PersistedAssemblyEmitter pae)
    {
        pae.MarkSequencePoint(fileName, entry.Line, entry.Column, entry.Line, entry.Column + 1);
    }
    
    // Else: no source mapping for this IL position.
}
```

For `DynamicMethod`, sequence points are not supported by the runtime. Debugging compiled methods relies on the IL compiler's choice to emit calls into a "debug hook" function that the debugger can intercept.

## Logging integration

The engine exposes a logging API for tracing:

```csharp
public partial class Engine
{
    public ILogger? Logger { get; set; }
    
    public bool TraceEnabled { get; set; }
}

public interface ILogger
{
    void Log(LogLevel level, string message);
}

public enum LogLevel
{
    Debug, Info, Warning, Error, Critical
}
```

When `TraceEnabled` is set, the interpreter emits trace lines at each predicate call and exit:

```
[TRACE] CALL: parse_expr/3 at parser.pl:42
[TRACE] EXIT: parse_expr/3 success
[TRACE] CALL: parse_term/3 at parser.pl:45
[TRACE] FAIL: parse_term/3
[TRACE] REDO: parse_term/3
...
```

Tracing has noticeable overhead even when no debugger is attached, so `TraceEnabled` is off by default.

## Standard Prolog debugging predicates

The following ISO/de-facto-standard Prolog predicates interact with the debug system:

- `trace/0`: enable tracing.
- `notrace/0`: disable tracing.
- `spy/1`: set a spypoint on a predicate.
- `nospy/1`, `nospyall/0`: remove spypoints.
- `debug/0`, `nodebug/0`: enable/disable debugging.

These map to engine-level operations on the attached debugger (or, if no debugger is attached, to the trace logging).

## Bytecode size impact

Estimated impact on bytecode size at each level:

- **None**: baseline. ~5-9 bytes per instruction on average.
- **Basic**: +0 bytes per instruction (info is in a side table). The side table adds ~24 bytes per clause and ~12 bytes per goal.
- **Full**: +6 bytes per goal (the `dbg_info` opcode). For a typical predicate with 10 goals, that's +60 bytes per clause.

For a 50,000-LOC Prolog program:
- None: ~500 KB bytecode.
- Basic: ~500 KB bytecode + ~50 KB side table.
- Full: ~700-900 KB bytecode + ~100-200 KB side table.

Acceptable in all cases.

## Configuration

```csharp
public class EngineConfig
{
    public DebugLevel DebugLevel { get; set; } = DebugLevel.Basic;
    // ...
}

public enum DebugLevel
{
    None,
    Basic,
    Full,
}
```

The bundler accepts `--debug-level <none|basic|full>`. Default: Basic.

The engine can override the bundled debug level at load time (e.g., loading at None even if the bundle was built with Full).

## Test strategy

- **Basic level**: parse a small program, verify exception messages include source line.
- **Stack trace**: trigger an exception in deeply-nested call, verify stack trace shows all frames with correct source positions.
- **Full level**: attach a mock debugger, run a program, verify `OnDebugPoint` is called the expected number of times.
- **Variable inspection**: at a debug point, query variable values by name, verify correct mapping.
- **Spurious dbg_info safety**: include a malformed `dbg_info` (out-of-range entry id), verify graceful error.
- **Bundle round-trip**: produce a bundle at Full level, load, verify debug entries match.
- **IL compilation with sequence points**: compile to a persisted assembly, load in a .NET debugger, verify breakpoints map to source.
- **None level performance**: benchmark a hot loop at Basic vs. None, verify overhead is < 5%.
- **Full level performance**: benchmark same loop at Full, document the overhead.

## See also

- ADR-006 (Bytecode Encoding): the Meta opcode mechanism.
- `wam-instruction-set.md`: the `dbg_info` meta opcode specification.
- ADR-009 (Bundler): debug info in bundles.
- ADR-011 (IL Compiler): sequence point emission for compiled code.
