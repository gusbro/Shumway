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

    /// <summary>The predicate bytecode (the resolver walks the dispatch
    /// opcodes here).</summary>
    public required byte[] Bytecode { get; init; }
}

internal readonly record struct ChainNode(int ClauseIndex, int NextCursor);

/// <summary>Recogniser + runtime resolver for <see cref="IlIndexedDispatchInfo"/>.</summary>
internal static class IlIndexedDispatch
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
        or Opcode.SwitchOnAtomArg or Opcode.SwitchOnIntegerArg or Opcode.SwitchOnStructureArg;

    /// <summary>Tries to build the indexed-dispatch model for
    /// <paramref name="predicate"/>. Succeeds only for the WAM indexed
    /// shape (bytecode opens with switch_on_term or switch_on_arg, has the
    /// full try/retry/trust chain machinery, and every clause body fits the
    /// IL subset). Returns false (and a null model) otherwise.</summary>
    public static bool TryDescribe(
        Shumway.Compiler.Wam.CompiledPredicate predicate,
        System.Func<Opcode, int, bool> isBodyOpcodeEmittable,
        out IlIndexedDispatchInfo? info)
    {
        info = null;
        byte[] code = predicate.Bytecode;
        if (code.Length == 0) return false;
        var first = (Opcode)code[0];
        if (first != Opcode.SwitchOnTerm && first != Opcode.SwitchOnArg) return false;

        // ---- 1. Collect every try/retry/trust chain. ----
        // Chains are contiguous runs: try (retry)* trust. Each node carries
        // the clause-body address it dispatches to.
        var chainNodeAddrs = new List<int>();          // addresses of all try/retry/trust nodes
        var nodeBodyAddr = new Dictionary<int, int>();  // node addr -> body addr
        var nodeNextAddr = new Dictionary<int, int>();  // node addr -> next node addr (-1 if tail)
        var chainHeadAddrs = new HashSet<int>();        // addresses that start a chain (a `try`)

        int pc = 0;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            int size = OpcodeSize(code, pc);
            if (size <= 0) return false;
            if (op == Opcode.Try)
            {
                chainHeadAddrs.Add(pc);
                int body = BytecodeIO.ReadInt32(code, pc + 1);
                chainNodeAddrs.Add(pc);
                nodeBodyAddr[pc] = body;
                var next = (Opcode)code[pc + size];
                nodeNextAddr[pc] = (pc + size < code.Length && (next == Opcode.Retry || next == Opcode.Trust))
                    ? pc + size : -1;
            }
            else if (op == Opcode.Retry)
            {
                int body = BytecodeIO.ReadInt32(code, pc + 1);
                chainNodeAddrs.Add(pc);
                nodeBodyAddr[pc] = body;
                var next = (Opcode)code[pc + size];
                nodeNextAddr[pc] = (pc + size < code.Length && (next == Opcode.Retry || next == Opcode.Trust))
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
        // body. There must be exactly ClauseCount of them; sorted ascending
        // gives source order (the WAM lays bodies out in source order).
        var bodyAddrs = new SortedSet<int>(nodeBodyAddr.Values);
        if (bodyAddrs.Count != predicate.ClauseCount) return false;
        var bodyAddrToClause = new Dictionary<int, int>();
        int ci = 0;
        var clauseStarts = new List<int>(bodyAddrs.Count);
        foreach (int a in bodyAddrs) { bodyAddrToClause[a] = ci++; clauseStarts.Add(a); }

        // Clause ranges: [start_i, start_{i+1}) and the last to code.Length.
        var clauses = new List<(int, int)>(clauseStarts.Count);
        for (int i = 0; i < clauseStarts.Count; i++)
        {
            int s = clauseStarts[i];
            int e = i + 1 < clauseStarts.Count ? clauseStarts[i + 1] : code.Length;
            clauses.Add((s, e));
        }

        // ---- 3. Validate every clause body fits the IL subset. ----
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
        foreach (int target in CollectDispatchTargets(code, predicate.SwitchTables))
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
        int callSites = CountCalls(code);
        if (nodes.Count + 1 + callSites >= Engine.ResumeMarkerCursorStride) return false;

        info = new IlIndexedDispatchInfo
        {
            Clauses = clauses,
            Nodes = nodes,
            AddrToEntryCursor = addrToEntryCursor,
            SwitchTables = predicate.SwitchTables,
            Bytecode = code,
        };
        return true;
    }

    private static int CountCalls(byte[] code)
    {
        int n = 0, pc = 0;
        while (pc < code.Length)
        {
            if ((Opcode)code[pc] == Opcode.Call) n++;
            int size = OpcodeSize(code, pc);
            if (size <= 0) break;
            pc += size;
        }
        return n;
    }

    // ------------------------------------------------------------------
    // Runtime model holder — the emitted IL bakes an integer key and calls
    // ResolveEntryByKey to compute the entry cursor for the current call.
    // ------------------------------------------------------------------
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, IlIndexedDispatchInfo> _models = new();
    private static int _nextModelKey;

    public static int RegisterModel(IlIndexedDispatchInfo info)
    {
        int key = System.Threading.Interlocked.Increment(ref _nextModelKey);
        _models[key] = info;
        return key;
    }

    /// <summary>Called from emitted IL: resolves the entry cursor (chain
    /// node index) for the model registered under <paramref name="key"/>,
    /// given the engine's current argument registers.</summary>
    public static int ResolveEntryByKey(Engine engine, int key)
        => ResolveEntryCursor(engine, _models[key]);

    /// <summary>Every bytecode address a dispatch opcode can transfer
    /// control to: the four type labels of switch_on_term/arg, plus each
    /// typed table's entries and default.</summary>
    private static IEnumerable<int> CollectDispatchTargets(
        byte[] code, IReadOnlyList<SwitchTable> tables)
    {
        int pc = 0;
        while (pc < code.Length)
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
    public static int ResolveEntryCursor(Engine engine, IlIndexedDispatchInfo info)
    {
        byte[] code = info.Bytecode;
        var tables = info.SwitchTables;
        int pc = 0;
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
}
