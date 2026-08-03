# Phase 21 — Closure

**Status**: complete.

**Tagged**: `phase-21`.

Phase 21 is the **C# integration** phase: the bulk of ADR-010's
embedding-API surface that hadn't been built yet, plus the two
ISO-standard predicates (`consult/1` / `reconsult/1`) the REPL's
local file-loading helper had been hiding from embedders. The
phase spans chunks 235–245.

By the end of the phase, the typical C#-embeddable Prolog program
looks like this — and every piece below is type-checked end to
end, with no manual register / heap manipulation:

```csharp
[PrologTerm("person")]
public partial record Person(string Name, int Age);

public partial class Db
{
    [PrologPredicate("c#sharp_query/1", NonDeterministic = true)]
    public IEnumerable<Person> Query()
    {
        using var conn = _connectionPool.Rent();
        foreach (var row in conn.SelectPeople())
            yield return new Person(row.Name, row.Age);
    }
}

var engine = new PrologEngine();
engine.RegisterPredicates(new Db());
engine.ConsultFile("rules.pl");

foreach (var p in engine.Query<Person>("query(P), legal_adult(P).", "P"))
    Console.WriteLine($"{p.Name} ({p.Age})");
```

The chunks divide into three threads:

1. **Loading + lifecycle** — `consult/1`, `reconsult/1`, `PrologEngine.ConsultFile`, `PrologEngine.ReconsultFile` (chunks 235–236).
2. **Foreign predicates** — `[PrologPredicate]` registration, typed signatures, non-determinism, cleanup (chunks 237, 242, 244, 245).
3. **Term conversion** — scalar / composite / convention dispatcher, typed `Solution.Get<T>` / `engine.Query<T>`, `[PrologTerm]` + Roslyn source generator (chunks 238–241, 243).

## Loading + lifecycle (chunks 235–236)

The REPL's local `ConsultFile` helper got promoted to a public
embedding-API method, and the two builtins were registered with
classical Prolog semantics.

- **Chunk 235** — `PrologEngine.ConsultFile(path)` routes by
  extension: `.shum` through `LoadBundle`, everything else
  through `ConsultString`. `consult/1` and `reconsult/1`
  builtins as thin wrappers. Original `reconsult/1` was
  registered as a synonym for `consult/1`.
- **Chunk 236** — fix: classical `reconsult/1` actually abolishes
  predicates defined in the file before loading (so an
  edit-reload cycle replaces rather than duplicates), while
  leaving every other predicate alone. The user flagged the
  original semantics during review. The fix covers both `.pl`
  and `.shum` paths and reuses the bundle's per-predicate
  `Defined` visibility list for the bundle case.

## Foreign predicates (chunks 237, 242, 244, 245)

The foreign-predicate API grew from "raw `bool(Engine)` only" to
declarative typed signatures with full non-determinism support,
all behind a single attribute.

- **Chunk 237** — `[PrologPredicate]` attribute, three
  `RegisterPredicates` overloads (object instance, Type, generic),
  collision detection. Initially required the raw
  `bool Method(Engine)` signature. The follow-up commit changed
  the explicit-name constructor to take the canonical
  `Name/Arity` indicator string (`[PrologPredicate("distance/3")]`)
  matching every other Prolog name notation. The `(int arity)`
  overload (uses C# method name) survived.
- **Chunk 242** — typed signatures. The generator emits a bridge
  `_{Method}_PrologBridge(Engine)` that decodes typed input args
  via `FromTerm<T>`, calls the user method, and encodes the
  return value via `ToTerm<T>` if non-`void` / non-`bool`. Return
  shape rules: `void` always succeeds, `bool` is success/failure
  literal, `T` becomes the last argument's unification target. An
  `Engine` parameter (any position) is allowed and threaded
  through verbatim. Nested types emit their full `partial Outer
  { partial Inner { ... } }` hierarchy.
- **Chunk 244** — non-determinism opt-in: `NonDeterministic =
  true` + `IEnumerable<T>` return turns the method into a
  Prolog generator. Reuses `Engine.PushBuiltinChoicePoint` (the
  chunk-56 IL CP mechanism) — no new CP type, no new
  dispatcher. Bridge opens an `IEnumerator<T>` on first call;
  on each `MoveNext`-success pushes a re-arming CP whose
  delegate calls back into the advance helper; on
  `MoveNext`-false `Dispose`s and fails. Initially documented
  the cut-pruned cleanup as a v1 limitation.
- **Chunk 245** — closes the limitation: `IlChoicePointEntry`
  gains an optional `Action? OnPrune` callback fired in
  `Engine.Cut` (and only there — `PopIlChoicePointAndRestore`
  deliberately doesn't, since backtrack-into runs the
  delegate). Non-det bridge supplies `iter.Dispose` as
  `OnPrune` for deterministic cleanup on `!` and `->`.

The end-state foreign-predicate signature space:

| Signature                                   | Bridge | Behaviour |
|---------------------------------------------|--------|-----------|
| `bool M(Engine)`                            | none   | direct register manipulation |
| `void M(Engine?, T1, ..., Tn)`              | yes    | always succeeds |
| `bool M(Engine?, T1, ..., Tn)`              | yes    | literal success / failure |
| `R M(Engine?, T1, ..., Tn)`                 | yes    | unify R as last arg |
| `IEnumerable<R> M(Engine?, T1, ..., Tn)` + `NonDeterministic = true` | yes | non-det generator over R |

## Term conversion (chunks 238–241, 243)

The conversion subsystem grew in four tiers, each falling through
to the next when no match: user converters → built-in scalars →
composites → convention discovery (source-generated). Every tier
goes through the same `engine.ToTerm<T>` / `FromTerm<T>` entry
points, so composition is uniform (a `List<Point>` where
`Point` is a `[PrologTerm]` record just works).

- **Chunk 238** — foundation: `engine.RegisterConverter<T>`,
  `ToTerm<T>` / `FromTerm<T>`, `Solution.Get<T>` /
  `TryGet<T>`. Built-in scalars: `int / long / short / byte /
  uint / ulong / double / float / bool / char / string /
  BigInteger`, plus a `Term`-subclass passthrough. Dispatch is
  `typeof(T)` equality so each generic instantiation
  JIT-specialises to a single branch — no boxing on primitive
  paths.
- **Chunk 239** — composites: `T[]` / `List<T>` /
  `IEnumerable<T>` / `IReadOnlyList<T>` / etc. ↔ Prolog cons
  list; `Tuple<,>` / `ValueTuple<,>` / `KeyValuePair<,>` ↔
  `-(A, B)` pair; `Nullable<T>` ↔ `none` / `some(T)`;
  `Dictionary<K,V>` ↔ list of `K-V`. Reflective recursion via
  `ToTermDynamic(Type, value)` / `FromTermDynamic(Type, term)`,
  with a per-type `ConcurrentDictionary` cache for the
  per-element-type delegate (paid once, dict-probe + invoke
  per element thereafter).
- **Chunk 240** — typed queries: `engine.Query<T>(text)`
  auto-detects the query's single non-anonymous variable;
  `engine.Query<T>(text, varName)` is the explicit form;
  `engine.QueryFirst<T>(...)` for single-solution use. All three
  project each solution's binding through `FromTerm<T>`.
- **Chunk 241** — `[PrologTerm]` + Roslyn source generator.
  New `Shumway.SourceGen` project (netstandard2.0 — Roslyn's
  fixed ALC requirement). `PrologTermGenerator` emits a
  `partial` extension with `ToPrologTerm(engine)` and a
  paired `static FromPrologTerm(engine, term)` /
  `FromPrologTerm(term)` (the latter for nullary types). The
  runtime `ConventionConverters` tier discovers them via
  reflection and caches the resolved delegates per type.
  Includes the `IsExternalInitPolyfill` netstandard2.0
  records need.
- **Chunk 243** — `[PrologTermIgnore]`: opts a single property
  or field out of the term mapping. One-line filter in the
  generator.

## What the C# integration looks like end-to-end

Every piece composes through the same engine-level
`ToTerm<T>` / `FromTerm<T>` resolution. A typical type
hierarchy that mixes all five tiers:

```csharp
[PrologTerm("address")]                          // chunk 241 (convention tier)
public partial record Address(string Street, int Number);

[PrologTerm("user")]
public partial class User
{
    public string Name { get; set; } = "";       // chunk 238 (scalar tier)
    public Address Home { get; set; } = new("", 0);   // chunk 241 again
    public List<string> Tags { get; set; } = new();   // chunk 239 (composite tier)

    [PrologTermIgnore]                           // chunk 243
    public DateTime LastSeen { get; set; }
}

public partial class Service
{
    [PrologPredicate("c#sharp_users/1", NonDeterministic = true)]  // chunks 242 + 244
    public IEnumerable<User> AllUsers() { /* yield ... */ }
}

var engine = new PrologEngine();
engine.RegisterPredicates(new Service());        // chunk 237

foreach (User u in engine.Query<User>("c#sharp_users(U), tagged(U, vip).", "U"))
    Console.WriteLine($"{u.Name} @ {u.Home.Street}");  // chunk 240
```

## What's not covered (and why)

Items from ADR-010 that were not built in Phase 21:

- **`EnginePool`** (server pool with `Rent` / `RentAsync` /
  `PooledEngine`). Standard pattern — leaves room for the
  embedder to choose a pool flavour (`ObjectPool<T>`, custom
  rate-limited, etc.). Not blocking anything.
- **Async query API** (`IAsyncEnumerable<Solution>` +
  cooperative cancellation via safe points). The synchronous
  `IEnumerable` already covers most use cases; cancellation
  would be the larger lift than the iterator wrapper.
- **`TermHandle`** (`IDisposable` for terms that must outlive
  their producing operation). Shumway's `Term` AST class
  hierarchy is already managed-heap-allocated and survives
  past iteration naturally, so the ADR's "scope-bound term"
  problem doesn't exist in this implementation — no handle
  type needed.
- **Debug term-generation tracking**. Optional in the ADR;
  no concrete demand surfaced.

Items from the foreign-predicate side that could extend chunk
244+245:

- **`ForeignContext` wrapper** with the SWI-style
  `IsFirstCall` / `State` API. The current
  `IEnumerable<T>`-driven approach is more ergonomic for .NET
  consumers; the SWI shape would only be needed for a foreign
  predicate that can't be expressed as a generator.
- **Multi-arg "in/out" mode declarations**. Today the typed
  signature implicitly treats every parameter as input and the
  return value as the single output. A predicate that wants
  multiple outputs has to bundle them in a return tuple. A mode
  declaration system could give finer control.

## Stats

- 11 chunks (235–245), one bonus follow-up commit on chunk 237
  for the `Name/Arity` indicator-form change.
- One new project: `Shumway.SourceGen`.
- 71 new tests in `tests/Shumway.Tests.Embedding/`
  (`Chunk235Tests`–`Chunk245Tests`).
- Full suite at phase close: 1821 embedding + 275 ISO
  conformance + 248 compiler + 105 interpreter + 423 core =
  2872 tests, 0 failures, 3 long-standing skips.
- No engine invariants were modified.

## Files added or restructured

```
src/Shumway.Embedding/
├── PrologTermAttribute.cs          (chunk 241)
├── PrologTermIgnoreAttribute.cs    (chunk 243)
├── PrologPredicateAttribute.cs     (chunk 237; gained NonDeterministic 244)
├── TermConverters.cs               (chunk 238)
├── CompositeConverters.cs          (chunk 239)
├── ConventionConverters.cs         (chunk 241)
├── RegisterMarshalling.cs          (chunk 242)
├── PrologEngine.cs                 (heavily extended across the phase)
├── Solution.cs                     (Get<T> / TryGet<T> + Engine backref, 238)

src/Shumway.SourceGen/               (new project, chunk 241)
├── Shumway.SourceGen.csproj
├── IsExternalInitPolyfill.cs
├── PrologTermGenerator.cs
└── PrologPredicateGenerator.cs     (added chunk 242, NonDet 244)

src/Shumway.Core/Engine.cs
└── IlChoicePointEntry.OnPrune      (chunk 245)
```

## Phase 22 onward

No specific Phase 22 plan committed yet. Open threads if the
next phase wants to continue along the embedding axis:
`EnginePool`, async query API, the `ForeignContext` shape for
predicates that don't fit the `IEnumerable<T>` mold. Or a
return to engine internals — perf, ISO gap-closing, the
`retract/1` Blint bug recorded in memory.
