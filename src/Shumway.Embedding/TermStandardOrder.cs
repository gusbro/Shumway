using Shumway.Core;
using Shumway.Compiler.Ast;

namespace Shumway.Embedding;

/// <summary>
/// AST-level mirror of <see cref="Shumway.Builtins.StandardOrderComparator"/>.
/// Used by collection meta-builtins (<c>setof/3</c>) that have AST
/// <see cref="Term"/>s in hand rather than heap cells, and want to sort or
/// dedup them by the same standard-order-of-terms rules a user would observe
/// from <c>compare/3</c>.
///
/// <para>Standard order, top to bottom:</para>
/// <list type="number">
/// <item>Variables — ordered by their string name. For terms that came out
///   of <see cref="TermReader.Materialize"/> the names embed the heap
///   index (e.g. <c>_G42</c>), so name order tracks address order.</item>
/// <item>Numbers — by numeric value, with float &lt; integer on a tie.</item>
/// <item>Atoms — by name.</item>
/// <item>Strings — by content.</item>
/// <item>Compound terms — by arity, then functor name, then args
///   left-to-right.</item>
/// </list>
/// </summary>
public static class TermStandardOrder
{
    public static int Compare(Term a, Term b)
    {
        int aOrder = TypeOrder(a);
        int bOrder = TypeOrder(b);
        if (aOrder != bOrder) return aOrder.CompareTo(bOrder);

        return aOrder switch
        {
            0 => string.CompareOrdinal(((VarTerm)a).Name, ((VarTerm)b).Name),
            1 => CompareNumbers(a, b),
            2 => Utf16Text.CompareCodePointOrder(((AtomTerm)a).Name, ((AtomTerm)b).Name),
            3 => Utf16Text.CompareCodePointOrder(((StringTerm)a).Content, ((StringTerm)b).Content),
            4 => CompareCompounds((CompoundTerm)a, (CompoundTerm)b),
            _ => 0,
        };
    }

    private static int TypeOrder(Term t) => t switch
    {
        VarTerm _ => 0,
        IntTerm _ or FloatTerm _ => 1,
        AtomTerm _ => 2,
        StringTerm _ => 3,
        CompoundTerm _ => 4,
        _ => 5,
    };

    private static int CompareNumbers(Term a, Term b)
    {
        double aVal = a is FloatTerm fa ? fa.Value : ((IntTerm)a).Value;
        double bVal = b is FloatTerm fb ? fb.Value : ((IntTerm)b).Value;
        int cmp = aVal.CompareTo(bVal);
        if (cmp != 0) return cmp;
        // Tie-break by type — ISO puts float below integer on equal values.
        bool aFloat = a is FloatTerm;
        bool bFloat = b is FloatTerm;
        if (aFloat && !bFloat) return -1;
        if (!aFloat && bFloat) return 1;
        return 0;
    }

    private static int CompareCompounds(CompoundTerm a, CompoundTerm b)
    {
        int cmp = a.Args.Length.CompareTo(b.Args.Length);
        if (cmp != 0) return cmp;
        cmp = Utf16Text.CompareCodePointOrder(a.Functor, b.Functor);
        if (cmp != 0) return cmp;
        for (int i = 0; i < a.Args.Length; i++)
        {
            cmp = Compare(a.Args[i], b.Args[i]);
            if (cmp != 0) return cmp;
        }
        return 0;
    }
}
