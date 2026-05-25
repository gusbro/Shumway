namespace Shumway.Embedding;

/// <summary>Visibility of a predicate defined in a <c>.shmo</c>.
/// <list type="bullet">
/// <item><c>Local</c> — default. The predicate is not exported to the
/// global namespace; only call sites inside the same module can hit it
/// after a link (if the linker discovers no public/dynamic match
/// elsewhere, a call to a local predicate from another module's bytecode
/// fails with <c>existence_error</c> at runtime, exactly as ISO
/// requires).</item>
/// <item><c>Public</c> — declared <c>:- public foo/N</c>. Contributes to
/// the linker's global namespace and may collide with another module's
/// public of the same indicator (a link-time error).</item>
/// <item><c>Dynamic</c> — declared <c>:- dynamic foo/N</c>. Also
/// contributes to the global namespace; satisfies references even with
/// zero clauses (the linker won't flag the indicator as missing).</item>
/// </list></summary>
public enum PredicateVisibility : byte
{
    Local = 0,
    Public = 1,
    Dynamic = 2,
}

/// <summary>An unqualified predicate indicator <c>Name/Arity</c>. The
/// canonical key in the flat global namespace.</summary>
public readonly record struct PredicateRef(string Name, int Arity)
{
    public override string ToString() => $"{Name}/{Arity}";
}

/// <summary>A module-qualified predicate reference, e.g. <c>lists:append/3</c>.
/// Used only for the rare call site that writes <c>Module:Goal</c> explicitly;
/// the common case is an unqualified <see cref="PredicateRef"/> that the linker
/// resolves against the global namespace.</summary>
public readonly record struct QualifiedPredicateRef(string Module, string Name, int Arity)
{
    public override string ToString() => $"{Module}:{Name}/{Arity}";
}

/// <summary>One predicate defined inside a <c>.shmo</c>, together with
/// its visibility.</summary>
public sealed class ShmoDefinedPredicate
{
    public PredicateRef Indicator { get; }
    public PredicateVisibility Visibility { get; }
    public ShmoDefinedPredicate(PredicateRef indicator, PredicateVisibility visibility)
    {
        Indicator = indicator;
        Visibility = visibility;
    }
}

/// <summary>
/// In-memory representation of a Shumway compiled-object file
/// (<c>.shmo</c>). Built by <c>shumway-compile</c>, consumed by
/// <c>shumway-link</c>.
///
/// <para>The object carries everything the linker needs to resolve and
/// reach-walk a multi-module program:</para>
/// <list type="bullet">
/// <item><see cref="ModuleName"/> — identifier for diagnostics.</item>
/// <item><see cref="Source"/> — original Prolog source. Optional (may be
/// empty for stripped objects); the linker emits source-bearing bundles
/// when present so the runtime <see cref="PrologEngine.LoadBundle(Bundle)"/>
/// path keeps working as it does today.</item>
/// <item><see cref="Bytecode"/> — the per-module
/// <see cref="CompiledModuleCodec"/> output, identical in encoding to
/// what an in-process <c>ConsultString</c> would have produced.</item>
/// <item><see cref="Defined"/> — every predicate this module defines,
/// tagged with its visibility.</item>
/// <item><see cref="EnsureLinked"/> — predicates declared
/// <c>:- ensure_linked/1</c>; the linker treats them as additional
/// reachability roots so a predicate only invoked via runtime meta-call
/// is not dead-code-eliminated.</item>
/// <item><see cref="CallGraph"/> — for each defined predicate, the set
/// of unqualified call targets the linker should follow.</item>
/// <item><see cref="QualifiedRefs"/> — explicit <c>Module:Goal</c> call
/// sites. Rare; resolved against the named module's public set rather
/// than the flat global namespace.</item>
/// </list>
/// </summary>
public sealed class ShmoObject
{
    public string ModuleName { get; }
    public string Source { get; }
    public byte[] Bytecode { get; }
    public IReadOnlyList<ShmoDefinedPredicate> Defined { get; }
    public IReadOnlyList<PredicateRef> EnsureLinked { get; }
    public IReadOnlyDictionary<PredicateRef, IReadOnlyList<PredicateRef>> CallGraph { get; }
    public IReadOnlyList<QualifiedPredicateRef> QualifiedRefs { get; }

    /// <summary>The compilation mode the <c>.shmo</c> was built in
    /// (<c>--debug</c> vs <c>--release</c>). Defaults to
    /// <see cref="ShmoBuildMode.Release"/> for compatibility with V1
    /// objects that didn't carry the flag.</summary>
    public ShmoBuildMode BuildMode { get; }

    public ShmoObject(
        string moduleName,
        string source,
        byte[] bytecode,
        IReadOnlyList<ShmoDefinedPredicate> defined,
        IReadOnlyList<PredicateRef> ensureLinked,
        IReadOnlyDictionary<PredicateRef, IReadOnlyList<PredicateRef>> callGraph,
        IReadOnlyList<QualifiedPredicateRef> qualifiedRefs,
        ShmoBuildMode buildMode = ShmoBuildMode.Release)
    {
        ModuleName = moduleName;
        Source = source;
        Bytecode = bytecode;
        Defined = defined;
        EnsureLinked = ensureLinked;
        CallGraph = callGraph;
        QualifiedRefs = qualifiedRefs;
        BuildMode = buildMode;
    }
}
