using Shumway.Compiler.Ast;
using Shumway.Compiler.Modes;

namespace Shumway.Compiler.Parsing;

/// <summary>
/// The single canonical source-to-clauses transform pipeline applied before WAM
/// compilation. Every consumer — the engine's consult / assert / query paths and
/// the <see cref="Wam.PredicateDisassembler"/> — runs clauses through this method
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
    /// lowering for eligible plain-goal constructs — static compilation paths
    /// only; the runtime assert path must pass false (the incremental clause
    /// append doesn't rebase intra-clause branch operands).</summary>
    /// <param name="helperIdProvider">Synthesized-helper id source.
    /// Activation consult/assert paths pass the ENGINE's monotonic sequence so two
    /// transforms into the same module never reuse a helper name; null keeps the
    /// per-Apply counter (standalone tooling, where module mangling isolates).</param>
    /// <param name="helperPrefix">Reserved namespace for the query
    /// stub's helpers (<c>$q</c>): names are reused query-to-query (bounded atom
    /// space) and can never collide with consult-time helper names.</param>
    /// <param name="dcgFailFast">Enables the DCG leading-terminal hoist. Debug
    /// codegen passes false — the hoist erases the first terminal's body goal
    /// and with it that line's debug stop site (ADR-035 position coverage).</param>
    public static List<Clause> Apply(IEnumerable<Clause> clauses, ModeTable modes,
        bool inlineIte = false, Func<int>? helperIdProvider = null, string? helperPrefix = null,
        bool dcgFailFast = true)
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
                        DcgTransform.Apply(
                            SsuTransform.Apply(clauses), dcgFailFast))),
                modes);
        }
        finally
        {
            MetaTransform.InlineIteEnabled = prevInline;
            MetaTransform.HelperIdProvider = prevProvider;
            MetaTransform.HelperPrefix = prevPrefix;
        }
    }

    /// <summary>A key equal for two clauses that are variants of each other:
    /// the same kind and position, and the same term up to a renaming of the
    /// variables. The pipeline transforms variants alike, so a host memoizes a
    /// rule's transform on it and a rule asserted again takes the helper
    /// predicates its earlier variant took. Null for a clause the key cannot
    /// name (a cyclic term's back-edge).</summary>
    public static string? VariantKey(Clause clause)
    {
        ArgumentNullException.ThrowIfNull(clause);
        var sb = new System.Text.StringBuilder(64);
        var pos = clause.Position;
        sb.Append('K').Append((int)clause.Kind).Append(';')
          .Append(pos.Line).Append(',').Append(pos.Column).Append(',')
          .Append(pos.Offset).Append(',').Append(pos.FileId).Append(';');
        // Pre-order with each compound's arity written: unambiguous without
        // brackets. Iterative, as a body is as deep as a list literal is long.
        Dictionary<string, int>? vars = null;
        var work = new Stack<Term>();
        work.Push(clause.Term);
        while (work.Count > 0)
        {
            switch (work.Pop())
            {
                case AtomTerm a:
                    sb.Append('a').Append(a.Name.Length).Append(':').Append(a.Name);
                    break;
                case VarTerm v:
                    if (v.IsCycleBack) return null;
                    // Every `_` is its own variable, and the transforms know it
                    // by that name; the others number in order of first sight.
                    if (v.Name == "_") { sb.Append('_'); break; }
                    vars ??= new Dictionary<string, int>();
                    if (!vars.TryGetValue(v.Name, out int index))
                        vars[v.Name] = index = vars.Count;
                    sb.Append('v').Append(index).Append(';');
                    break;
                case IntTerm i:
                    sb.Append('i').Append(i.Value).Append(';');
                    break;
                case BigIntTerm b:
                {
                    string digits = b.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    sb.Append('b').Append(digits.Length).Append(':').Append(digits);
                    break;
                }
                case FloatTerm f:
                    sb.Append('f').Append(BitConverter.DoubleToInt64Bits(f.Value)).Append(';');
                    break;
                case RationalTerm r:
                {
                    string num = r.Num.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    string den = r.Den.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    sb.Append('r').Append(num.Length).Append(':').Append(num).Append('/').Append(den).Append(';');
                    break;
                }
                case StringTerm s:
                    sb.Append('s').Append((int)s.Kind).Append(',')
                      .Append(s.Content.Length).Append(':').Append(s.Content);
                    break;
                case CompoundTerm c:
                    sb.Append('c').Append(c.Args.Length).Append(',')
                      .Append(c.Functor.Length).Append(':').Append(c.Functor);
                    for (int i = c.Args.Length - 1; i >= 0; i--) work.Push(c.Args[i]);
                    break;
                default:
                    return null;
            }
        }
        return sb.ToString();
    }
}
