using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// ISO comparison predicates over the standard order of terms.
/// <see cref="Compare3"/> exposes the trichotomous result as an atom
/// (<c>&lt;</c> / <c>=</c> / <c>&gt;</c>); the four <c>@</c>-prefixed
/// predicates are convenient shortcuts that wrap it for the four typical
/// boolean uses.
/// </summary>
public static class StandardOrderBuiltins
{
    /// <summary><c>compare(Order, X, Y)</c> — unifies <c>Order</c> with the
    /// atom <c>&lt;</c>, <c>=</c>, or <c>&gt;</c> according to the standard
    /// order of <c>X</c> and <c>Y</c>.</summary>
    public static bool Compare3(Activation engine)
    {
        // §8.4.2.3: a bound Order must be one of the three order atoms —
        // anything else is type_error(atom, O) / domain_error(order, O).
        Cell ord = engine.GetRegister(0);
        if (ord.Tag == Tag.Ref) ord = engine.GetHeap(engine.Deref(ord.AsHeapIndex));
        if (ord.Tag is not (Tag.Ref or Tag.AttVar))
        {
            if (ord.Tag != Tag.Atom)
                throw new PrologRuntimeException("type_error", "atom", engine, ord);
            string on = AtomTable.GetById(ord.AsAtomId)?.Name ?? "";
            if (on is not ("<" or "=" or ">"))
                throw new PrologRuntimeException("domain_error", "order", engine, ord);
        }
        int cmp = StandardOrderComparator.Compare(
            engine, engine.GetRegister(1), engine.GetRegister(2));
        string name = cmp < 0 ? "<" : cmp > 0 ? ">" : "=";
        int atomId = AtomTable.Intern(name, permanent: true).Id;
        return engine.UnifyRegisterWithCell(0, Cell.Atom(atomId));
    }

    public static bool TermLess(Activation engine) =>
        StandardOrderComparator.Compare(engine,
            engine.GetRegister(0), engine.GetRegister(1)) < 0;

    public static bool TermGreater(Activation engine) =>
        StandardOrderComparator.Compare(engine,
            engine.GetRegister(0), engine.GetRegister(1)) > 0;

    public static bool TermLessOrEqual(Activation engine) =>
        StandardOrderComparator.Compare(engine,
            engine.GetRegister(0), engine.GetRegister(1)) <= 0;

    public static bool TermGreaterOrEqual(Activation engine) =>
        StandardOrderComparator.Compare(engine,
            engine.GetRegister(0), engine.GetRegister(1)) >= 0;
}
