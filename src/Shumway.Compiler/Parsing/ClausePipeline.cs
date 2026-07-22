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
    /// no mode declarations passes a fresh empty one.
    /// <paramref name="inlineIte"/> (ADR-025) enables the inline if-then-else
    /// lowering for eligible plain-goal constructs — STATIC compilation paths
    /// only; the runtime assert path must pass false (the incremental clause
    /// append doesn't rebase intra-clause branch operands).</summary>
    /// <param name="helperIdProvider">Synthesized-helper id source.
    /// Activation consult/assert paths pass the ENGINE's monotonic sequence so two
    /// transforms into the same module never reuse a helper name; null keeps the
    /// per-Apply counter (standalone tooling, where module mangling isolates).</param>
    /// <param name="helperPrefix">Reserved namespace for the QUERY
    /// stub's helpers (<c>$q</c>): names are reused query-to-query (bounded atom
    /// space) and can never collide with consult-time helper names.</param>
    public static List<Clause> Apply(IEnumerable<Clause> clauses, ModeTable modes,
        bool inlineIte = false, Func<int>? helperIdProvider = null, string? helperPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        ArgumentNullException.ThrowIfNull(modes);
        bool prevInline = MetaTransform.InlineIteEnabled;
        var prevProvider = MetaTransform.HelperIdProvider;
        var prevPrefix = MetaTransform.HelperPrefix;
        MetaTransform.InlineIteEnabled = inlineIte;
        MetaTransform.HelperIdProvider = helperIdProvider;
        MetaTransform.HelperPrefix = helperPrefix;
        try
        {
            return ModeSpecializationTransform.Apply(
                PhraseTransform.Apply(
                    MetaTransform.Apply(
                        DcgTransform.Apply(clauses))),
                modes);
        }
        finally
        {
            MetaTransform.InlineIteEnabled = prevInline;
            MetaTransform.HelperIdProvider = prevProvider;
            MetaTransform.HelperPrefix = prevPrefix;
        }
    }
}
