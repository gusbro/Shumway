using Shumway.Core;

namespace Shumway.Compiler.Wam;

/// <summary>ADR-063: the code of a fact asserted at run time, from the image of
/// its head and without the clause compiler: the head's matching stream, as
/// the clause compiler emits one, read off the cells. An atom, an integer the
/// operand holds, a float or closed packed text matches by its instruction, a
/// variable by its register, a compound by <c>get_structure</c> or
/// <c>get_list</c> and the stream of its arguments, and a nested compound or
/// boxed literal through a temporary the stream takes it from
/// (<c>unify_variable_x</c>), after its parent's stream. The literals go to
/// their pools as a source clause's do. A head the stream cannot express (an
/// integer past the operand, packed text that does not end in <c>[]</c>) is
/// copied whole from the image, interned in the pool, and unified argument by
/// argument (<c>put_blit</c>, <c>get_structure</c> in read mode,
/// <c>unify_value_x</c>).</summary>
public static class FactImageCompiler
{
    public static CompiledClause Compile(TermImage head, int functorId, LiteralPools pools)
    {
        var cells = head.Cells;
        int arity = FunctorTable.Lookup(cells[0].AsFunctorId).Arity;
        if (!StreamsCanMatch(head, arity))
        {
            var em = new BytecodeEmitter();
            int scratch = arity;
            em.EmitPutBlit(pools.Images.Intern(head), scratch);
            em.EmitGetStructure(cells[0].AsFunctorId, scratch);
            for (int i = 0; i < arity; i++) em.EmitUnifyValueX(i);
            em.EmitProceed();
            return new CompiledClause(em.ToBytes(), functorId, arity, registerCount: arity + 1,
                permanentCount: 0, callSites: System.Array.Empty<CallSite>());
        }
        var s = new Streams(head, pools, arity);
        for (int i = 0; i < arity; i++)
        {
            s.Match(cells[1 + i], i);
            s.Drain();
        }
        s.Emitter.EmitProceed();
        return new CompiledClause(s.Emitter.ToBytes(), functorId, arity, registerCount: s.NextRegister,
            permanentCount: 0, callSites: System.Array.Empty<CallSite>());
    }

    private sealed class Streams
    {
        private readonly TermImage _image;
        private readonly LiteralPools _pools;
        // The register a variable was given, by the offset of its home cell.
        private readonly Dictionary<int, int> _registers = new();
        // What a stream deferred to a temporary: the cell to match the
        // register against, after the stream.
        private readonly Queue<(int Register, Cell Cell)> _pending = new();
        public readonly BytecodeEmitter Emitter = new();
        public int NextRegister;

        public Streams(TermImage image, LiteralPools pools, int arity)
        {
            _image = image;
            _pools = pools;
            NextRegister = arity;
        }

        /// <summary>The instructions that match register <paramref name="reg"/>
        /// against the term cell <paramref name="c"/> names.</summary>
        public void Match(Cell c, int reg)
        {
            var cells = _image.Cells;
            switch (c.Tag)
            {
                case Tag.Atom:
                    Emitter.EmitGetAtom(c.AsAtomId, reg);
                    break;
                case Tag.Int:
                    Emitter.EmitGetInteger((int)c.AsInt, reg);
                    break;
                case Tag.Str:
                {
                    int at = c.AsHeapIndex;
                    int fid = cells[at].AsFunctorId;
                    Emitter.EmitGetStructure(fid, reg);
                    Arguments(at + 1, FunctorTable.Lookup(fid).Arity);
                    break;
                }
                case Tag.Lis:
                    Emitter.EmitGetList(reg);
                    Arguments(c.AsHeapIndex, 2);
                    break;
                default:
                {
                    int at = c.AsHeapIndex;
                    Cell v = cells[at];
                    if (v.Tag == Tag.Float) Emitter.EmitGetFloat(FloatId(at), reg);
                    else if (v.Tag == Tag.Pstr) Emitter.EmitGetPstr(TextId(at), reg);
                    // A variable: its first occurrence is the register itself; a
                    // later one unifies with the first's.
                    else if (_registers.TryGetValue(at, out int seen)) Emitter.EmitGetValueX(seen, reg);
                    else _registers[at] = reg;
                    break;
                }
            }
        }

        // The stream of a block's argument cells: an atom, an integer or a
        // variable in place; a nested block or a boxed literal through a
        // temporary, matched after the stream.
        private void Arguments(int first, int count)
        {
            var cells = _image.Cells;
            for (int i = 0; i < count; i++)
            {
                Cell c = cells[first + i];
                switch (c.Tag)
                {
                    case Tag.Atom:
                        if (c.AsAtomId == AtomTable.EmptyListId) Emitter.EmitUnifyNil();
                        else Emitter.EmitUnifyAtom(c.AsAtomId);
                        break;
                    case Tag.Int:
                        Emitter.EmitUnifyInteger((int)c.AsInt);
                        break;
                    case Tag.Str:
                    case Tag.Lis:
                        Defer(c);
                        break;
                    default:
                    {
                        int at = c.AsHeapIndex;
                        Cell v = cells[at];
                        if (v.Tag is Tag.Float or Tag.Pstr) Defer(c);
                        else if (_registers.TryGetValue(at, out int seen)) Emitter.EmitUnifyValueX(seen);
                        else
                        {
                            int t = NextRegister++;
                            _registers[at] = t;
                            Emitter.EmitUnifyVariableX(t);
                        }
                        break;
                    }
                }
            }
        }

        private void Defer(Cell c)
        {
            int t = NextRegister++;
            Emitter.EmitUnifyVariableX(t);
            _pending.Enqueue((t, c));
        }

        /// <summary>Matches what the streams deferred, each after the stream
        /// that deferred it, so a variable's first occurrence runs first.</summary>
        public void Drain()
        {
            while (_pending.Count > 0)
            {
                var (reg, c) = _pending.Dequeue();
                Match(c, reg);
            }
        }

        private int FloatId(int header)
            => _pools.Floats.Intern(Cell.DecodeFloat(_image.Cells[header], _image.Cells[header + 1]));

        private int TextId(int header)
            => _pools.Strings.Intern(new TextLiteral(ClosedText(_image, header, out var kind)!, kind));
    }

    // Whether every argument, at any depth, is one the streams express: an
    // integer the operand holds, packed text that ends in [].
    private static bool StreamsCanMatch(TermImage image, int arity)
    {
        var cells = image.Cells;
        var blocks = new Stack<(int First, int Count)>();
        blocks.Push((1, arity));
        while (blocks.Count > 0)
        {
            var (first, count) = blocks.Pop();
            for (int i = 0; i < count; i++)
            {
                Cell c = cells[first + i];
                switch (c.Tag)
                {
                    case Tag.Int:
                        if (c.AsInt < int.MinValue || c.AsInt > int.MaxValue) return false;
                        break;
                    case Tag.Str:
                    {
                        int at = c.AsHeapIndex;
                        blocks.Push((at + 1, FunctorTable.Lookup(cells[at].AsFunctorId).Arity));
                        break;
                    }
                    case Tag.Lis:
                        blocks.Push((c.AsHeapIndex, 2));
                        break;
                    case Tag.Ref:
                        if (cells[c.AsHeapIndex].Tag == Tag.Pstr && ClosedText(image, c.AsHeapIndex, out _) is null)
                            return false;
                        break;
                }
            }
        }
        return true;
    }

    // The text of the packed string whose header is at `header` when it ends in
    // [] through segments of its kind, as the clause reader gives one; null when
    // it ends otherwise.
    private static string? ClosedText(TermImage image, int header, out TextKind kind)
    {
        var cells = image.Cells;
        kind = cells[header].AsPstrKind;
        string text = image.PstrRun(header);
        while (true)
        {
            Cell tail = cells[image.PstrTailOffset(header)];
            if (tail.Tag == Tag.Atom && tail.AsAtomId == AtomTable.EmptyListId) return text;
            if (tail.Tag != Tag.Ref) return null;
            int next = tail.AsHeapIndex;
            if (cells[next].Tag != Tag.Pstr || cells[next].AsPstrKind != kind) return null;
            header = next;
            text += image.PstrRun(header);
        }
    }
}
