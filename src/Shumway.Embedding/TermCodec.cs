using System.IO;
using System.Numerics;
using System.Text;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Compact binary serialiser for <see cref="Term"/> / <see cref="Clause"/>
/// trees. Used by the .shmo format to carry the source clauses
/// of <c>:- dynamic foo/N.</c> predicates across the compile / load
/// boundary — they can't ride along as static bytecode (the engine has to
/// be able to assertz / retract / clause/2 against them), and re-shipping
/// the original Prolog source defeats <c>--strip</c>'s IP-protection
/// promise.
///
/// <para>Format (little-endian throughout):</para>
/// <list type="bullet">
/// <item>1 byte tag.</item>
/// <item>0 = AtomTerm: length-prefixed UTF-8 name.</item>
/// <item>1 = VarTerm: length-prefixed UTF-8 name.</item>
/// <item>2 = IntTerm: 8-byte signed long.</item>
/// <item>3 = BigIntTerm: length-prefixed byte array (BigInteger.ToByteArray).</item>
/// <item>4 = FloatTerm: 8-byte IEEE 754 double.</item>
/// <item>5 = StringTerm: length-prefixed UTF-8 value.</item>
/// <item>6 = CompoundTerm: length-prefixed functor name + uint32 arity + N encoded args.</item>
/// </list>
/// Length prefixes are unsigned 32-bit. Anonymous variables ride along as
/// the canonical name <c>_</c>.
/// </summary>
public static class TermCodec
{
    private const byte TagAtom = 0;
    private const byte TagVar = 1;
    private const byte TagInt = 2;
    private const byte TagBigInt = 3;
    private const byte TagFloat = 4;
    private const byte TagString = 5;
    private const byte TagCompound = 6;

    /// <summary>Writes a term. ITERATIVE over an explicit stack: a stored
    /// clause can hold a list of any length, and a recursive walk overflowed
    /// the C# stack — killing the process, not the compile — on a source file
    /// carrying one. Arguments are pushed right to left so they are written
    /// in order.</summary>
    public static void WriteTerm(BinaryWriter w, Term root)
    {
        var work = new List<Term>(32) { root };
        while (work.Count > 0)
        {
            Term term = work[^1];
            work.RemoveAt(work.Count - 1);
            WriteNode(w, term, work);
        }
    }

    private static void WriteNode(BinaryWriter w, Term term, List<Term> work)
    {
        switch (term)
        {
            case AtomTerm a:
                w.Write(TagAtom);
                WriteString(w, a.Name);
                break;
            case VarTerm v:
                w.Write(TagVar);
                WriteString(w, v.Name);
                break;
            case IntTerm i:
                w.Write(TagInt);
                w.Write(i.Value);
                break;
            case BigIntTerm bi:
                w.Write(TagBigInt);
                byte[] biBytes = bi.Value.ToByteArray();
                w.Write((uint)biBytes.Length);
                w.Write(biBytes);
                break;
            case FloatTerm f:
                w.Write(TagFloat);
                w.Write(f.Value);
                break;
            case StringTerm s:
                w.Write(TagString);
                WriteString(w, s.Content);
                w.Write((byte)s.Kind);
                break;
            case CompoundTerm c:
                w.Write(TagCompound);
                WriteString(w, c.Functor);
                w.Write((uint)c.Args.Length);
                for (int i = c.Args.Length - 1; i >= 0; i--) work.Add(c.Args[i]);
                break;
            default:
                throw new InvalidDataException(
                    $"TermCodec: unsupported term type {term.GetType().Name}.");
        }
    }

    /// <summary>One compound being decoded: its arguments so far.</summary>
    private sealed class ReadFrame
    {
        public string Functor = "";
        public Term[] Args = System.Array.Empty<Term>();
        public int Index;
    }

    /// <summary>Reads a term. ITERATIVE, for the same reason
    /// <see cref="WriteTerm"/> is: a bundle can carry a clause holding a list
    /// of any length, and the decode runs at load time, where a process death
    /// is at its least diagnosable.</summary>
    public static Term ReadTerm(BinaryReader r)
    {
        var stack = new List<ReadFrame>();
        while (true)
        {
            Term? done = ReadNode(r, stack);
            // Hand the finished node to its parent, completing parents as
            // they fill up.
            while (done is not null && stack.Count > 0)
            {
                ReadFrame f = stack[^1];
                f.Args[f.Index++] = done;
                if (f.Index < f.Args.Length) { done = null; break; }
                done = new CompoundTerm(f.Functor, f.Args);
                stack.RemoveAt(stack.Count - 1);
            }
            if (done is not null && stack.Count == 0) return done;
        }
    }

    /// <summary>Decodes one node. Returns null when it was a compound with
    /// arguments — that pushed a frame and the caller keeps reading.</summary>
    private static Term? ReadNode(BinaryReader r, List<ReadFrame> stack)
    {
        byte tag = r.ReadByte();
        switch (tag)
        {
            case TagAtom:
                return new AtomTerm(ReadString(r));
            case TagVar:
                return new VarTerm(ReadString(r));
            case TagInt:
                return new IntTerm(r.ReadInt64());
            case TagBigInt:
            {
                int len = checked((int)r.ReadUInt32());
                byte[] bytes = r.ReadBytes(len);
                if (bytes.Length != len)
                    throw new InvalidDataException(
                        $"TermCodec: short read on BigInt payload ({bytes.Length}/{len}).");
                return new BigIntTerm(new BigInteger(bytes));
            }
            case TagFloat:
                return new FloatTerm(r.ReadDouble());
            case TagString:
            {
                string text = ReadString(r);
                byte kind = r.ReadByte();
                if (kind > (byte)TextKind.Chars)
                    throw new InvalidDataException(
                        $"TermCodec: unknown TextKind {kind}.");
                return new StringTerm(text, (TextKind)kind);
            }
            case TagCompound:
            {
                string functor = ReadString(r);
                int arity = checked((int)r.ReadUInt32());
                if (arity == 0)
                    return new CompoundTerm(functor, System.Array.Empty<Term>());
                stack.Add(new ReadFrame
                {
                    Functor = functor,
                    Args = new Term[arity],
                });
                return null;
            }
            default:
                throw new InvalidDataException(
                    $"TermCodec: unknown tag 0x{tag:X2}.");
        }
    }

    /// <summary>Serialises a <see cref="Clause"/> as just its
    /// <c>Term</c> — the clause kind is recoverable from the term shape
    /// (<see cref="Clause.Classify"/>) and <see cref="SourcePosition"/>
    /// is debug info that doesn't survive Release builds anyway.</summary>
    public static void WriteClause(BinaryWriter w, Clause clause)
        => WriteTerm(w, clause.Term);

    public static Clause ReadClause(BinaryReader r)
    {
        var term = ReadTerm(r);
        return Clause.From(term);
    }

    public static byte[] EncodeClause(Clause clause)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            WriteClause(bw, clause);
        return ms.ToArray();
    }

    public static Clause DecodeClause(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        return ReadClause(br);
    }

    private static void WriteString(BinaryWriter w, string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        w.Write((uint)bytes.Length);
        w.Write(bytes);
    }

    private static string ReadString(BinaryReader r)
    {
        int len = checked((int)r.ReadUInt32());
        byte[] bytes = r.ReadBytes(len);
        if (bytes.Length != len)
            throw new InvalidDataException(
                $"TermCodec: short read on string payload ({bytes.Length}/{len}).");
        return Encoding.UTF8.GetString(bytes);
    }
}
