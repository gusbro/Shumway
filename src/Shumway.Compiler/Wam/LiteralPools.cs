using System.Numerics;

namespace Shumway.Compiler.Wam;

/// <summary>
/// The three literal pools — string, float, big-integer — a module
/// compilation interns into.
///
/// <para><see cref="ModuleCompiler.Compile"/> creates a fresh set per call
/// by default. ADR-015 chunk B instead has the engine own one persistent
/// set and pass it to every compilation, so a literal keeps a stable id
/// across queries — the precondition for caching a separately-linked
/// static code region whose bytecode embeds those ids. Interning dedupes,
/// so re-compiling an unchanged predicate re-yields the same ids.</para>
/// </summary>
public sealed class LiteralPools
{
    public LiteralPool<string> Strings { get; } = new();
    public LiteralPool<double> Floats { get; } = new();
    public LiteralPool<BigInteger> BigInts { get; } = new();
}
