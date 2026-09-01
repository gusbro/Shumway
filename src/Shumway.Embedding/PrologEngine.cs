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
public sealed partial class PrologEngine : Shumway.Builtins.IGlobalVarHost, Shumway.Builtins.IFlagHost, Shumway.Builtins.IDialectAwareHost, Shumway.Builtins.IRandomHost
{
    public const string DefaultModuleName = "user";

    /// <summary>Whether <paramref name="name"/> is a known library dialect
    /// (ADR-040) — so a CLI can tell a <c>dialect:path</c> library-dir spec
    /// from a plain Windows path before resolving it.</summary>
    public static bool IsKnownLibraryDialect(string name) =>
        DialectRegistry.IsKnownDialect(name);

    /// <summary>Shumway's version. THE single source: the
    /// <c>version_data</c> Prolog flag (<c>shumway(Major, Minor, Patch,
    /// [])</c>), the top-level banner and the assembly stamp all derive from
    /// these three numbers, so they can never disagree.</summary>
    public const int VersionMajor = 0;
    public const int VersionMinor = 9;
    public const int VersionPatch = 1;

    /// <summary>The version as <c>Major.Minor.Patch</c>.</summary>
    public static string VersionString =>
        $"{VersionMajor}.{VersionMinor}.{VersionPatch}";

    /// <summary>The top-level greeting — <c>Shumway Prolog 0.9.1 (64
    /// bits)</c>, in the shape GNU Prolog's banner uses. The bitness is the
    /// PROCESS's, not the machine's: a 32-bit .NET Framework host says
    /// <c>32 bits</c>, which is what a caller sizing a program to the address
    /// space needs to know (see the memory knees in ADR-043).</summary>
    public static string VersionBanner =>
        $"Shumway Prolog {VersionString} "
        + $"({(Environment.Is64BitProcess ? 64 : 32)} bits)";

    /// <summary>Per-engine global-variable store backing
    /// the SWI <c>nb_setval/2</c> / <c>nb_getval/2</c> family.
    /// Survives across queries on this engine.</summary>
    public Shumway.Builtins.GlobalVarStore GlobalVars { get; } =
        new Shumway.Builtins.GlobalVarStore();

    // statistics/2 support: a wall-clock reference taken at engine creation,
    // plus the last-observed totals so the classic
    // `statistics(runtime, _), Work, statistics(runtime, [_, Delta])` idiom
    // reports the elapsed time between two calls.
    private readonly long _statsWallStart = System.Diagnostics.Stopwatch.GetTimestamp();
    private long _statsLastRuntimeMs;
    private long _statsLastWalltimeMs;

    /// <summary>Wall-clock milliseconds since this engine was created.</summary>
    internal long StatsWalltimeMs() =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - _statsWallStart) * 1000L
            / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Process CPU milliseconds (user + kernel) so far — the
    /// <c>runtime</c> series. Process-wide, but for a single-threaded
    /// benchmark loop it tracks the engine's own compute.</summary>
    internal static long StatsRuntimeMs() =>
#if NETFRAMEWORK
        // Environment.CpuUsage is .NET 8+; the Process property is the same
        // number fetched the pre-8 way.
        (long)System.Diagnostics.Process.GetCurrentProcess()
            .TotalProcessorTime.TotalMilliseconds;
#else
        (long)System.Environment.CpuUsage.TotalTime.TotalMilliseconds;
#endif

    /// <summary>The runtime delta since the previous <c>statistics(runtime, _)</c>
    /// call, updating the reference.</summary>
    internal long StatsTakeRuntimeDelta(long total)
    {
        long d = total - _statsLastRuntimeMs;
        _statsLastRuntimeMs = total;
        return d;
    }

    /// <summary>The walltime delta since the previous
    /// <c>statistics(walltime, _)</c> call, updating the reference.</summary>
    internal long StatsTakeWalltimeDelta(long total)
    {
        long d = total - _statsLastWalltimeMs;
        _statsLastWalltimeMs = total;
        return d;
    }

    /// <summary>Per-engine <c>flag/3</c> store (SWI). A global,
    /// non-backtrackable key → value map, distinct from
    /// <see cref="GlobalVars"/>. Survives across queries — a flag counter
    /// persists through a failure-driven loop (library(gensym)).</summary>
    public Shumway.Builtins.FlagStore FlagStore { get; } =
        new Shumway.Builtins.FlagStore();

    internal readonly Dictionary<string, ModuleManifest> _modules = new()
    {
        [DefaultModuleName] = new ModuleManifest(DefaultModuleName),
    };
    // Lazily-built set of every static head functor across all modules.
    // Backs the static branch of HasPredicate / AllStaticAndDynamicFunctors
    // so those are O(1) instead of a full clause scan — predicate_property/2
    // (which Logtalk's compiler calls per goal) was O(clauses × calls) without
    // it. Nulled at every static-clause mutation (consult / restore / bundle
    // load); dynamic functors are checked live against _dynStore.Functors, so
    // assert/retract need not invalidate it.
    internal HashSet<int>? _staticHeadFunctorsCache;
    // Set for the duration of a RUNTIME consult (consult/1 called from within a
    // live query). When set, source-declared dynamic clauses are also pushed
    // into the live dispatch (AppendDynamicClauseIncremental) so a call later in
    // the SAME query sees them — exactly as a runtime assertz would. Null during
    // startup/ctor consults, where the next query builds dispatch fresh.
    internal Activation? _liveConsultEngine;
    internal readonly OperatorTable _operators = OperatorTable.Default();

    /// <summary>ADR-046 — per-module operator layers, each parented by
    /// <see cref="_operators"/> (the <c>user</c> table). Created on demand;
    /// a module that never declares an operator has no entry and reads
    /// straight from the user table (zero-cost common case). Distinct from
    /// <c>_moduleOperators</c> (the flat attribution list separate
    /// compilation persists).</summary>
    internal readonly Dictionary<string, OperatorTable> _moduleOpLayers = new();

    /// <summary>ADR-046 — the operators each module EXPORTS (the
    /// <c>op(P,T,N)</c> terms of its export list). Applied to the
    /// importer's layer by <c>use_module</c>.</summary>
    internal readonly Dictionary<string,
        List<(int Precedence, OperatorType Type, string Name)>> _moduleExportedOps = new();

    /// <summary>The operator layer used to READ module
    /// <paramref name="module"/>'s text: its own layer over the user
    /// table, or the user table itself for bare/user text.</summary>
    internal OperatorTable ModuleOperatorLayer(string module)
    {
        if (module == DefaultModuleName) return _operators;
        if (!_moduleOpLayers.TryGetValue(module, out var t))
        {
            _moduleOpLayers[module] = t = new OperatorTable(_operators);
            // The separate-compilation attribution (module -> its op defs,
            // persisted into the .shmo) listens on the user table; a layer
            // define must feed the same collector. Read through at fire
            // time — the collector delegate is (re)wired per consult.
            t.OnDefine = (n, prec, ty) => _operators.OnDefine?.Invoke(n, prec, ty);
        }
        return t;
    }

    /// <summary>ADR-046 — applies the operators exported by
    /// <paramref name="sourceModule"/> to <paramref name="target"/> (the
    /// importer's layer, or the user table for a top-level import).</summary>
    internal void ApplyExportedOperators(string sourceModule, OperatorTable target)
    {
        if (!_moduleExportedOps.TryGetValue(sourceModule, out var ops)) return;
        foreach (var (prec, type, opName) in ops)
            target.Define(opName, prec, type);
    }

    /// <summary>Save-state chronological log of every source
    /// string passed to <see cref="ConsultString"/>, excluding the
    /// auto-loaded prelude (which the ctor always loads first).
    /// <see cref="SaveState"/> writes this list verbatim into a
    /// snapshot bundle; <see cref="RestoreState"/> resets the engine
    /// and replays each entry in order to rebuild the same module
    /// state.</summary>
    internal readonly List<string> _consultHistory = new();

    /// <summary>Full paths already consulted — a re-consult of a loaded file
    /// is a no-op instead of doubling its clauses.</summary>
    internal readonly HashSet<string> _consultedPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What each loaded file looked like when it was loaded, so that
    /// "already loaded" can mean "already loaded, and unchanged".
    ///
    /// <para><c>use_module</c> is idempotent by design — importing a library
    /// twice must not consult it twice. But someone EDITING the file they just
    /// imported means the opposite by the same act: reloading the importer has
    /// to pick the change up, or the program runs against a version that is no
    /// longer on disk. Both are true, and the file itself says which applies.</para></summary>
    private readonly Dictionary<string, (DateTime Written, long Size)> _loadStamps
        = new(StringComparer.OrdinalIgnoreCase);

    private static (DateTime Written, long Size) StampOf(string fullPath)
    {
        try
        {
            var info = new System.IO.FileInfo(fullPath);
            return (info.LastWriteTimeUtc, info.Length);
        }
        catch { return (default, -1); }
    }

    /// <summary>Records a file as loaded, as it is now.</summary>
    internal void NoteFileLoaded(string path)
    {
        try
        {
            string full = System.IO.Path.GetFullPath(path);
            _consultedPaths.Add(full);
            _loadStamps[full] = StampOf(full);
        }
        catch { /* unresolvable path — idempotency simply won't apply */ }
    }

    /// <summary>Whether <paramref name="fullPath"/> differs from what was loaded.
    ///
    /// <para>No stamp means NOT changed — this only ever forces a reload of
    /// something we know has moved. A file loaded by a route that records no
    /// stamp (a <c>.shum</c> bundle, whose load returns before the source path
    /// is noted) would otherwise look changed forever and be re-loaded on every
    /// import, undoing the idempotence this sits inside.</para></summary>
    internal bool FileDiffersFromLoad(string fullPath)
        => _loadStamps.TryGetValue(fullPath, out var loaded) && StampOf(fullPath) != loaded;

    /// <summary>Whether a source file is already loaded AND unchanged since —
    /// i.e. whether <c>ensure_loaded/1</c> has nothing left to do. Changed on
    /// disk counts as not-loaded, the same reading <c>use_module</c> takes:
    /// someone is editing it, and a program running against a version that no
    /// longer exists is the worse outcome.</summary>
    /// <summary>Whether this path has been loaded at all, regardless of
    /// whether it has changed since.</summary>
    internal bool WasConsulted(string path)
    {
        try { return _consultedPaths.Contains(System.IO.Path.GetFullPath(path)); }
        catch { return false; }
    }

    internal bool IsLoadedAndUnchanged(string path)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch { return false; }
        return _consultedPaths.Contains(full) && !FileDiffersFromLoad(full);
    }

    /// <summary>ADR-035 — functors whose module was declared :- disable_debug.
    /// Engine-wide (fids are global) and additive across consults.</summary>
    internal readonly HashSet<int> _nonDebuggableFunctors = new();
    internal readonly HashSet<string> _nonDebuggableModules = new();

    /// <summary>ADR-035 — the DebugSiteTable file id the consult in progress
    /// is reading (default: the synthetic string-source file).</summary>
    internal int _debugFileId = Shumway.Core.DebugSiteTable.InternFile("<string>");

    /// <summary>Directory of the file being consulted (null for a raw string
    /// consult) — :- include/1 paths resolve against it.</summary>
    internal string? _consultBaseDir;

    /// <summary>The module + file currently being loaded, for
    /// <c>prolog_load_context/2</c> (SWI/Scryer) — a term_expansion/goal_expansion
    /// hook reads the current module through it rather than an extra hook argument.
    /// Set (and saved/restored for nested consults) around each consult; null
    /// outside a consult.</summary>
    internal string? _currentLoadModule;
    internal string? _currentLoadFile;

    /// <summary>Module name → the full path of the <c>.pl</c> it was consulted
    /// from, or <c>null</c> when it was loaded from an embedded source string
    /// (a baked-in library / shim / prelude / a raw <c>ConsultString</c>). Fed
    /// from <see cref="_currentLoadFile"/> at module registration; used by the
    /// separate-compilation tool to date a module's <c>.shmo</c> against its
    /// source for incremental rebuilds.</summary>
    internal readonly Dictionary<string, string?> _moduleSourceFile = new();

    /// <summary>The source file a module was consulted from, or <c>null</c> if
    /// it came from an embedded string (baked-in library, prelude, shim).</summary>
    internal string? ModuleSourceFile(string moduleName) =>
        _moduleSourceFile.TryGetValue(moduleName, out var f) ? f : null;

    /// <summary>Dynamic functor id → the module whose consult declared it
    /// <c>:- dynamic</c>. The dynamic store itself is engine-wide (flat
    /// global), but separate compilation needs to seed each dynamic
    /// predicate's clauses from its declaring module's <c>.shmo</c> exactly
    /// once — so each object is self-contained and no shared dependency is
    /// double-seeded.</summary>
    internal readonly Dictionary<int, string> _dynamicDeclaringModule = new();

    /// <summary>Meta-predicate templates from <c>:- meta_predicate(Spec)</c>
    /// directives, keyed by the template's functor id as written (the bare
    /// public name — which is what a <c>predicate_property/2</c> query
    /// resolves). Surfaced as the <c>meta_predicate(Template)</c> property;
    /// Logtalk's compiler reads it there to know a goal argument must be
    /// wrapped for the calling context.</summary>
    internal readonly Dictionary<int, Term> _metaPredicateTemplates = new();

    /// <summary>The control constructs' meta-templates. They are not
    /// predicates, so no <c>:- meta_predicate</c> directive can name them
    /// (`','(0,0)` in a directive reads as the conjunction-of-specs form),
    /// but predicate_property/2 must still report their goal arguments —
    /// Logtalk's compiler reads exactly this to decide what to wrap.</summary>
    private void SeedControlMetaTemplates()
    {
        foreach (string ctl in new[] { ",", ";", "->", "*->" })
        {
            int fid = FunctorTable.Intern(AtomTable.Intern(ctl, permanent: true).Id, 2);
            _metaPredicateTemplates[fid] = new CompoundTerm(ctl,
                new Term[] { new IntTerm(0), new IntTerm(0) });
        }
    }

    /// <summary>The module that declared a dynamic functor <c>:- dynamic</c>,
    /// or <c>null</c> if it was auto-promoted (implicit_dynamic) with no
    /// declaration.</summary>
    internal string? DynamicDeclaringModule(int functorId) =>
        _dynamicDeclaringModule.TryGetValue(functorId, out var m) ? m : null;

    /// <summary>The clause index the in-file term_expansion re-expansion pass is
    /// currently expanding, or -1 outside that pass. An in-file hook's clause is
    /// committed guarded by <c>'$te_after'(HookIndex)</c>, which succeeds when this
    /// is -1 (any later consult — the hook always applies then) or greater than
    /// HookIndex (this consult — the hook applies only to clauses AFTER its own
    /// definition, matching SWI/Scryer order-sensitivity).</summary>
    internal int _consultExpandPos = -1;

    /// <summary>Consult nesting depth (1 = top-level). A library loaded mid-consult
    /// via <c>use_module</c> consults at depth &gt; 1 — the in-file term_expansion
    /// re-expansion pass, which runs sub-queries, must not run there: those queries
    /// mid-outer-consult corrupt the outer consult's compile state. Such a library's
    /// re-expansion is a no-op anyway (its hooks target the OUTER file's clauses,
    /// which the outer file's own consult expands).</summary>
    internal int _consultDepth;

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
    System.Random Shumway.Builtins.IRandomHost.Random => Random;   // arith random/1

    /// <summary>Replaces the per-engine random generator with one
    /// seeded by <paramref name="seed"/>. Backs <c>randomize/1</c>.</summary>
    public void Randomize(int seed) => _random = new System.Random(seed);

    /// <summary>Per-engine stream registry. Owns every
    /// open stream, the alias map, and the current-input /
    /// current-output cursors. Lazily built on first access so an
    /// engine that never touches streams pays nothing.</summary>
    private StreamRegistry? _streams;
    internal StreamRegistry Streams =>
        _streams ??= new StreamRegistry(Out, In);

    /// <summary>Activation-wide mutable flag state. Builtins
    /// <c>set_prolog_flag/2</c> and <c>current_prolog_flag/2</c> read
    /// and write here. The parser instances created during ConsultString
    /// and SetupQuery receive the same instance by reference, so a
    /// <c>:- set_prolog_flag(double_quotes, codes).</c> directive at the
    /// top of a source affects every subsequent parse of that source
    /// and every query made against this engine.</summary>
    internal readonly PrologFlags _flags = new();

    /// <summary>Diagnostic accessor for the flag state. Tests
    /// can read <see cref="PrologFlags.DoubleQuotes"/> through this to
    /// verify <c>set_prolog_flag</c> took effect, but mutations should
    /// go through the builtin or a future host-side API.</summary>
    public PrologFlags Flags => _flags;

    /// <summary>The dynamic-predicate clause store (extracted component):
    /// which functors are dynamic and the ordered clause list each holds.
    /// The bytecode-level machinery (trampolines, chains, snapshots) stays
    /// on this engine and consults the store as its source of truth.</summary>
    internal readonly DynamicClauseStore _dynStore = new();

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



    // the generation lives in a shared GenerationBox handed to
    // every Activation this host sets up, so enter_dynamic samples it with a
    // field read instead of a Func<long> invoke per dynamic call. The ONE
    // bump site is InvalidateDynamicCache (every assertz / asserta /
    // retract / abolish funnels through it).
    internal readonly Shumway.Core.GenerationBox _dbGeneration = new();

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

    /// <summary>Head functor ids of every predicate defined by the auto-loaded
    /// prelude (<see cref="Prelude.Source"/>). These are library predicates —
    /// ISO / de-facto standards like <c>member/2</c>, <c>sub_atom/5</c>,
    /// <c>msort/2</c>, <c>subsumes_term/2</c>, <c>maplist/N</c> — implemented
    /// in Prolog rather than C#, so <see cref="Shumway.Builtins.BuiltinsRegistry"/>
    /// doesn't know them. <c>predicate_property/2</c> nonetheless reports them
    /// as <c>built_in</c> (they are not user-defined and cannot be modified),
    /// which is what a client like Logtalk's linter checks to decide a call is
    /// to a known system predicate rather than an undefined one.</summary>
    internal readonly HashSet<int> _preludeFunctors = new();

    /// <summary>True for a predicate the PRELUDE defines — library code a
    /// program sees as built_in, so current_predicate/1 skips it.</summary>
    internal bool IsPreludeFunctor(int functorId) => _preludeFunctors.Contains(functorId);

    /// <summary>The sink that I/O builtins (<c>write/1</c>, <c>nl/0</c>,
    /// <c>writeln/1</c>) write into. Defaults to <see cref="System.Console.Out"/>;
    /// swap in a <see cref="System.IO.StringWriter"/> to capture program
    /// output in tests.</summary>
    public System.IO.TextWriter Out { get; set; } = Console.Out;

    /// <summary>The output redirections <c>with_output_to/2</c> has open —
    /// its goal runs in the LIVE engine with the stream registry's current
    /// output pointed at an in-memory stream; <c>'$wot_end'</c> restores the
    /// displaced handle. A stack, because captures nest.</summary>
    internal System.Collections.Generic.Stack<(Shumway.Core.StreamHandle Prev,
        Shumway.Core.StreamHandle Mem, System.IO.StringWriter Sw,
        System.IO.TextWriter PrevOut)>? WotStack;

    /// <summary>Where load-time warnings go: a directive that failed, a
    /// <c>use_module</c> target that is not there, a directive nobody
    /// recognises. Defaults to standard error, which is right for a console
    /// host and invisible in one that has no console — a browser page would
    /// otherwise load a program that quietly did not load part of itself.
    /// Point it at the same writer as <see cref="Out"/> to have them read as
    /// one stream.</summary>
    /// Resolved late rather than captured at construction: unset, it IS standard
    /// error, so redirecting the console after building an engine still works.
    public System.IO.TextWriter Warnings
    {
        get => _warnings ?? Console.Error;
        set => _warnings = value;
    }

    private System.IO.TextWriter? _warnings;

    /// <summary>Whether a solution's values are materialized only as far as
    /// the answer will be SHOWN. Off by default: an embedder's <c>Query</c>
    /// hands back the whole term, always.
    ///
    /// <para>A top level turns it on, because its answers exist to be looked at
    /// and it elides them anyway. Eliding afterwards bounds the output and not
    /// the work: measured, an answer of 1.5 million cells spent 1.7s becoming
    /// AST nodes against 0.04s being rendered -- more than SOLVING the query
    /// cost. In WebShumway, with no JIT, the same wait is 25 seconds.</para>
    ///
    /// <para>How far is <c>answer_max_depth</c>, so the flag keeps its meaning
    /// and its documented zero ("print everything") still materializes
    /// everything. The allowance below is in NODES where the flag counts list
    /// elements, and a cons is two nodes plus what its head costs -- it is
    /// deliberately loose, so that what the answer looks like stays the
    /// elision's decision and never this one's.</para></summary>
    public bool ElideAnswersForDisplay { get; set; }

    /// <summary>Nodes to materialize per displayed answer, or 0 for all of
    /// them.</summary>
    internal int AnswerMaterializeLimit =>
        ElideAnswersForDisplay && Flags.AnswerMaxDepth > 0
            ? Flags.AnswerMaxDepth * 8 + 64
            : 0;

    /// <summary>Heap-collector totals for this ENGINE, across every query it
    /// has run.
    ///
    /// <para>A query gets a fresh <see cref="Activation"/>, so the collector's
    /// counters live and die with it. That is right for the machine and wrong
    /// for the report: <c>statistics/0</c> is typed at a top level, where the
    /// interesting question is what this session has done, and per-query
    /// counters answered it with the same zeroes every time. SWI's are
    /// session totals; so are these. The dying activation folds its counts in
    /// here (<see cref="AccountGarbageCollection"/>), and the report adds the
    /// activation still running.</para></summary>
    internal long GcRunsTotal { get; private set; }
    internal long GcCompactingTotal { get; private set; }
    internal long GcReclaimedCellsTotal { get; private set; }

    /// <summary>Folds a finished activation's collector counts into the
    /// engine's totals. Called once, from the query teardown.</summary>
    internal void AccountGarbageCollection(Activation engine)
    {
        GcRunsTotal += engine.HeapGcRuns;
        GcCompactingTotal += engine.HeapGcCount;
        GcReclaimedCellsTotal += engine.HeapGcReclaimedCells;
    }

    /// <summary>Reports a load-time warning through <see cref="Warnings"/>.</summary>
    internal void Warn(string message)
    {
        try { Warnings.WriteLine(message); }
        catch { /* a host whose sink is gone must not take the load down */ }
    }

    /// <summary>The source <c>user_input</c> reads from — <c>read/1</c>,
    /// <c>get_char/1</c> and the rest. Null means the host's standard input
    /// (and end-of-file where the host has none, as a browser does). Set it
    /// BEFORE the first query: the stream registry is built during query setup
    /// and <c>user_input</c> keeps whatever reader it was handed then.</summary>
    public System.IO.TextReader? In { get; set; }

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
    internal readonly Dictionary<string, Shumway.Compiler.Wam.CompiledModule> _precompiledModules = new();
    /// <summary>Float-literal support — for a precompiled (bundle) predicate, the
    /// float pool its bytecode's <c>get_float</c>/<c>put_float</c> literalIds index
    /// (its own module's <c>FloatLiterals</c>). The IL compiler bakes the value, so
    /// it needs the right pool per fid. Everything else (runtime-compiled clauses,
    /// dynamic snapshots) indexes the engine's live <c>_literalPools.Floats</c>.</summary>
    internal readonly Dictionary<int, IReadOnlyList<double>> _precompiledFloatPool = new();

    /// <summary>Every dynamic functor that currently has clauses. Used by the
    /// persisted-IL build to snapshot dynamic predicates — including ones the
    /// per-query caches deliberately skip because their bytecode references a
    /// pool literal (float / string / bigint), which is exactly the float case the
    /// IL compiler now value-bakes.</summary>
    internal IEnumerable<int> DynamicFunctorsWithClauses()
    {
        foreach (var fid in _dynStore.Functors)
            if (_dynStore.TryGetClauses(fid, out var cs) && cs.Count > 0)
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
    internal void RemapPrecompiledLiterals(Shumway.Compiler.Wam.CompiledModule module)
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
    internal readonly Dictionary<string, HashSet<int>> _precompiledModuleLocals = new();

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

    /// <summary>Diagnostic access for the <c>'$cp_owners'/0</c> builtin (CP-owner
    /// attribution by nearest-predicate-below floor search).</summary>
    internal IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate>? PredicatesByAddressForDiag
        => _currentPredicatesByAddress;

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
        SeedControlMetaTemplates();

        // Every operator defined (a `:- op` applies here at parse time) is
        // routed to the active consult's collection frame, so the
        // separate-compilation tool can attribute operators per module.
        _operators.OnDefine = (name, precedence, type) =>
        {
            if (_opCollect.Count > 0) _opCollect.Peek().Ops.Add((name, precedence, type));
        };

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
        // ADR-035 — the BAKED prelude is still the prelude: not the user's code,
        // never debuggable. Without this a debug-compiled program on a baked-
        // prelude engine (the browser's stdlib bundle, every --exe) steps into
        // permutation/2, and the prelude's re-compiled clauses take stop sites
        // attributed to the user's file. Marked AFTER LoadBundleCore so the
        // module's clauses exist to resolve.
        if (bundleHasPrelude)
            engine.MarkModuleNonDebuggable(Prelude.ModuleName);

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
        _dynStore.CopyInto(sub._dynStore);
        foreach (var (fid, clauses) in _dynStore.Slots)
            sub._dynStore[fid] = new List<Clause>(clauses);
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
    public ISet<int> DefinedFunctors()
    {
        var set = new HashSet<int>();
        foreach (var manifest in _modules.Values)
            foreach (var clause in manifest.Clauses)
                if (TryExtractHead(clause, out string n, out int a))
                    set.Add(FunctorTable.Intern(
                        AtomTable.Intern(n, permanent: true).Id, a));
        foreach (int fid in _dynStore.Functors) set.Add(fid);
        foreach (int fid in _dynStore.ClauseFunctors) set.Add(fid);
        return set;
    }

    /// <summary>If the most recent <see cref="Query"/> / <see cref="QueryAll"/>
    /// invocation was terminated by <c>halt/0</c> or <c>halt/1</c>, this
    /// holds the exit code requested. <c>null</c> when no halt has fired.
    /// Reset to <c>null</c> at the start of each query.</summary>
    public int? LastHaltExitCode { get; internal set; }

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

    /// <summary>Module name → the operators its consult defined. A `:- op`
    /// applies at parse time (<see cref="OperatorTable.OnDefine"/> fires); each
    /// <see cref="ConsultString"/> brackets a collection frame and, on exit,
    /// attributes the frame's operators to the module it committed. Feeds the
    /// separate-compilation tool so each module's <c>.shmo</c> carries its own
    /// operators — every object stays self-contained.</summary>
    internal readonly Dictionary<string, List<(string Name, int Precedence, OperatorType Type)>> _moduleOperators = new();

    private sealed class OpCollectFrame
    {
        public readonly List<(string Name, int Precedence, OperatorType Type)> Ops = new();
        public string? Module;
    }
    private readonly Stack<OpCollectFrame> _opCollect = new();

    /// <summary>The operators a module's consult defined (empty if none).</summary>
    internal IReadOnlyList<(string Name, int Precedence, OperatorType Type)> ModuleOperators(string moduleName) =>
        _moduleOperators.TryGetValue(moduleName, out var l)
            ? l
            : System.Array.Empty<(string, int, OperatorType)>();

    /// <summary>Start collecting operators defined during a consult (one frame
    /// per <see cref="ConsultString"/>, so nested <c>use_module</c> loads
    /// attribute to their own module).</summary>
    internal void PushOpCollection() => _opCollect.Push(new OpCollectFrame());

    /// <summary>Names the module the current collection frame will attribute
    /// its operators to (set when that module's clauses commit).</summary>
    internal void SetOpCollectionModule(string moduleName)
    {
        if (_opCollect.Count > 0) _opCollect.Peek().Module = moduleName;
    }

    /// <summary>Close the current collection frame, attributing its operators
    /// to the module it committed (deduplicated by name+fixity).</summary>
    internal void PopOpCollection()
    {
        if (_opCollect.Count == 0) return;
        var f = _opCollect.Pop();
        if (f.Module is not { } m || f.Ops.Count == 0) return;
        if (!_moduleOperators.TryGetValue(m, out var list))
            _moduleOperators[m] = list = new List<(string, int, OperatorType)>();
        foreach (var o in f.Ops)
        {
            list.RemoveAll(x => x.Name == o.Name && x.Type == o.Type);
            list.Add(o);
        }
    }

    /// <summary>Snapshot of every registered operator, as
    /// (Precedence, Type, Name) triples. Used by <c>current_op/3</c>
    /// to drive its backtracking enumeration.</summary>
    internal IEnumerable<(int Precedence, OperatorType Type, string Name)> EnumerateOperators()
        => _operators.Enumerate();

}
