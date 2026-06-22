using Shumway.Compiler.Ast;

namespace Shumway.Embedding;

/// <summary>ADR-024 — a reftype/preftype as a zero-copy CURSOR over a Prolog term,
/// not a copied C struct. A slot holds either a finalized term value (read from
/// Prolog by <c>fill_par</c>, or built scalar) or a compound under construction
/// (functor name + a sub-slot per argument). The .NET interop side reads its shape
/// and builds into it through <see cref="ReftypeApi"/>; <c>reftype_term</c>
/// materializes it back to a Prolog term and unifies.
///
/// <para>This first cut models the cursor at the AST <see cref="Term"/> level
/// (pragmatic and correct); a later refinement can push it down to raw heap cells.
/// The slot itself is wrapped as a Foreign cell (<see cref="Shumway.Core.Engine.
/// MakeForeign"/>) so it can live in a Prolog variable and pass between
/// builtins.</para></summary>
public sealed class TermSlot
{
    // ntype codes — the shared contract (ADR-024). Also used by findtype_c and the
    // future materializer, so the two never diverge.
    public const int Undef = 0, Integer = 1, Floating = 2, Atom = 3,
                     String = 4, Functor = 5, Nontype = 6;

    private Term? _value;       // a finalized / read value, or null
    private string? _functor;   // a compound under construction
    private TermSlot[]? _args;

    /// <summary>Set the slot to a finalized term (e.g. <c>fill_par</c> stores the
    /// Prolog term here). Clears any construction state.</summary>
    public void SetValue(Term term) { _value = term; _functor = null; _args = null; }

    /// <summary>Reset to an empty slot (an unbound variable until built).</summary>
    public void Clear() { _value = null; _functor = null; _args = null; }

    /// <summary>Materialize the slot to a Prolog term (the inverse of
    /// <see cref="SetValue"/>): the stored value, the compound being built (its
    /// argument slots materialized recursively), or a fresh variable when
    /// empty.</summary>
    public Term Materialize()
    {
        if (_functor is not null)
        {
            var args = new Term[_args!.Length];
            for (int i = 0; i < args.Length; i++) args[i] = _args[i].Materialize();
            return new CompoundTerm(_functor, args);
        }
        return _value ?? new VarTerm("_");
    }

    // ----- construction (put*) -------------------------------------------------

    public void PutInt(long v) => SetValue(new IntTerm(v));
    public void PutFloat(double v) => SetValue(new FloatTerm(v));
    // Arity "string" and "atom" are both a Shumway atom.
    public void PutAtom(string s) => SetValue(new AtomTerm(s));

    /// <summary>Begin a compound term of the given name and arity. Its argument
    /// slots start empty (unbound) and are filled via <see cref="Arg"/>.</summary>
    public void PutFunctor(string name, int arity)
    {
        _value = null;
        _functor = name;
        _args = new TermSlot[arity];
        for (int i = 0; i < arity; i++) _args[i] = new TermSlot();
    }

    /// <summary>The slot for argument <paramref name="n"/> (1-based) — a mutable
    /// sub-slot when building (from <see cref="PutFunctor"/>), or a read-only view
    /// of the stored compound's argument when reading. Null if out of range or the
    /// slot is neither.</summary>
    public TermSlot? Arg(int n)
    {
        if (_args is not null)
            return n >= 1 && n <= _args.Length ? _args[n - 1] : null;
        if (_value is CompoundTerm c && n >= 1 && n <= c.Args.Length)
        {
            var s = new TermSlot();
            s.SetValue(c.Args[n - 1]);
            return s;
        }
        return null;
    }

    // ----- reading (get* / findtype) -------------------------------------------

    /// <summary>The ntype code (ADR-024) of the slot's current shape.</summary>
    public int FindType()
    {
        if (_functor is not null) return Functor;
        return _value switch
        {
            null or VarTerm => Undef,
            IntTerm or BigIntTerm => Integer,
            FloatTerm => Floating,
            // a Shumway atom reads back as STRING (4) — Arity uses "string"
            // for nearly everything; both atom and string map to an atom.
            AtomTerm or StringTerm => String,
            CompoundTerm => Functor,
            _ => Nontype,
        };
    }

    public bool GetInt(out long v)
    {
        if (_value is IntTerm i) { v = i.Value; return true; }
        v = 0;
        return false;
    }

    public bool GetFloat(out double v)
    {
        switch (_value)
        {
            case FloatTerm f: v = f.Value; return true;
            case IntTerm i: v = i.Value; return true;   // int widens to float
            default: v = 0; return false;
        }
    }

    public bool GetText(out string s)
    {
        switch (_value)
        {
            case AtomTerm a: s = a.Name; return true;
            case StringTerm st: s = st.Content; return true;
            default: s = ""; return false;
        }
    }

    public bool GetFunctor(out string name, out int arity)
    {
        if (_functor is not null) { name = _functor; arity = _args!.Length; return true; }
        if (_value is CompoundTerm c) { name = c.Functor; arity = c.Args.Length; return true; }
        name = "";
        arity = 0;
        return false;
    }

    /// <summary>Structural equality of two slots' current terms (Arity
    /// <c>equrefs_c</c>).</summary>
    public bool TermEquals(TermSlot other) => TermStructEquals(Materialize(), other.Materialize());

    private static bool TermStructEquals(Term a, Term b) => (a, b) switch
    {
        (IntTerm x, IntTerm y) => x.Value == y.Value,
        (FloatTerm x, FloatTerm y) => x.Value == y.Value,
        (AtomTerm x, AtomTerm y) => x.Name == y.Name,
        (StringTerm x, StringTerm y) => x.Content == y.Content,
        (CompoundTerm x, CompoundTerm y) =>
            x.Functor == y.Functor && x.Args.Length == y.Args.Length
            && ArgsEqual(x.Args, y.Args),
        _ => false,
    };

    private static bool ArgsEqual(Term[] a, Term[] b)
    {
        for (int i = 0; i < a.Length; i++)
            if (!TermStructEquals(a[i], b[i])) return false;
        return true;
    }
}
