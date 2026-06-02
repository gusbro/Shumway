namespace Shumway.Embedding;

/// <summary>
/// In-memory representation of a Shumway bundle. A bundle bundles the source
/// for one or more named modules into a single deployable unit; the engine's
/// <see cref="PrologEngine.LoadBundle"/> consults each entry as if it had
/// been passed through <see cref="PrologEngine.ConsultString"/> directly.
/// </summary>
public sealed class Bundle
{
    public IReadOnlyList<BundleEntry> Entries { get; }

    /// <summary>Chunk 247 — names of .NET assemblies (file names,
    /// no path) shipped alongside the bundle that contain
    /// <c>[PrologPredicate]</c>-decorated static methods. The
    /// linker records these so the runtime
    /// <see cref="PrologEngine.LoadBundle(Bundle)"/> path can
    /// auto-register the foreign predicates without the embedder
    /// having to call <c>RegisterPredicates</c> for each type by
    /// hand. Empty for bundles linked without
    /// <c>--foreign-dll</c>.</summary>
    public IReadOnlyList<string> ForeignAssemblies { get; }

    /// <summary>Save-state chunk 264 — non-null when this bundle was
    /// produced by <see cref="PrologEngine.SaveState"/> rather than
    /// the regular shumway-link path. Carries consult history +
    /// dynamic clauses; <see cref="PrologEngine.RestoreState"/>
    /// consumes it. Bundles built by shumway-link have this null.</summary>
    public BundleSnapshot? Snapshot { get; }

    public Bundle(IReadOnlyList<BundleEntry> entries)
        : this(entries, foreignAssemblies: null) { }

    public Bundle(
        IReadOnlyList<BundleEntry> entries,
        IReadOnlyList<string>? foreignAssemblies)
        : this(entries, foreignAssemblies, snapshot: null) { }

    public Bundle(
        IReadOnlyList<BundleEntry> entries,
        IReadOnlyList<string>? foreignAssemblies,
        BundleSnapshot? snapshot)
    {
        Entries = entries;
        ForeignAssemblies = foreignAssemblies ?? Array.Empty<string>();
        Snapshot = snapshot;
    }
}

/// <summary>One module inside a bundle: its name, the original Prolog source,
/// and an optional pre-compiled bytecode payload. The source includes its
/// own <c>:- module(name).</c> directive (if any) so loading round-trips
/// through the standard consult path. The compiled payload is the
/// <see cref="CompiledModuleCodec"/> output for the module's clauses; when
/// present, future runtime paths can use it to skip parser / compiler work
/// at consult time.
///
/// <para><see cref="CompiledIl"/> (chunk 71) optionally holds a persisted
/// .NET assembly (.dll bytes) emitted by
/// <c>Shumway.Compiler.Il.PersistedIlBuilder</c>. When present, the load
/// path resolves each emitted predicate's <c>MethodInfo</c> and binds it
/// directly as a <c>PredicateDelegate</c>, skipping the Sigil emission
/// pass that warms <see cref="PrologEngine.IlPromotion"/> from the
/// bytecode blob alone.</para></summary>
public sealed class BundleEntry
{
    public string ModuleName { get; }
    public string Source { get; }
    public byte[]? CompiledBytecode { get; }
    public byte[]? CompiledIl { get; }

    /// <summary>Phase 17 — patch table for <see cref="CompiledIl"/>.
    /// Encoded via <see cref="IlPatchSiteCodec.Encode"/>. The persisted
    /// .dll bakes every atom-id / functor-id / resume-marker constant
    /// as a unique sentinel int (drawn from a reserved range); the
    /// patch table records each sentinel's absolute byte offset within
    /// <see cref="CompiledIl"/> along with the build-time name+arity.
    /// <see cref="PrologEngine.LoadBundle"/> reads the table, interns
    /// each name in the current process to get the runtime id, and
    /// overwrites the four bytes at the recorded offset before
    /// <c>Assembly.Load</c>. Empty for bundles built before Phase 17
    /// or without IL.</summary>
    public byte[]? CompiledIlPatches { get; }

    /// <summary>Phase 17 — per-method (slot, name, arity, method-name)
    /// table for the persisted IL. <see cref="PrologEngine.LoadBundle"/>
    /// uses this to register each delegate under the
    /// <em>runtime</em>-process functor id (intern name+arity in the
    /// current process), rather than the build-time id encoded in the
    /// method name (which doesn't match cross-process). Encoded via
    /// <see cref="IlPersistedEntryCodec.Encode"/>.</summary>
    public byte[]? CompiledIlEntries { get; }

    /// <summary>Chunk 178: per-predicate visibility list. Carries
    /// over the <see cref="ShmoObject.Defined"/> from each contributing
    /// .shmo. Used by <see cref="PrologEngine.LoadBundle(Bundle)"/>
    /// when the source is stripped: the engine has no source to
    /// re-consult, so it consults <em>this</em> list to know which
    /// predicates are public, which are dynamic, and which are local
    /// (mangled) — then plugs the entry's pre-compiled bytecode
    /// straight into the static link region. Empty list is fine for
    /// hand-built bundles or older formats that didn't persist it;
    /// the source-less load path is gated on this list being non-
    /// empty.</summary>
    public IReadOnlyList<ShmoDefinedPredicate> Defined { get; }

    /// <summary>Chunk 209 — clauses for <c>:- dynamic foo/N.</c>
    /// predicates carried as <see cref="TermCodec"/>-encoded blobs.
    /// See <see cref="ShmoObject.DynamicSeeds"/> for rationale.
    /// <see cref="PrologEngine.LoadBundle(Bundle)"/> deserialises each
    /// blob and seeds the engine's <c>_dynamicClauses[fid]</c> so
    /// assertz / retract / clause/2 see the source-declared
    /// initial clauses exactly as they would under
    /// <see cref="PrologEngine.ConsultString(string)"/>.</summary>
    public IReadOnlyList<ShmoDynamicSeed> DynamicSeeds { get; }

    public BundleEntry(
        string moduleName, string source,
        byte[]? compiledBytecode = null,
        byte[]? compiledIl = null,
        IReadOnlyList<ShmoDefinedPredicate>? defined = null,
        byte[]? compiledIlPatches = null,
        byte[]? compiledIlEntries = null,
        IReadOnlyList<ShmoDynamicSeed>? dynamicSeeds = null)
    {
        ModuleName = moduleName;
        Source = source;
        CompiledBytecode = compiledBytecode;
        CompiledIl = compiledIl;
        CompiledIlPatches = compiledIlPatches;
        CompiledIlEntries = compiledIlEntries;
        Defined = defined ?? Array.Empty<ShmoDefinedPredicate>();
        DynamicSeeds = dynamicSeeds ?? Array.Empty<ShmoDynamicSeed>();
    }
}
