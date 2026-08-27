% Constraints over the REALS: linear relationships you can run in any
% direction. Where CLP(FD) narrows integers to a finite domain, CLP(R) solves
% and KEEPS linear equations, so an answer can be a relationship rather than
% a number.
%
% Try:  celsius_fahrenheit(100, F).
%       celsius_fahrenheit(C, 212).      the same clause, backwards
%       mix(10, W, S).                   two unknowns, two equations
%       mix(Total, 3, S).                and again, from the other end
%       triangle(A, B, C).               angles: still under-determined
%       triangle(A, 90, 60).             now it is not
%       {X + Y =:= 10, X - Y =:= 2}.     a system, solved
%       {X + Y =:= 10}.                  one equation: the answer IS it
%       mortgage(100000, 0.01, 12, 0, Pay).      what does it cost a month?
%       mortgage(P, 0.01, 12, 0, 8884.88).       what can I afford?

:- use_module(library(clpr)).

% One relationship, read in whichever direction you give it. There is no
% "input" and no "output": give it C and it computes F, give it F and it
% computes C, give it neither and the answer is the line itself.
celsius_fahrenheit(C, F) :-
    {F =:= C * 9 / 5 + 32}.

% Blending: W kilos of a 30%-strength solution and S kilos of a 70% one make
% Total kilos at 50%. Two equations, three unknowns: fix any one and the
% other two follow, whichever one you pick.
mix(Total, W, S) :-
    {W + S =:= Total},
    {0.30 * W + 0.70 * S =:= 0.50 * Total}.

% The angles of a triangle. With none of them known this is one equation in
% three unknowns, and CLP(R) says so: the answer it gives back IS the
% equation, which is everything that is true so far. Name two and the third
% is arithmetic.
triangle(A, B, C) :-
    {A + B + C =:= 180},
    {A > 0},
    {B > 0},
    {C > 0}.

% The classic. A loan of P repaid over T periods at rate I per period,
% leaving balance B, paying Pay each time. Every step posts one equation, so
% the whole schedule is a single linear system: ask for the payment, or ask
% what principal a payment you can afford buys. Same program, no flag.
%
% Note what is NOT in the store: the period counter. Money is real and
% belongs there; counting down is ordinary integer arithmetic. Putting
% `T1 is T - 1` inside {} would make T1 a float, and a float never matches
% the integer 0 in the base clause.
mortgage(P, _, 0, B, _) :-
    {B =:= P}.
mortgage(P, I, T, B, Pay) :-
    T > 0,
    T1 is T - 1,
    {P1 =:= P * (1 + I) - Pay},
    mortgage(P1, I, T1, B, Pay).
