namespace Shumway.Embedding;

/// <summary>
/// Pure I/O — running a DCG over a stream's text without reading the whole
/// thing first. An <em>opt-in</em> module, loaded by
/// <see cref="PrologEngine.UseLazyInput"/> or by
/// <c>use_module(library(lazy_input))</c>. The technique is Scryer's
/// <c>library(pio)</c>; the name is not, because Scryer and SWI both ship a
/// <c>pio</c> and their contents differ from each other and from this.
///
/// <para>The input is a packed list whose tail is a frozen variable (ADR-047).
/// A grammar walks the window it already has, allocating nothing per element,
/// and only when it reaches the tail does the frozen goal wake and read the
/// next one. It is a library rather than a prelude predicate because it needs
/// <c>freeze/2</c>, which lives in the coroutining library — a library cannot
/// be loaded from inside the query that wants it, so the dependency has to be
/// declared here.</para>
///
/// <para>Reading goes through the stream's own reader, so the encoding declared
/// in <c>open/4</c> and the ADR-045 newline translation both apply.</para>
///
/// <para><b>Memory is not bounded yet.</b> The lazy list's tails are attributed
/// variables, and the heap collector stands down while any of those is live, so
/// consumed windows accumulate. See ADR-047; the fix is a heap-GC arc, not a
/// change here.</para>
/// </summary>
internal static class LazyInput
{
    public const string ModuleName = "lazy_input";

    public const string Source = """
        :- module(lazy_input).
        :- use_module(library(coroutining)).

        :- public phrase_from_stream/2.
        :- public phrase_from_stream/3.
        :- public phrase_from_file/2.
        :- public phrase_from_file/3.
        :- meta_predicate(phrase_from_stream(2, *)).
        :- meta_predicate(phrase_from_stream(2, *, *)).
        :- meta_predicate(phrase_from_file(2, *)).
        :- meta_predicate(phrase_from_file(2, *, *)).

        %! phrase_from_stream(:Body, +Stream) | Grammar | Runs the DCG Body over Stream's text, read lazily in windows.
        phrase_from_stream(Body, Stream) :-
            phrase_from_stream(Body, Stream, chars).

        %! phrase_from_stream(:Body, +Stream, +Kind) | Grammar | As phrase_from_stream/2, with Kind (chars or codes) choosing the list's elements.
        phrase_from_stream(Body, Stream, Kind) :-
            '$lazy_text'(Stream, Kind, 0, Ls),
            phrase(Body, Ls).

        %! phrase_from_file(:Body, +File) | Grammar | Runs the DCG Body over File's text, read lazily; the file is closed on the way out.
        phrase_from_file(Body, File) :-
            phrase_from_file(Body, File, []).

        %! phrase_from_file(:Body, +File, +Options) | Grammar | As phrase_from_file/2; Options are open/4's, plus text_kind(chars) or text_kind(codes).
        phrase_from_file(Body, File, Options) :-
            '$lazy_kind'(Options, Kind, OpenOptions),
            setup_call_cleanup(open(File, read, Stream, OpenOptions),
                               phrase_from_stream(Body, Stream, Kind),
                               close(Stream)).

        % The frozen goal runs AFTER the binding that woke it, so Ls already
        % holds whatever the grammar unified it with. That is why the step
        % UNIFIES Ls with the window rather than binding it: the grammar's
        % [H|T] meets the packed window and peels one element out of it.
        %
        % The offset is carried explicitly because reading is a side effect
        % backtracking cannot undo: a grammar that tries one clause, fails and
        % tries the next wakes the SAME cell twice, and a plain read would hand
        % it the next characters the second time — quietly parsing an input the
        % file does not contain. '$lazy_window'/6 is idempotent per offset.
        '$lazy_text'(Stream, Kind, Offset, Ls) :-
            freeze(Ls, '$lazy_text_step'(Stream, Kind, Offset, Ls)).

        '$lazy_text_step'(Stream, Kind, Offset, Ls) :-
            '$lazy_window'(Stream, Offset, 4096, Kind, Window, Length),
            ( Length =:= 0 ->
                Ls = []
            ; Next is Offset + Length,
              '$lazy_text'(Stream, Kind, Next, Ls0),
              partial_string(Window, Ls, Ls0)
            ).

        '$lazy_kind'([], chars, []).
        '$lazy_kind'([text_kind(K)|T], K, Rest) :- !, '$lazy_kind'(T, _, Rest).
        '$lazy_kind'([O|T], K, [O|Rest]) :- '$lazy_kind'(T, K, Rest).
        """;
}
