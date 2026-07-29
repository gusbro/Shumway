using Shumway.Builtins;
using Shumway.Compiler.Il;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>Severity of a <see cref="LinkDiagnostic"/>.</summary>
public enum LinkSeverity { Info, Warning, Error }

/// <summary>One diagnostic emitted by <see cref="ShmoLinker.Link"/>:
/// a missing predicate, a duplicate-public collision, an unreachable
/// module (dropped from the output bundle), an info trace, etc. The
/// CLI surfaces these directly to the user; the API surface exposes
/// them as a list on <see cref="LinkResult.Diagnostics"/>.</summary>
public sealed class LinkDiagnostic
{
    public LinkSeverity Severity { get; }
    public string Code { get; }
    public string Message { get; }
    public string? Source { get; }

    public LinkDiagnostic(LinkSeverity severity, string code, string message, string? source = null)
    {
        Severity = severity;
        Code = code;
        Message = message;
        Source = source;
    }
}

/// <summary>Configuration for one link run. Pass an in-memory set of
/// <see cref="ShmoObject"/>s (load from disk via
/// <see cref="ShmoReader.ReadFromFile(string)"/>) and a list of entry
/// points. The linker resolves every call site reachable from the
/// entry-point + <c>ensure_linked</c> roots against the union of all
/// objects' <c>:- public</c>/<c>:- dynamic</c> declarations, the
/// builtin registry, and the always-loaded prelude.</summary>
public sealed class LinkConfig
{
    public IReadOnlyList<ShmoObject> Objects { get; init; } = Array.Empty<ShmoObject>();
    public IReadOnlyList<PredicateRef> EntryPoints { get; init; } = Array.Empty<PredicateRef>();

    /// <summary>The <c>--goal</c>'s CALL-position references (see
    /// <see cref="ExecutableEmitter.TryCollectGoalRefs"/>). Unlike
    /// <see cref="EntryPoints"/>, one of these may resolve to a builtin or a
    /// prelude predicate — the goal is any query the REPL would accept
    /// (<c>time(main)</c>, <c>writeln(hola)</c>). Only a reference that
    /// resolves NOWHERE (not user, not builtin, not prelude) fails the
    /// link.</summary>
    public IReadOnlyList<PredicateRef> GoalCallRefs { get; init; } = Array.Empty<PredicateRef>();

    /// <summary>The <c>--goal</c>'s speculative subterm references — callable
    /// terms in argument position (<c>main</c> inside <c>time(main)</c>). One
    /// that resolves to a user predicate becomes a reachability root, exactly
    /// like an entry point; anything else is silently ignored (it may be plain
    /// data).</summary>
    public IReadOnlyList<PredicateRef> GoalTermRefs { get; init; } = Array.Empty<PredicateRef>();

    /// <summary>When <c>true</c>, missing predicates downgrade from
    /// errors to warnings. The bundle is still produced, and the engine
    /// raises <c>existence_error/2</c> if a missing predicate is
    /// actually called at runtime. Defaults to <c>false</c> — the link
    /// fails fast on any missing reference.</summary>
    public bool AllowUndefined { get; init; }

    /// <summary>When <c>true</c>, the linker
    /// replaces each <see cref="BundleEntry.Source"/> with the empty
    /// string before serialising. The compiled bytecode in each
    /// entry is preserved. Defaults to <c>false</c> — source
    /// survives the link, as before.
    ///
    /// <para>Stripped bundles dispatch correctly: chunks 178/179 added
    /// the source-less <c>LoadEntryFromBytecode</c> path (the engine
    /// registers predicates straight from the precompiled bytecode +
    /// <see cref="ShmoObject.Defined"/> metadata), plus
    /// the <see cref="ShmoObject.DynamicSeeds"/> trailer so
    /// <c>:- dynamic foo/N.</c> predicates with source clauses keep
    /// their clauses too. Useful for size analysis and IP-protection
    /// archives; <c>listing/0</c> output and parser stack traces lose
    /// their textual source.</para></summary>
    public bool StripSource { get; init; }

    /// <summary>when <c>true</c>, the linker compiles
    /// every IL-eligible predicate to .NET IL via
    /// <see cref="Shumway.Compiler.Il.PersistedIlBuilder"/> and
    /// embeds the resulting assembly bytes in each bundle entry's
    /// <see cref="BundleEntry.CompiledIl"/> slot. At load time the
    /// engine binds the persisted IL directly as
    /// <c>PredicateDelegate</c>s, so no Sigil-emit work happens at
    /// runtime — the IL compile cost is paid once, ahead of time,
    /// and amortised over every query that hits a promoted
    /// predicate.
    ///
    /// <para>Requires each <see cref="ShmoObject"/> to carry its
    /// source (since PersistedIlBuilder routes through
    /// <c>ConsultString</c> + a warm-up query to materialise the
    /// CompiledPredicates). Combining
    /// <see cref="IncludeCompiledIl"/> with
    /// <see cref="StripSource"/> means "compile the IL from source,
    /// then strip the source from the persisted bundle" — the
    /// resulting .shum carries IL bytes but no recoverable Prolog
    /// source.</para></summary>
    public bool IncludeCompiledIl { get; init; }

    /// <summary>When true (with <see cref="IncludeCompiledIl"/>), drops the
    /// redundant WAM bodies of every IL-promoted predicate from the bundle —
    /// they run from their IL delegate, reached by functor id, never a WAM
    /// address. Produces a JIT-only bundle: under Native AOT the IL can't load
    /// and these predicates would be unrunnable.</summary>
    public bool StripWam { get; init; }

    /// <summary>Stage 9 (dead-region) report opt-in. When true, after the reachability
    /// walk the linker decodes the reached modules, resolves the externally-reachable
    /// seeds to functor ids, and runs <see cref="RegionReachability"/> to report how many
    /// predicate standalone forms WOULD be prunable if the bundle were region-compiled —
    /// an analysis diagnostic (<c>stage9_prunable</c>), not yet an applied prune (which
    /// also needs region-mode bundle compilation). Off by default (the decode + per-pred
    /// region build is not free).</summary>
    public bool RegionPruneReport { get; init; }

    /// <summary>When <c>true</c>, the linker emits a
    /// <c>local_shadows_public</c> WARNING for each linked module whose LOCAL
    /// predicate shares an indicator with another linked module's public —
    /// the C <c>static</c>-shadows-global shape. Legal either way (the local
    /// wins inside its own module); the <c>--map</c> file always lists these
    /// regardless of the flag. Defaults to <c>false</c>.</summary>
    public bool WarnShadow { get; init; }

    /// <summary>Stage 9b-3 — the APPLIED dead-region prune. When the bundle builds
    /// compiled IL (<see cref="IncludeCompiledIl"/>), it is region-compiled (absorbed
    /// members live inside region methods) and each ABSORBED-ONLY predicate (reached only
    /// as a <c>br</c>-member, computed by <see cref="RegionReachability"/> from the
    /// externally-reachable seeds) gets NO standalone IL method — removing the
    /// all-as-roots duplication. The predicate keeps its Tier-0 WAM as a safety fallback.
    /// ON by default (regions validated correct + faster on call-bound
    /// code; an unpruned region bundle is 2.3× bigger for nothing); set false
    /// (CLI <c>--no-region-prune</c>) to build one standalone IL method per predicate.
    /// Ignored for WAM-only bundles.</summary>
    public bool RegionPrune { get; init; } = true;

    /// <summary>Stage 10 — when non-null, append a human-readable disassembly of the WAM
    /// each bundle entry SHIPS (its final <c>CompiledBytecode</c>, AFTER any
    /// <see cref="StripWam"/> / region prune) to this path. The ground truth of the Tier-0
    /// code in the linked bundle, post-link — narrower than <c>shumway-compile --dump-wam</c>
    /// (which dumps a single .shmo before linking). Appends; delete between runs.</summary>
    public string? DumpWamPath { get; init; }

    /// <summary>Stage 10 — when non-null, append the Tier-1 IL the bundle SHIPS to this
    /// path. Implies <see cref="IncludeCompiledIl"/> (there is no IL to dump otherwise); the
    /// dump fires from inside the persisted-IL build, so it reflects exactly what runs —
    /// post-prune, region mode + forced roots when <see cref="RegionPrune"/> is set — not the
    /// all-as-roots superset <c>shumway-compile --dump-il</c> produces. Appends; delete
    /// between runs.</summary>
    public string? DumpIlPath { get; init; }

    /// <summary>When non-null, the linker writes info diagnostics
    /// describing its progress (modules visited, predicates reached,
    /// etc.) to this writer. Useful for CLI <c>--verbose</c> mode.</summary>
    public TextWriter? VerboseOut { get; init; }

    /// <summary>paths to .NET assemblies that contain
    /// <c>[Shumway.Embedding.PrologPredicate]</c>-decorated static
    /// methods. At link time the assemblies are reflected; every
    /// discovered <c>(name, arity)</c> indicator is added to the
    /// "resolved" set so a Prolog call to a foreign predicate stops
    /// being flagged as <c>missing_predicate</c>. The resulting
    /// bundle records each assembly's filename (no path) in
    /// <see cref="Bundle.ForeignAssemblies"/>; the runtime
    /// <see cref="PrologEngine.LoadBundle"/> looks for them adjacent
    /// to the bundle file (or the executable) and auto-registers
    /// the static methods via
    /// <see cref="PrologEngine.RegisterForeignAssembly"/>.
    ///
    /// <para>Instance-method <c>[PrologPredicate]</c>s are reflected
    /// here (so the linker doesn't flag a call to them as missing)
    /// but skipped by the runtime auto-loader — they need an
    /// instance the loader can't construct. The embedder calls
    /// <c>engine.RegisterPredicates(instance)</c> explicitly for
    /// those.</para></summary>
    public IReadOnlyList<string> ForeignAssemblies { get; init; } = Array.Empty<string>();

    /// <summary>ADR-024 — native C libraries (DLL/.so/.dylib) backing
    /// <c>:- native</c> functions, from <c>--native-dll</c>. Their filenames are
    /// recorded in <see cref="Bundle.NativeLibraries"/>; the runtime
    /// <see cref="PrologEngine.LoadBundle"/> auto-loads each via
    /// <see cref="PrologEngine.UseNativeLibrary"/> (probing adjacent to the bundle /
    /// executable). No reflection — native functions are declared in the source via
    /// <c>:- native</c> + the <c>:- c</c> prototype.</summary>
    public IReadOnlyList<string> NativeLibraries { get; init; } = Array.Empty<string>();

    /// <summary>Library inputs (C-archive semantics). Each entry is a
    /// <c>.shum</c> librarian archive (built by <c>shumway-lib</c>) broken
    /// out into its member <c>.shmo</c> objects. Unlike
    /// <see cref="Objects"/> — every one of which is always linked — a
    /// library's members are pulled in ONLY on demand, to satisfy a
    /// reference the explicit objects (plus builtins / prelude) leave
    /// unresolved. Libraries are searched FIFO: when more than one provides
    /// a symbol, the one earlier in this list wins. Pulls are transitive (a
    /// pulled member's own references pull further members) and feed the
    /// same cross-module LTO + reachability + prune as the explicit
    /// objects, so a pulled member is optimized exactly like one passed
    /// directly. Members no reference reaches are simply not linked.</summary>
    public IReadOnlyList<LinkLibrary> Libraries { get; init; } = Array.Empty<LinkLibrary>();

    /// <summary>ADR-038 — library search directories (from <c>--library-dir</c> /
    /// <c>SHUMWAY_LIBRARY_PATH</c>). A <c>:- use_module(library(X))</c> dependency
    /// not satisfied by an explicit object or a <c>.shum</c> library member is
    /// resolved here — <c>X.pl</c> is compiled and linked in (transitively),
    /// C-linker style: already-provided inputs win, source compilation is the last
    /// resort.</summary>
    public IReadOnlyList<string> LibraryDirs { get; init; } = Array.Empty<string>();

    /// <summary>When <c>true</c>, bake the precompiled internal prelude into
    /// the bundle as a source-stripped <c>$prelude</c> entry (its WAM bytecode,
    /// and — under <see cref="IncludeCompiledIl"/> — its Tier-1 IL). A
    /// bare-loaded engine (<see cref="PrologEngine.FromBundle(Bundle)"/>, the
    /// generated <c>--exe</c>) then gets the prelude precompiled instead of
    /// parsing + compiling the ~780-line prelude at startup; a normal engine
    /// that already consulted the prelude drops the redundant entry on load.
    /// Auto-enabled by the CLI for <c>--exe</c>; off by default (so ordinary
    /// <c>.shum</c> bundles aren't bloated with the prelude they re-consult
    /// anyway).</summary>
    public bool BakePrelude { get; init; }

    /// <summary>with <see cref="BakePrelude"/>, bake only the
    /// prelude predicates the linked program can REACH (the indicators the
    /// reachability walk resolved against the prelude, closed over the
    /// prelude's own call graph) instead of the whole ~780-line prelude.
    /// Off by default and OPT-IN on purpose: a runtime-constructed meta-call
    /// (<c>call(Atom)</c>, a goal read at runtime) can name a prelude
    /// predicate the static walk never saw — same contract as user-code
    /// pruning, and same escape hatch (<c>:- ensure_linked</c> the predicates
    /// you conjure dynamically). Interactive/REPL-style consumers that accept
    /// arbitrary queries should keep the full prelude.</summary>
    public bool PrunePrelude { get; init; }
}

/// <summary>One library input to <see cref="ShmoLinker.Link"/>: a
/// <c>.shum</c> librarian archive's member <c>.shmo</c> objects, in archive
/// order (the FIFO tie-break within a single library). See
/// <see cref="LinkConfig.Libraries"/>.</summary>
public sealed class LinkLibrary
{
    public string Name { get; }
    public IReadOnlyList<ShmoObject> Members { get; }
    public LinkLibrary(string name, IReadOnlyList<ShmoObject> members)
    {
        Name = name;
        Members = members;
    }
}

/// <summary>Outcome of one <see cref="ShmoLinker.Link"/> call.
/// <see cref="Success"/> is <c>true</c> only when no error diagnostic
/// fired. <see cref="Bundle"/> and <see cref="Bytes"/> are populated
/// when the link succeeded (or when <see cref="LinkConfig.AllowUndefined"/>
/// is set and only warnings fired); otherwise <c>null</c>.</summary>
public sealed class LinkResult
{
    public bool Success { get; }
    public Bundle? Bundle { get; }
    public byte[]? Bytes { get; }
    public IReadOnlyList<LinkDiagnostic> Diagnostics { get; }
    public IReadOnlyList<PredicateRef> ReachedPredicates { get; }
    public IReadOnlyList<string> ReachedModules { get; }
    public IReadOnlyList<string> UnreachableModules { get; }
    public IReadOnlyList<PredicateRef> MissingPredicates { get; }

    /// <summary>Stage 9 (dead-region elimination) — the externally-reachable SEED set:
    /// the reached predicates that must keep a standalone (trampoline-callable) form
    /// because they are callable BY NAME from outside a region's <c>br</c>-absorption
    /// (entry / ensure_linked roots + every reached public / dynamic predicate). The
    /// seeds the linker feeds to <c>RegionReachability</c> once the bundle is
    /// region-compiled; see <see cref="ShmoLinker.ComputeExternallyReachableSeeds"/>.</summary>
    public IReadOnlyList<QualifiedPredicateRef> ExternallyReachableSeeds { get; }

    /// <summary>The objects the linker actually resolved against: the explicit
    /// <see cref="LinkConfig.Objects"/> followed by any members pulled from
    /// <see cref="LinkConfig.Libraries"/> on demand (the pre-LTO form, same as
    /// the explicit objects). Equal to <c>config.Objects</c> when no libraries
    /// were given. The CLI feeds this to the <c>--map</c> writer so pulled
    /// library modules appear in the map alongside the explicit ones.</summary>
    public IReadOnlyList<ShmoObject> LinkedObjects { get; }

    public LinkResult(bool success, Bundle? bundle, byte[]? bytes,
        IReadOnlyList<LinkDiagnostic> diagnostics,
        IReadOnlyList<PredicateRef> reachedPredicates,
        IReadOnlyList<string> reachedModules,
        IReadOnlyList<string> unreachableModules,
        IReadOnlyList<PredicateRef> missingPredicates,
        IReadOnlyList<QualifiedPredicateRef>? externallyReachableSeeds = null,
        IReadOnlyList<ShmoObject>? linkedObjects = null)
    {
        Success = success;
        Bundle = bundle;
        Bytes = bytes;
        Diagnostics = diagnostics;
        ReachedPredicates = reachedPredicates;
        ReachedModules = reachedModules;
        UnreachableModules = unreachableModules;
        MissingPredicates = missingPredicates;
        ExternallyReachableSeeds = externallyReachableSeeds ?? Array.Empty<QualifiedPredicateRef>();
        LinkedObjects = linkedObjects ?? Array.Empty<ShmoObject>();
    }
}
