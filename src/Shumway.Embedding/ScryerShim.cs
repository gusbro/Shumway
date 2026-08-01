namespace Shumway.Embedding;

/// <summary>ADR-040 — the Scryer compat shim. Auto-loaded the first time a
/// scryer-dialect module is loaded. Where the SWI shim supplies SWI system
/// predicates, this one mostly supplies <b>emulations of Scryer's Rust-VM
/// native instructions</b> (the <c>'$...'</c> calls its libraries bottom out
/// in) as bare-global predicates: a Scryer library's unresolved bare or
/// <c>builtins:</c>-qualified call falls through to the bare-global namespace,
/// so providing the native's contract there lets the library's own pure Prolog
/// run unmodified (random.pl, uuid.pl, files.pl, os.pl, charsio's char_type).
/// </summary>
internal static class ScryerShim
{
    public const string Source = """
        % ----- builtins.pl internals -----
        % Scryer's bootstrap module is implicitly visible in every module on
        % their VM; libraries call its helpers bare (arithmetic.pl).
        :- public must_be_number/2.
        must_be_number(N, _)   :- number(N), !.
        must_be_number(N, Ctx) :- var(N), !, throw(error(instantiation_error, Ctx)).
        must_be_number(N, Ctx) :- throw(error(type_error(number, N), Ctx)).
        % can_be = "is or could still become" (a variable passes).
        :- public can_be_number/2.
        can_be_number(N, _)   :- ( var(N) -> true ; number(N) ), !.
        can_be_number(N, Ctx) :- throw(error(type_error(number, N), Ctx)).

        % ----- random natives -----
        % random.pl: maybe/0, random/1, random_integer/3 (Upper EXCLUSIVE).
        % NOTE for every emulation here: call builtins via names no Scryer
        % library exports (or the $sys_* aliases) — imports win over builtins,
        % so calling e.g. random/1 by name after random.pl loads would resolve
        % back INTO the library this shim serves (the getenv loop).
        :- public '$maybe'/0.
        '$maybe' :- random_between(0, 1, B), B =:= 0.
        :- public '$random_integer'/3.
        '$random_integer'(Lower, Upper, R) :-
            Upper1 is Upper - 1,
            random_between(Lower, Upper1, R).

        % crypto.pl's random source, which uuid.pl builds uuidv4 on. NOT
        % cryptographically secure — a seedable PRNG, fine for uuids and
        % simulation, NOT for key material.
        :- public '$crypto_random_byte'/1.
        '$crypto_random_byte'(B) :- random_between(0, 255, B).

        % ----- os natives -----
        % Scryer text is chars lists; our builtins take atoms.
        '$scry_atom'(X, A) :- ( atom(X) -> A = X ; atom_chars(A, X) ).
        :- public '$getenv'/2.
        '$getenv'(Key, Value) :-
            '$scry_atom'(Key, KeyA),
            '$sys_getenv'(KeyA, ValueA),
            atom_chars(ValueA, Value).

        % ----- files natives -----
        :- public '$file_exists'/1.
        '$file_exists'(P) :- '$scry_atom'(P, A), exists_file(A).
        :- public '$directory_exists'/1.
        '$directory_exists'(P) :- '$scry_atom'(P, A), exists_directory(A).
        :- public '$delete_file'/1.
        '$delete_file'(P) :- '$scry_atom'(P, A), delete(A).
        :- public '$rename_file'/2.
        '$rename_file'(P0, P) :- '$scry_atom'(P0, A0), '$scry_atom'(P, A), rename(A0, A).
        :- public '$make_directory'/1.
        '$make_directory'(P) :- '$scry_atom'(P, A), mkdir(A).
        :- public '$delete_directory'/1.
        '$delete_directory'(P) :- '$scry_atom'(P, A), rmdir(A).
        :- public '$directory_separator'/1.
        '$directory_separator'('/').
        :- public '$make_directory_path'/1.
        '$make_directory_path'(P) :- '$scry_atom'(P, A), '$mk_path'(A).
        '$mk_path'(A) :-
            ( exists_directory(A) -> true
            ;   ( '$parent_dir'(A, Parent) -> '$mk_path'(Parent) ; true ),
                mkdir(A)
            ).
        % parent = up to the LAST path separator (either kind), if any remains.
        '$parent_dir'(A, Parent) :-
            atom_chars(A, Cs),
            '$split_last_sep'(Cs, ParentCs),
            ParentCs \= [],
            atom_chars(Parent, ParentCs).
        '$split_last_sep'(Cs, Parent) :-
            append(Parent, [Sep | Rest], Cs),
            ( Sep == ('/') ; Sep == ('\\') ),
            \+ ( append(_, [S2 | _], Rest), ( S2 == ('/') ; S2 == ('\\') ) ),
            !.
        :- public '$working_directory'/2.
        '$working_directory'(Old, New) :-
            '$sys_working_directory'(OldA, OldA),
            atom_chars(OldA, Old),
            ( var(New) -> New = Old
            ;   '$scry_atom'(New, NewA),
                ( NewA == OldA -> true ; '$sys_working_directory'(_, NewA) )
            ).
        :- public '$directory_files'/2.
        '$directory_files'(D, Files) :-
            '$scry_atom'(D, A),
            findall(NC, ( directory(A, N, _, _, _, _), atom_chars(N, NC) ), Files).

        % ----- charsio's char_type native -----
        % Scryer's category vocabulary mapped onto Shumway's char_type/2 and
        % code ranges. lower(L)/upper(U) carry the case-mapped CHARS list.
        % Self-contained (no char_type/2 call: after charsio loads, that name is
        % import-shadowed by charsio$char_type, whose implementation is THIS
        % dispatch — calling it back would loop). ASCII ranges + "non-ASCII is
        % alphabetic" as the unicode approximation.
        :- public '$char_type'/2.
        '$char_type'(C, T) :- char_code(C, Code), '$scry_ctype'(T, C, Code).
        '$scry_alpha'(Code) :-
            (   Code >= 0'a, Code =< 0'z -> true
            ;   Code >= 0'A, Code =< 0'Z -> true
            ;   Code > 127
            ).
        '$scry_ctype'(alnum, _, Code)        :- ( '$scry_alpha'(Code) -> true ; Code >= 0'0, Code =< 0'9 ).
        '$scry_ctype'(alpha, _, Code)        :- '$scry_alpha'(Code).
        '$scry_ctype'(alphabetic, _, Code)   :- '$scry_alpha'(Code).
        '$scry_ctype'(alphanumeric, C, Code) :- '$scry_ctype'(alnum, C, Code).
        '$scry_ctype'(ascii, _, Code)        :- Code < 128.
        '$scry_ctype'(ascii_graphic, _, Code) :- Code >= 33, Code =< 126.
        '$scry_ctype'(ascii_punctuation, _, Code) :-
            Code >= 33, Code =< 126,
            \+ '$scry_alpha'(Code),
            \+ ( Code >= 0'0, Code =< 0'9 ).
        '$scry_ctype'(binary_digit, _, Code) :- ( Code =:= 0'0 ; Code =:= 0'1 ).
        '$scry_ctype'(control, _, Code)      :- ( Code < 32 ; Code =:= 127 ).
        '$scry_ctype'(decimal_digit, _, Code) :- Code >= 0'0, Code =< 0'9.
        '$scry_ctype'(exponent, _, Code)     :- ( Code =:= 0'e ; Code =:= 0'E ).
        '$scry_ctype'(graphic, _, Code)      :- memberchk(Code, [0'#, 0'$, 0'&, 0'*, 0'+, 0'-, 0'., 0'/, 0':, 0'<, 0'=, 0'>, 0'?, 0'@, 0'^, 0'~, 0'\\]).
        '$scry_ctype'(graphic_token, C, Code) :- '$scry_ctype'(graphic, C, Code).
        '$scry_ctype'(hexadecimal_digit, _, Code) :-
            (   Code >= 0'0, Code =< 0'9 -> true
            ;   Code >= 0'a, Code =< 0'f -> true
            ;   Code >= 0'A, Code =< 0'F
            ).
        '$scry_ctype'(layout, _, Code)       :- ( Code =:= 32 ; Code >= 9, Code =< 13 ).
        '$scry_ctype'(lower, _, Code)        :- Code >= 0'a, Code =< 0'z.
        '$scry_ctype'(meta, _, Code)         :- memberchk(Code, [0'\\, 0''', 0'", 0'`]).
        '$scry_ctype'(numeric, _, Code)      :- Code >= 0'0, Code =< 0'9.
        '$scry_ctype'(octal_digit, _, Code)  :- Code >= 0'0, Code =< 0'7.
        '$scry_ctype'(octet, _, Code)        :- Code =< 255.
        '$scry_ctype'(sign, _, Code)         :- ( Code =:= 0'+ ; Code =:= 0'- ).
        '$scry_ctype'(solo, _, Code)         :- memberchk(Code, [0'!, 0'(, 0'), 0',, 0';, 0'[, 0'], 0'{, 0'}, 0'|, 0'%]).
        '$scry_ctype'(upper, _, Code)        :- Code >= 0'A, Code =< 0'Z.
        '$scry_ctype'(whitespace, _, Code)   :- ( Code =:= 32 ; Code >= 9, Code =< 13 ).
        '$scry_ctype'(lower(Ls), C, _)       :- downcase_atom(C, D), D \== C, atom_chars(D, Ls).
        '$scry_ctype'(upper(Us), C, _)       :- upcase_atom(C, U), U \== C, atom_chars(U, Us).
        """;
}
