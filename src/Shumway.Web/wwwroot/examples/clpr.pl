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

% Note for the curious: this solver propagates when an equation is posted.
% Adding {A + B =:= 10} and later pinning {A =:= 6} keeps the store sound
% (asking for B =:= 5 correctly fails) but leaves B as a residual rather
% than binding it to 4. Post what you know before what you are solving for,
% or post it all at once, and you get numbers.
