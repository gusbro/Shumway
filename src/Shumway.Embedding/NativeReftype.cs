using System;
using System.Runtime.InteropServices;
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
/// graph. <c>char*</c> text is ANSI (matching C <c>char*</c>); the integer is a
/// 32-bit <c>cint</c> as in Arity.</para>
/// </summary>
public static class NativeReftype
{
    // Field byte offsets within the 32-byte struct (all 8-byte aligned, no padding).
    private const int OffNtype = 0;
    private const int OffNelem = 8;
    private const int OffPars = 16;
    private const int OffCrep = 24;
    internal const int StructSize = 32;

    /// <summary>Copies a term into a freshly allocated native <c>t_reftype</c> graph
    /// and returns the pointer. Release it with <see cref="Free"/>.</summary>
    public static IntPtr Materialize(Term term)
    {
        IntPtr p = Marshal.AllocHGlobal(StructSize);
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
                Marshal.WriteInt64(p, OffNtype, Reftype.Codes.Integer);
                Marshal.WriteInt32(p, OffCrep, unchecked((int)i.Value));   // cint (32-bit)
                break;
            case BigIntTerm b:
                Marshal.WriteInt64(p, OffNtype, Reftype.Codes.Integer);
                Marshal.WriteInt32(p, OffCrep, unchecked((int)(long)(b.Value & ulong.MaxValue)));
                break;
            case FloatTerm f:
                Marshal.WriteInt64(p, OffNtype, Reftype.Codes.Floating);
                Marshal.WriteInt64(p, OffCrep, BitConverter.DoubleToInt64Bits(f.Value));   // cflt
                break;
            case AtomTerm a:
                Marshal.WriteInt64(p, OffNtype, Reftype.Codes.Atom);
                Marshal.WriteInt64(p, OffNelem, a.Name.Length);
                Marshal.WriteIntPtr(p, OffCrep, Marshal.StringToHGlobalAnsi(a.Name));      // cstr
                break;
            case StringTerm s:
                Marshal.WriteInt64(p, OffNtype, Reftype.Codes.String);
                Marshal.WriteInt64(p, OffNelem, s.Content.Length);
                Marshal.WriteIntPtr(p, OffCrep, Marshal.StringToHGlobalAnsi(s.Content));
                break;
            case CompoundTerm c:
                Marshal.WriteInt64(p, OffNtype, Reftype.Codes.Functor);
                Marshal.WriteInt64(p, OffNelem, c.Args.Length);
                Marshal.WriteIntPtr(p, OffCrep, Marshal.StringToHGlobalAnsi(c.Functor));   // functor name in cstr
                IntPtr pars = Marshal.AllocHGlobal(checked(c.Args.Length * IntPtr.Size));
                for (int k = 0; k < c.Args.Length; k++)
                    Marshal.WriteIntPtr(pars, k * IntPtr.Size, Materialize(c.Args[k]));
                Marshal.WriteIntPtr(p, OffPars, pars);
                break;
            // default: leaves Nontype.
        }
        return p;
    }

    /// <summary>Copies a native <c>t_reftype</c> graph back into a Prolog term — the
    /// counterpart of <see cref="Materialize"/>, also used after a native C function
    /// has built / modified the struct. Atom and string both become an atom;
    /// undef/nontype become a fresh unbound variable.</summary>
    public static Term Dematerialize(IntPtr p)
    {
        if (p == IntPtr.Zero) return new VarTerm("_");
        long ntype = Marshal.ReadInt64(p, OffNtype);
        switch (ntype)
        {
            case Reftype.Codes.Integer:
                return new IntTerm(Marshal.ReadInt32(p, OffCrep));   // low 32 bits — cint
            case Reftype.Codes.Floating:
                return new FloatTerm(BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, OffCrep)));
            case Reftype.Codes.Atom:
            case Reftype.Codes.String:
                return new AtomTerm(Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(p, OffCrep)) ?? string.Empty);
            case Reftype.Codes.Functor:
            {
                string name = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(p, OffCrep)) ?? string.Empty;
                int arity = checked((int)Marshal.ReadInt64(p, OffNelem));
                IntPtr pars = Marshal.ReadIntPtr(p, OffPars);
                var args = new Term[arity];
                for (int k = 0; k < arity; k++)
                    args[k] = Dematerialize(Marshal.ReadIntPtr(pars, k * IntPtr.Size));
                return new CompoundTerm(name, args);
            }
            default:   // Undef, Nontype, or any unknown code
                return new VarTerm("_");
        }
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
