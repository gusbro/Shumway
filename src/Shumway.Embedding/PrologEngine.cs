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
    /// via unification on a materialised heap copy).</summary>
    internal bool RemoveDynamicByReference(int functorId, Clause clause)
    {
        if (!_dynamicClauses.TryGetValue(functorId, out var list)) return false;
        bool removed = list.Remove(clause);
        if (removed) InvalidateDynamicCache(functorId);
        return removed;
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
            if (entry.CompiledIl is null || entry.CompiledIl.Length == 0) continue;
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

    /// <summary>Drops the cached compiled predicate for
    /// <paramref name="functorId"/>. Called by every mutation path
    /// (<see cref="Assertz"/>, <see cref="Asserta"/>,
    /// <see cref="RemoveDynamic"/>, <see cref="RemoveDynamicByReference"/>,
    /// <see cref="AbolishDynamic"/>) so the next query sees a fresh
    /// compile that picks up the modification.</summary>
    private void InvalidateDynamicCache(int functorId)
        => _dynamicPredicateCache.Remove(functorId);

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
        try { result = interp.Run(program, 0); }
        catch (PrologHaltException hex) { halted = true; host.LastHaltExitCode = hex.ExitCode; result = InterpreterResult.Failed; }
        catch (ShumwayPrologException) { { var st = host.CaptureStackTrace(engine); host.LastErrorStackTrace = st.Plain; host.LastErrorStackTraceWithPositions = st.WithPositions; throw; } }
        catch (PrologRuntimeException) { { var st = host.CaptureStackTrace(engine); host.LastErrorStackTrace = st.Plain; host.LastErrorStackTraceWithPositions = st.WithPositions; throw; } }

        while (!halted && result == InterpreterResult.Halted)
        {
            yield return BuildSolution(varNames, varHeapIndices, engine);
            try { result = interp.Backtrack(program); }
            catch (PrologHaltException hex) { halted = true; host.LastHaltExitCode = hex.ExitCode; break; }
            catch (ShumwayPrologException) { { var st = host.CaptureStackTrace(engine); host.LastErrorStackTrace = st.Plain; host.LastErrorStackTraceWithPositions = st.WithPositions; throw; } }
            catch (PrologRuntimeException) { { var st = host.CaptureStackTrace(engine); host.LastErrorStackTrace = st.Plain; host.LastErrorStackTraceWithPositions = st.WithPositions; throw; } }
        }
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
    public void ConsultString(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var rawClauses = new ClauseReader(new Lexer(source), _operators, _flags).ReadAll().ToList();

        string moduleName = DefaultModuleName;
        bool moduleDirectiveSeen = false;
        var publics = new HashSet<int>();
        var clauses = new List<Clause>();
        HashSet<int>? pendingDiscontiguous = null;
        HashSet<int>? pendingMultifile = null;
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
        if (_precompiledClauseCache.Count == 0 && _dynamicPredicateCache.Count == 0)
        {
            skipCompileCache = null;
        }
        else if (_dynamicPredicateCache.Count == 0)
        {
            skipCompileCache = _precompiledClauseCache;
        }
        else
        {
            var merged = new Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>(
                _precompiledClauseCache);
            foreach (var (fid, pred) in _dynamicPredicateCache)
                merged[fid] = pred;
            skipCompileCache = merged;
        }
        var module = new ModuleCompiler().Compile(
            allRewritten, skipCompileCache, unindexedFunctors);

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
        byte[] prefix = launcher.ToBytes();

        var linkResult = new Linker().Link(module, loadOffset: prefix.Length);
        // The synthetic query stays under its bare functor (it's local to
        // user but ModuleRewrite never mangles __query__ because it's not
        // present in user's local set: it was added after locals were
        // computed and isn't part of the user-defined predicates).
        int queryFunctorId = FunctorTable.Intern(
            AtomTable.Intern(queryFunctor, permanent: true).Id,
            varNames.Count);
        BytecodeIO.WriteInt32(prefix, callPos + 1, linkResult.Addresses[queryFunctorId]);

        byte[] program = new byte[prefix.Length + linkResult.Bytecode.Length];
        Array.Copy(prefix, program, prefix.Length);
        Array.Copy(linkResult.Bytecode, 0, program, prefix.Length, linkResult.Bytecode.Length);

        var engine = new Engine
        {
            Out = Out,
            Host = this,
            // Lets attributed-variable unification (chunk 79) run
            // verify_attributes/4 hooks — only the embedding layer can
            // execute Prolog goals, so the Core engine calls back here.
            AttrHookRunner = MetaBuiltins.RunVerifyAttributes,
            Operators = new OperatorTableAdapter(_operators),
            // The current-query address map lets IL-emitted Execute
            // opcodes (chunk 47) resolve their tail-call target via a
            // stable functor-id lookup instead of an embedded address
            // that would only be valid for one query's linked layout.
            CurrentFunctorAddresses = linkResult.Addresses,
            // String literal pool for IL-emitted get_pstr/put_pstr
            // (chunk 50) and the linked program byte array for the
            // IL Call re-entry helper.
            CurrentStringLiterals = module.StringLiterals,
            CurrentProgram = program,
        };
        var interp = new BytecodeInterpreter(
            engine, module.StringLiterals, module.FloatLiterals,
            linkResult.SwitchTables, module.BigIntLiterals);
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
