namespace Shumway.Embedding;

/// <summary>ADR-024 — the Arity <c>*_c</c> reftype accessor API as .NET static
/// methods over a <see cref="TermSlot"/>, so existing Arity C# (written against
/// <c>getint_c</c> / <c>putfunctor_c</c> / …) runs almost unchanged. Pointers
/// become <c>out</c> parameters and C buffers become <c>string</c> — the idiomatic
/// .NET shape; everything else maps one-to-one.
///
/// <para>This is a thin compatibility layer: the native Shumway API is the methods
/// on <see cref="TermSlot"/> itself (<c>PutInt</c>, <c>GetInt</c>,
/// <c>PutFunctor</c>, <c>Arg</c>, <c>FindType</c>, …), which new code can use
/// directly. Both sit on the same zero-copy cursor over the real Prolog term.</para>
///
/// <para>Use with <c>using static Shumway.Embedding.ReftypeApi;</c> to call the
/// bare names from interop code.</para></summary>
public static class ReftypeApi
{
    /// <summary>The ntype code of the term the slot currently holds (ADR-024:
    /// 0=undef 1=int 2=float 3=atom 4=string 5=functor 6=nontype). A Shumway atom
    /// reports 4 (string).</summary>
    public static int findtype_c(TermSlot r) => r.FindType();

    public static bool getint_c(TermSlot r, out long value) => r.GetInt(out value);
    public static void putint_c(long value, TermSlot r) => r.PutInt(value);

    public static bool getflt_c(TermSlot r, out double value) => r.GetFloat(out value);
    public static void putflt_c(double value, TermSlot r) => r.PutFloat(value);

    /// <summary>Reads the slot's atom/string text. (Arity's buffer + length
    /// parameters are vestigial in .NET — a Prolog atom already is a string.)</summary>
    public static bool gettxt_c(TermSlot r, out string text) => r.GetText(out text);
    public static void puttxt_c(string text, TermSlot r) => r.PutAtom(text);
    public static void putatm_c(string text, TermSlot r) => r.PutAtom(text);

    /// <summary>Reads a compound's functor name and arity.</summary>
    public static bool getfunctor_c(TermSlot r, out string name, out int arity)
        => r.GetFunctor(out name, out arity);

    /// <summary>Begins a compound of the given name and arity; fill its arguments
    /// via <see cref="getfuncarg_c"/> + the put/getfunctor calls.</summary>
    public static void putfunctor_c(string name, int arity, TermSlot r)
        => r.PutFunctor(name, arity);

    /// <summary>The Nth argument slot (1-based) — a mutable sub-slot when building,
    /// a read view when reading.</summary>
    public static bool getfuncarg_c(TermSlot r, int n, out TermSlot arg)
    {
        var a = r.Arg(n);
        arg = a!;
        return a is not null;
    }

    /// <summary>Structural equality of the two slots' terms.</summary>
    public static bool equrefs_c(TermSlot a, TermSlot b) => a.TermEquals(b);
}
