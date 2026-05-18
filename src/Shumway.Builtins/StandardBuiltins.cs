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
        BuiltinsRegistry.Register("between", 3, ArithmeticBuiltins.Between);
        BuiltinsRegistry.Register("succ",    2, ArithmeticBuiltins.Succ);
        BuiltinsRegistry.Register("plus",    3, ArithmeticBuiltins.Plus);

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
        BuiltinsRegistry.Register("ground",  1, TypeBuiltins.IsGround);

        // I/O.
        BuiltinsRegistry.Register("write",      1, IOBuiltins.Write);
        BuiltinsRegistry.Register("nl",         0, IOBuiltins.Nl);
        BuiltinsRegistry.Register("writeln",    1, IOBuiltins.Writeln);
        BuiltinsRegistry.Register("write_term",      2, IOBuiltins.WriteTerm);
        BuiltinsRegistry.Register("format",          2, IOBuiltins.Format);
        BuiltinsRegistry.Register("write_canonical", 1, IOBuiltins.WriteCanonical);
        BuiltinsRegistry.Register("print",           1, IOBuiltins.Print);

        // Streams: write + read modes; format/3 stream-aware.
        BuiltinsRegistry.Register("open",      3, StreamBuiltins.Open);
        BuiltinsRegistry.Register("close",     1, StreamBuiltins.Close);
        BuiltinsRegistry.Register("write",     2, StreamBuiltins.WriteToStream);
        BuiltinsRegistry.Register("nl",        1, StreamBuiltins.NlOnStream);
        BuiltinsRegistry.Register("get_char",  2, StreamBuiltins.GetChar);
        BuiltinsRegistry.Register("peek_char", 2, StreamBuiltins.PeekChar);
        BuiltinsRegistry.Register("format",    3, IOBuiltins.Format3);

        // Atom / list manipulation.
        // length/2 and sub_atom/5 moved to the prelude (chunk 43) so they
        // get full multi-mode semantics — length(L, N) with both args
        // free now enumerates 0, 1, 2, …, and sub_atom/5 backtracks
        // through every (Before, Length, After, Sub) decomposition. The
        // C# logic survives as the enumerating $-helpers below.
        BuiltinsRegistry.Register("append",       3, AtomListBuiltins.Append);
        BuiltinsRegistry.Register("atom_codes",   2, AtomListBuiltins.AtomCodes);
        BuiltinsRegistry.Register("atom_concat",  3, AtomListBuiltins.AtomConcat);
        BuiltinsRegistry.Register("atom_length",  2, AtomCharBuiltins.AtomLength);
        BuiltinsRegistry.Register("atom_chars",   2, AtomCharBuiltins.AtomChars);
        BuiltinsRegistry.Register("char_code",    2, AtomCharBuiltins.CharCode);
        BuiltinsRegistry.Register("number_codes", 2, AtomCharBuiltins.NumberCodes);
        BuiltinsRegistry.Register("number_chars", 2, AtomCharBuiltins.NumberChars);
        BuiltinsRegistry.Register("atom_string",  2, AtomCharBuiltins.AtomString);

        // Multi-solution helpers (chunk 43) called from the prelude.
        BuiltinsRegistry.Register("$list_length",              2, MultiSolutionHelpers.ListLength);
        BuiltinsRegistry.Register("$make_var_list",            2, MultiSolutionHelpers.MakeVarList);
        BuiltinsRegistry.Register("$sub_atom_decompositions",  2, MultiSolutionHelpers.SubAtomDecompositions);

        // String-oriented builtins (chunk 40).
        BuiltinsRegistry.Register("string_length", 2, StringBuiltins.StringLength);
        BuiltinsRegistry.Register("string_concat", 3, StringBuiltins.StringConcat);
        BuiltinsRegistry.Register("string_chars",  2, StringBuiltins.StringChars);
        BuiltinsRegistry.Register("string_codes",  2, StringBuiltins.StringCodes);
        BuiltinsRegistry.Register("split_string",  4, StringBuiltins.SplitString);
        BuiltinsRegistry.Register("upcase_atom",   2, StringBuiltins.UpcaseAtom);
        BuiltinsRegistry.Register("downcase_atom", 2, StringBuiltins.DowncaseAtom);

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
        BuiltinsRegistry.Register("halt", 0, ControlBuiltins.Halt0);
        BuiltinsRegistry.Register("halt", 1, ControlBuiltins.Halt1);

        // List manipulation extras. member/2 is intentionally NOT here —
        // chunk 40 moved it to the Prolog prelude so it can enumerate
        // solutions via standard backtracking rather than being a one-shot
        // first-solution builtin. ListBuiltins.Member is kept as a
        // private helper for the moment but no longer reachable from
        // Prolog source.
        BuiltinsRegistry.Register("nth0",         3, ListBuiltins.Nth0);
        BuiltinsRegistry.Register("nth1",         3, ListBuiltins.Nth1);
        BuiltinsRegistry.Register("reverse",      2, ListBuiltins.Reverse);
        BuiltinsRegistry.Register("last",         2, ListBuiltins.Last);
        BuiltinsRegistry.Register("list_to_set",  2, ListBuiltins.ListToSet);
    }
}
