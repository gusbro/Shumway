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
    private readonly Shumway.Compiler.Wam.LiteralPools _literalPools = new();

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
    private Shumway.Compiler.Wam.Linker.LinkResult? _staticLink;

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
    private byte[]? _persistentProgram;

    /// <summary>Logical end of <see cref="_persistentProgram"/>. The
    /// buffer is over-allocated (capacity-doubled), so a slack tail
    /// of zero bytes (Invalid opcode) follows the valid region; a stray
    /// PC into it fails loudly.</summary>
    private int _persistentLength;

    /// <summary>Cached link result for the dynamic predicates only —
    /// the address map a per-query link uses to resolve calls into
    /// the dynamic region without re-linking it.</summary>
    private Shumway.Compiler.Wam.Linker.LinkResult? _dynamicLink;


    /// <summary>when a query is in flight, the address at
    /// which the per-query overlay begins (the
    /// <see cref="ProgramView"/>'s <c>Split</c>). Persistent growth
    /// mid-query must stay below this, otherwise the query region's
    /// linked offsets collide with newly-extended dynamic bytecode.
    /// The setup code picks this with enough headroom over the
    /// persistent length for typical mid-query <c>assertz</c>
    /// growth.</summary>
    private int _querySplit = -1;

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
    private void InvalidatePersistent()
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
        _dynChainTable = new DynChainTable();
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
    private void InvalidateDynamicCache(int functorId)
    {
        // Every dynamic-store mutation funnels through here (assertz,
        // asserta, retract, abolish), so this is the one place the
        // ADR-015 generation clock has to advance and the
        // auto-compaction mutation counter ticks.
        _dbGeneration.Value++;
        _persistentMutationsSinceCompact++;
        DropDynamicPredicateCacheEntry(functorId);
        // ADR-023 — the predicate changed, so any cached Tier-1 IL snapshot of it
        // is stale: evict it. The next call falls back to the in-place-patched
        // Tier-0 bytecode (the current database); the predicate re-warms before
        // re-snapshotting, and past the churn limit stays on Tier 0.
        IlPromotion.EvictDelegate(functorId);
        // ADR-023/034 — every LIVE query's interpreter snapshotted the
        // delegate into its direct fid table at setup (IL callers dispatch
        // every callee by fid through it via resume markers). Clear the slot
        // in ALL of them so the very next dispatch re-resolves live — TryGet
        // misses, and the call falls back to the Tier-0 enter_dynamic chain
        // (the logical update view). ALL, not just the current one: a
        // SUSPENDED outer activation resumes with its own table, and a stale
        // slot there dispatched an evicted dynamic snapshot (the
        // Logtalk-under-promotion silent failure).
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
        // ADR-034 — and any CALLER whose IL embeds this predicate's inlined
        // snapshot must stop using it: the emitted clause-entry staleness test
        // reads this host-lifetime set (shared into every per-query engine),
        // so the fallback path takes over from the very next clause entry.
        _mutatedDynamicFids.Add(functorId);
        // the functor's clause list changed, so its cached
        // transformed/rewritten clauses are stale too.
        _dynamicRewriteCache.Remove(functorId);
    }

    /// <summary>removes a dynamic predicate's compiled-bytecode
    /// cache entry and keeps the merged skip-compile cache in step, falling
    /// back to the static / precompiled tier's entry when one exists (the
    /// same precedence the merge uses: precompiled &lt; static &lt;
    /// dynamic). Used by <see cref="InvalidateDynamicCache"/> and the
    /// JIT hotness-flip drops at query setup.</summary>
    private void DropDynamicPredicateCacheEntry(int functorId)
    {
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

}
