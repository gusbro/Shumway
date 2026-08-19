using System.Numerics;

namespace Shumway.Core;

public sealed partial class Activation
{
    // ----- Current-query functor address map (Tier-1) -----
    //
    // Set by the embedding-layer query setup once per query, this map
    // gives the bytecode address of every functor in the linked program.
    // IL-emitted Execute opcodes resolve their tail-call target by
    // looking up the *functor id* (stable across queries) here, instead
    // of embedding the address as a constant (which would only be valid
    // for one query's linked layout).
    public IReadOnlyDictionary<int, int>? CurrentFunctorAddresses { get; set; }

    /// <summary>write_term's <c>portrayed(true)</c> hook: runs the user's
    /// portray/1 re-entrantly for a subterm, returning true when it
    /// produced the output. Wired by the embedding at query setup.</summary>
    public Func<Activation, Cell, System.IO.TextWriter, bool>? PortrayHook { get; set; }

    // ADR-038 — the per-query module import map for runtime variable meta-calls.
    // Keyed by a packed (moduleAtomId, bareFunctorId) → the mangled Source$name
    // functor id the importing module bound that name to. A '$mqual'-tagged goal
    // whose bare functor misses the module's own locals consults this before the
    // bare-global namespace, mirroring the compile-time ModuleRewrite import step.
    // Null when no loaded module imports anything (the common case).
    public IReadOnlyDictionary<long, int>? CurrentImportMap { get; set; }

    /// <summary>functor ids that a mid-query <c>consult/1</c>
    /// (from a live query, e.g. Logtalk's <c>'$lgt_load_prolog_code'</c>)
    /// live-linked into the running query's code space and made globally
    /// visible. A Call/Execute site compiled at THIS query's setup — before
    /// the consult — baked the undefined-procedure sentinel for these; the
    /// dispatcher resolves such a sentinel to the live-linked STATIC address
    /// only when the fid is on this set, so ordinary module-local
    /// invisibility is preserved for everything else. Null outside a
    /// live-consult query.</summary>
    public System.Collections.Generic.HashSet<int>? LiveConsultVisibleFids { get; set; }

    /// <summary>functor ids DECLARED by a `:- discontiguous` or
    /// `:- multifile` directive. Such a predicate is known even with no
    /// clauses: calling it FAILS rather than raising existence_error,
    /// whatever the <c>unknown</c> flag says. Installed at query setup;
    /// null when nothing declared one.</summary>
    public System.Collections.Generic.HashSet<int>? DeclaredEmptyFids { get; set; }

    /// <summary>runtime action for a call to an undefined
    /// procedure (the ISO <c>unknown</c> prolog flag, wired through
    /// dispatch via <see cref="UnknownProcedure.Fails"/>). Set by the
    /// embedding layer at query setup and updated live by
    /// <c>set_prolog_flag(unknown, _)</c>.</summary>
    public UnknownAction OnUnknown { get; set; } = UnknownAction.Error;

    /// <summary>ADR-034 — functor ids of dynamic predicates mutated
    /// (assert/retract/abolish) at any point in this host engine's lifetime.
    /// A REFERENCE to the embedding layer's host-lifetime set (shared across
    /// this host's per-query engines, single-threaded by the engine
    /// concurrency contract), installed at query setup. Compiled IL whose
    /// clause embeds an inlined dynamic-SNAPSHOT (a stable dynamic
    /// with rules, ADR-023/034) tests membership at clause entry via
    /// <see cref="IsDynMutated"/> and takes the un-inlined fallback path when
    /// the snapshot is stale — mutation mid-query is visible immediately
    /// because the set instance is shared, not copied.</summary>
    public System.Collections.Generic.HashSet<int>? MutatedDynamicFids;

    /// <summary>ADR-034 — the emitted clause-entry staleness test (see
    /// <see cref="MutatedDynamicFids"/>).</summary>
    public bool IsDynMutated(int functorId)
        => MutatedDynamicFids is { Count: > 0 } s && s.Contains(functorId);

    /// <summary>Goal-dispatch counter for <c>time/1</c> — incremented once
    /// per Tier-0 interpreter dispatch (Call / Execute / CallBuiltin and
    /// their Il/Bytecode/Builtin variants): the Shumway analogue of SWI's
    /// "inferences". A plain field increment, cheap enough to stay always-on
    /// (A/B-verified). Under Tier-1 promotion the count UNDERCOUNTS —
    /// intra-region calls are raw branches that never pass the interpreter —
    /// so it is honest for the (Tier-0 by default) REPL prototyping loop
    /// time/1 exists for.</summary>
    public long Inferences;

    /// <summary><c>time/1</c> marks — (wall ms, heap cells, inferences) at
    /// start / last report; index = the mark handle bound by
    /// <c>'$time_start'</c>. Activation-lifetime (one query), so the list stays
    /// tiny and needs no cleanup.</summary>
    public System.Collections.Generic.List<(double WallMs, long Cells, long Inferences)>? TimeMarks;

    /// <summary>Heap-buffer pooling — replaces the ctor-allocated heap with a
    /// buffer recycled from a previous activation, skipping the doubling
    /// ladder (each doubling is an alloc + copy; a query peaking at N cells
    /// re-pays ~log2(N/64K) of them from a cold buffer). Only legal before
    /// the activation has allocated anything (<see cref="HeapTop"/> == 0 —
    /// the host adopts right after construction, before query setup
    /// materializes the goal). A buffer smaller than the configured initial
    /// heap is ignored. The stale cell contents are never read: nothing
    /// reads the heap at or above <see cref="HeapTop"/>.</summary>
    public void AdoptHeapBuffer(Cell[] buffer)
    {
        if (_heapTop != 0)
            throw new InvalidOperationException(
                "AdoptHeapBuffer: the activation has already allocated heap.");
        if (buffer.Length < _heap.Length) return;
        _heap = buffer;
    }

    /// <summary>Heap-buffer pooling — surrenders the heap buffer to the host
    /// when the activation dies (its solution enumeration completed or was
    /// disposed; solutions hold materialized AST terms, never heap
    /// references). The activation keeps an empty heap so any accidental
    /// post-mortem allocation fails loudly instead of silently resurrecting
    /// the recycled buffer.</summary>
    public Cell[] DetachHeapBuffer()
    {
        var buffer = _heap;
        _heap = System.Array.Empty<Cell>();
        return buffer;
    }

    /// <summary>Stack-buffer pooling, mirroring <see cref="AdoptHeapBuffer"/>:
    /// replaces the (deliberately tiny, see the pooled query-setup config)
    /// constructor stack with a recycled buffer. Must run before the first
    /// frame is pushed; a smaller-than-current buffer is ignored.</summary>
    public void AdoptStackBuffer(Cell[] buffer)
    {
        if (buffer.Length < _stack.Length) return;
        _stack = buffer;
    }

    /// <summary>Mirror of <see cref="DetachHeapBuffer"/> for the stack.</summary>
    public Cell[] DetachStackBuffer()
    {
        var buffer = _stack;
        _stack = System.Array.Empty<Cell>();
        return buffer;
    }

    // ----- Meta-call route cache -----
    //
    // Shared by the bytecode interpreter's DispatchCall and Tier-1's
    // IlMetaCallHelper.Dispatch: maps a runtime goal's
    // ((atomId << 16) | totalArity) to its resolved dispatch route, so a
    // repeat meta-call of the same functor skips the functor intern, the
    // control-construct comparisons, the builtins probe and the
    // address-map probe. Stamped with the CurrentFunctorAddresses
    // instance it was built against; the dispatchers discard it when the
    // stamp no longer matches (a new query linked a new map). See
    // MetaRoute.cs for the soundness argument.
    public Dictionary<long, MetaRoute>? MetaRouteCache;
    public object? MetaRouteCacheStamp;

    /// <summary>Scratch buffer for <c>call/N</c>'s extra arguments while
    /// the goal's own arguments are loaded into the low X registers.
    /// Safe as a single per-engine buffer: the extras are consumed into
    /// registers before anything (builtin impl, recursive dispatch) can
    /// re-enter the meta-call path. Sized for call/8; larger arities
    /// fall back to a fresh allocation.</summary>
    public readonly Cell[] MetaExtraScratch = new Cell[8];

    /// <summary>Functor id of the user hook <c>verify_attributes/4</c>.
    /// Interned once; used by <see cref="MergeAttributes"/> to detect
    /// whether the program defines an attribute-unification hook (its
    /// presence in <see cref="CurrentFunctorAddresses"/> means it does).</summary>
    private static readonly int VerifyAttributesFunctorId =
        FunctorTable.Intern(AtomTable.Intern("verify_attributes", permanent: true).Id, 4);

    /// <summary>Functor id of the bare SICStus/Scryer hook
    /// <c>verify_attributes/3</c> — the per-module attribute-unification hook
    /// (<c>M:verify_attributes(Var, Value, Goals)</c>) that Scryer libraries
    /// (atts.pl, clpz, dif, freeze) define, distinct from our
    /// <c>verify_attributes/4</c>.</summary>
    private static readonly int BareVerify3FunctorId =
        FunctorTable.Intern(AtomTable.Intern("verify_attributes", permanent: true).Id, 3);

    /// <summary>Resolves module <paramref name="moduleId"/>'s Scryer-style
    /// <c>verify_attributes/3</c> hook to a linked functor id, or -1 if the module
    /// has none. Tries the export-qualified name <c>Module$verify_attributes/3</c>
    /// first (a `:- module(M,[..])` library mangles it), then the bare functor.</summary>
    internal int Verify3FunctorId(int moduleId)
    {
        var addrs = CurrentFunctorAddresses;
        if (addrs is null) return -1;
        string? mod = AtomTable.GetById(moduleId)?.Name;
        if (mod is not null)
        {
            int mangled = FunctorTable.Intern(
                AtomTable.Intern(mod + "$verify_attributes", permanent: true).Id, 3);
            if (addrs.ContainsKey(mangled)) return mangled;
        }
        return addrs.ContainsKey(BareVerify3FunctorId) ? BareVerify3FunctorId : -1;
    }

    /// <summary>Resolves module <paramref name="moduleId"/>'s
    /// <c>verify_attributes/4</c> hook (<c>M:verify_attributes(M, AttrValue,
    /// Value, Goals)</c>) to a linked functor id, or -1 if the module has none.
    /// Per-module (ADR-040): the module-local <c>Module$verify_attributes/4</c>
    /// first, so two dialects' constraint libraries each own their own hook and
    /// coexist. Falls back to the bare-global <c>verify_attributes/4</c> — the
    /// legacy shared multifile form a single library may still declare.</summary>
    internal int Verify4FunctorId(int moduleId)
    {
        var addrs = CurrentFunctorAddresses;
        if (addrs is null) return -1;
        string? mod = AtomTable.GetById(moduleId)?.Name;
        if (mod is not null)
        {
            int mangled = FunctorTable.Intern(
                AtomTable.Intern(mod + "$verify_attributes", permanent: true).Id, 4);
            if (addrs.ContainsKey(mangled)) return mangled;
        }
        return addrs.ContainsKey(VerifyAttributesFunctorId) ? VerifyAttributesFunctorId : -1;
    }

    private IReadOnlyDictionary<int, int>? _attrHookScanFor;
    private bool _hasAnyAttrHook;

    /// <summary>True when SOME module defines an attribute-unification hook —
    /// a bare or module-local <c>verify_attributes/3</c> OR <c>/4</c> (ADR-040:
    /// the module-local <c>Module$verify_attributes/N</c> forms let two dialects'
    /// libraries coexist). Scanned once per distinct
    /// <see cref="CurrentFunctorAddresses"/> (attribute hooks are static, so it is
    /// stable for the query) and cached — gates the wakeup fast path so a hookless
    /// attributed-variable program pays nothing. This gate MUST see the
    /// module-local forms, or a real hook's wakeups would be silently cleared.</summary>
    internal bool HasAnyAttributeHook
    {
        get
        {
            var addrs = CurrentFunctorAddresses;
            if (addrs is null) return false;
            if (!ReferenceEquals(addrs, _attrHookScanFor))
            {
                _hasAnyAttrHook = addrs.ContainsKey(BareVerify3FunctorId)
                    || addrs.ContainsKey(VerifyAttributesFunctorId);
                if (!_hasAnyAttrHook)
                    foreach (int fid in addrs.Keys)
                    {
                        var (nameId, arity) = FunctorTable.Lookup(fid);
                        if ((arity == 3 || arity == 4)
                            && (AtomTable.GetById(nameId)?.Name.EndsWith(
                                    "$verify_attributes", System.StringComparison.Ordinal) ?? false))
                        { _hasAnyAttrHook = true; break; }
                    }
                _attrHookScanFor = addrs;
            }
            return _hasAnyAttrHook;
        }
    }

    /// <summary>Whether attribute module <paramref name="moduleId"/> has any
    /// unification hook — a per-module <c>verify_attributes/4</c> or a Scryer-style
    /// <c>verify_attributes/3</c>. Used by the merge rule to decide whether a shared
    /// module's values must be pre-unified (no hook) or left for the hook.</summary>
    internal bool ModuleHasHook(int moduleId) =>
        Verify4FunctorId(moduleId) >= 0 || Verify3FunctorId(moduleId) >= 0;

    /// <summary>Per-query string literal pool. Set by the embedding
    /// layer at query setup so IL-emitted <c>get_pstr</c> / <c>put_pstr</c>
    /// opcodes can resolve a literal id to its string at
    /// runtime — same lookup the bytecode interpreter does, but
    /// accessible from the Activation surface so Tier-1 IL doesn't need
    /// to carry its own pool reference.</summary>
    public IReadOnlyList<string>? CurrentStringLiterals { get; set; }

    /// <summary>Per-query bytecode program, set alongside
    /// <see cref="CurrentFunctorAddresses"/>. IL-emitted <c>Call</c>
    /// opcodes re-enter the bytecode interpreter on this
    /// program to run sub-predicates synchronously. ADR-015: the
    /// program grows — a dynamic predicate modified mid-query is
    /// recompiled and appended via <see cref="AppendCode"/>.</summary>
    public byte[]? CurrentProgram { get; set; }

    private int _programLength = -1;

    /// <summary>Logical length of the program (ADR-015).
    /// <see cref="CurrentProgram"/> is over-allocated — capacity grows by
    /// doubling — so <see cref="AppendCode"/> is amortised O(1) instead of
    /// copying the whole buffer each call. The slack tail is zero (the
    /// Invalid opcode), so a stray PC into it still fails loudly.</summary>
    public int ProgramLength =>
        _programLength >= 0 ? _programLength : (CurrentProgram?.Length ?? 0);

    /// <summary>the persistent dynamic-code buffer is
    /// over-allocated up front (so mid-query assertz extends without
    /// re-copy), so the engine needs to know its live length explicitly.
    /// Sets <see cref="ProgramLength"/> on a fresh engine before any
    /// <see cref="AppendCode"/> call. </summary>
    public void SetInitialProgramLength(int length) => _programLength = length;

    /// <summary>the per-query overlay buffer holding the
    /// synthetic <c>__query__</c> clause and its auxiliaries. Lives at
    /// logical addresses ≥ <see cref="CurrentQuerySplit"/>; addresses
    /// below the split index into <see cref="CurrentProgram"/>. Null
    /// when there's no overlay (e.g. a sub-engine, IL re-entry path
    /// driving over the persistent buffer alone).</summary>
    public byte[]? CurrentQueryOverlay { get; set; }

    /// <summary>The logical address at which <see cref="CurrentQueryOverlay"/>
    /// starts. Addresses in <c>[0, Split)</c> live in
    /// <see cref="CurrentProgram"/>; addresses in
    /// <c>[Split, Split + Overlay.Length)</c> live in the overlay.</summary>
    public int CurrentQuerySplit { get; set; }

    /// <summary>The two-buffer logical view used by the interpreter
    /// dispatch loop's hot-path refresh after a mid-query
    /// <see cref="AppendCode"/>. Reads stay correct across the
    /// realloc-and-grow path.</summary>
    public Shumway.Core.ProgramView GetProgramView()
    {
        var prog = CurrentProgram ?? Array.Empty<byte>();
        if (CurrentQueryOverlay is null) return new Shumway.Core.ProgramView(prog);
        return new Shumway.Core.ProgramView(prog, CurrentQueryOverlay, CurrentQuerySplit);
    }

    /// <summary>per-query switch tables, wired by the
    /// embedding layer at query setup as a mutable list so the
    /// new-key assertz path can add bucket keys in place
    /// by swapping the entry at a given table id.</summary>
    public System.Collections.Generic.List<Shumway.Core.SwitchTable>? SwitchTables { get; set; }

    /// <summary>Helper: returns the switch table at the given index
    /// or <c>null</c> when out of range / not wired. Used by
    /// PrologEngine's in-place assertz path to look up
    /// the bucket chain head for a new clause's key.</summary>
    public Shumway.Core.SwitchTable? GetSwitchTable(int id)
    {
        var tables = SwitchTables;
        if (tables is null || id < 0 || id >= tables.Count) return null;
        return tables[id];
    }

    /// <summary>replaces the switch table at
    /// <paramref name="id"/>, used by the new-key assertz path to
    /// extend a switch table with an additional <c>(key →
    /// bucket-chain-head)</c> entry. The interpreter reads through
    /// the list reference each dispatch, so the replacement takes
    /// effect immediately.</summary>
    public void ReplaceSwitchTable(int id, Shumway.Core.SwitchTable table)
    {
        var tables = SwitchTables;
        if (tables is null || id < 0 || id >= tables.Count) return;
        tables[id] = table;
    }

    /// <summary>Appends a linked bytecode chunk to <see cref="CurrentProgram"/>
    /// and returns its start offset. Existing offsets stay valid — the
    /// content is only ever appended, never moved. Capacity doubling keeps
    /// a long-running query's repeated dynamic recompiles from re-copying
    /// the whole (growing) buffer each time.</summary>
    public int AppendCode(byte[] chunk)
    {
        byte[] program = CurrentProgram ?? Array.Empty<byte>();
        int offset = ProgramLength;
        int needed = offset + chunk.Length;
        if (needed > program.Length)
        {
            var grown = new byte[Math.Max(needed, program.Length * 2)];
            Array.Copy(program, grown, offset);
            CurrentProgram = program = grown;
            // bumping the generation tells the interpreter's
            // dispatch loop to refresh its cached ProgramView. Plain
            // (in-place) byte writes don't change the array reference,
            // so they don't need a bump; only a reallocation does.
            _programGeneration++;
        }
        Array.Copy(chunk, 0, program, offset, chunk.Length);
        _programLength = needed;
        return offset;
    }

    /// <summary>Monotonic counter bumped whenever the bytecode
    /// program's underlying array reference changes (a reallocation
    /// inside <see cref="AppendCode"/>, an embedding-layer rewire of
    /// <see cref="CurrentProgram"/> / <see cref="CurrentQueryOverlay"/>
    /// / <see cref="CurrentQuerySplit"/>). The bytecode interpreter
    /// caches its <see cref="ProgramView"/> across dispatch iterations
    /// and only refreshes when this generation has changed — the
    /// per-iteration <c>GetProgramView()</c> call was measurable on
    /// Blint.pl's hot loop.</summary>
    public int ProgramGeneration => _programGeneration;
    private int _programGeneration;

    /// <summary>Bump after the embedding layer rewires program /
    /// overlay / split fields directly (e.g. the per-
    /// query reset of <see cref="CurrentQueryOverlay"/>). The
    /// interpreter then picks up the new view on its next dispatch
    /// iteration.</summary>
    public void BumpProgramGeneration() => _programGeneration++;

    /// <summary>The dynamic-database generation the currently-running
    /// goal saw when it entered (ADR-015, bytecode-level
    /// dispatch). Sampled by the upcoming <c>EnterDynamic</c> opcode at
    /// every dynamic-predicate entry, captured into each choice point's
    /// <c>ViewGen</c> slot by <c>try_me_else</c>, restored on
    /// <c>retry_me_else</c>. The upcoming <c>CheckVisible</c> instruction
    /// reads this against a clause's <c>born</c> / <c>died</c> to honour
    /// the ISO logical update view. Zero outside dynamic dispatch.</summary>
    public long CurrentViewGen { get; set; }

    /// <summary>Name of the builtin currently executing, set by the
    /// <c>CallBuiltin</c> dispatch right before invoking the impl. Read
    /// by <c>IsoError</c> when constructing an <c>error/2</c> term so the
    /// Context slot reports the offending builtin as <c>Name/Arity</c>
    /// rather than a fresh anonymous variable (the impl-defined identity
    /// ISO §7.12.2 calls for). Never reset on impl return — the next
    /// builtin dispatch overwrites it, and on an exception unwind the
    /// last-set value is exactly the one we want. <c>null</c> outside
    /// any builtin (during interpreter-emitted opcodes, IL-compiled
    /// bodies, or the embedding-layer query plumbing).</summary>
    public string? CurrentBuiltinName { get; set; }

    /// <summary>Arity companion to <see cref="CurrentBuiltinName"/>.</summary>
    public int CurrentBuiltinArity { get; set; }

    /// <summary>Per-engine stream registry. Wired by the
    /// embedding layer at query setup; <c>StreamBuiltins</c> uses it
    /// for every <c>open/close/read/write</c> dispatch so handles
    /// outlive any one query.</summary>
    public StreamRegistry? Streams { get; set; }

    /// <summary>Free-list of dead-clause bytecode regions
    /// available for reuse by within-query incremental
    /// <c>assertz</c> / <c>asserta</c>. Lives on <see cref="Activation"/>
    /// purely as legacy ABI — the live free-list moved
    /// to <c>PrologEngine</c>'s persistent buffer so chunks freed in
    /// one query are reusable by the next. Not consulted by any
    /// current code path; kept as a no-op holder for any external
    /// caller that still references it.</summary>
    public readonly List<(int Addr, int Length)> FreeChunks = new();

    /// <summary>Wired by the embedding layer at query setup —
    /// materialises a <see cref="Cell"/> into the AST <c>Term</c>
    /// type (held as an opaque <c>object</c> here because Core can't
    /// reference the AST namespace). Used by
    /// <see cref="PrologRuntimeException"/>'s value-carrying
    /// constructor so a throwing builtin can snapshot
    /// the offending term into the error's value slot; eager
    /// materialisation lets the value survive sub-engine teardown.
    /// </summary>
    public Func<Cell, object?>? MaterializeCellToTerm { get; set; }

    /// <summary>Embedding-supplied resolver from an absolute bytecode
    /// address to a human-readable <c>"name/arity@offset"</c> string,
    /// used by the opt-in <c>SHUMWAY_CP_TRACE</c> dump
    /// (<c>ChoicePointTrace.DumpAtSite</c>) to label each live
    /// choice-point's saved BP. Returns <c>null</c> when the address
    /// falls outside any known predicate range. Wired by
    /// <c>PrologEngine</c> at query setup against the same
    /// <c>_currentPredicatesByAddress</c> the stack-trace resolver
    /// uses.</summary>
    public Func<int, string?>? ResolveAddressToLabel { get; set; }

    /// <summary>Cheap-throw resolver: (ball heap index, min catch-frame
    /// index) → the recovery's code address, or −1 when no frame at/above
    /// the floor catches. Installed by the embedding layer (it owns the
    /// catch-frame matching); the interpreter uses it to handle a
    /// <c>throw/1</c> whose catcher lives in the SAME dispatch invocation
    /// with a plain PC jump instead of a .NET exception.</summary>
    public Func<int, int, int>? InlineThrowResolver { get; set; }

    /// <summary>Resolves a thrown ball against catch/3 frames opened INSIDE a
    /// nested in-engine goal (a verify_attributes wakeup, a findall driver):
    /// given the C# exception and the frame-stack depth at the nested goal's
    /// entry, returns the recovery address after rolling the machine back to
    /// the matching frame, or -1 (frame outside the nested goal, or no match)
    /// to let the exception unwind to the outer driver. Installed by the host
    /// at query setup — the ball exception type lives above Core. Without
    /// this, a caught throw inside a wakeup unwound the nested C# dispatch
    /// frame itself: the recovery resumed in the OUTER loop and the
    /// interrupted unification's continuation was silently lost (clpz's
    /// with_local_attributes made the whole query "succeed" doing
    /// nothing).</summary>
    public Func<Exception, int, int>? NestedCatchResolver { get; set; }

    /// <summary>Last-chance resolution of a functor the address map does not hold:
    /// the host materializes a runtime-assert MetaTransform helper (compiled by a
    /// DIFFERENT activation's assert — see PrologEngine.TryMaterializeAssertHelper)
    /// into THIS activation on demand. Returns the linked address, or -1. Consulted
    /// by the dispatchers right before raising existence_error.</summary>
    public Func<int, int>? ResolveLateHelper { get; set; }

    /// <summary>The consult-direct bare-call fallback: a bare goal no other
    /// route resolved is resolved to a DIRECTLY-consulted explicit module's
    /// local when exactly ONE such module defines the name — consulting a
    /// source means being able to call its predicates, module directive or
    /// not. Two candidates throw the ambiguity existence_error (qualify to
    /// choose); a module loaded only as a use_module dependency never
    /// participates. Returns the address, or -1. Consulted by the
    /// dispatchers after <see cref="ResolveLateHelper"/>, right before
    /// <see cref="UnknownProcedure.Fails"/>.</summary>
    public Func<int, int>? ResolveModuleLocalFallback { get; set; }

    /// <summary>Re-entrant semidet solve of a goal on THIS live activation, reusing
    /// the already-linked program (no fresh transient-region link, no new machine) —
    /// the cheap host→Prolog path for a foreign predicate that calls back into Prolog
    /// mid-execution (<c>C#→Prolog</c> in the <c>C#→main→C#→predX</c> pattern). Wired by
    /// the interpreter's <c>Run</c> to its <c>MetaCallInEngine</c>; runs the goal cell to
    /// its first solution with once-semantics (choice points it leaves are discarded),
    /// leaving the bindings on the shared heap/trail. Returns success. Contrast
    /// <see cref="Host"/>'s top-level <c>QueryAll</c>, which builds a whole new query.</summary>
    public Func<Cell, bool>? ReentrantSolve { get; set; }

    // Pooled variable maps for the re-entrant SolveOnce read path: a map must survive
    // materialize→solve→read within one call (the nested solve clobbers the shared
    // MaterializeScratchMap), so it is rented per call and returned after. A stack
    // makes it nesting-safe — each nested SolveOnce rents its own.
    private System.Collections.Generic.Stack<System.Collections.Generic.Dictionary<string, int>>? _reentrantMapPool;

    /// <summary>Rents a cleared variable map for a re-entrant <c>SolveOnce</c> read.</summary>
    public System.Collections.Generic.Dictionary<string, int> RentReentrantVarMap()
    {
        var pool = _reentrantMapPool;
        if (pool is not null && pool.Count > 0)
        {
            var d = pool.Pop();
            d.Clear();
            return d;
        }
        return new System.Collections.Generic.Dictionary<string, int>();
    }

    /// <summary>Returns a map rented via <see cref="RentReentrantVarMap"/>.</summary>
    public void ReturnReentrantVarMap(System.Collections.Generic.Dictionary<string, int> map)
        => (_reentrantMapPool ??= new()).Push(map);

    /// <summary>ADR-041 — dispatch-time clause selection for an unindexed
    /// dynamic chain, called at <c>enter_dynamic</c> with the trampoline's
    /// address. The host inspects the call's (dereferenced) first argument
    /// against the chain entries' first-arg keys and returns: an absolute
    /// jump address (exactly ONE candidate clause — dispatch jumps there with
    /// NO choice point), <c>-1</c> (ZERO candidates — the call fails without
    /// walking the chain), or <c>-2</c> (no selection: unbound argument,
    /// several candidates, indexed/unrecognised layout — the chain runs
    /// unchanged). Determinism must be uniform across tiers and JIT hotness;
    /// this hook is what makes the cold Tier-0 chain honour that.</summary>
    public Func<Activation, int, int>? DynChainSelect { get; set; }

    /// <summary>ISO number_chars/number_codes reads the characters as a TERM that
    /// must be a number (§8.16.8) — so `'-'1` (a quoted prefix minus) reads as -1,
    /// as does `- /**/1`. The custom number-token parser in
    /// <c>AtomCharBuiltins.TryBuildPrologNumber</c> handles the common cases; this
    /// host hook is the fallback that runs the FULL term reader when that fails,
    /// returning the boxed number (long / double / System.Numerics.BigInteger) or
    /// null when the chars are not a number. Wired by <c>PrologEngine</c> at query
    /// setup; kept as <c>object?</c> so <c>Shumway.Core</c> need not reference the
    /// parser's AST types.</summary>
    public Func<string, object?>? NumberFromChars { get; set; }

    /// <summary>Absolute byte position of the per-query fail-stub
    /// (ADR-015) — a tiny <c>call_builtin fail/0</c>
    /// emitted in the prefix. Dynamic predicates' last-clause chain
    /// instructions point here as their "no more clauses" target; an
    /// empty dynamic predicate's trampoline jumps here directly. Set by
    /// the embedding layer at query setup; zero outside dynamic
    /// dispatch.</summary>
    public int DynamicFailStubAddr { get; set; }

    /// <summary>Reads the host's current dynamic-database generation.
    /// Wired by the embedding layer at query setup so the
    /// <c>enter_dynamic</c> opcode can sample it without the interpreter
    /// having to depend on the embedding layer's types. kept as
    /// the fallback for bare-Activation tests; the production path is
    /// <see cref="DbGenerationBox"/>, which the interpreter checks first.</summary>
    public Func<long>? DbGenerationProvider { get; set; }

    /// <summary>the host's generation clock as a shared
    /// <see cref="GenerationBox"/>: <c>enter_dynamic</c> reads
    /// <c>Box.Value</c> directly (no delegate invoke per dynamic call).
    /// Null for bare engines, which fall back to
    /// <see cref="DbGenerationProvider"/>.</summary>
    public GenerationBox? DbGenerationBox { get; set; }

    /// <summary>ADR-015: refreshes the interpreter's
    /// literal pools after an <c>assertz</c> / <c>asserta</c> may have
    /// interned a new string / float / bigint literal. Wired at query
    /// setup; the incremental assert paths invoke it.</summary>
    public Action<IReadOnlyList<string>, IReadOnlyList<double>,
        IReadOnlyList<System.Numerics.BigInteger>>? RefreshLiteralPoolsCallback { get; set; }

    /// <summary>Snapshot of <see cref="CurrentViewGen"/> from a given CP
    /// — exposed so the choice-point save/restore stays inside
    /// <c>PushChoicePoint</c> / <c>RestoreCommonFromCurrentCp</c>.</summary>
    public long ViewGenOf(int cpBase, int arity) =>
        _stack[cpBase + CpViewGenOffset(arity)].Payload;   // strip RawInt tag

    // IlSubroutineRunner / BacktrackRunner / SetBacktrackFloor callbacks were
    // deleted when IL non-tail Call dispatch switched to threaded
    // continuation. The threaded design uses resume markers
    // and the natural CP cascade — no recursive
    // sub-engine, no separate backtrack driver, no floor pin.

    /// <summary>Walks the environment-frame chain starting at the
    /// current frame, yielding each frame's saved return address
    /// (<c>CP</c>) — the bytecode location the caller will resume at
    /// when the current procedure proceeds. The embedding layer
    /// translates these to predicate names via the per-query address
    /// map to assemble a stack trace at error reporting time
    ///.</summary>
    /// <summary>Walks the active choice-point chain from the current
    /// CP toward the root. Each yielded triple is
    /// <c>(stackB, savedBp, arity)</c> where <c>savedBp</c> is the
    /// next-clause address recorded at CP push time
    /// (<see cref="IlChoicePointSentinelBp"/> for IL-side CPs and
    /// builtin CPs that route through the IL pop path). Used by the
    /// opt-in <c>SHUMWAY_CP_TRACE</c> diagnostic to dump the live
    /// CP stack at suspicious error sites.</summary>
    /// <summary>The continuation (caller return pc) saved in the CP frame at
    /// stack slot <paramref name="b"/> — attributes an otherwise anonymous
    /// builtin/IL CP to the predicate that was running when it was pushed.</summary>
    public int CpSavedContinuation(int b)
    {
        int arity = (int)_stack[b + CpArityOffset].Data;
        return (int)_stack[b + CpCpOffset(arity)].Data;
    }

    public IEnumerable<(int StackB, int SavedBp, int Arity)> EnumerateChoicePoints()
    {
        int b = _b;
        // The CP chain is anchored at _b == -1 (no CPs left). Each frame
        // stores the previous B at CpBOffset(arity). Walk until we hit
        // the sentinel.
        while (b >= 0)
        {
            int arity = (int)_stack[b + CpArityOffset].Data;
            int bp = (int)_stack[b + CpBpOffset(arity)].Data;
            int prevB = (int)_stack[b + CpBOffset(arity)].Data;
            yield return (b, bp, arity);
            if (prevB == b) yield break;
            b = prevB;
        }
    }

    /// <summary>ADR-035 — how many environment frames are live: the logical call
    /// depth. A step controller recomputes this at every port rather than counting
    /// calls and returns incrementally, because it must stay right across last-call
    /// optimisation, cuts, and the opaque predicates a <c>:- disable_debug.</c>
    /// region compiles — all of which change the depth without telling anyone.</summary>
    public int EnvDepth => EnvDepthFrom(_e);

    /// <summary>ADR-035 — the environment depth the machine will be at once the top
    /// choice point is resumed. The redo port fires before the retry instruction has
    /// restored anything, so the depth of the computation that just <i>failed</i>
    /// (which can be arbitrarily deeper) says nothing about the goal being retried.
    /// The choice point itself carries the environment its clause will run in, so
    /// read the depth from there instead. An IL choice point (a backtrackable builtin
    /// re-satisfying) does not restore an environment, so the current one stands.
    /// </summary>
    public int PendingRedoEnvDepth => PendingRedoEnvDepthCapped(int.MaxValue - 1);

    /// <summary>The redo port's depth, counted no further than <paramref name="cap"/> — see
    /// <see cref="EnvDepthCapped"/>. A step in flight asks this at every backtrack, and a
    /// program that backtracks hard backtracks deep.</summary>
    public int PendingRedoEnvDepthCapped(int cap)
    {
        if (_b < 0 || TopChoicePointIsIl) return EnvDepthFrom(_e, cap);
        int arity = (int)_stack[_b + CpArityOffset].Data;
        return EnvDepthFrom((int)_stack[_b + CpCeOffset(arity)].Data, cap);
    }

    /// <summary>ADR-035 — the depth, but never counted past <paramref name="cap"/>: the
    /// answer is exact while it is at most <paramref name="cap"/>, and <c>cap + 1</c> for
    /// anything deeper.
    ///
    /// <para>Which is all a step needs, and the difference between a debugger and a
    /// stopwatch. A step's condition compares the depth against the depth it was taken FROM,
    /// so every port deeper than that is uninteresting — but counting it costs a walk of the
    /// whole environment chain, and a step over a goal that runs for a while passes millions
    /// of ports at whatever depth that goal reaches. Stepping over one Blint goal took 140
    /// seconds against 20 without a debugger: not the program, the counting. Walking from the
    /// top and stopping at the cap makes the cost the STEP's depth (four or five frames),
    /// not the program's.</para></summary>
    public int EnvDepthCapped(int cap) => EnvDepthFrom(_e, cap);

    private int EnvDepthFrom(int e) => EnvDepthFrom(e, int.MaxValue - 1);

    private int EnvDepthFrom(int e, int cap)
    {
        int depth = 0;
        while (e >= 0)
        {
            depth++;
            if (depth > cap) return depth;   // deeper than anybody asked about
            int prevE = (int)_stack[e + EnvCeOffset].Data;
            if (prevE == e || prevE < 0) break;
            e = prevE;
        }
        return depth;
    }

    public IEnumerable<int> EnumerateCallReturnAddresses() =>
        EnumerateCallReturnAddresses(_e, _cp);

    /// <summary>The return-address chain as it stands in a given environment /
    /// continuation pair, rather than the current one. ADR-035 uses it at the redo
    /// port, where the machine is still standing in the computation that failed but
    /// what the debugger must show is the one about to be retried — the choice point
    /// carries the environment its clause will run in.</summary>
    public IEnumerable<int> EnumerateCallReturnAddresses(int e, int cp)
    {
        // The first frame to surface is the IMMEDIATE return target —
        // cp is the caller's "next instruction after Call". After that
        // we walk env frames; each frame stores the *caller's* CP at
        // EnvCpOffset, and EnvCeOffset chains back to the next frame
        // up the call tree.
        //
        // The first environment on the chain is the CURRENT clause's, when it has one, and
        // `allocate` saved cp into it — so its stored CP duplicates the address just
        // yielded. Only THAT one does: don't generalize the skip to a value comparison
        // anywhere on the chain — in a recursive predicate every frame stores the same
        // address, so all of them match and a 500-deep recursion collapses to a
        // two-frame stack. Skip the first if it duplicates; take the rest as they come.
        if (cp >= 0) yield return cp;
        bool first = true;
        while (e >= 0)
        {
            int frameCp = (int)_stack[e + EnvCpOffset].Data;
            if (frameCp >= 0 && !(first && frameCp == cp)) yield return frameCp;
            first = false;
            int prevE = (int)_stack[e + EnvCeOffset].Data;
            if (prevE == e || prevE < 0) yield break;
            e = prevE;
        }
    }

    /// <summary>The environment and continuation the top choice point will restore —
    /// the state its retried clause runs in. See <see cref="PendingRedoEnvDepth"/>.
    /// Returns the current pair for an IL choice point, which restores neither.</summary>
    public (int E, int Cp) TopChoicePointContext
    {
        get
        {
            if (_b < 0 || TopChoicePointIsIl) return (_e, _cp);
            int arity = (int)_stack[_b + CpArityOffset].Data;
            return ((int)_stack[_b + CpCeOffset(arity)].Data,
                    (int)_stack[_b + CpCpOffset(arity)].Data);
        }
    }

    // ----- IL tail-call signal (Tier-1) -----
    //
    // When an IL delegate emits an Execute opcode, it sets _pc to the
    // tail-call target and raises this flag. The interpreter's Call /
    // Execute handlers consult the flag after the IL returns: when set,
    // they leave _pc alone instead of overriding it with _cp, so the
    // dispatch picks up at the target rather than returning to the
    // caller's continuation immediately. Cleared by the handler that
    // observes it.
    public bool IlTailCallPending { get; set; }

    // ----- IL choice points (Tier-1) -----
    //
    // A side table mapping a choice-point frame's stack index to the IL
    // delegate + cursor that should run when backtracking pops that frame.
    // The CP frame itself uses a sentinel BP (-1) so the bytecode
    // interpreter's standard PC-based backtrack path doesn't accidentally
    // jump into bytecode 0xFFFFFFFF.
    public const int IlChoicePointSentinelBp = -1;
    // _ilCpInfo is a stack-array (not a Dictionary) in favour of
    // the stack-array _ilCpStack/_ilCpTop declared just above (with
    // the IlChoicePointEntry struct).

    /// <summary>per-engine slot for the IL indexed-dispatch
    /// cache (the typed dictionary lives in Compiler.Il and Core can't
    /// name its type). Previously a
    /// <c>ConditionalWeakTable&lt;Activation, ConcurrentDictionary&gt;</c>
    /// in <c>IlIndexedDispatch._perEngineCache</c> — every IL Call to
    /// an indexed predicate paid an internal ConditionalWeakTable
    /// lock + a ConcurrentDictionary bucket lock. Activation is single-
    /// threaded and the cache lives exactly as long as the engine, so
    /// a plain instance field is both safe and free of those internal
    /// locks. Compiler.Il accesses it via an <c>is</c> pattern check
    /// to the typed Dictionary, which the JIT compiles to a single
    /// type-token compare (no Dictionary boxing / cast).</summary>
    public object? IlIndexedDispatchCache;

    // threaded Tier-1 dispatch. An IL non-tail Call site sets
    // engine.Cp to a *resume marker* address instead of recursing into
    // RunSubroutine. When the callee Proceeds (Pc = Cp), the bytecode
    // interpreter's main loop sees the marker, decodes it back to
    // (functorId, cursor), looks up the IL delegate via the active
    // Tier1Dispatcher, and invokes it at the right cursor. A marker is an
    // opaque int, so saving / restoring Cp around frames just works.
    //
    // encoding. The original arithmetic encoding
    // (Base + functorId * 4096 + cursor) capped the functor id at ~262 143
    // before markers overflowed int — a LIVE ceiling: the full test suite's
    // functor table crosses it (proven when a naming experiment minted fresh
    // helper atoms per query and marker users started failing mid-suite).
    // Markers are now DENSE IDS into a process-global side table of
    // (functorId, cursor) pairs:
    //   marker = ResumeMarkerBase + denseId
    // The table is process-global because markers are baked as constants into
    // IL delegates that are shared across engines; entries are interned
    // (one id per distinct pair) and never removed — capacity is
    // int.MaxValue - Base ≈ 1.07 B distinct pairs, effectively unbounded.
    // Same lock-free-read / locked-intern discipline as the atom/functor
    // tables.
    //
    // ResumeMarkerBase is set high enough that no plausible bytecode
    // address collides (the per-query overlay lives at
    // PersistentToQueryGap which is ~64 MB — markers start at 1 GB).
    // ResumeMarkerCursorStride is no longer part of the encoding; it
    // survives only as the IL emitters' per-predicate cursor-count BUDGET
    // (an emit-shape policy, not a correctness cap).
    public const int ResumeMarkerBase = 0x4000_0000;
    public const int ResumeMarkerCursorStride = 4096;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, int>
        _resumeMarkerByPair = new();
    private static (int Fid, int Cursor)[] _resumeMarkerPairs = new (int, int)[4096];
    private static int _resumeMarkerCount;
    private static readonly object _resumeMarkerLock = new();

    public static bool IsResumeMarker(int address) => address >= ResumeMarkerBase;

    public static int EncodeResumeMarker(int functorId, int cursor)
    {
        if (cursor < 0)
            throw new ArgumentOutOfRangeException(nameof(cursor), $"cursor must be ≥ 0; got {cursor}.");
        long key = ((long)functorId << 32) | (uint)cursor;
        // Fast path: already interned (lock-free dictionary read).
        if (_resumeMarkerByPair.TryGetValue(key, out int id))
            return ResumeMarkerBase + id;
        lock (_resumeMarkerLock)
        {
            if (_resumeMarkerByPair.TryGetValue(key, out id))
                return ResumeMarkerBase + id;
            id = _resumeMarkerCount;
            var pairs = _resumeMarkerPairs;
            if (id >= pairs.Length)
            {
                var grown = new (int, int)[pairs.Length * 2];
                Array.Copy(pairs, grown, pairs.Length);
                System.Threading.Volatile.Write(ref _resumeMarkerPairs, grown);
                pairs = grown;
            }
            pairs[id] = (functorId, cursor);
            _resumeMarkerCount = id + 1;
            // Publish LAST: a reader that finds the id in the dictionary is
            // guaranteed to see the pair write (the dictionary write has
            // release semantics).
            _resumeMarkerByPair[key] = id;
            return ResumeMarkerBase + id;
        }
    }

    public static (int FunctorId, int Cursor) DecodeResumeMarker(int address)
        => System.Threading.Volatile.Read(ref _resumeMarkerPairs)[address - ResumeMarkerBase];

    /// <summary>Region compilation — at a region member's proceed, decode
    /// the continuation (<see cref="Cp"/>): if it is a resume marker INTO this
    /// region (functor id == <paramref name="regionRootFunctorId"/>) the member's
    /// proceed continues inside the region's IL method at the returned cursor (the
    /// emit does an intra-method <c>br</c>); otherwise (a different functor, or not
    /// a marker at all — the region's own caller-continuation) it returns −1 and the
    /// member returns to the dispatch loop, which runs <c>Cp</c>.</summary>
    public int RegionReturnCursor(int regionRootFunctorId)
    {
        int cp = _cp;
        if (!IsResumeMarker(cp)) return -1;
        var (fid, cursor) = DecodeResumeMarker(cp);
        return fid == regionRootFunctorId ? cursor : -1;
    }

    private struct IlChoicePointEntry
    {
        public Func<Activation, int, bool> Del;
        public int Cursor;
        // the _b value at PushIlChoicePoint time. Lets
        // Cut(barrier) compare against the IL CP stack without going
        // through the Dictionary's KeyCollection (which was the
        // PopIlChoicePointAndRestore + Activation.Cut hot path: ~5.31%
        // self-time on Activation.Cut from the foreach over Keys, plus
        // ~1.55% from FindValue/MoveNext on dict ops in profiling
        // Blint with bundled user IL).
        public int Key;
        // optional cleanup callback invoked when this
        // CP is discarded without ever being backtracked into (cut
        // pruning, or — eventually — engine teardown). Non-det
        // foreign predicates supply iter.Dispose here so a
        // generator that holds non-managed resources gets
        // deterministic cleanup when Prolog `!` commits past its
        // choice point. Null for the (vast majority of) IL CPs
        // that have no extra-engine state to release.
        public Action? OnPrune;
    }

    // stack-array replacement for the previous
    // Dictionary<int, IlChoicePointEntry> _ilCpInfo. IL CPs are
    // always pushed in monotonic _b order (each CP push grows _b)
    // and popped (or cut) from the top — same shape as a plain
    // stack. Direct array index + a parallel _ilCpTop pointer
    // replaces dict hash/probe per op. Grown copy-on-resize when
    // _ilCpTop reaches capacity.
    private IlChoicePointEntry[] _ilCpStack = new IlChoicePointEntry[64];
    private int _ilCpTop;

    /// <summary>Pushes a choice point that, on backtrack, re-enters an IL
    /// delegate at <paramref name="nextCursor"/> instead of jumping to a
    /// bytecode address. State preservation matches the bytecode CP
    /// machinery exactly — the only difference is what happens at retry
    /// time.</summary>
    public void PushIlChoicePoint(Func<Activation, int, bool> del, int nextCursor, int arity)
        => PushIlChoicePoint(del, nextCursor, arity, onPrune: null);

    /// <summary>Overload that additionally registers an
    /// <paramref name="onPrune"/> callback invoked exactly once if
    /// this CP is discarded without ever being backtracked into
    /// (cut pruning). The callback fires before the entry's
    /// delegate reference is released; if it throws, the
    /// exception propagates and the remaining CPs above the
    /// barrier are <em>not</em> pruned — callers should keep the
    /// callback small and safe (a single Dispose, no
    /// arbitrary user code).</summary>
    public void PushIlChoicePoint(
        Func<Activation, int, bool> del, int nextCursor, int arity, Action? onPrune)
    {
        ArgumentNullException.ThrowIfNull(del);
        PushChoicePoint(arity, IlChoicePointSentinelBp);
        if (_ilCpTop == _ilCpStack.Length)
            System.Array.Resize(ref _ilCpStack, _ilCpStack.Length * 2);
        _ilCpStack[_ilCpTop++] = new IlChoicePointEntry
        {
            Del = del, Cursor = nextCursor, Key = _b, OnPrune = onPrune,
        };
    }

    /// <summary>ADR-037 — a resume delegate that always fails. <see cref="SoftCut"/>
    /// swaps a neutralised inline-ITE ELSE choice point's delegate to this: when
    /// backtracking later reaches that IL CP (after the condition's CPs above it
    /// are exhausted) it pops it and keeps backtracking instead of running the
    /// ELSE branch — the IL-tier analogue of the dead-<c>BP</c> sentinel a Tier-0
    /// ELSE CP carries.</summary>
    private static readonly Func<Activation, int, bool> SoftCutFailResume =
        static (_, _) => false;

    /// <summary>ADR-037 — neutralise the IL choice point whose frame sits at
    /// stack position <paramref name="barrier"/> (a <see cref="SoftCut"/> target
    /// whose <c>BP</c> is <see cref="IlChoicePointSentinelBp"/>), by replacing its
    /// resume delegate with <see cref="SoftCutFailResume"/>. IL CPs are pushed in
    /// strictly increasing <c>Key</c> (= <c>_b</c>) order, so the search from the
    /// top stops as soon as it passes the barrier.</summary>
    private void NeutralizeIlChoicePoint(int barrier)
    {
        for (int i = _ilCpTop - 1; i >= 0; i--)
        {
            int key = _ilCpStack[i].Key;
            if (key == barrier) { _ilCpStack[i].Del = SoftCutFailResume; return; }
            if (key < barrier) return;
        }
    }

    /// <summary>Wrapper around <see cref="PushIlChoicePoint"/> for
    /// builtins that need runtime choice-point semantics (the
    /// multi-solution <c>call/N</c>, the non-deterministic split modes
    /// of <c>append/3</c> and <c>atom_concat/3</c>). The push itself is
    /// identical to <see cref="PushIlChoicePoint"/>; the wrapper exists
    /// so the resume mechanism's "post-call PC" convention is named
    /// consistently across builtin sites.
    ///
    /// <para>On a successful retry the resume delegate is expected to
    /// call <see cref="ResumeAtReturnPc(int)"/> with the address of the
    /// instruction immediately after the <c>call_builtin</c> opcode
    /// that originally invoked the builtin. That sets the engine's PC
    /// and IL-tail-call flag so the interpreter resumes execution at
    /// the next goal instead of falling back on the saved <c>Cp</c>
    /// (which points at the parent procedure's continuation, not the
    /// next instruction in the current clause).</para></summary>
    public void PushBuiltinChoicePoint(
        Func<Activation, int, bool> del, int arity)
    {
        PushIlChoicePoint(del, nextCursor: 0, arity: arity);
    }

    /// <summary>builtin-CP overload that registers an
    /// <paramref name="onPrune"/> cleanup callback. Used by the
    /// non-deterministic [PrologPredicate] bridge to Dispose the
    /// iterator when Prolog `!` cuts past the CP without the
    /// engine backtracking through it (in which case
    /// MoveNext-returns-false already handles Dispose).</summary>
    public void PushBuiltinChoicePoint(
        Func<Activation, int, bool> del, int arity, Action? onPrune)
    {
        PushIlChoicePoint(del, nextCursor: 0, arity: arity, onPrune: onPrune);
    }

    // ----- ADR-033: the guard continuation stack -----

    // One packed int per active shared-copy call: (okCursor << 16) | failCursor.
    // Pushed by a CP-free guard's call site just before branching to a shared
    // fail-direct callee copy; popped by the copy's ok / fail epilogue, which
    // dispatches the corresponding half through the method's continuation
    // switch. Same-method only (v1): fail-direct code never leaves the IL
    // method invocation, so the guard's snapshot locals are still live when a
    // fail continuation dispatches. Holds ints — never a heap-GC root.
    private int[] _guardContStack = new int[64];
    private int _guardContTop;

    /// <summary>ADR-033 — current guard-continuation stack top (snapshotted by
    /// <c>catch/3</c> frames; reset at query setup).</summary>
    public int GuardContTop => _guardContTop;

    /// <summary>ADR-033 — truncates the guard-continuation stack (catch
    /// unwind / query setup). Stale entries above the mark belong to an
    /// abandoned computation.</summary>
    public void ResetGuardContTop(int top) => _guardContTop = top;

    /// <summary>ADR-033 — push a shared-copy call's continuations, packed
    /// <c>(okCursor &lt;&lt; 16) | failCursor</c> (method-local continuation
    /// cursors are small).</summary>
    public void PushGuardCont(int packed)
    {
        if (_guardContTop == _guardContStack.Length)
            System.Array.Resize(ref _guardContStack, _guardContStack.Length * 2);
        _guardContStack[_guardContTop++] = packed;
    }

    /// <summary>ADR-033 — pop the top continuation pair and return the OK
    /// cursor (the shared copy succeeded).</summary>
    public int PopGuardContOk() => _guardContStack[--_guardContTop] >> 16;

    /// <summary>ADR-033 — pop the top continuation pair and return the FAIL
    /// cursor (the shared copy exhausted its alternatives).</summary>
    public int PopGuardContFail() => _guardContStack[--_guardContTop] & 0xFFFF;

    // ----- ADR-031 case B: CP-free binding-guard support -----

    /// <summary>Begins a CP-free binding-guard region (ADR-031): sets
    /// <see cref="Hb"/> to the current heap top — so every binding the guard
    /// makes to a pre-existing variable is trailed, exactly as if the skipped
    /// clause choice point had been pushed — and returns the previous HB for
    /// the commit/fail restore. The emitted IL pairs this with
    /// <see cref="CommitIlGuard"/> (guard succeeded) or
    /// <see cref="FailIlGuard"/> (guard failed).</summary>
    public int BeginIlGuard()
    {
        int old = _hb;
        AssignHb(_heapTop);
        return old;
    }

    /// <summary>Commits a CP-free binding guard (ADR-031): restores
    /// <see cref="Hb"/> to the heap boundary of the (unchanged) current top
    /// choice point. The guard's bindings stay; nothing was pushed, so the
    /// cut itself has nothing to tear down.</summary>
    public void CommitIlGuard(int savedHb) => AssignHb(savedHb);

    /// <summary>Fails a CP-free binding guard (ADR-031): undoes every binding
    /// the guard trailed, discards its heap allocations, restores
    /// <see cref="Hb"/>, and clears wakeups queued by the abandoned bindings —
    /// the exact restore the skipped choice point's pop would have performed.
    /// Registers / E / CP / B0 need no restore: the guard op whitelist writes
    /// no argument register and makes no calls.</summary>
    public void FailIlGuard(int bindingTop, int extraTop, int heapTop, int savedHb)
    {
        UnwindTrails(bindingTop, extraTop);
        _heapTop = heapTop;
        AssignHb(savedHb);
        _pendingWakeups.Clear();
    }

    /// <summary>ADR-031 rare path (cases B and G) — pushes the
    /// lazily-materialised clause choice point with the SAVED clause-entry
    /// marks. A binding guard has advanced the trails/heap by the time the
    /// commit runs its wakeup flush — and a FRAMED guard-call clause (case G)
    /// has additionally allocated its environment frame, moving <c>E</c> — so
    /// a failing hook backtracking into this CP must restore the CLAUSE-ENTRY
    /// state so the next clause sees the guard fully undone. The push itself
    /// records current state (argument registers are entry-identical — the
    /// emit saves/restores any the guard writes; <see cref="Hb"/> is left at
    /// the push's heap top so hook bindings trail correctly), then the five
    /// restore slots are overwritten with the entry marks.</summary>
    public void PushIlChoicePointWithMarks(
        Func<Activation, int, bool> del, int nextCursor, int arity,
        int bindingTop, int extraTop, int heapTop, int savedHb, int entryE)
    {
        PushIlChoicePoint(del, nextCursor, arity);
        int b = _b;
        _stack[b + CpBindingTrailOffset(arity)] = Cell.RawInt(bindingTop);
        _stack[b + CpExtraTrailOffset(arity)] = Cell.RawInt(extraTop);
        _stack[b + CpHeapTopOffset(arity)] = Cell.RawInt(heapTop);
        _stack[b + CpHbOffset(arity)] = Cell.RawInt(savedHb);
        _stack[b + CpCeOffset(arity)] = Cell.RawInt(entryE);
    }

    /// <summary>ADR-031 rare path — overwrites the just-pushed choice
    /// point's saved argument register <paramref name="index"/> with the
    /// clause-ENTRY value. A binding/call guard may have clobbered the live
    /// register with call staging by the time the lazy CP materializes at
    /// the commit; the CP's saved args must be ENTRY state (the contract
    /// <see cref="PushIlChoicePointWithMarks"/> documents) or a failing
    /// wakeup hook backtracks the next clause/bucket-node into the guard's
    /// staging values.</summary>
    public void SetTopCpArgRegister(int index, Cell value)
        => _stack[_b + CpArg1Offset + index] = value;

    /// <summary>Sets the engine's PC to <paramref name="returnPc"/> and
    /// flags an IL-style tail call so the interpreter, on this
    /// retry-success, leaves PC alone instead of overriding it with
    /// <see cref="Cp"/>. Used by builtin choice-point resume delegates
    /// to land execution on the instruction immediately
    /// after the <c>call_builtin</c> that pushed the CP.</summary>
    public void ResumeAtReturnPc(int returnPc)
    {
        if (TrapPc >= 0 && returnPc == TrapPc) SetPc(returnPc);
        _p = returnPc;
        IlTailCallPending = true;
    }

    /// <summary>True when the topmost choice point is an IL CP — the
    /// bytecode interpreter consults this on backtrack to choose between
    /// the standard PC-jump path and the IL re-dispatch path.</summary>
    public bool TopChoicePointIsIl =>
        _b >= 0 && _ilCpTop > 0 && _ilCpStack[_ilCpTop - 1].Key == _b;

    /// <summary>Pops the topmost IL choice point, restoring engine state
    /// (heap top, trails, registers, …) the same way <c>TrustMe</c> would
    /// for a bytecode CP, and returns the delegate + cursor that should
    /// be re-invoked. The caller (usually the interpreter's
    /// <c>TryBacktrack</c>) is responsible for actually calling the
    /// delegate.</summary>
    /// <summary>Diagnostic flag — when on, PushChoicePoint and the
    /// IL CP pop log <c>_b</c> / <c>_e</c> / <c>_stackTop</c> for
    /// every event. Used by the IL debug session to track
    /// whether a meta-CP's saved <c>_e</c> still names a valid
    /// frame at pop time.</summary>
    public static bool TraceCpStack { get; set; }

    public (Func<Activation, int, bool> Del, int Cursor) PopIlChoicePointAndRestore()
    {
        if (_b < 0)
            throw new InvalidOperationException("PopIlChoicePointAndRestore: no active choice point.");
        if (_ilCpTop == 0 || _ilCpStack[_ilCpTop - 1].Key != _b)
            throw new InvalidOperationException(
                "PopIlChoicePointAndRestore: the topmost choice point isn't an IL CP.");
        var info = _ilCpStack[_ilCpTop - 1];

        Diagnostics.PopRestoreTrace.PrePop(this, _b);
        if (TraceCpStack)
            System.Console.Error.WriteLine($"[cp-stack] pop-il _b={_b} _e_before_restore={_e} _stackTop_before={_stackTop} saved_e={_stack[_b + CpCeOffset((int)_stack[_b + CpArityOffset].Data)].Data}");
        int arity = RestoreCommonFromCurrentCp();
        Diagnostics.PopRestoreTrace.PostRestore(this, arity);
        AssignHb((int)_stack[_b + CpHbOffset(arity)].Data);
        int oldB = _b;
        _b = (int)_stack[_b + CpBOffset(arity)].Data;
        _stackTop = oldB;
        if (TraceCpStack)
            System.Console.Error.WriteLine($"[cp-stack] pop-il-done _b={_b} _e={_e} _stackTop={_stackTop}");
        // Clear the delegate reference so the array doesn't pin it
        // for GC after pop. The OnPrune is NOT
        // invoked here — backtracking *into* the CP means the
        // delegate runs and handles its own cleanup (the non-det
        // bridge's MoveNext-returns-false path Disposes the
        // iterator). OnPrune is only for cut-pruned discards.
        _ilCpStack[_ilCpTop - 1].Del = null!;
        _ilCpStack[_ilCpTop - 1].OnPrune = null;
        _ilCpTop--;
        return (info.Del, info.Cursor);
    }

}
