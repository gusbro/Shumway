# ADR-067: The kernel protocol and the Prolog-side server

## Status

Accepted (2026-11-15). Nothing is implemented. Records D5, D8, D9 and D10.
Two decisions changed before anything was written:
- D5 was first "one call per request, no loop". It was reversed once the
  retry mechanism below was found in Hercutalk's code.
- D8 was first a translation of Hercutalk's server, module by module. On
  acceptance it became a server of our own, with Hercutalk as the reference
  of a differential test.

## Context

In Hercutalk (0.47.0) the Python kernel and the Logtalk server talk
JSON-RPC 2.0 over the server's stdin and stdout, one message per line. The
protocol is small: five methods, with all of a cell's code travelling as
text in `call`. The server decides what the cell means. The exact contract
is in [`jupyterlite-protocol.md`](../../design/jupyterlite-protocol.md).

**How `retry` works.** `jupyter_request_handling::loop/3` reads and
dispatches requests. When a query succeeds, `jupyter_term_handling` does not
return to the top. It calls `loop/3` *again, nested inside the goal*, so the
goal's choice points stay on the stack. A later cell containing `retry` (or
`jupyter::retry`) makes that inner loop **fail**, and backtracking computes
the next solution. The nesting is the mechanism, so a design where each
request runs to completion and returns cannot support `retry`.

**The JavaScript side wants one awaitable call per request.** Every
`[JSExport]` returns a `Task` (ADR-042 §3).

**The server's code** is written as objects with `uses/2` and no inheritance
or dynamic messaging that matters: modules dressed as objects. The core
files are MIT, copyright Paulo Moura and Brecklinghaus, Leuschel and
dgelessus (the hhu-stups origin). `jupyter_widgets`, `jupyter_forms` and
`jupyter_inputs` are Apache-2.0.

## Decision

### D5: Hercutalk's protocol, a blocking loop inside, a `Task` per request outside

- **The wire format is Hercutalk's**: same methods, same result shape, same
  additional-data keys. Extensions are allowed; they are marked as such in
  the protocol document and offered upstream.
- **Inside the session, the server runs a request loop as Hercutalk's
  `loop/3` does**, nested inside a goal that succeeded, on the session's
  engine thread (ADR-068). It blocks reading the next request from
  an in-memory **request pipe** and writes responses to an in-memory
  **response pipe**. This is Hercutalk's stdin/stdout without a process, and
  it follows the pattern of `PageInput`'s `PageReader`.
- **Outside, `handle_request(sessionId, json) : Task<string>`.** It enqueues
  one line on the request pipe and completes when the matching response line
  appears on the response pipe. JavaScript never sees the loop.
- **Requests are read from a dedicated stream, not `current_input`.**
  Hercutalk's `read_message/1` reads `current_input`. If our server read it
  too, a `read/1` in a cell would consume JSON. The server reads requests from
  a stream alias (`jupyter_requests`), and `user_input` is bound to the
  session's page input (ADR-068). This is a small change worth proposing to
  Hercutalk, which today shares one stdin.
- **Interrupt behaves as in Hercutalk: it restarts the server.** Hercutalk
  kills the backend process on interrupt, and the next execution starts a
  fresh one, losing state. Here `QueryCancel` stops the running search.
  Cancellation reaches the host as an OperationCanceledException
  (`WebShumwayApp.cs` catches one) rather than as a Prolog exception, so the
  server's loop does not survive it. The session is torn down and recreated,
  and the user is told, as Hercutalk does. A cancellation that Prolog could
  catch, which would let the session keep its state, is future work and needs
  its own engine ADR.

The earlier objection to a blocking loop was that it holds the engine gate
forever. With one engine per kernel (ADR-068) that no longer matters: all of
that kernel's engine work goes through its server anyway, completion and
inspection included.

### D8: A server of our own, with Hercutalk as the reference

- **The server is written for Shumway**, in Prolog modules of its own.
  Hercutalk's code is not translated or copied. It is read as the
  specification of what a cell means, and what is learnt from it goes into
  the protocol document and into the corpus below.
- **A differential test says what is correct.** The same JSON-RPC lines go to
  Hercutalk, running on desktop Shumway through Logtalk, and to our server,
  and the responses are compared. A difference is fixed, or documented as
  intended (module-qualified names, for example). A case found later joins
  the corpus.
- **What is shared with Hercutalk is interface:**
  - the wire format (D5);
  - the names a notebook's author writes: the helpers of D9, `retry`, `halt`
    and the magics;
  - how `retry` works: the goal stays alive under a nested request loop.
- **The server uses what the engine has**: its term writer, residual goals
  through `copy_term/3`, output captured to an in-memory stream, a session's
  own input and output (ADR-068). It does not carry the portable forms of
  these that a server for many Prolog systems needs.

Each part of the server has a place in Hercutalk where its behaviour is
defined:

| Part of the server | Where Hercutalk defines the behaviour |
|---|---|
| Cell semantics: what a clause, a directive, a query or a magic does | `jupyter_term_handling` |
| The request loop, dispatch and `retry` | `jupyter_request_handling` |
| Running a query with its output captured, in memory and not in a file | `jupyter_query_handling` |
| Bindings carried across cells (`$Var`) | `jupyter_variable_bindings` |
| Preferences | `jupyter_preferences` |
| The helper module `jupyter` (D9) | `jupyter` |
| JSON-RPC messages | `jupyter_jsonrpc` |
| Transport, boot and logging | nowhere: they belong to the host |
| Widgets, forms and inputs | a later phase, decided in ADR-072 |

- **JSON:** Shumway has no JSON library. The server gets
  `jupyter_json_read/2` and `jupyter_json_write/2` (C# builtins, or Prolog if
  fast enough). The term shape is the server's own choice, since no code
  written for another shape runs on it.
- **Reference version:** the corpus records the Hercutalk version its
  expected responses came from (0.47.0, `865d6fd`, to begin with). A later
  upstream change is taken by running the corpus against the new version and
  reading what differs.
- **Exit condition:** if the server does not converge on the corpus (new
  differences keep appearing in cell semantics after the first third of the
  corpus passes), stop and re-evaluate translating `jupyter_term_handling`,
  with its MIT notice, as a stated exception to this repository's rule on
  other projects' code.

**Why not a translation.**
- This repository holds its own implementations, with equivalence shown by
  tests. The Logtalk adapter was rewritten that way.
- A translation brings Hercutalk's forms of what the engine already does, and
  every upstream change is then ported by hand.
- Hercutalk web runs Hercutalk's server itself, unmodified, on Logtalk. That
  is where fidelity to its code matters. In the Prolog variant what has to
  agree is the protocol and the behaviour.

### D9: Helper module `jupyter`

Hercutalk's `jupyter::print_table/1` becomes `jupyter:print_table/1`, and so
on. This deviation cannot be avoided: Prolog has modules, not objects. Bare
aliases that need no qualification (`retry`, `halt`) keep working as in
Hercutalk.

### D10: Server precompiled into the bundle

The server modules are compiled ahead of time into the bundle the session
loads, so opening a notebook does not consult them from source. This is the
same idea as Logtalk's embedding of a precompiled core, which Hercutalk web
will use for Logtalk.

## Consequences

- `retry`, nested queries and the cell semantics the corpus covers behave as
  in Hercutalk, because a differential test says so, not because the code is
  shared.
- A behaviour the corpus does not cover may differ, and is fixed when found.
- The server is first-party code throughout.
- `read/1` from a cell works, and works better than in Hercutalk, because
  requests and user input are separate streams.
- An interrupt loses the session's state, as it does in Hercutalk today.
- An upstream change is found by running the corpus against the new
  Hercutalk version, and written here by hand.
- The session's engine is busy for as long as the kernel lives. That is fine
  with one engine per kernel, and it is the reason ADR-068 is a prerequisite.

## Alternatives considered

- **One call per request, no loop (the first D5).** Simpler and more
  natural with `[JSExport]`. Rejected because `retry` needs the goal alive
  across requests.
- **Port the original hhu-stups server.** It is SWI/SICStus-specific, and
  Hercutalk has since moved on (portability, magics, data). Rejected.
- **Translate Hercutalk's server module by module (the first D8).** Objects
  become modules, names and structure stay, and the files compare one to one
  with their originals, each with its MIT notice. Cell semantics have no
  specification other than `jupyter_term_handling`'s 1,400 lines, and a
  translation carries them over whole. Rejected for the reasons in D8, and
  kept as its exit condition.
- **Run Hercutalk's Python half in Pyodide.** It needs `subprocess`, an HTTP
  server thread, the native `dot` binary and ZMQ, none of which Pyodide
  provides. Rejected (see ADR-072).

## Related

ADR-066, ADR-068 (sessions), ADR-072 (outputs), ADR-073 (packages);
[`jupyterlite-protocol.md`](../../design/jupyterlite-protocol.md).
