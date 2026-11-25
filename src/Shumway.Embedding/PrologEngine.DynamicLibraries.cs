using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

// ADR-075: dynamic library bundles. A library is attached: every module of it
// is declared and none of its code is loaded. A module's code is brought in
// by the first call that needs it.
public sealed partial class PrologEngine
{
    /// <summary>A module of an attached library.</summary>
    internal sealed class AttachedModule
    {
        public AttachedModule(string library, BundleEntry entry, bool shippedClausesRead)
        {
            Library = library;
            Entry = entry;
            ShippedClausesRead = shippedClausesRead;
        }

        public readonly string Library;
        public readonly BundleEntry Entry;
        public readonly bool ShippedClausesRead;

        /// <summary>Its static predicates that are not public, by bare functor:
        /// what a call to <c>Module$p</c> finds in it.</summary>
        public readonly HashSet<int> Locals = new();

        /// <summary>Whether the module's code has been brought in.</summary>
        public bool CodeLoaded;
    }

    /// <summary>The library whose entries <c>LoadBundleCore</c> is declaring,
    /// or null: a source-less entry defers its code while this is set.</summary>
    internal string? _attachingLibrary;

    // The static predicates of every attached module by the bare functor a
    // call names: a public, and a predicate of a module without a module
    // directive, which is user's (ADR-056, amendment). The library attached
    // first answers. A call to Module$p is not indexed here: its name says
    // which module to ask (AttachedOwnerOf), and interning that name for
    // every predicate of every module made the attach of a large library
    // slow.
    private Dictionary<int, AttachedModule>? _attachedByFid;
    private Dictionary<string, AttachedModule>? _attachedModules;

    // The atom ids of the attached modules whose code is not in: what a
    // running goal's qualified calls ask (Activation.PendingModules).
    private readonly HashSet<int> _pendingModuleAtoms = new();
    private readonly HashSet<string> _attachedLibraryPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file each <c>ensure_loaded(bundle(Name))</c> attached, by
    /// the name it gave, the first time it gave it.</summary>
    internal readonly Dictionary<string, string> _bundlesAttachedByName = new(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="module"/> is declared by an attached
    /// library and its code is not in yet.</summary>
    internal bool IsCodePending(string module)
        => _attachedModules is { } modules && modules.TryGetValue(module, out var am) && !am.CodeLoaded;

    /// <summary>Records a module whose code <c>LoadBundleCore</c> deferred.</summary>
    internal void NoteAttachedModule(BundleEntry entry, bool shippedClausesRead)
    {
        var am = new AttachedModule(_attachingLibrary!, entry, shippedClausesRead);
        (_attachedModules ??= new(StringComparer.Ordinal))[entry.ModuleName] = am;
        _pendingModuleAtoms.Add(AtomTable.Intern(entry.ModuleName, permanent: true).Id);
        _attachedByFid ??= new();
        bool ownsUser = !entry.IsExportQualified;
        foreach (var d in entry.Defined)
        {
            if (d.Visibility == PredicateVisibility.Dynamic) continue;
            int fid = FunctorTable.Intern(
                AtomTable.Intern(d.Indicator.Name, permanent: true).Id, d.Indicator.Arity);
            if (IsGlobalHookFunctor(fid)) continue;
            if (d.Visibility == PredicateVisibility.Public)
            {
                _attachedByFid.TryAdd(fid, am);
                continue;
            }
            am.Locals.Add(fid);
            if (ownsUser && !IsMetaHelperFunctorName(fid)) _attachedByFid.TryAdd(fid, am);
        }
    }

    /// <summary>Entries of the index of attached predicates by bare functor.
    /// For tests.</summary>
    internal int AttachedIndexCount => _attachedByFid?.Count ?? 0;

    /// <summary>The attached module a call to <paramref name="fid"/> is for:
    /// by its bare functor, or, for <c>Module$p</c>, the module of that name
    /// when it defines <c>p</c> and does not make it public.</summary>
    private AttachedModule? AttachedOwnerOf(int fid)
    {
        if (_attachedByFid!.TryGetValue(fid, out var am)) return am;
        if (ModuleQualify.TryDemangle(fid, out int moduleAtom, out int bare)
            && AtomTable.GetById(moduleAtom)?.Name is { } module
            && _attachedModules!.TryGetValue(module, out am) && am.Locals.Contains(bare))
            return am;
        return null;
    }

    /// <summary>Finds <c>Name.shum</c> on the library search path.</summary>
    internal bool TryResolveBundle(string name, out string path)
    {
        foreach (string dir in EnumerateLibraryDirs())
        {
            string candidate = System.IO.Path.Combine(dir, name + ".shum");
            try
            {
                if (System.IO.File.Exists(candidate))
                {
                    path = System.IO.Path.GetFullPath(candidate);
                    return true;
                }
            }
            catch { /* an invalid path component */ }
        }
        path = "";
        return false;
    }

    /// <summary><c>ensure_loaded(bundle(Name))</c>: attaches the library
    /// <c>Name.shum</c> of the search path, once.</summary>
    internal void AttachBundleByName(string name)
    {
        if (!TryResolveBundle(name, out string path))
            throw new PrologRuntimeException(
                "existence_error", "source_sink",
                (object)new CompoundTerm("bundle", new Term[] { new AtomTerm(name) }));
        _bundlesAttachedByName.TryAdd(name, path);
        if (!_attachedLibraryPaths.Add(path)) return;
        var read = ProfMark.Now();
        var bundle = BundleReader.ReadFromFile(path);
        read.AddTo(ref ProfAttachReadTicks, ref ProfAttachReadBytes);
        AttachBundle(bundle, System.IO.Path.GetDirectoryName(path), name);
    }

    /// <summary>ADR-075, decision 6: attaches a library a bundle was linked
    /// against, found next to the bundle or on the library search path. Each
    /// predicate the bundle uses of it has to be offered; a dynamic one is
    /// declared by the attach, and a static one's module is brought in.</summary>
    internal void AttachLinkedLibrary(BundleDynamicLibrary library, string? bundleDir)
    {
        string? path = null;
        if (bundleDir is not null)
        {
            string beside = System.IO.Path.Combine(bundleDir, library.Name + ".shum");
            if (System.IO.File.Exists(beside)) path = System.IO.Path.GetFullPath(beside);
        }
        if (path is null && !TryResolveBundle(library.Name, out path))
            throw new PrologRuntimeException(
                "existence_error", "source_sink",
                (object)new CompoundTerm("bundle", new Term[] { new AtomTerm(library.Name) }));
        if (_attachedLibraryPaths.Add(path))
            AttachBundle(BundleReader.ReadFromFile(path), System.IO.Path.GetDirectoryName(path), library.Name);
        foreach (var use in library.Uses)
        {
            int bare = FunctorTable.Intern(AtomTable.Intern(use.Name, permanent: true).Id, use.Arity);
            if (_dynStore.IsDynamic(use.Module.Length == 0 ? bare : MangleFunctor(use.Module, bare)))
                continue;
            if (AttachedDefinerOf(use) is not { } am)
                throw new PrologRuntimeException(
                    "existence_error", "procedure", (object)LibraryUseTerm(library.Name, use));
            if (!am.CodeLoaded) BringInModule(am, null);
        }
    }

    // The attached module that defines what a linked bundle uses: by its
    // name, or in the module the use names.
    private AttachedModule? AttachedDefinerOf(QualifiedPredicateRef use)
    {
        if (_attachedModules is null) return null;
        if (use.Module.Length == 0)
            return AttachedOwnerOf(FunctorTable.Intern(
                AtomTable.Intern(use.Name, permanent: true).Id, use.Arity));
        if (!_attachedModules.TryGetValue(use.Module, out var am)) return null;
        foreach (var d in am.Entry.Defined)
            if (d.Visibility != PredicateVisibility.Dynamic
                && d.Indicator.Name == use.Name && d.Indicator.Arity == use.Arity)
                return am;
        return null;
    }

    // bundle(Library):Name/Arity, or bundle(Library):(Module:Name)/Arity.
    private static Term LibraryUseTerm(string library, QualifiedPredicateRef use)
    {
        Term name = use.Module.Length == 0
            ? new AtomTerm(use.Name)
            : new CompoundTerm(":", new Term[] { new AtomTerm(use.Module), new AtomTerm(use.Name) });
        return new CompoundTerm(":", new Term[]
        {
            new CompoundTerm("bundle", new Term[] { new AtomTerm(library) }),
            new CompoundTerm("/", new Term[] { name, new IntTerm(use.Arity) }),
        });
    }

    /// <summary>Attaches a library: declares its modules, and brings in the
    /// ones with initialization goals, whose goals then run in the library's
    /// order (ADR-075, decision 10).</summary>
    internal void AttachBundle(Bundle bundle, string? bundleDir, string name)
    {
        var initialization = new List<(Term Goal, string Module)>();
        var link = ProfMark.Now();
        var gcPause = LoadProfEnabled ? ProfGcPauseSoFar() : default;
        int gcs = LoadProfEnabled ? System.GC.CollectionCount(0) : 0;
        _libraryBundleLoadDepth++;
        try
        {
            LoadBundleLinkingLive(() =>
            {
                string? outer = _attachingLibrary;
                _attachingLibrary = name;
                var load = ProfMark.Now();
                try { LoadBundleCore(bundle, bundleDir, initialization); }
                finally
                {
                    _attachingLibrary = outer;
                    load.AddTo(ref ProfAttachLoadTicks, ref ProfAttachLoadBytes);
                }
            });
        }
        finally { _libraryBundleLoadDepth--; }
        link.AddTo(ref ProfAttachLinkTicks, ref ProfAttachLinkBytes);
        // ADR-035: a library is code the user did not write. A module whose
        // code waits is marked with its dynamic predicates, the code it can
        // run now; its other predicates are marked when its code comes in.
        var nonDebuggable = ProfMark.Now();
        foreach (string module in Bundles.LastLoadedModules)
            if (module != DefaultModuleName)
            {
                if (IsCodePending(module)) Consults.MarkModuleNonDebuggable(module, dynamicsOnly: true);
                else MarkModuleNonDebuggable(module);
            }
        nonDebuggable.AddTo(ref ProfAttachNonDebugTicks, ref ProfAttachNonDebugBytes);
        var init = ProfMark.Now();
        // Decision 10: a module with initialization goals is brought in before
        // they run, into the goal that attaches, as a call would bring it in.
        // Their links join the program together, with one copy of its maps.
        var links = _adoptionBatch = new List<LivePublish>();
        try
        {
            foreach (var (_, module) in initialization)
                if (_attachedModules is { } attached && attached.TryGetValue(module, out var withGoals)
                    && !withGoals.CodeLoaded)
                    BringInModule(withGoals, _liveConsultEngine);
        }
        finally { _adoptionBatch = null; }
        if (links.Count > 0 && (_liveConsultEngine is not { } attaching || !AdoptLiveLinks(attaching, links)))
            InvalidateLayout(statics: true);
        foreach (var (goal, module) in initialization)
            RunInitializationGoal(goal, module);
        init.AddTo(ref ProfAttachInitTicks, ref ProfAttachInitBytes);
        // The goal kept the layout and the attach filled its buffer: the buffer
        // gets the room a layout gives one now, so that the code the next goals
        // link does not move it.
        if (_liveConsultEngine is { } live && EngineOwnsHostBuffer(live))
        {
            live.ReserveCode(2 * live.ProgramLength);
            SyncOrInvalidateAfterMutation(live, ownedHostBuffer: true);
        }
        if (LoadProfEnabled)
        {
            ProfAttachCalls++;
            ProfAttachGcPause += ProfGcPauseSoFar() - gcPause;
            ProfAttachGcs += System.GC.CollectionCount(0) - gcs;
        }
    }

    /// <summary>Set by an attach that only declared modules: the layout stays
    /// for the goal the attach runs in, which adopts what it declared
    /// (<see cref="AdoptDeclaredDynamics"/>).</summary>
    internal bool _attachDeclaredOnly;

    /// <summary>Dynamic predicates a goal declared and kept in the persistent
    /// buffer: the buffer runs them, and the program product holds them from
    /// its next build.</summary>
    internal readonly HashSet<int> _liveDeclaredDynamics = new();

    /// <summary>Makes the dynamic predicates an attach declared, which
    /// <paramref name="engine"/> linked into the host's buffer, part of the
    /// linked program, as <see cref="AdoptLiveLink"/> does for code: the next
    /// query finds the program as it is. False, and nothing changed, when it
    /// cannot be: the caller lays the program out again.</summary>
    internal string? AdoptDeclaredDynamics(Activation engine,
        Dictionary<int, (int Addr, Shumway.Compiler.Wam.CompiledPredicate Pred)> declared,
        HashSet<int> staleBefore, HashSet<int> pendingBefore)
    {
        if (_persistentProgram is null || _staticLink is null || _dynamicLink is null
            || _programProduct is not { } product
            || _persistentAddressesCache is null || _persistentAddressBaseCache is null
            || _persistentPredsByAddressCache is null)
            return "no layout";
        if (!EngineOwnsHostBuffer(engine)) return "the goal does not own the buffer";
        if (_debugEvalDepth > 0 || DebugSession is not null || _flags.EmitDebugInfo || _flags.DebugCodegen)
            return "debugging";
        // A predicate the program had took the attach's stub: kept when the
        // stub is all it has, or goes first in the goal's chain, where the
        // store has it (PlaceAttachStubsFirst).
        var stubbed = new List<int>();
        foreach (int fid in _productStaleDynamicFids)
        {
            if (!product.DynamicFunctors.Contains(fid)) continue;
            bool newStub = HasPendingClauses(fid) && !pendingBefore.Contains(fid);
            if (!newStub)
            {
                if (staleBefore.Contains(fid)) continue;
                return "clauses for " + FunctorDisplayName(fid);
            }
            if (!_dynStore.TryGetClauses(fid, out var clauses) || clauses.Count == 0
                || (clauses.Count > 1 && !ChainStartsWith(engine, fid, _pendingClauses![fid].Stub)))
                return "the stub is not first: " + FunctorDisplayName(fid);
            stubbed.Add(fid);
        }
        // A name a module's text defines: its callers are rewritten again now
        // that the name is dynamic.
        foreach (int fid in declared.Keys)
        {
            if (_staticRewriteUserLocals?.Contains(fid) == true) return "a local of user: " + FunctorDisplayName(fid);
            if (_staticRewriteModuleLocals is { } modules)
                foreach (var locals in modules.Values)
                    if (locals.Contains(fid)) return "a local of a module: " + FunctorDisplayName(fid);
        }
        // New maps, not the ones in use: an activation suspended on this
        // buffer reads them (AdoptLiveLink).
        var addresses = new Dictionary<int, int>(_persistentAddressesCache);
        var baseMap = new Dictionary<int, int>(_persistentAddressBaseCache);
        var byAddress = new Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>(_persistentPredsByAddressCache);
        foreach (var (fid, (at, pred)) in declared)
        {
            addresses[fid] = at;
            baseMap[fid] = at;
            byAddress[at] = pred;
            _liveDeclaredDynamics.Add(fid);
            // Its chain is the plain one of a cold compile: the setup lays it
            // out indexed when its calls cross the threshold.
            _jitIndexProfile.RecordCompileDecision(fid, false);
        }
        foreach (int fid in stubbed) _liveDeclaredDynamics.Add(fid);
        _persistentAddressesCache = addresses;
        _persistentAddressBaseCache = baseMap;
        _persistentPredsByAddressCache = byAddress;
        AdoptedDeclarations++;
        _persistentLengthAtLink = _persistentLength;
        return null;
    }

    /// <summary>Puts the stub an attach gave a predicate with clauses, which
    /// <paramref name="engine"/> already runs, first in its chain, where the
    /// store has it: at the end, the clauses before it would answer, and then
    /// again after it installs.</summary>
    internal void PlaceAttachStubsFirst(Activation engine, HashSet<int> pendingBefore)
    {
        if (_pendingClauses is null || GetChainTable(engine) is not { } table) return;
        foreach (var (fid, pending) in _pendingClauses)
            if (!pendingBefore.Contains(fid) && table.Chains.ContainsKey(fid)
                && _dynStore.TryGetClauses(fid, out var clauses) && clauses.Count > 1)
                PrependDynamicClauseIncremental(engine, fid, pending.Stub);
    }

    // Whether the goal's chain for fid runs clause first: its trampoline
    // leads to that clause's chunk.
    private bool ChainStartsWith(Activation engine, int fid, Clause clause)
    {
        if (GetChainTable(engine) is not { } table || !table.Chains.TryGetValue(fid, out var chain)
            || chain.TrampolineExecuteOperandAddr <= 0 || engine.CurrentProgram is not { } program)
            return false;
        int head = Shumway.Core.BytecodeIO.ReadInt32(program, chain.TrampolineExecuteOperandAddr);
        foreach (var entry in chain.Entries)
            if (!entry.Retired && ReferenceEquals(entry.Clause, clause)) return entry.ChunkAddr == head;
        return false;
    }

    /// <summary>Attaches whose declarations the goal kept
    /// (<see cref="AdoptDeclaredDynamics"/>). For tests.</summary>
    internal long AdoptedDeclarations;

    /// <summary>Brings a module's code in. From a running goal, the code is
    /// linked into that goal.</summary>
    private void BringInModule(AttachedModule am, Activation? engine)
    {
        if (am.CodeLoaded) return;
        am.CodeLoaded = true;
        _pendingModuleAtoms.Remove(AtomTable.Intern(am.Entry.ModuleName, permanent: true).Id);
        var prevLive = _liveConsultEngine;
        if (engine is not null) _liveConsultEngine = engine;
        // A dynamic predicate's code is linked from the store, and it was
        // declared at the attach: only the static ones are new to the program.
        int statics = 0;
        _lastLivePublish = null;
        _captureLivePublish = true;
        try
        {
            LoadBundleLinkingLive(() =>
            {
                Bundles.LoadEntryCode(am.Entry, am.ShippedClausesRead, broughtIn: true);
                if (_nonDebuggableModules.Contains(am.Entry.ModuleName))
                    MarkModuleNonDebuggable(am.Entry.ModuleName);
                if (_precompiledModules.TryGetValue(am.Entry.ModuleName, out var compiled))
                    foreach (var pred in compiled.Predicates)
                        if (!_dynStore.IsDynamic(pred.FunctorId)) statics++;
            });
        }
        finally
        {
            _liveConsultEngine = prevLive;
            _captureLivePublish = false;
        }
        // The goal linked the module's code into the buffer it runs on. When
        // that is the host's buffer and the link has every predicate, it
        // stays: the next query finds the program as it is (ADR-015,
        // amendment). Else the program is laid out again, the module's code
        // with the static region.
        var published = _lastLivePublish;
        _lastLivePublish = null;
        if (statics == 0) return;
        if (engine is not null && published is not null
            && published.Link.PredicatesByAddress.Count == statics)
        {
            if (_adoptionBatch is { } batch)
            {
                batch.Add(published);
                return;
            }
            if (AdoptLiveLink(engine, published)) return;
        }
        InvalidateLayout(statics: true);
    }

    // The links of modules brought in together, adopted at once by whoever
    // set it (AttachBundle).
    private List<LivePublish>? _adoptionBatch;

    /// <summary>ADR-075, decision 11: the static predicates of an attached
    /// module are defined before its code is in. Their bare functors, the
    /// ones its code gives the program when it comes in: for the set of
    /// defined predicates.</summary>
    internal IEnumerable<int> PendingAttachedFunctors()
    {
        if (_attachedByFid is null) yield break;
        foreach (var (fid, am) in _attachedByFid)
            if (!am.CodeLoaded) yield return fid;
    }

    /// <summary>Decision 11: <c>clause/2</c> of a static predicate brings its
    /// module in, so its clauses are there to read.</summary>
    internal void BringInOwnerOf(Activation engine, int fid)
    {
        if (_attachedByFid is not null && AttachedOwnerOf(fid) is { CodeLoaded: false } am)
            BringInModule(am, engine);
    }

    /// <summary><c>listing/1</c> names a predicate by its name, and its arity
    /// or not (<paramref name="arity"/> -1): every attached module whose
    /// code is not in and that defines a static predicate of that name comes
    /// in, so the listing finds its clauses.</summary>
    internal void BringInDefinersOf(Activation engine, string name, int arity)
    {
        if (_attachedModules is null) return;
        foreach (var am in new List<AttachedModule>(_attachedModules.Values))
        {
            if (am.CodeLoaded) continue;
            foreach (var d in am.Entry.Defined)
                if (d.Visibility != PredicateVisibility.Dynamic && d.Indicator.Name == name
                    && (arity < 0 || d.Indicator.Arity == arity))
                {
                    BringInModule(am, engine);
                    break;
                }
        }
    }

    /// <summary>Brings in the module <paramref name="module"/> names, when an
    /// attached library declares it and its code is not in: a qualified call
    /// <c>m:Goal</c> made at run time knows its module before it looks the
    /// predicate up.</summary>
    internal void EnsureModuleCode(Activation engine, string module)
    {
        if (_attachedModules is { } modules && modules.TryGetValue(module, out var am) && !am.CodeLoaded)
            BringInModule(am, engine);
    }

    /// <summary>Times the undefined-predicate path asked the attached
    /// libraries: a call that resolves does not come here.</summary>
    internal long AttachedLibraryLookups;

    /// <summary>The undefined-predicate path (ADR-075, decision 7): the
    /// address of <paramref name="fid"/> in <paramref name="engine"/> when a
    /// module of an attached library defines it, bringing the module in, or
    /// linking it into this goal when another goal brought it in; -1
    /// otherwise.</summary>
    internal int ResolveFromAttachedLibraries(Activation engine, int fid)
    {
        if (_attachedByFid is null) return -1;
        AttachedLibraryLookups++;
        if (AttachedOwnerOf(fid) is not { } am)
        {
            // A name user imported from a module with module/2 after this goal
            // was compiled: the import names the module, which is no autoload.
            if (!_modules.TryGetValue(DefaultModuleName, out var user)
                || !user.Imports.TryGetValue(fid, out string? source)
                || AttachedOwnerOf(fid = MangleFunctor(source, fid)) is not { } imported)
                return -1;
            am = imported;
        }
        if (!am.CodeLoaded) BringInModule(am, engine);
        int address = LiveAddressOf(engine, fid);
        if (address != -1) return address;
        if (_precompiledModules.TryGetValue(am.Entry.ModuleName, out var compiled))
            LinkLoadedPredicatesLive(engine, compiled.Predicates);
        return LiveAddressOf(engine, fid);
    }

    // ---- the clauses of a dynamic predicate (ADR-075, decision 9) ----

    /// <summary>The clauses the attached libraries give one dynamic predicate
    /// and nobody has asked for yet.</summary>
    private sealed class PendingClauses
    {
        public PendingClauses(Clause stub) => Stub = stub;

        /// <summary>The predicate's first clause while this is pending: the
        /// first call runs it, and it installs the clauses and calls again.</summary>
        public readonly Clause Stub;
        public readonly List<ShmoDynamicSeed> Seeds = new();
    }

    private Dictionary<int, PendingClauses>? _pendingClauses;

    /// <summary>The builtin the stub clause calls.</summary>
    internal const string InstallClausesBuiltin = "$dynlib_install";

    /// <summary>Whether attached libraries hold clauses of the dynamic
    /// predicate under the store key <paramref name="fid"/> that are not
    /// installed.</summary>
    internal bool HasPendingClauses(int fid)
        => _pendingClauses is { } pending && pending.ContainsKey(fid);

    /// <summary>The store keys of the predicates with clauses pending.</summary>
    internal HashSet<int> PendingClauseFunctors()
        => _pendingClauses is { } pending ? new HashSet<int>(pending.Keys) : new HashSet<int>();

    /// <summary>The same by name; <paramref name="owner"/> is the module that
    /// owns the predicate, when one does.</summary>
    internal bool HasPendingClauses(string name, int arity, string? owner = null)
    {
        int fid = FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);
        return HasPendingClauses(owner is null ? fid : MangleFunctor(owner, fid));
    }

    /// <summary>Records the clauses a module of an attached library gives the
    /// dynamic predicate <paramref name="fid"/>, the store key of
    /// <paramref name="bareFid"/> in <paramref name="owner"/> (null: no module
    /// owns it). They are decoded and installed when the predicate is first
    /// touched, with those of every other module, in the order recorded.</summary>
    internal void NotePendingSeed(int fid, int bareFid, string? owner, ShmoDynamicSeed seed)
    {
        _pendingClauses ??= new();
        if (!_pendingClauses.TryGetValue(fid, out var pending))
        {
            pending = new PendingClauses(InstallStub(bareFid, owner));
            _pendingClauses[fid] = pending;
            _dynStore.PrependClause(fid, pending.Stub);
            InvalidateDynamicCache(fid);
        }
        pending.Seeds.Add(seed);
    }

    // p(A1, ..., An) :- !, '$dynlib_install'(Owner, p/n), G = Owner:p(A1, ..., An), call(G).
    // The cut drops the clauses this call would go on to: the call made after
    // the install runs them all. That call is resolved when it runs, since the
    // install may give the predicate another entry point.
    private static Clause InstallStub(int bareFid, string? owner)
    {
        var (atomId, arity) = FunctorTable.Lookup(bareFid);
        string name = AtomTable.GetById(atomId)!.Name;
        Term head;
        if (arity == 0) head = new AtomTerm(name);
        else
        {
            var args = new Term[arity];
            for (int i = 0; i < arity; i++) args[i] = new VarTerm("A" + i);
            head = new CompoundTerm(name, args);
        }
        Term module = new AtomTerm(owner ?? DefaultModuleName);
        Term goal = new VarTerm("G");
        Term install = new CompoundTerm(InstallClausesBuiltin, new Term[]
        {
            module,
            new CompoundTerm("/", new Term[] { new AtomTerm(name), new IntTerm(arity) }),
        });
        Term bind = new CompoundTerm("=", new[] { goal, new CompoundTerm(":", new[] { module, head }) });
        Term body = Conj(new AtomTerm("!"), Conj(install, Conj(bind, new CompoundTerm("call", new[] { goal }))));
        return Clause.From(new CompoundTerm(":-", new[] { head, body }));

        static Term Conj(Term a, Term b) => new CompoundTerm(",", new[] { a, b });
    }

    /// <summary>Installs the clauses attached libraries hold for the dynamic
    /// predicate <paramref name="fid"/>, after the ones it has. Every reader
    /// and writer of a dynamic predicate's clauses calls this first; a call
    /// reaches it through the stub clause. From a running goal, pass its
    /// activation: the clauses are linked into it as assertz links one.</summary>
    internal void InstallPendingClauses(Activation? engine, int fid)
    {
        if (_pendingClauses is null || !_pendingClauses.Remove(fid, out var pending)) return;
        if (engine is not null) RemoveDynamicByReference(engine, fid, pending.Stub);
        else RemoveStubFromStore(fid, pending.Stub);
        // Each clause as RestoreDb re-installs one: the store, the funnel of
        // every mutation, and the running goal's chain.
        var install = ProfMark.Now();
        try
        {
        foreach (var seed in pending.Seeds)
            foreach (var encoded in seed.EncodedClauses)
            {
                var clause = TermCodec.DecodeClause(encoded);
                _dynStore.AppendClause(fid, clause);
                InvalidateDynamicCache(fid);
                if (engine is not null) AppendDynamicClauseIncremental(engine, fid, clause);
                if (LoadProfEnabled) ProfPendingInstallClauses++;
            }
        }
        finally { install.AddTo(ref ProfPendingInstallTicks, ref ProfPendingInstallBytes); }
        if (engine is not null) return;
        // No goal named: the next query links the predicate from the store,
        // and each open activation's view of it is built again.
        InvalidateLayout(statics: false, "pending clauses");
        HashSet<DynChainTable>? seen = null;
        foreach (var live in new List<Activation>(ChainPatcher.LiveActivations()))
            if (GetChainTable(live) is { } table && (seen ??= new()).Add(table))
                RebuildEngineFidChainView(live, fid);
    }

    private void RemoveStubFromStore(int fid, Clause stub)
    {
        if (!_dynStore.HasClauses(fid)) return;
        int at = _dynStore.PhysicalClauses(fid).IndexOf(stub);
        if (at < 0) return;
        _dynStore.RemoveClauseAt(fid, at);
        InvalidateDynamicCache(fid);
    }

    /// <summary>A full reset of the engine: nothing is attached any more.</summary>
    internal void ForgetAttachedLibraries()
    {
        _pendingClauses = null;
        _attachedByFid = null;
        _attachedModules = null;
        _pendingModuleAtoms.Clear();
        _attachedLibraryPaths.Clear();
        _bundlesAttachedByName.Clear();
    }

    /// <summary>Forgets the clauses pending for a predicate that is losing
    /// every clause it has.</summary>
    internal void DropPendingClauses(int fid) => _pendingClauses?.Remove(fid);

    /// <summary>Installs every pending clause: for what reads or replaces the
    /// whole dynamic database.</summary>
    internal void InstallAllPendingClauses(Activation? engine)
    {
        if (_pendingClauses is not { Count: > 0 } pending) return;
        foreach (int fid in new List<int>(pending.Keys)) InstallPendingClauses(engine, fid);
    }

    private static int LiveAddressOf(Activation engine, int fid)
        => engine.CurrentFunctorAddresses is LayeredIntMap<int> map
           && map.TryGetValue(fid, out int address) && !CallTarget.IsUnresolved(address)
            ? address : -1;
}
