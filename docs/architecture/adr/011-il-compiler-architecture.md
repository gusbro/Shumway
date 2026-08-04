# ADR-011: IL Compiler Architecture

## Status

Accepted (Phase 1). The tiered Tier-0/Tier-1 split, the Sigil-based runtime
emitter and the persisted build-time backend all shipped, and the Tier-1 arc
went far past this ADR.

> **Read the mechanism sketches below as the original Phase-1 design, not the
> as-built code.** Several concrete mechanisms described in the Decision are
> superseded or never shipped under the names used here:
> - **Tier-1 dispatch is threaded continuation, not a call-site inline cache.**
>   The `CallSiteCache` / `_predicateTableVersion` / `LookupAndCacheCallSite`
>   inline-caching design does not exist; the non-tail Call site sets a
>   resume marker and returns to the dispatch loop (ADR-016 / phase-16;
>   `Activation.Tier1.cs`). The old recursive `RunSubroutine` is gone.
> - **The IL store is per-engine, keyed by functor id, holding strong refs**
>   (`IlPromotionStore`) — not a process-wide, bytecode-hash-keyed,
>   weak-reference `ConcurrentDictionary`. Cross-engine reuse is via persisted
>   bundles, not a runtime cache.
> - **Promotion is off by default** (`IlPromotion.Threshold == 0` disables it);
>   there is no `CompilationStrategy` / `EngineConfig` / `Tier1PromotionThreshold`
>   configuration surface, and no `Predicate` / `InterpretedPredicate` /
>   `CompiledIlPredicate` class hierarchy (`RunBytecode` does not exist) — the
>   swap installs a delegate by functor id.
> - **What shipped beyond this ADR:** region compilation (default on),
>   deep cut / full indexed dispatch / backtrackable builtins / `call/N`
>   meta-calls all IL-emittable, cross-process persisted name-relative IL
>   bundles, and **dynamic predicates promoted as IL snapshots (ADR-023)** —
>   so the "only static predicates" note below is no longer true.
>
> For the current surface see the user guide, ADR-016, ADR-023, and the
> `Shumway.Compiler.Il` sources.

## Context

Shumway's performance target—comparable to or better than GNU Prolog in real-world scenarios—is unattainable with bytecode interpretation alone for compute-heavy workloads. The interpreter pays per-instruction dispatch overhead that limits throughput to roughly 2–5× slower than native code.

The standard solution is to compile predicates to native code on demand. In .NET, this means emitting CIL (Common Intermediate Language) that the runtime's JIT compiles to native code. The challenge is choosing the right strategy:

- **Eager (everything to IL at load)**: maximum performance but slow startup, wasted work for cold code.
- **Lazy (compile on first call)**: balances cost but pays the JIT cost on first invocation.
- **Tiered (interpret, then compile when hot)**: best for workloads with a mix of hot and cold code.

There is also the question of **where** the IL goes:

- **`DynamicMethod`**: lightweight, garbage-collectable, but cannot be persisted to disk. Limited to runtime use.
- **`AssemblyBuilder` + `PersistedAssemblyBuilder` (in .NET 9+)**: produces a `.dll` file that can be loaded as a normal assembly. Useful for build-time compilation of bundles. Slightly more expensive per-method.

For a system that targets both runtime adaptive compilation (Tier 1) and pre-compiled bundles (Phase 2), a unified IL emission layer that can target either backend is desirable.

Finally, there is the **scope of optimizations**:

- **Level 1 (translation)**: each WAM instruction maps to a few IL instructions that call back to engine methods. Eliminates the dispatch loop but doesn't inline operations.
- **Level 2 (inlining)**: hot operations (unification, deref, common patterns) are inlined into IL directly.
- **Level 3 (type-specialized)**: based on observed or declared types, generate specialized code paths that skip dispatch.
- **Level 4 (mode-aware)**: with `:- mode` declarations, generate code that assumes a determinism pattern (e.g., a `+,+,-` mode means no choice points, no trail entries).

Each level requires more compiler infrastructure. Phase 1 shipped Level 1 + selected Level 2; the later optimization arc went well past the original roadmap by different routes (regions, ADR-029..034) — Levels 3-4 as literally described (type/mode specialization) were not built (see ADR-012).

## Decision

Shumway has a tiered compilation strategy:

- **Tier 0**: WAM bytecode interpreter (always available, see ADR-006).
- **Tier 1**: IL-compiled code, generated at runtime via `DynamicMethod` + Sigil, or at build time via `PersistedAssemblyBuilder`.

### Promotion strategy

By default (`CompilationStrategy.Tiered`):

1. All predicates start in Tier 0.
2. Each invocation increments an invocation counter on the predicate.
3. When the counter exceeds `Tier1PromotionThreshold` (default: 1000), the predicate is queued for compilation.
4. Compilation runs on a **background thread** to avoid blocking the engine.
5. When the compiled code is ready, the predicate's entry in the engine's table is atomically swapped to the compiled form.
6. Subsequent invocations use the compiled code; the bytecode form is kept for reference (e.g., for debugging).

Alternative strategies via `EngineConfig.Compilation`:

- `Interpreted`: never compile to IL. All execution is via Tier 0. Useful for AOT or low-memory scenarios.
- `EagerIl`: compile everything at load time. Slower startup, peak performance.

### IL emission layer abstraction

The IL compiler emits IL through an abstraction that can target either `DynamicMethod` or `PersistedAssemblyBuilder`:

```csharp
public interface IIlEmitter
{
    void EmitLoadEngine();
    void EmitLoadConst(int value);
    void EmitLoadConst(long value);
    void EmitLoadArgument(int idx);
    void EmitCall(MethodInfo method);
    void EmitCallVirt(MethodInfo method);
    void EmitBranchIfFalse(Label target);
    void EmitJump(Label target);
    Label DefineLabel();
    void MarkLabel(Label label);
    LocalBuilder DeclareLocal(Type type);
    void EmitReturn();
    // ... more, abstracted over Sigil and raw ILGenerator
    
    Delegate FinalizeAsDelegate();          // for DynamicMethod
    MethodInfo FinalizeAsMethod();          // for PersistedAssemblyBuilder
}

public class DynamicMethodEmitter : IIlEmitter
{
    // Uses Sigil for type-safe IL emission
    private Sigil.Emit<PredicateDelegate> _emit;
}

public class PersistedAssemblyEmitter : IIlEmitter
{
    // Uses raw ILGenerator with internal typed wrappers
    private MethodBuilder _methodBuilder;
    private ILGenerator _il;
}
```

The compiler itself is target-agnostic: it iterates the bytecode and calls `IIlEmitter` methods. The same compilation logic produces runtime methods or assembly methods.

### Sigil for runtime safety

For runtime emission, **Sigil** (MS-PL license) is used as a typed wrapper over `ILGenerator`. Sigil validates the IL stack and types during emission, catching errors before the JIT does. This drastically reduces development time for IL-emitting code.

```csharp
var emit = Sigil.Emit<Func<Engine, int, bool>>.NewDynamicMethod("compiled");
emit.LoadArgument(0);                       // Engine
emit.LoadField(typeof(Engine).GetField("_registers"));
emit.LoadConstant(3);
emit.LoadElement<Cell>();
// ... etc
var del = emit.CreateDelegate();
```

For build-time emission (PersistedAssemblyBuilder), Sigil's generic-typed API is not directly applicable (the method is not typed at compile time of the generator). The implementation uses a thin custom layer over `ILGenerator` for build-time emission, with the same patterns as the Sigil-based runtime emitter.

### Compilation method signature

All compiled predicates have the same delegate signature:

```csharp
public delegate bool PredicateDelegate(Engine engine, int argBase);
```

- `engine`: the engine instance. The compiled code is engine-agnostic; the engine is a parameter.
- `argBase`: the offset in the engine's register array where arguments are stored.
- Returns `true` on success, `false` on failure.

For non-deterministic predicates (with multiple solutions via choice points), the same signature is used: the compiled code creates and manages CPs internally, just like the interpreter does.

This signature decision is critical: it makes the compiled code reusable across engines (shared global IL cache) and across queries.

### Global IL cache

Compiled IL code is keyed by the **hash of the bytecode** of the predicate. When a predicate is promoted:

1. Compute the hash of the bytecode (e.g., SHA-256 truncated to 64 bits).
2. Look up the hash in the global IL cache (`ConcurrentDictionary<long, PredicateDelegate>`).
3. If hit, use the cached delegate.
4. If miss, compile, store in cache, use.

Two engines that load the same module get the same bytecode, hence the same hash, hence share the compiled IL. This is significant for server scenarios where many engines load the same rules.

The cache uses weak references: when no engine references a delegate, the .NET GC can reclaim it.

### Phase 1 optimization scope

For Phase 1, the IL compiler implemented:

**Level 1 (translation, all opcodes)**:
- Every WAM opcode has a corresponding IL emission method.
- The emitted IL calls back into engine methods for complex operations (deref, unify, allocate, etc.).
- Eliminates the bytecode dispatch loop overhead.

**Level 2 inlining (top opcodes only)**:
- The 5–10 most frequent opcodes have inlined IL implementations:
  - `get_constant`, `put_constant` (constant unification)
  - `allocate`, `deallocate` (stack management)
  - `proceed` (return)
  - Builtin opcodes (`=/2`, `is/2`, basic comparisons)
- These avoid the `call` instruction to engine methods, allowing the JIT to optimize further.

**No type specialization** (Level 3) or **mode-aware compilation** (Level 4) in Phase 1.

### Phase 2+: build-time IL bundles

In phase 2, the bundler can produce a `.dll` containing pre-compiled IL for all static predicates in the bundle, using `PersistedAssemblyBuilder`:

```csharp
public class IlBundleCompiler
{
    public void CompileBundleIL(Bundle bundle, string outputDllPath)
    {
        var asmName = new AssemblyName($"Shumway.Compiled.{bundle.Name}");
        var asm = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        var module = asm.DefineDynamicModule(bundle.Name);
        var type = module.DefineType("CompiledPredicates", TypeAttributes.Public);
        
        foreach (var pred in bundle.StaticPredicates)
        {
            var method = type.DefineMethod(
                GetMethodName(pred),
                MethodAttributes.Public | MethodAttributes.Static,
                typeof(bool),
                new[] { typeof(Engine), typeof(int) });
            
            var emitter = new PersistedAssemblyEmitter(method);
            CompilePredicateBody(emitter, pred.Bytecode);
            emitter.FinalizeAsMethod();
        }
        
        type.CreateType();
        
        // Serialize to .dll
        var metadataBuilder = asm.GenerateMetadata(out var ilStream, out var fieldData);
        // ... PE building (see PersistedAssemblyBuilder docs)
    }
}
```

The resulting `.dll` is loaded at engine startup, populating the IL cache with all pre-compiled predicates. This avoids JIT pauses at runtime.

### Native AOT

`DynamicMethod` and `PersistedAssemblyBuilder` both rely on the JIT, so they don't work under .NET Native AOT. For AOT scenarios:

- Tier 1 is disabled.
- All predicates run via Tier 0 (interpreter).
- The system functions correctly but with lower peak performance.

Native AOT support with Tier 1 is on the long-term roadmap (Phase 4+), requiring a different approach (source generators or AOT-friendly code generation).

### Recovery and fallback

If IL compilation fails for any reason (a bug, a runtime resource error, etc.), the system **gracefully falls back to Tier 0**:

- The error is logged (via the configured logging mechanism).
- The predicate remains in Tier 0.
- The invocation counter is not incremented to retry (to avoid repeated failed compilation attempts).

This ensures that an IL compilation bug doesn't crash applications; it just degrades performance.

### Inline caching at call sites

When compiled code calls another predicate, the call site uses **inline caching** for performance:

```
Compiled IL for caller pred:
  ...
  ; Set up arguments in registers
  ldarg.0                                    ; engine
  ldsfld _callSite_42                        ; CallSite struct
  ldfld _callSite_42.CachedDelegate           ; cached predicate delegate
  brfalse SlowPath
  ldfld _callSite_42.CachedVersion
  ldsfld _engine._predicateTableVersion
  bne.un SlowPath                            ; if version changed, re-resolve
  ; Fast path: call cached delegate directly
  callvirt PredicateDelegate.Invoke
  ret
SlowPath:
  ldarg.0
  ldc.i4 functor_id
  callvirt Engine.LookupAndCacheCallSite
  callvirt PredicateDelegate.Invoke
  ret
```

The cached delegate is invalidated when the predicate table version changes (e.g., a dynamic predicate is modified). Static predicates' delegates never change once cached.

This optimization is part of Phase 1 because the cost is small (a few IL instructions per call site) and the benefit is significant for hot code.

## Alternatives Considered

### Tier 0 only (no IL compilation)

**Rejected.** Performance would be 2–5× slower than competing implementations. Compute-heavy grammar-processing workloads would suffer.

### Eager IL compilation (no Tier 0)

**Rejected as default.** Startup time would be unacceptable for large programs (50,000-LOC programs would take seconds to compile). Configurable via `EngineConfig.Compilation = EagerIl` for cases where startup time doesn't matter.

### Compile to direct C# source code, then to IL via Roslyn

**Considered, rejected.** Generating C# textual code is friendlier for debugging but slower to compile. Roslyn is a multi-MB dependency. The IL emission approach is more direct and lower-overhead.

### Generate to LLVM IR via LLVMSharp

**Rejected.** LLVM is a heavy dependency. .NET IL is already a well-optimized target; emitting LLVM would not yield significantly better code for this domain.

### Use `Expression<T>.Compile`

**Rejected.** Expression trees cannot represent all the patterns needed (complex control flow, goto-style jumps for choice points). They are also slower to compile than direct IL emission.

### Use Lokad.ILPack instead of PersistedAssemblyBuilder

**Rejected.** Lokad.ILPack was the workaround for the absence of `AssemblyBuilder.Save` in .NET Core 1.x–8.x. In .NET 9+, `PersistedAssemblyBuilder` is the official solution and does not require an external dependency.

## Consequences

### Positive

- **Performance ramps up after warm-up**: hot predicates run at near-native speed.
- **Startup is fast**: programs load in tier 0, are usable immediately, and optimize in the background.
- **Build-time IL for bundles**: production deployments can pre-compile, eliminating JIT pauses at startup.
- **Configurable**: clients with specific needs (max startup speed, AOT, etc.) can choose strategies.
- **Code sharing across engines**: the global cache reduces redundant compilation.
- **Failure recovery**: a compiler bug degrades performance, not correctness.

### Negative

- **Two execution paths to maintain**: interpreter and IL compiler must produce identical results. Bugs in one but not the other are possible.
- **Background compilation introduces threading**: even though engines are single-threaded, compilation happens in a separate thread, requiring synchronization for the swap.
- **Memory cost of cached IL**: large programs with many predicates have large memory footprints. Mitigated by the weak-reference cache.

### Mitigations

- **Cross-validation testing**: run the same Prolog programs through Tier 0 and Tier 1, compare results. Any divergence is a bug.
- **Compilation thread isolation**: the background compiler does not access engine-private state; it only reads bytecode and produces a delegate. The atomic swap is the only synchronization point.
- **Configurable cache eviction**: when memory pressure is high, the global cache can be cleared by the runtime.

## Implementation Notes

### Background compilation thread

A dedicated `Task` is spawned for each compilation. The compiled delegate, when ready, is atomically swapped into the engine's predicate table.

To prevent runaway compilation queues, a single global compilation queue (with concurrency limit) is used. Predicates queued multiple times (e.g., promoted in multiple engines) are deduplicated by their bytecode hash.

### Atomic swap of predicate

The engine's predicate table maps `FunctorId → Predicate`. `Predicate` is a base class with subclasses `InterpretedPredicate` and `CompiledIlPredicate`:

```csharp
public abstract class Predicate
{
    public abstract bool Invoke(Engine engine, int argBase);
}

public class InterpretedPredicate : Predicate
{
    public byte[] Bytecode;
    public int InvocationCount;
    public override bool Invoke(Engine engine, int argBase)
        => engine.RunBytecode(Bytecode, argBase);
}

public class CompiledIlPredicate : Predicate
{
    public PredicateDelegate Compiled;
    public override bool Invoke(Engine engine, int argBase)
        => Compiled(engine, argBase);
}
```

Swapping is a dictionary entry replacement. Since dictionaries in .NET allow concurrent reads while writes happen, the swap is safe for the read path: a reader either sees the old or new value, both correct.

### Call site representation

Inline caching at call sites is implemented via a static field per call site (`_callSite_NNN`). The field holds a small `CallSiteCache` struct:

```csharp
internal struct CallSiteCache
{
    public PredicateDelegate? CachedDelegate;
    public int CachedVersion;
}
```

The engine has a `_predicateTableVersion` counter, incremented when any predicate table changes. Call sites compare versions to validate the cache.

For static predicates that never change, this is just a one-time miss followed by all hits. For dynamic predicates, the cache may invalidate more often.

### Hash-based deduplication

The bytecode hash is computed once per predicate when it is loaded:

```csharp
public long ComputeBytecodeHash(byte[] bytecode, int start, int length)
{
    // SHA-256 or xxHash truncated to 64 bits
}
```

Hash collisions are theoretically possible but vanishingly unlikely in practice. Even a 1-in-2^32 collision rate would require billions of predicates to be a concern.

### Exception handling in compiled code

The compiled code wraps its body in a try/catch:

```csharp
public bool Invoke(Engine engine, int argBase)
{
    try
    {
        // compiled IL body
    }
    catch (PrologException pe)
    {
        engine.SetPendingException(pe);
        return false;
    }
    catch (Exception ex)
    {
        engine.SetPendingException(new PrologRuntimeException(ex));
        return false;
    }
}
```

The interpreter (or higher-level engine code) checks `_pendingException` after each call to propagate the exception to a `catch/3` handler or up to the client.

## Test Strategy

- **Cross-validation**: every test in the test suite runs in both Tier 0 and Tier 1 (with eager compilation), comparing outputs.
- **Promotion mechanics**: invoke a predicate the threshold number of times, verify it gets promoted, verify subsequent calls use IL.
- **Background compilation timing**: stress test that runs many promotions in parallel, verifies no deadlocks, no races, correct results.
- **Cache deduplication**: load the same module in two engines, verify only one compilation occurs.
- **Failed compilation fallback**: inject a fault in the compiler, verify the predicate continues to work via Tier 0.
- **Inline caching**: profile call sites, verify cache hits dominate after warm-up.
- **PersistedAssemblyBuilder roundtrip**: compile a bundle to .dll, load it in a fresh engine, verify queries work.
- **Native AOT**: build the project with AOT, verify it runs (Tier 0 only).

## Related ADRs

- ADR-006 (Bytecode Encoding): the IL compiler consumes bytecode.
- ADR-008 (Module Visibility): static predicates promote to Tier 1; dynamic
  predicates promote too, as clause snapshots (ADR-023).
- ADR-009 (Bundler): compiled-IL bundles are produced with
  `shumway-link --with-compiled-il`.
- ADR-016 (Threaded Tier-1 dispatch): the as-built call mechanism.
- ADR-023 (Dynamic predicates in IL): dynamic-predicate promotion.
- ADR-012 (Mode Inference): the mode-specialized codegen that shipped in Phase 3.

## Related Design Docs

- `design/il-emission-patterns.md`: how the Tier-1 compiler lowers WAM to CIL
  (the real delegate contract and per-opcode lowering).
- `design/inline-caching.md`: notes that the once-sketched call-site inline cache
  was **not** built — the shipped dispatch is threaded continuation (ADR-016).
