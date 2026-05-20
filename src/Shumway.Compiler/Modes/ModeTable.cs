namespace Shumway.Compiler.Modes;

/// <summary>
/// Chunk 73 — aggregates every <c>:- mode</c> declaration the engine
/// has seen into one queryable table, keyed by functor id. A predicate
/// may have multiple declarations (one per callable mode), so each
/// key maps to a list.
///
/// <para>This is the accessor the Phase-3 specialised code generator
/// consults: given a functor id it asks "which modes were declared,
/// and which of them are deterministic?". The table also runs the
/// semantic <see cref="Validate"/> pass that turns suspicious
/// declarations (modes on never-defined predicates, contradictory
/// determinism annotations for the same mode pattern) into
/// diagnostics.</para>
/// </summary>
public sealed class ModeTable
{
    private readonly Dictionary<int, List<ModeDeclaration>> _byFunctor = new();

    /// <summary>Records one mode declaration. Multiple declarations for
    /// the same functor accumulate; the analysis treats each as a
    /// distinct callable mode.</summary>
    public void Add(ModeDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (!_byFunctor.TryGetValue(declaration.FunctorId, out var list))
        {
            list = new List<ModeDeclaration>();
            _byFunctor[declaration.FunctorId] = list;
        }
        list.Add(declaration);
    }

    /// <summary>The mode declarations for a functor, in source order.
    /// Empty when the predicate has no <c>:- mode</c> directive — the
    /// caller then treats every argument as <see cref="ModeIndicator.Either"/>
    /// and the predicate as <see cref="Determinism.Nondet"/>.</summary>
    public IReadOnlyList<ModeDeclaration> ModesFor(int functorId) =>
        _byFunctor.TryGetValue(functorId, out var list)
            ? list
            : Array.Empty<ModeDeclaration>();

    /// <summary>True iff at least one <c>:- mode</c> directive named
    /// this functor.</summary>
    public bool HasModes(int functorId) => _byFunctor.ContainsKey(functorId);

    /// <summary>Every functor id that carries at least one mode
    /// declaration.</summary>
    public IEnumerable<int> DeclaredFunctors => _byFunctor.Keys;

    /// <summary>True iff <paramref name="functorId"/> has a declared
    /// mode whose determinism promises at most one solution. Phase-3
    /// code generation reads this to decide whether the choice-point
    /// machinery can be dropped for a specialised path.</summary>
    public bool HasDeterministicMode(int functorId)
    {
        foreach (var decl in ModesFor(functorId))
            if (decl.IsDeterministic) return true;
        return false;
    }

    /// <summary>Semantic validation pass. <paramref name="definedFunctors"/>
    /// is the set of functor ids the loaded program actually defines
    /// (static or dynamic). Produces:
    /// <list type="bullet">
    /// <item>a <b>warning</b> for a mode declaration whose functor has
    /// no clauses — likely a typo or stale declaration (ADR-012 says
    /// the predicate "may be defined later", so it's not an
    /// error);</item>
    /// <item>a <b>warning</b> for two declarations that share an
    /// identical mode pattern but disagree on determinism — only one
    /// can be right, and the code generator would have to pick.</item>
    /// </list>
    /// Indicator / determinism syntax is already validated at parse
    /// time, so this pass only covers cross-declaration and
    /// cross-program consistency.</summary>
    public IReadOnlyList<ModeValidationIssue> Validate(IReadOnlySet<int> definedFunctors)
    {
        ArgumentNullException.ThrowIfNull(definedFunctors);
        var issues = new List<ModeValidationIssue>();
        foreach (var (functorId, decls) in _byFunctor)
        {
            if (!definedFunctors.Contains(functorId))
            {
                issues.Add(new ModeValidationIssue(
                    functorId,
                    ModeValidationSeverity.Warning,
                    "mode declaration names a predicate with no clauses in the loaded program."));
            }

            // Same arg-mode pattern, conflicting determinism.
            for (int i = 0; i < decls.Count; i++)
            {
                for (int j = i + 1; j < decls.Count; j++)
                {
                    if (SamePattern(decls[i].ArgModes, decls[j].ArgModes)
                        && decls[i].EffectiveDeterminism != decls[j].EffectiveDeterminism)
                    {
                        issues.Add(new ModeValidationIssue(
                            functorId,
                            ModeValidationSeverity.Warning,
                            "two mode declarations share an argument pattern but "
                            + $"declare different determinism ({decls[i].EffectiveDeterminism} "
                            + $"vs {decls[j].EffectiveDeterminism})."));
                    }
                }
            }
        }
        return issues;
    }

    private static bool SamePattern(
        IReadOnlyList<ModeIndicator> a, IReadOnlyList<ModeIndicator> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}

/// <summary>Severity of a <see cref="ModeValidationIssue"/>.</summary>
public enum ModeValidationSeverity
{
    Warning,
    Error,
}

/// <summary>One issue found by <see cref="ModeTable.Validate"/>.</summary>
public sealed record ModeValidationIssue(
    int FunctorId,
    ModeValidationSeverity Severity,
    string Message);
