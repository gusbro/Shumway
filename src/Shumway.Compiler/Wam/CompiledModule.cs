namespace Shumway.Compiler.Wam;

/// <summary>
/// The compiled output of one Prolog source file: a collection of
/// <see cref="CompiledPredicate"/>s in source order, plus the original clause
/// stream's pre-compilation directives if a downstream stage wants to inspect
/// them (currently unused; reserved for the consult-style API). Linking a
/// module to a runnable program is the job of <see cref="Linker"/>.
///
/// <para>Switch tables introduced by first-argument indexing live on each
/// <see cref="CompiledPredicate"/> with predicate-local addresses. The
/// <see cref="Linker"/> aggregates them, shifts addresses to be
/// program-absolute, and returns the resulting flat list as part of its
/// <see cref="Linker.LinkResult"/>.</para>
/// </summary>
public sealed class CompiledModule
{
    public IReadOnlyList<CompiledPredicate> Predicates { get; }

    /// <summary>String literals referenced by <c>get_pstr</c> / <c>put_pstr</c>
    /// instructions in this module's bytecode. Indexed by the literal id
    /// baked into the bytecode operand.</summary>
    public IReadOnlyList<string> StringLiterals { get; }

    /// <summary>Float literals referenced by <c>get_float</c> / <c>put_float</c>
    /// / <c>unify_float</c> instructions.</summary>
    public IReadOnlyList<double> FloatLiterals { get; }

    public CompiledModule(
        IReadOnlyList<CompiledPredicate> predicates,
        IReadOnlyList<string> stringLiterals,
        IReadOnlyList<double> floatLiterals)
    {
        Predicates = predicates;
        StringLiterals = stringLiterals;
        FloatLiterals = floatLiterals;
    }
}
