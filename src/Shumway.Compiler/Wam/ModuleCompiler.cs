using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Compiler.Wam;

/// <summary>
/// Compiles a Prolog source's clause stream into a <see cref="CompiledModule"/>.
/// Clauses are grouped by functor (name + arity), and each group is handed to
/// <see cref="PredicateCompiler"/>. Source order is preserved across the whole
/// module: a predicate's slot in <see cref="CompiledModule.Predicates"/> matches
/// the position of its first clause in the source, and its clauses are tried in
/// the order they were written.
///
/// <para>Directives encountered in the stream are <em>not</em> emitted as
/// bytecode — they were already processed by <see cref="ClauseReader"/> (the
/// <c>:- op</c> case mutates the operator table in place) and are simply
/// skipped here.</para>
/// </summary>
public sealed class ModuleCompiler
{
    /// <summary>Load-profiling counters (SHUMWAY_LOAD_PROF): ticks spent in the
    /// ADR-030 cut-elision fixpoint, predicates actually compiled vs reused from
    /// the skip cache, and ticks inside PredicateCompiler — across all Compile
    /// calls this process.</summary>
    public static long ProfElideTicks, ProfCompiledPreds, ProfSkippedPreds, ProfPredTicks;
    public static long ProfGroupTicks, ProfSnapshotTicks, ProfCompileCalls;
    public static long ProfMissNoEntry, ProfMissRejected;
    public static System.Collections.Generic.Dictionary<int, int>? ProfCompiledByFid;

    /// <summary>propagated to
    /// <see cref="PredicateCompiler.EmitDebugInfo"/>. <c>false</c> drops
    /// the per-clause Meta/DbgInfo markers from the emitted bytecode —
    /// Release mode in <c>shumway-compile</c> sets this so a stripped
    /// .shmo's bytecode carries no debug bytes at all.</summary>
    public bool EmitDebugInfo { get; set; } = true;

    /// <summary>ADR-035 — propagated to
    /// <see cref="PredicateCompiler.DebugCodegen"/>: frames on every rule clause
    /// and a runtime-switchable last call.</summary>
    public bool DebugCodegen { get; set; }

    /// <summary>ADR-035 — propagated to
    /// <see cref="PredicateCompiler.DebugFileId"/>.</summary>
    public int DebugFileId { get; set; }

    /// <summary>ADR-035 — functors that a <c>:- disable_debug.</c> region covered.
    /// They compile with full release codegen even while the rest of the program
    /// is debuggable: no frames forced, no runtime-switchable last call, no stop
    /// sites. A debugger sees them as one opaque step, and the predicates they
    /// call are debugged normally — which is the point of being able to switch
    /// debugging off for part of a module rather than all of it.</summary>
    public ISet<int>? NonDebuggableFunctors { get; set; }

    /// <summary>ADR-030 — when set, run <see cref="DeterminismAnalysis"/> over the
    /// whole clause set (which this compiler already sees in full) and drop the
    /// redundant trailing top-level cut from each static predicate's last clause
    /// whose prefix goals are all deterministic. Off by default: only the
    /// whole-module *consult* / bundle-build sites turn it on — never query-goal
    /// or single-clause <c>assertz</c> compilation, which lack the module view
    /// the fixpoint needs. Dynamic predicates are always excluded (their clause
    /// set changes at runtime, so "last clause" is not stable).</summary>
    public bool ElideRedundantCuts { get; set; }

    public CompiledModule Compile(IEnumerable<Clause> clauses)
        => Compile(clauses, cache: null);

    /// <summary>Overload accepting the JIT-indexing
    /// <paramref name="unindexedFunctors"/> set without a skip-compile
    /// cache.</summary>
    public CompiledModule Compile(
        IEnumerable<Clause> clauses,
        ISet<int>? unindexedFunctors)
        => Compile(clauses, cache: null, unindexedFunctors);

    /// <summary><paramref name="cache"/> short-circuits compilation: any
    /// predicate-group whose functor id is in the cache <em>and</em> whose
    /// cached <see cref="CompiledPredicate"/> doesn't reference per-module
    /// literal-pool indices reuses the cached bytecode verbatim instead of
    /// running <see cref="PredicateCompiler"/> over the source clauses.
    /// This is the Tier-0 half of the bundle pipeline's skip-compile path
    /// — a loaded bundle's <c>CompiledPredicate</c>s can be
    /// re-served at query setup without the consulted-source round trip.
    /// Cache misses (functor not in the cache, or the cached predicate
    /// uses literals whose indices wouldn't survive a fresh pool) fall
    /// through to normal compilation.</summary>
    public CompiledModule Compile(
        IEnumerable<Clause> clauses,
        IReadOnlyDictionary<int, CompiledPredicate>? cache)
        => Compile(clauses, cache, unindexedFunctors: null);

    /// <summary><paramref name="unindexedFunctors"/> is the
    /// set of functor ids the engine wants compiled without indexing —
    /// dynamic predicates that haven't proven hot at runtime. Each such
    /// functor's group goes to <see cref="PredicateCompiler.Compile"/>
    /// with <c>enableIndexing: false</c>, producing a plain
    /// <c>try_me_else</c> chain instead of switch tables.</summary>
    public CompiledModule Compile(
        IEnumerable<Clause> clauses,
        IReadOnlyDictionary<int, CompiledPredicate>? cache,
        ISet<int>? unindexedFunctors)
        => Compile(clauses, cache, unindexedFunctors, pools: null, dynamicFunctors: null, failStubAddr: 0);

    public CompiledModule Compile(
        IEnumerable<Clause> clauses,
        IReadOnlyDictionary<int, CompiledPredicate>? cache,
        ISet<int>? unindexedFunctors,
        LiteralPools? pools)
        => Compile(clauses, cache, unindexedFunctors, pools, dynamicFunctors: null, failStubAddr: 0);

    public CompiledModule Compile(
        IEnumerable<Clause> clauses,
        IReadOnlyDictionary<int, CompiledPredicate>? cache,
        ISet<int>? unindexedFunctors,
        LiteralPools? pools,
        ISet<int>? dynamicFunctors)
        => Compile(clauses, cache, unindexedFunctors, pools, dynamicFunctors, failStubAddr: 0);

    /// <summary><paramref name="pools"/> (ADR-015 chunk B) lets the caller
    /// supply persistent literal pools instead of the fresh per-call set.
    /// Passed pools accumulate across compilations, so a literal keeps a
    /// stable id from one query to the next. When <c>null</c> a fresh set
    /// is used — the original per-module behaviour.
    ///
    /// <para><paramref name="dynamicFunctors"/> (ADR-015 chunk C) marks
    /// the functors whose clauses should be compiled with the
    /// generation-filtered dispatch prefix: an <c>enter_dynamic</c> opcode
    /// at the predicate entry and a <c>check_visible</c> guard before each
    /// clause body. Step 3 emits them with always-visible sentinel values
    /// (born=0, died=long.MaxValue) — no observable behaviour change.</para></summary>
    public CompiledModule Compile(
        IEnumerable<Clause> clauses,
        IReadOnlyDictionary<int, CompiledPredicate>? cache,
        ISet<int>? unindexedFunctors,
        LiteralPools? pools,
        ISet<int>? dynamicFunctors,
        int failStubAddr)
    {
        ArgumentNullException.ThrowIfNull(clauses);

        // ADR-030 — redundant-cut elimination runs before grouping, over the full
        // clause list (the fixpoint needs every predicate). Dynamic functors are
        // excluded via the eligibility gate. Off unless a whole-module consult /
        // bundle-build site opts in.
        if (ElideRedundantCuts)
        {
            long profEl0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var materialised = clauses as IReadOnlyList<Clause> ?? clauses.ToList();
            clauses = DeterminismAnalysis.EliminateRedundantTrailingCuts(
                materialised,
                dynamicFunctors is null
                    ? null
                    : c => !dynamicFunctors.Contains(GetFunctorId(c)));
            ProfElideTicks += System.Diagnostics.Stopwatch.GetTimestamp() - profEl0;
        }

        // Group by functor in first-occurrence order. The order matters: when
        // we emit the program, predicates appear in the order the source
        // introduced them, which is the natural debugging-friendly order.
        ProfCompileCalls++;
        long profGr0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var groups = new Dictionary<int, List<Clause>>();
        var order = new List<int>();

        foreach (var clause in clauses)
        {
            if (clause.Kind == ClauseKind.Directive)
                continue;   // already executed by ClauseReader

            int functorId = GetFunctorId(clause);
            if (!groups.TryGetValue(functorId, out var list))
            {
                list = new List<Clause>();
                groups[functorId] = list;
                order.Add(functorId);
            }
            list.Add(clause);
        }
        ProfGroupTicks += System.Diagnostics.Stopwatch.GetTimestamp() - profGr0;

        // Persistent pools (ADR-015) accumulate across compilations so
        // literal ids stay stable query to query; absent them, a fresh
        // per-module set — the original behaviour.
        bool callerSuppliedPools = pools is not null;
        pools ??= new LiteralPools();
        var stringLiterals = pools.Strings;
        var floatLiterals = pools.Floats;
        var bigIntLiterals = pools.BigInts;

        var predicates = new List<CompiledPredicate>(order.Count);
        var predicateCompiler = new PredicateCompiler
        {
            EmitDebugInfo = EmitDebugInfo,
            DebugCodegen = DebugCodegen,
            DebugFileId = DebugFileId,
        };
        foreach (int fid in order)
        {
            // Reusable when compiled against THESE pools (persistent,
            // append-only — literal ids stable), or pool-free (safe against
            // any pools; covers bundle-decoded entries).
            if (cache is not null && cache.TryGetValue(fid, out var cached))
            {
                if (ReferenceEquals(cached.PoolsRef, pools)
                    || IsCachedPredicateReusable(cached))
                {
                    predicates.Add(cached);
                    ProfSkippedPreds++;
                    continue;
                }
                ProfMissRejected++;
            }
            else
            {
                ProfMissNoEntry++;
            }
            bool enableIndexing = unindexedFunctors is null
                || !unindexedFunctors.Contains(fid);
            bool isDynamic = dynamicFunctors is not null && dynamicFunctors.Contains(fid);
            // ADR-035 — debuggability is per predicate, not per module: the
            // `:- disable_debug.` / `:- enable_debug.` directives are positional.
            predicateCompiler.DebugCodegen =
                DebugCodegen && NonDebuggableFunctors?.Contains(fid) != true;
            long profPd0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var compiled = predicateCompiler.Compile(
                groups[fid], stringLiterals, floatLiterals, bigIntLiterals,
                enableIndexing, isDynamic, failStubAddr);
            if (callerSuppliedPools) compiled.PoolsRef = pools;
            predicates.Add(compiled);
            ProfPredTicks += System.Diagnostics.Stopwatch.GetTimestamp() - profPd0;
            ProfCompiledPreds++;
            if (ProfCompiledByFid is { } pcf)
                pcf[fid] = pcf.TryGetValue(fid, out int n) ? n + 1 : 1;
        }

        long profSn0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = new CompiledModule(
            predicates,
            stringLiterals.Snapshot(),
            floatLiterals.Snapshot(),
            bigIntLiterals.Snapshot());
        ProfSnapshotTicks += System.Diagnostics.Stopwatch.GetTimestamp() - profSn0;
        return result;
    }

    /// <summary>True iff <paramref name="pred"/>'s bytecode references no
    /// per-module literal pool (string / float / big-integer) — i.e. it
    /// can be lifted into a freshly-compiled module without its
    /// <see cref="OperandKind.LiteralId"/> operands needing to be
    /// re-keyed to the new pools. Atom ids, functor ids, and builtin
    /// ids are all globally interned so they don't need this guard.
    ///
    /// <para>Exposed so the embedding layer can decide which
    /// dynamic predicates are eligible for cross-query caching: a freshly
    /// compiled dynamic predicate is safe to reuse next query only if
    /// none of its operands carry pool-specific literal ids.</para></summary>
    public static bool IsCachedPredicateReusable(CompiledPredicate pred)
    {
        // the scan below is a pure function of the immutable
        // bytecode; memoize it on the predicate so the three per-query
        // call sites (cache reuse here, plus the static / dynamic cache
        // populate loops at query setup) stop re-walking every cached
        // predicate's entire bytecode on every query.
        int memo = pred.PoolFreeMemo;
        if (memo != 0) return memo == 2;
        bool result = ComputePoolFree(pred);
        pred.PoolFreeMemo = result ? 2 : 1;
        return result;
    }

    private static bool ComputePoolFree(CompiledPredicate pred)
    {
        byte[] code = pred.Bytecode;
        int pc = 0;
        while (pc < code.Length)
        {
            byte opByte = code[pc];
            var info = OpcodeTable.Get(opByte);
            if (!info.IsDefined || info.Size == 0) return false;
            if (info.OperandKinds is not null)
            {
                // every literal pool (float, bigint, string/PSTR)
                // is a per-engine LiteralPool<T>: append-only and deduplicating,
                // so the value at a given id NEVER moves, and the only flow that
                // consults the cross-query caches (query setup) always compiles
                // against the engine's persistent `_literalPools` instances.
                // A literal id is therefore as stable as an atom/functor id
                // within its engine, and predicates carrying float, bigint or
                // string literals are all cache-reusable (floats, bigint and
                // PSTR each audited before being exempted).
                // The guard remains for any FUTURE LiteralId carrier outside
                // this audited set.
                bool stableLiteralOp = opByte is (byte)Opcode.GetFloat
                    or (byte)Opcode.PutFloat or (byte)Opcode.UnifyFloat
                    or (byte)Opcode.GetBigInt or (byte)Opcode.PutBigInt
                    or (byte)Opcode.UnifyBigInt
                    or (byte)Opcode.GetPstr or (byte)Opcode.PutPstr;
                for (int i = 0; i < info.OperandKinds.Length; i++)
                {
                    if (info.OperandKinds[i] == OperandKind.LiteralId && !stableLiteralOp)
                        return false;
                }
            }
            pc += info.Size;
        }
        return true;
    }

    private static int GetFunctorId(Clause clause)
    {
        if (clause.Kind != ClauseKind.DcgRule && clause.HeadFidMemo != 0)
            return clause.HeadFidMemo - 1;
        // For facts and rules the head is the term (or the :-/2's first arg).
        Term head = clause.Kind == ClauseKind.Rule
            ? ((CompoundTerm)clause.Term).Args[0]
            : clause.Term;

        int fid = head switch
        {
            AtomTerm a =>
                FunctorTable.Intern(AtomTable.Intern(a.Name, permanent: true).Id, 0),
            CompoundTerm c =>
                FunctorTable.Intern(AtomTable.Intern(c.Functor, permanent: true).Id, c.Args.Length),
            _ => throw new InvalidOperationException(
                $"Clause head must be an atom or compound, got {head.GetType().Name}."),
        };
        if (clause.Kind != ClauseKind.DcgRule) clause.HeadFidMemo = fid + 1;
        return fid;
    }
}
