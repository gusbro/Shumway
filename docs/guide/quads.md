# Quad test transcripts

A quad file is a machine-readable test transcript: a list of queries,
each followed by the outcomes a conforming Prolog may produce for it.
The format comes from the ISO conformity testing pages at
<https://www.complang.tuwien.ac.at/ulrich/iso-prolog/>, which publish
whole test suites this way (`length_quad.pl`, `phrase_quad.pl`).

A transcript is not a program. Consulting one without support is a
syntax error, and that is correct: the query lines need an infix `?-`,
the expected blocks would otherwise read as clauses for protected
predicates, and outcome alternatives join with a bare `|`.
`library(quads)` supplies what is missing and turns the file into
runnable tests.

## The format

```prolog
% one test: an id, the query, then the sanctioned outcomes
1 ?- length(L, N).
      L = [], N = 0
   ;  L = [_A], N = 1
   ;  ... .
8 ?- length(L, -1).
      domain_error(not_less_than_zero,-1).
22 ?- length(L, L).
      resource_error(finite_memory)
   |  resource_error(...)
   |  loops.
28 ?- L = [a|L], length(L, 7).
      sto, false
   |  sto, L = [a,a,_A,_B,_C,_D,_E]. % tau
16, "7.8.3.4#9"
?- call((write(3), X)).
      outputs("3"), instantiation_error.
```

Piece by piece:

- **The test line**: an id, then `?-`, then the goal, ending with a
  period. The id is any ground term, so a test may be named by more than
  a number: `16, "7.8.3.4#9"` names both the test and the clause of the
  standard it comes from. It need not share a line with its `?-`, since
  a transcript is read as terms rather than as lines.
- **The expected block**: the sentences that follow, up to the next test
  line. One block is a single sentence, so the period appears once, at
  its end.
- **`;` continues an answer sequence**: `L = [], N = 0 ; L = [_A], N = 1`
  is one expected outcome showing successive answers. A trailing `...`
  means the enumeration continues.
- **`|` separates alternative outcomes**: any one of them makes the test
  pass. Systems differ within the sanctioned set; that is the point of
  listing alternatives.
- **Error outcomes** are written as the error term the goal must raise:
  `instantiation_error`, `type_error(integer,a)`,
  `domain_error(not_less_than_zero,-1)`, and so on. `...` inside one
  stands for any argument.
- **`false`** means the goal fails; **`true`** means it succeeds.
- **`loops`** sanctions non-termination. No harness can observe an
  infinite loop directly, so the goal runs under a 15 second limit and
  still-running counts as this outcome.
- **`sto,`** prefixes an outcome that assumes the run is subject to
  occurs-check territory (rational trees involved); the outcome after
  the prefix is what is checked.
- **`outputs(Text),`** prefixes an outcome to say the goal writes Text
  first. What is checked is the outcome after it; the written text
  itself is not compared.
- **`% name`** at the end of an alternative attributes it to the system
  that produces it; it is a comment.

## Running quads from the top level

```prolog
?- use_module(library(quads)).
?- consult('length_quad.pl').
?- run_quads.
quads: 37/37
```

Importing `library(quads)` activates the `?-` (xfx 1200) and `|`
(xfy 1100) operators for your session and installs the capture: from
then on, consulting a quad file stores its tests instead of trying to
compile them, and consulting ordinary files is unaffected. Quads
accumulate across consults.

- `run_quads` runs every loaded quad and prints `quads: Passed/Total`,
  with the failing ids listed when there are any, and a note for any
  quad whose expected block could not be classified.
- `run_quads(Id)` runs a single test by its id.
- `clear_quads` forgets the loaded set.

Goals in the published suites use `freeze/2` and `dif/2`;
`library(quads)` loads `library(coroutining)` itself so they just work.

## Running quads from the command line

```text
shumway --quads length_quad.pl
```

`--quads <file>` loads `library(quads)`, consults the transcript, and
runs `run_quads`. The flag is repeatable;
all the files' quads run as one set. The session then stays at the
prompt, so `run_quads(Id)` can replay a failing test interactively.

## In the browser

The library ships inside the engine, so the same workflow runs in
WebShumway: upload the quad file to the workspace, then
`use_module(library(quads))`, consult it, and `run_quads`.

## How outcomes are checked

Each expected alternative is classified as one of: succeeds, fails,
error of a given kind, loops, or lenient (an alternative the classifier
cannot pin down matches any outcome, mirroring the intent of the
published pages). The goal runs once and its outcome must match one
sanctioned class. An answer-sequence block counts as succeeds: the
checker verifies the goal's outcome class, not the printed bindings.
