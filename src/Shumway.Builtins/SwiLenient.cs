using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>ADR-040 — helpers for builtins whose STRICT ISO behaviour would
/// raise a <c>type_error(atom)</c> for a non-atom argument, but whose SWI
/// counterpart coerces any atomic to text. Each such builtin, on the path where
/// it was about to raise, asks <see cref="CallerIsSwi"/> whether its caller lives
/// in an SWI-dialect module (a chain-walk over the call-return frames — see
/// <see cref="IDialectAwareHost"/>) and, if so, coerces via
/// <see cref="TryAtomicText"/>. The cost is paid only on the would-raise path, so
/// a strict program keeps ISO/GProlog behaviour at zero cost.</summary>
internal static class SwiLenient
{
    /// <summary>Whether the running goal (or an ancestor) lives in a module
    /// loaded as the SWI dialect.</summary>
    public static bool CallerIsSwi(Activation engine)
        => engine.Host is IDialectAwareHost h && h.CallerModuleHasDialect(engine, "swi");

    /// <summary>A bound, non-variable term this can render as text: an atom, a
    /// number, or a packed text list.</summary>
    public static bool IsBoundAtomic(Cell c) =>
        c.Tag is Tag.Atom or Tag.Int or Tag.BigInt or Tag.Float or Tag.Rational
            or Tag.Pstr;

    /// <summary>Renders a bound atomic cell to its text (an atom's name, a
    /// number's canonical text, a string's content). False for a shape we cannot
    /// render, so the caller falls back to the strict error.</summary>
    public static bool TryAtomicText(Activation engine, Cell c, out string text)
    {
        switch (c.Tag)
        {
            case Tag.Atom: text = AtomTable.GetById(c.AsAtomId)?.Name ?? ""; return true;
            case Tag.Int: text = c.AsInt.ToString(System.Globalization.CultureInfo.InvariantCulture); return true;
            case Tag.BigInt: text = engine.AsBigInt(c).ToString(System.Globalization.CultureInfo.InvariantCulture); return true;
            case Tag.Rational: text = engine.AsRational(c).ToString(); return true;
            case Tag.Float:
                text = Number.FormatPrologFloat(
                    Cell.DecodeFloat(c, engine.GetHeap(c.FloatPairedIndex)));
                return true;
            case Tag.Pstr: text = engine.ReadPstrChain(c, out _); return true;
            default: text = ""; return false;
        }
    }

    /// <summary>Combined test: the caller is SWI, the cell is atomic, and it
    /// renders. Returns the coerced text. Keeps each call site to a single guard.</summary>
    public static bool TryCoerce(Activation engine, Cell c, out string text)
    {
        text = "";
        return IsBoundAtomic(c) && CallerIsSwi(engine) && TryAtomicText(engine, c, out text);
    }
}
