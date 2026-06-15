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
    /// <see cref="Name"/> against <c>"call"</c> per call (chunk 416).</summary>
    public bool IsCall { get; }

    /// <summary>True for <c>'$call'/2</c>, the chunk-88 barrier-carrying
    /// meta-call. Precomputed like <see cref="IsCall"/>.</summary>
    public bool IsDollarCall { get; }

    /// <summary>True for builtins that push a choice point and resume via
    /// <c>ResumeAtReturnPc</c> (the chunk-218 mechanism) — their Tier-1 IL
    /// <c>call_builtin</c> site needs a forward-resume cursor. Precomputed in
    /// the constructor (the <see cref="IsCall"/> precedent) so emit-time
    /// classification reads a bool instead of running the name switch per
    /// <c>CallBuiltin</c> per bytecode walk (chunk 433).</summary>
    public bool IsBacktrackable { get; }

    /// <summary>Canonical backtrackable-builtin name set (chunk 218; moved
    /// here from <c>IlPredicateCompiler.IsBacktrackableBuiltinName</c> in
    /// chunk 433 so the flag above can be precomputed at registration).</summary>
    public static bool IsBacktrackableName(string name) => name switch
    {
        "between" or "append" or "atom_concat" or "string_concat"
        or "nb_current" or "current_op" or "current_char_conversion"
        or "current_stream" or "stream_property" or "repeat" or "retract"
        // Cursor builtins added in the backtrackable-builtin alloc sweep: each
        // PushBuiltinChoicePoint's at runtime, so the IL emit MUST set up the
        // chunk-218 resume marker + BuiltinReturnPc. WAM tolerated the omission
        // (its CallBuiltin handler always sets BuiltinReturnPc); Tier-1 IL did
        // not — a missing name made the resume jump to PC 0 and lose solutions.
        or "$clause_enum" or "$current_predicate_enum"
        or "nth0" or "nth1" or "recorded" or "keys"
        or "string_search" or "directory" => true,
        _ => false,
    };

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
        IsBacktrackable = IsBacktrackableName(name);
    }

    public override string ToString() => $"{Name}/{Arity} (#{Id})";
}
