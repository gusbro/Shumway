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
public sealed partial class PrologEngine : Shumway.Builtins.IGlobalVarHost
{
    public const string DefaultModuleName = "user";

    /// <summary>Per-engine global-variable store backing
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

    /// <summary>Save-state chronological log of every source
    /// string passed to <see cref="ConsultString"/>, excluding the
    /// auto-loaded prelude (which the ctor always loads first).
    /// <see cref="SaveState"/> writes this list verbatim into a
    /// snapshot bundle; <see cref="RestoreState"/> resets the engine
    /// and replays each entry in order to rebuild the same module
    /// state.</summary>
    private readonly List<string> _consultHistory = new();

    /// <summary>Arity-Prolog recorded database.
    /// Lazily constructed on first access so engines that never use it
    /// pay nothing.</summary>
    private RecordedDatabase? _records;
    public RecordedDatabase Records => _records ??= new RecordedDatabase();

    /// <summary>per-engine pseudo-random generator
    /// behind <c>random/1</c>, <c>random_between/3</c> and
    /// <c>randomize/1</c>. Seedable via Randomize; defaults to a
    /// time-based seed on first access.</summary>
    private System.Random? _random;
    internal System.Random Random => _random ??= new System.Random();

    /// <summary>Replaces the per-engine random generator with one
    /// seeded by <paramref name="seed"/>. Backs <c>randomize/1</c>.</summary>
    public void Randomize(int seed) => _random = new System.Random(seed);

    /// <summary>Per-engine stream registry. Owns every
    /// open stream, the alias map, and the current-input /
    /// current-output cursors. Lazily built on first access so an
    /// engine that never touches streams pays nothing.</summary>
    private StreamRegistry? _streams;
    internal StreamRegistry Streams =>
        _streams ??= new StreamRegistry(Out);

    /// <summary>Activation-wide mutable flag state. Builtins
    /// <c>set_prolog_flag/2</c> and <c>current_prolog_flag/2</c> read
    /// and write here. The parser instances created during ConsultString
    /// and SetupQuery receive the same instance by reference, so a
    /// <c>:- set_prolog_flag(double_quotes, codes).</c> directive at the
    /// top of a source affects every subsequent parse of that source
    /// and every query made against this engine.</summary>
    private readonly PrologFlags _flags = new();

    /// <summary>Diagnostic accessor for the flag state. Tests
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

    // ----- Heap-buffer pool -----
    // Extracted component: see HeapBufferPool. One slot, decayed-peak
    // retention; handed to activations at setup, reclaimed at death.
    private readonly HeapBufferPool _heapPool = new();

    /// <summary>Test hook — the pooled buffer's capacity in cells (0 = empty).</summary>
    internal long PooledHeapCapacityCells => _heapPool.CapacityCells;

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
        /// the chunk's total length. Tracked so the chain
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
        /// free-list (persistent across queries) where the
        /// next <c>assertz</c> / <c>asserta</c> can reuse the bytes
        /// instead of extending the program buffer.</summary>
        public readonly List<(int Addr, int Length)> DeadChunks = new();
    }
    /// <summary>dynamic-chain metadata,
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

        /// <summary>live-linked static
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
    /// buffer is; replaced whenever the buffer is rebuilt or
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

    /// <summary>every engine born via SetupQueryFromTerm, weakly
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
    /// is synced back to the host; a non-owner (stale) engine
    /// instead marks the host buffer for rebuild — the store already holds
    /// the mutation, and the next setup re-derives dispatch from it.</summary>
    private void SyncOrInvalidateAfterMutation(Activation engine, bool ownedHostBuffer)
    {
        if (ownedHostBuffer) SyncPersistentFromEngine(engine);
        else InvalidatePersistent();
    }

    /// <summary>synchronises <see cref="_persistentProgram"/>
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

    // the generation lives in a shared GenerationBox handed to
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

    /// <summary>JIT indexing profile. Tracks per-predicate
    /// runtime call counts so the engine can defer building switch
    /// tables for a dynamic predicate until it proves hot. Set
    /// <c>engine.JitIndexing.Threshold</c> to tune (or, in tests, to
    /// force) the cold→hot transition.</summary>
    public JitIndexProfile JitIndexing => _jitIndexProfile;
    private readonly JitIndexProfile _jitIndexProfile = new();

    /// <summary>Pre-decoded compiled modules from any bundle loaded
    /// with a <see cref="BundleEntry.CompiledBytecode"/> blob
    ///. Future runtime paths
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

    // most-recent query's functor→address map, for the
    // profiler's address→name resolution. Null until the first query
    // under a profiling build.
    private IReadOnlyDictionary<int, int>? _profileFunctorAddresses;

    /// <summary>renders the current <see cref="Shumway.Core.Profiler"/>
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

        // nearest-predicate-at-or-below resolver for pc-keyed counters
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

    /// <summary>per-module set of BARE (un-mangled) local
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
    /// list of <c>Name/Arity</c> predicate indicators.
    /// Captured automatically when a runtime error escapes; available
    /// via <see cref="ShumwayPrologException.StackTrace"/>.</summary>
    public IReadOnlyList<(string Name, int Arity)> LastErrorStackTrace { get; private set; }
        = Array.Empty<(string, int)>();

    /// <summary>Source-position-enriched view of
    /// <see cref="LastErrorStackTrace"/>. Each frame carries
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
        // parent's compiled-static-predicate cache is valid
        // for it — pass it through so a meta-call's sub-engine query
        // doesn't recompile the whole program from scratch.
        foreach (var (fid, pred) in _staticPredicateCache)
            sub._staticPredicateCache[fid] = pred;
        _jitIndexProfile.CopyInto(sub._jitIndexProfile);
        return sub;
    }

    /// <summary>Snapshot of every module currently loaded into the engine.
    /// Useful for tests and tooling; the underlying objects are live and
    /// shouldn't be mutated directly.</summary>
    public IReadOnlyDictionary<string, ModuleManifest> Modules => _modules;

    /// <summary>every <c>:- mode</c> declaration the engine
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

    /// <summary>Adds an operator to the engine's parser table. Used by the
    /// runtime <c>op/3</c> builtin so user code can introduce operators
    /// that subsequent queries (and asserted clauses) will recognise.</summary>
    internal void DefineOperator(string name, int precedence, OperatorType type)
        => _operators.Define(name, precedence, type);

    /// <summary>Snapshot of every registered operator, as
    /// (Precedence, Type, Name) triples. Used by <c>current_op/3</c>
    /// to drive its backtracking enumeration.</summary>
    internal IEnumerable<(int Precedence, OperatorType Type, string Name)> EnumerateOperators()
        => _operators.Enumerate();

}
