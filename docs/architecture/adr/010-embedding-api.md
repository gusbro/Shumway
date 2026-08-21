# ADR-010: Embedding API

## Status

Accepted ([Phase 1](../../history/phase-1-closure.md)). Amended by
[ADR-047](047-pstr-is-a-list.md) (2026-08-20): there is no string *type* at the
boundary, so the `String`/`Pstr` surface (`MakeString`, `AsString`, `AsPstr`,
`TermKind.String`, `TermKind.Pstr`) is gone. The amendments are marked inline
below.

## Context

Shumway exists primarily to be embedded in .NET applications. The public API is the contract that .NET developers see and use. It must:

1. **Feel idiomatic to .NET developers**: PascalCase methods, properties, generics, async support, IDisposable, IEnumerable patterns.
2. **Minimize the cost of crossing the C# ↔ Prolog boundary**: this is where Shumway can outperform alternatives like GNU Prolog. Conversions, allocations, and indirections at the boundary should be minimal.
3. **Support multiple usage patterns**: single-shot queries, streaming solutions, foreign predicates, struct mapping, engine pooling for servers.
4. **Be type-safe where possible**: leverage C# generics and source generators.
5. **Compose with async**: long-running queries should integrate with `async`/`await` and cancellation.
6. **Match the threading model**: engines are single-threaded internally but thread-agile.

A common failure mode of FFI APIs is that they leak the internal model to the user, making client code feel like a thin wrapper over C functions. Another is that the API is so abstract it adds overhead. Shumway aims for a middle ground: idiomatic .NET, minimal overhead.

The API also intersects with the atom system (ADR-003): atoms exposed to C# code register weak references in the atom table. Term references handed to C# must be valid within their scope.

## Decision

> **The C# in this Decision is the original Phase-1 API sketch, not the shipped
> surface.** The design goals (idiomatic .NET, cheap boundary crossing, typed
> conversion, foreign predicates, pooling, async) all held and were built — but
> almost every concrete type and member name below was restructured on the way
> in. The current, accurate API reference is
> [`../../guide/user-guide.md`](../../guide/user-guide.md) and the
> `Shumway.Embedding` types themselves. Notable differences to keep in mind
> while reading:
> - **Terms** are the AST classes `AtomTerm` / `CompoundTerm` / `VarTerm` /
>   `IntTerm` / … (pattern-matched), **not** a `Term` struct with a `TermKind`
>   enum and `MakeAtom` / `IsAtom` helpers. `PrologEngine` has no term-factory
>   methods; terms are built with the AST constructors.
> - **Querying** is `engine.Query(string)` → a single `Solution`,
>   `engine.QueryAll(string)` → `IEnumerable<Solution>`, and
>   `engine.QueryAsync(string)` → `IAsyncEnumerable<Solution>`. There is no
>   `Query` class and no `Solutions()` / `FirstSolution()` family.
> - **Foreign predicates** use the `[PrologPredicate("name/arity")]` attribute +
>   `engine.RegisterPredicates(...)`, with methods taking a
>   `Shumway.Core.Activation` and returning `bool` (non-determinism via
>   `IEnumerable<T>` + `NonDeterministic = true`). There is no
>   `ForeignPredicate` delegate, `ForeignResult` enum, or `ForeignContext`.
> - **`PrologEngine` is not `IDisposable`** and there is no `TermHandle` /
>   scoped-handle mechanism: bindings are materialized AST terms held by
>   `Solution`.
> - **Exceptions** surface as the single `ShumwayPrologException` (with a
>   `Term Term`), not a typed `PrologSyntaxException` / … hierarchy.
> - **Pooling** is `EnginePool` handing out a `Lease` (with `.Activation`), not
>   a `PooledEngine`. There is no `EngineConfig` / `CompilationStrategy`
>   configuration type.

The public API surface is structured around several core types:

### `PrologEngine`: the central type

```csharp
public class PrologEngine : IDisposable
{
    public PrologEngine();
    public PrologEngine(EngineConfig config);
    
    public EngineFlags Flags { get; }
    public IReadOnlyList<ModuleInfo> LoadedModules { get; }
    
    // Loading
    public LoadResult Consult(string path);
    public LoadResult ConsultString(string source, string moduleName = "user");
    public LoadResult LoadBundle(string bundlePath);
    public LoadResult LoadBundle(Stream bundleStream);
    public LoadResult Reconsult(string path);
    
    // Term construction
    public Term MakeAtom(string name);
    public Term MakeInt(long value);
    public Term MakeInt(BigInteger value);
    public Term MakeFloat(double value);
    public Term MakeText(string value, TextKind kind = TextKind.Chars);  // ADR-047
    public Term MakeForeign(object obj);
    public Term Nil { get; }
    public Term MakeCompound(string functor, params Term[] args);
    public Term MakeList(IEnumerable<Term> elements);
    public Term MakeList(IEnumerable<Term> elements, Term tail);
    public Term MakeVariable();
    public Term ParseTerm(string source);
    
    // Conversion (with source generators)
    public Term ToTerm<T>(T value);
    public T FromTerm<T>(Term term);
    public void RegisterConverter<T>(Func<T, Term> toTerm, Func<Term, T> fromTerm);
    
    // Query execution
    public Query Query(string queryText);
    public Query Query(string queryText, IReadOnlyDictionary<string, Term> bindings);
    public Query Query(Term goal);
    
    // Foreign predicates
    public void RegisterPredicate(string functorName, int arity, ForeignPredicate predicate);
    public void RegisterPredicate<T>(T instance) where T : class;  // discovers [PrologPredicate]
    
    // Lifecycle
    public void Reset();
    public void Dispose();
}
```

### `Term`: the value type for Prolog terms

```csharp
public readonly struct Term : IEquatable<Term>
{
    // Construction is via PrologEngine.Make* methods
    
    public TermKind Kind { get; }
    
    public bool IsAtom { get; }
    public bool IsInt { get; }
    public bool IsBigInt { get; }
    public bool IsFloat { get; }
    public bool IsCompound { get; }
    public bool IsList { get; }
    public bool IsEmptyList { get; }
    public bool IsVariable { get; }
    public bool IsForeign { get; }
    public bool IsGround { get; }
    
    public string AsAtom { get; }           // throws if not atom
    public long AsInt { get; }              // throws if not int (or out of long range)
    public BigInteger AsBigInt { get; }
    public double AsFloat { get; }
    public object AsForeign { get; }
    
    public string Functor { get; }
    public int Arity { get; }
    public Term GetArg(int index);
    public Term Head { get; }
    public Term Tail { get; }
    public IEnumerable<Term> EnumerateList();
    
    public bool TryGetAtom(out string value);
    public bool TryGetInt(out long value);
    public bool TryGetFloat(out double value);
    public bool TryGetForeign<T>(out T value);
    public bool TryAsText(out string value);  // ADR-047: reads an atom or a
                                              // text list without materializing nodes
    
    public string ToString();
    public string ToCanonicalString();
    
    public bool Equals(Term other);  // structural equality (==/2 semantics)
}

public enum TermKind
{
    Variable,
    Atom,
    Integer,
    BigInteger,
    Float,
    Compound,
    List,
    EmptyList,
    Foreign,
}
```

> **Amended by ADR-047 — the representation is not observable at the boundary.**
> Text reaches C# as one of two things, decided by what it *is* in Prolog and
> never by how it happens to be stored:
>
> - **text as a value** is an atom, and crosses as `string`;
> - **text as a sequence** is a list, and crosses as a list.
>
> A packed list therefore reports `TermKind.List`, answers `IsList`, and
> enumerates element by element exactly like the cons list it denotes. This is
> load-bearing: a `[PrologPredicate]` method called once with a packed list and
> once with the equivalent cons list must receive the same argument, or the two
> callers are not interchangeable and the internal representation has leaked into
> the contract.
>
> `TryAsText` is the escape hatch for the cost, not for the type: it reads a text
> list (packed or not) or an atom into a `string` without materializing per-element
> nodes. The caller opts into it; nothing about the term changes.

A `Term` is a lightweight reference to a heap cell. **Term lifetime is scoped**: a `Term` is valid only within the operation that produced it (typically a `Solution` from a `Query`). Once the operation completes (the foreach loop ends, the query is disposed), terms obtained from it should not be used. Using a term outside its scope is undefined behavior.

For terms that must outlive their scope, the API provides handles:

```csharp
public class TermHandle : IDisposable
{
    public Term Term { get; }  // re-projected each access
    public void Dispose();      // releases the underlying reference
}

public partial class PrologEngine
{
    public TermHandle MakeHandle(Term term);
}
```

A `TermHandle` keeps the term's data alive by storing it in a per-engine stable region. Use sparingly; most code should use scope-bound terms.

### `Query`: a running goal with potentially multiple solutions

```csharp
public class Query : IDisposable
{
    public IEnumerable<Solution> Solutions();
    public IAsyncEnumerable<Solution> SolutionsAsync(CancellationToken ct = default);
    
    public Solution? FirstSolution();
    public Solution? OnlySolution();        // null if 0 or more than 1
    public bool HasSolution();
    public int CountSolutions();             // careful with infinite queries
    
    public T? FirstResult<T>(string varName);
    public IEnumerable<T> Results<T>(string varName);
    
    public void Dispose();
}
```

A `Query` is created from `engine.Query(...)`. Iterating its solutions advances Prolog's execution; backtracking happens between iterations. When the `Query` is disposed (typically via `using`), any active choice points are discarded.

### `Solution`: a snapshot of variable bindings

```csharp
public class Solution
{
    public Term this[string varName] { get; }
    public bool TryGet(string varName, out Term value);
    public IReadOnlyDictionary<string, Term> Bindings { get; }
}
```

The terms in a `Solution` are valid as long as the iteration is on this solution (until `MoveNext` is called on the underlying iterator). For longer-lived terms, use `MakeHandle`.

### Foreign predicates

Two registration patterns are supported:

**Direct delegate** (concise, for one-off predicates):

```csharp
public delegate ForeignResult ForeignPredicate(ForeignContext context);

public enum ForeignResult
{
    Success,
    Failure,
    SuccessWithChoice,  // for non-deterministic predicates
}

public class ForeignContext
{
    public PrologEngine Engine { get; }
    
    public Term GetArg(int index);
    public bool Unify(int argIndex, Term value);
    public bool Unify(Term a, Term b);
    
    // For non-deterministic predicates: state across calls.
    public object? State { get; set; }
    public bool IsFirstCall { get; }
}

engine.RegisterPredicate("get_current_time", 1, ctx =>
{
    var time = ctx.Engine.MakeInt(DateTime.UtcNow.Ticks);
    return ctx.Unify(0, time) ? ForeignResult.Success : ForeignResult.Failure;
});
```

**Attribute-based** (for classes that group multiple predicates):

```csharp
public class MathPredicates
{
    [PrologPredicate("sqrt_int", 2)]
    public ForeignResult IntegerSqrt(ForeignContext ctx)
    {
        long n = ctx.GetArg(0).AsInt;
        long sqrt = (long)Math.Sqrt(n);
        return ctx.Unify(1, ctx.Engine.MakeInt(sqrt)) 
            ? ForeignResult.Success 
            : ForeignResult.Failure;
    }
    
    [PrologPredicate("gcd", 3)]
    public ForeignResult Gcd(ForeignContext ctx) { /* ... */ }
}

// Register all methods marked with [PrologPredicate]
engine.RegisterPredicate(new MathPredicates());
```

### Struct ↔ term mapping (source generators)

For zero-overhead conversion between .NET types and Prolog terms:

```csharp
[PrologTerm("point")]
public partial class Point
{
    public int X;
    public int Y;
}

// The source generator emits:
public partial class Point
{
    public static Point FromTerm(Term t)
    {
        if (!t.IsCompound || t.Functor != "point" || t.Arity != 2)
            throw new TermConversionException(...);
        return new Point
        {
            X = (int)t.GetArg(0).AsInt,
            Y = (int)t.GetArg(1).AsInt,
        };
    }
    
    public Term ToTerm(PrologEngine engine)
    {
        return engine.MakeCompound("point",
            engine.MakeInt(X),
            engine.MakeInt(Y));
    }
}
```

Built-in conversions are provided for common .NET types:

| .NET Type | Prolog Term |
|-----------|-------------|
| `int`, `long` | Integer (inline or BigInteger) |
| `BigInteger` | BigInteger |
| `double`, `float` | Float |
| `string` | Atom (ADR-047; a text *list* is requested with `[PrologText(TextKind.Chars)]` on the field) |
| `bool` | Atom `true` or `false` |
| `char` | Atom of one char |
| `DateTime` | Compound `datetime(Year, Month, Day, ...)` |
| `Guid` | Atom (string representation) |
| `IEnumerable<T>`, `T[]` | List |
| `Dictionary<K,V>` | List of `K-V` (Compound `-`) |
| `Nullable<T>` | `none` atom or `some(T)` compound |
| `Tuple<T1, T2>` | Compound `-(T1, T2)` |

Custom converters can be registered:

```csharp
engine.RegisterConverter<DateTime>(
    toTerm: dt => engine.MakeCompound("datetime",
        engine.MakeInt(dt.Year),
        engine.MakeInt(dt.Month),
        engine.MakeInt(dt.Day),
        engine.MakeInt(dt.Hour),
        engine.MakeInt(dt.Minute),
        engine.MakeInt(dt.Second)),
    fromTerm: t => new DateTime(
        (int)t.GetArg(0).AsInt,
        (int)t.GetArg(1).AsInt,
        (int)t.GetArg(2).AsInt,
        (int)t.GetArg(3).AsInt,
        (int)t.GetArg(4).AsInt,
        (int)t.GetArg(5).AsInt));
```

### Engine pool

For server scenarios:

```csharp
public class EnginePool : IDisposable
{
    public EnginePool(int initialCount, Func<PrologEngine> factory);
    public EnginePool(int initialCount, int maxCount, Func<PrologEngine> factory);
    
    public PooledEngine Rent();
    public Task<PooledEngine> RentAsync(CancellationToken ct = default);
    
    public void Dispose();
}

public class PooledEngine : IDisposable
{
    public PrologEngine Engine { get; }
    public void Dispose();  // returns to pool
}

// Usage in a server
var pool = new EnginePool(10, () =>
{
    var e = new PrologEngine();
    e.LoadBundle("rules.shum");
    return e;
});

async Task ProcessRequest(Request req)
{
    using var pooled = pool.Rent();
    var engine = pooled.Engine;
    using var query = engine.Query("process_request(...)");
    var result = query.FirstSolution();
    // ...
}
```

### Async query execution

```csharp
public partial class Query
{
    public IAsyncEnumerable<Solution> SolutionsAsync(CancellationToken ct = default);
    public Task<Solution?> FirstSolutionAsync(CancellationToken ct = default);
}
```

Cancellation is **cooperative via safe points**. As built, the cancellation
token is observed the next time the heap-GC watermark is crossed, at which
point the query throws `OperationCanceledException` — so the per-goal path pays
nothing. A heap-bounded loop such as `repeat, fail` that never crosses that
watermark is therefore **not** cancellable by design.

```csharp
await foreach (var sol in query.SolutionsAsync(cts.Token))
{
    Console.WriteLine(sol["X"]);
}
```

As built, `QueryAsync` drives each solution step on a thread-pool thread
(`await Task.Run(() => iter.MoveNext())`) so the caller's thread is free
between solutions; a single engine is still single-threaded, so the steps do
not run concurrently with each other. For concurrent queries, use one engine
per thread (or an `EnginePool`).

### Exception model

```csharp
public abstract class PrologException : Exception
{
    public Term? PrologError { get; }   // the term representation, if available
}

public class PrologSyntaxException : PrologException { }       // parse errors
public class PrologExistenceException : PrologException { }    // predicate not found
public class PrologTypeException : PrologException { }         // type mismatch
public class PrologDomainException : PrologException { }       // value out of domain
public class PrologEvaluationException : PrologException { }   // arithmetic, etc.
public class PrologPermissionException : PrologException { }   // modify static, etc.
public class PrologRepresentationException : PrologException { } // limits exceeded
public class PrologRuntimeException : PrologException { }      // thrown via throw/1
public class TermConversionException : PrologException { }     // struct mapping failed
```

When Prolog code executes `throw(error(...))`, the embedding API converts the error term to an appropriate exception type. The original term is preserved in `PrologError` for callers that need the structured form.

Conversely, when a foreign predicate throws a .NET exception, it is wrapped as a Prolog error term and propagated to the calling Prolog code. The Prolog `catch/3` can catch it; if uncaught, it propagates back to .NET as `PrologRuntimeException`.

### Engine configuration

```csharp
public class EngineConfig
{
    public int InitialHeapSize { get; set; } = 65536;
    public int MaxHeapSize { get; set; } = 0;  // 0 = unlimited
    public int InitialStackSize { get; set; } = 8192;
    public int MaxStackSize { get; set; } = 0;
    public int InitialBindingTrailSize { get; set; } = 1024;
    public int InitialExtraTrailSize { get; set; } = 64;
    
    public DoubleQuotesMode DoubleQuotes { get; set; } = DoubleQuotesMode.Codes;
    public bool StrictDynamicDeclarations { get; set; } = false;
    public DebugLevel DebugLevel { get; set; } = DebugLevel.Basic;
    public CompilationStrategy Compilation { get; set; } = CompilationStrategy.Tiered;
    
    public int Tier1PromotionThreshold { get; set; } = 1000;
    public int SafePointInstructionInterval { get; set; } = 1024;
}

// ADR-047: default is Chars. String is an SWI compatibility alias that produces
// a packed list of chars — not a distinct type. There is no Pstr mode: packing
// is a storage decision, never a term-type decision.
public enum DoubleQuotesMode { Codes, Chars, Atom, String }
public enum DebugLevel { None, Basic, Full }
public enum CompilationStrategy { Interpreted, Tiered, EagerIl }
```

## Alternatives Considered

### Term as a reference-counted handle

**Rejected.** Reference counting on every term access would add overhead to the common case (terms in tight loops during query iteration). The scope-bound model with optional handles is cleaner and faster.

### Term as a record class (managed object)

**Rejected.** Allocating a managed object for every term operation would generate massive GC pressure. The lightweight struct backed by a heap index is much cheaper.

### Synchronous-only API

**Rejected.** Server scenarios with many concurrent requests benefit from async APIs for cancellation. The cost of supporting `IAsyncEnumerable` is small.

### Async/await internally in the interpreter

**Rejected.** The interpreter is performance-critical and synchronous. Async machinery (state machines, awaits) would slow it down. The async API at the embedding layer is a thin shell that delegates to the synchronous core, with cooperative cancellation via safe points.

### Querying via lambda expressions ("LINQ to Prolog")

**Considered, deferred.** A LINQ-style API where queries are expressed as C# expression trees and translated to Prolog goals is interesting but complex. The current string-based and term-based query API covers the use cases without that complexity. Could be added in phase 2+ as an experimental feature.

### Required Dispose for terms

**Rejected.** Forcing users to dispose every term would be onerous. The scope-bound model (terms valid only during the operation that produced them) is friendlier. Explicit handles are available for the rare cases that need persistence.

## Consequences

### Positive

- **Idiomatic .NET**: PascalCase, IDisposable, IEnumerable, generics, async/await.
- **Low overhead for the common case**: terms are 8-byte handles, not heap-allocated objects.
- **Type-safe with generics**: source generators eliminate boxing in struct ↔ term conversion.
- **Composable with async**: cancellation, IAsyncEnumerable.
- **Engine pool for servers**: standard pattern, easy to use.
- **Clear exception model**: Prolog errors map to typed .NET exceptions.

### Negative

- **Term lifetime is not enforced by the type system**: a developer can hold a term beyond its scope and get undefined behavior. The documentation must be clear.
- **Foreign predicates require boilerplate**: each foreign predicate is a method or delegate with `ForeignContext` access. Less ergonomic than declarative interop in some other systems.
- **Async is single-threaded per engine**: developers expecting parallel speedup within one engine will be disappointed; they need multiple engines.

### Mitigations

- **Debug builds detect out-of-scope term usage**: the engine tracks term generation; using a term from a previous generation throws.
- **Roslyn analyzers**: a Shumway-specific analyzer can detect common misuses (holding terms across operation boundaries, using terms after Dispose, etc.) at compile time.
- **Documentation and samples**: every public type has XML docs; the `samples/` directory in the repo has worked examples.

## Implementation Notes

### Term generation tracking (debug)

In debug builds, each term carries a generation number. Operations that invalidate previous terms (backtracking past a CP that created the term) increment a generation counter on the engine. Term access checks the generation; mismatch throws.

This is opt-in via a build flag and not present in release. Cost: one field per term, one comparison per access.

### Source generator implementation

The source generator scans for `[PrologTerm]` and `[PrologPredicate]` attributes and emits partial class extensions. The generated code is regular C#, fully inspectable, and benefits from JIT optimizations.

### `MakeForeign` and the foreign table

When `engine.MakeForeign(obj)` is called, the object is added to a per-engine foreign table. The returned `Term` references the table entry by id. When the engine is disposed, the foreign table is cleared.

For long-lived foreign objects, the host application is responsible for keeping the object alive (via its own references).

### Thread safety of `EnginePool`

The pool itself is thread-safe (rent/return can be called from any thread). The engines returned by `Rent` are exclusively owned by the renter until `Dispose` returns them.

### `EnginePool.RentAsync`

If no engine is available and the pool has not reached `maxCount`, `RentAsync` creates a new one (via the factory). If maxCount is reached, it awaits an engine being returned.

## Test Strategy

- **Construction roundtrip**: construct terms of every kind via the API, inspect them, verify properties.
- **Query iteration**: simple queries with multiple solutions; verify all are produced and iteration ends correctly.
- **Query disposal**: dispose a Query mid-iteration; verify the engine state is consistent for subsequent queries.
- **Async query with cancellation**: long-running query, cancel, verify `OperationCanceledException` is raised at the next safe point.
- **Foreign predicate (deterministic)**: register, call from Prolog, verify result.
- **Foreign predicate (non-deterministic)**: register, call, iterate multiple solutions.
- **Foreign predicate throwing**: throw .NET exception in foreign predicate; verify Prolog catch/3 can catch it; verify uncaught propagation to .NET.
- **Struct mapping**: define a struct with `[PrologTerm]`, convert to term, convert back, verify equality.
- **Custom converter**: register, use, verify.
- **Engine pool**: stress test with many concurrent rents/returns from multiple threads.
- **Term out-of-scope (debug)**: hold a term past its scope, use it, verify exception (debug build only).
- **Engine migration across threads**: use the same engine from thread A then thread B (with serialization between), verify correctness.

## Related ADRs

- ADR-001 (Engines and Global Tables): thread-agility and pooling.
- ADR-003 (Atom Three-Tier System): atoms exposed via API register foreign holds.
- ADR-011 (IL Compiler): the IL strategy is configurable via `EngineConfig`.

## Related Design Docs

- `design/api-reference.md`: a compact map of the real public surface (the user
  guide is the worked reference).
- `design/foreign-predicates.md`: the shipped `[PrologPredicate]` foreign-predicate
  mechanism.
