using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>
/// Signature shared by every Tier-1 IL-compiled predicate. The compiled
/// method takes the engine and reads its argument registers
/// (<c>X[0..arity-1]</c>) directly; on success it returns <c>true</c>,
/// on failure <c>false</c>. Choice-point management for multi-solution
/// predicates is the compiled body's responsibility (deferred to a
/// future chunk — this MVP only handles deterministic single-clause
/// predicates).
/// </summary>
public delegate bool PredicateDelegate(Engine engine);
