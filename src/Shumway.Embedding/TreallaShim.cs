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
        % walk hit it (their wrapper decides whether to extend). NOT a
        % delegation to memberchk/2: their lists module IMPORT rebinds the
        % bare name in user scope at dispatch time, so a shim body calling
        % memberchk/2 re-enters their wrapper — which calls this — forever.
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

        % Trealla builtin names over the engine's own (ADR-040: the shim IS
        % the mapping — the engine surface does not occupy dialect names).
        :- public limit/2.
        limit(N, Goal) :- call_with_limit(N, Goal).
        :- public offset/2.
        offset(N, Goal) :- call_with_offset(N, Goal).
        :- public load_text/2.
        load_text(Text, _Options) :- consult_text(Text).
        :- public srandom/1.
        srandom(Seed) :- set_seed(Seed).

        % Trealla's 4-arg must_be BIF — must_be(Value, Type, Context, _),
        % VALUE FIRST (the reverse of the common must_be/2). Their library
        % sources (arithmetic, builtins' predicate_property) validate
        % through it; errors ride the engine's must_be/2 for identical
        % error terms.
        :- public must_be/4.
        must_be(Value, Type, _Context, _) :- must_be(Type, Value).

        % crypto_n_random_bytes(+N, -Bytes) — their crypto BIF, the entropy
        % source uuid's uuidv4/1 rides. Not cryptographically strong here
        % (random_between over the engine PRNG); uuids are well-formed and
        % unique-enough for the library's uses.
        :- public crypto_n_random_bytes/2.
        crypto_n_random_bytes(N, Bytes) :-
            must_be(integer, N),
            length(Bytes, N),
            crypto_fill_bytes(Bytes).
        crypto_fill_bytes([]).
        crypto_fill_bytes([B|Bs]) :-
            random_between(0, 255, B),
            crypto_fill_bytes(Bs).

        % hex_bytes(?HexChars, ?Bytes) — their crypto library's hex codec
        % (uuid_string rides it). Lowercase hex chars, two per byte.
        :- public hex_bytes/2.
        hex_bytes(Hex, Bytes) :-
            (   var(Hex) ->
                bytes_hex_chars(Bytes, Hex)
            ;   hex_chars_bytes(Hex, Bytes)
            ).
        bytes_hex_chars([], []).
        bytes_hex_chars([B|Bs], [H, L|Hs]) :-
            Hi is B >> 4, Lo is B /\ 15,
            hex_digit_char(Hi, H), hex_digit_char(Lo, L),
            bytes_hex_chars(Bs, Hs).
        hex_chars_bytes([], []).
        hex_chars_bytes([H, L|Hs], [B|Bs]) :-
            hex_digit_char(Hi, H), hex_digit_char(Lo, L),
            B is Hi << 4 \/ Lo,
            hex_chars_bytes(Hs, Bs).
        hex_digit_char(0, '0'). hex_digit_char(1, '1').
        hex_digit_char(2, '2'). hex_digit_char(3, '3').
        hex_digit_char(4, '4'). hex_digit_char(5, '5').
        hex_digit_char(6, '6'). hex_digit_char(7, '7').
        hex_digit_char(8, '8'). hex_digit_char(9, '9').
        hex_digit_char(10, a). hex_digit_char(11, b).
        hex_digit_char(12, c). hex_digit_char(13, d).
        hex_digit_char(14, e). hex_digit_char(15, f).
        """;
}
