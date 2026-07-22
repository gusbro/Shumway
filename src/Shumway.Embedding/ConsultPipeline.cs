using System.Collections.Immutable;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;

namespace Shumway.Embedding;

/// <summary>
/// The consult pipeline (extracted component): file / string consult and
/// reconsult, the transform chain (directives, tabling, native blocks,
/// implicit dynamics), module bookkeeping and the live-consult path that
/// pushes freshly consulted dynamic clauses into a running query's
/// dispatch. First-stage extraction: back-references the owning engine (E);
/// the seam narrows in a later pass.
/// </summary>
internal sealed class ConsultPipeline
{
    private readonly PrologEngine E;
    public ConsultPipeline(PrologEngine engine) => E = engine;

    /// <summary>Loads Prolog source. The first <c>:- module(name).</c>
    /// directive in the source (if any) chooses the target module — re-consulting
    /// the same module replaces its previous contents. Source with no module
    /// directive appends to the default <see cref="PrologEngine.DefaultModuleName"/>
    /// module.
    ///
    /// <para>The call drives the source through <see cref="ClauseReader"/> once
    /// up front so any <c>:- op</c> declarations take effect immediately; the
    /// returned clause stream is sorted into module-local storage and a final
    /// compile happens at query time.</para></summary>
    /// <summary>public file-loading entry, the embedding-API
    /// counterpart of the REPL's local <c>ConsultFile</c> and the
    /// <c>consult/1</c> / <c>reconsult/1</c> builtins. Routes by
    /// extension: <c>.shum</c> goes through
    /// <see cref="E.LoadBundle(string)"/> (precompiled bytecode + maybe
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


    public void ConsultFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.EndsWith(".shum", StringComparison.OrdinalIgnoreCase))
        {
            E.LoadBundle(path);
            return;
        }
        try { E._consultedPaths.Add(Path.GetFullPath(path)); }
        catch { /* unresolvable path — idempotency simply won't apply */ }
        // ISO include/1 — `:- include('lib/x.pl')` resolves relative to the
        // INCLUDING file's directory, so consulting a file records its
        // directory for the duration of the consult (restored after: a
        // nested ConsultFile from an initialization goal must not leak).
        string? prevBase = E._consultBaseDir;
        E._consultBaseDir = Path.GetDirectoryName(Path.GetFullPath(path));
        // ADR-035 — stop sites compiled during this consult are stamped with this
        // file, so a debugger can map a breakpoint in foo.pl back to them.
        int prevFile = E._debugFileId;

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
        E._debugFileId = DebugSiteTable.InternFile(debugPath);

        // And the debugger is TOLD about the file, now, whether or not anything ever stops in
        // it. A breakpoint binds against a module and a module IS a file: until the debugger
        // knows the name, the frames of that file have no module, so they are grey, carry no
        // language, and open nothing when clicked. It used to learn the names only from the
        // command line (a launch) or from the frames of a stop that had already happened — so
        // a file consulted from the top level (`?- [blint].`) was invisible until the SECOND
        // time the program stopped. The engine knows which files it loaded; it should say so.
        if (E.DebugSession is not null)
            Debugging.ShumwayDebugHelper.NoteSourceFile(path);
        try
        {
            ConsultString(File.ReadAllText(path));
        }
        finally
        {
            E._consultBaseDir = prevBase;
            E._debugFileId = prevFile;
        }
    }

    /// <summary>ADR-035 — functors a <c>:- disable_debug.</c> region covered.
    /// Engine-wide (fids are global) and additive across consults: a predicate is
    /// either debuggable or it isn't, whichever file it came from.</summary>


    /// <summary>ADR-035 — the <c>Name/Arity</c> of a clause's head, or false for
    /// anything that is not one.</summary>
    internal static bool TryReadClauseHead(Clause clause, out (string Name, int Arity) spec)
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
    internal void MarkModuleNonDebuggable(string moduleName)
    {
        E._nonDebuggableModules.Add(moduleName);
        if (!E._modules.TryGetValue(moduleName, out var module)) return;
        var specs = new HashSet<(string Name, int Arity)>();
        foreach (var clause in module.Clauses)
            if (TryReadClauseHead(clause, out var spec))
                specs.Add(spec);
        E._nonDebuggableFunctors.UnionWith(ResolveNonDebuggableFids(specs, moduleName));
    }



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
        var prev = E._liveConsultEngine;
        E._liveConsultEngine = liveEngine;
        try { ConsultFile(path); }
        finally { E._liveConsultEngine = prev; }
    }

    /// <summary>Directory of the file currently being consulted (null for a
    /// raw <see cref="ConsultString"/>) — the base `:- include/1` paths
    /// resolve against.</summary>


    /// <summary>classical <c>reconsult/1</c> semantics: for
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
    /// through <see cref="E.LoadBundle(Bundle)"/>. For entries with no
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
                    ? PrologEngine.DefaultModuleName : entry.ModuleName;
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
            E.LoadBundle(bundle);
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

    /// <summary>light reader pass over a source string,
    /// returning the target module name (the <c>:- module/1</c>
    /// directive's argument, or <see cref="PrologEngine.DefaultModuleName"/> if
    /// absent) and the set of head functor ids of every non-directive
    /// clause. Used by <see cref="ReconsultFile"/> to know what to
    /// abolish before loading. Parses the source twice (the subsequent
    /// <see cref="ConsultString"/> reads it again) — the cost is
    /// acceptable for the developer edit-reload path.</summary>
    private (string ModuleName, HashSet<int> HeadFunctorIds) ScanSourceForDefinedHeads(
        string source)
    {
        var rawClauses = new ClauseReader(
            new Lexer(source, E._flags.CharConversionEnabled ? E._flags.CharConversion : null),
            E._operators, E._flags).ReadAll().ToList();
        string moduleName = PrologEngine.DefaultModuleName;
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

    /// <summary>abolishes the named functor in the given
    /// module: drops every matching clause from the module manifest
    /// (and removes the functor from any companion sets:
    /// PublicFunctors / DiscontiguousFunctors / MultifileFunctors /
    /// ModeDeclarations), then — if the functor was dynamic at all —
    /// calls <see cref="E.AbolishDynamic(int)"/> to clear runtime state.
    /// Module-scoped, so other modules' clauses for the same functor
    /// are left alone (matters for multifile predicates).</summary>
    private void AbolishPredicateInModule(string moduleName, int fid)
    {
        if (E._modules.TryGetValue(moduleName, out var manifest))
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

        if (E._dynStore.IsDynamic(fid) || E._dynStore.HasClauses(fid))
            E.AbolishDynamic(fid);

        // Compiled-code caches must be dropped so the next query
        // recompiles against the trimmed manifest.
        E._staticPredicateCache.Clear();
        E._skipCompileMergedCache = null;   // static cache cleared
        E._staticLink = null;
        E.InvalidatePersistent();
    }

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

    internal void ConsultStringInner(string source, bool recordInHistory,
        string? moduleNameFallback = null)
    {
        // ADR-036 — serialized against AddBreakpoint (same gate as query setup): a
        // debug session's idle watcher arms breakpoints from ITS OWN thread, and an
        // arm's EnsureCodeLinked racing this method's cache invalidation tears the
        // code space — predicates vanish (existence_error on a consulted predicate)
        // and stray bytes read as reserved_invalid opcodes. The launch flow made the
        // race routine: breakpoints arrive moments before the post-configure consult.
        // Reentrancy is safe: a directive's query setup takes the same lock on this
        // same thread.
        lock (E.DebugArmGate)
        {
            ConsultStringLocked(source, recordInHistory, moduleNameFallback);
        }
    }

    private void ConsultStringLocked(string source, bool recordInHistory,
        string? moduleNameFallback)
    {
        // Save-state record every user-visible consult so
        // SaveState can serialize it. The prelude (auto-loaded by the
        // ctor) and any other engine-internal source go through this
        // private path with recordInHistory=false to stay out of the
        // snapshot.
        if (recordInHistory) E._consultHistory.Add(source);
        // The static program is about to change — drop the
        // compiled-static-predicate cache so the next query recompiles,
        // and the ADR-015 cached static linked region with it.
        E._staticPredicateCache.Clear();
        E._skipCompileMergedCache = null;   // static cache cleared
        E._staticLink = null;
        E.InvalidatePersistent();
        List<Clause> rawClauses;
        if (ReferenceEquals(source, Prelude.Source) && s_preludeClauses is { } cached)
        {
            rawClauses = cached;   // reuse the one-time prelude parse
        }
        else
        {
            rawClauses = new ClauseReader(
                new Lexer(source, E._flags.CharConversionEnabled ? E._flags.CharConversion : null)
                {
                    // ADR-035 — every position this consult produces knows which FILE it came
                    // from. It has to travel with the position, because the clause is not
                    // compiled now: compilation happens at query setup, long after the consult
                    // that read the file is over, and a compiler field saying "the file we are
                    // reading" says nothing true by then.
                    FileId = E._debugFileId,
                },
                E._operators, E._flags).ReadAll().ToList();
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
                    E._preludeFunctors.Add(HeadFunctorIdOf(c));

        // ISO 7.4.2.7 `:- include(File)` — textual inclusion (semantics in
        // IncludeExpander). Paths resolve against the consulting file's
        // directory (ConsultFile), else the process CWD. Returns the same
        // list when nothing expands, keeping the cached prelude parse shared.
        rawClauses = Shumway.Compiler.Parsing.IncludeExpander.Expand(
            rawClauses, E._consultBaseDir, E._operators, E._flags);

        // a source-carrying bundle entry consults under the
        // entry's module name (the per-file fallback ShmoCompiler resolved
        // at compile time), so two module-less files keep their own local
        // namespaces instead of merging into a rolling "user" module. A
        // plain ConsultString (no fallback) keeps the historic behaviour.
        string moduleName = string.IsNullOrEmpty(moduleNameFallback)
            ? PrologEngine.DefaultModuleName
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
                    E._dynStore.MarkDynamic(fid);
                    // Reserve an entry so retract on a never-asserted dynamic
                    // predicate fails cleanly instead of throwing.
                    if (!E._dynStore.HasClauses(fid))
                        E._dynStore[fid] = new List<Clause>();
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
                    E.MarkNativeFunction(n, a);
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
                    E._dynStore.MarkDynamic(fid);
                    if (!E._dynStore.HasClauses(fid))
                        E._dynStore[fid] = new List<Clause>();
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
                // flags that the parser doesn't already
                // pre-process (double_quotes is the only one) get
                // applied at consult time. Mirrors the runtime
                // set_prolog_flag/2 builtin so the directive form
                // takes effect before subsequent clauses are
                // processed (e.g. implicit_dynamic toggles the
                // CollectImplicitDynamics pre-scan).
                E.ApplyConsultSetPrologFlag(spfFlag.Name, spfValue.Name);
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
                E.ExecuteUseModuleDirective(umDir.Args[0]);
            }
            else if (body is CompoundTerm { Functor: "use_module", Args.Length: 2 } umDir2)
            {
                // `:- use_module(Spec, ImportList)` — the import list is
                // advisory (Shumway's public predicates share a flat global
                // namespace); load the module the same way.
                E.ExecuteUseModuleDirective(umDir2.Args[0]);
            }
            // op/3 already processed in-place by ClauseReader. Other
            // unrecognised directives pass through silently — they may be
            // implementation-defined hooks that future chunks handle.
            // arity_compat only: same policy as
            // shumway-compile — an unknown directive is reported as a
            // warning (to stderr) and consult continues. Without the
            // flag the silent pass-through above is unchanged.
            else if (E._flags.ArityCompat)
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

        // Discontiguous enforcement: clauses for a given
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
        if (E._flags.ArityCompat)
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
            E.RegisterReftypeGlobals(cDecls);
            // ADR-024 — keep the prototypes/typedefs so a P/Invoke `:- native` call
            // can derive its native marshalling signature at query time.
            E.RegisterNativePrototypes(cDecls);
            string prefix = "$nb$" + (E._nativeBlockConsultSeq++) + "$";
            clauses = NativeTransform.Apply(clauses, cDecls, E.ResolveNativeInterop,
                (name, vars, stmts, scalars, _) => E.AddNativeBlock(name, vars, stmts, scalars), prefix,
                E.IsNativeFunctionName);
        }

        // Source-declared clauses for dynamic predicates: route
        // them to the runtime _dynamicClauses store so retract / assertz
        // see them just like runtime-asserted clauses do. Without this
        // routing, source-declared facts for a `:- dynamic foo/N.`
        // predicate would be invisible to retract/2 and clause/2.
        if (E._dynStore.FunctorCount > 0)
        {
            var keptClauses = new List<Clause>(clauses.Count);
            foreach (var c in clauses)
            {
                if (PrologEngine.TryExtractHead(c, out string n, out int a))
                {
                    int fid = FunctorTable.Intern(
                        AtomTable.Intern(n, permanent: true).Id, a);
                    if (E._dynStore.IsDynamic(fid))
                    {
                        E._dynStore.Slot(fid).Add(c);
                        // ADR-023 — a CONSULT-borne clause is a mutation of the
                        // dynamic predicate exactly like a runtime assertz, and
                        // must invalidate the same things: the promoted IL
                        // snapshot (evict), every live interpreter's
                        // IlByFunctorId slot, the caller-inlined-snapshot
                        // staleness set, the caches. Without this, Logtalk's
                        // '$lgt_current_object_'/11 — whose registrations are
                        // consulted generated facts, not runtime asserts — kept
                        // serving a pre-consult snapshot under promotion and
                        // the load failed silently on the missing objects.
                        E.InvalidateDynamicCache(fid);
                        // ADR-023 priming — a `:- dynamic`/`:- visible`
                        // predicate declared WITH source clauses runs as its
                        // Tier-1 IL snapshot from the first call (evictable on
                        // the first mutation).
                        E.IlPromotion.MarkPrime(fid);
                        // clauses routed here from a named
                        // module (a source-carrying bundle entry, or an
                        // explicit `:- module/1` source) must be rewritten
                        // under that module's context at query setup so
                        // their body calls to module-locals mangle the
                        // same way the module's static clauses do.
                        if (moduleName != PrologEngine.DefaultModuleName)
                            E._dynamicSeedModule[fid] = moduleName;
                        // Mid-query consult (consult/1 from a live query): the
                        // clause is already in E._dynStore.Slots (above), so
                        // clause/2 — which reads the live store — sees it in the
                        // SAME query; direct-call dispatch picks it up on the
                        // next query's clean recompile of the predicate.
                        //
                        // do NOT patch the live dispatch in place at
                        // this site. AppendDynamicClauseIncremental extends the
                        // dynamic predicate's compiled chain, and for a predicate
                        // whose dispatch lives in the *persistent* code region
                        // (< E._querySplit — the common case, since Logtalk's
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
                        // stale chain (see DynChainAddressesStale) so
                        // the heavy-load corruption that motivated skipping this
                        // is prevented at the write sites, not by dropping the
                        // extend.
                        if (E._liveConsultEngine is { } le)
                            E.AppendDynamicClauseIncremental(le, fid, c);
                        continue;
                    }
                }
                keptClauses.Add(c);
            }
            clauses = keptClauses;
        }

        // Tabling: a `:- table p/N` predicate's clauses are
        // re-headed to '$tabled$p'/N and a driver clause that routes
        // through '$table_call' is synthesised. Done after the dynamic
        // routing so it only sees the static clauses.
        if (tabledFunctors is not null && tabledFunctors.Count > 0)
            clauses = TransformTabledPredicates(clauses, tabledFunctors, publics);

        // implicit_dynamic pre-scan. When the flag is on
        // (the default), walk every clause body for a literal
        // `assertz(Head)` / `asserta(Head)` / `assert(Head)` call and
        // auto-add Head's functor to E._dynStore.Functors if it has no
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
        if (E._flags.ImplicitDynamic)
            E.CollectImplicitDynamics(clauses, publics);

        // ADR-035 — the predicates the `:- disable_debug.` regions covered. The
        // module name is only settled now (the `:- module` directive may sit
        // anywhere in the file), and it is what decides how a local predicate's
        // name is mangled — so the fids are resolved here rather than inline.
        if (nonDebuggable is not null)
            E._nonDebuggableFunctors.UnionWith(
                ResolveNonDebuggableFids(nonDebuggable, moduleName));

        if (moduleDirectiveSeen || moduleName != PrologEngine.DefaultModuleName)
        {
            // Explicit module (or a per-file fallback module
            // from a source-carrying bundle entry): replace any previous
            // load of this module.
            var manifest = new ModuleManifest(moduleName);
            manifest.Clauses.AddRange(clauses);
            manifest.PublicFunctors.UnionWith(publics);
            if (pendingDiscontiguous is not null) manifest.DiscontiguousFunctors.UnionWith(pendingDiscontiguous);
            if (pendingMultifile is not null) manifest.MultifileFunctors.UnionWith(pendingMultifile);
            if (pendingModes is not null)
                foreach (var (fid, modes) in pendingModes) manifest.ModeDeclarations[fid] = modes;
            E._modules[moduleName] = manifest;
        }
        else
        {
            // Default user module: append. Multiple unrelated consults share
            // a single rolling 'user' module — matches the historic behaviour
            // from before the module system landed.
            var existing = E._modules[PrologEngine.DefaultModuleName];
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
        // dynamic predicate. Drop the cache wholesale — the
        // next query will recompile each dynamic predicate against the
        // updated clause set. Consult is one-shot at engine setup in the
        // common case, so this is amortised away.
        E._dynamicPredicateCache.Clear();
        // New static clauses just landed in E._modules — drop the head-functor
        // cache so HasPredicate / predicate_property/2 sees them.
        E._staticHeadFunctorsCache = null;

        // Mid-query consult (consult/1 from a live query): live-link the
        // just-added predicates into the running query's code space so a
        // later goal — in particular a runtime meta-call — in this query
        // can reach them. Startup consults (no live engine) skip this:
        // their predicates link at the next query setup as always.
        if (E._liveConsultEngine is { } liveEng)
        {
            // Dynamics FIRST — a static clause's body may call one, and the
            // static link resolves such calls against the address map the
            // trampolines populate. A `:- dynamic`/`:- multifile` predicate
            // declared in this consult (Logtalk's hook predicates, e.g.
            // message_hook/4, declared then called before any clause is
            // added) needs a live trampoline so the call FAILS rather than
            // existence_errors.
            E.EnsureLiveDynamicTrampolines(liveEng);
            // Statics next — `clauses` here is the static-only set (the
            // dynamic-head clauses were routed into _dynamicClauses above).
            if (clauses.Count > 0)
            {
                E.LinkConsultedStaticPredicatesLive(liveEng, clauses, moduleName);
                // BROADCAST: a suspended outer engine (a nested
                // deferred-init query consulted this file) must also reach
                // the new predicates when it resumes — e.g. Logtalk's
                // arbitrary.lgt compile (outer engine) statically binds to
                // fast_random's tables consulted by an inner engine. Same
                // single-code-space alignment as the dynamic broadcast.
                if (E.OtherLiveEnginesByTable(liveEng) is { } others)
                    foreach (var other in others)
                    {
                        E.EnsureLiveDynamicTrampolines(other);
                        E.LinkConsultedStaticPredicatesLive(other, clauses, moduleName);
                    }
            }
        }

        // The code the debugger's breakpoints were waiting for has just arrived. Bind them
        // BEFORE the initialization goals run — those goals ARE the program, and a breakpoint
        // bound after them is a breakpoint that never fires.
        E.RebindPendingBreakpoints();

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
                    E.LastHaltExitCode = null;
                    bool ok = false;
                    foreach (var sol in E.QueryAll(g)) { ok = sol.Success; break; }

                    // halt/0-1 does NOT reach us as an exception: QueryAll catches it and
                    // reports the goal as failed, leaving the code behind in
                    // E.LastHaltExitCode. So a goal that halted looked exactly like one that
                    // failed — the load went on, and the process it was told to end lived
                    // on to read from stdin. Re-raise it here: halting from an
                    // initialization goal ends the load, which is what the paragraph above
                    // has always claimed and what SWI does.
                    if (E.LastHaltExitCode is int exitCode)
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

        // RESUME-boundary reconciliation: this consult (and the
        // nested queries its initialization goals spawned) may have mutated
        // dynamic predicates through OTHER engines' views; before control
        // returns to the suspended caller, diff its dispatch view against
        // the store so no ghost clause (missed broadcast patch) survives
        // into its continuation. See ReconcileEngineDynamicView.
        if (E._liveConsultEngine is { } resumeEng)
            E.ReconcileEngineDynamicView(resumeEng);
    }

    /// <summary>Enforces the contiguity rule for clauses inside a single
    /// consulted source. Clauses for the same functor must
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

    internal static int HeadFunctorIdOf(Clause clause)
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
            if (PrologEngine.TryExtractHead(c, out string n, out int a))
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
            // arity_compat — Arity annotates directive indicators:
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

}
