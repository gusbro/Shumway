# Foreign predicates

A *foreign predicate* is a Prolog predicate implemented in C# (or any .NET
language) instead of Prolog — for I/O, calls into other libraries, or custom
data handling. This documents the shipped mechanism;
`src/Shumway.Embedding/PrologPredicateAttribute.cs`,
`PrologEngine.ManagedInterop.cs`, and the source generator in
`Shumway.SourceGen` are authoritative. The [user guide](../guide/user-guide.md)
has worked examples.

## Registration

Annotate a method with `[PrologPredicate("name/arity")]` (the indicator is
`"name/arity"`; a bare `[PrologPredicate(arity)]` uses the method name), then
register the holder:

```csharp
engine.RegisterPredicates(new MyService());   // instance
engine.RegisterPredicates(typeof(MyStatics)); // static methods
engine.RegisterPredicates<MyService>();       // type parameter
```

Registration conflicts (an indicator already defined) throw.

## Method shape

Two forms:

- **Raw** — `bool Method(Shumway.Core.Activation engine)`. Read arguments with
  `engine.GetRegister(0..arity-1)` and unify results directly. Return `true` to
  succeed, `false` to fail.
- **Typed** — parameters and return of ordinary C# types. The source generator
  emits a `_{Method}_PrologBridge(Activation)` wrapper that decodes each input
  via `FromTerm<T>` and encodes the return via `ToTerm<T>`. Standard C# modifiers
  map to Prolog modes: a plain parameter is `+`, `out T` is `-`, `ref T?` is `?`.
  A `+` parameter that arrives unbound raises `instantiation_error`.

## Non-determinism

Set `[PrologPredicate(..., NonDeterministic = true)]` and return
`IEnumerable<T>`. The generator drives the enumerator through a builtin choice
point (`Engine.PushBuiltinChoicePoint`), yielding one solution per element and
disposing deterministically on cut. `out`/`ref` are incompatible with a
non-deterministic or non-`bool`/`void` return.

## Errors

Throw `Shumway.Core.PrologRuntimeException` to raise a Prolog error; it
surfaces to Prolog as a catchable `error/2` term (and to an uncaught host
caller as `ShumwayPrologException`).

## Threading

An engine is single-threaded, so a foreign method runs on the engine's thread
with no reentrancy from other threads on the same engine. Keep foreign methods
free of blocking waits on the engine.

## Shipping foreign predicates in a bundle

`shumway-link --foreign-dll <path>` records the assembly in the bundle; the
indicators it defines count as resolved during the reachability walk, and
`LoadBundle` auto-registers them (probing next to the bundle, then
`AppContext.BaseDirectory`). `--exe` / `--dll` copy the foreign DLLs next to the
output.
