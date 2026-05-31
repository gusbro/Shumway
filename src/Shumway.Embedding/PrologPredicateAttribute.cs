namespace Shumway.Embedding;

/// <summary>
/// Chunk 237 — marks a C# method as a foreign Prolog predicate so
/// <see cref="PrologEngine.RegisterPredicates(object)"/> (and its
/// static-class overload) can register it with
/// <see cref="Shumway.Builtins.BuiltinsRegistry"/> without
/// boilerplate per call.
///
/// <para>The decorated method's C# signature must be
/// <c>bool Method(Shumway.Core.Engine engine)</c> — the same shape
/// as a native Shumway builtin. Arguments are read from
/// <c>engine.GetRegister(0..arity-1)</c> and results are unified
/// via the engine's APIs. A return of <c>true</c> means the
/// predicate succeeded; <c>false</c> means it failed; throwing
/// <see cref="Shumway.Core.PrologRuntimeException"/> raises an ISO
/// error term that <c>catch/3</c> can intercept.</para>
///
/// <para>Instance methods bind to the registered instance; static
/// methods are registered as-is. Either way the resulting
/// <see cref="Shumway.Builtins.BuiltinImpl"/> delegate has no
/// per-call reflection overhead — reflection only happens once at
/// registration time.</para>
///
/// <para>A richer signature (typed parameters with auto-conversion
/// through the embedding's term converters) is on the roadmap; it
/// will live behind the same attribute alongside this raw form, so
/// migrating later is opt-in.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class PrologPredicateAttribute : Attribute
{
    /// <summary>The Prolog predicate name. When <c>null</c> (the
    /// <c>[PrologPredicate(arity)]</c> form), the C# method's name is
    /// used verbatim — Prolog names are case-sensitive, so a method
    /// named <c>my_pred</c> registers as <c>my_pred/N</c> and
    /// <c>MyPred</c> registers as <c>MyPred/N</c>. Override
    /// explicitly when the C# name doesn't match the desired Prolog
    /// atom.</summary>
    public string? Name { get; }

    /// <summary>Predicate arity. Always present — the C# method
    /// signature is fixed at <c>bool(Engine)</c>, so arity can't be
    /// inferred from parameter count.</summary>
    public int Arity { get; }

    /// <summary>Optional documentation category (e.g. <c>"Database"</c>
    /// or <c>"Control"</c>). Surfaces in the generated
    /// <c>docs/predicates.md</c> reference.</summary>
    public string? Category { get; init; }

    /// <summary>Optional moded call template, e.g.
    /// <c>"between(+Low, +High, ?X)"</c>. Surfaces in the predicate
    /// reference next to <see cref="Summary"/>.</summary>
    public string? Template { get; init; }

    /// <summary>Optional one-line summary describing what the
    /// predicate does. Surfaces in the predicate reference.</summary>
    public string? Summary { get; init; }

    /// <summary>Use the C# method's name as the Prolog atom, with
    /// <paramref name="arity"/> as the predicate arity. Convenient
    /// when the C# method is already named the way Prolog wants the
    /// atom (e.g. <c>my_pred</c>).</summary>
    public PrologPredicateAttribute(int arity)
    {
        Arity = arity;
    }

    /// <summary>Register under the given Prolog predicate indicator,
    /// canonical <c>Name/Arity</c> form — e.g.
    /// <c>[PrologPredicate("distance/3")]</c>. The string is
    /// validated at registration time; arity must be a non-negative
    /// integer and the name a non-empty Prolog atom (the runtime
    /// quotes it verbatim — no escaping is performed).</summary>
    public PrologPredicateAttribute(string indicator)
    {
        ArgumentNullException.ThrowIfNull(indicator);
        int slash = indicator.LastIndexOf('/');
        if (slash <= 0 || slash == indicator.Length - 1
            || !int.TryParse(indicator.AsSpan(slash + 1), out int arity)
            || arity < 0)
        {
            throw new ArgumentException(
                $"[PrologPredicate] indicator '{indicator}' must be in 'Name/Arity' "
                + "form with Arity a non-negative integer (e.g. \"distance/3\").",
                nameof(indicator));
        }
        Name = indicator.Substring(0, slash);
        Arity = arity;
    }
}
