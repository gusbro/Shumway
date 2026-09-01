% driver.pl — robust runner over the Schimpf harness.pl primitives.
% Reads every term itself (tail-recursive loop, no repeat/fail) and wraps each
% test in a catch-all, so one broken test cannot stop the count. Load AFTER
% harness.pl (it needs the harness operators and interpret_test/3).
%
% Entry points:
%   run_iso.        % the whole suite: iso.tst with the standard accommodations
%   run_file(File). % one .tst file, no accommodations

% The suite assumes double_quotes = codes (2013-era default) and opens its
% binary-stream section with close(bin, [force(true)]) before bin ever
% exists — an ECLiPSe idiom where force(true) swallows the existence error.
% Shumway (like SWI and Scryer) raises existence_error(stream, bin) there,
% so without a pre-opened alias the open never runs and 10 tests cascade.
run_iso :-
    set_prolog_flag(double_quotes, codes),
    catch(open(hello, read, _, [type(binary), alias(bin)]), _, true),
    run_file('iso.tst').

run_file(File) :-
    counter_set(test_count, 0),
    counter_set(non_test_count, 0),
    counter_set(succeeded_test_count, 0),
    counter_set(failed_test_count, 0),
    counter_set(skipped_test_count, 0),
    counter_set(broken_test_count, 0),
    counter_set(malformed_count, 0),
    current_output(Out),
    open(File, read, In),
    drive(In, Out, 0),
    close(In),
    counter_get(test_count, N),
    counter_get(succeeded_test_count, TN),
    counter_get(failed_test_count, FN),
    counter_get(skipped_test_count, SN),
    counter_get(broken_test_count, BN),
    counter_get(malformed_count, MN),
    counter_get(non_test_count, NN),
    nl(Out),
    write(Out, summary(total(N), ok(TN), failed(FN), skipped(SN),
                       broken(BN), malformed(MN), non_test(NN))),
    nl(Out).

drive(In, Out, N) :-
    catch(read(In, T), _E, T = '$synerr'),
    (   T == end_of_file
    ->  true
    ;   T == '$synerr'
    ->  counter_inc(malformed_count),
        drive(In, Out, N)
    ;   N1 is N + 1,
        counter_inc(test_count),
        (   catch(catch(interpret_test(T, N1, Out), continue, true), Ball,
                  ( write(Out, 'Test '), write(Out, N1),
                    write(Out, ': BROKEN escaped ball '), write(Out, Ball),
                    nl(Out),
                    counter_inc(broken_test_count) ))
        ->  true
        ;   write(Out, 'Test '), write(Out, N1),
            write(Out, ': BROKEN (test machinery failed)'), nl(Out),
            counter_inc(broken_test_count)
        ),
        drive(In, Out, N1)
    ).
