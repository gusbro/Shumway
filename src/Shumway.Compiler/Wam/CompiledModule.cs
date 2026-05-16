namespace Shumway.Compiler.Wam;

/// <summary>
/// The compiled output of one Prolog source file: a collection of
/// <see cref="CompiledPredicate"/>s in source order, plus the original clause
/// stream's pre-compilation directives if a downstream stage wants to inspect
/// them (currently unused; reserved for the consult-style API). Linking a
/// module to a runnable program is the job of <see cref="Linker"/>.
/// </summary>
public sealed class CompiledModule
{
    public IReadOnlyList<CompiledPredicate> Predicates { get; }

    public CompiledModule(IReadOnlyList<CompiledPredicate> predicates)
    {
        Predicates = predicates;
    }
}
