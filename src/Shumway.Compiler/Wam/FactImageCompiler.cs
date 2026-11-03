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
/// <c>unify_value_x</c>). A compound argument of at least the head threshold's
/// cells has an image of its own and <c>get_blit</c> before its stream, as a
/// source clause's head does: the argument unbound, the copy stands in for
/// the stream.</summary>
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
        s.RegisterPlainArguments();
        for (int i = 0; i < arity; i++)
        {
            Cell arg = cells[1 + i];
            int blit = s.BeginArgumentImage(arg, i);
            s.Match(arg, i);
            s.Drain();
            if (blit >= 0) s.EndArgumentImage(blit, arg);
        }
        s.Emitter.EmitProceed();
        return new CompiledClause(s.Emitter.ToBytes(), functorId, arity, registerCount: s.NextRegister,
            permanentCount: 0, callSites: System.Array.Empty<CallSite>(),
            dispatchSites: s.DispatchSites.Count == 0 ? null : s.DispatchSites);
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
        private readonly int _arity;
        // The homes with a register before the argument under an image ran
        // its stream: the copy reads those registers and writes the rest.
        private HashSet<int>? _knownBefore;
        /// <summary>The address operands of the <c>get_blit</c>s, relative to the
        /// clause: the code's placement relocates them.</summary>
        public readonly List<int> DispatchSites = new();

        public Streams(TermImage image, LiteralPools pools, int arity)
        {
            _image = image;
            _pools = pools;
            _arity = arity;
            NextRegister = arity;
        }

        /// <summary>A variable that is a plain argument has its register from
        /// the call, wherever it sits: the streams read it as such.</summary>
        public void RegisterPlainArguments()
        {
            var cells = _image.Cells;
            for (int i = 0; i < _arity; i++)
            {
                Cell c = cells[1 + i];
                if (c.Tag != Tag.Ref) continue;
                int home = c.AsHeapIndex;
                if (cells[home].Tag == Tag.Ref && !_registers.ContainsKey(home)) _registers[home] = i;
            }
        }

        /// <summary>ADR-063, heads: <c>get_blit</c> before the stream of a
        /// compound argument of at least the head threshold's cells, holes for
        /// at most half of them, that names nothing laid outside its own run of
        /// the head's image. The instruction's position, patched when the
        /// stream is done; -1 for an argument that keeps its stream alone.</summary>
        public int BeginArgumentImage(Cell c, int reg)
        {
            if (!ClauseCompiler.ImageHeads || c.Tag is not (Tag.Str or Tag.Lis)) return -1;
            int threshold = ClauseCompiler.HeadThreshold;
            if (threshold <= 0 || _image.Length - 1 - _arity < threshold) return -1;
            var cells = _image.Cells;
            int offset = c.AsHeapIndex, end = _image.BlockEnd(offset);
            if (end - offset < threshold) return -1;
            int holes = 0;
            for (int i = offset; i < end; i++)
            {
                Cell x = cells[i];
                if (x.Tag != Tag.Ref) continue;
                int t = x.AsHeapIndex;
                if (t == i) holes++;                                     // a variable's home
                else if (t < offset || t >= end)
                {
                    // A variable from outside, with its register already: a
                    // plain argument's, or one an earlier stream gave it.
                    if (cells[t].Tag != Tag.Ref || !_registers.ContainsKey(t)) return -1;
                    holes++;
                }
            }
            if (holes * 2 > end - offset || (holes > 0 && !ClauseCompiler.ImageHoles)) return -1;
            // The run is the argument's alone when what the argument reaches
            // fills it: the copier lays a sub-term as one run, and a block or
            // a literal laid elsewhere, or another argument's laid in the run,
            // would be copied for nothing or counted toward the threshold.
            if (ReachableCells(offset, c.Tag == Tag.Lis) != end - offset) return -1;
            _knownBefore = new HashSet<int>(_registers.Keys);
            return Emitter.EmitGetBlit(0, reg);
        }

        // The cells the block at `at` reaches, each once: its own, the blocks
        // it names, the floats and packed strings it refers to, their tails.
        private int ReachableCells(int at, bool list)
        {
            var cells = _image.Cells;
            var seen = new HashSet<int>();
            var blocks = new Stack<(int At, bool List)>();
            var visits = new Stack<int>();
            blocks.Push((at, list));
            int count = 0;
            while (blocks.Count > 0)
            {
                var (b, isList) = blocks.Pop();
                if (!seen.Add(b)) continue;
                int first = isList ? b : b + 1;
                int k = isList ? 2 : FunctorTable.Lookup(cells[b].AsFunctorId).Arity;
                count += isList ? 2 : 1 + k;
                for (int i = 0; i < k; i++) visits.Push(first + i);
                while (visits.Count > 0)
                {
                    int idx = visits.Pop();
                    Cell x = cells[idx];
                    if (x.Tag is Tag.Str or Tag.Lis)
                    {
                        blocks.Push((x.AsHeapIndex, x.Tag == Tag.Lis));
                        continue;
                    }
                    if (x.Tag != Tag.Ref || x.AsHeapIndex == idx) continue;
                    int t = x.AsHeapIndex;
                    Cell target = cells[t];
                    if (target.Tag == Tag.Float)
                    {
                        if (seen.Add(~t)) count += 2;
                    }
                    else if (target.Tag == Tag.Pstr && seen.Add(~t))
                    {
                        int end = TermImage.PstrBlockEnd(t, target);
                        count += end - t;
                        visits.Push(end - 1);   // the tail: [], a variable, or the next segment
                    }
                }
            }
            return count;
        }

        /// <summary>The stream done, every variable of the argument has its
        /// register: the image is cut from the head's, with a hole at each
        /// home (read when the register was there before this stream, written
        /// when this stream gave it) and at each reference to a home outside,
        /// and the instruction learns its id and where the stream ends.</summary>
        public void EndArgumentImage(int at, Cell c)
        {
            var cells = _image.Cells;
            int offset = c.AsHeapIndex, end = _image.BlockEnd(offset);
            var image = new Cell[end - offset];
            _image.CopyBlockTo(image, 0, offset, end);
            var holes = new List<ImageHole>();
            for (int i = offset; i < end; i++)
            {
                Cell x = cells[i];
                if (x.Tag != Tag.Ref) continue;
                int t = x.AsHeapIndex;
                if (t == i)
                    holes.Add(new ImageHole(i - offset,
                        _knownBefore!.Contains(i) ? ImageHole.In : ImageHole.Out, _registers[i]));
                else if (t < offset || t >= end)
                {
                    // Relocated, the reference is no cell of the image: an
                    // unbound cell until the copy fills it.
                    image[i - offset] = Cell.Ref(i - offset);
                    holes.Add(new ImageHole(i - offset, ImageHole.In, _registers[t]));
                }
            }
            int id = _pools.Images.Intern(new TermImage(image, c.Tag, holes.Count == 0 ? null : holes.ToArray()));
            Emitter.PatchInt32(at + 1, id);
            Emitter.PatchInt32(at + 9, Emitter.Position);
            DispatchSites.Add(at + 9);
            _knownBefore = null;
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
                    else if (!_registers.TryGetValue(at, out int seen)) _registers[at] = reg;
                    else if (seen != reg) Emitter.EmitGetValueX(seen, reg);
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
