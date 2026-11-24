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

        /// <summary>Whether the module's code has been brought in.</summary>
        public bool CodeLoaded;
    }

    /// <summary>The library whose entries <c>LoadBundleCore</c> is declaring,
    /// or null: a source-less entry with no initialization goals defers its
    /// code while this is set.</summary>
    internal string? _attachingLibrary;

    // The static predicates of every attached module, by the functor a call
    // names: the module's own (Module$p) always, and the bare one for a public
    // and for a predicate of a module without a module directive, which is
    // user's (ADR-056, amendment). The library attached first answers.
    private Dictionary<int, AttachedModule>? _attachedByFid;
    private Dictionary<string, AttachedModule>? _attachedModules;

    // The atom ids of the attached modules whose code is not in: what a
    // running goal's qualified calls ask (Activation.PendingModules).
    private readonly HashSet<int> _pendingModuleAtoms = new();
    private readonly HashSet<string> _attachedLibraryPaths = new(StringComparer.OrdinalIgnoreCase);

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
            _attachedByFid.TryAdd(MangleFunctor(entry.ModuleName, fid), am);
            if (ownsUser && !IsMetaHelperFunctorName(fid)) _attachedByFid.TryAdd(fid, am);
        }
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
        if (!_attachedLibraryPaths.Add(path)) return;
        AttachBundle(BundleReader.ReadFromFile(path), System.IO.Path.GetDirectoryName(path), name);
    }

    /// <summary>Attaches a library: declares its modules, and brings in the
    /// ones with initialization goals, whose goals then run in the library's
    /// order (ADR-075, decision 10).</summary>
    internal void AttachBundle(Bundle bundle, string? bundleDir, string name)
    {
        var initialization = new List<(Term Goal, string Module)>();
        _libraryBundleLoadDepth++;
        try
        {
            LoadBundleLinkingLive(() =>
            {
                string? outer = _attachingLibrary;
                _attachingLibrary = name;
                try { LoadBundleCore(bundle, bundleDir, initialization); }
                finally { _attachingLibrary = outer; }
            });
        }
        finally { _libraryBundleLoadDepth--; }
        // ADR-035: a library is code the user did not write.
        foreach (string module in Bundles.LastLoadedModules)
            if (module != DefaultModuleName) MarkModuleNonDebuggable(module);
        foreach (var (goal, module) in initialization)
            RunInitializationGoal(goal, module);
    }

    /// <summary>Brings a module's code in. From a running goal, the code is
    /// linked into that goal.</summary>
    private void BringInModule(AttachedModule am, Activation? engine)
    {
        if (am.CodeLoaded) return;
        am.CodeLoaded = true;
        _pendingModuleAtoms.Remove(AtomTable.Intern(am.Entry.ModuleName, permanent: true).Id);
        var prevLive = _liveConsultEngine;
        if (engine is not null) _liveConsultEngine = engine;
        try
        {
            LoadBundleLinkingLive(() => Bundles.LoadEntryCode(am.Entry, am.ShippedClausesRead));
        }
        finally { _liveConsultEngine = prevLive; }
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

    /// <summary>The undefined-predicate path (ADR-075, decision 7): the
    /// address of <paramref name="fid"/> in <paramref name="engine"/> when a
    /// module of an attached library defines it, bringing the module in, or
    /// linking it into this goal when another goal brought it in; -1
    /// otherwise.</summary>
    internal int ResolveFromAttachedLibraries(Activation engine, int fid)
    {
        if (_attachedByFid is null) return -1;
        if (!_attachedByFid.TryGetValue(fid, out var am))
        {
            // A name user imported from a module with module/2 after this goal
            // was compiled: the import names the module, which is no autoload.
            if (!_modules.TryGetValue(DefaultModuleName, out var user)
                || !user.Imports.TryGetValue(fid, out string? source)
                || !_attachedByFid.TryGetValue(fid = MangleFunctor(source, fid), out am))
                return -1;
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
        foreach (var seed in pending.Seeds)
            foreach (var encoded in seed.EncodedClauses)
            {
                var clause = TermCodec.DecodeClause(encoded);
                _dynStore.AppendClause(fid, clause);
                InvalidateDynamicCache(fid);
                if (engine is not null) AppendDynamicClauseIncremental(engine, fid, clause);
            }
        if (engine is not null) return;
        // No goal named: the next query links the predicate from the store,
        // and each open activation's view of it is built again.
        InvalidatePersistent("pending clauses");
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
