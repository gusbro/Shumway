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
    // ============================================================================
    // Dynamic predicate runtime store (asserts / retracts)
    // ============================================================================

    /// <summary>Adds <paramref name="clause"/> to the end of its predicate's
    /// dynamic clause list. The predicate must have been declared
    /// <c>:- dynamic foo/N</c> previously (in any module). Returns the
    /// head's functor id (the caller needs it for the
    /// incremental dispatch update; returning it avoids a second
    /// extraction's string intern).</summary>
    internal int Assertz(Clause clause)
    {
        clause = Shumway.Compiler.Ast.ClauseBodyConversion.Convert(clause);
        int fid = ExtractHeadFunctorId(clause);
        EnsureDynamic(fid);
        GetOrCreateDynamicSlot(fid).Add(clause);
        InvalidateDynamicCache(fid);
        return fid;
    }

    /// <summary>Adds <paramref name="clause"/> at the front of its predicate's
    /// dynamic clause list. Returns the head's functor id.</summary>
    internal int Asserta(Clause clause)
    {
        clause = Shumway.Compiler.Ast.ClauseBodyConversion.Convert(clause);
        int fid = ExtractHeadFunctorId(clause);
        EnsureDynamic(fid);
        GetOrCreateDynamicSlot(fid).Insert(0, clause);
        InvalidateDynamicCache(fid);
        // persistent invalidation moved into
        // PrependDynamicClauseIncremental — the in-place path can
        // handle most indexed-dynamic asserta cases now, and only
        // genuinely-unhandled ones force a rebuild.
        return fid;
    }

    /// <summary>Removes the first clause whose <see cref="Clause"/> is
    /// structurally equal to <paramref name="clause"/>. Returns
    /// <c>true</c> if a match was removed.</summary>
    internal bool RemoveDynamic(Clause clause)
    {
        int fid = ExtractHeadFunctorId(clause);
        if (!_dynStore.TryGetClauses(fid, out var list)) return false;
        for (int i = 0; i < list.Count; i++)
        {
            if (TermsStructurallyEqual(list[i].Term, clause.Term))
            {
                list.RemoveAt(i);
                InvalidateDynamicCache(fid);
                if (_jitIndexProfile.IsHot(fid)) InvalidatePersistent();
                return true;
            }
        }
        return false;
    }

    /// <summary>Snapshot of currently asserted clauses for a given functor —
    /// used by the runtime <c>retract/1</c> path to enumerate candidates
    /// before unifying with the user's pattern.</summary>
    internal IReadOnlyList<Clause> DynamicClausesFor(int functorId)
    {
        return _dynStore.TryGetClauses(functorId, out var list)
            ? list
            : Array.Empty<Clause>();
    }

    /// <summary>ADR-023 — compiles a STATIC-style snapshot of dynamic predicate
    /// <paramref name="fid"/>'s currently-visible clauses (a plain
    /// <c>try_me_else</c> chain, no <c>enter_dynamic</c> / <c>check_visible</c>),
    /// for Tier-1 IL promotion. Reuses the same transformed clauses the dynamic
    /// bytecode is built from (<see cref="_dynamicRewriteCache"/>, populated at
    /// query setup), filtered to this predicate's own clauses — the MetaTransform
    /// helper clauses it may have spawned are separate predicates. Returns null
    /// when the predicate has no visible clauses or its rewrite cache isn't built
    /// yet; <see cref="IlPromotionStore"/> treats null as a retry, not a permanent
    /// rejection. A later mutation evicts the snapshot (see
    /// <see cref="InvalidateDynamicCache"/>).</summary>
    internal Shumway.Compiler.Wam.CompiledPredicate? BuildDynamicSnapshot(int fid)
    {
        if (!_dynStore.TryGetClauses(fid, out var raw) || raw.Count == 0) return null;
        if (!_dynamicRewriteCache.TryGetValue(fid, out var entry)) return null;
        var own = new List<Clause>(entry.Clauses.Count);
        for (int i = 0; i < entry.Clauses.Count; i++)
            if (entry.HeadFids[i] == fid) own.Add(entry.Clauses[i]);
        if (own.Count == 0) return null;
        var snap = new Shumway.Compiler.Wam.PredicateCompiler { EmitDebugInfo = _flags.EmitDebugInfo }
            .Compile(own, _literalPools.Strings, _literalPools.Floats, _literalPools.BigInts,
                enableIndexing: true, isDynamic: false, failStubAddr: 0);
        // ADR-034 — mark the snapshot so caller-side inlining knows this
        // "static-looking" predicate is really a dynamic whose truth can
        // change; having rules (per the RAW source clauses — the transformed
        // ones may have been rewritten) gates checked caller-inlining.
        snap.IsDynamicSnapshot = true;
        foreach (var c in raw)
            if (c.Kind == Shumway.Compiler.Ast.ClauseKind.Rule)
            {
                snap.SnapshotHasRules = true;
                break;
            }
        return snap;
    }

    /// <summary>ADR-023 build-time persist (for <c>--with-compiled-il</c> / <c>--exe</c>
    /// bundles) — like <see cref="BuildDynamicSnapshot"/>, but returns null when the
    /// snapshot references a string / float / bigint literal that is NOT already in
    /// this engine's (bundle-loaded) literal pools. Those literals are referenced by
    /// pool INDEX, and the persisted IL bakes the index. A runtime process that loads
    /// the bundle populates its pools from the bundle bytecode only — it never compiles
    /// the snapshot — so a snapshot-only literal would not be present at that index and
    /// the baked IL would read the wrong value. Atoms and functors are patched by NAME
    /// at load (<see cref="IlPatchKind"/>), so they are always safe; only the three
    /// index-addressed pools constrain persistability. A predicate that fails this test
    /// is simply not baked — it stays Tier-0 and (in a JIT process) runtime-promotes
    /// normally. Returns null exactly when not safe to persist.</summary>
    internal Shumway.Compiler.Wam.CompiledPredicate? BuildPersistableDynamicSnapshot(int fid)
    {
        int s0 = _literalPools.Strings.Count;
        int b0 = _literalPools.BigInts.Count;
        var snap = BuildDynamicSnapshot(fid);
        if (snap is null) return null;
        // Float literals are value-baked into the IL (ldc.r8), so a snapshot-only
        // float is fine. Strings / bigints are still index-addressed and would
        // mis-read at runtime, so a snapshot that introduces one stays Tier-0.
        if (_literalPools.Strings.Count != s0
            || _literalPools.BigInts.Count != b0)
            return null;
        return snap;
    }

    /// <summary>True iff the given functor was declared
    /// <c>:- dynamic</c>. Exposed to <c>MetaBuiltins.Retract</c> /
    /// <c>Abolish</c> so they can raise the ISO
    /// <c>permission_error(modify, static_procedure, _)</c> rather than
    /// silently failing on a static predicate.</summary>
    internal bool IsDynamic(int functorId) => _dynStore.IsDynamic(functorId);

    /// <summary>True when any loaded module declared this functor
    /// <c>:- multifile</c>. Reported by predicate_property/2.</summary>
    internal bool IsMultifileFunctor(int functorId)
    {
        foreach (var m in _modules.Values)
            if (m.MultifileFunctors.Contains(functorId)) return true;
        return false;
    }

    /// <summary>Snapshot of every functor declared <c>:- dynamic</c>.
    /// Used by <c>garbage_collect_clauses/0</c> to iterate them.
    ///</summary>
    internal IEnumerable<int> AllDynamicFunctors() => _dynStore.Functors.ToArray();

    // single spare buffer reused across retract/1's remaining-
    // candidates snapshots (the ISO call-time view copied at CP-push time).
    // One buffer covers the overwhelmingly common case (non-nested retract);
    // a nested enumeration that misses simply allocates, exactly like the
    // pre-431 code. Lifecycle: the buffer is exclusively owned by one
    // enumeration from Rent until the matching Return, which fires at
    // exactly one of (a) the resume's no-further-match failure, (b) the
    // resume's last-candidate success (no new CP pushed), or (c) the CP's
    // OnPrune when a cut discards it (fired exactly once, and
    // cleared on pop so a resumed CP can never also prune). Discard paths
    // with no hook (exception unwind, query teardown) just drop the buffer
    // to the .NET GC — same as pre-431, never a double-hand-out.

    /// <summary>returns a buffer with at least
    /// <paramref name="minLength"/> slots for a retract tail snapshot,
    /// reusing the per-engine spare when it fits.</summary>
    internal Clause[] RentRetractSnapshot(int minLength) => _dynStore.RentRetractSnapshot(minLength);

    /// <summary>hands a snapshot buffer back for reuse. Clears
    /// the used range so the pool never pins retracted clause ASTs alive,
    /// and keeps the larger of (current spare, returned buffer), capped so
    /// one huge predicate can't park a giant array on the engine.</summary>
    internal void ReturnRetractSnapshot(Clause[] buffer, int usedCount)
        => _dynStore.ReturnRetractSnapshot(buffer, usedCount);

    /// <summary>Removes the clause object identical to <paramref name="clause"/>
    /// from the dynamic store (used after the runtime caller has matched it
    /// via unification on a materialised heap copy). When ADR-015 chunk C
    /// chain state exists for the functor, also patches the matching
    /// clause's <c>died</c> slot in the running program's bytecode so an
    /// already-compiled dispatch's <c>check_visible</c> filters it out
    /// from now on.</summary>
    // ---- clause references (asserta/2, clause/3, erase/1) ----
    // Opaque ids handed out lazily per Clause OBJECT (the dynamic store
    // keeps the identical instance from assert to retract, so identity is
    // the stable key; clause_3_02 requires the same clause to yield the
    // same reference on every lookup). Stale entries after erase/abolish
    // are harmless: every fetch re-verifies liveness against the store.
    private long _nextClauseRefId = 1;
    private readonly Dictionary<long, (int Fid, Clause Clause)> _clauseRefsById = new();
    private readonly Dictionary<Clause, long> _clauseRefIds = new(ReferenceEqualityComparer.Instance);

    internal long ClauseRefFor(int fid, Clause clause)
    {
        if (_clauseRefIds.TryGetValue(clause, out long id)) return id;
        id = _nextClauseRefId++;
        _clauseRefIds[clause] = id;
        _clauseRefsById[id] = (fid, clause);
        return id;
    }

    internal bool TryGetClauseByRef(long id, out int fid, out Clause clause)
    {
        if (_clauseRefsById.TryGetValue(id, out var e))
        {
            (fid, clause) = e;
            // liveness: the clause must still sit in its predicate's list.
            if (_dynStore.TryGetClauses(fid, out var list))
                for (int i = 0; i < list.Count; i++)
                    if (ReferenceEquals(list[i], clause)) return true;
        }
        fid = 0;
        clause = null!;
        return false;
    }

    internal bool RemoveDynamicByReference(
        Activation engine, int functorId, Clause clause, int knownIndex = -1)
    {
        if (!_dynStore.TryGetClauses(functorId, out var list)) return false;
        // retract's first step scans the live list, so it
        // already knows the match's index — trust it after a cheap
        // reference check instead of an O(N) IndexOf (Blint: 80K
        // retracts × an ~125-entry IndexOf walk). The resume path
        // scans a tail snapshot whose indices don't map; it passes -1.
        int idx = knownIndex >= 0 && knownIndex < list.Count
                  && ReferenceEquals(list[knownIndex], clause)
            ? knownIndex
            : list.IndexOf(clause);
        if (idx < 0) return false;
        // for a indexed predicate, capture the
        // matched clause's body address from the var chain BEFORE
        // removing the clause from _dynamicClauses (the var-chain
        // walk reads idx-indexed entries in their pre-removal order).
        bool isIndexed = IsExtensibleIndexedLayout(engine, functorId);
        int retiredBodyAddr = -1;
        if (isIndexed)
            retiredBodyAddr = FindBodyAddrForClauseIndex(engine, functorId, idx);
        list.RemoveAt(idx);
        InvalidateDynamicCache(functorId);
        // a retract from a non-owner engine (a nested query
        // rebuilt the host buffer since this engine's setup) patches only
        // this engine's buffer; the host's newer buffer would keep the
        // clause visible on cross-query reuse. Force a rebuild from the
        // (already-updated) store. Owner engines pay nothing.
        if (!EngineOwnsHostBuffer(engine)) InvalidatePersistent();
        // For the chain layout, PatchDiedFromChain walks
        // the predicate's single chain and patches entry[idx]'s
        // died slot. For the indexed layout, the chain
        // state populated by PopulateDynChainFor lists every chain
        // entry from every bucket + the var chain in CONTIGUOUS
        // bytecode order (PopulateDynChainFor does a linear walk),
        // so entry[idx] maps to an unrelated bucket's died slot
        // — skip the plain-chain path here and let
        // the multi-chain patching below handle it.
        if (!isIndexed)
        {
            // patch by CLAUSE IDENTITY, not by store index. The
            // index-based patch was sound when the single chain mirrored
            // _dynamicClauses 1:1; with per-engine chain tables an engine's
            // chain can lag the store (a broadcast skipped by a guard, or
            // entries added while this engine didn't exist), so the store
            // index lands on the WRONG entry — killing a live clause in
            // this engine's own view while the retracted one stays visible
            // (observed as Logtalk's loading-stack "ghost" entries).
            PatchDiedFromChainByClause(engine, functorId, clause);
            // reclaim accumulated dead clauses from the chain
            // when it's safe (no in-progress enumeration of this
            // predicate). An assert+retract loop (e.g. next_char_i) would
            // otherwise grow the chain with retracted-but-still-linked
            // clauses that every dispatch must skip via check_visible —
            // profiling Blint showed 99.3% of dynamic dispatch walking
            // dead clauses.
            TryReclaimDeadDynamicChain(engine, functorId);
        }
        // broadcast the retract to every other live engine's
        // buffer (matched by clause identity — chain entries hold the same
        // Clause objects the store held), so a suspended outer query's
        // dispatch also stops seeing the clause when it resumes.
        StackDiag("retract", engine, functorId);
        if (OtherLiveEnginesByTable(engine) is { } others)
            foreach (var other in others)
            {
                StackDiag("retract-bcast", other, functorId);
                if (PatchDiedFromChainByClause(other, functorId, clause) == 0)
                    // No chain record — the target's view may use the
                    // indexed layout (its bucket entries the chain table
                    // can't patch); rebuild that fid's view from the store
                    // so the retracted clause doesn't survive there.
                    RebuildEngineFidChainView(other, functorId);
            }
        if (retiredBodyAddr > 0
            && TryPatchDiedInAllIndexedChains(engine, functorId, retiredBodyAddr))
            return true;
        // Fallback: hot indexed predicate that we couldn't retract in
        // place → rebuild via persistent invalidation.
        if (_jitIndexProfile.IsHot(functorId)) InvalidatePersistent();
        return true;
    }


    /// <summary>Minimum number of dead (retracted-but-still-linked)
    /// clauses in a dynamic chain before reclamation kicks in.
    /// Re-threading costs O(live) pointer patches (it does NOT recompile
    /// anything — see <see cref="GarbageCollectClauses"/>), so this only
    /// amortises the choice-point scan and the patch writes. Every
    /// dispatch between reclaims walks up to this many tombstones — the
    /// real cost for read-heavy churn idioms (Blint's <c>next_char_i</c>
    /// unget buffer is READ via <c>call/1</c> dispatch ~105K times per
    /// lint, each walking the tombstones the threshold lets linger).
    /// swept the value on Blint's deterministic opcode count:
    /// 32→29.21M, 16→28.40M, 8→28.02M, 4→27.78M, 2→27.69M. 4 is the
    /// knee — below it the per-fire fixed costs (the CP-stack scan and
    /// the chainAddrs set) buy almost nothing.</summary>
    private const int ReclaimDeadThreshold = 4;

    /// <summary>number of times the automatic dead-chain
    /// reclamation actually fired across this engine's lifetime.
    /// Deterministic diagnostic for tests; the re-thread itself lives in
    /// the persistent program bytes.</summary>
    public long ChainReclaims { get; private set; }

    /// <summary>physically drops retracted-but-still-linked
    /// clauses from a dynamic predicate's chain by rebuilding
    /// it from the live clauses, but ONLY when no in-progress enumeration
    /// could still need them (ISO logical update view). A clause
    /// retracted while a call is enumerating the predicate must stay
    /// visible to that call; such a call has a choice point whose resume
    /// address (SavedBp) points at a chunk in this chain. So reclamation
    /// is safe exactly when no active choice point resumes into the
    /// chain. A fresh call re-samples the current generation at
    /// enter_dynamic and never sees the dropped clauses.
    ///
    /// <para>dropped the old <c>dead &lt; Entries.Count</c>
    /// gate (a leftover from when reclamation recompiled
    /// the live clauses): it made the steady-state tombstone load scale
    /// with the LIVE clause count, so a busy predicate with ~125 live
    /// entries (Blint's <c>saved_cur_line_i/2</c> save-stack) sat
    /// permanently at ~100 tombstones that every read walked — 1.55M
    /// retry dispatches per lint. The re-thread is O(live) pointer
    /// patches, so the dead count alone is the right trigger.</para></summary>
    private void TryReclaimDeadDynamicChain(Activation engine, int functorId)
    {
        if (engine.CurrentProgram is null) return;
        if (GetChainTable(engine) is not { } tbl
            || !tbl.Chains.TryGetValue(functorId, out var chain)) return;
        // Only the plain trampoline+chain layout. Indexed
        // dynamic predicates use the rebuild-on-mutate
        // fallback and aren't handled here.
        if (chain.HeadClauseAddr < 0 || chain.TrampolineExecuteOperandAddr < 0) return;
        int dead = chain.DeadChunks.Count;
        if (dead < ReclaimDeadThreshold) return;
        // A source-block clause (ChunkAddr < 0) isn't individually
        // relocatable — skip reclamation if the chain holds any.
        foreach (var e in chain.Entries)
            if (e.ChunkAddr < 0) return;

        // Safety: collect every chunk start address in this chain (live
        // entries + dead chunks + the head). A choice point enumerating
        // the predicate has SavedBp at one of these. If any active CP
        // does, an enumeration is in progress — keep the dead clauses.
        var chainAddrs = new HashSet<int>();
        foreach (var e in chain.Entries) chainAddrs.Add(e.ChunkAddr);
        foreach (var (a, _) in chain.DeadChunks) chainAddrs.Add(a);
        chainAddrs.Add(chain.HeadClauseAddr);
        foreach (var (_, savedBp, _) in engine.EnumerateChoicePoints())
            if (chainAddrs.Contains(savedBp)) return;
        ChainReclaims++;

        // Safe and worthwhile — re-thread the chain through its live
        // entries in place. This keeps the trampoline at its
        // address, so every caller's already-baked Call operand stays
        // valid (rebuilding the trampoline at a new address would orphan
        // them); it only patches the in-chunk <next> links to bypass the
        // dead entries, and drains the dead chunks to the free list for
        // reuse by later assertz/asserta. The "avoid mid-query
        // while another goal iterates this predicate" caveat is exactly
        // the safety condition checked above.
        GarbageCollectClauses(engine, functorId, reclaimChunks: false);
    }

    /// <summary>Removes every asserted clause of the given dynamic functor and
    /// drops the functor from the dynamic registry, so subsequent calls raise
    /// "not declared dynamic" rather than fail silently. Mirrors ISO
    /// <c>abolish/1</c>.</summary>
    internal void AbolishDynamic(int functorId)
    {
        _dynStore.RemoveSlot(functorId);
        _dynStore.UnmarkDynamic(functorId);
        _dynStore.Abolished.Add(functorId);
        InvalidateDynamicCache(functorId);
        // dropping a dynamic functor changes the
        // dynamic-region layout — the next query has to rebuild
        // the persistent program.
        InvalidatePersistent();
    }

    /// <summary>ADR-015 chunk C step 4 — engine-aware overload that also
    /// patches the <c>died</c> slot of every chain entry in place, so an
    /// already-compiled dispatch in the running program filters all the
    /// abolished clauses out via <c>check_visible</c>.</summary>
    internal void AbolishDynamic(Activation engine, int functorId)
    {
        AbolishDynamic(functorId);              // bumps _dbGeneration
        AbolishDynamicInChain(engine, functorId);
        // broadcast: suspended engines' dispatch must also
        // stop seeing the abolished clauses when they resume.
        if (OtherLiveEnginesByTable(engine) is { } others)
            foreach (var other in others)
                AbolishDynamicInChain(other, functorId);
    }

    private void AbolishDynamicInChain(Activation engine, int functorId)
    {
        if (engine.CurrentProgram is null) return;
        if (GetChainTable(engine) is not { } tbl
            || !tbl.Chains.TryGetValue(functorId, out var chain)) return;
        var program = engine.CurrentProgram;
        foreach (var entry in chain.Entries)
        {
            if (entry.DiedOperandAddr > 0
                && entry.DiedOperandAddr + sizeof(long) <= program.Length)
                Shumway.Core.BytecodeIO.WriteInt64(
                    program, entry.DiedOperandAddr, _dbGeneration.Value);
            // stage incremental chunks for GC reclamation.
            if (entry.ChunkAddr >= 0)
                chain.DeadChunks.Add((entry.ChunkAddr, entry.ChunkLength));
        }
        chain.Entries.Clear();
    }

    // ============================================================================
    // Arity save/0,1 + restore/0,1 — dynamic-database snapshots
    // ============================================================================

    /// <summary>The in-memory <c>save/0</c> snapshot: functor id → a copy of
    /// its clause list at save time. Null = no <c>save/0</c> yet — restore
    /// treats that as the EMPTY snapshot (wipes every user dynamic).
    /// Clause objects are immutable ASTs, so sharing them is safe.</summary>
    private Dictionary<int, List<Clause>>? _dbSnapshot;

    /// <summary>Arity save/restore operate on USER dynamics only: engine /
    /// library internals (tabling's <c>$tbl_*</c>, <c>$wfs_mode</c>, the
    /// prelude's <c>$prelude$…</c> locals) are excluded from both the
    /// snapshot and the restore wipe — wiping them mid-session would corrupt
    /// engine-internal state. User module-local dynamics (mangled
    /// <c>module$name</c>) don't start with <c>$</c> and are included.</summary>
    private static bool IsUserDynamicFid(int fid)
    {
        var (atomId, _) = Shumway.Core.FunctorTable.Lookup(fid);
        string? name = Shumway.Core.AtomTable.GetById(atomId)?.Name;
        return !string.IsNullOrEmpty(name) && name![0] != '$';
    }

    /// <summary>Removes every clause of a dynamic functor while KEEPING its
    /// <c>:- dynamic</c> declaration (unlike <see cref="AbolishDynamic(int)"/>):
    /// calls fail instead of raising, and a later assert works normally.
    /// Mid-query correct — patches the live chains' <c>died</c> slots (this
    /// engine + suspended siblings) and runs the full invalidation funnel
    /// (caches, IL eviction, the ADR-034 mutated-fids set).</summary>
    internal void ClearDynamicClauses(Activation engine, int functorId)
    {
        if (_dynStore.TryGetClauses(functorId, out var list)) list.Clear();
        InvalidateDynamicCache(functorId);
        InvalidatePersistent();
        AbolishDynamicInChain(engine, functorId);
        if (OtherLiveEnginesByTable(engine) is { } others)
            foreach (var other in others)
                AbolishDynamicInChain(other, functorId);
    }

    /// <summary><c>save/0</c> — snapshots the user dynamic database (clause
    /// lists) in memory, replacing any previous snapshot.</summary>
    internal void SaveDb()
    {
        var snap = new Dictionary<int, List<Clause>>();
        foreach (var (fid, list) in _dynStore.Slots)
            if (list.Count > 0 && IsUserDynamicFid(fid))
                snap[fid] = new List<Clause>(list);
        _dbSnapshot = snap;
    }

    /// <summary><c>restore/0</c> — destructive REPLACE: wipes every user
    /// dynamic predicate's clauses (declarations survive) and re-installs the
    /// last <c>save/0</c> snapshot. No snapshot = the empty snapshot: the
    /// wipe alone. Statics are never touched.</summary>
    internal void RestoreDb(Activation engine) => RestoreDbFrom(engine, _dbSnapshot);

    private void RestoreDbFrom(Activation engine, Dictionary<int, List<Clause>>? snapshot)
    {
        // Wipe first — every user dynamic that currently has clauses.
        var toClear = new List<int>();
        foreach (var (fid, list) in _dynStore.Slots)
            if (list.Count > 0 && IsUserDynamicFid(fid))
                toClear.Add(fid);
        foreach (int fid in toClear)
            ClearDynamicClauses(engine, fid);

        if (snapshot is null) return;
        // Re-install through the canonical mutation path: the store gets the
        // clause, and the incremental chain append makes the restored state
        // visible to the RUNNING query's dispatch (ADR-015 logical update
        // view), exactly as a sequence of assertz would. The snapshot is
        // keyed by fid (not re-derived from the head term) so module-local
        // dynamics whose storage name is mangled restore to the right slot.
        foreach (var (fid, clauses) in snapshot)
        {
            EnsureDynamic(fid);
            var slot = GetOrCreateDynamicSlot(fid);
            foreach (var c in clauses)
            {
                slot.Add(c);
                InvalidateDynamicCache(fid);
                AppendDynamicClauseIncremental(engine, fid, c);
            }
        }
    }

    private const uint DbSnapshotMagic = 0x53484442;   // "SHDB"
    private const int DbSnapshotVersion = 1;

    /// <summary><c>save(+File)</c> — like <see cref="SaveDb"/> but writes the
    /// snapshot to <paramref name="path"/> (a compact binary: per-predicate
    /// storage name/arity + <see cref="TermCodec"/>-encoded clauses).</summary>
    internal void SaveDbToFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs, System.Text.Encoding.UTF8);
        var preds = new List<(int Fid, List<Clause> Clauses)>();
        foreach (var (fid, list) in _dynStore.Slots)
            if (list.Count > 0 && IsUserDynamicFid(fid))
                preds.Add((fid, list));
        w.Write(DbSnapshotMagic);
        w.Write(DbSnapshotVersion);
        w.Write(preds.Count);
        foreach (var (fid, clauses) in preds)
        {
            var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
            w.Write(Shumway.Core.AtomTable.GetById(atomId)!.Name);
            w.Write(arity);
            w.Write(clauses.Count);
            foreach (var c in clauses)
            {
                byte[] bytes = TermCodec.EncodeClause(c);
                w.Write(bytes.Length);
                w.Write(bytes);
            }
        }
    }

    /// <summary><c>restore(+File)</c> — <see cref="RestoreDb"/> semantics,
    /// with the snapshot read from <paramref name="path"/>. Throws
    /// <see cref="InvalidDataException"/> when the file is not a
    /// <c>save/1</c> snapshot.</summary>
    internal void RestoreDbFromFile(Activation engine, string path)
    {
        var snapshot = new Dictionary<int, List<Clause>>();
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
        using (var r = new BinaryReader(fs, System.Text.Encoding.UTF8))
        {
            if (r.ReadUInt32() != DbSnapshotMagic)
                throw new InvalidDataException("not a save/1 snapshot (bad magic)");
            int version = r.ReadInt32();
            if (version != DbSnapshotVersion)
                throw new InvalidDataException($"unsupported snapshot version {version}");
            int predCount = r.ReadInt32();
            for (int p = 0; p < predCount; p++)
            {
                string name = r.ReadString();
                int arity = r.ReadInt32();
                int fid = Shumway.Core.FunctorTable.Intern(
                    Shumway.Core.AtomTable.Intern(name, permanent: true).Id, arity);
                int clauseCount = r.ReadInt32();
                var clauses = new List<Clause>(clauseCount);
                for (int i = 0; i < clauseCount; i++)
                {
                    int len = r.ReadInt32();
                    clauses.Add(TermCodec.DecodeClause(r.ReadBytes(len)));
                }
                snapshot[fid] = clauses;
            }
        }
        RestoreDbFrom(engine, snapshot);
    }

    /// <summary>re-threads <paramref name="functorId"/>'s
    /// chain through only its live entries, bypassing every dead
    /// (retracted or abolished) entry in the running bytecode. The
    /// dead entries' bytecode is left in place (orphaned but
    /// harmless); the win is dispatch speed — future calls walk
    /// O(live) entries instead of O(ever-asserted).
    ///
    /// <para>Safe to call between queries. The design
    /// captures view-gen at goal entry and saves CP <c>NextOperandAddr</c>
    /// values into per-CP state, so in-flight goals that already hold a
    /// CP into a dead entry's <c>retry_me_else</c> address still resume
    /// there correctly — they hit the dead entry's
    /// <c>check_visible</c> which filters by their captured view-gen.
    /// Calling GC mid-query while another goal is iterating the same
    /// dynamic predicate is the case to avoid; documentation only.</para>
    /// </summary>
    internal int GarbageCollectClauses(Activation engine, int functorId, bool reclaimChunks = true)
    {
        if (GetChainTable(engine) is not { } tbl
            || !tbl.Chains.TryGetValue(functorId, out var chain)) return 0;
        if (engine.CurrentProgram is null) return 0;
        if (chain.HeadClauseAddr < 0) return 0;
        var program = engine.CurrentProgram;
        int failStub = engine.DynamicFailStubAddr;

        // skip the reclaim when the chain's cached offsets are
        // stale relative to the live buffer (bounds OR structural: re-threading
        // through them would write past the array, or splice a wrong <next>
        // into an unrelated instruction → heap corruption surfacing later as an
        // out-of-range cell in a thrown ball). Dead-chunk reclamation is
        // optional; the chain rebuilds cleanly at the next query setup. No
        // InvalidatePersistent (would desync other live chains mid-load).
        if (DynChainAddressesStale(chain, program.Length, failStub)
            || DynChainStructurallyStale(program, chain))
            return 0;

        // The trampoline always points at chain.HeadClauseAddr, which
        // is a try_me_else of stable 9-byte footprint — either the
        // empty stub emitted at consult-time, or an asserta'd clause
        // emitted by with native try_me_else. The GC never
        // touches the trampoline or the head opcode (the alternative
        // would be promoting a non-head retry_me_else to try_me_else,
        // which is safe only when the entry has the
        // 4-Nop-pad — a native 5-byte retry_me_else from assertz has
        // its check_visible at bytes 5-8 and can't be widened in
        // place). Instead, GC re-threads the <next> chain starting
        // from the head: dead entries are skipped by patching the
        // previous live entry's (or the head's) <next> to point at
        // the next live entry's chain-instruction address.
        //
        // If the head itself is dead (retracted from the asserta'd
        // head, or the empty stub that's always "dead"
        // with check_visible(0, 0)), dispatch walks it once, the
        // check_visible filters it, and the GC-patched <next> jumps
        // straight to the first live entry. One extra walk per call;
        // negligible compared to the O(n) walk over n dead entries
        // the GC eliminates.

        int prevNext = chain.HeadClauseAddr + 1;   // head's <next> operand
        foreach (var entry in chain.Entries)
        {
            int entryClauseAddr = entry.NextOperandAddr - 1;
            if (entryClauseAddr == chain.HeadClauseAddr)
            {
                // The head itself is this live entry — its <next>
                // is already the right anchor for the next jump;
                // don't patch it to point at itself.
                prevNext = entry.NextOperandAddr;
                continue;
            }
            Shumway.Core.BytecodeIO.WriteInt32(program, prevNext, entryClauseAddr);
            prevNext = entry.NextOperandAddr;
        }
        // Tail's <next> goes to the fail-stub.
        Shumway.Core.BytecodeIO.WriteInt32(program, prevNext, failStub);
        chain.TailNextAddr = prevNext;

        // Drain the dead-chunk staging into the engine-wide free
        // list so subsequent incremental assertz / asserta can
        // reuse the bytes (a long-lived engine that retracts and
        // re-asserts thousands of clauses then has bounded memory
        // growth instead of monotonic). Returns the total bytes
        // reclaimed for diagnostics.
        // Mid-query reclamation (reclaimChunks=false): leave the dead
        // chunks' bytecode in place rather than recycling it. A choice
        // point still in flight (e.g. retract's own re-satisfiable CP,
        // or a failure-driven loop's outer CP) may resume into a dead
        // chunk; check_visible then filters it by captured
        // view-gen — but only if the bytecode is intact. Recycling an
        // address an in-flight CP still references would let a later
        // assertz overwrite a live retry_me_else (observed as
        // "RetryMeElse without an active choice point"). The bypassed
        // bytes are reclaimed by the persistent compaction
        // between queries instead. We still clear DeadChunks so the
        // reclaim threshold resets.
        int reclaimed = 0;
        if (reclaimChunks)
        {
            foreach (var (addr, length) in chain.DeadChunks)
            {
                tbl.FreeChunks.Add((addr, length));
                reclaimed += length;
            }
        }
        chain.DeadChunks.Clear();
        return reclaimed;
    }

    /// <summary>Static clauses whose head functor matches
    /// <paramref name="functorId"/>, across every loaded module. Used by
    /// <c>clause/2</c> as the static half of the lookup; dynamic clauses
    /// come from <see cref="DynamicClausesFor"/>.</summary>
    internal IEnumerable<Clause> StaticClausesFor(int functorId)
    {
        foreach (var manifest in _modules.Values)
        {
            foreach (var c in manifest.Clauses)
            {
                if (TryExtractHead(c, out string n, out int a))
                {
                    int fid = FunctorTable.Intern(
                        AtomTable.Intern(n, permanent: true).Id, a);
                    if (fid == functorId) yield return c;
                }
            }
        }
    }

    /// <summary>The user-defined predicates eligible for <c>listing/0,1</c>:
    /// every static predicate of a user module (never <c>$prelude</c> or
    /// <c>clpfd</c>, and never a builtin) plus every dynamic predicate that
    /// currently holds clauses. Each is flagged dynamic-or-not so listing
    /// can print a <c>:- dynamic</c> header for the dynamic ones.</summary>
    /// <summary>strips the <c>&lt;module&gt;$</c>
    /// prefix that <see cref="ModuleRewrite"/> adds to local
    /// predicates so the listing path can present users with the
    /// name they actually wrote. <c>user$helper</c> →
    /// <c>helper</c>, <c>foo$bar$baz</c> → <c>bar$baz</c> (only
    /// the first prefix segment is removed, so a user predicate
    /// that legitimately contains <c>$</c> survives intact).
    /// Names without a <c>$</c> pass through unchanged.</summary>
    public static string DemangleLocalName(string mangled)
    {
        int sep = mangled.IndexOf('$');
        if (sep <= 0) return mangled;
        return mangled.Substring(sep + 1);
    }

    /// <summary>ADR-035 — the debugger-facing name of a synthesised control-construct helper.
    ///
    /// <para><see cref="Shumway.Compiler.Parsing.MetaTransform"/> rewrites a control construct
    /// into a call to a fresh helper predicate — <c>catch/3</c> becomes <c>'$catchgoal_N'</c>,
    /// <c>\+</c> becomes <c>'$neg_N'</c>, <c>once/1</c> / <c>ignore/1</c> become
    /// <c>'$once_N'</c> / <c>'$ign_N'</c>, a disjunction becomes <c>'$disj_N'</c>. A debugger
    /// stopped on (or showing a frame for) one of these should name the construct the USER
    /// wrote, not the internal helper it was lowered to. Given the demangled helper name (and
    /// its helper arity), returns the construct's <c>(Name, Arity)</c>; anything unrecognised is
    /// returned unchanged.</para></summary>
    internal static (string Name, int Arity) DebugConstructName(string demangled, int arity)
    {
        if (demangled.StartsWith("$catchgoal_", StringComparison.Ordinal)
            || demangled.StartsWith("$catchrec_", StringComparison.Ordinal))
            return ("catch", 3);
        if (demangled.StartsWith("$neg_", StringComparison.Ordinal)) return ("\\+", 1);
        if (demangled.StartsWith("$once_", StringComparison.Ordinal)) return ("once", 1);
        if (demangled.StartsWith("$ign_", StringComparison.Ordinal)) return ("ignore", 1);
        // ADR-035 (Camino B) — the all-solutions meta-predicates lower to a $disj /
        // $neg collect loop tagged with their own kind (MetaTransform.NextHelperKind),
        // so they show and stop as the goal the user actually wrote, not a transparent
        // ';'. A genuine user ';'/'->' stays a $disj_N and is rendered transparent.
        if (demangled.StartsWith("$findall_", StringComparison.Ordinal)) return ("findall", 3);
        if (demangled.StartsWith("$bagof_", StringComparison.Ordinal)) return ("bagof", 3);
        if (demangled.StartsWith("$setof_", StringComparison.Ordinal)) return ("setof", 3);
        if (demangled.StartsWith("$forall_", StringComparison.Ordinal)) return ("forall", 2);
        if (demangled.StartsWith("$disj_", StringComparison.Ordinal)) return (";", 2);
        return (demangled, arity);
    }

    /// <summary>for a source-stripped bundle the engine
    /// has no AST to print, but it does have the
    /// <see cref="Shumway.Compiler.Wam.CompiledPredicate"/>
    /// metadata (arity + clause count) in
    /// <see cref="_precompiledStaticPredicates"/>. Listing falls
    /// back to a comment line so the user at least sees the
    /// predicate exists and how many clauses it has — rather than
    /// the misleading bare <c>true.</c> they'd get otherwise.
    /// Returns <c>null</c> when there is no precompiled record
    /// either (i.e. the predicate genuinely doesn't exist).</summary>
    internal Shumway.Compiler.Wam.CompiledPredicate? PrecompiledRecordFor(int functorId)
    {
        return _precompiledStaticPredicates.TryGetValue(functorId, out var p)
            ? p : null;
    }

    /// <summary>enumerates the AST clauses backing
    /// <paramref name="functorId"/>. Pulls from every user module's
    /// <c>Clauses</c> list (filtering by head functor) for static
    /// predicates, and from <c>_dynStore[fid]</c> for dynamic
    /// ones. The AST retains the original <see cref="VarTerm.Name"/>
    /// the parser captured — listing prints them as the user wrote
    /// them, without a heap round-trip that would replace them with
    /// synthetic <c>_GN</c> names.</summary>
    internal IEnumerable<Shumway.Compiler.Ast.Clause> ClausesForListing(int functorId)
    {
        foreach (var (name, manifest) in _modules)
        {
            if (IsLibraryModule(name)) continue;
            foreach (var c in manifest.Clauses)
            {
                if (TryExtractHead(c, out string n, out int a))
                {
                    int fid = FunctorTable.Intern(
                        AtomTable.Intern(n, permanent: true).Id, a);
                    if (fid == functorId) yield return c;
                }
            }
        }
        if (_dynStore.TryGetClauses(functorId, out var dyn))
            foreach (var c in dyn) yield return c;
    }

    internal IEnumerable<(int FunctorId, bool IsDynamic)> ListablePredicates()
    {
        var seen = new HashSet<int>();
        foreach (var (name, manifest) in _modules)
        {
            if (IsLibraryModule(name)) continue;
            foreach (var c in manifest.Clauses)
                if (TryExtractHead(c, out string n, out int a))
                {
                    int fid = FunctorTable.Intern(
                        AtomTable.Intern(n, permanent: true).Id, a);
                    if (seen.Add(fid)) yield return (fid, false);
                }
        }
        foreach (var (fid, clauses) in _dynStore.Slots)
            if (clauses.Count > 0 && seen.Add(fid)) yield return (fid, true);
        // source-stripped bundles populate
        // _precompiledStaticPredicates without ever touching
        // manifest.Clauses. Surface those so listing/0 enumerates
        // every user predicate, stripped-source or not. The
        // stripped/in-source-or-not distinction surfaces inside
        // $listing_pred_source which prints a "source stripped"
        // comment when there's no AST to render.
        foreach (var (fid, _) in _precompiledStaticPredicates)
        {
            // Skip prelude / clpfd functors — they get filtered
            // out of normal listing too. We can't tell which
            // module a precompiled fid came from cheaply, so
            // approximate by skipping when the functor's name +
            // arity already appears in either library's
            // PublicFunctors set.
            if (IsLibraryFunctor(fid)) continue;
            if (seen.Add(fid)) yield return (fid, false);
        }
    }

    /// <summary>The libraries the engine itself provides. Their predicates are
    /// not the user's program, so listing skips them the way it skips
    /// builtins.</summary>
    private static readonly string[] LibraryModules =
    {
        Prelude.ModuleName, Clpfd.ModuleName, Clpr.ModuleName, Coroutining.ModuleName,
        Pio.ModuleName,
    };

    internal static bool IsLibraryModule(string moduleName)
        => Array.IndexOf(LibraryModules, moduleName) >= 0;

    /// <summary>true when the functor belongs to one of those libraries.
    ///
    /// <para>The name test is not redundant with the public-functor test: a
    /// library's LOCAL predicates are mangled <c>&lt;module&gt;$&lt;name&gt;</c>
    /// and appear in no PublicFunctors set, so an engine booted from a bundle
    /// with a baked prelude — precompiled records rather than manifest clauses —
    /// would otherwise list every one of them
    /// (<c>$prelude$$member3/3: 2 clauses, source stripped</c>).</para></summary>
    private bool IsLibraryFunctor(int fid)
    {
        foreach (string module in LibraryModules)
            if (_modules.TryGetValue(module, out var m) && m.PublicFunctors.Contains(fid))
                return true;

        var (atomId, _) = FunctorTable.Lookup(fid);
        string name = AtomTable.GetById(atomId)?.Name ?? "";
        foreach (string module in LibraryModules)
            if (name.Length > module.Length
                && name[module.Length] == '$'
                && name.StartsWith(module, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>Snapshot of every static and dynamic functor id across all
    /// loaded modules. Backs the prelude's <c>current_predicate/1</c>
    /// enumeration; the builtin namespace comes from
    /// <see cref="Shumway.Builtins.BuiltinsRegistry.AllRegisteredFunctorIds"/>
    /// separately so the two snapshots can be merged with deduping.</summary>

    internal IEnumerable<int> AllStaticAndDynamicFunctors()
    {
        var seen = new HashSet<int>();
        foreach (int fid in _dynStore.Functors)
            if (!_dynStore.IsImplicitOnly(fid) && seen.Add(fid)) yield return fid;
        foreach (int fid in StaticHeadFunctors())
            if (seen.Add(fid)) yield return fid;
    }

    /// <summary>The set of every static head functor across all modules,
    /// built lazily and cached. Invalidated (<c>_staticHeadFunctorsCache =
    /// null</c>) at every static-clause mutation. Membership is O(1); building
    /// it is one clause scan, amortised across the many
    /// <see cref="HasPredicate"/> / <c>predicate_property/2</c> calls between
    /// consults.</summary>
    private HashSet<int> StaticHeadFunctors()
    {
        if (_staticHeadFunctorsCache is not null) return _staticHeadFunctorsCache;
        var set = new HashSet<int>();
        foreach (var manifest in _modules.Values)
        {
            foreach (var c in manifest.Clauses)
            {
                if (TryExtractHead(c, out string n, out int a))
                    set.Add(FunctorTable.Intern(
                        AtomTable.Intern(n, permanent: true).Id, a));
            }
            // A `:- discontiguous` / `:- multifile` declaration makes the
            // predicate exist even with no clauses of its own.
            set.UnionWith(manifest.DiscontiguousFunctors);
            set.UnionWith(manifest.MultifileFunctors);
        }
        _staticHeadFunctorsCache = set;
        return set;
    }

    /// <summary>Functors a `:- discontiguous` / `:- multifile` directive
    /// declared, host-lifetime. The activation holds this INSTANCE, not a
    /// copy: Logtalk consults its compiled objects from inside a live query,
    /// so a declaration seen mid-query has to take effect immediately.</summary>
    private readonly HashSet<int> _declaredEmptyFids = new();

    internal HashSet<int> DeclaredEmptyFunctors() => _declaredEmptyFids;

    internal void RecordDeclaredEmpty(HashSet<int>? discontiguous, HashSet<int>? multifile)
    {
        if (discontiguous is not null) _declaredEmptyFids.UnionWith(discontiguous);
        if (multifile is not null) _declaredEmptyFids.UnionWith(multifile);
    }

    /// <summary>True iff <paramref name="functorId"/> is the functor of any
    /// loaded predicate — static, dynamic, or builtin. Backs the
    /// ground-mode case of <c>current_predicate/1</c>.</summary>
    internal bool HasPredicate(int functorId)
    {
        if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(functorId, out _))
            return true;
        if (_dynStore.IsDynamic(functorId)) return true;
        return StaticHeadFunctors().Contains(functorId);
    }

    /// <summary>The §7.8 control constructs are callable (call/1 accepts
    /// them) but live in no registry — current_predicate excludes them,
    /// while predicate_property reports them built_in (SWI/SICStus
    /// agree; the Logtalk conformity testers gate on it).</summary>
    private static bool IsControlConstructFid(int functorId)
    {
        var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(functorId);
        string? n = Shumway.Core.AtomTable.GetById(atomId)?.Name;
        return (arity, n) switch
        {
            (2, "," or ";" or "->" or "*->") => true,
            (0, "!") => true,
            _ => false,
        };
    }

    /// <summary>The property atom ids for <paramref name="functorId"/>, as
    /// enumerated by <c>predicate_property/2</c>. An undefined predicate yields
    /// the empty list (so <c>predicate_property/2</c> fails for it). A defined
    /// predicate yields exactly one of <c>built_in</c> / <c>dynamic</c> /
    /// <c>static</c> plus <c>defined</c>. Built-in wins over dynamic wins over
    /// static (a builtin can't be redefined; a user predicate is dynamic if it
    /// was declared/asserted so, else static).</summary>
    internal List<int> PredicatePropertyAtomIds(int functorId)
    {
        var props = new List<int>();
        if (!HasPredicate(functorId) && !IsControlConstructFid(functorId))
            return props;
        int kind;
        if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(functorId, out _)
            || IsControlConstructFid(functorId)
            || _preludeFunctors.Contains(functorId))
            kind = AtomTable.Intern("built_in", permanent: true).Id;
        else if (_dynStore.IsDynamic(functorId))
            kind = AtomTable.Intern("dynamic", permanent: true).Id;
        else
            kind = AtomTable.Intern("static", permanent: true).Id;
        props.Add(kind);
        props.Add(AtomTable.Intern("defined", permanent: true).Id);
        return props;
    }

    private List<Clause> GetOrCreateDynamicSlot(int fid) => _dynStore.Slot(fid);

    /// <summary>retractall/1 modifiability check (SWI / SICStus semantics):
    /// returns <c>true</c> when the predicate is dynamic (so the retract loop
    /// should run), <c>false</c> when it is UNDEFINED (retractall is then a
    /// silent no-op — the predicate is left undefined, no dispatch trampoline is
    /// fabricated), and throws <c>permission_error(modify, static_procedure)</c>
    /// for a static procedure or a builtin (you can't retractall those).</summary>
    /// <summary>The §7.12.2.h ball for modifying a static procedure or
    /// builtin, with the Name/Arity indicator riding in the Value slot so
    /// the translated permission_error carries the culprit.</summary>
    internal static Shumway.Core.PrologRuntimeException StaticProcedureError(int fid)
    {
        var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
        return new Shumway.Core.PrologRuntimeException(
            "permission_error", "modify,static_procedure",
            (object)new CompoundTerm("/", new Term[]
            {
                new AtomTerm(Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "?"),
                new IntTerm(arity),
            }));
    }

    internal bool IsRetractAllModifiable(int fid)
    {
        if (_dynStore.IsDynamic(fid)) return true;
        if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(fid, out _) || HasStaticClauses(fid))
            throw StaticProcedureError(fid);
        return false;   // undefined → retractall is a no-op
    }

    /// <summary>abolish/1 modifiability: dynamic → true; builtin or static →
    /// <c>permission_error(modify, static_procedure, Name/Arity)</c> with the
    /// indicator as culprit; undefined → false (abolish is a silent no-op).</summary>
    internal bool IsAbolishModifiable(int fid)
    {
        if (_dynStore.IsDynamic(fid)) return true;
        if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(fid, out _) || HasStaticClauses(fid))
        {
            var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
            throw new ShumwayPrologException(IsoError.PermissionError(
                "modify", "static_procedure",
                new CompoundTerm("/", new Term[]
                {
                    new AtomTerm(Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "?"),
                    new IntTerm(arity),
                })));
        }
        return false;
    }

    internal void EnsureDynamic(int fid)
    {
        if (_dynStore.IsDynamic(fid))
        {
            // An asserted clause makes it a REAL dynamic: the implicit_dynamic
            // scan's provisional mark (linker-only) is now backed by the
            // database, so it enumerates and its empty chain fails like any
            // declared dynamic.
            _dynStore.ClearImplicitOnly(fid);
            return;
        }

        // implicit_dynamic flag (default true) auto-
        // promotes an undefined predicate on its first assertz/asserta.
        // Matches SWI-Prolog / SICStus / GNU Prolog: in all three, the
        // first assert on a predicate without static clauses creates
        // it as dynamic with no permission_error.
        //
        // Auto-promotion is gated on "the predicate has nowhere else
        // to live" — a registered builtin or a predicate with static
        // clauses still raises permission_error regardless of the
        // flag, matching ISO §7.12.2.h.
        if (_flags.ImplicitDynamic
            && !Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(fid, out _)
            && !HasStaticClauses(fid))
        {
            _dynStore.MarkDynamic(fid);
            if (!_dynStore.HasClauses(fid))
                _dynStore[fid] = new List<Clause>();
            // NO derivation bump. Auto-promotion cannot change any static
            // rewrite: the promoted functor has no static clauses, so it was
            // never module-local, and MangleIfLocal leaves its callers bare
            // both before and after (rewrite contexts read the live
            // _dynStore.Functors set anyway). A derivation bump here cleared
            // the whole-program transform + compiled-predicate caches per
            // promotion — during a hook-heavy library load that recompiled
            // every predicate dozens of times. The assert that triggered the
            // promotion bumps _programStamp (DropDynamicPredicateCacheEntry),
            // which is exactly the invalidation the compiled-program product
            // needs to re-link with the new trampoline.
            _programStamp++;
            return;
        }

        // ISO §7.12.2.h — modifying a static procedure
        // is permission_error(modify, static_procedure, Name/Arity).
        // The Detail string carries the indicator for diagnostic
        // continuity; the translated form lands in the second slot
        // of the permission_error compound through the standard
        // TranslateRuntimeError path (the third "Obj" slot stays as
        // an anonymous variable for now — a richer Term-valued
        // exception payload is queued for later).
        // Detail encodes Operation,ObjectType — TranslateRuntimeError
        // splits and builds the three-arg permission_error compound
        // (the third Obj slot stays as an anonymous variable, since
        // PrologRuntimeException can't carry a Term yet).
        throw StaticProcedureError(fid);
    }

    /// <summary>emits a fresh empty-dynamic trampoline for
    /// <paramref name="fid"/> directly into the engine's live program
    /// buffer mid-query and registers it in
    /// <see cref="Activation.CurrentFunctorAddresses"/>. Called by the
    /// asserta/assertz incremental paths when the
    /// <c>implicit_dynamic</c> flag auto-promoted the predicate AFTER
    /// <see cref="SetupQueryFromTerm"/> ran (so the trampoline that
    /// SetupQueryFromTerm normally builds for every declared dynamic
    /// was never built for this one).
    ///
    /// <para>Replicates the single-clause-dynamic shape
    /// <see cref="Shumway.Compiler.Wam.PredicateCompiler"/> emits for
    /// the <c>head(_) :- fail.</c> stubs <c>EmitEmptyDynamicStubs</c>
    /// injects at query setup: <c>enter_dynamic; execute &lt;chain-
    /// head=6&gt;; try_me_else &lt;fail-stub-addr&gt; arity;
    /// check_visible 0 MAX; CallBuiltin fail/0; Proceed</c>. After
    /// appending the bytecode to <see cref="Activation.CurrentProgram"/>
    /// via <see cref="Activation.AppendCode"/>, the helper:</para>
    /// <list type="bullet">
    /// <item>Patches the body's <c>CallBuiltin</c> call sites (the
    ///   fail stub uses a fixed builtin id, so there's nothing to
    ///   resolve — kept for symmetry with the append
    ///   path).</item>
    /// <item>Adds <paramref name="fid"/> to the engine's address map
    ///   (the underlying dictionary, cast through the
    ///   <see cref="IReadOnlyDictionary{TKey,TValue}"/> facade).</item>
    /// <item>Calls <see cref="PopulateDynChainFor"/> so
    ///   <see cref="_dynChains"/> picks up the trampoline's
    ///   <c>TailNextAddr</c> / <c>HeadClauseAddr</c> /
    ///   <c>TrampolineExecuteOperandAddr</c>, making subsequent
    ///   / in-place assertz / asserta
    ///   extensions work just like declared-dynamic
    ///   predicates.</item>
    /// </list></summary>
    internal void MaterializeDynamicTrampoline(Activation engine, int fid)
    {
        // Root fix: never append at a stale owner position (see
        // ResyncOwnerAppendPosition).
        ResyncOwnerAppendPosition(engine);
        // capture buffer ownership before AppendCode (growth
        // reallocation changes the engine's reference).
        bool ownsHost = EngineOwnsHostBuffer(engine);
        var (atomId, arity) = FunctorTable.Lookup(fid);
        string name = AtomTable.GetById(atomId)?.Name
            ?? throw new InvalidOperationException(
                $"Cannot auto-promote: atom for fid {fid} has no name.");

        // Synthesise `head(_, _, ..., _) :- fail.` — the same shape
        // EmitEmptyDynamicStubs produces at query setup.
        Term head = arity == 0
            ? (Term)new AtomTerm(name)
            : new CompoundTerm(name,
                Enumerable.Range(0, arity).Select(_ => (Term)new VarTerm("_")).ToArray());
        Term stubTerm = new CompoundTerm(":-", new[] { head, (Term)new AtomTerm("fail") });
        var stubClause = new Clause(ClauseKind.Rule, stubTerm,
            Shumway.Compiler.Lexer.SourcePosition.Start);

        // Compile-pipeline parity with the setup path: same transforms
        // (DCG / Meta / Phrase / mode-spec), same ModuleRewrite, same
        // PredicateCompiler with isDynamic=true so the result IS a
        // trampoline.
        var transformed = ClausePipeline.Apply(new[] { stubClause }, Modes, helperPrefix: "$q");
        var dynCtx = new ModuleRewrite.Context(
            DefaultModuleName, new HashSet<int>(), _dynStore.Functors);
        var rewritten = transformed.Select(c => ModuleRewrite.Rewrite(c, dynCtx)).ToList();

        var predicate = new Shumway.Compiler.Wam.PredicateCompiler
            {
                EmitDebugInfo = _flags.EmitDebugInfo,
                DebugCodegen = _flags.DebugCodegen,
                DebugFileId = _debugFileId,
            }.Compile(
            rewritten,
            _literalPools.Strings, _literalPools.Floats, _literalPools.BigInts,
            enableIndexing: false,
            isDynamic: true,
            failStubAddr: engine.DynamicFailStubAddr);

        int trampolineAddr = engine.AppendCode(predicate.Bytecode);
        // ADR-041 — mid-query trampolines are invisible to the per-query
        // PredicatesByAddress map; register the address→fid here so the
        // dispatch-time clause selector covers live-linked chains too.
        ChainPatcher.GetOrCreateChainTable(engine).TrampolineFids[trampolineAddr] = fid;

        // PredicateCompiler emits the trampoline's execute opcode with
        // a PREDICATE-LOCAL target (6); the module-compile path patches
        // that operand to an absolute address during link. The mid-
        // query materialise bypasses ModuleCompiler so we do the same
        // relocation here — every DispatchSite address operand needs
        // its predicate-local value shifted by trampolineAddr.
        foreach (int siteRel in predicate.DispatchSites)
        {
            int operandAbsPos = trampolineAddr + siteRel;
            int predLocalTarget = Shumway.Core.BytecodeIO.ReadInt32(
                engine.CurrentProgram!, operandAbsPos);
            Shumway.Core.BytecodeIO.WriteInt32(
                engine.CurrentProgram!, operandAbsPos,
                trampolineAddr + predLocalTarget);
        }

        // Patch any call-site operands the predicate emitted (the fail
        // body has none, but the path mirrors the append for
        // symmetry / future-proofing against richer auto-promote stubs).
        var addrMap = engine.CurrentFunctorAddresses;
        foreach (var site in predicate.CallSites)
        {
            int operandPos = trampolineAddr + site.OpcodeOffset + 1;
            int target = addrMap is not null
                         && addrMap.TryGetValue(site.CalleeFunctorId, out int siteAddr)
                ? siteAddr
                : Shumway.Core.CallTarget.ForUndefined(site.CalleeFunctorId);
            Shumway.Core.BytecodeIO.WriteInt32(engine.CurrentProgram!, operandPos, target);
        }

        // Register the trampoline in the address map. SetupQueryFromTerm
        // always assigns a LayeredIntMap (per-query overlay over the shared
        // persistent base); the write lands in this activation's overlay.
        if (addrMap is not Shumway.Core.LayeredIntMap<int> mutableMap)
            throw new InvalidOperationException(
                "implicit_dynamic mid-query trampoline materialise: "
                + "CurrentFunctorAddresses is not a mutable Dictionary — "
                + "no auto-promote path available.");
        mutableMap[fid] = trampolineAddr;

        // Populate _dynChains[fid] manually. We can't use
        // PopulateDynChainFor here because it would associate the
        // stub-clause's check_visible with whichever user clause is
        // already in _dynStore.Slots (host.Assertz appends to
        // _dynamicClauses before calling AppendDynamicClauseIncremental,
        // so by the time MaterializeDynamicTrampoline runs the list
        // already holds the asserted clause). The mismatch would
        // make retract patch the stub's died slot instead of the
        // real clause's, leaving the latter visible.
        //
        // Manual layout: only the trampoline-level offsets matter for
        // incremental append — the chunk emits the real
        // clause's own retry_me_else + check_visible + body and adds
        // a fresh DynChainEntry to chain.Entries pointing at the
        // emitted chunk. So _dynChains[fid].Entries stays empty here.
        var chain = new DynChainState
        {
            TrampolineExecuteOperandAddr = trampolineAddr + 2,
            HeadClauseAddr = trampolineAddr + 6,
            TailNextAddr = trampolineAddr + 7,
        };
        // record into THIS engine's chain table: the
        // trampoline lives in this engine's buffer.
        GetOrCreateChainTable(engine).Chains[fid] = chain;

        // Sync the host's persistent program reference — engine.AppendCode
        // may have reallocated the buffer (owner engine); a non-owner
        // engine's trampoline exists only in its own buffer, so force the
        // next setup to rebuild from _dynStore.Functors/_dynamicClauses.
        SyncOrInvalidateAfterMutation(engine, ownsHost);
    }

    /// <summary>
    /// live-links a batch of newly consulted
    /// STATIC predicates into the running query's code space so a later
    /// goal in the SAME query can reach them. The static counterpart of
    /// <see cref="AppendDynamicClauseIncremental"/>: a <c>consult/1</c>
    /// issued from inside a live query (Logtalk's
    /// <c>'$lgt_load_prolog_code'</c> during <c>'$lgt_runtime_initialization'</c>)
    /// must make its predicates callable immediately, not only at the next
    /// top-level query.
    ///
    /// <para>Crucially this reuses the SAME compilation pipeline as
    /// <see cref="SetupQueryFromTerm"/>'s static branch —
    /// <c>MetaWrapperUnfold → ClausePipeline → ModuleRewrite →
    /// ModuleCompiler → Linker</c> — so there is ONE compilation scheme.
    /// The persistent program is always rebuilt statically at the next
    /// setup; this only appends a transient live-linked copy for the
    /// current query. No second code path.</para>
    ///
    /// <para>The batch links at the live program's end (inside the reserved
    /// persistent→query address gap), resolving external calls against the
    /// live address map. New addresses — plus bare-name aliases, so runtime
    /// meta-calls (<c>call/N</c> resolving through
    /// <see cref="Activation.CurrentFunctorAddresses"/>) reach them — merge into
    /// the map; switch tables append to <see cref="Activation.SwitchTables"/>;
    /// forward references to predicates a later batch defines are re-patched
    /// as those addresses become known.</para>
    /// </summary>
    internal void LinkConsultedStaticPredicatesLive(
        Activation engine, IReadOnlyList<Clause> newStaticClauses, string moduleName)
    {
        bool diagLive = Environment.GetEnvironmentVariable("SHUMWAY_UNDEF_DIAG") == "1";
        if (newStaticClauses.Count == 0) return;
        // Only meaningful with a live query in flight: a program buffer, a
        // mutable address map, and a mutable switch-table list. Absent any,
        // the predicates link normally at the next setup.
        if (engine.CurrentProgram is null
            || engine.CurrentFunctorAddresses is not Shumway.Core.LayeredIntMap<int> addrMap
            || engine.SwitchTables is not { } switchTables)
        {
            if (diagLive) Console.Error.WriteLine(
                $"[LIVE-LINK] skip(no-live-query) mod={moduleName} n={newStaticClauses.Count}"
                + $" prog={(engine.CurrentProgram is null ? "null" : "ok")}"
                + $" map={engine.CurrentFunctorAddresses?.GetType().Name ?? "null"}"
                + $" swt={(engine.SwitchTables is null ? "null" : "ok")}");
            return;
        }
        if (!_modules.TryGetValue(moduleName, out var manifest))
        {
            if (diagLive) Console.Error.WriteLine(
                $"[LIVE-LINK] skip(no-manifest) mod={moduleName} n={newStaticClauses.Count}");
            return;
        }
        // capture buffer ownership before AppendCode below.
        bool ownsHost = EngineOwnsHostBuffer(engine);

        // --- SAME transform pipeline as the setup static branch --------
        // ADR-035 — keep the wrapper a real predicate for a debuggable module
        // (see the setup branch: the unfold would erase its stop sites and
        // scatter anonymous control frames over the caller).
        bool opaqueModule = _nonDebuggableModules.Contains(moduleName);
        var unfolded = (_flags.DebugCodegen && !opaqueModule)
            ? newStaticClauses
            : MetaWrapperUnfold.Apply(newStaticClauses);
        var transformed = ClausePipeline.Apply(
            unfolded, Modes, inlineIte: EnableInlineIte,
            helperIdProvider: NextMetaHelperId, dcgFailFast: !_flags.DebugCodegen);
        // Body-call mangling locals: the batch's own head functors. For a
        // self-contained consulted file (a Logtalk entity's scratch code)
        // these are exactly the predicates it defines; cross-batch calls
        // resolve through the bare-name aliases merged below.
        var locals = ComputeLocalFunctors(transformed, manifest.PublicFunctors);
        if (_precompiledModuleLocals.TryGetValue(moduleName, out var bundleLocals))
            locals.UnionWith(bundleLocals);
        var ctx = new ModuleRewrite.Context(moduleName, locals, _dynStore.Functors, manifest.Imports);
        var rewritten = new List<Clause>(transformed.Count);
        foreach (var c in transformed)
            rewritten.Add(ModuleRewrite.Rewrite(c, ctx));

        // --- SAME ModuleCompiler + Linker as setup ---------------------
        int failStubAddr =
            OpcodeTable.Get(Opcode.Call).Size + OpcodeTable.Get(Opcode.Halt).Size;
        int loadOffset = engine.ProgramLength;
        Shumway.Compiler.Wam.Linker.LinkResult link;
        try
        {
            var module = new Shumway.Compiler.Wam.ModuleCompiler
                {
                    EmitDebugInfo = _flags.EmitDebugInfo,
                    DebugCodegen = _flags.DebugCodegen,               // ADR-035
                    DebugFileId = _debugFileId,                      // ADR-035
                    NonDebuggableFunctors = _nonDebuggableFunctors,  // ADR-035
                    ElideRedundantCuts = _flags.ElideRedundantCuts,   // ADR-030
                }.Compile(
                rewritten, cache: null, unindexedFunctors: null,
                _literalPools, dynamicFunctors: _dynStore.Functors,
                failStubAddr: failStubAddr);
            link = new Shumway.Compiler.Wam.Linker().Link(
                module.Predicates, loadOffset,
                externalSymbols: addrMap,
                switchTableIdBase: switchTables.Count);
            RegisterLateHelpers(module.Predicates);   // cross-activation visibility
        }
        catch (InvalidOperationException ex)
        {
            // A structural issue (e.g. a duplicate head across the batch):
            // never fail the consult — the next top-level setup links the
            // whole module coherently.
            if (diagLive) Console.Error.WriteLine(
                $"[LIVE-LINK] skip(compile-throw) mod={moduleName}: {ex.Message}");
            return;
        }

        engine.AppendCode(link.Bytecode);
        byte[] prog = engine.CurrentProgram!;

        // Merge new addresses + bare-name aliases so both direct calls in
        // later batches and runtime meta-calls resolve the new predicates.
        var visible = engine.LiveConsultVisibleFids ??= new HashSet<int>();
        foreach (var (fid, a) in link.Addresses)
        {
            // A RELOAD redefines a predicate an earlier batch already linked
            // — and earlier batches may have BAKED the old entry address into
            // their call sites (the forward-reference re-patch below resolves
            // sites to a concrete address, not through the map). Redirect the
            // old entry with `execute <new>` so stale baked sites flow to the
            // redefinition — same trick as RebuildEngineFidChainView; every
            // predicate entry is ≥ 5 bytes, and only fresh calls read the
            // entry (a CP's bp points at a later clause, never the entry).
            if (addrMap.TryGetValue(fid, out int oldAddr)
                && oldAddr != a
                && oldAddr >= 0
                && !Shumway.Core.CallTarget.IsUnresolved(oldAddr)
                && !Activation.IsResumeMarker(oldAddr)
                && oldAddr + 5 <= prog.Length)
            {
                prog[oldAddr] = (byte)Opcode.Execute;
                Shumway.Core.BytecodeIO.WriteInt32(prog, oldAddr + 1, a);
            }
            addrMap[fid] = a;
            visible.Add(fid);
        }
        // Bare-name aliases feed both the address map (so a bare call
        // resolves) and the visibility set (so a setup-time-baked sentinel
        // for the bare fid resolves to this static address).
        AddBareLocalAliases(addrMap, link.Addresses, recordAdded: visible);
        switchTables.AddRange(link.SwitchTables);

        // Forward references: record this batch's unresolved sites (baked
        // with the undefined sentinel), then re-patch every accumulated
        // site whose callee is now linked. Absolute operand position =
        // loadOffset + Offset + 1 (skip the Call opcode byte).
        // The list lives on the per-buffer chain table — its positions are
        // offsets into THIS engine's buffer.
        var unresolved = GetOrCreateChainTable(engine).LiveConsultUnresolved
            ??= new List<(int, int)>();
        foreach (var (off, fid) in link.UnresolvedSites)
            unresolved.Add((loadOffset + off + 1, fid));
        for (int i = unresolved.Count - 1; i >= 0; i--)
        {
            var (absPos, fid) = unresolved[i];
            if (addrMap.TryGetValue(fid, out int resolvedAddr))
            {
                Shumway.Core.BytecodeIO.WriteInt32(prog, absPos, resolvedAddr);
                unresolved.RemoveAt(i);
            }
        }

        // Sync PrologEngine's cached persistent reference (AppendCode may
        // have reallocated; owner engine only — a mid-query consult has
        // typically already invalidated the host buffer, and a non-owner
        // engine's live-link must not clobber a newer buffer reference),
        // push any newly interned literals to the interpreter, and bump
        // the program generation so the dispatch loop refreshes its
        // cached view over the grown buffer.
        SyncOrInvalidateAfterMutation(engine, ownsHost);
        RefreshLiteralPoolsIfGrown(engine);
        engine.BumpProgramGeneration();
    }

    /// <summary>
    /// ensures every dynamic functor that a
    /// mid-query consult declared (<c>:- dynamic</c> / <c>:- multifile</c>)
    /// has a live dynamic trampoline in the running query, materialising an
    /// empty one for any that lacks it and linking its already-routed
    /// clauses. Without this a hook predicate declared then called before
    /// any clause is added (Logtalk's <c>message_hook/4</c>) raises
    /// <c>existence_error</c> instead of failing. A trampoline starts with
    /// <c>enter_dynamic</c>, which the dispatcher's
    /// <c>ResolveTargetMaybeAutoPromoted</c> already resolves through — so
    /// no separate visibility set is needed for dynamics.
    /// </summary>
    internal void EnsureLiveDynamicTrampolines(Activation engine)
    {
        if (engine.CurrentFunctorAddresses is not Shumway.Core.LayeredIntMap<int> addrMap)
            return;
        if (engine.CurrentProgram is null) return;

        // A trampoline the setup (or an earlier mid-query materialise) built
        // starts with enter_dynamic at its address. Anything else — an
        // unresolved sentinel, or no entry at all — means this dynamic
        // functor has no live home yet.
        List<int>? materialized = null;
        foreach (int fid in _dynStore.Functors)
        {
            bool hasTrampoline =
                addrMap.TryGetValue(fid, out int addr)
                && !Shumway.Core.CallTarget.IsUnresolved(addr)
                && addr >= 0 && addr < engine.ProgramLength
                && (Opcode)engine.CurrentProgram![addr] == Opcode.EnterDynamic;
            if (hasTrampoline) continue;
            MaterializeDynamicTrampoline(engine, fid);
            (materialized ??= new List<int>()).Add(fid);
        }

        // The freshly-materialised trampolines are empty (a fail stub). Any
        // clauses this consult routed into their _dynamicClauses slot
        // no-op'd through AppendDynamicClauseIncremental (no trampoline then)
        // — link them now that a trampoline exists. Core (non-broadcast)
        // on purpose: these clauses already exist in the store, so another
        // live engine either baked them at its setup or received them via
        // the broadcast when they were asserted — re-broadcasting here
        // would duplicate them there.
        if (materialized is not null)
            foreach (int fid in materialized)
                if (_dynStore.TryGetClauses(fid, out var cs))
                    foreach (var c in cs)
                        AppendDynamicClauseIncrementalCore(engine, fid, c);
    }

    /// <summary>Applies a <c>:- set_prolog_flag(Flag, Value)</c>
    /// directive at consult time so subsequent clauses in the same
    /// consult see the new value. The parser already pre-processes
    /// the parser-visible flags (e.g. <c>double_quotes</c>); this
    /// handles the rest of the recognised set. Unknown flags are
    /// silently ignored at consult time — the runtime builtin
    /// surfaces the diagnostic.</summary>
    internal void ApplyConsultSetPrologFlag(string flagName, string valueName)
    {
        switch (flagName)
        {
            case "implicit_dynamic":
                if (valueName == "true") _flags.ImplicitDynamic = true;
                else if (valueName == "false") _flags.ImplicitDynamic = false;
                break;
            case "prefer_rationals":
                if (valueName == "true") _flags.PreferRationals = true;
                else if (valueName == "false") _flags.PreferRationals = false;
                break;
            case "arity_compat":
                // consult-time directive form. The ClauseReader's
                // pre-pass already flipped the live lexer for THIS file; this
                // records it for subsequent consults. Arity call semantics
                // ride along: undefined predicates FAIL (a later explicit
                // set_prolog_flag(unknown, _) overrides).
                if (valueName == "true") { _flags.ArityCompat = true; _flags.Unknown = "fail"; }
                else if (valueName == "false") _flags.ArityCompat = false;
                break;
            case "unknown":
                if (valueName == "error" || valueName == "fail" || valueName == "warning")
                    _flags.Unknown = valueName;
                break;
            case "occurs_check":
                if (valueName == "false" || valueName == "true" || valueName == "error")
                    _flags.OccursCheck = valueName;
                break;
            case "compile_mode":
                // Takes effect for predicates compiled later in this consult.
                if (valueName == "debug") _flags.EmitDebugInfo = _flags.DebugCodegen = true;
                else if (valueName == "release") _flags.EmitDebugInfo = _flags.DebugCodegen = false;
                break;
            // double_quotes is handled by ClauseReader's directive
            // pre-pass (it has to take effect during lexing of the
            // subsequent tokens, before consult-time directive
            // processing even sees it).
        }
    }

    /// <summary>walks every clause's body looking for
    /// <c>assertz(Head)</c>, <c>asserta(Head)</c>, or <c>assert(Head)</c>
    /// with a literal-callable Head (an atom or a compound), and
    /// auto-declares the corresponding functor as dynamic when it has
    /// no static clauses and isn't already a registered builtin. This
    /// runs at consult time so the next query setup links the
    /// predicate with a real dynamic trampoline; a first-time assertz
    /// at runtime then has somewhere to put the new clause and
    /// subsequent calls dispatch to it.</summary>
    internal void CollectImplicitDynamics(IEnumerable<Clause> clauses, HashSet<int> publicsInSameConsult)
    {
        var seen = new HashSet<int>();
        foreach (var c in clauses)
        {
            Term body = c.Kind == ClauseKind.Rule
                ? ((CompoundTerm)c.Term).Args[1]
                : null!;
            if (body is null) continue;
            ScanForAssertHeads(body, seen);
        }
        foreach (int fid in seen)
        {
            if (_dynStore.IsDynamic(fid)) continue;
            if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(fid, out _)) continue;
            if (HasStaticClauses(fid)) continue;
            // Also skip if the same consult is about to define this
            // functor as a public (static) predicate — caught by
            // looking at publics + the about-to-be-added clauses.
            if (publicsInSameConsult.Contains(fid)) continue;
            if (ClausesDefineFunctor(clauses, fid)) continue;
            _dynStore.MarkDynamic(fid);
            // …but only the LINKER needs to know yet. The predicate is not in
            // the database until something declares or asserts it, so it stays
            // out of current_predicate/1 and calling it goes through the
            // `unknown` flag (§8.8.2.1; GNU, SWI and Scryer all agree).
            _dynStore.ImplicitOnly.Add(fid);
            if (!_dynStore.HasClauses(fid))
                _dynStore[fid] = new List<Clause>();
        }
    }

    private static void ScanForAssertHeads(Term goal, HashSet<int> sink)
    {
        if (goal is CompoundTerm c)
        {
            if ((c.Functor == "assertz" || c.Functor == "asserta" || c.Functor == "assert")
                && c.Args.Length == 1)
            {
                Term arg = c.Args[0];
                // The clause form (Head :- Body) — peel to Head.
                if (arg is CompoundTerm cl && cl.Functor == ":-" && cl.Args.Length == 2)
                    arg = cl.Args[0];
                if (arg is AtomTerm a)
                    sink.Add(FunctorTable.Intern(
                        AtomTable.Intern(a.Name, permanent: true).Id, 0));
                else if (arg is CompoundTerm cc)
                    sink.Add(FunctorTable.Intern(
                        AtomTable.Intern(cc.Functor, permanent: true).Id, cc.Args.Length));
                return;
            }
            // Recurse into control constructs and any compound — a
            // nested `( ... ; assertz(p) ; ... )` should still register p.
            foreach (var sub in c.Args) ScanForAssertHeads(sub, sink);
        }
    }

    private static bool ClausesDefineFunctor(IEnumerable<Clause> clauses, int fid)
    {
        foreach (var c in clauses)
        {
            if (TryExtractHead(c, out string n, out int a))
            {
                int cfid = FunctorTable.Intern(
                    AtomTable.Intern(n, permanent: true).Id, a);
                if (cfid == fid) return true;
            }
        }
        return false;
    }

    private bool HasStaticClauses(int fid)
    {
        foreach (var manifest in _modules.Values)
        {
            foreach (var c in manifest.Clauses)
            {
                if (TryExtractHead(c, out string n, out int a))
                {
                    int cfid = FunctorTable.Intern(
                        AtomTable.Intern(n, permanent: true).Id, a);
                    if (cfid == fid) return true;
                }
            }
        }
        return false;
    }

    private static int ExtractHeadFunctorId(Clause clause)
    {
        Term head = clause.Kind == ClauseKind.Rule
            ? ((CompoundTerm)clause.Term).Args[0]
            : clause.Term;
        return head switch
        {
            // read-through the AST node's cached ids (seeded by
            // TermReader on the assert path) — drops a string-keyed table
            // probe per assert. The lazy intern is transient; ClauseCompiler
            // promotes asserted predicate names permanent when it compiles
            // the clause, and promotion preserves the id.
            AtomTerm a => FunctorTable.Intern(a.ResolveAtomId(), 0),
            CompoundTerm c => c.ResolveFunctorId(),
            // ISO assertz/asserta/retract — a clause head
            // that isn't callable raises type_error(callable, Head);
            // an unbound head raises instantiation_error.
            VarTerm => throw new Shumway.Core.PrologRuntimeException("instantiation_error"),
            _ => throw new Shumway.Core.PrologRuntimeException("type_error", "callable"),
        };
    }

    private static bool TermsStructurallyEqual(Term a, Term b)
    {
        return (a, b) switch
        {
            (AtomTerm ax, AtomTerm bx) => ax.Name == bx.Name,
            (IntTerm ax, IntTerm bx) => ax.Value == bx.Value,
            (BigIntTerm ax, BigIntTerm bx) => ax.Value == bx.Value,
            (FloatTerm ax, FloatTerm bx) => ax.Value == bx.Value,
            (StringTerm ax, StringTerm bx) => ax.Content == bx.Content,
            (VarTerm ax, VarTerm bx) => ax.Name == bx.Name,
            (CompoundTerm ax, CompoundTerm bx) when ax.Functor == bx.Functor
                && ax.Args.Length == bx.Args.Length
                => Enumerable.Range(0, ax.Args.Length)
                    .All(i => TermsStructurallyEqual(ax.Args[i], bx.Args[i])),
            _ => false,
        };
    }

    /// <summary>single-clause transform + compile for the four
    /// runtime <c>assertz</c> / <c>asserta</c> compile sites (chain and
    /// extensible-indexed, append and prepend). Facts take a fast path that
    /// bypasses the ClausePipeline and ModuleRewrite entirely — each pass is
    /// a verified structural no-op for a fact: DcgTransform rewrites only
    /// <c>DcgRule</c> clauses, MetaTransform and PhraseTransform rewrite
    /// only <c>Rule</c> bodies, and ModuleRewrite's dynamic context carries
    /// an empty local-functor set so a fact head never mangles. The one
    /// pass that CAN touch a fact is mode specialization (<c>H.</c> →
    /// <c>H :- !.</c> when every declared mode is deterministic), so the
    /// fast path is gated on <c>!Modes.AllModesDeterministic</c>. Returns
    /// <c>null</c> when the transform produced nothing (the pre-427
    /// <c>rewritten.Count == 0</c> guard).</summary>
    internal Shumway.Compiler.Wam.CompiledClause? CompileRuntimeAssertClause(
        Activation engine, int functorId, Clause newClause)
    {
        Clause toCompile;
        if (newClause.Kind == Shumway.Compiler.Ast.ClauseKind.Fact
            && !Modes.AllModesDeterministic(functorId))
        {
            toCompile = newClause;
        }
        else
        {
            // Apply the same transforms the setup path runs — dynamic
            // clauses share a flat module rewrite context. The first
            // transformed clause is the asserted one; any MetaTransform
            // helper clauses (a body catch/3 → '$catchgoal_N'/'$catchrec_N',
            // a nested control construct → '$disj_N', ...) follow it.
            var transformed = ClausePipeline.Apply(new[] { newClause }, Modes, helperIdProvider: NextMetaHelperId, dcgFailFast: !_flags.DebugCodegen);
            if (transformed.Count == 0) return null;
            _assertDynCtx ??= new ModuleRewrite.Context(
                DefaultModuleName, new HashSet<int>(), _dynStore.Functors);
            toCompile = ModuleRewrite.Rewrite(transformed[0], _assertDynCtx);
            // the helpers used to be DROPPED here, leaving the
            // asserted clause's body calling a '$catchgoal_N' that nothing
            // defines until the next query setup regenerates it from the
            // store — an existence_error when the clause runs in the SAME
            // query it was asserted in (Logtalk's hooked test-file aux
            // registration). Link each helper into the live engine as a
            // single-clause static predicate before the caller patches the
            // asserted clause's call sites against the address map.
            if (transformed.Count > 1)
                LinkRuntimeAssertHelpers(engine, transformed);
        }
        _assertClauseCompiler ??= new Shumway.Compiler.Wam.ClauseCompiler();
        return _assertClauseCompiler.Compile(
            toCompile,
            _literalPools.Strings, _literalPools.Floats, _literalPools.BigInts);
    }

    /// <summary>compiles and live-links the MetaTransform helper
    /// clauses generated while incrementally compiling a runtime-asserted
    /// clause (<paramref name="transformed"/>[1..]). Helpers are grouped by
    /// head functor and each group compiled as ONE multi-clause static
    /// predicate via <see cref="Shumway.Compiler.Wam.PredicateCompiler"/> —
    /// an if-then-else '$disj_N' has TWO clauses (the guarded then-branch
    /// and the else-branch) and per-clause compilation registered only the
    /// LAST one, so `(C -&gt; T ; E)` in a runtime-asserted clause ran the
    /// else unconditionally (Logtalk's type::check "callable(X) failing on
    /// an atom" mystery). Addresses are registered first, call sites
    /// patched in a second pass (a helper may call a later helper from the
    /// same transform). Helpers are deliberately NOT added to the store —
    /// the next query setup regenerates them from the original clause,
    /// exactly like the setup path always has.</summary>
    private void LinkRuntimeAssertHelpers(
        Activation engine, IReadOnlyList<Clause> transformed)
    {
        if (engine.CurrentProgram is null
            || engine.CurrentFunctorAddresses is not Shumway.Core.LayeredIntMap<int> addrMap)
            return;
        // Group by head fid, preserving clause order within each group.
        Dictionary<int, List<Clause>>? groups = null;
        List<int>? order = null;
        for (int i = 1; i < transformed.Count; i++)
        {
            var rewritten = ModuleRewrite.Rewrite(transformed[i], _assertDynCtx!);
            int fid = HeadFunctorIdOf(rewritten);
            groups ??= new Dictionary<int, List<Clause>>();
            order ??= new List<int>();
            if (!groups.TryGetValue(fid, out var list))
            {
                groups[fid] = list = new List<Clause>();
                order.Add(fid);
            }
            list.Add(rewritten);
        }
        if (groups is null) return;
        var linked = new List<(Shumway.Compiler.Wam.CompiledPredicate Pred, int Addr)>();
        foreach (int fid in order!)
        {
            var pred = new Shumway.Compiler.Wam.PredicateCompiler
                {
                EmitDebugInfo = _flags.EmitDebugInfo,
                DebugCodegen = _flags.DebugCodegen,
                DebugFileId = _debugFileId,
            }.Compile(
                groups[fid],
                _literalPools.Strings, _literalPools.Floats, _literalPools.BigInts,
                enableIndexing: false,
                isDynamic: false);
            int addr = engine.AppendCode(pred.Bytecode);
            // Relocate predicate-local dispatch targets (try_me_else /
            // retry_me_else chain operands) to absolute addresses — the
            // mid-query append bypasses ModuleCompiler's link step, same
            // as MaterializeDynamicTrampoline.
            foreach (int siteRel in pred.DispatchSites)
            {
                int operandAbsPos = addr + siteRel;
                int predLocalTarget = Shumway.Core.BytecodeIO.ReadInt32(
                    engine.CurrentProgram!, operandAbsPos);
                Shumway.Core.BytecodeIO.WriteInt32(
                    engine.CurrentProgram!, operandAbsPos, addr + predLocalTarget);
            }
            addrMap[fid] = addr;
            linked.Add((pred, addr));
            // The compiled helper is kept host-side so ANOTHER live activation
            // can materialize it on demand (see TryMaterializeAssertHelper):
            // the asserted CLAUSE is visible to every activation through the
            // shared ADR-015 chains, so its helper must be reachable from
            // every activation too — this map + only this activation's map
            // was the Logtalk-under-promotion existence_error. (Registered via
            // RegisterLateHelpers so the bare alias is covered too.)
            RegisterLateHelpers(new[] { pred });
        }
        var program = engine.CurrentProgram!;
        foreach (var (pred, addr) in linked)
            foreach (var site in pred.CallSites)
            {
                int operandPos = addr + site.OpcodeOffset + 1;
                int target = addrMap.TryGetValue(site.CalleeFunctorId, out int a)
                    ? a
                    : Shumway.Core.CallTarget.ForUndefined(site.CalleeFunctorId);
                Shumway.Core.BytecodeIO.WriteInt32(program, operandPos, target);
            }
    }

    /// <summary>MetaTransform helper predicates by head fid — compiled at assert
    /// time (<see cref="LinkRuntimeAssertHelpers"/>) or at a query setup's module
    /// compile — kept so any LIVE activation can link one on demand
    /// (<see cref="TryMaterializeAssertHelper"/>). Grows monotonically (helper ids
    /// are never reused); each entry is a small one-or-two-clause predicate.</summary>
    private readonly Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>
        _runtimeAssertHelperPreds = new();

    /// <summary>Whether a functor names a MetaTransform helper
    /// (<c>[mod$]$disj_N</c> family) — the registry filter for
    /// <see cref="RegisterLateHelpers"/>.</summary>
    private static bool IsMetaHelperFunctorName(int fid)
    {
        var (atomId, _) = Shumway.Core.FunctorTable.Lookup(fid);
        string? name = Shumway.Core.AtomTable.GetById(atomId)?.Name;
        if (name is null) return false;
        // MetaTransform helper names are '{prefix}${kind}_{id}' — after any
        // module mangling, the segment following the LAST '$' is
        // '<letters>_<digits>' (disj_12, catchrec_7, bagof_1739, …). Match the
        // shape rather than an enumerated kind list, so a new helper kind can
        // never silently fall outside the late-materialization registry.
        int last = name.LastIndexOf('$');
        if (last < 0 || last + 2 >= name.Length) return false;
        // The helper's own name STARTS with '$' — bare it is '$disj_12',
        // module-mangled 'mod$$disj_12' (a DOUBLE dollar). A single-dollar
        // name is a mangled USER local, and a user predicate named like
        // 'mod$test_326' fits the letters_digits shape by accident: register
        // it (under its bare name, first-compile-wins) and the module wall
        // leaks — a bare test_326 resolved cross-module, preempting both the
        // existence_error and the consult-direct fallback's ambiguity check.
        if (last > 0 && name[last - 1] != '$') return false;
        // NEVER register the query-stub's own '$q…' helpers: their ids are
        // deliberately REUSED query-to-query (MetaTransform.HelperPrefix "$q"),
        // so a first-compile-wins registry would materialize a PREVIOUS query's
        // body under the next query's name — silent wrong execution.
        if (name[last + 1] == 'q') return false;
        int i = last + 1;
        int letters = 0;
        while (i < name.Length && name[i] >= 'a' && name[i] <= 'z') { i++; letters++; }
        if (letters == 0 || i >= name.Length || name[i] != '_') return false;
        i++;
        int digits = 0;
        while (i < name.Length && name[i] >= '0' && name[i] <= '9') { i++; digits++; }
        return digits > 0 && i == name.Length;
    }

    /// <summary>Registers every helper-shaped compiled predicate for on-demand
    /// cross-activation materialization. First compile wins (ids are minted once,
    /// so a fid's bytecode never legitimately changes). Each helper is ALSO
    /// registered under its BARE (unmangled) functor: goal-as-data references —
    /// the '$catchrec_N'(RecVars) recovery term '$catch_begin' stores, a
    /// meta-called collect-loop goal — carry the bare name, which normally
    /// resolves through the map's bare aliases but must resolve here too when
    /// the asking activation never linked the helper.</summary>
    private void RegisterLateHelpers(
        IEnumerable<Shumway.Compiler.Wam.CompiledPredicate> preds)
    {
        foreach (var p in preds)
        {
            if (_runtimeAssertHelperPreds.ContainsKey(p.FunctorId)
                || !IsMetaHelperFunctorName(p.FunctorId))
                continue;
            _runtimeAssertHelperPreds[p.FunctorId] = p;
            var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(p.FunctorId);
            string? name = Shumway.Core.AtomTable.GetById(atomId)?.Name;
            int dollar = name?.IndexOf('$') ?? -1;
            if (name is not null && dollar > 0 && _modules.ContainsKey(name[..dollar]))
            {
                int bareFid = FunctorTable.Intern(
                    AtomTable.Intern(name[(dollar + 1)..], permanent: true).Id, arity);
                _runtimeAssertHelperPreds.TryAdd(bareFid, p);
            }
        }
    }

    /// <summary>The on-demand half of the ADR-015 visibility story for
    /// runtime-assert helpers. An asserted clause is visible to EVERY live
    /// activation through the shared dynamic chains — but its MetaTransform
    /// helpers (<c>'$disj_N'</c>, <c>'$catchgoal_N'</c>, …) were linked only into
    /// the ASSERTING activation's transient region and address map. A different
    /// activation (the outer query suspended around a nested
    /// consult-with-initialization — the Logtalk load shape) then runs the clause
    /// and calls — or meta-calls, via a findall collect-loop goal term — a helper
    /// its own map has never heard of. This materializes the compiled helper into
    /// the asking activation, exactly like <see cref="MaterializeDynamicTrampoline"/>
    /// does for freshly-promoted dynamics: append, relocate dispatch sites,
    /// register, patch call sites (materializing callee helpers recursively —
    /// registration-before-patch makes self/mutual recursion converge). Returns
    /// the linked address, or -1 when the fid is not a known assert helper.</summary>
    internal int TryMaterializeAssertHelper(Activation engine, int fid)
    {
        if (!_runtimeAssertHelperPreds.TryGetValue(fid, out var pred))
        {
            // Not an assert-time helper: a SETUP-minted one (the derivation of a
            // NESTED query recompiled a mutated dynamic — or regenerated the
            // statics — while THIS activation was suspended; its helpers were
            // linked into that setup's regions only). Their ASTs are in the
            // rewrite caches; compile the requested one on demand and memoize.
            List<Clause>? clauses = null;
            foreach (var entry in _dynamicRewriteCache.Values)
            {
                for (int i = 0; i < entry.HeadFids.Count; i++)
                    if (entry.HeadFids[i] == fid)
                        (clauses ??= new List<Clause>()).Add(entry.Clauses[i]);
                if (clauses is not null) break;
            }
            if (clauses is null && _staticRewriteHeadFids is { } sh && sh.Contains(fid)
                && _staticRewriteClauses is { } sc)
            {
                foreach (var c in sc)
                    if (HeadFunctorIdOf(c) == fid)
                        (clauses ??= new List<Clause>()).Add(c);
            }
            if (clauses is null) return -1;
            pred = new Shumway.Compiler.Wam.PredicateCompiler
                {
                    EmitDebugInfo = _flags.EmitDebugInfo,
                    DebugCodegen = _flags.DebugCodegen,
                    DebugFileId = _debugFileId,
                }.Compile(
                clauses,
                _literalPools.Strings, _literalPools.Floats, _literalPools.BigInts,
                enableIndexing: false,
                isDynamic: false);
            _runtimeAssertHelperPreds[fid] = pred;
        }
        if (engine.CurrentProgram is null
            || engine.CurrentFunctorAddresses is not Shumway.Core.LayeredIntMap<int> addrMap)
            return -1;
        if (addrMap.TryGetValue(fid, out int existing)
            && !Shumway.Core.CallTarget.IsUnresolved(existing)
            && !Activation.IsResumeMarker(existing))
            return existing;

        int addr = engine.AppendCode(pred.Bytecode);
        foreach (int siteRel in pred.DispatchSites)
        {
            int operandAbsPos = addr + siteRel;
            int predLocalTarget = Shumway.Core.BytecodeIO.ReadInt32(
                engine.CurrentProgram!, operandAbsPos);
            Shumway.Core.BytecodeIO.WriteInt32(
                engine.CurrentProgram!, operandAbsPos, addr + predLocalTarget);
        }
        addrMap[fid] = addr;   // before call-site patching: self-recursion lands here
        foreach (var site in pred.CallSites)
        {
            if (!addrMap.TryGetValue(site.CalleeFunctorId, out int target)
                || Shumway.Core.CallTarget.IsUnresolved(target))
            {
                int mat = TryMaterializeAssertHelper(engine, site.CalleeFunctorId);
                target = mat >= 0
                    ? mat
                    : Shumway.Core.CallTarget.ForUndefined(site.CalleeFunctorId);
            }
            Shumway.Core.BytecodeIO.WriteInt32(
                engine.CurrentProgram!, addr + site.OpcodeOffset + 1, target);
        }
        RefreshLiteralPoolsIfGrown(engine);
        return addr;
    }

    /// <summary>refreshes the interpreter's literal pools only
    /// when the engine pools actually grew past what the interpreter holds
    /// (recorded at query setup / last refresh). The pools are append-only
    /// with stable ids (<see cref="Shumway.Compiler.Wam.LiteralPool{T}"/>),
    /// so unchanged counts mean the interpreter's snapshot is already
    /// complete and the three per-assert <c>Snapshot()</c> array copies can
    /// be skipped — the common case: an asserted fact like
    /// <c>next_char_i(42)</c> interns nothing.</summary>
    internal void RefreshLiteralPoolsIfGrown(Activation engine)
    {
        // per-engine counters (an engine with no record refreshes
        // unconditionally; refresh is idempotent).
        var counts = _interpPoolCounts.GetValue(engine, static _ => new int[3]);
        if (_literalPools.Strings.Count == counts[0]
            && _literalPools.Floats.Count == counts[1]
            && _literalPools.BigInts.Count == counts[2])
            return;
        engine.RefreshLiteralPoolsCallback?.Invoke(
            _literalPools.Strings.Snapshot(),
            _literalPools.Floats.Snapshot(),
            _literalPools.BigInts.Snapshot());
        counts[0] = _literalPools.Strings.Count;
        counts[1] = _literalPools.Floats.Count;
        counts[2] = _literalPools.BigInts.Count;
    }

    /// <summary>ADR-015 chunk C step 4: incrementally compile and append a
    /// newly asserted clause's bytecode, then patch the chain's tail
    /// <c>&lt;next&gt;</c> operand to link it in. Avoids a full predicate
    /// recompile — the per-assertz cost stays O(clause size) rather than
    /// O(predicate size). Falls back silently (no-op) when the predicate's
    /// chain isn't in the new structure (paso-3 trust_me tail or indexed
    /// dispatch); in those cases the chunk-C redirect handles the update.
    ///
    /// <para>BROADCAST entry point: applies the extension to the
    /// mutating engine AND to every other live engine's buffer (each via
    /// its own chain table), so suspended outer/nested queries observe the
    /// assert when they resume — the single-code-space semantics SWI /
    /// GProlog give natively. Single-engine callers pay nothing (the
    /// other-engines list is null).</para></summary>
    internal void AppendDynamicClauseIncremental(
        Activation engine, int functorId, Clause newClause)
    {
        StackDiag("append", engine, functorId);
        AppendDynamicClauseIncrementalCore(engine, functorId, newClause);
        if (OtherLiveEnginesByTable(engine) is { } others)
            foreach (var other in others)
            {
                StackDiag("append-bcast", other, functorId);
                AppendDynamicClauseIncrementalCore(other, functorId, newClause);
            }
    }

    /// <summary>reconciles <paramref name="engine"/>'s dynamic
    /// dispatch view with the authoritative store at a RESUME boundary
    /// (a mid-query consult returning to its suspended caller). The
    /// mutation broadcast keeps live views coherent when every in-place
    /// patch lands, but a single silently-skipped patch (a guard bail, a
    /// buffer realloc racing a suspended view) leaves a permanent ghost —
    /// a clause visible in one engine's dispatch that the store no longer
    /// holds (Logtalk's loading-stack "file is already loading" error) or
    /// vice versa. Instead of trusting N incremental patches, diff each
    /// chain against the store BY CLAUSE IDENTITY: dead entries get their
    /// died slot patched in this engine's buffer; store clauses missing
    /// from the chain are appended through the normal incremental path.
    /// (Appended late-arrivals land at the chain tail regardless of their
    /// store position — set-visibility is exact, relative order of
    /// clauses this engine never saw may differ from the store.)</summary>
    internal void ReconcileEngineDynamicView(Activation engine)
    {
        if (GetChainTable(engine) is not { } tbl) return;
        if (engine.CurrentProgram is null) return;
        // Snapshot fids: the rebuild path can materialize new trampolines,
        // mutating the dictionary while we walk.
        var fids = new List<int>(tbl.Chains.Keys);
        foreach (int fid in fids)
        {
            if (!tbl.Chains.TryGetValue(fid, out var chain)) continue;
            var store = _dynStore.TryGetClauses(fid, out var cs)
                ? cs : (IReadOnlyList<Clause>)Array.Empty<Clause>();
            // Set-compare by clause identity: equal → the view is exact.
            bool diverged = store.Count != chain.Entries.Count;
            if (!diverged)
            {
                var storeSet = new HashSet<Clause>(store, ReferenceEqualityComparer.Instance);
                foreach (var e in chain.Entries)
                    if (!storeSet.Contains(e.Clause)) { diverged = true; break; }
            }
            if (!diverged) continue;
            // Any divergence → rebuild wholesale. A fine-grained diff-append
            // is unsound for a view whose real layout the chain table
            // doesn't describe (indexed buckets — the appends would land in
            // the buckets a SECOND time), and the rebuild is O(store)
            // anyway. It also realigns the table so the next reconcile
            // compares equal.
            RebuildEngineFidChainView(engine, fid);
        }
    }

    /// <summary>rebuilds one functor's dynamic-dispatch view in
    /// <paramref name="target"/>'s buffer from the authoritative store.
    /// Needed when the in-place mutation paths can't keep that view
    /// coherent — the archetype: the target's buffer compiled the dynamic
    /// predicate with the INDEXED layout (it went hot), whose
    /// bucket entries the chain-table-based retract broadcast cannot
    /// patch, leaving retracted clauses visible forever in that engine
    /// (Logtalk's loading-stack ghosts). Strategy: materialize a fresh
    /// chain-layout trampoline, re-link every store clause behind it, and
    /// overwrite the OLD entry point's first bytes with
    /// <c>execute &lt;new&gt;</c> so already-baked call sites reach the
    /// rebuilt view. The old layout's interior is left intact — a
    /// suspended choice point resuming into it still finds its code
    /// (its clauses now carry dead/stale visibility, which only narrows
    /// what an old goal sees — the safe direction for bookkeeping
    /// predicates).</summary>
    /// <summary>Re-entrancy guard for <see cref="RebuildEngineFidChainView"/>:
    /// the rebuild re-links the store through
    /// <see cref="AppendDynamicClauseIncrementalCore"/>, whose repair paths
    /// call the rebuild — a fresh trampoline always accepts in-place
    /// appends so recursion shouldn't arise, but a guard keeps a
    /// pathological shape from looping. Single-threaded per host.</summary>
    internal bool _inFidViewRebuild;

    internal void RebuildEngineFidChainView(Activation target, int functorId)
    {
        if (_inFidViewRebuild) return;
        if (target.CurrentProgram is null
            || target.DynamicFailStubAddr <= 0
            || target.CurrentFunctorAddresses is not Shumway.Core.LayeredIntMap<int> addrMap
            || !addrMap.TryGetValue(functorId, out int oldAddr)
            || Shumway.Core.CallTarget.IsUnresolved(oldAddr)
            || Activation.IsResumeMarker(oldAddr)
            || oldAddr < 0
            || oldAddr + 5 > target.CurrentProgram.Length)
            return;
        _inFidViewRebuild = true;
        try
        {
            // Drop the stale chain record so MaterializeDynamicTrampoline
            // builds fresh state, then re-link the current store.
            GetOrCreateChainTable(target).Chains.Remove(functorId);
            MaterializeDynamicTrampoline(target, functorId);
            if (_dynStore.TryGetClauses(functorId, out var cs))
                foreach (var c in cs)
                    AppendDynamicClauseIncrementalCore(target, functorId, c);
            if (!addrMap.TryGetValue(functorId, out int newAddr) || newAddr == oldAddr)
                return;
            // Redirect the old entry point. Any dynamic layout starts with
            // enter_dynamic (1 byte) + a dispatch opcode of ≥5 bytes, so a
            // 5-byte execute always fits without clobbering a chain
            // instruction a suspended CP could resume at.
            var program = target.CurrentProgram!;
            program[oldAddr] = (byte)Shumway.Core.Opcode.Execute;
            Shumway.Core.BytecodeIO.WriteInt32(program, oldAddr + 1, newAddr);
        }
        finally { _inFidViewRebuild = false; }
    }

    /// <summary>Diag (SHUMWAY_STACK_DIAG, read once) — traces the dynamic
    /// mutation fan-out for one predicate: "1" traces Logtalk's
    /// '$lgt_file_loading_stack_'/2 (the original target); any other
    /// value names the functor to trace. The diagnostic that pinned both
    /// the misaligned index-based retract patch and the indexed-layout
    /// ghost views.</summary>
    private static readonly string? StackDiagTarget =
        Environment.GetEnvironmentVariable("SHUMWAY_STACK_DIAG") switch
        {
            null or "" or "0" => null,
            "1" => "$lgt_file_loading_stack_",
            var v => v,
        };
    private static readonly bool StackDiagEnabled = StackDiagTarget is not null;

    private void StackDiag(string op, Activation engine, int functorId)
    {
        if (!StackDiagEnabled) return;
        var (aid, ar) = FunctorTable.Lookup(functorId);
        string name = AtomTable.GetById(aid)?.Name ?? "?";
        if (name != StackDiagTarget) return;
        var tbl = GetChainTable(engine);
        int entries = tbl is not null && tbl.Chains.TryGetValue(functorId, out var ch)
            ? ch.Entries.Count : -1;
        Console.Error.WriteLine(
            $"[STK-{op}] eng={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(engine):X8}"
            + $" buf={(engine.CurrentProgram is { } p ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p).ToString("X8") : "null")}"
            + $" tbl={(tbl is not null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(tbl).ToString("X8") : "null")}"
            + $" owns={EngineOwnsHostBuffer(engine)} entries={entries}");
    }

    private void AppendDynamicClauseIncrementalCore(
        Activation engine, int functorId, Clause newClause)
    {
        // Root fix: a suspended owner resuming after a sibling extended the
        // shared buffer must append AFTER that content, not over it.
        ResyncOwnerAppendPosition(engine);
        // try the new extensible-indexed in-place
        // extension first. If the predicate uses the extensible-indexed
        // layout (enter_dynamic + switch_on_term + try_me_else
        // bucket chains) AND the new clause's arg-0 key matches an
        // existing bucket, we extend each affected chain in place
        // — no rebuild needed. Returns true if handled here.
        if (TryAppendToIndexedDynamic(engine, functorId, newClause))
            return;
        // Fall back to the chain layout or, for cases
        // can't yet handle (new key, var-arg, multi-arg
        // indexed), to the persistent-buffer rebuild.
        // Fall back: chain extension applies only when
        // chain state is populated (i.e. the predicate was compiled
        // as a try_me_else chain). Otherwise the layout is some
        // form of indexed dispatch that didn't handle
        // — for a hot predicate, request a persistent rebuild so the
        // next query sees the new clause through a fresh compile.
        // resolve chain state through THIS engine's table (the
        // one describing its buffer; see DynChainTable) and capture buffer
        // ownership before any AppendCode.
        bool ownsHost = EngineOwnsHostBuffer(engine);
        var chainTable = GetChainTable(engine);
        // mid-query trampoline materialisation. If the
        // predicate was auto-promoted mid-query via EnsureDynamic
        // (implicit_dynamic=true with a runtime-bound assertz head),
        // _dynStore.Functors holds it but no chain state was ever
        // built — the trampoline lives in bytecode emitted by
        // SetupQueryFromTerm, and that ran before the auto-promote.
        // Build a fresh trampoline now so the extension
        // below has something to extend.
        if ((chainTable is null || !chainTable.Chains.ContainsKey(functorId))
            && _dynStore.IsDynamic(functorId)
            && engine.CurrentProgram is not null
            && engine.DynamicFailStubAddr > 0)
        {
            MaterializeDynamicTrampoline(engine, functorId);
            chainTable ??= GetChainTable(engine);
        }

        if (chainTable is null
            || !chainTable.Chains.TryGetValue(functorId, out var chain)
            || chain.TailNextAddr < 0
            || engine.CurrentProgram is null
            || engine.DynamicFailStubAddr <= 0)
        {
            if (_jitIndexProfile.IsHot(functorId)) InvalidatePersistent();
            // an unextendable chain record (a trust_me tail, or
            // a contiguous walk over an indexed layout) means THIS engine's
            // live view can't take the clause in place — without repair its
            // dispatch silently diverges from the store forever (the
            // '$lgt_current_object_' stuck-entries signature). Rebuild the
            // fid's view wholesale; the rebuilt chain absorbs the whole
            // store, new clause included.
            RebuildEngineFidChainView(engine, functorId);
            return;
        }

        // last-line safety: skip the in-place extend when the
        // chain is stale relative to the live buffer (the tail-next patch
        // below would splice into instruction interior → "reserved_invalid
        // / 0xCF" corruption). With per-engine chain tables this should
        // never fire — a table only ever describes its own engine's
        // buffer — but if it does, fall back to the store (authoritative),
        // force a host rebuild, and repair this engine's live view.
        // NB: checked against ProgramLength (content), not CurrentProgram
        // .Length (capacity) — a chain reaching past this activation's
        // believed content end means its append position is stale and an
        // in-place extend would overwrite live entries.
        if (DynChainAddressesStale(
                chain, engine.ProgramLength, engine.DynamicFailStubAddr)
            || !IsChainInstructionAt(engine.CurrentProgram, chain.TailNextAddr - 1))
        {
            InvalidatePersistent();
            RebuildEngineFidChainView(engine, functorId);
            return;
        }

        // Apply the same transforms the setup path runs — dynamic clauses
        // share a flat module rewrite context (shared helper
        // with a fact fast path).
        var compiledClause = CompileRuntimeAssertClause(engine, functorId, newClause);
        if (compiledClause is null) return;

        // Build the chunk:
        //   retry_me_else <fail-stub>   (5 bytes — chain op, <next>=fail-stub)
        //   check_visible <born> <died> (17 bytes)
        //   <body bytes>
        var emitter = new BytecodeEmitter();
        emitter.EmitRetryMeElse(engine.DynamicFailStubAddr);
        const int NextOperandLocal = 1;        // position of <next> operand
        emitter.EmitCheckVisible(born: _dbGeneration.Value, died: long.MaxValue);
        const int DiedOperandLocal = 5 + 9;    // retry_me_else (5) + opcode (1) + born (8)
        int bodyStartLocal = emitter.Position;
        emitter.AppendBytes(compiledClause.Bytecode);
        byte[] chunk = emitter.ToBytes();

        // try the free-list (chunks reclaimed by a prior
        // GC) before extending the program buffer. Tripwire: a reused
        // chunk must not contain the tail slot we're about to patch —
        // copying into it would destroy the live tail and the patch below
        // would write the chunk's own address into its own <next> operand
        // (a self-referential chain = an unbreakable dispatch cycle).
        int chunkAddr = TryReuseFreeChunk(chainTable.FreeChunks, chunk.Length);
        if (chunkAddr >= 0
            && chain.TailNextAddr >= chunkAddr
            && chain.TailNextAddr < chunkAddr + chunk.Length)
        {
            ChainCorruptionRecover(
                "assertz-chain-reuse", engine, functorId,
                $"reused chunk [{chunkAddr}..{chunkAddr + chunk.Length}) contains tail slot {chain.TailNextAddr}");
            return;
        }
        if (chunkAddr >= 0)
            Array.Copy(chunk, 0, engine.CurrentProgram!, chunkAddr, chunk.Length);
        else
            chunkAddr = engine.AppendCode(chunk);
        var program = engine.CurrentProgram!;

        // Patch call sites inside the body to absolute targets.
        var addrMap = engine.CurrentFunctorAddresses;
        foreach (var site in compiledClause.CallSites)
        {
            int operandPos = chunkAddr + bodyStartLocal + site.OpcodeOffset + 1;
            int target = (addrMap is not null
                          && addrMap.TryGetValue(site.CalleeFunctorId, out int addr))
                ? addr
                : Shumway.Core.CallTarget.ForUndefined(site.CalleeFunctorId);
            Shumway.Core.BytecodeIO.WriteInt32(program, operandPos, target);
        }

        // Link the new clause into the chain: previous tail's <next> now
        // points at our chunk's chain instruction. Tripwire: writing the
        // chunk's own address into a slot inside the chunk itself is the
        // self-cycle — unreachable given the staleness check above, kept
        // as the last line of defense before the corrupting write.
        if (chain.TailNextAddr >= chunkAddr
            && chain.TailNextAddr < chunkAddr + chunk.Length)
        {
            ChainCorruptionRecover(
                "assertz-chain-patch", engine, functorId,
                $"chunk {chunkAddr} would self-splice at tail slot {chain.TailNextAddr}");
            return;
        }
        Shumway.Core.BytecodeIO.WriteInt32(program, chain.TailNextAddr, chunkAddr);

        // Update chain state.
        chain.Entries.Add(new DynChainEntry(
            newClause,
            died: chunkAddr + DiedOperandLocal,
            next: chunkAddr + NextOperandLocal,
            chunkAddr: chunkAddr,
            chunkLength: chunk.Length));
        chain.TailNextAddr = chunkAddr + NextOperandLocal;

        // AppendCode may have reallocated the buffer;
        // refresh PrologEngine's reference so the next query sees the
        // live buffer (owner engine) — or, when this engine's buffer is
        // no longer the host's (a nested query rebuilt it), mark the
        // host buffer for rebuild: the newer buffer did NOT get this
        // in-place extension and would otherwise miss the clause on
        // cross-query reuse.
        SyncOrInvalidateAfterMutation(engine, ownsHost);

        // The clause may have interned new literals — refresh the
        // interpreter so check_visible isn't running against a stale
        // pool snapshot for any subsequent call (skipped
        // when the pools didn't grow).
        RefreshLiteralPoolsIfGrown(engine);
    }

    /// <summary>Test hook: returns the chain's current tail-next address
    /// (where the next assertz would patch), or <c>null</c> when no chain
    /// state exists.</summary>
    internal int? PeekTailNextAddr(int functorId)
    {
        if (!DynChains.Chains.TryGetValue(functorId, out var chain)) return null;
        return chain.TailNextAddr;
    }

    /// <summary>Test hook: returns the chain's current head-clause
    /// address — what asserta will demote to retry_me_else + nops on the
    /// next call.</summary>
    internal int? PeekHeadClauseAddr(int functorId)
    {
        if (!DynChains.Chains.TryGetValue(functorId, out var chain)) return null;
        return chain.HeadClauseAddr;
    }

    /// <summary>ADR-015 chunk C step 4 — asserta path. Compiles the new
    /// clause as a chunk headed by <c>try_me_else &lt;old-head&gt;</c>,
    /// appends it, demotes the previous head's <c>try_me_else</c> in
    /// place to <c>retry_me_else &lt;same-next&gt;</c> + 4 nops (same
    /// 9-byte footprint), and patches the trampoline's
    /// <c>execute &lt;chain-head&gt;</c> to the new chunk. Falls back
    /// silently when the chain doesn't have a trampoline (paso-3
    /// emission or indexed dispatch).
    ///
    /// <para>BROADCAST entry point (see
    /// <see cref="AppendDynamicClauseIncremental"/>).</para></summary>
    internal void PrependDynamicClauseIncremental(
        Activation engine, int functorId, Clause newClause)
    {
        StackDiag("prepend", engine, functorId);
        PrependDynamicClauseIncrementalCore(engine, functorId, newClause);
        if (OtherLiveEnginesByTable(engine) is { } others)
            foreach (var other in others)
            {
                StackDiag("prepend-bcast", other, functorId);
                PrependDynamicClauseIncrementalCore(other, functorId, newClause);
            }
    }

    private void PrependDynamicClauseIncrementalCore(
        Activation engine, int functorId, Clause newClause)
    {
        // Root fix: a suspended owner resuming after a sibling extended the
        // shared buffer must append AFTER that content, not over it.
        ResyncOwnerAppendPosition(engine);
        // try in-place asserta for layout.
        if (TryPrependToIndexedDynamic(engine, functorId, newClause))
            return;
        // mirror the assertz path: when the predicate was
        // auto-promoted mid-query (no trampoline ever built), build
        // one now so the chain prepend below has a chain to prepend
        // to.
        // resolve chain state through THIS engine's table and
        // capture buffer ownership before any AppendCode.
        bool ownsHost = EngineOwnsHostBuffer(engine);
        var chainTable = GetChainTable(engine);
        if ((chainTable is null || !chainTable.Chains.ContainsKey(functorId))
            && _dynStore.IsDynamic(functorId)
            && engine.CurrentProgram is not null
            && engine.DynamicFailStubAddr > 0)
        {
            MaterializeDynamicTrampoline(engine, functorId);
            chainTable ??= GetChainTable(engine);
        }
        // Fall back to chain prepend for chain layout, or
        // rebuild for indexed layouts we can't extend in place.
        if (chainTable is null
            || !chainTable.Chains.TryGetValue(functorId, out var chain)
            || chain.TrampolineExecuteOperandAddr < 0
            || engine.CurrentProgram is null)
        {
            if (_jitIndexProfile.IsHot(functorId)) InvalidatePersistent();
            // repair this engine's live view (see the assertz
            // counterpart). The rebuild re-links the store in order, so
            // the asserta'd clause (already prepended there) lands at the
            // rebuilt chain's head.
            RebuildEngineFidChainView(engine, functorId);
            return;
        }
        if (engine.DynamicFailStubAddr <= 0) return;

        // last-line safety: skip the in-place prepend when the
        // chain is stale (the head-demotion / trampoline patch would write
        // past the live buffer or into an unrelated instruction). With
        // per-engine chain tables this should never fire; if it does, fall
        // back to the store, force a host rebuild, and repair the view.
        // NB: checked against ProgramLength (content), not capacity —
        // see the assertz path for the stale-append-position rationale.
        if (chain.TrampolineExecuteOperandAddr + sizeof(long) > engine.ProgramLength
            || DynChainAddressesStale(
                   chain, engine.ProgramLength, engine.DynamicFailStubAddr)
            || (chain.HeadClauseAddr >= 0
                && !IsChainInstructionAt(engine.CurrentProgram, chain.HeadClauseAddr)))
        {
            InvalidatePersistent();
            RebuildEngineFidChainView(engine, functorId);
            return;
        }

        var (_, arity) = FunctorTable.Lookup(functorId);

        // Same transform pipeline as the setup path (shared
        // helper with a fact fast path).
        var compiledClause = CompileRuntimeAssertClause(engine, functorId, newClause);
        if (compiledClause is null) return;

        // Chunk layout:
        //   try_me_else <chain-head-target>, <arity>   (9 bytes)
        //   check_visible <born> <died>                (17 bytes)
        //   <body>
        int oldHead = chain.HeadClauseAddr;
        int chainHeadTarget = oldHead >= 0 ? oldHead : engine.DynamicFailStubAddr;

        var emitter = new BytecodeEmitter();
        emitter.EmitTryMeElse(chainHeadTarget, arity);
        const int NextOperandLocal = 1;
        emitter.EmitCheckVisible(born: _dbGeneration.Value, died: long.MaxValue);
        const int DiedOperandLocal = 9 + 9;            // try_me_else (9) + opcode (1) + born (8)
        int bodyStartLocal = emitter.Position;
        emitter.AppendBytes(compiledClause.Bytecode);
        byte[] chunk = emitter.ToBytes();

        // try the free-list before extending the program.
        // Tripwire (mirrors the assertz path): a reused chunk must not
        // contain the trampoline operand or the old head we patch below.
        int chunkAddr = TryReuseFreeChunk(chainTable.FreeChunks, chunk.Length);
        if (chunkAddr >= 0
            && ((chain.TrampolineExecuteOperandAddr >= chunkAddr
                 && chain.TrampolineExecuteOperandAddr < chunkAddr + chunk.Length)
                || (oldHead >= chunkAddr && oldHead < chunkAddr + chunk.Length)))
        {
            ChainCorruptionRecover(
                "asserta-chain-reuse", engine, functorId,
                $"reused chunk [{chunkAddr}..{chunkAddr + chunk.Length}) overlaps live patch slots");
            return;
        }
        if (chunkAddr >= 0)
            Array.Copy(chunk, 0, engine.CurrentProgram!, chunkAddr, chunk.Length);
        else
            chunkAddr = engine.AppendCode(chunk);
        var program = engine.CurrentProgram!;

        // Patch the body's call sites.
        var addrMap = engine.CurrentFunctorAddresses;
        foreach (var site in compiledClause.CallSites)
        {
            int operandPos = chunkAddr + bodyStartLocal + site.OpcodeOffset + 1;
            int target = (addrMap is not null
                          && addrMap.TryGetValue(site.CalleeFunctorId, out int addr))
                ? addr
                : Shumway.Core.CallTarget.ForUndefined(site.CalleeFunctorId);
            Shumway.Core.BytecodeIO.WriteInt32(program, operandPos, target);
        }

        // Demote the previous head's try_me_else (9 bytes) to
        // retry_me_else <same-next> (5 bytes) + 4 nops. The address
        // operand at +1..+4 stays — retry_me_else uses it as its <next>.
        if (oldHead >= 0)
        {
            program[oldHead] = (byte)Shumway.Core.Opcode.RetryMeElse;
            program[oldHead + 5] = (byte)Shumway.Core.Opcode.Nop;
            program[oldHead + 6] = (byte)Shumway.Core.Opcode.Nop;
            program[oldHead + 7] = (byte)Shumway.Core.Opcode.Nop;
            program[oldHead + 8] = (byte)Shumway.Core.Opcode.Nop;
        }

        // Patch the trampoline's execute operand to the new head.
        Shumway.Core.BytecodeIO.WriteInt32(
            program, chain.TrampolineExecuteOperandAddr, chunkAddr);

        // Update chain state. The new clause is now the head; existing
        // entries shift one to the right, matching _dynamicClauses where
        // asserta prepended.
        chain.HeadClauseAddr = chunkAddr;
        chain.Entries.Insert(0, new DynChainEntry(
            newClause,
            died: chunkAddr + DiedOperandLocal,
            next: chunkAddr + NextOperandLocal,
            chunkAddr: chunkAddr,
            chunkLength: chunk.Length));
        // If this was the first ever clause, the new chunk is also the tail.
        if (chain.TailNextAddr < 0)
            chain.TailNextAddr = chunkAddr + NextOperandLocal;

        // keep the persistent-buffer reference in sync
        // (owner engine) — or mark the host buffer for rebuild when this
        // engine's buffer is no longer the host's.
        SyncOrInvalidateAfterMutation(engine, ownsHost);

        // Refresh interpreter pools — same reasoning as the assertz path
        // (skipped when the pools didn't grow).
        RefreshLiteralPoolsIfGrown(engine);
    }

    /// <summary>ADR-015 chunk C: recompiles a dynamic predicate from its
    /// current clauses and appends the bytecode to the running program,
    /// returning the new entry address. Invoked lazily — on the first call
    /// to the predicate after an <c>assertz</c> / <c>retract</c> /
    /// <c>abolish</c> marked it stale. The clauses run through the same
    /// transform pipeline as query setup; the predicate is compiled
    /// unindexed (so there are no switch tables to merge into the running
    /// interpreter) and linked against the query's existing symbol map, so
    /// its body's calls into static — or other dynamic — predicates
    /// resolve. Old compiled bodies are left in place, so a call already
    /// backtracking through one keeps its clause set (the logical update
    /// view).</summary>
    /// <summary>ADR-015 chunk C step 4: patch the bytecode <c>died</c>
    /// slot of the clause at index <paramref name="clauseIndex"/> in
    /// <paramref name="functorId"/>'s chain. The next call's
    /// <c>check_visible</c> reads it and filters the clause out. Sets
    /// <c>died</c> to the current <see cref="DbGeneration"/> — the
    /// generation already bumped by the surrounding modification, so a
    /// query whose captured view-gen is below it still sees the clause.
    /// After patching the slot, also drops the chain entry — the chain
    /// stays aligned with <see cref="_dynamicClauses"/>.</summary>
    /// <summary>true if any of a dynamic chain's cached byte
    /// offsets fall outside the writable range of the live program buffer.
    /// A stale chain entry (its <see cref="_dynChains"/> record indexing a
    /// buffer since rebuilt smaller) would make an in-place died / next
    /// patch write past the array; the callers skip that in-place patch and
    /// fall back to the store (which is already authoritative) rather than
    /// crash. An 8-byte tail margin covers both the int32 <c>next</c> and
    /// int64 <c>died</c> operands with one check.</summary>
    private static bool DynChainAddressesStale(
        DynChainState chain, int programLength, int failStub)
    {
        static bool Bad(int addr, int len) => addr > 0 && addr + sizeof(long) > len;
        if (Bad(chain.HeadClauseAddr, programLength)
            || Bad(chain.TailNextAddr, programLength)
            || Bad(failStub, programLength))
            return true;
        foreach (var e in chain.Entries)
            if (Bad(e.NextOperandAddr, programLength) || Bad(e.DiedOperandAddr, programLength))
                return true;
        return false;
    }

    /// <summary>O(1) structural staleness check: the byte at
    /// <paramref name="opcodeAddr"/> must be a chain instruction
    /// (<c>try_me_else</c> / <c>retry_me_else</c>). A dynamic chain's
    /// <c>TailNextAddr</c> is the <c>&lt;next&gt;</c> operand at offset +1 of
    /// such an instruction; if the opcode byte before it isn't one, the cached
    /// chain no longer maps to the live bytecode (stale-but-in-range) and an
    /// in-place patch there would corrupt an unrelated instruction. Cheaper
    /// than re-walking every entry, so safe on the hot assertz path.</summary>
    private static bool IsChainInstructionAt(byte[]? program, int opcodeAddr)
    {
        if (program is null || opcodeAddr < 0 || opcodeAddr >= program.Length)
            return false;
        byte op = program[opcodeAddr];
        return op == (byte)Shumway.Core.Opcode.TryMeElse
            || op == (byte)Shumway.Core.Opcode.RetryMeElse;
    }

    /// <summary>full structural staleness check for a dynamic
    /// chain: the head and every entry's <c>&lt;next&gt;</c> operand
    /// (offset +1) must sit right after a chain instruction in the live
    /// buffer. Catches a stale-but-in-range chain (its cached offsets point
    /// inside the buffer but no longer at the actual chain instructions —
    /// e.g. after the persistent buffer was rebuilt) that the bounds-only
    /// <see cref="DynChainAddressesStale"/> misses. O(entries); used only on
    /// the retract-family paths (dead-chain reclaim, died patch), not the
    /// per-clause assertz fast path.</summary>
    private static bool DynChainStructurallyStale(byte[]? program, DynChainState chain)
    {
        if (program is null) return true;
        if (chain.HeadClauseAddr >= 0 && !IsChainInstructionAt(program, chain.HeadClauseAddr))
            return true;
        foreach (var e in chain.Entries)
            if (!IsChainInstructionAt(program, e.NextOperandAddr - 1))
                return true;
        return false;
    }

    /// <summary>broadcast counterpart of
    /// <see cref="PatchDiedFromChain"/>: finds the target engine's chain
    /// entries whose <see cref="DynChainEntry.Clause"/> IS the retracted
    /// clause (reference identity — the store and every chain share the
    /// same Clause objects) and patches each one's died slot in that
    /// engine's buffer. By-reference matching (not by index) because a
    /// suspended engine's chain may be missing entries the mutating
    /// engine's has (an append it never received pre-registration), so
    /// positions need not line up.</summary>
    /// <returns>Number of chain entries matched (and retired). 0 means
    /// this engine's chain has no record of the clause — either it never
    /// received it (fine: not visible either) or its view uses a layout
    /// the chain table doesn't describe (indexed) — the caller decides
    /// whether to rebuild.</returns>
    private int PatchDiedFromChainByClause(Activation engine, int functorId, Clause clause)
    {
        bool diag = StackDiagEnabled;
        if (GetChainTable(engine) is not { } tbl
            || !tbl.Chains.TryGetValue(functorId, out var chain))
        {
            if (diag) StackDiag("died-NOCHAIN", engine, functorId);
            return 0;
        }
        var program = engine.CurrentProgram;
        int matched = 0, patched = 0;
        for (int i = chain.Entries.Count - 1; i >= 0; i--)
        {
            var entry = chain.Entries[i];
            if (!ReferenceEquals(entry.Clause, clause)) continue;
            matched++;
            if (program is not null && entry.DiedOperandAddr > 0
                && entry.DiedOperandAddr + sizeof(long) <= program.Length
                && IsChainInstructionAt(program, entry.NextOperandAddr - 1))
            {
                BytecodeIO.WriteInt64(program, entry.DiedOperandAddr, _dbGeneration.Value);
                patched++;
            }
            if (entry.ChunkAddr >= 0)
                chain.DeadChunks.Add((entry.ChunkAddr, entry.ChunkLength));
            chain.Entries.RemoveAt(i);
        }
        if (diag) StackDiag($"died-m{matched}p{patched}", engine, functorId);
        return matched;
    }

    private void PatchDiedFromChain(Activation engine, int functorId, int clauseIndex)
    {
        // the engine's own chain table (describes ITS buffer).
        if (GetChainTable(engine) is not { } tbl
            || !tbl.Chains.TryGetValue(functorId, out var chain)) return;
        if (clauseIndex < 0 || clauseIndex >= chain.Entries.Count) return;
        var entry = chain.Entries[clauseIndex];
        var program = engine.CurrentProgram;
        // last-line safety: skip the in-place died patch when
        // the entry's cached slot is out of range OR structurally stale
        // (should never fire with per-engine tables). The clause is
        // already removed from _dynamicClauses by the caller, so clause/2
        // is correct; the chain rebuilds at the next query setup.
        if (program is not null && entry.DiedOperandAddr > 0
            && entry.DiedOperandAddr + sizeof(long) <= program.Length
            && IsChainInstructionAt(program, entry.NextOperandAddr - 1))
            BytecodeIO.WriteInt64(program, entry.DiedOperandAddr, _dbGeneration.Value);
        // stage the chunk for free-list reuse on GC, but
        // only when it was an incrementally-allocated chunk (consult-
        // time blocks have ChunkAddr=-1 and can't be freed without
        // disturbing the rest of the predicate's contiguous bytecode).
        if (entry.ChunkAddr >= 0)
            chain.DeadChunks.Add((entry.ChunkAddr, entry.ChunkLength));
        chain.Entries.RemoveAt(clauseIndex);
    }

    /// <summary>Builds the per-functor chain state by walking the linked
    /// program for each dynamic predicate's compiled bytecode and locating
    /// the trampoline + each clause's <c>check_visible</c>. Called by
    /// <see cref="SetupQueryFromTerm"/> once the linked program is in
    /// place; subsequent mid-query <c>assertz</c> / <c>retract</c> /
    /// <c>abolish</c> mutate chain state and the live program in place,
    /// no rebuild needed.</summary>
    private void PopulateDynChains(
        byte[] program,
        IReadOnlyDictionary<int, int> addressMap,
        IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate> predicatesByAddress)
    {
        DynChains.Chains.Clear();
        var seen = new HashSet<int>();
        foreach (int fid in _dynStore.Functors)
            if (seen.Add(fid))
                PopulateDynChainViaAddressMap(fid, program, addressMap, predicatesByAddress);
        foreach (int fid in _dynStore.ClauseFunctors)
            if (seen.Add(fid))
                PopulateDynChainViaAddressMap(fid, program, addressMap, predicatesByAddress);
    }

    private void PopulateDynChainViaAddressMap(
        int fid, byte[] program,
        IReadOnlyDictionary<int, int> addressMap,
        IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate> predicatesByAddress)
    {
        if (!addressMap.TryGetValue(fid, out int predAddr)) return;
        if (!predicatesByAddress.TryGetValue(predAddr, out var pred)) return;
        PopulateDynChainFor(fid, program, predAddr, pred.Bytecode.Length);
    }

    /// <summary>Walks <paramref name="predByteLength"/> bytes of
    /// <paramref name="program"/> starting at <paramref name="predAddr"/>,
    /// pairing each <c>check_visible</c> opcode it finds with the
    /// corresponding clause from <see cref="_dynamicClauses"/> in order.
    /// Replaces any prior chain state for the functor.</summary>
    private void PopulateDynChainFor(
        int fid, byte[] program, int predAddr, int predByteLength)
    {
        DynChains.Chains.Remove(fid);
        // Empty dynamic predicates still need chain state for incremental
        // assertz — the empty-stub clause's try_me_else <fail-stub> is the
        // first patch target. So default to an empty clause list rather
        // than skipping when _dynamicClauses has no entry.
        var clauses = _dynStore.TryGetClauses(fid, out var cs)
            ? cs : (IReadOnlyList<Clause>)Array.Empty<Clause>();

        var chain = new DynChainState();
        int pc = predAddr;
        int end = predAddr + predByteLength;
        int clauseIndex = 0;
        int pendingNextOperand = -1;
        int tailNextOperand = -1;
        // track each chunk's start address as we walk so
        // entries record ChunkAddr / ChunkLength for the free-list
        // reuse on GC. The chunk starts at the chain instruction and
        // ends at the next chain instruction (or the end of the
        // predicate's bytecode). Lengths are filled in retroactively
        // when the next chunk's start is seen.
        int currentChunkStart = -1;

        // Locate the trampoline (enter_dynamic; execute <chain-head>),
        // if any. The trampoline structure was emitted by paso-4's
        // compile path; older paso-3 emission has no Execute after
        // EnterDynamic.
        if (pc < end && program[pc] == (byte)Shumway.Core.Opcode.EnterDynamic
            && pc + 1 < end && program[pc + 1] == (byte)Shumway.Core.Opcode.Execute)
        {
            chain.TrampolineExecuteOperandAddr = pc + 2;
            chain.HeadClauseAddr =
                Shumway.Core.BytecodeIO.ReadInt32(program, pc + 2);
            // ADR-041 — the dispatch-time selector resolves a trampoline pc
            // to its functor through this map. Setup-compiled predicates in a
            // REUSED persistent buffer are in no later query's
            // PredicatesByAddress snapshot, so without this entry their
            // chains never select CP-free (Logtalk's '$lgt_current_category_'
            // lookup leaked its chain CP into every ^^ cache miss).
            DynChains.TrampolineFids[predAddr] = fid;
            // Advance past the trampoline (EnterDynamic + Execute = 6 bytes).
            pc += 6;
        }
        while (pc < end)
        {
            var info = Shumway.Core.OpcodeTable.Get(program[pc]);
            if (info.Op == Shumway.Core.Opcode.TryMeElse
                || info.Op == Shumway.Core.Opcode.RetryMeElse)
            {
                // a new chain instruction marks a chunk
                // boundary. Close out the previous chunk's length on
                // the most-recent entry (if any).
                if (currentChunkStart >= 0 && chain.Entries.Count > 0)
                {
                    var last = chain.Entries[^1];
                    if (last.ChunkAddr == currentChunkStart)
                        last.ChunkLength = pc - currentChunkStart;
                }
                currentChunkStart = pc;
                pendingNextOperand = pc + 1;
                tailNextOperand = pc + 1;
            }
            else if (info.Op == Shumway.Core.Opcode.TrustMe)
            {
                pendingNextOperand = -1;
                tailNextOperand = -1;   // not patchable
            }
            else if (info.Op == Shumway.Core.Opcode.CheckVisible
                     && clauseIndex < clauses.Count)
            {
                chain.Entries.Add(new DynChainEntry(
                    clauses[clauseIndex],
                    died: pc + 9,
                    next: pendingNextOperand,
                    chunkAddr: currentChunkStart,
                    chunkLength: 0));   // patched on next chunk start / end
                pendingNextOperand = -1;
                clauseIndex++;
            }
            pc += info.Size;
        }
        // Close out the final chunk's length.
        if (currentChunkStart >= 0 && chain.Entries.Count > 0)
        {
            var last = chain.Entries[^1];
            if (last.ChunkAddr == currentChunkStart && last.ChunkLength == 0)
                last.ChunkLength = end - currentChunkStart;
        }
        chain.TailNextAddr = tailNextOperand;
        // Always record chain state when a tail-next exists, even when
        // _dynamicClauses is empty (declared-but-never-asserted dynamic
        // predicates have the empty-stub clause as the patch target for
        // the first incremental assertz).
        if (chain.Entries.Count > 0 || tailNextOperand >= 0)
            DynChains.Chains[fid] = chain;
    }

}
