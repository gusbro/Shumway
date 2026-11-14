# ADR-010: Embedding API

## Status

Accepted ([Phase 1](../../history/phase-1-closure.md)); built in
[Phase 21](../../history/phase-21-closure.md) (foreign predicates, typed
conversion, `[PrologTerm]`) and later phases (pool, async). Rewritten
2026-11-14 to state the API as built: the Phase-1 sketch this ADR used to
carry proposed a different surface, most of which was never built (see *What
the Phase-1 sketch proposed*). Amended by [ADR-047](047-pstr-is-a-list.md)
(2026-08-20): there is no string type at the boundary. Amended 2026-11-14: an
error the program does not catch reaches the host as one exception type,
whatever raised it.

The authoritative member-by-member reference is the
[user guide](../../guide/user-guide.md), the [interop guide](../../guide/interop.md)
and the XML doc-comments of `Shumway.Embedding`; this ADR records the shape of
the API and why.

## Context

Shumway exists primarily to be embedded in .NET applications. The public API is the contract that .NET developers see and use. It must:

1. **Feel idiomatic to .NET developers**: PascalCase methods, properties, generics, async support, IDisposable, IEnumerable patterns.
2. **Minimize the cost of crossing the C# ↔ Prolog boundary**: this is where Shumway can outperform alternatives like GNU Prolog. Conversions, allocations, and indirections at the boundary should be minimal.
3. **Support multiple usage patterns**: single-shot queries, streaming solutions, foreign predicates, struct mapping, engine pooling for servers.
4. **Be type-safe where possible**: leverage C# generics and source generators.
5. **Compose with async**: long-running queries should integrate with `async`/`await` and cancellation.
6. **Match the threading model**: engines are single-threaded internally but thread-agile.

A common failure mode of FFI APIs is that they leak the internal model to the user, making client code feel like a thin wrapper over C functions. Another is that the API is so abstract it adds overhead. Shumway aims for a middle ground: idiomatic .NET, minimal overhead.

The API also intersects with the atom system (ADR-003): atoms exposed to C# code register weak references in the atom table. And with the heap (ADR-002): the cells a query works on belong to that query, so what C# keeps after a call has to be a copy.

## Decision

Everything public lives in `Shumway.Embedding`; terms are the AST classes of
`Shumway.Compiler.Ast`, and the cell-level types are in `Shumway.Core`.

### One engine type

`PrologEngine` is a `sealed class`, not `IDisposable`. It is single-threaded
and thread-agile (ADR-001): one thread at a time, any thread.

- **Loading**: `ConsultString`, `ConsultFile`, `ReconsultFile`,
  `ReconsultString`; `LoadBundle(path | Bundle)` and
  `PrologEngine.FromBundle(...)` for precompiled bundles (ADR-009).
- **Opt-in libraries**: `UseClpfd()`, `UseClpr()`, `UseCoroutining()`,
  `UseReif()`; library directories with `AddLibraryDirectory(dir[, dialect])`.
- **Configuration** is on the engine, not in a configuration object: `Flags`
  (the Prolog flags and the engine's switches) and `IlPromotion` (when and how
  predicates are compiled to Tier-1, ADR-011).

### Two term surfaces

- **Convenience**: a term handed to C# is an AST object (`AtomTerm`,
  `IntTerm`, `FloatTerm`, `BigIntTerm`, `CompoundTerm`, `VarTerm`, …), built
  with its constructors and read by pattern matching. It is an ordinary
  managed object that owns its data: it stays valid after the query moves on
  or ends, so there are no lifetime rules and no handles.
- **Hot path**: a raw foreign predicate, `bool M(Shumway.Core.Activation)`,
  reads and writes the query's `Cell[]` heap directly (`GetRegister`,
  `Deref`, `GetHeap`, `AllocateHeap`, `UnifyRegisterWithCell`; the layout of
  ADR-002 and ADR-017). Nothing is converted or allocated. Measured against an
  engine embedded over P/Invoke, this is where the in-process design wins
  3–30× ([cross-engine comparison](../../benchmarks/cross-engine-comparison.md)).

Text crosses as ADR-047 decides: text as a value is an atom and crosses as
`string`; text as a sequence is a list, packed or not, and crosses as a list.

### Queries

- `Query(string)` returns the first `Solution` (`Success` is false when there
  is none); `QueryAll(string | Term[, CancellationToken])` returns
  `IEnumerable<Solution>`, each `MoveNext` running the goal to its next answer
  by backtracking; `QueryAsync(string, CancellationToken)` returns
  `IAsyncEnumerable<Solution>`.
- Typed: `Query<T>(text[, var])` and `QueryFirst<T>(text[, var])` project a
  binding through the typed conversion below.
- A `Solution` holds `Success`, `IsLast`, `Bindings`, the indexer
  `this[name]` and `Get<T>` / `TryGet<T>`.
- A query ends when its enumerator does: answers exhausted, failure, or the
  caller stopping. The `setup_call_cleanup/3` cleanups still pending run then.
- From inside a foreign predicate, `SolveOnce(activation, goal, …)` runs a
  goal on the live query, re-entrantly, instead of opening a new top-level
  query.

### Foreign predicates

A method annotated `[PrologPredicate("name/arity")]` is registered with
`RegisterPredicates(instance | typeof(T))` or `RegisterPredicates<T>()`; a
conflict with a defined predicate throws.

- **Raw**: `bool M(Activation)`, the hot-path form above.
- **Typed**: ordinary C# parameters and return; the source generator emits the
  bridge that decodes the arguments and encodes the result through the typed
  conversion. A plain parameter is mode `+`, `out T` is `-`, `ref T?` is `?`; a
  `+` argument that arrives unbound raises `instantiation_error`.
- **Non-deterministic**: `NonDeterministic = true` and a return of
  `IEnumerable<T>`; each element is one solution on backtracking.

The mechanism is documented in
[`foreign-predicates.md`](../../design/foreign-predicates.md).

### Typed conversion

`ToTerm<T>`, `FromTerm<T>` and `RegisterConverter<T>(toTerm, fromTerm)` go
through four tiers, each falling through to the next: the user's converters,
the built-in scalars, the composites, and the convention tier.

| .NET type | Prolog term |
|---|---|
| `int`, `long`, `short`, `byte`, `uint`, `ulong` | integer (a big integer past 60 bits) |
| `BigInteger` | integer |
| `double`, `float` | float |
| `string` | atom (ADR-047) |
| `bool` | atom `true` or `false` |
| `char` | atom of one character |
| `T[]`, `List<T>`, `IEnumerable<T>`, `IReadOnlyList<T>` | list |
| `Tuple<A,B>`, `ValueTuple<A,B>`, `KeyValuePair<K,V>` | `A-B` |
| `Dictionary<K,V>` and its interfaces | list of `K-V` |
| `Nullable<T>` | `none` or `some(T)` |
| a `Term` subclass | itself |

The convention tier is `[PrologTerm("name")]` on a `partial` type: the
generator emits `ToPrologTerm` and `FromPrologTerm`, which read and write the
type's term, and `[PrologTermIgnore]` leaves a member out. A type no tier
converts is an `InvalidOperationException`; a term of the wrong shape or
range for the type is an `InvalidCastException` or an `OverflowException`.

### Engine pool

`EnginePool(factory, maxSize, PoolReusePolicy)` or `EnginePool.FromSource(...)`
hands out a `Lease` (`IDisposable`, the engine in `Lease.Activation`) from
`Rent()` or `RentAsync()`. An engine is built by the factory on first need, up
to `maxSize`; past that a rent waits for a lease to be returned. Under
`ReuseState` a returned engine keeps its state for the next renter; under
`FreshEngine` it is discarded and the next rent builds a new one. The pool is
thread-safe, and a leased engine belongs to its renter until the lease is
disposed.

### Async and cancellation

`QueryAsync` runs each step (one `MoveNext`) on a thread-pool thread, so the
caller's thread is free between solutions. One engine is still single-threaded:
the steps never overlap. Concurrency is one engine per thread, or a pool.

A `CancellationToken` passed to `QueryAll` or `QueryAsync` is observed at a
safe point, the next time the heap collector's watermark is crossed, so the
per-goal path pays nothing. The query then throws `OperationCanceledException`,
which `catch/3` does not intercept. A loop that allocates no heap, such as
`repeat, fail`, is not cancellable.

### Exception model

- An error the program does not catch surfaces from a query (`Query`,
  `QueryAll`, `QueryAsync`) or a load (`ConsultString`, `ConsultFile` and their
  reconsult forms) as `ShumwayPrologException`, whose `Term` is the ball
  `catch/3` would have unified with. It is the same exception whatever raised
  the error, whether or not a `catch/3` it did not match was on the way, and
  whether the error came from a goal or from preparing the query or the load
  (a syntax error of a strict load, a query with no room for its code).
- A builtin or a foreign predicate raises an error by throwing
  `Shumway.Core.PrologRuntimeException`: an ISO error kind and its detail as
  strings, since Core cannot reference the term types. The engine translates it
  to its `error/2` ball before it looks for a catcher. When none catches it, the
  `PrologRuntimeException` is the `InnerException` of the
  `ShumwayPrologException` the host gets; it is never the exception the host
  gets.
- Three outcomes are not errors and keep their own types: `abort/0` surfaces as
  `Shumway.Core.PrologAbortException`, cancellation as
  `OperationCanceledException`, and `halt/0,1` ends the query with an exit code
  instead of throwing.

There is one exception type and no hierarchy because the classification is the
term: an ISO error term already says its kind, and a host tests the term as
`catch/3` does. Until 2026-11-14 an error raised with no `catch/3` on the way
reached the host as the bare `PrologRuntimeException`, and the same error with a
non-matching `catch/3` on the way as a `ShumwayPrologException`: a host had to
catch two unrelated types for one error.

## What the Phase-1 sketch proposed

This ADR first carried a sketch of the API, written before any of it was built.
What it proposed and what was built instead:

| Sketch | Built |
|---|---|
| `Term`, a struct over a heap cell, valid only inside the operation that produced it; `TermHandle` to keep one; debug builds tracking term generations | AST objects that own their data (no lifetime rules, no handles), and raw cell access for the hot path |
| Term factory on the engine (`MakeAtom`, `MakeCompound`, …) | the AST constructors |
| A `Query` class with `Solutions()`, `FirstSolution()`, `Results<T>()` | `Query`, `QueryAll`, `QueryAsync`, `Query<T>`, `QueryFirst<T>` on the engine |
| `ForeignPredicate` delegates over a `ForeignContext` with `IsFirstCall` and `State` | `[PrologPredicate]` methods, raw or typed, non-determinism as `IEnumerable<T>` |
| `PrologEngine : IDisposable`, with `Reset()` | not disposable |
| `EngineConfig` (heap and stack sizes, double quotes, compilation strategy, promotion threshold) | `Flags` and `IlPromotion` on the engine |
| `PooledEngine` | `EnginePool.Lease` |
| An exception hierarchy (`PrologTypeException`, `PrologExistenceException`, …) | one `ShumwayPrologException` carrying the term |
| `DateTime` and `Guid` among the built-in conversions | not built in; a `RegisterConverter<T>` away |

## Alternatives Considered

### Term as a value struct over a heap cell

**Not adopted** (it was the Phase-1 sketch). It saves an allocation per term,
and pays for it with lifetime rules the type system cannot enforce: a term used
after its query backtracked reads another term's cells. The AST objects have no
such rule ([Phase 21](../../history/phase-21-closure.md): the scope-bound term
problem does not exist, so no handle type is needed), and the hot path that the
struct was meant to make cheap is cheaper still as raw cells.

### Term as a reference-counted handle

**Rejected.** Reference counting on every term access would add overhead to the common case.

### A `ForeignContext` with `IsFirstCall` and `State`

**Not adopted.** A non-deterministic foreign predicate as an `IEnumerable<T>`
generator is the idiomatic .NET shape; the explicit-state form would only be
needed by a predicate that cannot be written as a generator.

### Typed exceptions per ISO error kind

**Not adopted.** See *Exception model*: the term is the classification.

### Synchronous-only API

**Rejected.** Server scenarios with many concurrent requests benefit from async APIs for cancellation. The cost of supporting `IAsyncEnumerable` is small.

### Async/await internally in the interpreter

**Rejected.** The interpreter is performance-critical and synchronous. Async machinery (state machines, awaits) would slow it down. The async API at the embedding layer is a thin shell that delegates to the synchronous core, with cooperative cancellation via safe points.

### Querying via lambda expressions ("LINQ to Prolog")

**Considered, deferred.** A LINQ-style API where queries are expressed as C# expression trees and translated to Prolog goals is interesting but complex. The string-based and term-based queries cover the use cases without that complexity.

## Consequences

### Positive

- **Idiomatic .NET**: PascalCase, `IEnumerable`, `IAsyncEnumerable`, generics, attributes.
- **No term lifetime rules** on the convenience surface: a binding can be kept as long as the host likes.
- **A zero-copy hot path**: a foreign predicate works on the query's cells directly.
- **Typed conversion with source generators**: `[PrologPredicate]` and `[PrologTerm]` bridges are generated C#, inspectable and JIT-optimized.
- **Engine pool for servers**, and async with cancellation.
- **One exception type** for every error the program does not catch.

### Negative

- **The convenience surface allocates**: a typed query or a typed foreign predicate builds a term tree per binding it reads, except for the shapes the generator reads from the cells directly ([interop guide](../../guide/interop.md)).
- **The hot path is low level**: it requires the cell layout (ADR-002, ADR-017) and a dereference before every inspection.
- **Async is single-threaded per engine**: parallel speedup needs several engines.
- **Cancellation is coarse**: it is observed at the heap collector's watermark, so a loop that allocates nothing cannot be cancelled.

## Implementation Notes

### Source generators

The generator (`Shumway.SourceGen`) scans for `[PrologTerm]` and
`[PrologPredicate]` and emits partial-type extensions: the term converters of a
type, and the bridge of a typed foreign predicate. The generated code is
regular C#.

### Thread safety of `EnginePool`

Rent and return can be called from any thread. A permit per engine bounds the
pool at `maxSize`; a factory that throws gives its permit back.

## Test Strategy

- **Query iteration** and **solutions**: answers, `IsLast`, typed accessors.
- **Async query with cancellation**: a long-running query, cancelled, raises `OperationCanceledException` at the next safe point (`QueryAsyncTests`).
- **Foreign predicates**, deterministic and non-deterministic, raw and typed, modes (`Chunk235Tests` to `Chunk245Tests`).
- **Typed conversion**: every tier, `[PrologTerm]` round trips, custom converters (`Chunk238Tests` to `Chunk243Tests`).
- **Errors at the boundary**: an uncaught error is a `ShumwayPrologException` with the ball, with and without a `catch/3` on the way (`IsoConversionTests`).
- **Engine pool**: concurrent rents and returns, both reuse policies (`EnginePoolTests`).

## Related ADRs

- ADR-001 (Engines and Global Tables): thread-agility and pooling.
- ADR-002 (Cell Layout) and ADR-017 (inline compound references): what the hot path reads.
- ADR-003 (Atom Three-Tier System): atoms exposed via the API register foreign holds.
- ADR-011 (IL Compiler): Tier-1 promotion, configured through `IlPromotion`.
- ADR-024 (Generic Prolog-Term Interop): whole terms into native graphs.
- ADR-047 (A packed string is a list): text at the boundary.

## Related Design Docs

- `design/api-reference.md`: a compact map of the public surface.
- `design/foreign-predicates.md`: the `[PrologPredicate]` mechanism.
