using Shumway.Core;

namespace Shumway.Compiler.Wam;

/// <summary>
/// The transient atoms a compiled predicate's code names (ADR-003): its atom
/// operands, the name atoms of its functor operands, and the keys of its atom- and
/// functor-keyed switch tables. Images name theirs through the pool entry that
/// holds them. An engine claims these while the code can run.
/// </summary>
public sealed class CodeAtoms
{
    /// <summary>The ids, ascending: what a claimant retains and drops.</summary>
    public int[] Ids { get; }

    private CodeAtoms(int[] ids) => Ids = ids;

    public static readonly CodeAtoms None = new(System.Array.Empty<int>());

    /// <summary>The atoms <paramref name="pred"/> names, computed once.</summary>
    public static CodeAtoms Of(CompiledPredicate pred)
        => Volatile.Read(ref pred.AtomsMemo)
           ?? (pred.AtomsMemo = Compute(pred.Bytecode, pred.SwitchTables));

    /// <summary>The atoms one clause's code names, computed once.</summary>
    public static CodeAtoms Of(CompiledClause clause)
        => Volatile.Read(ref clause.AtomsMemo)
           ?? (clause.AtomsMemo = Compute(clause.Bytecode, System.Array.Empty<SwitchTable>()));

    /// <summary>The transient atoms <paramref name="length"/> bytes of linked
    /// code from <paramref name="start"/> name, ascending: a clause's chunk, which
    /// holds no switch table. A byte that is no opcode (a breakpoint patched over
    /// one) ends the walk.</summary>
    public static int[] OfRange(byte[] code, int start, int length)
    {
        var found = new Found(stackalloc int[Found.Inline]);
        int end = start + length;
        for (int pc = start; pc < end;)
        {
            var info = OpcodeTable.Get(code[pc]);
            if (!info.IsDefined || info.Size == 0) break;
            if (info.OperandKinds is { } kinds)
            {
                int at = pc + 1;
                foreach (var kind in kinds)
                {
                    if (kind == OperandKind.Atom) found.Add(BytecodeIO.ReadInt32(code, at));
                    else if (kind == OperandKind.Functor) AddFunctor(ref found, BytecodeIO.ReadInt32(code, at));
                    at += kind == OperandKind.LongValue ? 8 : 4;
                }
            }
            pc += info.Size;
        }
        return found.ToArray();
    }

    private static CodeAtoms Compute(byte[] code, IReadOnlyList<SwitchTable> tables)
    {
        var found = new Found(stackalloc int[Found.Inline]);
        for (int pc = 0; pc < code.Length;)
        {
            var info = OpcodeTable.Get(code[pc]);
            if (!info.IsDefined || info.Size == 0)
                throw new InvalidOperationException($"atom walk: undefined opcode 0x{code[pc]:X2} at {pc}.");
            if (info.OperandKinds is { } kinds)
            {
                int at = pc + 1;
                foreach (var kind in kinds)
                {
                    switch (kind)
                    {
                        case OperandKind.Atom:
                            found.Add(BytecodeIO.ReadInt32(code, at));
                            break;
                        case OperandKind.Functor:
                            AddFunctor(ref found, BytecodeIO.ReadInt32(code, at));
                            break;
                        case OperandKind.TableId:
                            AddTable(ref found, (Opcode)code[pc], tables, BytecodeIO.ReadInt32(code, at));
                            break;
                    }
                    at += kind == OperandKind.LongValue ? 8 : 4;
                }
            }
            pc += info.Size;
        }
        int[] ids = found.ToArray();
        return ids.Length == 0 ? None : new CodeAtoms(ids);
    }

    private static void AddFunctor(ref Found found, int functorId)
    {
        if (FunctorTable.TryLookup(functorId, out var entry)) found.Add(entry.AtomId);
    }

    // The table's kind is the opcode's: atom keys, functor keys, or integers.
    private static void AddTable(ref Found found, Opcode op, IReadOnlyList<SwitchTable> tables, int tableId)
    {
        if ((uint)tableId >= (uint)tables.Count) return;
        var keys = tables[tableId].Keys;
        switch (op)
        {
            case Opcode.SwitchOnAtom:
            case Opcode.SwitchOnAtomArg:
            case Opcode.SwitchOnAtomSub:
                foreach (int k in keys) found.Add(k);
                break;
            case Opcode.SwitchOnStructure:
            case Opcode.SwitchOnStructureArg:
            case Opcode.SwitchOnStructureSub:
                foreach (int k in keys) AddFunctor(ref found, k);
                break;
        }
    }

    // The transient ids a walk meets, kept ascending and distinct. Every assert
    // and every chunk of a rebuilt dynamic region walks its code, and most code
    // names none or a few: only the result is allocated, unless more than
    // Inline turn up.
    private ref struct Found
    {
        public const int Inline = 32;

        private readonly Span<int> _inline;
        private int _count;
        private List<int>? _spill;

        public Found(Span<int> inline)
        {
            _inline = inline;
            _count = 0;
            _spill = null;
        }

        public void Add(int id)
        {
            if (!AtomTable.IsTransient(id)) return;
            if (_spill is not null)
            {
                _spill.Add(id);
                return;
            }
            int at = _count;
            while (at > 0 && _inline[at - 1] > id) at--;
            if (at > 0 && _inline[at - 1] == id) return;
            if (_count == _inline.Length)
            {
                _spill = new List<int>(_count * 2);
                for (int i = 0; i < _count; i++) _spill.Add(_inline[i]);
                _spill.Add(id);
                return;
            }
            for (int i = _count; i > at; i--) _inline[i] = _inline[i - 1];
            _inline[at] = id;
            _count++;
        }

        public readonly int[] ToArray()
        {
            if (_spill is null)
                return _count == 0 ? System.Array.Empty<int>() : _inline.Slice(0, _count).ToArray();
            _spill.Sort();
            int n = 0;
            for (int i = 0; i < _spill.Count; i++)
                if (n == 0 || _spill[i] != _spill[n - 1]) _spill[n++] = _spill[i];
            _spill.RemoveRange(n, _spill.Count - n);
            return _spill.ToArray();
        }
    }
}
