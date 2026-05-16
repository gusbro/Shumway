namespace Shumway.Builtins;

/// <summary>
/// Bootstrap for the standard Shumway builtins. <see cref="EnsureRegistered"/>
/// is idempotent — repeated calls have no effect after the first — so callers
/// (typically the <c>PrologEngine</c> constructor) can invoke it unconditionally
/// without worrying about double-registration. Builtins themselves are
/// registered through <see cref="BuiltinsRegistry"/>, which deduplicates by
/// functor.
///
/// <para>Phase 1 currently registers the four unification-comparison
/// predicates (<c>=/2</c>, <c>\=/2</c>, <c>==/2</c>, <c>\==/2</c>). Future
/// chunks will add arithmetic, type tests, I/O, and so on under the same
/// bootstrap.</para>
/// </summary>
public static class StandardBuiltins
{
    private static int _initialized;

    public static void EnsureRegistered()
    {
        // Cheap lock-free guard: the registry itself is thread-safe, but we
        // don't want every call to walk all registrations.
        if (System.Threading.Interlocked.Exchange(ref _initialized, 1) != 0)
            return;

        BuiltinsRegistry.Register("=",   2, UnifyBuiltins.Unify);
        BuiltinsRegistry.Register("\\=", 2, UnifyBuiltins.NotUnifiable);
        BuiltinsRegistry.Register("==",  2, UnifyBuiltins.StructurallyEqual);
        BuiltinsRegistry.Register("\\==",2, UnifyBuiltins.StructurallyNotEqual);

        // Arithmetic.
        BuiltinsRegistry.Register("is",  2, ArithmeticBuiltins.Is);
        BuiltinsRegistry.Register("=:=", 2, ArithmeticBuiltins.ArithEqual);
        BuiltinsRegistry.Register("=\\=",2, ArithmeticBuiltins.ArithNotEqual);
        BuiltinsRegistry.Register("<",   2, ArithmeticBuiltins.ArithLess);
        BuiltinsRegistry.Register(">",   2, ArithmeticBuiltins.ArithGreater);
        BuiltinsRegistry.Register("=<",  2, ArithmeticBuiltins.ArithLessOrEqual);
        BuiltinsRegistry.Register(">=",  2, ArithmeticBuiltins.ArithGreaterOrEqual);

        // Type tests.
        BuiltinsRegistry.Register("var",     1, TypeBuiltins.IsVar);
        BuiltinsRegistry.Register("nonvar",  1, TypeBuiltins.IsNonVar);
        BuiltinsRegistry.Register("atom",    1, TypeBuiltins.IsAtom);
        BuiltinsRegistry.Register("integer", 1, TypeBuiltins.IsInteger);
        BuiltinsRegistry.Register("float",   1, TypeBuiltins.IsFloat);
        BuiltinsRegistry.Register("number",  1, TypeBuiltins.IsNumber);
        BuiltinsRegistry.Register("atomic",  1, TypeBuiltins.IsAtomic);
        BuiltinsRegistry.Register("compound",1, TypeBuiltins.IsCompound);
        BuiltinsRegistry.Register("is_list", 1, TypeBuiltins.IsList);

        // I/O.
        BuiltinsRegistry.Register("write",   1, IOBuiltins.Write);
        BuiltinsRegistry.Register("nl",      0, IOBuiltins.Nl);
        BuiltinsRegistry.Register("writeln", 1, IOBuiltins.Writeln);

        // Atom / list manipulation.
        BuiltinsRegistry.Register("length",       2, AtomListBuiltins.Length);
        BuiltinsRegistry.Register("append",       3, AtomListBuiltins.Append);
        BuiltinsRegistry.Register("atom_codes",   2, AtomListBuiltins.AtomCodes);
        BuiltinsRegistry.Register("atom_concat",  3, AtomListBuiltins.AtomConcat);

        // Standard order of terms.
        BuiltinsRegistry.Register("compare", 3, StandardOrderBuiltins.Compare3);
        BuiltinsRegistry.Register("@<",      2, StandardOrderBuiltins.TermLess);
        BuiltinsRegistry.Register("@>",      2, StandardOrderBuiltins.TermGreater);
        BuiltinsRegistry.Register("@=<",     2, StandardOrderBuiltins.TermLessOrEqual);
        BuiltinsRegistry.Register("@>=",     2, StandardOrderBuiltins.TermGreaterOrEqual);

        // Sorting.
        BuiltinsRegistry.Register("sort",  2, SortBuiltins.Sort);
        BuiltinsRegistry.Register("msort", 2, SortBuiltins.Msort);

        // Control.
        BuiltinsRegistry.Register("fail", 0, ControlBuiltins.Fail);
        BuiltinsRegistry.Register("true", 0, ControlBuiltins.True);
    }
}
