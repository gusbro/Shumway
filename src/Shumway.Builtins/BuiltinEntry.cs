namespace Shumway.Builtins;

/// <summary>
/// One entry in the <see cref="BuiltinsRegistry"/>: the integer id baked into
/// <c>call_builtin</c> bytecode operands, the predicate name and arity (for
/// diagnostics), and the implementation function.
///
/// <para><see cref="Category"/>, <see cref="Template"/> and
/// <see cref="Summary"/> are optional user documentation recorded at the
/// registration site and consumed by the predicate-reference generator.
/// Internal helper builtins (the <c>$</c>-named ones) leave them null and are
/// omitted from the generated reference.</para>
/// </summary>
public sealed class BuiltinEntry
{
    public int Id { get; }
    public string Name { get; }
    public int Arity { get; }
    public BuiltinImpl Impl { get; }

    /// <summary>Documentation area this builtin belongs to, or null for an
    /// undocumented internal helper.</summary>
    public string? Category { get; }

    /// <summary>Call template with moded, named parameters — e.g.
    /// <c>between(+Low, +High, ?X)</c> — or null for an undocumented helper.</summary>
    public string? Template { get; }

    /// <summary>One-line description for the generated predicate reference,
    /// or null for an undocumented internal helper.</summary>
    public string? Summary { get; }

    /// <summary>True for the <c>call/1..7</c> family. Precomputed so the
    /// dispatch hot paths test a bool instead of comparing
    /// <see cref="Name"/> against <c>"call"</c> per call.</summary>
    public bool IsCall { get; }

    /// <summary>True for <c>'$call'/2</c>, the barrier-carrying
    /// meta-call. Precomputed like <see cref="IsCall"/>.</summary>
    public bool IsDollarCall { get; }

    /// <summary>True for builtins that push a choice point and resume via
    /// <c>ResumeAtReturnPc</c> — their Tier-1 IL
    /// <c>call_builtin</c> site needs a forward-resume cursor.
    ///
    /// <para>DERIVED, not declared: <see cref="BacktrackableDetector"/> walks the
    /// implementation's IL for a transitive call to a CP-creating sink, so a new
    /// cursor builtin can't be silently forgotten (the old hand-maintained name
    /// list was exactly that footgun). Read only by the IL compiler — a non-AOT
    /// context — and cached per method, so the reflection is lazy and never runs
    /// under Native AOT.</para></summary>
    public bool IsBacktrackable => BacktrackableDetector.IsBacktrackable(Impl);

    public BuiltinEntry(int id, string name, int arity, BuiltinImpl impl,
        string? category = null, string? template = null, string? summary = null)
    {
        Id = id;
        Name = name;
        Arity = arity;
        Impl = impl;
        Category = category;
        Template = template;
        Summary = summary;
        IsCall = name == "call";
        IsDollarCall = name == "$call";
    }

    public override string ToString() => $"{Name}/{Arity} (#{Id})";
}
