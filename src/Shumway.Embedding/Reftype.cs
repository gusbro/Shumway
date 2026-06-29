using System;
using System.Linq;
using Shumway.Compiler.Ast;

namespace Shumway.Embedding;

/// <summary>ADR-024 — the **materializer tier**. A managed, by-value snapshot of a
/// Prolog term shaped like Arity's <c>t_reftype</c> struct:
///
/// <code>
/// union u_crep { char* cstr; int cint; double cflt; };
/// typedef struct t_reftype {
///     int64_t ntype;            // tag — see Ntype codes
///     int64_t nelem;            // arity (functor) | string length
///     struct t_reftype** pars;  // argument array (functor)
///     union u_crep crep;        // value
/// } *reftype, t_reftype;
/// </code>
///
/// <para>Unlike the cursor tier (a <see cref="TermSlot"/> over the live heap, zero
/// copy), this is a real recursive **copy** — the representation handed to logic
/// that cannot touch the Shumway heap: a native C function via P/Invoke, or a .NET
/// interop method that wants a struct snapshot rather than a cursor. This managed
/// form backs the snapshot path; the blittable native-memory form (for the P/Invoke
/// trampoline) mirrors this layout.</para>
///
/// <para><see cref="Materialize"/> copies a term into a tree of these; the native C
/// or .NET code reads / rebuilds it; <see cref="Dematerialize"/> copies the
/// (possibly modified) tree back into a term. The two never diverge from the
/// <see cref="Ntype"/> contract.</para>
/// </summary>
public sealed class Reftype
{
    /// <summary>The term's kind — an <see cref="Ntype"/> code.</summary>
    public long Ntype;
    /// <summary>Functor arity, or string/atom length. 0 for scalars.</summary>
    public long Nelem;
    /// <summary>Functor argument snapshots (<c>Ntype == Functor</c>); otherwise null.</summary>
    public Reftype[]? Pars;
    /// <summary>Integer value (<c>Ntype == Integer</c>) — the union's <c>cint</c>.</summary>
    public long Cint;
    /// <summary>Float value (<c>Ntype == Floating</c>) — the union's <c>cflt</c>.</summary>
    public double Cflt;
    /// <summary>Text — an atom/string's characters (<c>Ntype == Atom/String</c>) or a
    /// functor's name (<c>Ntype == Functor</c>) — the union's <c>cstr</c>.</summary>
    public string? Cstr;

    /// <summary>ntype codes — the source of truth shared with the cursor API
    /// (<c>findtype_c</c>) and the native blittable form, so they never diverge
    /// (ADR-024 § ntype codes).</summary>
    public static class Codes
    {
        public const long Undef = 0;     // unbound variable
        public const long Integer = 1;   // integer cell
        public const long Floating = 2;  // float cell
        public const long Atom = 3;      // atom
        public const long String = 4;    // Arity "string" — an atom in Shumway
        public const long Functor = 5;   // compound (functor + args)
        public const long Nontype = 6;   // treated as undef
    }

    /// <summary>Copies a Prolog term (AST) into a <see cref="Reftype"/> tree —
    /// recursing over a compound's arguments. A variable becomes <c>Undef</c>; an
    /// atom <c>Atom</c>; a string <c>String</c> (both read back as an atom).</summary>
    public static Reftype Materialize(Term term) => term switch
    {
        VarTerm => new Reftype { Ntype = Codes.Undef },
        IntTerm i => new Reftype { Ntype = Codes.Integer, Cint = i.Value },
        // Arity's cint is a 32-bit int; a bigint is truncated to its low 64 bits
        // here (the reftype struct cannot represent it — same limit as Arity).
        BigIntTerm b => new Reftype { Ntype = Codes.Integer, Cint = (long)(b.Value & ulong.MaxValue) },
        FloatTerm f => new Reftype { Ntype = Codes.Floating, Cflt = f.Value },
        AtomTerm a => new Reftype { Ntype = Codes.Atom, Cstr = a.Name, Nelem = a.Name.Length },
        StringTerm s => new Reftype { Ntype = Codes.String, Cstr = s.Content, Nelem = s.Content.Length },
        CompoundTerm c => new Reftype
        {
            Ntype = Codes.Functor,
            Cstr = c.Functor,
            Nelem = c.Args.Length,
            Pars = c.Args.Select(Materialize).ToArray(),
        },
        _ => new Reftype { Ntype = Codes.Nontype },
    };

    /// <summary>Copies a <see cref="Reftype"/> tree back into a Prolog term.
    /// <c>Undef</c> / <c>Nontype</c> become a fresh unbound variable; both
    /// <c>Atom</c> and <c>String</c> become an atom (Arity "string" is an atom in
    /// Shumway); a <c>Functor</c> rebuilds the compound from its <see cref="Pars"/>.</summary>
    public static Term Dematerialize(Reftype r) => r.Ntype switch
    {
        Codes.Integer => new IntTerm(r.Cint),
        Codes.Floating => new FloatTerm(r.Cflt),
        Codes.Atom or Codes.String => new AtomTerm(r.Cstr ?? string.Empty),
        Codes.Functor => new CompoundTerm(r.Cstr ?? string.Empty,
            (r.Pars ?? Array.Empty<Reftype>()).Select(Dematerialize).ToArray()),
        // Undef, Nontype, or any unknown code → a fresh unbound variable.
        _ => new VarTerm("_"),
    };
}
