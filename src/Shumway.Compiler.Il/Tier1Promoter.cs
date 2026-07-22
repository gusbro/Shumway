using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;

namespace Shumway.Compiler.Il;

/// <summary>
/// Convenience surface for the Phase-1 Tier-1 path. Wraps the
/// parse → predicate-compile → IL-emit pipeline behind a single
/// <see cref="TryCompile"/> call, returning a callable
/// <see cref="PredicateDelegate"/> when the predicate's bytecode is
/// inside the IL compiler's supported subset, and <c>null</c> otherwise.
///
/// <para>Auto-promotion (counter-driven background compilation and
/// atomic delegate swap inside the interpreter's dispatch loop) lives
/// in the embedding layer's promotion store; this class is the manual
/// entry point that tests and ahead-of-time tools use.</para>
/// </summary>
public static class Tier1Promoter
{
    /// <summary>Parses <paramref name="source"/> as a single predicate's
    /// clauses, compiles the bytecode through <see cref="PredicateCompiler"/>,
    /// and IL-compiles via <see cref="IlPredicateCompiler"/>. Returns the
    /// bound delegate on success; <c>null</c> when the bytecode falls
    /// outside the IL subset (so callers can fall back to Tier 0).</summary>
    public static PredicateDelegate? TryCompile(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var clauses = new ClauseReader(source).ReadAll().ToList();
        if (clauses.Count == 0) return null;
        var pred = new PredicateCompiler().Compile(clauses);
        var ic = new IlPredicateCompiler();
        return ic.CanCompile(pred) ? ic.Compile(pred) : null;
    }
}
