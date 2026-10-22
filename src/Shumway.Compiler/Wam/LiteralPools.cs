using System.Numerics;
using Shumway.Core;

namespace Shumway.Compiler.Wam;

/// <summary>
/// The literal pools a module compilation interns into: packed text (PSTR),
/// floats, big integers (rationals name two), and the images of the ground
/// terms a body copies (ADR-063).
///
/// <para><see cref="ModuleCompiler.Compile"/> creates a fresh set per call
/// by default. ADR-015 instead has the engine own one set for its life and
/// pass it to every compilation, so a literal keeps a stable id across
/// queries: the precondition for caching a separately linked static region
/// whose bytecode embeds those ids. The pools are released with the code:
/// the walks here add, release or pin the operands of a code unit as it
/// enters or leaves the live code, and a sweep frees what nothing names
/// (<see cref="LiteralPool{T}"/>).</para>
/// </summary>
public sealed class LiteralPools
{
    public LiteralPool<TextLiteral> Strings { get; } = new();
    public LiteralPool<double> Floats { get; } = new();
    public LiteralPool<BigInteger> BigInts { get; } = new();
    public LiteralPool<TermImage> Images { get; } = new();

    private enum Visit { AddRef, Release, Pin, Probe, Collect }

    /// <summary>A code unit entered the live code: one reference per
    /// literal operand of <paramref name="code"/>. True when it names any.</summary>
    public bool AddRefs(byte[] code) => Walk(code, 0, code.Length, Visit.AddRef, null);

    public bool AddRefs(byte[] code, int start, int length) => Walk(code, start, length, Visit.AddRef, null);

    /// <summary>A code unit left the live code: the references
    /// <see cref="AddRefs(byte[])"/> took, back.</summary>
    public bool Release(byte[] code) => Walk(code, 0, code.Length, Visit.Release, null);

    public bool Release(byte[] code, int start, int length) => Walk(code, start, length, Visit.Release, null);

    /// <summary>Static code: every literal operand of <paramref name="code"/>
    /// pinned for the pools' life.</summary>
    public bool Pin(byte[] code) => Walk(code, 0, code.Length, Visit.Pin, null);

    /// <summary>Whether <paramref name="code"/> names any pool literal.</summary>
    public bool NamesAny(byte[] code) => Walk(code, 0, code.Length, Visit.Probe, null);

    /// <summary>The values <paramref name="code"/> names, by id, frozen: a
    /// compile on another thread reads these while the pools go on.</summary>
    public LiteralViews ViewsFor(byte[] code)
    {
        var views = new LiteralViews(Strings.Count, Floats.Count, BigInts.Count, Images.Count);
        Walk(code, 0, code.Length, Visit.Collect, views);
        return views;
    }

    /// <summary>Whether any pool has enough released entries for a sweep.</summary>
    public bool SweepDue => Strings.SweepDue || Floats.SweepDue || BigInts.SweepDue || Images.SweepDue;

    /// <summary>Frees every counted entry at zero in every pool; the entries freed.</summary>
    public int Sweep() => Strings.Sweep() + Floats.Sweep() + BigInts.Sweep() + Images.Sweep();

    /// <summary>A sweep when one is due; the entries freed, else 0.</summary>
    public int SweepIfDue() => SweepDue ? Sweep() : 0;

    private bool Walk(byte[] code, int start, int length, Visit visit, LiteralViews? views)
    {
        bool any = false;
        int end = start + length;
        for (int pc = start; pc < end;)
        {
            byte b = code[pc];
            var info = OpcodeTable.Get(b);
            if (!info.IsDefined || info.Size == 0)
                throw new InvalidOperationException(
                    $"literal walk: undefined opcode 0x{b:X2} at {pc}.");
            switch ((Opcode)b)
            {
                case Opcode.GetFloat:
                case Opcode.PutFloat:
                case Opcode.UnifyFloat:
                    any = true;
                    Touch(Floats, views?.Floats, code, pc + 1, visit);
                    break;
                case Opcode.GetBigInt:
                case Opcode.PutBigInt:
                case Opcode.UnifyBigInt:
                    any = true;
                    Touch(BigInts, views?.BigInts, code, pc + 1, visit);
                    break;
                // ADR-039: both ids of a rational name the big-integer pool.
                case Opcode.GetRational:
                case Opcode.PutRational:
                case Opcode.UnifyRational:
                    any = true;
                    Touch(BigInts, views?.BigInts, code, pc + 1, visit);
                    Touch(BigInts, views?.BigInts, code, pc + 5, visit);
                    break;
                case Opcode.GetPstr:
                case Opcode.PutPstr:
                    any = true;
                    Touch(Strings, views?.Strings, code, pc + 1, visit);
                    break;
                case Opcode.PutBlit:
                case Opcode.GetBlit:
                case Opcode.UnifyBlit:
                    any = true;
                    Touch(Images, views?.Images, code, pc + 1, visit);
                    break;
                case Opcode.AEvalPush:
                {
                    // <kind:4> <operand:4>; kind 1 names a big integer, 2 a float.
                    int kind = BytecodeIO.ReadInt32(code, pc + 1);
                    if (kind == 2) { any = true; Touch(Floats, views?.Floats, code, pc + 5, visit); }
                    else if (kind == 1) { any = true; Touch(BigInts, views?.BigInts, code, pc + 5, visit); }
                    break;
                }
            }
            if (any && visit == Visit.Probe) return true;
            pc += info.Size;
        }
        return any;
    }

    private static void Touch<T>(LiteralPool<T> pool, Dictionary<int, T>? view, byte[] code, int offset, Visit visit)
        where T : notnull
    {
        int id = BytecodeIO.ReadInt32(code, offset);
        switch (visit)
        {
            case Visit.AddRef: pool.AddRef(id); break;
            case Visit.Release: pool.Release(id); break;
            case Visit.Pin: pool.Pin(id); break;
            case Visit.Collect: view![id] = pool.Items[id]; break;
        }
    }
}

/// <summary>The values one code unit names in each pool, by id, taken on
/// the engine's thread: a compile that bakes them (an IL snapshot of a
/// dynamic predicate, on the worker) reads these, never the pools, so a
/// sweep and a reuse of an id meanwhile cannot reach it. Each view answers
/// the ids the unit names and the pool's length when taken.</summary>
public sealed class LiteralViews
{
    internal readonly Dictionary<int, TextLiteral> Strings = new();
    internal readonly Dictionary<int, double> Floats = new();
    internal readonly Dictionary<int, BigInteger> BigInts = new();
    internal readonly Dictionary<int, TermImage> Images = new();
    private readonly int _strings, _floats, _bigInts, _images;

    internal LiteralViews(int strings, int floats, int bigInts, int images)
    {
        _strings = strings;
        _floats = floats;
        _bigInts = bigInts;
        _images = images;
    }

    public IReadOnlyList<TextLiteral> StringView => new SparseView<TextLiteral>(Strings, _strings);
    public IReadOnlyList<double> FloatView => new SparseView<double>(Floats, _floats);
    public IReadOnlyList<BigInteger> BigIntView => new SparseView<BigInteger>(BigInts, _bigInts);
    public IReadOnlyList<TermImage> ImageView => new SparseView<TermImage>(Images, _images);

    private sealed class SparseView<T> : IReadOnlyList<T>
    {
        private readonly Dictionary<int, T> _byId;
        public SparseView(Dictionary<int, T> byId, int count) { _byId = byId; Count = count; }
        public int Count { get; }
        public T this[int index] => _byId.TryGetValue(index, out var v)
            ? v
            : throw new InvalidOperationException($"literal {index} is not one the code unit names.");
        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) if (_byId.TryGetValue(i, out var v)) yield return v;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
