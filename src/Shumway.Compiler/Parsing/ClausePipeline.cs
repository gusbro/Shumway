using Shumway.Compiler.Ast;
using Shumway.Compiler.Modes;

namespace Shumway.Compiler.Parsing;

/// <summary>
/// The single canonical source-to-clauses transform pipeline applied before WAM
/// compilation. Every consumer — the engine's consult / assert / query paths and
/// the <see cref="Wam.PredicateDisassembler"/> — runs clauses through THIS method
/// so the compiled (and disassembled) code is identical to what executes.
///
/// <para>Order matters: DCG rule expansion first (<c>--&gt;</c> → ordinary
/// clauses with difference-list args), then meta-call lowering
/// (<see cref="MetaTransform"/>: if-then-else / <c>;</c> / <c>\+</c> /
/// <c>findall</c> / … → synthesised helper clauses + plain calls), then
/// <c>phrase/2,3</c> expansion, then mode specialization. A mode-free
/// <see cref="ModeTable"/> makes the last step a no-op.</para>
/// </summary>
public static class ClausePipeline
{
    /// <summary>Runs <paramref name="clauses"/> through the full transform
    /// pipeline. Pass the engine's live <see cref="ModeTable"/>; tooling that has
    /// no mode declarations passes a fresh empty one.</summary>
    public static List<Clause> Apply(IEnumerable<Clause> clauses, ModeTable modes)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        ArgumentNullException.ThrowIfNull(modes);
        return ModeSpecializationTransform.Apply(
            PhraseTransform.Apply(
                MetaTransform.Apply(
                    DcgTransform.Apply(clauses))),
            modes);
    }
}
