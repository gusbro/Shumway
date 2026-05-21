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

        const string Cmp = "Unification & comparison";
        BuiltinsRegistry.Register("=",   2, UnifyBuiltins.Unify,
            Cmp, "=(?Term1, ?Term2)", "Unifies the two terms.");
        BuiltinsRegistry.Register("\\=", 2, UnifyBuiltins.NotUnifiable,
            Cmp, "\\=(?Term1, ?Term2)", "Succeeds if the two terms do not unify.");
        BuiltinsRegistry.Register("==",  2, UnifyBuiltins.StructurallyEqual,
            Cmp, "==(@Term1, @Term2)", "Succeeds if the two terms are structurally identical.");
        BuiltinsRegistry.Register("\\==",2, UnifyBuiltins.StructurallyNotEqual,
            Cmp, "\\==(@Term1, @Term2)", "Succeeds if the two terms are not structurally identical.");

        // Arithmetic.
        const string Arith = "Arithmetic";
        BuiltinsRegistry.Register("is",  2, ArithmeticBuiltins.Is,
            Arith, "is(?Result, +Expr)", "Evaluates the arithmetic expression on the right and unifies it with the left.");
        BuiltinsRegistry.Register("=:=", 2, ArithmeticBuiltins.ArithEqual,
            Arith, "=:=(+Expr1, +Expr2)", "Succeeds if the two arithmetic expressions are equal.");
        BuiltinsRegistry.Register("=\\=",2, ArithmeticBuiltins.ArithNotEqual,
            Arith, "=\\=(+Expr1, +Expr2)", "Succeeds if the two arithmetic expressions are unequal.");
        BuiltinsRegistry.Register("<",   2, ArithmeticBuiltins.ArithLess,
            Arith, "<(+Expr1, +Expr2)", "Arithmetic less-than comparison.");
        BuiltinsRegistry.Register(">",   2, ArithmeticBuiltins.ArithGreater,
            Arith, ">(+Expr1, +Expr2)", "Arithmetic greater-than comparison.");
        BuiltinsRegistry.Register("=<",  2, ArithmeticBuiltins.ArithLessOrEqual,
            Arith, "=<(+Expr1, +Expr2)", "Arithmetic less-than-or-equal comparison.");
        BuiltinsRegistry.Register(">=",  2, ArithmeticBuiltins.ArithGreaterOrEqual,
            Arith, ">=(+Expr1, +Expr2)", "Arithmetic greater-than-or-equal comparison.");
        BuiltinsRegistry.Register("between", 3, ArithmeticBuiltins.Between,
            Arith, "between(+Low, +High, ?X)", "Succeeds when X is in the inclusive integer range; enumerates it when unbound.");
        BuiltinsRegistry.Register("succ",    2, ArithmeticBuiltins.Succ,
            Arith, "succ(?Int1, ?Int2)", "Relates a non-negative integer to its successor, in either direction.");
        BuiltinsRegistry.Register("plus",    3, ArithmeticBuiltins.Plus,
            Arith, "plus(?Int1, ?Int2, ?Sum)", "Relates Int1 + Int2 = Sum, solving for whichever single argument is unbound.");

        // Type tests.
        const string Types = "Type checking";
        BuiltinsRegistry.Register("var",     1, TypeBuiltins.IsVar,
            Types, "var(@Term)", "Succeeds if the argument is an unbound variable.");
        BuiltinsRegistry.Register("nonvar",  1, TypeBuiltins.IsNonVar,
            Types, "nonvar(@Term)", "Succeeds if the argument is not an unbound variable.");
        BuiltinsRegistry.Register("atom",    1, TypeBuiltins.IsAtom,
            Types, "atom(@Term)", "Succeeds if the argument is an atom.");
        BuiltinsRegistry.Register("integer", 1, TypeBuiltins.IsInteger,
            Types, "integer(@Term)", "Succeeds if the argument is an integer.");
        BuiltinsRegistry.Register("float",   1, TypeBuiltins.IsFloat,
            Types, "float(@Term)", "Succeeds if the argument is a float.");
        BuiltinsRegistry.Register("number",  1, TypeBuiltins.IsNumber,
            Types, "number(@Term)", "Succeeds if the argument is a number.");
        BuiltinsRegistry.Register("atomic",  1, TypeBuiltins.IsAtomic,
            Types, "atomic(@Term)", "Succeeds if the argument is atomic (atom, number or string).");
        BuiltinsRegistry.Register("compound",1, TypeBuiltins.IsCompound,
            Types, "compound(@Term)", "Succeeds if the argument is a compound term.");
        BuiltinsRegistry.Register("is_list", 1, TypeBuiltins.IsList,
            Types, "is_list(@Term)", "Succeeds if the argument is a proper list.");
        BuiltinsRegistry.Register("ground",  1, TypeBuiltins.IsGround,
            Types, "ground(@Term)", "Succeeds if the argument contains no unbound variables.");
        BuiltinsRegistry.Register("attvar",  1, TypeBuiltins.IsAttVar,
            Types, "attvar(@Term)", "Succeeds if the argument is an attributed variable.");

        // Attributed variables (chunk 77, Phase 4).
        const string Attr = "Attributed variables";
        BuiltinsRegistry.Register("put_attr", 3, AttvarBuiltins.PutAttr,
            Attr, "put_attr(+Var, +Module, +Value)", "Attaches (or replaces) a module's attribute on a variable.");
        BuiltinsRegistry.Register("get_attr", 3, AttvarBuiltins.GetAttr,
            Attr, "get_attr(+Var, +Module, -Value)", "Reads a module's attribute from a variable.");
        BuiltinsRegistry.Register("del_attr", 2, AttvarBuiltins.DelAttr,
            Attr, "del_attr(+Var, +Module)", "Removes a module's attribute from a variable.");

        // I/O.
        const string Io = "Input / output";
        BuiltinsRegistry.Register("write",      1, IOBuiltins.Write,
            Io, "write(+Term)", "Writes a term to the current output stream.");
        BuiltinsRegistry.Register("nl",         0, IOBuiltins.Nl,
            Io, "nl", "Writes a newline to the current output stream.");
        BuiltinsRegistry.Register("writeln",    1, IOBuiltins.Writeln,
            Io, "writeln(+Term)", "Writes a term followed by a newline.");
        BuiltinsRegistry.Register("write_term",      2, IOBuiltins.WriteTerm,
            Io, "write_term(+Term, +Options)", "Writes a term honouring the given list of write options.");
        BuiltinsRegistry.Register("format",          2, IOBuiltins.Format,
            Io, "format(+Format, +Arguments)", "Writes formatted output from a control string and an argument list.");
        BuiltinsRegistry.Register("write_canonical", 1, IOBuiltins.WriteCanonical,
            Io, "write_canonical(+Term)", "Writes a term in a quoted, operator-free form that reads back.");
        BuiltinsRegistry.Register("print",           1, IOBuiltins.Print,
            Io, "print(+Term)", "Writes a term using print conventions.");

        // Streams: write + read modes; format/3 stream-aware.
        BuiltinsRegistry.Register("open",      3, StreamBuiltins.Open,
            Io, "open(+File, +Mode, -Stream)", "Opens a file as a stream handle.");
        BuiltinsRegistry.Register("close",     1, StreamBuiltins.Close,
            Io, "close(+Stream)", "Closes an open stream.");
        BuiltinsRegistry.Register("write",     2, StreamBuiltins.WriteToStream,
            Io, "write(+Stream, +Term)", "Writes a term to the given stream.");
        BuiltinsRegistry.Register("nl",        1, StreamBuiltins.NlOnStream,
            Io, "nl(+Stream)", "Writes a newline to the given stream.");
        BuiltinsRegistry.Register("get_char",  2, StreamBuiltins.GetChar,
            Io, "get_char(+Stream, -Char)", "Reads and consumes one character from a stream.");
        BuiltinsRegistry.Register("peek_char", 2, StreamBuiltins.PeekChar,
            Io, "peek_char(+Stream, -Char)", "Peeks the next character of a stream without consuming it.");
        BuiltinsRegistry.Register("format",    3, IOBuiltins.Format3,
            Io, "format(+Stream, +Format, +Arguments)", "Writes formatted output to the given stream.");

        // Atom / list manipulation.
        // length/2 and sub_atom/5 moved to the prelude (chunk 43) so they
        // get full multi-mode semantics — length(L, N) with both args
        // free now enumerates 0, 1, 2, …, and sub_atom/5 backtracks
        // through every (Before, Length, After, Sub) decomposition. The
        // C# logic survives as the enumerating $-helpers below.
        const string Lists = "Lists";
        const string Strings = "Atoms & strings";
        BuiltinsRegistry.Register("append",       3, AtomListBuiltins.Append,
            Lists, "append(?List1, ?List2, ?List)", "Concatenates List1 and List2 into List; backtracks over splits of List.");
        BuiltinsRegistry.Register("atom_codes",   2, AtomListBuiltins.AtomCodes,
            Strings, "atom_codes(?Atom, ?Codes)", "Converts between an atom and its list of character codes.");
        BuiltinsRegistry.Register("atom_concat",  3, AtomListBuiltins.AtomConcat,
            Strings, "atom_concat(?Atom1, ?Atom2, ?Atom)", "Concatenates Atom1 and Atom2 into Atom; backtracks over splits of Atom.");
        BuiltinsRegistry.Register("atom_length",  2, AtomCharBuiltins.AtomLength,
            Strings, "atom_length(+Atom, ?Length)", "Relates an atom to its length in characters.");
        BuiltinsRegistry.Register("atom_chars",   2, AtomCharBuiltins.AtomChars,
            Strings, "atom_chars(?Atom, ?Chars)", "Converts between an atom and its list of one-character atoms.");
        BuiltinsRegistry.Register("char_code",    2, AtomCharBuiltins.CharCode,
            Strings, "char_code(?Char, ?Code)", "Relates a one-character atom to its character code.");
        BuiltinsRegistry.Register("number_codes", 2, AtomCharBuiltins.NumberCodes,
            Strings, "number_codes(?Number, ?Codes)", "Converts between a number and its list of character codes.");
        BuiltinsRegistry.Register("number_chars", 2, AtomCharBuiltins.NumberChars,
            Strings, "number_chars(?Number, ?Chars)", "Converts between a number and its list of one-character atoms.");
        BuiltinsRegistry.Register("atom_string",  2, AtomCharBuiltins.AtomString,
            Strings, "atom_string(?Atom, ?String)", "Converts between an atom and a string.");
        BuiltinsRegistry.Register("atom_number",  2, AtomCharBuiltins.AtomNumber,
            Strings, "atom_number(?Atom, ?Number)", "Converts between an atom and the number it denotes; fails if the atom is not numeric.");
        BuiltinsRegistry.Register("number_string",2, AtomCharBuiltins.NumberString,
            Strings, "number_string(?Number, ?String)", "Converts between a number and its string representation; fails if the string is not numeric.");

        // Multi-solution helpers (chunk 43) called from the prelude.
        BuiltinsRegistry.Register("$list_length",              2, MultiSolutionHelpers.ListLength);
        BuiltinsRegistry.Register("$make_var_list",            2, MultiSolutionHelpers.MakeVarList);
        BuiltinsRegistry.Register("$sub_atom_decompositions",  2, MultiSolutionHelpers.SubAtomDecompositions);

        // String-oriented builtins (chunk 40).
        BuiltinsRegistry.Register("string_length", 2, StringBuiltins.StringLength,
            Strings, "string_length(+String, ?Length)", "Relates a string to its length in characters.");
        BuiltinsRegistry.Register("string_concat", 3, StringBuiltins.StringConcat,
            Strings, "string_concat(?String1, ?String2, ?String)", "Concatenates String1 and String2 into String.");
        BuiltinsRegistry.Register("string_chars",  2, StringBuiltins.StringChars,
            Strings, "string_chars(?String, ?Chars)", "Converts between a string and its list of one-character atoms.");
        BuiltinsRegistry.Register("string_codes",  2, StringBuiltins.StringCodes,
            Strings, "string_codes(?String, ?Codes)", "Converts between a string and its list of character codes.");
        BuiltinsRegistry.Register("split_string",  4, StringBuiltins.SplitString,
            Strings, "split_string(+String, +SepChars, +PadChars, -SubStrings)", "Splits a string on separator characters, trimming pad characters.");
        BuiltinsRegistry.Register("upcase_atom",   2, StringBuiltins.UpcaseAtom,
            Strings, "upcase_atom(+Atom, -Upper)", "Relates an atom to its upper-cased form.");
        BuiltinsRegistry.Register("downcase_atom", 2, StringBuiltins.DowncaseAtom,
            Strings, "downcase_atom(+Atom, -Lower)", "Relates an atom to its lower-cased form.");

        // Standard order of terms.
        const string Order = "Term ordering";
        BuiltinsRegistry.Register("compare", 3, StandardOrderBuiltins.Compare3,
            Order, "compare(?Order, @Term1, @Term2)", "Unifies Order with the relation (<, = or >) between the two terms.");
        BuiltinsRegistry.Register("@<",      2, StandardOrderBuiltins.TermLess,
            Order, "@<(@Term1, @Term2)", "Standard-order-of-terms less-than comparison.");
        BuiltinsRegistry.Register("@>",      2, StandardOrderBuiltins.TermGreater,
            Order, "@>(@Term1, @Term2)", "Standard-order-of-terms greater-than comparison.");
        BuiltinsRegistry.Register("@=<",     2, StandardOrderBuiltins.TermLessOrEqual,
            Order, "@=<(@Term1, @Term2)", "Standard-order-of-terms less-than-or-equal comparison.");
        BuiltinsRegistry.Register("@>=",     2, StandardOrderBuiltins.TermGreaterOrEqual,
            Order, "@>=(@Term1, @Term2)", "Standard-order-of-terms greater-than-or-equal comparison.");

        // Sorting.
        BuiltinsRegistry.Register("sort",  2, SortBuiltins.Sort,
            Lists, "sort(+List, -Sorted)", "Sorts a list into standard order, removing duplicates.");
        BuiltinsRegistry.Register("msort", 2, SortBuiltins.Msort,
            Lists, "msort(+List, -Sorted)", "Sorts a list into standard order, keeping duplicates.");

        // Control.
        const string Control = "Control";
        BuiltinsRegistry.Register("fail", 0, ControlBuiltins.Fail,
            Control, "fail", "Always fails.");
        BuiltinsRegistry.Register("true", 0, ControlBuiltins.True,
            Control, "true", "Always succeeds.");
        BuiltinsRegistry.Register("halt", 0, ControlBuiltins.Halt0,
            Control, "halt", "Halts the engine with exit code 0.");
        BuiltinsRegistry.Register("halt", 1, ControlBuiltins.Halt1,
            Control, "halt(+Status)", "Halts the engine with the given exit code.");

        // List manipulation extras. member/2 is intentionally NOT here —
        // chunk 40 moved it to the Prolog prelude so it can enumerate
        // solutions via standard backtracking rather than being a one-shot
        // first-solution builtin. ListBuiltins.Member is kept as a
        // private helper for the moment but no longer reachable from
        // Prolog source.
        BuiltinsRegistry.Register("nth0",         3, ListBuiltins.Nth0,
            Lists, "nth0(?Index, ?List, ?Elem)", "Relates a 0-based index to the list element at that position.");
        BuiltinsRegistry.Register("nth1",         3, ListBuiltins.Nth1,
            Lists, "nth1(?Index, ?List, ?Elem)", "Relates a 1-based index to the list element at that position.");
        BuiltinsRegistry.Register("reverse",      2, ListBuiltins.Reverse,
            Lists, "reverse(?List, ?Reversed)", "Relates a list to its reverse.");
        BuiltinsRegistry.Register("last",         2, ListBuiltins.Last,
            Lists, "last(?List, ?Last)", "Relates a list to its last element.");
        BuiltinsRegistry.Register("list_to_set",  2, ListBuiltins.ListToSet,
            Lists, "list_to_set(+List, -Set)", "Removes duplicates from a list, keeping the first occurrence of each.");
    }
}
