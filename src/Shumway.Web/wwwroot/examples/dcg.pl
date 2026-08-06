% Definite clause grammars: Prolog's parser notation. A grammar rule is a
% clause, so parsing and generating are the same program read two ways.
% Try:  phrase(expr(V), "12+3*4").      V is the value
%       phrase(sentence, [the,cat,sees,a,dog]).

% --- arithmetic over a string of characters, with precedence -------------
expr(V)    --> term(T), expr_rest(T, V).
expr_rest(Acc, V) --> "+", term(T), { A is Acc + T }, expr_rest(A, V).
expr_rest(Acc, V) --> "-", term(T), { A is Acc - T }, expr_rest(A, V).
expr_rest(V, V)   --> [].

term(V)    --> factor(F), term_rest(F, V).
term_rest(Acc, V) --> "*", factor(F), { A is Acc * F }, term_rest(A, V).
term_rest(V, V)   --> [].

factor(V)  --> digits(Ds), { number_codes(V, Ds) }.
factor(V)  --> "(", expr(V), ")".

digits([D|Ds]) --> digit(D), digits(Ds).
digits([D])    --> digit(D).
digit(D)       --> [D], { D >= 0'0, D =< 0'9 }.

% --- a toy natural-language grammar --------------------------------------
sentence  --> noun_phrase, verb_phrase.
noun_phrase --> determiner, noun.
verb_phrase --> verb, noun_phrase.
determiner --> [the].
determiner --> [a].
noun --> [cat].
noun --> [dog].
verb --> [sees].
verb --> [chases].
