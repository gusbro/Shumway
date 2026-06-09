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

    public LinkResult(bool success, Bundle? bundle, byte[]? bytes,
        IReadOnlyList<LinkDiagnostic> diagnostics,
        IReadOnlyList<PredicateRef> reachedPredicates,
        IReadOnlyList<string> reachedModules,
        IReadOnlyList<string> unreachableModules,
        IReadOnlyList<PredicateRef> missingPredicates,
        IReadOnlyList<QualifiedPredicateRef>? externallyReachableSeeds = null)
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

        // ----- 4b. Chunk 247: reflect foreign DLLs, add their
        //          [PrologPredicate] indicators to the resolved set,
        //          record their filenames for the bundle trailer. -----
        var foreignAssemblyNames = new List<string>();
        foreach (var asmPath in config.ForeignAssemblies)
        {
            System.Reflection.Assembly asm;
            try { asm = System.Reflection.Assembly.LoadFrom(asmPath); }
            catch (Exception ex)
            {
                Emit(LinkSeverity.Error, "foreign_assembly_load_failed",
                    $"Could not load foreign assembly '{asmPath}': {ex.Message}");
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
                    builtinPredicates.Add(new PredicateRef(name, attr.Arity));
                    discovered++;
                }
            }
            // Only record the assembly for runtime auto-load if we
            // actually found at least one [PrologPredicate]. Avoids
            // padding the trailer (and the runtime probe path) with
            // irrelevant DLLs the user happened to pass.
            if (discovered > 0)
            {
                foreignAssemblyNames.Add(System.IO.Path.GetFileName(asmPath));
                Emit(LinkSeverity.Info, "foreign_assembly_loaded",
                    $"Loaded foreign assembly '{System.IO.Path.GetFileName(asmPath)}' "
                    + $"with {discovered} [PrologPredicate] method(s).");
            }
            else
            {
                Emit(LinkSeverity.Warning, "foreign_assembly_empty",
                    $"Foreign assembly '{asmPath}' carries no [PrologPredicate] "
                    + "methods; ignored.");
            }
        }

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

        // ----- 6b. Stage 9 (dead-region elimination) seed set -----
        // The predicates that must keep a STANDALONE (trampoline-callable) form because
        // they are callable BY NAME from outside a region's br-absorption. This is what
        // the linker will feed to RegionReachability once the bundle is region-compiled
        // (Stage 9b); for now we compute + report + expose it. A soundness
        // over-approximation, never under.
        var stage9Seeds = ComputeExternallyReachableSeeds(
            roots.Select(r => (r.Module, r.Pred)), reached, moduleDefined);
        Emit(LinkSeverity.Info, "stage9_seeds",
            $"Stage 9 (dead-region): {stage9Seeds.Count} externally-reachable seed(s) "
            + $"among {reached.Count} reached predicate(s).");

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
                    defined: obj.Defined,
                    dynamicSeeds: obj.DynamicSeeds));
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
            if (config.IncludeCompiledIl)
            {
                bytes = BundleWriter.ToBytes(bundle,
                    includeCompiledBytecode: true,
                    includeCompiledIl: true,
                    stripWam: config.StripWam);
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
            externallyReachableSeeds: seedList);
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
            // V4+ (chunk 209): dynamic seeds trailer.
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
        // V5+ (chunk 247): foreign-assemblies trailer. Must mirror
        // BundleWriter.ToBytes's V5 section exactly so a bundle
        // round-trips through either writer identically.
        bw.Write((uint)bundle.ForeignAssemblies.Count);
        foreach (var asmName in bundle.ForeignAssemblies)
            WriteString(bw, asmName);
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
