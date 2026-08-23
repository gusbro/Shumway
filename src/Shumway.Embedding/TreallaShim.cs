namespace Shumway.Embedding;

/// <summary>
/// The Trealla compat shim (ADR-040) — emulations of the '$...' C natives its
/// PURE-PROLOG library sources call, so a configured tree
/// (<c>-L trealla:dir</c>) can serve the libraries the engine does not cover
/// natively (lists' extras, ordsets, assoc, gensym, format's DCG entry
/// points, ...). Deliberately tiny: libraries whose Trealla version rides C
/// machinery the engine already provides natively (builtins, atts, iso_ext,
/// charsio, error, clpz) are marker-overridden instead — see
/// <c>NativeOverrideMarkers</c>.
/// </summary>
internal static class TreallaShim
{
    public const string LibraryName = "trealla";

    public const string Source = """
        :- public '$memberchk'/3.
        :- public help/2.

        % memberchk's partial-list core: Tail comes back NONVAR when E was
        % found in the proper prefix, or as the open tail itself when the
        % walk hit it (the caller decides whether to extend); a proper list
        % without E simply fails.
        '$memberchk'(E, Ls, Tail) :-
            (   var(Ls) -> Tail = Ls
            ;   Ls = [X|Xs] ->
                (   X = E -> Tail = []
                ;   '$memberchk'(E, Xs, Tail)
                )
            ;   fail
            ).

        % `:- help(Signature, Meta)` documentation directives, all over their
        % sources: accepted, ignored.
        help(_, _).
        """;
}
