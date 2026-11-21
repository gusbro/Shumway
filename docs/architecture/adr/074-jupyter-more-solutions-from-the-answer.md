# ADR-074: More solutions are asked for from the answer itself

## Status

Accepted (2026-11-20). Nothing is implemented. Records D18.

## Context

**How a notebook gets a query's next solution today.** A query that left
choice points stays alive in the server, under a request loop of its own
(ADR-067 D5). A cell that is `retry.` makes that loop fail, and the query
gives its next solution. The queries alive are a stack, and `retry` reaches
the latest. It is the reference's mechanism, and the corpus checks it.

**What is wrong with it for someone who writes Prolog.**

- An answer does not say whether there are more. `X = a` is shown the same
  when it is the only solution and when ten are left. A top level shows it:
  `X = a ;` or `X = a.`
- The next solution is shown under another cell, the one that says `retry.`
  Running that cell again puts the new solution in place of the one before:
  the solutions of a query are never together.
- `retry.` does not say which query it means. It is whichever was left alive
  last, wherever its cell is.
- Nothing ends a query. Each run of a cell whose query leaves choice points
  leaves one more query alive, under the ones before, until the session
  ends.

**What the server already knows.** It tells a solution that left choice
points from one that did not: only the first kind gets a loop
(`jupyter_server.pl`, `solved/9`). It tells no one.

**What the top level offers** (`AnswerPrompt`): `;` or space for the next
solution, `f` to go on to the next multiple of five, `a` for every remaining
one, and Enter to stop.

**What an interrupt does.** It ends the session (ADR-067 D5, and the plan's
engine track): a query stopped half way leaves no state behind that the
user cannot see.

## Decision

### D18: An answer shows whether there are more, and has the top level's choices

**The answer reads as the top level's.** A solution that left choice points
ends with ` ;`, and the last one with `.` A query that has no next solution
when it is asked for one answers `false.` The text is the same in the
output's plain text, so a saved notebook and another front end show it.

**The solutions of a query are shown together**, under the cell that asked
it, in the order they came.

**Under the answer of a query that is alive there are four buttons**, the
top level's four choices:

| Button | Top level | What it does |
|---|---|---|
| Next | `;` | the next solution |
| 5 | `f` | on to the next multiple of five: four after one solution, five after five |
| All | `a` | every remaining solution |
| Stop | Enter | ends the query: its choice points are cut, and its answer ends with `.` |

**Stop never cuts a query half way.** It acts between solutions:

| State | Stop | To cut at once |
|---|---|---|
| The query is alive and nothing runs | enabled: ends the query | |
| `5` or `All` is running | enabled: ends the query when the solutions being computed arrive | Interrupt, which restarts the session |
| The cell's first run, or `Next`, is running | disabled | Interrupt, which restarts the session |

Between two solutions a query is where a top level shows its prompt: it has
just succeeded. Ending it there is what Enter does, the cleanup handlers of
`setup_call_cleanup/3` run, and the session goes on with all it had. So the
rule of the interrupt holds: a computation is never stopped half way with
the session kept.

While `5` or `All` runs, the other three buttons are disabled. When Stop was
pressed and the solutions being computed do not arrive in a few seconds, the
answer says so, and that Interrupt restarts the session.

**`All` is the kernel asking again and again**, five solutions a request.
Stop is looked at between two requests, so it takes effect at most five
solutions later, and `All` does not cost a request for each solution.

**Only the latest query alive can be asked.** The engine has one stack: a
solution of an older query is found by undoing the queries after it. The
buttons of an older query are disabled, and say that a later query is still
open. They are enabled again when the later ones have no more solutions or
were stopped.

**Running a cell again ends the query its run before left alive.** When
that query is not the latest, it is ended as soon as it becomes the latest,
and it gives no solution in between.

**An answer whose session ended has no buttons**: after a restart, an
interrupt, `halt`, or in a notebook opened again. Its solutions stay as
text.

**The keyboard.** A command asks the next solution of the active cell's
answer, with `;` in command mode as its key.

**`retry.` goes on working as in the reference.** Its cell shows the
solution, and the buttons of the query it retried learn whether the query
is still alive.

### The protocol

Extensions (**[ext]**) to the reference's protocol
(`docs/design/jupyterlite-protocol.md`), all additive:

- A query alive has an identifier, a number unique in its session. The
  `success` entry of a solution that left choice points carries it as
  `"alive": <id>`. An entry without it is a last solution.
- `more`, with `{"query": <id>, "count": <n>}`: up to `n` more solutions of
  that query, as entries of the shape `call` gives, and whether it is still
  alive. A query with no next solution answers a failure entry and is alive
  no more. An exception in it is an error entry, and ends it.
- `stop`, with `{"query": <id>}`: ends the query. One that is not the
  latest is ended when it becomes the latest.
- Every result of `call`, `more` and `stop` says which queries are alive,
  oldest first, so the kernel knows which is the latest.
- `more` on a query that is not the latest is an error that says so. On one
  that is not alive, the error is `no_active_call`.

A kernel that never sends `more` or `stop`, and ignores the new keys, sees
the reference's protocol. A server that sends no `alive` gets a kernel that
shows answers as before, with no buttons: the Logtalk variant, until its
server says it.

### The page

The buttons are drawn by the extension, which is the notebook site's own in
every form the site takes. The plain text of the output holds the
solutions, so a front end without the extension shows them and uses
`retry.`

The cell an answer belongs to is the one whose execute request the kernel
got it from (`metadata.cellId`). A request without one, as a console's, gets
the answer's text and no buttons.

## Consequences

- A query's solutions are read where the query is, and how many were asked
  for is the reader's choice, as at a top level.
- A session no longer keeps the queries of cells run again. A query still
  stays alive until it is stopped, exhausted, or its cell runs again.
- The server gains two methods and a cut of a query alive. The cut ends
  the query's loop the way `halt` ends every loop: the loop returns, and
  the query's choice points go with the `->` that called it.
- The extension gains its first part that draws in the page: until now the
  kernel sent standard outputs and JupyterLab drew them.
- The corpus does not cover `more` and `stop`: the reference has neither.
  The server's own tests do, and the browser check clicks the buttons.

## To verify when it is built

- How the kernel tells the answers in the page which queries are alive
  after each request: an output of a type of its own, or a message of its
  own on IOPub.
- That an output the extension adds to a cell after its run ended is saved
  with the notebook.

## Alternatives considered

- **The top level's prompt through the input request**: `;` and Enter typed
  in the box a `read/1` uses. No part of the extension draws anything.
  Rejected: every query that leaves choice points would hold the kernel
  until answered, and a notebook run from top to bottom would stop at each.
- **Stop that cuts the solution being computed and keeps the session.** It
  is what a top level's cancel does. Rejected for the reason an interrupt
  restarts the session: what the query did before the cut stays, and the
  user cannot see it.
- **A solution of any query alive, by ending the later ones.** Rejected: a
  click on one answer would end another's query without the reader asking
  for that.
- **One request for `All`.** Rejected: nothing could stop it but an
  interrupt, and a query with solutions without end would cost the session.

## Related

ADR-067 (the server, `retry`, the interrupt), ADR-072 (what the kernel
shows), ADR-073 (the feature table and the protocol's version).
