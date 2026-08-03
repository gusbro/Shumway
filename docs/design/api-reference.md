# Public API Reference

> **Design-time snapshot (2026-05).** The embedding surface has grown since
> (CLP opt-ins `UseClpfd`/`UseClpr`, native interop `UseNativeLibrary`/`Reftype`,
> debugging `EnableDebugging`/`DebugOptions`, `EnginePool`, async queries).
> The XML docs on `PrologEngine` and [`../user-guide.md`](../guide/user-guide.md) are
> the current reference; this file covers the v1 core, which is unchanged.

This document is the exhaustive reference for Shumway's public .NET API. It complements ADR-010 by providing complete signatures, parameter descriptions, exceptions, and examples for every public type.

## Namespace: `Shumway`

The core public types live in the `Shumway` namespace.

---

## PrologEngine

The main entry point for embedding Prolog in .NET applications.

### Constructors

```csharp
public PrologEngine();
public PrologEngine(EngineConfig config);
```

Creates a new engine. The parameterless constructor uses default configuration. The engine is ready to use immediately; built-ins are pre-registered.

**Example**:
```csharp
using var engine = new PrologEngine();
// engine is ready for use
```

### Properties

```csharp
public EngineFlags Flags { get; }
public IReadOnlyList<ModuleInfo> LoadedModules { get; }
public EngineConfig Config { get; }
public CancellationToken CancellationToken { get; }
```

- `Flags`: runtime flags (mutable via `SetFlag`).
- `LoadedModules`: snapshot of currently loaded modules.
- `Config`: the configuration this engine was created with (immutable after construction).
- `CancellationToken`: token signaled when the engine is being cancelled (e.g., for async query cancellation).

### Loading

```csharp
public LoadResult Consult(string path);
public LoadResult ConsultString(string source, string moduleName = "user");
public LoadResult LoadBundle(string bundlePath);
public LoadResult LoadBundle(Stream bundleStream);
public LoadResult Reconsult(string path);
```

- `Consult(path)`: parses and compiles a Prolog source file. Returns a `LoadResult` with success/error info.
- `ConsultString`: same but from a string. The module name defaults to "user".
- `LoadBundle`: loads a pre-compiled bundle. Faster than consulting source.
- `Reconsult`: reloads a previously loaded file, replacing its module.

**Exceptions**:
- `FileNotFoundException`: source/bundle file doesn't exist.
- `PrologSyntaxException`: parse error in source.
- `BundleFormatException`: invalid bundle format.

### Term construction

```csharp
public Term MakeAtom(string name);
public Term MakeInt(long value);
public Term MakeInt(BigInteger value);
public Term MakeFloat(double value);
public Term MakeString(string value);
public Term MakePstr(string value);
public Term MakeForeign(object obj);
public Term Nil { get; }
public Term MakeCompound(string functor, params Term[] args);
public Term MakeCompound(int functorId, params Term[] args);
public Term MakeList(IEnumerable<Term> elements);
public Term MakeList(IEnumerable<Term> elements, Term tail);
public Term MakeVariable();
public Term ParseTerm(string source);
public TermBuilder NewBuilder();
public ListBuilder NewListBuilder();
```

Constructs terms in the engine's heap. The returned `Term` is valid until the next operation that may modify the heap (e.g., a query that backtracks past this point).

**`MakeInt(long)`** returns an INT or BIGINT cell depending on the value's magnitude.

**`MakeCompound`**: creates a compound term. The first overload looks up or interns the functor name; the second uses a pre-computed `FunctorId` for performance.

**`MakeList`**: builds a list. With one argument, terminates with `[]`. With two, the second is the tail (allowing partial lists).

**`ParseTerm`**: parses a Prolog term from a string, returning the corresponding Term.

**`NewBuilder` / `NewListBuilder`**: for efficient batch construction.

**Examples**:
```csharp
Term t1 = engine.MakeAtom("foo");
Term t2 = engine.MakeInt(42);
Term t3 = engine.MakeCompound("point", engine.MakeInt(3), engine.MakeInt(4));
Term t4 = engine.MakeList(new[] {
    engine.MakeAtom("a"),
    engine.MakeAtom("b"),
    engine.MakeAtom("c")
});
Term t5 = engine.ParseTerm("foo(bar, baz(1,2))");
```

### Term builder (batch construction)

```csharp
public class TermBuilder
{
    public Term Atom(string name);
    public Term Int(long value);
    public Term Compound(string functor, params Term[] args);
    public Term List(params Term[] elements);
    // ... etc., shortcuts to engine.Make*
}

public class ListBuilder
{
    public void Add(Term element);
    public void Add(string atomName);
    public void Add(long intValue);
    public Term Build();
    public Term Build(Term tail);
}
```

The builders avoid repeated `engine.Make*` calls when constructing many terms.

### Conversion (struct-term)

```csharp
public Term ToTerm<T>(T value);
public T FromTerm<T>(Term term);
public void RegisterConverter<T>(Func<T, Term> toTerm, Func<Term, T> fromTerm);
```

Convert between .NET values and Prolog terms. Uses registered converters and built-ins for common types.

`ToTerm` uses the registered converter or built-in conversion for `T`. Throws `MissingConverterException` if no converter is registered and `T` is not a built-in type.

`FromTerm` is the inverse.

### Query execution

```csharp
public Query Query(string queryText);
public Query Query(string queryText, IReadOnlyDictionary<string, Term> initialBindings);
public Query Query(Term goal);
```

Creates a `Query` object that, when iterated, produces solutions.

**Initial bindings**: when calling with a string, the engine binds named variables in the query before execution. Useful for parameterized queries.

**Example**:
```csharp
using var query = engine.Query("member(X, [a, b, c])");
foreach (var sol in query.Solutions())
{
    Console.WriteLine(sol["X"].AsAtom);  // prints a, b, c
}
```

### Foreign predicate registration

```csharp
public void RegisterPredicate(string functorName, int arity, ForeignPredicate predicate);
public void RegisterPredicate(string functorName, int arity, ForeignPredicate predicate, 
    string module, Visibility visibility);
public void RegisterPredicate<T>(T instance) where T : class;
```

See `foreign-predicates.md` for details.

### Lifecycle

```csharp
public void Reset();
public void Dispose();
public void RunAtomGc();
```

- `Reset()`: clears execution state (heap, stack, trails, registers, auxiliary tables). Preserves loaded predicates and modules.
- `Dispose()`: releases all resources. After Dispose, the engine cannot be used.
- `RunAtomGc()`: explicitly triggers atom GC.

### Flags

```csharp
public void SetFlag(string name, Term value);
public Term GetFlag(string name);
```

Programmatic access to engine flags. Equivalent to `set_prolog_flag/2` and `current_prolog_flag/2`.

---

## Term

A reference to a Prolog term. Lightweight (single 8-byte cell + optional engine reference). Valid only within its scope.

### Properties

```csharp
public TermKind Kind { get; }
public bool IsAtom { get; }
public bool IsInt { get; }       // includes BigInt
public bool IsBigInt { get; }
public bool IsFloat { get; }
public bool IsString { get; }
public bool IsPstr { get; }
public bool IsCompound { get; }
public bool IsList { get; }
public bool IsEmptyList { get; }
public bool IsVariable { get; }
public bool IsForeign { get; }
public bool IsGround { get; }
public bool IsCallable { get; }
public PrologEngine Engine { get; }
```

### Value accessors (throw on type mismatch)

```csharp
public string AsAtom { get; }
public long AsInt { get; }
public BigInteger AsBigInt { get; }
public double AsFloat { get; }
public string AsString { get; }
public string AsPstr { get; }
public object AsForeign { get; }
public T AsForeign<T>();
```

### Compound term inspection

```csharp
public string Functor { get; }
public int Arity { get; }
public FunctorId FunctorId { get; }
public Term GetArg(int index);             // 0-indexed
public Term Head { get; }                  // for lists
public Term Tail { get; }                  // for lists
public IEnumerable<Term> EnumerateList();  // for proper lists
```

### Try-pattern accessors

```csharp
public bool TryGetAtom(out string value);
public bool TryGetInt(out long value);
public bool TryGetBigInt(out BigInteger value);
public bool TryGetFloat(out double value);
public bool TryGetString(out string value);
public bool TryGetPstr(out string value);
public bool TryGetForeign<T>(out T value);
public bool TryGetList(out IReadOnlyList<Term> elements);
public bool TryGetCompound(out string functor, out IReadOnlyList<Term> args);
```

### Comparison

```csharp
public bool Equals(Term other);                  // structural ==
public int CompareTo(Term other);                // standard order of terms
public override int GetHashCode();
```

### Conversion

```csharp
public string ToString();                        // Prolog representation, default options
public string ToCanonicalString();               // canonical form
public string ToString(TermWriteOptions options);
```

```csharp
public class TermWriteOptions
{
    public bool Quoted { get; set; } = false;
    public bool IgnoreOps { get; set; } = false;
    public int MaxDepth { get; set; } = -1;
    public bool NumberVars { get; set; } = false;
    public bool PortrayCallback { get; set; } = false;
}
```

---

## TermHandle

A persistent reference to a term that outlives its original scope.

### Properties

```csharp
public Term Term { get; }                        // re-projected each access
public PrologEngine Engine { get; }
public bool IsValid { get; }                     // false after Dispose
```

### Methods

```csharp
public void Dispose();                           // releases the underlying reference
```

**Example**:
```csharp
TermHandle? cached;

using (var query = engine.Query("compute_result(X)"))
{
    var sol = query.OnlySolution();
    cached = engine.MakeHandle(sol!["X"]);  // can outlive the query
}

// Later:
Console.WriteLine(cached.Term.AsInt);
cached.Dispose();
```

---

## Query

A running goal that may produce multiple solutions.

### Iteration

```csharp
public IEnumerable<Solution> Solutions();
public IAsyncEnumerable<Solution> SolutionsAsync(CancellationToken ct = default);
```

### Convenience methods

```csharp
public Solution? FirstSolution();
public Solution? OnlySolution();    // returns null if 0 or >1 solutions
public bool HasSolution();
public int CountSolutions();         // careful with infinite queries

public T? FirstResult<T>(string varName);
public T FirstResultOrThrow<T>(string varName);
public IEnumerable<T> Results<T>(string varName);
public async IAsyncEnumerable<T> ResultsAsync<T>(string varName, CancellationToken ct = default);
```

`FirstResult<T>` shortcuts the common case: take the first solution's binding for the given variable and convert to type T.

### Disposal

```csharp
public void Dispose();
```

Disposing a Query discards its active choice points. Subsequent calls to `Solutions()` after `Dispose()` throw `ObjectDisposedException`.

---

## Solution

A snapshot of variable bindings for a single solution.

### Access

```csharp
public Term this[string varName] { get; }
public bool TryGet(string varName, out Term value);
public IReadOnlyDictionary<string, Term> Bindings { get; }
```

### Conversion

```csharp
public T Get<T>(string varName);
public bool TryGet<T>(string varName, out T value);
```

The binding terms are valid until the next iteration step on the parent Query. For longer retention, use `engine.MakeHandle(sol["X"])`.

---

## EnginePool

A pool of engines for server scenarios.

### Constructors

```csharp
public EnginePool(int initialCount, Func<PrologEngine> factory);
public EnginePool(int initialCount, int maxCount, Func<PrologEngine> factory);
```

`factory` is called to create a new engine when the pool needs one. It's called once per engine creation (typically loading bundles or registering foreign predicates).

### Methods

```csharp
public PooledEngine Rent();
public Task<PooledEngine> RentAsync(CancellationToken ct = default);
public void Dispose();
public int AvailableCount { get; }
public int TotalCount { get; }
public PoolStatistics GetStatistics();
```

`Rent` blocks until an engine is available (or creates a new one if under maxCount). Use `using` for automatic return.

`RentAsync` does the same but awaitable.

**Example**:
```csharp
var pool = new EnginePool(10, 100, () =>
{
    var e = new PrologEngine();
    e.LoadBundle("rules.shum");
    return e;
});

async Task ProcessRequest(Request req)
{
    using var pooled = await pool.RentAsync();
    using var query = pooled.Engine.Query("process(@input, X)", new Dictionary<string, Term>
    {
        ["input"] = pooled.Engine.MakeString(req.Input)
    });
    return query.FirstResult<string>("X");
}
```

---

## PooledEngine

```csharp
public class PooledEngine : IDisposable
{
    public PrologEngine Engine { get; }
    public void Dispose();      // returns the engine to the pool
}
```

---

## Foreign predicate types

```csharp
public delegate ForeignResult ForeignPredicate(ForeignContext context);

public enum ForeignResult
{
    Success,
    Failure,
    SuccessWithChoice,
}

public class ForeignContext
{
    public PrologEngine Engine { get; }
    public int Arity { get; }
    public Term GetArg(int index);
    public Term GetRawArg(int index);
    public bool Unify(int argIndex, Term value);
    public bool Unify(Term a, Term b);
    public Term MakeAtom(string name);
    public Term MakeInt(long value);
    public Term MakeCompound(string functor, params Term[] args);
    public Term MakeList(IEnumerable<Term> elements);
    public object? State { get; set; }
    public bool IsFirstCall { get; }
    public bool IsRedo { get; }
    public bool IsCleanup { get; }
    public ForeignResult ThrowError(Term errorTerm);
    public ForeignResult ThrowTypeError(string expectedType, Term got);
    public ForeignResult ThrowDomainError(string expectedDomain, Term got);
    public ForeignResult ThrowInstantiationError();
    public ForeignResult ThrowExistenceError(string objectType, Term obj);
}
```

See `foreign-predicates.md` for usage patterns.

---

## Attributes

```csharp
[AttributeUsage(AttributeTargets.Method)]
public class PrologPredicateAttribute : Attribute
{
    public string Name { get; }
    public int Arity { get; }
    public string? Module { get; set; }
    public Visibility Visibility { get; set; } = Visibility.Public;
    public bool IsDynamic { get; set; } = false;
    
    public PrologPredicateAttribute(string name, int arity);
}

[AttributeUsage(AttributeTargets.Method)]
public class PrologModeAttribute : Attribute
{
    public string ModePattern { get; }
    public Determinism Determinism { get; }
    
    public PrologModeAttribute(string modePattern, Determinism determinism = Determinism.NoneDeclared);
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public class PrologTermAttribute : Attribute
{
    public string Functor { get; }
    
    public PrologTermAttribute(string functor);
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class PrologFieldAttribute : Attribute
{
    public int Index { get; set; } = -1;     // -1 = auto-detect by declaration order
    public bool Optional { get; set; }
    public string? Name { get; set; }         // for named fields in dictionaries
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class PrologStringAsAttribute : Attribute
{
    public StringKind Kind { get; }
    public PrologStringAsAttribute(StringKind kind);
}

public enum StringKind { Atom, String, Pstr, Codes, Chars }
```

---

## Enumerations

```csharp
public enum TermKind
{
    Variable, Atom, Integer, BigInteger, Float, String, Pstr, 
    Compound, List, EmptyList, Foreign
}

public enum Visibility { Local, Public }

public enum Determinism { Det, SemiDet, Multi, NonDet, NoneDeclared }

public enum DoubleQuotesMode { Codes, Chars, Atom, Pstr, String }

public enum DebugLevel { None, Basic, Full }

public enum CompilationStrategy { Interpreted, Tiered, EagerIl }

public enum UnknownPredicateMode { Error, Warning, Fail }
```

---

## EngineConfig

```csharp
public class EngineConfig
{
    public int InitialHeapSize { get; set; } = 65536;
    public int MaxHeapSize { get; set; } = 0;
    public int InitialStackSize { get; set; } = 8192;
    public int MaxStackSize { get; set; } = 0;
    public int InitialBindingTrailSize { get; set; } = 1024;
    public int InitialExtraTrailSize { get; set; } = 64;
    public int InitialRegisterCount { get; set; } = 64;
    
    public DoubleQuotesMode DoubleQuotes { get; set; } = DoubleQuotesMode.Codes;
    public bool StrictDynamicDeclarations { get; set; } = false;
    public UnknownPredicateMode UnknownPredicate { get; set; } = UnknownPredicateMode.Error;
    public DebugLevel DebugLevel { get; set; } = DebugLevel.Basic;
    public CompilationStrategy Compilation { get; set; } = CompilationStrategy.Tiered;
    
    public int Tier1PromotionThreshold { get; set; } = 1000;
    public int SafePointInstructionInterval { get; set; } = 1024;
    public int AtomGcThreshold { get; set; } = 100_000;
    public TimeSpan AtomGcInterval { get; set; } = TimeSpan.FromMinutes(5);
    public bool AtomGcAfterEachQuery { get; set; } = false;
    public TimeSpan AtomGcTimeout { get; set; } = TimeSpan.FromSeconds(30);
    
    public bool CollectCallSiteStats { get; set; } = false;
    public bool CollectPredicateStats { get; set; } = false;
    
    public ILogger? Logger { get; set; }
}
```

---

## EngineFlags

```csharp
public class EngineFlags
{
    public DoubleQuotesMode DoubleQuotes { get; set; }
    public UnknownPredicateMode Unknown { get; set; }
    public bool BoundedIntegers { get; }   // always false in Shumway
    public int MinTaggedInteger { get; }   // computed from cell layout
    public int MaxTaggedInteger { get; }
    public bool OccursCheck { get; set; }
}
```

---

## Results and reports

```csharp
public class LoadResult
{
    public bool Success { get; }
    public string ModuleName { get; }
    public IReadOnlyList<LoadError> Errors { get; }
    public IReadOnlyList<LoadWarning> Warnings { get; }
}

public class LoadError
{
    public string File { get; }
    public int Line { get; }
    public int Column { get; }
    public string Message { get; }
    public LoadErrorKind Kind { get; }
}

public enum LoadErrorKind { SyntaxError, SemanticError, UnresolvedReference, Conflict }

public class LoadWarning
{
    public string File { get; }
    public int Line { get; }
    public int Column { get; }
    public string Message { get; }
    public LoadWarningKind Kind { get; }
}
```

---

## Exceptions

```csharp
public abstract class PrologException : Exception
{
    public Term? PrologError { get; }
    public IReadOnlyList<StackFrame>? StackTrace { get; }
}

public class PrologSyntaxException : PrologException { }
public class PrologExistenceException : PrologException { }
public class PrologTypeException : PrologException { }
public class PrologDomainException : PrologException { }
public class PrologEvaluationException : PrologException { }
public class PrologPermissionException : PrologException { }
public class PrologRepresentationException : PrologException { }
public class PrologRuntimeException : PrologException { }
public class PrologResourceException : PrologException { }
public class TermConversionException : PrologException { }
public class BundleFormatException : Exception { }
public class MissingConverterException : Exception { }
public class ModeViolationException : Exception { }
```

---

## Module info

```csharp
public class ModuleInfo
{
    public string Name { get; }
    public string? SourceFile { get; }
    public IReadOnlyList<string> PublicPredicates { get; }  // "name/arity" strings
    public IReadOnlyList<string> LocalPredicates { get; }
    public IReadOnlyList<string> DynamicPredicates { get; }
}
```

---

## Statistics

```csharp
public class EngineStatistics
{
    public long QueriesExecuted { get; }
    public long SolutionsProduced { get; }
    public long AtomsCreated { get; }
    public long AtomGcRuns { get; }
    public long HeapHighWaterMark { get; }
    public long StackHighWaterMark { get; }
    public long Tier1Compilations { get; }
}

public partial class PrologEngine
{
    public EngineStatistics GetStatistics();
}
```

---

## Usage patterns

### Simple query

```csharp
using var engine = new PrologEngine();
engine.Consult("rules.pl");

using var query = engine.Query("solve(input, X)");
foreach (var sol in query.Solutions())
    Console.WriteLine(sol["X"]);
```

### Async server

```csharp
var pool = new EnginePool(10, () => 
{
    var e = new PrologEngine();
    e.LoadBundle("app.shum");
    return e;
});

async Task<string> ProcessAsync(string input, CancellationToken ct)
{
    using var pooled = await pool.RentAsync(ct);
    using var query = pooled.Engine.Query("process(@in, @out)", new()
    {
        ["in"] = pooled.Engine.MakeString(input)
    });
    
    await foreach (var sol in query.SolutionsAsync(ct))
    {
        return sol.Get<string>("out");
    }
    
    throw new InvalidOperationException("No solution");
}
```

### Foreign predicates with attributes

```csharp
public class FileOps
{
    [PrologPredicate("read_file", 2)]
    [PrologMode("+, -", Determinism.Det)]
    public string ReadFile(string path) => File.ReadAllText(path);
    
    [PrologPredicate("write_file", 2)]
    public void WriteFile(string path, string content) => 
        File.WriteAllText(path, content);
}

engine.RegisterPredicate(new FileOps());
```

### Struct mapping

```csharp
[PrologTerm("person")]
public partial class Person
{
    public string Name;
    public int Age;
    public List<string> Hobbies;
}

// In Prolog: person('Alice', 30, [reading, hiking])

using var query = engine.Query("get_person(P)");
foreach (var sol in query.Solutions())
{
    Person p = engine.FromTerm<Person>(sol["P"]);
    Console.WriteLine($"{p.Name}, {p.Age}");
}
```

---

## See also

- ADR-010 (Embedding API): high-level rationale.
- `foreign-predicates.md`: detailed patterns for foreign predicates.
- `builtins-catalog.md`: list of available Prolog predicates.
- The `samples/` directory in the repository: worked examples.
