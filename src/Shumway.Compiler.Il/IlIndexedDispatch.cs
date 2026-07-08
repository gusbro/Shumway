using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>
/// Chunk 216 — full first/multi-argument indexed dispatch for Tier-1 IL,
/// reproducing the WAM switch machinery (<c>switch_on_term</c> /
/// <c>switch_on_arg</c> + the typed <c>switch_on_atom</c> /
/// <c>switch_on_integer</c> / <c>switch_on_structure</c>(<c>_arg</c>)
/// tables) with O(1) key lookup and correct bucket backtracking, instead
/// of the chunk-189 linear clause walk.
///
/// <para>The model represents a predicate as a flat list of <b>chain
/// nodes</b>. A node is one <c>try</c> / <c>retry</c> / <c>trust</c>
/// position in the bytecode: it runs a particular clause body and, on
/// backtrack, hands off to its <see cref="ChainNode.NextCursor"/> (the
/// following node in the same chain, or -1 for a chain tail). This
/// generalises the chunk-188 emit, where the "next" was implicitly
/// <c>i + 1</c>; here a bucket chain links only the clauses that share a
/// key, and a var-head clause legitimately appears in several chains.</para>
///
/// <para>The dispatch cascade itself (which chain a given call enters) is
/// resolved at run time by <see cref="ResolveEntryCursor"/>, a dispatch-
/// only mini-interpreter that mirrors <c>BytecodeInterpreter</c>'s switch
/// opcodes exactly and returns the entry node's cursor. The emit then runs
/// that node and its chain; clause bodies are emitted once and shared
/// across the nodes that reference them.</para>
/// </summary>
internal sealed class IlIndexedDispatchInfo
{
    /// <summary>Clause body byte ranges, in source order; the index is the
    /// clause's cursor-independent identity used by <see cref="ChainNode"/>.</summary>
    public required IReadOnlyList<(int Start, int End)> Clauses { get; init; }

    /// <summary>Chain nodes; the list index is the node's cursor.</summary>
    public required IReadOnlyList<ChainNode> Nodes { get; init; }

    /// <summary>Maps a dispatch-target bytecode address (a chain head, or a
    /// clause body reached deterministically) to the cursor that runs it.</summary>
    public required IReadOnlyDictionary<int, int> AddrToEntryCursor { get; init; }

    /// <summary>The predicate's switch tables (key → bytecode address),
    /// needed by the runtime resolver.</summary>
    public required IReadOnlyList<SwitchTable> SwitchTables { get; init; }

    /// <summary>The predicate bytecode the resolver walks. May be the
    /// predicate's own bytecode (build-time path, addresses start at 0) or
    /// the engine's linked program (lazy-built at LoadBundle, predicate
    /// region starts at <see cref="EntryAddress"/>).</summary>
    public required byte[] Bytecode { get; init; }

    /// <summary>Byte offset within <see cref="Bytecode"/> where this
    /// predicate's dispatch cascade starts. Zero for the build-time path
    /// (predicate-relative bytecode); the linked-code address for the
    /// runtime path.</summary>
    public required int EntryAddress { get; init; }
}

internal readonly record struct ChainNode(int ClauseIndex, int NextCursor);

/// <summary>Recogniser + runtime resolver for <see cref="IlIndexedDispatchInfo"/>.
/// Must be <c>public</c> so the persisted IL (loaded via
/// <c>Assembly.Load</c> in a fresh process) can call
/// <see cref="ResolveEntryByFunctorId"/> — internal would trip
/// <see cref="System.MethodAccessException"/>. The implementation-detail
/// model types (<see cref="IlIndexedDispatchInfo"/>, <see cref="ChainNode"/>)
/// stay internal; the IL only references the entry point.</summary>
public static class IlIndexedDispatch
{
    // Switch opcode sizes (from OpcodeInfo).
    private const int SwitchOnTermSize = 17;
    private const int SwitchOnArgSize = 21;
    private const int SwitchTypedSize = 5;      // switch_on_atom / integer / structure
    private const int SwitchTypedArgSize = 9;   // *_arg variants
    private const int TrySize = 9;
    private const int RetrySize = 5;
    private const int TrustSize = 5;

    private static bool IsDispatchSwitch(Opcode op) => op is
        Opcode.SwitchOnTerm or Opcode.SwitchOnArg
        or Opcode.SwitchOnAtom or Opcode.SwitchOnInteger or Opcode.SwitchOnStructure
        or Opcode.SwitchOnAtomArg or Opcode.SwitchOnIntegerArg or Opcode.SwitchOnStructureArg
        or Opcode.SwitchOnAtomSub or Opcode.SwitchOnIntegerSub or Opcode.SwitchOnStructureSub;

    /// <summary>Tries to build the indexed-dispatch model for
    /// <paramref name="predicate"/>. Succeeds only for the WAM indexed
    /// shape (bytecode opens with switch_on_term or switch_on_arg, has the
    /// full try/retry/trust chain machinery, and every clause body fits the
    /// IL subset). Returns false (and a null model) otherwise.</summary>
    internal static bool TryDescribe(
        Shumway.Compiler.Wam.CompiledPredicate predicate,
        System.Func<Opcode, int, bool> isBodyOpcodeEmittable,
        out IlIndexedDispatchInfo? info)
        => TryDescribeBytes(predicate.Bytecode, 0, predicate.Bytecode.Length,
            predicate.ClauseCount, predicate.SwitchTables,
            isBodyOpcodeEmittable, out info);

    /// <summary>Address-range variant — works over <paramref name="code"/>
    /// in <c>[start, end)</c> with absolute addresses (matching the
    /// engine's linked code). Used at <c>LoadBundle</c> time to rebuild
    /// the model from the engine's linked program without needing a
    /// <c>CompiledPredicate</c>: pass <paramref name="expectedClauseCount"/>
    /// = -1 to skip the clause-count cross-check and
    /// <paramref name="isBodyOpcodeEmittable"/> = null to skip the
    /// IL-subset body check (the build-time emit already validated it).</summary>
    internal static bool TryDescribeBytes(
        byte[] code, int start, int end,
        int expectedClauseCount,
        IReadOnlyList<SwitchTable> switchTables,
        System.Func<Opcode, int, bool>? isBodyOpcodeEmittable,
        out IlIndexedDispatchInfo? info)
    {
        info = null;
        if (code.Length == 0 || start >= end) return false;
        var first = (Opcode)code[start];
        if (first != Opcode.SwitchOnTerm && first != Opcode.SwitchOnArg) return false;

        // ---- 1. Collect every try/retry/trust chain in [start, end). ----
        // Chains are contiguous runs: try (retry)* trust. Each node carries
        // the clause-body address it dispatches to.
        var chainNodeAddrs = new List<int>();          // addresses of all try/retry/trust nodes
        var nodeBodyAddr = new Dictionary<int, int>();  // node addr -> body addr
        var nodeNextAddr = new Dictionary<int, int>();  // node addr -> next node addr (-1 if tail)

        int pc = start;
        while (pc < end)
        {
            var op = (Opcode)code[pc];
            int size = OpcodeSize(code, pc);
            if (size <= 0) return false;
            if (op == Opcode.Try)
            {
                int body = BytecodeIO.ReadInt32(code, pc + 1);
                chainNodeAddrs.Add(pc);
                nodeBodyAddr[pc] = body;
                var next = pc + size < end ? (Opcode)code[pc + size] : (Opcode)0;
                nodeNextAddr[pc] = (pc + size < end && (next == Opcode.Retry || next == Opcode.Trust))
                    ? pc + size : -1;
            }
            else if (op == Opcode.Retry)
            {
                int body = BytecodeIO.ReadInt32(code, pc + 1);
                chainNodeAddrs.Add(pc);
                nodeBodyAddr[pc] = body;
                var next = pc + size < end ? (Opcode)code[pc + size] : (Opcode)0;
                nodeNextAddr[pc] = (pc + size < end && (next == Opcode.Retry || next == Opcode.Trust))
                    ? pc + size : -1;
            }
            else if (op == Opcode.Trust)
            {
                int body = BytecodeIO.ReadInt32(code, pc + 1);
                chainNodeAddrs.Add(pc);
                nodeBodyAddr[pc] = body;
                nodeNextAddr[pc] = -1;
            }
            pc += size;
        }
        if (chainNodeAddrs.Count == 0) return false;

        // ---- 2. Identify clause bodies (source order). ----
        // Every distinct body address a chain node points at is a clause
        // body. Sorted ascending gives source order (the WAM lays bodies
        // out in source order). When called with a known clause count
        // (build-time CompiledPredicate path), cross-check; at load time
        // (-1) trust the parse.
        var bodyAddrs = new SortedSet<int>(nodeBodyAddr.Values);
        if (expectedClauseCount >= 0 && bodyAddrs.Count != expectedClauseCount) return false;
        var bodyAddrToClause = new Dictionary<int, int>();
        int ci = 0;
        var clauseStarts = new List<int>(bodyAddrs.Count);
        foreach (int a in bodyAddrs) { bodyAddrToClause[a] = ci++; clauseStarts.Add(a); }

        // Clause ranges: [start_i, start_{i+1}) and the last to end.
        var clauses = new List<(int, int)>(clauseStarts.Count);
        for (int i = 0; i < clauseStarts.Count; i++)
        {
            int s = clauseStarts[i];
            int e = i + 1 < clauseStarts.Count ? clauseStarts[i + 1] : end;
            clauses.Add((s, e));
        }

        // ---- 3. (Optional) validate every clause body fits the IL subset. ----
        // Skipped at load time (the build-time emit already validated it).
        if (isBodyOpcodeEmittable is not null)
        {
            foreach (var (s, e) in clauses)
            {
                int q = s;
                while (q < e)
                {
                    var op = (Opcode)code[q];
                    int size = OpcodeSize(code, q);
                    if (size <= 0) return false;
                    if (!isBodyOpcodeEmittable(op, q)) return false;
                    q += size;
                }
            }
        }

        // ---- 4. Assign cursors to chain nodes (in address order for
        // determinism) and to deterministic single-clause direct targets. ----
        chainNodeAddrs.Sort();
        var addrToCursor = new Dictionary<int, int>();
        var nodes = new List<ChainNode>();
        // First pass: every chain node gets a cursor.
        foreach (int a in chainNodeAddrs)
        {
            addrToCursor[a] = nodes.Count;
            nodes.Add(new ChainNode(bodyAddrToClause[nodeBodyAddr[a]], NextCursor: -1));
        }
        // Second pass: wire NextCursor now that all node cursors exist.
        for (int i = 0; i < chainNodeAddrs.Count; i++)
        {
            int a = chainNodeAddrs[i];
            int nextAddr = nodeNextAddr[a];
            int next = nextAddr >= 0 && addrToCursor.TryGetValue(nextAddr, out int nc) ? nc : -1;
            nodes[addrToCursor[a]] = nodes[addrToCursor[a]] with { NextCursor = next };
        }

        // ---- 5. Entry-cursor map for every dispatch target. ----
        // A target that is a chain head maps to that head's node cursor. A
        // target that is a clause body (a deterministic, no-choice-point
        // entry) gets a fresh single-node cursor with no successor.
        var addrToEntryCursor = new Dictionary<int, int>(addrToCursor);
        foreach (int target in CollectDispatchTargets(code, start, end, switchTables))
        {
            if (addrToEntryCursor.ContainsKey(target)) continue;
            if (bodyAddrToClause.TryGetValue(target, out int clause))
            {
                addrToEntryCursor[target] = nodes.Count;
                nodes.Add(new ChainNode(clause, NextCursor: -1));
            }
            // A target that is itself a switch opcode is resolved through by
            // the runtime resolver (it never returns a switch address), so no
            // cursor is needed for it.
        }

        // Cursor budget: cursor 0 = resolve, 1..K = nodes, K+1.. = call-site
        // resumes. The resume-marker encoding caps a predicate at
        // Engine.ResumeMarkerCursorStride cursors.
        int callSites = CountCalls(code, start, end);
        if (nodes.Count + 1 + callSites >= Engine.ResumeMarkerCursorStride) return false;

        info = new IlIndexedDispatchInfo
        {
            Clauses = clauses,
            Nodes = nodes,
            AddrToEntryCursor = addrToEntryCursor,
            EntryAddress = start,
            SwitchTables = switchTables,
            Bytecode = code,
        };
        return true;
    }

    private static int CountCalls(byte[] code, int start, int end)
    {
        int n = 0, pc = start;
        while (pc < end)
        {
            var op = (Opcode)code[pc];
            // ADR-025 — each inline ITE takes one ELSE resume cursor; counted
            // via its `jump` (exactly one per ITE, never in dispatch).
            if (op == Opcode.Call || op == Opcode.Jump) n++;
            int size = OpcodeSize(code, pc);
            if (size <= 0) break;
            pc += size;
        }
        return n;
    }

    // ------------------------------------------------------------------
    // Per-engine model cache — keyed by functor id. The emitted IL bakes
    // the functor id and calls ResolveEntryByFunctorId; the cache lazily
    // builds the model from the engine's linked code + switch tables on
    // first call. Used by BOTH the runtime promotion path and the
    // persisted-bundle path — the latter is the whole point (a persisted
    // .dll loaded in a fresh process has no build-time model holder, but
    // the functor id is name-relative via chunk-197 patching and the
    // engine's linked code is available at first call).
    // ------------------------------------------------------------------
    // Chunk 233 — per-engine cache moved onto the Engine itself
    // (Engine.IlIndexedDispatchCache is a plain object slot). The
    // previous shape was a ConditionalWeakTable<Engine, ConcurrentDictionary> —
    // every IL Call to an indexed predicate paid the ConditionalWeakTable's
    // internal lock + the ConcurrentDictionary's bucket lock (visible
    // as Monitor.Enter_Slowpath in dotnet-trace). Engine is single-
    // threaded so a plain Dictionary suffices; the engine-typed slot
    // gives the cache engine lifetime without the weak-table.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static Dictionary<int, IndexGraph> CacheFor(Engine engine)
    {
        if (engine.IlIndexedDispatchCache is Dictionary<int, IndexGraph> typed)
            return typed;
        var fresh = new Dictionary<int, IndexGraph>();
        engine.IlIndexedDispatchCache = fresh;
        return fresh;
    }

    /// <summary>Called from emitted IL: resolves the entry cursor for the
    /// predicate identified by <paramref name="functorId"/>. The dispatch model
    /// is a WAM-independent <see cref="IndexGraph"/>: built lazily on first call
    /// from the engine's linked code (for a predicate whose WAM is present), or
    /// pre-registered from the bundle (a stripped predicate — see
    /// <see cref="RegisterModelForEngine"/>). Once cached, the walk reads no
    /// bytecode.</summary>
    public static int ResolveEntryByFunctorId(Engine engine, int functorId)
    {
        var cache = CacheFor(engine);
        if (!cache.TryGetValue(functorId, out var graph))
        {
            var info = BuildModelFromEngine(engine, functorId)
                ?? throw new InvalidOperationException(
                    $"Indexed-dispatch model build failed for functor id {functorId}.");
            graph = IlIndexGraph.Build(info)
                ?? throw new InvalidOperationException(
                    $"Indexed-dispatch graph build failed for functor id {functorId}.");
            cache[functorId] = graph;
        }
        return IlIndexGraph.Resolve(engine, graph);
    }

    /// <summary>Pre-registers a WAM-independent dispatch graph for a functor —
    /// used by the LoadBundle path for a predicate whose WAM body was stripped
    /// (--strip-wam): its switch model lives in the bundle as an
    /// <see cref="IndexGraph"/>, not in the linked code.</summary>
    internal static void RegisterModelForEngine(Engine engine, int functorId, IndexGraph graph)
    {
        var cache = CacheFor(engine);
        cache[functorId] = graph;
    }

    /// <summary>Public load-path entry point (the model types stay internal):
    /// decodes a persisted dispatch graph and registers it for the functor, so a
    /// stripped predicate dispatches without its WAM body. Keys are interned in
    /// the current process by the codec.</summary>
    public static void RegisterPersistedGraph(Engine engine, int functorId, byte[] graphBytes)
        => RegisterModelForEngine(engine, functorId, IndexGraphCodec.Decode(graphBytes));

    private static IlIndexedDispatchInfo? BuildModelFromEngine(Engine engine, int functorId)
    {
        var addrMap = engine.CurrentFunctorAddresses
            ?? throw new InvalidOperationException(
                "Indexed-dispatch: engine has no CurrentFunctorAddresses.");
        if (!addrMap.TryGetValue(functorId, out int start))
            throw new InvalidOperationException(
                $"Indexed-dispatch: functor id {functorId} not in CurrentFunctorAddresses.");
        byte[] code = engine.CurrentProgram
            ?? throw new InvalidOperationException(
                "Indexed-dispatch: engine has no CurrentProgram.");
        // End of this predicate's region = the next predicate's start
        // (predicates are laid out contiguously in linked code), or the
        // end of the program for the last one.
        int end = code.Length;
        foreach (int addr in addrMap.Values)
            if (addr > start && addr < end) end = addr;

        var tables = engine.SwitchTables ?? (IReadOnlyList<SwitchTable>)Array.Empty<SwitchTable>();
        TryDescribeBytes(code, start, end, expectedClauseCount: -1, tables,
            isBodyOpcodeEmittable: null, out var info);
        return info;
    }

    /// <summary>Every bytecode address a dispatch opcode in
    /// <c>[start, end)</c> can transfer control to: the four type labels
    /// of switch_on_term/arg, plus each typed table's entries and
    /// default.</summary>
    private static IEnumerable<int> CollectDispatchTargets(
        byte[] code, int start, int end, IReadOnlyList<SwitchTable> tables)
    {
        int pc = start;
        while (pc < end)
        {
            var op = (Opcode)code[pc];
            int size = OpcodeSize(code, pc);
            if (size <= 0) yield break;
            switch (op)
            {
                case Opcode.SwitchOnTerm:
                    yield return BytecodeIO.ReadInt32(code, pc + 1);
                    yield return BytecodeIO.ReadInt32(code, pc + 5);
                    yield return BytecodeIO.ReadInt32(code, pc + 9);
                    yield return BytecodeIO.ReadInt32(code, pc + 13);
                    break;
                case Opcode.SwitchOnArg:
                    yield return BytecodeIO.ReadInt32(code, pc + 5);
                    yield return BytecodeIO.ReadInt32(code, pc + 9);
                    yield return BytecodeIO.ReadInt32(code, pc + 13);
                    yield return BytecodeIO.ReadInt32(code, pc + 17);
                    break;
                case Opcode.SwitchOnAtom:
                case Opcode.SwitchOnInteger:
                case Opcode.SwitchOnStructure:
                {
                    int tableId = BytecodeIO.ReadInt32(code, pc + 1);
                    foreach (int t in TableTargets(tables, tableId)) yield return t;
                    break;
                }
                case Opcode.SwitchOnAtomArg:
                case Opcode.SwitchOnIntegerArg:
                case Opcode.SwitchOnStructureArg:
                {
                    int tableId = BytecodeIO.ReadInt32(code, pc + 5);
                    foreach (int t in TableTargets(tables, tableId)) yield return t;
                    break;
                }
                case Opcode.SwitchOnAtomSub:
                case Opcode.SwitchOnIntegerSub:
                case Opcode.SwitchOnStructureSub:
                {
                    int tableId = BytecodeIO.ReadInt32(code, pc + 13);
                    foreach (int t in TableTargets(tables, tableId)) yield return t;
                    break;
                }
            }
            pc += size;
        }
    }

    private static IEnumerable<int> TableTargets(IReadOnlyList<SwitchTable> tables, int tableId)
    {
        if (tableId < 0 || tableId >= tables.Count) yield break;
        var t = tables[tableId];
        yield return t.DefaultAddress;
        for (int i = 0; i < t.Values.Count; i++) yield return t.Values[i];
    }

    private static int OpcodeSize(byte[] code, int pc)
    {
        var info = OpcodeTable.Get(code[pc]);
        if (!info.IsDefined) return -1;
        // Meta's size is sub-opcode dependent; the dbg_info variant is 6.
        if ((Opcode)code[pc] == Opcode.Meta) return 6;
        return info.Size;
    }

    // ------------------------------------------------------------------
    // Runtime resolver — mirrors BytecodeInterpreter's switch opcodes and
    // returns the entry cursor for the current engine state.
    // ------------------------------------------------------------------

    /// <summary>Walks the dispatch opcodes from the predicate's entry,
    /// reproducing the interpreter's switch semantics exactly, and returns
    /// the cursor of the chain node (or deterministic clause) the call
    /// enters. Pure read-only over the engine's argument registers / heap.</summary>
    internal static int ResolveEntryCursor(Engine engine, IlIndexedDispatchInfo info)
    {
        byte[] code = info.Bytecode;
        var tables = info.SwitchTables;
        int pc = info.EntryAddress;
        while (true)
        {
            var op = (Opcode)code[pc];
            int target;
            switch (op)
            {
                case Opcode.SwitchOnTerm:
                    target = TermTarget(engine, code, pc, argIdx: 0, addrBase: 1);
                    break;
                case Opcode.SwitchOnArg:
                {
                    int argIdx = BytecodeIO.ReadInt32(code, pc + 1);
                    target = TermTarget(engine, code, pc, argIdx, addrBase: 5);
                    break;
                }
                case Opcode.SwitchOnAtom:
                    target = AtomTarget(engine, tables, BytecodeIO.ReadInt32(code, pc + 1), argIdx: 0);
                    break;
                case Opcode.SwitchOnInteger:
                    target = IntegerTarget(engine, tables, BytecodeIO.ReadInt32(code, pc + 1), argIdx: 0);
                    break;
                case Opcode.SwitchOnStructure:
                    target = StructureTarget(engine, tables, BytecodeIO.ReadInt32(code, pc + 1), argIdx: 0);
                    break;
                case Opcode.SwitchOnAtomArg:
                    target = AtomTarget(engine, tables, BytecodeIO.ReadInt32(code, pc + 5),
                        BytecodeIO.ReadInt32(code, pc + 1));
                    break;
                case Opcode.SwitchOnIntegerArg:
                    target = IntegerTarget(engine, tables, BytecodeIO.ReadInt32(code, pc + 5),
                        BytecodeIO.ReadInt32(code, pc + 1));
                    break;
                case Opcode.SwitchOnStructureArg:
                    target = StructureTarget(engine, tables, BytecodeIO.ReadInt32(code, pc + 5),
                        BytecodeIO.ReadInt32(code, pc + 1));
                    break;
                case Opcode.SwitchOnAtomSub:
                    target = AtomSubTarget(engine, tables, BytecodeIO.ReadInt32(code, pc + 13),
                        BytecodeIO.ReadInt32(code, pc + 1), BytecodeIO.ReadInt32(code, pc + 5),
                        BytecodeIO.ReadInt32(code, pc + 9));
                    break;
                case Opcode.SwitchOnIntegerSub:
                    target = IntegerSubTarget(engine, tables, BytecodeIO.ReadInt32(code, pc + 13),
                        BytecodeIO.ReadInt32(code, pc + 1), BytecodeIO.ReadInt32(code, pc + 5),
                        BytecodeIO.ReadInt32(code, pc + 9));
                    break;
                case Opcode.SwitchOnStructureSub:
                    target = StructureSubTarget(engine, tables, BytecodeIO.ReadInt32(code, pc + 13),
                        BytecodeIO.ReadInt32(code, pc + 1), BytecodeIO.ReadInt32(code, pc + 5),
                        BytecodeIO.ReadInt32(code, pc + 9));
                    break;
                default:
                    // Not a switch: a chain head (try) or a clause body.
                    return info.AddrToEntryCursor[pc];
            }
            pc = target;
        }
    }

    private static Cell DerefArg(Engine engine, int argIdx)
    {
        Cell c = engine.GetRegister(argIdx);
        return c.Tag == Tag.Ref ? engine.GetHeap(engine.Deref(c.AsHeapIndex)) : c;
    }

    private static int TermTarget(Engine engine, byte[] code, int pc, int argIdx, int addrBase)
    {
        int varAddr    = BytecodeIO.ReadInt32(code, pc + addrBase);
        int constAddr  = BytecodeIO.ReadInt32(code, pc + addrBase + 4);
        int listAddr   = BytecodeIO.ReadInt32(code, pc + addrBase + 8);
        int structAddr = BytecodeIO.ReadInt32(code, pc + addrBase + 12);
        Cell a = DerefArg(engine, argIdx);
        return a.Tag switch
        {
            Tag.Ref => varAddr,
            Tag.Atom or Tag.Int or Tag.Float => constAddr,
            Tag.Lis => listAddr,
            Tag.Str => structAddr,
            _ => varAddr,
        };
    }

    private static int AtomTarget(Engine engine, IReadOnlyList<SwitchTable> tables, int tableId, int argIdx)
    {
        var table = tables[tableId];
        Cell a = DerefArg(engine, argIdx);
        return a.Tag == Tag.Atom ? table.Lookup(a.AsAtomId) : table.DefaultAddress;
    }

    private static int IntegerTarget(Engine engine, IReadOnlyList<SwitchTable> tables, int tableId, int argIdx)
    {
        var table = tables[tableId];
        Cell a = DerefArg(engine, argIdx);
        if (a.Tag != Tag.Int) return table.DefaultAddress;
        long v = a.AsInt;
        return v >= int.MinValue && v <= int.MaxValue ? table.Lookup((int)v) : table.DefaultAddress;
    }

    private static int StructureTarget(Engine engine, IReadOnlyList<SwitchTable> tables, int tableId, int argIdx)
    {
        var table = tables[tableId];
        Cell a = DerefArg(engine, argIdx);
        if (a.Tag != Tag.Str) return table.DefaultAddress;
        int functorId = engine.GetHeap(a.AsHeapIndex).AsFunctorId;
        return table.Lookup(functorId);
    }

    // ADR-027 — second-level (sub-argument) targets. Mirror the interpreter's
    // SwitchOnAtomSub / SwitchOnIntegerSub: walk a bounded path from X[argIdx],
    // then key the table on the atom / integer reached (default on a miss).
    private static int AtomSubTarget(Engine engine, IReadOnlyList<SwitchTable> tables,
        int tableId, int argIdx, int sub0, int sub1)
    {
        var table = tables[tableId];
        return TrySubCell(engine, DerefArg(engine, argIdx), sub0, sub1, out Cell sub) && sub.Tag == Tag.Atom
            ? table.Lookup(sub.AsAtomId)
            : table.DefaultAddress;
    }

    private static int IntegerSubTarget(Engine engine, IReadOnlyList<SwitchTable> tables,
        int tableId, int argIdx, int sub0, int sub1)
    {
        var table = tables[tableId];
        if (TrySubCell(engine, DerefArg(engine, argIdx), sub0, sub1, out Cell sub) && sub.Tag == Tag.Int)
        {
            long v = sub.AsInt;
            if (v >= int.MinValue && v <= int.MaxValue) return table.Lookup((int)v);
        }
        return table.DefaultAddress;
    }

    // ADR-028 — structure-keyed sub target: key the table on the FUNCTOR of the
    // sub-terminal (a nested list keys as the cons functor), default on a miss.
    private static int StructureSubTarget(Engine engine, IReadOnlyList<SwitchTable> tables,
        int tableId, int argIdx, int sub0, int sub1)
    {
        var table = tables[tableId];
        if (TrySubCell(engine, DerefArg(engine, argIdx), sub0, sub1, out Cell sub))
        {
            if (sub.Tag == Tag.Str)
                return table.Lookup(engine.GetHeap(sub.AsHeapIndex).AsFunctorId);
            if (sub.Tag == Tag.Lis)
                return table.Lookup(AtomTable.ConsFunctorId);
        }
        return table.DefaultAddress;
    }

    /// <summary>Walks a bounded sub-argument path (<paramref name="sub0"/>, then
    /// <paramref name="sub1"/> if &gt;= 0) from a deref'd <paramref name="cell"/>;
    /// returns the deref'd terminal, or false on a non-compound / out-of-range
    /// hop. Mirrors <c>BytecodeInterpreter.TrySubCell</c>.</summary>
    private static bool TrySubCell(Engine engine, Cell cell, int sub0, int sub1, out Cell result)
    {
        if (!TryHop(engine, cell, sub0, out result)) return false;
        if (sub1 >= 0 && !TryHop(engine, result, sub1, out result)) return false;
        return true;
    }

    /// <summary>Inline-resolver helper (ADR-027): walks the bounded sub-path
    /// (<paramref name="sub0"/>, then <paramref name="sub1"/> if &gt;= 0) from
    /// <paramref name="cell"/> and returns the deref'd terminal, or a REF sentinel
    /// on a miss (a non-compound / out-of-range hop). The emitted inline resolver's
    /// subsequent <c>tag == Atom/Int</c> test then routes a miss to the table
    /// default — exactly the runtime walk's semantics. Public because the emitted
    /// IL (loaded via <c>Assembly.Load</c> in a fresh process for a persisted
    /// bundle) calls it directly.</summary>
    public static Cell WalkSubOrMiss(Engine engine, Cell cell, int sub0, int sub1)
        => TrySubCell(engine, cell, sub0, sub1, out Cell r) ? r : Cell.Ref(0);

    private static bool TryHop(Engine engine, Cell cell, int idx, out Cell next)
    {
        next = default;
        if (cell.Tag == Tag.Lis)
        {
            if ((uint)idx > 1u) return false;
            next = Deref(engine, engine.GetHeap(cell.AsHeapIndex + idx));
            return true;
        }
        if (cell.Tag == Tag.Str)
        {
            int structIdx = cell.AsHeapIndex;
            int arity = FunctorTable.Lookup(engine.GetHeap(structIdx).AsFunctorId).Arity;
            if ((uint)idx >= (uint)arity) return false;
            next = Deref(engine, engine.GetHeap(structIdx + 1 + idx));
            return true;
        }
        return false;
    }

    private static Cell Deref(Engine engine, Cell c) =>
        c.Tag == Tag.Ref ? engine.GetHeap(engine.Deref(c.AsHeapIndex)) : c;
}
