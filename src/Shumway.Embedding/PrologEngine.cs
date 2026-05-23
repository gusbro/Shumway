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
public sealed class PrologEngine
{
    public const string DefaultModuleName = "user";

    private readonly Dictionary<string, ModuleManifest> _modules = new()
    {
        [DefaultModuleName] = new ModuleManifest(DefaultModuleName),
    };
    private readonly OperatorTable _operators = OperatorTable.Default();

    /// <summary>Engine-wide mutable flag state (chunk 58). Builtins
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

    /// <summary>ADR-015 chunk C step 4: per-dynamic-functor chain state
    /// — one entry per clause currently in <see cref="_dynamicClauses"/>,
    /// in the same order, carrying the absolute byte position of the
    /// clause's <c>check_visible</c> died-slot in
    /// <see cref="Engine.CurrentProgram"/>. <c>retract</c> patches the
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
        public DynChainEntry(Clause c, int died, int next)
        {
            Clause = c;
            DiedOperandAddr = died;
            NextOperandAddr = next;
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
    }
    private readonly Dictionary<int, DynChainState> _dynChains = new();

    /// <summary>Test hook: returns the absolute byte offset of clause
    /// <paramref name="clauseIndex"/>'s died slot in the running program,
    /// or <c>null</c> when no chain state exists. Used by chunk-123 tests
    /// to verify <c>retract</c> patches the slot.</summary>
    internal int? PeekDiedAddr(int functorId, int clauseIndex)
    {
        if (!_dynChains.TryGetValue(functorId, out var chain)) return null;
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
        if (!_dynChains.TryGetValue(functorId, out var chain)) return null;
        if (clauseIndex < 0 || clauseIndex >= chain.Entries.Count) return null;
        return chain.Entries[clauseIndex].NextOperandAddr;
    }

    private long _dbGeneration;

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
    public long DbGeneration => _dbGeneration;

    /// <summary>Set of functor ids declared <c>:- dynamic</c> across every
    /// module. The set is global so a single shared store can satisfy
    /// assertz / retract from any module; <see cref="ModuleRewrite"/> reads
    /// it to skip mangling dynamic functors.</summary>
    private readonly HashSet<int> _dynamicFunctors = new();

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

    public PrologEngine()
    {
        // The standard builtins (=/2, ==/2, etc.) need to be registered before
        // the WAM compiler can recognise them. EnsureRegistered is idempotent.
        Shumway.Builtins.StandardBuiltins.EnsureRegistered();
        // Meta-builtins (findall/3 etc.) live in the Embedding layer because
        // they spawn sub-PrologEngines — Builtins can't reference Embedding.
        MetaBuiltins.EnsureRegistered();

        // Consult the internal prelude — Prolog-level definitions of
        // multi-solution predicates (member/2, clause/2, current_predicate/1)
        // that ride the standard WAM choice-point machinery instead of
        // faking backtracking inside a single-shot builtin.
        ConsultString(Prelude.Source);
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
    /// <c>:- dynamic foo/N</c> previously (in any module).</summary>
    internal void Assertz(Clause clause)
    {
        int fid = ExtractHeadFunctorId(clause);
        EnsureDynamic(fid);
        GetOrCreateDynamicSlot(fid).Add(clause);
        InvalidateDynamicCache(fid);
    }

    /// <summary>Adds <paramref name="clause"/> at the front of its predicate's
    /// dynamic clause list.</summary>
    internal void Asserta(Clause clause)
    {
        int fid = ExtractHeadFunctorId(clause);
        EnsureDynamic(fid);
        GetOrCreateDynamicSlot(fid).Insert(0, clause);
        InvalidateDynamicCache(fid);
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

    /// <summary>Removes the clause object identical to <paramref name="clause"/>
    /// from the dynamic store (used after the runtime caller has matched it
    /// via unification on a materialised heap copy). When ADR-015 chunk C
    /// chain state exists for the functor, also patches the matching
    /// clause's <c>died</c> slot in the running program's bytecode so an
    /// already-compiled dispatch's <c>check_visible</c> filters it out
    /// from now on.</summary>
    internal bool RemoveDynamicByReference(
        Engine engine, int functorId, Clause clause)
    {
        if (!_dynamicClauses.TryGetValue(functorId, out var list)) return false;
        int idx = list.IndexOf(clause);
        if (idx < 0) return false;
        list.RemoveAt(idx);
        InvalidateDynamicCache(functorId);
        PatchDiedFromChain(engine, functorId, idx);
        return true;
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
    }

    /// <summary>ADR-015 chunk C step 4 — engine-aware overload that also
    /// patches the <c>died</c> slot of every chain entry in place, so an
    /// already-compiled dispatch in the running program filters all the
    /// abolished clauses out via <c>check_visible</c>.</summary>
    internal void AbolishDynamic(Engine engine, int functorId)
    {
        AbolishDynamic(functorId);              // bumps _dbGeneration
        if (engine.CurrentProgram is null) return;
        if (!_dynChains.TryGetValue(functorId, out var chain)) return;
        var program = engine.CurrentProgram;
        foreach (var entry in chain.Entries)
        {
            if (entry.DiedOperandAddr > 0)
                Shumway.Core.BytecodeIO.WriteInt64(
                    program, entry.DiedOperandAddr, _dbGeneration);
        }
        chain.Entries.Clear();
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
        foreach (var manifest in _modules.Values)
        {
            foreach (var c in manifest.Clauses)
            {
                if (TryExtractHead(c, out string n, out int a))
                {
                    int fid = FunctorTable.Intern(
                        AtomTable.Intern(n, permanent: true).Id, a);
                    if (seen.Add(fid)) yield return fid;
                }
            }
        }
    }

    /// <summary>True iff <paramref name="functorId"/> is the functor of any
    /// loaded predicate — static, dynamic, or builtin. Backs the
    /// ground-mode case of <c>current_predicate/1</c>.</summary>
    internal bool HasPredicate(int functorId)
    {
        if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(functorId, out _))
            return true;
        if (_dynamicFunctors.Contains(functorId)) return true;
        foreach (var manifest in _modules.Values)
        {
            foreach (var c in manifest.Clauses)
            {
                if (TryExtractHead(c, out string n, out int a)
                    && FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, a) == functorId)
                    return true;
            }
        }
        return false;
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

    private void EnsureDynamic(int fid)
    {
        if (!_dynamicFunctors.Contains(fid))
        {
            var (atomId, arity) = FunctorTable.Lookup(fid);
            string name = AtomTable.GetById(atomId)?.Name ?? "?";
            throw new InvalidOperationException(
                $"assertz/retract: predicate {name}/{arity} is not declared dynamic. "
                + $"Add `:- dynamic {name}/{arity}.` to the source.");
        }
    }

    private static int ExtractHeadFunctorId(Clause clause)
    {
        Term head = clause.Kind == ClauseKind.Rule
            ? ((CompoundTerm)clause.Term).Args[0]
            : clause.Term;
        return head switch
        {
            AtomTerm a => FunctorTable.Intern(
                AtomTable.Intern(a.Name, permanent: true).Id, 0),
            CompoundTerm c => FunctorTable.Intern(
                AtomTable.Intern(c.Functor, permanent: true).Id, c.Args.Length),
            _ => throw new InvalidOperationException(
                "assertz/retract: clause head must be atom or compound."),
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

    /// <summary>Adds an operator to the engine's parser table. Used by the
    /// runtime <c>op/3</c> builtin so user code can introduce operators
    /// that subsequent queries (and asserted clauses) will recognise.</summary>
    internal void DefineOperator(string name, int precedence, OperatorType type)
        => _operators.Define(name, precedence, type);

    /// <summary>Loads a Shumway bundle (.shum) from disk and consults every
    /// module inside it. Equivalent to calling <see cref="ConsultString"/>
    /// for each entry in the bundle's manifest, in order. Throws
    /// <see cref="InvalidDataException"/> if the file isn't a valid
    /// bundle.</summary>
    public void LoadBundle(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Bundle bundle = BundleReader.ReadFromFile(path);
        LoadBundle(bundle);
    }

    /// <summary>Loads an in-memory <see cref="Bundle"/> into this engine —
    /// useful for tests and for in-process pipelines that prefer not to
    /// round-trip through disk. Entries that carry a pre-compiled
    /// bytecode blob (chunk 38 / chunk 45) get their IL-eligible
    /// predicates eagerly warmed via <see cref="IlPromotion"/>'s
    /// <c>Warm</c> path; the precompiled clause list is cached on
    /// <see cref="PrecompiledClauseCache"/> so subsequent query setups
    /// can skip the WAM compile for those clauses (chunk 53).</summary>
    public void LoadBundle(Bundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        foreach (var entry in bundle.Entries)
            ConsultString(entry.Source);

        // Phase-1 runtime use of the compiled blob: decode each entry's
        // CompiledModule and try to install Tier-1 IL delegates for every
        // predicate the IL compiler can handle. Predicates outside the
        // subset stay on Tier 0; the existing counter still works for
        // anything new the loaded blob hasn't covered. The decoded
        // module is also stashed on PrecompiledModules + indexed by
        // functor id on PrecompiledClauseCache so the query setup
        // path can re-use the precompiled bytecode instead of running
        // ModuleCompiler over the consulted source a second time.
        foreach (var entry in bundle.Entries)
        {
            if (entry.CompiledBytecode is null) continue;
            var module = CompiledModuleCodec.Decode(entry.CompiledBytecode);
            _precompiledModules[entry.ModuleName] = module;
            foreach (var pred in module.Predicates)
            {
                IlPromotion.Warm(pred.FunctorId, pred);
                _precompiledClauseCache[pred.FunctorId] = pred;
            }
        }
        // A bundle's predicates join the static program — drop the
        // ADR-015 cached static linked region so the next query rebuilds it.
        _staticLink = null;

        // Chunk 71: when an entry carries a persisted-IL .dll blob,
        // load the assembly in-memory and bind each pre-emitted method
        // as a PredicateDelegate. This skips the Sigil emit step that
        // IlPromotion.Warm would otherwise run and surfaces the
        // already-JIT-able IL directly to the engine.
        //
        // Self-referential IL CPs (multi-clause / meta-CP shapes)
        // dispatch through a static PredicateDelegate[] field on the
        // emitted type; we populate it here before any predicate runs
        // so the first IL CP push finds its target.
        foreach (var entry in bundle.Entries)
        {
            // A persisted-IL blob is JIT-able IL — loading and running it
            // is runtime code generation, so under Native AOT the entry's
            // bytecode (decoded above) is used instead.
            if (entry.CompiledIl is null || entry.CompiledIl.Length == 0
                || !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
                continue;
            var asm = System.Reflection.Assembly.Load(entry.CompiledIl);
            var type = asm.GetType(Shumway.Compiler.Il.PersistedIlBuilder.TypeName);
            if (type is null) continue;

            // Method-name layout from PersistedIlBuilder:
            //   P_{slot}_{functorId}_{sanitisedName}
            // The slot lookup goes into the static delegates array,
            // the functorId binds the engine's IL promotion entry.
            // Reflection ordering isn't guaranteed; sort by slot so
            // the array population is deterministic.
            var bound = new List<(int Slot, int FunctorId,
                Shumway.Compiler.Il.PredicateDelegate Delegate)>();
            foreach (var method in type.GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (!method.Name.StartsWith("P_")) continue;
                int u1 = method.Name.IndexOf('_');
                int u2 = method.Name.IndexOf('_', u1 + 1);
                int u3 = method.Name.IndexOf('_', u2 + 1);
                if (u1 < 0 || u2 < 0 || u3 < 0) continue;
                if (!int.TryParse(method.Name.AsSpan(u1 + 1, u2 - u1 - 1), out int slot)) continue;
                if (!int.TryParse(method.Name.AsSpan(u2 + 1, u3 - u2 - 1), out int functorId)) continue;
                var del = method.CreateDelegate<Shumway.Compiler.Il.PredicateDelegate>();
                bound.Add((slot, functorId, del));
                IlPromotion.RegisterBoundDelegate(functorId, del);
            }

            // Populate the static delegates array (chunk 71 multi-clause
            // self-reference). The array is sized to fit max(slot)+1
            // and each entry lands at its parsed slot.
            var delegatesField = type.GetField(
                Shumway.Compiler.Il.PersistedIlBuilder.DelegatesFieldName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (delegatesField is not null && bound.Count > 0)
            {
                int size = bound.Max(b => b.Slot) + 1;
                var arr = new Shumway.Compiler.Il.PredicateDelegate[size];
                foreach (var (slot, _, del) in bound) arr[slot] = del;
                delegatesField.SetValue(null, arr);
            }
        }
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

    /// <summary>Persistent literal pools (ADR-015 chunk B). One set for the
    /// engine's life, so a literal keeps a stable id across queries — the
    /// precondition for caching the static linked region, whose bytecode
    /// embeds those ids.</summary>
    private readonly Shumway.Compiler.Wam.LiteralPools _literalPools = new();

    /// <summary>The static program, linked once and reused across queries
    /// (ADR-015 chunk B). Null until the first query builds it; nulled
    /// whenever the static program changes (<see cref="ConsultString"/> /
    /// bundle load). A query links only its transient region against this.</summary>
    private Shumway.Compiler.Wam.Linker.LinkResult? _staticLink;

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
    /// <c>'$findall_collect'</c> pops it. Solutions live as AST terms off
    /// the WAM heap, so the <c>fail</c>-driven backtracking that
    /// enumerates the goal doesn't unwind them. A stack so nested findall
    /// calls each get their own frame.</summary>
    private readonly List<List<Term>> _findallStack = new();

    internal void PushFindallFrame() => _findallStack.Add(new List<Term>());

    internal void RecordFindallSolution(Term solution)
    {
        if (_findallStack.Count == 0)
            throw new InvalidOperationException(
                "'$findall_record' invoked with no active findall frame.");
        _findallStack[^1].Add(solution);
    }

    internal List<Term> PopFindallFrame()
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
        // ADR-015 generation clock has to advance.
        _dbGeneration++;
        _dynamicPredicateCache.Remove(functorId);
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
            setup.Engine, setup.Interp);
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
        CaptureStackTrace(Engine engine)
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
        byte[] program,
        List<string> varNames,
        int[] varHeapIndices,
        Engine engine,
        BytecodeInterpreter interp)
    {
        InterpreterResult result;
        bool halted = false;
        try { result = host.RunCatching(interp, program, engine, () => interp.Run(program, 0)); }
        catch (PrologHaltException hex) { halted = true; host.LastHaltExitCode = hex.ExitCode; result = InterpreterResult.Failed; }
        catch (ShumwayPrologException) { { var st = host.CaptureStackTrace(engine); host.LastErrorStackTrace = st.Plain; host.LastErrorStackTraceWithPositions = st.WithPositions; throw; } }
        catch (PrologRuntimeException) { { var st = host.CaptureStackTrace(engine); host.LastErrorStackTrace = st.Plain; host.LastErrorStackTraceWithPositions = st.WithPositions; throw; } }

        while (!halted && result == InterpreterResult.Halted)
        {
            yield return BuildSolution(varNames, varHeapIndices, engine);
            try { result = host.RunCatching(interp, program, engine, () => interp.Backtrack(program)); }
            catch (PrologHaltException hex) { halted = true; host.LastHaltExitCode = hex.ExitCode; break; }
            catch (ShumwayPrologException) { { var st = host.CaptureStackTrace(engine); host.LastErrorStackTrace = st.Plain; host.LastErrorStackTraceWithPositions = st.WithPositions; throw; } }
            catch (PrologRuntimeException) { { var st = host.CaptureStackTrace(engine); host.LastErrorStackTrace = st.Plain; host.LastErrorStackTraceWithPositions = st.WithPositions; throw; } }
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
        BytecodeInterpreter interp, byte[] program, Engine engine,
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
    private static int TryCatch(Engine engine, Term ballTerm, out bool hadActiveFrame)
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
    private static int SetupRecoveryCall(Engine engine, int recoveryHeapIdx)
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
            throw new InvalidOperationException(
                "catch/3 recovery goal is not callable.");
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
    public void UseClpfd() => ConsultString(Clpfd.Source);

    /// <summary>Loads the CLP(R) constraint library (chunk 99) into this
    /// engine, making linear-equality constraints over the reals available
    /// through the <c>{Constraint}</c> wrapper. CLP(R) is opt-in: an engine
    /// that never calls this carries none of the library's weight.
    ///
    /// <para>CLP(R) and CLP(FD) both define a <c>verify_attributes/4</c>
    /// hook as a public predicate, so for now only one of the two may be
    /// loaded into a given engine.</para></summary>
    public void UseClpr() => ConsultString(Clpr.Source);

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
    public void ConsultString(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // The static program is about to change — drop the chunk-82
        // compiled-static-predicate cache so the next query recompiles,
        // and the ADR-015 cached static linked region with it.
        _staticPredicateCache.Clear();
        _staticLink = null;
        var rawClauses = new ClauseReader(new Lexer(source), _operators, _flags).ReadAll().ToList();

        string moduleName = DefaultModuleName;
        bool moduleDirectiveSeen = false;
        var publics = new HashSet<int>();
        var clauses = new List<Clause>();
        HashSet<int>? pendingDiscontiguous = null;
        HashSet<int>? pendingMultifile = null;
        HashSet<int>? tabledFunctors = null;
        Dictionary<int, List<Shumway.Compiler.Modes.ModeDeclaration>>? pendingModes = null;

        foreach (var clause in rawClauses)
        {
            if (clause.Kind != ClauseKind.Directive)
            {
                clauses.Add(clause);
                continue;
            }

            // Strip the leading `:- /1` wrapper to get the directive body.
            if (clause.Term is not CompoundTerm dWrap || dWrap.Args.Length != 1) continue;
            Term body = dWrap.Args[0];

            if (TryReadModuleDirective(body, out string? name))
            {
                if (moduleDirectiveSeen)
                    throw new InvalidOperationException(
                        "Multiple :- module(...) directives in one ConsultString call.");
                moduleName = name;
                moduleDirectiveSeen = true;
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
                    pendingMultifile.Add(FunctorTable.Intern(
                        AtomTable.Intern(n, permanent: true).Id, a));
            }
            else if (TryReadFunctorIndicatorDirective(body, "table", out var tableSpecs))
            {
                tabledFunctors ??= new HashSet<int>();
                foreach (var (n, a) in tableSpecs)
                    tabledFunctors.Add(FunctorTable.Intern(
                        AtomTable.Intern(n, permanent: true).Id, a));
            }
            else if (Shumway.Compiler.Modes.ModeDirectiveParser.TryParse(
                body, out var modeDecl, out string? modeError))
            {
                if (modeError is not null)
                    throw new InvalidOperationException(modeError);
                pendingModes ??= new Dictionary<int, List<Shumway.Compiler.Modes.ModeDeclaration>>();
                if (!pendingModes.TryGetValue(modeDecl!.FunctorId, out var declList))
                {
                    declList = new List<Shumway.Compiler.Modes.ModeDeclaration>();
                    pendingModes[modeDecl.FunctorId] = declList;
                }
                declList.Add(modeDecl);
            }
            // op/3 already processed in-place by ClauseReader. Other
            // unrecognised directives pass through silently — they may be
            // implementation-defined hooks that future chunks handle.
        }

        // Discontiguous enforcement (chunk 60): clauses for a given
        // functor must appear contiguously in source unless the
        // functor is declared :- discontiguous. We walk the just-read
        // clauses in source order, tracking which functors have been
        // "closed" (another functor's clauses started after them),
        // and throw if a closed functor is revisited without a
        // discontiguous declaration.
        ValidateContiguity(clauses, pendingDiscontiguous);

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

        if (moduleDirectiveSeen)
        {
            // Explicit module: replace any previous load of this module.
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
        Term head = clause.Kind == ClauseKind.Rule
            ? ((CompoundTerm)clause.Term).Args[0]
            : clause.Term;
        return head switch
        {
            AtomTerm a => FunctorTable.Intern(
                AtomTable.Intern(a.Name, permanent: true).Id, 0),
            CompoundTerm c => FunctorTable.Intern(
                AtomTable.Intern(c.Functor, permanent: true).Id, c.Args.Length),
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
        throw new InvalidOperationException(
            $"Malformed :- {directiveName} directive (expected Name/Arity or a list of them).");
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
    private static List<Clause> TransformTabledPredicates(
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

    private static bool TryReadModuleDirective(Term body, out string name)
    {
        if (body is CompoundTerm m && m.Functor == "module" && m.Args.Length == 1
            && m.Args[0] is AtomTerm a)
        {
            name = a.Name;
            return true;
        }
        name = "";
        return false;
    }

    private static bool TryReadDynamicDirective(
        Term body, out List<(string Name, int Arity)> specs)
    {
        specs = new List<(string, int)>();
        if (body is not CompoundTerm c || c.Functor != "dynamic" || c.Args.Length != 1)
            return false;

        Term arg = c.Args[0];
        if (TryReadFunctorSpec(arg, out var single))
        {
            specs.Add(single);
            return true;
        }
        if (TryReadFunctorSpecList(arg, specs))
            return true;
        throw new InvalidOperationException(
            "Malformed :- dynamic directive (expected Name/Arity or a list of them).");
    }

    private static bool TryReadPublicDirective(
        Term body, out List<(string Name, int Arity)> publics)
    {
        publics = new List<(string, int)>();
        if (body is not CompoundTerm c || c.Functor != "public" || c.Args.Length != 1)
            return false;

        // A single Name/Arity term or a list of them.
        Term arg = c.Args[0];
        if (TryReadFunctorSpec(arg, out var single))
        {
            publics.Add(single);
            return true;
        }
        if (TryReadFunctorSpecList(arg, publics))
            return true;
        throw new InvalidOperationException(
            "Malformed :- public directive (expected Name/Arity or a list of them).");
    }

    private static bool TryReadFunctorSpec(Term term, out (string Name, int Arity) spec)
    {
        if (term is CompoundTerm slash && slash.Functor == "/" && slash.Args.Length == 2
            && slash.Args[0] is AtomTerm name && slash.Args[1] is IntTerm arity)
        {
            spec = (name.Name, (int)arity.Value);
            return true;
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

    /// <summary>Parses and runs a query, returning the first solution if one
    /// exists or a failed <see cref="Solution"/> otherwise. Equivalent to
    /// <c>QueryAll(queryText).FirstOrDefault(failed)</c>.</summary>
    public Solution Query(string queryText)
    {
        foreach (var sol in QueryAll(queryText))
            return sol;
        return new Solution(success: false, bindings: ImmutableDictionary<string, Term>.Empty);
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
            setup.Engine, setup.Interp);
    }

    private (byte[] Program,
             List<string> VarNames,
             int[] VarHeapIndices,
             Engine Engine,
             BytecodeInterpreter Interp) SetupQuery(string queryText)
    {
        var queryParser = new Parser(new Lexer(queryText), _operators, _flags);
        Term queryTerm = queryParser.ReadClauseTerm();
        return SetupQueryFromTerm(queryTerm);
    }

    /// <summary>Shared workhorse used by both the string-parsing
    /// <see cref="SetupQuery(string)"/> and the Term-level
    /// <see cref="QueryAll(Term)"/>: gathers every module's clauses through
    /// DCG / meta / module-mangle transforms, wraps the goal in a synthetic
    /// clause in the user module, compiles + links, primes X[0..n-1] with
    /// fresh heap unbounds, and hands the lot back to the caller's
    /// run/backtrack iterator.</summary>
    private (byte[] Program,
             List<string> VarNames,
             int[] VarHeapIndices,
             Engine Engine,
             BytecodeInterpreter Interp) SetupQueryFromTerm(Term queryTerm)
    {
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
        var allRewritten = new List<Clause>();
        HashSet<int>? userLocalsCache = null;

        // Chunk 74 — mode specialization. Built once per query setup; the
        // transform appends an implicit cut to every clause of a
        // predicate whose declared modes are all deterministic. Applied
        // after DCG / meta / phrase expansion so it conjoins onto the
        // final plain-rule body.
        var modeTable = Modes;

        foreach (var (name, manifest) in _modules)
        {
            var transformed = DcgTransform.Apply(manifest.Clauses);
            transformed = MetaTransform.Apply(transformed);
            transformed = PhraseTransform.Apply(transformed);
            transformed = Shumway.Compiler.Modes.ModeSpecializationTransform.Apply(
                transformed, modeTable);

            var locals = ComputeLocalFunctors(transformed, manifest.PublicFunctors);
            if (name == DefaultModuleName) userLocalsCache = locals;

            var ctx = new ModuleRewrite.Context(name, locals, _dynamicFunctors);
            foreach (var clause in transformed)
                allRewritten.Add(ModuleRewrite.Rewrite(clause, ctx));
        }

        // Dynamic clauses asserted at runtime. They share a flat global
        // namespace (no module prefix), so the rewrite happens with an empty
        // local set and the engine's dynamic functor set in scope.
        if (_dynamicClauses.Count > 0)
        {
            var dynCtx = new ModuleRewrite.Context(
                DefaultModuleName, new HashSet<int>(), _dynamicFunctors);
            foreach (var (_, clauses) in _dynamicClauses)
            {
                if (clauses.Count == 0) continue;
                var transformed = PhraseTransform.Apply(
                    MetaTransform.Apply(DcgTransform.Apply(clauses)));
                transformed = Shumway.Compiler.Modes.ModeSpecializationTransform.Apply(
                    transformed, modeTable);
                foreach (var clause in transformed)
                    allRewritten.Add(ModuleRewrite.Rewrite(clause, dynCtx));
            }
        }

        // Stub clauses for declared-but-empty dynamic functors. Without
        // these, calls to a dynamic predicate that's been declared but
        // never assertz'd would fail at link time with an unresolved-call
        // error. The stub always fails — its purpose is just to give the
        // predicate a valid bytecode home.
        EmitEmptyDynamicStubs(allRewritten, queryTerm.Position);

        // Snapshot the functor ids of every clause that exists *before*
        // the synthetic query clause is added — the static + dynamic
        // program. Only these are eligible for the chunk-82 static cache:
        // the __query__ clause, and any auxiliary predicate a transform
        // or the compiler derives from a query's control constructs, are
        // query-specific — caching them would let one query's goal leak
        // into the next.
        var cacheableFunctors = new HashSet<int>();
        foreach (var c in allRewritten)
            cacheableFunctors.Add(HeadFunctorIdOf(c));

        // Synthetic query clause — rewrite in the user module's context, but
        // with userLocalsCache (which doesn't include __query__) so the
        // head functor remains bare.
        {
            var queryTransformed = PhraseTransform.Apply(
                MetaTransform.Apply(
                    DcgTransform.Apply(new[] { syntheticClause })));
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
        foreach (int fid in _dynamicFunctors)
        {
            if (_jitIndexProfile.HotnessChangedSinceCompile(fid))
                _dynamicPredicateCache.Remove(fid);
            if (!_jitIndexProfile.IsHot(fid))
                unindexedFunctors.Add(fid);
        }
        foreach (int fid in _dynamicClauses.Keys)
        {
            if (_jitIndexProfile.HotnessChangedSinceCompile(fid))
                _dynamicPredicateCache.Remove(fid);
            if (!_jitIndexProfile.IsHot(fid))
                unindexedFunctors.Add(fid);
        }

        // Skip-compile cache. Two contributors live here:
        //   - Bundle skip-compile (chunk 55): populated by LoadBundle from
        //     a bundle's compiled bytecode blob.
        //   - Dynamic predicate cache (chunk 68): populated lazily by the
        //     query-setup path itself; invalidated on every assertz /
        //     asserta / retract / abolish that touches the functor.
        // ModuleCompiler reuses any cached predicate whose bytecode doesn't
        // reference per-module literal pools.
        IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate>? skipCompileCache;
        if (_precompiledClauseCache.Count == 0
            && _dynamicPredicateCache.Count == 0
            && _staticPredicateCache.Count == 0)
        {
            skipCompileCache = null;
        }
        else
        {
            // Merge the three caches: bundle precompiled (chunk 55),
            // static (chunk 82), then dynamic (chunk 68) — dynamic last
            // so a predicate that turned dynamic wins over a stale static
            // entry (a consult clears the static cache anyway).
            var merged = new Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>(
                _precompiledClauseCache);
            foreach (var (fid, pred) in _staticPredicateCache)
                merged[fid] = pred;
            foreach (var (fid, pred) in _dynamicPredicateCache)
                merged[fid] = pred;
            skipCompileCache = merged;
        }
        // Pre-compute the fail-stub address — it sits at the end of the
        // launcher prefix, at offset Call(9) + Halt(1) = 10. We need it
        // available to the compiler so dynamic predicates emit their
        // last-clause chain instruction with the absolute target.
        int failStubAddr =
            OpcodeTable.Get(Opcode.Call).Size + OpcodeTable.Get(Opcode.Halt).Size;
        var module = new ModuleCompiler().Compile(
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
                    _dynamicPredicateCache[pred.FunctorId] = pred;
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

        // --- ADR-015 chunk B: persistent code space --------------------
        // Partition the compiled predicates into the static region —
        // linked once and cached — and the per-query region (dynamic
        // predicates plus the __query__ clause and its auxiliaries).
        var staticPreds = new List<Shumway.Compiler.Wam.CompiledPredicate>();
        var queryPreds = new List<Shumway.Compiler.Wam.CompiledPredicate>();
        foreach (var pred in module.Predicates)
        {
            if (cacheableFunctors.Contains(pred.FunctorId)
                && !_dynamicFunctors.Contains(pred.FunctorId))
                staticPreds.Add(pred);
            else
                queryPreds.Add(pred);
        }

        // The static region links once at a fixed load offset (the prefix
        // length never varies) and is reused until the static program
        // changes — ConsultString / a bundle load null _staticLink.
        var staticLink = _staticLink
            ?? (_staticLink = new Linker().Link(staticPreds, loadOffset: prefix.Length));

        // The per-query region is appended after the static region. Its
        // calls into static predicates resolve through the static region's
        // address map; its switch-table ids continue past the static set's.
        var queryLink = new Linker().Link(
            queryPreds,
            loadOffset: prefix.Length + staticLink.Bytecode.Length,
            externalSymbols: staticLink.Addresses,
            switchTableIdBase: staticLink.SwitchTables.Count);

        byte[] program = new byte[
            prefix.Length + staticLink.Bytecode.Length + queryLink.Bytecode.Length];
        Array.Copy(prefix, program, prefix.Length);
        Array.Copy(staticLink.Bytecode, 0, program,
            prefix.Length, staticLink.Bytecode.Length);
        Array.Copy(queryLink.Bytecode, 0, program,
            prefix.Length + staticLink.Bytecode.Length, queryLink.Bytecode.Length);

        // A static predicate may call a dynamic one, whose address is only
        // known now — it lives in the per-query region. Such sites were
        // left as undefined-predicate sentinels when the static region was
        // linked; re-patch the ones the query region now resolves. Only
        // program is written — the cached staticLink.Bytecode is untouched.
        foreach (var (offset, fid) in staticLink.UnresolvedSites)
            if (queryLink.Addresses.TryGetValue(fid, out int dynAddr))
                BytecodeIO.WriteInt32(program, prefix.Length + offset + 1, dynAddr);

        // Merge the two regions' link metadata; downstream code is
        // region-agnostic and reads this combined view.
        var mergedAddresses = new Dictionary<int, int>(staticLink.Addresses);
        foreach (var (fid, a) in queryLink.Addresses) mergedAddresses[fid] = a;
        var mergedSwitchTables =
            new List<Shumway.Core.SwitchTable>(staticLink.SwitchTables);
        mergedSwitchTables.AddRange(queryLink.SwitchTables);
        var mergedPredicatesByAddress =
            new Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>(
                staticLink.PredicatesByAddress);
        foreach (var (a, p) in queryLink.PredicatesByAddress)
            mergedPredicatesByAddress[a] = p;
        var linkResult = new Linker.LinkResult(
            program, mergedAddresses, mergedSwitchTables, mergedPredicatesByAddress,
            Array.Empty<(int, int)>());

        // The synthetic query stays under its bare functor (it's local to
        // user but ModuleRewrite never mangles __query__ because it's not
        // present in user's local set: it was added after locals were
        // computed and isn't part of the user-defined predicates).
        int queryFunctorId = FunctorTable.Intern(
            AtomTable.Intern(queryFunctor, permanent: true).Id,
            varNames.Count);
        // Patch the launcher's call target straight in program — the
        // prefix sits at program offset 0, so callPos is the same in both.
        BytecodeIO.WriteInt32(program, callPos + 1, linkResult.Addresses[queryFunctorId]);

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
                _staticPredicateCache[fid] = pred;
        }

        // Runtime call/1 (chunk 86) dispatches a goal by its bare functor,
        // but a module-local predicate is linked under its mangled
        // "module$name" functor. Add a bare-functor alias for each so a
        // runtime call/N can resolve a local predicate by its plain name.
        var addressMap = new Dictionary<int, int>(linkResult.Addresses);
        foreach (var (mangledFunctorId, address) in linkResult.Addresses)
        {
            var (atomId, arity) = FunctorTable.Lookup(mangledFunctorId);
            string mangledName = AtomTable.GetById(atomId)?.Name ?? "";
            int dollar = mangledName.IndexOf('$');
            if (dollar <= 0) continue;
            if (!_modules.ContainsKey(mangledName.Substring(0, dollar))) continue;
            int bareFunctorId = FunctorTable.Intern(
                AtomTable.Intern(mangledName.Substring(dollar + 1), permanent: true).Id,
                arity);
            if (!addressMap.ContainsKey(bareFunctorId))
                addressMap[bareFunctorId] = address;
        }

        var engine = new Engine
        {
            Out = Out,
            Host = this,
            Operators = new OperatorTableAdapter(_operators),
            // The current-query address map lets IL-emitted Execute
            // opcodes (chunk 47) resolve their tail-call target via a
            // stable functor-id lookup instead of an embedded address
            // that would only be valid for one query's linked layout.
            CurrentFunctorAddresses = addressMap,
            // String literal pool for IL-emitted get_pstr/put_pstr
            // (chunk 50) and the linked program byte array for the
            // IL Call re-entry helper.
            CurrentStringLiterals = module.StringLiterals,
            CurrentProgram = program,
            // ADR-015 chunk C — bytecode-level dynamic dispatch reads the
            // host's generation through this callback at every
            // enter_dynamic opcode (avoids the interpreter depending on
            // the embedding layer's types).
            DbGenerationProvider = () => _dbGeneration,
            // ADR-015 chunk C step 4: where the fail-stub lives in the
            // prefix. Used by the upcoming incremental-assertz path and
            // by dynamic predicates' last-clause chain instructions.
            DynamicFailStubAddr = failStubAddr,
        };

        var interp = new BytecodeInterpreter(
            engine, module.StringLiterals, module.FloatLiterals,
            linkResult.SwitchTables, module.BigIntLiterals);

        // ADR-015 chunk C step 4: refresh the interpreter's literal pools
        // after an incremental assertz/asserta interns a new literal.
        engine.RefreshLiteralPoolsCallback = (s, f, b) =>
        {
            interp.RefreshLiteralPools(s, f, b);
            engine.CurrentStringLiterals = s;
        };

        // ADR-015 chunk C step 4: per-functor chain state — record where
        // each clause's check_visible died slot lives in the running
        // program. retract patches the slot in place; next call's
        // check_visible filters the clause out (the bytecode-level
        // logical-update view path that supersedes chunk C's redirect).
        PopulateDynChains(program, addressMap, mergedPredicatesByAddress);

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
        // IL Call (chunk 50): runs a sub-predicate synchronously by
        // re-entering the bytecode interpreter on the linked program.
        engine.IlSubroutineRunner = target => interp.RunSubroutine(program, target);
        // IL meta-CP backtrack hook (chunk 66): drives one round of
        // backtrack inside the bytecode interpreter so an IL Call
        // site's meta-CP can fetch the next solution from a non-leaf
        // callee on resume. Returns true when the backtrack landed on
        // an alternative that proceeded to a halt (success), false
        // when no further CPs were available.
        engine.BacktrackRunner = () =>
            interp.Backtrack(program) == Shumway.Interpreter.InterpreterResult.Halted;
        // Remember the per-query address → predicate map so error
        // reporting (chunk 51) can translate the engine's PC and env-
        // chain return addresses into Name/Arity stack frames.
        _currentPredicatesByAddress = linkResult.PredicatesByAddress;

        int[] varHeapIndices = new int[varNames.Count];
        for (int i = 0; i < varNames.Count; i++)
        {
            int h = engine.AllocateHeapUnbound();
            varHeapIndices[i] = h;
            engine.SetRegister(i, Cell.Ref(h));
        }

        return (program, varNames, varHeapIndices, engine, interp);
    }

    /// <summary>ADR-015 chunk C step 4: incrementally compile and append a
    /// newly asserted clause's bytecode, then patch the chain's tail
    /// <c>&lt;next&gt;</c> operand to link it in. Avoids a full predicate
    /// recompile — the per-assertz cost stays O(clause size) rather than
    /// O(predicate size). Falls back silently (no-op) when the predicate's
    /// chain isn't in the new structure (paso-3 trust_me tail or indexed
    /// dispatch); in those cases the chunk-C redirect handles the update.
    /// </summary>
    internal void AppendDynamicClauseIncremental(
        Engine engine, int functorId, Clause newClause)
    {
        if (!_dynChains.TryGetValue(functorId, out var chain)) return;
        if (chain.TailNextAddr < 0) return;
        if (engine.CurrentProgram is null) return;
        if (engine.DynamicFailStubAddr <= 0) return;

        // Apply the same transforms the setup path runs — dynamic clauses
        // share a flat module rewrite context.
        var single = new[] { newClause };
        var transformed = PhraseTransform.Apply(
            MetaTransform.Apply(DcgTransform.Apply(single)));
        transformed = Shumway.Compiler.Modes.ModeSpecializationTransform.Apply(
            transformed, Modes);
        var dynCtx = new ModuleRewrite.Context(
            DefaultModuleName, new HashSet<int>(), _dynamicFunctors);
        var rewritten = transformed.Select(c => ModuleRewrite.Rewrite(c, dynCtx))
            .ToList();
        if (rewritten.Count == 0) return;
        var compiledClause = new ClauseCompiler().Compile(
            rewritten[0],
            _literalPools.Strings, _literalPools.Floats, _literalPools.BigInts);

        // Build the chunk:
        //   retry_me_else <fail-stub>   (5 bytes — chain op, <next>=fail-stub)
        //   check_visible <born> <died> (17 bytes)
        //   <body bytes>
        var emitter = new BytecodeEmitter();
        emitter.EmitRetryMeElse(engine.DynamicFailStubAddr);
        const int NextOperandLocal = 1;        // position of <next> operand
        emitter.EmitCheckVisible(born: _dbGeneration, died: long.MaxValue);
        const int DiedOperandLocal = 5 + 9;    // retry_me_else (5) + opcode (1) + born (8)
        int bodyStartLocal = emitter.Position;
        emitter.AppendBytes(compiledClause.Bytecode);
        byte[] chunk = emitter.ToBytes();

        // Append.
        int chunkAddr = engine.AppendCode(chunk);
        var program = engine.CurrentProgram;

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
        // points at our chunk's chain instruction.
        Shumway.Core.BytecodeIO.WriteInt32(program, chain.TailNextAddr, chunkAddr);

        // Update chain state.
        chain.Entries.Add(new DynChainEntry(
            newClause,
            died: chunkAddr + DiedOperandLocal,
            next: chunkAddr + NextOperandLocal));
        chain.TailNextAddr = chunkAddr + NextOperandLocal;

        // The clause may have interned new literals — refresh the
        // interpreter so check_visible isn't running against a stale
        // pool snapshot for any subsequent call.
        engine.RefreshLiteralPoolsCallback?.Invoke(
            _literalPools.Strings.Snapshot(),
            _literalPools.Floats.Snapshot(),
            _literalPools.BigInts.Snapshot());
    }

    /// <summary>Test hook: returns the chain's current tail-next address
    /// (where the next assertz would patch), or <c>null</c> when no chain
    /// state exists.</summary>
    internal int? PeekTailNextAddr(int functorId)
    {
        if (!_dynChains.TryGetValue(functorId, out var chain)) return null;
        return chain.TailNextAddr;
    }

    /// <summary>Test hook: returns the chain's current head-clause
    /// address — what asserta will demote to retry_me_else + nops on the
    /// next call.</summary>
    internal int? PeekHeadClauseAddr(int functorId)
    {
        if (!_dynChains.TryGetValue(functorId, out var chain)) return null;
        return chain.HeadClauseAddr;
    }

    /// <summary>ADR-015 chunk C step 4 — asserta path. Compiles the new
    /// clause as a chunk headed by <c>try_me_else &lt;old-head&gt;</c>,
    /// appends it, demotes the previous head's <c>try_me_else</c> in
    /// place to <c>retry_me_else &lt;same-next&gt;</c> + 4 nops (same
    /// 9-byte footprint), and patches the trampoline's
    /// <c>execute &lt;chain-head&gt;</c> to the new chunk. Falls back
    /// silently when the chain doesn't have a trampoline (paso-3
    /// emission or indexed dispatch).</summary>
    internal void PrependDynamicClauseIncremental(
        Engine engine, int functorId, Clause newClause)
    {
        if (!_dynChains.TryGetValue(functorId, out var chain)) return;
        if (chain.TrampolineExecuteOperandAddr < 0) return;
        if (engine.CurrentProgram is null) return;
        if (engine.DynamicFailStubAddr <= 0) return;

        var (_, arity) = FunctorTable.Lookup(functorId);

        // Same transform pipeline as the setup path.
        var single = new[] { newClause };
        var transformed = PhraseTransform.Apply(
            MetaTransform.Apply(DcgTransform.Apply(single)));
        transformed = Shumway.Compiler.Modes.ModeSpecializationTransform.Apply(
            transformed, Modes);
        var dynCtx = new ModuleRewrite.Context(
            DefaultModuleName, new HashSet<int>(), _dynamicFunctors);
        var rewritten = transformed.Select(c => ModuleRewrite.Rewrite(c, dynCtx))
            .ToList();
        if (rewritten.Count == 0) return;
        var compiledClause = new ClauseCompiler().Compile(
            rewritten[0],
            _literalPools.Strings, _literalPools.Floats, _literalPools.BigInts);

        // Chunk layout:
        //   try_me_else <chain-head-target>, <arity>   (9 bytes)
        //   check_visible <born> <died>                (17 bytes)
        //   <body>
        int oldHead = chain.HeadClauseAddr;
        int chainHeadTarget = oldHead >= 0 ? oldHead : engine.DynamicFailStubAddr;

        var emitter = new BytecodeEmitter();
        emitter.EmitTryMeElse(chainHeadTarget, arity);
        const int NextOperandLocal = 1;
        emitter.EmitCheckVisible(born: _dbGeneration, died: long.MaxValue);
        const int DiedOperandLocal = 9 + 9;            // try_me_else (9) + opcode (1) + born (8)
        int bodyStartLocal = emitter.Position;
        emitter.AppendBytes(compiledClause.Bytecode);
        byte[] chunk = emitter.ToBytes();

        int chunkAddr = engine.AppendCode(chunk);
        var program = engine.CurrentProgram;

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
            next: chunkAddr + NextOperandLocal));
        // If this was the first ever clause, the new chunk is also the tail.
        if (chain.TailNextAddr < 0)
            chain.TailNextAddr = chunkAddr + NextOperandLocal;

        // Refresh interpreter pools — same reasoning as the assertz path.
        engine.RefreshLiteralPoolsCallback?.Invoke(
            _literalPools.Strings.Snapshot(),
            _literalPools.Floats.Snapshot(),
            _literalPools.BigInts.Snapshot());
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
    private void PatchDiedFromChain(Engine engine, int functorId, int clauseIndex)
    {
        if (!_dynChains.TryGetValue(functorId, out var chain)) return;
        if (clauseIndex < 0 || clauseIndex >= chain.Entries.Count) return;
        var entry = chain.Entries[clauseIndex];
        var program = engine.CurrentProgram;
        if (program is not null && entry.DiedOperandAddr > 0)
            BytecodeIO.WriteInt64(program, entry.DiedOperandAddr, _dbGeneration);
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
        _dynChains.Clear();
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
        _dynChains.Remove(fid);
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
                    next: pendingNextOperand));
                pendingNextOperand = -1;
                clauseIndex++;
            }
            pc += info.Size;
        }
        chain.TailNextAddr = tailNextOperand;
        // Always record chain state when a tail-next exists, even when
        // _dynamicClauses is empty (declared-but-never-asserted dynamic
        // predicates have the empty-stub clause as the patch target for
        // the first incremental assertz).
        if (chain.Entries.Count > 0 || tailNextOperand >= 0)
            _dynChains[fid] = chain;
    }

    /// <summary>Adds a fail-only stub clause for every dynamic functor that
    /// has neither static nor asserted clauses yet, so that calls to it
    /// resolve at link time (and fail at runtime — which is what an
    /// "empty dynamic predicate" should do).</summary>
    private void EmitEmptyDynamicStubs(
        List<Clause> allRewritten, Shumway.Compiler.Lexer.SourcePosition pos)
    {
        if (_dynamicFunctors.Count == 0) return;

        var seen = new HashSet<int>();
        foreach (var c in allRewritten)
            if (TryExtractHead(c, out string n, out int a))
                seen.Add(FunctorTable.Intern(
                    AtomTable.Intern(n, permanent: true).Id, a));

        foreach (int fid in _dynamicFunctors)
        {
            if (seen.Contains(fid)) continue;
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
        List<string> varNames, int[] varHeapIndices, Engine engine)
    {
        var bindings = new Dictionary<string, Term>(varNames.Count);
        for (int i = 0; i < varNames.Count; i++)
            bindings[varNames[i]] = TermReader.Materialize(engine, varHeapIndices[i]);
        return new Solution(success: true, bindings: bindings);
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
