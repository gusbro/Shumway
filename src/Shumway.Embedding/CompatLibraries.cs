namespace Shumway.Embedding;

/// <summary>
/// Built-in compatibility libraries for Scryer / Trealla Prolog programs,
/// loaded on demand by <c>use_module(library(Name))</c>. Each is ordinary
/// Prolog consulted into the global (user) namespace so a program written for
/// Scryer/Trealla — which imports these stdlib modules — consults unchanged.
///
/// <para>Three of them supply predicates Shumway does not have in its prelude:
/// <c>dcgs</c> (<c>seq//1</c>, <c>...//0</c>; <c>phrase/2,3</c> are already in
/// the prelude), <c>format</c> (<c>format_//2</c>, the DCG-format subset used
/// by real programs), and <c>dif</c> (a non-coroutining <c>dif/2</c>
/// approximation — see the note on it below). The remainder — <c>lists</c>,
/// <c>charsio</c>, and friends — name predicates Shumway already provides in
/// its prelude / builtins, so importing them is a no-op that simply marks the
/// module available (a genuinely unknown library name still raises
/// <c>existence_error(library, Name)</c> so typos surface).</para>
///
/// <para>These deliberately assume the Scryer default <c>double_quotes = chars</c>
/// for the arguments they inspect (the format string, terminal lists). The
/// libraries themselves are written with explicit quoted character atoms
/// (<c>'~'</c>, <c>'s'</c>) so they parse identically under any
/// <c>double_quotes</c> flag; a program that relies on them still needs to run
/// under <c>chars</c>, exactly as it does on Scryer/Trealla.</para>
/// </summary>
internal static class CompatLibraries
{
    /// <summary>Resolves a <c>library(Name)</c> import. Returns <c>true</c> for
    /// a known compatibility library, with <paramref name="source"/> set to its
    /// Prolog source (empty when the library is a no-op covered by the
    /// prelude). Returns <c>false</c> for an unknown library name.</summary>
    public static bool TryGet(string name, out string source)
    {
        source = name switch
        {
            "dcgs"   => Dcgs,
            "format" => Format,
            "dif"    => Dif,
            // Covered by Shumway's prelude / builtins — importing them is a
            // no-op that just marks the module available.
            "lists" or "charsio" or "error" or "iso_ext" or "between"
              or "apply" or "pio" or "si" or "debug" or "pairs"
              or "ordsets" or "assoc" or "dcg" or "dcg_basics" => "",
            _ => null!,
        };
        return source is not null;
    }

    // library(dcgs) — the generic DCG helpers. phrase/2,3 live in the prelude.
    private const string Dcgs = """
        :- public seq/3.
        seq([]) --> [].
        seq([X|Xs]) --> [X], seq(Xs).

        :- public '...'/2.
        '...' --> [].
        '...' --> [_], '...'.
        """;

    // library(dif) — a non-coroutining approximation. When the arguments are
    // decidably unequal it succeeds; when identical it fails; otherwise (they
    // could still unify, e.g. an unbound var vs a value) it optimistically
    // succeeds. The true dif/2 would delay; a program that later forces such a
    // pair equal would observe the difference. Sufficient for the common
    // "these are already bound / will never be unified" usage.
    private const string Dif = """
        :- public dif/2.
        dif(X, Y) :- ( X \= Y -> true ; X == Y -> fail ; true ).
        """;

    // library(format) — the DCG-format non-terminal format_//2. Supports the
    // directives real programs use: ~s (char/code list, spliced verbatim),
    // ~d (integer), ~a (atom), ~n (newline), ~~ (literal tilde); any other
    // character is emitted literally. Self-contained (does not depend on
    // library(dcgs) load order).
    private const string Format = """
        :- public format_/4.
        format_([], _) --> [].
        format_(['~', 's' | Fs], [A | As]) --> !, '$fmt_seq'(A), format_(Fs, As).
        format_(['~', 'd' | Fs], [A | As]) --> !,
            { number_chars(A, Cs) }, '$fmt_seq'(Cs), format_(Fs, As).
        format_(['~', 'a' | Fs], [A | As]) --> !,
            { atom_chars(A, Cs) }, '$fmt_seq'(Cs), format_(Fs, As).
        format_(['~', 'n' | Fs], As) --> !, ['\n'], format_(Fs, As).
        format_(['~', '~' | Fs], As) --> !, ['~'], format_(Fs, As).
        format_([C | Fs], As) --> [C], format_(Fs, As).

        '$fmt_seq'([]) --> [].
        '$fmt_seq'([C | Cs]) --> [C], '$fmt_seq'(Cs).
        """;
}
