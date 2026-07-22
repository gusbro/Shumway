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

/// <summary>one call-graph edge: the unqualified target plus
/// the DIRECT-vs-META marker.
///
/// <para><see cref="IsMeta"/> is <c>true</c> when every reference this
/// MODULE makes to <see cref="Target"/> sits inside a meta-call argument
/// — <c>call/1</c>, the <c>call/N</c> closure goal, or the goal argument
/// of <c>findall</c> / <c>bagof</c> / <c>setof</c> / <c>forall</c> /
/// <c>once</c> / <c>ignore</c> / <c>catch</c> / <c>\+</c> / <c>not</c> —
/// and <c>false</c> when at least one reference is a plain body goal
/// (or the target only appears in synthesised helper bodies the
/// transform pipeline produced). The bit is computed module-wide per
/// TARGET (not per call site): the MetaTransform pipeline erases the
/// meta wrappers before the call-graph walk (rewrites
/// <c>call(g(X))</c> to a direct <c>g(X)</c>; findall goals are inlined
/// into <c>$disj</c> helper bodies), so a per-site bit measured on the
/// transformed bodies would mis-mark exactly the sites that matter. A
/// module-wide bit is also the conservative direction — one direct
/// reference anywhere in the module marks every edge to that target
/// DIRECT.</para>
///
/// <para>The linker uses the marker for Arity call semantics (an
/// undeclared predicate that is only ever meta-called links as an
/// implicit empty dynamic); for non-Arity modules it is carried but
/// ignored.</para></summary>
public readonly record struct ShmoCallEdge(PredicateRef Target, bool IsMeta)
{
    public override string ToString() => IsMeta ? $"{Target}[meta]" : Target.ToString();
}

/// <summary>One <c>:- op/3</c> definition a module's source executed at
/// compile time. Carried through
/// <c>.shmo</c> → <c>.shum</c> so <c>LoadBundle</c> can replay it into the
/// runtime engine's operator table — a SOURCE-STRIPPED bundle otherwise
/// loses the ops, and any runtime <c>read/1</c> / <c>string_term/2</c> of
/// text using them mis-parses (the debug path never noticed: it re-consults
/// the source, re-executing the directives). <see cref="Type"/> is the
/// canonical specifier atom (<c>fx … yfx</c>).</summary>
public readonly record struct ShmoOperatorDef(int Priority, string Type, string Name)
{
    public override string ToString() => $"op({Priority}, {Type}, {Name})";
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
/// empty for stripped objects); the linker emits source-carrying bundles
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
/// of unqualified call targets the linker should follow, each marked
/// DIRECT or META (<see cref="ShmoCallEdge"/>).</item>
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
    public IReadOnlyDictionary<PredicateRef, IReadOnlyList<ShmoCallEdge>> CallGraph { get; }
    public IReadOnlyList<QualifiedPredicateRef> QualifiedRefs { get; }

    /// <summary>The compilation mode the <c>.shmo</c> was built in
    /// (<c>--debug</c> vs <c>--release</c>). Defaults to
    /// <see cref="ShmoBuildMode.Release"/> for compatibility with V1
    /// objects that didn't carry the flag.</summary>
    public ShmoBuildMode BuildMode { get; }

    /// <summary><c>true</c> when the module was compiled in
    /// Arity compatibility mode (<c>shumway-compile --arity</c>, or an
    /// in-file <c>:- set_prolog_flag(arity_compat, true)</c> at any
    /// point during the compile). The linker uses it to apply Arity
    /// call semantics to this module's unresolved references: a call to
    /// an undeclared predicate is valid in Arity — it fails if nothing
    /// was asserted — so the linker registers the target as an implicit
    /// empty dynamic predicate instead of erroring.</summary>
    public bool ArityCompat { get; }

    /// <summary>clauses for <c>:- dynamic foo/N.</c>
    /// predicates carried as <see cref="TermCodec"/>-encoded terms
    /// rather than baked into <see cref="Bytecode"/>. The bytecode
    /// can't hold them because the engine has to mutate them at
    /// runtime (assertz / retract / clause/2). At load,
    /// <see cref="PrologEngine.LoadBundle(Bundle)"/> deserialises
    /// each entry and seeds the engine's
    /// <c>_dynamicClauses[fid]</c> store — mirroring exactly what
    /// <see cref="PrologEngine.ConsultString(string)"/> does when
    /// it routes a source-declared dynamic clause to the runtime
    /// store. Empty for V1/V2 objects.</summary>
    public IReadOnlyList<ShmoDynamicSeed> DynamicSeeds { get; }

    /// <summary>the module's STATIC clauses as
    /// <see cref="TermCodec"/>-encoded terms, RAW (post-parse, pre-DCG /
    /// pre-MetaTransform / pre-unfold; dynamic-head clauses excluded — those
    /// travel in <see cref="DynamicSeeds"/>). The LTO channel (user decision,
    /// mirroring fat object files): always present, Release included — the
    /// <c>.shmo</c> is an intermediate build artifact; IP stripping applies to
    /// the shipped <c>.shum</c>/exe, not here. The linker uses it for
    /// cross-module link-time optimization (the meta-wrapper unfold's
    /// cross-module driver recompiles affected callers from these clauses), and
    /// it is the substrate for any future LTO pass.</summary>
    public IReadOnlyList<byte[]> ClauseTerms { get; }

    /// <summary>ADR-022 item 1 — the module's embedded native blocks. The
    /// compiler rewrites each <c>{ … }</c> block to a portable
    /// <c>'$native_run'('$nb$…', Vars)</c> dispatch (in <see cref="Bytecode"/> and
    /// <see cref="ClauseTerms"/>); the block's marshalling data travels here, so a
    /// source-stripped bundle can run it. At load
    /// <see cref="PrologEngine.LoadBundle(Bundle)"/> repopulates the engine's block
    /// table from these. Empty for a module with no native blocks.</summary>
    public IReadOnlyList<ShmoNativeBlock> NativeBlocks { get; }

    /// <summary>ADR-023 priming — an in-memory, NON-serialized static-style WAM
    /// snapshot module (a <see cref="CompiledModuleCodec"/>-encoded blob) of this
    /// object's <c>:- dynamic</c>/<c>:- visible</c> predicates' clauses. Populated
    /// by <see cref="ShmoCompiler"/> at compile time so <c>--dump-wam</c> /
    /// <c>--dump-il</c> can show the WAM/IL those predicates run from the first
    /// call (their clauses live in <see cref="DynamicSeeds"/>, so the static
    /// <see cref="Bytecode"/> module is empty for them). Not written to the
    /// <c>.shmo</c>: the runtime rebuilds its own snapshot from the live clauses,
    /// so this is a build-time dump aid only. Null when there are no such
    /// predicates or the object was read back from disk.</summary>
    public byte[]? DynamicSnapshotBytecode { get; set; }

    public ShmoObject(
        string moduleName,
        string source,
        byte[] bytecode,
        IReadOnlyList<ShmoDefinedPredicate> defined,
        IReadOnlyList<PredicateRef> ensureLinked,
        IReadOnlyDictionary<PredicateRef, IReadOnlyList<ShmoCallEdge>> callGraph,
        IReadOnlyList<QualifiedPredicateRef> qualifiedRefs,
        ShmoBuildMode buildMode = ShmoBuildMode.Release,
        IReadOnlyList<ShmoDynamicSeed>? dynamicSeeds = null,
        IReadOnlyList<byte[]>? clauseTerms = null,
        bool arityCompat = false,
        IReadOnlyList<ShmoNativeBlock>? nativeBlocks = null,
        IReadOnlyList<PredicateRef>? nativeFunctions = null,
        string? nativeDecls = null,
        IReadOnlyList<ShmoOperatorDef>? operators = null)
    {
        ModuleName = moduleName;
        Source = source;
        Bytecode = bytecode;
        Defined = defined;
        EnsureLinked = ensureLinked;
        CallGraph = callGraph;
        QualifiedRefs = qualifiedRefs;
        BuildMode = buildMode;
        DynamicSeeds = dynamicSeeds ?? System.Array.Empty<ShmoDynamicSeed>();
        ClauseTerms = clauseTerms ?? System.Array.Empty<byte[]>();
        ArityCompat = arityCompat;
        NativeBlocks = nativeBlocks ?? System.Array.Empty<ShmoNativeBlock>();
        NativeFunctions = nativeFunctions ?? System.Array.Empty<PredicateRef>();
        NativeDecls = nativeDecls;
        Operators = operators ?? System.Array.Empty<ShmoOperatorDef>();
    }

    /// <summary>ADR-024 — the <c>:- native fn/N</c> indicators in this module, so a
    /// source-stripped bundle restores them at load (a native function resolves via
    /// the materializer protocol — P/Invoke or a managed snapshot).</summary>
    public IReadOnlyList<PredicateRef> NativeFunctions { get; }

    /// <summary>ADR-024 — the raw <c>:- c</c> declaration text (prototypes + typedefs)
    /// of this module, re-parsed at load to derive native-call signatures. Null when
    /// the module has no <c>:- c</c> region.</summary>
    public string? NativeDecls { get; }

    /// <summary>Every <c>:- op/3</c> this module's source
    /// executed at compile time, in source order (list-name forms expanded).
    /// Replayed into the engine's operator table by <c>LoadBundle</c> so
    /// runtime term reading in a source-stripped bundle parses with the same
    /// operators the source declared.</summary>
    public IReadOnlyList<ShmoOperatorDef> Operators { get; }
}

/// <summary>ADR-022 — one embedded native block's marshalling data, carried
/// across the compile/load boundary. <see cref="Name"/> is the dispatch atom
/// (<c>'$nb$…'</c>) the bytecode references; <see cref="RawText"/> is the block's
/// statement source (re-parsed to the statement list at load — the C symbol table
/// is not needed at run time, only for the compile-time inference already baked
/// into <see cref="Vars"/>); <see cref="Vars"/> are the marshalled Prolog
/// variables, in argument-register order.</summary>
public sealed class ShmoNativeBlock
{
    public string Name { get; }
    public string RawText { get; }
    public IReadOnlyList<Shumway.Compiler.NativeC.NativeVar> Vars { get; }
    /// <summary>ADR-022 — the scalar `:- c` globals the block reads/writes (mapped
    /// to per-engine persistent storage at load). Carried because the `:- c`
    /// declarations themselves do not travel in the bundle.</summary>
    public IReadOnlyList<Shumway.Compiler.NativeC.NativeScalarGlobal> ScalarGlobals { get; }
    public ShmoNativeBlock(string name, string rawText,
        IReadOnlyList<Shumway.Compiler.NativeC.NativeVar> vars,
        IReadOnlyList<Shumway.Compiler.NativeC.NativeScalarGlobal> scalarGlobals)
    {
        Name = name;
        RawText = rawText;
        Vars = vars;
        ScalarGlobals = scalarGlobals;
    }
}

/// <summary>One <c>:- dynamic foo/N.</c> predicate's source-declared
/// clauses, carried across the compile/load boundary as
/// <see cref="TermCodec"/>-encoded byte blobs. See
/// <see cref="ShmoObject.DynamicSeeds"/>.</summary>
public sealed class ShmoDynamicSeed
{
    public PredicateRef Indicator { get; }
    public IReadOnlyList<byte[]> EncodedClauses { get; }
    /// <summary>True for a <c>:- multifile</c> predicate. Its clauses are
    /// module-rewritten at COMPILE time under their origin module, so the
    /// load path must NOT record a per-fid seed module for them — several
    /// modules contribute to one fid, and a single module context would
    /// rewrite the other contributors' clauses under the wrong locals.</summary>
    public bool Multifile { get; }
    public ShmoDynamicSeed(PredicateRef indicator, IReadOnlyList<byte[]> encodedClauses,
        bool multifile = false)
    {
        Indicator = indicator;
        EncodedClauses = encodedClauses;
        Multifile = multifile;
    }
}
