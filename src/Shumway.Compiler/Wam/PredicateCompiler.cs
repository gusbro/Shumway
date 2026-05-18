using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Compiler.Wam;

/// <summary>
/// Compiles all clauses of a single predicate into one <see cref="CompiledPredicate"/>.
/// The clauses must share a functor name and arity; callers (typically
/// <see cref="ModuleCompiler"/>) are responsible for grouping a source file's
/// flat clause stream by functor before invoking this.
///
/// <para><b>Single-clause case.</b> If there's exactly one clause the predicate's
/// bytecode is just that clause's bytes — no dispatch wrapping is needed because
/// no alternative exists to backtrack to.</para>
///
/// <para><b>Multi-clause case, no indexing opportunity.</b> If every clause has
/// a variable first argument (or the predicate is 0-ary), the compiler emits the
/// classical <c>try_me_else</c> / <c>retry_me_else</c> / <c>trust_me</c> chain
/// where each clause body is inlined right after its dispatch instruction. The
/// embedded BPs are pre-computed in pass 1 so pass 2 emits without
/// back-patching.</para>
///
/// <para><b>Multi-clause case, first-argument indexing.</b> When at least one
/// clause discriminates by the type/value of <c>A1</c>, the compiler emits
/// (per ADR-007):
/// <code>
///   switch_on_term VarLbl, ConstLbl, ListLbl, StructLbl
///   VarLbl:    try / retry / trust over every clause in source order
///   ConstLbl:  switch_on_atom (if atom clauses) → switch_on_integer (if int
///              clauses) → fall through to VarLbl as the last resort
///   ListLbl:   try / retry / trust over (list clauses + var clauses)
///   StructLbl: switch_on_structure
///   &lt;per-group chains for atom / int / struct groups with 2+ clauses&gt;
///   &lt;clause bodies, one after another, no dispatch wrapper&gt;
/// </code>
/// Single-clause groups skip the chain — the switch table maps the key
/// straight to the clause body. Variable-headed clauses join every bucket so
/// they're tried in source order alongside the type-specific clauses.</para>
/// </summary>
public sealed class PredicateCompiler
{
    public CompiledPredicate Compile(IReadOnlyList<Clause> clauses)
        => Compile(clauses,
            new LiteralPool<string>(),
            new LiteralPool<double>(),
            new LiteralPool<System.Numerics.BigInteger>());

    public CompiledPredicate Compile(
        IReadOnlyList<Clause> clauses,
        LiteralPool<string> stringLiterals,
        LiteralPool<double> floatLiterals,
        LiteralPool<System.Numerics.BigInteger> bigIntLiterals)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        if (clauses.Count == 0)
            throw new ArgumentException("At least one clause is required.", nameof(clauses));

        // Compile each clause independently.
        var compiledClauses = new List<CompiledClause>(clauses.Count);
        var compiler = new ClauseCompiler();
        foreach (var c in clauses)
            compiledClauses.Add(compiler.Compile(c, stringLiterals, floatLiterals, bigIntLiterals));

        // Verify all clauses share the same functor signature.
        int functorId = compiledClauses[0].FunctorId;
        int arity = compiledClauses[0].Arity;
        for (int i = 1; i < compiledClauses.Count; i++)
        {
            if (compiledClauses[i].FunctorId != functorId)
                throw new ArgumentException(
                    "All clauses passed to PredicateCompiler must share the same functor "
                    + $"(clause 0 = id {functorId}, clause {i} = id {compiledClauses[i].FunctorId}).");
        }

        // Single-clause shortcut.
        if (compiledClauses.Count == 1)
        {
            return new CompiledPredicate(
                compiledClauses[0].Bytecode,
                functorId,
                arity,
                clauseCount: 1,
                callSites: compiledClauses[0].CallSites,
                dispatchSites: Array.Empty<int>(),
                switchTables: Array.Empty<SwitchTable>(),
                switchTableIdSites: Array.Empty<int>(),
                sourcePosition: clauses[0].Position);
        }

        // Decide whether first-argument indexing pays off. It does whenever at
        // least one clause discriminates by the type/value of A1; if every
        // clause has a variable first arg, the switch dispatch would just add
        // a constant overhead with no skipped work.
        var firstArgs = clauses.Select(ClassifyFirstArg).ToArray();
        bool indexable = arity > 0
            && firstArgs.Any(f => f.Kind != FirstArgKind.Var && f.Kind != FirstArgKind.Other);

        return indexable
            ? CompileIndexed(compiledClauses, firstArgs, functorId, arity, clauses[0].Position)
            : CompileTryMeElseChain(compiledClauses, functorId, arity, clauses[0].Position);
    }

    // ============================================================================
    // try_me_else / retry_me_else / trust_me chain (no first-arg indexing)
    // ============================================================================

    private static CompiledPredicate CompileTryMeElseChain(
        IReadOnlyList<CompiledClause> compiledClauses, int functorId, int arity,
        Shumway.Compiler.Lexer.SourcePosition position)
    {
        int n = compiledClauses.Count;
        int[] clauseBodyOffsets = new int[n];
        int pos = 0;
        for (int i = 0; i < n; i++)
        {
            int dispatchSize = i == 0 ? 9 : i == n - 1 ? 1 : 5;
            pos += dispatchSize;
            clauseBodyOffsets[i] = pos;
            pos += compiledClauses[i].Bytecode.Length;
        }

        var emitter = new BytecodeEmitter();
        var callSites = new List<CallSite>();
        var dispatchSites = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (i == 0)
            {
                int nextDispatch = clauseBodyOffsets[1] - DispatchSizeFor(1, n);
                int opPos = emitter.Position;
                emitter.EmitTryMeElse(nextDispatch, arity);
                dispatchSites.Add(opPos + 1);
            }
            else if (i == n - 1)
            {
                emitter.EmitTrustMe();
            }
            else
            {
                int nextDispatch = clauseBodyOffsets[i + 1] - DispatchSizeFor(i + 1, n);
                int opPos = emitter.Position;
                emitter.EmitRetryMeElse(nextDispatch);
                dispatchSites.Add(opPos + 1);
            }

            int clauseStart = emitter.Position;
            emitter.AppendBytes(compiledClauses[i].Bytecode);
            foreach (var site in compiledClauses[i].CallSites)
                callSites.Add(new CallSite(
                    clauseStart + site.OpcodeOffset, site.CalleeFunctorId, site.IsExecute));
        }

        return new CompiledPredicate(
            emitter.ToBytes(), functorId, arity, n, callSites, dispatchSites,
            Array.Empty<SwitchTable>(), Array.Empty<int>(), position);
    }

    private static int DispatchSizeFor(int clauseIndex, int totalClauses) =>
        clauseIndex == 0
            ? 9
            : clauseIndex == totalClauses - 1
                ? 1
                : 5;

    // ============================================================================
    // First-argument indexing (ADR-007)
    // ============================================================================

    private enum FirstArgKind { Var, Atom, Int, List, Struct, Other }

    private readonly record struct FirstArgInfo(FirstArgKind Kind, int Key);

    private static FirstArgInfo ClassifyFirstArg(Clause clause)
    {
        // For a Rule the clause Term is `:-/2` with head at Args[0]; for a Fact
        // the clause Term IS the head.
        Term headTerm = clause.Kind == ClauseKind.Rule
            ? ((CompoundTerm)clause.Term).Args[0]
            : clause.Term;
        if (headTerm is not CompoundTerm compound || compound.Args.Length == 0)
            return new FirstArgInfo(FirstArgKind.Var, 0);

        return compound.Args[0] switch
        {
            VarTerm => new FirstArgInfo(FirstArgKind.Var, 0),
            AtomTerm a => new FirstArgInfo(
                FirstArgKind.Atom, AtomTable.Intern(a.Name, permanent: true).Id),
            IntTerm n when n.Value >= int.MinValue && n.Value <= int.MaxValue
                => new FirstArgInfo(FirstArgKind.Int, (int)n.Value),
            CompoundTerm c when c.Functor == "." && c.Args.Length == 2
                => new FirstArgInfo(FirstArgKind.List, 0),
            CompoundTerm c => new FirstArgInfo(
                FirstArgKind.Struct,
                FunctorTable.Intern(
                    AtomTable.Intern(c.Functor, permanent: true).Id, c.Args.Length)),
            // FloatTerm / StringTerm / IntTerm out of int range / etc. behave
            // like a variable for indexing purposes — they fall through to the
            // full chain.
            _ => new FirstArgInfo(FirstArgKind.Other, 0),
        };
    }

    private static CompiledPredicate CompileIndexed(
        IReadOnlyList<CompiledClause> compiledClauses,
        IReadOnlyList<FirstArgInfo> firstArgs,
        int functorId,
        int arity,
        Shumway.Compiler.Lexer.SourcePosition position)
    {
        int n = compiledClauses.Count;

        // ----- Bucketise -----

        var varIdxs = new List<int>();
        var atomMap = new SortedDictionary<int, List<int>>();
        var intMap = new SortedDictionary<int, List<int>>();
        var listIdxs = new List<int>();
        var structMap = new SortedDictionary<int, List<int>>();

        for (int i = 0; i < n; i++)
        {
            FirstArgInfo info = firstArgs[i];
            switch (info.Kind)
            {
                case FirstArgKind.Var: varIdxs.Add(i); break;
                case FirstArgKind.Atom: GetOrAdd(atomMap, info.Key).Add(i); break;
                case FirstArgKind.Int: GetOrAdd(intMap, info.Key).Add(i); break;
                case FirstArgKind.List: listIdxs.Add(i); break;
                case FirstArgKind.Struct: GetOrAdd(structMap, info.Key).Add(i); break;
                case FirstArgKind.Other: varIdxs.Add(i); break;
            }
        }

        // Each specific bucket gets the var clauses interleaved at their
        // source positions — var clauses match every concrete value.
        List<int> MergeWithVar(List<int> specifics)
        {
            var seen = new HashSet<int>(specifics);
            foreach (int v in varIdxs) seen.Add(v);
            var merged = seen.ToList();
            merged.Sort();
            return merged;
        }

        var atomBuckets = atomMap.ToDictionary(kv => kv.Key, kv => MergeWithVar(kv.Value));
        var intBuckets = intMap.ToDictionary(kv => kv.Key, kv => MergeWithVar(kv.Value));
        var structBuckets = structMap.ToDictionary(kv => kv.Key, kv => MergeWithVar(kv.Value));
        var listBucket = MergeWithVar(listIdxs);
        var varBucket = Enumerable.Range(0, n).ToList();   // VarLbl tries everything

        bool hasAtoms = atomBuckets.Count > 0;
        bool hasInts = intBuckets.Count > 0;
        bool hasStructs = structBuckets.Count > 0;

        // ----- Pass 1: layout offsets -----

        static int ChainSize(int count) => count switch
        {
            0 => 0,
            1 => 0,             // single-target buckets jump directly to the clause body
            _ => 9 + 5 * (count - 1),    // try(9) + (count-2)*retry(5) + trust(5)
        };

        int pos = 17;             // after switch_on_term
        int varLblPos = pos;
        pos += ChainSize(varBucket.Count);

        int switchOnAtomPos = hasAtoms ? pos : -1;
        if (hasAtoms) pos += 5;
        int switchOnIntPos = hasInts ? pos : -1;
        if (hasInts) pos += 5;

        int constLblPos = hasAtoms
            ? switchOnAtomPos
            : hasInts
                ? switchOnIntPos
                : varLblPos;

        int listLblPos;
        if (listBucket.Count == 0) listLblPos = varLblPos;
        else if (listBucket.Count == 1) listLblPos = -1;   // patched after clause body offsets known
        else { listLblPos = pos; pos += ChainSize(listBucket.Count); }

        int switchOnStructPos = hasStructs ? pos : -1;
        if (hasStructs) pos += 5;
        int structLblPos = hasStructs ? switchOnStructPos : varLblPos;

        var atomGroupPos = new Dictionary<int, int>();
        foreach (var (key, group) in atomBuckets)
            if (group.Count >= 2) { atomGroupPos[key] = pos; pos += ChainSize(group.Count); }
        var intGroupPos = new Dictionary<int, int>();
        foreach (var (key, group) in intBuckets)
            if (group.Count >= 2) { intGroupPos[key] = pos; pos += ChainSize(group.Count); }
        var structGroupPos = new Dictionary<int, int>();
        foreach (var (key, group) in structBuckets)
            if (group.Count >= 2) { structGroupPos[key] = pos; pos += ChainSize(group.Count); }

        int[] clauseBodyPos = new int[n];
        for (int i = 0; i < n; i++)
        {
            clauseBodyPos[i] = pos;
            pos += compiledClauses[i].Bytecode.Length;
        }

        // Now we can resolve any "single-clause direct jump" addresses.
        if (listBucket.Count == 1) listLblPos = clauseBodyPos[listBucket[0]];

        // ----- Pass 2: build switch tables (predicate-local addresses) -----

        SwitchTable BuildTable(
            IReadOnlyDictionary<int, List<int>> buckets,
            IReadOnlyDictionary<int, int> groupPos,
            int defaultAddr)
        {
            var keys = new int[buckets.Count];
            var values = new int[buckets.Count];
            int idx = 0;
            foreach (var (key, group) in buckets)
            {
                keys[idx] = key;
                values[idx] = group.Count == 1
                    ? clauseBodyPos[group[0]]
                    : groupPos[key];
                idx++;
            }
            return new SwitchTable(keys, values, defaultAddr);
        }

        var switchTables = new List<SwitchTable>();
        int atomTableLocalId = -1, intTableLocalId = -1, structTableLocalId = -1;
        if (hasAtoms)
        {
            // Atom default chains to switch_on_integer if present; if not (or
            // the int lookup also misses), we end up at VarLbl.
            int defaultAddr = hasInts ? switchOnIntPos : varLblPos;
            atomTableLocalId = switchTables.Count;
            switchTables.Add(BuildTable(atomBuckets, atomGroupPos, defaultAddr));
        }
        if (hasInts)
        {
            intTableLocalId = switchTables.Count;
            switchTables.Add(BuildTable(intBuckets, intGroupPos, varLblPos));
        }
        if (hasStructs)
        {
            structTableLocalId = switchTables.Count;
            switchTables.Add(BuildTable(structBuckets, structGroupPos, varLblPos));
        }

        // ----- Pass 3: emit bytecode -----

        var emitter = new BytecodeEmitter();
        var callSites = new List<CallSite>();
        var dispatchSites = new List<int>();
        var switchTableIdSites = new List<int>();

        int switchOnTermStart = emitter.Position;
        emitter.EmitSwitchOnTerm(varLblPos, constLblPos, listLblPos, structLblPos);
        // The four address operands of switch_on_term need predicate-local →
        // program-absolute translation by the linker.
        dispatchSites.Add(switchOnTermStart + 1);
        dispatchSites.Add(switchOnTermStart + 5);
        dispatchSites.Add(switchOnTermStart + 9);
        dispatchSites.Add(switchOnTermStart + 13);

        EmitChain(emitter, varBucket, clauseBodyPos, arity, dispatchSites);

        if (hasAtoms)
        {
            int site = emitter.Position + 1;
            switchTableIdSites.Add(site);
            emitter.EmitSwitchOnAtom(atomTableLocalId);
        }
        if (hasInts)
        {
            int site = emitter.Position + 1;
            switchTableIdSites.Add(site);
            emitter.EmitSwitchOnInteger(intTableLocalId);
        }

        if (listBucket.Count >= 2)
            EmitChain(emitter, listBucket, clauseBodyPos, arity, dispatchSites);

        if (hasStructs)
        {
            int site = emitter.Position + 1;
            switchTableIdSites.Add(site);
            emitter.EmitSwitchOnStructure(structTableLocalId);
        }

        foreach (var (key, group) in atomBuckets)
            if (group.Count >= 2)
                EmitChain(emitter, group, clauseBodyPos, arity, dispatchSites);
        foreach (var (key, group) in intBuckets)
            if (group.Count >= 2)
                EmitChain(emitter, group, clauseBodyPos, arity, dispatchSites);
        foreach (var (key, group) in structBuckets)
            if (group.Count >= 2)
                EmitChain(emitter, group, clauseBodyPos, arity, dispatchSites);

        for (int i = 0; i < n; i++)
        {
            int clauseStart = emitter.Position;
            emitter.AppendBytes(compiledClauses[i].Bytecode);
            foreach (var site in compiledClauses[i].CallSites)
                callSites.Add(new CallSite(
                    clauseStart + site.OpcodeOffset, site.CalleeFunctorId, site.IsExecute));
        }

        return new CompiledPredicate(
            emitter.ToBytes(), functorId, arity, n,
            callSites, dispatchSites, switchTables, switchTableIdSites, position);
    }

    /// <summary>Emits a <c>try</c> / (zero or more <c>retry</c>) / <c>trust</c>
    /// sequence over <paramref name="clauseIndices"/>'s clause body offsets.
    /// Each instruction's address operand is registered as a dispatch site so
    /// the linker can later shift them from predicate-local to absolute. Single
    /// or empty buckets don't emit anything — their lookups jump directly to
    /// the clause body (or fall to the default chain).</summary>
    private static void EmitChain(
        BytecodeEmitter emitter,
        IReadOnlyList<int> clauseIndices,
        int[] clauseBodyPos,
        int arity,
        List<int> dispatchSites)
    {
        int count = clauseIndices.Count;
        if (count < 2) return;

        for (int i = 0; i < count; i++)
        {
            int target = clauseBodyPos[clauseIndices[i]];
            int opPos = emitter.Position;
            if (i == 0)
            {
                emitter.EmitTry(target, arity);
                dispatchSites.Add(opPos + 1);
            }
            else if (i == count - 1)
            {
                emitter.EmitTrust(target);
                dispatchSites.Add(opPos + 1);
            }
            else
            {
                emitter.EmitRetry(target);
                dispatchSites.Add(opPos + 1);
            }
        }
    }

    private static List<int> GetOrAdd(SortedDictionary<int, List<int>> map, int key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<int>();
            map[key] = list;
        }
        return list;
    }
}
