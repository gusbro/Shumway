using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// The signature every Shumway builtin satisfies. The implementation reads its
/// arguments from the X registers (<c>X[0]</c> through <c>X[arity-1]</c>) and
/// writes any output bindings into the heap via the engine's standard unification
/// / binding primitives. Returns <c>true</c> for success — the interpreter
/// continues at the next instruction — and <c>false</c> for failure, which
/// triggers the usual backtrack-or-fail path.
///
/// <para>Builtins run within the calling clause's execution context: they share
/// the heap, stack, trails, and registers with the rest of the program. Any
/// bindings they create are reversible by backtracking, just like bindings
/// from interpreted clauses.</para>
/// </summary>
public delegate bool BuiltinImpl(Engine engine);
