using System.Collections.Immutable;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;

namespace Shumway.Embedding;

/// <summary>
/// High-level entry point for embedding Shumway in a .NET host. Accumulates
/// consulted Prolog source into a set of named modules, then satisfies queries
/// by compiling every module's clauses with module-aware functor mangling,
/// linking, and running the result through the interpreter.
///
/// <para>The default module is <c>user</c>: source without an explicit
/// <c>:- module(name).</c> directive is appended there. An explicit
/// <c>:- module(name).</c> at the top of a consult creates / replaces the
/// named module — re-consulting the same module overwrites the previous
/// contents, matching ADR-008.</para>
/// </summary>
public sealed class PrologEngine : Shumway.Builtins.IGlobalVarHost
{
    public const string DefaultModuleName = "user";

    /// <summary>Per-engine global-variable store (chunk 145) backing
    /// the SWI <c>nb_setval/2</c> / <c>nb_getval/2</c> family.
    /// Survives across queries on this engine.</summary>
    public Shumway.Builtins.GlobalVarStore GlobalVars { get; } =
        new Shumway.Builtins.GlobalVarStore();

    private readonly Dictionary<string, ModuleManifest> _modules = new()
    {
        [DefaultModuleName] = new ModuleManifest(DefaultModuleName),
    };
    // Lazily-built set of every static head functor across all modules.
    // Backs the static branch of HasPredicate / AllStaticAndDynamicFunctors
    // so those are O(1) instead of a full clause scan — predicate_property/2
    // (which Logtalk's compiler calls per goal) was O(clauses × calls) without
    // it. Nulled at every static-clause mutation (consult / restore / bundle
    // load); dynamic functors are checked live against _dynamicFunctors, so
    // assert/retract need not invalidate it.
    private HashSet<int>? _staticHeadFunctorsCache;
    // Set for the duration of a RUNTIME consult (consult/1 called from within a
    // live query). When set, source-declared dynamic clauses are also pushed
    // into the live dispatch (AppendDynamicClauseIncremental) so a call later in
    // the SAME query sees them — exactly as a runtime assertz would. Null during
    // startup/ctor consults, where the next query builds dispatch fresh.
    private Activation? _liveConsultEngine;
    private readonly OperatorTable _operators = OperatorTable.Default();

    /// <summary>Save-state chunk 264: chronological log of every source
    /// string passed to <see cref="ConsultString"/>, excluding the
    /// auto-loaded prelude (which the ctor always loads first).
    /// <see cref="SaveState"/> writes this list verbatim into a
    /// snapshot bundle; <see cref="RestoreState"/> resets the engine
    /// and replays each entry in order to rebuild the same module
    /// state.</summary>
    private readonly List<string> _consultHistory = new();

    /// <summary>Phase 24 chunk 266 — Arity-Prolog recorded database.
    /// Lazily constructed on first access so engines that never use it
    /// pay nothing.</summary>
    private RecordedDatabase? _records;
    public RecordedDatabase Records => _records ??= new RecordedDatabase();

    /// <summary>Phase 24 chunk 272 — per-engine pseudo-random generator
    /// behind <c>random/1</c>, <c>random_between/3</c> and
    /// <c>randomize/1</c>. Seedable via Randomize; defaults to a
    /// time-based seed on first access.</summary>
    private System.Random? _random;
    internal System.Random Random => _random ??= new System.Random();

    /// <summary>Replaces the per-engine random generator with one
    /// seeded by <paramref name="seed"/>. Backs <c>randomize/1</c>.</summary>
    public void Randomize(int seed) => _random = new System.Random(seed);

    /// <summary>Per-engine stream registry (chunk 140). Owns every
    /// open stream, the alias map, and the current-input /
    /// current-output cursors. Lazily built on first access so an
    /// engine that never touches streams pays nothing.</summary>
    private StreamRegistry? _streams;
    internal StreamRegistry Streams =>
        _streams ??= new StreamRegistry(Out);

    /// <summary>Activation-wide mutable flag state (chunk 58). Builtins
    /// <c>set_prolog_flag/2</c> and <c>current_prolog_flag/2</c> read
    /// and write here. The parser instances created during ConsultString
    /// and SetupQuery receive the same instance by reference, so a
    /// <c>:- set_prolog_flag(double_quotes, codes).</c> directive at the
    /// top of a source affects every subsequent parse of that source
    /// and every query made against this engine.</summary>
    private readonly PrologFlags _flags = new();

    /// <summary>Diagnostic accessor for the flag state (chunk 58). Tests
    /// can read <see cref="PrologFlags.DoubleQuotes"/> through this to
    /// verify <c>set_prolog_flag</c> took effect, but mutations should
    /// go through the builtin or a future host-side API.</summary>
    public PrologFlags Flags => _flags;

    /// <summary>Runtime store for clauses added via <c>assertz/1</c> /
    /// <c>asserta/1</c>. Keyed by functor id; the value is the ordered list
    /// of clauses (in source / assertion order). Merged with each module's
    /// static clauses at query-compile time so subsequent queries see every
    /// asserted clause. Mutations made during an in-flight query are NOT
    /// visible to that query — they take effect on the next compilation.</summary>
    private readonly Dictionary<int, List<Clause>> _dynamicClauses = new();

    /// <summary>ADR-034 — dynamic functor ids mutated at any point in this
    /// host's lifetime. Shared BY REFERENCE into every per-query
    /// <see cref="Activation"/> (see <see cref="Activation.MutatedDynamicFids"/>) so
    /// baked clause-entry staleness tests see a mid-query mutation
    /// immediately. Grows monotonically; never cleared (a caller whose IL
    /// embeds a stale snapshot must stay on its fallback path for the rest of
    /// the process — re-linking or re-promotion is the way back).</summary>
    private readonly HashSet<int> _mutatedDynamicFids = new();

    // ----- Heap-buffer pool (one slot) -----
    //
    // A big query grows the activation heap by doubling (alloc + copy per
    // step: a 300M-cell peak from the 64K initial = 13 reallocations copying
    // ~2 GB), and the fully-grown buffer died with the activation — so
    // REPEATING the query re-paid the whole ladder, and the dead 4 GB array
    // lingered as GC-retained LOH. The host now recycles the buffer across
    // activations: at most ONE pooled buffer (overlapping activations — a
    // suspended QueryAll plus a nested query — allocate fresh as before),
    // handed to the next activation at setup, taken back when an activation's
    // solution enumeration dies.
    //
    // Retention policy (user-designed): a decayed usage peak.
    //   recentUse = max(thisActivationUse, recentUse / 2)   per death
    // The buffer is kept only while its capacity ≤ max(4 × recentUse, floor):
    // repeating big queries keeps it hot; a big spike followed by small
    // queries halves the peak each query and the oversized buffer is dropped
    // (geometric descent — a returning big query pays one ladder, once).
    // Trimming at the boundary is free: the heap is logically empty, so
    // "trim" is just not keeping the reference.
    private Shumway.Core.Cell[]? _pooledHeap;
    private long _recentHeapUseCells;
    private const long PooledHeapFloorCells = 1L << 20;   // 8 MB — always OK to keep

    /// <summary>Hands the pooled heap buffer (if any) to a fresh activation.
    /// Must run before query setup materializes anything onto the heap.</summary>
    private void AdoptPooledHeap(Activation activation)
    {
        var buffer = _pooledHeap;
        if (buffer is null) return;
        _pooledHeap = null;
        activation.AdoptHeapBuffer(buffer);
    }

    /// <summary>Takes a dead activation's heap buffer back into the pool,
    /// applying the decayed-peak retention policy. CellsAllocated (cumulative
    /// allocations, capped at capacity) proxies the activation's usage.</summary>
    private void ReturnHeapBuffer(Activation activation)
    {
        var buffer = activation.DetachHeapBuffer();
        long used = Math.Min(activation.CellsAllocated, buffer.LongLength);
        _recentHeapUseCells = Math.Max(used, _recentHeapUseCells / 2);
        long keepCap = Math.Max(4 * _recentHeapUseCells, PooledHeapFloorCells);
        if (buffer.LongLength > keepCap) return;   // decayed workload no longer justifies it
        if (_pooledHeap is null || _pooledHeap.Length < buffer.Length)
            _pooledHeap = buffer;
    }

    /// <summary>Test hook — the pooled buffer's capacity in cells (0 = empty).</summary>
    internal long PooledHeapCapacityCells => _pooledHeap?.LongLength ?? 0;

    /// <summary>ADR-015 chunk C step 4: per-dynamic-functor chain state
    /// — one entry per clause currently in <see cref="_dynamicClauses"/>,
    /// in the same order, carrying the absolute byte position of the
    /// clause's <c>check_visible</c> died-slot in
    /// <see cref="Activation.CurrentProgram"/>. <c>retract</c> patches the
    /// 8-byte died slot in place; the next call's
    /// <c>check_visible</c> sees the new value and skips the clause.
    /// Populated after every query setup / dynamic-predicate
    /// recompile.</summary>
    private sealed class DynChainEntry
    {
        public readonly Clause Clause;
        /// <summary>Absolute byte position of this clause's
        /// <c>check_visible</c> died slot (8 bytes). <c>retract</c>
        /// patches it in place to mark the clause logically gone.</summary>
        public int DiedOperandAddr;
        /// <summary>Absolute byte position of this clause's chain
        /// instruction's <c>&lt;next&gt;</c> address operand (4 bytes) —
        /// where its <c>try_me_else</c> or <c>retry_me_else</c> says to
        /// jump on backtrack. The last clause's points at the fail-stub;
        /// <c>assertz</c> will patch the previous-last's slot in place to
        /// link a freshly compiled clause into the chain. -1 when the
        /// clause has no chain instruction in front of it (paso-3 single-
        /// clause emission without a fail-stub address — those predicates
        /// fall back to the chunk-C recompile path).</summary>
        public int NextOperandAddr;
        /// <summary>The absolute start of this clause's bytecode chunk
        /// in the program buffer (the chain-instruction address) and
        /// the chunk's total length. Tracked so the chunk-150 chain
        /// GC can reclaim the bytes of dead clauses into a per-engine
        /// free-list for reuse by subsequent <c>assertz</c> /
        /// <c>asserta</c>.</summary>
        public int ChunkAddr;
        public int ChunkLength;
        public DynChainEntry(Clause c, int died, int next, int chunkAddr, int chunkLength)
        {
            Clause = c;
            DiedOperandAddr = died;
            NextOperandAddr = next;
            ChunkAddr = chunkAddr;
            ChunkLength = chunkLength;
        }
    }
    private sealed class DynChainState
    {
        public readonly List<DynChainEntry> Entries = new();
        /// <summary>Absolute byte position of the operand at the bytecode
        /// tail of this predicate's chain — the <c>&lt;next&gt;</c> of
        /// either the latest-appended clause's <c>retry_me_else</c>, or
        /// (for never-asserted dynamic predicates) the empty-stub clause's
        /// <c>try_me_else</c>. <c>assertz</c> writes the new clause's
        /// chunk address here to link it onto the chain, then updates
        /// this to point at the new clause's own <c>&lt;next&gt;</c>
        /// operand. -1 when the predicate's last instruction is
        /// <c>trust_me</c> (paso-3 emission without a fail-stub) — those
        /// predicates fall back to the chunk-C recompile path.</summary>
        public int TailNextAddr = -1;
        /// <summary>Absolute byte position of the trampoline's
        /// <c>execute &lt;chain-head&gt;</c> operand — the 4-byte address
        /// asserta patches to install a new chain head. -1 when there is
        /// no trampoline.</summary>
        public int TrampolineExecuteOperandAddr = -1;
        /// <summary>Absolute byte position of the current chain head's
        /// chain instruction (a <c>try_me_else</c>). asserta in-place
        /// rewrites this byte from <c>try_me_else</c> (9 bytes) to
        /// <c>retry_me_else &lt;same-next&gt;</c> (5 bytes) + 4 nops,
        /// demoting the old head into a regular middle clause without
        /// shifting anything.</summary>
        public int HeadClauseAddr = -1;

        /// <summary>Chunk-150 free-list staging: the bytecode regions
        /// of clauses that have been retracted or abolished and so are
        /// candidates for the engine-wide free list. Populated by
        /// <c>retract</c> / <c>abolish</c>; drained by
        /// <c>garbage_collect_clauses</c> into the engine-wide
        /// free-list (chunk 151b: persistent across queries) where the
        /// next <c>assertz</c> / <c>asserta</c> can reuse the bytes
        /// instead of extending the program buffer.</summary>
        public readonly List<(int Addr, int Length)> DeadChunks = new();
    }
    /// <summary>Phase 33 (Logtalk bring-up) — dynamic-chain metadata,
    /// one table PER persistent buffer. Chain state records absolute byte
    /// offsets into one specific buffer, but nested queries (a deferred
    /// <c>:- initialization</c> goal running QueryAll from inside a
    /// mid-query consult — Logtalk's loader files nest these many levels
    /// deep) rebuild the persistent buffer while OUTER engines are still
    /// executing on their older buffers. A single host-level table would
    /// describe only the newest buffer — a resumed outer engine's assertz
    /// would then patch new-buffer offsets into its old buffer (the
    /// 0xCF / ArgumentOutOfRange corruption family), or, with the
    /// staleness guards, skip the patch and lose same-query visibility of
    /// its own assert (the Logtalk conditional-compilation failure). So
    /// each Activation is associated at query setup with the table describing
    /// ITS buffer, and every in-place mutation resolves chain state
    /// through the engine performing it. The free-chunk list rides along
    /// because its addresses are equally buffer-relative. SWI / GProlog
    /// sidestep all of this with a single mutable code space; the
    /// per-engine table is the equivalent alignment for our
    /// snapshot-per-query model, with <see cref="_dynamicClauses"/> as
    /// the authoritative store bridging buffer generations.</summary>
    private sealed class DynChainTable
    {
        public readonly Dictionary<int, DynChainState> Chains = new();

        /// <summary>Chunk-150 free-list of dead-clause bytecode regions
        /// in THIS table's buffer. <c>garbage_collect_clauses</c> moves a
        /// predicate's <see cref="DynChainState.DeadChunks"/> here; the
        /// next <c>assertz</c> / <c>asserta</c> scans for a fit
        /// (first-fit) and reuses the bytes instead of extending the
        /// program buffer via <c>engine.AppendCode</c>.</summary>
        public readonly List<(int Addr, int Length)> FreeChunks = new();

        /// <summary>Phase 33 (Logtalk bring-up) — live-linked static
        /// predicates' unresolved call sites (absolute operand position in
        /// THIS table's buffer, callee fid), accumulated across the consult
        /// batches linked into it so a forward reference (batch N calls a
        /// predicate a later batch M&gt;N defines) is re-patched once M
        /// links. Per-buffer because the positions are buffer offsets —
        /// with the consult broadcast, batches land in several engines'
        /// buffers, each with its own positions.</summary>
        public List<(int AbsPos, int FunctorId)>? LiveConsultUnresolved;
    }

    /// <summary>The table describing the host's CURRENT persistent buffer
    /// (<see cref="_persistentProgram"/>). Reused across queries while the
    /// buffer is (chunk 151b); replaced whenever the buffer is rebuilt or
    /// invalidated.</summary>
    private DynChainTable _dynChainTable = new();

    /// <summary>Activation → the chain table for the buffer that engine runs
    /// on. Registered at query setup; weak so a finished query's engine
    /// (and, once unshared, its table) is collectable.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Activation, DynChainTable>
        _engineChainTables = new();

    private DynChainTable? GetChainTable(Activation engine)
        => _engineChainTables.TryGetValue(engine, out var t) ? t : null;

    private DynChainTable GetOrCreateChainTable(Activation engine)
        => _engineChainTables.GetValue(engine, static _ => new DynChainTable());

    /// <summary>Phase 33 — every engine born via SetupQueryFromTerm, weakly
    /// held (in birth order). Backs the dynamic-mutation BROADCAST: with
    /// nested queries (Logtalk's deferred <c>:- initialization</c> chains)
    /// several engines are suspended mid-execution at once, each on its own
    /// buffer; a mutation applied only to the mutating engine's buffer is
    /// invisible to the others when they resume (a parent never sees a
    /// child's <c>$lgt_loaded_file_</c> assert; a child baked a
    /// <c>$lgt_file_loading_stack_</c> entry the parent later retracts).
    /// SWI/GProlog get this for free from their single mutable code space;
    /// broadcasting to every live engine's buffer is the equivalent for our
    /// snapshot-per-query model. Collected engines are pruned as the list
    /// is walked; finished-but-uncollected engines receive harmless writes
    /// (and keep a to-be-reused buffer current).</summary>
    private readonly List<WeakReference<Activation>> _liveEngines = new();

    private void RegisterLiveEngine(Activation engine)
    {
        for (int i = _liveEngines.Count - 1; i >= 0; i--)
            if (!_liveEngines[i].TryGetTarget(out _)) _liveEngines.RemoveAt(i);
        _liveEngines.Add(new WeakReference<Activation>(engine));
    }

    /// <summary>Live engines OTHER than <paramref name="except"/>, at most
    /// one per distinct chain table (two engines sharing a reused buffer
    /// share its table — the mutation must be applied once). Newest first.
    /// Returns null instead of an empty list on the common single-engine
    /// path so callers skip broadcast work entirely.</summary>
    private List<Activation>? OtherLiveEnginesByTable(Activation except)
    {
        List<Activation>? result = null;
        var seen = new HashSet<DynChainTable>();
        if (GetChainTable(except) is { } exceptTable) seen.Add(exceptTable);
        for (int i = _liveEngines.Count - 1; i >= 0; i--)
        {
            if (!_liveEngines[i].TryGetTarget(out var e))
            {
                _liveEngines.RemoveAt(i);
                continue;
            }
            if (ReferenceEquals(e, except)) continue;
            if (GetChainTable(e) is not { } t || !seen.Add(t)) continue;
            (result ??= new List<Activation>()).Add(e);
        }
        return result;
    }

    /// <summary>True when <paramref name="engine"/>'s program buffer IS
    /// the host's current persistent buffer — i.e. no nested query rebuilt
    /// it since this engine's setup. Capture BEFORE any
    /// <c>engine.AppendCode</c> (growth reallocation changes the engine's
    /// reference; for an owner the post-growth sync keeps host and engine
    /// aligned). A mutation by a non-owner engine still patches the
    /// engine's own buffer (self-visibility — the Logtalk compiler must
    /// see its own asserts), but the host buffer misses it, so the caller
    /// must invalidate instead of syncing.</summary>
    private bool EngineOwnsHostBuffer(Activation engine)
        => engine.CurrentProgram is not null
           && ReferenceEquals(engine.CurrentProgram, _persistentProgram);

    /// <summary>Post-mutation buffer bookkeeping: an owner engine's growth
    /// is synced back to the host (chunk 151b); a non-owner (stale) engine
    /// instead marks the host buffer for rebuild — the store already holds
    /// the mutation, and the next setup re-derives dispatch from it.</summary>
    private void SyncOrInvalidateAfterMutation(Activation engine, bool ownedHostBuffer)
    {
        if (ownedHostBuffer) SyncPersistentFromEngine(engine);
        else InvalidatePersistent();
    }

    /// <summary>Chunk 151b — synchronises <see cref="_persistentProgram"/>
    /// back from the running engine after a mid-query
    /// <see cref="Activation.AppendCode"/> may have reallocated and grown
    /// the buffer. PrologEngine holds its own reference to the buffer
    /// for the next query's two-buffer view; without this, that
    /// reference would be left pointing at the pre-grow stale buffer.
    /// Only valid for an engine that owns the host buffer — non-owner
    /// callers go through <see cref="SyncOrInvalidateAfterMutation"/>.
    /// </summary>
    private void SyncPersistentFromEngine(Activation engine)
    {
        if (engine.CurrentProgram is null) return;
        _persistentProgram = engine.CurrentProgram;
        _persistentLength = engine.ProgramLength;
    }

    /// <summary>Root fix for the suspended-activation stale-append-position
    /// corruption. An activation suspended mid-enumeration while ANOTHER
    /// activation extended the shared persistent buffer still believes the
    /// content length from its own setup — its next <c>AppendCode</c> would
    /// land ON the newer live entries and overwrite them; the tail patch
    /// that follows then writes the new entry's own address into its own
    /// <c>&lt;next&gt;</c> operand (the self-pointing <c>retry_me_else</c>
    /// observed as an unbreakable dispatch/walk cycle). Every in-place
    /// mutation entry point brings an owner's append position forward to
    /// the host's synced persistent length first; appends then extend
    /// instead of overwriting, and the in-place fast path stays valid.
    /// Non-owners (buffer since rebuilt) are untouched — their mutations
    /// already invalidate the host buffer.</summary>
    private void ResyncOwnerAppendPosition(Activation engine)
    {
        if (EngineOwnsHostBuffer(engine) && engine.ProgramLength < _persistentLength)
            engine.SetInitialProgramLength(_persistentLength);
    }

    /// <summary>Corruption tripwire — an in-place dynamic-chain patch was
    /// about to write through addresses that don't fit the live buffer's
    /// content (a stale chain record, a stale append position, or a reused
    /// chunk swallowing a live slot). Writing would splice the chain into
    /// itself — the self-pointing <c>retry_me_else</c> observed as an
    /// unbreakable dispatch/walk cycle. Report loudly (this is a
    /// should-never-happen event worth a bug report) and rebuild the
    /// predicate's dispatch from the clause store, which is authoritative
    /// — the running query then sees the correct clause set.</summary>
    private void ChainCorruptionRecover(
        string site, Activation engine, int functorId, string detail)
    {
        var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(functorId);
        Console.Error.WriteLine(
            $"shumway: dynamic-chain tripwire at {site} for "
            + $"{Shumway.Core.AtomTable.GetById(atomId)?.Name}/{arity} ({detail}); "
            + "rebuilding the predicate's dispatch from the clause store.");
        if (_inFidViewRebuild) return;   // already repairing this view
        InvalidatePersistent();
        RebuildEngineFidChainView(engine, functorId);
    }

    // ====================================================================
    // Chunk 155b — runtime in-place extension of extensible-indexed
    // dynamic predicates (chunk 155a layout).
    // ====================================================================

    /// <summary>Chunk 155b — returns <c>true</c> iff the predicate
    /// <paramref name="functorId"/>'s live dispatch is the chunk-155a
    /// extensible-indexed layout (<c>enter_dynamic</c> +
    /// <c>switch_on_term</c> + <c>try_me_else</c>-headed bucket
    /// chains). Walks the predicate's entry to distinguish:
    /// chunk-155a chains begin with <c>try_me_else</c> (patchable
    /// <c>&lt;next&gt;</c> operand) whereas chunk-154's contiguous
    /// indexed layout begins with <c>try</c>.</summary>
    /// <summary>Chunk 156 — walks through the multi-level cascade
    /// from the predicate's <c>switch_on_term</c> var label,
    /// descending through any number of <c>switch_on_arg</c> level
    /// switches, until it reaches the final chain head (the chain
    /// that enumerates EVERY clause regardless of indexable args).
    /// Returns -1 if the layout doesn't match.</summary>
    private int FindFinalVarChainHead(Activation engine, int predAddr)
    {
        var prog = engine.CurrentProgram;
        if (prog is null || predAddr + 18 > prog.Length) return -1;
        int p = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 2);
        while (p > 0 && p + 1 <= prog.Length)
        {
            var op = (Shumway.Core.Opcode)prog[p];
            if (op == Shumway.Core.Opcode.TryMeElse) return p;
            if (op == Shumway.Core.Opcode.RetryMeElse
                && p + 6 <= prog.Length
                && prog[p + 5] == (byte)Shumway.Core.Opcode.Nop) return p;
            if (op != Shumway.Core.Opcode.SwitchOnArg) return -1;
            if (p + 9 > prog.Length) return -1;
            p = Shumway.Core.BytecodeIO.ReadInt32(prog, p + 5);
        }
        return -1;
    }

    private bool IsExtensibleIndexedLayout(Activation engine, int functorId)
    {
        var addrMap = engine.CurrentFunctorAddresses;
        if (addrMap is null) return false;
        if (!addrMap.TryGetValue(functorId, out int predAddr)) return false;
        var prog = engine.CurrentProgram;
        if (prog is null || predAddr + 18 > prog.Length) return false;
        if (prog[predAddr] != (byte)Shumway.Core.Opcode.EnterDynamic) return false;
        if (prog[predAddr + 1] != (byte)Shumway.Core.Opcode.SwitchOnTerm) return false;
        // Chunk 156: for single-arg the var label points at a
        // try_me_else chain head; for multi-arg it points at the
        // next level's switch_on_arg (or, after layers of cascade,
        // eventually at a chain head). Walk through switch_on_arg
        // nodes until we reach a try_me_else chain head; that's
        // the signature of every chunk-155/156 indexed layout.
        int varLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 2);
        while (varLbl > 0 && varLbl + 1 <= prog.Length)
        {
            var op = (Shumway.Core.Opcode)prog[varLbl];
            if (op == Shumway.Core.Opcode.TryMeElse) return true;
            if (op == Shumway.Core.Opcode.RetryMeElse
                && varLbl + 6 <= prog.Length
                && prog[varLbl + 5] == (byte)Shumway.Core.Opcode.Nop) return true;
            if (op != Shumway.Core.Opcode.SwitchOnArg) return false;
            // switch_on_arg's var label is at +5 (skip the opcode
            // byte and the 4-byte arg_idx).
            if (varLbl + 9 > prog.Length) return false;
            varLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, varLbl + 5);
        }
        return false;
    }

    /// <summary>Chunk 155b — walks the chain starting at
    /// <paramref name="chainHead"/>, following <c>&lt;next&gt;</c>
    /// operands until the entry whose <c>&lt;next&gt;</c> is the
    /// absolute <see cref="Activation.DynamicFailStubAddr"/>. Returns the
    /// absolute byte offset of that tail entry's <c>&lt;next&gt;</c>
    /// operand (where the assertz extension patches), or <c>-1</c>
    /// on any malformed chain.</summary>
    private static int WalkChainToTailNextOperand(
        byte[] prog, int chainHead, int failStubAddr)
    {
        int cur = chainHead;
        bool isHead = true;
        // A chain instruction is ≥5 bytes, so a well-formed chain has at
        // most prog.Length/5 distinct entries — more steps than that means
        // the <next> operands form a cycle (corrupted buffer). Returning -1
        // sends the caller to the rebuild fallback instead of spinning.
        int stepsLeft = prog.Length / 5 + 1;
        while (true)
        {
            if (cur < 0 || cur + 5 > prog.Length || --stepsLeft < 0) return -1;
            var op = (Shumway.Core.Opcode)prog[cur];
            if (isHead)
            {
                if (op != Shumway.Core.Opcode.TryMeElse) return -1;
            }
            else
            {
                if (op != Shumway.Core.Opcode.RetryMeElse) return -1;
            }
            int next = Shumway.Core.BytecodeIO.ReadInt32(prog, cur + 1);
            if (next == failStubAddr) return cur + 1;
            cur = next;
            isHead = false;
        }
    }

    /// <summary>Chunk 155f — returns <c>true</c> when the chain
    /// entry at <paramref name="entryAddr"/> sits in a 9-byte
    /// chain-instruction slot (the original try_me_else footprint,
    /// possibly demoted to retry_me_else + 4 nops by an asserta).
    /// In that case the entry's check_visible / execute live at the
    /// head offsets (+9 / +26); for a native non-head 5-byte slot
    /// they live at +5 / +22. The distinguisher is the byte at
    /// entry+5: <c>Nop</c> for a demoted-head slot, anything else
    /// (start of check_visible) for a native non-head.</summary>
    private static int ChainEntryHeaderSize(byte[] prog, int entryAddr)
    {
        // try_me_else (9-byte) head: opcode TryMeElse.
        if (entryAddr + 1 <= prog.Length
            && prog[entryAddr] == (byte)Shumway.Core.Opcode.TryMeElse)
            return 9;
        // retry_me_else: 5-byte native OR 9-byte demoted-from-head.
        // Demoted has Nop at offset +5; native has CheckVisible.
        if (entryAddr + 6 <= prog.Length
            && prog[entryAddr + 5] == (byte)Shumway.Core.Opcode.Nop)
            return 9;
        return 5;
    }

    /// <summary>Chunk 155b — given the predicate entry and the new
    /// clause's arg-0 classification, locate the bucket chain head
    /// the new clause should be appended to. Returns <c>-1</c> when
    /// no bucket exists (new key) or the arg is var (every bucket
    /// would need to extend). Both cases are deferred to the
    /// persistent-rebuild fallback by the caller.</summary>
    private static int FindBucketChainHead(
        Activation engine, int predAddr, Shumway.Compiler.Ast.Clause newClause)
    {
        var prog = engine.CurrentProgram!;
        // The switch_on_term sits at predAddr + 1; its operands at
        // +2 (var), +6 (const), +10 (list), +14 (struct).
        int constLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 6);
        int listLbl  = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 10);
        int structLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 14);
        int varLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 2);

        // Pull arg-0 of the new clause's head.
        Shumway.Compiler.Ast.Term head = newClause.Kind == Shumway.Compiler.Ast.ClauseKind.Rule
            ? ((Shumway.Compiler.Ast.CompoundTerm)newClause.Term).Args[0]
            : newClause.Term;
        if (head is not Shumway.Compiler.Ast.CompoundTerm headComp || headComp.Args.Length == 0)
            return -1;
        var arg0 = headComp.Args[0];

        // Walk the const-label cascade for level 0 only. The cascade
        // is atom → integer → structure within one level; on multi-
        // arg (chunk 156) the cascade's last default points at the
        // NEXT LEVEL's switch_on_arg, which marks the level boundary
        // and stops the walk — arg-0's bucket is in level 0 only,
        // higher-level buckets are routed through different chain
        // extensions.
        int CascadeLookup(int startAddr, int key, Shumway.Core.Opcode wantedOpcode)
        {
            int p = startAddr;
            while (p > 0 && p + 5 <= prog.Length)
            {
                var op = (Shumway.Core.Opcode)prog[p];
                // SwitchOnArg marks the boundary between level 0 and
                // the next indexable level — stop here.
                if (op == Shumway.Core.Opcode.SwitchOnArg) return -1;
                if (op != Shumway.Core.Opcode.SwitchOnAtom
                    && op != Shumway.Core.Opcode.SwitchOnInteger
                    && op != Shumway.Core.Opcode.SwitchOnStructure) return -1;
                int tableId = Shumway.Core.BytecodeIO.ReadInt32(prog, p + 1);
                var table = engine.GetSwitchTable(tableId);
                if (table is null) return -1;
                if (op == wantedOpcode)
                {
                    int target = table.Lookup(key);
                    return target == table.DefaultAddress ? -1 : target;
                }
                p = table.DefaultAddress;
                if (p == varLbl) return -1;
            }
            return -1;
        }

        switch (arg0)
        {
            case Shumway.Compiler.Ast.AtomTerm a:
                int atomId = Shumway.Core.AtomTable.Intern(a.Name, permanent: true).Id;
                return CascadeLookup(constLbl, atomId, Shumway.Core.Opcode.SwitchOnAtom);
            case Shumway.Compiler.Ast.IntTerm n
                when n.Value >= int.MinValue && n.Value <= int.MaxValue:
                return CascadeLookup(constLbl, (int)n.Value, Shumway.Core.Opcode.SwitchOnInteger);
            case Shumway.Compiler.Ast.CompoundTerm c when c.Functor == "." && c.Args.Length == 2:
                return listLbl == varLbl ? -1 : listLbl;
            case Shumway.Compiler.Ast.CompoundTerm c:
                int functorId = Shumway.Core.FunctorTable.Intern(
                    Shumway.Core.AtomTable.Intern(c.Functor, permanent: true).Id, c.Args.Length);
                // struct cascade: structLbl points at switch_on_structure.
                if (structLbl <= 0 || structLbl == varLbl) return -1;
                if (prog[structLbl] != (byte)Shumway.Core.Opcode.SwitchOnStructure) return -1;
                int sTableId = Shumway.Core.BytecodeIO.ReadInt32(prog, structLbl + 1);
                var sTable = engine.GetSwitchTable(sTableId);
                if (sTable is null) return -1;
                int sTarget = sTable.Lookup(functorId);
                return sTarget == sTable.DefaultAddress ? -1 : sTarget;
            default:
                return -1;
        }
    }

    /// <summary>Chunk 155b/c — in-place extension of an extensible-
    /// indexed dynamic predicate's chains for an <c>assertz</c>.
    /// Compiles the new clause body, appends it to the buffer, then
    /// either (155b) extends the bucket chain for the new clause's
    /// arg-0 key when that key has an existing bucket, or (155c)
    /// creates a brand-new bucket chain at the end of the buffer
    /// and adds the new (key → chain-head) entry to the appropriate
    /// sub-switch table when the key is new. The var-fallthrough
    /// chain is always extended.
    ///
    /// Returns <c>false</c> if the predicate isn't using the chunk-
    /// 155a layout, the new clause is var-arg-at-0 (would need to
    /// extend every bucket — a chunk-155d concern), or the new key
    /// needs a sub-switch that doesn't exist yet (e.g. first int
    /// assertz to an atom-only predicate). In any of those cases
    /// the caller falls back to rebuild.</summary>
    private bool TryAppendToIndexedDynamic(
        Activation engine, int functorId, Shumway.Compiler.Ast.Clause newClause)
    {
        if (!IsExtensibleIndexedLayout(engine, functorId)) return false;
        // Phase 33 — capture buffer ownership before any AppendCode.
        bool ownsHost = EngineOwnsHostBuffer(engine);
        var addrMap = engine.CurrentFunctorAddresses!;
        var prog = engine.CurrentProgram!;
        int predAddr = addrMap[functorId];
        int failStub = engine.DynamicFailStubAddr;
        if (failStub <= 0) return false;

        // Pull the new clause's head arg-0 — we classify it to decide
        // the same-key vs new-key vs var-arg path.
        Shumway.Compiler.Ast.Term head = newClause.Kind == Shumway.Compiler.Ast.ClauseKind.Rule
            ? ((Shumway.Compiler.Ast.CompoundTerm)newClause.Term).Args[0]
            : newClause.Term;
        if (head is not Shumway.Compiler.Ast.CompoundTerm headComp || headComp.Args.Length == 0)
            return false;
        var arg0 = headComp.Args[0];
        bool isVarArg = arg0 is Shumway.Compiler.Ast.VarTerm;

        // Plan which chain tails the new clause's body will be
        // linked into. var-arg-at-0 (chunk 155e) extends every
        // chain — var fallthrough, list bucket if present, and
        // every bucket chain reachable through the atom / integer /
        // structure sub-switch tables. Concrete arg-0 (chunks
        // 155b / 155c) extends just (var chain + the specific
        // bucket chain, creating it for new-key).
        int bucketChainHead = -1;
        bool isNewKey = false;
        // Chunk 156: for multi-arg layouts, the var slot at
        // predAddr+2 points at the next level's switch_on_arg, not
        // at the final var chain. Walk through the level cascade to
        // reach the actual chain head.
        int varChainHead = FindFinalVarChainHead(engine, predAddr);
        if (varChainHead < 0) return false;
        var chainTailNexts = new List<int>();
        int newKeyValue = 0;
        int subSwitchTableId = -1;

        if (isVarArg)
        {
            // Collect every chain's tail-next operand. Includes the
            // var chain itself so we don't double-extend below.
            if (!CollectAllChainTailNextOperands(engine, predAddr, chainTailNexts))
                return false;
        }
        else
        {
            int varTailNext = WalkChainToTailNextOperand(prog, varChainHead, failStub);
            if (varTailNext < 0) return false;
            chainTailNexts.Add(varTailNext);
            bucketChainHead = FindBucketChainHead(engine, predAddr, newClause);
            isNewKey = bucketChainHead < 0;
            if (!isNewKey)
            {
                int bucketTailNext = WalkChainToTailNextOperand(prog, bucketChainHead, failStub);
                if (bucketTailNext < 0) return false;
                chainTailNexts.Add(bucketTailNext);
            }
            else
            {
                // Locate the sub-switch matching the new key's type;
                // bail to rebuild if the type's sub-switch doesn't
                // exist yet (would be a layout change).
                if (!TryLocateSubSwitchForArg(
                        engine, predAddr, arg0, out newKeyValue, out subSwitchTableId))
                    return false;
            }
        }

        // Corruption tripwire — every tail slot we're about to patch must
        // lie inside this activation's believed content length. A slot at
        // or beyond ProgramLength means the chain in the shared buffer
        // extends past this activation's append position (a stale
        // ProgramLength): AppendCode would OVERWRITE those live entries and
        // the tail patch would then write the new entry's own address into
        // its own <next> operand — the self-pointing retry_me_else cycle.
        // Rebuild from the store instead of writing the corruption.
        foreach (int tn in chainTailNexts)
            if (tn + sizeof(int) > engine.ProgramLength)
            {
                ChainCorruptionRecover(
                    "assertz-indexed", engine, functorId,
                    $"tail slot {tn} beyond content length {engine.ProgramLength}");
                return true;   // the rebuild absorbed the store (new clause included)
            }

        // Compile the new clause (transforms identical to the chain
        // path; chunk 427 — shared helper with a fact fast path).
        var compiledClause = CompileRuntimeAssertClause(engine, functorId, newClause);
        if (compiledClause is null) return false;

        // Body chunk: [meta(dbg, 0)] + body bytes. Clause-source-position
        // index is irrelevant for runtime-asserted clauses; the dbg marker is
        // gated on the compile_mode flag (release omits it — the interpreter
        // never dispatches a no-op on entry).
        var bodyEmitter = new Shumway.Compiler.Wam.BytecodeEmitter();
        if (_flags.EmitDebugInfo) bodyEmitter.EmitMetaDbgInfo(0);
        int bodyContentLocalStart = bodyEmitter.Position;
        bodyEmitter.AppendBytes(compiledClause.Bytecode);
        byte[] bodyChunk = bodyEmitter.ToBytes();
        int bodyAddr = engine.AppendCode(bodyChunk);
        prog = engine.CurrentProgram!;

        // Patch call sites inside the body to absolute targets.
        foreach (var site in compiledClause.CallSites)
        {
            int operandPos = bodyAddr + bodyContentLocalStart + site.OpcodeOffset + 1;
            int target = addrMap.TryGetValue(site.CalleeFunctorId, out int addr)
                ? addr
                : Shumway.Core.CallTarget.ForUndefined(site.CalleeFunctorId);
            Shumway.Core.BytecodeIO.WriteInt32(prog, operandPos, target);
        }

        // Helper: append a non-head chain entry (retry_me_else
        // <fail_stub>; check_visible; execute <targetBody>).
        int AppendNonHeadEntry(int targetBody)
        {
            var em = new Shumway.Compiler.Wam.BytecodeEmitter();
            em.EmitRetryMeElse(failStub);
            em.EmitCheckVisible(born: _dbGeneration.Value, died: long.MaxValue);
            em.EmitExecute(targetBody);
            return engine.AppendCode(em.ToBytes());
        }

        // For the new-key concrete case, the new bucket itself is
        // built (and added to the sub-switch table) BEFORE we walk
        // the chain-tail list — the new bucket isn't in
        // chainTailNexts because it didn't exist when we planned.
        if (!isVarArg && isNewKey)
        {
            // Build a fresh bucket chain containing every var-arg
            // clause's body (they match every concrete key) plus the
            // new clause's body, then add (new_key → new_chain_head)
            // to the sub-switch table.
            var varArgBodies = CollectVarArgBodies(engine, varChainHead, functorId);
            int newBucketHead = BuildAndAppendNewBucketChain(
                engine, failStub, headArity: headComp.Args.Length,
                varArgBodies, bodyAddr);
            prog = engine.CurrentProgram!;
            var oldTable = engine.GetSwitchTable(subSwitchTableId);
            if (oldTable is null) return false;
            var newTable = oldTable.WithAdditionalEntry(newKeyValue, newBucketHead);
            engine.ReplaceSwitchTable(subSwitchTableId, newTable);
            MirrorSwitchTableIntoDynamicLink(subSwitchTableId, newTable);
        }

        // For every chain in the plan, append a new chain entry
        // pointing at the new body and patch the chain's prior tail.
        // This covers (depending on path):
        //   - chunk 155b: bucket + var (2 entries).
        //   - chunk 155c: var only (the new bucket already includes
        //     the new clause as its tail).
        //   - chunk 155e: every chain (var + list + every bucket
        //     across all sub-switches).
        foreach (int tailNext in chainTailNexts)
        {
            int newEntry = AppendNonHeadEntry(bodyAddr);
            prog = engine.CurrentProgram!;
            // Tripwire: a fresh append always lands beyond every existing
            // chain slot. newEntry at or below the slot being patched means
            // the append position was stale and the write would splice the
            // chain into itself. (The pre-append ProgramLength check above
            // makes this unreachable; keep it as the last line of defense —
            // the write below is the one that creates the dispatch cycle.)
            if (newEntry <= tailNext)
            {
                ChainCorruptionRecover(
                    "assertz-indexed-patch", engine, functorId,
                    $"new entry {newEntry} at/below tail slot {tailNext}");
                return true;
            }
            Shumway.Core.BytecodeIO.WriteInt32(prog, tailNext, newEntry);
        }

        // The new chunks may have grown the persistent buffer (chunk
        // 151b) — keep PrologEngine's cached reference current (owner),
        // or mark the host buffer for rebuild (non-owner engine: its
        // in-place extension is invisible to the newer buffer).
        SyncOrInvalidateAfterMutation(engine, ownsHost);

        // Refresh interpreter pools — the clause may have interned new
        // literals (chunk 427: skipped when the pools didn't grow).
        RefreshLiteralPoolsIfGrown(engine);
        return true;
    }

    /// <summary>Chunk 155c — locates the sub-switch
    /// (<c>switch_on_atom</c> / <c>switch_on_integer</c> /
    /// <c>switch_on_structure</c>) that handles the new clause's
    /// arg-0 type and returns its table id and the key value for the
    /// new arg. Returns <c>false</c> if the predicate doesn't yet
    /// have a sub-switch of that type — adding one would be a layout
    /// change beyond chunk 155c's scope.</summary>
    private static bool TryLocateSubSwitchForArg(
        Activation engine, int predAddr, Shumway.Compiler.Ast.Term arg0,
        out int keyValue, out int tableId)
    {
        keyValue = 0; tableId = -1;
        var prog = engine.CurrentProgram!;
        int constLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 6);
        int varLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 2);

        Shumway.Core.Opcode wantedOp;
        switch (arg0)
        {
            case Shumway.Compiler.Ast.AtomTerm a:
                wantedOp = Shumway.Core.Opcode.SwitchOnAtom;
                keyValue = Shumway.Core.AtomTable.Intern(a.Name, permanent: true).Id;
                break;
            case Shumway.Compiler.Ast.IntTerm n
                when n.Value >= int.MinValue && n.Value <= int.MaxValue:
                wantedOp = Shumway.Core.Opcode.SwitchOnInteger;
                keyValue = (int)n.Value;
                break;
            case Shumway.Compiler.Ast.CompoundTerm c when c.Functor != "." || c.Args.Length != 2:
                wantedOp = Shumway.Core.Opcode.SwitchOnStructure;
                keyValue = Shumway.Core.FunctorTable.Intern(
                    Shumway.Core.AtomTable.Intern(c.Functor, permanent: true).Id, c.Args.Length);
                break;
            default:
                return false;  // list (handled by listLbl, not a sub-switch) or unsupported
        }

        // Walk the cascade from constLbl looking for the wanted
        // sub-switch opcode. Stop at SwitchOnArg — chunk-156 multi-
        // arg layouts put a level boundary there, and the new key's
        // sub-switch must live within level 0.
        int p = constLbl;
        while (p > 0 && p + 5 <= prog.Length && p != varLbl)
        {
            var op = (Shumway.Core.Opcode)prog[p];
            if (op == Shumway.Core.Opcode.SwitchOnArg) return false;
            if (op != Shumway.Core.Opcode.SwitchOnAtom
                && op != Shumway.Core.Opcode.SwitchOnInteger
                && op != Shumway.Core.Opcode.SwitchOnStructure)
                return false;
            if (op == wantedOp)
            {
                tableId = Shumway.Core.BytecodeIO.ReadInt32(prog, p + 1);
                return true;
            }
            int tid = Shumway.Core.BytecodeIO.ReadInt32(prog, p + 1);
            var table = engine.GetSwitchTable(tid);
            if (table is null) return false;
            p = table.DefaultAddress;
        }
        return false;
    }

    /// <summary>Chunk 155c — walks the var-fallthrough chain and
    /// returns the body addresses of clauses whose arg-0 is var
    /// (so they'd be merged into every concrete bucket chain). The
    /// var chain enumerates clauses in source order, so its Nth
    /// entry's <c>execute &lt;body&gt;</c> target is the body of
    /// <c>_dynamicClauses[functorId][N]</c>; the dynamic-store
    /// clause carries the original arg-0 classification.</summary>
    private List<int> CollectVarArgBodies(Activation engine, int varChainHead, int functorId)
    {
        var result = new List<int>();
        if (!_dynamicClauses.TryGetValue(functorId, out var clauses))
            return result;
        var prog = engine.CurrentProgram!;
        int failStub = engine.DynamicFailStubAddr;
        int cur = varChainHead;
        int idx = 0;
        // Cycle guard — same bound as WalkChainToTailNextOperand: a
        // corrupted <next> cycle must terminate the walk, not hang it.
        int stepsLeft = prog.Length / 5 + 1;
        while (true)
        {
            if (cur < 0 || cur + 27 > prog.Length || --stepsLeft < 0) break;
            int chainHeaderSize = ChainEntryHeaderSize(prog, cur);
            int execOpPos = cur + chainHeaderSize + 17;
            if (execOpPos + 5 > prog.Length) break;
            if (prog[execOpPos] != (byte)Shumway.Core.Opcode.Execute) break;
            int bodyAddr = Shumway.Core.BytecodeIO.ReadInt32(prog, execOpPos + 1);
            if (idx < clauses.Count - 1 && IsVarArgAt0(clauses[idx]))
                result.Add(bodyAddr);
            idx++;
            int next = Shumway.Core.BytecodeIO.ReadInt32(prog, cur + 1);
            if (next == failStub) break;
            cur = next;
        }
        return result;
    }

    private static bool IsVarArgAt0(Shumway.Compiler.Ast.Clause c)
    {
        Shumway.Compiler.Ast.Term head = c.Kind == Shumway.Compiler.Ast.ClauseKind.Rule
            ? ((Shumway.Compiler.Ast.CompoundTerm)c.Term).Args[0]
            : c.Term;
        if (head is not Shumway.Compiler.Ast.CompoundTerm hc || hc.Args.Length == 0)
            return false;
        return hc.Args[0] is Shumway.Compiler.Ast.VarTerm;
    }

    /// <summary>Chunk 156 — recursively enumerates every chain head
    /// reachable from a switch address. Walks both single- and
    /// multi-level layouts: switch_on_term and switch_on_arg cascade
    /// through 4 child labels; switch_on_atom / _integer /
    /// _structure (and their _arg variants) recurse through every
    /// table value plus the default. Chain heads — try_me_else, or
    /// a demoted retry_me_else + Nop at +5 — are added to
    /// <paramref name="heads"/>. Recursion stops at fail-stub
    /// addresses or unrecognised opcodes.</summary>
    private void EnumerateChainHeadsRecursive(
        Activation engine, int addr, HashSet<int> heads, HashSet<int> visited)
    {
        if (addr <= 0) return;
        var prog = engine.CurrentProgram;
        if (prog is null || addr + 1 > prog.Length) return;
        if (!visited.Add(addr)) return;
        var op = (Shumway.Core.Opcode)prog[addr];
        switch (op)
        {
            case Shumway.Core.Opcode.SwitchOnTerm:
            {
                int varLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 1);
                int constLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 5);
                int listLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 9);
                int structLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 13);
                EnumerateChainHeadsRecursive(engine, varLbl, heads, visited);
                EnumerateChainHeadsRecursive(engine, constLbl, heads, visited);
                EnumerateChainHeadsRecursive(engine, listLbl, heads, visited);
                EnumerateChainHeadsRecursive(engine, structLbl, heads, visited);
                break;
            }
            case Shumway.Core.Opcode.SwitchOnArg:
            {
                int varLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 5);
                int constLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 9);
                int listLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 13);
                int structLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 17);
                EnumerateChainHeadsRecursive(engine, varLbl, heads, visited);
                EnumerateChainHeadsRecursive(engine, constLbl, heads, visited);
                EnumerateChainHeadsRecursive(engine, listLbl, heads, visited);
                EnumerateChainHeadsRecursive(engine, structLbl, heads, visited);
                break;
            }
            case Shumway.Core.Opcode.SwitchOnAtom:
            case Shumway.Core.Opcode.SwitchOnInteger:
            case Shumway.Core.Opcode.SwitchOnStructure:
            {
                int tid = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 1);
                var table = engine.GetSwitchTable(tid);
                if (table is null) break;
                foreach (int v in table.Values)
                    EnumerateChainHeadsRecursive(engine, v, heads, visited);
                EnumerateChainHeadsRecursive(engine, table.DefaultAddress, heads, visited);
                break;
            }
            case Shumway.Core.Opcode.SwitchOnAtomArg:
            case Shumway.Core.Opcode.SwitchOnIntegerArg:
            case Shumway.Core.Opcode.SwitchOnStructureArg:
            {
                // arg_idx at +1, table_id at +5.
                int tid = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 5);
                var table = engine.GetSwitchTable(tid);
                if (table is null) break;
                foreach (int v in table.Values)
                    EnumerateChainHeadsRecursive(engine, v, heads, visited);
                EnumerateChainHeadsRecursive(engine, table.DefaultAddress, heads, visited);
                break;
            }
            case Shumway.Core.Opcode.TryMeElse:
                heads.Add(addr);
                break;
            case Shumway.Core.Opcode.RetryMeElse:
                // Demoted chain head (after asserta) — has Nop at +5.
                if (addr + 6 <= prog.Length
                    && prog[addr + 5] == (byte)Shumway.Core.Opcode.Nop)
                    heads.Add(addr);
                break;
            // fail_stub or anything else: stop.
        }
    }

    /// <summary>Chunk 155e/156 — collects every chain's tail-next
    /// operand, used by the var-arg-at-0 / multi-arg extension paths
    /// that need to append a new entry to every chain. Builds on
    /// <see cref="EnumerateChainHeadsRecursive"/> so multi-level
    /// layouts (chunk-156) are fully covered.</summary>
    private bool CollectAllChainTailNextOperands(
        Activation engine, int predAddr, List<int> tailNextOperands)
    {
        var prog = engine.CurrentProgram;
        if (prog is null) return false;
        int failStub = engine.DynamicFailStubAddr;
        if (failStub <= 0) return false;
        var heads = new HashSet<int>();
        var visited = new HashSet<int>();
        // predAddr+1 is the top-level switch_on_term / switch_on_arg.
        EnumerateChainHeadsRecursive(engine, predAddr + 1, heads, visited);
        foreach (int head in heads)
        {
            int tailNext = WalkChainToTailNextOperand(prog, head, failStub);
            if (tailNext < 0) return false;
            tailNextOperands.Add(tailNext);
        }
        return true;
    }

    // ====================================================================
    // Chunk 155f — in-place asserta for extensible-indexed dynamic
    // predicates.
    // ====================================================================

    /// <summary>Chunk 155f — prepends a clause to a chunk-155a
    /// indexed predicate in place. Asserta is harder than assertz
    /// because the chain head's address changes (the new entry
    /// becomes the head, the old head gets demoted to a non-head),
    /// and the old head was referenced from external pointer slots
    /// — switch_on_term operands, switch_on_atom/integer/structure
    /// table values, sub-switch default-cascade addresses. Each of
    /// those slots that pointed at an old head gets redirected to
    /// the new head; the demoted old head's <c>&lt;next&gt;</c>
    /// operand is unchanged (still references the second entry in
    /// the chain or fail_stub).
    ///
    /// Returns <c>false</c> when the predicate isn't using the
    /// chunk-155a layout, the new key has no existing sub-switch
    /// (would be a layout change), or any chain to demote isn't
    /// currently a <c>try_me_else</c> head (a chain whose only
    /// remaining live entry has died — unusual but a possibility
    /// after a retract).</summary>
    private bool TryPrependToIndexedDynamic(
        Activation engine, int functorId, Shumway.Compiler.Ast.Clause newClause)
    {
        if (!IsExtensibleIndexedLayout(engine, functorId)) return false;
        // Phase 33 — capture buffer ownership before any AppendCode.
        bool ownsHost = EngineOwnsHostBuffer(engine);
        var addrMap = engine.CurrentFunctorAddresses!;
        var prog = engine.CurrentProgram!;
        int predAddr = addrMap[functorId];
        int failStub = engine.DynamicFailStubAddr;
        if (failStub <= 0) return false;

        Shumway.Compiler.Ast.Term head = newClause.Kind == Shumway.Compiler.Ast.ClauseKind.Rule
            ? ((Shumway.Compiler.Ast.CompoundTerm)newClause.Term).Args[0]
            : newClause.Term;
        if (head is not Shumway.Compiler.Ast.CompoundTerm headComp || headComp.Args.Length == 0)
            return false;
        var arg0 = headComp.Args[0];
        int arity = headComp.Args.Length;
        bool isVarArg = arg0 is Shumway.Compiler.Ast.VarTerm;

        // Chunk 156: for multi-arg, the var slot at predAddr+2
        // cascades through switch_on_arg before reaching a chain
        // head. Walk the cascade to find the final var chain.
        int varChainHead = FindFinalVarChainHead(engine, predAddr);
        if (varChainHead < 0) return false;

        // Plan: which chain heads need demotion? var-arg touches
        // every chain; same-key touches (bucket + var); new-key
        // touches (var) and creates a brand-new bucket without
        // demotion.
        var chainsToDemote = new List<int>();
        bool isNewKey = false;
        int newKeyValue = 0;
        int subSwitchTableId = -1;

        if (isVarArg)
        {
            var heads = new HashSet<int>();
            if (!CollectAllChainHeadsForRedirect(engine, predAddr, heads))
                return false;
            chainsToDemote.AddRange(heads);
        }
        else
        {
            chainsToDemote.Add(varChainHead);
            int bucketChainHead = FindBucketChainHead(engine, predAddr, newClause);
            isNewKey = bucketChainHead < 0;
            if (!isNewKey)
            {
                chainsToDemote.Add(bucketChainHead);
            }
            else
            {
                if (!TryLocateSubSwitchForArg(
                        engine, predAddr, arg0, out newKeyValue, out subSwitchTableId))
                    return false;
            }
        }

        // Validate every chain head we plan to demote currently has
        // try_me_else as its first opcode. A chain whose head was
        // demoted by a prior asserta or whose head was retracted
        // (died != MaxValue) but never compacted would fail this
        // check — let the rebuild handle those rarities.
        foreach (int h in chainsToDemote)
        {
            if (h < 0 || h + 9 > prog.Length) return false;
            if (prog[h] != (byte)Shumway.Core.Opcode.TryMeElse) return false;
        }
        // Also dedupe — a chain referenced by multiple keys (e.g.
        // every bucket reachable from a shared sub-switch default)
        // would otherwise be demoted twice. CollectAllChainHeadsForRedirect
        // dedupes, but the same-key path adds bucket + var manually.
        chainsToDemote = chainsToDemote.Distinct().ToList();

        // Compile the new clause's body (chunk 427 — shared helper with
        // a fact fast path).
        var compiledClause = CompileRuntimeAssertClause(engine, functorId, newClause);
        if (compiledClause is null) return false;

        var bodyEmitter = new Shumway.Compiler.Wam.BytecodeEmitter();
        if (_flags.EmitDebugInfo) bodyEmitter.EmitMetaDbgInfo(0);
        int bodyContentLocalStart = bodyEmitter.Position;
        bodyEmitter.AppendBytes(compiledClause.Bytecode);
        byte[] bodyChunk = bodyEmitter.ToBytes();
        int bodyAddr = engine.AppendCode(bodyChunk);
        prog = engine.CurrentProgram!;

        // Patch call sites in the body to absolute targets.
        foreach (var site in compiledClause.CallSites)
        {
            int operandPos = bodyAddr + bodyContentLocalStart + site.OpcodeOffset + 1;
            int target = addrMap.TryGetValue(site.CalleeFunctorId, out int addr)
                ? addr
                : Shumway.Core.CallTarget.ForUndefined(site.CalleeFunctorId);
            Shumway.Core.BytecodeIO.WriteInt32(prog, operandPos, target);
        }

        // For new-key concrete: build a brand-new bucket chain
        // (NEW BODY FIRST, then var-args), add to switch table.
        // No demotion needed for the new bucket.
        if (isNewKey)
        {
            var varArgBodies = CollectVarArgBodies(engine, varChainHead, functorId);
            // Asserta-flavoured layout: new body first, var-args after.
            int newBucketHead = BuildAndAppendBucketChainAsserta(
                engine, failStub, arity, bodyAddr, varArgBodies);
            prog = engine.CurrentProgram!;
            var oldTable = engine.GetSwitchTable(subSwitchTableId);
            if (oldTable is null) return false;
            var newTable = oldTable.WithAdditionalEntry(newKeyValue, newBucketHead);
            engine.ReplaceSwitchTable(subSwitchTableId, newTable);
            MirrorSwitchTableIntoDynamicLink(subSwitchTableId, newTable);
        }

        // Demote each chain to demote, build new head chunk, record
        // the old → new redirect.
        var redirectMap = new Dictionary<int, int>();
        foreach (int oldHead in chainsToDemote)
        {
            // Demote try_me_else (9 bytes) to retry_me_else (5
            // bytes) + 4 nops (4 bytes). The <next> operand at
            // +1..+4 stays — retry_me_else uses it identically.
            prog[oldHead] = (byte)Shumway.Core.Opcode.RetryMeElse;
            prog[oldHead + 5] = (byte)Shumway.Core.Opcode.Nop;
            prog[oldHead + 6] = (byte)Shumway.Core.Opcode.Nop;
            prog[oldHead + 7] = (byte)Shumway.Core.Opcode.Nop;
            prog[oldHead + 8] = (byte)Shumway.Core.Opcode.Nop;

            // New head: try_me_else <oldHead> arity + check_visible
            // + execute <bodyAddr>.
            var em = new Shumway.Compiler.Wam.BytecodeEmitter();
            em.EmitTryMeElse(oldHead, arity);
            em.EmitCheckVisible(born: _dbGeneration.Value, died: long.MaxValue);
            em.EmitExecute(bodyAddr);
            int newHead = engine.AppendCode(em.ToBytes());
            prog = engine.CurrentProgram!;
            redirectMap[oldHead] = newHead;
        }

        // Walk every pointer slot that could reference a chain head
        // and redirect any that match.
        RedirectChainHeads(engine, predAddr, redirectMap);

        SyncOrInvalidateAfterMutation(engine, ownsHost);
        // Chunk 427: refresh skipped when the pools didn't grow.
        RefreshLiteralPoolsIfGrown(engine);
        return true;
    }

    /// <summary>Chunk 155f — like
    /// <see cref="BuildAndAppendNewBucketChain"/> but with the new
    /// clause's body FIRST (the asserta order) followed by the var-
    /// arg bodies in source order. Returns the new bucket chain
    /// head address.</summary>
    private int BuildAndAppendBucketChainAsserta(
        Activation engine, int failStub, int headArity,
        int newBodyAddr, IReadOnlyList<int> varArgBodies)
    {
        var bodies = new List<int> { newBodyAddr };
        bodies.AddRange(varArgBodies);
        int count = bodies.Count;
        const int HeadEntrySize = 9 + 17 + 5;
        const int NonHeadEntrySize = 5 + 17 + 5;
        int startAddr = engine.ProgramLength;
        var em = new Shumway.Compiler.Wam.BytecodeEmitter();
        for (int i = 0; i < count; i++)
        {
            int thisSize = i == 0 ? HeadEntrySize : NonHeadEntrySize;
            int nextAddr = (i == count - 1) ? failStub
                                            : startAddr + em.Position + thisSize;
            if (i == 0) em.EmitTryMeElse(nextAddr, headArity);
            else        em.EmitRetryMeElse(nextAddr);
            em.EmitCheckVisible(born: _dbGeneration.Value, died: long.MaxValue);
            em.EmitExecute(bodies[i]);
        }
        int chunkAddr = engine.AppendCode(em.ToBytes());
        System.Diagnostics.Debug.Assert(chunkAddr == startAddr);
        return chunkAddr;
    }

    /// <summary>Chunk 155f/156 — collects every chain head reachable
    /// from the predicate's entry into <paramref name="heads"/>,
    /// across every level of multi-arg switch dispatch (chunk-156).
    /// Delegates to <see cref="EnumerateChainHeadsRecursive"/>.</summary>
    private bool CollectAllChainHeadsForRedirect(
        Activation engine, int predAddr, HashSet<int> heads)
    {
        if (engine.CurrentProgram is null) return false;
        var visited = new HashSet<int>();
        EnumerateChainHeadsRecursive(engine, predAddr + 1, heads, visited);
        return true;
    }

    /// <summary>Chunk 155f — walks every pointer slot that can
    /// reference a chain head and replaces any value present in
    /// <paramref name="redirect"/> with the new address. Touches:
    /// <c>switch_on_term</c>'s var / list / struct operands at
    /// <c>predAddr + 2 / 10 / 14</c> (const_lbl is excluded —
    /// it points at a sub-switch, never a chain head directly);
    /// every sub-switch table's keys-and-values plus its default
    /// address. Each modified switch table is replaced with a new
    /// instance and mirrored into the cached <c>_dynamicLink</c>.</summary>
    private void RedirectChainHeads(
        Activation engine, int predAddr, IReadOnlyDictionary<int, int> redirect)
    {
        if (redirect.Count == 0) return;
        // Chunk 156: walk the predicate's dispatch graph recursively,
        // patching every switch operand and switch-table value/default
        // that matches an entry in redirect.
        var visitedSwitches = new HashSet<int>();
        RedirectChainHeadsRecursive(engine, predAddr + 1, redirect, visitedSwitches);
    }

    private void RedirectChainHeadsRecursive(
        Activation engine, int addr, IReadOnlyDictionary<int, int> redirect,
        HashSet<int> visited)
    {
        if (addr <= 0) return;
        var prog = engine.CurrentProgram!;
        if (addr + 1 > prog.Length) return;
        if (!visited.Add(addr)) return;
        var op = (Shumway.Core.Opcode)prog[addr];

        void PatchOperand(int operandPos)
        {
            int cur = Shumway.Core.BytecodeIO.ReadInt32(prog, operandPos);
            if (redirect.TryGetValue(cur, out int repl))
                Shumway.Core.BytecodeIO.WriteInt32(prog, operandPos, repl);
        }

        switch (op)
        {
            case Shumway.Core.Opcode.SwitchOnTerm:
                // 4 address operands at +1, +5, +9, +13.
                for (int j = 0; j < 4; j++) PatchOperand(addr + 1 + 4 * j);
                // Recurse into each label.
                for (int j = 0; j < 4; j++)
                {
                    int lbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 1 + 4 * j);
                    RedirectChainHeadsRecursive(engine, lbl, redirect, visited);
                }
                break;
            case Shumway.Core.Opcode.SwitchOnArg:
                // arg_idx at +1; 4 address operands at +5, +9, +13, +17.
                for (int j = 0; j < 4; j++) PatchOperand(addr + 5 + 4 * j);
                for (int j = 0; j < 4; j++)
                {
                    int lbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 5 + 4 * j);
                    RedirectChainHeadsRecursive(engine, lbl, redirect, visited);
                }
                break;
            case Shumway.Core.Opcode.SwitchOnAtom:
            case Shumway.Core.Opcode.SwitchOnInteger:
            case Shumway.Core.Opcode.SwitchOnStructure:
            {
                int tid = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 1);
                var table = engine.GetSwitchTable(tid);
                if (table is null) break;
                var newTable = RedirectSwitchTable(table, redirect);
                var live = newTable ?? table;
                if (newTable is not null)
                {
                    engine.ReplaceSwitchTable(tid, newTable);
                    MirrorSwitchTableIntoDynamicLink(tid, newTable);
                }
                foreach (int v in live.Values)
                    RedirectChainHeadsRecursive(engine, v, redirect, visited);
                RedirectChainHeadsRecursive(engine, live.DefaultAddress, redirect, visited);
                break;
            }
            case Shumway.Core.Opcode.SwitchOnAtomArg:
            case Shumway.Core.Opcode.SwitchOnIntegerArg:
            case Shumway.Core.Opcode.SwitchOnStructureArg:
            {
                int tid = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 5);
                var table = engine.GetSwitchTable(tid);
                if (table is null) break;
                var newTable = RedirectSwitchTable(table, redirect);
                var live = newTable ?? table;
                if (newTable is not null)
                {
                    engine.ReplaceSwitchTable(tid, newTable);
                    MirrorSwitchTableIntoDynamicLink(tid, newTable);
                }
                foreach (int v in live.Values)
                    RedirectChainHeadsRecursive(engine, v, redirect, visited);
                RedirectChainHeadsRecursive(engine, live.DefaultAddress, redirect, visited);
                break;
            }
            // Other opcodes (chain heads, fail_stub, anything else):
            // stop. Chain heads aren't switch nodes — they don't have
            // outgoing pointers that need redirecting beyond their
            // <next>, which is internal to the chain.
        }
    }

    /// <summary>Chunk 155f — returns a new
    /// <see cref="Shumway.Core.SwitchTable"/> with every value
    /// (including the default address) replaced if present in
    /// <paramref name="redirect"/>. Returns <c>null</c> when no
    /// entry was redirected — caller skips the replacement.</summary>
    private static Shumway.Core.SwitchTable? RedirectSwitchTable(
        Shumway.Core.SwitchTable old, IReadOnlyDictionary<int, int> redirect)
    {
        bool changed = false;
        int[] keys = old.Keys.ToArray();
        int[] values = old.Values.ToArray();
        for (int i = 0; i < values.Length; i++)
        {
            if (redirect.TryGetValue(values[i], out int repl))
            {
                values[i] = repl;
                changed = true;
            }
        }
        int newDefault = old.DefaultAddress;
        if (redirect.TryGetValue(newDefault, out int defRepl))
        {
            newDefault = defRepl;
            changed = true;
        }
        return changed ? new Shumway.Core.SwitchTable(keys, values, newDefault) : null;
    }

    // ====================================================================
    // Chunk 155d — in-place retract for extensible-indexed dynamic
    // predicates.
    // ====================================================================

    /// <summary>Chunk 155d — returns the body address of the
    /// <paramref name="clauseIndex"/>'th still-alive clause in the
    /// var-fallthrough chain, where "alive" is defined as
    /// <c>died == long.MaxValue</c> in the entry's
    /// <c>check_visible</c>. Previously-retracted entries (died set
    /// to some generation by a prior chunk-155d retract) are
    /// skipped, so the index aligns with the post-removal
    /// <c>_dynamicClauses</c> ordering — except that this lookup
    /// runs BEFORE the current <c>RemoveAt</c>, so
    /// <paramref name="clauseIndex"/> is the position in the
    /// pre-removal list. Returns <c>-1</c> on layout mismatch or
    /// when the index runs off the chain.</summary>
    private int FindBodyAddrForClauseIndex(Activation engine, int functorId, int clauseIndex)
    {
        if (!IsExtensibleIndexedLayout(engine, functorId)) return -1;
        var addrMap = engine.CurrentFunctorAddresses;
        if (addrMap is null || !addrMap.TryGetValue(functorId, out int predAddr)) return -1;
        var prog = engine.CurrentProgram!;
        int failStub = engine.DynamicFailStubAddr;
        int varChainHead = FindFinalVarChainHead(engine, predAddr);
        if (varChainHead < 0) return -1;
        int cur = varChainHead;
        int aliveIdx = 0;
        while (true)
        {
            if (cur < 0 || cur + 27 > prog.Length) return -1;
            int chainHeaderSize = ChainEntryHeaderSize(prog, cur);
            int diedAddr = cur + chainHeaderSize + 9;
            int execOpPos = cur + chainHeaderSize + 17;
            if (execOpPos + 5 > prog.Length) return -1;
            if (prog[execOpPos] != (byte)Shumway.Core.Opcode.Execute) return -1;
            long died = Shumway.Core.BytecodeIO.ReadInt64(prog, diedAddr);
            if (died == long.MaxValue)
            {
                if (aliveIdx == clauseIndex)
                    return Shumway.Core.BytecodeIO.ReadInt32(prog, execOpPos + 1);
                aliveIdx++;
            }
            int next = Shumway.Core.BytecodeIO.ReadInt32(prog, cur + 1);
            if (next == failStub) return -1;
            cur = next;
        }
    }

    /// <summary>Chunk 155d — walks every chain in the chunk-155a
    /// indexed predicate (each bucket chain reached through a switch
    /// table value, the list chain head, and the var-fallthrough
    /// chain), and patches the died slot of every entry whose
    /// <c>execute</c> targets <paramref name="bodyAddr"/>. The died
    /// slot is the second 8-byte field of the entry's
    /// <c>check_visible</c>, located at chain-header-size + 1 + 8
    /// from the entry's start. Returns <c>true</c> when at least one
    /// entry was patched (the chain actually held a reference to the
    /// body); <c>false</c> when the predicate isn't using the chunk-
    /// 155a layout or no chain referenced the retired body.</summary>
    private bool TryPatchDiedInAllIndexedChains(Activation engine, int functorId, int bodyAddr)
    {
        if (!IsExtensibleIndexedLayout(engine, functorId)) return false;
        var addrMap = engine.CurrentFunctorAddresses!;
        var prog = engine.CurrentProgram!;
        int predAddr = addrMap[functorId];
        int failStub = engine.DynamicFailStubAddr;

        // Chunk 156: enumerate every chain head reachable from the
        // top-level switch, including multi-level cascades through
        // switch_on_arg.
        var heads = new HashSet<int>();
        var visited = new HashSet<int>();
        EnumerateChainHeadsRecursive(engine, predAddr + 1, heads, visited);

        bool anyPatched = false;
        foreach (int head in heads)
        {
            int cur = head;
            while (cur > 0 && cur + 27 <= prog.Length)
            {
                int chainHeaderSize = ChainEntryHeaderSize(prog, cur);
                int execOpPos = cur + chainHeaderSize + 17;
                if (execOpPos + 5 > prog.Length) break;
                if (prog[execOpPos] != (byte)Shumway.Core.Opcode.Execute) break;
                int target = Shumway.Core.BytecodeIO.ReadInt32(prog, execOpPos + 1);
                if (target == bodyAddr)
                {
                    int diedAddr = cur + chainHeaderSize + 9;
                    Shumway.Core.BytecodeIO.WriteInt64(prog, diedAddr, _dbGeneration.Value);
                    anyPatched = true;
                }
                int next = Shumway.Core.BytecodeIO.ReadInt32(prog, cur + 1);
                if (next == failStub) break;
                cur = next;
            }
        }
        return anyPatched;
    }

    /// <summary>Chunk 155c — writes <paramref name="newTable"/> into
    /// the dynamic region's slot of the cached <see cref="_dynamicLink"/>
    /// so the next query's <see cref="SetupQueryFromTerm"/> (which
    /// rebuilds the merged engine.SwitchTables list from
    /// staticLink + _dynamicLink + queryLink) carries the chunk-155c
    /// mutation forward. The merged-table id at runtime is
    /// <c>staticLink.SwitchTables.Count + dynamicLocalId</c>; we
    /// undo the offset to find the right slot in _dynamicLink.
    /// </summary>
    private void MirrorSwitchTableIntoDynamicLink(int mergedTableId, Shumway.Core.SwitchTable newTable)
    {
        if (_dynamicLink is null) return;
        int staticCount = _staticLink?.SwitchTables.Count ?? 0;
        int dynLocalId = mergedTableId - staticCount;
        if (dynLocalId < 0 || dynLocalId >= _dynamicLink.SwitchTables.Count) return;
        if (_dynamicLink.SwitchTables is List<Shumway.Core.SwitchTable> dynList)
            dynList[dynLocalId] = newTable;
        // If the link's IReadOnlyList isn't actually a List (some
        // alternative implementation), the mutation can't be made
        // persistent — chunk 155c degrades gracefully: the current
        // query holds the update via engine.SwitchTables, the next
        // query will rebuild from the unmutated link and miss it.
        // That just regresses to the chunk-154 rebuild fallback for
        // the affected predicate, which is correct, only slower.
    }

    /// <summary>Chunk 155c — emits a fresh bucket chain containing
    /// every var-arg clause's body (in source order) followed by
    /// the new clause's body, appends it to the buffer, and returns
    /// the chain head address. The chain head uses
    /// <c>try_me_else</c> (9 bytes); subsequent entries use
    /// <c>retry_me_else</c> (5 bytes); the last entry's
    /// <c>&lt;next&gt;</c> is the fail stub.</summary>
    private int BuildAndAppendNewBucketChain(
        Activation engine, int failStub, int headArity,
        IReadOnlyList<int> varArgBodies, int newBodyAddr)
    {
        // Chain bodies in source order: var-arg first, then new.
        var bodies = new List<int>(varArgBodies);
        bodies.Add(newBodyAddr);
        int count = bodies.Count;

        // Plan the chunk: layout & size up front so we can compute
        // each entry's address (each entry's <next> points at the
        // following entry's absolute address).
        const int HeadEntrySize = 9 + 17 + 5;
        const int NonHeadEntrySize = 5 + 17 + 5;

        // We don't know the chain's start address yet — depends on
        // engine.ProgramLength. Probe AppendCode by appending a
        // single empty buffer; instead, just compute offsets relative
        // to ProgramLength now and then emit in one go.
        int startAddr = engine.ProgramLength;
        // Build the entire chain in one BytecodeEmitter so the
        // offsets are right by construction, then AppendCode the
        // result. Each entry's <next> is the absolute address of
        // the next entry, or fail_stub for the last.
        var em = new Shumway.Compiler.Wam.BytecodeEmitter();
        for (int i = 0; i < count; i++)
        {
            int thisSize = i == 0 ? HeadEntrySize : NonHeadEntrySize;
            int nextAddr = (i == count - 1) ? failStub
                                            : startAddr + em.Position + thisSize;
            if (i == 0) em.EmitTryMeElse(nextAddr, headArity);
            else        em.EmitRetryMeElse(nextAddr);
            em.EmitCheckVisible(born: _dbGeneration.Value, died: long.MaxValue);
            em.EmitExecute(bodies[i]);
        }
        int chunkAddr = engine.AppendCode(em.ToBytes());
        // chunkAddr should equal startAddr (we computed offsets to
        // match). Verify in debug builds.
        System.Diagnostics.Debug.Assert(chunkAddr == startAddr,
            $"chunk 155c: new bucket chain address mismatch (expected {startAddr}, got {chunkAddr}).");
        return chunkAddr;
    }

    /// <summary>Chunk 150/151b — pulls the first free chunk whose
    /// length is at least <paramref name="needed"/> off the chain
    /// table's free-list and returns its address; the chunk's tail
    /// (beyond <paramref name="needed"/> bytes) goes back on the list.
    /// Returns -1 when no fit is available, meaning the caller should
    /// fall back to <c>engine.AppendCode</c>. The free-list lives on the
    /// per-buffer <see cref="DynChainTable"/> (Phase 33 — its addresses
    /// are buffer-relative), so chunks freed in one query are reusable
    /// by the next while that buffer is reused.</summary>
    private static int TryReuseFreeChunk(
        List<(int Addr, int Length)> freeChunks, int needed)
    {
        for (int i = 0; i < freeChunks.Count; i++)
        {
            var (addr, length) = freeChunks[i];
            if (length < needed) continue;
            freeChunks.RemoveAt(i);
            int leftover = length - needed;
            if (leftover > 0)
                freeChunks.Add((addr + needed, leftover));
            return addr;
        }
        return -1;
    }

    /// <summary>Test hook: returns the absolute byte offset of clause
    /// <paramref name="clauseIndex"/>'s died slot in the running program,
    /// or <c>null</c> when no chain state exists. Used by chunk-123 tests
    /// to verify <c>retract</c> patches the slot.</summary>
    internal int? PeekDiedAddr(int functorId, int clauseIndex)
    {
        if (!_dynChainTable.Chains.TryGetValue(functorId, out var chain)) return null;
        if (clauseIndex < 0 || clauseIndex >= chain.Entries.Count) return null;
        return chain.Entries[clauseIndex].DiedOperandAddr;
    }

    /// <summary>Test hook: returns the absolute byte offset of clause
    /// <paramref name="clauseIndex"/>'s chain-instruction <c>&lt;next&gt;</c>
    /// operand in the running program, or <c>null</c> when no chain state
    /// exists. -1 when the clause was emitted without a chain instruction
    /// in front of it.</summary>
    internal int? PeekNextAddr(int functorId, int clauseIndex)
    {
        if (!_dynChainTable.Chains.TryGetValue(functorId, out var chain)) return null;
        if (clauseIndex < 0 || clauseIndex >= chain.Entries.Count) return null;
        return chain.Entries[clauseIndex].NextOperandAddr;
    }

    // chunk 432: the generation lives in a shared GenerationBox handed to
    // every Activation this host sets up, so enter_dynamic samples it with a
    // field read instead of a Func<long> invoke per dynamic call. The ONE
    // bump site is InvalidateDynamicCache (every assertz / asserta /
    // retract / abolish funnels through it).
    private readonly Shumway.Core.GenerationBox _dbGeneration = new();

    /// <summary>A monotonic counter bumped by every <c>assertz</c> /
    /// <c>asserta</c> / <c>retract</c> / <c>abolish</c> — the
    /// logical-update-view clock of ADR-015. A query captures the value at
    /// start; a later chunk (C) has a dynamic-predicate call see only the
    /// clauses whose born/died range contains the captured value, so a
    /// goal observes the database as of when that goal began. Chunk A
    /// (this) lays the counter; the born/died clause stamps that consume
    /// it land with the dynamic-dispatch chunk that reads them. Also a
    /// useful public signal: an embedder can detect whether the dynamic
    /// database changed since it last looked.</summary>
    public long DbGeneration => _dbGeneration.Value;

    /// <summary>Set of functor ids declared <c>:- dynamic</c> across every
    /// module. The set is global so a single shared store can satisfy
    /// assertz / retract from any module; <see cref="ModuleRewrite"/> reads
    /// it to skip mangling dynamic functors.</summary>
    private readonly HashSet<int> _dynamicFunctors = new();

    /// <summary>Head functor ids of every predicate defined by the auto-loaded
    /// prelude (<see cref="Prelude.Source"/>). These are library predicates —
    /// ISO / de-facto standards like <c>member/2</c>, <c>sub_atom/5</c>,
    /// <c>msort/2</c>, <c>subsumes_term/2</c>, <c>maplist/N</c> — implemented
    /// in Prolog rather than C#, so <see cref="Shumway.Builtins.BuiltinsRegistry"/>
    /// doesn't know them. <c>predicate_property/2</c> nonetheless reports them
    /// as <c>built_in</c> (they are not user-defined and cannot be modified),
    /// which is what a client like Logtalk's linter checks to decide a call is
    /// to a known system predicate rather than an undefined one.</summary>
    private readonly HashSet<int> _preludeFunctors = new();

    /// <summary>The sink that I/O builtins (<c>write/1</c>, <c>nl/0</c>,
    /// <c>writeln/1</c>) write into. Defaults to <see cref="System.Console.Out"/>;
    /// swap in a <see cref="System.IO.StringWriter"/> to capture program
    /// output in tests.</summary>
    public System.IO.TextWriter Out { get; set; } = Console.Out;

    /// <summary>Per-engine state for Tier-0 → Tier-1 auto-promotion: an
    /// invocation counter per functor plus a cache of successfully
    /// IL-compiled delegates. The store's <c>Threshold</c> property
    /// gates the promotion machinery — left at <c>0</c> nothing ever
    /// promotes, which is the default. Set
    /// <c>engine.IlPromotion.Threshold = N</c> to enable; future
    /// <c>:- option(...)</c> directives may surface a friendlier knob.</summary>
    public IlPromotionStore IlPromotion { get; } = new();

    /// <summary>Chunk 75 — JIT indexing profile. Tracks per-predicate
    /// runtime call counts so the engine can defer building switch
    /// tables for a dynamic predicate until it proves hot. Set
    /// <c>engine.JitIndexing.Threshold</c> to tune (or, in tests, to
    /// force) the cold→hot transition.</summary>
    public JitIndexProfile JitIndexing => _jitIndexProfile;
    private readonly JitIndexProfile _jitIndexProfile = new();

    /// <summary>Pre-decoded compiled modules from any bundle loaded
    /// with a <see cref="BundleEntry.CompiledBytecode"/> blob
    /// (chunk 38 / chunk 45). Future runtime paths (chunk 51 onward)
    /// can consult this cache to skip the WAM compile step entirely
    /// when the consulted source matches a precompiled module — for
    /// now it surfaces purely as a diagnostic property.</summary>
    public IReadOnlyDictionary<string, Shumway.Compiler.Wam.CompiledModule> PrecompiledModules
        => _precompiledModules;
    private readonly Dictionary<string, Shumway.Compiler.Wam.CompiledModule> _precompiledModules = new();
    /// <summary>Float-literal support — for a precompiled (bundle) predicate, the
    /// float pool its bytecode's <c>get_float</c>/<c>put_float</c> literalIds index
    /// (its own module's <c>FloatLiterals</c>). The IL compiler bakes the value, so
    /// it needs the right pool per fid. Everything else (runtime-compiled clauses,
    /// dynamic snapshots) indexes the engine's live <c>_literalPools.Floats</c>.</summary>
    private readonly Dictionary<int, IReadOnlyList<double>> _precompiledFloatPool = new();

    /// <summary>Every dynamic functor that currently has clauses. Used by the
    /// persisted-IL build to snapshot dynamic predicates — including ones the
    /// per-query caches deliberately skip because their bytecode references a
    /// pool literal (float / string / bigint), which is exactly the float case the
    /// IL compiler now value-bakes.</summary>
    internal IEnumerable<int> DynamicFunctorsWithClauses()
    {
        foreach (var fid in _dynamicFunctors)
            if (_dynamicClauses.TryGetValue(fid, out var cs) && cs.Count > 0)
                yield return fid;
    }

    /// <summary>The float-literal pool predicate <paramref name="fid"/>'s bytecode
    /// indexes — wired into <see cref="IlPromotionStore.FloatPoolProvider"/>. After
    /// <see cref="RemapPrecompiledLiterals"/> every predicate's float ids index the
    /// engine's live pool, so this is just the live pool.</summary>
    internal IReadOnlyList<double> FloatPoolForFid(int fid)
        => _precompiledFloatPool.TryGetValue(fid, out var pool)
            ? pool
            : _literalPools.Floats.Items;

    /// <summary>Remaps a source-less precompiled module's float / string / bigint
    /// literal ids from its module-local pools into this engine's shared
    /// <see cref="_literalPools"/>, rewriting the bytecode operands in place. See the
    /// call site in <c>LoadEntryFromBytecode</c> for why.</summary>
    private void RemapPrecompiledLiterals(Shumway.Compiler.Wam.CompiledModule module)
    {
        var floats = module.FloatLiterals;
        var strings = module.StringLiterals;
        var bigints = module.BigIntLiterals;
        if (floats.Count == 0 && strings.Count == 0 && bigints.Count == 0) return;
        foreach (var pred in module.Predicates)
        {
            byte[] code = pred.Bytecode;
            int pc = 0;
            while (pc < code.Length)
            {
                var info = Shumway.Core.OpcodeTable.Get(code[pc]);
                if (!info.IsDefined || info.Size == 0) break;   // corrupt — stop walking
                switch ((Shumway.Core.Opcode)code[pc])
                {
                    case Shumway.Core.Opcode.GetFloat:
                    case Shumway.Core.Opcode.PutFloat:
                    case Shumway.Core.Opcode.UnifyFloat:
                        RemapLit(code, pc + 1, floats, _literalPools.Floats);
                        break;
                    case Shumway.Core.Opcode.GetBigInt:
                    case Shumway.Core.Opcode.PutBigInt:
                    case Shumway.Core.Opcode.UnifyBigInt:
                        RemapLit(code, pc + 1, bigints, _literalPools.BigInts);
                        break;
                    case Shumway.Core.Opcode.GetPstr:
                    case Shumway.Core.Opcode.PutPstr:
                        RemapLit(code, pc + 1, strings, _literalPools.Strings);
                        break;
                    case Shumway.Core.Opcode.AEvalPush:
                    {
                        // <kind:4> <operand:4>; kind 1 = bigint lit, 2 = float lit.
                        int kind = Shumway.Core.BytecodeIO.ReadInt32(code, pc + 1);
                        if (kind == 2) RemapLit(code, pc + 5, floats, _literalPools.Floats);
                        else if (kind == 1) RemapLit(code, pc + 5, bigints, _literalPools.BigInts);
                        break;
                    }
                }
                pc += info.Size;
            }
        }
    }

    private static void RemapLit<T>(byte[] code, int off,
        IReadOnlyList<T> srcPool, Shumway.Compiler.Wam.LiteralPool<T> dstPool)
        where T : notnull
    {
        int oldId = Shumway.Core.BytecodeIO.ReadInt32(code, off);
        if ((uint)oldId >= (uint)srcPool.Count) return;   // defensive
        int newId = dstPool.Intern(srcPool[oldId]);
        if (newId != oldId) Shumway.Core.BytecodeIO.WriteInt32(code, off, newId);
    }

    // Phase 20: most-recent query's functor→address map, for the
    // profiler's address→name resolution. Null until the first query
    // under a profiling build.
    private IReadOnlyDictionary<int, int>? _profileFunctorAddresses;

    /// <summary>Phase 20 — renders the current <see cref="Shumway.Core.Profiler"/>
    /// report, resolving recorded callee addresses to <c>Name/Arity</c>
    /// via the most recent query's link and builtin ids via the
    /// registry. Returns the empty string in a non-profile build.</summary>
    public string ProfileReport(int top = 25)
    {
        if (!Shumway.Core.Profiler.Enabled) return string.Empty;

        // Invert functor→address once for the address→name lookup.
        var addrToName = new Dictionary<int, string>();
        if (_profileFunctorAddresses is not null)
            foreach (var (fid, addr) in _profileFunctorAddresses)
            {
                var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
                string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? $"fid{fid}";
                addrToName[addr] = $"{name}/{arity}";
            }

        // Chunk 403: nearest-predicate-at-or-below resolver for pc-keyed counters
        // (retry_me_else attribution) — a clause's retry pc sits INSIDE its
        // predicate's code range, past the entry address.
        var sortedAddrs = addrToName.Keys.ToArray();
        Array.Sort(sortedAddrs);
        string? NearestName(int pc)
        {
            int lo = 0, hi = sortedAddrs.Length - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (sortedAddrs[mid] <= pc) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return best >= 0 ? addrToName[sortedAddrs[best]] : null;
        }

        return Shumway.Core.Profiler.Report(
            addressName: a => addrToName.TryGetValue(a, out var n) ? n : null,
            builtinName: id =>
            {
                var e = Shumway.Builtins.BuiltinsRegistry.GetById(id);
                return $"{e.Name}/{e.Arity}";
            },
            top: top,
            nearestPredicateName: NearestName);
    }

    /// <summary>Chunk 209 — per-module set of BARE (un-mangled) local
    /// functor ids contributed by a bundle loaded via
    /// <see cref="LoadEntryFromBytecode"/>. A bundle's predicates are
    /// already compiled (and mangled <c>module$name</c>), so
    /// <c>ComputeLocalFunctors</c> — which walks <c>manifest.Clauses</c>
    /// — can't rediscover them at query setup (manifest.Clauses is
    /// empty for a bundled module). SetupQueryFromTerm folds these bare
    /// fids into the module's locals so a dynamic clause's body call to
    /// a bundled local predicate gets the same mangling the bundle's
    /// own bytecode used.</summary>
    private readonly Dictionary<string, HashSet<int>> _precompiledModuleLocals = new();

    /// <summary>Snapshot of the most recent query's call stack as a
    /// list of <c>Name/Arity</c> predicate indicators (chunk 51).
    /// Captured automatically when a runtime error escapes; available
    /// via <see cref="ShumwayPrologException.StackTrace"/>.</summary>
    public IReadOnlyList<(string Name, int Arity)> LastErrorStackTrace { get; private set; }
        = Array.Empty<(string, int)>();

    /// <summary>Source-position-enriched view of
    /// <see cref="LastErrorStackTrace"/> (chunk 53). Each frame carries
    /// the first-clause <c>SourcePosition</c> of the predicate, when
    /// the bytecode came from a source consult; synthetic predicates
    /// surface as <see cref="Shumway.Compiler.Lexer.SourcePosition.Start"/>.</summary>
    public IReadOnlyList<StackFrame> LastErrorStackTraceWithPositions { get; private set; }
        = Array.Empty<StackFrame>();

    /// <summary>One frame in <see cref="LastErrorStackTraceWithPositions"/>:
    /// the predicate's <c>Name/Arity</c> plus the source position of its
    /// first clause (or <see cref="Shumway.Compiler.Lexer.SourcePosition.Start"/>
    /// for synthetic / blob-loaded predicates without source info).</summary>
    public readonly record struct StackFrame(
        string Name, int Arity, Shumway.Compiler.Lexer.SourcePosition Position)
    {
        public override string ToString()
        {
            // "name/arity at line:col" reads naturally in trace lines.
            if (Position.Line <= 1 && Position.Column <= 1 && Position.Offset == 0)
                return $"{Name}/{Arity}";
            return $"{Name}/{Arity} at {Position}";
        }
    }

    private IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate>? _currentPredicatesByAddress;

    /// <summary>Diagnostic accessor for the most recently linked query's
    /// address → predicate map. Used by tests that need to verify which
    /// predicate instances ended up in the linked program (e.g. confirming
    /// the bundle skip-compile path reused the cached
    /// <see cref="Shumway.Compiler.Wam.CompiledPredicate"/> by reference).
    /// Returns <c>null</c> before the first query runs.</summary>
    public IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate>? CurrentPredicatesByAddressForTest
        => _currentPredicatesByAddress;

    public PrologEngine() : this(consultPrelude: true) { }

    /// <summary>Bare construction: when <paramref name="consultPrelude"/> is
    /// <c>false</c> the engine starts WITHOUT the internal prelude. Used by
    /// <see cref="FromBundle(Bundle)"/> so a bundle that bakes a precompiled
    /// prelude (shumway-link <c>--exe</c> / <c>--stdlib</c>) supplies it
    /// instead of the engine paying the parse + compile at startup. A bare
    /// engine is unusable until a prelude is installed (the bundle's, or the
    /// FromBundle fallback consult).</summary>
    internal PrologEngine(bool consultPrelude)
    {
        // The standard builtins (=/2, ==/2, etc.) need to be registered before
        // the WAM compiler can recognise them. EnsureRegistered is idempotent.
        Shumway.Builtins.StandardBuiltins.EnsureRegistered();
        // Meta-builtins (findall/3 etc.) live in the Embedding layer because
        // they spawn sub-PrologEngines — Builtins can't reference Embedding.
        MetaBuiltins.EnsureRegistered();

        // ADR-022 item 2 — let Tier-1 IL inline this engine's native blocks at
        // their `$native_run` call sites. The provider returns null until a block
        // is registered, so non-native programs pay nothing.
        IlPromotion.NativeInlineProvider = GetNativeInlineContext;
        // ADR-023 — let a read-hot, mutation-cold `:- dynamic` predicate run as
        // Tier-1 IL (a snapshot of its visible clauses), evicted on any mutation.
        IlPromotion.DynamicSnapshotProvider = BuildDynamicSnapshot;
        IlPromotion.FloatPoolProvider = FloatPoolForFid;

        // Consult the internal prelude — Prolog-level definitions of
        // multi-solution predicates (member/2, clause/2, current_predicate/1)
        // that ride the standard WAM choice-point machinery instead of
        // faking backtracking inside a single-shot builtin.
        if (consultPrelude)
        {
            ConsultStringInner(Prelude.Source, recordInHistory: false);
            MarkModuleNonDebuggable(Prelude.ModuleName);   // ADR-035
        }
    }

    /// <summary>Loads a bundle into a fresh engine, using the bundle's BAKED
    /// prelude (produced by <c>shumway-link --exe</c> / <c>--stdlib</c>)
    /// when present so startup skips compiling the prelude. Falls back to
    /// consulting the prelude when the bundle doesn't carry one (older bundles
    /// or a link without <c>--stdlib</c>). The fast-startup entry point
    /// the generated <c>--exe</c> uses; the result is equivalent to
    /// <c>var e = new PrologEngine(); e.LoadBundle(bundle);</c>.</summary>
    public static PrologEngine FromBundle(Bundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return LoadBundleBare(bundle, bundleDir: null);
    }

    /// <summary>ADR-035 — <see cref="FromBundle(Bundle)"/> for a host that wants the engine
    /// DEBUGGABLE. Passing a non-null <paramref name="debug"/> turns debugging on BEFORE the
    /// bundle's modules are consulted — which is the only moment it can matter, since
    /// debuggability is decided when code is compiled — so the modules load debuggable and a
    /// module that still carries its source is shown from that embedded source. This is what
    /// the <c>shumway-link --dll</c> factory's <c>CreateEngine(debug: true)</c> calls; there
    /// is one debugger per process, so a second debuggable engine in the same process
    /// throws.</summary>
    public static PrologEngine FromBundle(Bundle bundle, Debugging.DebugOptions? debug)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return LoadBundleBare(bundle, bundleDir: null, debug);
    }

    /// <summary>File-path overload of <see cref="FromBundle(Bundle)"/>.</summary>
    public static PrologEngine FromBundle(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return LoadBundleBare(BundleReader.ReadFromFile(path),
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
    }

    /// <summary>Debuggable file-path overload — see <see cref="FromBundle(Bundle,
    /// Debugging.DebugOptions)"/>.</summary>
    public static PrologEngine FromBundle(string path, Debugging.DebugOptions? debug)
    {
        ArgumentNullException.ThrowIfNull(path);
        return LoadBundleBare(BundleReader.ReadFromFile(path),
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)), debug);
    }

    private static PrologEngine LoadBundleBare(
        Bundle bundle, string? bundleDir, Debugging.DebugOptions? debug = null)
    {
        var engine = new PrologEngine(consultPrelude: false);
        // ADR-035 — a host that asked for a debuggable engine gets the switch thrown BEFORE
        // any module is consulted (debuggability is a compile-time property), exactly as when
        // the host builds the engine by hand. The prelude below is consulted and marked
        // non-debuggable afterward, so it stays opaque either way; the bundle's own modules,
        // loaded by LoadBundleCore, compile debuggable and materialise their embedded source.
        Debugging.ChannelDebugSession? debugSession = null;
        bool waitForAttach = debug is { WaitForAttach: true };
        if (debug is not null)
        {
            // If a wait-for-attach was requested, DON'T let EnableDebugging block here — the
            // bundle's modules have not loaded yet, so the source the debugger needs is not
            // materialised or announced. Enable debug codegen now; wait AFTER LoadBundleCore.
            var enableNow = waitForAttach
                ? new Debugging.DebugOptions
                {
                    SourceFiles = debug.SourceFiles,
                    LastCallOptimisation = debug.LastCallOptimisation,
                    WaitForAttach = false,
                    AttachTimeout = debug.AttachTimeout,
                }
                : debug;
            debugSession = engine.EnableDebugging(enableNow);
        }
        // The prelude must be present BEFORE the bundle's entries load — a
        // persisted-IL entry resolves its call targets / region-member aliases
        // against the prelude's functors at load, and the same prelude-then-
        // program order a normal `new PrologEngine(); LoadBundle()` uses keeps
        // that sound. When the bundle BAKES the prelude (a Tier-0 --exe), it
        // supplies it as an entry, so we let LoadBundleCore install it; only
        // when no baked prelude is present do we consult it up front.
        bool bundleHasPrelude = false;
        foreach (var e in bundle.Entries)
            if (e.ModuleName == Prelude.ModuleName) { bundleHasPrelude = true; break; }
        if (!bundleHasPrelude)
        {
            engine.ConsultStringInner(Prelude.Source, recordInHistory: false);
            engine.MarkModuleNonDebuggable(Prelude.ModuleName);   // ADR-035
        }
        engine.LoadBundleCore(bundle, bundleDir);

        // Now the bundle's modules are consulted (and their source materialised + announced),
        // so an attach-to-debug-from-the-first-goal launcher can safely wait for the debugger.
        if (waitForAttach && debugSession is not null)
            engine.WaitForDebuggerReady(debugSession, debug!.AttachTimeout);
        return engine;
    }

    /// <summary>Builds a peer <see cref="PrologEngine"/> sharing this engine's
    /// consulted modules and operator declarations. Used by meta-builtins like
    /// <c>findall/3</c> that need to enumerate every solution of a goal
    /// independently of the calling engine's choice-point stack.</summary>
    internal PrologEngine CreateSubEngine()
    {
        var sub = new PrologEngine { Out = Out };
        // Replace the sub-engine's default empty module set with deep copies
        // of ours so modifications in the sub-engine never bleed back.
        sub._modules.Clear();
        foreach (var (name, manifest) in _modules)
        {
            var copy = new ModuleManifest(name);
            copy.Clauses.AddRange(manifest.Clauses);
            copy.PublicFunctors.UnionWith(manifest.PublicFunctors);
            copy.DynamicFunctors.UnionWith(manifest.DynamicFunctors);
            sub._modules[name] = copy;
        }
        sub._dynamicFunctors.UnionWith(_dynamicFunctors);
        foreach (var (fid, clauses) in _dynamicClauses)
            sub._dynamicClauses[fid] = new List<Clause>(clauses);
        foreach (var (fid, pred) in _dynamicPredicateCache)
            sub._dynamicPredicateCache[fid] = pred;
        // The sub-engine shares the parent's static program, so the
        // parent's compiled-static-predicate cache (chunk 82) is valid
        // for it — pass it through so a meta-call's sub-engine query
        // doesn't recompile the whole program from scratch.
        foreach (var (fid, pred) in _staticPredicateCache)
            sub._staticPredicateCache[fid] = pred;
        _jitIndexProfile.CopyInto(sub._jitIndexProfile);
        return sub;
    }

    // ============================================================================
    // Dynamic predicate runtime store (asserts / retracts)
    // ============================================================================

    /// <summary>Adds <paramref name="clause"/> to the end of its predicate's
    /// dynamic clause list. The predicate must have been declared
    /// <c>:- dynamic foo/N</c> previously (in any module). Returns the
    /// head's functor id (chunk 427 — the caller needs it for the
    /// incremental dispatch update; returning it avoids a second
    /// extraction's string intern).</summary>
    internal int Assertz(Clause clause)
    {
        int fid = ExtractHeadFunctorId(clause);
        EnsureDynamic(fid);
        GetOrCreateDynamicSlot(fid).Add(clause);
        InvalidateDynamicCache(fid);
        return fid;
    }

    /// <summary>Adds <paramref name="clause"/> at the front of its predicate's
    /// dynamic clause list. Returns the head's functor id (chunk 427).</summary>
    internal int Asserta(Clause clause)
    {
        int fid = ExtractHeadFunctorId(clause);
        EnsureDynamic(fid);
        GetOrCreateDynamicSlot(fid).Insert(0, clause);
        InvalidateDynamicCache(fid);
        // Chunk 155f: persistent invalidation moved into
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
        if (!_dynamicClauses.TryGetValue(fid, out var list)) return false;
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
        return _dynamicClauses.TryGetValue(functorId, out var list)
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
        if (!_dynamicClauses.TryGetValue(fid, out var raw) || raw.Count == 0) return null;
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
        // change; rule-bearing (from the RAW source clauses — the transformed
        // ones may have been rewritten) gates checked caller-inlining.
        snap.IsDynamicSnapshot = true;
        foreach (var c in raw)
            if (c.Kind == Shumway.Compiler.Ast.ClauseKind.Rule)
            {
                snap.SnapshotRuleBearing = true;
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
    /// silently failing on a static predicate (chunk 131e).</summary>
    internal bool IsDynamic(int functorId) => _dynamicFunctors.Contains(functorId);

    /// <summary>Snapshot of every functor declared <c>:- dynamic</c>.
    /// Used by <c>garbage_collect_clauses/0</c> to iterate them.
    /// (Chunk 150.)</summary>
    internal IEnumerable<int> AllDynamicFunctors() => _dynamicFunctors.ToArray();

    // chunk 431 — single spare buffer reused across retract/1's remaining-
    // candidates snapshots (the ISO call-time view copied at CP-push time).
    // One buffer covers the overwhelmingly common case (non-nested retract);
    // a nested enumeration that misses simply allocates, exactly like the
    // pre-431 code. Lifecycle: the buffer is exclusively owned by one
    // enumeration from Rent until the matching Return, which fires at
    // exactly one of (a) the resume's no-further-match failure, (b) the
    // resume's last-candidate success (no new CP pushed), or (c) the CP's
    // OnPrune when a cut discards it (chunk 245 — fired exactly once, and
    // cleared on pop so a resumed CP can never also prune). Discard paths
    // with no hook (exception unwind, query teardown) just drop the buffer
    // to the .NET GC — same as pre-431, never a double-hand-out.
    private Clause[]? _retractSnapshotSpare;
    private const int RetractSnapshotSpareMaxLen = 4096;

    /// <summary>chunk 431 — returns a buffer with at least
    /// <paramref name="minLength"/> slots for a retract tail snapshot,
    /// reusing the per-engine spare when it fits.</summary>
    internal Clause[] RentRetractSnapshot(int minLength)
    {
        Clause[]? spare = _retractSnapshotSpare;
        if (spare is not null && spare.Length >= minLength)
        {
            _retractSnapshotSpare = null;
            return spare;
        }
        return new Clause[minLength];
    }

    /// <summary>chunk 431 — hands a snapshot buffer back for reuse. Clears
    /// the used range so the pool never pins retracted clause ASTs alive,
    /// and keeps the larger of (current spare, returned buffer), capped so
    /// one huge predicate can't park a giant array on the engine.</summary>
    internal void ReturnRetractSnapshot(Clause[] buffer, int usedCount)
    {
        System.Array.Clear(buffer, 0, usedCount);
        if (buffer.Length > RetractSnapshotSpareMaxLen) return;
        Clause[]? spare = _retractSnapshotSpare;
        if (spare is null || spare.Length < buffer.Length)
            _retractSnapshotSpare = buffer;
    }

    /// <summary>Removes the clause object identical to <paramref name="clause"/>
    /// from the dynamic store (used after the runtime caller has matched it
    /// via unification on a materialised heap copy). When ADR-015 chunk C
    /// chain state exists for the functor, also patches the matching
    /// clause's <c>died</c> slot in the running program's bytecode so an
    /// already-compiled dispatch's <c>check_visible</c> filters it out
    /// from now on.</summary>
    internal bool RemoveDynamicByReference(
        Activation engine, int functorId, Clause clause, int knownIndex = -1)
    {
        if (!_dynamicClauses.TryGetValue(functorId, out var list)) return false;
        // Chunk 423: retract's first step scans the live list, so it
        // already knows the match's index — trust it after a cheap
        // reference check instead of an O(N) IndexOf (Blint: 80K
        // retracts × an ~125-entry IndexOf walk). The resume path
        // scans a tail snapshot whose indices don't map; it passes -1.
        int idx = knownIndex >= 0 && knownIndex < list.Count
                  && ReferenceEquals(list[knownIndex], clause)
            ? knownIndex
            : list.IndexOf(clause);
        if (idx < 0) return false;
        // Chunk 155d: for a chunk-155a indexed predicate, capture the
        // matched clause's body address from the var chain BEFORE
        // removing the clause from _dynamicClauses (the var-chain
        // walk reads idx-indexed entries in their pre-removal order).
        bool isIndexed = IsExtensibleIndexedLayout(engine, functorId);
        int retiredBodyAddr = -1;
        if (isIndexed)
            retiredBodyAddr = FindBodyAddrForClauseIndex(engine, functorId, idx);
        list.RemoveAt(idx);
        InvalidateDynamicCache(functorId);
        // Phase 33 — a retract from a non-owner engine (a nested query
        // rebuilt the host buffer since this engine's setup) patches only
        // this engine's buffer; the host's newer buffer would keep the
        // clause visible on cross-query reuse. Force a rebuild from the
        // (already-updated) store. Owner engines pay nothing.
        if (!EngineOwnsHostBuffer(engine)) InvalidatePersistent();
        // For the chunk-127 chain layout, PatchDiedFromChain walks
        // the predicate's single chain and patches entry[idx]'s
        // died slot. For the chunk-155a indexed layout, the chain
        // state populated by PopulateDynChainFor lists every chain
        // entry from every bucket + the var chain in CONTIGUOUS
        // bytecode order (PopulateDynChainFor does a linear walk),
        // so entry[idx] maps to an unrelated bucket's died slot
        // — skip the chunk-127 path here and let chunk-155d handle
        // the multi-chain patching below.
        if (!isIndexed)
        {
            // Phase 33 — patch by CLAUSE IDENTITY, not by store index. The
            // index-based patch was sound when the single chain mirrored
            // _dynamicClauses 1:1; with per-engine chain tables an engine's
            // chain can lag the store (a broadcast skipped by a guard, or
            // entries added while this engine didn't exist), so the store
            // index lands on the WRONG entry — killing a live clause in
            // this engine's own view while the retracted one stays visible
            // (observed as Logtalk's loading-stack "ghost" entries).
            PatchDiedFromChainByClause(engine, functorId, clause);
            // Phase 20: reclaim accumulated dead clauses from the chain
            // when it's safe (no in-progress enumeration of this
            // predicate). An assert+retract loop (e.g. next_char_i) would
            // otherwise grow the chain with retracted-but-still-linked
            // clauses that every dispatch must skip via check_visible —
            // profiling Blint showed 99.3% of dynamic dispatch walking
            // dead clauses.
            TryReclaimDeadDynamicChain(engine, functorId);
        }
        // Phase 33 — broadcast the retract to every other live engine's
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
    /// clauses in a chunk-127 dynamic chain before reclamation kicks in.
    /// Re-threading costs O(live) pointer patches (it does NOT recompile
    /// anything — see <see cref="GarbageCollectClauses"/>), so this only
    /// amortises the choice-point scan and the patch writes. Every
    /// dispatch between reclaims walks up to this many tombstones — the
    /// real cost for read-heavy churn idioms (Blint's <c>next_char_i</c>
    /// unget buffer is READ via <c>call/1</c> dispatch ~105K times per
    /// lint, each walking the tombstones the threshold lets linger).
    /// Chunk 422 swept the value on Blint's deterministic opcode count:
    /// 32→29.21M, 16→28.40M, 8→28.02M, 4→27.78M, 2→27.69M. 4 is the
    /// knee — below it the per-fire fixed costs (the CP-stack scan and
    /// the chainAddrs set) buy almost nothing.</summary>
    private const int ReclaimDeadThreshold = 4;

    /// <summary>Chunk 420 — number of times the automatic dead-chain
    /// reclamation actually fired across this engine's lifetime.
    /// Deterministic diagnostic for tests; the re-thread itself lives in
    /// the persistent program bytes.</summary>
    public long ChainReclaims { get; private set; }

    /// <summary>Phase 20 — physically drops retracted-but-still-linked
    /// clauses from a dynamic predicate's chunk-127 chain by rebuilding
    /// it from the live clauses, but ONLY when no in-progress enumeration
    /// could still need them (ISO logical update view). A clause
    /// retracted while a call is enumerating the predicate must stay
    /// visible to that call; such a call has a choice point whose resume
    /// address (SavedBp) points at a chunk in this chain. So reclamation
    /// is safe exactly when no active choice point resumes into the
    /// chain. A fresh call re-samples the current generation at
    /// enter_dynamic and never sees the dropped clauses.
    ///
    /// <para>Chunk 420 dropped the old <c>dead &lt; Entries.Count</c>
    /// gate (a chunk-150-era leftover from when reclamation recompiled
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
        // Only the plain chunk-127 trampoline+chain layout. Indexed
        // dynamic predicates (chunk 155/156) use the rebuild-on-mutate
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
        // entries in place (chunk 150). This keeps the trampoline at its
        // address, so every caller's already-baked Call operand stays
        // valid (rebuilding the trampoline at a new address would orphan
        // them); it only patches the in-chunk <next> links to bypass the
        // dead entries, and drains the dead chunks to the free list for
        // reuse by later assertz/asserta. The chunk-150 "avoid mid-query
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
        _dynamicClauses.Remove(functorId);
        _dynamicFunctors.Remove(functorId);
        InvalidateDynamicCache(functorId);
        // Chunk 151b: dropping a dynamic functor changes the
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
        // Phase 33 — broadcast: suspended engines' dispatch must also
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
            // Chunk 150: stage incremental chunks for GC reclamation.
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
        return !string.IsNullOrEmpty(name) && name[0] != '$';
    }

    /// <summary>Removes every clause of a dynamic functor while KEEPING its
    /// <c>:- dynamic</c> declaration (unlike <see cref="AbolishDynamic(int)"/>):
    /// calls fail instead of raising, and a later assert works normally.
    /// Mid-query correct — patches the live chains' <c>died</c> slots (this
    /// engine + suspended siblings) and runs the full invalidation funnel
    /// (caches, IL eviction, the ADR-034 mutated-fids set).</summary>
    internal void ClearDynamicClauses(Activation engine, int functorId)
    {
        if (_dynamicClauses.TryGetValue(functorId, out var list)) list.Clear();
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
        foreach (var (fid, list) in _dynamicClauses)
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
        foreach (var (fid, list) in _dynamicClauses)
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
        foreach (var (fid, list) in _dynamicClauses)
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

    /// <summary>Chunk 150 — re-threads <paramref name="functorId"/>'s
    /// chain through only its live entries, bypassing every dead
    /// (retracted or abolished) entry in the running bytecode. The
    /// dead entries' bytecode is left in place (orphaned but
    /// harmless); the win is dispatch speed — future calls walk
    /// O(live) entries instead of O(ever-asserted).
    ///
    /// <para>Safe to call between queries. The chunk-120 design
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

        // Phase 33 — skip the reclaim when the chain's cached offsets are
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
        // emitted by chunk 128 with native try_me_else. The GC never
        // touches the trampoline or the head opcode (the alternative
        // would be promoting a non-head retry_me_else to try_me_else,
        // which is safe only when the entry has the chunk-128
        // 4-Nop-pad — a native 5-byte retry_me_else from assertz has
        // its check_visible at bytes 5-8 and can't be widened in
        // place). Instead, GC re-threads the <next> chain starting
        // from the head: dead entries are skipped by patching the
        // previous live entry's (or the head's) <next> to point at
        // the next live entry's chain-instruction address.
        //
        // If the head itself is dead (retracted from the asserta'd
        // chunk-128 head, or the empty stub that's always "dead"
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
        // chunk; chunk-120's check_visible then filters it by captured
        // view-gen — but only if the bytecode is intact. Recycling an
        // address an in-flight CP still references would let a later
        // assertz overwrite a live retry_me_else (observed as
        // "RetryMeElse without an active choice point"). The bypassed
        // bytes are reclaimed by the chunk-158 persistent compaction
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
    /// <summary>Chunk 256 — strips the <c>&lt;module&gt;$</c>
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

    /// <summary>Chunk 255 — for a source-stripped bundle the engine
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

    /// <summary>Chunk 254 — enumerates the AST clauses backing
    /// <paramref name="functorId"/>. Pulls from every user module's
    /// <c>Clauses</c> list (filtering by head functor) for static
    /// predicates, and from <c>_dynamicClauses[fid]</c> for dynamic
    /// ones. The AST retains the original <see cref="VarTerm.Name"/>
    /// the parser captured — listing prints them as the user wrote
    /// them, without a heap round-trip that would replace them with
    /// synthetic <c>_GN</c> names.</summary>
    internal IEnumerable<Shumway.Compiler.Ast.Clause> ClausesForListing(int functorId)
    {
        foreach (var (name, manifest) in _modules)
        {
            if (name == Prelude.ModuleName || name == Clpfd.ModuleName) continue;
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
        if (_dynamicClauses.TryGetValue(functorId, out var dyn))
            foreach (var c in dyn) yield return c;
    }

    internal IEnumerable<(int FunctorId, bool IsDynamic)> ListablePredicates()
    {
        var seen = new HashSet<int>();
        foreach (var (name, manifest) in _modules)
        {
            if (name == Prelude.ModuleName || name == Clpfd.ModuleName) continue;
            foreach (var c in manifest.Clauses)
                if (TryExtractHead(c, out string n, out int a))
                {
                    int fid = FunctorTable.Intern(
                        AtomTable.Intern(n, permanent: true).Id, a);
                    if (seen.Add(fid)) yield return (fid, false);
                }
        }
        foreach (var (fid, clauses) in _dynamicClauses)
            if (clauses.Count > 0 && seen.Add(fid)) yield return (fid, true);
        // Chunk 255: source-stripped bundles populate
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

    /// <summary>Chunk 255 — true when the functor is part of the
    /// always-loaded prelude / clpfd library. Listing skips these
    /// the same way it skips builtins.</summary>
    private bool IsLibraryFunctor(int fid)
    {
        if (_modules.TryGetValue(Prelude.ModuleName, out var pre)
            && pre.PublicFunctors.Contains(fid)) return true;
        if (_modules.TryGetValue(Clpfd.ModuleName, out var cl)
            && cl.PublicFunctors.Contains(fid)) return true;
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
        foreach (int fid in _dynamicFunctors)
            if (seen.Add(fid)) yield return fid;
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
        }
        _staticHeadFunctorsCache = set;
        return set;
    }

    /// <summary>True iff <paramref name="functorId"/> is the functor of any
    /// loaded predicate — static, dynamic, or builtin. Backs the
    /// ground-mode case of <c>current_predicate/1</c>.</summary>
    internal bool HasPredicate(int functorId)
    {
        if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(functorId, out _))
            return true;
        if (_dynamicFunctors.Contains(functorId)) return true;
        return StaticHeadFunctors().Contains(functorId);
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
        if (!HasPredicate(functorId)) return props;
        int kind;
        if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(functorId, out _)
            || _preludeFunctors.Contains(functorId))
            kind = AtomTable.Intern("built_in", permanent: true).Id;
        else if (_dynamicFunctors.Contains(functorId))
            kind = AtomTable.Intern("dynamic", permanent: true).Id;
        else
            kind = AtomTable.Intern("static", permanent: true).Id;
        props.Add(kind);
        props.Add(AtomTable.Intern("defined", permanent: true).Id);
        return props;
    }

    private List<Clause> GetOrCreateDynamicSlot(int fid)
    {
        if (!_dynamicClauses.TryGetValue(fid, out var list))
        {
            list = new List<Clause>();
            _dynamicClauses[fid] = list;
        }
        return list;
    }

    /// <summary>retractall/1 modifiability check (SWI / SICStus semantics):
    /// returns <c>true</c> when the predicate is dynamic (so the retract loop
    /// should run), <c>false</c> when it is UNDEFINED (retractall is then a
    /// silent no-op — the predicate is left undefined, no dispatch trampoline is
    /// fabricated), and throws <c>permission_error(modify, static_procedure)</c>
    /// for a static procedure or a builtin (you can't retractall those).</summary>
    internal bool IsRetractAllModifiable(int fid)
    {
        if (_dynamicFunctors.Contains(fid)) return true;
        if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(fid, out _) || HasStaticClauses(fid))
            throw new Shumway.Core.PrologRuntimeException(
                "permission_error", "modify,static_procedure");
        return false;   // undefined → retractall is a no-op
    }

    private void EnsureDynamic(int fid)
    {
        if (_dynamicFunctors.Contains(fid)) return;

        // Phase 19+ — implicit_dynamic flag (default true) auto-
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
            _dynamicFunctors.Add(fid);
            if (!_dynamicClauses.ContainsKey(fid))
                _dynamicClauses[fid] = new List<Clause>();
            // Chunk 430 — the dynamic-functor set feeds the ModuleRewrite
            // contexts; this is the one mutation path that doesn't run
            // through InvalidatePersistent, so advance the derivation
            // generation explicitly. (Auto-promotion can't actually change
            // a static rewrite — the promoted functor has no static
            // clauses, so it was never module-local — but a one-time
            // re-transform per promotion is cheap insurance.)
            _derivationGen++;
            return;
        }

        // Chunk 131e: ISO §7.12.2.h — modifying a static procedure
        // is permission_error(modify, static_procedure, Name/Arity).
        // The Detail string carries the indicator for diagnostic
        // continuity; the translated form lands in the second slot
        // of the permission_error compound through the standard
        // TranslateRuntimeError path (the third "Obj" slot stays as
        // an anonymous variable for now — a richer Term-valued
        // exception payload is queued for later in chunk 131).
        // Detail encodes Operation,ObjectType — TranslateRuntimeError
        // splits and builds the three-arg permission_error compound
        // (the third Obj slot stays as an anonymous variable, since
        // PrologRuntimeException can't carry a Term yet).
        throw new Shumway.Core.PrologRuntimeException(
            "permission_error", "modify,static_procedure");
    }

    /// <summary>Phase 19+ — emits a fresh empty-dynamic trampoline for
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
    ///   resolve — kept for symmetry with the chunk-127 append
    ///   path).</item>
    /// <item>Adds <paramref name="fid"/> to the engine's address map
    ///   (the underlying dictionary, cast through the
    ///   <see cref="IReadOnlyDictionary{TKey,TValue}"/> facade).</item>
    /// <item>Calls <see cref="PopulateDynChainFor"/> so
    ///   <see cref="_dynChains"/> picks up the trampoline's
    ///   <c>TailNextAddr</c> / <c>HeadClauseAddr</c> /
    ///   <c>TrampolineExecuteOperandAddr</c>, making subsequent
    ///   chunk-127 / chunk-128 in-place assertz / asserta
    ///   extensions work just like declared-dynamic
    ///   predicates.</item>
    /// </list></summary>
    private void MaterializeDynamicTrampoline(Activation engine, int fid)
    {
        // Root fix: never append at a stale owner position (see
        // ResyncOwnerAppendPosition).
        ResyncOwnerAppendPosition(engine);
        // Phase 33 — capture buffer ownership before AppendCode (growth
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
            DefaultModuleName, new HashSet<int>(), _dynamicFunctors);
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
        // body has none, but the path mirrors the chunk-127 append for
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

        // Register the trampoline in the address map. The map was
        // created as a Dictionary<int,int> at SetupQueryFromTerm and
        // assigned to the engine as IReadOnlyDictionary — cast back to
        // mutate. If the host ever switches to an immutable
        // implementation this path needs revisiting.
        if (addrMap is not Dictionary<int, int> mutableMap)
            throw new InvalidOperationException(
                "implicit_dynamic mid-query trampoline materialise: "
                + "CurrentFunctorAddresses is not a mutable Dictionary — "
                + "no auto-promote path available.");
        mutableMap[fid] = trampolineAddr;

        // Populate _dynChains[fid] manually. We can't use
        // PopulateDynChainFor here because it would associate the
        // stub-clause's check_visible with whichever user clause is
        // already in _dynamicClauses (host.Assertz appends to
        // _dynamicClauses before calling AppendDynamicClauseIncremental,
        // so by the time MaterializeDynamicTrampoline runs the list
        // already holds the asserted clause). The mismatch would
        // make retract patch the stub's died slot instead of the
        // real clause's, leaving the latter visible.
        //
        // Manual layout: only the trampoline-level offsets matter for
        // chunk-127's incremental append — the chunk emits the real
        // clause's own retry_me_else + check_visible + body and adds
        // a fresh DynChainEntry to chain.Entries pointing at the
        // emitted chunk. So _dynChains[fid].Entries stays empty here.
        var chain = new DynChainState
        {
            TrampolineExecuteOperandAddr = trampolineAddr + 2,
            HeadClauseAddr = trampolineAddr + 6,
            TailNextAddr = trampolineAddr + 7,
        };
        // Phase 33 — record into THIS engine's chain table: the
        // trampoline lives in this engine's buffer.
        GetOrCreateChainTable(engine).Chains[fid] = chain;

        // Sync the host's persistent program reference — engine.AppendCode
        // may have reallocated the buffer (owner engine); a non-owner
        // engine's trampoline exists only in its own buffer, so force the
        // next setup to rebuild from _dynamicFunctors/_dynamicClauses.
        SyncOrInvalidateAfterMutation(engine, ownsHost);
    }

    /// <summary>
    /// Phase 33 (Logtalk bring-up) — live-links a batch of newly consulted
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
    private void LinkConsultedStaticPredicatesLive(
        Activation engine, IReadOnlyList<Clause> newStaticClauses, string moduleName)
    {
        if (newStaticClauses.Count == 0) return;
        // Only meaningful with a live query in flight: a program buffer, a
        // mutable address map, and a mutable switch-table list. Absent any,
        // the predicates link normally at the next setup.
        if (engine.CurrentProgram is null
            || engine.CurrentFunctorAddresses is not Dictionary<int, int> addrMap
            || engine.SwitchTables is not { } switchTables)
            return;
        if (!_modules.TryGetValue(moduleName, out var manifest))
            return;
        // Phase 33 — capture buffer ownership before AppendCode below.
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
            helperIdProvider: NextMetaHelperId);
        // Body-call mangling locals: the batch's own head functors. For a
        // self-contained consulted file (a Logtalk entity's scratch code)
        // these are exactly the predicates it defines; cross-batch calls
        // resolve through the bare-name aliases merged below.
        var locals = ComputeLocalFunctors(transformed, manifest.PublicFunctors);
        if (_precompiledModuleLocals.TryGetValue(moduleName, out var bundleLocals))
            locals.UnionWith(bundleLocals);
        var ctx = new ModuleRewrite.Context(moduleName, locals, _dynamicFunctors);
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
                _literalPools, dynamicFunctors: _dynamicFunctors,
                failStubAddr: failStubAddr);
            link = new Shumway.Compiler.Wam.Linker().Link(
                module.Predicates, loadOffset,
                externalSymbols: addrMap,
                switchTableIdBase: switchTables.Count);
        }
        catch (InvalidOperationException)
        {
            // A structural issue (e.g. a duplicate head across the batch):
            // never fail the consult — the next top-level setup links the
            // whole module coherently.
            return;
        }

        engine.AppendCode(link.Bytecode);
        byte[] prog = engine.CurrentProgram!;

        // Merge new addresses + bare-name aliases so both direct calls in
        // later batches and runtime meta-calls resolve the new predicates.
        var visible = engine.LiveConsultVisibleFids ??= new HashSet<int>();
        foreach (var (fid, a) in link.Addresses)
        {
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
        // loadOffset + Offset + 1 (skip the Call opcode byte). Phase 33:
        // the list lives on the per-buffer chain table — its positions are
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
    /// Phase 33 (Logtalk bring-up) — ensures every dynamic functor that a
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
    private void EnsureLiveDynamicTrampolines(Activation engine)
    {
        if (engine.CurrentFunctorAddresses is not Dictionary<int, int> addrMap)
            return;
        if (engine.CurrentProgram is null) return;

        // A trampoline the setup (or an earlier mid-query materialise) built
        // starts with enter_dynamic at its address. Anything else — an
        // unresolved sentinel, or no entry at all — means this dynamic
        // functor has no live home yet.
        List<int>? materialized = null;
        foreach (int fid in _dynamicFunctors)
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
                if (_dynamicClauses.TryGetValue(fid, out var cs))
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
    private void ApplyConsultSetPrologFlag(string flagName, string valueName)
    {
        switch (flagName)
        {
            case "implicit_dynamic":
                if (valueName == "true") _flags.ImplicitDynamic = true;
                else if (valueName == "false") _flags.ImplicitDynamic = false;
                break;
            case "arity_compat":
                // Phase 30 — consult-time directive form. The ClauseReader's
                // pre-pass already flipped the live lexer for THIS file; this
                // records it for subsequent consults.
                if (valueName == "true") _flags.ArityCompat = true;
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

    /// <summary>Phase 19+ — walks every clause's body looking for
    /// <c>assertz(Head)</c>, <c>asserta(Head)</c>, or <c>assert(Head)</c>
    /// with a literal-callable Head (an atom or a compound), and
    /// auto-declares the corresponding functor as dynamic when it has
    /// no static clauses and isn't already a registered builtin. This
    /// runs at consult time so the next query setup links the
    /// predicate with a real dynamic trampoline; a first-time assertz
    /// at runtime then has somewhere to put the new clause and
    /// subsequent calls dispatch to it.</summary>
    private void CollectImplicitDynamics(IEnumerable<Clause> clauses, HashSet<int> publicsInSameConsult)
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
            if (_dynamicFunctors.Contains(fid)) continue;
            if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(fid, out _)) continue;
            if (HasStaticClauses(fid)) continue;
            // Also skip if the same consult is about to define this
            // functor as a public (static) predicate — caught by
            // looking at publics + the about-to-be-added clauses.
            if (publicsInSameConsult.Contains(fid)) continue;
            if (ClausesDefineFunctor(clauses, fid)) continue;
            _dynamicFunctors.Add(fid);
            if (!_dynamicClauses.ContainsKey(fid))
                _dynamicClauses[fid] = new List<Clause>();
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
            // chunk 431: read-through the AST node's cached ids (seeded by
            // TermReader on the assert path) — drops a string-keyed table
            // probe per assert. The lazy intern is transient; ClauseCompiler
            // promotes asserted predicate names permanent when it compiles
            // the clause, and promotion preserves the id.
            AtomTerm a => FunctorTable.Intern(a.ResolveAtomId(), 0),
            CompoundTerm c => c.ResolveFunctorId(),
            // Chunk 131e: ISO assertz/asserta/retract — a clause head
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

    /// <summary>Snapshot of every module currently loaded into the engine.
    /// Useful for tests and tooling; the underlying objects are live and
    /// shouldn't be mutated directly.</summary>
    public IReadOnlyDictionary<string, ModuleManifest> Modules => _modules;

    /// <summary>Chunk 73 — every <c>:- mode</c> declaration the engine
    /// has consulted, aggregated across all modules into one
    /// <see cref="Shumway.Compiler.Modes.ModeTable"/>. Built fresh on
    /// each access so it always reflects the current module set. The
    /// Phase-3 specialised code generator reads this; tooling can call
    /// <see cref="Shumway.Compiler.Modes.ModeTable.Validate"/> to
    /// surface declarations on never-defined predicates.</summary>
    public Shumway.Compiler.Modes.ModeTable Modes
    {
        get
        {
            var table = new Shumway.Compiler.Modes.ModeTable();
            foreach (var manifest in _modules.Values)
                foreach (var declList in manifest.ModeDeclarations.Values)
                    foreach (var decl in declList)
                        table.Add(decl);
            return table;
        }
    }

    /// <summary>Functor ids the engine has clauses for — static
    /// (consulted source, in any module) or dynamic (declared
    /// <c>:- dynamic</c> or runtime-asserted). Used as the
    /// "defined predicates" input to
    /// <see cref="Shumway.Compiler.Modes.ModeTable.Validate"/>.</summary>
    public IReadOnlySet<int> DefinedFunctors()
    {
        var set = new HashSet<int>();
        foreach (var manifest in _modules.Values)
            foreach (var clause in manifest.Clauses)
                if (TryExtractHead(clause, out string n, out int a))
                    set.Add(FunctorTable.Intern(
                        AtomTable.Intern(n, permanent: true).Id, a));
        foreach (int fid in _dynamicFunctors) set.Add(fid);
        foreach (int fid in _dynamicClauses.Keys) set.Add(fid);
        return set;
    }

    /// <summary>If the most recent <see cref="Query"/> / <see cref="QueryAll"/>
    /// invocation was terminated by <c>halt/0</c> or <c>halt/1</c>, this
    /// holds the exit code requested. <c>null</c> when no halt has fired.
    /// Reset to <c>null</c> at the start of each query.</summary>
    public int? LastHaltExitCode { get; private set; }

    /// <summary>The parser operator table used by this engine. Reflects
    /// every <c>:- op(P, T, N)</c> directive consulted so far, including
    /// operators introduced by libraries (e.g. CLP(FD)'s <c>in</c>,
    /// <c>#=</c>). Exposed so renderers can produce reader-friendly
    /// output (<c>A in 6..9</c> rather than <c>in(A, ..(6, 9))</c>) for
    /// terms that mention library operators.</summary>
    public OperatorTable Operators => _operators;

    /// <summary>ADR-035 — the debug session every query's activation is born
    /// with, or <c>null</c> (the normal case) for an undebugged engine. Set by
    /// <see cref="SetTracing"/> today; a Concord debug session will set it
    /// too.</summary>
    internal Shumway.Core.IDebugSession? DebugSession { get; private set; }

    /// <summary>True while a four-port tracer is attached (<c>trace/0</c>).</summary>
    public bool Tracing => DebugSession is Debugging.DebugTracer;

    /// <summary>ADR-035 — the goal the running query was set up from, as the user would
    /// read it back. The <c>__query__</c> wrapper is compiled, not written, so nothing
    /// downstream of compilation knows what it says; a call stack whose top frame is a bare
    /// <c>?-</c> tells the user nothing. Captured at query setup (debug sessions only —
    /// rendering a term is not free, and an undebugged engine has no one to tell).</summary>
    internal string? CurrentQueryText { get; private set; }

    /// <summary>What to CALL the running query in a debugger, when the goal the engine was
    /// handed is not the goal the user typed. A top level wraps what you type (Shumway's own
    /// wraps it in a <c>copy_term/3</c> so it can show residual constraints), and rendering
    /// that back produced a frame named after machinery the user never wrote. Set it to what
    /// they typed; the engine falls back to rendering the goal when it is not set.</summary>
    public string? QueryLabel { get; set; }

    /// <summary>Turns the four-port tracer on or off (the engine side of
    /// <c>trace/0</c> / <c>notrace/0</c>). Port lines go to
    /// <paramref name="output"/>, defaulting to the engine's own output sink so
    /// a hosted engine's trace lands wherever its program's writes do.</summary>
    public void SetTracing(bool on, System.IO.TextWriter? output = null)
        => DebugSession = on ? new Debugging.DebugTracer(this, output ?? Out) : null;

    // ----- ADR-035: breakpoints -----
    //
    // The armed SOURCE SITES are the truth. The byte patches in the program are
    // derived from them, and are re-derived whenever the code space changes (a
    // relink, a compaction, a consult) — which is why a breakpoint set once keeps
    // working across queries instead of pointing at whatever moved into its old
    // address.

    private readonly HashSet<int> _breakpointSites = new();
    private readonly Dictionary<int, byte> _breakpointPatches = new();
    private HashSet<int> _compiledSites = new();
    private byte[]? _patchedProgram;

    /// <summary>ADR-035 — the source sites currently armed
    /// (<see cref="Shumway.Core.DebugSiteTable"/> ids).</summary>
    public IReadOnlyCollection<int> Breakpoints => _breakpointSites;

    /// <summary>ADR-035 — arms every stop site on this source line, and returns how
    /// many bound. Zero means the breakpoint cannot bind ANYWHERE below the line: the
    /// file has no code left — or it belongs to a predicate compiled without debug
    /// (<c>:- disable_debug.</c>), or the program is not debug-compiled at all. A
    /// debugger renders that as a hollow breakpoint rather than pretending it took.
    ///
    /// <para>A line with no stop site of its own — blank, a comment, or a rule's head,
    /// whose "clause entered" point IS its first goal's — snaps FORWARD to the next
    /// line that has one, which is what every debugger does with a breakpoint set on a
    /// line that is not code. <see cref="BoundLine"/> reports where it landed.</para>
    ///
    /// <para>Binding is decided against THIS engine's compiled code, not against the
    /// global site table: the table is process-wide, so two engines that both
    /// consulted a string source share its site ids, and only the code that is
    /// actually loaded here can be stopped in. Forces the code space to link if it
    /// has not yet, since before that there is nothing to answer with.</para></summary>
    public int AddBreakpoint(string file, int line) => AddBreakpoint(file, line, null);

    /// <summary>ADR-035 D5 — a CONDITIONAL breakpoint: <paramref name="condition"/> is a
    /// Prolog goal evaluated when the breakpoint is reached, in the frame it fired in
    /// (its variables substituted by name, like the Immediate window's). The breakpoint
    /// stops only when the goal SUCCEEDS; a goal that fails lets the program run on as if
    /// the breakpoint were not there. A condition that cannot run — a syntax error, an
    /// exception, a timeout — STOPS and says why (see
    /// <see cref="DebugStopEvent.ConditionError"/>): a broken condition that silently
    /// swallowed its breakpoint would be undiagnosable. Null (the 2-arg overload) means
    /// unconditional — and REPLACES any previous condition on this breakpoint, because
    /// the debugger writes its whole desired state each time.</summary>
    public int AddBreakpoint(string file, int line, string? condition)
    {
        // Serialized against query setup — the session's idle watcher calls this from
        // its own thread, and F9 landing exactly as a query starts raced the setup's
        // table rebuild. See SetupQueryFromTerm.
        lock (_debugArmGate)
        {
            // Remembered whether it binds or not. A breakpoint set on a file that has not
            // been consulted YET is the normal case under a launch — the user draws it,
            // then starts the program — and binding it against code that does not exist is
            // not possible, so for want of this line it was quietly dropped and the program
            // ran clean through every breakpoint in it. It binds in
            // RebindPendingBreakpoints, when the code arrives.
            _requestedBreakpoints.Add((file, line));
            if (string.IsNullOrWhiteSpace(condition))
                _breakpointConditions.Remove((file, line));
            else
                _breakpointConditions[(file, line)] = condition!;

            EnsureCodeLinked();
            int fileId = Shumway.Core.DebugSiteTable.InternFile(file);

            int target = SnapToCompiledLine(fileId, line);
            if (target < 0) return 0;

            int bound = 0;
            foreach (int id in Shumway.Core.DebugSiteTable.SitesOnLine(fileId, target))
            {
                if (!_compiledSites.Contains(id)) continue;
                _breakpointSites.Add(id);
                _breakpointRequests[id] = (fileId, line);
                bound++;
            }
            if (bound > 0) RefreshBreakpoints();
            return bound;
        }
    }

    // Which breakpoint each armed site belongs to — the line the USER asked for, which
    // is not always the line the code is on (a breakpoint on a rule's head binds at its
    // first goal). A debugger that has to match a hit back to the red dot it drew needs
    // the line it drew, not the line we bound.
    private readonly Dictionary<int, (int FileId, int Line)> _breakpointRequests = new();

    // Every breakpoint the debugger has ASKED for, bound or not. The armed sites above are
    // derived from these; these are the truth.
    private readonly HashSet<(string File, int Line)> _requestedBreakpoints = new();

    // ADR-035 D5 — the condition each requested breakpoint carries, keyed like the request.
    // Absent = unconditional (the ordinary case pays one failed lookup per hit, nothing more).
    private readonly Dictionary<(string File, int Line), string> _breakpointConditions = new();

    /// <summary>ADR-035 D5 — the condition of the breakpoint armed at this address, or null
    /// when the breakpoint is unconditional (or the stop is not a breakpoint's). Looked up
    /// through the same site→request map a hit is reported through, so the condition follows
    /// the breakpoint wherever its line actually bound.</summary>
    internal string? BreakpointConditionAt(int pc)
    {
        int siteId = SiteAt(pc);
        if (siteId >= 0 && _breakpointRequests.TryGetValue(siteId, out var request)
            && _breakpointConditions.TryGetValue(
                (Shumway.Core.DebugSiteTable.FileName(request.FileId), request.Line),
                out string? condition))
            return condition;
        return null;
    }

    /// <summary>ADR-035 D4 — binds the breakpoints that had nowhere to bind. Called when new
    /// code arrives (a consult), which is the moment a breakpoint set on a file that had not
    /// been loaded yet finally has something to attach to.
    ///
    /// <para>This is what makes a LAUNCH work at all: the user draws the red dot, presses the
    /// button, and the file is consulted afterwards. Without it the breakpoint is asked for
    /// against an empty program, binds nothing, and is forgotten — and the program runs to
    /// completion untouched, which is exactly what it did.</para></summary>
    private void RebindPendingBreakpoints()
    {
        if (_requestedBreakpoints.Count == 0) return;

        // Relink first. The set of sites a breakpoint may bind to (_compiledSites) is rebuilt
        // when a query is SET UP, not when a file is consulted — so right after a consult it
        // still describes the program as it was before, and the clauses that just arrived are
        // invisible. EnsureCodeLinked would not do it: it sees a linked code space and returns
        // happy. A trivial query is what actually rebuilds the map.
        foreach (var _ in QueryAll("true.")) break;

        // A copy: AddBreakpoint writes to the set (idempotently — it is a set), and
        // enumerating a collection one is adding to is not allowed even when nothing changes.
        // The rebind re-asks for the breakpoint AS THE USER SET IT — condition included; the
        // 2-arg overload would silently strip it.
        foreach ((string file, int line) in _requestedBreakpoints.ToArray())
            AddBreakpoint(file, line,
                _breakpointConditions.TryGetValue((file, line), out string? c) ? c : null);
    }

    /// <summary>ADR-035 — the breakpoint (as the user asked for it) armed at this
    /// address, or null if the stop is not one.</summary>
    internal (string File, int Line)? BreakpointRequestAt(int pc)
    {
        int siteId = SiteAt(pc);
        if (siteId >= 0 && _breakpointRequests.TryGetValue(siteId, out var request))
            return (Shumway.Core.DebugSiteTable.FileName(request.FileId), request.Line);
        return null;
    }

    /// <summary>ADR-035 — the line an <see cref="AddBreakpoint"/> for this line would
    /// actually bind at, or -1 if none. A debugger moves the red dot there.</summary>
    public int BoundLine(string file, int line)
    {
        EnsureCodeLinked();
        return SnapToCompiledLine(Shumway.Core.DebugSiteTable.InternFile(file), line);
    }

    // The source span of every debuggable clause: where its head is written, and the
    // first and last lines it can be stopped at. Built alongside _stopPcs.
    private readonly List<(int FileId, int HeadLine, int FirstLine, int LastLine)>
        _clauseLines = new();

    /// <summary>ADR-035 — the line a breakpoint on <paramref name="line"/> binds at, or
    /// -1 for a hollow one.
    ///
    /// <para>A line with a stop site binds where it is. A line without one binds forward
    /// to the next site OF THE CLAUSE IT IS IN — which is how a breakpoint on a rule's
    /// head (whose entry point IS its first goal's) or on a blank line inside a body
    /// finds its code. It does NOT wander past the end of that clause: a breakpoint on a
    /// blank line between predicates, or inside a <c>:- disable_debug.</c> region, has
    /// nothing to bind to, and saying so is better than silently arming a line the user
    /// was not looking at.</para></summary>
    private int SnapToCompiledLine(int fileId, int line)
    {
        foreach (int id in _compiledSites)
        {
            var site = Shumway.Core.DebugSiteTable.Get(id);
            if (site.FileId == fileId && site.Line == line) return line;
        }

        int best = -1;
        foreach (var clause in _clauseLines)
        {
            if (clause.FileId != fileId) continue;
            if (line < clause.HeadLine || line > clause.LastLine) continue;
            int target = FirstSiteLineAtOrAfter(fileId, Math.Max(line, clause.FirstLine));
            if (target > 0 && (best < 0 || target < best)) best = target;
        }
        return best;
    }

    private int FirstSiteLineAtOrAfter(int fileId, int line)
    {
        int best = -1;
        foreach (int id in _compiledSites)
        {
            var site = Shumway.Core.DebugSiteTable.Get(id);
            if (site.FileId != fileId || site.Line < line) continue;
            if (best < 0 || site.Line < best) best = site.Line;
        }
        return best;
    }

    /// <summary>ADR-035 — disarms the breakpoint the user set on this source line. The
    /// line is snapped exactly as <see cref="AddBreakpoint"/> snapped it, or a breakpoint
    /// set on a rule's head could never be removed from the head line it is drawn
    /// on.</summary>
    public void RemoveBreakpoint(string file, int line)
    {
        lock (_debugArmGate)   // vs query setup: see AddBreakpoint
        {
            _requestedBreakpoints.Remove((file, line));
            _breakpointConditions.Remove((file, line));
            int fileId = Shumway.Core.DebugSiteTable.InternFile(file);
            int target = SnapToCompiledLine(fileId, line);
            if (target < 0) target = line;

            foreach (int id in Shumway.Core.DebugSiteTable.SitesOnLine(fileId, target))
            {
                _breakpointSites.Remove(id);
                _breakpointRequests.Remove(id);
            }
            RefreshBreakpoints();
        }
    }

    /// <summary>ADR-035 — links the code space if no query has yet done so, which is
    /// what makes the engine's stop sites known. A trivial goal is the cheapest way
    /// to ask for exactly the work a query's setup does.</summary>
    private void EnsureCodeLinked()
    {
        if (_currentPredicatesByAddress is not null) return;
        foreach (var _ in QueryAll("true.")) break;
    }

    /// <summary>ADR-035 — turns last-call optimisation on or off for queries from here
    /// on. A debugger turns it OFF, because LCO reclaims a predicate's frame before its
    /// final goal runs and a frame the machine has reclaimed is a frame the debugger
    /// cannot show. To change it for the query already running — which is what a
    /// debugger stopped inside one actually wants — see
    /// <see cref="Debugging.DebugService.SetLastCallOptimisation"/>, or set the
    /// <c>debug_lco</c> prolog flag from a goal.</summary>
    public void SetDebugLastCall(bool on) => _flags.DebugLco = on;

    public void ClearBreakpoints()
    {
        lock (_debugArmGate)   // vs query setup: see AddBreakpoint
        {
            _requestedBreakpoints.Clear();
            _breakpointConditions.Clear();
            _breakpointSites.Clear();
            _breakpointRequests.Clear();
            RefreshBreakpoints();
        }
    }

    /// <summary>ADR-035 — re-applies the patches to the buffer the debugged activation is
    /// executing RIGHT NOW, so a breakpoint set or cleared while a query is stopped takes effect
    /// on that same query. A no-op before the first query, where the next
    /// <see cref="SetupQueryFromTerm"/> will do it anyway.
    ///
    /// <para>The buffer comes from the LIVE activation, never from a cached reference: a
    /// mid-query <c>assertz</c> can reallocate the bytecode array (grow-and-copy), and the
    /// activation then runs the NEW array. Un-patching a stale cached array would restore a dead
    /// buffer and leave the live one with an orphaned <c>Break</c> byte — the crash this replaces.
    /// Whatever the activation runs now is the one and only buffer to touch; if it did not change,
    /// it is the same array, and if it did, it is the new one.</para></summary>
    private void RefreshBreakpoints()
    {
        // _lastQueryEngine is the activation of the query in flight (or the one just stopped),
        // and it is saved/restored around an Immediate-window evaluation, so this is the buffer
        // the code the user is looking at actually runs — following a mid-query realloc.
        byte[]? live = _lastQueryEngine?.CurrentProgram ?? _patchedProgram;
        if (live is null) return;
        // A live buffer already carries our Break bytes (a realloc copies them), so un-patching
        // it must find a Break at every recorded pc — anything else is real drift, not the
        // routine fresh-rebuild case (which only happens at query setup).
        SyncBreakpoints(live, bufferCarriesOurPatches: true);
    }

    /// <summary>ADR-035 — makes <paramref name="program"/>'s patched bytes agree with the armed
    /// sites. First removes the patches this engine applied before, then patches what is armed
    /// now, recording each original byte for the interpreter to re-dispatch a <c>Break</c>.
    ///
    /// <para><paramref name="bufferCarriesOurPatches"/> says whether <paramref name="program"/>
    /// is the buffer our recorded patches live in — true for the buffer the activation runs
    /// (same array or a grow-and-copy of it, which carries the <c>Break</c> bytes) and for a
    /// REUSED persistent buffer at setup; false only for a FRESHLY-REBUILT one, which was linked
    /// clean from the compiled predicates and never carried a Break, so there is nothing to
    /// remove and the recorded originals (from the now-dead buffer) must not be written into
    /// it.</para></summary>
    private void SyncBreakpoints(byte[] program, bool bufferCarriesOurPatches)
    {
        if (bufferCarriesOurPatches)
            foreach (var (pc, original) in _breakpointPatches)
            {
                if ((uint)pc < (uint)program.Length
                    && program[pc] == (byte)Shumway.Core.Opcode.Break)
                    program[pc] = original;
                else
                {
                    // We recorded a Break at this pc but the buffer the activation runs has none.
                    // The guard above keeps us from writing a stale original over live code, but
                    // this should NEVER happen — it means the breakpoint table drifted from the
                    // executed buffer, exactly the class of bug this design exists to prevent, so
                    // surface it loudly for investigation rather than papering over it.
                    string msg = $"breakpoint table out of step: recorded a Break at pc={pc} but "
                        + $"the live buffer holds 0x{(pc < program.Length ? program[pc] : 0):X2}";
                    Debugging.ShumwayDebugHelper.DiagLine("[Shumway diag] " + msg);
                    System.Diagnostics.Debug.Fail(msg);
                }
            }
        _breakpointPatches.Clear();
        _patchedProgram = program;

        if (_breakpointSites.Count == 0 || _currentPredicatesByAddress is null) return;
        foreach (var (predAddr, pred) in _currentPredicatesByAddress)
        {
            foreach (var stop in pred.DebugStops)
            {
                if (!_breakpointSites.Contains(stop.SiteId)) continue;
                int pc = predAddr + stop.Offset;
                if ((uint)pc >= (uint)program.Length) continue;
                if (_breakpointPatches.ContainsKey(pc)) continue;   // one site, many clauses
                _breakpointPatches[pc] = program[pc];
                program[pc] = (byte)Shumway.Core.Opcode.Break;
            }
        }
    }

    // Every stop site in the loaded program, by program address, sorted. Built
    // alongside _compiledSites; empty unless something was compiled debuggable.
    private int[] _stopPcs = Array.Empty<int>();
    private int[] _stopSiteIds = Array.Empty<int>();

    /// <summary>ADR-035 — the source site AT this program address, or -1 if the
    /// address is not a stop site. What a session that receives <c>OnBreak(pc)</c>
    /// uses to say where it stopped.</summary>
    public int SiteAt(int pc)
    {
        int i = Array.BinarySearch(_stopPcs, pc);
        return i >= 0 ? _stopSiteIds[i] : -1;
    }

    /// <summary>ADR-035 — the source site this program address is INSIDE: the last
    /// stop site at or before it. A pc in the middle of a goal's instructions —
    /// which is where the four ports find it — belongs to the goal whose site
    /// precedes it. Returns -1 before the first site in the program.</summary>
    public int SiteAtOrBefore(int pc)
    {
        if (_stopPcs.Length == 0) return -1;
        int i = Array.BinarySearch(_stopPcs, pc);
        if (i < 0) i = ~i - 1;
        return i >= 0 ? _stopSiteIds[i] : -1;
    }

    // Every debuggable clause in the loaded program, by program address, sorted.
    // Built alongside _stopPcs; empty unless something was compiled debuggable.
    private int[] _clauseStarts = Array.Empty<int>();
    private Shumway.Compiler.Wam.DebugClauseFrame[] _clauseFrames
        = Array.Empty<Shumway.Compiler.Wam.DebugClauseFrame>();

    /// <summary>ADR-035 — the first clause at or after a predicate's entry address. A
    /// predicate does not begin with its clause: the dispatch prologue comes first, and the
    /// frame map is keyed by CLAUSE. Used for the top-level query, whose own address is the
    /// only thing we know about it.</summary>
    private int FirstClauseStartAtOrAfter(int predicateAddress)
    {
        if (_clauseStarts.Length == 0) return -1;
        int i = Array.BinarySearch(_clauseStarts, predicateAddress);
        if (i < 0) i = ~i;                       // the first clause that starts after it
        return i < _clauseStarts.Length ? _clauseStarts[i] : -1;
    }

    /// <summary>ADR-035 — the clause executing at this program address.</summary>
    private Shumway.Compiler.Wam.DebugClauseFrame? ClauseAt(int pc)
    {
        if (_clauseStarts.Length == 0 || pc < 0) return null;
        int i = Array.BinarySearch(_clauseStarts, pc);
        if (i < 0) i = ~i - 1;
        if (i < 0) return null;
        var clause = _clauseFrames[i];
        return pc < clause.End ? clause : null;
    }

    /// <summary>ADR-035 — one entry of the Prolog call stack, with the variables of the
    /// clause it is running, rendered as the user wrote them.</summary>
    public readonly record struct DebugFrame(
        string Name, int Arity, string File, int Line, int Pc,
        IReadOnlyList<(string Name, string Value)> Variables)
    {
        /// <summary>ADR-035 — the frame as the CALL it is: the head's arguments with their
        /// CURRENT values, parenthesised and ready to display — <c>(120, foo/2, _G5)</c> —
        /// instantiating as the clause runs. Empty when the clause was not compiled
        /// debuggable (there is no head skeleton to fill in), and for the query and the
        /// omitted-frames sentence, which are not calls.</summary>
        public string HeadArgs { get; init; } = "";

        /// <summary>ADR-035 — which clause of its predicate this frame is running, 1-based,
        /// in source order: the <c>!2</c> of <c>total(...)!2</c>. Zero when unknown.</summary>
        public int ClauseNumber { get; init; }

        public override string ToString() => $"{Name}/{Arity} at {File}:{Line}";
    }

    /// <summary>ADR-035 — the Prolog call stack, innermost frame first, recomposed
    /// from the activation's environment chain. Never from the C# stack: the Tier-0
    /// interpreter runs the whole program inside one <c>Dispatch</c> frame, so the
    /// C# stack says nothing about where Prolog is.
    ///
    /// <para>What the machine reclaims, the debugger cannot show. A predicate whose
    /// frame last-call optimisation has already popped is not on this list — which
    /// is exactly why debug code compiles its last call as
    /// <c>debug_lastcall</c> and a debugger turns <c>debug_lco</c> off.</para></summary>
    public IReadOnlyList<DebugFrame> CaptureFrames(Activation engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return CaptureFrames(engine, engine.P, engine.E, engine.Cp);
    }

    /// <summary>ADR-035 — the call stack of a computation the machine is not standing
    /// in. At the redo port the machine has not yet restored the choice point, so
    /// <c>P</c> and the environment chain still describe the computation that just
    /// failed; the stack the debugger must show is the one the retried clause will run
    /// in, which the choice point carries.</summary>
    public IReadOnlyList<DebugFrame> CaptureFrames(Activation engine, int pc, int e, int cp)
    {
        ArgumentNullException.ThrowIfNull(engine);

        // TWO PASSES, and the reason is the deep stack. A recursion 2 700 frames deep is a
        // real thing to be stopped in, and nobody reads 2 700 frames: what they read is the
        // few at the top (where they are) and the few at the bottom (how they got in). Between
        // them is the same clause 2 600 times.
        //
        // Building all of them means rendering every variable of every one — the expensive
        // part of a stop by far — and then not showing most of them, because the stack has to
        // cross a fixed-size buffer. So the first pass finds the frames and NOTHING else
        // (a binary search apiece), and the second builds only the ones that will be seen,
        // with one synthetic frame in the middle saying how many are not.
        var sites = new List<(int Pc, int Env)>();
        CollectFrameSites(engine, sites, pc, e, cp);

        // One bag per capture: the frames of one stop share their bindings, and their
        // renderings — see DebugValueBag.
        var bag = new DebugValueBag(this, engine);

        var frames = new List<DebugFrame>();
        var plan = BuildFramePlan(sites);
        for (int k = 0; k < plan.Shown.Length; k++)
        {
            if (plan.OmittedCount > 0 && k == plan.OmitAfter)
                frames.Add(new DebugFrame(
                    plan.OmittedCount == 1
                        ? "... 1 frame omitted ..."
                        : $"... {plan.OmittedCount:N0} frames omitted ...",
                    OmittedFramesArity, "", 0, -1, Array.Empty<(string, string)>()));
            int idx = plan.Shown[k];
            AddFrame(engine, frames, sites[idx].Pc, sites[idx].Env, bag);
        }
        return frames;
    }

    /// <summary>ADR-035 — the (pc, environment) behind one frame of the CURRENT stop's
    /// display list, by the index the debugger's frames carry. Mirrors
    /// <see cref="CaptureFrames(Activation, int, int, int)"/>'s head/tail selection exactly,
    /// omitted-frames sentence included (that index answers false: it is not a frame).
    /// What the Immediate window's goal evaluation resolves a frame index against.</summary>
    internal bool TryGetDisplayFrameContext(
        Activation engine, int displayIndex, out int pc, out int env)
    {
        pc = -1;
        env = -1;
        if (displayIndex < 0) return false;
        var sites = new List<(int Pc, int Env)>();
        CollectFrameSites(engine, sites, engine.P, engine.E, engine.Cp);

        var plan = BuildFramePlan(sites);
        if (plan.OmittedCount <= 0)
        {
            if (displayIndex >= plan.Shown.Length) return false;
            (pc, env) = sites[plan.Shown[displayIndex]];
            return true;
        }
        if (displayIndex < plan.OmitAfter)
        {
            (pc, env) = sites[plan.Shown[displayIndex]];
            return true;
        }
        if (displayIndex == plan.OmitAfter) return false;   // "... N frames omitted ..."
        int si = displayIndex - 1;                          // past the sentence
        if (si >= plan.Shown.Length) return false;
        (pc, env) = sites[plan.Shown[si]];
        return true;
    }

    /// <summary>ADR-035 — the variables of one frame as TERMS, not renderings: what the
    /// Immediate window substitutes into a goal. A variable whose slot has not been
    /// written yet (or holds something that is not a term) is simply absent — the goal's
    /// variable of that name stays free.</summary>
    internal IReadOnlyList<(string Name, Term Value)> MaterializeFrameVariables(
        Activation engine, int pc, int env)
    {
        var result = new List<(string, Term)>();
        foreach (var (name, value, _, _) in MaterializeFrameVariablesWithAddresses(engine, pc, env))
            result.Add((name, value));
        return result;
    }

    /// <summary>ADR-035 D5+ — the frame's variables as terms AND as heap ADDRESSES on the
    /// suspended activation, which is what the bind-into-frame commit needs: the address is
    /// the real cell a committed binding unifies against, where the term is only a copy.
    /// <c>Addr</c> is the DEREFERENCED slot address for a heap-referencing slot, or -1 when
    /// the slot holds an inline value (bound immediate — nothing to bind into) or could not
    /// be read. <c>IsAttVar</c> flags an attributed variable (bind-into-frame refuses those:
    /// unifying one schedules hook wakeups the suspended machine is in no state to run).</summary>
    internal IReadOnlyList<(string Name, Term Value, int Addr, bool IsAttVar)>
        MaterializeFrameVariablesWithAddresses(Activation engine, int pc, int env)
    {
        var clause = ClauseAt(pc);
        if (env < 0 || clause is null || clause.Value.Variables.Count == 0)
            return Array.Empty<(string, Term, int, bool)>();

        var result = new List<(string, Term, int, bool)>();
        foreach (var v in clause.Value.Variables)
        {
            try
            {
                Cell cell = engine.GetY(env, v.Slot);
                if (cell.Tag is Tag.RawInt or Tag.PstrBuffer) continue;
                int at;
                int addr = -1;
                if (cell.Tag == Tag.Ref)
                {
                    at = engine.Deref(cell.AsHeapIndex);
                    addr = at;
                }
                else
                {
                    at = engine.AllocateHeap(1);
                    engine.SetHeap(at, cell);
                }
                bool isAttVar = addr >= 0 && engine.GetHeap(addr).Tag == Tag.AttVar;
                result.Add((v.Name, TermReader.Materialize(engine, at), addr, isAttVar));
            }
            catch (Exception)
            {
                // Best-effort, like every frame read: a value that cannot be materialized
                // leaves its variable free rather than failing the whole evaluation.
            }
        }
        return result;
    }

    /// <summary>ADR-035 — everything a nested Immediate-window evaluation clobbers.
    ///
    /// <para>An evaluated goal runs as a REAL query — <c>SetupQueryFromTerm</c>, a fresh
    /// activation, the live database — which is exactly the semantics asked for
    /// (an <c>assertz</c> persists like any mid-query nested activation's). But query setup
    /// also rebuilds the per-query debug tables and the address→predicate map, and the
    /// SUSPENDED query — the one the user is stopped in, and will resume with F5 — still
    /// needs its own: its wrapper's addresses are not in the new map, and a stack walk
    /// through them after the eval would mislabel the bottom of the user's stack. So the
    /// eval brackets itself: save these, run, put them back. The code space itself is
    /// append-only; nothing the eval linked invalidates the suspended query's
    /// addresses.</para></summary>
    internal sealed class DebugEvalScope
    {
        public IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate>? Predicates;
        public int[]? SortedEntries;
        public HashSet<int> CompiledSites = new();
        public int[] StopPcs = Array.Empty<int>();
        public int[] StopSiteIds = Array.Empty<int>();
        public int[] ClauseStarts = Array.Empty<int>();
        public Shumway.Compiler.Wam.DebugClauseFrame[] ClauseFrames
            = Array.Empty<Shumway.Compiler.Wam.DebugClauseFrame>();
        public (int, int, int, int)[] ClauseLines = Array.Empty<(int, int, int, int)>();
        public string? QueryText;
        public string? Label;
        public Activation? LastEngine;
    }

    internal DebugEvalScope BeginDebugEvaluation()
    {
        var scope = new DebugEvalScope
        {
            Predicates = _currentPredicatesByAddress,
            SortedEntries = _sortedPredEntries,
            CompiledSites = _compiledSites,
            StopPcs = _stopPcs,
            StopSiteIds = _stopSiteIds,
            ClauseStarts = _clauseStarts,
            ClauseFrames = _clauseFrames,
            ClauseLines = _clauseLines.ToArray(),
            QueryText = CurrentQueryText,
            Label = QueryLabel,
            LastEngine = _lastQueryEngine,
        };
        _debugEvalDepth++;
        return scope;
    }

    internal void EndDebugEvaluation(DebugEvalScope scope)
    {
        _debugEvalDepth--;
        _currentPredicatesByAddress = scope.Predicates;
        _sortedPredEntries = scope.SortedEntries;
        _compiledSites = scope.CompiledSites;
        _stopPcs = scope.StopPcs;
        _stopSiteIds = scope.StopSiteIds;
        _clauseStarts = scope.ClauseStarts;
        _clauseFrames = scope.ClauseFrames;
        _clauseLines.Clear();
        _clauseLines.AddRange(scope.ClauseLines);
        CurrentQueryText = scope.QueryText;
        QueryLabel = scope.Label;
        _lastQueryEngine = scope.LastEngine;
    }

    /// <summary>ADR-035 D5 — how many debug evaluations (Immediate-window goals, breakpoint
    /// conditions) are running right now. While one is, the OUTER query is suspended
    /// mid-flight — its activation, its Break bytes, its in-flight choice points all live in
    /// the current code space — so a nested query's setup must not treat itself as the safe
    /// point it usually is: no auto-compaction (chunk 158's "no in-flight choice points hold
    /// addresses into it" premise is false here; the compaction is merely deferred to the
    /// next real query), and no breakpoint re-sync (the armed table describes the OUTER
    /// query's buffer and must keep doing so).</summary>
    private int _debugEvalDepth;

    /// <summary>ADR-035 D5 — the persistent buffer was REBUILT by a debug evaluation's
    /// nested setup (the outer query had already invalidated it), which skips the breakpoint
    /// sync: the fresh buffer never received the armed Break bytes. The next real setup
    /// consumes this to pass <c>bufferCarriesOurPatches: false</c> for a buffer it would
    /// otherwise assume it had patched.</summary>
    private bool _persistentRebuiltPatchFree;

    /// <summary>How many frames a stop carries before it starts leaving some out, and how
    /// they are divided when it does: the innermost <see cref="HeadFrames"/> — where the
    /// machine is — and the outermost <see cref="TailFrames"/> — how it got there. What lies
    /// between is the middle of a recursion, and it is the same clause over and over.</summary>
    private const int MaxFrames = 120;
    private const int HeadFrames = 80;
    private const int TailFrames = 20;

    /// <summary>The longest cycle the recursion detector looks for — a period of predicates
    /// that repeats down the stack (1 = plain recursion, 2 = mutual, and so on).</summary>
    private const int MaxCyclePeriod = 8;

    /// <summary>The arity of the "... N frames omitted ..." frame — not a predicate, and not
    /// the query either (which is -1). A debugger renders a negative arity as no arity at
    /// all, so it shows as the sentence it is.</summary>
    private const int OmittedFramesArity = -2;

    /// <summary>ADR-035 — which site indices a stop displays, and where the
    /// "... N frames omitted ..." sentence falls among them. <see cref="Shown"/> lists the
    /// site indices to render, in display order (innermost first); <see cref="OmitAfter"/> is
    /// how many of them precede the sentence (-1 when nothing is elided); <see cref="OmittedCount"/>
    /// is how many sites the sentence stands for.</summary>
    private readonly record struct FramePlan(int[] Shown, int OmitAfter, int OmittedCount);

    /// <summary>ADR-035 — the display plan for a captured stack: the single place that decides
    /// which frames to show and which to elide, so <see cref="CaptureFrames(Activation, int, int, int)"/>
    /// and <see cref="TryGetDisplayFrameContext"/> can never disagree about it.
    ///
    /// <para>Under <see cref="MaxFrames"/> every frame shows. Over it the middle is left out —
    /// but a stack that deep is almost always a RECURSION, the same short cycle of predicates
    /// repeated hundreds of times, and a blind head/tail cut slices through the middle of a
    /// cycle at each edge. <see cref="TryBuildCyclePlan"/> keeps the SAME budget — the innermost
    /// <see cref="HeadFrames"/> and outermost <see cref="TailFrames"/>, so the display never
    /// shrinks below ~100 frames — but SNAPS each cut to a cycle boundary, so both ends show
    /// whole cycles: the innermost (where the machine is) and the outermost (where the recursion
    /// STARTED, together with the non-recursive frames that led into it). A cut may keep a few
    /// frames more than the budget to reach the boundary; that is deliberate. Seeing the origin
    /// end whole is what lets a user read where the chain came from and Run-to-cursor onto the
    /// goal after it. Falls back to a blind head/tail cut when there is no cycle spanning the
    /// cut region.</para></summary>
    private static FramePlan BuildFramePlan(IReadOnlyList<(int Pc, int Env)> sites)
    {
        int n = sites.Count;
        if (n <= MaxFrames)
        {
            var all = new int[n];
            for (int i = 0; i < n; i++) all[i] = i;
            return new FramePlan(all, -1, 0);
        }

        if (TryBuildCyclePlan(sites, out var cyclePlan)) return cyclePlan;

        // Fallback: innermost HeadFrames + the sentence + outermost TailFrames.
        var shown = new int[HeadFrames + TailFrames];
        for (int i = 0; i < HeadFrames; i++) shown[i] = i;
        for (int i = 0; i < TailFrames; i++) shown[HeadFrames + i] = n - TailFrames + i;
        return new FramePlan(shown, HeadFrames, n - HeadFrames - TailFrames);
    }

    /// <summary>Detect the dominant repeating cycle in the stack and build a plan that cuts on
    /// its boundaries. The signature of a frame is its code position (<c>Pc</c>): the same pc
    /// is the same clause resumed at the same call site, so a recursion — however its clauses
    /// are selected — reads as a run of identical pcs (plain recursion, period 1) or a run that
    /// repeats every P frames (mutual recursion, period P). Returns false when no cycle is long
    /// enough to elide, or when the frames OUTSIDE the cycle are themselves too many to show.</summary>
    private static bool TryBuildCyclePlan(
        IReadOnlyList<(int Pc, int Env)> sites, out FramePlan plan)
    {
        plan = default;
        int n = sites.Count;

        // The longest contiguous block that repeats with a period of at most MaxCyclePeriod.
        int bestStart = -1, bestPeriod = 0, bestLen = 0;
        for (int p = 1; p <= MaxCyclePeriod; p++)
        {
            int i = 0;
            while (i < n)
            {
                int runStart = i;
                while (i + p < n && sites[i].Pc == sites[i + p].Pc) i++;
                if (i > runStart)
                {
                    int len = (i - runStart) + p;   // periodic block [runStart, runStart+len)
                    if (len > bestLen) { bestLen = len; bestStart = runStart; bestPeriod = p; }
                }
                i++;
            }
        }

        if (bestStart < 0) return false;

        int fullCycles = bestLen / bestPeriod;
        if (fullCycles < 2) return false;           // not a recursion worth cutting on

        int bandStart = bestStart;
        int bandEnd = bestStart + fullCycles * bestPeriod;   // exclusive, whole cycles

        // Keep the head/tail BUDGET, but move each cut onto a cycle boundary of the band so the
        // two ends show whole cycles. The inner cut rounds UP (>= HeadFrames), the outer cut
        // rounds DOWN (leaves >= TailFrames), so the display stays at least HeadFrames+TailFrames.
        int innerCut = SnapUpToCycle(HeadFrames, bandStart, bandEnd, bestPeriod);
        int outerCut = SnapDownToCycle(n - TailFrames, bandStart, bandEnd, bestPeriod);

        int omitted = outerCut - innerCut;
        if (omitted < bestPeriod) return false;     // the band does not span the cut — head/tail

        // Shown = [0, innerCut) ++ [outerCut, n): the innermost budget (ending on a boundary),
        // then everything from the outer boundary — the outermost whole cycles AND the
        // non-recursive frames that started the chain.
        int shownCount = innerCut + (n - outerCut);
        var shown = new int[shownCount];
        int k = 0;
        for (int i = 0; i < innerCut; i++) shown[k++] = i;
        for (int i = outerCut; i < n; i++) shown[k++] = i;

        plan = new FramePlan(shown, innerCut, omitted);
        return true;
    }

    /// <summary>The smallest cycle boundary at or after <paramref name="x"/> within the band
    /// [<paramref name="bandStart"/>, <paramref name="bandEnd"/>); <paramref name="x"/> itself
    /// when it is before the band (nothing to snap), <paramref name="bandEnd"/> when past it.</summary>
    private static int SnapUpToCycle(int x, int bandStart, int bandEnd, int period)
    {
        if (x <= bandStart) return x;
        if (x >= bandEnd) return bandEnd;
        int m = (x - bandStart + period - 1) / period;   // ceil
        return Math.Min(bandStart + m * period, bandEnd);
    }

    /// <summary>The largest cycle boundary at or before <paramref name="x"/> within the band;
    /// clamped to the band's ends.</summary>
    private static int SnapDownToCycle(int x, int bandStart, int bandEnd, int period)
    {
        if (x <= bandStart) return bandStart;
        if (x >= bandEnd) return bandEnd;
        int m = (x - bandStart) / period;                // floor
        return bandStart + m * period;
    }

    /// <summary>Pass one: WHERE the frames are — a (pc, environment) pair each — without
    /// building any of them. Same walk, same rules, same stopping condition (the query is the
    /// bottom of every stack).</summary>
    private void CollectFrameSites(
        Activation engine, List<(int Pc, int Env)> sites, int pc, int e, int cp)
    {
        // The environment chain holds exactly the clauses that HAVE a frame, innermost
        // first. Only the clause we are standing in can be frameless (a frameless
        // clause makes no non-tail call, so it can never be a caller waiting to
        // resume) — and if it is, the first environment on the chain is already its
        // caller's. That one question decides the whole alignment.
        var envs = new List<int>();
        foreach (int env in engine.EnumerateEnvChain(e)) envs.Add(env);
        bool ownFrame = ClauseAt(pc)?.HasFrame ?? false;

        if (AddSite(sites, pc, ownFrame && envs.Count > 0 ? envs[0] : -1))
            return;

        int envIndex = ownFrame ? 1 : 0;
        foreach (int returnPc in engine.EnumerateCallReturnAddresses(e, cp))
        {
            // A return address points at the instruction AFTER the call, which is
            // where the NEXT goal's code begins — so looking its line up directly
            // would blame a caller for the goal it has not run yet. Step back a byte
            // to land inside the call itself, the goal the frame is really waiting on.
            //
            // The query is the BOTTOM of the stack, and it is not recursive: once it is on
            // the list, the walk is done. What lies past it is the address the query returns
            // to — the top level's own code, which no Prolog frame describes — and it looked
            // enough like the wrapper to be named `?-` a second time. One query, one frame.
            if (AddSite(sites, returnPc - 1, envIndex < envs.Count ? envs[envIndex] : -1))
                break;
            envIndex++;
        }
    }

    /// <summary>Records one frame's site, if the address names a predicate at all. Returns
    /// true when it was the QUERY's — the bottom of the stack, and the end of the walk.
    /// </summary>
    private bool AddSite(List<(int Pc, int Env)> sites, int pc, int env)
    {
        int i = IndexOfPredicateAt(pc);
        if (i < 0) return false;
        // ADR-035 fully-transparent control: a ,/;/-> construct (or its lowered
        // $disj_N / $call_* plumbing) is flow, not a goal — it takes NO frame in
        // the call stack. Skip it but keep walking; the caller advances envIndex
        // regardless, so the next real frame still pairs with its own environment.
        if (IsTransparentControlFunctor(
                _currentPredicatesByAddress![SortedPredicateEntries()[i]].FunctorId))
            return false;
        sites.Add((pc, env));
        return IsQueryEntry(i);
    }

    /// <summary>The predicate whose code contains <paramref name="pc"/>, as an index into
    /// <see cref="SortedPredicateEntries"/>; -1 when the address names none.</summary>
    private int IndexOfPredicateAt(int pc)
    {
        if (_currentPredicatesByAddress is null || pc < 0) return -1;
        var entries = SortedPredicateEntries();
        int i = Array.BinarySearch(entries, pc);
        if (i < 0) i = ~i - 1;
        return i;
    }

    private bool IsQueryEntry(int entryIndex)
    {
        var pred = _currentPredicatesByAddress![SortedPredicateEntries()[entryIndex]];
        var (atomId, _) = FunctorTable.Lookup(pred.FunctorId);
        return DemangleLocalName(AtomTable.GetById(atomId)?.Name ?? "?") == "__query__";
    }

    /// <summary>Is this address really inside the predicate the binary search landed on, or
    /// did the search merely CLAMP to it? The search takes the last entry at or before the
    /// address, so an address past the end of every predicate — a return into the launcher,
    /// say — comes back named as the last one, which is a guess and not a fact.</summary>
    private static bool WithinPredicate(
        Shumway.Compiler.Wam.CompiledPredicate pred, int predicateAddress, int pc)
        => pc >= predicateAddress && pc < predicateAddress + pred.Bytecode.Length;

    /// <summary>A call stack is a column, not a page: a query long enough to wrap turns the
    /// whole stack unreadable, and the tail of a goal is not what identifies it.</summary>
    private static string Ellipsize(string text, int max)
        => text.Length <= max ? text : text.Substring(0, max - 3) + "...";

    /// <summary>How much of a variable's value a frame carries. A stack is a hundred frames
    /// of a few variables each, and it has to fit in one buffer; a single term big enough to
    /// fill it on its own would take the rest of the stack with it.</summary>
    private const int MaxVariableChars = 512;

    /// <summary>Adds the frame at <paramref name="pc"/>. Returns true when it was the
    /// QUERY's — the bottom of the stack, and the end of the walk.</summary>
    private bool AddFrame(
        Activation engine, List<DebugFrame> frames, int pc, int env, DebugValueBag bag)
    {
        if (_currentPredicatesByAddress is null || pc < 0) return false;
        var entries = SortedPredicateEntries();
        int i = Array.BinarySearch(entries, pc);
        if (i < 0) i = ~i - 1;
        if (i < 0) return false;
        var pred = _currentPredicatesByAddress[entries[i]];
        var (atomId, arity) = FunctorTable.Lookup(pred.FunctorId);
        string name = DemangleLocalName(AtomTable.GetById(atomId)?.Name ?? "?");

        // The wrapper the engine puts the goal in. An error's stack trace hides it, and
        // rightly — the user did not write it. A DEBUGGER may not: stopped inside a
        // top-level query the user IS standing there, and a query of nothing but builtins
        // (`?- writeln(uno), debugger_break, writeln(dos).`) has no other frame at all. The
        // debugger showed an empty stack and looked broken. It shows the query now — as
        // `?-`, which is what the user typed, and NOT as a predicate: arity -1 says "this is
        // not a Name/Arity", and the debugger renders it without one.
        bool isQuery = name == "__query__";
        if (isQuery)
        {
            // `?-` on its own says only "a query is running", which the user could see from
            // the fact that they are stopped. It shows the GOAL — the text they typed —
            // because that is the frame's identity, the way `step/2` is a clause's.
            name = CurrentQueryText is null ? "?-" : "?- " + Ellipsize(CurrentQueryText, 120);
            arity = -1;

            // The environment of a query whose frame last-call optimisation has already
            // reclaimed belongs to somebody else by now, and reading the wrapper's variable
            // map out of a stranger's frame produced confident nonsense — the answer reported
            // as a loop counter. If this address is not really inside the wrapper's code (the
            // search only landed here by clamping), there are no variables to be had.
            if (!WithinPredicate(pred, entries[i], pc)) env = -1;

            int firstClause = FirstClauseStartAtOrAfter(entries[i]);
            if (firstClause >= 0) pc = firstClause;
        }

        // ADR-035 — a synthesised control-construct helper ('$catchgoal_N', '$neg_N', …) shows
        // as the construct the user wrote (catch/3, \+/1, …), not the lowered helper. Its head
        // args are the helper's free variables, which do not correspond to the construct's
        // surface arguments, so they are not rendered.
        bool isConstruct = false;
        if (!isQuery)
        {
            var mapped = DebugConstructName(name, arity);
            if (!ReferenceEquals(mapped.Name, name) && mapped.Name != name)
            {
                name = mapped.Name;
                arity = mapped.Arity;
                isConstruct = true;
            }
        }

        int siteId = SiteAtOrBefore(pc);
        var site = siteId >= 0
            ? Shumway.Core.DebugSiteTable.Get(siteId)
            : default;
        string file = siteId >= 0
            ? Shumway.Core.DebugSiteTable.FileName(site.FileId)
            : "";
        var clause = ClauseAt(pc);
        frames.Add(new DebugFrame(
            name, arity, file, siteId >= 0 ? site.Line : 0, pc,
            ReadVariables(engine, pc, env, bag))
        {
            HeadArgs = isQuery || isConstruct ? "" : RenderHeadArgs(engine, clause, env, bag),
            ClauseNumber = isQuery || isConstruct ? 0 : clause?.ClauseNumber ?? 0,
        });
        return isQuery;
    }

    /// <summary>ADR-035 — the variables of the clause running at <paramref name="pc"/>,
    /// read out of the environment frame at <paramref name="env"/> and rendered the way
    /// the user wrote them. An unbound variable renders as <c>_</c> plus its heap cell,
    /// which is how it will keep printing until something binds it.
    ///
    /// <para>Empty when the clause has no frame, or was not compiled debuggable: its
    /// variables live in X registers the next call overwrites, and there is no honest
    /// answer to give. Debug codegen exists precisely to stop that from happening —
    /// it makes every named variable permanent.</para></summary>
    /// <summary>ADR-035 — the frame's head arguments with their current values, rendered
    /// for the call-stack line: <c>total([item(_, 25)|T], Acc, Total)</c> shown as
    /// <c>([item(_, 25)], 10, _G5)</c>. The skeleton is the head as WRITTEN; each named
    /// variable in it is substituted by its current value (through the capture's bag, so a
    /// term shared with the Locals list is rendered once), an anonymous or not-yet-written
    /// one by <c>_</c>. Each argument is cut to <see cref="MaxHeadArgChars"/> — a stack line
    /// is read at a glance; the full value is in Locals.</summary>
    private string RenderHeadArgs(
        Activation engine, Shumway.Compiler.Wam.DebugClauseFrame? clause, int env,
        DebugValueBag bag)
    {
        if (clause?.HeadArgs is not { Count: > 0 } args) return "";

        var slots = clause.Value.Variables;
        Term Substitute(Term t)
        {
            switch (t)
            {
                case VarTerm v:
                    if (v.Name.Length == 0 || v.Name[0] == '_') return new VarTerm("_");
                    if (env >= 0)
                    {
                        foreach (var s in slots)
                            if (s.Name == v.Name)
                                return new VarTerm(bag.Render(engine.GetY(env, s.Slot)));
                    }
                    return new VarTerm("_");
                case CompoundTerm c:
                    var parts = new Term[c.Args.Length];
                    for (int i = 0; i < parts.Length; i++) parts[i] = Substitute(c.Args[i]);
                    return new CompoundTerm(c.Functor, parts);
                default:
                    return t;
            }
        }

        try
        {
            var text = new System.Text.StringBuilder("(");
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) text.Append(", ");
                // The substituted values arrive as VarTerms NAMED by their rendering — the
                // renderer prints a variable's name verbatim, which splices an already
                // rendered (and already capped) value into the skeleton without
                // materializing anything twice.
                text.Append(Ellipsize(
                    AstTermRenderer.Render(Substitute(args[i]), 999, Operators),
                    MaxHeadArgChars));
            }
            return text.Append(')').ToString();
        }
        catch (Exception)
        {
            return "";   // best-effort, like every frame read
        }
    }

    /// <summary>One argument of a call-stack line. The LINE has to be read at a glance —
    /// the whole call, clause number and all, in a window column; a variable's full value
    /// (itself capped at <see cref="MaxVariableChars"/>) belongs to the Locals window.</summary>
    private const int MaxHeadArgChars = 64;

    private IReadOnlyList<(string Name, string Value)> ReadVariables(
        Activation engine, int pc, int env, DebugValueBag bag)
    {
        if (env < 0) return Array.Empty<(string, string)>();
        var clause = ClauseAt(pc);
        if (clause is null || clause.Value.Variables.Count == 0)
            return Array.Empty<(string, string)>();

        var result = new List<(string, string)>(clause.Value.Variables.Count);
        foreach (var v in clause.Value.Variables)
            result.Add((v.Name, bag.Render(engine.GetY(env, v.Slot))));
        return result;
    }

    /// <summary>
    /// ADR-035 — one rendering per VALUE, however many frames hold it.
    ///
    /// <para>A call stack is mostly the same bindings seen from different clauses: the
    /// caller passed <c>Data</c> down, so every frame of the recursion holds the same term
    /// — the same heap cell. Rendering it per frame did the expensive part of a stop (walk
    /// the term, build the AST, print it) once per frame instead of once, and serialised
    /// the same characters once per frame too. This bag lives for ONE capture (bindings
    /// change between stops) and keys on the dereferenced cell: same cell, same string —
    /// the very instance, which is what lets the channel write it once and point at it.</para>
    /// </summary>
    private sealed class DebugValueBag
    {
        private readonly PrologEngine _host;
        private readonly Activation _engine;
        private readonly Dictionary<(Tag, long), string> _byCell = new();

        public DebugValueBag(PrologEngine host, Activation engine)
        {
            _host = host;
            _engine = engine;
        }

        /// <summary>The rendering of whatever this cell is bound to right now — shared with
        /// every other variable bound to the same thing.</summary>
        public string Render(Cell slot)
        {
            try
            {
                // A VARIABLE WHOSE TURN HAS NOT COME. `allocate` leaves the Y slots
                // untouched — RawInt(0), a control word, not a term — because standard WAM
                // codegen writes a permanent at its FIRST occurrence and never reads it
                // before. It is a plain unbound variable as far as the user is concerned —
                // it has no value yet. (Handing it to the materializer instead threw a
                // NotSupportedException — caught, but printed into Visual Studio's Output
                // window at every Break All.)
                if (slot.Tag == Tag.RawInt) return "_";

                // Not a term at all — the raw backing store of a partial string, which no
                // variable is ever bound TO.
                if (slot.Tag == Tag.PstrBuffer) return "<internal>";

                // THE KEY IS THE DEREFERENCED CELL. Two variables bound to the same term
                // dereference to the same cell — same tag, same payload — wherever their own
                // slots live. An unbound variable's identity is its final Ref cell, so the
                // same variable shows the same `_G` in every frame that shares it, by
                // construction. (Equal-but-distinct terms in different cells do NOT share —
                // the bag dedups sharing, not equality; the channel's string table catches
                // the equal-content case at serialisation.)
                Cell cell = slot;
                int at = -1;
                if (cell.Tag == Tag.Ref)
                {
                    at = _engine.Deref(cell.AsHeapIndex);
                    cell = _engine.GetHeap(at);
                    if (cell.Tag == Tag.Ref) at = cell.AsHeapIndex;   // unbound: its own address
                }
                var key = (cell.Tag, cell.Data);
                if (_byCell.TryGetValue(key, out string? cached)) return cached;

                // Materialization reads from the heap; a value that is not already there (a
                // Y slot holding a direct value cell) is staged into one fresh heap cell.
                // Copying the CELL keeps sharing intact.
                if (at < 0)
                {
                    at = _engine.AllocateHeap(1);
                    _engine.SetHeap(at, cell);
                }
                Term term = TermReader.Materialize(_engine, at);

                // Ellipsized, and not as a nicety. A real program binds real data: a Blint
                // variable holds the parsed contents of the file it is linting, and rendering
                // it whole put a megabyte of text into a variable the Locals window shows on
                // one line — which nobody can read, and which overran the channel that had to
                // carry the WHOLE stack. (Seeing inside a big term is what expanding it in the
                // Locals window is for; that is a func-eval, and it is on the D5 list.)
                string value = Ellipsize(
                    AstTermRenderer.Render(term, 999, _host.Operators), MaxVariableChars);
                _byCell[key] = value;
                return value;
            }
            catch (Exception)
            {
                // Reading a frame is best-effort by nature: a debugger must never take
                // the program down because it could not render something.
                return "<unavailable>";
            }
        }
    }

    /// <summary>ADR-035 — the source site of the clause a choice point's retry address
    /// will actually run.
    ///
    /// <para>A retry address does not point at a clause. It points at the link of a
    /// dispatch chain — <c>trust 72</c>, say — and in an indexed predicate the whole
    /// chain sits ahead of every clause body, so the retry address precedes all the
    /// predicate's source sites and asking which site it is <i>inside</i> answers
    /// "none". Two hops get there: the link names its clause (an <c>address</c>
    /// operand) or the clause simply follows it; and the clause's site sits a few
    /// bytes past its first instruction, behind the <c>meta dbg_info</c>. Returns the
    /// address of the site, so a frame built on it lands on the right line.</para>
    /// </summary>
    internal int RetryClauseSite(int retryPc)
    {
        var program = _patchedProgram;
        if (program is null || retryPc < 0 || retryPc >= program.Length) return retryPc;

        byte opByte = OpcodeAt(program, retryPc);
        var op = (Shumway.Core.Opcode)opByte;
        int body = op switch
        {
            // try / retry / trust carry the clause address as their first operand.
            Shumway.Core.Opcode.Try or Shumway.Core.Opcode.Retry or Shumway.Core.Opcode.Trust
                => BitConverter.ToInt32(program, retryPc + 1),
            // try_me_else / retry_me_else / trust_me name the NEXT clause; their own
            // clause is the code that follows them.
            _ => retryPc + Shumway.Core.OpcodeTable.Get(opByte).Size,
        };
        if (body < 0 || body >= program.Length) return retryPc;

        // The first site at or after the clause's first instruction — but not past the
        // end of the predicate, or a clause with no site of its own (one the compiler
        // built, or a predicate compiled without debug) would borrow the next
        // predicate's line.
        if (_stopPcs.Length == 0) return body;
        int i = Array.BinarySearch(_stopPcs, body);
        if (i < 0) i = ~i;
        if (i >= _stopPcs.Length) return body;

        var entries = SortedPredicateEntries();
        int j = Array.BinarySearch(entries, body);
        if (j < 0) j = ~j - 1;
        int endOfPredicate = j >= 0 && j + 1 < entries.Length ? entries[j + 1] : int.MaxValue;

        return _stopPcs[i] < endOfPredicate ? _stopPcs[i] : body;
    }

    /// <summary>The opcode really at <paramref name="pc"/> — an armed breakpoint has
    /// overwritten the byte with <c>Break</c>, and the original is in the table.</summary>
    private byte OpcodeAt(byte[] program, int pc)
    {
        byte b = program[pc];
        return b == (byte)Shumway.Core.Opcode.Break
               && _breakpointPatches.TryGetValue(pc, out byte original)
            ? original
            : b;
    }

    /// <summary>ADR-035 — the predicate an address falls INSIDE, as opposed to
    /// <see cref="LookupPredicateByAddress"/>, which only recognises an entry point.
    /// The redo port needs it: a choice point's retry address points into the middle
    /// of a clause chain, never at its head.</summary>
    internal (string Name, int Arity)? PredicateContaining(int address)
    {
        if (_currentPredicatesByAddress is null || address < 0) return null;
        var entries = SortedPredicateEntries();
        int i = Array.BinarySearch(entries, address);
        if (i < 0) i = ~i - 1;
        if (i < 0) return null;
        var pred = _currentPredicatesByAddress[entries[i]];
        var (atomId, arity) = FunctorTable.Lookup(pred.FunctorId);
        string name = DemangleLocalName(AtomTable.GetById(atomId)?.Name ?? "?");
        return name == "__query__" ? null : (name, arity);
    }

    /// <summary>ADR-035 — the module a compiled predicate belongs to: the prefix of its MANGLED
    /// name before the <c>$</c> (a module-local predicate is compiled as <c>module$name</c>).
    /// Null for a global/public predicate, a builtin, a synthesised helper, or the query wrapper
    /// — none carry a module prefix. Read straight off the code the frame is running, so it is
    /// the exact string the mangling used, whatever it is; the debugger's module resolution then
    /// does not depend on how that string was derived at compile time.</summary>
    internal string? ModulePrefixAt(int address)
    {
        if (_currentPredicatesByAddress is null || address < 0) return null;
        var entries = SortedPredicateEntries();
        int i = Array.BinarySearch(entries, address);
        if (i < 0) i = ~i - 1;
        if (i < 0) return null;
        var pred = _currentPredicatesByAddress[entries[i]];
        var (atomId, _) = FunctorTable.Lookup(pred.FunctorId);
        string mangled = AtomTable.GetById(atomId)?.Name ?? "";
        int sep = mangled.IndexOf('$');
        return sep > 0 ? mangled.Substring(0, sep) : null;
    }

    /// <summary>ADR-035 — the module the current frame is in, the way the CALL STACK LINE names
    /// it: the same source-file base name the frame decoder prints (<c>Blint:main</c> comes from
    /// <c>Blint.pl</c>). Two ways, most precise first:
    /// <list type="number">
    /// <item>the frame's OWN mangled module prefix, when it is running a module-local predicate
    /// (<see cref="ModulePrefixAt"/>);</item>
    /// <item>otherwise the base name of the frame's SOURCE FILE — a public predicate is compiled
    /// global (no prefix) and a control-construct helper (<c>$catchgoal_N</c>) is global too, but
    /// the call-stack line still shows the module of the <c>.pl</c> they came from, and so do we.
    /// This is returned as-is (not filtered against the known modules): whether it truly defines
    /// the goal is decided at resolution, which falls back to the unique defining module when the
    /// file's name — e.g. a filename that differs from a <c>:- module</c> name — does not.</item>
    /// </list>
    /// Null only when the frame has no source file (a synthetic <c>&lt;string&gt;</c> consult).</summary>
    internal string? ModuleForFrame(int pc)
    {
        string? own = ModulePrefixAt(pc);
        if (own is not null) return own;

        int siteId = SiteAtOrBefore(pc);
        if (siteId >= 0)
        {
            var site = Shumway.Core.DebugSiteTable.Get(siteId);
            return ModuleNameFromFile(Shumway.Core.DebugSiteTable.FileName(site.FileId));
        }
        return null;
    }

    /// <summary>The module name a file's base name gives — the same <c>GetFileNameWithoutExtension</c>
    /// the call-stack frame decoder uses, with any trailing <c>.pl</c> stripped (a materialised
    /// <c>Blint.pl.pl</c> reads as <c>Blint</c>). Null for a synthetic file (<c>&lt;string&gt;</c>).</summary>
    private static string? ModuleNameFromFile(string file)
    {
        if (string.IsNullOrEmpty(file) || file[0] == '<') return null;
        string baseName;
        try { baseName = System.IO.Path.GetFileNameWithoutExtension(file); }
        catch { return null; }
        while (baseName.EndsWith(".pl", StringComparison.OrdinalIgnoreCase))
            baseName = baseName.Substring(0, baseName.Length - 3);
        return baseName.Length == 0 ? null : baseName;
    }

    /// <summary>ADR-035 — is <paramref name="mangledName"/>/<paramref name="arity"/> a defined
    /// predicate in the current code space? Decides whether a module-qualified name resolves
    /// before falling back to the plain (global / builtin) name.</summary>
    internal bool HasDefinedPredicate(string mangledName, int arity)
    {
        if (_currentPredicatesByAddress is null) return false;
        foreach (var pred in _currentPredicatesByAddress.Values)
        {
            var (atomId, ar) = FunctorTable.Lookup(pred.FunctorId);
            if (ar != arity) continue;
            if ((AtomTable.GetById(atomId)?.Name ?? "") == mangledName) return true;
        }
        return false;
    }

    /// <summary>ADR-035 — every module prefix present in the current code space (the
    /// <c>$</c>-prefix of a mangled local). Lets a typed module name be matched to the real one,
    /// forgiving a trailing <c>.pl</c> and case.</summary>
    internal HashSet<string> DefinedModulePrefixes()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (_currentPredicatesByAddress is not null)
            foreach (var pred in _currentPredicatesByAddress.Values)
            {
                var (atomId, _) = FunctorTable.Lookup(pred.FunctorId);
                string mangled = AtomTable.GetById(atomId)?.Name ?? "";
                int sep = mangled.IndexOf('$');
                if (sep > 0) set.Add(mangled.Substring(0, sep));
            }
        return set;
    }

    /// <summary>ADR-035 — resolve module qualification in an Immediate-window goal.
    /// <list type="bullet">
    /// <item><c>Module:Goal</c> runs Goal in Module (predicates addressed as <c>Module$name</c>).
    /// A typed Module that differs from a real one only by a trailing <c>.pl</c> or case is
    /// matched to it; an unknown Module falls back to the stopped frame's.</item>
    /// <item>An unqualified goal is tried in <paramref name="frameModule"/> first — the module of
    /// the frame the user is stopped in — so a module-local predicate is callable by the name the
    /// source uses (<c>show_usage</c>, not <c>blint$show_usage</c>); if no such local exists it is
    /// left as written, a global / public predicate or a builtin.</item>
    /// </list>
    /// Control constructs (<c>,</c> <c>;</c> <c>-&gt;</c> <c>*-&gt;</c> <c>\+</c> <c>not</c>
    /// <c>once</c> <c>ignore</c> <c>call</c>) are transparent: their goal arguments are resolved,
    /// they themselves are not.</summary>
    internal Term ResolveGoalModule(Term goal, string? frameModule)
        => RewriteModuleGoal(goal, frameModule);

    private Term RewriteModuleGoal(Term g, string? mod)
    {
        if (g is CompoundTerm c)
        {
            if (c.Functor == ":" && c.Args.Length == 2 && c.Args[0] is AtomTerm mAtom)
                return RewriteModuleGoal(c.Args[1], ResolveTypedModule(mAtom.Name, mod));

            if (c.Args.Length == 2 && c.Functor is "," or ";" or "->" or "*->")
                return new CompoundTerm(c.Functor,
                    new[] { RewriteModuleGoal(c.Args[0], mod), RewriteModuleGoal(c.Args[1], mod) });

            if (c.Args.Length >= 1 && c.Functor is "\\+" or "not" or "once" or "ignore" or "call")
            {
                var args = (Term[])c.Args.Clone();
                args[0] = RewriteModuleGoal(args[0], mod);
                return new CompoundTerm(c.Functor, args);
            }
        }
        return MangleModuleLeaf(g, mod);
    }

    private Term MangleModuleLeaf(Term g, string? mod)
    {
        string name;
        int arity;
        switch (g)
        {
            case AtomTerm a: name = a.Name; arity = 0; break;
            case CompoundTerm c: name = c.Functor; arity = c.Args.Length; break;
            default: return g;
        }
        if (name.Length == 0 || name[0] == '$') return g;   // already mangled / a helper

        // The frame's module first — the module named on the current call-stack line. A local
        // there shadows a global of the same name, so it is the precise answer.
        if (mod is not null && HasDefinedPredicate(mod + "$" + name, arity))
            return Remangle(g, mod + "$" + name);

        // The frame's module did not define it (or could not be determined — a public predicate
        // or a lowered helper whose file named no module). Fall back to the module that UNIQUELY
        // defines the name: the only module the unqualified call could mean. Ambiguous (two
        // modules) or absent → leave it global (a public predicate or a builtin).
        string? unique = UniqueModuleDefining(name, arity);
        return unique is not null ? Remangle(g, unique + "$" + name) : g;
    }

    private static Term Remangle(Term g, string mangled)
        => g is AtomTerm ? new AtomTerm(mangled) : new CompoundTerm(mangled, ((CompoundTerm)g).Args);

    /// <summary>The one module whose <c>module$name/arity</c> is defined, or null when none is or
    /// more than one is (ambiguous — not ours to guess).</summary>
    private string? UniqueModuleDefining(string name, int arity)
    {
        string? found = null;
        foreach (var m in DefinedModulePrefixes())
            if (HasDefinedPredicate(m + "$" + name, arity))
            {
                if (found is not null) return null;
                found = m;
            }
        return found;
    }

    private string ResolveTypedModule(string typed, string? frameModule)
    {
        var prefixes = DefinedModulePrefixes();
        if (prefixes.Contains(typed)) return typed;
        foreach (var p in prefixes)
            if (string.Equals(StripPl(p), StripPl(typed), StringComparison.OrdinalIgnoreCase))
                return p;
        return frameModule ?? typed;

        static string StripPl(string s)
            => s.EndsWith(".pl", StringComparison.OrdinalIgnoreCase) ? s.Substring(0, s.Length - 3) : s;
    }

    private int[]? _sortedPredEntries;

    private int[] SortedPredicateEntries()
    {
        if (_sortedPredEntries is not null) return _sortedPredEntries;
        var keys = new int[_currentPredicatesByAddress!.Count];
        int n = 0;
        foreach (int addr in _currentPredicatesByAddress.Keys) keys[n++] = addr;
        Array.Sort(keys);
        return _sortedPredEntries = keys;
    }

    /// <summary>ADR-035 — attaches a debug session (or <c>null</c> to detach).
    /// Every query's activation is born with it, and it receives the four Prolog
    /// ports plus the stop sites of any debug-compiled code. This is what a real
    /// debugger uses; <see cref="SetTracing"/> is the same seam with the tracer
    /// as the session.</summary>
    public void AttachDebugSession(Shumway.Core.IDebugSession? session)
        => DebugSession = session;

    // ADR-035 — set once, when the first debug session's diagnostic logging is armed, so a
    // second EnableDebugging (after a dispose + re-enable) does not stack a second handler.
    private static bool _debugDiagLoggingArmed;

    /// <summary>
    /// ADR-035 — turn on source-level debugging for THIS engine, so a debugger attached to
    /// this PROCESS can set breakpoints in the <c>.pl</c> files it consults, step, inspect
    /// the mixed Prolog+C# call stack, and run goals in the Immediate window. It is the
    /// embedding-API equivalent of the REPL's <c>--debug</c>: the point is to debug Shumway
    /// when it is one part of a larger .NET application, in that application's own process,
    /// rather than only in the standalone REPL.
    ///
    /// <para>CALL IT BEFORE CONSULTING the code you want to debug. Debuggability is a
    /// property of the CODE, decided when it is compiled: predicates compiled after this call
    /// keep their variable names, their frames and their source positions; predicates
    /// compiled before it already threw those away. (Loading a bundle counts as consulting —
    /// a bundle that still carries its module sources is re-compiled debuggable, and the
    /// debugger is pointed at that embedded source; a source-stripped bundle is resolved the
    /// ordinary way, by module name to a <c>.pl</c> on disk.)</para>
    ///
    /// <para>Returns the session; keep it alive for as long as you want to be debuggable, and
    /// dispose it to end debugging (it unpins the channel and detaches). There is one debug
    /// session per process — calling this twice without disposing the first throws.</para>
    /// </summary>
    public Debugging.ChannelDebugSession EnableDebugging(Debugging.DebugOptions? options = null)
    {
        if (Debugging.ShumwayDebugHelper.Session is not null)
            throw new InvalidOperationException(
                "a debug session is already active in this process; dispose it before "
                + "enabling debugging again (there is one debugger per process).");

        options ??= new Debugging.DebugOptions();

        // Debug metadata + debug codegen: named variables kept, frames forced, env trimming
        // and redundant-cut elision off, per-goal source positions recorded. This is the
        // switch the REPL's --debug throws; without it the compiler produces release code
        // with nothing for a debugger to stop at or show.
        _flags.EmitDebugInfo = true;
        _flags.DebugCodegen = true;

        // A reclaimed frame is a frame nobody can show, so a debug session wants LCO off — but
        // SHUMWAY_DEBUG_LCO is a PIN, and a pin the code overrides is not one, so honour it
        // when set and take the caller's choice only otherwise.
        if (Environment.GetEnvironmentVariable("SHUMWAY_DEBUG_LCO") is null)
            _flags.DebugLco = options.LastCallOptimisation;

        // SHUMWAY_DEBUG_DIAG=1 — log every exception the engine THROWS, caught or not, with
        // its stack. A handled house-keeping throw is invisible from outside and loud from
        // inside a debugger (Visual Studio prints "Exception thrown" into Output for each);
        // this tells the bug being hunted from the noise. Armed once for the process.
        if (!_debugDiagLoggingArmed
            && Environment.GetEnvironmentVariable("SHUMWAY_DEBUG_DIAG") == "1")
        {
            _debugDiagLoggingArmed = true;
            string trace = Path.Combine(
                Path.GetTempPath(), "shumway-debug", "engine-exceptions.log");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(trace)!);
                AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
                {
                    try
                    {
                        File.AppendAllText(trace,
                            DateTime.Now.ToString("HH:mm:ss.fff") + "  "
                            + e.Exception.GetType().Name + ": " + e.Exception.Message + "\n"
                            + e.Exception.StackTrace + "\n\n");
                    }
                    catch (Exception) { /* a diagnostic must never be the thing that fails */ }
                };
            }
            catch (Exception) { /* no temp dir — run without the log */ }
        }

        // The files we are ABOUT to consult, said before we consult them: a breakpoint drawn
        // before the process stops anywhere binds against a module, a module is a .pl file,
        // and a just-started process has no frames for the debugger to learn the names from.
        // Optional — every file is also announced as it is consulted.
        if (options.SourceFiles is { Count: > 0 } files)
        {
            var full = new string[files.Count];
            for (int i = 0; i < files.Count; i++)
            {
                try { full[i] = Path.GetFullPath(files[i]); }
                catch (Exception) { full[i] = files[i]; }
            }
            Debugging.ShumwayDebugHelper.SourceFiles = full;
        }

        var session = new Debugging.ChannelDebugSession(this);

        if (options.WaitForAttach)
            WaitForDebuggerReady(session, options.AttachTimeout);

        return session;
    }

    /// <summary>ADR-035 — block until a debugger has attached to this process AND finished
    /// arming its breakpoints, or <paramref name="timeout"/> elapses. Split out of
    /// <see cref="EnableDebugging"/> so a bundle-loading path can defer the wait until AFTER
    /// its modules have been consulted (that consult materialises + announces their source,
    /// which is what an attaching debugger must find before the goal runs).</summary>
    internal void WaitForDebuggerReady(
        Debugging.ChannelDebugSession session, TimeSpan timeout)
    {
        // --debug-wait means WAIT. The whole reason to launch a program this way is to debug
        // it from its first goal, so there is NO deadline on the attach itself: a user who
        // takes a minute to open Visual Studio and attach still lands at the entry, rather
        // than finding the program already run to the end. (A program launched to be debugged
        // that runs on without a debugger is useless; hanging until Ctrl-C is the honest
        // behaviour. The old 10 s timeout is what made it "wait, then run past me".)
        Debugging.ShumwayDebugHelper.DiagLine("waiting for a debugger to attach...");
        while (!System.Diagnostics.Debugger.IsAttached)
            System.Threading.Thread.Sleep(50);

        // Attached is not ready: the debugger still has to find the channel and arm the
        // breakpoints the user drew before pressing the button. Consulting now would run the
        // program straight past them. This wait IS bounded — it is the "has it gone quiet"
        // wait (milliseconds), not the "will anyone ever come" wait above.
        int quietMs = (int)timeout.TotalMilliseconds;
        session.WaitForDebuggerCommands(quietMs > 0 ? quietMs : 0);

        // --debug-wait's other promise: the program stops when the debugger is ready — at the
        // entry, not somewhere the user has to guess at. Arm a stop on the first goal.
        if (System.Diagnostics.Debugger.IsAttached)
        {
            session.ArmEntryBreak();
            Debugging.ShumwayDebugHelper.DiagLine(
                "debugger attached; armed stop-at-entry for the first goal");
        }
        else
        {
            Debugging.ShumwayDebugHelper.DiagLine(
                "attach was lost before the entry stop could be armed");
        }
    }

    /// <summary>Adds an operator to the engine's parser table. Used by the
    /// runtime <c>op/3</c> builtin so user code can introduce operators
    /// that subsequent queries (and asserted clauses) will recognise.</summary>
    internal void DefineOperator(string name, int precedence, OperatorType type)
        => _operators.Define(name, precedence, type);

    /// <summary>Snapshot of every registered operator, as
    /// (Precedence, Type, Name) triples. Used by <c>current_op/3</c>
    /// (chunk 138) to drive its backtracking enumeration.</summary>
    internal IEnumerable<(int Precedence, OperatorType Type, string Name)> EnumerateOperators()
        => _operators.Enumerate();

    /// <summary>Loads a Shumway bundle (.shum) from disk and consults every
    /// module inside it. Equivalent to calling <see cref="ConsultString"/>
    /// for each entry in the bundle's manifest, in order. Throws
    /// <see cref="InvalidDataException"/> if the file isn't a valid
    /// bundle.</summary>
    public void LoadBundle(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Bundle bundle = BundleReader.ReadFromFile(path);
        // Chunk 247 — pass the bundle's directory so the foreign-
        // assembly auto-loader can find sibling DLLs (the typical
        // `myapp.shum` + `MyForeigns.dll` layout).
        LoadBundleCore(bundle, System.IO.Path.GetDirectoryName(
            System.IO.Path.GetFullPath(path)));
    }

    /// <summary>Loads an in-memory <see cref="Bundle"/> into this engine —
    /// useful for tests and for in-process pipelines that prefer not to
    /// round-trip through disk. Entries that carry a pre-compiled
    /// bytecode blob (chunk 38 / chunk 45) get their IL-eligible
    /// predicates eagerly warmed via <see cref="IlPromotion"/>'s
    /// <c>Warm</c> path; the precompiled clause list is cached on
    /// <see cref="PrecompiledClauseCache"/> so subsequent query setups
    /// can skip the WAM compile for those clauses (chunk 53).</summary>
    public void LoadBundle(Bundle bundle) => LoadBundleCore(bundle, bundleDir: null);

    /// <summary>ADR-035 — write a bundle entry's embedded source to a stable file the
    /// debugger can open, and return its full path. Named for the module so
    /// <see cref="Shumway.Core.DebugSiteTable"/>'s base-name file identity matches the
    /// breakpoint a user draws on it. This is the exact text the module was compiled from —
    /// which is the whole point of preferring it to a same-named <c>.pl</c> on disk that may
    /// have drifted.
    ///
    /// <para>All of a program's modules share ONE directory, keyed by the EXECUTABLE (not the
    /// process id): re-running the same binary materialises to the SAME paths, so the debugger
    /// reuses its source windows and the breakpoints bound to those paths survive the new run —
    /// instead of opening a second identical window per module and orphaning every breakpoint
    /// (which is what a per-process directory did). One directory per program, N files for N
    /// modules — not N directories.</para>
    ///
    /// <para>The file is made READ-ONLY. There is no hot relinking — an edit here could not
    /// reach the running code, so it would only diverge silently from what executes. (The day
    /// we can reload edited source, drop the read-only flag.) If the module's source changed
    /// since a prior run (a recompile), the file is rewritten in place at the same path.</para></summary>
    private static string MaterialiseDebugSource(string moduleName, string source)
    {
        // ADR-035 — write CONSISTENT line endings. The embedded source can carry mixed
        // CRLF/LF (a file edited on more than one platform), and the debugger's editor flags
        // that on open. Normalising CRLF -> LF -> CRLF removes the mix without moving any line:
        // every `\n` boundary the compiler counted the stop-site lines against is preserved, so
        // breakpoints and the entry stop still land where they should.
        string normalised = source.Replace("\r\n", "\n").Replace("\n", "\r\n");

        // One directory for the whole program, keyed by the executable path; the FILE keeps its
        // clean "<module>.pl" name — the window title, and the base name the DebugSiteTable
        // matches stop sites against.
        string dir = Path.Combine(Path.GetTempPath(), "shumway-debug", ProgramKey());
        Directory.CreateDirectory(dir);

        var safe = new char[moduleName.Length == 0 ? 1 : moduleName.Length];
        char[] invalid = Path.GetInvalidFileNameChars();
        if (moduleName.Length == 0) safe[0] = '_';
        for (int i = 0; i < moduleName.Length; i++)
            safe[i] = Array.IndexOf(invalid, moduleName[i]) >= 0 ? '_' : moduleName[i];
        string path = Path.Combine(dir, new string(safe) + ".pl");

        try
        {
            bool exists = File.Exists(path);
            // Rewrite only when the text actually changed (a recompile) — a read of a read-only
            // file is fine; the write needs the flag cleared first.
            if (!exists || File.ReadAllText(path) != normalised)
            {
                if (exists)
                {
                    var a = File.GetAttributes(path);
                    if ((a & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
                }
                File.WriteAllText(path, normalised);
            }

            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) == 0)
                File.SetAttributes(path, attrs | FileAttributes.ReadOnly);
        }
        catch (IOException) { /* open in the debugger, same content — the path is what we need */ }
        catch (UnauthorizedAccessException) { /* already read-only from a prior run */ }
        return path;
    }

    /// <summary>A short, STABLE (cross-process) key for the program, so all of its modules share
    /// one materialised-source directory that re-runs of the same binary reuse. The executable
    /// path (SHA-256, not <see cref="string.GetHashCode"/> — that is randomised per process, so
    /// it would give a different directory every run and defeat the whole point).</summary>
    private static string ProgramKey()
    {
        string exe = Environment.ProcessPath ?? AppContext.BaseDirectory ?? "shumway";
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(exe));
        var sb = new System.Text.StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    /// <summary>ADR-035 — does this entry's bytecode carry the baked debug side tables (was it
    /// compiled <see cref="ShmoBuildMode.Debuggable"/>)? Decodes the module and looks for any
    /// stop site — a real module compiled debuggable always has some (even a fact gets one).
    /// Only ever called on the debug-session load path, so the decode cost is never on a
    /// release load; the decode re-interns the sites, which is idempotent with the load that
    /// follows.</summary>
    private static bool EntryCarriesDebugWam(BundleEntry entry)
    {
        if (entry.CompiledBytecode is not { Length: > 0 }) return false;
        try
        {
            var module = CompiledModuleCodec.Decode(entry.CompiledBytecode);
            foreach (var pred in module.Predicates)
                if (pred.DebugStops.Count > 0) return true;
        }
        catch (Exception) { /* undecodable → not a debug entry we can use directly */ }
        return false;
    }

    private void LoadBundleCore(Bundle bundle, string? bundleDir)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        // Chunk 247 — auto-register every foreign DLL the linker
        // recorded into the bundle. Each entry is a filename only;
        // we look for it adjacent to the bundle file first, then
        // alongside the executable (AppContext.BaseDirectory), then
        // fall back to the runtime's normal Assembly.Load resolution.
        // A missing DLL throws — same loudness as a missing predicate
        // would surface at first call.
        foreach (var asmName in bundle.ForeignAssemblies)
        {
            string? resolved = ResolveForeignAssemblyPath(asmName, bundleDir);
            if (resolved is null)
                throw new FileNotFoundException(
                    $"Bundle declared a foreign assembly '{asmName}' but no matching file "
                    + "was found next to the bundle, next to the executable, or via the "
                    + "default Assembly.Load probe path.",
                    asmName);
            RegisterForeignAssembly(resolved);
        }
        // ADR-024 — native C libraries (--native-dll): load each so a `:- native`
        // function resolves by P/Invoke. Probed like a foreign assembly (next to
        // the bundle / executable), then the OS loader's default search.
        foreach (var libName in bundle.NativeLibraries)
        {
            string probe = ResolveForeignAssemblyPath(libName, bundleDir) ?? libName;
            UseNativeLibrary(probe);
        }
        // A shumway-lib librarian archive stores its modules as verbatim
        // .shmo objects (bundle.ArchiveMembers) rather than post-link
        // Entries. Derive a runnable entry from each — exactly the fields a
        // .shmo carries that the per-entry load path below needs — and run it
        // through the same machinery, so an archive loads identically to
        // consulting each object's source / loading its compiled bytecode.
        // No linking or pruning happens: every member is kept verbatim.
        IReadOnlyList<BundleEntry> effectiveEntries = bundle.Entries;
        if (bundle.ArchiveMembers.Count > 0)
        {
            var combined = new List<BundleEntry>(
                bundle.Entries.Count + bundle.ArchiveMembers.Count);
            combined.AddRange(bundle.Entries);
            foreach (var member in bundle.ArchiveMembers)
            {
                var shmo = ShmoReader.FromBytes(member.ShmoBytes);
                combined.Add(new BundleEntry(
                    shmo.ModuleName, shmo.Source,
                    compiledBytecode: shmo.Bytecode,
                    compiledIl: null,
                    defined: shmo.Defined,
                    compiledIlPatches: null,
                    compiledIlEntries: null,
                    dynamicSeeds: shmo.DynamicSeeds,
                    nativeBlocks: shmo.NativeBlocks,
                    operators: shmo.Operators));   // Phase 33 (PrologToC)
            }
            effectiveEntries = combined;
        }
        // A bundle may bake a precompiled `$prelude` entry (shumway-link
        // --exe / --stdlib) so a bare engine (FromBundle / the generated
        // --exe) gets the prelude without compiling it. A NORMAL engine
        // already consulted the prelude in its constructor, so that entry is
        // redundant here — drop it to avoid a double install.
        if (_modules.ContainsKey(Prelude.ModuleName)
            && effectiveEntries.Any(e => e.ModuleName == Prelude.ModuleName))
        {
            effectiveEntries = effectiveEntries
                .Where(e => e.ModuleName != Prelude.ModuleName).ToList();
        }
        foreach (var entry in effectiveEntries)
        {
            // Phase 33 (PrologToC) — replay the entry's `:- op/3` definitions
            // into the runtime operator table BEFORE loading it. A
            // source-stripped entry otherwise loses its ops entirely (the
            // debug path re-executes them via ConsultString, for which this
            // replay is an idempotent no-op) — and any runtime read/1 /
            // string_term/2 of text using them would mis-parse.
            foreach (var od in entry.Operators)
            {
                var opType = od.Type switch
                {
                    "fx" => Shumway.Compiler.Parsing.OperatorType.Fx,
                    "fy" => Shumway.Compiler.Parsing.OperatorType.Fy,
                    "xf" => Shumway.Compiler.Parsing.OperatorType.Xf,
                    "yf" => Shumway.Compiler.Parsing.OperatorType.Yf,
                    "xfx" => Shumway.Compiler.Parsing.OperatorType.Xfx,
                    "xfy" => Shumway.Compiler.Parsing.OperatorType.Xfy,
                    "yfx" => Shumway.Compiler.Parsing.OperatorType.Yfx,
                    _ => (Shumway.Compiler.Parsing.OperatorType?)null,
                } ;
                if (opType is { } t) DefineOperator(od.Name, od.Priority, t);
            }
            // Chunk 178: source-less load. When the bundle was built
            // with --strip (or compiled in Release with chunk 177's
            // source omission), Source is empty and we cannot
            // ConsultString. The entry's CompiledBytecode + Defined
            // metadata carry everything we need — the bytecode is
            // already runtime-ready (mangled per chunk 176) and the
            // Defined list tells us which functors are public /
            // dynamic / local. Set up a ModuleManifest from the
            // metadata and queue the precompiled predicates for the
            // static-link region; SetupQueryFromTerm will plug them
            // in next time it rebuilds the link.
            if (string.IsNullOrEmpty(entry.Source)
                && entry.CompiledBytecode is not null
                && entry.Defined.Count > 0)
            {
                LoadEntryFromBytecode(entry);
                continue;
            }
            // ADR-035 — a Debuggable bundle bakes the debug-shape WAM (frames, Y-slots, no
            // trimming/LCO, stop sites + frame/variable maps) straight into its bytecode. Under
            // a debug session we run THAT directly — no re-consult, zero recompile at load — and
            // materialise the embedded source only for the debugger to open. The baked stop
            // sites already reference "<module>.pl" (by base name), and the materialised file
            // has that base name, so interning its full path upgrades the site's file to an
            // openable one; the stop sites then flow into the query-setup breakpoint index
            // (SetupQueryFromTerm) exactly as a fresh debug consult's would.
            if (_flags.DebugCodegen && entry.CompiledBytecode is { Length: > 0 }
                && entry.Defined.Count > 0 && EntryCarriesDebugWam(entry))
            {
                string dbgFile = MaterialiseDebugSource(entry.ModuleName, entry.Source);
                Shumway.Core.DebugSiteTable.InternFile(dbgFile);   // upgrade to an openable path
                if (DebugSession is not null)
                    Debugging.ShumwayDebugHelper.NoteSourceFile(dbgFile);
                LoadEntryFromBytecode(entry);
                continue;
            }

            // Otherwise (a non-Debuggable source-bearing entry): when debugging, show the code
            // FROM that source. The source-stripped entry took the bytecode branch above; there
            // is nothing to show but the module name, and the debugger resolves it the ordinary
            // way (by module name to a `<module>.pl` on disk). But here the exact text the
            // module was compiled from is in hand, so materialise it to a file the debugger can
            // open and stamp this consult's stop sites with that path — a breakpoint in the
            // .shum's code then resolves to the code that is IN the .shum, not a possibly-
            // different .pl someone happens to have on disk. (This re-consult path is the
            // fallback for a Debug — not Debuggable — bundle loaded under a debug session.)
            int prevDebugFile = _debugFileId;
            try
            {
                if (_flags.DebugCodegen)
                {
                    string materialised = MaterialiseDebugSource(entry.ModuleName, entry.Source);
                    _debugFileId = Shumway.Core.DebugSiteTable.InternFile(materialised);
                    if (DebugSession is not null)
                        Debugging.ShumwayDebugHelper.NoteSourceFile(materialised);
                }
                // Chunk 440 — consult under the entry's module name so a
                // module-less file keeps the per-file module identity its
                // .shmo bytecode was compiled (and mangled) with, instead of
                // merging into the rolling "user" module.
                ConsultStringInner(entry.Source, recordInHistory: true,
                    moduleNameFallback: entry.ModuleName);
            }
            finally
            {
                _debugFileId = prevDebugFile;
            }
        }

        // Decode each entry's CompiledModule and stash the predicates
        // for diagnostics. The source-bearing entries also feed
        // IL warmup from here (their PrecompiledClauseCache
        // substitution remains active — chunk 176 made the .shmo
        // bytecode byte-identical to what SetupQueryFromTerm would
        // produce, so the warmed IL delegates' call sites now
        // resolve correctly).
        // Chunk 192: load persisted IL FIRST (before the Sigil warm
        // path below) so RegisterBoundDelegate's first-wins
        // semantics actually let the pre-compiled IL take effect.
        // Pre-chunk-192 the order was reversed and IlPromotion.Warm
        // had already invoked Sigil for every promotable predicate
        // by the time the persisted bind ran — the persisted IL was
        // technically loaded but never used.
        foreach (var entry in effectiveEntries)
        {
            // A persisted-IL blob is JIT-able IL — loading and running it
            // is runtime code generation, so under Native AOT the entry's
            // bytecode (decoded above) is used instead.
            if (entry.CompiledIl is null || entry.CompiledIl.Length == 0
                || !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
                continue;

            // Phase 33 T3 — clone + patch + Assembly.Load + delegate binding
            // happen ONCE per IL content for the whole process
            // (GetOrLoadPersistedIl, mirroring _loadedNativeLibraries); this
            // engine only replays the per-engine registrations from the cache.
            var module = GetOrLoadPersistedIl(entry);
            if (module is null) continue;
            foreach (var (_, functorId, del) in module.Bound)
                IlPromotion.RegisterBoundDelegate(functorId, del);
            // A stripped indexed predicate carries its dispatch graph in the
            // bundle. Stash it by runtime functor id; each query's fresh
            // engine gets it registered at setup (the engine is per-query, so
            // we can't register here). Without a WAM body the delegate would
            // otherwise have nothing to rebuild the switch model from.
            if (module.IndexGraphs is not null)
                foreach (var kv in module.IndexGraphs)
                    _persistedIndexGraphs[kv.Key] = kv.Value;
            // Chunk 402: a region method publishes its members' entry cursors.
            // The alias marker dispatches the region delegate at the member's
            // entry. Consumed (lowest priority) by the query address map.
            if (module.RegionAliases is not null)
                foreach (var kv in module.RegionAliases)
                    _regionMemberAliases[kv.Key] = kv.Value;
        }

        foreach (var entry in effectiveEntries)
        {
            if (entry.CompiledBytecode is null) continue;
            // Source-less entries already decoded above (via LoadEntryFromBytecode).
            if (_precompiledModules.ContainsKey(entry.ModuleName)
                && string.IsNullOrEmpty(entry.Source)) continue;
            // Source-bearing: the source consult is the truth, so the bytecode is an
            // IL-warm / skip-compile cache only — don't register static predicates.
            // (The shared helper also remaps literals, which fixes float value-baking
            // for a warmed-from-bytecode source-bearing predicate under Threshold>0.)
            DecodeAndRegisterPrecompiledModule(entry, registerStaticPredicates: false);
        }
        // A bundle's predicates join the static program — drop the
        // ADR-015 cached static linked region so the next query rebuilds it.
        _staticLink = null;
        InvalidatePersistent();

        // Cross-process functor-id drift diagnostic (see PersistedIlBuilder).
        var dumpFidsEnv = System.Environment.GetEnvironmentVariable("SHUMWAY_PERSIST_DUMP_FIDS");
        if (!string.IsNullOrEmpty(dumpFidsEnv))
        {
            foreach (var ind in dumpFidsEnv.Split(','))
            {
                var slash = ind.IndexOf('/');
                if (slash < 0) continue;
                if (!int.TryParse(ind.AsSpan(slash + 1), out int ar)) continue;
                string nm = ind.Substring(0, slash);
                int aid = Shumway.Core.AtomTable.Intern(nm).Id;
                int fid = Shumway.Core.FunctorTable.Intern(aid, ar);
                System.Console.Error.WriteLine($"[load-fid] {nm}/{ar} atom={aid} functor={fid}");
            }
        }
    }

    /// <summary>Phase 17 — overwrites every build-time atom-id / functor-id
    /// / resume-marker constant in <paramref name="ilBytes"/> with the
    /// runtime-process equivalent, in-place. The patch sites carry the
    /// build-process <c>(name, arity)</c> pair plus a recorded absolute
    /// byte offset; for each, we intern the name in the current process,
    /// compute the runtime id (or recompute the resume marker via
    /// <see cref="Shumway.Core.Activation.EncodeResumeMarker"/>), and write
    /// the four little-endian bytes back into <paramref name="ilBytes"/>
    /// at that offset. Runs BEFORE <c>Assembly.Load</c> so the JIT sees
    /// runtime values as inline IL constants — zero per-dispatch
    /// overhead.</summary>
    /// <summary>Chunk 225 Stage B.1 — populate the interpreter's
    /// IlByFunctorId table from <see cref="IlPromotion"/>, then rewrite
    /// every <see cref="Shumway.Core.Opcode.Call"/> site whose callee
    /// already has a registered IL delegate into the equivalent
    /// <see cref="Shumway.Core.Opcode.CallIl"/>. The two opcodes share
    /// width (9 bytes) and operand layout — the only difference is the
    /// opcode byte and the meaning of the 4-byte target operand
    /// (address → functor id) — so the rewrite is in-place. Idempotent
    /// (skips sites whose opcode is no longer <c>Call</c>, e.g. when
    /// a re-link revisits a previously-rewritten persistent buffer).</summary>
    private int _diagCallIlCount;
    private void InstallCallIlRewrites(
        Shumway.Interpreter.BytecodeInterpreter interp,
        Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate> predicatesByAddress,
        byte[] queryBytes)
    {
        _diagCallIlCount = 0;
        // Phase 33 L1 — fresh per-query mid-run rewrite state, and the hook the
        // promotion store fires when a delegate installs (sync or drained async).
        _promotableCallSites?.Clear();
        _rewriteInterp = interp;
        IlPromotion.OnPromotionInstalled = OnCalleePromoted;
        // Snapshot every currently-promoted IL delegate, indexed by
        // functor id. The PredicateDelegate -> Func<Activation,int,bool>
        // bridge allocates one wrapper per IL predicate, here at link
        // time — not per dispatch.
        int maxFid = -1;
        foreach (int fid in IlPromotion.PromotedFunctorIds())
            if (fid > maxFid) maxFid = fid;
        Func<Shumway.Core.Activation, int, bool>?[]? ilTable = null;
        if (maxFid >= 0)
        {
            ilTable = new Func<Shumway.Core.Activation, int, bool>?[maxFid + 1];
            foreach (int fid in IlPromotion.PromotedFunctorIds())
            {
                var del = IlPromotion.TryGet(fid);
                if (del is null) continue;
                // Method-group conversion: del.Invoke creates a
                // Func<Activation,int,bool> that calls through to del.
                ilTable[fid] = del.Invoke;
            }
        }
        interp.IlByFunctorId = ilTable;
        DiagIlTable(ilTable);

        // Chunk 226 Stage B.2 — build a fid-keyed view of
        // predicatesByAddress so we can look up the callee's
        // CompiledPredicate by functor id when classifying Call sites
        // as bytecode-only. The same predicate may live under
        // multiple addresses (the chunk-127 enter_dynamic trampoline
        // is at the entry address but the chain bodies sit at later
        // addresses); the functor id is unique.
        var predicateByFid = new Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>(
            predicatesByAddress.Count);
        foreach (var (_, p) in predicatesByAddress)
            predicateByFid[p.FunctorId] = p;

        // Rewrite every Call whose callee state is decided at link time.
        // Three opcodes today (B.3 will add ExecuteIl / ExecuteBytecode):
        //   - CallIl: callee already has a bundle-IL delegate. Operand
        //     is rewritten from target address to callee functor id.
        //   - CallBytecode: callee will never have IL (Threshold==0,
        //     layout-excluded, oversized, or already rejected). Operand
        //     is the absolute target address, unchanged.
        //   - Call: callee may still earn IL via JIT promotion later.
        // Walks both the persistent buffer (addresses < _querySplit)
        // and the per-query overlay. Execute is left alone for now.
        foreach (var (predAddr, pred) in predicatesByAddress)
        {
            foreach (var site in pred.CallSites)
            {
                int calleeFid = site.CalleeFunctorId;

                int absAddr = predAddr + site.OpcodeOffset;
                byte[] buf;
                int bufOffset;
                if (absAddr < _querySplit)
                {
                    buf = _persistentProgram!;
                    bufOffset = absAddr;
                }
                else
                {
                    buf = queryBytes;
                    bufOffset = absAddr - _querySplit;
                }
                // Idempotence + safety: only rewrite the original
                // Call/Execute opcode. A previous query may have already
                // rewritten this site in the persistent buffer.
                int width = site.IsExecute ? 5 : 9;
                if (bufOffset < 0 || bufOffset + width > buf.Length) continue;
                byte expected = site.IsExecute
                    ? (byte)Shumway.Core.Opcode.Execute
                    : (byte)Shumway.Core.Opcode.Call;
                if (buf[bufOffset] != expected) continue;

                // Prefer the IL variant when IL is available.
                // ADR-023/034 — but never for a DYNAMIC callee: its delegate
                // (the bundle-baked or runtime-promoted snapshot) is EVICTED
                // on the first assert/retract, while a CallIl/ExecuteIl
                // rewrite persists in the buffer across queries — the
                // hardened site would run the stale snapshot (or crash on
                // the cleared table slot). Dynamic callees stay on the
                // generic Call/Execute path, whose OnDispatch resolves per
                // call and sees the eviction. (The pre-fix symptom:
                // `assertz(f(7)), f(7)` FALSE through a baked-snapshot
                // bundle — the ISO logical update view broken.)
                bool hasIl = !_dynamicFunctors.Contains(calleeFid)
                    && ilTable is not null
                    && (uint)calleeFid < (uint)ilTable.Length
                    && ilTable[calleeFid] is not null;
                if (hasIl)
                {
                    DiagCountCallIlRewrite();
                    buf[bufOffset] = site.IsExecute
                        ? (byte)Shumway.Core.Opcode.ExecuteIl
                        : (byte)Shumway.Core.Opcode.CallIl;
                    // Replace the address operand with the functor id;
                    // for Call the trailing numLivePerms stays put.
                    Shumway.Core.BytecodeIO.WriteInt32(buf, bufOffset + 1, calleeFid);
                    continue;
                }

                // Otherwise classify as bytecode-only when we can prove
                // IL will never come. Falls through (leaves Call /
                // Execute as-is) when:
                //   - the callee is unresolved (no CompiledPredicate at
                //     link time — e.g., an assertz-auto-promoted
                //     functor materialised after the linker ran)
                //   - the callee MAY still be promotable
                //   - the callee is a dynamic predicate. Dynamic
                //     dispatch goes through the JitIndexProfile (chunk
                //     75) counter inside OnDispatch — rewriting to
                //     CallBytecode would bypass RecordCall, breaking
                //     the dynamic-predicate re-index threshold. Static
                //     bytecode-only predicates (oversized, threshold-
                //     disabled, etc.) have no such tracking concern
                //     and are safe to rewrite.
                if (predicateByFid.TryGetValue(calleeFid, out var calleePred)
                    && IlPromotion.IsPermanentlyBytecodeOnly(calleeFid, calleePred)
                    && !IsDynamicPredicate(calleePred))
                {
                    buf[bufOffset] = site.IsExecute
                        ? (byte)Shumway.Core.Opcode.ExecuteBytecode
                        : (byte)Shumway.Core.Opcode.CallBytecode;
                    // Operand stays as the absolute target address.
                    continue;
                }

                // Phase 33 L1 (Stage B.4) — the site stays a generic Call/Execute
                // because the callee may still earn IL mid-query. Record it (by
                // callee fid) so the moment the callee's delegate installs, the
                // site is patched to CallIl/ExecuteIl for the rest of the query.
                // Persistent-buffer sites only: the query overlay is rebuilt at
                // the next setup anyway, and its buffer may be replaced mid-query.
                // Skip dynamic callees — their dispatch must keep feeding the
                // JitIndexProfile counter inside OnDispatch (chunk 227).
                if (absAddr < _querySplit
                    && (calleePred is null || !IsDynamicPredicate(calleePred)))
                {
                    (_promotableCallSites ??= new()).TryGetValue(calleeFid, out var list);
                    if (list is null) _promotableCallSites[calleeFid] = list = new();
                    list.Add((absAddr, site.IsExecute));
                }
            }
        }
        DiagIlRewriteTotal();
    }

    // Phase 33 L1 — generic Call/Execute sites whose callee may promote later,
    // indexed by callee fid, in the PERSISTENT buffer. Rebuilt every query setup
    // (see InstallCallIlRewrites); consumed by OnCalleePromoted.
    private Dictionary<int, List<(int AbsAddr, bool IsExecute)>>? _promotableCallSites;
    private Shumway.Interpreter.BytecodeInterpreter? _rewriteInterp;

    /// <summary>Phase 33 L1 — Stage B.4: called (on the engine thread, from
    /// <see cref="IlPromotionStore.OnPromotionInstalled"/>) when a delegate is
    /// installed mid-query. Publishes the delegate in the interpreter's direct
    /// <c>IlByFunctorId</c> table and patches the callee's recorded generic call
    /// sites to <c>CallIl</c>/<c>ExecuteIl</c> — the rest of the running query
    /// dispatches directly instead of paying the OnDispatch interface + dict +
    /// wrapper per call (previously that tax lasted until the next query setup,
    /// i.e. the whole run for a single-goal <c>--exe</c>).</summary>
    private void OnCalleePromoted(int calleeFid, Shumway.Compiler.Il.PredicateDelegate del)
    {
        var interp = _rewriteInterp;
        if (interp is null) return;
        // 1. Direct dispatch table (grow if needed; engine thread — no races).
        var table = interp.IlByFunctorId;
        if (table is null || calleeFid >= table.Length)
        {
            var grown = new Func<Shumway.Core.Activation, int, bool>?[calleeFid + 1];
            table?.CopyTo(grown, 0);
            interp.IlByFunctorId = table = grown;
        }
        table[calleeFid] = del.Invoke;
        // 2. Patch the recorded persistent-buffer sites.
        if (_promotableCallSites is null
            || !_promotableCallSites.TryGetValue(calleeFid, out var sites)) return;
        var buf = _persistentProgram;
        if (buf is not null)
        {
            foreach (var (absAddr, isExecute) in sites)
            {
                int width = isExecute ? 5 : 9;
                if (absAddr + width > buf.Length) continue;
                byte expected = isExecute
                    ? (byte)Shumway.Core.Opcode.Execute
                    : (byte)Shumway.Core.Opcode.Call;
                if (buf[absAddr] != expected) continue;   // already rewritten / stale
                DiagCountCallIlRewrite();
                buf[absAddr] = isExecute
                    ? (byte)Shumway.Core.Opcode.ExecuteIl
                    : (byte)Shumway.Core.Opcode.CallIl;
                Shumway.Core.BytecodeIO.WriteInt32(buf, absAddr + 1, calleeFid);
            }
        }
        _promotableCallSites.Remove(calleeFid);
    }

    /// <summary>Chunk 414 — diag-build-only (<c>-p:ShumwayDiag=true</c> +
    /// <c>SHUMWAY_IL_DIAG=1</c>): the chunk-396 per-query IL-dispatch
    /// diagnostics. All three hooks are stripped from normal builds.</summary>
    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private void DiagIlTable(Func<Shumway.Core.Activation, int, bool>?[]? ilTable)
    {
        if (System.Environment.GetEnvironmentVariable("SHUMWAY_IL_DIAG") == "1")
            System.Console.Error.WriteLine(
                $"[il-diag] promoted/registered fids={IlPromotion.PromotedFunctorIds().Count()} "
                + $"ilTable.Length={(ilTable?.Length ?? 0)} Threshold={IlPromotion.Threshold}");
    }

    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private void DiagCountCallIlRewrite() => _diagCallIlCount++;

    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private void DiagIlRewriteTotal()
    {
        if (System.Environment.GetEnvironmentVariable("SHUMWAY_IL_DIAG") == "1")
            System.Console.Error.WriteLine(
                $"[il-diag] CallIl/ExecuteIl rewrites installed this query={_diagCallIlCount}");
    }

    /// <summary>Chunk 226/227 — true when the predicate is a dynamic
    /// one (its bytecode begins with <see cref="Shumway.Core.Opcode.EnterDynamic"/>
    /// per chunk 159). Dynamic predicates must keep using the
    /// <see cref="Shumway.Core.Opcode.Call"/> / <see cref="Shumway.Core.Opcode.Execute"/>
    /// path so the OnDispatch hook can bump the JitIndexProfile
    /// counter (chunk 75) that drives re-indexing.</summary>
    private static bool IsDynamicPredicate(Shumway.Compiler.Wam.CompiledPredicate pred)
        => pred.Bytecode.Length > 0
            && pred.Bytecode[0] == (byte)Shumway.Core.Opcode.EnterDynamic;

    // ---- Phase 33 T3 — process-wide persisted-IL cache ---------------------
    // Loading a bundle entry's persisted IL means: clone + patch the assembly
    // image, Assembly.Load, reflect the P_* methods, CreateDelegate each. All
    // of that output is engine-agnostic — compiled IL takes Activation as a
    // parameter (the ADR-011 invariant), functor ids come from the process-
    // global atom/functor tables, and resume markers are process-global dense
    // ids — and the patch application itself is deterministic within a process
    // (each sentinel resolves by NAME through the global tables). So the load
    // is done ONCE per IL content for the process lifetime and shared across
    // engines, mirroring the _loadedNativeLibraries table. Without this, an
    // EnginePool loading the same bundle N times paid N Assembly.Loads and N
    // JITs of identical code. Entries never evict — like a loaded native
    // library, a loaded assembly can't be unloaded anyway (no collectible
    // AssemblyLoadContext here by design: the delegates are cached globally).
    private sealed class PersistedIlModule
    {
        public required List<(int Slot, int FunctorId,
            Shumway.Compiler.Il.PredicateDelegate Delegate)> Bound;
        public Dictionary<int, byte[]>? IndexGraphs;   // runtime fid → dispatch graph
        public Dictionary<int, int>? RegionAliases;    // member fid → resume marker
    }

    private static readonly Dictionary<string, PersistedIlModule?> _loadedPersistedIl = new();
    private static readonly object _loadedPersistedIlLock = new();

    /// <summary>Test/diagnostic: the number of real <c>Assembly.Load</c> calls
    /// for persisted IL (distinct content loads once for the whole process).</summary>
    internal static int PersistedIlLoadCount;

    /// <summary>Test/diagnostic: whether this entry's persisted IL is already
    /// in the process-wide cache (a later LoadBundle of the same content
    /// reuses the loaded assembly + delegates instead of re-loading).</summary>
    internal static bool IsPersistedIlCached(BundleEntry entry)
    {
        lock (_loadedPersistedIlLock)
            return _loadedPersistedIl.ContainsKey(PersistedIlCacheKey(entry));
    }

    /// <summary>Content key over everything that determines the loaded module:
    /// the IL image plus its patch and entries tables. Same bytes ⇒ same
    /// patched assembly within this process (patches resolve by name against
    /// the global tables), so a hash of the inputs is a sound identity.</summary>
    private static string PersistedIlCacheKey(BundleEntry entry)
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var len = new byte[4];
        void Add(byte[]? b)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(len, b?.Length ?? -1);
            sha.AppendData(len);
            if (b is not null) sha.AppendData(b);
        }
        Add(entry.CompiledIl);
        Add(entry.CompiledIlPatches);
        Add(entry.CompiledIlEntries);
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static PersistedIlModule? GetOrLoadPersistedIl(BundleEntry entry)
    {
        string key = PersistedIlCacheKey(entry);
        // The lock is held across Assembly.Load on purpose (same discipline as
        // UseNativeLibrary): the guarantee is load-ONCE, and a racing second
        // loader would otherwise produce a duplicate assembly + JIT.
        lock (_loadedPersistedIlLock)
        {
            if (_loadedPersistedIl.TryGetValue(key, out var cached)) return cached;
            var module = LoadPersistedIl(entry);
            _loadedPersistedIl[key] = module;
            return module;
        }
    }

    private static PersistedIlModule? LoadPersistedIl(BundleEntry entry)
    {
        // Phase 17 — overwrite each baked build-time atom/functor id sentinel
        // with the runtime-process id BEFORE handing the bytes to
        // Assembly.Load. Once the assembly is loaded its IL is read-only
        // mapped, so the patch must happen on the byte buffer (a copy so we
        // don't mutate the caller's reusable BundleEntry).
        byte[] ilBytes = entry.CompiledIl!;
        if (entry.CompiledIlPatches is not null && entry.CompiledIlPatches.Length > 0)
        {
            ilBytes = (byte[])entry.CompiledIl!.Clone();
            ApplyIlPatches(ilBytes, entry.CompiledIlPatches);
        }
        var asm = System.Reflection.Assembly.Load(ilBytes);
        System.Threading.Interlocked.Increment(ref PersistedIlLoadCount);
        var type = asm.GetType(Shumway.Compiler.Il.PersistedIlBuilder.TypeName);
        if (type is null) return null;

        // Method-name layout from PersistedIlBuilder:
        //   P_{slot}_{functorId}_{sanitisedName}
        // Phase 17 — when CompiledIlEntries is present (V3+ bundles), use the
        // per-method (name, arity) table to intern the name in THIS process
        // and bind the delegate under the runtime functor id. Falls back to
        // parsing the build-time functor id from the method name only for
        // pre-V3 bundles (which never run cross-process correctly anyway).
        Dictionary<string, (string Name, int Arity, int Slot)>? methodInfo = null;
        Dictionary<string, byte[]>? graphByMethod = null;
        Dictionary<string, IReadOnlyList<(string Name, int Arity, int Cursor)>>?
            regionMembersByMethod = null;
        if (entry.CompiledIlEntries is not null && entry.CompiledIlEntries.Length > 0)
        {
            methodInfo = new Dictionary<string, (string, int, int)>();
            foreach (var pe in Shumway.Compiler.Il.IlPersistedEntryCodec.Decode(
                entry.CompiledIlEntries))
            {
                methodInfo[pe.MethodName] = (pe.Name, pe.Arity, pe.Slot);
                if (pe.IndexGraph is { Length: > 0 } g)
                    (graphByMethod ??= new Dictionary<string, byte[]>())[pe.MethodName] = g;
                if (pe.RegionMembers is { Count: > 0 } rm)
                    (regionMembersByMethod ??= new())[pe.MethodName] = rm;
            }
        }
        var bound = new List<(int Slot, int FunctorId,
            Shumway.Compiler.Il.PredicateDelegate Delegate)>();
        Dictionary<int, byte[]>? indexGraphs = null;
        Dictionary<int, int>? regionAliases = null;
        foreach (var method in type.GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (!method.Name.StartsWith("P_")) continue;
            int slot;
            int functorId;
            if (methodInfo is not null && methodInfo.TryGetValue(method.Name, out var info))
            {
                int aid = Shumway.Core.AtomTable.Intern(info.Name).Id;
                functorId = Shumway.Core.FunctorTable.Intern(aid, info.Arity);
                slot = info.Slot;
            }
            else
            {
                int u1 = method.Name.IndexOf('_');
                int u2 = method.Name.IndexOf('_', u1 + 1);
                int u3 = method.Name.IndexOf('_', u2 + 1);
                if (u1 < 0 || u2 < 0 || u3 < 0) continue;
                if (!int.TryParse(method.Name.AsSpan(u1 + 1, u2 - u1 - 1), out slot)) continue;
                if (!int.TryParse(method.Name.AsSpan(u2 + 1, u3 - u2 - 1), out functorId)) continue;
            }
            var del = method.CreateDelegate<Shumway.Compiler.Il.PredicateDelegate>();
            bound.Add((slot, functorId, del));
            if (graphByMethod is not null
                && graphByMethod.TryGetValue(method.Name, out var graphBytes))
                (indexGraphs ??= new())[functorId] = graphBytes;
            // Chunk 402: functorId here is the region ROOT's runtime fid; each
            // member's runtime fid maps to a marker at the member's entry cursor.
            if (regionMembersByMethod is not null
                && regionMembersByMethod.TryGetValue(method.Name, out var rMembers))
            {
                foreach (var (mName, mArity, mCursor) in rMembers)
                {
                    int mAid = Shumway.Core.AtomTable.Intern(mName, permanent: true).Id;
                    int mFid = Shumway.Core.FunctorTable.Intern(mAid, mArity);
                    (regionAliases ??= new())[mFid] =
                        Activation.EncodeResumeMarker(functorId, mCursor);
                }
            }
        }

        // Populate the static delegates array (chunk 71 multi-clause
        // self-reference). Once per loaded assembly — the field lives on the
        // loaded type, shared by every engine using this module.
        var dF = type.GetField(
            Shumway.Compiler.Il.PersistedIlBuilder.DelegatesFieldName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (dF is not null && bound.Count > 0)
        {
            int size = bound.Max(b => b.Slot) + 1;
            var arr = new Shumway.Compiler.Il.PredicateDelegate[size];
            foreach (var (slot, _, del) in bound) arr[slot] = del;
            dF.SetValue(null, arr);
        }
        return new PersistedIlModule
        {
            Bound = bound,
            IndexGraphs = indexGraphs,
            RegionAliases = regionAliases,
        };
    }

    // ---- Phase 33 T4 — process-wide static-region link cache ---------------
    // The static region links once per ENGINE (chunk 151b caches it in
    // _staticLink), but an EnginePool loading the same bundle N times still
    // ran N identical full-program links on each engine's first query. The
    // link is a pure function of the ordered predicate list (bytecode +
    // switch tables + call sites — all immutable) and the load offset, so
    // the result is shared process-wide, keyed by a content hash. The hash
    // covers the post-literal-remap bytecode bytes, so an engine whose
    // literal pools were populated in a different order simply misses and
    // links fresh — never a wrong hit. LinkResult is read-only downstream:
    // its bytecode is COPIED into each engine's persistent buffer (per-engine
    // Call→CallIl patches land in the copy), and static switch tables are
    // never mutated (chunk-155 in-place mutation applies to dynamics only).
    private static readonly Dictionary<string, Shumway.Compiler.Wam.Linker.LinkResult>
        _sharedStaticLinks = new();
    private static readonly object _sharedStaticLinksLock = new();

    // Crude growth bound: a long-lived process churning DISTINCT static
    // programs (a test suite, a REPL consulting repeatedly) would otherwise
    // accumulate full program images forever. The pool scenario this cache
    // exists for uses a handful of distinct programs, so wholesale reset on
    // overflow is simpler than LRU and costs one relink per evicted program.
    private const int SharedStaticLinkCapacity = 64;

    /// <summary>Test/diagnostic: the number of real static-region link runs
    /// (identical static programs link once for the whole process).</summary>
    internal static int StaticLinkBuildCount;

    /// <summary>Test/diagnostic (per-engine, so parallel tests can't perturb
    /// it): whether this engine's most recent static-region link came from
    /// the process-wide shared cache instead of a fresh link run.</summary>
    internal bool LastStaticLinkWasSharedHit;

    private Shumway.Compiler.Wam.Linker.LinkResult GetOrLinkStatic(
        List<Shumway.Compiler.Wam.CompiledPredicate> staticPreds, int loadOffset)
    {
        string key = StaticLinkKey(staticPreds, loadOffset);
        lock (_sharedStaticLinksLock)
        {
            if (_sharedStaticLinks.TryGetValue(key, out var hit))
            {
                LastStaticLinkWasSharedHit = true;
                return hit;
            }
            LastStaticLinkWasSharedHit = false;
            if (_sharedStaticLinks.Count >= SharedStaticLinkCapacity)
                _sharedStaticLinks.Clear();
            var link = new Shumway.Compiler.Wam.Linker().Link(staticPreds, loadOffset: loadOffset);
            System.Threading.Interlocked.Increment(ref StaticLinkBuildCount);
            _sharedStaticLinks[key] = link;
            return link;
        }
    }

    /// <summary>Content fingerprint of the static link inputs: load offset
    /// plus, per predicate in order, functor id, bytecode bytes, and the
    /// switch-table content (keys/values/default live OUTSIDE the bytecode)
    /// and call-site table (drives the linker's resolution).</summary>
    private static string StaticLinkKey(
        List<Shumway.Compiler.Wam.CompiledPredicate> staticPreds, int loadOffset)
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var word = new byte[4];
        void AddInt(int v)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(word, v);
            sha.AppendData(word);
        }
        AddInt(loadOffset);
        AddInt(staticPreds.Count);
        foreach (var p in staticPreds)
        {
            AddInt(p.FunctorId);
            AddInt(p.Bytecode.Length);
            sha.AppendData(p.Bytecode);
            AddInt(p.SwitchTables.Count);
            foreach (var t in p.SwitchTables)
            {
                AddInt(t.DefaultAddress);
                AddInt(t.Count);
                for (int i = 0; i < t.Count; i++) { AddInt(t.Keys[i]); AddInt(t.Values[i]); }
            }
            AddInt(p.CallSites.Count);
            foreach (var c in p.CallSites)
            {
                AddInt(c.OpcodeOffset);
                AddInt(c.CalleeFunctorId);
                AddInt(c.IsExecute ? 1 : 0);
            }
            // ADR-035 — the debug metadata is part of the identity, even though it is
            // not part of the bytecode. Two programs can compile to byte-identical code
            // and be written on entirely different LINES: the stop sites and the frame
            // maps are what tell them apart. Leaving them out of the key let one
            // program's link be handed to another, which then reported its neighbour's
            // source positions — a debugger showing the wrong file, with no error
            // anywhere. Debug programs are also exactly the case this cache was never
            // for (it exists so a pool can load one bundle N times), so the extra
            // hashing costs nothing that matters.
            AddInt(p.DebugStops.Count);
            foreach (var s in p.DebugStops)
            {
                AddInt(s.Offset);
                AddInt(s.SiteId);
            }
            AddInt(p.DebugFrames.Count);
            foreach (var f in p.DebugFrames)
            {
                AddInt(f.Start);
                AddInt(f.End);
                AddInt(f.HasFrame ? 1 : 0);
                AddInt(f.Variables.Count);
                foreach (var v in f.Variables)
                {
                    AddInt(v.Slot);
                    sha.AppendData(System.Text.Encoding.UTF8.GetBytes(v.Name));
                }
            }
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static void ApplyIlPatches(byte[] ilBytes, byte[] patchTable)
    {
        var sites = Shumway.Compiler.Il.IlPatchSiteCodec.Decode(patchTable);
        foreach (var s in sites)
        {
            int runtimeValue;
            switch (s.Kind)
            {
                case Shumway.Compiler.Il.IlPatchKind.Atom:
                    runtimeValue = Shumway.Core.AtomTable.Intern(s.Name).Id;
                    break;
                case Shumway.Compiler.Il.IlPatchKind.Functor:
                {
                    int aid = Shumway.Core.AtomTable.Intern(s.Name).Id;
                    runtimeValue = Shumway.Core.FunctorTable.Intern(aid, s.Arity);
                    break;
                }
                case Shumway.Compiler.Il.IlPatchKind.ResumeMarker:
                {
                    int aid = Shumway.Core.AtomTable.Intern(s.Name).Id;
                    int fid = Shumway.Core.FunctorTable.Intern(aid, s.Arity);
                    runtimeValue = Shumway.Core.Activation.EncodeResumeMarker(fid, s.Cursor);
                    break;
                }
                default:
                    throw new InvalidDataException(
                        $"Unknown IL patch kind {(int)s.Kind}.");
            }

            int off = s.AbsoluteByteOffset;
            if (off < 0 || off + 4 > ilBytes.Length)
                throw new InvalidDataException(
                    $"IL patch site at offset 0x{off:X8} is out of range "
                    + $"(IL buffer is {ilBytes.Length} bytes).");
            // Defensive sanity: the four bytes currently at the offset
            // must equal the recorded sentinel. If they don't, the
            // patch table is desynchronised from the .dll — fail loudly
            // rather than silently writing into the wrong instruction.
            int current = ilBytes[off]
                | (ilBytes[off + 1] << 8)
                | (ilBytes[off + 2] << 16)
                | (ilBytes[off + 3] << 24);
            if (current != s.Sentinel)
                throw new InvalidDataException(
                    $"IL patch site for {s.Kind} {s.Name}/{s.Arity} at "
                    + $"offset 0x{off:X8} holds 0x{current:X8}, expected "
                    + $"sentinel 0x{s.Sentinel:X8} — patch table out of "
                    + $"sync with the embedded .dll.");
            ilBytes[off] = (byte)(runtimeValue & 0xFF);
            ilBytes[off + 1] = (byte)((runtimeValue >> 8) & 0xFF);
            ilBytes[off + 2] = (byte)((runtimeValue >> 16) & 0xFF);
            ilBytes[off + 3] = (byte)((runtimeValue >> 24) & 0xFF);
        }
    }

    /// <summary>Chunk 178: registers a source-less bundle entry's
    /// predicates with the engine without going through
    /// <see cref="ConsultString"/>. Populates the per-module
    /// <see cref="ModuleManifest"/> from the entry's
    /// <see cref="BundleEntry.Defined"/> list (publics → public set,
    /// dynamics → dynamic set + engine-wide <c>_dynamicFunctors</c>),
    /// then decodes the entry's <see cref="BundleEntry.CompiledBytecode"/>
    /// and registers each predicate in
    /// <see cref="_precompiledStaticPredicates"/>. The next
    /// <see cref="SetupQueryFromTerm"/> appends those predicates to
    /// the static-link region so call sites resolve identically to
    /// the source-bearing path. The bytecode is byte-identical to
    /// what <see cref="SetupQueryFromTerm"/> would have produced
    /// (chunk 176's ModuleRewrite parity).</summary>
    /// <summary>Decodes a bundle entry's <see cref="BundleEntry.CompiledBytecode"/>,
    /// remaps its module-local literal ids into the engine's shared pools (see
    /// <see cref="RemapPrecompiledLiterals"/>), records the module, and warms its IL
    /// when Tier 1 is enabled. The single decode path shared by the source-less
    /// <see cref="LoadEntryFromBytecode"/> and the source-bearing
    /// <see cref="LoadBundleCore"/> loop.
    ///
    /// <para><paramref name="registerStaticPredicates"/> — true only for the
    /// source-less path: there the bytecode IS the definition, so each predicate
    /// goes into <see cref="_precompiledStaticPredicates"/>. For a source-bearing
    /// entry the source consult is the truth and the bytecode is only an IL-warm /
    /// skip-compile cache, so it is NOT registered there.</para></summary>
    private Shumway.Compiler.Wam.CompiledModule DecodeAndRegisterPrecompiledModule(
        BundleEntry entry, bool registerStaticPredicates)
    {
        var module = CompiledModuleCodec.Decode(entry.CompiledBytecode!);
        // Remap COMPILE-TIME, module-local float/string/bigint literal ids into the
        // engine's ONE shared _literalPools (mutating the freshly-decoded bytecode in
        // place) — else a static literal reads whatever value sits at that id in the
        // merged pool (the two-float bug). Afterward every id indexes the live pool,
        // so IL float value-baking reads _literalPools.Floats directly.
        RemapPrecompiledLiterals(module);
        _precompiledModules[entry.ModuleName] = module;
        // Warm IL only when the host opted into Tier 1 (Threshold > 0). Warming under
        // the default threshold=0 would force Sigil over every eligible predicate at
        // load — wasteful when the user wanted Tier 0, and it hits chunk-189/190 IL
        // corner cases the runtime promotion path avoids under threshold 0.
        bool warmIl = IlPromotion.Threshold > 0;
        foreach (var pred in module.Predicates)
        {
            if (registerStaticPredicates)
                _precompiledStaticPredicates[pred.FunctorId] = pred;
            if (warmIl)
                IlPromotion.Warm(pred.FunctorId, pred);
        }
        return module;
    }

    private void LoadEntryFromBytecode(BundleEntry entry)
    {
        if (entry.CompiledBytecode is null)
            throw new InvalidOperationException(
                $"LoadEntryFromBytecode: entry '{entry.ModuleName}' has no compiled bytecode.");

        // Resolve the manifest under the entry's module name. The
        // contract here mirrors ConsultString's "explicit module"
        // path (PrologEngine.cs:2792) — `:- module(name).` would have
        // landed us in the same place. A subsequent source-bearing
        // load of the same module name is allowed to extend it
        // (consistent with the "rolling user module" pattern), but
        // each predicate id is at most once in the precompiled set.
        if (!_modules.TryGetValue(entry.ModuleName, out var manifest))
        {
            manifest = new ModuleManifest(entry.ModuleName);
            _modules[entry.ModuleName] = manifest;
        }

        foreach (var d in entry.Defined)
        {
            int fid = Shumway.Core.FunctorTable.Intern(
                Shumway.Core.AtomTable.Intern(d.Indicator.Name, permanent: true).Id,
                d.Indicator.Arity);
            if (d.Visibility == PredicateVisibility.Public)
                manifest.PublicFunctors.Add(fid);
            else if (d.Visibility == PredicateVisibility.Dynamic)
            {
                manifest.DynamicFunctors.Add(fid);
                _dynamicFunctors.Add(fid);
                if (!_dynamicClauses.ContainsKey(fid))
                    _dynamicClauses[fid] = new List<Clause>();
            }
            else // Local — record the bare fid so query setup can fold
                 // it into the module's locals (chunk 209).
            {
                if (!_precompiledModuleLocals.TryGetValue(entry.ModuleName, out var localSet))
                {
                    localSet = new HashSet<int>();
                    _precompiledModuleLocals[entry.ModuleName] = localSet;
                }
                localSet.Add(fid);
            }
        }

        // Chunk 209: seed _dynamicClauses with the source-declared
        // clauses of every `:- dynamic foo/N.` predicate. Mirrors what
        // ConsultString does (PrologEngine.cs:3318-3341) — without
        // this, dispatch / clause/2 / retract would see an empty
        // dynamic store and the predicate would behave as if it had
        // no clauses. TermCodec rehydrates the AST so
        // SetupQueryFromTerm's downstream PredicateCompiler builds
        // the dynamic trampoline with check_visible entries pointing
        // at born=current-gen / died=MAX initial clauses.
        foreach (var seed in entry.DynamicSeeds)
        {
            int fid = Shumway.Core.FunctorTable.Intern(
                Shumway.Core.AtomTable.Intern(seed.Indicator.Name, permanent: true).Id,
                seed.Indicator.Arity);
            var slot = GetOrCreateDynamicSlot(fid);
            foreach (var encoded in seed.EncodedClauses)
                slot.Add(TermCodec.DecodeClause(encoded));
            // ADR-023 priming — a bundle's `:- dynamic`/`:- visible` predicate
            // shipped WITH clauses runs as its Tier-1 IL snapshot from the first
            // call (evictable on the first mutation).
            if (seed.EncodedClauses.Count > 0)
                IlPromotion.MarkPrime(fid);
            // Chunk 440 — remember which module these clauses came from.
            // The entry's static bytecode was mangled by ShmoCompiler
            // under entry.ModuleName, so the query-setup rewrite of these
            // rehydrated clauses must run under the SAME module context
            // (module name + that module's locals) or a body call to a
            // module-local predicate stays bare while its target is
            // `module$name`-mangled.
            if (entry.ModuleName != DefaultModuleName)
                _dynamicSeedModule[fid] = entry.ModuleName;
        }

        // ADR-022 — repopulate this engine's native-block table so the baked
        // `'$native_run'('$nb$…', Vars)` dispatch (in the entry's bytecode) finds
        // its block at run time. The C statement source is re-parsed here (the C
        // symbol table is only needed for the compile-time inference, already
        // baked into the serialized vars); a malformed block would have failed at
        // compile, so a parse error here is a corrupt bundle — surfaced, not
        // swallowed.
        foreach (var nb in entry.NativeBlocks)
        {
            var stmts = Shumway.Compiler.NativeC.CParser.ParseStatements(nb.RawText);
            AddNativeBlock(nb.Name, nb.Vars.ToArray(), stmts.ToArray(), nb.ScalarGlobals.ToArray());
        }

        // ADR-024 — restore the `:- native` indicators + `:- c` prototypes so a
        // source-stripped bundle resolves native calls (the directive/prototypes
        // are not re-applied without re-consulting the source).
        foreach (var pr in entry.NativeFunctions)
        {
            _nativeFunctions.Add(FunctorTable.Intern(
                AtomTable.Intern(pr.Name, permanent: true).Id, pr.Arity));
            _nativeFunctionNames.Add(pr.Name);
        }
        if (!string.IsNullOrEmpty(entry.NativeDecls))
            RegisterNativePrototypes(
                Shumway.Compiler.NativeC.CParser.ParseDeclarations(entry.NativeDecls));

        // Decode + literal-remap + record + warm IL (the bytecode IS the definition
        // here, so register the static predicates).
        DecodeAndRegisterPrecompiledModule(entry, registerStaticPredicates: true);

        // The static program just changed shape — drop the cached
        // static link region so the next query rebuild picks up the
        // new predicates.
        _staticLink = null;
        _staticPredicateCache.Clear();
        _skipCompileMergedCache = null;   // chunk 430 — static cache cleared
        InvalidatePersistent();
    }

    /// <summary>Per-engine cache of precompiled predicates from any
    /// bundle blob loaded with <see cref="LoadBundle(Bundle)"/>
    /// (chunk 53). The query-setup path consults this cache before
    /// running ModuleCompiler over the consulted source — for any
    /// predicate whose functor id is in the cache, the cached
    /// <see cref="Shumway.Compiler.Wam.CompiledPredicate"/> is reused
    /// verbatim. Mutating the cache directly is not supported; use
    /// <see cref="LoadBundle(Bundle)"/> to populate it.</summary>
    public IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate> PrecompiledClauseCache
        => _precompiledClauseCache;
    private readonly Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate> _precompiledClauseCache = new();

    /// <summary>Chunk 178: pre-compiled predicates from source-less
    /// bundle entries. Their bytecode is already mangled + runtime-
    /// ready (ShmoCompiler in chunk 176 applies the same transforms
    /// SetupQueryFromTerm would), so they bypass the AST → ModuleCompiler
    /// pipeline entirely and slot straight into the static-link region.
    /// Populated by <see cref="LoadEntryFromBytecode"/> on
    /// <see cref="LoadBundle(Bundle)"/>; consumed by
    /// <see cref="SetupQueryFromTerm"/> when it (re)builds the static
    /// link. Keyed by FunctorId so a later source-bearing consult of
    /// the same predicate replaces the precompiled entry cleanly.</summary>
    private readonly Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>
        _precompiledStaticPredicates = new();

    /// <summary>Persisted WAM-independent dispatch graphs (--strip-wam), keyed by
    /// runtime functor id. Populated at LoadBundle; registered onto each query's
    /// fresh engine at setup (the indexed-dispatch cache is per-engine), so a
    /// stripped indexed predicate resolves its entry clause without a WAM body.</summary>
    private readonly Dictionary<int, byte[]> _persistedIndexGraphs = new();

    /// <summary>Chunk 402 — region member-entry aliases, keyed by the member's runtime
    /// functor id; the value is <c>EncodeResumeMarker(regionRootRuntimeFid, entryCursor)</c>.
    /// Populated at LoadBundle from each region method's <see cref="Shumway.Compiler.Il.
    /// IlPersistedEntry.RegionMembers"/> table. Injected into the query address map
    /// (lowest priority — only for a member with no WAM address and no standalone IL
    /// delegate) so a by-fid call to a stripped absorbed member dispatches INTO its
    /// region method at the member's entry cursor.</summary>
    private readonly Dictionary<int, int> _regionMemberAliases = new();

    /// <summary>Chunk 230 — read-only view of
    /// <see cref="_precompiledStaticPredicates"/>. Lets
    /// <see cref="BundleWriter.CompileEntryToIl"/> see the predicates
    /// loaded from a source-less bundle entry (the chunk-178 path),
    /// not just the ones populated by ConsultString.</summary>
    public IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate>
        PrecompiledStaticPredicates => _precompiledStaticPredicates;

    /// <summary>Per-engine cache of compiled dynamic predicates (chunk 68).
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
    private readonly Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate> _dynamicPredicateCache = new();

    /// <summary>Per-engine cache of compiled <em>static</em> predicates
    /// (chunk 82). Static predicates are immutable between consults, so
    /// once compiled their bytecode is reused on every subsequent query
    /// instead of being recompiled from source — which is the dominant
    /// cost a meta-call (findall / call / forall, each a fresh query
    /// setup) used to pay. Cleared wholesale by <see cref="ConsultString"/>,
    /// the only operation that changes the static program. Predicates
    /// whose bytecode references per-module literal pools are filtered
    /// out at populate time, exactly as for the dynamic cache.</summary>
    public IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate> StaticPredicateCache
        => _staticPredicateCache;
    private readonly Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate> _staticPredicateCache = new();

    // ====================================================================
    // Chunk 430 — per-query setup caching. Query setup used to re-derive
    // stable state on every query: the full transform chain over every
    // consulted module (prelude included), three dictionary merges, and a
    // bare-alias loop doing two Substring allocs + an intern per linked
    // functor. Everything below is a cache of one of those derivations,
    // keyed either by the derivation generation or by the persistent-link
    // rebuild.
    // ====================================================================

    /// <summary>Chunk 430 — derivation generation. Bumped by every mutation
    /// that can change the per-module transform pipeline's output: consult,
    /// bundle load, abolish, restore_state and every dynamic-functor-set
    /// change funnel through <see cref="InvalidatePersistent"/> (which
    /// bumps), and the implicit_dynamic auto-promotion in
    /// <see cref="EnsureDynamic"/> (which doesn't invalidate the persistent
    /// buffer) bumps explicitly. Bumping more often than strictly necessary
    /// (e.g. on the chunk-158 compaction) is safe — it just costs one
    /// re-transform at the next query setup, which was the per-query status
    /// quo before this chunk.</summary>
    private int _derivationGen;

    /// <summary>Chunk 430 — cached output of the static transform chain
    /// (MetaWrapperUnfold → ClausePipeline → ModuleRewrite) over every
    /// consulted module, plus the user-module locals set and the set of
    /// rewritten-clause head functor ids. A pure function of the module
    /// manifests, the mode table, the dynamic-functor set and the
    /// precompiled module locals — all of which bump
    /// <see cref="_derivationGen"/> when they change. Between consults,
    /// every query reuses this instead of re-transforming the whole program
    /// (the ~600-line prelude included) per query.</summary>
    private List<Clause>? _staticRewriteClauses;
    private HashSet<int>? _staticRewriteUserLocals;
    private HashSet<int>? _staticRewriteHeadFids;
    private int _staticRewriteGen = -1;

    /// <summary>Chunk 440 — per-module locals sets computed by the static
    /// rewrite pass (the same `locals` each module's ModuleRewrite context
    /// gets), cached alongside <see cref="_staticRewriteUserLocals"/>. The
    /// dynamic-clause rewrite consults it so a dynamic clause attributed to
    /// a non-user module (see <see cref="_dynamicSeedModule"/>) mangles its
    /// body calls against THAT module's locals — matching the entry's
    /// static bytecode, which ShmoCompiler mangled under the per-file
    /// module name.</summary>
    private Dictionary<string, HashSet<int>>? _staticRewriteModuleLocals;

    /// <summary>Chunk 440 — module attribution for dynamic predicates whose
    /// clauses came from a named module: bundle dynamic-seed rehydration
    /// (<see cref="LoadEntryFromBytecode"/>) and source-bearing bundle
    /// entries consulted under the entry's module name. A fid absent here
    /// rewrites under the default user context (runtime asserts, plain
    /// ConsultString — unchanged behaviour). Chunk 209 used to sidestep
    /// this by forcing every module-less .shmo to module "user", which made
    /// two module-less files unlinkable (duplicate_module) and aliased
    /// their locals.</summary>
    private readonly Dictionary<int, string> _dynamicSeedModule = new();

    /// <summary>Chunk 430 — per-functor cache of the dynamic clause lists'
    /// transform + rewrite (ClausePipeline + ModuleRewrite under the
    /// user-module dynamic context, including any MetaTransform helper
    /// clauses the pipeline synthesised, whose head fids are recorded
    /// alongside). An entry drops when its functor's clause list mutates
    /// (<see cref="InvalidateDynamicCache"/>); the whole table drops when
    /// <see cref="_derivationGen"/> moves (the rewrite context's inputs —
    /// user locals, dynamic-functor set, mode table — may have changed).</summary>
    private readonly Dictionary<int, (List<Clause> Clauses, List<int> HeadFids)>
        _dynamicRewriteCache = new();
    private int _dynamicRewriteGen = -1;

    /// <summary>Chunk 430 — merged skip-compile cache (the per-query merge
    /// of <see cref="_precompiledClauseCache"/> +
    /// <see cref="_staticPredicateCache"/> + <see cref="_dynamicPredicateCache"/>,
    /// dynamic winning, exactly the precedence the per-query merge used).
    /// Maintained incrementally: nulled wherever
    /// <see cref="_staticPredicateCache"/> is cleared, kept in step with
    /// every <see cref="_dynamicPredicateCache"/> add / remove
    /// (<see cref="DropDynamicPredicateCacheEntry"/>).</summary>
    private Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>? _skipCompileMergedCache;

    /// <summary>Chunk 430 — link metadata derived from
    /// <see cref="_staticLink"/> + <see cref="_dynamicLink"/>:
    /// <see cref="_persistentAddressesCache"/> is the merged REAL address
    /// map (fed to the query linker as external symbols — it must NOT
    /// contain aliases, or a query call site to a module-local predicate's
    /// bare name would link-resolve and break module visibility);
    /// <see cref="_persistentAddressBaseCache"/> additionally carries the
    /// bare-name aliases for module-local predicates (the chunk-86
    /// meta-call alias loop, hoisted out of the per-query path);
    /// <see cref="_persistentPredsByAddressCache"/> is the merged
    /// predicates-by-address map. All three are rebuilt exactly when the
    /// persistent regions are rebuilt (<c>builtPersistentNow</c>); every
    /// <see cref="_staticLink"/> invalidation also forces that via
    /// <see cref="InvalidatePersistent"/>, and the link results themselves
    /// are immutable (the chunk-155c switch-table mirror mutates
    /// <c>_dynamicLink.SwitchTables</c> only, which is why the merged
    /// switch-table list is still rebuilt per query).</summary>
    private Dictionary<int, int>? _persistentAddressesCache;
    private Dictionary<int, int>? _persistentAddressBaseCache;
    private Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>? _persistentPredsByAddressCache;

    /// <summary>Persistent literal pools (ADR-015 chunk B). One set for the
    /// engine's life, so a literal keeps a stable id across queries — the
    /// precondition for caching the static linked region, whose bytecode
    /// embeds those ids.</summary>
    private readonly Shumway.Compiler.Wam.LiteralPools _literalPools = new();

    /// <summary>Chunk 427 — runtime-assert compile cache. The per-assert
    /// pipeline used to build a fresh <see cref="ModuleRewrite.Context"/>
    /// (+ HashSet) and ClauseCompiler per call; both are safe to reuse
    /// (ClauseCompiler re-binds its pool refs at the top of every Compile;
    /// the Context only holds set references, so <c>_dynamicFunctors</c>
    /// mutations stay visible through it), so one of each per engine
    /// suffices.</summary>
    private Shumway.Compiler.Wam.ClauseCompiler? _assertClauseCompiler;
    private ModuleRewrite.Context? _assertDynCtx;

    /// <summary>Chunk 427 — the literal-pool lengths the live query's
    /// interpreter currently holds; recorded at query setup and after each
    /// refresh. <see cref="RefreshLiteralPoolsIfGrown"/> compares against
    /// these (not "did this one compile grow the pool") so a compile that
    /// interned a literal and then bailed to a fallback path can't leave a
    /// later same-literal compile thinking the interpreter is current.</summary>
    /// <summary>Chunk 427 / Phase 33 — the literal-pool lengths each
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

    /// <summary>Chunk 151b: the persistent program buffer —
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


    /// <summary>Chunk 151b: when a query is in flight, the address at
    /// which the per-query overlay begins (the
    /// <see cref="ProgramView"/>'s <c>Split</c>). Persistent growth
    /// mid-query must stay below this, otherwise the query region's
    /// linked offsets collide with newly-extended dynamic bytecode.
    /// The setup code picks this with enough headroom over the
    /// persistent length for typical mid-query <c>assertz</c>
    /// growth.</summary>
    private int _querySplit = -1;

    /// <summary>Chunk 151b: how much address space to reserve between
    /// the persistent program's end and the per-query region's start.
    /// Mid-query <c>assertz</c> may extend persistent up to this much
    /// before the persistent / query address ranges would collide; an
    /// assert that would overflow this gap forces a rebuild
    /// (effectively reverting to the chunk-150 within-query free-list
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
        // Chunk 430 — every mutation that can change the per-module
        // transform derivation funnels through here (consult, bundle
        // load, abolish, restore_state, dynamic-functor-set changes);
        // advance the derivation generation so the static / dynamic
        // rewrite caches recompute at the next query setup. The
        // persistent link-metadata caches need no explicit nulling:
        // they are rebuilt whenever the persistent buffer is
        // (builtPersistentNow), which this method forces.
        _derivationGen++;
        // Phase 33 — chain state + free-list describe offsets in the
        // now-invalidated buffer; start a fresh table for the rebuild.
        // Live engines still running on the old buffer keep their own
        // table through _engineChainTables — invalidation never desyncs
        // an in-flight query's in-place dispatch.
        _dynChainTable = new DynChainTable();
    }

    /// <summary>Phase 11 chunk 157 — user-facing entry point for
    /// persistent-buffer compaction. Invalidates the cached dynamic-
    /// region link so the next query setup rebuilds it from current
    /// <c>_dynamicClauses</c>. After a long run of in-place
    /// mutations (chunks 155b-f), the buffer accumulates clause
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

    /// <summary>Phase 12 chunk 158 — mutation counter that drives
    /// the auto-compaction watermark. Bumped on every dynamic-store
    /// mutation (assertz / asserta / retract / abolish); reset by
    /// <see cref="CompactDynamicCodeBuffer"/> and by the auto-
    /// compaction trigger in <c>SetupQueryFromTerm</c>.</summary>
    private long _persistentMutationsSinceCompact;

    /// <summary>Phase 12 chunk 158 — how many dynamic-store mutations
    /// the engine accumulates before automatically compacting the
    /// persistent buffer at the next query's setup. Default 1000;
    /// host code can raise it (large batch workloads where rebuild
    /// cost dominates) or lower it (memory-tight environments
    /// preferring smaller buffers) or set to <c>long.MaxValue</c>
    /// to disable auto-compaction entirely. Compaction itself stays
    /// callable via <c>compact_dynamic_buffer/0,1</c>.</summary>
    public long CompactWatermark { get; set; } = 1000;

    /// <summary>Phase 12 chunk 158 — diagnostic read of the mutation
    /// counter (mainly for tests that need to verify auto-compaction
    /// fires at the right moment).</summary>
    public long PersistentMutationsSinceCompact =>
        _persistentMutationsSinceCompact;

    /// <summary>Canonical encodings of every (subgoal, answer) pair the
    /// tabling driver has recorded (chunk 106). Backs the <c>'$tbl_seen'/1</c>
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

    /// <summary>Stack of in-flight findall solution buffers (chunk 83).
    /// MetaTransform rewrites <c>findall/3</c> with a callable goal into a
    /// goal sequence driven by the <c>'$findall_*'</c> builtins, which run
    /// in this engine: <c>'$findall_push'</c> pushes a frame here,
    /// <c>'$findall_record'</c> appends a template copy to the top frame,
    /// <c>'$findall_collect'</c> pops it. Solutions must survive the
    /// <c>fail</c>-driven backtracking that enumerates the goal, so they are
    /// held off the WAM heap. Each frame entry is one of two backtrack-safe
    /// forms: a <see cref="Cell"/>[] cell image (Phase 33 I2b — the fast
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

    /// <summary>Records a solution as a backtrack-safe cell image (Phase 33 I2b,
    /// the fast findall path).</summary>
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
        // ADR-015 generation clock has to advance and the chunk-158
        // auto-compaction mutation counter ticks.
        _dbGeneration.Value++;
        _persistentMutationsSinceCompact++;
        DropDynamicPredicateCacheEntry(functorId);
        // ADR-023 — the predicate changed, so any cached Tier-1 IL snapshot of it
        // is stale: evict it. The next call falls back to the in-place-patched
        // Tier-0 bytecode (the current database); the predicate re-warms before
        // re-snapshotting, and past the churn limit stays on Tier 0.
        IlPromotion.EvictDelegate(functorId);
        // ADR-023/034 — the CURRENT query's interpreter snapshotted the
        // delegate into its direct fid table at setup (IL callers dispatch
        // every callee by fid through it via resume markers). Clear the slot
        // so the very next dispatch re-resolves live — TryGet misses, and the
        // call falls back to the Tier-0 enter_dynamic chain (the logical
        // update view).
        var ilTable = _rewriteInterp?.IlByFunctorId;
        if (ilTable is not null && (uint)functorId < (uint)ilTable.Length)
            ilTable[functorId] = null;
        // ADR-034 — and any CALLER whose IL embeds this predicate's inlined
        // snapshot must stop using it: the emitted clause-entry staleness test
        // reads this host-lifetime set (shared into every per-query engine),
        // so the fallback path takes over from the very next clause entry.
        _mutatedDynamicFids.Add(functorId);
        // Chunk 430 — the functor's clause list changed, so its cached
        // transformed/rewritten clauses are stale too.
        _dynamicRewriteCache.Remove(functorId);
    }

    /// <summary>Chunk 430 — removes a dynamic predicate's compiled-bytecode
    /// cache entry and keeps the merged skip-compile cache in step, falling
    /// back to the static / precompiled tier's entry when one exists (the
    /// same precedence the merge uses: precompiled &lt; static &lt;
    /// dynamic). Used by <see cref="InvalidateDynamicCache"/> and the
    /// chunk-75 JIT hotness-flip drops at query setup.</summary>
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

    /// <summary>Runs an AST goal through the same machinery as the string
    /// form, yielding each solution in turn. The free variables of
    /// <paramref name="goal"/> show up in <see cref="Solution.Bindings"/>
    /// under the names they carry in the AST (synthetic <c>_GN</c> names if
    /// the term came from <see cref="TermReader.Materialize"/>).</summary>
    public IEnumerable<Solution> QueryAll(Term goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        LastHaltExitCode = null;
        var setup = SetupQueryFromTerm(goal);
        return RunIteration(this, setup.Program, setup.VarNames, setup.VarHeapIndices,
            setup.Activation, setup.Interp);
    }

    /// <summary>As <see cref="QueryAll(Term)"/> but the supplied
    /// <paramref name="cancellationToken"/> aborts a long-running search at the
    /// next safe point — the engine throws <see cref="OperationCanceledException"/>
    /// (it bubbles past any surrounding <c>catch/3</c>). Runs on the calling
    /// thread; fire the token from another thread (e.g. a key watcher).</summary>
    public IEnumerable<Solution> QueryAll(Term goal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(goal);
        LastHaltExitCode = null;
        var setup = SetupQueryFromTerm(goal);
        return RunIterationCancellable(setup, cancellationToken);
    }

    /// <summary>ADR-035: names the predicate whose entry point is exactly
    /// <paramref name="address"/> — the shape a call/execute operand has, so a
    /// dictionary probe is enough and no containment search is needed. Returns
    /// <c>null</c> for an address that is not a predicate entry (the synthetic
    /// <c>__query__</c> wrapper included), which a debug session reads as "not
    /// a goal worth reporting".</summary>
    internal (string Name, int Arity)? LookupPredicateByAddress(int address)
    {
        var map = _currentPredicatesByAddress;
        if (map is null || !map.TryGetValue(address, out var pred)) return null;
        var (atomId, arity) = FunctorTable.Lookup(pred.FunctorId);
        string name = AtomTable.GetById(atomId)?.Name ?? "?";
        return name == "__query__" ? null : (name, arity);
    }

    /// <summary>ADR-035 — is this predicate one a debugger may stop IN?
    ///
    /// <para>The prelude and the libraries are implicitly <c>:- disable_debug</c>, and they
    /// are compiled that way — but a PORT is raised by the interpreter at every call and
    /// every proceed, whatever the callee was compiled from. So a step landed in
    /// <c>copy_term/3</c>, in <c>$prelude$$attr_goals_of/2</c>, in the top level's own
    /// wrapper goals: code the user did not write, cannot see, and did not ask to step
    /// through. Stepping has to stay in THEIR program, which means a port in code that is
    /// not theirs must not stop it.</para></summary>
    internal bool IsDebuggableAddress(int address)
        => FunctorAtAddress(address) is int fid && !_nonDebuggableFunctors.Contains(fid);

    /// <summary>ADR-035 — the functor whose compiled code contains <paramref name="address"/>,
    /// or null if the address names none. Shared by the CONTAINER check
    /// (<see cref="IsDebuggableAddress"/> — "is the code I am standing in the user's?") and the
    /// CALLEE check (<see cref="IsDebuggableCallee"/> — "should a call to here stop?").</summary>
    private int? FunctorAtAddress(int address)
    {
        if (_currentPredicatesByAddress is null || address < 0) return null;
        var entries = SortedPredicateEntries();
        int i = Array.BinarySearch(entries, address);
        if (i < 0) i = ~i - 1;
        if (i < 0) return null;
        return _currentPredicatesByAddress[entries[i]].FunctorId;
    }

    /// <summary>ADR-035 — should a CALL landing at <paramref name="address"/> stop? Unlike
    /// <see cref="IsDebuggableAddress"/> (the CONTAINER question) this also refuses a
    /// TRANSPARENT control construct: calling a <c>$disj_N</c> / <c>$call_*</c> helper is
    /// flow, not a goal, so the step passes straight through it to the real callee. The
    /// distinction matters because a <c>$disj_N</c> region CONTAINS user goals — it is a valid
    /// place to be standing (a user goal in a disjunction branch), just not a valid thing to
    /// stop ON when it is the callee.</summary>
    internal bool IsDebuggableCallee(int address)
        => FunctorAtAddress(address) is int fid && IsDebuggableFunctor(fid);

    internal bool IsDebuggableFunctor(int functorId)
        => !_nonDebuggableFunctors.Contains(functorId)
           && !IsTransparentControlFunctor(functorId);

    /// <summary>ADR-035 — is this functor a pure CONTROL CONSTRUCT (or its lowered
    /// plumbing), which the debugger renders TRANSPARENTLY: no stop port and no
    /// call-stack frame? A standard Prolog tracer (SWI, GProlog) never surfaces
    /// <c>,</c> / <c>;</c> / <c>-&gt;</c> / <c>*-&gt;</c> as goals — they are flow,
    /// not calls — so stepping goes straight from a clause to the real user goals.
    /// The <em>meta</em>-predicates the user invoked by name (<c>catch/3</c>,
    /// <c>once/1</c>, <c>ignore/1</c>, <c>\+</c>) are NOT control constructs: they
    /// stay visible (see <see cref="DebugConstructName"/>).
    ///
    /// <para>What is transparent: the bare operators (never normally reached as a
    /// predicate, but harmless to list); the synthesised disjunction / if-then-else
    /// helper (<c>$disj_N</c>, covering both <c>(A;B)</c> and <c>(C-&gt;T;E)</c>);
    /// and the prelude runtime meta-dispatch helpers that re-enter call dispatch
    /// for a variable goal (<c>$call</c>, <c>$call_conj</c>, <c>$call_disj</c>,
    /// <c>$call_arrow</c>). <c>$call_neg</c> is deliberately left visible — it is
    /// the runtime form of <c>\+</c>, a meta-goal.</para></summary>
    internal bool IsTransparentControlFunctor(int functorId)
    {
        var (atomId, arity) = FunctorTable.Lookup(functorId);
        var name = AtomTable.GetById(atomId)?.Name;
        return name is not null && IsTransparentControlName(DemangleLocalName(name), arity);
    }

    private static bool IsTransparentControlName(string demangled, int arity)
    {
        switch (demangled)
        {
            case "," or ";" or "->" or "*->" when arity == 2:
            case "$call" or "$call_conj" or "$call_disj" or "$call_arrow":
                return true;
        }
        return demangled.StartsWith("$disj_", StringComparison.Ordinal);
    }

    /// <summary>ADR-035 — is <paramref name="pc"/> inside the synthetic <c>__query__</c>
    /// wrapper the engine puts a top-level goal in? The wrapper is compiled user query code,
    /// so it is a DEBUGGABLE address — but it is not code the user wrote, and its call port to
    /// the entry goal maps to the end of the source (it has no line of its own). "Stop at the
    /// entry point" must skip it and land in the entry predicate itself.</summary>
    internal bool IsQueryWrapperAddress(int pc)
    {
        int i = IndexOfPredicateAt(pc);
        return i >= 0 && IsQueryEntry(i);
    }

    /// <summary>Translates each address in <paramref name="addresses"/>
    /// to the <c>Name/Arity</c> of the predicate that *contains* it
    /// (the largest predicate-entry address ≤ the given address) via
    /// the current query's link-time predicates-by-address map.
    /// Used by the runtime error path to assemble a Prolog-side stack
    /// trace (chunk 51).</summary>
    private IReadOnlyList<(string Name, int Arity)> ResolveAddressesToFunctors(
        IEnumerable<int> addresses)
    {
        var map = _currentPredicatesByAddress;
        if (map is null) return Array.Empty<(string, int)>();
        // Sort predicate-entry addresses once so we can binary-search
        // each query for its containing predicate.
        int[] sortedEntries = map.Keys.OrderBy(a => a).ToArray();
        var result = new List<(string, int)>();
        var seen = new HashSet<int>();
        foreach (int addr in addresses)
        {
            int idx = Array.BinarySearch(sortedEntries, addr);
            if (idx < 0) idx = ~idx - 1;
            if (idx < 0) continue;
            int entryAddr = sortedEntries[idx];
            if (!seen.Add(entryAddr)) continue;
            var pred = map[entryAddr];
            var (atomId, arity) = FunctorTable.Lookup(pred.FunctorId);
            string name = AtomTable.GetById(atomId)?.Name ?? "?";
            // Hide the synthetic __query__ predicate from user-visible
            // stack traces — it's an implementation detail of how the
            // engine wraps queries in a top-level clause.
            if (name == "__query__") continue;
            result.Add((name, arity));
        }
        return result;
    }

    /// <summary>Captures the current call stack as a list of
    /// <c>Name/Arity</c> entries — the innermost active predicate is
    /// at index 0, its caller at index 1, and so on. Exposed for
    /// debugging and used internally to populate
    /// <see cref="LastErrorStackTrace"/> when a runtime error escapes
    /// (chunks 51 + 53).</summary>
    private (IReadOnlyList<(string, int)> Plain, IReadOnlyList<StackFrame> WithPositions)
        CaptureStackTrace(Activation engine)
    {
        // Innermost address: the predicate the engine's PC is sitting
        // inside. Walk the env chain via the engine helper for the
        // ancestors.
        var addresses = new List<int>();
        addresses.Add(engine.P);
        foreach (int retAddr in engine.EnumerateCallReturnAddresses())
            addresses.Add(retAddr);
        return ResolveAddressesWithPositions(addresses);
    }

    /// <summary>Variant of <see cref="ResolveAddressesToFunctors"/>
    /// that also returns each frame's source position (chunk 53).
    /// Returned as a pair: the legacy <c>(name, arity)</c> tuples for
    /// <see cref="LastErrorStackTrace"/> back-compat, plus the
    /// position-enriched <see cref="StackFrame"/> list for the new
    /// chunk-53 surface.</summary>
    private (IReadOnlyList<(string Name, int Arity)> Plain,
             IReadOnlyList<StackFrame> WithPositions)
        ResolveAddressesWithPositions(IEnumerable<int> addresses)
    {
        var map = _currentPredicatesByAddress;
        if (map is null)
            return (Array.Empty<(string, int)>(), Array.Empty<StackFrame>());
        int[] sortedEntries = map.Keys.OrderBy(a => a).ToArray();
        var plain = new List<(string, int)>();
        var frames = new List<StackFrame>();
        var seen = new HashSet<int>();
        foreach (int addr in addresses)
        {
            int idx = Array.BinarySearch(sortedEntries, addr);
            if (idx < 0) idx = ~idx - 1;
            if (idx < 0) continue;
            int entryAddr = sortedEntries[idx];
            if (!seen.Add(entryAddr)) continue;
            var pred = map[entryAddr];
            var (atomId, arity) = FunctorTable.Lookup(pred.FunctorId);
            string name = AtomTable.GetById(atomId)?.Name ?? "?";
            if (name == "__query__") continue;
            plain.Add((name, arity));
            // Locate the most recent Meta(DbgInfo) opcode at or before the
            // PC inside this predicate's bytecode (chunk 55). Its payload
            // is the clause index; we use it to pick the precise per-clause
            // source position from ClauseSourcePositions, falling back to
            // the predicate's first-clause position when no Meta opcode is
            // present (single-clause predicates or older bundle blobs).
            SourcePosition framePos = FindClausePosition(pred, addr - entryAddr);
            frames.Add(new StackFrame(name, arity, framePos));
        }
        return (plain, frames);
    }

    /// <summary>Scans <paramref name="pred"/>'s bytecode from offset 0 up to
    /// (but not past) <paramref name="predLocalPc"/> for the most recent
    /// <see cref="Opcode.Meta"/> + <see cref="MetaSubOpcode.DbgInfo"/>
    /// opcode and returns the clause position its 4-byte payload indexes
    /// into. Returns the predicate's first-clause position when no Meta
    /// opcode is found (single-clause predicates, or bundle-rebuilt
    /// predicates whose <see cref="CompiledPredicate.ClauseSourcePositions"/>
    /// is empty).</summary>
    private static SourcePosition FindClausePosition(
        Shumway.Compiler.Wam.CompiledPredicate pred, int predLocalPc)
    {
        if (pred.ClauseSourcePositions.Count == 0) return pred.SourcePosition;
        byte[] code = pred.Bytecode;
        int pc = 0;
        int lastClauseIndex = -1;
        while (pc < code.Length && pc <= predLocalPc)
        {
            byte opByte = code[pc];
            if (opByte == (byte)Opcode.Meta
                && pc + 1 < code.Length
                && (MetaSubOpcode)code[pc + 1] == MetaSubOpcode.DbgInfo)
            {
                lastClauseIndex = BytecodeIO.ReadInt32(code, pc + 2);
                pc += 6;
                continue;
            }
            var info = OpcodeTable.Get(opByte);
            if (!info.IsDefined || info.Size == 0) break;
            pc += info.Size;
        }
        if (lastClauseIndex >= 0 && lastClauseIndex < pred.ClauseSourcePositions.Count)
            return pred.ClauseSourcePositions[lastClauseIndex];
        return pred.SourcePosition;
    }

    /// <summary>Drives the interpreter's run / backtrack loop and yields a
    /// <see cref="Solution"/> at each <see cref="InterpreterResult.Halted"/>
    /// outcome. A <see cref="PrologHaltException"/> ends the iteration
    /// gracefully (the user invoked <c>halt/0</c> or <c>halt/1</c>) — the
    /// embedding caller stops seeing further solutions rather than a .NET
    /// exception propagating out of their <c>foreach</c>.</summary>
    private static IEnumerable<Solution> RunIteration(
        PrologEngine host,
        Shumway.Core.ProgramView program,
        List<string> varNames,
        int[] varHeapIndices,
        Activation engine,
        BytecodeInterpreter interp)
    {
        // Heap-buffer pool: the finally runs exactly when the activation
        // dies — enumeration completed, disposed early (foreach break /
        // Query taking one solution), or unwound by an exception — and NOT
        // on suspension between yields. Solutions hold materialized AST
        // terms, never references into the surrendered buffer.
        try
        {
            InterpreterResult result;
            bool halted = false;
            // Choice-point level before the query runs. After a solution, if
            // the engine's B has fallen back to (or below) this, no
            // query-local choice point remains — the solution is the last
            // one. Lets the top-level skip the `;` prompt + trailing
            // `false` for a deterministic goal, matching other Prologs.
            int baseB = engine.B;
            try { result = host.RunCatching(interp, program, engine, () => interp.Run(program, 0)); }
            catch (PrologHaltException hex) { halted = true; host.LastHaltExitCode = hex.ExitCode; result = InterpreterResult.Failed; }
            catch (ShumwayPrologException) { { var st = host.CaptureStackTrace(engine); host.LastErrorStackTrace = st.Plain; host.LastErrorStackTraceWithPositions = st.WithPositions; throw; } }
            catch (PrologRuntimeException) { { var st = host.CaptureStackTrace(engine); host.LastErrorStackTrace = st.Plain; host.LastErrorStackTraceWithPositions = st.WithPositions; throw; } }

            while (!halted && result == InterpreterResult.Halted)
            {
                bool isLast = engine.B <= baseB;
                // ADR-035 — the machine is about to hand the answer back and stand still.
                // A step still in flight can never be satisfied from here (no port is
                // coming until somebody asks for another solution), and a debugger left
                // waiting for it thinks the program is running.
                host.DebugSession?.OnLeaveProlog(engine);
                yield return BuildSolution(varNames, varHeapIndices, engine, isLast, host);
                // A known-last solution: don't backtrack — there's nothing
                // to find and re-running would just confirm failure.
                if (isLast) break;
                try { result = host.RunCatching(interp, program, engine, () => interp.Backtrack(program)); }
                catch (PrologHaltException hex) { halted = true; host.LastHaltExitCode = hex.ExitCode; break; }
                catch (ShumwayPrologException) { { var st = host.CaptureStackTrace(engine); host.LastErrorStackTrace = st.Plain; host.LastErrorStackTraceWithPositions = st.WithPositions; throw; } }
                catch (PrologRuntimeException) { { var st = host.CaptureStackTrace(engine); host.LastErrorStackTrace = st.Plain; host.LastErrorStackTraceWithPositions = st.WithPositions; throw; } }
            }
        }
        finally
        {
            // The activation is dead — the query failed, ran out of solutions, or the
            // caller stopped asking. Same as a yield, and more final.
            host.DebugSession?.OnLeaveProlog(engine);
            host.ReturnHeapBuffer(engine);
        }
    }

    /// <summary>Runs an interpreter step, intercepting <c>throw/1</c> for
    /// in-engine <c>catch/3</c> (chunk 85). When the thrown ball unifies
    /// with the catcher of an active catch frame, the engine rolls back to
    /// that frame and resumes at its recovery goal; the loop repeats if
    /// recovery (or the continuation) throws again. A ball that no frame
    /// catches propagates unchanged. A Core <see cref="PrologRuntimeException"/>
    /// is funnelled through the same path as its ISO <c>error/2</c> term.</summary>
    private InterpreterResult RunCatching(
        BytecodeInterpreter interp, Shumway.Core.ProgramView program, Activation engine,
        Func<InterpreterResult> action)
    {
        Func<InterpreterResult> step = action;
        while (true)
        {
            try
            {
                return step();
            }
            catch (ShumwayPrologException ex)
            {
                int addr = TryCatch(engine, ex.Term, out _);
                if (addr < 0) throw;
                step = () => interp.Run(program, addr);
            }
            catch (PrologRuntimeException ex)
            {
                Term ball = MetaBuiltins.TranslateRuntimeError(ex);
                int addr = TryCatch(engine, ball, out bool insideCatch);
                if (addr >= 0)
                    step = () => interp.Run(program, addr);
                else if (insideCatch)
                    // It passed through a catch (just no catcher matched),
                    // so it propagates as the Prolog-visible error/2 term.
                    throw new ShumwayPrologException(ball);
                else
                    // No catch at all — keep the raw Core exception.
                    throw;
            }
        }
    }

    /// <summary>Walks the catch-frame stack from the innermost frame out,
    /// trial-unifying <paramref name="ballTerm"/> with each active frame's
    /// catcher. On the first match it rolls the machine back to that frame,
    /// binds the catcher to the ball for real, loads the recovery goal's
    /// arguments into the registers, and returns the recovery predicate's
    /// code address. Returns -1 when no frame catches the ball;
    /// <paramref name="hadActiveFrame"/> then reports whether any active
    /// catch frame was seen at all (it was just a catcher mismatch) — used
    /// to decide whether an uncaught runtime error keeps its raw form.</summary>
    private static int TryCatch(Activation engine, Term ballTerm, out bool hadActiveFrame)
    {
        hadActiveFrame = false;
        for (int i = engine.CatchFrameCount - 1; i >= 0; i--)
        {
            CatchFrame frame = engine.GetCatchFrame(i);
            if (!frame.Active) continue;
            hadActiveFrame = true;

            // Speculatively unify the ball with the catcher, then undo —
            // testing the match must not disturb the machine.
            int savedHeapTop = engine.HeapTop;
            int savedBindingTrail = engine.BindingTrailTop;
            int savedExtraTrail = engine.ExtraTrailTop;
            int savedHb = engine.Hb;
            engine.SetHb(engine.HeapTop);
            Cell trialBall = Materializer.MaterializeAsCell(engine, ballTerm);
            bool matched = engine.UnifyHeapWithCell(frame.CatcherHeapIdx, trialBall);
            engine.UnwindTrails(savedBindingTrail, savedExtraTrail);
            engine.SetHeapTop(savedHeapTop);
            engine.SetHb(savedHb);
            if (!matched) continue;

            // Commit: roll back everything the guarded goal did, then bind
            // the catcher to the ball for keeps and prime the recovery call.
            engine.UnwindToCatchFrame(i);
            Cell ball = Materializer.MaterializeAsCell(engine, ballTerm);
            engine.UnifyHeapWithCell(frame.CatcherHeapIdx, ball);
            return SetupRecoveryCall(engine, frame.RecoveryHeapIdx);
        }
        return -1;
    }

    /// <summary>Decodes the recovery goal cell — a <c>'$catchrec_N'(Vars)</c>
    /// helper call — into argument registers and returns its code address,
    /// so the interpreter can be re-entered to run the recovery.</summary>
    private static int SetupRecoveryCall(Activation engine, int recoveryHeapIdx)
    {
        Cell goal = engine.GetHeap(recoveryHeapIdx);
        if (goal.Tag == Tag.Ref)
            goal = engine.GetHeap(engine.Deref(goal.AsHeapIndex));

        int functorId;
        int argBase;
        int arity;
        if (goal.Tag == Tag.Atom)
        {
            functorId = FunctorTable.Intern(goal.AsAtomId, 0);
            arity = 0;
            argBase = -1;
        }
        else if (goal.Tag == Tag.Str)
        {
            int functorIdx = goal.AsHeapIndex;
            functorId = engine.GetHeap(functorIdx).AsFunctorId;
            (_, arity) = FunctorTable.Lookup(functorId);
            argBase = functorIdx + 1;
        }
        else
        {
            // Chunk 131e: ISO §8.15.3.3 — a non-callable Recovery goal
            // is type_error(callable, Recovery); an unbound one is
            // instantiation_error.
            if (goal.Tag == Tag.Ref)
                throw new Shumway.Core.PrologRuntimeException("instantiation_error");
            throw new Shumway.Core.PrologRuntimeException("type_error", "callable");
        }

        for (int i = 0; i < arity; i++)
            engine.SetRegister(i, engine.GetHeap(argBase + i));

        var addresses = engine.CurrentFunctorAddresses;
        if (addresses is not null && addresses.TryGetValue(functorId, out int address))
            return address;
        throw new InvalidOperationException(
            "catch/3 recovery helper predicate has no compiled address.");
    }

    /// <summary>Loads the CLP(FD) constraint library (chunk 89) into this
    /// engine, making the finite-domain constraints — <c>#=</c>, <c>#\=</c>,
    /// <c>#&lt;</c>, <c>#&gt;</c>, <c>#=&lt;</c>, <c>#&gt;=</c>, <c>in</c>,
    /// <c>ins</c> — and their operators available to subsequently consulted
    /// source and queries. CLP(FD) is opt-in: an engine that never calls
    /// this carries none of the library's weight.</summary>
    public void UseClpfd()
    {
        ConsultString(Clpfd.Source);
        MarkModuleNonDebuggable(Clpfd.ModuleName);   // ADR-035 — a library, not the user's code
    }

    /// <summary>Loads the CLP(R) constraint library (chunk 99) into this
    /// engine, making linear-equality constraints over the reals available
    /// through the <c>{Constraint}</c> wrapper. CLP(R) is opt-in: an engine
    /// that never calls this carries none of the library's weight.
    ///
    /// <para>CLP(R) and CLP(FD) both define a <c>verify_attributes/4</c>
    /// hook as a public predicate, so for now only one of the two may be
    /// loaded into a given engine.</para></summary>
    public void UseClpr()
    {
        ConsultString(Clpr.Source);
        MarkModuleNonDebuggable(Clpr.ModuleName);   // ADR-035 — a library, not the user's code
    }

    // Compatibility libraries loaded on demand by use_module(library(Name)),
    // tracked so a repeated import (or a program that imports the same library
    // as one of its dependencies) does not re-consult and trip the
    // public-predicate uniqueness check.
    private readonly HashSet<string> _loadedCompatLibraries = new();

    /// <summary>Loads a built-in Scryer/Trealla compatibility library by name
    /// (see <see cref="CompatLibraries"/>), idempotently. Returns <c>true</c>
    /// if <paramref name="name"/> is a known compatibility library (whether it
    /// carries Prolog source or is a prelude-covered no-op), <c>false</c> for
    /// an unknown library name.</summary>
    internal bool UseCompatLibrary(string name)
    {
        if (!CompatLibraries.TryGet(name, out string source))
            return false;
        // recordInHistory:false — the importing program's own source (which
        // carries the use_module directive) is what SaveState replays; the
        // directive re-loads the library on restore, so recording the library
        // body too would double-consult it (and trip public uniqueness).
        if (_loadedCompatLibraries.Add(name) && source.Length > 0)
            ConsultStringInner(source, recordInHistory: false);
        return true;
    }

    /// <summary>Executes a <c>use_module/1</c> directive body at consult time.
    /// <c>library(Name)</c> loads a constraint or compatibility library;
    /// a plain atom names a file to consult. An unknown library is reported as
    /// a warning and skipped (the program may only need predicates Shumway
    /// already provides, or it will surface a clearer per-predicate
    /// existence_error later) rather than aborting the whole consult.</summary>
    private void ExecuteUseModuleDirective(Term spec)
    {
        if (spec is CompoundTerm { Functor: "library", Args: [AtomTerm lib] })
        {
            switch (lib.Name)
            {
                case "clpfd": UseClpfd(); return;
                case "clpr":  UseClpr();  return;
                default:
                    if (UseCompatLibrary(lib.Name)) return;
                    Console.Error.WriteLine(
                        $"warning: unknown library '{lib.Name}' in use_module/1 — ignored");
                    return;
            }
        }
        if (spec is AtomTerm fileAtom)
        {
            // Already-loaded module (e.g. consulted directly on the command
            // line, or imported earlier) — importing it again is a no-op.
            if (_modules.ContainsKey(fileAtom.Name)) return;
            string path = fileAtom.Name;
            if (_consultBaseDir is not null && !System.IO.Path.IsPathRooted(path))
                path = System.IO.Path.Combine(_consultBaseDir, path);
            if (!System.IO.Path.HasExtension(path) && System.IO.File.Exists(path + ".pl"))
                path += ".pl";
            // Idempotent: a file already consulted (on the command line or via
            // an earlier import) is not re-consulted — re-loading it would
            // double its clauses.
            try
            {
                if (System.IO.File.Exists(path)
                    && _consultedPaths.Contains(System.IO.Path.GetFullPath(path)))
                    return;
            }
            catch { /* fall through to the normal load */ }
            if (!System.IO.File.Exists(path))
            {
                Console.Error.WriteLine(
                    $"warning: use_module/1 target '{fileAtom.Name}' not found — ignored");
                return;
            }
            // A failing import must not abort the importing consult — warn and
            // continue; any predicate genuinely needed surfaces a clearer
            // existence_error at the call site.
            try { ConsultFile(path); }
            catch (System.Exception ex)
            {
                Console.Error.WriteLine(
                    $"warning: use_module('{fileAtom.Name}') failed: {ex.Message}");
            }
        }
    }

    // ADR-022 — the interop class whose public static methods back the C
    // functions called from embedded native `{...}` blocks. Defaults to
    // auto-discovering `Shumway.Native.Interop` across the loaded assemblies;
    // UseNativeInterop overrides it with an explicit class.
    private Dictionary<string, System.Reflection.MethodInfo>? _nativeInterop;

    /// <summary>Binds the class whose <c>public static</c> methods implement the
    /// C functions called from embedded native blocks (ADR-022). Call before
    /// consulting Arity sources that use <c>{...}</c> blocks. Without an explicit
    /// call the engine auto-discovers a class named <c>Shumway.Native.Interop</c>
    /// in the loaded assemblies.</summary>
    public void UseNativeInterop(Type interopClass)
    {
        ArgumentNullException.ThrowIfNull(interopClass);
        _nativeInterop = interopClass
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .GroupBy(m => m.Name)
            .ToDictionary(g => g.Key, g => g.First());   // C has no overloading
    }

    /// <summary>Resolves a native-block C function name to its implementing
    /// method, auto-discovering <c>Shumway.Native.Interop</c> on first use if
    /// <see cref="UseNativeInterop"/> was never called. Returns null when no such
    /// method exists.</summary>
    internal System.Reflection.MethodInfo? ResolveNativeInterop(string name)
    {
        if (_nativeInterop is null)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? t = null;
                try { t = asm.GetType("Shumway.Native.Interop"); } catch { }
                if (t is not null) { UseNativeInterop(t); break; }
            }
            _nativeInterop ??= new Dictionary<string, System.Reflection.MethodInfo>();
        }
        return _nativeInterop.TryGetValue(name, out var m) ? m : null;
    }

    // ADR-022 item 1 — per-engine table of embedded native blocks, keyed by a
    // stable name. The `'$native_goal'(Text)` capture is rewritten to
    // `'$native_run'('$nb$…', V1..Vk)`; the `$native_run` builtin looks the block
    // up here by name (portable cross-process — the bytecode references the name,
    // not a synthesized-builtin id) and runs it. Populated at consult (in-process)
    // and at bundle load (from the serialized table).
    private readonly Dictionary<string, NativeBlockEntry> _nativeBlocks = new();

    private int _nativeBlockConsultSeq;
    // Phase 33 — the engine's monotonic synthesized-helper sequence: every
    // consult/assert transform on this engine draws unique helper ids, so a
    // second consult's `$disj_N` can never collide with the first's in the same
    // module. Per-engine (not global) so the atom space stays bounded across
    // engines/processes; the query stub uses the reserved `$q` prefix instead.
    private int _metaHelperSeq;
    private int NextMetaHelperId() => ++_metaHelperSeq;


    /// <summary>ADR-025 — enables the inline if-then-else lowering: an eligible
    /// plain-goal <c>(C -&gt; T ; E)</c> / <c>(A ; B)</c> compiles INSIDE the host
    /// clause (get_level; try_me_else; cut; jump) instead of a synthesized
    /// 2-clause helper reached by a Call. STATIC consult paths only — the
    /// runtime assert path always uses the helper form. Default OFF (stage (c)
    /// of the ADR-025 rollout): a predicate with an inline ITE is not yet
    /// Tier-1-promotable (the IL compiler rejects the shape gracefully and it
    /// stays on Tier-0), so flipping this on trades Tier-1 eligibility for the
    /// Tier-0 win until the ADR's stage (b) lands. Set before consulting.</summary>
    public bool EnableInlineIte { get; set; }

    // Phase 33 D4 → D1 — the per-engine NATIVE ARENA: a chunked unmanaged bump
    // allocator with mark/restore, serving every call-scoped native buffer a
    // `:- native` P/Invoke needs — out-scalar/out-string cells (D4), and now
    // (D1) the whole t_reftype graph: nodes, pars arrays and char* buffers.
    // The D1 measurement showed AllocHGlobal/FreeHGlobal was 96% of the
    // reftype marshal cost on a 50-element list (mat+free 92.9 of 96.7 µs
    // roundtrip); with the arena, Materialize bump-allocates and the entire
    // release is a mark restore — no graph walk, no per-node free.
    //
    // Safety contract (same as the recorded-allocations mode this replaces):
    // release frees exactly the memory WE allocated for the call — a native
    // function that swapped a cstr/pars with its own allocator leaves our
    // (now unlinked) block to die with the mark and its foreign pointer is
    // never touched. Nested native calls compose via mark/restore; the
    // engine is single-threaded. Chunks are engine-lifetime (high-watermark
    // retention, like the WAM heap); an oversize request gets its own chunk.
    private readonly System.Collections.Generic.List<IntPtr> _nativeArenaChunks = new();
    private readonly System.Collections.Generic.List<int> _nativeArenaSizes = new();
    private int _nativeArenaChunk;
    private int _nativeArenaOffset;
    private const int NativeArenaChunkSize = 64 * 1024;

    internal long NativeScratchMark => ((long)_nativeArenaChunk << 32) | (uint)_nativeArenaOffset;

    internal void NativeScratchRelease(long mark)
    {
        _nativeArenaChunk = (int)(mark >> 32);
        _nativeArenaOffset = (int)(uint)mark;
    }

    /// <summary>Bump-allocates <paramref name="bytes"/> (8-byte aligned) of
    /// call-scoped native memory. Released wholesale by
    /// <see cref="NativeScratchRelease"/> with the mark taken at call entry.</summary>
    internal IntPtr NativeArenaAlloc(int bytes)
    {
        int aligned = (bytes + 7) & ~7;
        while (true)
        {
            if (_nativeArenaChunk == _nativeArenaChunks.Count)
            {
                int size = System.Math.Max(NativeArenaChunkSize, aligned);
                _nativeArenaChunks.Add(System.Runtime.InteropServices.Marshal.AllocHGlobal(size));
                _nativeArenaSizes.Add(size);
            }
            if (_nativeArenaOffset + aligned <= _nativeArenaSizes[_nativeArenaChunk])
            {
                IntPtr p = _nativeArenaChunks[_nativeArenaChunk] + _nativeArenaOffset;
                _nativeArenaOffset += aligned;
                return p;
            }
            _nativeArenaChunk++;
            _nativeArenaOffset = 0;
        }
    }

    // Phase 33 A4 — the per-dispatch block lookup keyed by ATOM ID: '$native_run'
    // reads the block-name register as a raw atom cell (no Term materialization)
    // and probes this int-keyed cache instead of hashing the name string per call.
    // Populated lazily; cleared whenever a block is (re)registered.
    private readonly Dictionary<int, NativeBlockEntry> _nativeBlocksByAtomId = new();

    internal NativeBlockEntry? NativeBlockByAtomId(int atomId)
    {
        if (_nativeBlocksByAtomId.TryGetValue(atomId, out var hit)) return hit;
        var entry = NativeBlock(Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "");
        if (entry is not null) _nativeBlocksByAtomId[atomId] = entry;
        return entry;
    }

    internal void AddNativeBlock(string name,
        Shumway.Compiler.NativeC.NativeVar[] vars, Shumway.Compiler.NativeC.CStmt[] stmts,
        Shumway.Compiler.NativeC.NativeScalarGlobal[] scalarGlobals)
    {
        _nativeBlocks[name] = new NativeBlockEntry(vars, stmts, scalarGlobals);
        _nativeBlocksByAtomId.Clear();   // re-registration invalidates the id cache
    }

    // ADR-024 — the text encoding for char* marshalling to/from native t_reftype
    // structs (atom/string content + functor names), used by the materializer tier.
    // Default UTF-8 (the common native-C convention); set to Latin1 / a codepage to
    // match a particular native library. Must be a byte-oriented encoding.
    private System.Text.Encoding _nativeTextEncoding = NativeReftype.DefaultEncoding;

    /// <summary>The <c>char*</c> text encoding for the native materializer tier
    /// (ADR-024). Defaults to UTF-8; set it to match the native library you call.
    /// Must be byte-oriented (UTF-8 / ASCII / Latin1 / a codepage): native strings
    /// are NUL-terminated byte sequences, so an encoding that emits interior zero
    /// bytes for ordinary characters (UTF-16/32) would silently truncate every
    /// marshalled string — rejected here rather than corrupting at the call.</summary>
    public System.Text.Encoding NativeTextEncoding
    {
        get => _nativeTextEncoding;
        set
        {
            if (value is null) throw new System.ArgumentNullException(nameof(value));
            // Byte-oriented check: a plain ASCII character must encode to exactly
            // one byte with no embedded NUL. UTF-16 gives "A\0" (2 bytes), UTF-32
            // four — both would break single-NUL-terminated char* marshalling.
            byte[] probe = value.GetBytes("A");
            if (probe.Length != 1 || probe[0] == 0)
                throw new System.ArgumentException(
                    $"NativeTextEncoding must be a byte-oriented encoding (UTF-8, ASCII, "
                    + $"Latin1, or a single/multi-byte codepage); '{value.WebName}' encodes "
                    + "ASCII characters to multiple bytes and cannot represent NUL-terminated "
                    + "native char* strings.", nameof(value));
            _nativeTextEncoding = value;
        }
    }

    // ADR-024 — functor ids declared `:- native fn/N`: a native function using the
    // materializer protocol (P/Invoke or a managed Reftype snapshot) rather than a
    // plain .NET interop method.
    private readonly HashSet<int> _nativeFunctions = new();
    private readonly HashSet<string> _nativeFunctionNames = new();

    /// <summary>True if <paramref name="name"/>/<paramref name="arity"/> was declared
    /// <c>:- native</c>.</summary>
    internal bool IsNativeFunction(string name, int arity)
        => _nativeFunctions.Count > 0
           && _nativeFunctions.Contains(FunctorTable.Intern(AtomTable.Intern(name).Id, arity));

    /// <summary>True if any <c>:- native</c> declaration uses <paramref name="name"/>
    /// (at any arity) — so the consult-time block validation does not require it to
    /// be a C# interop method (a native function resolves at run time).</summary>
    internal bool IsNativeFunctionName(string name) => _nativeFunctionNames.Contains(name);

    // ADR-024 — registered native libraries (handles) for `:- native` functions
    // resolved by P/Invoke; the `:- c` prototypes (signature) and typedefs collected
    // at consult; and the per-function resolution cache so a call resolves once
    // (C# interop method vs native export) and reuses the decision thereafter.
    private readonly System.Collections.Generic.List<IntPtr> _nativeLibraries = new();
    private System.Collections.Generic.Dictionary<string, Shumway.Compiler.NativeC.CPrototype>? _nativePrototypes;
    private System.Collections.Generic.Dictionary<string, Shumway.Compiler.NativeC.CType>? _nativeTypedefs;
    private readonly System.Collections.Generic.Dictionary<int, NativeResolution> _nativeCallCache = new();

    /// <summary>The consulted <c>:- c</c> typedef table, for the native-block code
    /// generators — a block-local declared via a typedef (`s: pchar`) must type as
    /// the resolved C type (char* → string model), matching the interpreter.</summary>
    internal System.Collections.Generic.IReadOnlyDictionary<string, Shumway.Compiler.NativeC.CType>? NativeTypedefsView
        => _nativeTypedefs;

    // ADR-024 — native libraries are loaded ONCE PER PATH for the process lifetime,
    // not once per engine. The OS maps a module once (LoadLibrary/dlopen refcounts),
    // so a per-engine Load would leak one refcount per engine under churn; this
    // process-global table makes the shared mapping explicit and deduplicates the
    // load. The handle is never freed — the mapping lives until the process exits.
    // (A global table guarded by a lock, like the atom / functor tables.)
    private static readonly System.Collections.Generic.Dictionary<string, IntPtr> _loadedNativeLibraries = new();
    private static readonly object _loadedNativeLibrariesLock = new();
    /// <summary>Test/diagnostic: the number of real <c>NativeLibrary.Load</c> calls
    /// (a distinct path is loaded once for the whole process).</summary>
    internal static int NativeLibraryLoadCount;

    /// <summary>ADR-024 — registers a native library (a C DLL/.so/.dylib) whose
    /// exported functions back <c>:- native</c> declarations resolved by P/Invoke.
    /// Call before querying; later registrations invalidate the resolution cache.
    /// The library is loaded once per path for the whole process and shared across
    /// engines (see the <c>_loadedNativeLibraries</c> note).</summary>
    public void UseNativeLibrary(string path)
    {
        System.ArgumentNullException.ThrowIfNull(path);
        // Key by full path when it names an existing file (the --native-dll /
        // LoadBundle case); otherwise by the raw string (an OS-searched bare name).
        string key = System.IO.File.Exists(path) ? System.IO.Path.GetFullPath(path) : path;
        IntPtr h;
        lock (_loadedNativeLibrariesLock)
        {
            if (!_loadedNativeLibraries.TryGetValue(key, out h))
            {
                h = System.Runtime.InteropServices.NativeLibrary.Load(key);
                _loadedNativeLibraries[key] = h;
                System.Threading.Interlocked.Increment(ref NativeLibraryLoadCount);
            }
        }
        if (!_nativeLibraries.Contains(h)) _nativeLibraries.Add(h);
        // ADR-024 — if the library provides the reftype allocator API
        // (newreftype/freepar/…), use it so a native function that builds sub-nodes
        // and the materializer share one heap (freepar can release the mixed graph).
        _nativeAllocator ??= NativeReftypeAllocator.TryResolve(h);
        _nativeCallCache.Clear();
    }

    private NativeReftypeAllocator? _nativeAllocator;

    /// <summary>The native library's reftype allocator, if one is registered — used
    /// to materialize/free reftype graphs through the library's own heap (ADR-024).</summary>
    internal NativeReftypeAllocator? NativeAllocator => _nativeAllocator;

    /// <summary>Collects the <c>:- c</c> prototypes + typedefs so a P/Invoke
    /// <c>:- native</c> call can derive its marshalling signature. Called at consult
    /// once the C symbol table is parsed.</summary>
    internal void RegisterNativePrototypes(System.Collections.Generic.IReadOnlyList<Shumway.Compiler.NativeC.CDecl> cDecls)
    {
        foreach (var d in cDecls)
            switch (d)
            {
                case Shumway.Compiler.NativeC.CPrototype p:
                    (_nativePrototypes ??= new())[p.Name] = p;
                    break;
                case Shumway.Compiler.NativeC.CTypedef td:
                    (_nativeTypedefs ??= new())[td.Alias] = td.Underlying;
                    break;
            }
        _nativeCallCache.Clear();
    }

    /// <summary>The cached resolution of a <c>:- native</c> call — a C# interop
    /// method (managed snapshot) or a native export (P/Invoke). Resolved once per
    /// functor and reused.</summary>
    internal sealed class NativeResolution
    {
        public System.Reflection.MethodInfo? CsMethod;     // non-null → managed path
        public IntPtr NativeFn;                            // P/Invoke target
        public NativeCall.Signature? Signature;            // P/Invoke marshalling
    }

    internal NativeResolution ResolveNativeCall(string name, int arity)
    {
        int fid = FunctorTable.Intern(AtomTable.Intern(name).Id, arity);
        if (_nativeCallCache.TryGetValue(fid, out var cached)) return cached;
        var r = BuildNativeResolution(name, arity);
        _nativeCallCache[fid] = r;
        return r;
    }

    private NativeResolution BuildNativeResolution(string name, int arity)
    {
        // 1. A C# interop method → managed (snapshot) path.
        var m = ResolveNativeInterop(name);
        if (m is not null) return new NativeResolution { CsMethod = m };

        // 2. A native export from a registered library → P/Invoke path.
        IntPtr fn = IntPtr.Zero;
        foreach (var lib in _nativeLibraries)
            if (System.Runtime.InteropServices.NativeLibrary.TryGetExport(lib, name, out fn))
                break;
        if (fn != IntPtr.Zero)
        {
            if (_nativePrototypes is null || !_nativePrototypes.TryGetValue(name, out var proto))
                throw new System.InvalidOperationException(
                    $":- native '{name}': no ':- c' prototype found to derive its native signature.");
            var sig = NativeCall.FromPrototype(proto,
                _nativeTypedefs ?? new System.Collections.Generic.Dictionary<string, Shumway.Compiler.NativeC.CType>());
            return new NativeResolution { NativeFn = fn, Signature = sig };
        }
        throw new System.InvalidOperationException(
            $":- native '{name}/{arity}': not a public static method of the interop class and not exported "
            + "by any registered native library (UseNativeLibrary).");
    }

    // ADR-022 — per-engine persistent storage for SCALAR `:- c` globals (a plain
    // int/long/float/double global, as opposed to a char*/reftype holder). Like
    // _reftypeSlots these persist across calls/queries — Arity static-storage
    // semantics. A native block seeds its value on entry and writes it through on
    // every assignment. Plain CLR values, heap-independent, so they survive query
    // teardown.
    private readonly Dictionary<string, long> _nativeGlobalInt = new();
    private readonly Dictionary<string, double> _nativeGlobalFloat = new();

    /// <summary>Reads an integer scalar `:- c` native global's persistent value
    /// (0 if never written). Public so runtime Expression / Tier-1 IL native-block
    /// codegen can emit a direct call.</summary>
    public long GetNativeGlobalInt(string name)
        => _nativeGlobalInt.TryGetValue(name, out var v) ? v : 0L;
    /// <summary>Writes an integer scalar `:- c` native global's persistent value.</summary>
    public void SetNativeGlobalInt(string name, long v) => _nativeGlobalInt[name] = v;
    /// <summary>Reads a float scalar `:- c` native global's persistent value.</summary>
    public double GetNativeGlobalFloat(string name)
        => _nativeGlobalFloat.TryGetValue(name, out var v) ? v : 0.0;
    /// <summary>Writes a float scalar `:- c` native global's persistent value.</summary>
    public void SetNativeGlobalFloat(string name, double v) => _nativeGlobalFloat[name] = v;

    internal NativeBlockEntry? NativeBlock(string name)
        => _nativeBlocks.TryGetValue(name, out var b) ? b : null;

    // ADR-024 — per-engine term slots for `reftype` globals declared in `:- c`
    // regions (par1ref… and the program's own). Persist across queries (an Arity
    // global buffer is reused between calls; fill_par overwrites it). The slot
    // holds an AST term, self-contained and heap-independent, so it survives query
    // teardown. `&name` / `name` in a native block resolves to the slot.
    private readonly Dictionary<string, TermSlot> _reftypeSlots = new();

    /// <summary>The term slot for a `reftype` global, or null if the name isn't a
    /// registered reftype global.</summary>
    internal TermSlot? ReftypeSlot(string name)
        => _reftypeSlots.TryGetValue(name, out var s) ? s : null;

    /// <summary>The term slot for a `reftype` global, created on first reference.
    /// Used by the native-block runner when a block takes the address of a reftype
    /// global (<c>&amp;name</c>) or passes one to an interop function expecting a
    /// <see cref="TermSlot"/> — so a slot exists even when the `:- c` declarations
    /// didn't travel (a source-stripped bundle: the declarations are compile-time;
    /// the block runs in the interpreter and creates its slots here).</summary>
    internal TermSlot GetOrCreateReftypeSlot(string name)
    {
        if (!_reftypeSlots.TryGetValue(name, out var s))
            _reftypeSlots[name] = s = new TermSlot();
        return s;
    }

    private void RegisterReftypeGlobals(IReadOnlyList<Shumway.Compiler.NativeC.CDecl> decls)
    {
        foreach (var d in decls)
            if (d is Shumway.Compiler.NativeC.CGlobalVar g
                && g.Type.Name is "reftype" or "preftype" or "t_reftype"
                && !_reftypeSlots.ContainsKey(g.Name))
                _reftypeSlots[g.Name] = new TermSlot();
    }

    private Shumway.Compiler.NativeC.NativeInlineContext? _nativeInlineContext;

    /// <summary>ADR-022 item 2 — the context the IL compiler uses to inline this
    /// engine's native blocks (build-time IL). Null until a block is registered;
    /// then built once (the marshalling handles are constant; the block lookup and
    /// interop resolver close over this engine, so they track later state).</summary>
    internal Shumway.Compiler.NativeC.NativeInlineContext? GetNativeInlineContext()
    {
        if (_nativeBlocks.Count == 0) return null;
        return _nativeInlineContext ??= BuildNativeInlineContext();
    }

    private Shumway.Compiler.NativeC.NativeInlineContext BuildNativeInlineContext()
    {
        var fromTerm = typeof(PrologEngine).GetMethod(nameof(FromTerm))!;
        var toTerm = typeof(PrologEngine).GetMethod(nameof(ToTerm))!;
        return new Shumway.Compiler.NativeC.NativeInlineContext
        {
            BlockProvider = n =>
            {
                var e = NativeBlock(n);
                return e is null ? null
                    : new Shumway.Compiler.NativeC.NativeBlockBody(e.Vars, e.Stmts, e.ScalarGlobals);
            },
            InteropResolver = ResolveNativeInterop,
            TypedefsProvider = () => _nativeTypedefs,
            ReadRegisterAsTerm = typeof(RegisterMarshalling)
                .GetMethod(nameof(RegisterMarshalling.ReadRegisterAsTerm))!,
            UnifyRegisterWithTerm = typeof(RegisterMarshalling)
                .GetMethod(nameof(RegisterMarshalling.UnifyRegisterWithTerm))!,
            HostGetter = typeof(Activation).GetProperty(nameof(Activation.Host))!.GetGetMethod()!,
            HostType = typeof(PrologEngine),
            FromTermLong = fromTerm.MakeGenericMethod(typeof(long)),
            FromTermDouble = fromTerm.MakeGenericMethod(typeof(double)),
            FromTermString = fromTerm.MakeGenericMethod(typeof(string)),
            ToTermLong = toTerm.MakeGenericMethod(typeof(long)),
            ToTermDouble = toTerm.MakeGenericMethod(typeof(double)),
            AtomTermCtor = typeof(Shumway.Compiler.Ast.AtomTerm)
                .GetConstructor(new[] { typeof(string) })!,
            // ADR-022 persistent scalar-global accessors.
            GetNativeGlobalInt = typeof(PrologEngine).GetMethod(nameof(GetNativeGlobalInt))!,
            SetNativeGlobalInt = typeof(PrologEngine).GetMethod(nameof(SetNativeGlobalInt))!,
            GetNativeGlobalFloat = typeof(PrologEngine).GetMethod(nameof(GetNativeGlobalFloat))!,
            SetNativeGlobalFloat = typeof(PrologEngine).GetMethod(nameof(SetNativeGlobalFloat))!,
            // ADR-024 reftype tier handles.
            TermSlotType = typeof(TermSlot),
            GetOrCreateReftypeSlot = typeof(PrologEngine).GetMethod(
                nameof(GetOrCreateReftypeSlot),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!,
            MakeForeign = typeof(Activation).GetMethod(nameof(Activation.MakeForeign))!,
            UnifyRegisterWithCell = typeof(Activation).GetMethod(nameof(Activation.UnifyRegisterWithCell))!,
            ReadReftypeSlot = typeof(NativeBlockCompiler).GetMethod(
                nameof(NativeBlockCompiler.ReadReftypeSlot))!,
            SlotSetValue = typeof(TermSlot).GetMethod(nameof(TermSlot.SetValue))!,
            SlotMaterialize = typeof(TermSlot).GetMethod(nameof(TermSlot.Materialize))!,
        };
    }

    /// <summary>Save-state chunk 264 — writes a snapshot of this engine's
    /// state to <paramref name="path"/> as a V6 .shum bundle.
    ///
    /// <para>Full mode (<paramref name="dynamicOnly"/> = false, the
    /// default) captures every source previously passed to
    /// <see cref="ConsultString"/> (in order, excluding the auto-
    /// loaded prelude) plus every currently asserted dynamic clause.
    /// <see cref="RestoreState"/> on a fresh engine reconstitutes
    /// equivalent state by replaying the consults and re-asserting
    /// the dynamic clauses.</para>
    ///
    /// <para>Dynamic-only mode (<paramref name="dynamicOnly"/> = true)
    /// skips the consult history and captures only the dynamic
    /// clauses — useful for persisting an application's facts
    /// without re-shipping the code that operates on them. Loaded
    /// with <see cref="RestoreState"/>, the clauses merge into the
    /// engine's current state via <c>assertz</c>, without resetting
    /// anything.</para></summary>
    public void SaveState(string path, bool dynamicOnly = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        BundleWriter.WriteToFile(BuildSnapshotBundle(dynamicOnly), path);
    }

    /// <summary>Save-state chunk 264 — in-memory variant returning the
    /// serialized bundle bytes. Used by tests; the file-path overload
    /// is what user code typically calls.</summary>
    public byte[] SaveStateToBytes(bool dynamicOnly = false)
        => BundleWriter.ToBytes(BuildSnapshotBundle(dynamicOnly));

    private Bundle BuildSnapshotBundle(bool dynamicOnly)
    {
        var consultHistory = dynamicOnly
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : _consultHistory.ToArray();
        var dynamicSeeds = new List<ShmoDynamicSeed>();
        foreach (var (fid, clauses) in _dynamicClauses)
        {
            if (clauses.Count == 0) continue;
            var (atomId, arity) = FunctorTable.Lookup(fid);
            string name = AtomTable.GetById(atomId)?.Name
                ?? throw new InvalidOperationException(
                    $"SaveState: functor id {fid} has no atom-table entry.");
            var encoded = new byte[clauses.Count][];
            for (int i = 0; i < clauses.Count; i++)
                encoded[i] = TermCodec.EncodeClause(clauses[i]);
            dynamicSeeds.Add(new ShmoDynamicSeed(
                new PredicateRef(name, arity), encoded));
        }
        var snapshot = new BundleSnapshot(dynamicOnly, consultHistory, dynamicSeeds);
        return new Bundle(Array.Empty<BundleEntry>(), foreignAssemblies: null, snapshot);
    }

    /// <summary>Save-state chunk 264 — restores a snapshot previously
    /// written by <see cref="SaveState"/>. Full-mode snapshots reset
    /// this engine's state first (clearing every consulted module,
    /// dynamic clause, and operator declaration not in the parser
    /// default) and then replay the saved consults + clauses.
    /// Dynamic-only snapshots merge their clauses into the current
    /// state via <c>assertz</c>, leaving consults and operators
    /// untouched.
    ///
    /// <para>Throws <see cref="InvalidDataException"/> if the file
    /// isn't a Shumway bundle or carries no snapshot trailer (i.e.
    /// was produced by <c>shumway-link</c> / <c>shumway-compile</c>
    /// rather than <see cref="SaveState"/>).</para></summary>
    public void RestoreState(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        RestoreStateFromBundle(BundleReader.ReadFromFile(path));
    }

    /// <summary>Save-state chunk 264 — in-memory variant of
    /// <see cref="RestoreState"/>; reads from a bundle byte array.</summary>
    public void RestoreStateFromBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        RestoreStateFromBundle(BundleReader.FromBytes(data));
    }

    private void RestoreStateFromBundle(Bundle bundle)
    {
        if (bundle.Snapshot is not { } snap)
            throw new InvalidDataException(
                "RestoreState: bundle has no save-state snapshot trailer "
                + "(was it produced by shumway-link rather than SaveState?).");

        if (!snap.DynamicOnly)
        {
            // Full reset: drop every consulted module (keep only the
            // default 'user' module), every dynamic clause, the chunk-82
            // static-predicate cache, the cached static link region, the
            // persistent dynamic buffer, and the consult-history log.
            // Then re-consult the prelude (the ctor's first step) and
            // replay the saved history.
            _modules.Clear();
            _modules[DefaultModuleName] = new ModuleManifest(DefaultModuleName);
            _dynamicClauses.Clear();
            _dynChainTable = new DynChainTable();
            _staticPredicateCache.Clear();
            _dynamicPredicateCache.Clear();
            _skipCompileMergedCache = null;   // chunk 430 — both caches cleared
            _precompiledStaticPredicates.Clear();
            _staticLink = null;
            InvalidatePersistent();
            _consultHistory.Clear();
            ConsultStringInner(Prelude.Source, recordInHistory: false);
            MarkModuleNonDebuggable(Prelude.ModuleName);   // ADR-035
            foreach (var src in snap.ConsultHistory)
                ConsultString(src);
        }

        // Re-assert the snapshot's dynamic clauses. In full mode this
        // restores the post-snapshot state on top of the replayed
        // consults; in dynamic-only mode it merges into the engine as-is.
        // We bypass the AppendDynamicClauseIncremental in-place path
        // (which needs a live Activation) and just bookkeep via Assertz +
        // invalidate the persistent buffer once at the end. The next
        // query rebuilds dispatch from scratch and sees every restored
        // clause through the normal chunk-126 trampoline path.
        bool anyRestored = false;
        foreach (var seed in snap.DynamicClauses)
        {
            foreach (var encoded in seed.EncodedClauses)
            {
                var clause = TermCodec.DecodeClause(encoded);
                Assertz(clause);
                anyRestored = true;
            }
        }
        if (anyRestored) InvalidatePersistent();
    }

    /// <summary>Loads Prolog source. The first <c>:- module(name).</c>
    /// directive in the source (if any) chooses the target module — re-consulting
    /// the same module replaces its previous contents. Source with no module
    /// directive appends to the default <see cref="DefaultModuleName"/>
    /// module.
    ///
    /// <para>The call drives the source through <see cref="ClauseReader"/> once
    /// up front so any <c>:- op</c> declarations take effect immediately; the
    /// returned clause stream is sorted into module-local storage and a final
    /// compile happens at query time.</para></summary>
    /// <summary>Chunk 235 — public file-loading entry, the embedding-API
    /// counterpart of the REPL's local <c>ConsultFile</c> and the
    /// <c>consult/1</c> / <c>reconsult/1</c> builtins. Routes by
    /// extension: <c>.shum</c> goes through
    /// <see cref="LoadBundle(string)"/> (precompiled bytecode + maybe
    /// IL), everything else is read as text and handed to
    /// <see cref="ConsultString"/>.
    ///
    /// <para>Throws <see cref="System.IO.FileNotFoundException"/> if the
    /// path doesn't exist (callers — including the <c>consult/1</c>
    /// builtin — translate to ISO <c>existence_error(source_sink, _)</c>).
    /// Source-level parse / compile errors propagate as
    /// <see cref="PrologRuntimeException"/>.</para></summary>
    // Full paths consulted via ConsultFile — so a use_module/1 import of an
    // already-loaded file is a no-op instead of a re-consult (which would
    // double the file's clauses).
    private readonly HashSet<string> _consultedPaths = new(StringComparer.OrdinalIgnoreCase);

    public void ConsultFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.EndsWith(".shum", StringComparison.OrdinalIgnoreCase))
        {
            LoadBundle(path);
            return;
        }
        try { _consultedPaths.Add(Path.GetFullPath(path)); }
        catch { /* unresolvable path — idempotency simply won't apply */ }
        // ISO include/1 — `:- include('lib/x.pl')` resolves relative to the
        // INCLUDING file's directory, so consulting a file records its
        // directory for the duration of the consult (restored after: a
        // nested ConsultFile from an initialization goal must not leak).
        string? prevBase = _consultBaseDir;
        _consultBaseDir = Path.GetDirectoryName(Path.GetFullPath(path));
        // ADR-035 — stop sites compiled during this consult are stamped with this
        // file, so a debugger can map a breakpoint in foo.pl back to them.
        int prevFile = _debugFileId;

        // THE FULL PATH, resolved against the ENGINE's directory — which is the only process
        // that knows it. `shumway --debug Blint.pl` run in c:\temp consults c:\temp\Blint.pl,
        // and if a frame says only "Blint.pl" the debugger has to guess where that is: it
        // resolves it against ITS OWN directory (Visual Studio's), finds no such file, matches
        // no module, and shows the frame grey — no language, no source, nothing to click. The
        // engine knows; it should say. (DebugSiteTable identifies a file by its base name, so
        // a breakpoint the editor sets still binds — but the NAME it reports is now one anybody
        // can find.)
        string debugPath = path;
        try { debugPath = Path.GetFullPath(path); }
        catch (Exception) { /* unresolvable — the name as given is the best we have */ }
        _debugFileId = DebugSiteTable.InternFile(debugPath);

        // And the debugger is TOLD about the file, now, whether or not anything ever stops in
        // it. A breakpoint binds against a module and a module IS a file: until the debugger
        // knows the name, the frames of that file have no module, so they are grey, carry no
        // language, and open nothing when clicked. It used to learn the names only from the
        // command line (a launch) or from the frames of a stop that had already happened — so
        // a file consulted from the top level (`?- [blint].`) was invisible until the SECOND
        // time the program stopped. The engine knows which files it loaded; it should say so.
        if (DebugSession is not null)
            Debugging.ShumwayDebugHelper.NoteSourceFile(path);
        try
        {
            ConsultString(File.ReadAllText(path));
        }
        finally
        {
            _consultBaseDir = prevBase;
            _debugFileId = prevFile;
        }
    }

    /// <summary>ADR-035 — functors a <c>:- disable_debug.</c> region covered.
    /// Engine-wide (fids are global) and additive across consults: a predicate is
    /// either debuggable or it isn't, whichever file it came from.</summary>
    private readonly HashSet<int> _nonDebuggableFunctors = new();

    /// <summary>ADR-035 — the <c>Name/Arity</c> of a clause's head, or false for
    /// anything that is not one.</summary>
    private static bool TryReadClauseHead(Clause clause, out (string Name, int Arity) spec)
    {
        Term head = clause.Term is CompoundTerm { Functor: ":-", Args.Length: 2 } rule
            ? rule.Args[0]
            : clause.Term;
        switch (head)
        {
            case AtomTerm a: spec = (a.Name, 0); return true;
            case CompoundTerm c: spec = (c.Functor, c.Args.Length); return true;
            default: spec = default; return false;
        }
    }

    /// <summary>ADR-035 — the functor ids of the predicates a
    /// <c>:- disable_debug.</c> region covered. A predicate's name is mangled
    /// (<c>module$name</c>) if it is module-local and left bare if it is public or
    /// dynamic, and which of those it is has not been decided at the point the
    /// directive was read — so both spellings are interned and the set holds
    /// whichever the compiler ends up grouping by. Interning a functor that never
    /// gets used costs an int.</summary>
    /// <summary>ADR-035 — every predicate of a module the user did not write is
    /// implicitly <c>:- disable_debug.</c>: the prelude, the CLP libraries. Stepping
    /// into <c>append/3</c> and landing in library source nobody asked to see is the
    /// oldest annoyance in debugging, and the library is not what the user is debugging.
    ///
    /// <para>This has to be recorded rather than inferred from the flag at compile time,
    /// because the library's clauses are RE-compiled at query setup, by which point the
    /// engine's <c>compile_mode</c> is whatever the user's program set — so without this
    /// the library would silently become debuggable, take stop sites, and attribute them
    /// to the user's file.</para></summary>
    private void MarkModuleNonDebuggable(string moduleName)
    {
        _nonDebuggableModules.Add(moduleName);
        if (!_modules.TryGetValue(moduleName, out var module)) return;
        var specs = new HashSet<(string Name, int Arity)>();
        foreach (var clause in module.Clauses)
            if (TryReadClauseHead(clause, out var spec))
                specs.Add(spec);
        _nonDebuggableFunctors.UnionWith(ResolveNonDebuggableFids(specs, moduleName));
    }

    private readonly HashSet<string> _nonDebuggableModules = new();

    private static HashSet<int> ResolveNonDebuggableFids(
        HashSet<(string Name, int Arity)> specs, string moduleName)
    {
        var fids = new HashSet<int>();
        foreach (var (n, a) in specs)
        {
            fids.Add(FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, a));
            fids.Add(FunctorTable.Intern(
                AtomTable.Intern($"{moduleName}${n}", permanent: true).Id, a));
        }
        return fids;
    }

    /// <summary>ADR-035 — the <see cref="DebugSiteTable"/> file the consult in
    /// progress is reading. Defaults to the synthetic <c>&lt;string&gt;</c> file
    /// that <see cref="ConsultString"/> sources belong to.</summary>
    private int _debugFileId = DebugSiteTable.InternFile("<string>");

    /// <summary>Runtime <c>consult/1</c> — a consult invoked from a live query.
    /// Same as <see cref="ConsultFile"/> but marks the consult as mid-query so
    /// source-declared dynamic clauses land in the live dispatch too (a later
    /// call in the same query sees them, matching a runtime <c>assertz</c> and
    /// the ISO logical update view). Save/restore keeps nested consults sound.</summary>
    internal void ConsultFileLive(string path, Activation liveEngine)
    {
        // A .shum bundle load has no runtime dynamic-clause routing to patch.
        if (path.EndsWith(".shum", StringComparison.OrdinalIgnoreCase))
        {
            ConsultFile(path);
            return;
        }
        var prev = _liveConsultEngine;
        _liveConsultEngine = liveEngine;
        try { ConsultFile(path); }
        finally { _liveConsultEngine = prev; }
    }

    /// <summary>Directory of the file currently being consulted (null for a
    /// raw <see cref="ConsultString"/>) — the base `:- include/1` paths
    /// resolve against.</summary>
    private string? _consultBaseDir;

    /// <summary>Chunk 236 — classical <c>reconsult/1</c> semantics: for
    /// every <c>Name/Arity</c> the file defines in its target module,
    /// abolish any pre-existing clauses (static <em>and</em> dynamic),
    /// then load the file. Predicates the file doesn't mention are left
    /// untouched — the difference vs <see cref="ConsultFile"/>, which
    /// appends and would duplicate clauses on every reload.
    ///
    /// <para>For <c>.shum</c> bundles: each entry's
    /// <see cref="BundleEntry.Defined"/> list (when populated by
    /// <c>shumway-link</c>) names every predicate the bundle owns; we
    /// abolish those in their respective target modules and then go
    /// through <see cref="LoadBundle(Bundle)"/>. For entries with no
    /// <c>Defined</c> list (hand-built bundles), we fall back to
    /// parsing the entry source.</para>
    ///
    /// <para>Static predicates are replaced silently — the immutability
    /// invariant applies to the compiled-code level, while the clause
    /// store this method edits is the source-of-truth that the next
    /// query recompiles from. This matches the SWI / GProlog
    /// edit-reload workflow.</para></summary>
    public void ReconsultFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.EndsWith(".shum", StringComparison.OrdinalIgnoreCase))
        {
            var bundle = BundleReader.ReadFromFile(path);
            foreach (var entry in bundle.Entries)
            {
                string targetModule = string.IsNullOrEmpty(entry.ModuleName)
                    ? DefaultModuleName : entry.ModuleName;
                HashSet<int> defined;
                if (entry.Defined.Count > 0)
                {
                    defined = new HashSet<int>(entry.Defined.Count);
                    foreach (var d in entry.Defined)
                        defined.Add(FunctorTable.Intern(
                            AtomTable.Intern(d.Indicator.Name, permanent: true).Id,
                            d.Indicator.Arity));
                }
                else
                {
                    (_, defined) = ScanSourceForDefinedHeads(entry.Source);
                }
                foreach (int fid in defined)
                    AbolishPredicateInModule(targetModule, fid);
            }
            LoadBundle(bundle);
        }
        else
        {
            string source = File.ReadAllText(path);
            var (moduleName, defined) = ScanSourceForDefinedHeads(source);
            foreach (int fid in defined)
                AbolishPredicateInModule(moduleName, fid);
            ConsultString(source);
        }
    }

    /// <summary>Chunk 236 — light reader pass over a source string,
    /// returning the target module name (the <c>:- module/1</c>
    /// directive's argument, or <see cref="DefaultModuleName"/> if
    /// absent) and the set of head functor ids of every non-directive
    /// clause. Used by <see cref="ReconsultFile"/> to know what to
    /// abolish before loading. Parses the source twice (the subsequent
    /// <see cref="ConsultString"/> reads it again) — the cost is
    /// acceptable for the developer edit-reload path.</summary>
    private (string ModuleName, HashSet<int> HeadFunctorIds) ScanSourceForDefinedHeads(
        string source)
    {
        var rawClauses = new ClauseReader(
            new Lexer(source, _flags.CharConversionEnabled ? _flags.CharConversion : null),
            _operators, _flags).ReadAll().ToList();
        string moduleName = DefaultModuleName;
        var heads = new HashSet<int>();
        foreach (var clause in rawClauses)
        {
            if (clause.Kind == ClauseKind.Directive)
            {
                if (clause.Term is CompoundTerm dWrap && dWrap.Args.Length == 1
                    && TryReadModuleDirective(dWrap.Args[0], out string? name))
                    moduleName = name;
                continue;
            }
            heads.Add(HeadFunctorIdOf(clause));
        }
        return (moduleName, heads);
    }

    /// <summary>Chunk 236 — abolishes the named functor in the given
    /// module: drops every matching clause from the module manifest
    /// (and removes the functor from any companion sets:
    /// PublicFunctors / DiscontiguousFunctors / MultifileFunctors /
    /// ModeDeclarations), then — if the functor was dynamic at all —
    /// calls <see cref="AbolishDynamic(int)"/> to clear runtime state.
    /// Module-scoped, so other modules' clauses for the same functor
    /// are left alone (matters for multifile predicates).</summary>
    private void AbolishPredicateInModule(string moduleName, int fid)
    {
        if (_modules.TryGetValue(moduleName, out var manifest))
        {
            int kept = 0;
            for (int i = 0; i < manifest.Clauses.Count; i++)
            {
                if (HeadFunctorIdOf(manifest.Clauses[i]) != fid)
                {
                    if (kept != i) manifest.Clauses[kept] = manifest.Clauses[i];
                    kept++;
                }
            }
            if (kept < manifest.Clauses.Count)
                manifest.Clauses.RemoveRange(kept, manifest.Clauses.Count - kept);
            manifest.PublicFunctors.Remove(fid);
            manifest.DiscontiguousFunctors.Remove(fid);
            manifest.MultifileFunctors.Remove(fid);
            manifest.ModeDeclarations.Remove(fid);
        }

        if (_dynamicFunctors.Contains(fid) || _dynamicClauses.ContainsKey(fid))
            AbolishDynamic(fid);

        // Compiled-code caches must be dropped so the next query
        // recompiles against the trimmed manifest.
        _staticPredicateCache.Clear();
        _skipCompileMergedCache = null;   // chunk 430 — static cache cleared
        _staticLink = null;
        InvalidatePersistent();
    }

    /// <summary>Chunk 237 — registers every method on
    /// <paramref name="instance"/> that carries a
    /// <see cref="PrologPredicateAttribute"/>, binding it as a
    /// foreign Prolog predicate. The method's C# signature must be
    /// <c>bool Method(Activation engine)</c>; instance methods capture
    /// <paramref name="instance"/> into the registered delegate, so
    /// the instance stays alive for as long as any engine has the
    /// predicate registered.
    ///
    /// <para>Throws <see cref="InvalidOperationException"/> when a
    /// decorated method has the wrong signature, or when the
    /// resulting <c>Name/Arity</c> collides with a previously
    /// registered builtin (the existing builtin would silently win
    /// otherwise — confusing during development).</para></summary>
    /// <summary>Chunk 238 — per-engine custom term converters
    /// registered via <see cref="RegisterConverter{T}"/>. Lazily
    /// allocated; null when the engine only uses the built-in
    /// scalar mappings.</summary>
    private Dictionary<Type, (object ToTerm, object FromTerm)>? _userConverters;

    /// <summary>Chunk 238 — register a custom C#-type ↔ Prolog-term
    /// pair for <typeparamref name="T"/>. Takes precedence over the
    /// built-in scalar conversions (so a host can override the
    /// default <c>string</c> → <see cref="StringTerm"/> mapping with,
    /// say, <see cref="AtomTerm"/> semantics). Replaces any prior
    /// registration for the same type.</summary>
    public void RegisterConverter<T>(
        Func<PrologEngine, T, Term> toTerm, Func<Term, T> fromTerm)
    {
        ArgumentNullException.ThrowIfNull(toTerm);
        ArgumentNullException.ThrowIfNull(fromTerm);
        _userConverters ??= new();
        _userConverters[typeof(T)] = (toTerm, fromTerm);
    }

    /// <summary>Chunk 238 — converts <paramref name="value"/> to a
    /// Prolog <see cref="Term"/>. Resolution order: a user converter
    /// for <typeparamref name="T"/> if registered, then the built-in
    /// scalar mapping (<see cref="TermConverters"/>). Throws
    /// <see cref="InvalidOperationException"/> when neither covers
    /// the type — the diagnostic names the type so the host can
    /// register a converter.</summary>
    public Term ToTerm<T>(T value)
    {
        if (_userConverters is not null
            && _userConverters.TryGetValue(typeof(T), out var pair))
        {
            return ((Func<PrologEngine, T, Term>)pair.ToTerm)(this, value);
        }
        if (TermConverters.TryToTerm<T>(value, out Term result))
            return result;
        if (CompositeConverters.TryToTerm<T>(this, value, out result))
            return result;
        // Chunk 241: convention discovery — a generator-emitted (or
        // hand-written) ToPrologTerm(engine) on T.
        if (ConventionConverters.TryToTerm<T>(this, value, out result))
            return result;
        throw new InvalidOperationException(
            $"No term converter registered for type '{typeof(T).FullName}'. "
            + "Register one with engine.RegisterConverter<T>(toTerm, fromTerm), "
            + "or add [PrologTerm] to a partial type to get generated converters.");
    }

    /// <summary>Chunk 238 — inverse of <see cref="ToTerm{T}"/>:
    /// extracts a <typeparamref name="T"/> from <paramref name="term"/>.
    /// User converters win over the built-in mappings.</summary>
    public T FromTerm<T>(Term term)
    {
        ArgumentNullException.ThrowIfNull(term);
        if (_userConverters is not null
            && _userConverters.TryGetValue(typeof(T), out var pair))
        {
            return ((Func<Term, T>)pair.FromTerm)(term);
        }
        if (TermConverters.TryFromTerm<T>(term, out T result))
            return result;
        if (CompositeConverters.TryFromTerm<T>(this, term, out result))
            return result;
        if (ConventionConverters.TryFromTerm<T>(this, term, out result))
            return result;
        throw new InvalidOperationException(
            $"No term converter registered for type '{typeof(T).FullName}'. "
            + "Register one with engine.RegisterConverter<T>(toTerm, fromTerm), "
            + "or add [PrologTerm] to a partial type to get generated converters.");
    }

    /// <summary>Chunk 239 — reflective bridge: invoke
    /// <see cref="ToTerm{T}"/> when the element type is only known
    /// at runtime (the path the collection / tuple / nullable /
    /// dictionary handlers take to recurse into element types).
    /// The generic method handle is built and cached on first use
    /// per element type; subsequent calls are a dictionary probe +
    /// delegate invoke.</summary>
    internal Term ToTermDynamic(Type type, object? value)
    {
        // Phase 33 C3 — the cached delegate is now COMPILED (engine.ToTerm<T>((T)v))
        // instead of a wrapper that re-ran MethodInfo.Invoke + a fresh object[] per
        // ELEMENT of every converted collection. Expression.Compile interprets
        // under Native AOT, so this stays AOT-correct.
        var del = _toTermDynamicCache.GetOrAdd(type, static t =>
        {
            var m = typeof(PrologEngine)
                .GetMethod(nameof(ToTerm))!
                .MakeGenericMethod(t);
            var engP = System.Linq.Expressions.Expression.Parameter(typeof(PrologEngine), "engine");
            var valP = System.Linq.Expressions.Expression.Parameter(typeof(object), "value");
            return System.Linq.Expressions.Expression.Lambda<Func<PrologEngine, object?, Term>>(
                System.Linq.Expressions.Expression.Call(engP, m,
                    System.Linq.Expressions.Expression.Convert(valP, t)),
                engP, valP).Compile();
        });
        return del(this, value);
    }

    /// <summary>Chunk 239 — reflective bridge for the inverse
    /// direction; same caching strategy as
    /// <see cref="ToTermDynamic"/>.</summary>
    internal object? FromTermDynamic(Type type, Term term)
    {
        var del = _fromTermDynamicCache.GetOrAdd(type, static t =>
        {
            var m = typeof(PrologEngine)
                .GetMethod(nameof(FromTerm))!
                .MakeGenericMethod(t);
            var engP = System.Linq.Expressions.Expression.Parameter(typeof(PrologEngine), "engine");
            var termP = System.Linq.Expressions.Expression.Parameter(typeof(Term), "term");
            return System.Linq.Expressions.Expression.Lambda<Func<PrologEngine, Term, object?>>(
                System.Linq.Expressions.Expression.Convert(
                    System.Linq.Expressions.Expression.Call(engP, m, termP), typeof(object)),
                engP, termP).Compile();
        });
        return del(this, term);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        Type, Func<PrologEngine, object?, Term>> _toTermDynamicCache = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        Type, Func<PrologEngine, Term, object?>> _fromTermDynamicCache = new();

    public void RegisterPredicates(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        RegisterPredicatesImpl(instance.GetType(), instance);
    }

    /// <summary>Static-class overload: discovers and registers every
    /// <c>static</c> method on <paramref name="type"/> annotated
    /// with <see cref="PrologPredicateAttribute"/>. Use for classes
    /// that group stateless predicates (the common case for
    /// embedding-side helpers).</summary>
    public void RegisterPredicates(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        RegisterPredicatesImpl(type, instance: null, staticOnly: false);
    }

    /// <summary>Chunk 247 overload — when
    /// <paramref name="staticOnly"/> is true, instance methods
    /// decorated with <c>[PrologPredicate]</c> are silently
    /// skipped instead of throwing. Used by the auto-loader path
    /// that walks every type in a foreign-DLL: instance methods
    /// can't be auto-registered without an instance, but other
    /// (static) methods in the same DLL should still surface.</summary>
    public void RegisterPredicates(Type type, bool staticOnly)
    {
        ArgumentNullException.ThrowIfNull(type);
        RegisterPredicatesImpl(type, instance: null, staticOnly: staticOnly);
    }

    /// <summary>Generic convenience: <c>engine.RegisterPredicates&lt;MyClass&gt;()</c>
    /// for static classes.</summary>
    public void RegisterPredicates<T>() => RegisterPredicates(typeof(T));

    /// <summary>Chunk 247 — loads <paramref name="assemblyPath"/>
    /// via <see cref="System.Reflection.Assembly.LoadFrom"/> and
    /// registers every <c>[PrologPredicate]</c>-decorated <c>static</c>
    /// method across all types in the assembly. Instance methods are
    /// skipped (they need a constructed instance the loader doesn't
    /// have). The runtime <see cref="LoadBundle(Bundle)"/> path calls
    /// this for each <see cref="Bundle.ForeignAssemblies"/> entry the
    /// linker recorded.</summary>
    /// <summary>Chunk 247 — probes for a foreign assembly by name
    /// in the directories the bundle's foreign-DLL convention
    /// inspects: the bundle's own directory first (typical
    /// <c>myapp.shum</c> + <c>MyForeigns.dll</c> layout), then the
    /// executable's base directory (the <c>--exe</c> path that
    /// copies foreign DLLs next to the produced executable), then
    /// the runtime's default <c>Assembly.Load</c> probe path as a
    /// last resort. Returns <c>null</c> if every probe misses; the
    /// caller surfaces a file-not-found.</summary>
    private static string? ResolveForeignAssemblyPath(string name, string? bundleDir)
    {
        if (bundleDir is not null)
        {
            string candidate = System.IO.Path.Combine(bundleDir, name);
            if (System.IO.File.Exists(candidate)) return candidate;
        }
        string baseDir = AppContext.BaseDirectory;
        string baseCandidate = System.IO.Path.Combine(baseDir, name);
        if (System.IO.File.Exists(baseCandidate)) return baseCandidate;
        // Last resort: Assembly.Load on the bare assembly name (no
        // extension) — the runtime walks its probing paths. Returns
        // a path-less reference if successful; we hand back the
        // assembly's location for symmetry with the file paths above.
        try
        {
            string nameNoExt = System.IO.Path.GetFileNameWithoutExtension(name);
            var asm = System.Reflection.Assembly.Load(nameNoExt);
            return asm.Location;
        }
        catch { return null; }
    }

    public void RegisterForeignAssembly(string assemblyPath)
    {
        ArgumentNullException.ThrowIfNull(assemblyPath);
        var asm = System.Reflection.Assembly.LoadFrom(assemblyPath);
        foreach (var type in asm.GetTypes())
        {
            // Cheap pre-filter: skip types with no [PrologPredicate]
            // decoration anywhere. The full RegisterPredicates pass
            // does this check too, but a `false` quick reject avoids
            // the BindingFlags walk per type for the typical case.
            bool hasAttribute = false;
            foreach (var method in type.GetMethods(
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (System.Reflection.CustomAttributeExtensions
                    .GetCustomAttribute<PrologPredicateAttribute>(method) is not null)
                {
                    hasAttribute = true;
                    break;
                }
            }
            if (!hasAttribute) continue;
            RegisterPredicates(type, staticOnly: true);
        }
    }

    private void RegisterPredicatesImpl(Type type, object? instance, bool staticOnly = false)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.DeclaredOnly;

        // Walk type + bases so inherited [PrologPredicate] methods
        // are picked up exactly once.
        var seen = new HashSet<System.Reflection.MethodInfo>();
        for (Type? t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (var method in t.GetMethods(flags))
            {
                if (!seen.Add(method)) continue;
                var attr = System.Reflection.CustomAttributeExtensions
                    .GetCustomAttribute<PrologPredicateAttribute>(method);
                if (attr is null) continue;

                if (method.IsStatic == false && instance is null)
                {
                    // Chunk 247: the static-only auto-loader path
                    // (foreign-DLL scanning) silently skips instance
                    // methods — there's no instance available, and
                    // failing the whole DLL because one type mixes
                    // static + instance [PrologPredicate]s would
                    // be hostile. The explicit RegisterPredicates
                    // (Type) call still throws.
                    if (staticOnly) continue;
                    throw new InvalidOperationException(
                        $"[PrologPredicate] on instance method '{type.FullName}.{method.Name}' "
                        + "requires RegisterPredicates(instance); call the (Type) / generic overload "
                        + "only for static methods.");
                }

                var parameters = method.GetParameters();
                bool isCanonical = method.ReturnType == typeof(bool)
                    && parameters.Length == 1
                    && parameters[0].ParameterType == typeof(Shumway.Core.Activation);

                System.Reflection.MethodInfo dispatchTarget;
                if (isCanonical)
                {
                    dispatchTarget = method;
                }
                else
                {
                    // Chunk 242: typed signature — locate the
                    // generator-emitted bridge in the same type.
                    // Name convention: "_{Method}_PrologBridge". The
                    // bridge has the same staticness as the user
                    // method (so the same CreateDelegate path below
                    // works); RegisterPredicates was already
                    // checking instance / Type-overload mismatches
                    // upstream, so the bridge inherits that check.
                    string bridgeName = "_" + method.Name + "_PrologBridge";
                    var bridge = t.GetMethod(
                        bridgeName,
                        flags,
                        binder: null,
                        types: new[] { typeof(Shumway.Core.Activation) },
                        modifiers: null);
                    if (bridge is null || bridge.ReturnType != typeof(bool))
                    {
                        throw new InvalidOperationException(
                            $"[PrologPredicate] method '{type.FullName}.{method.Name}' has a typed "
                            + "signature but no matching generator-emitted bridge "
                            + $"'{bridgeName}(Shumway.Core.Activation)' was found. Ensure the "
                            + "Shumway.SourceGen analyzer is referenced — "
                            + "<ProjectReference ... OutputItemType=\"Analyzer\" /> — and the build "
                            + "succeeded.");
                    }
                    if (bridge.IsStatic != method.IsStatic)
                    {
                        throw new InvalidOperationException(
                            $"[PrologPredicate] bridge '{bridgeName}' has different staticness than "
                            + $"the user method '{method.Name}'. The generator should have matched it — "
                            + "this likely means a hand-written method named the same as a generator "
                            + "output. Rename the user method.");
                    }
                    dispatchTarget = bridge;
                }

                string name = attr.Name ?? method.Name;
                int arity = attr.Arity;
                var del = (Shumway.Builtins.BuiltinImpl)dispatchTarget.CreateDelegate(
                    typeof(Shumway.Builtins.BuiltinImpl),
                    dispatchTarget.IsStatic ? null : instance);

                // BuiltinsRegistry.Register is idempotent — a second call with
                // the same functor returns the existing id and silently
                // discards the new impl. That's the right behaviour when the
                // same [PrologPredicate] is re-registered (e.g. the same
                // attribute discovered across two engines in one process, or
                // a test re-running with shared static state), but wrong when
                // a *different* implementation tries to use the name —
                // there's no diagnostic and the second impl just never runs.
                // Detect the latter case explicitly: an existing entry whose
                // delegate target+method differ from ours is a real conflict.
                int functorId = Shumway.Core.FunctorTable.Intern(
                    Shumway.Core.AtomTable.Intern(name, permanent: true).Id, arity);
                if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(functorId, out int existingId))
                {
                    var existing = Shumway.Builtins.BuiltinsRegistry.GetById(existingId);
                    if (!ReferenceEquals(existing.Impl.Method, del.Method)
                        || !Equals(existing.Impl.Target, del.Target))
                    {
                        // Chunk 247: the static-only auto-loader
                        // path (foreign-DLL scanning) silently skips
                        // collisions for the same reason it skips
                        // instance methods — failing the whole DLL
                        // load because one type's [PrologPredicate]
                        // happens to collide with a standard builtin
                        // would be hostile to the embedder. The
                        // explicit-Type RegisterPredicates call
                        // still throws.
                        if (staticOnly) continue;
                        throw new InvalidOperationException(
                            $"[PrologPredicate] '{name}/{arity}' from '{type.FullName}.{method.Name}' "
                            + "collides with an already-registered builtin. Re-registration would be a "
                            + "no-op and the new implementation would never run — pick a different name/arity.");
                    }
                    // Same method+target — silent no-op, exactly what
                    // BuiltinsRegistry.Register would do anyway.
                    _foreignBuiltinIds[existingId] = 0;
                    continue;
                }

                _foreignBuiltinIds[Shumway.Builtins.BuiltinsRegistry.Register(
                    name, arity, del, attr.Category, attr.Template, attr.Summary)] = 0;
            }
        }
    }

    /// <summary>The builtins that are somebody's C# — a <c>[PrologPredicate]</c> foreign
    /// predicate rather than an implementation of ours. Global, like the registry itself.
    ///
    /// <para>ADR-035 reads it: a foreign call is the one place a debugger can end up stopped
    /// in code the ENGINE is not standing in, and the Prolog stack under that C# is what
    /// makes the stack mixed rather than merely managed. The engine cannot be asked for it
    /// then — it is frozen inside the call — so it publishes it on the way in.</para></summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte>
        _foreignBuiltinIds = new();

    internal static bool IsForeignBuiltin(int builtinId)
        => _foreignBuiltinIds.ContainsKey(builtinId);

    public void ConsultString(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ConsultStringInner(source, recordInHistory: true);
    }

    /// <summary>Cached parse of the internal prelude. The prelude source is a
    /// compile-time constant, consulted identically into every fresh engine
    /// with default operators/flags and no operator/flag directives that would
    /// make the parse engine-dependent — so its <see cref="ClauseReader"/>
    /// output (the bulk of each engine's construction cost) is parsed once and
    /// the immutable AST shared across engines. Compilation to WAM still
    /// happens lazily per engine at query setup, exactly as a fresh parse
    /// would, so engine state and behaviour are unchanged.</summary>
    private static List<Clause>? s_preludeClauses;

    private void ConsultStringInner(string source, bool recordInHistory,
        string? moduleNameFallback = null)
    {
        // Save-state chunk 264: record every user-visible consult so
        // SaveState can serialize it. The prelude (auto-loaded by the
        // ctor) and any other engine-internal source go through this
        // private path with recordInHistory=false to stay out of the
        // snapshot.
        if (recordInHistory) _consultHistory.Add(source);
        // The static program is about to change — drop the chunk-82
        // compiled-static-predicate cache so the next query recompiles,
        // and the ADR-015 cached static linked region with it.
        _staticPredicateCache.Clear();
        _skipCompileMergedCache = null;   // chunk 430 — static cache cleared
        _staticLink = null;
        InvalidatePersistent();
        List<Clause> rawClauses;
        if (ReferenceEquals(source, Prelude.Source) && s_preludeClauses is { } cached)
        {
            rawClauses = cached;   // reuse the one-time prelude parse
        }
        else
        {
            rawClauses = new ClauseReader(
                new Lexer(source, _flags.CharConversionEnabled ? _flags.CharConversion : null)
                {
                    // ADR-035 — every position this consult produces knows which FILE it came
                    // from. It has to travel with the position, because the clause is not
                    // compiled now: compilation happens at query setup, long after the consult
                    // that read the file is over, and a compiler field saying "the file we are
                    // reading" says nothing true by then.
                    FileId = _debugFileId,
                },
                _operators, _flags).ReadAll().ToList();
            // First prelude consult in the process: cache its parse (computed
            // with this engine's default operators/flags) for every later one.
            if (ReferenceEquals(source, Prelude.Source))
                System.Threading.Volatile.Write(ref s_preludeClauses, rawClauses);
        }

        // Record the prelude's predicates so predicate_property/2 reports them
        // as built_in (they are library predicates written in Prolog, not
        // user-defined). Every engine consults the prelude once at
        // construction; the head fids are the bare (unmangled) functor ids a
        // caller uses. Directives contribute no head.
        if (ReferenceEquals(source, Prelude.Source))
            foreach (var c in rawClauses)
                if (c.Kind != ClauseKind.Directive)
                    _preludeFunctors.Add(HeadFunctorIdOf(c));

        // ISO 7.4.2.7 `:- include(File)` — textual inclusion (semantics in
        // IncludeExpander). Paths resolve against the consulting file's
        // directory (ConsultFile), else the process CWD. Returns the same
        // list when nothing expands, keeping the cached prelude parse shared.
        rawClauses = Shumway.Compiler.Parsing.IncludeExpander.Expand(
            rawClauses, _consultBaseDir, _operators, _flags);

        // Chunk 440 — a source-bearing bundle entry consults under the
        // entry's module name (the per-file fallback ShmoCompiler resolved
        // at compile time), so two module-less files keep their own local
        // namespaces instead of merging into a rolling "user" module. A
        // plain ConsultString (no fallback) keeps the historic behaviour.
        string moduleName = string.IsNullOrEmpty(moduleNameFallback)
            ? DefaultModuleName
            : moduleNameFallback;
        bool moduleDirectiveSeen = false;
        var publics = new HashSet<int>();
        var clauses = new List<Clause>();
        HashSet<int>? pendingDiscontiguous = null;
        HashSet<int>? pendingMultifile = null;
        HashSet<int>? tabledFunctors = null;
        Dictionary<int, List<Shumway.Compiler.Modes.ModeDeclaration>>? pendingModes = null;
        List<Term>? initializationGoals = null;
        // ADR-022 — accumulated raw text of this consult's `:- c` regions (the
        // captured `$native_decls` directives), parsed into the C symbol table
        // for the embedded native-block transform below.
        System.Text.StringBuilder? nativeDecls = null;
        // ADR-035 — `:- disable_debug.` / `:- enable_debug.` are POSITIONAL: each
        // one sets the debuggability of the clauses that follow it, until the
        // next such directive or the end of the file. So a module can hand the
        // debugger the predicates worth stepping through and keep the rest
        // compiled for speed.
        bool debuggableHere = true;
        HashSet<(string Name, int Arity)>? nonDebuggable = null;

        foreach (var clause in rawClauses)
        {
            if (clause.Kind != ClauseKind.Directive)
            {
                clauses.Add(clause);
                if (!debuggableHere && TryReadClauseHead(clause, out var headSpec))
                    (nonDebuggable ??= new()).Add(headSpec);
                continue;
            }

            // Strip the leading `:- /1` wrapper to get the directive body.
            if (clause.Term is not CompoundTerm dWrap || dWrap.Args.Length != 1) continue;
            Term body = dWrap.Args[0];

            // ADR-035 — the positional debug switches. Handled here, before every
            // other directive, because they carry no argument and mean nothing to
            // the rest of the pipeline.
            if (body is AtomTerm { Name: "disable_debug" }) { debuggableHere = false; continue; }
            if (body is AtomTerm { Name: "enable_debug" }) { debuggableHere = true; continue; }

            if (TryReadModuleDirective(body, out string? name, out var moduleExports))
            {
                if (moduleDirectiveSeen)
                    throw new InvalidOperationException(
                        "Multiple :- module(...) directives in one ConsultString call.");
                moduleName = name;
                moduleDirectiveSeen = true;
                // Standard two-arg `:- module(Name, [p/N, ...])` — the export
                // list makes those predicates public (globally visible), the
                // rest stay module-local.
                if (moduleExports != null)
                    foreach (var (n, a) in moduleExports)
                        publics.Add(FunctorTable.Intern(
                            AtomTable.Intern(n, permanent: true).Id, a));
            }
            else if (TryReadPublicDirective(body, out var publicSpecs))
            {
                foreach (var (n, a) in publicSpecs)
                    publics.Add(FunctorTable.Intern(
                        AtomTable.Intern(n, permanent: true).Id, a));
            }
            else if (TryReadDynamicDirective(body, out var dynamicSpecs))
            {
                // Dynamic functors are tracked engine-wide so assertz / retract
                // hit a single store regardless of which module declared them.
                foreach (var (n, a) in dynamicSpecs)
                {
                    int fid = FunctorTable.Intern(
                        AtomTable.Intern(n, permanent: true).Id, a);
                    _dynamicFunctors.Add(fid);
                    // Reserve an entry so retract on a never-asserted dynamic
                    // predicate fails cleanly instead of throwing.
                    if (!_dynamicClauses.ContainsKey(fid))
                        _dynamicClauses[fid] = new List<Clause>();
                }
            }
            else if (TryReadFunctorIndicatorDirective(body, "native", out var nativeSpecs))
            {
                // ADR-024 — `:- native fn/N` marks fn as a native function using the
                // materializer protocol (a native C function via P/Invoke, or a .NET
                // method taking a managed Reftype snapshot) rather than a plain .NET
                // interop method. The block call site materializes its reftype args.
                foreach (var (n, a) in nativeSpecs)
                {
                    _nativeFunctions.Add(FunctorTable.Intern(
                        AtomTable.Intern(n, permanent: true).Id, a));
                    _nativeFunctionNames.Add(n);
                }
            }
            else if (TryReadFunctorIndicatorDirective(body, "discontiguous", out var discSpecs))
            {
                // Store the metadata against the module that's about to be
                // committed; the writer below picks it up via the
                // `pendingDiscontiguous` capture.
                pendingDiscontiguous ??= new HashSet<int>();
                foreach (var (n, a) in discSpecs)
                    pendingDiscontiguous.Add(FunctorTable.Intern(
                        AtomTable.Intern(n, permanent: true).Id, a));
            }
            else if (TryReadFunctorIndicatorDirective(body, "multifile", out var multiSpecs))
            {
                pendingMultifile ??= new HashSet<int>();
                foreach (var (n, a) in multiSpecs)
                {
                    int fid = FunctorTable.Intern(
                        AtomTable.Intern(n, permanent: true).Id, a);
                    pendingMultifile.Add(fid);
                    // A multifile predicate accumulates clauses from several
                    // sources (assert, other files) — so it is callable and
                    // must FAIL (not existence_error) when it currently has no
                    // clauses, exactly like a dynamic predicate. Register it as
                    // dynamic + reserve an empty clause slot. (Logtalk's compiler
                    // declares its hook predicates `:- multifile` and calls them
                    // before any clause is added, relying on failure.)
                    _dynamicFunctors.Add(fid);
                    if (!_dynamicClauses.ContainsKey(fid))
                        _dynamicClauses[fid] = new List<Clause>();
                }
            }
            else if (TryReadFunctorIndicatorDirective(body, "table", out var tableSpecs))
            {
                tabledFunctors ??= new HashSet<int>();
                foreach (var (n, a) in tableSpecs)
                    tabledFunctors.Add(FunctorTable.Intern(
                        AtomTable.Intern(n, permanent: true).Id, a));
            }
            else if (body is CompoundTerm { Functor: "$native_decls", Args: [StringTerm cText] })
            {
                // ADR-022 — a captured `:- c` region. Accumulate its raw C text
                // (in source order) for the symbol table the block transform uses.
                (nativeDecls ??= new System.Text.StringBuilder()).Append(cText.Content).Append('\n');
            }
            else if (body is CompoundTerm { Functor: "initialization", Args.Length: 1 } initDir)
            {
                // ISO/SWI `:- initialization(Goal)` (the fx 1150 operator also
                // admits the paren-less `:- initialization main.`). The goal
                // runs AFTER this consult commits — SWI load-time semantics —
                // see the execution loop at the end of this method.
                (initializationGoals ??= new List<Term>()).Add(initDir.Args[0]);
            }
            else if (body is CompoundTerm spf
                     && spf.Functor == "set_prolog_flag"
                     && spf.Args.Length == 2
                     && spf.Args[0] is AtomTerm spfFlag
                     && spf.Args[1] is AtomTerm spfValue)
            {
                // Phase 19+ — flags that the parser doesn't already
                // pre-process (double_quotes is the only one) get
                // applied at consult time. Mirrors the runtime
                // set_prolog_flag/2 builtin so the directive form
                // takes effect before subsequent clauses are
                // processed (e.g. implicit_dynamic toggles the
                // CollectImplicitDynamics pre-scan).
                ApplyConsultSetPrologFlag(spfFlag.Name, spfValue.Name);
            }
            else if (Shumway.Compiler.Modes.ModeDirectiveParser.TryParseAll(
                body, out var modeDecls, out string? modeError))
            {
                // TryParseAll accepts both the single-spec form and the
                // classic DEC-10/Quintus ','-chain (`:- mode f(+,-), g(+).`).
                if (modeError is not null)
                    throw new InvalidOperationException(modeError);
                pendingModes ??= new Dictionary<int, List<Shumway.Compiler.Modes.ModeDeclaration>>();
                foreach (var modeDecl in modeDecls!)
                {
                    if (!pendingModes.TryGetValue(modeDecl.FunctorId, out var declList))
                    {
                        declList = new List<Shumway.Compiler.Modes.ModeDeclaration>();
                        pendingModes[modeDecl.FunctorId] = declList;
                    }
                    declList.Add(modeDecl);
                }
            }
            else if (body is CompoundTerm { Functor: "use_module", Args.Length: 1 } umDir)
            {
                // `:- use_module(library(Name))` / `:- use_module(File)` —
                // executed inline at consult time so a library's predicates
                // (and operators, already handled at parse time for the
                // constraint libs) are available to the rest of this consult
                // and to later queries. Scryer/Trealla programs import their
                // stdlib this way; without this the directive was silently
                // dropped and the imports never loaded.
                ExecuteUseModuleDirective(umDir.Args[0]);
            }
            else if (body is CompoundTerm { Functor: "use_module", Args.Length: 2 } umDir2)
            {
                // `:- use_module(Spec, ImportList)` — the import list is
                // advisory (Shumway's public predicates share a flat global
                // namespace); load the module the same way.
                ExecuteUseModuleDirective(umDir2.Args[0]);
            }
            // op/3 already processed in-place by ClauseReader. Other
            // unrecognised directives pass through silently — they may be
            // implementation-defined hooks that future chunks handle.
            // Chunk 436 (arity_compat only): same policy as
            // shumway-compile — an unknown directive is reported as a
            // warning (to stderr) and consult continues. Without the
            // flag the silent pass-through above is unchanged.
            else if (_flags.ArityCompat)
            {
                string dirName = body switch
                {
                    AtomTerm dirAtom => dirAtom.Name,
                    CompoundTerm dirComp => dirComp.Functor,
                    _ => "",
                };
                if (dirName.Length > 0
                    && !ShmoCompiler.RecognizedDirectives.Contains(dirName)
                    && !ShmoCompiler.SilentlyIgnoredDirectives.Contains(dirName))
                    Console.Error.WriteLine(
                        $"warning: unknown directive '{dirName}' ignored (arity_compat)");
            }
        }

        // Discontiguous enforcement (chunk 60): clauses for a given
        // functor must appear contiguously in source unless the
        // functor is declared :- discontiguous. We walk the just-read
        // clauses in source order, tracking which functors have been
        // "closed" (another functor's clauses started after them),
        // and throw if a closed functor is revisited without a
        // discontiguous declaration.
        // ADR-024 — generic-term interop. The Arity term-interface predicates
        // (reftype_term, fill_par, …) are recognized by name and provided as
        // builtins; their prlg_ifce.pl source clauses (which use the reftype-struct
        // tier — `->`, `..`, getargp, newreftype — that we deliberately do NOT
        // compile) are dropped here, BEFORE the native transform sees their blocks.
        // Also drops any redefinition of a Shumway builtin (e.g. make_c_string/4),
        // with a warning. Gated on arity_compat so a non-Arity program defining one
        // of these names is unaffected.
        if (_flags.ArityCompat)
        {
            var droppedBuiltins = new List<(string Name, int Arity)>();
            clauses = ReftypeInterface.DropInterfaceClauses(clauses, droppedBuiltins);
            foreach (var (name, arity) in droppedBuiltins)
                Console.Error.WriteLine(
                    $"warning: redefinition of builtin {name}/{arity} ignored (arity_compat)");
        }

        ValidateContiguity(clauses, pendingDiscontiguous);

        // ADR-022 — embedded native-code wiring. Rewrite each captured
        // `$native_goal(RawText)` body goal into a portable `'$native_run'('$nb$…',
        // Vars)` dispatch and register the analysed block in this engine's block
        // table, using the C symbol table parsed from the accumulated `:- c`
        // regions. Runs BEFORE the dynamic-clause routing below: a `:- dynamic` /
        // `:- visible` predicate whose source clauses use native code must have its
        // blocks transformed too (declaring a predicate dynamic is about
        // assert/retract, not about whether its source clauses can compile) — the
        // routing then moves the rewritten clause (carrying `$native_run`) into the
        // runtime store, where it runs the block exactly as a static clause does.
        // An unsupported block raises a consult error (never silently inert).
        if (NativeTransform.HasNativeBlock(clauses))
        {
            var cDecls = nativeDecls is null
                ? new List<Shumway.Compiler.NativeC.CDecl>()
                : Shumway.Compiler.NativeC.CParser.ParseDeclarations(nativeDecls.ToString());
            // ADR-024 — register a term slot for every `reftype` global in the
            // `:- c` regions (par1ref…, or the program's own). `&name` / `name` in a
            // native block resolves to this slot's cursor.
            RegisterReftypeGlobals(cDecls);
            // ADR-024 — keep the prototypes/typedefs so a P/Invoke `:- native` call
            // can derive its native marshalling signature at query time.
            RegisterNativePrototypes(cDecls);
            string prefix = "$nb$" + (_nativeBlockConsultSeq++) + "$";
            clauses = NativeTransform.Apply(clauses, cDecls, ResolveNativeInterop,
                (name, vars, stmts, scalars, _) => AddNativeBlock(name, vars, stmts, scalars), prefix,
                IsNativeFunctionName);
        }

        // Source-declared clauses for dynamic predicates (chunk 68): route
        // them to the runtime _dynamicClauses store so retract / assertz
        // see them just like runtime-asserted clauses do. Without this
        // routing, source-declared facts for a `:- dynamic foo/N.`
        // predicate would be invisible to retract/2 and clause/2.
        if (_dynamicFunctors.Count > 0)
        {
            var keptClauses = new List<Clause>(clauses.Count);
            foreach (var c in clauses)
            {
                if (TryExtractHead(c, out string n, out int a))
                {
                    int fid = FunctorTable.Intern(
                        AtomTable.Intern(n, permanent: true).Id, a);
                    if (_dynamicFunctors.Contains(fid))
                    {
                        GetOrCreateDynamicSlot(fid).Add(c);
                        // ADR-023 priming — a `:- dynamic`/`:- visible`
                        // predicate declared WITH source clauses runs as its
                        // Tier-1 IL snapshot from the first call (evictable on
                        // the first mutation).
                        IlPromotion.MarkPrime(fid);
                        // Chunk 440 — clauses routed here from a named
                        // module (a source-bearing bundle entry, or an
                        // explicit `:- module/1` source) must be rewritten
                        // under that module's context at query setup so
                        // their body calls to module-locals mangle the
                        // same way the module's static clauses do.
                        if (moduleName != DefaultModuleName)
                            _dynamicSeedModule[fid] = moduleName;
                        // Mid-query consult (consult/1 from a live query): the
                        // clause is already in _dynamicClauses (above), so
                        // clause/2 — which reads the live store — sees it in the
                        // SAME query; direct-call dispatch picks it up on the
                        // next query's clean recompile of the predicate.
                        //
                        // Phase 33 — do NOT patch the live dispatch in place at
                        // this site. AppendDynamicClauseIncremental extends the
                        // dynamic predicate's compiled chain, and for a predicate
                        // whose dispatch lives in the *persistent* code region
                        // (< _querySplit — the common case, since Logtalk's
                        // internal `$lgt_*` tables are declared dynamic and
                        // compiled into the persistent buffer) that means writing
                        // into shared bytecode the running query is executing
                        // from. Under a heavy multi-file consult (loading
                        // Logtalk's `random` / `basic_types` libraries) the
                        // resulting chain becomes inconsistent — a dispatch jumps
                        // into the middle of an unrelated instruction ("reserved
                        // invalid opcode / Opcode 0xCF — bytecode corruption").
                        // Deferring to a clean next-query rebuild is both correct
                        // (clause/2 gives same-query visibility; the store is the
                        // source of truth) and the design mature engines use.
                        // Runtime assertz/asserta (the tested in-place path) is a
                        // different call site and is unaffected.
                        // Mid-query consult (consult/1 from a live query): push
                        // the clause into the live dispatch so a later call in a
                        // subsequent sub-query sees it. Logtalk's runtime init
                        // depends on this — each built-in entity's compiled code
                        // is CONSULTED (its `$lgt_current_protocol_` /
                        // `$lgt_current_object_` registrations are generated
                        // dynamic facts), and a later entity's compilation
                        // direct-calls those registrations; without the in-place
                        // extend they are invisible and the load fails
                        // (core_messages: permission_error / unknown protocol).
                        // AppendDynamicClauseIncremental self-guards against a
                        // stale chain (Phase 33 — see DynChainAddressesStale) so
                        // the heavy-load corruption that motivated skipping this
                        // is prevented at the write sites, not by dropping the
                        // extend.
                        if (_liveConsultEngine is { } le)
                            AppendDynamicClauseIncremental(le, fid, c);
                        continue;
                    }
                }
                keptClauses.Add(c);
            }
            clauses = keptClauses;
        }

        // Tabling (chunk 104): a `:- table p/N` predicate's clauses are
        // re-headed to '$tabled$p'/N and a driver clause that routes
        // through '$table_call' is synthesised. Done after the dynamic
        // routing so it only sees the static clauses.
        if (tabledFunctors is not null && tabledFunctors.Count > 0)
            clauses = TransformTabledPredicates(clauses, tabledFunctors, publics);

        // Phase 19+ — implicit_dynamic pre-scan. When the flag is on
        // (the default), walk every clause body for a literal
        // `assertz(Head)` / `asserta(Head)` / `assert(Head)` call and
        // auto-add Head's functor to _dynamicFunctors if it has no
        // static clauses and no other declaration. This mirrors the
        // SWI / SICStus / GNU behaviour where assertz on an undefined
        // predicate creates it as dynamic — but at *consult* time, not
        // at the first runtime assertz, so the linker sees the
        // predicate as dynamic and emits a real trampoline. The
        // runtime EnsureDynamic still gates the case where the
        // assertz target is computed at runtime (the head is a
        // variable bound after consult); for those, the dispatcher
        // would still raise undefined_procedure on the matching
        // call, but the assertz itself no longer raises permission
        // _error.
        if (_flags.ImplicitDynamic)
            CollectImplicitDynamics(clauses, publics);

        // ADR-035 — the predicates the `:- disable_debug.` regions covered. The
        // module name is only settled now (the `:- module` directive may sit
        // anywhere in the file), and it is what decides how a local predicate's
        // name is mangled — so the fids are resolved here rather than inline.
        if (nonDebuggable is not null)
            _nonDebuggableFunctors.UnionWith(
                ResolveNonDebuggableFids(nonDebuggable, moduleName));

        if (moduleDirectiveSeen || moduleName != DefaultModuleName)
        {
            // Explicit module (or a chunk-440 per-file fallback module
            // from a source-bearing bundle entry): replace any previous
            // load of this module.
            var manifest = new ModuleManifest(moduleName);
            manifest.Clauses.AddRange(clauses);
            manifest.PublicFunctors.UnionWith(publics);
            if (pendingDiscontiguous is not null) manifest.DiscontiguousFunctors.UnionWith(pendingDiscontiguous);
            if (pendingMultifile is not null) manifest.MultifileFunctors.UnionWith(pendingMultifile);
            if (pendingModes is not null)
                foreach (var (fid, modes) in pendingModes) manifest.ModeDeclarations[fid] = modes;
            _modules[moduleName] = manifest;
        }
        else
        {
            // Default user module: append. Multiple unrelated consults share
            // a single rolling 'user' module — matches the historic behaviour
            // from before the module system landed.
            var existing = _modules[DefaultModuleName];
            existing.Clauses.AddRange(clauses);
            existing.PublicFunctors.UnionWith(publics);
            if (pendingDiscontiguous is not null) existing.DiscontiguousFunctors.UnionWith(pendingDiscontiguous);
            if (pendingMultifile is not null) existing.MultifileFunctors.UnionWith(pendingMultifile);
            if (pendingModes is not null)
                foreach (var (fid, modes) in pendingModes)
                {
                    // Append, not replace: a later consult declaring more
                    // modes for the same functor adds to what's there.
                    if (existing.ModeDeclarations.TryGetValue(fid, out var prior))
                        prior.AddRange(modes);
                    else
                        existing.ModeDeclarations[fid] = modes;
                }
        }

        // Consulting may have added source clauses for an already-cached
        // dynamic predicate (chunk 68). Drop the cache wholesale — the
        // next query will recompile each dynamic predicate against the
        // updated clause set. Consult is one-shot at engine setup in the
        // common case, so this is amortised away.
        _dynamicPredicateCache.Clear();
        // New static clauses just landed in _modules — drop the head-functor
        // cache so HasPredicate / predicate_property/2 sees them.
        _staticHeadFunctorsCache = null;

        // Mid-query consult (consult/1 from a live query): live-link the
        // just-added predicates into the running query's code space so a
        // later goal — in particular a runtime meta-call — in this query
        // can reach them. Startup consults (no live engine) skip this:
        // their predicates link at the next query setup as always.
        if (_liveConsultEngine is { } liveEng)
        {
            // Dynamics FIRST — a static clause's body may call one, and the
            // static link resolves such calls against the address map the
            // trampolines populate. A `:- dynamic`/`:- multifile` predicate
            // declared in this consult (Logtalk's hook predicates, e.g.
            // message_hook/4, declared then called before any clause is
            // added) needs a live trampoline so the call FAILS rather than
            // existence_errors.
            EnsureLiveDynamicTrampolines(liveEng);
            // Statics next — `clauses` here is the static-only set (the
            // dynamic-head clauses were routed into _dynamicClauses above).
            if (clauses.Count > 0)
            {
                LinkConsultedStaticPredicatesLive(liveEng, clauses, moduleName);
                // Phase 33 — BROADCAST: a suspended outer engine (a nested
                // deferred-init query consulted this file) must also reach
                // the new predicates when it resumes — e.g. Logtalk's
                // arbitrary.lgt compile (outer engine) statically binds to
                // fast_random's tables consulted by an inner engine. Same
                // single-code-space alignment as the dynamic broadcast.
                if (OtherLiveEnginesByTable(liveEng) is { } others)
                    foreach (var other in others)
                    {
                        EnsureLiveDynamicTrampolines(other);
                        LinkConsultedStaticPredicatesLive(other, clauses, moduleName);
                    }
            }
        }

        // The code the debugger's breakpoints were waiting for has just arrived. Bind them
        // BEFORE the initialization goals run — those goals ARE the program, and a breakpoint
        // bound after them is a breakpoint that never fires.
        RebindPendingBreakpoints();

        // `:- initialization(Goal)` goals run now, after the consult has
        // committed, in source order (SWI load-time semantics). A goal that
        // fails or raises prints a warning and loading continues — matching
        // SWI. halt/0-1 (PrologHaltException) propagates: halting from an
        // initialization goal ends the load, exactly as under SWI.
        if (initializationGoals is not null)
        {
            foreach (var g in initializationGoals)
            {
                try
                {
                    LastHaltExitCode = null;
                    bool ok = false;
                    foreach (var sol in QueryAll(g)) { ok = sol.Success; break; }

                    // halt/0-1 does NOT reach us as an exception: QueryAll catches it and
                    // reports the goal as failed, leaving the code behind in
                    // LastHaltExitCode. So a goal that halted looked exactly like one that
                    // failed — the load went on, and the process it was told to end lived
                    // on to read from stdin. Re-raise it here: halting from an
                    // initialization goal ends the load, which is what the paragraph above
                    // has always claimed and what SWI does.
                    if (LastHaltExitCode is int exitCode)
                        throw new PrologHaltException(exitCode);

                    if (!ok)
                        Console.Error.WriteLine(
                            $"Warning: initialization goal failed: {g}");
                }
                catch (PrologHaltException) { throw; }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"Warning: initialization goal raised: {g}: {ex.Message}");
                    if (Environment.GetEnvironmentVariable("SHUMWAY_DEBUG_TRACE") == "1")
                        Console.Error.WriteLine(ex.StackTrace);
                }
            }
        }

        // Phase 33 — RESUME-boundary reconciliation: this consult (and the
        // nested queries its initialization goals spawned) may have mutated
        // dynamic predicates through OTHER engines' views; before control
        // returns to the suspended caller, diff its dispatch view against
        // the store so no ghost clause (missed broadcast patch) survives
        // into its continuation. See ReconcileEngineDynamicView.
        if (_liveConsultEngine is { } resumeEng)
            ReconcileEngineDynamicView(resumeEng);
    }

    /// <summary>Enforces the contiguity rule for clauses inside a single
    /// consulted source (chunk 60). Clauses for the same functor must
    /// be adjacent unless the functor is declared <c>:- discontiguous</c>.
    /// Splitting them is almost always a bug — the discontiguous
    /// declaration is the explicit opt-in that turns the diagnostic
    /// off for predicates that legitimately need scattered
    /// definitions.</summary>
    private static void ValidateContiguity(
        IReadOnlyList<Clause> clauses, HashSet<int>? discontiguous)
    {
        int? currentFid = null;
        var closed = new HashSet<int>();
        foreach (var clause in clauses)
        {
            int fid = HeadFunctorIdOf(clause);
            if (currentFid is null)
            {
                currentFid = fid;
                continue;
            }
            if (fid == currentFid) continue;
            // Functor changed: mark the previous one as closed.
            closed.Add(currentFid.Value);
            currentFid = fid;
            if (closed.Contains(fid)
                && (discontiguous is null || !discontiguous.Contains(fid)))
            {
                var (atomId, arity) = FunctorTable.Lookup(fid);
                string functorName = AtomTable.GetById(atomId)?.Name ?? "?";
                throw new InvalidOperationException(
                    $"Clauses for {functorName}/{arity} are not contiguous. "
                    + $"Either reorder them so they appear together, or add "
                    + $":- discontiguous {functorName}/{arity}. at the top of "
                    + "the source.");
            }
        }
    }

    private static int HeadFunctorIdOf(Clause clause)
    {
        // Rule and DcgRule both encode (head, body) under args[0] /
        // args[1] of their wrapping compound. For DcgRule the
        // contiguity comparison must use the *expanded* arity
        // (Arity + 2 for the diff-list pair) to match what
        // DcgTransform.Apply will produce later in the pipeline —
        // otherwise a file mixing DCG rules and regular clauses of
        // the same name/arity ends up flagged.
        Term head;
        int arityOffset = 0;
        if ((clause.Kind == ClauseKind.Rule || clause.Kind == ClauseKind.DcgRule)
            && clause.Term is CompoundTerm wrap && wrap.Args.Length == 2)
        {
            head = wrap.Args[0];
            if (clause.Kind == ClauseKind.DcgRule) arityOffset = 2;
        }
        else
        {
            head = clause.Term;
        }
        return head switch
        {
            AtomTerm a => FunctorTable.Intern(
                AtomTable.Intern(a.Name, permanent: true).Id, arityOffset),
            CompoundTerm c => FunctorTable.Intern(
                AtomTable.Intern(c.Functor, permanent: true).Id, c.Args.Length + arityOffset),
            _ => throw new InvalidOperationException(
                $"Clause head must be an atom or compound, got {head.GetType().Name}."),
        };
    }

    /// <summary>Matches the shape used by <c>:- discontiguous</c> and
    /// <c>:- multifile</c> — a Name/Arity term or a list of them. Returns
    /// <c>false</c> when the directive functor doesn't match, throws on a
    /// malformed argument.</summary>
    private static bool TryReadFunctorIndicatorDirective(
        Term body, string directiveName, out List<(string Name, int Arity)> specs)
    {
        specs = new List<(string, int)>();
        if (body is not CompoundTerm c || c.Functor != directiveName || c.Args.Length != 1)
            return false;
        Term arg = c.Args[0];
        if (TryReadFunctorSpec(arg, out var single))
        {
            specs.Add(single);
            return true;
        }
        if (TryReadFunctorSpecList(arg, specs))
            return true;
        if (TryReadFunctorSpecConjunction(arg, specs))
            return true;
        throw new InvalidOperationException(
            $"Malformed :- {directiveName} directive (expected Name/Arity, a list of them, "
            + "or a comma-separated sequence).");
    }

    /// <summary>Rewrites the clauses of every <c>:- table</c>d predicate
    /// for semi-naive evaluation. Each clause is classified:
    /// <list type="bullet">
    /// <item>a <em>base</em> clause (no tabled body call) → <c>'$tbase$p'</c>;</item>
    /// <item>a <em>simple recursive</em> clause (exactly one tabled body
    /// literal, as a direct conjunct) → <c>'$trec$p'</c> with that literal
    /// rewritten to a <c>'$tbl_consume'</c> call that reads the producer's
    /// delta;</item>
    /// <item>a <em>complex</em> clause (two-plus tabled literals, or a
    /// tabled call nested in a control construct) → kept verbatim in both
    /// <c>'$tbase$p'</c> and <c>'$trec$p'</c>, so it is re-run every round
    /// (correct, just not differentiated).</item>
    /// </list>
    /// A driver clause <c>p(..) :- '$table_call'(p(..), '$tbase$p'(..),
    /// '$trec$p'(..))</c> is added. <c>p/N</c>, <c>'$tbase$p'/N</c> and
    /// <c>'$trec$p'/N</c> are made public so module mangling — which does
    /// not reach the driver's data-position references — cannot desync
    /// them.</summary>
    internal static List<Clause> TransformTabledPredicates(
        List<Clause> clauses, HashSet<int> tabled, HashSet<int> publics)
    {
        var result = new List<Clause>();
        var baseClauses = new Dictionary<int, List<Clause>>();
        var recClauses = new Dictionary<int, List<Clause>>();
        var present = new List<int>();

        foreach (var c in clauses)
        {
            if (TryExtractHead(c, out string n, out int a))
            {
                int fid = FunctorTable.Intern(
                    AtomTable.Intern(n, permanent: true).Id, a);
                if (tabled.Contains(fid))
                {
                    if (!baseClauses.ContainsKey(fid))
                    {
                        baseClauses[fid] = new List<Clause>();
                        recClauses[fid] = new List<Clause>();
                        present.Add(fid);
                    }
                    TransformTabledClause(c, n, tabled,
                        baseClauses[fid], recClauses[fid]);
                    continue;
                }
            }
            result.Add(c);
        }

        foreach (int fid in present)
        {
            var (atomId, arity) = FunctorTable.Lookup(fid);
            string name = AtomTable.GetById(atomId)!.Name;
            var bs = baseClauses[fid];
            var rs = recClauses[fid];
            if (bs.Count == 0) bs.Add(MakeFailStub("$tbase$" + name, arity));
            if (rs.Count == 0) rs.Add(MakeFailStub("$trec$" + name, arity));
            result.AddRange(bs);
            result.AddRange(rs);
            result.Add(MakeTableDriverClause(name, arity));
            publics.Add(fid);
            publics.Add(FunctorTable.Intern(
                AtomTable.Intern("$tbase$" + name, permanent: true).Id, arity));
            publics.Add(FunctorTable.Intern(
                AtomTable.Intern("$trec$" + name, permanent: true).Id, arity));
        }

        // If any clause body negates a tabled goal, mark the program for
        // well-founded evaluation — '$tbl_dispatch' then routes a
        // top-level tabled call through the alternating fixpoint.
        foreach (var cl in result)
            if (TermMentions(cl.Term, "$tbl_negate"))
            {
                result.Add(Clause.From(new AtomTerm("$wfs_mode")));
                break;
            }
        return result;
    }

    /// <summary>True when any subterm of <paramref name="t"/> is a
    /// compound with the given functor.</summary>
    private static bool TermMentions(Term t, string functor)
    {
        if (t is CompoundTerm c)
        {
            if (c.Functor == functor) return true;
            foreach (var arg in c.Args)
                if (TermMentions(arg, functor)) return true;
        }
        return false;
    }

    /// <summary>Classifies one tabled clause and appends its rewritten
    /// form(s) to the predicate's base / recursive clause lists.</summary>
    private static void TransformTabledClause(
        Clause c, string name, HashSet<int> tabled,
        List<Clause> baseOut, List<Clause> recOut)
    {
        Term head;
        Term? body;
        if (c.Term is CompoundTerm rule && rule.Functor == ":-" && rule.Args.Length == 2)
        {
            head = rule.Args[0];
            body = rule.Args[1];
        }
        else
        {
            head = c.Term;
            body = null;
        }

        var conjuncts = body is null ? new List<Term>() : FlattenConjunction(body);
        // `\+ G` / `not(G)` over a tabled goal cannot be read off a
        // monotone fixpoint — rewrite it to '$tbl_negate'(G), which
        // evaluates G to completion before testing it.
        for (int i = 0; i < conjuncts.Count; i++)
            conjuncts[i] = RewriteNegation(conjuncts[i], tabled);

        int cleanCount = 0, cleanIndex = -1;
        bool hasComplex = false;
        for (int i = 0; i < conjuncts.Count; i++)
        {
            if (IsCleanTabledCall(conjuncts[i], tabled))
            {
                cleanCount++;
                cleanIndex = i;
            }
            else if (ContainsTabledFunctor(conjuncts[i], tabled))
            {
                hasComplex = true;
            }
        }

        Term baseHead = Rehead(head, "$tbase$" + name);
        Term recHead = Rehead(head, "$trec$" + name);

        if (conjuncts.Count == 0)
        {
            baseOut.Add(Clause.From(baseHead));   // a fact
        }
        else if (hasComplex || cleanCount >= 2)
        {
            Term b = RebuildConjunction(conjuncts);
            baseOut.Add(MakeRule(baseHead, b));
            recOut.Add(MakeRule(recHead, b));
        }
        else if (cleanCount == 1)
        {
            conjuncts[cleanIndex] = MakeConsume(conjuncts[cleanIndex]);
            recOut.Add(MakeRule(recHead, RebuildConjunction(conjuncts)));
        }
        else
        {
            baseOut.Add(MakeRule(baseHead, RebuildConjunction(conjuncts)));
        }
    }

    /// <summary>Rewrites a body conjunct <c>\+ G</c> or <c>not(G)</c> whose
    /// negated goal mentions a tabled predicate into <c>'$tbl_negate'(G)</c>;
    /// every other conjunct is returned unchanged.</summary>
    private static Term RewriteNegation(Term conjunct, HashSet<int> tabled)
    {
        if (conjunct is CompoundTerm c && c.Args.Length == 1
            && (c.Functor == "\\+" || c.Functor == "not")
            && ContainsTabledFunctor(c.Args[0], tabled))
            return new CompoundTerm("$tbl_negate", new[] { c.Args[0] });
        return conjunct;
    }

    private static Clause MakeRule(Term head, Term body)
        => Clause.From(new CompoundTerm(":-", new[] { head, body }));

    private static List<Term> FlattenConjunction(Term body)
    {
        var goals = new List<Term>();
        FlattenInto(body, goals);
        return goals;
    }
    private static void FlattenInto(Term t, List<Term> goals)
    {
        if (t is CompoundTerm c && c.Functor == "," && c.Args.Length == 2)
        {
            FlattenInto(c.Args[0], goals);
            FlattenInto(c.Args[1], goals);
        }
        else goals.Add(t);
    }
    private static Term RebuildConjunction(List<Term> goals)
    {
        Term acc = goals[^1];
        for (int i = goals.Count - 2; i >= 0; i--)
            acc = new CompoundTerm(",", new[] { goals[i], acc });
        return acc;
    }

    /// <summary>True when <paramref name="t"/>'s principal functor is a
    /// tabled predicate.</summary>
    private static bool IsTabledFunctor(Term t, HashSet<int> tabled)
    {
        string name;
        int arity;
        if (t is CompoundTerm c) { name = c.Functor; arity = c.Args.Length; }
        else if (t is AtomTerm at) { name = at.Name; arity = 0; }
        else return false;
        return tabled.Contains(FunctorTable.Intern(
            AtomTable.Intern(name, permanent: true).Id, arity));
    }

    /// <summary>True when <paramref name="g"/> is a plain call to a tabled
    /// predicate with no tabled functor buried in its arguments.</summary>
    private static bool IsCleanTabledCall(Term g, HashSet<int> tabled)
    {
        if (!IsTabledFunctor(g, tabled)) return false;
        if (g is CompoundTerm c)
            foreach (var arg in c.Args)
                if (ContainsTabledFunctor(arg, tabled)) return false;
        return true;
    }

    private static bool ContainsTabledFunctor(Term t, HashSet<int> tabled)
    {
        if (IsTabledFunctor(t, tabled)) return true;
        if (t is CompoundTerm c)
            foreach (var arg in c.Args)
                if (ContainsTabledFunctor(arg, tabled)) return true;
        return false;
    }

    /// <summary>Rewrites a tabled body literal <c>q(A..)</c> into
    /// <c>'$tbl_consume'(q(A..), '$tbase$q'(A..), '$trec$q'(A..))</c>.</summary>
    private static Term MakeConsume(Term lit)
    {
        if (lit is CompoundTerm c)
            return new CompoundTerm("$tbl_consume", new[]
            {
                lit,
                new CompoundTerm("$tbase$" + c.Functor, c.Args),
                new CompoundTerm("$trec$" + c.Functor, c.Args),
            });
        var at = (AtomTerm)lit;
        return new CompoundTerm("$tbl_consume", new Term[]
        {
            lit, new AtomTerm("$tbase$" + at.Name), new AtomTerm("$trec$" + at.Name),
        });
    }

    private static Term Rehead(Term head, string newName) => head switch
    {
        CompoundTerm ct => new CompoundTerm(newName, ct.Args),
        _ => new AtomTerm(newName),
    };

    /// <summary>A failing stub <c>name(_..) :- fail</c> — gives an empty
    /// <c>'$tbase$p'</c> / <c>'$trec$p'</c> a defined bytecode home.</summary>
    private static Clause MakeFailStub(string name, int arity)
    {
        Term head;
        if (arity == 0) head = new AtomTerm(name);
        else
        {
            var vars = new Term[arity];
            for (int i = 0; i < arity; i++) vars[i] = new VarTerm("_TblS" + i);
            head = new CompoundTerm(name, vars);
        }
        return MakeRule(head, new AtomTerm("fail"));
    }

    /// <summary>Builds <c>p(V..) :- '$table_call'(p(V..), '$tbase$p'(V..),
    /// '$trec$p'(V..))</c> for a tabled predicate.</summary>
    private static Clause MakeTableDriverClause(string name, int arity)
    {
        Term head, baseRun, recRun;
        if (arity == 0)
        {
            head = new AtomTerm(name);
            baseRun = new AtomTerm("$tbase$" + name);
            recRun = new AtomTerm("$trec$" + name);
        }
        else
        {
            var vars = new Term[arity];
            for (int i = 0; i < arity; i++) vars[i] = new VarTerm("_Tbl" + i);
            head = new CompoundTerm(name, vars);
            baseRun = new CompoundTerm("$tbase$" + name, vars);
            recRun = new CompoundTerm("$trec$" + name, vars);
        }
        Term body = new CompoundTerm("$tbl_dispatch", new[] { head, baseRun, recRun });
        return MakeRule(head, body);
    }

    private static bool TryReadModuleDirective(Term body, out string name) =>
        TryReadModuleDirective(body, out name, out _);

    /// <summary>Recognises <c>:- module(Name)</c> (Shumway's one-arg form) and
    /// the standard ISO/SWI/Scryer two-arg <c>:- module(Name, ExportList)</c>.
    /// For the two-arg form the export list (a list of <c>Name/Arity</c>
    /// predicate indicators — any non-PI entries such as <c>op/3</c> exports are
    /// skipped) is returned so the caller can register the exports as public,
    /// exactly as if each appeared in a <c>:- public</c> directive.</summary>
    private static bool TryReadModuleDirective(
        Term body, out string name, out List<(string Name, int Arity)>? exports)
    {
        exports = null;
        if (body is CompoundTerm m && m.Functor == "module")
        {
            if (m.Args.Length == 1 && m.Args[0] is AtomTerm a1)
            {
                name = a1.Name;
                return true;
            }
            if (m.Args.Length == 2 && m.Args[0] is AtomTerm a2)
            {
                name = a2.Name;
                // Parse the export list leniently: collect every Name/Arity
                // indicator, skipping entries we don't understand (op/3, etc.).
                var ex = new List<(string, int)>();
                Term cursor = m.Args[1];
                while (cursor is CompoundTerm cons && cons.Functor == "."
                    && cons.Args.Length == 2)
                {
                    if (TryReadFunctorSpec(cons.Args[0], out var spec))
                        ex.Add(spec);
                    cursor = cons.Args[1];
                }
                if (ex.Count > 0) exports = ex;
                return true;
            }
        }
        name = "";
        return false;
    }

    private static bool TryReadDynamicDirective(
        Term body, out List<(string Name, int Arity)> specs)
    {
        specs = new List<(string, int)>();
        // `visible` is Arity's spelling for a mutable, exported predicate. We
        // map it to `dynamic`: an Arity `:- visible foo/N.` predicate WITH
        // clauses stays ISO-mutable (assert/retract allowed), but — when it has
        // clauses — also gets a build-time WAM/IL snapshot that runs from the
        // first call and is evicted the instant it is mutated (ADR-023 priming).
        if (body is not CompoundTerm c
            || (c.Functor != "dynamic" && c.Functor != "visible")
            || c.Args.Length != 1)
            return false;

        Term arg = c.Args[0];
        if (TryReadFunctorSpec(arg, out var single))
        {
            specs.Add(single);
            return true;
        }
        if (TryReadFunctorSpecList(arg, specs))
            return true;
        // GNU-style grouped form: :- dynamic a/0, b/1, c/2.
        if (TryReadFunctorSpecConjunction(arg, specs))
            return true;
        throw new InvalidOperationException(
            "Malformed :- dynamic directive (expected Name/Arity, a list of them, "
            + "or a comma-separated sequence).");
    }

    private static bool TryReadPublicDirective(
        Term body, out List<(string Name, int Arity)> publics)
    {
        publics = new List<(string, int)>();
        if (body is not CompoundTerm c || c.Functor != "public" || c.Args.Length != 1)
            return false;

        // A single Name/Arity term, a list of them, or a comma-
        // separated sequence (the GNU grouped form).
        Term arg = c.Args[0];
        if (TryReadFunctorSpec(arg, out var single))
        {
            publics.Add(single);
            return true;
        }
        if (TryReadFunctorSpecList(arg, publics))
            return true;
        if (TryReadFunctorSpecConjunction(arg, publics))
            return true;
        throw new InvalidOperationException(
            "Malformed :- public directive (expected Name/Arity, a list of them, "
            + "or a comma-separated sequence).");
    }

    private static bool TryReadFunctorSpec(Term term, out (string Name, int Arity) spec)
    {
        if (term is CompoundTerm slash && slash.Functor == "/" && slash.Args.Length == 2
            && slash.Args[0] is AtomTerm name)
        {
            Term arityTerm = slash.Args[1];
            // Phase 30 (arity_compat) — Arity annotates directive indicators:
            // `:- public foo/8:far.` / `:- public f/2:system(...)`. With `:`
            // at xfy 200 (tighter than `/` 400) that parses as
            // /(name, :(arity, Annotation)) — accept and IGNORE the
            // annotation. The shape has no ISO meaning, so it is stripped
            // unconditionally rather than gated (a non-Arity program can't
            // reach it with valid syntax).
            if (arityTerm is CompoundTerm colon && colon.Functor == ":"
                && colon.Args.Length == 2)
                arityTerm = colon.Args[0];
            if (arityTerm is IntTerm arity)
            {
                spec = (name.Name, (int)arity.Value);
                return true;
            }
        }
        spec = ("", 0);
        return false;
    }

    private static bool TryReadFunctorSpecList(Term list, List<(string, int)> output)
    {
        Term cursor = list;
        while (cursor is CompoundTerm cons && cons.Functor == "." && cons.Args.Length == 2)
        {
            if (!TryReadFunctorSpec(cons.Args[0], out var spec)) return false;
            output.Add(spec);
            cursor = cons.Args[1];
        }
        return cursor is AtomTerm { Name: "[]" };
    }

    /// <summary>Walks a comma-conjunction tree of <c>Name/Arity</c>
    /// specs. GNU Prolog accepts <c>:- dynamic a/0, b/1, c/2.</c> as
    /// a grouped declaration; this is the corresponding parse —
    /// the body is a right-leaning <c>,/2</c> tree whose leaves are
    /// each <c>Name/Arity</c>. Returns <c>false</c> if any leaf
    /// isn't a well-formed indicator.</summary>
    private static bool TryReadFunctorSpecConjunction(Term term, List<(string, int)> output)
    {
        if (TryReadFunctorSpec(term, out var single))
        {
            output.Add(single);
            return true;
        }
        if (term is CompoundTerm conj && conj.Functor == "," && conj.Args.Length == 2)
        {
            int savedCount = output.Count;
            if (TryReadFunctorSpecConjunction(conj.Args[0], output)
                && TryReadFunctorSpecConjunction(conj.Args[1], output))
                return true;
            output.RemoveRange(savedCount, output.Count - savedCount);
            return false;
        }
        return false;
    }

    /// <summary>Parses and runs a query, returning the first solution if one
    /// exists or a failed <see cref="Solution"/> otherwise. Equivalent to
    /// <c>QueryAll(queryText).FirstOrDefault(failed)</c>.</summary>
    public Solution Query(string queryText)
    {
        foreach (var sol in QueryAll(queryText))
            return sol;
        return new Solution(success: false, bindings: ImmutableDictionary<string, Term>.Empty,
            engine: this);
    }

    /// <summary>Chunk 240 — typed-result query: runs the query and
    /// projects the binding of <paramref name="variableName"/>
    /// through <see cref="FromTerm{T}"/> for every solution. The
    /// natural shape for a query that asks for one specific value
    /// out of each answer (the typical embedding-side
    /// "give me all the X such that p(X)" use case).
    /// <c>foreach (var x in engine.Query&lt;int&gt;("p(X).", "X")) ...</c></summary>
    public IEnumerable<T> Query<T>(string queryText, string variableName)
    {
        ArgumentNullException.ThrowIfNull(queryText);
        ArgumentNullException.ThrowIfNull(variableName);
        foreach (var sol in QueryAll(queryText))
        {
            if (!sol.Bindings.TryGetValue(variableName, out var t))
                throw new InvalidOperationException(
                    $"Query<T> asked for variable '{variableName}' but the query "
                    + $"does not bind it. Bound variables: "
                    + (sol.Bindings.Count == 0
                        ? "(none)"
                        : string.Join(", ", sol.Bindings.Keys)));
            yield return FromTerm<T>(t);
        }
    }

    /// <summary>Chunk 240 — single-variable overload: when the
    /// query has exactly one non-anonymous variable, infer the
    /// name. Useful for the common
    /// <c>engine.Query&lt;int&gt;("between(1, 5, X).")</c> idiom
    /// where naming the variable in C# is just noise.
    ///
    /// <para>Throws when the query has zero variables (a yes/no
    /// query — use <see cref="QueryAll(string)"/>) or more than
    /// one variable (the explicit-name overload disambiguates).</para></summary>
    public IEnumerable<T> Query<T>(string queryText)
    {
        ArgumentNullException.ThrowIfNull(queryText);
        // Parse once to discover the query's variable set, then defer
        // to the explicit-name overload. The Term parse here costs an
        // extra walk, but it's a one-shot setup pass — the iteration
        // dwarfs it for any non-trivial query.
        var queryParser = new Parser(
            new Lexer(queryText, _flags.CharConversionEnabled ? _flags.CharConversion : null),
            _operators, _flags);
        Term queryTerm = queryParser.ReadClauseTerm();
        var vars = new List<string>();
        var seen = new HashSet<string>();
        CollectVariables(queryTerm, vars, seen);
        if (vars.Count == 0)
            throw new InvalidOperationException(
                $"engine.Query<{typeof(T).Name}>(\"{queryText}\") has no variables — "
                + "use QueryAll(string) for boolean queries, or add the variable to "
                + "extract.");
        if (vars.Count > 1)
            throw new InvalidOperationException(
                $"engine.Query<{typeof(T).Name}>(\"{queryText}\") has multiple variables "
                + $"({string.Join(", ", vars)}); use the (queryText, variableName) "
                + "overload to disambiguate.");
        return Query<T>(queryText, vars[0]);
    }

    /// <summary>Chunk 240 — runs the query and returns the first
    /// solution's binding of <paramref name="variableName"/>
    /// projected through <see cref="FromTerm{T}"/>; <c>default</c>
    /// (a null reference / zero value) when the query fails. Drops
    /// the remaining solutions; the engine state is unaffected (the
    /// underlying iterator handles disposal).</summary>
    public T? QueryFirst<T>(string queryText, string variableName)
    {
        foreach (var v in Query<T>(queryText, variableName))
            return v;
        return default;
    }

    /// <summary>Chunk 240 — single-variable overload of
    /// <see cref="QueryFirst{T}(string,string)"/>.</summary>
    public T? QueryFirst<T>(string queryText)
    {
        foreach (var v in Query<T>(queryText))
            return v;
        return default;
    }

    /// <summary>Parses and runs a query, lazily yielding every solution. The
    /// engine state is preserved between yields so the iterator can drive the
    /// interpreter through backtracking on demand.</summary>
    public IEnumerable<Solution> QueryAll(string queryText)
    {
        ArgumentNullException.ThrowIfNull(queryText);
        LastHaltExitCode = null;
        var setup = SetupQuery(queryText);
        return RunIteration(this, setup.Program, setup.VarNames, setup.VarHeapIndices,
            setup.Activation, setup.Interp);
    }

    /// <summary>Theme 2 — a cancellable lazy solution stream. Identical to
    /// <see cref="QueryAll(string)"/> but the supplied
    /// <paramref name="cancellationToken"/> aborts a long-running search: the
    /// interpreter observes the request the next time the heap GC watermark is
    /// crossed (so the common per-goal path pays nothing — a heap-bounded loop
    /// such as <c>repeat, fail</c> is not cancellable) and throws
    /// <see cref="OperationCanceledException"/> (NOT a Prolog ball — a
    /// surrounding <c>catch/3</c> never intercepts it). Still synchronous: it
    /// runs on the calling thread. Use <see cref="QueryAsync"/> to run off-thread.</summary>
    public IEnumerable<Solution> QueryAll(string queryText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queryText);
        LastHaltExitCode = null;
        var setup = SetupQuery(queryText);
        return RunIterationCancellable(setup, cancellationToken);
    }

    private IEnumerable<Solution> RunIterationCancellable(
        (Shumway.Core.ProgramView Program, List<string> VarNames, int[] VarHeapIndices,
         Activation Activation, BytecodeInterpreter Interp) setup,
        CancellationToken cancellationToken)
    {
        using var reg = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static e => ((Activation)e!).RequestCancellation(), setup.Activation)
            : default;
        foreach (var sol in RunIteration(this, setup.Program, setup.VarNames,
                     setup.VarHeapIndices, setup.Activation, setup.Interp))
            yield return sol;
    }

    /// <summary>Theme 2 — an asynchronous, cancellable solution stream. Drives
    /// the (synchronous, CPU-bound) search on a thread-pool thread so the
    /// caller's thread is free between solutions, and surfaces results via
    /// <c>await foreach</c>. Cancellation works as in
    /// <see cref="QueryAll(string, CancellationToken)"/> — the engine aborts at
    /// the next heap GC watermark crossing. One query at a time per engine; pair
    /// with <see cref="EnginePool"/> for concurrency.</summary>
    public async IAsyncEnumerable<Solution> QueryAsync(
        string queryText,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queryText);
        LastHaltExitCode = null;
        var setup = SetupQuery(queryText);
        using var reg = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static e => ((Activation)e!).RequestCancellation(), setup.Activation)
            : default;
        using var iter = RunIteration(this, setup.Program, setup.VarNames,
            setup.VarHeapIndices, setup.Activation, setup.Interp).GetEnumerator();
        while (true)
        {
            // Each MoveNext runs one Run/Backtrack step off the calling thread.
            // The engine is thread-agile and the steps are awaited (never
            // overlapping), so a different pool thread per step is sound.
            bool has = await Task.Run(() => iter.MoveNext(), cancellationToken).ConfigureAwait(false);
            if (!has) break;
            yield return iter.Current;
        }
    }

    private (Shumway.Core.ProgramView Program,
             List<string> VarNames,
             int[] VarHeapIndices,
             Activation Activation,
             BytecodeInterpreter Interp) SetupQuery(string queryText)
    {
        var queryParser = new Parser(
            new Lexer(queryText, _flags.CharConversionEnabled ? _flags.CharConversion : null),
            _operators, _flags);
        Term queryTerm = queryParser.ReadClauseTerm();
        return SetupQueryFromTerm(queryTerm);
    }

    /// <summary>Parses <paramref name="queryText"/> as a goal and returns
    /// the parsed AST plus the list of distinct named variables in order
    /// of first occurrence. Lets a top-level synthesise a wrapped goal
    /// (e.g. appending <c>copy_term/3</c> to extract residual
    /// constraints) over the same variables before calling
    /// <see cref="QueryAll(Term)"/>.</summary>
    public (Term Goal, IReadOnlyList<string> VarNames) ParseGoal(string queryText)
    {
        ArgumentNullException.ThrowIfNull(queryText);
        var queryParser = new Parser(
            new Lexer(queryText, _flags.CharConversionEnabled ? _flags.CharConversion : null),
            _operators, _flags);
        Term queryTerm = queryParser.ReadClauseTerm();
        var names = new List<string>();
        CollectVariables(queryTerm, names, new HashSet<string>());
        return (queryTerm, names);
    }

    /// <summary>Shared workhorse used by both the string-parsing
    /// <see cref="SetupQuery(string)"/> and the Term-level
    /// <see cref="QueryAll(Term)"/>: gathers every module's clauses through
    /// DCG / meta / module-mangle transforms, wraps the goal in a synthetic
    /// clause in the user module, compiles + links, primes X[0..n-1] with
    /// fresh heap unbounds, and hands the lot back to the caller's
    /// run/backtrack iterator.</summary>
    private (Shumway.Core.ProgramView Program,
             List<string> VarNames,
             int[] VarHeapIndices,
             Activation Activation,
             BytecodeInterpreter Interp) SetupQueryFromTerm(Term queryTerm)
    {
        // ADR-035 — serialized against the debug session's own thread. A breakpoint can
        // arrive while the engine is IDLE (F9 at the prompt), and the session's idle
        // watcher applies it from its own thread — which raced this method's table
        // rebuild the moment a query started at the same instant, and a Dictionary read
        // concurrent with a write throws ConcurrentOperationsNotSupported (seen live: F9
        // followed immediately by a query). Uncontended cost is a fenced check; the
        // watcher takes the same gate in AddBreakpoint/RemoveBreakpoint/ClearBreakpoints.
        lock (_debugArmGate)
            return SetupQueryFromTermUnderGate(queryTerm);
    }

    /// <summary>Cross-thread gate for the debug-table surface: query setup rebuilds the
    /// per-query tables on the engine's thread, and the session's idle watcher arms
    /// breakpoints from its own. See <see cref="SetupQueryFromTerm"/>.</summary>
    private readonly object _debugArmGate = new();

    private (Shumway.Core.ProgramView Program,
             List<string> VarNames,
             int[] VarHeapIndices,
             Activation Activation,
             BytecodeInterpreter Interp) SetupQueryFromTermUnderGate(Term queryTerm)
    {
        // Phase 12 chunk 158: auto-compaction. When the accumulated
        // mutation count crosses the watermark, invalidate the
        // persistent buffer here at query setup (the safe point —
        // no in-flight choice points hold addresses into it). The
        // rebuild that follows below picks up the trim automatically.
        //
        // ADR-035 D5 — except during a DEBUG EVALUATION (an Immediate-window goal, a
        // breakpoint condition): that nested query's setup is NOT a safe point — the outer
        // query is suspended mid-flight and everything it holds points into the current
        // buffers. Compaction is paused, not skipped: the counter keeps accumulating and
        // the next real query's setup does the deferred work.
        if (_debugEvalDepth == 0 && _persistentMutationsSinceCompact >= CompactWatermark)
            CompactDynamicCodeBuffer();

        // Phase 33 — live-linked-consult forward-reference sites now live on
        // the per-buffer chain table, so they follow their buffer's lifetime
        // automatically (a rebuilt buffer starts a fresh table).

        var varNames = new List<string>();
        var seen = new HashSet<string>();
        CollectVariables(queryTerm, varNames, seen);

        const string queryFunctor = "__query__";
        Term head = varNames.Count == 0
            ? new AtomTerm(queryFunctor)
            : new CompoundTerm(
                queryFunctor,
                varNames.Select(n => (Term)new VarTerm(n)).ToArray());
        Term clauseTerm = new CompoundTerm(":-", new[] { head, queryTerm });
        var syntheticClause = new Clause(ClauseKind.Rule, clauseTerm, queryTerm.Position);

        // What the user typed, kept for the call stack's query frame (see AddFrame). Only
        // under a debug session: this renders a term, and nobody else is looking. A host that
        // wrapped the goal says so with QueryLabel — rendering ITS wrapper back would name
        // the frame after machinery the user never wrote.
        CurrentQueryText = DebugSession is null
            ? null
            : QueryLabel ?? AstTermRenderer.Render(queryTerm, 999, _operators);

        // Chunk 417 — the Phase-19+ implicit_dynamic pre-scan is NO
        // LONGER applied to the query body. Pre-declaring an
        // assertz-target made it observable as an EMPTY dynamic
        // predicate from the query's start, so a goal sequenced BEFORE
        // the assertz in the same query (`catch(call(zzz(1)), _, true),
        // assertz(zzz(1))`) saw it fail instead of raising
        // existence_error — diverging from ISO/SWI and from the same
        // goal without the later assertz. The REPL pattern the pre-scan
        // existed for (`?- assertz(pepe), call(pepe).`) is covered by
        // the chunk-207 runtime path: assertz auto-promotes and
        // materialises a trampoline mid-query; a direct call site's
        // unresolved sentinel re-resolves through
        // ResolveTargetMaybeAutoPromoted and a meta-call probes the
        // live CurrentFunctorAddresses.

        // Validate public uniqueness across modules. The check raises before
        // any compilation so the error message points squarely at the user's
        // module declarations rather than at the bytecode that wouldn't link.
        ValidatePublicUniqueness();

        // Apply DCG → clause and meta-call (\+ / not) transforms per module,
        // then mangle local functors so each module ends up with its own
        // private namespace. The synthetic query clause is transformed and
        // rewritten under the user module's context but kept out of that
        // module's local set — its head functor stays bare so the launcher
        // can call it by name.
        List<Clause> allRewritten;
        HashSet<int>? userLocalsCache;
        // Chunk 440 — every module's locals set, for the per-fid dynamic-
        // clause rewrite below (a dynamic clause attributed to module M
        // rewrites under M's context, not user's).
        Dictionary<string, HashSet<int>>? moduleLocalsCache;
        // Chunk 430 — head functor ids of every clause in allRewritten
        // (static + dynamic program). Maintained alongside the clause
        // list so the stub emission and the cacheableFunctors snapshot
        // below stop re-interning every clause head per query.
        HashSet<int> rewrittenHeadFids;

        // Chunk 74 — mode specialization. Built once per query setup; the
        // transform appends an implicit cut to every clause of a
        // predicate whose declared modes are all deterministic. Applied
        // after DCG / meta / phrase expansion so it conjoins onto the
        // final plain-rule body.
        var modeTable = Modes;

        if (_staticRewriteGen == _derivationGen && _staticRewriteClauses is not null)
        {
            // Chunk 430 — the static program hasn't changed since the
            // last setup: reuse the transformed + rewritten clause list.
            // Copy it, because stubs and the synthetic query clause are
            // appended below; the Clause objects themselves are immutable
            // ASTs and safe to share across queries.
            allRewritten = new List<Clause>(_staticRewriteClauses);
            userLocalsCache = _staticRewriteUserLocals;
            moduleLocalsCache = _staticRewriteModuleLocals;
            rewrittenHeadFids = new HashSet<int>(_staticRewriteHeadFids!);
        }
        else
        {
            // Phase 33 — the derivation is being REGENERATED: MetaTransform
            // helper names ('$disj_N', '$catchgoal_N', ...) come from the
            // global NextMetaHelperId counter, so the fresh ASTs reference
            // fresh helper ids. Any COMPILED artifact from the previous
            // derivation still calls the OLD helper ids — helpers that no
            // longer exist in the new clause set, so the link bakes an
            // undefined sentinel and the call raises existence_error
            // ('$disj_N'/K — the long-standing Logtalk '$disj_95' gap; hit
            // reliably by lgtunit's runtime send-cache asserta, which bumps
            // the derivation between queries). Nothing compiled may outlive
            // the derivation that produced its call sites:
            //  * the per-predicate bytecode caches, and
            //  * the linked STATIC REGION (_staticLink) — a dynamic clause
            //    recompiled under the new derivation calls new '$disj_N'
            //    helpers, and those helper predicates are STATIC: they only
            //    reach the code space through a fresh static link. Reusing
            //    the old region would silently drop them (observed as
            //    existence_error('$disj_N'/K) on the first call after a
            //    runtime assert bumped the derivation).
            _staticPredicateCache.Clear();
            _dynamicPredicateCache.Clear();
            _skipCompileMergedCache = null;
            _staticLink = null;
            allRewritten = new List<Clause>();
            userLocalsCache = null;
            moduleLocalsCache = new Dictionary<string, HashSet<int>>();
            foreach (var (name, manifest) in _modules)
            {
                // ADR-035 — the meta-wrapper unfold (below) erases the call to a
                // user control wrapper (ifthenelse/3, ifthen/2), inlining its body as
                // raw ->/;/\+ at the CALLER's position. Under a debug session that is
                // doubly wrong: the wrapper's own clauses take no stop site (nothing
                // ever calls them), so a breakpoint in ifthenelse/3 never binds; and
                // the caller sprouts anonymous control-construct frames (";/2", ",")
                // at its own line instead of a clean step-into. So for a DEBUGGABLE
                // module we keep the wrapper as a real predicate. Opaque modules
                // (prelude, :- disable_debug) run without stop sites anyway, so they
                // still get the optimization.
                bool opaqueModule = _nonDebuggableModules.Contains(name);
                // Chunk 407 — module-local meta-wrapper unfold (ifthen/2-style user
                // control wrappers called with statically-known goals become inline
                // if-then-else, eliminating the goal-term build + wrapper frame +
                // runtime meta-dispatch). Runs BEFORE the pipeline so MetaTransform
                // lowers the inserted control constructs. manifest.Clauses is the
                // STATIC clause set (dynamic-head clauses were routed to
                // _dynamicClauses), so a detected wrapper is immutable by invariant.
                var unfolded = (_flags.DebugCodegen && !opaqueModule)
                    ? manifest.Clauses
                    : MetaWrapperUnfold.Apply(manifest.Clauses);
                var transformed = ClausePipeline.Apply(unfolded, modeTable, inlineIte: EnableInlineIte, helperIdProvider: NextMetaHelperId);

                var locals = ComputeLocalFunctors(transformed, manifest.PublicFunctors);
                // Chunk 209: fold in the bare local fids contributed by a
                // bundled (precompiled) version of this module — those
                // predicates aren't in manifest.Clauses, so the line above
                // can't see them.
                if (_precompiledModuleLocals.TryGetValue(name, out var bundleLocals))
                    locals.UnionWith(bundleLocals);
                if (name == DefaultModuleName) userLocalsCache = locals;
                moduleLocalsCache[name] = locals;

                var ctx = new ModuleRewrite.Context(name, locals, _dynamicFunctors);
                // ADR-035 — a library's HELPERS are library code too. MetaTransform
                // lowers control constructs into generated predicates ('$call_conj' and
                // friends), which are not in manifest.Clauses and so cannot be marked at
                // consult time — but they are right here, in `transformed`, and they
                // carry the library's source positions. Left debuggable they would take
                // stop sites at the library's line numbers and attribute them to the
                // user's file. Compiling a module is what makes its predicates; if the
                // module is not debuggable, neither is anything it made.
                foreach (var clause in transformed)
                {
                    var rewritten = ModuleRewrite.Rewrite(clause, ctx);
                    allRewritten.Add(rewritten);
                    if (opaqueModule && TryReadClauseHead(rewritten, out var spec))
                        _nonDebuggableFunctors.Add(FunctorTable.Intern(
                            AtomTable.Intern(spec.Name, permanent: true).Id, spec.Arity));
                }
            }
            rewrittenHeadFids = new HashSet<int>();
            foreach (var c in allRewritten)
                rewrittenHeadFids.Add(HeadFunctorIdOf(c));
            // Chunk 430 — snapshot for the next setup (the transform chain
            // is a pure function of the consulted program; every input
            // mutation bumps _derivationGen).
            _staticRewriteClauses = new List<Clause>(allRewritten);
            _staticRewriteUserLocals = userLocalsCache;
            _staticRewriteModuleLocals = moduleLocalsCache;
            _staticRewriteHeadFids = new HashSet<int>(rewrittenHeadFids);
            _staticRewriteGen = _derivationGen;
        }

        // Dynamic clauses asserted at runtime (or declared
        // `:- dynamic foo/N.` in source, then routed into
        // _dynamicClauses at consult). The dynamic predicate itself
        // sits in the flat global namespace (no module prefix on its
        // head), but a CALL inside the dynamic clause's body to a
        // user-module-local predicate (e.g. `helper/0` from
        // `main :- helper.` when `main` is dynamic and `helper` is a
        // plain user-module clause) needs the same mangling the rest
        // of the user module is getting — otherwise the call site
        // stays bare while the target was mangled to `user$helper/0`
        // and dispatch fails with existence_error.
        //
        // Pass the user-module locals into the rewrite so body calls
        // resolve through them. The user module is the right default:
        // source-declared dynamic clauses from modules without a
        // `:- module/1` directive land in user, and runtime-asserted
        // clauses have no inherent module so user is the conventional
        // home. Multi-module hosts with per-module dynamic-clause
        // namespacing are a more invasive change parked for later.
        if (_dynamicClauses.Count > 0)
        {
            // Chunk 430 — per-functor transform cache. A functor's entry
            // is dropped by InvalidateDynamicCache when its clause list
            // mutates; the whole table is dropped when the derivation
            // generation moves (the dynamic rewrite context's inputs —
            // user locals, dynamic-functor set, mode table — may have
            // changed). So a query after N asserts re-transforms only
            // the asserted functors, not every dynamic predicate.
            if (_dynamicRewriteGen != _derivationGen)
            {
                _dynamicRewriteCache.Clear();
                _dynamicRewriteGen = _derivationGen;
            }
            var dynCtx = new ModuleRewrite.Context(
                DefaultModuleName,
                userLocalsCache ?? new HashSet<int>(),
                _dynamicFunctors);
            // Chunk 440 — per-module contexts for dynamic predicates whose
            // clauses came from a named module (bundle seeds / source-
            // bearing entries). Built lazily; everything unattributed
            // keeps the user context above.
            Dictionary<string, ModuleRewrite.Context>? namedDynCtx = null;
            foreach (var (fid, clauses) in _dynamicClauses)
            {
                if (clauses.Count == 0) continue;
                if (!_dynamicRewriteCache.TryGetValue(fid, out var entry))
                {
                    var fidCtx = dynCtx;
                    if (_dynamicSeedModule.TryGetValue(fid, out var seedModule))
                    {
                        namedDynCtx ??= new Dictionary<string, ModuleRewrite.Context>();
                        if (!namedDynCtx.TryGetValue(seedModule, out fidCtx))
                        {
                            HashSet<int>? seedLocals = null;
                            moduleLocalsCache?.TryGetValue(seedModule, out seedLocals);
                            fidCtx = new ModuleRewrite.Context(
                                seedModule,
                                seedLocals ?? new HashSet<int>(),
                                _dynamicFunctors);
                            namedDynCtx[seedModule] = fidCtx;
                        }
                    }
                    var transformed = ClausePipeline.Apply(clauses, modeTable, inlineIte: EnableInlineIte, helperIdProvider: NextMetaHelperId);
                    var rewritten = new List<Clause>(transformed.Count);
                    foreach (var clause in transformed)
                        rewritten.Add(ModuleRewrite.Rewrite(clause, fidCtx));
                    // Head fids include any MetaTransform helper clauses'
                    // heads, mirroring what the per-clause HeadFunctorIdOf
                    // walk over allRewritten used to collect.
                    var headFids = new List<int>(rewritten.Count);
                    foreach (var c in rewritten)
                        headFids.Add(HeadFunctorIdOf(c));
                    entry = (rewritten, headFids);
                    _dynamicRewriteCache[fid] = entry;
                }
                allRewritten.AddRange(entry.Clauses);
                foreach (int f in entry.HeadFids)
                    rewrittenHeadFids.Add(f);
            }
        }

        // Stub clauses for declared-but-empty dynamic functors. Without
        // these, calls to a dynamic predicate that's been declared but
        // never assertz'd would fail at link time with an unresolved-call
        // error. The stub always fails — its purpose is just to give the
        // predicate a valid bytecode home. Chunk 430: the precomputed
        // head-fid set replaces the per-query re-intern of every clause
        // head; stub fids are added to it as they're emitted.
        EmitEmptyDynamicStubs(allRewritten, queryTerm.Position, rewrittenHeadFids);

        // Snapshot the functor ids of every clause that exists *before*
        // the synthetic query clause is added — the static + dynamic
        // program. Only these are eligible for the chunk-82 static cache:
        // the __query__ clause, and any auxiliary predicate a transform
        // or the compiler derives from a query's control constructs, are
        // query-specific — caching them would let one query's goal leak
        // into the next. (Chunk 430 — rewrittenHeadFids is exactly the
        // head fids of allRewritten at this point, stubs included.)
        var cacheableFunctors = rewrittenHeadFids;

        // Synthetic query clause — rewrite in the user module's context, but
        // with userLocalsCache (which doesn't include __query__) so the
        // head functor remains bare. Phase 33 — the stub's synthesized helpers
        // use the reserved `$q` namespace: they are rewritten under the SAME
        // user-module mangling as the consulted clauses' helpers, so without
        // the prefix a stub `$disj_1` collides with a consulted `$disj_1`
        // (the helper-name-collision latent bug). `$q` names are reused
        // query-to-query, keeping the atom space bounded.
        {
            var prevPrefix = Shumway.Compiler.Parsing.MetaTransform.HelperPrefix;
            Shumway.Compiler.Parsing.MetaTransform.HelperPrefix = "$q";
            List<Clause> queryTransformed;
            try
            {
                queryTransformed = PhraseTransform.Apply(
                    MetaTransform.Apply(
                        DcgTransform.Apply(new[] { syntheticClause })));
            }
            finally
            {
                Shumway.Compiler.Parsing.MetaTransform.HelperPrefix = prevPrefix;
            }
            var ctx = new ModuleRewrite.Context(
                DefaultModuleName,
                userLocalsCache ?? new HashSet<int>(),
                _dynamicFunctors);
            foreach (var clause in queryTransformed)
                allRewritten.Add(ModuleRewrite.Rewrite(clause, ctx));
        }

        // JIT indexing (chunk 75): a dynamic predicate compiles
        // unindexed until its runtime call count crosses the JIT
        // threshold. A cold-but-now-hot predicate (or vice versa) has
        // a stale cached compile at the wrong indexing level — drop it
        // so ModuleCompiler rebuilds it. The unindexed set then names
        // every dynamic functor still below the threshold.
        var unindexedFunctors = new HashSet<int>();
        bool anyHotnessFlip = false;
        foreach (int fid in _dynamicFunctors)
        {
            if (_jitIndexProfile.HotnessChangedSinceCompile(fid))
            {
                // Chunk 430 — route through the drop helper so the merged
                // skip-compile cache stays in step.
                DropDynamicPredicateCacheEntry(fid);
                anyHotnessFlip = true;
            }
            if (!_jitIndexProfile.IsHot(fid))
                unindexedFunctors.Add(fid);
        }
        foreach (int fid in _dynamicClauses.Keys)
        {
            if (_jitIndexProfile.HotnessChangedSinceCompile(fid))
            {
                DropDynamicPredicateCacheEntry(fid);   // chunk 430
                anyHotnessFlip = true;
            }
            if (!_jitIndexProfile.IsHot(fid))
                unindexedFunctors.Add(fid);
        }
        // Chunk 154: a cold→hot transition needs the persistent buffer
        // rebuilt so the JIT-promoted indexed compilation actually
        // takes effect at runtime — without this the cache holds the
        // indexed form but the live dispatch still runs the chain
        // emitted at predicate-cold time.
        if (anyHotnessFlip) InvalidatePersistent();

        // Skip-compile cache. Two contributors live here:
        //   - Bundle skip-compile (chunk 55): populated by LoadBundle from
        //     a bundle's compiled bytecode blob.
        //   - Dynamic predicate cache (chunk 68): populated lazily by the
        //     query-setup path itself; invalidated on every assertz /
        //     asserta / retract / abolish that touches the functor.
        // ModuleCompiler reuses any cached predicate whose bytecode doesn't
        // reference per-module literal pools.
        // Chunk 430 — the three-way merge is maintained incrementally
        // across queries instead of being re-copied per query: built here
        // on demand, nulled wherever _staticPredicateCache is cleared,
        // kept in step with every dynamic-cache add / remove
        // (DropDynamicPredicateCacheEntry) and with the two populate
        // loops below. Merge precedence unchanged: bundle precompiled
        // (chunk 55), static (chunk 82), then dynamic (chunk 68) —
        // dynamic last so a predicate that turned dynamic wins over a
        // stale static entry (a consult clears the static cache anyway).
        var mergedSkip = _skipCompileMergedCache;
        if (mergedSkip is null)
        {
            mergedSkip = new Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>(
                _precompiledClauseCache);
            foreach (var (fid, pred) in _staticPredicateCache)
                mergedSkip[fid] = pred;
            foreach (var (fid, pred) in _dynamicPredicateCache)
                mergedSkip[fid] = pred;
            _skipCompileMergedCache = mergedSkip;
        }
        IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate>? skipCompileCache =
            mergedSkip.Count == 0 ? null : mergedSkip;
        // Pre-compute the fail-stub address — it sits at the end of the
        // launcher prefix, at offset Call(9) + Halt(1) = 10. We need it
        // available to the compiler so dynamic predicates emit their
        // last-clause chain instruction with the absolute target.
        int failStubAddr =
            OpcodeTable.Get(Opcode.Call).Size + OpcodeTable.Get(Opcode.Halt).Size;
        var module = new ModuleCompiler
        {
            EmitDebugInfo = _flags.EmitDebugInfo,
            DebugCodegen = _flags.DebugCodegen,               // ADR-035
            DebugFileId = _debugFileId,                      // ADR-035
            NonDebuggableFunctors = _nonDebuggableFunctors,  // ADR-035
            ElideRedundantCuts = _flags.ElideRedundantCuts,   // ADR-030
        }.Compile(
            allRewritten, skipCompileCache, unindexedFunctors, _literalPools,
            dynamicFunctors: _dynamicFunctors, failStubAddr: failStubAddr);

        // Populate the dynamic cache with any newly-compiled dynamic
        // predicate whose bytecode is safe to reuse next query (no
        // pool-specific literal ids). A predicate is "dynamic" iff its
        // functor is in _dynamicFunctors — whether its clauses live in
        // _modules (source-declared `:- dynamic foo/N.` plus inline
        // facts) or _dynamicClauses (runtime assertz / asserta), both
        // contribute to the same predicate. Cached entries are kept
        // until the next assertz / retract / abolish invalidates them.
        if (_dynamicFunctors.Count > 0)
        {
            foreach (var pred in module.Predicates)
            {
                if (!_dynamicFunctors.Contains(pred.FunctorId)) continue;
                // Snapshot the JIT-indexing decision this compile used so
                // a later query can detect a cold→hot flip.
                _jitIndexProfile.RecordCompileDecision(
                    pred.FunctorId, _jitIndexProfile.IsHot(pred.FunctorId));
                if (_dynamicPredicateCache.ContainsKey(pred.FunctorId)) continue;
                if (Shumway.Compiler.Wam.ModuleCompiler.IsCachedPredicateReusable(pred))
                {
                    _dynamicPredicateCache[pred.FunctorId] = pred;
                    // Chunk 430 — mirror into the merged skip-compile
                    // cache (dynamic has top precedence in the merge).
                    if (_skipCompileMergedCache is not null)
                        _skipCompileMergedCache[pred.FunctorId] = pred;
                }
            }
        }

        var launcher = new BytecodeEmitter();
        int callPos = launcher.Position;
        launcher.EmitCall(targetAddress: 0, numLivePermanents: 0);
        launcher.EmitHalt();
        // ADR-015 chunk C step 4: a fail-stub at a known offset in the
        // prefix. Dynamic predicates' last-clause chain instructions point
        // here via `retry_me_else <fail-stub>` (instead of trust_me) so a
        // future assertz can patch the operand in place. retry_me_else
        // does not remove the CP, so the stub itself runs trust_me first
        // to pop the dynamic predicate's chain CP — otherwise backtracking
        // would loop right back to this fail-stub forever. Then
        // call_builtin fail/0 returns false and the interpreter resumes
        // backtracking at whatever caller-side CP survives.
        // The compiler was already told this address (Compile call above);
        // assert the launcher's position agrees.
        if (launcher.Position != failStubAddr)
            throw new InvalidOperationException(
                $"launcher position {launcher.Position} != pre-computed fail-stub addr {failStubAddr}");
        launcher.EmitTrustMe();
        int failFunctorId = FunctorTable.Intern(
            AtomTable.Intern("fail", permanent: true).Id, 0);
        if (!Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(
            failFunctorId, out int failBuiltinId))
            throw new InvalidOperationException(
                "fail/0 builtin must be registered for ADR-015 dynamic dispatch.");
        launcher.EmitCallBuiltin(failBuiltinId, numLivePermanents: 0);
        byte[] prefix = launcher.ToBytes();

        // --- ADR-015 chunk B + chunk 151b: persistent code space -------
        // Partition the compiled predicates into three regions:
        //   * static  — cacheable + non-dynamic, linked once.
        //   * dynamic — cacheable + dynamic, linked once into the
        //     persistent buffer; mutated in place by chunk-120
        //     assertz / retract / abolish across queries.
        //   * query   — non-cacheable (the synthetic __query__ clause
        //     plus catch/disjunction/negation helpers). Linked per
        //     query at a high-address overlay so persistent can grow
        //     mid-query without colliding with query addresses.
        var staticPreds = new List<Shumway.Compiler.Wam.CompiledPredicate>();
        var dynamicPreds = new List<Shumway.Compiler.Wam.CompiledPredicate>();
        var queryPreds = new List<Shumway.Compiler.Wam.CompiledPredicate>();
        var addedFids = new HashSet<int>();
        foreach (var pred in module.Predicates)
        {
            bool isCacheable = cacheableFunctors.Contains(pred.FunctorId);
            bool isDynamic = _dynamicFunctors.Contains(pred.FunctorId);
            if (isCacheable && !isDynamic) staticPreds.Add(pred);
            else if (isCacheable && isDynamic) dynamicPreds.Add(pred);
            else queryPreds.Add(pred);
            addedFids.Add(pred.FunctorId);
        }
        // Chunk 178: source-less bundle predicates are already
        // compiled (LoadEntryFromBytecode populated
        // _precompiledStaticPredicates). Append them to the static
        // region — they bypassed the AST → ModuleCompiler pipeline
        // entirely, and their bytecode is byte-identical to what
        // we'd have produced from source. Any predicate id that
        // also appeared in module.Predicates above (e.g. a later
        // source-bearing consult of the same functor) wins by
        // staying in module.Predicates and we skip the precompiled
        // copy so we don't add the same id twice to the linker.
        foreach (var (fid, pred) in _precompiledStaticPredicates)
        {
            if (!addedFids.Add(fid)) continue;
            bool isDynamic = _dynamicFunctors.Contains(fid);
            if (!isDynamic) staticPreds.Add(pred);
            else dynamicPreds.Add(pred);
        }

        // The static region links once at a fixed load offset (the prefix
        // length never varies) and is reused until the static program
        // changes — ConsultString / a bundle load null _staticLink.
        var staticLink = _staticLink
            ?? (_staticLink = GetOrLinkStatic(staticPreds, prefix.Length));

        // Dynamic region: linked once into the persistent buffer.
        // Mid-query assertz extends in place; only a change to the
        // dynamic-functor set (abolish, consult) invalidates this.
        bool builtPersistentNow = _persistentProgram is null || _dynamicLink is null;
        if (builtPersistentNow)
        {
            int dynamicLoadOffset = prefix.Length + staticLink.Bytecode.Length;
            _dynamicLink = new Linker().Link(
                dynamicPreds,
                loadOffset: dynamicLoadOffset,
                externalSymbols: staticLink.Addresses,
                switchTableIdBase: staticLink.SwitchTables.Count);
            _persistentLength =
                prefix.Length + staticLink.Bytecode.Length + _dynamicLink.Bytecode.Length;
            // Over-allocate so capacity-doubling AppendCode appends
            // cheaply mid-query without forcing immediate realloc.
            int initialCapacity = Math.Max(_persistentLength * 2, 1024);
            _persistentProgram = new byte[initialCapacity];
            Array.Copy(prefix, _persistentProgram, prefix.Length);
            Array.Copy(staticLink.Bytecode, 0, _persistentProgram,
                prefix.Length, staticLink.Bytecode.Length);
            Array.Copy(_dynamicLink.Bytecode, 0, _persistentProgram,
                dynamicLoadOffset, _dynamicLink.Bytecode.Length);
            // The static→dynamic unresolved sites get patched in
            // _persistentProgram with the dynamic region's freshly
            // assigned addresses.
            foreach (var (offset, fid) in staticLink.UnresolvedSites)
                if (_dynamicLink.Addresses.TryGetValue(fid, out int dynAddr))
                    BytecodeIO.WriteInt32(_persistentProgram!, prefix.Length + offset + 1, dynAddr);
        }
        // Chunk 151b: pick the per-query overlay's start address with
        // enough headroom over the persistent length for mid-query
        // assertz extensions (typically far less than 64 MB).
        _querySplit = _persistentLength + PersistentToQueryGap;

        // Build the merged external-symbols table for the query
        // linker — it resolves calls into both the static and
        // dynamic regions of the persistent buffer. Chunk 430: cached
        // alongside the persistent link itself (the LinkResult address
        // maps are immutable), together with the bare-alias overlay and
        // the merged predicates-by-address map — rebuilding the three
        // per query re-copied two large dictionaries and re-ran the
        // alias loop's per-functor string work for no observable change
        // while the persistent regions are reused.
        if (builtPersistentNow || _persistentAddressesCache is null)
        {
            var pa = new Dictionary<int, int>(staticLink.Addresses);
            foreach (var (fid, a) in _dynamicLink!.Addresses) pa[fid] = a;
            _persistentAddressesCache = pa;

            // Runtime call/1 (chunk 86) dispatches a goal by its bare
            // functor, but a module-local predicate is linked under its
            // mangled "module$name" functor. Pre-compute the persistent
            // regions' bare-functor aliases once per rebuild; the
            // per-query loop below only has to alias the (tiny) query
            // region. Module set changes always invalidate the
            // persistent regions, so the _modules guard inside stays
            // consistent with this cache's lifetime.
            var baseMap = new Dictionary<int, int>(pa);
            AddBareLocalAliases(baseMap, pa);
            _persistentAddressBaseCache = baseMap;

            var pba = new Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>(
                staticLink.PredicatesByAddress);
            foreach (var (a, p) in _dynamicLink.PredicatesByAddress) pba[a] = p;
            _persistentPredsByAddressCache = pba;
        }
        var persistentAddresses = _persistentAddressesCache;

        // The per-query region is appended in a SEPARATE buffer at a
        // logical address well above the persistent buffer's end.
        // The ProgramView built below routes addresses in [0, split)
        // to the persistent buffer and [split, split+queryLen) to the
        // per-query overlay; persistent growth between now and the
        // query's end stays in [persistentLength, split) so the
        // overlay's linked addresses remain stable.
        var queryLink = new Linker().Link(
            queryPreds,
            loadOffset: _querySplit,
            externalSymbols: persistentAddresses,
            switchTableIdBase:
                // _dynamicLink is non-null here (built in the
                // builtPersistentNow block above); ! matches the sibling
                // sites at _dynamicLink!.Addresses / .SwitchTables.
                staticLink.SwitchTables.Count + _dynamicLink!.SwitchTables.Count);

        byte[] queryBytes = queryLink.Bytecode;

        // Merge the three regions' link metadata; downstream code is
        // region-agnostic and reads this combined view. Chunk 430: seed
        // from the cached persistent base, which already carries the
        // persistent regions' bare-name aliases — a query-region REAL
        // address overwrites a colliding alias here, preserving the old
        // construction order (reals were always in the map before the
        // alias loop ran). The only observable delta is that the
        // profiler's diagnostic map now also contains those aliases.
        var mergedAddresses = new Dictionary<int, int>(_persistentAddressBaseCache!);
        foreach (var (fid, a) in queryLink.Addresses) mergedAddresses[fid] = a;
        // Phase 20: keep the functor→address map of the most recent
        // query so the profiler can resolve a recorded callee address
        // back to a Name/Arity. Only assembled when profiling is
        // compiled in — otherwise it's a cheap reference assignment we
        // skip entirely.
        if (Shumway.Core.Profiler.Enabled)
            _profileFunctorAddresses = mergedAddresses;
        // The merged switch-table list is still rebuilt per query (cheap
        // reference copies): the chunk-155c new-key assertz path REPLACES
        // entries of _dynamicLink.SwitchTables in place for cross-query
        // persistence, so a cached merged snapshot would go stale.
        var mergedSwitchTables =
            new List<Shumway.Core.SwitchTable>(staticLink.SwitchTables);
        mergedSwitchTables.AddRange(_dynamicLink!.SwitchTables);
        mergedSwitchTables.AddRange(queryLink.SwitchTables);
        // Chunk 430 — persistent part pre-merged at rebuild time.
        var mergedPredicatesByAddress =
            new Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>(
                _persistentPredsByAddressCache!);
        foreach (var (a, p) in queryLink.PredicatesByAddress)
            mergedPredicatesByAddress[a] = p;
        // The "program" in the LinkResult is now a logical concept —
        // the live bytes live across two physical buffers. Downstream
        // consumers that don't access linkResult.Bytecode (most of
        // them) work unchanged; the few that do get the persistent
        // half — the static and dynamic regions they care about.
        var linkResult = new Linker.LinkResult(
            _persistentProgram!, mergedAddresses, mergedSwitchTables,
            mergedPredicatesByAddress, Array.Empty<(int, int)>());

        // The synthetic query stays under its bare functor (it's local to
        // user but ModuleRewrite never mangles __query__ because it's not
        // present in user's local set: it was added after locals were
        // computed and isn't part of the user-defined predicates).
        int queryFunctorId = FunctorTable.Intern(
            AtomTable.Intern(queryFunctor, permanent: true).Id,
            varNames.Count);
        // Patch the launcher's call target — the prefix sits at
        // _persistentProgram offset 0, so callPos points there.
        BytecodeIO.WriteInt32(_persistentProgram!, callPos + 1, linkResult.Addresses[queryFunctorId]);

        // `program` is the persistent byte[] (used by all mutation
        // paths: assertz/retract/abolish chain patching, AppendCode);
        // `programView` is the two-buffer logical view passed to the
        // interpreter and IL helpers — they read across the gap into
        // the per-query overlay transparently.
        byte[] program = _persistentProgram!;
        var programView = new Shumway.Core.ProgramView(
            _persistentProgram!, queryBytes, _querySplit);

        // Cache freshly-compiled static predicates (chunk 82). A predicate
        // is cacheable only if its functor headed a clause in the static +
        // dynamic program (cacheableFunctors) — that excludes the
        // __query__ clause and every query-derived auxiliary — and it is
        // not dynamic. The literal-pool reusability guard is the same one
        // the dynamic cache uses.
        foreach (var pred in module.Predicates)
        {
            int fid = pred.FunctorId;
            if (!cacheableFunctors.Contains(fid) || _dynamicFunctors.Contains(fid)) continue;
            if (_staticPredicateCache.ContainsKey(fid)) continue;
            if (Shumway.Compiler.Wam.ModuleCompiler.IsCachedPredicateReusable(pred))
            {
                _staticPredicateCache[fid] = pred;
                // Chunk 430 — mirror into the merged skip-compile cache.
                // Dynamic entries take precedence in the merge; a fid here
                // is never in the dynamic cache (it isn't in
                // _dynamicFunctors, and abolish drops cache entries when
                // a functor leaves the dynamic set), but keep the guard
                // so the precedence is structural rather than assumed.
                if (_skipCompileMergedCache is not null
                    && !_dynamicPredicateCache.ContainsKey(fid))
                    _skipCompileMergedCache[fid] = pred;
            }
        }

        // Runtime call/1 (chunk 86) dispatches a goal by its bare functor,
        // but a module-local predicate is linked under its mangled
        // "module$name" functor. Add a bare-functor alias for each so a
        // runtime call/N can resolve a local predicate by its plain name.
        // Chunk 430: the persistent regions' aliases are already in
        // mergedAddresses (pre-computed at persistent rebuild — see
        // _persistentAddressBaseCache); only the query region's handful
        // of entries still need the per-query string walk.
        var addressMap = new Dictionary<int, int>(mergedAddresses);
        AddBareLocalAliases(addressMap, queryLink.Addresses);

        // --strip-wam: a predicate whose WAM body was dropped from the bundle
        // (its IL delegate carries the body) has no entry in linkResult.Addresses,
        // so it is invisible to every dispatch path that resolves a goal by
        // functor id through CurrentFunctorAddresses — the runtime meta-call
        // sites (MetaCallInEngine, DispatchCall, and the IL meta-call helper
        // IlMetaCallHelper.Dispatch). Map each such IL-only functor to its
        // resume marker (EncodeResumeMarker(fid, 0)): the marker flows through
        // SetPc and the main Dispatch loop's IsResumeMarker check routes it to
        // the IL delegate via IlByFunctorId — exactly the chunk-316 path a
        // compiled CallIl already uses. Only inject where there is no WAM
        // address (a non-stripped IL predicate keeps its WAM and meta-calls
        // through it unchanged). A module-local predicate is registered under
        // its mangled "module$name" functor, so it also needs a bare-name alias
        // (mirroring the WAM bare-alias loop above) pointing at the SAME marker
        // — a runtime meta-call (an if-then-else condition, call/N) names the
        // predicate by its plain name.
        foreach (int ilFid in IlPromotion.PromotedFunctorIds())
        {
            int marker = Activation.EncodeResumeMarker(ilFid, 0);
            if (!addressMap.ContainsKey(ilFid))
                addressMap[ilFid] = marker;
            var (atomId, arity) = FunctorTable.Lookup(ilFid);
            string name = AtomTable.GetById(atomId)?.Name ?? "";
            int dollar = name.IndexOf('$');
            if (dollar <= 0) continue;
            if (!_modules.ContainsKey(name.Substring(0, dollar))) continue;
            int bareFid = FunctorTable.Intern(
                AtomTable.Intern(name.Substring(dollar + 1), permanent: true).Id, arity);
            if (!addressMap.ContainsKey(bareFid))
                addressMap[bareFid] = marker;
        }

        // Chunk 402 — region member-entry aliases, LOWEST priority: an absorbed-only
        // member with no WAM address (stripped) and no standalone IL delegate (pruned)
        // still resolves by fid — into its region method at the member's entry cursor.
        // The ContainsKey guards keep every better resolution (a real WAM address, or
        // a standalone delegate's (fid, 0) marker from the loop above) ahead of it.
        // Same bare-name aliasing as above: meta-calls name predicates unmangled.
        foreach (var (memberFid, marker) in _regionMemberAliases)
        {
            if (!addressMap.ContainsKey(memberFid))
                addressMap[memberFid] = marker;
            var (atomId, arity) = FunctorTable.Lookup(memberFid);
            string name = AtomTable.GetById(atomId)?.Name ?? "";
            int dollar = name.IndexOf('$');
            if (dollar <= 0) continue;
            if (!_modules.ContainsKey(name.Substring(0, dollar))) continue;
            int bareFid = FunctorTable.Intern(
                AtomTable.Intern(name.Substring(dollar + 1), permanent: true).Id, arity);
            if (!addressMap.ContainsKey(bareFid))
                addressMap[bareFid] = marker;
        }

        var engine = new Activation
        {
            Out = Out,
            Host = this,
            Operators = new OperatorTableAdapter(_operators),
            // Per-engine stream registry (chunk 140) — wired through
            // so StreamBuiltins reaches handles, the alias map, and
            // the current-input / current-output cursors.
            Streams = Streams,
            // The current-query address map lets IL-emitted Execute
            // opcodes (chunk 47) resolve their tail-call target via a
            // stable functor-id lookup instead of an embedded address
            // that would only be valid for one query's linked layout.
            CurrentFunctorAddresses = addressMap,
            // Chunk 417 — the ISO `unknown` flag, wired through dispatch.
            OnUnknown = _flags.Unknown switch
            {
                "fail" => Shumway.Core.UnknownAction.Fail,
                "warning" => Shumway.Core.UnknownAction.Warning,
                _ => Shumway.Core.UnknownAction.Error,
            },
            // String literal pool for IL-emitted get_pstr/put_pstr
            // (chunk 50) and the linked program byte array for the
            // IL Call re-entry helper.
            CurrentStringLiterals = module.StringLiterals,
            CurrentProgram = program,
            // ADR-015 chunk C — bytecode-level dynamic dispatch reads the
            // host's generation at every enter_dynamic opcode. chunk 432:
            // through the shared GenerationBox (a field read) instead of
            // a Func<long> invoke per dynamic call.
            DbGenerationBox = _dbGeneration,
            // ADR-015 chunk C step 4: where the fail-stub lives in the
            // prefix. Used by the upcoming incremental-assertz path and
            // by dynamic predicates' last-clause chain instructions.
            DynamicFailStubAddr = failStubAddr,
            // ADR-034 — the host-lifetime mutated-dynamics set, shared by
            // reference so baked staleness tests see mutations live.
            MutatedDynamicFids = _mutatedDynamicFids,
            // ADR-035 — the debug seam. Null unless a session is attached
            // (trace/0, or a debugger), in which case the Tier-0 interpreter
            // raises the four Prolog ports on it.
            Debug = DebugSession,
            // ADR-035 — inert unless the program was compiled under
            // compile_mode=debug (only then does any debug_lastcall exist).
            LastCallOptimisation = _flags.DebugLco,
        };
        // Heap-buffer pool: seed the fresh activation with the recycled
        // buffer (if any) BEFORE anything materializes onto the heap.
        AdoptPooledHeap(engine);
        // Chunk 151b: the persistent buffer is over-allocated, so the
        // engine's ProgramLength must reflect the live region (not the
        // raw byte[] capacity) for AppendCode's offset accounting. The
        // overlay + split let the dispatch loop refresh the
        // ProgramView correctly after a mid-query AppendCode.
        engine.SetInitialProgramLength(_persistentLength);
        engine.CurrentQueryOverlay = queryBytes;
        engine.CurrentQuerySplit = _querySplit;
        // Chunk 169: the dispatch loop caches its ProgramView and
        // refreshes only when this generation flips, so the per-
        // query rewire above has to advertise itself.
        engine.BumpProgramGeneration();
        // Chunk 155b/c: expose the linked switch tables on the engine
        // as a MUTABLE list. The same list reference is handed to the
        // interpreter; the new-key assertz path swaps entries in place
        // and the interpreter sees the update on the next dispatch
        // because it reads through the list reference each time.
        var mutableSwitchTables =
            new List<Shumway.Core.SwitchTable>(linkResult.SwitchTables);
        engine.SwitchTables = mutableSwitchTables;

        var interp = new BytecodeInterpreter(
            engine, module.StringLiterals, module.FloatLiterals,
            mutableSwitchTables, module.BigIntLiterals);

        // --strip-wam: register each persisted dispatch graph onto this query's
        // fresh engine, so a stripped indexed predicate resolves its entry clause
        // from the graph (its WAM body is gone). The indexed-dispatch cache is
        // per-engine, so this runs per query.
        if (_persistedIndexGraphs.Count > 0)
            foreach (var (fid, graphBytes) in _persistedIndexGraphs)
                Shumway.Compiler.Il.IlIndexedDispatch.RegisterPersistedGraph(
                    engine, fid, graphBytes);

        // Chunk 225 Stage B.1: wire the direct IL-delegate table (the
        // CallIl opcode reads this) and rewrite every Call site whose
        // callee already has IL into a CallIl. The opcode's slow path
        // (Call → DispatchToTier1OrBytecode → Tier1Dispatcher?.OnDispatch)
        // is the hottest non-Dispatch frame in the dotnet-trace profile
        // for a bundle-IL workload; CallIl bypasses it with a direct
        // delegate invoke. Same byte width and operand layout as Call,
        // so the patch is one opcode-byte swap + one 4-byte operand
        // overwrite (target address → callee functor id).
        InstallCallIlRewrites(interp, mergedPredicatesByAddress, queryBytes);

        // ADR-015 chunk C step 4: refresh the interpreter's literal pools
        // after an incremental assertz/asserta interns a new literal.
        engine.RefreshLiteralPoolsCallback = (s, f, b) =>
        {
            interp.RefreshLiteralPools(s, f, b);
            engine.CurrentStringLiterals = s;
        };
        // Chunk 427 — record the pool lengths the interpreter was built
        // with; RefreshLiteralPoolsIfGrown compares against these so the
        // per-assert refresh (three Snapshot() array copies) only runs
        // when a compile actually interned a new literal. Phase 33: per
        // engine (see _interpPoolCounts).
        _interpPoolCounts.AddOrUpdate(engine, new[]
        {
            module.StringLiterals.Count,
            module.FloatLiterals.Count,
            module.BigIntLiterals.Count,
        });

        // Chunk 144: lets a PrologRuntimeException thrown from a
        // builtin Impl carry the offending term in its error/2 value
        // slot, instead of the Phase-9 fresh anonymous variable.
        // Eager materialisation here means the term survives sub-engine
        // teardown — the per-query Activation is gone by the time the
        // parent's catch/3 handler translates the runtime exception.
        engine.MaterializeCellToTerm = cell =>
        {
            // Snapshot to a heap slot so the standard "read by heap
            // index" TermReader path applies (avoids a cell-direct
            // reader variant).
            int slot = engine.AllocateHeap(1);
            engine.SetHeap(slot, cell);
            return TermReader.Materialize(engine, slot);
        };

        // Chunk 162: opt-in SHUMWAY_CP_TRACE dump. The diagnostic prints
        // "name/arity@offset" for each live CP's saved BP using the
        // same address->predicate map the stack-trace resolver uses,
        // so we can spot a CP that should have been cut but is still
        // alive at the moment a builtin is re-entered with an
        // unbound arg.
        {
            var resolverMap = mergedPredicatesByAddress;
            int[] sortedAddrs = resolverMap.Keys.OrderBy(a => a).ToArray();
            engine.ResolveAddressToLabel = addr =>
            {
                if (sortedAddrs.Length == 0) return null;
                int idx = Array.BinarySearch(sortedAddrs, addr);
                if (idx < 0) idx = ~idx - 1;
                if (idx < 0) return null;
                int entryAddr = sortedAddrs[idx];
                if (!resolverMap.TryGetValue(entryAddr, out var pred))
                    return null;
                var (atomId, arity) = FunctorTable.Lookup(pred.FunctorId);
                string name = AtomTable.GetById(atomId)?.Name ?? "?";
                return $"{name}/{arity}@+{addr - entryAddr}";
            };
        }

        // ADR-015 chunk C step 4: per-functor chain state — record where
        // each clause's check_visible died slot lives in the running
        // program. retract patches the slot in place; next call's
        // check_visible filters the clause out (the bytecode-level
        // logical-update view path that supersedes chunk C's redirect).
        // Chunk 151b: only rebuild chain state from scratch when the
        // persistent buffer is fresh. While it's being reused across
        // queries, the incremental assertz / asserta / retract paths
        // maintain _dynChains directly — a contiguous walk from
        // predAddr can't see chunks appended elsewhere by AppendCode.
        if (builtPersistentNow)
        {
            // Phase 33 — a fresh buffer gets a fresh chain table. Older
            // tables stay alive through _engineChainTables for any outer
            // (nested-query) engines still running on their buffers.
            _dynChainTable = new DynChainTable();
            PopulateDynChains(program, addressMap, mergedPredicatesByAddress);
        }
        // Phase 33 — associate this engine with the table describing the
        // buffer it runs on: every in-place dynamic mutation this engine
        // performs resolves chain state through this association, so a
        // nested query's rebuild can never make it patch wrong offsets.
        _engineChainTables.AddOrUpdate(engine, _dynChainTable);
        // ... and track it for the dynamic-mutation broadcast (a mutation
        // from a nested query must also reach the buffers of suspended
        // outer engines — see _liveEngines).
        RegisterLiveEngine(engine);

        if (StackDiagEnabled)
        {
            int probeFid = FunctorTable.Intern(
                AtomTable.Intern("$lgt_file_loading_stack_", permanent: true).Id, 2);
            int chainEntries = _dynChainTable.Chains.TryGetValue(probeFid, out var pch)
                ? pch.Entries.Count : -1;
            int storeCount = _dynamicClauses.TryGetValue(probeFid, out var pcs) ? pcs.Count : -1;
            Console.Error.WriteLine(
                $"[STK-SETUP] eng={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(engine):X8}"
                + $" builtNow={builtPersistentNow}"
                + $" buf={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(program):X8}"
                + $" tbl={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_dynChainTable):X8}"
                + $" chainEntries={chainEntries} store={storeCount}");
        }

        // Tier-1 promotion: hook the interpreter up to this engine's
        // IlPromotionStore via an address-keyed adapter. The store itself
        // is functor-keyed and persists across queries; the adapter holds
        // the per-query PredicatesByAddress map so it can translate the
        // bytecode-PC the interpreter has into the functor the store
        // wants.
        interp.Tier1Dispatcher = new Tier1DispatcherAdapter(
            IlPromotion, linkResult.PredicatesByAddress, _jitIndexProfile);

        // Chunk 76 — PGO phase-2 pass. Once per query setup, off the
        // hot path: any promoted, instrumented predicate that has
        // accumulated enough profile samples is recompiled to its
        // optimised (dispatch-reordered) form. Build a functor-keyed
        // view of this query's program for the recompile to read.
        var functorToPredicate = new Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>();
        foreach (var (_, pred) in linkResult.PredicatesByAddress)
            functorToPredicate[pred.FunctorId] = pred;
        IlPromotion.ConsiderPgoRecompiles(functorToPredicate, functorToPredicate);
        // Phase 16 chunk 183: IlSubroutineRunner / BacktrackRunner /
        // SetBacktrackFloor wirings deleted. The chunk-50 IL Call /
        // chunk-66 meta-CP backtrack-driver / chunk-174 floor pin
        // were all replaced by threaded resume-marker dispatch
        // (chunks 181 + 182).
        // Remember the per-query address → predicate map so error
        // reporting (chunk 51) can translate the engine's PC and env-
        // chain return addresses into Name/Arity stack frames.
        _currentPredicatesByAddress = linkResult.PredicatesByAddress;

        // ADR-035 — which source sites this engine's code actually contains (what a
        // breakpoint can bind to), then (re)apply the armed ones to the program this
        // query will run. Addresses are derived, not stored: the source site is the
        // truth, and the code space it maps into can be relinked or compacted
        // between queries. Both loops are skipped entirely unless something was
        // compiled debuggable, so release queries pay nothing.
        _sortedPredEntries = null;   // the layout may have moved
        if (_flags.DebugCodegen || _compiledSites.Count > 0)
        {
            var sites = new HashSet<int>();
            var byPc = new SortedDictionary<int, int>();
            var frames = new SortedDictionary<int, Shumway.Compiler.Wam.DebugClauseFrame>();
            _clauseLines.Clear();
            foreach (var (predAddr, pred) in _currentPredicatesByAddress)
            {
                foreach (var stop in pred.DebugStops)
                {
                    sites.Add(stop.SiteId);
                    byPc[predAddr + stop.Offset] = stop.SiteId;
                }
                // The clause frame maps take the same second relocation as the stop
                // sites: clause-local, then predicate-local, then program-absolute.
                for (int i = 0; i < pred.DebugFrames.Count; i++)
                {
                    var f = pred.DebugFrames[i];
                    frames[predAddr + f.Start] = new Shumway.Compiler.Wam.DebugClauseFrame(
                        predAddr + f.Start, predAddr + f.End, f.HasFrame, f.Variables)
                    {
                        HeadArgs = f.HeadArgs,
                        ClauseNumber = f.ClauseNumber,
                    };

                    // The clause's source span: from where its head is written down to
                    // the last line it can be stopped at. What a breakpoint on a line
                    // with no code of its own snaps within, and no further.
                    int fileId = -1, first = int.MaxValue, last = -1;
                    foreach (var stop in pred.DebugStops)
                    {
                        if (stop.Offset < f.Start || stop.Offset >= f.End) continue;
                        var site = Shumway.Core.DebugSiteTable.Get(stop.SiteId);
                        fileId = site.FileId;
                        if (site.Line < first) first = site.Line;
                        if (site.Line > last) last = site.Line;
                    }
                    if (last < 0) continue;
                    int headLine = i < pred.ClauseSourcePositions.Count
                        ? pred.ClauseSourcePositions[i].Line
                        : first;
                    _clauseLines.Add((fileId, Math.Min(headLine, first), first, last));
                }
            }
            _compiledSites = sites;
            _stopPcs = new int[byPc.Count];
            _stopSiteIds = new int[byPc.Count];
            byPc.Keys.CopyTo(_stopPcs, 0);
            byPc.Values.CopyTo(_stopSiteIds, 0);

            _clauseStarts = new int[frames.Count];
            _clauseFrames = new Shumway.Compiler.Wam.DebugClauseFrame[frames.Count];
            frames.Keys.CopyTo(_clauseStarts, 0);
            frames.Values.CopyTo(_clauseFrames, 0);
        }
        // A freshly-rebuilt persistent buffer carries no Break bytes and the recorded originals
        // belong to the now-dead one; a reused buffer still carries them. Only un-patch when the
        // buffer actually holds our patches. (RefreshBreakpoints, mid-query, follows the live
        // activation via _lastQueryEngine — set just below — so a realloc is tracked there.)
        //
        // ADR-035 D5 — a DEBUG EVALUATION's nested query does not touch the sync at all: the
        // armed table describes the OUTER query's buffer, which is where the machine returns
        // when the evaluation is done, and re-deriving it here would point it at the eval's.
        // The usual case reuses the outer's buffer anyway (patches in place, table already
        // right); in the rare rebuilt-under-eval case the fresh buffer simply runs without
        // Break bytes — an eval's stops are suppressed regardless — and the flag below tells
        // the NEXT real setup that this persistent buffer never received its patches, so its
        // per-byte un-patch must not expect to find them (a false "drift" alarm otherwise).
        if (_debugEvalDepth == 0)
        {
            SyncBreakpoints(program,
                bufferCarriesOurPatches: !builtPersistentNow && !_persistentRebuiltPatchFree);
            _persistentRebuiltPatchFree = false;
        }
        else if (builtPersistentNow)
        {
            _persistentRebuiltPatchFree = true;
        }
        // Shared BY REFERENCE, and shared even when it is empty: a breakpoint can be armed
        // on a query that is already running (F9 during a long goal), and the Break byte it
        // patches into the program is reached by an activation that was set up before the
        // table had anything in it. Handing over null when the table happened to be empty
        // would leave that activation with a Break it cannot decode.
        engine.BreakpointOriginals = _breakpointPatches;

        int[] varHeapIndices = new int[varNames.Count];
        for (int i = 0; i < varNames.Count; i++)
        {
            int h = engine.AllocateHeapUnbound();
            varHeapIndices[i] = h;
            engine.SetRegister(i, Cell.Ref(h));
        }

        // ADR-016: register the heap roots the engine cannot see. The
        // query-variable cells are read out of the heap by BuildSolution
        // after the query, so a collection during the query must keep
        // them alive (mark) and rewrite their recorded indices
        // (relocate) — otherwise the extracted bindings are scrambled.
        // The global-variable store's compound values are roots for the
        // same reason within a query.
        var globals = GlobalVars;
        engine.OnGcMark = (markCell, markReferents) =>
        {
            for (int i = 0; i < varHeapIndices.Length; i++) markCell(varHeapIndices[i]);
            foreach (var (_, cell) in globals.All()) markReferents(cell);
            // ADR-035: an attached debug session holds heap indices too (the
            // open goals' argument cells).
            engine.Debug?.MarkHeapRoots(markCell);
        };
        engine.OnGcRelocate = (relocIndex, relocCell) =>
        {
            for (int i = 0; i < varHeapIndices.Length; i++)
                varHeapIndices[i] = relocIndex(varHeapIndices[i]);
            globals.RelocateCells(relocCell);
            engine.Debug?.RelocateHeapRoots(relocIndex);
        };

        // Tier-0 deterministic benchmark metric: keep a reference to the
        // per-query engine so the harness can read its monotonic
        // CellsAllocated after the query completes (the engine is
        // otherwise local and discarded). Read-only diagnostic; does not
        // affect execution.
        _lastQueryEngine = engine;
        return (programView, varNames, varHeapIndices, engine, interp);
    }

    private Activation? _lastQueryEngine;

    /// <summary>Monotonic count of WAM heap cells reserved by the most
    /// recent query's engine (0 before any query). A deterministic,
    /// wall-clock-independent metric for allocation-affecting changes —
    /// see <see cref="Activation.CellsAllocated"/> and the benchmark
    /// harness <c>--alloc</c> mode.</summary>
    public long LastQueryCellsAllocated => _lastQueryEngine?.CellsAllocated ?? 0;

    /// <summary>Chunk 427 — single-clause transform + compile for the four
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
    private Shumway.Compiler.Wam.CompiledClause? CompileRuntimeAssertClause(
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
            var transformed = ClausePipeline.Apply(new[] { newClause }, Modes, helperIdProvider: NextMetaHelperId);
            if (transformed.Count == 0) return null;
            _assertDynCtx ??= new ModuleRewrite.Context(
                DefaultModuleName, new HashSet<int>(), _dynamicFunctors);
            toCompile = ModuleRewrite.Rewrite(transformed[0], _assertDynCtx);
            // Phase 33 — the helpers used to be DROPPED here, leaving the
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

    /// <summary>Phase 33 — compiles and live-links the MetaTransform helper
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
            || engine.CurrentFunctorAddresses is not Dictionary<int, int> addrMap)
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

    /// <summary>Chunk 427 — refreshes the interpreter's literal pools only
    /// when the engine pools actually grew past what the interpreter holds
    /// (recorded at query setup / last refresh). The pools are append-only
    /// with stable ids (<see cref="Shumway.Compiler.Wam.LiteralPool{T}"/>),
    /// so unchanged counts mean the interpreter's snapshot is already
    /// complete and the three per-assert <c>Snapshot()</c> array copies can
    /// be skipped — the common case: an asserted fact like
    /// <c>next_char_i(42)</c> interns nothing.</summary>
    private void RefreshLiteralPoolsIfGrown(Activation engine)
    {
        // Phase 33 — per-engine counters (an engine with no record refreshes
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
    /// <para>Phase 33 — BROADCAST entry point: applies the extension to the
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

    /// <summary>Phase 33 — reconciles <paramref name="engine"/>'s dynamic
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
    private void ReconcileEngineDynamicView(Activation engine)
    {
        if (GetChainTable(engine) is not { } tbl) return;
        if (engine.CurrentProgram is null) return;
        // Snapshot fids: the rebuild path can materialize new trampolines,
        // mutating the dictionary while we walk.
        var fids = new List<int>(tbl.Chains.Keys);
        foreach (int fid in fids)
        {
            if (!tbl.Chains.TryGetValue(fid, out var chain)) continue;
            var store = _dynamicClauses.TryGetValue(fid, out var cs)
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

    /// <summary>Phase 33 — rebuilds one functor's dynamic-dispatch view in
    /// <paramref name="target"/>'s buffer from the authoritative store.
    /// Needed when the in-place mutation paths can't keep that view
    /// coherent — the archetype: the target's buffer compiled the dynamic
    /// predicate with the chunk-155 INDEXED layout (it went hot), whose
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
    private bool _inFidViewRebuild;

    private void RebuildEngineFidChainView(Activation target, int functorId)
    {
        if (_inFidViewRebuild) return;
        if (target.CurrentProgram is null
            || target.DynamicFailStubAddr <= 0
            || target.CurrentFunctorAddresses is not Dictionary<int, int> addrMap
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
            if (_dynamicClauses.TryGetValue(functorId, out var cs))
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
        // Chunk 155b: try the new extensible-indexed in-place
        // extension first. If the predicate uses the chunk-155
        // layout (enter_dynamic + switch_on_term + try_me_else
        // bucket chains) AND the new clause's arg-0 key matches an
        // existing bucket, we extend each affected chain in place
        // — no rebuild needed. Returns true if handled here.
        if (TryAppendToIndexedDynamic(engine, functorId, newClause))
            return;
        // Fall back to the chain layout (chunk-127) or, for cases
        // chunk-155b can't yet handle (new key, var-arg, multi-arg
        // indexed), to the persistent-buffer rebuild.
        // Fall back: chunk-127 chain extension applies only when
        // chain state is populated (i.e. the predicate was compiled
        // as a try_me_else chain). Otherwise the layout is some
        // form of indexed dispatch that chunk-155b/c didn't handle
        // — for a hot predicate, request a persistent rebuild so the
        // next query sees the new clause through a fresh compile.
        // Phase 33 — resolve chain state through THIS engine's table (the
        // one describing its buffer; see DynChainTable) and capture buffer
        // ownership before any AppendCode.
        bool ownsHost = EngineOwnsHostBuffer(engine);
        var chainTable = GetChainTable(engine);
        // Phase 19+ — mid-query trampoline materialisation. If the
        // predicate was auto-promoted mid-query via EnsureDynamic
        // (implicit_dynamic=true with a runtime-bound assertz head),
        // _dynamicFunctors holds it but no chain state was ever
        // built — the trampoline lives in bytecode emitted by
        // SetupQueryFromTerm, and that ran before the auto-promote.
        // Build a fresh trampoline now so the chunk-127 extension
        // below has something to extend.
        if ((chainTable is null || !chainTable.Chains.ContainsKey(functorId))
            && _dynamicFunctors.Contains(functorId)
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
            // Phase 33 — an unextendable chain record (a trust_me tail, or
            // a contiguous walk over an indexed layout) means THIS engine's
            // live view can't take the clause in place — without repair its
            // dispatch silently diverges from the store forever (the
            // '$lgt_current_object_' stuck-entries signature). Rebuild the
            // fid's view wholesale; the rebuilt chain absorbs the whole
            // store, new clause included.
            RebuildEngineFidChainView(engine, functorId);
            return;
        }

        // Phase 33 — last-line safety: skip the in-place extend when the
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
        // share a flat module rewrite context (chunk 427 — shared helper
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

        // Chunk 150: try the free-list (chunks reclaimed by a prior
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

        // Chunk 151b: AppendCode may have reallocated the buffer;
        // refresh PrologEngine's reference so the next query sees the
        // live buffer (owner engine) — or, when this engine's buffer is
        // no longer the host's (a nested query rebuilt it), mark the
        // host buffer for rebuild: the newer buffer did NOT get this
        // in-place extension and would otherwise miss the clause on
        // cross-query reuse.
        SyncOrInvalidateAfterMutation(engine, ownsHost);

        // The clause may have interned new literals — refresh the
        // interpreter so check_visible isn't running against a stale
        // pool snapshot for any subsequent call (chunk 427: skipped
        // when the pools didn't grow).
        RefreshLiteralPoolsIfGrown(engine);
    }

    /// <summary>Test hook: returns the chain's current tail-next address
    /// (where the next assertz would patch), or <c>null</c> when no chain
    /// state exists.</summary>
    internal int? PeekTailNextAddr(int functorId)
    {
        if (!_dynChainTable.Chains.TryGetValue(functorId, out var chain)) return null;
        return chain.TailNextAddr;
    }

    /// <summary>Test hook: returns the chain's current head-clause
    /// address — what asserta will demote to retry_me_else + nops on the
    /// next call.</summary>
    internal int? PeekHeadClauseAddr(int functorId)
    {
        if (!_dynChainTable.Chains.TryGetValue(functorId, out var chain)) return null;
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
    /// <para>Phase 33 — BROADCAST entry point (see
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
        // Chunk 155f: try in-place asserta for chunk-155a layout.
        if (TryPrependToIndexedDynamic(engine, functorId, newClause))
            return;
        // Phase 19+ — mirror the assertz path: when the predicate was
        // auto-promoted mid-query (no trampoline ever built), build
        // one now so the chain prepend below has a chain to prepend
        // to.
        // Phase 33 — resolve chain state through THIS engine's table and
        // capture buffer ownership before any AppendCode.
        bool ownsHost = EngineOwnsHostBuffer(engine);
        var chainTable = GetChainTable(engine);
        if ((chainTable is null || !chainTable.Chains.ContainsKey(functorId))
            && _dynamicFunctors.Contains(functorId)
            && engine.CurrentProgram is not null
            && engine.DynamicFailStubAddr > 0)
        {
            MaterializeDynamicTrampoline(engine, functorId);
            chainTable ??= GetChainTable(engine);
        }
        // Fall back to chunk-128 chain prepend for chain layout, or
        // rebuild for indexed layouts we can't extend in place.
        if (chainTable is null
            || !chainTable.Chains.TryGetValue(functorId, out var chain)
            || chain.TrampolineExecuteOperandAddr < 0
            || engine.CurrentProgram is null)
        {
            if (_jitIndexProfile.IsHot(functorId)) InvalidatePersistent();
            // Phase 33 — repair this engine's live view (see the assertz
            // counterpart). The rebuild re-links the store in order, so
            // the asserta'd clause (already prepended there) lands at the
            // rebuilt chain's head.
            RebuildEngineFidChainView(engine, functorId);
            return;
        }
        if (engine.DynamicFailStubAddr <= 0) return;

        // Phase 33 — last-line safety: skip the in-place prepend when the
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

        // Same transform pipeline as the setup path (chunk 427 — shared
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

        // Chunk 150: try the free-list before extending the program.
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

        // Chunk 151b: keep the persistent-buffer reference in sync
        // (owner engine) — or mark the host buffer for rebuild when this
        // engine's buffer is no longer the host's (Phase 33).
        SyncOrInvalidateAfterMutation(engine, ownsHost);

        // Refresh interpreter pools — same reasoning as the assertz path
        // (chunk 427: skipped when the pools didn't grow).
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
    /// <summary>Phase 33 — true if any of a dynamic chain's cached byte
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

    /// <summary>Phase 33 — O(1) structural staleness check: the byte at
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

    /// <summary>Phase 33 — full structural staleness check for a dynamic
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

    /// <summary>Phase 33 — broadcast counterpart of
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
        // Phase 33 — the engine's own chain table (describes ITS buffer).
        if (GetChainTable(engine) is not { } tbl
            || !tbl.Chains.TryGetValue(functorId, out var chain)) return;
        if (clauseIndex < 0 || clauseIndex >= chain.Entries.Count) return;
        var entry = chain.Entries[clauseIndex];
        var program = engine.CurrentProgram;
        // Phase 33 — last-line safety: skip the in-place died patch when
        // the entry's cached slot is out of range OR structurally stale
        // (should never fire with per-engine tables). The clause is
        // already removed from _dynamicClauses by the caller, so clause/2
        // is correct; the chain rebuilds at the next query setup.
        if (program is not null && entry.DiedOperandAddr > 0
            && entry.DiedOperandAddr + sizeof(long) <= program.Length
            && IsChainInstructionAt(program, entry.NextOperandAddr - 1))
            BytecodeIO.WriteInt64(program, entry.DiedOperandAddr, _dbGeneration.Value);
        // Chunk 150: stage the chunk for free-list reuse on GC, but
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
        _dynChainTable.Chains.Clear();
        var seen = new HashSet<int>();
        foreach (int fid in _dynamicFunctors)
            if (seen.Add(fid))
                PopulateDynChainViaAddressMap(fid, program, addressMap, predicatesByAddress);
        foreach (int fid in _dynamicClauses.Keys)
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
        _dynChainTable.Chains.Remove(fid);
        // Empty dynamic predicates still need chain state for incremental
        // assertz — the empty-stub clause's try_me_else <fail-stub> is the
        // first patch target. So default to an empty clause list rather
        // than skipping when _dynamicClauses has no entry.
        var clauses = _dynamicClauses.TryGetValue(fid, out var cs)
            ? cs : (IReadOnlyList<Clause>)Array.Empty<Clause>();

        var chain = new DynChainState();
        int pc = predAddr;
        int end = predAddr + predByteLength;
        int clauseIndex = 0;
        int pendingNextOperand = -1;
        int tailNextOperand = -1;
        // Chunk 150: track each chunk's start address as we walk so
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
            // Advance past the trampoline (EnterDynamic + Execute = 6 bytes).
            pc += 6;
        }
        while (pc < end)
        {
            var info = Shumway.Core.OpcodeTable.Get(program[pc]);
            if (info.Op == Shumway.Core.Opcode.TryMeElse
                || info.Op == Shumway.Core.Opcode.RetryMeElse)
            {
                // Chunk 150: a new chain instruction marks a chunk
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
            _dynChainTable.Chains[fid] = chain;
    }

    /// <summary>Adds a fail-only stub clause for every dynamic functor that
    /// has neither static nor asserted clauses yet, so that calls to it
    /// resolve at link time (and fail at runtime — which is what an
    /// "empty dynamic predicate" should do).</summary>
    private void EmitEmptyDynamicStubs(
        List<Clause> allRewritten, Shumway.Compiler.Lexer.SourcePosition pos,
        HashSet<int> seen)
    {
        // Chunk 430 — `seen` arrives as the precomputed head-fid set of
        // allRewritten (maintained by the caller's cached transform
        // bookkeeping), replacing the per-query walk that re-interned
        // every clause head. Stub fids are added to the set so the
        // caller's cacheableFunctors snapshot includes them, exactly as
        // the old post-stub HeadFunctorIdOf walk did.
        if (_dynamicFunctors.Count == 0) return;

        foreach (int fid in _dynamicFunctors)
        {
            if (!seen.Add(fid)) continue;
            var (atomId, arity) = FunctorTable.Lookup(fid);
            string name = AtomTable.GetById(atomId)?.Name ?? "?";
            Term head = arity == 0
                ? (Term)new AtomTerm(name)
                : new CompoundTerm(
                    name,
                    Enumerable.Range(0, arity).Select(_ => (Term)new VarTerm("_")).ToArray());
            Term stubTerm = new CompoundTerm(":-", new[] { head, (Term)new AtomTerm("fail") });
            allRewritten.Add(new Clause(ClauseKind.Rule, stubTerm, pos));
        }
    }

    /// <summary>Chunk 430 — the chunk-86 bare-functor alias computation,
    /// factored out so it can run once per persistent rebuild over the
    /// persistent regions' addresses (cached in
    /// <see cref="_persistentAddressBaseCache"/>) and per query over just
    /// the query region's addresses. For every <c>module$name</c> entry in
    /// <paramref name="entries"/> whose module is loaded, adds
    /// <c>name/arity → address</c> to <paramref name="map"/> unless the
    /// bare functor already resolves (a real definition or an earlier
    /// alias wins, preserving the original first-wins semantics).</summary>
    private void AddBareLocalAliases(
        Dictionary<int, int> map, IReadOnlyDictionary<int, int> entries,
        HashSet<int>? recordAdded = null)
    {
        foreach (var (mangledFunctorId, address) in entries)
        {
            var (atomId, arity) = FunctorTable.Lookup(mangledFunctorId);
            string mangledName = AtomTable.GetById(atomId)?.Name ?? "";
            int dollar = mangledName.IndexOf('$');
            if (dollar <= 0) continue;
            if (!_modules.ContainsKey(mangledName.Substring(0, dollar))) continue;
            int bareFunctorId = FunctorTable.Intern(
                AtomTable.Intern(mangledName.Substring(dollar + 1), permanent: true).Id,
                arity);
            if (!map.ContainsKey(bareFunctorId))
            {
                map[bareFunctorId] = address;
                recordAdded?.Add(bareFunctorId);
            }
        }
    }

    /// <summary>Returns the functor ids that are <em>local</em> to a module
    /// (defined as a head functor but not exported via <c>:- public</c>).
    /// Used by <see cref="ModuleRewrite"/> to decide which call targets need
    /// the synthetic <c>module$name</c> prefix.</summary>
    private static HashSet<int> ComputeLocalFunctors(
        IEnumerable<Clause> clauses, HashSet<int> publicFunctors)
    {
        var locals = new HashSet<int>();
        foreach (var c in clauses)
        {
            if (!TryExtractHead(c, out string name, out int arity)) continue;
            int fid = FunctorTable.Intern(
                AtomTable.Intern(name, permanent: true).Id, arity);
            if (!publicFunctors.Contains(fid)) locals.Add(fid);
        }
        return locals;
    }

    private static bool TryExtractHead(Clause clause, out string name, out int arity)
    {
        Term headTerm = clause.Kind == ClauseKind.Rule
            ? ((CompoundTerm)clause.Term).Args[0]
            : clause.Term;
        switch (headTerm)
        {
            case AtomTerm a: name = a.Name; arity = 0; return true;
            case CompoundTerm c: name = c.Functor; arity = c.Args.Length; return true;
            default: name = ""; arity = 0; return false;
        }
    }

    /// <summary>Throws if more than one module declares the same functor
    /// public — the public namespace is flat across all loaded modules.</summary>
    private void ValidatePublicUniqueness()
    {
        var owner = new Dictionary<int, string>();
        foreach (var (name, manifest) in _modules)
        {
            foreach (int fid in manifest.PublicFunctors)
            {
                if (owner.TryGetValue(fid, out var other))
                {
                    // Multifile escape hatch (chunk 60): if both the
                    // already-owning module and the current one declare
                    // the functor :- multifile, the duplicate is
                    // intentional. Each module's clauses live
                    // independently; the linker concatenates them as if
                    // they came from one source.
                    bool bothMultifile =
                        _modules[other].MultifileFunctors.Contains(fid)
                        && manifest.MultifileFunctors.Contains(fid);
                    if (bothMultifile) continue;

                    var (atomId, arity) = FunctorTable.Lookup(fid);
                    string functorName = AtomTable.GetById(atomId)?.Name ?? "?";
                    throw new InvalidOperationException(
                        $"Functor {functorName}/{arity} is declared :- public in both "
                        + $"module '{other}' and module '{name}'. Public predicates must "
                        + "be unique across the engine (unless both modules also "
                        + "declare it :- multifile).");
                }
                owner[fid] = name;
            }
        }
    }

    private static Solution BuildSolution(
        List<string> varNames, int[] varHeapIndices, Activation engine,
        bool isLast = false,
        PrologEngine? host = null)
    {
        var bindings = new Dictionary<string, Term>(varNames.Count);
        for (int i = 0; i < varNames.Count; i++)
            bindings[varNames[i]] = TermReader.Materialize(engine, varHeapIndices[i]);
        return new Solution(success: true, bindings: bindings, isLast: isLast, engine: host);
    }

    private static void CollectVariables(Term term, List<string> order, HashSet<string> seen)
    {
        switch (term)
        {
            case VarTerm v when v.Name != "_":
                if (seen.Add(v.Name)) order.Add(v.Name);
                break;
            case CompoundTerm c:
                foreach (Term arg in c.Args)
                    CollectVariables(arg, order, seen);
                break;
        }
    }
}
