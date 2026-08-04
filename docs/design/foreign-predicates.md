# Foreign Predicates: Implementation Patterns

> **Early design — this describes a pre-rename model that did not ship.** The
> real mechanism: annotate a method with `[PrologPredicate("name/arity")]` and
> register it via `engine.RegisterPredicates(instance | typeof | <T>())`. A
> method takes a `Shumway.Core.Activation` and returns `bool` (reading arguments
> with `engine.GetRegister(0..arity-1)`), or uses typed parameters the source
> generator decodes; non-determinism is `NonDeterministic = true` + an
> `IEnumerable<T>` return; `out` / `ref` parameters map to `-` / `?` modes;
> errors are thrown as `PrologRuntimeException`. There is **no**
> `ForeignPredicate` delegate, `ForeignResult` enum, `ForeignContext`, or
> `[PrologMode]` attribute. See the [user guide](../guide/user-guide.md) and
> `src/Shumway.Embedding/PrologPredicateAttribute.cs`. Read the patterns below as
> historical design.

This document specifies in detail how foreign predicates work in Shumway: registration, invocation, argument access, unification, non-determinism, exceptions, and threading model.

A "foreign predicate" is a Prolog predicate whose body is implemented in C# (or any other .NET language) rather than in Prolog. Foreign predicates extend Prolog with native operations: file I/O, network access, database calls, custom data manipulation, calls to other .NET libraries.

## Registration patterns

### Pattern 1: Direct delegate

For one-off predicates or small sets:

```csharp
engine.RegisterPredicate("get_current_time", 1, ctx =>
{
    var time = ctx.Engine.MakeInt(DateTime.UtcNow.Ticks);
    return ctx.Unify(0, time) ? ForeignResult.Success : ForeignResult.Failure;
});
```

Signature:

```csharp
public delegate ForeignResult ForeignPredicate(ForeignContext context);

engine.RegisterPredicate(string functorName, int arity, ForeignPredicate predicate);
```

The predicate is registered as a public predicate in the **system pseudo-module**. It's callable from any Prolog module.

To register as a local predicate of a specific module:

```csharp
engine.RegisterPredicate("get_current_time", 1, ctx => { /* ... */ }, 
    module: "system_utils", visibility: Visibility.Local);
```

### Pattern 2: Class with attributes

For grouping related predicates:

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
    [PrologMode("+, +, -", Determinism.Det)]
    public ForeignResult Gcd(ForeignContext ctx)
    {
        long a = ctx.GetArg(0).AsInt;
        long b = ctx.GetArg(1).AsInt;
        return ctx.Unify(2, ctx.Engine.MakeInt(BigInteger.GreatestCommonDivisor(a, b)))
            ? ForeignResult.Success 
            : ForeignResult.Failure;
    }
}

engine.RegisterPredicate(new MathPredicates());
```

The `[PrologMode]` attribute provides mode declarations for the predicate. They are stored as metadata (used by linter; in Phase 3, used for code generation).

The `[PrologPredicate]` attribute syntax:

```csharp
[PrologPredicate(name, arity, options...)]
```

Options:
- `name` (string): the Prolog functor name. Required.
- `arity` (int): the arity. Required.
- `Module = "..."`: register in a specific module (default: system).
- `Visibility = Visibility.Local | Visibility.Public`: default: Public.
- `IsDynamic = true | false`: whether assertz/retract can modify (default: false; foreign preds are static).

### Pattern 3: Strongly-typed predicates with conversion

For predicates that work with .NET types and don't need direct term manipulation:

```csharp
public class StringPredicates
{
    [PrologPredicate("uppercase", 2)]
    public string Uppercase(string input)
    {
        return input.ToUpper();
    }
    
    [PrologPredicate("string_length", 2)]
    public int StringLength(string s) => s.Length;
    
    [PrologPredicate("starts_with", 2)]
    public bool StartsWith(string s, string prefix) => s.StartsWith(prefix);
}
```

Conventions:

- The first arguments (whose .NET types are bound) are mapped to Prolog inputs (`+` mode).
- The return value is unified with the last Prolog argument (`-` mode).
- `bool` return type means semidet: `true` succeeds, `false` fails. There is no Prolog argument for the bool.
- Multiple return values can be expressed as a tuple type that converts to a compound term.

The source generator emits wrappers that:

1. Convert `Term` arguments to .NET types using registered converters.
2. Call the C# method.
3. Convert the return value back to a `Term`.
4. Unify with the appropriate argument.

This pattern is the most ergonomic for typical use cases.

## ForeignContext API

```csharp
public class ForeignContext
{
    // The owning engine.
    public PrologEngine Engine { get; }
    
    // The arity of the predicate being called.
    public int Arity { get; }
    
    // Read the i-th argument (0-indexed). Returns the dereferenced term.
    public Term GetArg(int index);
    
    // Read the i-th argument without dereferencing (for inspection of vars).
    public Term GetRawArg(int index);
    
    // Unify the i-th argument with a term.
    public bool Unify(int argIndex, Term value);
    
    // Unify two terms (general-purpose).
    public bool Unify(Term a, Term b);
    
    // Term construction (delegates to Engine, included for convenience).
    public Term MakeAtom(string name);
    public Term MakeInt(long value);
    public Term MakeCompound(string functor, params Term[] args);
    public Term MakeList(IEnumerable<Term> elements);
    // ... etc.
    
    // For non-deterministic predicates: state across calls.
    public object? State { get; set; }
    public bool IsFirstCall { get; }
    public bool IsRedo { get; }       // true if called after a backtrack
    public bool IsCleanup { get; }    // true if called for cleanup (e.g., on cut)
    
    // Throw a Prolog error from foreign code.
    public ForeignResult ThrowError(Term errorTerm);
    public ForeignResult ThrowTypeError(string expectedType, Term got);
    public ForeignResult ThrowDomainError(string expectedDomain, Term got);
    public ForeignResult ThrowInstantiationError();
    public ForeignResult ThrowExistenceError(string objectType, Term obj);
}
```

## Deterministic predicates

A deterministic predicate returns `ForeignResult.Success` or `ForeignResult.Failure` on a single call. The engine does not create a choice point.

```csharp
[PrologPredicate("atom_to_int", 2)]
public ForeignResult AtomToInt(ForeignContext ctx)
{
    var atom = ctx.GetArg(0);
    if (!atom.IsAtom)
        return ctx.ThrowTypeError("atom", atom);
    
    if (!long.TryParse(atom.AsAtom, out long value))
        return ForeignResult.Failure;
    
    return ctx.Unify(1, ctx.Engine.MakeInt(value)) 
        ? ForeignResult.Success 
        : ForeignResult.Failure;
}
```

## Non-deterministic predicates

A non-deterministic predicate may produce multiple solutions. It uses `ForeignResult.SuccessWithChoice` to indicate that more solutions are available.

```csharp
[PrologPredicate("range", 3)]
public ForeignResult Range(ForeignContext ctx)
{
    // range(+Low, +High, ?X): X is each integer in [Low, High]
    
    if (ctx.IsFirstCall)
    {
        long low = ctx.GetArg(0).AsInt;
        long high = ctx.GetArg(1).AsInt;
        ctx.State = new RangeState { Current = low, High = high };
    }
    
    var state = (RangeState)ctx.State!;
    
    if (state.Current > state.High)
        return ForeignResult.Failure;
    
    if (!ctx.Unify(2, ctx.Engine.MakeInt(state.Current)))
    {
        // unification failed; try the next value
        state.Current++;
        return Range(ctx);  // tail call to retry
    }
    
    state.Current++;
    
    if (state.Current > state.High)
        return ForeignResult.Success;       // last solution
    return ForeignResult.SuccessWithChoice;  // more solutions available
}

private class RangeState
{
    public long Current;
    public long High;
}
```

When `SuccessWithChoice` is returned:

1. The engine creates a choice point that records the foreign predicate's state.
2. On backtrack, the engine calls the foreign predicate again with `IsRedo = true` and the same `State`.
3. The predicate computes the next solution.

The `State` object is **engine-managed**: it's stored in the choice point. When the choice point is discarded (by `trust_me` or cut), the state is released. If `State` implements `IDisposable`, the engine calls `Dispose()` on cleanup.

### Cleanup phase

If a foreign predicate has resources (file handles, database connections) that need cleanup, it can implement cleanup logic:

```csharp
[PrologPredicate("read_file_line", 2)]
public ForeignResult ReadFileLine(ForeignContext ctx)
{
    if (ctx.IsFirstCall)
    {
        string path = ctx.GetArg(0).AsAtom;
        var reader = new StreamReader(path);
        ctx.State = reader;
    }
    
    if (ctx.IsCleanup)
    {
        var reader = (StreamReader)ctx.State!;
        reader.Dispose();
        return ForeignResult.Success;  // cleanup result is ignored
    }
    
    var theReader = (StreamReader)ctx.State!;
    string? line = theReader.ReadLine();
    
    if (line == null)
    {
        theReader.Dispose();
        return ForeignResult.Failure;
    }
    
    if (!ctx.Unify(1, ctx.Engine.MakeString(line)))
        return ForeignResult.Failure;
    
    if (theReader.EndOfStream)
    {
        theReader.Dispose();
        return ForeignResult.Success;
    }
    return ForeignResult.SuccessWithChoice;
}
```

The engine guarantees:

- After `SuccessWithChoice`, the predicate will be called again with `IsRedo = true` (on backtrack) OR with `IsCleanup = true` (if cut discards the choice point).
- Exactly one of `IsRedo` or `IsCleanup` will be set on a follow-up call.
- After `Success` or `Failure`, no further calls occur (the predicate's choice point, if any, was discarded).

## Argument access patterns

### Reading bound arguments

```csharp
var arg = ctx.GetArg(0);  // dereferenced

// Type-check before extraction
if (!arg.IsAtom)
    return ctx.ThrowTypeError("atom", arg);

string name = arg.AsAtom;
```

The `As*` getters throw `InvalidCastException` on type mismatch. For robust handling, use `TryGet*`:

```csharp
if (!arg.TryGetInt(out long value))
    return ctx.ThrowTypeError("integer", arg);
```

### Inspecting variables

```csharp
var arg = ctx.GetRawArg(0);   // no deref
if (arg.IsVariable)
{
    // The argument is an unbound variable; predicate must bind it.
}
```

For predicates that work in multiple modes (output mode):

```csharp
[PrologPredicate("length_pred", 2)]
public ForeignResult Length(ForeignContext ctx)
{
    var listArg = ctx.GetArg(0);
    var lengthArg = ctx.GetRawArg(1);
    
    if (lengthArg.IsVariable)
    {
        // Mode (+, -): compute length
        int count = listArg.EnumerateList().Count();
        return ctx.Unify(1, ctx.Engine.MakeInt(count)) 
            ? ForeignResult.Success 
            : ForeignResult.Failure;
    }
    else
    {
        // Mode (+, +): verify length
        int expected = (int)lengthArg.AsInt;
        int actual = listArg.EnumerateList().Count();
        return expected == actual ? ForeignResult.Success : ForeignResult.Failure;
    }
}
```

### Building results

```csharp
var result = ctx.Engine.MakeCompound("point",
    ctx.Engine.MakeInt(x),
    ctx.Engine.MakeInt(y));

return ctx.Unify(2, result) ? ForeignResult.Success : ForeignResult.Failure;
```

For list construction:

```csharp
var elements = new List<Term>();
foreach (var item in mySource)
    elements.Add(ctx.Engine.MakeAtom(item));

var listTerm = ctx.Engine.MakeList(elements);
return ctx.Unify(0, listTerm) ? ForeignResult.Success : ForeignResult.Failure;
```

For incremental building (when constructing large lists):

```csharp
var builder = ctx.Engine.NewListBuilder();
foreach (var item in mySource)
    builder.Add(ctx.Engine.MakeAtom(item));
var listTerm = builder.Build();
```

## Exceptions

### Throwing Prolog errors from foreign code

Use the `ctx.Throw*` methods:

```csharp
return ctx.ThrowTypeError("integer", arg);
return ctx.ThrowDomainError("positive_integer", arg);
return ctx.ThrowInstantiationError();
return ctx.ThrowExistenceError("file", pathArg);

// Custom error term:
var customError = ctx.Engine.MakeCompound("my_error",
    ctx.Engine.MakeAtom("description"),
    ctx.Engine.MakeInt(errorCode));
return ctx.ThrowError(customError);
```

These set up the exception in the engine and return a sentinel result. The engine then unwinds to the nearest `catch/3`.

### .NET exceptions from foreign code

If a foreign predicate throws a .NET exception that it doesn't handle:

```csharp
[PrologPredicate("read_file", 2)]
public ForeignResult ReadFile(ForeignContext ctx)
{
    string path = ctx.GetArg(0).AsAtom;
    string content = File.ReadAllText(path);  // may throw IOException
    return ctx.Unify(1, ctx.Engine.MakeString(content)) 
        ? ForeignResult.Success 
        : ForeignResult.Failure;
}
```

The engine catches the exception and converts it to a Prolog error term:

```
error(
    system_error(io_error, "File not found: foo.txt"),
    context(read_file/2, _)
)
```

Prolog's `catch/3` can then catch it. If uncaught, it propagates back to .NET as `PrologRuntimeException` with the original .NET exception as `InnerException`.

For finer control, foreign predicates can catch .NET exceptions and convert them explicitly:

```csharp
try
{
    string content = File.ReadAllText(path);
    // ...
}
catch (FileNotFoundException)
{
    return ctx.ThrowExistenceError("file", ctx.Engine.MakeAtom(path));
}
catch (UnauthorizedAccessException)
{
    return ctx.ThrowError(ctx.Engine.MakeCompound("permission_error",
        ctx.Engine.MakeAtom("read"),
        ctx.Engine.MakeAtom("file"),
        ctx.Engine.MakeAtom(path)));
}
```

## Threading model

Foreign predicates are called **synchronously** on the engine's current thread. Since engines are single-threaded (per ADR-001), there's no concurrency to worry about within a foreign predicate call.

**Long-running foreign predicates** block the engine. The caller (in the .NET host) is also blocked until the predicate returns. For truly long operations, the foreign predicate should:

1. Spawn its own thread/task to do the work.
2. Block (with cancellation support) on the result.
3. Return when complete or cancelled.

```csharp
[PrologPredicate("http_get", 2)]
public ForeignResult HttpGet(ForeignContext ctx)
{
    string url = ctx.GetArg(0).AsAtom;
    
    // Synchronous wait on the async operation
    string body;
    try
    {
        body = _httpClient.GetStringAsync(url, ctx.Engine.CancellationToken)
            .GetAwaiter().GetResult();
    }
    catch (HttpRequestException ex)
    {
        return ctx.ThrowError(/* ... */);
    }
    
    return ctx.Unify(1, ctx.Engine.MakeString(body)) 
        ? ForeignResult.Success 
        : ForeignResult.Failure;
}
```

The engine's `CancellationToken` is available via `ctx.Engine.CancellationToken`. It's tied to the query's cancellation: if the user cancels the async query, the token is signaled, and the foreign predicate can abort.

## Reentry from foreign code

A foreign predicate can call back into Prolog (executing a goal):

```csharp
[PrologPredicate("for_each_solution", 2)]
public ForeignResult ForEachSolution(ForeignContext ctx)
{
    var goal = ctx.GetArg(0);
    var action = ctx.GetArg(1);
    
    foreach (var sol in ctx.Engine.Query(goal).Solutions())
    {
        // Bind action's variables and call it
        // ...
    }
    
    return ForeignResult.Success;
}
```

Reentry is supported but should be used carefully:

- The reentered query uses the same engine, so its choice points interact with the calling predicate's state.
- Recursive reentry is allowed, but stack/heap consumption multiplies.
- The reentered query inherits the cancellation token.

## Source generators

The source generator processes `[PrologPredicate]` attributes and emits:

1. Registration code (when `engine.RegisterPredicate(instance)` is called).
2. Wrapper methods that adapt the strongly-typed C# methods to the `ForeignContext`-based signature.
3. Conversion code using the registered converters and built-in conversions for common types.

Example: for the `Uppercase(string) → string` method, the generator emits:

```csharp
public static ForeignResult __Wrapper_Uppercase(ForeignContext ctx)
{
    Term arg0 = ctx.GetArg(0);
    if (!arg0.TryGetAtom(out string input) && !arg0.TryGetString(out input))
        return ctx.ThrowTypeError("atom_or_string", arg0);
    
    string result;
    try
    {
        result = _instance.Uppercase(input);
    }
    catch (Exception ex)
    {
        return ctx.ThrowError(ConvertException(ex, ctx.Engine));
    }
    
    Term resultTerm = ctx.Engine.MakeString(result);
    return ctx.Unify(1, resultTerm) ? ForeignResult.Success : ForeignResult.Failure;
}
```

The user sees the simple method; the generator handles the boilerplate.

## Performance considerations

- **`GetArg`** is O(deref-chain-length); typically O(1) in well-formed programs.
- **`Unify`** is O(structural-size) for compound terms; O(1) for atomics.
- **`MakeCompound`** allocates heap cells: 1 for STR + 1 for FUNCTOR + N for args.
- **`MakeList(IEnumerable)`** is O(length); allocates 2 cells per element.

For high-frequency predicates, prefer:

- Pre-computed `FunctorId`s (cached at registration).
- Builder patterns for list construction.
- Minimizing the number of `Unify` calls (combine into one structural unify when possible).

## Lifetime considerations

Terms obtained via `ctx.GetArg(i)` are valid only during the current foreign predicate call. Storing them in long-lived C# fields and using them later is undefined behavior.

For long-lived references, convert to handles or to .NET-native types:

```csharp
// BAD:
private List<Term> _cachedTerms = new();
public ForeignResult CacheMe(ForeignContext ctx)
{
    _cachedTerms.Add(ctx.GetArg(0));  // term not valid after this call
    return ForeignResult.Success;
}

// GOOD (via handle):
private List<TermHandle> _cachedTerms = new();
public ForeignResult CacheMe(ForeignContext ctx)
{
    _cachedTerms.Add(ctx.Engine.MakeHandle(ctx.GetArg(0)));
    return ForeignResult.Success;
}

// GOOD (convert to managed):
private List<string> _cachedStrings = new();
public ForeignResult CacheMe(ForeignContext ctx)
{
    if (ctx.GetArg(0).IsAtom)
        _cachedStrings.Add(ctx.GetArg(0).AsAtom);
    return ForeignResult.Success;
}
```

## Testing foreign predicates

```csharp
[Fact]
public void Uppercase_ConvertsToUppercase()
{
    using var engine = new PrologEngine();
    engine.RegisterPredicate(new StringPredicates());
    
    using var query = engine.Query("uppercase(hello, X)");
    var sol = query.OnlySolution();
    
    Assert.NotNull(sol);
    Assert.Equal("HELLO", sol!["X"].AsString);
}
```

The standard xUnit pattern works directly with the embedding API.

## See also

- ADR-010 (Embedding API): high-level API design.
- `builtins-catalog.md`: list of foreign-implemented builtins.
- `api-reference.md`: complete API documentation.
