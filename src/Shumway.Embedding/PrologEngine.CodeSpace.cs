using System.Collections.Immutable;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;

namespace Shumway.Embedding;

public sealed partial class PrologEngine
{
    /// <summary>Persistent literal pools (ADR-015 chunk B). One set for the
    /// engine's life, so a literal keeps a stable id across queries — the
    /// precondition for caching the static linked region, whose bytecode
    /// embeds those ids.</summary>
    internal readonly Shumway.Compiler.Wam.LiteralPools _literalPools = new();

    /// <summary>Every live query's interpreter (weak — a finished query's interp
    /// collects naturally), so mutation-time invalidation can clear a fid's
    /// <c>IlByFunctorId</c> slot in ALL of them (see InvalidateDynamicCache).</summary>
    internal readonly List<WeakReference<Shumway.Interpreter.BytecodeInterpreter>>
        _liveInterps = new();

    /// <summary>runtime-assert compile cache. The per-assert
    /// pipeline used to build a fresh <see cref="ModuleRewrite.Context"/>
    /// (+ HashSet) and ClauseCompiler per call; both are safe to reuse
    /// (ClauseCompiler re-binds its pool refs at the top of every Compile;
    /// the Context only holds set references, so <c>_dynStore.Functors</c>
    /// mutations stay visible through it), so one of each per engine
    /// suffices.</summary>
    private Shumway.Compiler.Wam.ClauseCompiler? _assertClauseCompiler;
    private ModuleRewrite.Context? _assertDynCtx;

    /// <summary>the literal-pool lengths the live query's
    /// interpreter currently holds; recorded at query setup and after each
    /// refresh. <see cref="RefreshLiteralPoolsIfGrown"/> compares against
    /// these (not "did this one compile grow the pool") so a compile that
    /// interned a literal and then bailed to a fallback path can't leave a
    /// later same-literal compile thinking the interpreter is current.</summary>
    /// <summary>The literal-pool lengths each
    /// engine's interpreter was built (or last refreshed) with, PER
    /// ENGINE. Host-level counters broke under the mutation broadcast:
    /// refreshing engine A updated them, so engine B's refresh compared
    /// equal and skipped — leaving B's interpreter on a stale (possibly
    /// empty) pool snapshot ("Float literal id N is out of range").</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Activation, int[]>
        _interpPoolCounts = new();

    /// <summary>The static program, linked once and reused across queries
    /// (ADR-015 chunk B). Null until the first query builds it; nulled
    /// whenever the static program changes (<see cref="ConsultString"/> /
    /// bundle load). A query links only its transient region against this.</summary>
    internal Shumway.Compiler.Wam.Linker.LinkResult? _staticLink;

    /// <summary>the persistent program buffer —
    /// <c>prefix + static + dynamic</c>. Owned by PrologEngine across
    /// queries; <c>assertz</c> / <c>asserta</c> extend it in-place
    /// (capacity-doubled). Each query's
    /// <see cref="Activation.CurrentProgram"/> is a two-buffer
    /// <see cref="ProgramView"/> with this as <c>Primary</c> and the
    /// per-query bytecode as <c>Overflow</c>, with a reserved address
    /// gap between them so mid-query persistent growth doesn't collide
    /// with the query region's linked offsets. Null until the first
    /// query builds it; nulled by <see cref="InvalidatePersistent"/>
    /// when the dynamic-functor set changes (consult, declaration).</summary>
    internal byte[]? _persistentProgram;

    /// <summary>Logical end of <see cref="_persistentProgram"/>. The
    /// buffer is over-allocated (capacity-doubled), so a slack tail
    /// of zero bytes (Invalid opcode) follows the valid region; a stray
    /// PC into it fails loudly.</summary>
    internal int _persistentLength;

    /// <summary>Cached link result for the dynamic predicates only —
    /// the address map a per-query link uses to resolve calls into
    /// the dynamic region without re-linking it.</summary>
    internal Shumway.Compiler.Wam.Linker.LinkResult? _dynamicLink;


    /// <summary>when a query is in flight, the address at
    /// which the per-query overlay begins (the
    /// <see cref="ProgramView"/>'s <c>Split</c>). Persistent growth
    /// mid-query must stay below this, otherwise the query region's
    /// linked offsets collide with newly-extended dynamic bytecode.
    /// The setup code picks this with enough headroom over the
    /// persistent length for typical mid-query <c>assertz</c>
    /// growth.</summary>
    internal int _querySplit = -1;

    /// <summary>how much address space to reserve between
    /// the persistent program's end and the per-query region's start.
    /// Mid-query <c>assertz</c> may extend persistent up to this much
    /// before the persistent / query address ranges would collide; an
    /// assert that would overflow this gap forces a rebuild
    /// (effectively reverting to the within-query free-list
    /// model for that one assertz). 64 MB is more than any realistic
    /// per-query dynamic burst needs.</summary>
    private const int PersistentToQueryGap = 64 * 1024 * 1024;

    /// <summary>Marks the persistent program as stale so the next query
    /// setup rebuilds it. Called on every consult and on every change
    /// to the dynamic-functor set.</summary>
    internal void InvalidatePersistent()
    {
        _persistentProgram = null;
        _persistentLength = 0;
        _dynamicLink = null;
        // every mutation that can change the per-module
        // transform derivation funnels through here (consult, bundle
        // load, abolish, restore_state, dynamic-functor-set changes);
        // advance the derivation generation so the static / dynamic
        // rewrite caches recompute at the next query setup. The
        // persistent link-metadata caches need no explicit nulling:
        // they are rebuilt whenever the persistent buffer is
        // (builtPersistentNow), which this method forces.
        _derivationGen++;
        // chain state + free-list describe offsets in the
        // now-invalidated buffer; start a fresh table for the rebuild.
        // Live engines still running on the old buffer keep their own
        // table through _engineChainTables — invalidation never desyncs
        // an in-flight query's in-place dispatch.
        ResetDynChains();
    }

    /// <summary>user-facing entry point for
    /// persistent-buffer compaction. Invalidates the cached dynamic-
    /// region link so the next query setup rebuilds it from current
    /// <c>_dynamicClauses</c>. After a long run of in-place
    /// mutations, the buffer accumulates clause
    /// bodies and chain entries that aren't reachable from any
    /// current clause; compaction reclaims them by starting the
    /// next dynamic region's layout from scratch. Reachable
    /// addresses captured by in-flight choice points stay valid
    /// only until the next query setup runs, so callers should
    /// invoke compaction between top-level queries — not inside a
    /// running query.</summary>
    internal void CompactDynamicCodeBuffer()
    {
        InvalidatePersistent();
        _persistentMutationsSinceCompact = 0;
    }

    /// <summary>mutation counter that drives
    /// the auto-compaction watermark. Bumped on every dynamic-store
    /// mutation (assertz / asserta / retract / abolish); reset by
    /// <see cref="CompactDynamicCodeBuffer"/> and by the auto-
    /// compaction trigger in <c>SetupQueryFromTerm</c>.</summary>
    private long _persistentMutationsSinceCompact;

    /// <summary>how many dynamic-store mutations
    /// the engine accumulates before automatically compacting the
    /// persistent buffer at the next query's setup. Default 1000;
    /// host code can raise it (large batch workloads where rebuild
    /// cost dominates) or lower it (memory-tight environments
    /// preferring smaller buffers) or set to <c>long.MaxValue</c>
    /// to disable auto-compaction entirely. Compaction itself stays
    /// callable via <c>compact_dynamic_buffer/0,1</c>.</summary>
    public long CompactWatermark { get; set; } = 1000;

    /// <summary>diagnostic read of the mutation
    /// counter (mainly for tests that need to verify auto-compaction
    /// fires at the right moment).</summary>
    public long PersistentMutationsSinceCompact =>
        _persistentMutationsSinceCompact;

    /// <summary>Canonical encodings of every (subgoal, answer) pair the
    /// tabling driver has recorded. Backs the <c>'$tbl_seen'/1</c>
    /// builtin — an O(1) duplicate-answer test for the semi-naive fixpoint.
    /// Persists for the engine's life, alongside the tabling dynamic
    /// predicates it mirrors.</summary>
    private readonly HashSet<string> _tablingSeen = new();

    /// <summary>Records <paramref name="key"/>; returns <c>true</c> when it
    /// was not present (the answer is new), <c>false</c> when it was.</summary>
    internal bool RegisterTablingKey(string key) => _tablingSeen.Add(key);

    /// <summary>Empties the tabling key set — part of table invalidation
    /// (<c>abolish_all_tables/0</c>).</summary>
    internal void ClearTablingKeys() => _tablingSeen.Clear();

    /// <summary>Stack of in-flight findall solution buffers.
    /// MetaTransform rewrites <c>findall/3</c> with a callable goal into a
    /// goal sequence driven by the <c>'$findall_*'</c> builtins, which run
    /// in this engine: <c>'$findall_push'</c> pushes a frame here,
    /// <c>'$findall_record'</c> appends a template copy to the top frame,
    /// <c>'$findall_collect'</c> pops it. Solutions must survive the
    /// <c>fail</c>-driven backtracking that enumerates the goal, so they are
    /// held off the WAM heap. Each frame entry is one of two backtrack-safe
    /// forms: a <see cref="Cell"/>[] cell image (the fast
    /// findall path, no per-node managed object) or a managed <see cref="Term"/>
    /// AST (bagof/setof, which inspect the terms for witness grouping, and the
    /// findall value-leaf fallback). A stack so nested findall calls each get
    /// their own frame.</summary>
    private readonly List<List<object>> _findallStack = new();

    internal void PushFindallFrame() => _findallStack.Add(new List<object>());

    /// <summary>Records a solution as a managed AST term (bagof/setof, and the
    /// findall value-leaf fallback).</summary>
    internal void RecordFindallSolution(Term solution)
    {
        if (_findallStack.Count == 0)
            throw new InvalidOperationException(
                "'$findall_record' invoked with no active findall frame.");
        _findallStack[^1].Add(solution);
    }

    /// <summary>Records an opaque frame entry (the bagof/setof record step
    /// stores key+payload pairs the lazy enumerator groups later).</summary>
    internal void RecordFindallEntry(object entry)
    {
        if (_findallStack.Count == 0)
            throw new InvalidOperationException(
                "'$bagof_record' invoked with no active findall frame.");
        _findallStack[^1].Add(entry);
    }

    /// <summary>Records a solution as a backtrack-safe cell image
    /// (the fast findall path).</summary>
    internal void RecordFindallSnapshot(Cell[] snapshot)
    {
        if (_findallStack.Count == 0)
            throw new InvalidOperationException(
                "'$findall_record' invoked with no active findall frame.");
        _findallStack[^1].Add(snapshot);
    }

    internal List<object> PopFindallFrame()
    {
        if (_findallStack.Count == 0)
            throw new InvalidOperationException(
                "'$findall_collect' invoked with no active findall frame.");
        var frame = _findallStack[^1];
        _findallStack.RemoveAt(_findallStack.Count - 1);
        return frame;
    }

    /// <summary>Drops the cached compiled predicate for
    /// <paramref name="functorId"/>. Called by every mutation path
    /// (<see cref="Assertz"/>, <see cref="Asserta"/>,
    /// <see cref="RemoveDynamic"/>, <see cref="RemoveDynamicByReference"/>,
    /// <see cref="AbolishDynamic"/>) so the next query sees a fresh
    /// compile that picks up the modification.</summary>
    internal void InvalidateDynamicCache(int functorId)
    {
        // Every dynamic-store mutation funnels through here (assertz,
        // asserta, retract, abolish), so this is the one place the
        // ADR-015 generation clock has to advance and the
        // auto-compaction mutation counter ticks.
        _dbGeneration.Value++;
        _persistentMutationsSinceCompact++;
        DropDynamicPredicateCacheEntry(functorId);
        // ADR-023 — the predicate changed, so any cached Tier-1 IL snapshot of it
        // is stale: evict it, and clear every LIVE query interpreter's direct
        // fid-table slot so the very next dispatch re-resolves live (falling
        // back to the Tier-0 enter_dynamic chain — the logical update view).
        // ALL live interps, not just the current one: a SUSPENDED outer
        // activation resumes with its own table, and a stale slot there
        // dispatched an evicted dynamic snapshot (the Logtalk-under-promotion
        // silent failure).
        InvalidateIlForFunctor(functorId);
        // ADR-034 — and any CALLER whose IL embeds this predicate's inlined
        // snapshot must stop using it: the emitted clause-entry staleness test
        // reads this host-lifetime set (shared into every per-query engine),
        // so the fallback path takes over from the very next clause entry.
        _mutatedDynamicFids.Add(functorId);
        // An asserted/retracted expansion hook changes what the discriminator
        // index may skip.
        if (IsGlobalHookFunctor(functorId)) _hookIndexValid = false;
        // the functor's clause list changed, so its cached
        // transformed/rewritten clauses are stale too.
        _dynamicRewriteCache.Remove(functorId);
    }

    /// <summary>Drops a functor's promoted Tier-1 IL delegate and clears every
    /// live interpreter's direct-dispatch slot for it. For a STATIC predicate
    /// whose clause set changed at consult time — the global expansion hooks
    /// (term_expansion/goal_expansion) are the sanctioned case: each library
    /// consult appends its hook clauses to the same global predicate. A hook
    /// promoted mid-consult (dcgs's ~50 clauses cross the call threshold) kept
    /// serving the pre-append IL, silently hiding every later library's hook
    /// clause (atts' `:- attribute` never fired). Dynamic mutations get this
    /// via <see cref="InvalidateDynamicCache"/>; consult-time static commits
    /// call it directly.</summary>
    internal void InvalidateIlForFunctor(int functorId)
    {
        IlPromotion.EvictDelegate(functorId);
        for (int i = _liveInterps.Count - 1; i >= 0; i--)
        {
            if (!_liveInterps[i].TryGetTarget(out var li))
            {
                _liveInterps.RemoveAt(i);
                continue;
            }
            var ilTable = li.IlByFunctorId;
            if (ilTable is not null && (uint)functorId < (uint)ilTable.Length)
                ilTable[functorId] = null;
        }
    }

    /// <summary>removes a dynamic predicate's compiled-bytecode
    /// cache entry and keeps the merged skip-compile cache in step, falling
    /// back to the static / precompiled tier's entry when one exists (the
    /// same precedence the merge uses: precompiled &lt; static &lt;
    /// dynamic). Used by <see cref="InvalidateDynamicCache"/> and the
    /// JIT hotness-flip drops at query setup.</summary>
    internal void DropDynamicPredicateCacheEntry(int functorId)
    {
        // The compiled program product embeds this functor's compile.
        _programStamp++;
        _dynamicPredicateCache.Remove(functorId);
        var merged = _skipCompileMergedCache;
        if (merged is null) return;
        if (_staticPredicateCache.TryGetValue(functorId, out var stat))
            merged[functorId] = stat;
        else if (_precompiledClauseCache.TryGetValue(functorId, out var pre))
            merged[functorId] = pre;
        else
            merged.Remove(functorId);
    }


    /// <summary>Per-engine cache of precompiled predicates from any
    /// bundle blob loaded with <see cref="LoadBundle(Bundle)"/>
    ///. The query-setup path consults this cache before
    /// running ModuleCompiler over the consulted source — for any
    /// predicate whose functor id is in the cache, the cached
    /// <see cref="Shumway.Compiler.Wam.CompiledPredicate"/> is reused
    /// verbatim. Mutating the cache directly is not supported; use
    /// <see cref="LoadBundle(Bundle)"/> to populate it.</summary>
    public IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate> PrecompiledClauseCache
        => _precompiledClauseCache;
    internal readonly Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate> _precompiledClauseCache = new();

    /// <summary>pre-compiled predicates from source-less
    /// bundle entries. Their bytecode is already mangled + runtime-
    /// ready (ShmoCompiler in applies the same transforms
    /// SetupQueryFromTerm would), so they bypass the AST → ModuleCompiler
    /// pipeline entirely and slot straight into the static-link region.
    /// Populated by <see cref="LoadEntryFromBytecode"/> on
    /// <see cref="LoadBundle(Bundle)"/>; consumed by
    /// <see cref="SetupQueryFromTerm"/> when it (re)builds the static
    /// link. Keyed by FunctorId so a later source-carrying consult of
    /// the same predicate replaces the precompiled entry cleanly.</summary>
    internal readonly Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>
        _precompiledStaticPredicates = new();

    /// <summary>Persisted WAM-independent dispatch graphs (--strip-wam), keyed by
    /// runtime functor id. Populated at LoadBundle; registered onto each query's
    /// fresh engine at setup (the indexed-dispatch cache is per-engine), so a
    /// stripped indexed predicate resolves its entry clause without a WAM body.</summary>
    internal readonly Dictionary<int, byte[]> _persistedIndexGraphs = new();

    /// <summary>Module names already warned about unbindable persisted IL, so
    /// the two load passes that visit an entry warn once, not twice.</summary>
    internal readonly HashSet<string> _warnedIlUnbindable = new();

    /// <summary>region member-entry aliases, keyed by the member's runtime
    /// functor id; the value is <c>EncodeResumeMarker(regionRootRuntimeFid, entryCursor)</c>.
    /// Populated at LoadBundle from each region method's <see cref="Shumway.Compiler.Il.
    /// IlPersistedEntry.RegionMembers"/> table. Injected into the query address map
    /// (lowest priority — only for a member with no WAM address and no standalone IL
    /// delegate) so a by-fid call to a stripped absorbed member dispatches INTO its
    /// region method at the member's entry cursor.</summary>
    internal readonly Dictionary<int, int> _regionMemberAliases = new();

    /// <summary>True when a call to <paramref name="fid"/> dispatches through
    /// Tier-1 IL: it has its own promoted delegate, OR a persisted-IL region
    /// method covers it as an absorbed member (region alias). Region members
    /// have no standalone delegate by design, so
    /// <see cref="IlPromotionStore.IsPromoted"/> alone understates Tier-1
    /// coverage of a region-compiled bundle.</summary>
    internal bool IsTier1Dispatched(int fid) =>
        IlPromotion.IsPromoted(fid) || _regionMemberAliases.ContainsKey(fid);

    /// <summary>Eagerly Sigil-compiles every compilable static predicate to
    /// Tier-1 IL now — the opt-in counterpart to the lazy default, for a program
    /// that will do enough queries to want the whole set hot before the first
    /// (a server warming up before it serves). Returns the number newly promoted.
    /// No-op when Tier-1 is disabled (Threshold ≤ 0 — the dispatch would keep
    /// call sites on bytecode, so a delegate would never run) or under Native AOT
    /// (no runtime codegen). Predicates already bound (persisted IL / prior
    /// promotion), region members (covered by their region method), and
    /// unpromotable shapes (dynamics, oversized) are skipped inside Warm.</summary>
    public int WarmAllCompilable()
    {
        if (IlPromotion.Threshold <= 0) return 0;
        int before = IlPromotion.PromotedCount;
        // Every predicate the engine has already compiled to WAM: each loaded
        // bundle module's decoded predicates (source-stripped AND the bytecode
        // blob of a source-carrying entry), plus the consulted-source static
        // cache. A freshly consulted source that has not run a query yet has no
        // compiled predicates to warm — those promote lazily on first use.
        foreach (var module in _precompiledModules.Values)
            foreach (var pred in module.Predicates)
                if (!_regionMemberAliases.ContainsKey(pred.FunctorId))
                    IlPromotion.Warm(pred.FunctorId, pred);
        foreach (var (fid, pred) in StaticPredicateCache)
            if (!_regionMemberAliases.ContainsKey(fid))
                IlPromotion.Warm(fid, pred);
        return IlPromotion.PromotedCount - before;
    }

    /// <summary>read-only view of
    /// <see cref="_precompiledStaticPredicates"/>. Lets
    /// <see cref="BundleWriter.CompileEntryToIl"/> see the predicates
    /// loaded from a source-less bundle entry (the path),
    /// not just the ones populated by ConsultString.</summary>
    public IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate>
        PrecompiledStaticPredicates => _precompiledStaticPredicates;

    /// <summary>Per-engine cache of compiled dynamic predicates.
    /// The query-setup path consults this cache alongside
    /// <see cref="_precompiledClauseCache"/> so the ModuleCompiler can
    /// skip recompiling a dynamic predicate's bytecode + switch tables
    /// when its clause set hasn't changed since the last compile.
    /// Invalidated on every <c>assertz</c> / <c>asserta</c> /
    /// <c>retract</c> / <c>abolish</c> against the same functor.
    /// Predicates whose bytecode references per-module literal pools
    /// (string / float / big-integer) are filtered out at populate time
    /// — see <see cref="Shumway.Compiler.Wam.ModuleCompiler.IsCachedPredicateReusable"/>.</summary>
    public IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate> DynamicPredicateCache
        => _dynamicPredicateCache;
    internal readonly Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate> _dynamicPredicateCache = new();

    /// <summary>Per-engine cache of compiled <em>static</em> predicates
    ///. Static predicates are immutable between consults, so
    /// once compiled their bytecode is reused on every subsequent query
    /// instead of being recompiled from source — which is the dominant
    /// cost a meta-call (findall / call / forall, each a fresh query
    /// setup) used to pay. Cleared wholesale by <see cref="ConsultString"/>,
    /// the only operation that changes the static program. Predicates
    /// whose bytecode references per-module literal pools are filtered
    /// out at populate time, exactly as for the dynamic cache.</summary>
    public IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate> StaticPredicateCache
        => _staticPredicateCache;
    internal readonly Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate> _staticPredicateCache = new();

    // ====================================================================
    // per-query setup caching. Query setup used to re-derive
    // stable state on every query: the full transform chain over every
    // consulted module (prelude included), three dictionary merges, and a
    // bare-alias loop doing two Substring allocs + an intern per linked
    // functor. Everything below is a cache of one of those derivations,
    // keyed either by the derivation generation or by the persistent-link
    // rebuild.
    // ====================================================================

    /// <summary>derivation generation. Bumped by every mutation
    /// that can change the per-module transform pipeline's output: consult,
    /// bundle load, abolish, restore_state and every dynamic-functor-set
    /// change funnel through <see cref="InvalidatePersistent"/> (which
    /// bumps), and the implicit_dynamic auto-promotion in
    /// <see cref="EnsureDynamic"/> (which doesn't invalidate the persistent
    /// buffer) bumps explicitly. Bumping more often than strictly necessary
    /// (e.g. on the compaction) is safe — it just costs one
    /// re-transform at the next query setup, which was the per-query status
    /// quo before this chunk.</summary>
    internal int _derivationGen;

    /// <summary>cached output of the static transform chain
    /// (MetaWrapperUnfold → ClausePipeline → ModuleRewrite) over every
    /// consulted module, plus the user-module locals set and the set of
    /// rewritten-clause head functor ids. A pure function of the module
    /// manifests, the mode table, the dynamic-functor set and the
    /// precompiled module locals — all of which bump
    /// <see cref="_derivationGen"/> when they change. Between consults,
    /// every query reuses this instead of re-transforming the whole program
    /// (the ~600-line prelude included) per query.</summary>
    internal List<Clause>? _staticRewriteClauses;
    internal HashSet<int>? _staticRewriteUserLocals;
    internal HashSet<int>? _staticRewriteHeadFids;
    internal int _staticRewriteGen = -1;

    /// <summary>per-module locals sets computed by the static
    /// rewrite pass (the same `locals` each module's ModuleRewrite context
    /// gets), cached alongside <see cref="_staticRewriteUserLocals"/>. The
    /// dynamic-clause rewrite consults it so a dynamic clause attributed to
    /// a non-user module (see <see cref="_dynamicSeedModule"/>) mangles its
    /// body calls against THAT module's locals — matching the entry's
    /// static bytecode, which ShmoCompiler mangled under the per-file
    /// module name.</summary>
    internal Dictionary<string, HashSet<int>>? _staticRewriteModuleLocals;

    /// <summary>Per-module transform cache underneath the whole-program
    /// <see cref="_staticRewriteClauses"/> snapshot. When a derivation bump
    /// forces the regenerate branch, only the modules whose manifest actually
    /// CHANGED re-run the transform chain — the rest reuse their previous
    /// rewritten clause lists verbatim (same Clause objects, same MetaTransform
    /// helper ids), which also keeps their compiled-predicate cache entries
    /// valid. The key fingerprints every transform input that varies per
    /// module: the clause list identity (reference + element-wise reference
    /// snapshot — append-only growth, reconsult AND in-place hook re-expansion
    /// all change it), the public/import/export surface sizes, the mode-table
    /// version, opaqueness, and the codegen flags. A module that regenerates
    /// (or vanishes) drops its old head fids from the static compiled cache —
    /// the targeted version of the old blanket Clear, preserving the
    /// "nothing compiled outlives its clauses' derivation" invariant.</summary>
    internal sealed class ModuleTransformEntry
    {
        public required object ClausesRef;
        /// <summary>Element-wise reference snapshot of the manifest's clause
        /// list at build time. Compared by reference per element: an in-place
        /// INTERIOR replacement (the in-file hook re-expansion pass rewrites
        /// clauses without changing count or endpoints) must invalidate.</summary>
        public required Clause[] ClauseSnapshot;
        public required int PublicCount;
        public required int ImportCount;
        public required int ExportCount;
        public required int ModesVersion;
        public required bool Opaque;
        public required bool DebugCodegen;
        public required bool InlineIte;
        public required int BundleLocalsCount;
        /// <summary>The distinct static Module:Goal resolutions this transform
        /// performed (null when it had none). Revalidated per reuse against
        /// the live resolver — a module load only re-transforms the modules
        /// whose qualified goals now resolve differently.</summary>
        public required Dictionary<(string Mod, string Name, int Arity), string?>?
            QualifiedResolutions;
        public required List<Clause> Rewritten;
        public required HashSet<int> Locals;
        public required HashSet<int> HeadFids;
    }
    internal readonly Dictionary<string, ModuleTransformEntry> _moduleTransformCache = new();

    internal static bool ClauseSnapshotMatches(Clause[] snapshot, List<Clause> live)
    {
        if (snapshot.Length != live.Count) return false;
        for (int i = 0; i < snapshot.Length; i++)
            if (!ReferenceEquals(snapshot[i], live[i])) return false;
        return true;
    }

    /// <summary>ADR-030 staleness guard for the per-module reuse above: the
    /// head fids whose LAST clause had its redundant trailing cut elided in the
    /// previous product build. Elision is a WHOLE-program analysis, so a
    /// reused module's compiled predicate can go stale when a DIFFERENT
    /// module's regeneration flips its callees' det-ness — the build compares
    /// the fresh elided set against this one and drops the flipped fids.</summary>
    internal HashSet<int>? _lastElidedStaticFids;

    /// <summary>Drops compiled static-cache entries for <paramref name="fids"/>,
    /// keeping the merged skip-compile cache in step (same fallback precedence
    /// as <see cref="DropDynamicPredicateCacheEntry"/>).</summary>
    internal void DropStaticCompiledFids(IEnumerable<int> fids)
    {
        foreach (int fid in fids)
        {
            if (!_staticPredicateCache.Remove(fid)) continue;
            var merged = _skipCompileMergedCache;
            if (merged is null) continue;
            if (_dynamicPredicateCache.TryGetValue(fid, out var dyn))
                merged[fid] = dyn;
            else if (_precompiledClauseCache.TryGetValue(fid, out var pre))
                merged[fid] = pre;
            else
                merged.Remove(fid);
        }
    }

    /// <summary>The compiled PROGRAM product: everything query setup derives
    /// from the static + dynamic program alone — compiled predicates
    /// partitioned into the ADR-015 static / dynamic regions, the rerouted
    /// leftovers for the query overlay, and the cacheable-head set. A query
    /// only ever ADDS the synthetic <c>__query__</c> clause (plus its
    /// <c>$q</c> helpers) on top, so between program changes every query
    /// reuses this instead of re-compiling / re-partitioning / re-probing
    /// the whole program (the per-query cost scaled linearly with program
    /// size — ~32 ms per trivial query at 5000 clauses). Validity:
    /// <see cref="DerivationGen"/> (consults, abolish, functor-set changes),
    /// <see cref="ProgramStamp"/> (per-functor dynamic mutations, JIT
    /// hotness flips), reference-equality of the static link (every external
    /// invalidation site nulls <c>_staticLink</c>), and the debug-codegen
    /// flags.</summary>
    internal sealed class CompiledProgramProduct
    {
        public required int DerivationGen;
        public required int ProgramStamp;
        public required bool EmitDebugInfo;
        public required bool DebugCodegen;
        /// <summary>The static link the reroute ran against (null when the
        /// product was built before any static link existed; patched to the
        /// freshly-built link by the setup that builds it).</summary>
        public Shumway.Compiler.Wam.Linker.LinkResult? StaticLinkRef;
        public required List<Shumway.Compiler.Wam.CompiledPredicate> StaticPreds;
        public required List<Shumway.Compiler.Wam.CompiledPredicate> DynamicPreds;
        /// <summary>Static-classified predicates absent from the cached
        /// static link (fresh MetaTransform helpers of a recompiled dynamic
        /// predicate) — linked into every query's overlay instead.</summary>
        public required List<Shumway.Compiler.Wam.CompiledPredicate> ExtraQueryPreds;
        public required HashSet<int> CacheableFunctors;
    }

    internal CompiledProgramProduct? _programProduct;

    /// <summary>Bumped by every per-functor invalidation that does NOT move
    /// <see cref="_derivationGen"/> (assertz / asserta / retract on an
    /// existing dynamic functor, JIT hotness flips) — the compiled program
    /// product contains that functor's compile and must rebuild.</summary>
    internal int _programStamp;

    /// <summary>module attribution for dynamic predicates whose
    /// clauses came from a named module: bundle dynamic-seed rehydration
    /// (<see cref="LoadEntryFromBytecode"/>) and source-carrying bundle
    /// entries consulted under the entry's module name. A fid absent here
    /// rewrites under the default user context (runtime asserts, plain
    /// ConsultString — unchanged behaviour). used to sidestep
    /// this by forcing every module-less .shmo to module "user", which made
    /// two module-less files unlinkable (duplicate_module) and aliased
    /// their locals.</summary>
    internal readonly Dictionary<int, string> _dynamicSeedModule = new();

    /// <summary>per-functor cache of the dynamic clause lists'
    /// transform + rewrite (ClausePipeline + ModuleRewrite under the
    /// user-module dynamic context, including any MetaTransform helper
    /// clauses the pipeline synthesised, whose head fids are recorded
    /// alongside). An entry drops when its functor's clause list mutates
    /// (<see cref="InvalidateDynamicCache"/>) or when the rewrite-context
    /// inputs it was built under changed — recorded per entry as the
    /// LOCALS SET INSTANCE (the per-module transform cache reuses the same
    /// HashSet while its module is unchanged, so reference equality is an
    /// exact fingerprint) plus the mode-table version. Entries survive
    /// derivation bumps whose regeneration didn't touch their module —
    /// the old whole-table clear per derivation recompiled every dynamic
    /// predicate at every product build of a library load.</summary>
    internal readonly Dictionary<int, (List<Clause> Clauses, List<int> HeadFids,
            object LocalsRef, int ModesVersion)>
        _dynamicRewriteCache = new();

    /// <summary>Stable empty-locals sentinel for dynamic rewrite contexts —
    /// a fresh <c>new HashSet&lt;int&gt;()</c> per build would defeat the
    /// reference-equality validity check above.</summary>
    internal static readonly HashSet<int> EmptyLocalsSentinel = new();

    /// <summary>ADR-030 elision-result cache across product builds. The
    /// elision DECISIONS are a pure function of the eligible (static) clause
    /// content, the defined-indicator set, and the per-indicator eligibility
    /// (dynamic-ness) — dynamic clause BODIES are never analyzed (ineligible
    /// predicates never enter the det set). So when no module re-transformed
    /// and both fid sets match, the previous build's substitution map
    /// (original clause → elided clause) replays in O(N) reference probes
    /// instead of re-running the whole-program fixpoint.</summary>
    internal Dictionary<Clause, Clause>? _elideSubstitutions;
    internal HashSet<int>? _elideKeyHeadFids;
    internal HashSet<int>? _elideKeyDynFids;

    /// <summary>merged skip-compile cache (the per-query merge
    /// of <see cref="_precompiledClauseCache"/> +
    /// <see cref="_staticPredicateCache"/> + <see cref="_dynamicPredicateCache"/>,
    /// dynamic winning, exactly the precedence the per-query merge used).
    /// Maintained incrementally: nulled wherever
    /// <see cref="_staticPredicateCache"/> is cleared, kept in step with
    /// every <see cref="_dynamicPredicateCache"/> add / remove
    /// (<see cref="DropDynamicPredicateCacheEntry"/>).</summary>
    internal Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>? _skipCompileMergedCache;

    /// <summary>link metadata derived from
    /// <see cref="_staticLink"/> + <see cref="_dynamicLink"/>:
    /// <see cref="_persistentAddressesCache"/> is the merged REAL address
    /// map (fed to the query linker as external symbols — it must NOT
    /// contain aliases, or a query call site to a module-local predicate's
    /// bare name would link-resolve and break module visibility);
    /// <see cref="_persistentAddressBaseCache"/> additionally carries the
    /// bare-name aliases for module-local predicates (the
    /// meta-call alias loop, hoisted out of the per-query path);
    /// <see cref="_persistentPredsByAddressCache"/> is the merged
    /// predicates-by-address map. All three are rebuilt exactly when the
    /// persistent regions are rebuilt (<c>builtPersistentNow</c>); every
    /// <see cref="_staticLink"/> invalidation also forces that via
    /// <see cref="InvalidatePersistent"/>, and the link results themselves
    /// are immutable (the switch-table mirror mutates
    /// <c>_dynamicLink.SwitchTables</c> only, which is why the merged
    /// switch-table list is still rebuilt per query).</summary>
    internal Dictionary<int, int>? _persistentAddressesCache;
    internal Dictionary<int, int>? _persistentAddressBaseCache;
    internal Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>? _persistentPredsByAddressCache;

}
