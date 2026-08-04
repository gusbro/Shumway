# Public API reference

The authoritative, worked reference for embedding Shumway is the
[user guide](../guide/user-guide.md) and the XML doc-comments on the
`Shumway.Embedding` types. This page is a compact map of the real public
surface. Everything lives in the **`Shumway.Embedding`** namespace (terms are in
`Shumway.Compiler.Ast`).

## Engine

`PrologEngine` — the central type (a `sealed class`; **not** `IDisposable`).

- **Loading**: `ConsultString(string)`, `ConsultFile(string)`,
  `ReconsultFile(string)` (all `void`); `LoadBundle(string)` /
  `LoadBundle(Bundle)`; `PrologEngine.FromBundle(Bundle)`.
- **Querying**: `Query(string)` → a single `Solution` (first answer);
  `QueryAll(string)` → `IEnumerable<Solution>`; `QueryAsync(string)` →
  `IAsyncEnumerable<Solution>` (cancellable); typed `Query<T>(text[, var])` and
  `QueryFirst<T>(...)`. There is no separate `Query` type.
- **Opt-in libraries**: `UseClpfd()`, `UseClpr()`, `UseCoroutining()`;
  `UseNativeLibrary(path)`; `AddLibraryDirectory(dir[, dialect])`.
- **Flags / modules**: `Flags` (`PrologFlags`), `Modules`
  (`IReadOnlyDictionary<string, ModuleManifest>`).

## Terms

`Term` is the AST class hierarchy in `Shumway.Compiler.Ast`: `AtomTerm`,
`IntTerm`, `FloatTerm`, `BigIntTerm`, `StringTerm`, `CompoundTerm`, `VarTerm`.
Build with the constructors (`new CompoundTerm(".", head, tail)`); read by
pattern-matching (`if (t is IntTerm i) …`).

## Solution

`Success` (bool), `Bindings` (`IReadOnlyDictionary<string, Term>`), the indexer
`this[name]` → `Term?`, `IsLast` (bool), and the typed accessors `Get<T>(name)`
/ `TryGet<T>(name, out T)`.

## Typed conversion

`engine.ToTerm<T>(value)`, `engine.FromTerm<T>(term)`,
`engine.RegisterConverter<T>(Func<PrologEngine,T,Term> toTerm, Func<...> fromTerm)`.
The `[PrologTerm]` source generator emits `ToPrologTerm` / `FromPrologTerm` for
a POCO; `[PrologTermIgnore]` opts a field out.

## Foreign predicates

Annotate a method `[PrologPredicate("name/arity")]` and register the holder with
`engine.RegisterPredicates(instance | typeof(T) | <T>())`. A method takes a
`Shumway.Core.Activation` and returns `bool` (reading arguments via
`engine.GetRegister(0..arity-1)`), or uses typed parameters the generator
decodes — `out`/`ref` parameters map to `-`/`?` modes. Non-determinism is
`[PrologPredicate(..., NonDeterministic = true)]` returning `IEnumerable<T>`.
Raise errors by throwing `Shumway.Core.PrologRuntimeException`. See
[`foreign-predicates.md`](foreign-predicates.md).

## Pooling

`EnginePool(Func<PrologEngine> factory, int maxSize, PoolReusePolicy)` (or
`EnginePool.FromSource(...)`); `Rent()` / `RentAsync()` return a `Lease`
(`IDisposable`) whose engine is `Lease.Activation`.

## Exceptions

An uncaught Prolog `throw/1` surfaces as `ShumwayPrologException` (in
`Shumway.Core`), exposing the ball as `Term Term`.
