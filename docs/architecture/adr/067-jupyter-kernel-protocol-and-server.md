# ADR-067: The kernel protocol and the Prolog-side server

## Status

Proposed (2026-11-14). Design only; nothing is implemented. Records D5, D8,
D9 and D10. D5 was first decided as "one call per request, no loop". It was
reversed before anything was written, once the retry mechanism below was
found in Hercutalk's code.

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
- **Inside the session, the server's `loop/3` runs as in Hercutalk**, on the
  session's engine thread (ADR-068). It blocks reading the next request from
  an in-memory **request pipe** and writes responses to an in-memory
  **response pipe**. This is Hercutalk's stdin/stdout without a process, and
  it follows the pattern of `PageInput`'s `PageReader`.
- **Outside, `handle_request(sessionId, json) : Task<string>`.** It enqueues
  one line on the request pipe and completes when the matching response line
  appears on the response pipe. JavaScript never sees the loop.
- **Requests are read from a dedicated stream, not `current_input`.**
  Hercutalk's `read_message/1` reads `current_input`. If our port read it
  too, a `read/1` in a cell would consume JSON. The port reads requests from
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

### D8: Hybrid server: translate what defines behaviour, write the transport

Translate **module by module**, keeping file names, predicate names and
structure, so that `jupyter_X.lgt` and `jupyter_X.pl` can be compared side by
side:

| Hercutalk file | Treatment |
|---|---|
| `jupyter_term_handling` | translate (cell semantics, the heart of it) |
| `jupyter_request_handling` | translate, **including `loop/3`** |
| `jupyter_query_handling` | translate; replace capture-to-file with capture to an in-memory stream |
| `jupyter_variable_bindings` | translate |
| `jupyter_preferences` | translate |
| `jupyter` (helpers) | translate into module `jupyter` (D9) |
| `jupyter_jsonrpc` | translate the message layer; JSON reading and writing as below |
| `jupyter_logging` | write ours (console, or a file in MEMFS) |
| `jupyter_server`, `loader` | write ours (boot inside the session) |
| `jupyter_widgets`, `jupyter_forms`, `jupyter_inputs` | later phase (ADR-072); Apache-2.0, keep that licence on those files |

- **Mapping:** `:- object(X)` becomes `:- module(X, [...])`, `uses/2` becomes
  imports, and `X::p` becomes `X:p`. Logtalk-specific calls
  (`logtalk::print_message`, `meta::map`, `term_io`, `debugger`) map to
  Shumway equivalents. A call with no equivalent is listed in the port's
  notes rather than improvised.
- **JSON:** Shumway has no JSON library. Provide `jupyter_json_read/2` and
  `jupyter_json_write/2` (C# builtins, or Prolog if fast enough) producing
  and consuming **the term shape Hercutalk's code expects** (`json([Key-Value,
  ...])` for objects and `@(null)` for null, as seen in
  `jupyter_request_handling`. The exact encoding of strings, booleans and
  numbers must be read off the server code and Logtalk's `json` library
  before writing the bridge). Fidelity to that shape is what lets the
  translated code run unchanged.
- **Licence:** each translated file keeps the original MIT notice and adds
  its own line, for example "Translated to Prolog for Shumway from Hercutalk
  0.47.0 (865d6fd)".
- **Version pin:** the port records the Hercutalk version it was translated
  from. Porting a later upstream change is then a diff between files that
  correspond one to one.
- **Exit condition:** if translating `jupyter_term_handling` turns out to be
  forced (deeply tied to Logtalk, or unmaintainable), stop after the first
  third and re-evaluate writing that module from scratch against the protocol
  document.

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

- `retry`, nested queries and every semantic detail behave as in Hercutalk,
  because the code that defines them is the same code.
- The only first-party code on the server side is the transport, the boot,
  logging, output capture and JSON.
- `read/1` from a cell works, and works better than in Hercutalk, because
  requests and user input are separate streams.
- An interrupt loses the session's state, as it does in Hercutalk today.
- Upstream changes have to be ported by hand. The version pin keeps that
  manageable.
- The session's engine is busy for as long as the kernel lives. That is fine
  with one engine per kernel, and it is the reason ADR-068 is a prerequisite.

## Alternatives considered

- **One call per request, no loop (the first D5).** Simpler and more
  natural with `[JSExport]`. Rejected because `retry` needs the goal alive
  across requests.
- **Port the original hhu-stups server.** It is SWI/SICStus-specific, and
  Hercutalk has since moved on (portability, magics, data). Rejected.
- **Write a server from scratch against the protocol.** The protocol fixes
  the wire format, but cell semantics have no specification other than
  `jupyter_term_handling`'s 1,400 lines. Rejected as the default, and kept as
  D8's exit condition.
- **Run Hercutalk's Python half in Pyodide.** It needs `subprocess`, an HTTP
  server thread, the native `dot` binary and ZMQ, none of which Pyodide
  provides. Rejected (see ADR-072).

## Related

ADR-066, ADR-068 (sessions), ADR-072 (outputs), ADR-073 (packages);
[`jupyterlite-protocol.md`](../../design/jupyterlite-protocol.md).
