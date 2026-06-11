using System.Text;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;

namespace Shumway.Embedding;

/// <summary>
/// Writes a <see cref="Bundle"/> to the on-disk Shumway bundle format
/// (see <see cref="BundleFormat"/> for the layout).
///
/// <para>The writer validates the bundle by running every entry through a
/// throwaway <see cref="PrologEngine"/>'s consult / first-query path. Any
/// parse or compile error surfaces here rather than at deployment.</para>
///
/// <para>Set <c>includeCompiledBytecode</c> to embed a compiled-bytecode
/// payload for each entry (produced via <see cref="CompiledModuleCodec"/>).
/// Bundles produced without that flag still round-trip correctly; their
/// entries simply expose <c>CompiledBytecode == null</c>.</para>
/// </summary>
public static class BundleWriter
{
    public static void WriteToFile(Bundle bundle, string path,
        bool includeCompiledBytecode = false,
        bool includeCompiledIl = false,
        bool stripWam = false)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllBytes(path,
            ToBytes(bundle, includeCompiledBytecode, includeCompiledIl, stripWam));
    }

    public static byte[] ToBytes(Bundle bundle,
        bool includeCompiledBytecode = false,
        bool includeCompiledIl = false,
        bool stripWam = false,
        IReadOnlyCollection<(string Module, PredicateRef Pred)>? regionPruneSeeds = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ValidateOrThrow(bundle);
        // --strip-wam only makes sense alongside IL: it drops the WAM bodies of
        // IL-promoted predicates, which only exist when IL is built.
        if (stripWam && !includeCompiledIl)
            throw new ArgumentException("stripWam requires includeCompiledIl.", nameof(stripWam));

        // If the caller asked for compiled blobs and an entry doesn't already
        // carry one, synthesise it now from the source — keeping the writer
        // ergonomic for hand-built bundles (the typical case in tests / CLI).
        BundleEntry[] effective = bundle.Entries.ToArray();
        if (includeCompiledBytecode || includeCompiledIl)
        {
            for (int i = 0; i < effective.Length; i++)
            {
                byte[]? compiledBytecode = effective[i].CompiledBytecode;
                byte[]? compiledIl = effective[i].CompiledIl;
                byte[]? compiledIlPatches = effective[i].CompiledIlPatches;
                if (includeCompiledBytecode && compiledBytecode is null)
                    compiledBytecode = CompileEntryToBytes(effective[i].Source);
                byte[]? compiledIlEntries = effective[i].CompiledIlEntries;
                if (includeCompiledIl && compiledIl is null)
                {
                    _lastPatchTableBytes = null;
                    _lastEntriesTableBytes = null;
                    _lastIlFunctorIds = null;
                    _lastPrunableFids = null;
                    compiledIl = CompileEntryToIl(effective[i], regionPruneSeeds);
                    compiledIlPatches = _lastPatchTableBytes;
                    compiledIlEntries = _lastEntriesTableBytes;
                    // --strip-wam: drop the redundant WAM bodies. Two sets, both now safe
                    // (chunk 402):
                    //  • STANDALONE-IL predicates (_lastIlFunctorIds) — each has its own IL
                    //    delegate; every by-fid path (CallIl, meta-call via the chunk-316
                    //    marker alias, and catch-recovery via the chunk-402 Run() marker fix)
                    //    reaches the IL, never the WAM.
                    //  • ABSORBED-ONLY region members (_lastPrunableFids) — no standalone
                    //    form, but each region method publishes its members' entry cursors
                    //    (RegionCursorKind.MemberEntry → IlPersistedEntry.RegionMembers), and
                    //    LoadBundle aliases the member's functor to
                    //    EncodeResumeMarker(rootFid, entryCursor) — so a by-fid call (a
                    //    top-level query, a meta-call through a user meta-predicate, a catch
                    //    recovery) dispatches INTO the owning region at the member's entry.
                    //    This is what the chunk-401 incident (Blint --exe --goal main:
                    //    existence_error(main/1) / startPc out of range) was missing.
                    if (stripWam && compiledBytecode is not null)
                    {
                        var stripSet = new HashSet<int>();
                        if (_lastIlFunctorIds is not null) stripSet.UnionWith(_lastIlFunctorIds);
                        if (_lastPrunableFids is not null) stripSet.UnionWith(_lastPrunableFids);
                        if (stripSet.Count > 0)
                            compiledBytecode = StripIlBodies(compiledBytecode, stripSet);
                    }
                }
                effective[i] = new BundleEntry(
                    effective[i].ModuleName,
                    effective[i].Source,
                    compiledBytecode,
                    compiledIl,
                    effective[i].Defined,
                    compiledIlPatches,
                    compiledIlEntries,
                    effective[i].DynamicSeeds);
            }
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(BundleFormat.Magic);
        bw.Write((uint)BundleFormat.CurrentVersion);
        bw.Write((uint)effective.Length);
        foreach (var entry in effective)
        {
            WriteLengthPrefixedUtf8(bw, entry.ModuleName);
            WriteLengthPrefixedUtf8(bw, entry.Source);
            byte[] compiled = entry.CompiledBytecode ?? Array.Empty<byte>();
            bw.Write((uint)compiled.Length);
            bw.Write(compiled);
            byte[] compiledIl = entry.CompiledIl ?? Array.Empty<byte>();
            bw.Write((uint)compiledIl.Length);
            bw.Write(compiledIl);
            // Per-predicate visibility metadata. Empty list is fine —
            // the source-less load path only fires when this is non-empty
            // AND Source is stripped.
            bw.Write((uint)entry.Defined.Count);
            foreach (var d in entry.Defined)
            {
                WriteLengthPrefixedUtf8(bw, d.Indicator.Name);
                bw.Write((uint)d.Indicator.Arity);
                bw.Write((byte)d.Visibility);
            }
            // IL patch table + per-method entries table (Phase 17).
            // Both always emitted (even empty) so layout stays positional.
            byte[] patches = entry.CompiledIlPatches ?? Array.Empty<byte>();
            bw.Write((uint)patches.Length);
            bw.Write(patches);
            byte[] ilEntries = entry.CompiledIlEntries ?? Array.Empty<byte>();
            bw.Write((uint)ilEntries.Length);
            bw.Write(ilEntries);
            // Dynamic seeds trailer (chunk 209).
            bw.Write((uint)entry.DynamicSeeds.Count);
            foreach (var seed in entry.DynamicSeeds)
            {
                WriteLengthPrefixedUtf8(bw, seed.Indicator.Name);
                bw.Write((uint)seed.Indicator.Arity);
                bw.Write((uint)seed.EncodedClauses.Count);
                foreach (var enc in seed.EncodedClauses)
                {
                    bw.Write((uint)enc.Length);
                    bw.Write(enc);
                }
            }
        }
        // Foreign-assemblies trailer (chunk 247) after the
        // per-entry payloads. Pre-V5 readers stop after the last
        // entry and never see this section.
        bw.Write((uint)bundle.ForeignAssemblies.Count);
        foreach (var name in bundle.ForeignAssemblies)
            WriteLengthPrefixedUtf8(bw, name);
        // Save-state snapshot trailer (chunk 264). A
        // regular shumway-link / shumway-compile bundle writes
        // snapshotPresent=0 (one byte) and stops; PrologEngine.SaveState
        // writes snapshotPresent=1 and the consult-history + dynamic-
        // clauses payload that lets RestoreState rebuild the engine.
        if (bundle.Snapshot is { } snap)
        {
            bw.Write((byte)1);
            bw.Write((byte)(snap.DynamicOnly ? 1 : 0));
            bw.Write((uint)snap.ConsultHistory.Count);
            foreach (var src in snap.ConsultHistory)
                WriteLengthPrefixedUtf8(bw, src);
            bw.Write((uint)snap.DynamicClauses.Count);
            foreach (var seed in snap.DynamicClauses)
            {
                WriteLengthPrefixedUtf8(bw, seed.Indicator.Name);
                bw.Write((uint)seed.Indicator.Arity);
                bw.Write((uint)seed.EncodedClauses.Count);
                foreach (var enc in seed.EncodedClauses)
                {
                    bw.Write((uint)enc.Length);
                    bw.Write(enc);
                }
            }
        }
        else
        {
            bw.Write((byte)0);
        }
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>Compiles <paramref name="entry"/>'s source through the WAM
    /// pipeline, then through
    /// <see cref="Shumway.Compiler.Il.PersistedIlBuilder"/> to produce a
    /// .NET assembly containing one static method per IL-eligible
    /// predicate. The resulting .dll bytes embed into the bundle and the
    /// load path uses them to bind <c>PredicateDelegate</c>s without
    /// re-running the Sigil pipeline at consult time.</summary>
    private static byte[] CompileEntryToIl(BundleEntry entry,
        IReadOnlyCollection<(string Module, PredicateRef Pred)>? regionPruneSeeds = null)
    {
        Shumway.Builtins.StandardBuiltins.EnsureRegistered();
        // Run through a full PrologEngine warm-up so the module
        // rewriter, dynamic-functor routing, prelude, and per-query
        // synthetic launcher all agree on which functor ids end up
        // representing each predicate. PersistedIlBuilder then sees
        // the same CompiledPredicate the runtime path would compile.
        //
        // Chunk 230 — handle source-less entries too. shumway-compile
        // defaults to release mode which strips Source from the .shmo
        // (so the only ground truth is CompiledBytecode), and
        // shumway-link passes that through unchanged. Pre-chunk-230
        // CompileEntryToIl unconditionally called ConsultString,
        // which on an empty source produced an engine with ONLY the
        // prelude in its caches — so IL was compiled for prelude
        // helpers but never for any user predicate from the bundle.
        // For Blint that meant 159 prelude methods in the IL bundle
        // and zero user methods, leaving every user Call dispatching
        // through Tier-0 WAM (with the chunk-225/226/227 fast paths
        // bypassing OnDispatch only for the prelude callers). With
        // this fix the source-less path routes through
        // engine.LoadBundle on a single-entry bundle, which populates
        // PrecompiledStaticPredicates from CompiledBytecode.
        var engine = new Shumway.Embedding.PrologEngine();
        // Prefer the COMPILED BYTECODE (the .shmo) over re-consulting the source: it is
        // the ground truth that (a) ships in the bundle, (b) the runtime LoadBundle
        // dispatches against, and (c) the linker's dead-region ANALYSIS decodes. Compiling
        // the IL from a fresh re-consult of the source instead would re-run a different
        // pipeline and could disagree on which predicates a region absorbs — the Stage-9d
        // analysis↔compile inconsistency that broke the WAM strip. (You asked for embedded
        // IL → the bundle is self-contained from its bytecode; the source re-consult is a
        // fallback only for entries that carry no compiled bytecode, e.g. hand-built
        // test bundles.)
        if (entry.CompiledBytecode is not null && entry.Defined.Count > 0)
        {
            // Construct a single-entry bundle whose CompiledIl is null
            // so LoadBundle takes the source-less LoadEntryFromBytecode
            // path (which populates PrecompiledStaticPredicates) and
            // skips its own IL setup (we're about to do it ourselves
            // through PersistedIlBuilder.Build).
            var bareEntry = new BundleEntry(
                entry.ModuleName, source: "", compiledBytecode: entry.CompiledBytecode,
                compiledIl: null, defined: entry.Defined,
                compiledIlPatches: null, compiledIlEntries: null,
                dynamicSeeds: entry.DynamicSeeds);
            engine.LoadBundle(new Bundle(new[] { bareEntry }));
        }
        else if (!string.IsNullOrEmpty(entry.Source))
        {
            engine.ConsultString(entry.Source);
        }
        engine.Query("true.");
        // Pull every IL-eligible predicate the warm-up populated.
        // Static (chunk 82) covers immutable user clauses from
        // ConsultString; dynamic (chunk 68) covers `:- dynamic`-
        // declared ones; precompiled (chunk 178) covers user clauses
        // loaded from a source-less bundle entry.
        var predicates = new Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>();
        foreach (var (fid, pred) in engine.StaticPredicateCache)
            predicates[fid] = pred;
        foreach (var (fid, pred) in engine.DynamicPredicateCache)
            predicates[fid] = pred;
        foreach (var (fid, pred) in engine.PrecompiledStaticPredicates)
            predicates[fid] = pred;
        // Stage 9b-3 / 9c / 9d: compute the dead-region prune set HERE, over the EXACT
        // calleeMap the IL compile is about to use (`predicates` — the warm-up engine's
        // FULL set: user module + prelude + every reached callee). The linker's per-module
        // analysis (it decodes only the entry's own .shmo bytecode) diverged from this under
        // the region budget — the two absorbed DIFFERENT members, so an "absorbed-only"
        // predicate could be cross-region-called-by-fid in the real compile and break once
        // its WAM was stripped (§9d). Running the prune where the real region membership is
        // decided closes that gap. Absorbed-only predicates get no standalone IL (skipped in
        // Build) and their WAM is strippable (ToBytes). The seeds come from the linker (the
        // externally-reachable by-name-callable set); we resolve them against THIS engine's
        // functor table.
        HashSet<int>? prunableFids = null;
        var savedForcedRoots = Shumway.Compiler.Il.IlPredicateCompiler.RegionForcedRootFids;
        if (regionPruneSeeds is not null && predicates.Count > 0)
        {
            var byName = new Dictionary<(string, int), int>();
            foreach (int fid in predicates.Keys)
            {
                var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
                byName[(Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "", arity)] = fid;
            }
            var seedFids = ShmoLinker.ResolveSeedFids(regionPruneSeeds, byName);
            var ic = new Shumway.Compiler.Il.IlPredicateCompiler();
            long minSaving = long.TryParse(
                System.Environment.GetEnvironmentVariable("SHUMWAY_REGION_ROOT_MINSAVE"),
                out var ms) ? ms : 64;
            var forcedRoots = Shumway.Compiler.Il.RegionRootSelector.ComputeForcedRoots(
                predicates.Keys,
                (f, ex) => ic.RegionMemberFids(predicates[f], predicates, ex),
                f => predicates.TryGetValue(f, out var p) ? p.Bytecode.Length : 0,
                minSaving);
            var regionReachable = Shumway.Compiler.Il.RegionReachability.TrampolineReachable(
                predicates, seedFids,
                fid => ic.RegionMemberFids(predicates[fid], predicates, forcedRoots));
            var fullReachable = Shumway.Compiler.Il.RegionReachability.TrampolineReachable(
                predicates, seedFids, fid => new[] { fid });
            var pruned = new HashSet<int>();
            foreach (int f in fullReachable)
                if (!regionReachable.Contains(f)) pruned.Add(f);
            // (The chunk-398 constructed-constant meta-guard that used to rescue meta-call
            // targets from the prune is GONE — superseded by chunk 402's member-entry
            // aliases: EVERY absorbed member is fid-resolvable into its region method via
            // CurrentFunctorAddresses, so a meta-call / top-level / catch-recovery call to
            // a pruned member dispatches correctly without a standalone form.)
            prunableFids = pruned;
            DiagPrune(entry.ModuleName, predicates.Count, seedFids.Count,
                forcedRoots.Count, regionReachable.Count, fullReachable.Count,
                pruned.Count);
            // The Stage-9c promotions must hold during the IL compile below, so the regions
            // Build emits match the membership the prune assumed. Restored after Build.
            Shumway.Compiler.Il.IlPredicateCompiler.RegionForcedRootFids = forcedRoots;
        }
        _lastPrunableFids = prunableFids;
        // Caches still empty? Fall through to an empty assembly
        // (the load path simply finds no methods to bind).
        //
        // Chunk 418 — bundle region policy lives HERE, not in the runtime
        // default: a persisted bundle region-compiles ONLY when it also
        // prunes (regionPruneSeeds present). Region-compiling all-as-roots
        // without the prune bakes every absorbed member into every region
        // that pulls it (measured 2.3× bundle bloat, chunk 391). The
        // RUNTIME default (IlPredicateCompiler.RegionCompile, now ON) is
        // deliberately overridden for the persisted build either way so a
        // direct ToBytes caller gets the same bundle regardless of the
        // process-wide toggle.
        bool savedRegionCompile = Shumway.Compiler.Il.IlPredicateCompiler.RegionCompile;
        Shumway.Compiler.Il.IlPredicateCompiler.RegionCompile = regionPruneSeeds is not null;
        byte[] dllBytes;
        System.Collections.Generic.IReadOnlyList<Shumway.Compiler.Il.PersistedIlBuilder.Entry> persistedEntries;
        System.Collections.Generic.IReadOnlyList<Shumway.Compiler.Il.IlPatchSite> patches;
        try
        {
            (dllBytes, persistedEntries, patches) = Shumway.Compiler.Il.PersistedIlBuilder.Build(
                "ShumwayCompiledIl_" + SanitiseModuleName(entry.ModuleName),
                predicates, prunableFids);
        }
        finally
        {
            Shumway.Compiler.Il.IlPredicateCompiler.RegionCompile = savedRegionCompile;
            Shumway.Compiler.Il.IlPredicateCompiler.RegionForcedRootFids = savedForcedRoots;
        }
        // Phase 17 stash: the patch table the LoadBundle path needs to
        // overwrite each build-time atom/functor id sentinel with the
        // runtime-process equivalent. Plus the per-method (name, arity)
        // table so LoadBundle can register each delegate under the
        // RUNTIME functor id (interning the name in the current
        // process), rather than the build-time id baked into the method
        // name. Carried alongside the .dll bytes in side channels —
        // see <see cref="BundleEntry.CompiledIlPatches"/> and
        // <see cref="BundleEntry.CompiledIlEntries"/>.
        _lastPatchTableBytes = Shumway.Compiler.Il.IlPatchSiteCodec.Encode(patches);
        var persistedEntryList = new List<Shumway.Compiler.Il.IlPersistedEntry>(persistedEntries.Count);
        foreach (var pe in persistedEntries)
        {
            persistedEntryList.Add(new Shumway.Compiler.Il.IlPersistedEntry
            {
                Slot = pe.DelegateSlot,
                Name = pe.FunctorName.Contains('/')
                    ? pe.FunctorName.Substring(0, pe.FunctorName.IndexOf('/'))
                    : pe.FunctorName,
                Arity = pe.Arity,
                MethodName = pe.MethodName,
                IndexGraph = pe.IndexGraph,
                RegionMembers = pe.RegionMembers,
            });
        }
        _lastEntriesTableBytes = Shumway.Compiler.Il.IlPersistedEntryCodec.Encode(persistedEntryList);

        // --strip-wam: record the functor ids whose WAM body may be dropped —
        // those that received a SELF-CONTAINED IL delegate. A WAM-backed
        // indexed-dispatch predicate (chunk 216/217) reads its WAM lazily on
        // first call, so it keeps its body.
        var stripFids = new HashSet<int>(persistedEntries.Count);
        foreach (var pe in persistedEntries)
            if (pe.Strippable)
                stripFids.Add(pe.FunctorId);
        _lastIlFunctorIds = stripFids;
        return dllBytes;
    }

    /// <summary>Chunk 414 — diag-build-only (<c>-p:ShumwayDiag=true</c> +
    /// <c>SHUMWAY_PRUNE_DIAG=1</c>): per-entry region-prune figures.
    /// Stripped from normal builds.</summary>
    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private static void DiagPrune(string moduleName, int predicates, int seedFids,
        int forcedRoots, int regionReachable, int fullReachable, int pruned)
    {
        if (System.Environment.GetEnvironmentVariable("SHUMWAY_PRUNE_DIAG") is not null)
            System.Console.Error.WriteLine(
                $"[prune-diag] module={moduleName} predicates={predicates} "
                + $"seedFids={seedFids} forcedRoots(regions)={forcedRoots} "
                + $"regionReachable={regionReachable} fullReachable={fullReachable} "
                + $"pruned(stripped)={pruned}");
    }

    [System.ThreadStaticAttribute]
    private static HashSet<int>? _lastIlFunctorIds;

    /// <summary>Side-channel staging slot (mirrors <see cref="_lastIlFunctorIds"/>): the
    /// absorbed-only predicate fids computed by <see cref="CompileEntryToIl"/> over the
    /// warm-up engine's exact calleeMap (§9d). The outer ToBytes loop unions this with
    /// <see cref="_lastIlFunctorIds"/> for the --strip-wam set.</summary>
    [System.ThreadStaticAttribute]
    private static HashSet<int>? _lastPrunableFids;

    /// <summary>Rebuilds the CompiledModule blob with the WAM bodies of every
    /// IL-promoted predicate removed (--strip-wam). The predicate stays in the
    /// entry's <c>Defined</c> metadata (so it is registered), and its IL
    /// delegate carries the body; callers reach it by functor id (CallIl for
    /// bytecode callers, the chunk-316 marker for IL callers), never through a
    /// WAM address — so the body is pure dead weight. JIT-only: under Native
    /// AOT the IL can't load and these predicates would be unrunnable.</summary>
    private static byte[] StripIlBodies(byte[] compiledModuleBytes, IReadOnlySet<int> ilFids)
    {
        var module = CompiledModuleCodec.Decode(compiledModuleBytes);
        var kept = new List<Shumway.Compiler.Wam.CompiledPredicate>(module.Predicates.Count);
        foreach (var pred in module.Predicates)
            if (!ilFids.Contains(pred.FunctorId))
                kept.Add(pred);
        if (kept.Count == module.Predicates.Count)
            return compiledModuleBytes;   // nothing stripped
        var stripped = new Shumway.Compiler.Wam.CompiledModule(
            kept, module.StringLiterals, module.FloatLiterals, module.BigIntLiterals);
        return CompiledModuleCodec.Encode(stripped);
    }

    /// <summary>Side-channel staging slot used by
    /// <see cref="CompileEntryToIl"/> to hand the patch table to the
    /// outer ToBytes loop without changing the method signature mid-
    /// refactor. The loop reads this immediately after each
    /// CompileEntryToIl call; no concurrency.</summary>
    [System.ThreadStaticAttribute]
    private static byte[]? _lastPatchTableBytes;

    [System.ThreadStaticAttribute]
    private static byte[]? _lastEntriesTableBytes;

    private static string SanitiseModuleName(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return sb.ToString();
    }

    /// <summary>Compiles every entry through a fresh engine and runs a tiny
    /// dummy query so any unresolved-call or duplicate-public error fires.
    /// Throws on the first failure so callers (CLI / API) can surface a
    /// useful error message.</summary>
    private static void ValidateOrThrow(Bundle bundle)
    {
        var engine = new PrologEngine();
        foreach (var entry in bundle.Entries)
            engine.ConsultString(entry.Source);
        // Tickle the compile-once-per-query path so unresolved references
        // and public-uniqueness collisions surface now.
        engine.Query("true.");
    }

    /// <summary>Parses one module's source, compiles its clauses, and
    /// encodes the resulting <see cref="CompiledModule"/> into the codec's
    /// portable byte form. Mirrors the parser-then-compile pipeline that
    /// <see cref="PrologEngine.ConsultString"/> would use, minus the
    /// module-aware mangling — the bundle stores per-module compiled
    /// output and re-mangles on consult.</summary>
    private static byte[] CompileEntryToBytes(string source)
    {
        // The shared builtins need to be registered before the WAM compiler
        // can resolve calls like `is/2` and `=/2`. EnsureRegistered is
        // idempotent, so calling it from the writer doesn't disturb any
        // engine the host happens to have spun up.
        Shumway.Builtins.StandardBuiltins.EnsureRegistered();

        var clauses = new ClauseReader(new Lexer(source), OperatorTable.Default())
            .ReadAll()
            .Where(c => c.Kind != ClauseKind.Directive)
            .ToList();
        var module = new ModuleCompiler().Compile(clauses);
        return CompiledModuleCodec.Encode(module);
    }

    private static void WriteLengthPrefixedUtf8(BinaryWriter bw, string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        bw.Write((uint)bytes.Length);
        bw.Write(bytes);
    }
}
