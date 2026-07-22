using System;
using System.Runtime.InteropServices;
using System.Text;
using Shumway.Compiler.Ast;

namespace Shumway.Embedding;

/// <summary>ADR-024 — the **blittable native-memory** form of the materializer tier.
/// A Prolog term is copied into a real <c>t_reftype</c> struct graph in unmanaged
/// memory so a native C function (reached via P/Invoke) — which cannot touch the
/// Shumway heap — can read and rebuild it. The layout is identical to Arity's:
///
/// <code>
/// union u_crep { char* cstr; int cint; double cflt; };   // 8 bytes
/// struct t_reftype {
///     int64_t ntype;            // +0
///     int64_t nelem;            // +8
///     struct t_reftype** pars;  // +16  (pointer to an array of t_reftype*)
///     union u_crep crep;        // +24
/// };                                                       // 32 bytes
/// </code>
///
/// <para>The managed <see cref="Reftype"/> snapshot and this share the
/// <see cref="Reftype.Codes"/> ntype contract; this one allocates with
/// <see cref="Marshal.AllocHGlobal(int)"/> and must be released with
/// <see cref="Free"/> (the equivalent of Arity's <c>freepar</c>), which walks the
/// graph. <c>char*</c> text is encoded with a caller-supplied
/// <see cref="System.Text.Encoding"/> (default UTF-8; configurable per engine via
/// <see cref="PrologEngine.NativeTextEncoding"/>); the integer is a 32-bit
/// <c>cint</c> as in Arity.</para>
/// </summary>
public static class NativeReftype
{
    // Field byte offsets within the 32-byte struct (all 8-byte aligned, no padding).
    private const int OffNtype = 0;
    private const int OffNelem = 8;
    private const int OffPars = 16;
    private const int OffCrep = 24;
    internal const int StructSize = 32;

    /// <summary>The default <c>char*</c> text encoding when none is supplied — UTF-8
    /// (no BOM). Configurable per engine via <see cref="PrologEngine.NativeTextEncoding"/>.</summary>
    public static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Copies a term into a freshly allocated native <c>t_reftype</c> graph
    /// and returns the pointer. <paramref name="encoding"/> (default UTF-8) governs
    /// <c>char*</c> text — atom/string content and a functor's name. Release the
    /// graph with <see cref="Free"/>.</summary>
    public static IntPtr Materialize(Term term, Encoding? encoding = null)
        => Materialize(term, encoding, arena: null);

    /// <summary>when <paramref name="arena"/> is non-null, every
    /// allocation (nodes, pars arrays, char* buffers) bump-allocates from the
    /// engine's native arena instead of <c>AllocHGlobal</c>; the caller
    /// releases the WHOLE graph by restoring its arena mark taken at call
    /// entry (no walk, no per-node free — alloc+free measured as 96% of the
    /// marshal cost of a 50-element list). Same safety contract as the
    /// recorded-allocations mode this superseded: the release covers exactly
    /// the memory WE allocated — a native function that swapped a
    /// <c>cstr</c>/<c>pars</c> with its own allocator leaves our (unlinked)
    /// block to die with the mark, and its foreign pointer is never touched
    /// (a graph-walking free would corrupt the foreign heap).</summary>
    internal static IntPtr Materialize(Term term, Encoding? encoding, PrologEngine? arena)
    {
        Encoding enc = encoding ?? DefaultEncoding;
        IntPtr p = arena is not null
            ? arena.NativeArenaAlloc(StructSize)
            : Marshal.AllocHGlobal(StructSize);
        // Zero the whole struct first so unused fields (pars/crep high bytes) are
        // deterministic.
        Marshal.WriteInt64(p, OffNtype, Reftype.Codes.Nontype);
        Marshal.WriteInt64(p, OffNelem, 0);
        Marshal.WriteIntPtr(p, OffPars, IntPtr.Zero);
        Marshal.WriteInt64(p, OffCrep, 0);

        switch (term)
        {
            case VarTerm:
                Marshal.WriteInt64(p, OffNtype, Reftype.Codes.Undef);
                break;
            case IntTerm i:
                // Arity's cint is 32-bit; a wider value cannot round-trip. Raise a
                // catchable ISO error instead of silently truncating (wrong answers).
                if (i.Value is > int.MaxValue or < int.MinValue)
                    throw new Shumway.Core.PrologRuntimeException(
                        "representation_error", "native_cint_32");
                Marshal.WriteInt64(p, OffNtype, Reftype.Codes.Integer);
                Marshal.WriteInt32(p, OffCrep, (int)i.Value);   // cint (32-bit)
                break;
            case BigIntTerm b:
                if (b.Value > int.MaxValue || b.Value < int.MinValue)
                    throw new Shumway.Core.PrologRuntimeException(
                        "representation_error", "native_cint_32");
                Marshal.WriteInt64(p, OffNtype, Reftype.Codes.Integer);
                Marshal.WriteInt32(p, OffCrep, (int)b.Value);
                break;
            case FloatTerm f:
                Marshal.WriteInt64(p, OffNtype, Reftype.Codes.Floating);
                Marshal.WriteInt64(p, OffCrep, BitConverter.DoubleToInt64Bits(f.Value));   // cflt
                break;
            case AtomTerm a:
                Marshal.WriteInt64(p, OffNtype, Reftype.Codes.Atom);
                // nelem for text is the ENCODED BYTE length — the invariant native C
                // relies on is `nelem == strlen(crep.cstr)`. Under Arity's original
                // single-byte encodings byte count and char count coincide; under
                // UTF-8 (Shumway's default) nelem stays the strlen, by design.
                Marshal.WriteInt64(p, OffNelem, enc.GetByteCount(a.Name));
                Marshal.WriteIntPtr(p, OffCrep, AllocString(a.Name, enc, arena));    // cstr
                break;
            case StringTerm s:
                Marshal.WriteInt64(p, OffNtype, Reftype.Codes.String);
                Marshal.WriteInt64(p, OffNelem, enc.GetByteCount(s.Content));
                Marshal.WriteIntPtr(p, OffCrep, AllocString(s.Content, enc, arena));
                break;
            case CompoundTerm c:
                Marshal.WriteInt64(p, OffNtype, Reftype.Codes.Functor);
                Marshal.WriteInt64(p, OffNelem, c.Args.Length);
                Marshal.WriteIntPtr(p, OffCrep, AllocString(c.Functor, enc, arena)); // functor name in cstr
                IntPtr pars = arena is not null
                    ? arena.NativeArenaAlloc(checked(c.Args.Length * IntPtr.Size))
                    : Marshal.AllocHGlobal(checked(c.Args.Length * IntPtr.Size));
                // Link pars into the node BEFORE filling children: if a child's
                // materialization throws (e.g. cint range), the recorded-free path
                // still releases everything allocated so far, and the walking Free
                // sees a consistent (zeroed) tail.
                Marshal.WriteIntPtr(p, OffPars, pars);
                for (int k = 0; k < c.Args.Length; k++)
                    Marshal.WriteIntPtr(pars, k * IntPtr.Size, IntPtr.Zero);
                for (int k = 0; k < c.Args.Length; k++)
                    Marshal.WriteIntPtr(pars, k * IntPtr.Size, Materialize(c.Args[k], enc, arena));
                break;
            // default: leaves Nontype.
        }
        return p;
    }

    /// <summary>Copies a native <c>t_reftype</c> graph back into a Prolog term — the
    /// counterpart of <see cref="Materialize"/>, also used after a native C function
    /// has built / modified the struct. Atom and string both become an atom;
    /// undef/nontype become a fresh unbound variable.</summary>
    public static Term Dematerialize(IntPtr p, Encoding? encoding = null)
    {
        if (p == IntPtr.Zero) return new VarTerm("_");
        Encoding enc = encoding ?? DefaultEncoding;
        long ntype = Marshal.ReadInt64(p, OffNtype);
        switch (ntype)
        {
            case Reftype.Codes.Integer:
                return new IntTerm(Marshal.ReadInt32(p, OffCrep));   // low 32 bits — cint
            case Reftype.Codes.Floating:
                return new FloatTerm(BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, OffCrep)));
            case Reftype.Codes.Atom:
            case Reftype.Codes.String:
                return new AtomTerm(ReadString(Marshal.ReadIntPtr(p, OffCrep), enc));
            case Reftype.Codes.Functor:
            {
                string name = ReadString(Marshal.ReadIntPtr(p, OffCrep), enc);
                int arity = checked((int)Marshal.ReadInt64(p, OffNelem));
                IntPtr pars = Marshal.ReadIntPtr(p, OffPars);
                var args = new Term[arity];
                for (int k = 0; k < arity; k++)
                    args[k] = Dematerialize(Marshal.ReadIntPtr(pars, k * IntPtr.Size), enc);
                return new CompoundTerm(name, args);
            }
            default:   // Undef, Nontype, or any unknown code
                return new VarTerm("_");
        }
    }

    // Allocates a NUL-terminated native byte buffer holding `s` encoded with
    // `enc` — a C `char*`. Byte-oriented encodings only (UTF-8 / ASCII /
    // Latin1 / a codepage), where a single 0 byte terminates the string.
    // encodes through a POOLED buffer: no per-call byte[]
    // garbage. with an `arena` the buffer is call-scoped
    // (released by the caller's mark restore); otherwise HGlobal, freed by
    // the graph walk / the caller.</summary>
    internal static IntPtr AllocString(string s, Encoding enc, PrologEngine? arena = null)
    {
        byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(enc.GetMaxByteCount(s.Length));
        int len = enc.GetBytes(s, 0, s.Length, buf, 0);
        IntPtr p = arena is not null
            ? arena.NativeArenaAlloc(len + 1)
            : Marshal.AllocHGlobal(len + 1);
        if (len > 0) Marshal.Copy(buf, 0, p, len);
        Marshal.WriteByte(p, len, 0);   // NUL terminator
        System.Buffers.ArrayPool<byte>.Shared.Return(buf);
        return p;
    }

    // Reads a NUL-terminated native `char*` and decodes it with `enc`.
    // decodes through a POOLED buffer; only the string allocates.
    internal static string ReadString(IntPtr p, Encoding enc)
    {
        if (p == IntPtr.Zero) return string.Empty;
        int len = 0;
        while (Marshal.ReadByte(p, len) != 0) len++;
        if (len == 0) return string.Empty;
        byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(len);
        Marshal.Copy(p, buf, 0, len);
        string result = enc.GetString(buf, 0, len);
        System.Buffers.ArrayPool<byte>.Shared.Return(buf);
        return result;
    }

    /// <summary>Recursively frees a native graph from <see cref="Materialize"/> (or
    /// one a native C function built and handed back) — Arity's <c>freepar</c>:
    /// the per-node <c>cstr</c> buffer, a functor's children and its <c>pars</c>
    /// array, then the node. Null is a no-op.</summary>
    public static void Free(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        long ntype = Marshal.ReadInt64(p, OffNtype);
        if (ntype is Reftype.Codes.Atom or Reftype.Codes.String or Reftype.Codes.Functor)
        {
            IntPtr cstr = Marshal.ReadIntPtr(p, OffCrep);
            if (cstr != IntPtr.Zero) Marshal.FreeHGlobal(cstr);
        }
        if (ntype == Reftype.Codes.Functor)
        {
            IntPtr pars = Marshal.ReadIntPtr(p, OffPars);
            if (pars != IntPtr.Zero)
            {
                long arity = Marshal.ReadInt64(p, OffNelem);
                for (long k = 0; k < arity; k++)
                    Free(Marshal.ReadIntPtr(pars, checked((int)(k * IntPtr.Size))));
                Marshal.FreeHGlobal(pars);
            }
        }
        Marshal.FreeHGlobal(p);
    }
}
