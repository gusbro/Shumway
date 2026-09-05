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
12, "8.1.2.3#4"
?- call((write(z), X)).
      outputs("z"), instantiation_error.
?- atom(a).
      true.
40 ?- read(T).
      inputs("bar."), T = bar, unexpected.
      inputs("bar."), peeks(" "), T = bar.
```

Piece by piece:

- **The test line**: an id, then `?-`, then the goal, ending with a
  period. A query is recognised by that `?-` alone, so a test that needs
  no name is written `?- Goal.` and is reported by its position in the
  file. The id is any ground term, so a test may be named by more than a
  number: `12, "8.1.2.3#4"` names both the test and the clause of the
  standard it comes from. It need not share a line with its `?-`, since
  a transcript is read as terms rather than as lines.
- **The expected block**: every sentence that follows, up to the next
  test line. There may be more than one, and each contributes its
  alternatives to the same test.
- **`;` continues an answer sequence**: `L = [], N = 0 ; L = [_A], N = 1`
  is one expected outcome showing successive answers, in the order the
  goal gives them. The answers themselves are compared, so the sequence
  says both what each answer binds and how many there are: written out
  in full it claims the goal answers exactly that many times. A trailing
  `...` leaves it open, claiming only the answers shown.
  A query and the sentences describing its answers are separate terms,
  so it is the variable NAMES that relate them: the `L` of `L = []` is
  the `L` of the query because they are spelled alike. A variable the
  description does not mention is one the answer left unbound, the way a
  top level shows nothing for it, and a name starting with an underscore
  is not shown at all. A variable named freshly in the answer, `_A`
  above, stands for a variable in the answer: what it is called does not
  matter, being a variable does.
- **`|` separates alternative outcomes**: any one of them makes the test
  pass. Systems differ within the sanctioned set; that is the point of
  listing alternatives.
- **Error outcomes** are written as the error term the goal must raise:
  `instantiation_error`, `type_error(integer,a)`,
  `domain_error(not_less_than_zero,-1)`, and so on. The whole term is
  compared, culprit included, so raising the right kind of error about
  the wrong value does not pass. `...` inside one stands for a part left
  unwritten and matches whatever is in that position; the rest still has
  to agree.
- **`false`** means the goal fails; **`true`** means it succeeds.
- **`waits`** says the goal blocks for input that never comes. Nothing
  this library can supply tells waiting apart from anything else, since
  an empty input is end of file rather than a wait, so a test that
  sanctions only this is named in the report under `not run` instead of
  being counted as a failure.
- **`loops`** sanctions non-termination. No harness can observe an
  infinite loop directly, so the goal runs under a 15 second limit and
  still-running counts as this outcome.
- **`waits`** says the goal blocks for input that never comes. What can
  be observed is not the blocking, which no harness can wait out, but the
  reading: a goal that waits is one that went looking for input. So the
  goal runs against an input of a single character, and this outcome
  holds when that character is gone afterwards. A goal that answers
  without reading leaves it there.
- **`sto,`** prefixes an outcome that assumes the run is subject to
  occurs-check territory (rational trees involved); the outcome after
  the prefix is what is checked.
- **`outputs(Text),`** prefixes an outcome to say the goal writes Text
  first. Both halves are checked: the text the goal writes and the
  outcome after it. The text may be written in pieces with `...` between
  them, as in `outputs(("f(_", ..., ")"))`, which says the output starts
  and ends that way and says nothing about the middle. That is how a
  claim about output survives an implementation's choice of variable
  names. A text written down whole claims the output entire. The text may be written in pieces with `...` between
  them, as in `outputs(("f(_", ..., ")"))`, which says the output starts
  and ends that way and says nothing about the middle. That is how a
  claim about output survives an implementation's choice of variable
  names. A text written down whole claims the output entire.
- **`inputs(Text)` and `peeks(Text)`** say what the goal reads: it must
  consume `inputs` and leave `peeks` unread. The two are supplied to the
  goal as one input and both halves are checked. Writing the peek down
  separately is how a claim about reading becomes testable: taking `1.`
  off an input of exactly `1.` cannot tell that the number ended there,
  and `inputs("1."), peeks(" ")` says the reader had to look at the
  space to know.
- **`unexpected`** at the end of an alternative marks it as a WRONG
  answer, written down because some system produces it. It never makes a
  test pass, so a test whose alternatives are all `unexpected` can only
  fail.
- **`% name`** at the end of an alternative attributes it to the system
  that produces it; it is a comment.

An answer display says what the QUERY's variables became, and says it the
way a top level does, so three things it cannot be: an equation whose
left side is not a variable, one whose left side is a name the query does
not have, and one whose whole value is a variable appearing nowhere else,
which is what an unbound variable looks like and an unbound variable is
not shown at all. A test line whose id is not ground is a test with no
name, since the id is how the report calls it and how `run_quads/1` asks
for it; it is still a test, and it is reported and counted as one.

An alternative written in a vocabulary this library does not know is not
guessed at. It is left out of the sanctioned set and named in the report
under `not understood`, with the test it belongs to, so a transcript
using something new reads as unchecked rather than as a pass.

The variable names come from the source, which is read a second time to
recover them. If the file is no longer there when the tests run, the
goals still run and everything else is still checked, and the report
lists those tests under `answers not compared` rather than counting the
weaker check as a comparison.

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
  with the failing ids listed when there are any, and every answer
  description it could not read named under `not understood` with the
  test it belongs to.
- `run_quads(Id)` runs a single test by its id.
- `quads_result(Passed, Total)` gives back the counts the last run
  reported, for a program that wants to act on them. It fails when
  nothing has been run, which is how "none run" is told apart from "none
  passed".
- `clear_quads` forgets the loaded set.

Goals in the published suites use `freeze/2` and `dif/2`;
`library(quads)` loads `library(coroutining)` itself so they just work.

## Running quads from the command line

```text
shumway --quads length_quad.pl
```

`--quads <file>` loads `library(quads)`, consults the transcript, runs
`run_quads`, and exits. The flag is repeatable; all the files' quads run
as one set. A transcript is a test run rather than a session, so the
verdict is in the exit code: zero only when every quad passed, and
non-zero when one failed or when the files held no quads at all. That
makes it usable from a build script.

To replay a single test interactively, take the three steps of the
previous section and use `run_quads(Id)`.

## In the browser

The library ships inside the engine, so the same workflow runs in
WebShumway: upload the quad file to the workspace, then
`use_module(library(quads))`, consult it, and `run_quads`.

## How outcomes are checked

Each sanctioned alternative says what the goal does: succeed, fail,
raise a particular error, loop, or give a particular sequence of
answers. Alternatives that read the same input are decided by one run,
since reading is the expensive part.

A goal described by its answers is run for as many answers as the
description mentions, plus one, which is how a goal that answers more
times than the transcript says gets caught. Every other goal is run once
and its outcome compared. Whatever a description states is compared:
the answers, the error term, the text written, the input left unread. An
alternative that states less is checked as far as it goes, and one this
library cannot read at all is reported rather than assumed to pass.
