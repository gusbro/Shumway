using Shumway.Builtins;

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
    /// <para><b>Current limitation</b>: the engine's
    /// <c>LoadBundle</c> path consults each entry's source to
    /// register its clauses; the embedded bytecode is currently
    /// only a Tier-1 IL warm-up cache, not a substitute. A
    /// source-stripped bundle therefore loads cleanly but its
    /// predicates do not dispatch (<c>existence_error/2</c> at
    /// call time). The flag is provided for size analysis, IP-
    /// protection archives and the chunk-174 <c>--exe</c> path
    /// where the host knows the bundle's contents. A "loadable
    /// strip" — direct dispatch from bytecode without re-consult —
    /// is queued for a future chunk.</para></summary>
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

    /// <summary>When non-null, the linker writes info diagnostics
    /// describing its progress (modules visited, predicates reached,
    /// etc.) to this writer. Useful for CLI <c>--verbose</c> mode.</summary>
    public TextWriter? VerboseOut { get; init; }
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

    public LinkResult(bool success, Bundle? bundle, byte[]? bytes,
        IReadOnlyList<LinkDiagnostic> diagnostics,
        IReadOnlyList<PredicateRef> reachedPredicates,
        IReadOnlyList<string> reachedModules,
        IReadOnlyList<string> unreachableModules,
        IReadOnlyList<PredicateRef> missingPredicates)
    {
        Success = success;
        Bundle = bundle;
        Bytes = bytes;
        Diagnostics = diagnostics;
        ReachedPredicates = reachedPredicates;
        ReachedModules = reachedModules;
        UnreachableModules = unreachableModules;
        MissingPredicates = missingPredicates;
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

        // ----- 1. Index objects, detect duplicate module names -----
        var byModule = new Dictionary<string, ShmoObject>();
        foreach (var obj in config.Objects)
        {
            if (byModule.ContainsKey(obj.ModuleName))
            {
                Emit(LinkSeverity.Error, "duplicate_module",
                    $"Module '{obj.ModuleName}' is defined in two .shmo objects.",
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
        foreach (var obj in config.Objects)
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
        foreach (var obj in config.Objects)
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

        // ----- 6. Reachability walk -----
        var reached = new HashSet<(string, PredicateRef)>();
        var reachedModules = new HashSet<string>();
        var missing = new HashSet<PredicateRef>();
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
            foreach (var edge in edges)
            {
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
                // 6) Anything else is missing.
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

        // ----- 7. Unreachable modules → warning + drop -----
        var unreachable = new List<string>();
        foreach (var obj in config.Objects)
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
            foreach (var obj in config.Objects)
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

                entries.Add(new BundleEntry(
                    moduleName: obj.ModuleName,
                    source: entrySource,
                    compiledBytecode: entryBytecode,
                    compiledIl: null,
                    defined: obj.Defined));
            }
            // Chunk 179: the chunk-172 "stripped_bundle" warning is gone —
            // stripped bundles now dispatch correctly via chunk 178's
            // source-less LoadBundle path.
            bundle = new Bundle(entries);
            // Chunk 192: --with-compiled-il routes the bundle through
            // BundleWriter.ToBytes, which (under includeCompiledIl=true)
            // runs PersistedIlBuilder per entry to materialise IL for
            // every eligible predicate. The resulting bytes carry the
            // IL .dll alongside the bytecode blob. Default path is the
            // linker's direct SerialiseBundle (no IL emit at link time).
            // BundleWriter still validates via a sub-engine ConsultString;
            // the linker has already done the heavy lifting so a failure
            // here is a real bug, not an operator-ordering quirk.
            if (config.IncludeCompiledIl)
            {
                bytes = BundleWriter.ToBytes(bundle,
                    includeCompiledBytecode: true,
                    includeCompiledIl: true);
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
        }

        var reachedList = reached.Select(r => r.Item2).Distinct().ToList();
        return new LinkResult(
            success: success,
            bundle: bundle,
            bytes: bytes,
            diagnostics: diagnostics,
            reachedPredicates: reachedList,
            reachedModules: reachedModules.ToList(),
            unreachableModules: unreachable,
            missingPredicates: missing.ToList());
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
}
