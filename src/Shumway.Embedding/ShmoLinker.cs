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

    /// <summary>When <c>true</c>, missing predicates downgrade from
    /// errors to warnings. The bundle is still produced, and the engine
    /// raises <c>existence_error/2</c> if a missing predicate is
    /// actually called at runtime. Defaults to <c>false</c> — the link
    /// fails fast on any missing reference.</summary>
    public bool AllowUndefined { get; init; }

    /// <summary>Phase 14 chunk 172: when <c>true</c>, the linker
    /// replaces each <see cref="BundleEntry.Source"/> with the empty
    /// string before serialising. The compiled bytecode in each
    /// entry is preserved. Defaults to <c>false</c> — source
    /// survives the link, as before.
    ///
    /// <para>Stripped bundles dispatch correctly: chunks 178/179 added
    /// the source-less <c>LoadEntryFromBytecode</c> path (the engine
    /// registers predicates straight from the precompiled bytecode +
    /// <see cref="ShmoObject.Defined"/> metadata), and chunk 209 added
    /// the <see cref="ShmoObject.DynamicSeeds"/> trailer so
    /// <c>:- dynamic foo/N.</c> predicates with source clauses keep
    /// their clauses too. Useful for size analysis and IP-protection
    /// archives; <c>listing/0</c> output and parser stack traces lose
    /// their textual source.</para></summary>
    public bool StripSource { get; init; }

    /// <summary>Chunk 192: when <c>true</c>, the linker compiles
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

    /// <summary>Stage 9b-3 — the APPLIED dead-region prune. When the bundle builds
    /// compiled IL (<see cref="IncludeCompiledIl"/>), it is region-compiled (absorbed
    /// members live inside region methods) and each ABSORBED-ONLY predicate (reached only
    /// as a <c>br</c>-member, computed by <see cref="RegionReachability"/> from the
    /// externally-reachable seeds) gets NO standalone IL method — removing the
    /// all-as-roots duplication. The predicate keeps its Tier-0 WAM as a safety fallback.
    /// ON by default since chunk 418 (regions validated correct + faster on call-bound
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

    /// <summary>Chunk 247 — paths to .NET assemblies that contain
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

/// <summary>
/// Resolves a set of <see cref="ShmoObject"/>s into a deployable
/// <see cref="Bundle"/>. The linker:
/// <list type="number">
/// <item>Builds the global namespace from every object's
/// <c>:- public</c> and <c>:- dynamic</c> sets. Two objects declaring
/// the same <c>:- public</c> indicator is a hard error
/// (CLAUDE.md invariant: public predicates are globally unique).</item>
/// <item>Walks the call graph from <see cref="LinkConfig.EntryPoints"/>
/// plus every object's <c>:- ensure_linked</c> indicators
/// (chunk 162). The unqualified edges resolve against the local module
/// first, then the union public+dynamic namespace, then builtins, then
/// the prelude.</item>
/// <item>Reports every unresolved edge as a
/// <see cref="LinkDiagnostic"/> with code <c>missing_predicate</c>
/// (error by default, warning under
/// <see cref="LinkConfig.AllowUndefined"/>).</item>
/// <item>Drops objects that no root reached (dead-code elimination) and
/// warns about each.</item>
/// <item>Emits a <see cref="Bundle"/> containing only the reached
/// objects' source + bytecode.</item>
/// </list>
/// </summary>
public static class ShmoLinker
{
    public static LinkResult Link(LinkConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        StandardBuiltins.EnsureRegistered();
        // The engine-resident meta-builtins (assertz / asserta /
        // retract / call/N / findall / catch / etc.) are normally
        // registered the first time a PrologEngine is constructed.
        // The linker doesn't spin one up, so register them here so
        // the missing-predicate filter sees the full builtin set.
        MetaBuiltins.EnsureRegistered();

        var diagnostics = new List<LinkDiagnostic>();
        void Emit(LinkSeverity sev, string code, string msg, string? source = null)
        {
            var d = new LinkDiagnostic(sev, code, msg, source);
            diagnostics.Add(d);
            if (config.VerboseOut is not null
                && (sev != LinkSeverity.Info
                    || config.VerboseOut == Console.Error
                    || config.VerboseOut == Console.Out))
            {
                config.VerboseOut.WriteLine($"shumway-link: {sev.ToString().ToLowerInvariant()}: {msg}");
            }
        }

        // ----- 0-foreign. Chunk 247/444 — reflect foreign DLLs up front -----
        // Their [PrologPredicate] indicators are "already available" and must
        // be known BEFORE the library pull pre-pass, so a library member is
        // never pulled to satisfy a reference a foreign predicate provides.
        // The names + indicators are reused by step 4b (no second reflection).
        var foreignIndicators = ReflectForeignAssemblies(
            config, Emit, out var foreignAssemblyNames);

        // ----- 0a. Library resolution (C-archive semantics) -----
        // Explicit .shmo objects always link; .shum library members are pulled
        // in on demand, FIFO, to satisfy otherwise-unresolved references
        // (transitive, to a fixpoint). Done BEFORE the LTO unfold so the full
        // object set — explicit + pulled — goes through cross-module
        // optimization together (the resolve-then-optimize order real LTO
        // linkers use); a pulled member is optimized exactly like one passed
        // directly.
        IReadOnlyList<ShmoObject> linkInput = config.Libraries.Count == 0
            ? config.Objects
            : PullLibraryMembers(config.Objects, config.Libraries,
                config.EntryPoints, foreignIndicators, Emit);

        // ----- 0. Chunk 411 — cross-module meta-wrapper unfold (the LTO pass) -----
        // V4 .shmo objects carry their raw static clauses (ClauseTerms). Detect
        // every module's wrapper templates, export the PUBLIC ones globally, and
        // rewrite each module's call sites against (own locals ∪ global publics);
        // modules whose rewrite gained a CROSS-module unfold (beyond what their
        // compile-time local pass already did) are recompiled from their clause
        // terms. Runs before everything else so the reachability walk and the
        // bundle see the final call graph.
        IReadOnlyList<ShmoObject> objects = CrossModuleUnfold(
            linkInput, Emit, out var ltoPublicWrappers);

        // ----- 1. Index objects, detect duplicate module names -----
        var byModule = new Dictionary<string, ShmoObject>();
        foreach (var obj in objects)
        {
            if (byModule.ContainsKey(obj.ModuleName))
            {
                // Chunk 440 — a module-less file compiles under its file's
                // base name, so this now fires only for a genuine clash:
                // two files declaring the same `:- module/1`, or two
                // module-less files with the same base name compiled from
                // different directories. The name is baked into the
                // bytecode's local mangling at compile time, so the linker
                // cannot rename one of them; the user must disambiguate.
                Emit(LinkSeverity.Error, "duplicate_module",
                    $"Module '{obj.ModuleName}' is defined in two .shmo objects. "
                    + "If neither source has a ':- module/1' directive, the module "
                    + "name is the source file's base name — rename one file or "
                    + "give it an explicit ':- module(name).' directive and recompile.",
                    obj.ModuleName);
                continue;
            }
            byModule[obj.ModuleName] = obj;
        }

        // ----- 2. Build global public + dynamic namespaces -----
        var globalPublic = new Dictionary<PredicateRef, string>();    // pred → module
        var globalDynamic = new Dictionary<PredicateRef, List<string>>(); // pred → modules
        // Index each module's locally-defined indicators for quick lookup.
        var moduleDefined = new Dictionary<string, Dictionary<PredicateRef, PredicateVisibility>>();
        foreach (var obj in objects)
        {
            var localMap = new Dictionary<PredicateRef, PredicateVisibility>();
            moduleDefined[obj.ModuleName] = localMap;
            foreach (var d in obj.Defined)
            {
                localMap[d.Indicator] = d.Visibility;
                if (d.Visibility == PredicateVisibility.Public)
                {
                    if (globalPublic.TryGetValue(d.Indicator, out var existingMod)
                        && existingMod != obj.ModuleName)
                    {
                        Emit(LinkSeverity.Error, "duplicate_public",
                            $"Public predicate {d.Indicator} is declared in both "
                            + $"'{existingMod}' and '{obj.ModuleName}'.",
                            obj.ModuleName);
                    }
                    else
                    {
                        globalPublic[d.Indicator] = obj.ModuleName;
                    }
                }
                else if (d.Visibility == PredicateVisibility.Dynamic)
                {
                    if (!globalDynamic.TryGetValue(d.Indicator, out var list))
                    {
                        list = new List<string>();
                        globalDynamic[d.Indicator] = list;
                    }
                    list.Add(obj.ModuleName);
                }
            }
        }

        // ----- 3. Compile the prelude as an implicit object -----
        // The prelude is always loaded at engine startup, so its
        // public predicates should resolve without the user supplying
        // a prelude.shmo on the linker command line.
        var preludeObj = ShmoCompiler.CompileSource(Prelude.Source);
        var preludePublics = new HashSet<PredicateRef>(
            preludeObj.Defined
                .Where(d => d.Visibility == PredicateVisibility.Public)
                .Select(d => d.Indicator));

        // ----- 4. Snapshot the builtin set -----
        var builtinPredicates = new HashSet<PredicateRef>();
        foreach (var b in BuiltinsRegistry.AllEntries())
            builtinPredicates.Add(new PredicateRef(b.Name, b.Arity));
        // Add the structural / engine-private goals the WAM compiler
        // emits as call targets but which never resolve against a user
        // .shmo (cuts, the synthetic launcher, fail, etc.).
        builtinPredicates.Add(new PredicateRef("true", 0));
        builtinPredicates.Add(new PredicateRef("fail", 0));
        builtinPredicates.Add(new PredicateRef("false", 0));

        // ----- 4b. Chunk 247 / chunk 444: fold the foreign-DLL
        //          [PrologPredicate] indicators into the resolved builtin
        //          set. They are reflected ONCE up front (step 0-foreign)
        //          so the library pull pre-pass already treated them as
        //          available — a library member is never pulled to satisfy
        //          a reference a foreign predicate provides. -----
        builtinPredicates.UnionWith(foreignIndicators);

        // ----- 5. Resolve roots -----
        // Phase 18: relax the ":- public required" rule for entry points.
        // Other Prolog engines treat the runtime goal's predicate
        // references as if the user had typed them at the top level —
        // any defined predicate, local or public, is reachable. The
        // resolution algorithm:
        //   1. Match against globalPublic / globalDynamic first
        //      (cross-module-visible).
        //   2. Else scan all modules for a LOCAL definition.
        //      0 matches → entry_not_found.
        //      1 match  → use it (entry-point local promotion).
        //      2+ matches → ambiguous_entry; lists the colliding
        //      modules so the user can qualify the entry or mark
        //      the intended definition :- public.
        var roots = new List<(string Module, PredicateRef Pred, string Origin)>();
        foreach (var ep in config.EntryPoints)
        {
            string? mod = ResolveEntryPointModule(ep, globalPublic, globalDynamic,
                moduleDefined, out string? ambiguityMessage);
            if (ambiguityMessage is not null)
            {
                Emit(LinkSeverity.Error, "ambiguous_entry", ambiguityMessage, null);
                continue;
            }
            if (mod is null)
            {
                Emit(LinkSeverity.Error, "entry_not_found",
                    $"Entry point {ep} is not defined in any linked .shmo.", null);
                continue;
            }
            roots.Add((mod, ep, $"entry {ep}"));
        }
        foreach (var obj in objects)
        {
            foreach (var el in obj.EnsureLinked)
            {
                string? mod = ResolveDefiningModule(el, globalPublic, globalDynamic);
                if (mod is null)
                {
                    if (preludePublics.Contains(el) || builtinPredicates.Contains(el))
                        continue;  // already-available — silently ignore.
                    Emit(config.AllowUndefined ? LinkSeverity.Warning : LinkSeverity.Error,
                        "ensure_linked_unresolved",
                        $"ensure_linked target {el} (from '{obj.ModuleName}') is not "
                        + ":- public or :- dynamic in any linked .shmo.", obj.ModuleName);
                    continue;
                }
                roots.Add((mod, el, $"ensure_linked from '{obj.ModuleName}'"));
            }
        }
        // Chunk 411 — public meta-wrappers stay linked even when the LTO unfold
        // removed their last static call site: a runtime-built goal may still
        // dispatch to them, and the unfold must never shrink the linked set.
        foreach (var (mod, pred) in ltoPublicWrappers)
            roots.Add((mod, pred, $"lto wrapper in '{mod}'"));

        // ----- 6. Reachability walk -----
        var reached = new HashSet<(string, PredicateRef)>();
        var reachedModules = new HashSet<string>();
        var missing = new HashSet<PredicateRef>();
        // Chunk 441 — Arity call semantics for arity-compiled modules:
        // META-CALLING an undeclared predicate is VALID in Arity (it
        // simply fails when nothing was asserted, and works once
        // something is). An unresolved edge whose referencing module was
        // compiled with arity_compat AND whose ShmoCallEdge.IsMeta
        // marker is set (every in-module reference sits inside a
        // meta-call argument) is DEFERRED into pendingArityMeta instead
        // of erroring. After the walk, each pending target whose every
        // unresolved reference was such a meta edge (no DIRECT or
        // non-arity reference put it in `missing`) is registered as an
        // implicit EMPTY DYNAMIC predicate — exactly as if the first
        // referencing file had declared `:- dynamic Name/Arity.` with
        // zero clauses — so the bundle gets an empty trampoline: calls
        // fail cleanly at runtime and a later assertz works through the
        // normal dynamic machinery (implicit_dynamic). A DIRECT body
        // goal to an undefined predicate stays today's
        // missing_predicate error, even in an arity module.
        // implicitDynamics is keyed by the module the implicit
        // declaration is attributed to; folded into that module's
        // bundle-entry Defined list in step 8.
        var implicitDynamics = new Dictionary<string, List<PredicateRef>>();
        var pendingArityMeta =
            new Dictionary<PredicateRef, List<(string Module, PredicateRef Caller)>>();
        var qrefHandledModules = new HashSet<string>();
        var queue = new Queue<(string Module, PredicateRef Pred)>();
        foreach (var r in roots)
            queue.Enqueue((r.Module, r.Pred));

        while (queue.Count > 0)
        {
            var (curMod, curPred) = queue.Dequeue();
            if (!reached.Add((curMod, curPred))) continue;
            reachedModules.Add(curMod);

            // First time we touch this module: also walk its
            // module-level qualified references (Mod:Pred call sites).
            if (qrefHandledModules.Add(curMod)
                && byModule.TryGetValue(curMod, out var curObj))
            {
                foreach (var qr in curObj.QualifiedRefs)
                {
                    var target = new PredicateRef(qr.Name, qr.Arity);
                    if (!byModule.TryGetValue(qr.Module, out var qmod))
                    {
                        Emit(config.AllowUndefined ? LinkSeverity.Warning : LinkSeverity.Error,
                            "missing_predicate",
                            $"Qualified call {qr} from '{curMod}' references a module "
                            + "that is not in the link set.", curMod);
                        missing.Add(target);
                        continue;
                    }
                    if (!moduleDefined[qr.Module].TryGetValue(target, out var vis)
                        || vis != PredicateVisibility.Public)
                    {
                        Emit(config.AllowUndefined ? LinkSeverity.Warning : LinkSeverity.Error,
                            "missing_predicate",
                            $"Qualified call {qr} from '{curMod}': '{qr.Module}' does not "
                            + $"export {target} as :- public.", curMod);
                        missing.Add(target);
                        continue;
                    }
                    queue.Enqueue((qr.Module, target));
                }
            }

            if (!byModule.TryGetValue(curMod, out var modObj)) continue;
            if (!modObj.CallGraph.TryGetValue(curPred, out var edges)) continue;
            foreach (var callEdge in edges)
            {
                var edge = callEdge.Target;
                // 1) Module-local definition wins.
                if (moduleDefined[curMod].ContainsKey(edge))
                {
                    queue.Enqueue((curMod, edge));
                    continue;
                }
                // 2) Global public namespace.
                if (globalPublic.TryGetValue(edge, out var defMod))
                {
                    queue.Enqueue((defMod, edge));
                    continue;
                }
                // 3) Global dynamic namespace. Walk every module that
                //    declared it dynamic — its asserted clauses (if
                //    any) may transitively call user predicates.
                if (globalDynamic.TryGetValue(edge, out var dynModules))
                {
                    foreach (var dm in dynModules)
                        queue.Enqueue((dm, edge));
                    continue;
                }
                // 4) Builtin? Always resolves.
                if (builtinPredicates.Contains(edge)) continue;
                // 5) Prelude public? Always resolves.
                if (preludePublics.Contains(edge)) continue;
                // 6) Chunk 441 — a META-marked edge from an Arity-
                //    compiled module: defer the decision. If by the end
                //    of the walk the target collected ONLY such
                //    references (nothing put it in `missing`), it links
                //    as an implicit empty dynamic below; otherwise the
                //    direct/non-arity reference already errored.
                if (modObj.ArityCompat && callEdge.IsMeta)
                {
                    if (!pendingArityMeta.TryGetValue(edge, out var pendList))
                    {
                        pendList = new List<(string, PredicateRef)>();
                        pendingArityMeta[edge] = pendList;
                    }
                    pendList.Add((curMod, curPred));
                    continue;
                }
                // 7) Anything else is missing.
                if (missing.Add(edge))
                {
                    Emit(config.AllowUndefined ? LinkSeverity.Warning : LinkSeverity.Error,
                        "missing_predicate",
                        $"Predicate {edge} (called by '{curMod}':{curPred}) is not "
                        + "defined in any linked .shmo, builtin, or prelude.",
                        curMod);
                }
            }
        }

        // ----- 6a-arity. Chunk 441 — implicit empty dynamics -----
        // Decide each deferred meta-only target now that every
        // unresolved reference has been seen. A target also referenced
        // DIRECTLY (or from a non-arity module) is in `missing` — its
        // error already fired; skip it. Otherwise register the target as
        // an empty dynamic attributed to the FIRST referencing module
        // (multiple arity modules meta-referencing the same target get
        // exactly one registration). An empty dynamic has no clauses and
        // no call graph, so registering after the walk adds no
        // reachability; marking it reached + Dynamic in moduleDefined
        // makes it a prune seed in step 6b exactly like a declared
        // dynamic (dynamics keep a by-name-callable standalone form).
        foreach (var (target, refs) in pendingArityMeta)
        {
            if (missing.Contains(target)) continue;
            string ownerMod = refs[0].Module;
            moduleDefined[ownerMod][target] = PredicateVisibility.Dynamic;
            if (!globalDynamic.TryGetValue(target, out var implList))
            {
                implList = new List<string>();
                globalDynamic[target] = implList;
            }
            implList.Add(ownerMod);
            if (!implicitDynamics.TryGetValue(ownerMod, out var perMod))
            {
                perMod = new List<PredicateRef>();
                implicitDynamics[ownerMod] = perMod;
            }
            perMod.Add(target);
            reached.Add((ownerMod, target));
            Emit(LinkSeverity.Info, "arity_implicit_dynamic",
                $"arity: undeclared predicate {target} (meta-called from "
                + $"'{ownerMod}') linked as empty dynamic.", ownerMod);
        }

        // ----- 6b. Stage 9 (dead-region elimination) seed set -----
        // The predicates that must keep a STANDALONE (trampoline-callable) form because
        // they are callable BY NAME from outside a region's br-absorption. This is what
        // the linker will feed to RegionReachability once the bundle is region-compiled
        // (Stage 9b); for now we compute + report + expose it. A soundness
        // over-approximation, never under.
        var stage9Seeds = ComputeExternallyReachableSeeds(
            roots.Select(r => (r.Module, r.Pred)), reached, moduleDefined);
        Emit(LinkSeverity.Info, "prune_seeds",
            $"code pruning: {stage9Seeds.Count} of {reached.Count} reached predicate(s) "
            + "are callable by name from outside and keep a standalone compiled form.");

        // ----- 6c. Stage 9 dead-region analysis (DRY-RUN REPORT only) -----
        // The fid bridge: decode the reached modules into CompiledPredicates (their
        // functor ids + call graph, interned consistently in the global tables), resolve
        // the (module, PredicateRef) seeds to functor ids, and run the dead-region
        // reachability. The PRUNE SET is `absorbedOnly` = fullReachable − regionReachable
        // (live but reached ONLY as a br-member of a live region) — NEVER the unreachable
        // / dead-code bucket, so a meta-call-only predicate (absorbed by nothing, appears
        // unreachable) is kept (the prune rule that avoids hardening ensure_linked).
        //
        // §9d: this is an APPROXIMATION (it decodes only each module's own .shmo bytecode).
        // The APPLIED prune is computed inside BundleWriter.CompileEntryToIl over the warm-up
        // engine's EXACT calleeMap (user module + prelude + every reached callee), so the
        // absorbed-only set matches the real region membership and a meta-callable absorbed
        // predicate keeps its standalone form. Gated on --prune-report so a plain IL link
        // doesn't print per-module figures that diverge from what actually ships.
        var stage9PrunableFids = new HashSet<int>();
        var stage9ForcedRoots = new HashSet<int>();
        if (config.RegionPruneReport)
        {
            var predicates = new Dictionary<int, CompiledPredicate>();
            foreach (var obj in objects)
            {
                if (!reachedModules.Contains(obj.ModuleName) || obj.Bytecode.Length == 0) continue;
                foreach (var p in CompiledModuleCodec.Decode(obj.Bytecode).Predicates)
                    predicates[p.FunctorId] = p;
            }
            if (predicates.Count > 0)
            {
                var byName = new Dictionary<(string, int), int>();
                foreach (int fid in predicates.Keys)
                {
                    var (atomId, arity) = FunctorTable.Lookup(fid);
                    byName[(AtomTable.GetById(atomId)?.Name ?? "", arity)] = fid;
                }
                var seedFids = ResolveSeedFids(stage9Seeds, byName);
                var ic = new IlPredicateCompiler();

                // Stage 9c: cost-based root selection. Promote shared members to their own
                // roots (excluded from absorption) to cut inter-root duplication; passed
                // to the region-membership probe as `extraExcluded`. minSaving (bytes)
                // tunes the trade-off — env-overridable for measurement.
                // Default 64 B: measured Blint optimum — below it (e.g. 0) over-promotes
                // tiny shared predicates whose trampolines cost more than the dedup saving
                // (100 roots / 925 KB vs 86 / 914 KB at 64). Env-overridable.
                long minSaving = long.TryParse(
                    Environment.GetEnvironmentVariable("SHUMWAY_REGION_ROOT_MINSAVE"), out var ms)
                    ? ms : 64;
                stage9ForcedRoots = RegionRootSelector.ComputeForcedRoots(
                    predicates.Keys,
                    (f, ex) => ic.RegionMemberFids(predicates[f], predicates, ex),
                    f => predicates.TryGetValue(f, out var p) ? p.Bytecode.Length : 0,
                    minSaving);
                if (stage9ForcedRoots.Count > 0)
                    Emit(LinkSeverity.Info, "prune_roots",
                        $"prune analysis: {stage9ForcedRoots.Count} shared predicate(s) "
                        + "get their own compiled method instead of being duplicated "
                        + $"into callers (min saving {minSaving} bytes).");

                // Region-aware reachability (intra-region calls are br, don't reach the
                // standalone) vs plain reachability (every call trampolines). The
                // difference is the predicates that are LIVE but only reached as absorbed
                // members — the genuine region-prune benefit; predicates in neither set
                // are ordinary dead code (droppable independently of regions). Both honour
                // the Stage-9c promotions via `extraExcluded`.
                var regionReachable = RegionReachability.TrampolineReachable(
                    predicates, seedFids,
                    fid => ic.RegionMemberFids(predicates[fid], predicates, stage9ForcedRoots));
                var fullReachable = RegionReachability.TrampolineReachable(
                    predicates, seedFids, fid => new[] { fid });
                foreach (int f in fullReachable)
                    if (!regionReachable.Contains(f)) stage9PrunableFids.Add(f);
                int deadCode = predicates.Count - fullReachable.Count;
                Emit(LinkSeverity.Info, "prune_analysis",
                    $"prune analysis: of {predicates.Count} compiled predicates, "
                    + $"{stage9PrunableFids.Count} are only called from inside shared "
                    + $"methods (standalone form prunable) and {deadCode} are unreachable "
                    + $"— {stage9PrunableFids.Count + deadCode} prunable standalone forms "
                    + $"(from {seedFids.Count} entry seeds).");
            }
        }

        // ----- 7. Unreachable modules → warning + drop -----
        var unreachable = new List<string>();
        foreach (var obj in objects)
        {
            if (reachedModules.Contains(obj.ModuleName)) continue;
            unreachable.Add(obj.ModuleName);
            Emit(LinkSeverity.Warning, "unreachable_module",
                $"Module '{obj.ModuleName}' is not reachable from any entry / "
                + "ensure_linked / qualified-ref root and was dropped from the bundle.",
                obj.ModuleName);
        }

        // ----- 8. Decide success and build bundle -----
        bool hasErrors = diagnostics.Any(d => d.Severity == LinkSeverity.Error);
        bool success = !hasErrors;
        Bundle? bundle = null;
        byte[]? bytes = null;
        if (success || config.AllowUndefined)
        {
            // Phase 18: gather per-module entry-point promotions. When
            // an --entry pred/N was satisfied by a LOCAL definition in
            // module M (no `:- public pred/N` in the source), prepend
            // `:- public pred/N.` to that module's bundled source so
            // the LoadBundle path's ConsultString sees it as public —
            // otherwise the engine's query-time mangling would target
            // M$pred/N while the bare-name query types pred/N and the
            // dispatcher misses. Also null out the precompiled
            // bytecode for those modules: the .shmo bytecode was
            // compiled with pred/N as local (mangled), so it would
            // disagree with the now-public consult result. LoadBundle
            // re-consults the augmented source; one-time cost at load
            // in exchange for correctness without an alias mechanism
            // in the engine.
            var promotionsByModule = new Dictionary<string, List<PredicateRef>>();
            foreach (var ep in config.EntryPoints)
            {
                // Public/dynamic entries don't need promotion.
                if (globalPublic.ContainsKey(ep)) continue;
                if (globalDynamic.ContainsKey(ep)) continue;
                // Find the module the resolver actually picked (which
                // was the unique local-defining module — ambiguity
                // would have errored above).
                foreach (var (modName, defs) in moduleDefined)
                {
                    if (defs.ContainsKey(ep))
                    {
                        if (!promotionsByModule.TryGetValue(modName, out var list))
                        {
                            list = new List<PredicateRef>();
                            promotionsByModule[modName] = list;
                        }
                        list.Add(ep);
                        break;
                    }
                }
            }

            // Stable order: the linker preserves the order objects came
            // in, but drops the unreachable ones.
            var entries = new List<BundleEntry>();
            foreach (var obj in objects)
            {
                if (!reachedModules.Contains(obj.ModuleName)) continue;
                // Chunk 179: when StripSource is requested, also strip
                // the per-clause source positions AND the in-bytecode
                // Meta/DbgInfo opcodes. If obj.Source is still present
                // (Debug compile), recompile under Release through
                // ShmoCompiler — that's the canonical strip path and
                // covers both pieces. If obj.Source is already empty
                // (Release compile), the bytecode is already stripped
                // and we pass it through. Net result: a --strip'd
                // .shum has the same artifact shape regardless of
                // whether its sources were compiled in Debug or
                // Release upstream.
                string entrySource;
                byte[]? entryBytecode;
                if (config.StripSource && !string.IsNullOrEmpty(obj.Source))
                {
                    var restripped = ShmoCompiler.CompileSource(
                        obj.Source, obj.ModuleName, ShmoBuildMode.Release);
                    entrySource = "";
                    entryBytecode = restripped.Bytecode.Length > 0
                        ? restripped.Bytecode : null;
                }
                else
                {
                    entrySource = config.StripSource ? "" : obj.Source;
                    entryBytecode = obj.Bytecode.Length > 0 ? obj.Bytecode : null;
                }

                // Phase 18: apply entry-point promotions for this module.
                // Recompile via ShmoCompiler (which runs DCG / Meta /
                // PhraseTransform) so the new bytecode matches the
                // augmented source. The BundleWriter's own
                // CompileEntryToBytes path uses bare ModuleCompiler and
                // would NotSupportedException on any DCG rule.
                if (promotionsByModule.TryGetValue(obj.ModuleName, out var promoted)
                    && !string.IsNullOrEmpty(entrySource))
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var pr in promoted)
                        sb.Append(":- public ").Append(pr.Name).Append('/')
                          .Append(pr.Arity).Append(".\n");
                    sb.Append(entrySource);
                    string augmented = sb.ToString();
                    // Recompile augmented source so the bundled bytecode
                    // matches what LoadBundle's ConsultString would
                    // produce on it. Without this the precompiled-cache
                    // substitution at SetupQueryFromTerm would slot in
                    // bytecode that still has the entry mangled.
                    var recompiled = ShmoCompiler.CompileSource(
                        augmented, obj.ModuleName,
                        config.StripSource ? ShmoBuildMode.Release : ShmoBuildMode.Debug);
                    entrySource = config.StripSource ? "" : augmented;
                    entryBytecode = recompiled.Bytecode.Length > 0
                        ? recompiled.Bytecode : null;
                }

                // Chunk 441 — fold the implicit empty dynamics this
                // (arity-compiled) module's unresolved references created
                // into its Defined list, exactly as a source-level
                // `:- dynamic Name/Arity.` with zero clauses would have:
                // the source-less LoadBundle path registers each as a
                // dynamic functor with an empty clause list, and query
                // setup emits the fail-only stub trampoline. A clauseless
                // dynamic declaration adds no bytecode, so the entry's
                // CompiledBytecode is untouched; source-bearing entries
                // get the directive prepended so the ConsultString load
                // path registers the same declaration.
                IReadOnlyList<ShmoDefinedPredicate> entryDefined = obj.Defined;
                if (implicitDynamics.TryGetValue(obj.ModuleName, out var implicits)
                    && implicits.Count > 0)
                {
                    var augmentedDefined = new List<ShmoDefinedPredicate>(obj.Defined);
                    foreach (var p in implicits)
                        augmentedDefined.Add(new ShmoDefinedPredicate(
                            p, PredicateVisibility.Dynamic));
                    entryDefined = augmentedDefined;
                    if (!string.IsNullOrEmpty(entrySource))
                    {
                        var dsb = new System.Text.StringBuilder();
                        foreach (var p in implicits)
                            dsb.Append(":- dynamic ").Append(p.Name).Append('/')
                               .Append(p.Arity).Append(".\n");
                        dsb.Append(entrySource);
                        entrySource = dsb.ToString();
                    }
                }

                entries.Add(new BundleEntry(
                    moduleName: obj.ModuleName,
                    source: entrySource,
                    compiledBytecode: entryBytecode,
                    compiledIl: null,
                    defined: entryDefined,
                    dynamicSeeds: obj.DynamicSeeds));
            }
            // Bake the precompiled prelude so a bare-loaded engine
            // (PrologEngine.FromBundle / the generated --exe) gets it without
            // parsing + compiling the ~780-line prelude at startup. Source-
            // stripped (CompiledBytecode + Defined), so --with-compiled-il also
            // IL-compiles it via the per-entry path. A normal engine that
            // already has the prelude drops this entry on load.
            if (config.BakePrelude)
            {
                entries.Add(new BundleEntry(
                    moduleName: preludeObj.ModuleName,
                    source: "",
                    compiledBytecode: preludeObj.Bytecode,
                    compiledIl: null,
                    defined: preludeObj.Defined,
                    dynamicSeeds: preludeObj.DynamicSeeds));
                Emit(LinkSeverity.Info, "prelude_baked",
                    "baked the precompiled prelude into the bundle "
                    + "(bare-load startup skips prelude compilation).");
            }
            // Chunk 179: the chunk-172 "stripped_bundle" warning is gone —
            // stripped bundles now dispatch correctly via chunk 178's
            // source-less LoadBundle path.
            bundle = new Bundle(entries, foreignAssemblyNames);
            // Chunk 192: --with-compiled-il routes the bundle through
            // BundleWriter.ToBytes, which (under includeCompiledIl=true)
            // runs PersistedIlBuilder per entry to materialise IL for
            // every eligible predicate. The resulting bytes carry the
            // IL .dll alongside the bytecode blob. Default path is the
            // linker's direct SerialiseBundle (no IL emit at link time).
            // BundleWriter still validates via a sub-engine ConsultString;
            // the linker has already done the heavy lifting so a failure
            // here is a real bug, not an operator-ordering quirk.
            // Stage 10: --dump-il has no IL to dump unless the bundle builds it, so it
            // implies --with-compiled-il (the dump fires from inside the persisted build).
            if (config.IncludeCompiledIl || config.DumpIlPath is not null)
            {
                // Stage 9b-3: region-prune region-compiles the bundle (so absorbed
                // members live inside region methods) and skips emitting a standalone IL
                // method for each absorbed-only predicate. Since chunk 418 the
                // region-compile decision for the persisted build lives in
                // BundleWriter.CompileEntryToIl (region iff pruning), so the linker no
                // longer toggles RegionCompile here.
                var savedForcedRoots = Shumway.Compiler.Il.IlPredicateCompiler.RegionForcedRootFids;
                // Stage 10: route the persisted-IL emit's per-method dump (FinishPersistedEmit)
                // to --dump-il, so the dump is EXACTLY the IL this bundle ships — post-prune,
                // region mode + forced roots when the prune is on (the default).
                var savedIlDump = Shumway.Compiler.Il.IlPredicateCompiler.IlDumpPath;
                if (config.DumpIlPath is not null)
                {
                    Shumway.Compiler.Il.IlPredicateCompiler.IlDumpPath = config.DumpIlPath;
                    File.AppendAllText(config.DumpIlPath,
                        $";;; ===== shumway-link IL dump (regionPrune={config.RegionPrune}, "
                        + $"stripWam={config.StripWam}) =====\n");
                }
                try
                {
                    // Stage 9d: the prune (absorbed-only set + Stage-9c root selection) is
                    // computed INSIDE BundleWriter.CompileEntryToIl, over the warm-up
                    // engine's EXACT calleeMap (user module + prelude + every reached
                    // callee) — the same set the IL compile uses. We pass it the seeds (the
                    // externally-reachable by-name-callable predicates); it resolves them to
                    // functor ids and installs the forced roots per entry. The step-6c
                    // computation above is now just the dry-run REPORT (a per-module
                    // approximation), not the applied prune.
                    bytes = BundleWriter.ToBytes(bundle,
                        includeCompiledBytecode: true,
                        includeCompiledIl: true,
                        stripWam: config.StripWam,
                        regionPruneSeeds: config.RegionPrune ? stage9Seeds : null);
                }
                finally
                {
                    Shumway.Compiler.Il.IlPredicateCompiler.RegionForcedRootFids = savedForcedRoots;
                    Shumway.Compiler.Il.IlPredicateCompiler.IlDumpPath = savedIlDump;
                }
                if (config.DumpIlPath is not null)
                    Emit(LinkSeverity.Info, "dump_il",
                        $"IL dump -> {config.DumpIlPath} (the shipped Tier-1 IL).");
                // Re-read the bytes so the in-memory Bundle reflects
                // the persisted-IL slots the writer populated.
                bundle = BundleReader.FromBytes(bytes);
            }
            else
            {
                // Skip the bundle writer's own validate-by-consult pass:
                // the linker has already done the heavy lifting (call-
                // graph checks, missing-predicate reporting), and a
                // sub-engine ConsultString on a multi-module link can
                // fail for reasons (operator ordering, prelude
                // assumptions) that the linker shouldn't second-guess.
                bytes = SerialiseBundle(bundle);
            }

            // Stage 10: dump the WAM each entry actually SHIPS — its final
            // CompiledBytecode, AFTER any --strip-wam / region prune (the IL branch
            // re-read `bundle` from the post-strip bytes above).
            if (config.DumpWamPath is not null)
            {
                DumpBundleWam(bundle, config.DumpWamPath);
                Emit(LinkSeverity.Info, "dump_wam",
                    $"WAM dump -> {config.DumpWamPath} (the shipped Tier-0 bytecode).");
            }
        }

        var reachedList = reached.Select(r => r.Item2).Distinct().ToList();
        var seedList = stage9Seeds
            .Select(s => new QualifiedPredicateRef(s.Module, s.Pred.Name, s.Pred.Arity))
            .ToList();
        return new LinkResult(
            success: success,
            bundle: bundle,
            bytes: bytes,
            diagnostics: diagnostics,
            reachedPredicates: reachedList,
            reachedModules: reachedModules.ToList(),
            unreachableModules: unreachable,
            missingPredicates: missing.ToList(),
            externallyReachableSeeds: seedList,
            linkedObjects: linkInput);
    }

    /// <summary>Stage 9 (dead-region elimination): the externally-reachable SEED set —
    /// the reached predicates that must keep a STANDALONE (trampoline-callable) form
    /// because they are callable BY NAME from outside a region's <c>br</c>-absorption,
    /// so the dead-region prune must NEVER drop them. The set:
    /// <list type="bullet">
    ///   <item>the entry-point and <c>:- ensure_linked</c> roots
    ///     (<paramref name="reachedRoots"/>) — invoked by name by the runtime;</item>
    ///   <item>every reached PUBLIC predicate — the global namespace; another module or
    ///     the embedding host can call it by name;</item>
    ///   <item>every reached DYNAMIC predicate — called by name + asserted/retracted, and
    ///     never region-compiled (<c>enter_dynamic</c>). This INCLUDES <c>:- visible</c>,
    ///     which the compiler records as <see cref="PredicateVisibility.Dynamic"/>
    ///     (its Arity-Prolog alias, chunk 265).</item>
    /// </list>
    /// A soundness over-approximation (keep too much, never prune something needed). This
    /// is the set the linker passes as <c>RegionReachability.TrampolineReachable</c>'s
    /// <c>externallyReachable</c> argument once the bundle is region-compiled. Pure;
    /// public for direct testing.</summary>
    public static HashSet<(string Module, PredicateRef Pred)> ComputeExternallyReachableSeeds(
        IEnumerable<(string Module, PredicateRef Pred)> reachedRoots,
        IEnumerable<(string Module, PredicateRef Pred)> reached,
        IReadOnlyDictionary<string, Dictionary<PredicateRef, PredicateVisibility>> moduleDefined)
    {
        var seeds = new HashSet<(string, PredicateRef)>();
        foreach (var r in reachedRoots) seeds.Add(r);   // entry / ensure_linked roots
        foreach (var (mod, pred) in reached)
            if (moduleDefined.TryGetValue(mod, out var defs)
                && defs.TryGetValue(pred, out var vis)
                && vis is PredicateVisibility.Public or PredicateVisibility.Dynamic)
                seeds.Add((mod, pred));
        return seeds;
    }

    /// <summary>Chunk 411 — the cross-module meta-wrapper unfold (LTO pass).
    /// Decodes each V4 module's raw clause terms, detects wrapper templates
    /// (<see cref="Shumway.Compiler.Parsing.MetaWrapperUnfold"/>), exports the
    /// PUBLIC ones into a global registry, and rewrites every module against
    /// (own locals ∪ global publics) — locals shadow publics, matching call
    /// resolution. A module is recompiled (from its clause terms, via
    /// <see cref="ShmoCompiler.CompileFromParts"/>) only when the cross-module
    /// part contributed a rewrite its compile-time LOCAL unfold (chunk 407)
    /// didn't already produce — detected per clause: full-rewrite changed it
    /// while local-only left it alone. (A clause with BOTH a local and a cross
    /// site in it is conservatively skipped — the cross site keeps calling the
    /// public wrapper, which is correct, just un-optimized.) Wrapper modules
    /// themselves are never modified; their standalone predicates remain for
    /// runtime-built goals. Pre-V4 objects (no clause terms) pass through
    /// untouched — they neither export wrappers nor get rewritten.</summary>
    /// <param name="publicWrappers">OUT — every PUBLIC wrapper detected, as
    /// (definingModule, indicator). The caller adds these to the reachability
    /// ROOTS: unfolding can remove the last static call to a wrapper, but a
    /// runtime-built goal (<c>call/1</c>, <c>=..</c>) may still dispatch to it
    /// (the chunk-401 lesson) — the unfold must optimize call sites, never
    /// shrink the linked set. Over-keeps (wrappers whose every site stayed
    /// un-unfolded are roots too) — sound and tiny.</param>
    private static IReadOnlyList<ShmoObject> CrossModuleUnfold(
        IReadOnlyList<ShmoObject> objects,
        Action<LinkSeverity, string, string, string?> emit,
        out List<(string Module, PredicateRef Pred)> publicWrappers)
    {
        publicWrappers = new List<(string, PredicateRef)>();
        // Decode each module's raw static clauses (the V4 LTO channel).
        var decoded = new Dictionary<string, List<Shumway.Compiler.Ast.Clause>>();
        foreach (var obj in objects)
        {
            if (obj.ClauseTerms.Count == 0) continue;
            var list = new List<Shumway.Compiler.Ast.Clause>(obj.ClauseTerms.Count);
            foreach (var enc in obj.ClauseTerms)
                list.Add(TermCodec.DecodeClause(enc));
            decoded[obj.ModuleName] = list;
        }
        if (decoded.Count == 0) return objects;

        // Per-module wrapper registries + the global PUBLIC registry.
        var localReg = new Dictionary<string, Shumway.Compiler.Parsing.MetaWrapperUnfold.WrapperRegistry>();
        var publicReg = Shumway.Compiler.Parsing.MetaWrapperUnfold.WrapperRegistry.Empty;
        foreach (var obj in objects)
        {
            if (!decoded.TryGetValue(obj.ModuleName, out var cls)) continue;
            var reg = Shumway.Compiler.Parsing.MetaWrapperUnfold.DetectRegistry(cls);
            if (reg.Count == 0) continue;
            localReg[obj.ModuleName] = reg;
            var pubs = new HashSet<(string, int)>();
            foreach (var d in obj.Defined)
                if (d.Visibility == PredicateVisibility.Public)
                    pubs.Add((d.Indicator.Name, d.Indicator.Arity));
            var pub = reg.Restrict((n, a) => pubs.Contains((n, a)));
            if (pub.Count > 0)
            {
                publicReg = pub.MergeOver(publicReg);
                foreach (var (n, a) in pub.Keys)
                    publicWrappers.Add((obj.ModuleName, new PredicateRef(n, a)));
            }
        }
        if (publicReg.Count == 0) return objects;   // nothing visible cross-module

        var result = new List<ShmoObject>(objects.Count);
        foreach (var obj in objects)
        {
            if (!decoded.TryGetValue(obj.ModuleName, out var raw))
            {
                result.Add(obj);
                continue;
            }
            var own = localReg.TryGetValue(obj.ModuleName, out var lr)
                ? lr
                : Shumway.Compiler.Parsing.MetaWrapperUnfold.WrapperRegistry.Empty;
            // Shadowing follows call resolution: ANY predicate this module
            // DEFINES (template-shaped or not) takes its calls, so the public
            // wrapper registry must not apply to those indicators here — only
            // the module's own templates may (via `own`).
            var definedHere = new HashSet<(string, int)>();
            foreach (var d in obj.Defined)
                definedHere.Add((d.Indicator.Name, d.Indicator.Arity));
            var visiblePublics = publicReg.Restrict((n, a) => !definedHere.Contains((n, a)));
            var full = own.MergeOver(visiblePublics);
            var localOnly = Shumway.Compiler.Parsing.MetaWrapperUnfold.Apply(raw, own);
            var fullRewrite = Shumway.Compiler.Parsing.MetaWrapperUnfold.Apply(raw, full);
            bool crossContributed = false;
            if (!ReferenceEquals(fullRewrite, raw))
            {
                for (int i = 0; i < raw.Count; i++)
                {
                    bool fullChanged = !ReferenceEquals(fullRewrite[i], raw[i]);
                    bool localChanged = !ReferenceEquals(
                        ReferenceEquals(localOnly, raw) ? raw[i] : localOnly[i], raw[i]);
                    if (fullChanged && !localChanged) { crossContributed = true; break; }
                }
            }
            if (!crossContributed)
            {
                result.Add(obj);
                continue;
            }

            // Recompile from the rewritten clauses + the module's own metadata.
            var publicSet = new HashSet<PredicateRef>();
            var dynamicSet = new HashSet<PredicateRef>();
            foreach (var d in obj.Defined)
            {
                if (d.Visibility == PredicateVisibility.Public) publicSet.Add(d.Indicator);
                else if (d.Visibility == PredicateVisibility.Dynamic) dynamicSet.Add(d.Indicator);
            }
            var rawAll = new List<Shumway.Compiler.Ast.Clause>(fullRewrite);
            foreach (var seed in obj.DynamicSeeds)
                foreach (var enc in seed.EncodedClauses)
                    rawAll.Add(TermCodec.DecodeClause(enc));
            var errors = new List<ShmoCompileError>();
            var res = ShmoCompiler.CompileFromParts(
                obj.ModuleName, obj.Source, rawAll, publicSet, dynamicSet,
                new List<PredicateRef>(obj.EnsureLinked),
                new List<QualifiedPredicateRef>(), obj.BuildMode, errors,
                arityCompat: obj.ArityCompat);
            if (!res.Success || res.Object is null)
            {
                emit(LinkSeverity.Warning, "lto_unfold_recompile_failed",
                    $"module {obj.ModuleName}: cross-module unfold recompile failed "
                    + $"({errors.Count} error(s)); keeping the original object.",
                    obj.ModuleName);
                result.Add(obj);
                continue;
            }
            emit(LinkSeverity.Info, "lto_unfold",
                $"module {obj.ModuleName}: cross-module meta-wrapper unfold applied; recompiled.",
                obj.ModuleName);
            result.Add(res.Object);
        }
        return result;
    }

    /// <summary>Stage 9 fid bridge: resolve the linker's <c>(module, PredicateRef)</c>
    /// seeds to the functor ids the compiled bytecode uses, against
    /// <paramref name="byName"/> (a <c>(functorName, arity) → fid</c> index built from
    /// the decoded predicates). A predicate's functor name is either MANGLED
    /// (<c>module$name</c>, for a local predicate — see <c>ModuleRewrite.MangledName</c>)
    /// or BARE (<c>name</c>, for a public / dynamic predicate, and for a local entry that
    /// the linker promoted to public). We add BOTH forms that exist: a seed's true fid is
    /// always one of them, and including the other (if it happens to name a different
    /// predicate) only over-KEEPS — sound, since the prune must never drop a seed. Pure;
    /// public for direct testing.</summary>
    public static HashSet<int> ResolveSeedFids(
        IEnumerable<(string Module, PredicateRef Pred)> seeds,
        IReadOnlyDictionary<(string Name, int Arity), int> byName)
    {
        var fids = new HashSet<int>();
        foreach (var (mod, pred) in seeds)
        {
            if (byName.TryGetValue((mod + "$" + pred.Name, pred.Arity), out int local))
                fids.Add(local);
            if (byName.TryGetValue((pred.Name, pred.Arity), out int bare))
                fids.Add(bare);
        }
        return fids;
    }

    /// <summary>Stage 10: append a human-readable WAM disassembly of every predicate in
    /// every bundle entry's final <c>CompiledBytecode</c> (the bytecode the bundle ships)
    /// to <paramref name="path"/>, with per-entry / per-predicate headers. Stripped
    /// predicates (their bodies dropped by <c>--strip-wam</c>) simply don't appear — so the
    /// dump is the ground truth of the Tier-0 code that remains.</summary>
    private static void DumpBundleWam(Bundle bundle, string path)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var entry in bundle.Entries)
        {
            byte[]? bc = entry.CompiledBytecode;
            if (bc is null || bc.Length == 0) continue;
            Shumway.Compiler.Wam.CompiledModule module;
            try { module = CompiledModuleCodec.Decode(bc); }
            catch (Exception ex)
            {
                sb.Append($";;; (module {entry.ModuleName}: WAM decode failed: {ex.Message})\n");
                continue;
            }
            sb.Append($";;; ===== WAM dump: module {entry.ModuleName} "
                + $"({module.Predicates.Count} predicates) =====\n");
            foreach (var p in module.Predicates)
            {
                var (atomId, arity) = FunctorTable.Lookup(p.FunctorId);
                string name = AtomTable.GetById(atomId)?.Name ?? "?";
                sb.Append($"\n;;; {name}/{arity} clauses={p.ClauseCount} bytes={p.Bytecode.Length}\n");
                foreach (var ins in Shumway.Core.Disassembler.Iterate(p.Bytecode, 0, p.Bytecode.Length))
                    sb.Append($"    {ins}\n");
            }
        }
        File.AppendAllText(path, sb.ToString());
    }

    /// <summary>Async wrapper. Offloads the synchronous
    /// <see cref="Link(LinkConfig)"/> onto the thread pool so a
    /// caller on a UI / hosting thread doesn't block. The link itself
    /// is CPU-bound; the wrapper is a courtesy, not a fundamentally
    /// async pipeline. Cancellation is honoured *before* the link
    /// starts (the inner pass runs to completion once dispatched).</summary>
    public static Task<LinkResult> LinkAsync(LinkConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<LinkResult>(cancellationToken);
        return Task.Run(() => Link(config), cancellationToken);
    }

    /// <summary>Convenience: reads each <paramref name="shmoPaths"/>
    /// from disk through <see cref="ShmoReader.ReadFromFile(string)"/>
    /// and runs the link. Useful for .NET callers that already have
    /// a directory of compiled objects from the
    /// <c>shumway-compile</c> CLI.</summary>
    public static LinkResult LinkFromFiles(
        IReadOnlyList<string> shmoPaths,
        IReadOnlyList<PredicateRef> entryPoints,
        bool allowUndefined = false,
        TextWriter? verboseOut = null,
        bool stripSource = false)
    {
        ArgumentNullException.ThrowIfNull(shmoPaths);
        ArgumentNullException.ThrowIfNull(entryPoints);
        var objects = new List<ShmoObject>(shmoPaths.Count);
        foreach (var p in shmoPaths)
            objects.Add(ShmoReader.ReadFromFile(p));
        return Link(new LinkConfig
        {
            Objects = objects,
            EntryPoints = entryPoints,
            AllowUndefined = allowUndefined,
            VerboseOut = verboseOut,
            StripSource = stripSource,
        });
    }

    /// <summary>Convenience: compiles each
    /// <paramref name="sources"/> in memory via
    /// <see cref="ShmoCompiler.CompileSource"/>, then runs the link.
    /// Tuple shape is <c>(moduleNameFallback, source)</c>: the actual
    /// module name still comes from a <c>:- module/1</c> directive if
    /// present, falling back to the supplied name otherwise. Useful
    /// for in-process callers that want to bundle a program without
    /// touching the filesystem.</summary>
    public static LinkResult LinkFromSources(
        IReadOnlyList<(string ModuleNameFallback, string Source)> sources,
        IReadOnlyList<PredicateRef> entryPoints,
        bool allowUndefined = false,
        TextWriter? verboseOut = null,
        bool stripSource = false)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(entryPoints);
        var objects = new List<ShmoObject>(sources.Count);
        foreach (var (name, src) in sources)
            objects.Add(ShmoCompiler.CompileSource(src, name));
        return Link(new LinkConfig
        {
            Objects = objects,
            EntryPoints = entryPoints,
            AllowUndefined = allowUndefined,
            VerboseOut = verboseOut,
            StripSource = stripSource,
        });
    }

    private static string? ResolveDefiningModule(PredicateRef p,
        Dictionary<PredicateRef, string> globalPublic,
        Dictionary<PredicateRef, List<string>> globalDynamic)
    {
        if (globalPublic.TryGetValue(p, out var mod)) return mod;
        if (globalDynamic.TryGetValue(p, out var dynList) && dynList.Count > 0)
            return dynList[0];
        return null;
    }

    /// <summary>Phase 18 — entry-point resolution that doesn't require
    /// <c>:- public</c>. Falls back to scanning every module's local
    /// definitions when no public / dynamic match exists. Returns
    /// <c>null</c> + <paramref name="ambiguityMessage"/> = null for
    /// not-found, <c>null</c> + non-null message for the 2+ local
    /// matches case, or the resolved module name on success.</summary>
    private static string? ResolveEntryPointModule(PredicateRef p,
        Dictionary<PredicateRef, string> globalPublic,
        Dictionary<PredicateRef, List<string>> globalDynamic,
        Dictionary<string, Dictionary<PredicateRef, PredicateVisibility>> moduleDefined,
        out string? ambiguityMessage)
    {
        ambiguityMessage = null;
        if (globalPublic.TryGetValue(p, out var mod)) return mod;
        if (globalDynamic.TryGetValue(p, out var dynList) && dynList.Count > 0)
            return dynList[0];

        // No public/dynamic match — scan locals.
        List<string>? matches = null;
        foreach (var (modName, defs) in moduleDefined)
        {
            if (defs.ContainsKey(p))
            {
                matches ??= new List<string>();
                matches.Add(modName);
            }
        }
        if (matches is null || matches.Count == 0) return null;
        if (matches.Count == 1) return matches[0];
        matches.Sort(System.StringComparer.Ordinal);
        ambiguityMessage =
            $"Entry point {p} is defined as :- local in multiple modules ("
            + string.Join(", ", matches.Select(m => $"'{m}'"))
            + "). Mark exactly one definition :- public, or qualify the "
            + "entry as Module:Pred to disambiguate.";
        return null;
    }

    /// <summary>Chunk 247/444 — reflects every <c>--foreign-dll</c> assembly,
    /// returning the set of <c>[PrologPredicate]</c> <c>(name, arity)</c>
    /// indicators it exposes and (via <paramref name="assemblyNames"/>) the
    /// file names of the assemblies that carried at least one, for the bundle
    /// trailer. Pulled out of the main link body so the figures are computed
    /// ONCE and available to both the library pull pre-pass (which must not
    /// pull a member to satisfy a foreign-provided reference) and the
    /// builtin-set snapshot.</summary>
    private static HashSet<PredicateRef> ReflectForeignAssemblies(
        LinkConfig config,
        Action<LinkSeverity, string, string, string?> emit,
        out List<string> assemblyNames)
    {
        var indicators = new HashSet<PredicateRef>();
        assemblyNames = new List<string>();
        foreach (var asmPath in config.ForeignAssemblies)
        {
            System.Reflection.Assembly asm;
            try { asm = System.Reflection.Assembly.LoadFrom(asmPath); }
            catch (Exception ex)
            {
                emit(LinkSeverity.Error, "foreign_assembly_load_failed",
                    $"Could not load foreign assembly '{asmPath}': {ex.Message}", null);
                continue;
            }
            int discovered = 0;
            foreach (var type in SafeGetTypes(asm))
            {
                foreach (var method in type.GetMethods(
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    var attr = System.Reflection.CustomAttributeExtensions
                        .GetCustomAttribute<PrologPredicateAttribute>(method);
                    if (attr is null) continue;
                    string name = attr.Name ?? method.Name;
                    indicators.Add(new PredicateRef(name, attr.Arity));
                    discovered++;
                }
            }
            // Only record the assembly for runtime auto-load if we actually
            // found at least one [PrologPredicate] — avoids padding the trailer
            // (and the runtime probe path) with irrelevant DLLs.
            if (discovered > 0)
            {
                assemblyNames.Add(System.IO.Path.GetFileName(asmPath));
                emit(LinkSeverity.Info, "foreign_assembly_loaded",
                    $"Loaded foreign assembly '{System.IO.Path.GetFileName(asmPath)}' "
                    + $"with {discovered} [PrologPredicate] method(s).", null);
            }
            else
            {
                emit(LinkSeverity.Warning, "foreign_assembly_empty",
                    $"Foreign assembly '{asmPath}' carries no [PrologPredicate] "
                    + "methods; ignored.", null);
            }
        }
        return indicators;
    }

    /// <summary>C-archive library resolution (see
    /// <see cref="LinkConfig.Libraries"/>). The explicit objects are always
    /// linked; each library's members are pulled in only on demand to satisfy
    /// a reference the explicit set (plus builtins / prelude) leaves
    /// unresolved, searching libraries FIFO (first provider wins) and pulling
    /// transitively to a fixpoint. Returns the explicit objects followed by
    /// the pulled members in pull order; the caller runs the normal pipeline
    /// (LTO unfold + reachability + prune) over the whole set.
    ///
    /// <para>Pulls are at MODULE granularity — like a C linker pulling a whole
    /// <c>.o</c> to get one symbol. This selection deliberately only needs to
    /// avoid UNDER-pulling: it follows the same call-graph / qref / ensure_linked
    /// edges the main reachability walk does, but on the pre-LTO graph. Any
    /// member it over-pulls the main walk drops as unreachable; anything it
    /// somehow under-pulls surfaces as a normal <c>missing_predicate</c>. The
    /// arity-meta / missing distinction is left entirely to the main
    /// walk — here an unresolved edge a library can't satisfy is just dropped.</para></summary>
    private static IReadOnlyList<ShmoObject> PullLibraryMembers(
        IReadOnlyList<ShmoObject> explicitObjects,
        IReadOnlyList<LinkLibrary> libraries,
        IReadOnlyList<PredicateRef> entryPoints,
        IReadOnlySet<PredicateRef> foreignIndicators,
        Action<LinkSeverity, string, string, string?> emit)
    {
        // FIFO-ordered flat list of every library member.
        var libMembers = new List<(string Lib, ShmoObject Member)>();
        foreach (var lib in libraries)
            foreach (var m in lib.Members)
                libMembers.Add((lib.Name, m));

        // Namespace over the currently-included modules; grows as members pull.
        var included = new Dictionary<string, ShmoObject>();
        var publicOf = new Dictionary<PredicateRef, string>();
        var dynamicOf = new Dictionary<PredicateRef, List<string>>();
        var definedOf = new Dictionary<string, HashSet<PredicateRef>>();

        void Index(ShmoObject o)
        {
            if (!definedOf.TryGetValue(o.ModuleName, out var defs))
                definedOf[o.ModuleName] = defs = new HashSet<PredicateRef>();
            foreach (var d in o.Defined)
            {
                defs.Add(d.Indicator);
                // TryAdd keeps the FIRST: explicit objects (indexed up front)
                // win over libraries, and earlier libraries win over later —
                // the FIFO / "explicit wins" tie-break, for free.
                if (d.Visibility == PredicateVisibility.Public)
                    publicOf.TryAdd(d.Indicator, o.ModuleName);
                else if (d.Visibility == PredicateVisibility.Dynamic)
                {
                    if (!dynamicOf.TryGetValue(d.Indicator, out var list))
                        dynamicOf[d.Indicator] = list = new List<string>();
                    list.Add(o.ModuleName);
                }
            }
        }

        foreach (var o in explicitObjects)
            if (included.TryAdd(o.ModuleName, o)) Index(o); // dup module: main link diagnoses

        // Builtins + prelude publics never trigger a pull.
        StandardBuiltins.EnsureRegistered();
        MetaBuiltins.EnsureRegistered();
        var available = new HashSet<PredicateRef>();
        foreach (var b in BuiltinsRegistry.AllEntries())
            available.Add(new PredicateRef(b.Name, b.Arity));
        available.Add(new PredicateRef("true", 0));
        available.Add(new PredicateRef("fail", 0));
        available.Add(new PredicateRef("false", 0));
        foreach (var d in ShmoCompiler.CompileSource(Prelude.Source).Defined)
            if (d.Visibility == PredicateVisibility.Public)
                available.Add(d.Indicator);
        // Foreign predicates (from --foreign-dll) resolve too — never pull a
        // library member to satisfy one.
        available.UnionWith(foreignIndicators);

        var pulled = new List<ShmoObject>();
        var queue = new Queue<(string Module, PredicateRef Pred)>();
        var reached = new HashSet<(string, PredicateRef)>();
        var entered = new HashSet<string>();

        // Pull the first FIFO library member that provides `pred`. For call
        // edges only a cross-module-visible (:- public / :- dynamic) definition
        // qualifies; for entry / ensure_linked roots a local definition counts
        // too (entry-point local promotion). A module name already taken (by an
        // explicit object or an earlier pull) is skipped — first module wins.
        bool PullProviding(PredicateRef pred, bool crossModuleOnly)
        {
            foreach (var (libName, m) in libMembers)
            {
                if (included.ContainsKey(m.ModuleName)) continue;
                bool provides = false;
                foreach (var d in m.Defined)
                    if (d.Indicator.Equals(pred)
                        && (!crossModuleOnly || d.Visibility != PredicateVisibility.Local))
                    { provides = true; break; }
                if (!provides) continue;
                included[m.ModuleName] = m;
                Index(m);
                pulled.Add(m);
                emit(LinkSeverity.Info, "library_member_pulled",
                    $"pulled module '{m.ModuleName}' from library '{libName}' to satisfy {pred}.",
                    m.ModuleName);
                return true;
            }
            return false;
        }

        // Pull a member by module NAME (an explicit Module:Pred qualified ref).
        bool PullModule(string moduleName)
        {
            if (included.ContainsKey(moduleName)) return true;
            foreach (var (libName, m) in libMembers)
            {
                if (m.ModuleName != moduleName) continue;
                included[m.ModuleName] = m;
                Index(m);
                pulled.Add(m);
                emit(LinkSeverity.Info, "library_member_pulled",
                    $"pulled module '{m.ModuleName}' from library '{libName}' (qualified reference).",
                    m.ModuleName);
                return true;
            }
            return false;
        }

        // Enqueue the provider(s) of a call-edge target, pulling a library
        // member first when the included set can't satisfy it.
        void ResolveEdge(string curMod, PredicateRef target)
        {
            if (definedOf.TryGetValue(curMod, out var dm) && dm.Contains(target))
            { queue.Enqueue((curMod, target)); return; }
            if (publicOf.TryGetValue(target, out var pmod))
            { queue.Enqueue((pmod, target)); return; }
            if (dynamicOf.TryGetValue(target, out var dmods))
            { foreach (var d in dmods) queue.Enqueue((d, target)); return; }
            if (available.Contains(target)) return;
            if (PullProviding(target, crossModuleOnly: true))
            {
                if (publicOf.TryGetValue(target, out var pmod2)) queue.Enqueue((pmod2, target));
                else if (dynamicOf.TryGetValue(target, out var dmods2))
                    foreach (var d in dmods2) queue.Enqueue((d, target));
            }
            // else leave unresolved — the main walk reports missing / arity-meta.
        }

        // Roots: entry points (which may live only in a library).
        foreach (var ep in entryPoints)
        {
            if (publicOf.TryGetValue(ep, out var pmod)) { queue.Enqueue((pmod, ep)); continue; }
            if (dynamicOf.TryGetValue(ep, out var dmods))
            { foreach (var d in dmods) queue.Enqueue((d, ep)); continue; }
            string? localMod = FindDefiningModule(definedOf, ep);
            if (localMod is not null) { queue.Enqueue((localMod, ep)); continue; }
            if (available.Contains(ep)) continue;
            if (PullProviding(ep, crossModuleOnly: false)
                && FindDefiningModule(definedOf, ep) is { } pulledMod)
                queue.Enqueue((pulledMod, ep));
        }

        while (queue.Count > 0)
        {
            var (curMod, curPred) = queue.Dequeue();
            if (!reached.Add((curMod, curPred))) continue;

            if (entered.Add(curMod) && included.TryGetValue(curMod, out var firstVisit))
            {
                foreach (var el in firstVisit.EnsureLinked)
                    ResolveEdge(curMod, el);
                foreach (var qr in firstVisit.QualifiedRefs)
                    if (PullModule(qr.Module))
                        queue.Enqueue((qr.Module, new PredicateRef(qr.Name, qr.Arity)));
            }

            if (!included.TryGetValue(curMod, out var modObj)) continue;
            if (!modObj.CallGraph.TryGetValue(curPred, out var edges)) continue;
            foreach (var e in edges)
                ResolveEdge(curMod, e.Target);
        }

        if (pulled.Count == 0) return explicitObjects;
        var result = new List<ShmoObject>(explicitObjects.Count + pulled.Count);
        result.AddRange(explicitObjects);
        result.AddRange(pulled);
        return result;
    }

    private static string? FindDefiningModule(
        Dictionary<string, HashSet<PredicateRef>> definedOf, PredicateRef pred)
    {
        foreach (var kv in definedOf)
            if (kv.Value.Contains(pred)) return kv.Key;
        return null;
    }

    /// <summary>Serialises <paramref name="bundle"/> straight to bytes
    /// without re-validating via <see cref="BundleWriter.ToBytes"/>'s
    /// consult pass. Mirrors that writer's on-disk layout (see
    /// <see cref="BundleFormat"/>).</summary>
    private static byte[] SerialiseBundle(Bundle bundle)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        bw.Write(BundleFormat.Magic);
        bw.Write((uint)BundleFormat.CurrentVersion);
        bw.Write((uint)bundle.Entries.Count);
        foreach (var e in bundle.Entries)
        {
            WriteString(bw, e.ModuleName);
            WriteString(bw, e.Source);
            byte[] compiled = e.CompiledBytecode ?? Array.Empty<byte>();
            bw.Write((uint)compiled.Length);
            bw.Write(compiled);
            byte[] compiledIl = e.CompiledIl ?? Array.Empty<byte>();
            bw.Write((uint)compiledIl.Length);
            bw.Write(compiledIl);
            // V2+: per-predicate visibility metadata. The source-less
            // LoadBundle path (chunk 178) reads this list to populate
            // a ModuleManifest without re-consulting source.
            bw.Write((uint)e.Defined.Count);
            foreach (var d in e.Defined)
            {
                WriteString(bw, d.Indicator.Name);
                bw.Write((uint)d.Indicator.Arity);
                bw.Write((byte)d.Visibility);
            }
            // V3+ (Phase 17): per-entry IL patch + entries tables.
            // This direct linker path skips the BundleWriter.ToBytes
            // sub-engine validation pass, so it never emits IL — both
            // tables are empty here.
            byte[] patches = e.CompiledIlPatches ?? Array.Empty<byte>();
            bw.Write((uint)patches.Length);
            bw.Write(patches);
            byte[] ilEntries = e.CompiledIlEntries ?? Array.Empty<byte>();
            bw.Write((uint)ilEntries.Length);
            bw.Write(ilEntries);
            // Dynamic seeds trailer (chunk 209).
            bw.Write((uint)e.DynamicSeeds.Count);
            foreach (var seed in e.DynamicSeeds)
            {
                WriteString(bw, seed.Indicator.Name);
                bw.Write((uint)seed.Indicator.Arity);
                bw.Write((uint)seed.EncodedClauses.Count);
                foreach (var enc in seed.EncodedClauses)
                {
                    bw.Write((uint)enc.Length);
                    bw.Write(enc);
                }
            }
        }
        // Foreign-assemblies trailer (chunk 247). Must mirror
        // BundleWriter.ToBytes's section exactly so a bundle
        // round-trips through either writer identically.
        bw.Write((uint)bundle.ForeignAssemblies.Count);
        foreach (var asmName in bundle.ForeignAssemblies)
            WriteString(bw, asmName);
        // Snapshot presence byte (chunk 264) — part of the single supported
        // layout (chunk 413 froze the format: every section unconditional).
        // A linker-produced bundle never carries a save-state snapshot.
        bw.Write((byte)0);
        // Librarian archive trailer (shumway-lib) — always empty here: the
        // linker stores its modules as post-link Entries, never as archive
        // members. Written so a linker bundle and a librarian bundle share
        // one on-disk layout (BundleReader reads this section either way).
        bw.Write((uint)bundle.ArchiveMembers.Count);
        foreach (var member in bundle.ArchiveMembers)
        {
            WriteString(bw, member.FileName);
            bw.Write((uint)member.ShmoBytes.Length);
            bw.Write(member.ShmoBytes);
        }
        bw.Flush();
        return ms.ToArray();
    }

    private static void WriteString(BinaryWriter bw, string s)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s);
        bw.Write((uint)bytes.Length);
        bw.Write(bytes);
    }

    /// <summary>Chunk 247 — GetTypes that tolerates partial loader
    /// failures. Defensive: a foreign DLL may reference types we
    /// don't have, so plain Assembly.GetTypes() can throw
    /// ReflectionTypeLoadException — recover the types that DID
    /// resolve and keep going. The linker only needs to see
    /// [PrologPredicate]-decorated methods on resolved types.</summary>
    private static IEnumerable<Type> SafeGetTypes(System.Reflection.Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>();
        }
    }
}
