using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>
/// A WAM-independent first-/multi-argument indexed-dispatch model: the switch
/// cascade re-encoded as a small node graph. Each node is one switch
/// (<c>switch_on_term</c> / <c>switch_on_arg</c> → a <see cref="IndexNodeKind.Term"/>
/// node; <c>switch_on_atom</c>/<c>integer</c>/<c>structure</c>[<c>_arg</c>] → an
/// Atom / Int / Struct node). A target is either a terminal cursor or another
/// node. <see cref="Resolve"/> walks the graph exactly like
/// <c>IlIndexedDispatch.ResolveEntryCursor</c> walks the bytecode — but needs no
/// WAM, so an indexed predicate's WAM body can be stripped from the bundle
/// (the graph is persisted instead). Handles single- and multi-argument
/// indexing uniformly (a multi-arg switch is just a Term node with a non-zero
/// <see cref="IndexNode.ArgIdx"/>).
/// </summary>
internal sealed class IndexGraph
{
    /// <summary>Switch nodes; <c>Nodes[0]</c> is the root (the predicate entry).</summary>
    public required IndexNode[] Nodes { get; init; }
}

internal enum IndexNodeKind : byte { Term, Atom, Int, Struct }

/// <summary>A branch target: another node (<see cref="IsNode"/>) or a terminal
/// entry cursor.</summary>
internal readonly record struct IndexTarget(bool IsNode, int Value)
{
    public static IndexTarget Node(int index) => new(true, index);
    public static IndexTarget Cursor(int cursor) => new(false, cursor);
}

internal sealed class IndexNode
{
    public required IndexNodeKind Kind { get; init; }

    /// <summary>The argument register this switch tests (0 for first-arg).</summary>
    public required int ArgIdx { get; init; }

    // ADR-027 second-level indexing (Atom / Int nodes only). Sub0 = -1 means a
    // plain argument lookup; Sub0 >= 0 walks a bounded path (Sub0, then Sub1 if
    // >= 0) into the argument before keying the table.
    public int Sub0 { get; init; } = -1;
    public int Sub1 { get; init; } = -1;

    // Term node — one target per dereferenced argument tag.
    public IndexTarget VarTarget { get; init; }
    public IndexTarget ConstTarget { get; init; }
    public IndexTarget ListTarget { get; init; }
    public IndexTarget StructTarget { get; init; }

    // Atom / Int / Struct node — a keyed table + a default.
    /// <summary>Keys: atom ids (Atom), integer values (Int) or functor ids (Struct),
    /// parallel to <see cref="Targets"/>.</summary>
    public int[]? Keys { get; init; }
    public IndexTarget[]? Targets { get; init; }
    public IndexTarget DefaultTarget { get; init; }
}

/// <summary>Build (from the bytecode model) and runtime walk of an
/// <see cref="IndexGraph"/>.</summary>
internal static class IlIndexGraph
{
    // Switch opcode sizes (mirror OpcodeInfo).
    private const int SwitchOnTermSize = 17;
    private const int SwitchOnArgSize = 21;

    private static bool IsSwitch(Opcode op) => op is
        Opcode.SwitchOnTerm or Opcode.SwitchOnArg or
        Opcode.SwitchOnAtom or Opcode.SwitchOnInteger or Opcode.SwitchOnStructure or
        Opcode.SwitchOnAtomArg or Opcode.SwitchOnIntegerArg or Opcode.SwitchOnStructureArg or
        Opcode.SwitchOnAtomSub or Opcode.SwitchOnIntegerSub or Opcode.SwitchOnStructureSub;

    /// <summary>Converts the bytecode-walking <see cref="IlIndexedDispatchInfo"/>
    /// into a WAM-independent <see cref="IndexGraph"/>. Returns null if the entry
    /// is not a switch (a degenerate / non-indexed shape) — the caller then keeps
    /// the bytecode path.</summary>
    public static IndexGraph? Build(IlIndexedDispatchInfo info)
    {
        byte[] code = info.Bytecode;
        var tables = info.SwitchTables;
        if (info.EntryAddress >= code.Length || !IsSwitch((Opcode)code[info.EntryAddress]))
            return null;

        // 1. Discover every switch address reachable from the entry, in DFS
        //    order — that order IS each node's index.
        var addrToIndex = new Dictionary<int, int>();
        var order = new List<int>();
        var stack = new Stack<int>();
        stack.Push(info.EntryAddress);
        addrToIndex[info.EntryAddress] = 0;
        order.Add(info.EntryAddress);
        while (stack.Count > 0)
        {
            int pc = stack.Pop();
            foreach (int t in SwitchTargets(code, tables, pc))
            {
                if (t >= code.Length || !IsSwitch((Opcode)code[t])) continue;
                if (addrToIndex.ContainsKey(t)) continue;
                addrToIndex[t] = order.Count;
                order.Add(t);
                stack.Push(t);
            }
        }

        // 2. Build a node per switch address.
        IndexTarget ToTarget(int addr) =>
            addrToIndex.TryGetValue(addr, out int idx)
                ? IndexTarget.Node(idx)
                : IndexTarget.Cursor(info.AddrToEntryCursor[addr]);

        var nodes = new IndexNode[order.Count];
        for (int i = 0; i < order.Count; i++)
            nodes[i] = BuildNode(code, tables, order[i], ToTarget);
        return new IndexGraph { Nodes = nodes };
    }

    /// <summary>The bytecode addresses a switch at <paramref name="pc"/> can
    /// branch to.</summary>
    private static IEnumerable<int> SwitchTargets(
        byte[] code, IReadOnlyList<SwitchTable> tables, int pc)
    {
        switch ((Opcode)code[pc])
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
                var table = tables[BytecodeIO.ReadInt32(code, pc + 1)];
                foreach (int v in table.Values) yield return v;
                yield return table.DefaultAddress;
                break;
            }
            case Opcode.SwitchOnAtomArg:
            case Opcode.SwitchOnIntegerArg:
            case Opcode.SwitchOnStructureArg:
            {
                var table = tables[BytecodeIO.ReadInt32(code, pc + 5)];
                foreach (int v in table.Values) yield return v;
                yield return table.DefaultAddress;
                break;
            }
            case Opcode.SwitchOnAtomSub:
            case Opcode.SwitchOnIntegerSub:
            case Opcode.SwitchOnStructureSub:
            {
                var table = tables[BytecodeIO.ReadInt32(code, pc + 13)];
                foreach (int v in table.Values) yield return v;
                yield return table.DefaultAddress;
                break;
            }
        }
    }

    private static IndexNode BuildNode(
        byte[] code, IReadOnlyList<SwitchTable> tables, int pc, Func<int, IndexTarget> toTarget)
    {
        switch ((Opcode)code[pc])
        {
            case Opcode.SwitchOnTerm:
                return new IndexNode
                {
                    Kind = IndexNodeKind.Term, ArgIdx = 0,
                    VarTarget = toTarget(BytecodeIO.ReadInt32(code, pc + 1)),
                    ConstTarget = toTarget(BytecodeIO.ReadInt32(code, pc + 5)),
                    ListTarget = toTarget(BytecodeIO.ReadInt32(code, pc + 9)),
                    StructTarget = toTarget(BytecodeIO.ReadInt32(code, pc + 13)),
                };
            case Opcode.SwitchOnArg:
                return new IndexNode
                {
                    Kind = IndexNodeKind.Term, ArgIdx = BytecodeIO.ReadInt32(code, pc + 1),
                    VarTarget = toTarget(BytecodeIO.ReadInt32(code, pc + 5)),
                    ConstTarget = toTarget(BytecodeIO.ReadInt32(code, pc + 9)),
                    ListTarget = toTarget(BytecodeIO.ReadInt32(code, pc + 13)),
                    StructTarget = toTarget(BytecodeIO.ReadInt32(code, pc + 17)),
                };
            case Opcode.SwitchOnAtom:
                return KeyedNode(IndexNodeKind.Atom, 0, tables[BytecodeIO.ReadInt32(code, pc + 1)], toTarget);
            case Opcode.SwitchOnInteger:
                return KeyedNode(IndexNodeKind.Int, 0, tables[BytecodeIO.ReadInt32(code, pc + 1)], toTarget);
            case Opcode.SwitchOnStructure:
                return KeyedNode(IndexNodeKind.Struct, 0, tables[BytecodeIO.ReadInt32(code, pc + 1)], toTarget);
            case Opcode.SwitchOnAtomArg:
                return KeyedNode(IndexNodeKind.Atom, BytecodeIO.ReadInt32(code, pc + 1),
                    tables[BytecodeIO.ReadInt32(code, pc + 5)], toTarget);
            case Opcode.SwitchOnIntegerArg:
                return KeyedNode(IndexNodeKind.Int, BytecodeIO.ReadInt32(code, pc + 1),
                    tables[BytecodeIO.ReadInt32(code, pc + 5)], toTarget);
            case Opcode.SwitchOnStructureArg:
                return KeyedNode(IndexNodeKind.Struct, BytecodeIO.ReadInt32(code, pc + 1),
                    tables[BytecodeIO.ReadInt32(code, pc + 5)], toTarget);
            case Opcode.SwitchOnAtomSub:
                return KeyedNode(IndexNodeKind.Atom, BytecodeIO.ReadInt32(code, pc + 1),
                    tables[BytecodeIO.ReadInt32(code, pc + 13)], toTarget,
                    sub0: BytecodeIO.ReadInt32(code, pc + 5), sub1: BytecodeIO.ReadInt32(code, pc + 9));
            case Opcode.SwitchOnIntegerSub:
                return KeyedNode(IndexNodeKind.Int, BytecodeIO.ReadInt32(code, pc + 1),
                    tables[BytecodeIO.ReadInt32(code, pc + 13)], toTarget,
                    sub0: BytecodeIO.ReadInt32(code, pc + 5), sub1: BytecodeIO.ReadInt32(code, pc + 9));
            case Opcode.SwitchOnStructureSub:
                return KeyedNode(IndexNodeKind.Struct, BytecodeIO.ReadInt32(code, pc + 1),
                    tables[BytecodeIO.ReadInt32(code, pc + 13)], toTarget,
                    sub0: BytecodeIO.ReadInt32(code, pc + 5), sub1: BytecodeIO.ReadInt32(code, pc + 9));
            default:
                throw new System.InvalidOperationException(
                    $"IlIndexGraph: address {pc} is not a switch opcode.");
        }
    }

    private static IndexNode KeyedNode(
        IndexNodeKind kind, int argIdx, SwitchTable table, Func<int, IndexTarget> toTarget,
        int sub0 = -1, int sub1 = -1)
    {
        var keys = new int[table.Count];
        var targets = new IndexTarget[table.Count];
        for (int i = 0; i < table.Count; i++)
        {
            keys[i] = table.Keys[i];
            targets[i] = toTarget(table.Values[i]);
        }
        return new IndexNode
        {
            Kind = kind, ArgIdx = argIdx,
            Keys = keys, Targets = targets,
            DefaultTarget = toTarget(table.DefaultAddress),
            Sub0 = sub0, Sub1 = sub1,
        };
    }

    /// <summary>Walks the graph for the engine's current registers and returns
    /// the entry cursor — the WAM-free counterpart of
    /// <c>IlIndexedDispatch.ResolveEntryCursor</c>.</summary>
    public static int Resolve(Activation engine, IndexGraph graph)
    {
        int idx = 0;
        while (true)
        {
            var node = graph.Nodes[idx];
            IndexTarget t = TargetFor(engine, node);
            if (!t.IsNode) return t.Value;
            idx = t.Value;
        }
    }

    private static IndexTarget TargetFor(Activation engine, IndexNode node)
    {
        Cell a = DerefArg(engine, node.ArgIdx);
        switch (node.Kind)
        {
            case IndexNodeKind.Term:
                return a.Tag switch
                {
                    Tag.Ref => node.VarTarget,
                    Tag.Lis => node.ListTarget,
                    Tag.Str => node.StructTarget,
                    Tag.Atom or Tag.Int or Tag.Float => node.ConstTarget,
                    _ => node.VarTarget,
                };
            case IndexNodeKind.Atom:
            {
                // ADR-027: a sub-node walks a path into the argument first.
                if (node.Sub0 >= 0)
                    return TrySubCell(engine, a, node.Sub0, node.Sub1, out Cell s) && s.Tag == Tag.Atom
                        ? Lookup(node, s.AsAtomId) : node.DefaultTarget;
                return a.Tag == Tag.Atom ? Lookup(node, a.AsAtomId) : node.DefaultTarget;
            }
            case IndexNodeKind.Int:
            {
                if (node.Sub0 >= 0)
                {
                    if (!TrySubCell(engine, a, node.Sub0, node.Sub1, out Cell s) || s.Tag != Tag.Int)
                        return node.DefaultTarget;
                    long sv = s.AsInt;
                    return sv >= int.MinValue && sv <= int.MaxValue
                        ? Lookup(node, (int)sv) : node.DefaultTarget;
                }
                if (a.Tag != Tag.Int) return node.DefaultTarget;
                long v = a.AsInt;
                return v >= int.MinValue && v <= int.MaxValue
                    ? Lookup(node, (int)v) : node.DefaultTarget;
            }
            case IndexNodeKind.Struct:
            {
                // ADR-028: a structure-sub node walks a path first, then keys on
                // the functor of the terminal (a nested list keys as cons).
                if (node.Sub0 >= 0)
                {
                    if (!TrySubCell(engine, a, node.Sub0, node.Sub1, out Cell s))
                        return node.DefaultTarget;
                    if (s.Tag == Tag.Str) return Lookup(node, engine.GetHeap(s.AsHeapIndex).AsFunctorId);
                    if (s.Tag == Tag.Lis) return Lookup(node, AtomTable.ConsFunctorId);
                    return node.DefaultTarget;
                }
                return a.Tag == Tag.Str
                    ? Lookup(node, engine.GetHeap(a.AsHeapIndex).AsFunctorId)
                    : node.DefaultTarget;
            }
            default:
                return node.DefaultTarget;
        }
    }

    private static IndexTarget Lookup(IndexNode node, int key)
    {
        int[] keys = node.Keys!;
        for (int i = 0; i < keys.Length; i++)
            if (keys[i] == key) return node.Targets![i];
        return node.DefaultTarget;
    }

    private static Cell DerefArg(Activation engine, int argIdx)
    {
        Cell c = engine.GetRegister(argIdx);
        return c.Tag == Tag.Ref ? engine.GetHeap(engine.Deref(c.AsHeapIndex)) : c;
    }

    // ADR-027 — bounded sub-argument path walk (mirrors
    // BytecodeInterpreter.TrySubCell / IlIndexedDispatch.TrySubCell).
    private static bool TrySubCell(Activation engine, Cell cell, int sub0, int sub1, out Cell result)
    {
        if (!TryHop(engine, cell, sub0, out result)) return false;
        if (sub1 >= 0 && !TryHop(engine, result, sub1, out result)) return false;
        return true;
    }

    private static bool TryHop(Activation engine, Cell cell, int idx, out Cell next)
    {
        next = default;
        if (cell.Tag == Tag.Lis)
        {
            if ((uint)idx > 1u) return false;
            next = DerefCell(engine, engine.GetHeap(cell.AsHeapIndex + idx));
            return true;
        }
        if (cell.Tag == Tag.Str)
        {
            int structIdx = cell.AsHeapIndex;
            int arity = FunctorTable.Lookup(engine.GetHeap(structIdx).AsFunctorId).Arity;
            if ((uint)idx >= (uint)arity) return false;
            next = DerefCell(engine, engine.GetHeap(structIdx + 1 + idx));
            return true;
        }
        return false;
    }

    private static Cell DerefCell(Activation engine, Cell c) =>
        c.Tag == Tag.Ref ? engine.GetHeap(engine.Deref(c.AsHeapIndex)) : c;
}
