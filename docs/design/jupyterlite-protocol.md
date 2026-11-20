# Kernel ⇄ server protocol (Shumway notebooks and Hercutalk)

**Protocol version:** 1 (draft).
**Based on:** Hercutalk 0.47.0 (`865d6fd`), files
`logtalk_kernel/logtalk_server/jupyter_request_handling.lgt`,
`jupyter_term_handling.lgt`, `jupyter_jsonrpc.lgt`, and the client side in
`logtalk_kernel/logtalk_kernel_base_implementation.py`.

**Normative rule.** For everything inherited from Hercutalk, Hercutalk's code
at the pinned version is the authority. This document summarises it so that
the TypeScript kernel can be written without reading Python. Where the two
disagree, the code wins, and the disagreement is a bug in this document.
Additions are marked **[ext]** and must not change the meaning of anything
inherited.

**What is checked.** `tests/jupyter/corpus` holds sessions of requests and
what Hercutalk answered to each. The Prolog server is sent the same requests
and its responses are compared with those (ADR-067 D8). §9 lists where it
answers differently on purpose.

Decisions behind this document: ADR-067 (protocol and server) and ADR-072
(outputs).

## 1. Transport

- **Framing:** JSON-RPC 2.0, one JSON message per line (`\n`-terminated), as
  Hercutalk does over stdio.
- **Shumway:** two in-memory pipes per session. The server reads requests
  from the stream alias `jupyter_requests`, not from `current_input`, and
  writes responses to `jupyter_responses`. `user_input` is the session's page
  input. See ADR-067 D5.
- **JavaScript API:** `SessionRequest(kernelId, line) : Task<string>` sends
  one request line and resolves with the matching response line.
  Requests on one session are strictly sequential: the next one is sent only
  after the previous one is answered.
- **The `id` of a request** is the Jupyter execution count for `call`, and 0
  for internal requests, as Hercutalk does.

## 2. Methods

| Method | Params | Result | Shumway notes |
|---|---|---|---|
| `call` | `{"code": "<cell text>", "notebook": "<file name>"}`; `notebook` is optional | per-term results (§3); `""` for an empty cell; `{}` for a cell with nothing to run (`%%save`, `%%highlight`) | the core of everything. `notebook` is the file name of the cell's notebook, which the kernel sends with every cell; a console sends none |
| `backend` | none | atom: the backend's `prolog_dialect` | answers `shumway` |
| `enable_logging` | none | boolean: whether a log file was created | answers `false`: no log file is kept |
| `version` | none | `"Major.Minor.Patch"` of the backend | Shumway's version |
| `jupyter_predicate_docs` | none | object: docs of the predicates in `jupyter` | module `jupyter` here (ADR-067 D9) |
| `protocol` **[ext]** | none | `{"version": 1, "implementation": "shumway"}` | checked by the kernel at session start (ADR-073) |

An unknown method gets a `method_not_found` error.

**Startup sequence used by Hercutalk's client** (keep it, so behaviour
matches):

1. `backend`;
2. `enable_logging`, if configured;
3. `call` with `jupyter::update_completion_data.`;
4. `jupyter_predicate_docs`.

On Shumway, `protocol` comes first, and the third request is
`jupyter:update_completion_data.`.

## 3. Result of `call`

The result is an object keyed by the term's 1-based position as a string:
`"1"`, `"2"`, …, one entry per term in the cell. The client iterates from
`"1"` while the key exists.

Each entry has a `status`:

**`success`:**

```json
{ "status": "success",
  "type": "query" | "widget" | "form" | "...",
  "bindings": { "X": "value", "D": { "dom": "1..3" } },
  "output": "<captured output of the term>",
  "<additional key>": { ... } }
```

- `bindings` is `{}` when there are none. A value is either a string (the
  printed term) or an object; Hercutalk uses `{"dom": ...}` for a `clpfd`
  variable, rendered as `X in Dom`.
- For a `query` with no bindings, the client prints the backend's success
  text; when additional-data handling fails, it prints the failure text.
- The full list of `type` values comes from the server code. Clients must
  accept unknown types.

**`error`:**

```json
{ "status": "error", "error": { "code": -4712, "message": "Exception", "data": { ... } } }
```

The `data` object carries:

- the text of the error, under `prolog_message` from the Prolog server and
  `logtalk_message` from the Logtalk one (ADR-067 D5). The kernel reads
  whichever is present. The text is the system's own: the Prolog server
  sends the line Shumway's top level shows;
- `output`, when the term wrote something before it failed or raised.

**A cell that does not parse** is answered with an error for the whole
request (`error` at the top level, code -4712), and none of it runs.

**`halt`:** `{ "status": "halt" }`. The server stopped, and the next execution
needs a new session.

If any term failed, the Jupyter reply for the whole cell is an error,
reported for the **first** error, as in Hercutalk.

### Error codes

| Code | Name |
|---|---|
| -32700 | parse_error: the line is not JSON |
| -32600 | invalid_request |
| -32601 | method_not_found |
| -32602 | invalid_params |
| -32603 | internal_error |
| -4711 | failure |
| -4712 | exception |
| -4713 | no_active_call |
| -4714 | invalid_json_response |
| -4715 | unhandled_exception |

## 4. Additional-data keys (in a `success` entry)

| Key | Payload | Kernel renders as | Phase (ADR-072) |
|---|---|---|---|
| `predicate_atoms` | list of predicate heads, arguments lettered (`append(A,B,C)`, `jupyter:print_table(A)`) | completion data, no output | 1 |
| `print_table` | `{"VariableNames": [...], "ValuesLists": [[...], ...]}` | `text/markdown` table plus `text/plain` | 1 |
| `print_and_save_table` | as above, plus format and file | table; **the server writes the file** | 1 |
| `print_sld_tree` | DOT source | `image/svg+xml` drawn by dagre, plus the DOT as `text/vnd.graphviz` and `text/plain` (ADR-072) | 2 |
| `print_transition_graph` | DOT source | as above | 2 |
| `show_data` | plot description (matplotlib-style) | **Vega-Lite** spec built by the kernel (§6) | 3 |
| `input_html` | HTML with callbacks | `text/html`, once the callback is configurable | 4 |
| `set_prolog_backend` | backend name | `not_applicable` on Shumway | |
| `display_data` **[ext]** | list of `{"mime": "...", "data": "..."}`, one for each call of `jupyter:display/2` in the query, in order | each as that MIME, plus `text/plain`; where the MIME's name ends in `json`, the data is parsed | 1 |
| `residual_goals` **[ext]** | list of goal strings | shown after the bindings, one to a line | 1 |

`display_data` is a list because a query may show more than one thing, a
figure for each solution it walks, say, where a table is one for a query.
`data` is text: markup for `image/svg+xml`, `text/html` and `text/markdown`,
base64 for an image that is not text, and JSON text for a MIME such as
Vega-Lite's. `jupyter:display/2` is not exported: `display/2` is a name a
program may want for itself.

`residual_goals` holds the constraints left on the variables of the query
(`clpfd`, `dif`, `freeze`), as the goals Shumway's top level shows, each
written with the query's variable names:
`"residual_goals": ["X in 1..5", "X#<Y"]`. The key is absent when there are
none. It only adds information: a client that does not know it still shows
`dom` as before. Accepted for the Prolog variant on 2026-11-16 (plan, O2),
and proposed to Hercutalk so that both variants show constraints alike.

## 5. Cell semantics (summary, authoritative in `jupyter_term_handling`)

- **Every term goes through `handle_query_term`.** The header comment of
  `jupyter_term_handling` still lists "clause definitions" as a term type, but
  in 0.47.0 the entry point `handle_term/5` sends every term to
  `handle_query_term/6`. **Program code is entered with cell magics**
  (`%%user`, `%%file`, …), which write a file and load it. Treat the code as
  the authority, and confirm with Paulo Moura that the comment is outdated.
- **The Prolog variant keeps this rule** (plan, O1, decided 2026-11-16). A
  fact in an ordinary cell cannot be told from a query, so a cell is never
  loaded as a program by guessing. A term that can only be a clause (a rule,
  a directive, a grammar rule) is an error whose text points to `%%user`.
- **A cell may hold several queries.** Each is answered in order, under its
  position, and an error or a failure in one does not stop the next.
- **`$Var` in a query** is the latest value an earlier query gave `Var`. A
  variable left unbound has none, and neither has one whose name starts with
  an underscore. When the query has no other use of `Var`, `Var` is shown
  among its bindings with that value.
- **`retry`/`jupyter:retry`** asks the latest query for its next solution.
  The queries that had a solution are a stack: when the latest has no more,
  `retry` answers a failure and the one before it becomes the latest. With
  no query left, the error is `no_active_call`.
- **`halt`/`jupyter:halt`** answers `{"status": "halt"}` and stops the
  server. What follows it in the cell does not run.
- **Bindings.** A variable is shown with its value written with quotes where
  needed. One left unbound is not shown, unless it is the same variable as
  an earlier one of the query. A variable whose name starts with an
  underscore is not shown.
- **Output.** What a query writes to standard output or standard error is
  its `output`, less one final line end.
- **Cell magics** are recognised on the first line of the cell: `%%user`,
  `%%user+`, `%%file F`, `%%file+ F`, `%%save F`, `%%load F`, `%%highlight`,
  `%%table`, `%%csv F`, `%%tsv F`, `%%tree`, `%%data`. A first line that
  names none of them is a comment. In the Prolog variant `%%user` writes
  a file of the notebook and consults it, where Hercutalk writes `user.lgt`
  and loads it. The file of `NAME.ipynb` is `NAME.user.pl`, so notebooks in
  one directory keep apart; a call without `notebook` uses `user.pl`. A
  notebook renamed since its last `%%user` cell takes its file along, unless
  a file of the new name is already there. The load is a query of the cell:
  its answer carries what loading wrote.
- **Line magics** are a cell that is `%NAME` and nothing else: `%bindings`,
  `%queries`, `%help`, `%pwd`, `%magic`, `%versions`, `%flags`. Each runs a
  helper of `jupyter`.
- **Helpers.** A helper of module `jupyter` that shows more than text adds a
  key of §4 to the answer of its query. `print_table`, `print_and_save_table`
  and `print_queries` are also accepted without the module.

## 6. Vega-Lite from `show_data`

The server sends a `show_data` description as the reference's server does:
the `Key-Value` pairs of the query's `Data` (or `_Data`) as a JSON object,
`type` and the keys of the matplotlib function of that name, every atom as
text. The kernel turns it into a Vega-Lite v5 spec and emits it as
`application/vnd.vegalite.v5+json` plus a `text/plain` summary (ADR-072,
second amendment). Option names that cannot be mapped are reported in the
cell output, never dropped silently. The mapping lives with the code
(`data.ts`), and each plot type has a row in `jupyterlite-features.yaml`.

## 7. Interrupt and restart

- **Interrupt:** the kernel calls `SessionCancel`. The current behaviour
  matches Hercutalk: the server does not survive, the kernel recreates the
  session, and the cell output says so. A future engine ADR may make
  cancellation catchable, so the session keeps its state.
  JupyterLite 0.8.7 does not tell a kernel that it was interrupted: its
  `interrupt` drops the cells that wait and leaves the running one alone.
  The extension wraps that call so that the kernel hears of it.
  JupyterLite hands a cell to the kernel a little after it is sent, so an
  interrupt can come before the cell's query leaves: the kernel keeps when
  it came, and a cell sent before it runs nothing, keeps the session as it
  was, and says it was interrupted.
- **Restart:** `SessionDispose`, then `SessionCreate`.
- **`halt`** (§3): same as restart, triggered by the server.
- **A runtime that stops answering:** a step that waits for the runtime
  longer than `shumwayRuntimePatience` seconds (10) asks it a trivial call,
  `SessionLimit(0)`. One that does not answer in `shumwayRuntimeTimeout`
  seconds (30) takes the runtime for stopped: the cell, and every cell
  after it in the page, says to save the notebook and reload the page,
  which is the only thing that starts the runtime again. A query may take
  as long as it needs, since the trivial call answers while it runs. Seen
  in WebKit, where .NET's threads now and then stop taking calls from the
  page; a minimal .NET app with no Shumway in it does the same.
- **A runtime that does not start:** downloading and booting it has
  `shumwayRuntimeStartTimeout` seconds (180); after that, every cell of the
  page says to reload it. The steps of the start that ask the runtime
  something are watched as above.

## 8. Input

When a goal reads from `user_input`, the session raises an input request.
The kernel sends Jupyter an `input_request`, then forwards the reply with
`SessionSupplyInput`, or calls `SessionSupplyEndOfFile` when the input is
closed. JSON requests never travel on `user_input` (§1).

## 9. Where the Prolog server differs from Hercutalk on purpose

Each of these is declared, with its reason, in the test that compares the two
(`tests/Shumway.Tests.Wasm/NotebookServerCorpusTests.cs`). That test fails if
Hercutalk comes to answer as the Prolog server does, so the list stays true.

**Answers that differ:**

| Case | Hercutalk 0.47.1 | Prolog server |
|---|---|---|
| `X = 1, Y = $X.` | `X` is shown with its earlier value | `X` is the query's own variable; `$X` is the earlier value |
| A file of a cell magic that does not parse | a failure, with the reader's message in the output | an exception, with the reader's message as its text; nothing of the file is loaded |
| A helper inside a conjunction | fails: it has to be the whole query | works |
| The file of `print_and_save_table` | written by the client | written by the server, in the session's directory |
| A table format other than `csv` or `tsv` | passed on to the client | `domain_error(table_format, F)` |
| `enable_logging` | the text `"true"` | `false`: no log file is kept |
| `jupyter:update_completion_data.` | an empty result: the code that collects the names is commented out | a query like any other, whose answer has `predicate_atoms`: the helpers, the predicates the engine documents, the ones the session defined and the exports of the modules it loaded (`m:p(A)` where `user` does not import them) |

Hercutalk 0.47.0 differed in three more: a line that is not JSON stopped
it, a variable bound to another was shown as an atom with that variable's
name, and an empty list was a JSON array. They were reported and 0.47.1
answers as the Prolog server does.

The completion request is the one row the corpus does not hold: Hercutalk's
answer is empty, and ours is a list that changes with the engine's
predicates. `NotebookServerTests` checks what ours has.

**Text that is each system's own.** These are compared without their text:

- the text of an error message, and the name of its key (§3);
- the number in the name of a variable that has no name;
- what `%versions`, `%help`, `%magic`, `%flags`, `%pwd`, `%queries`,
  `print_query_time` and `jupyter_predicate_docs` say. A helper is written
  `jupyter:Name` here and `jupyter::Name` there;
- what `jupyter:trace(Goal)` prints. It is the engine's own tracer here.
  Hercutalk's is Logtalk's debugger, which prints nothing when the engine
  under it is Shumway.

A query that is `trace` alone is an error in both. In the Prolog server a
tracer that a query leaves on (`trace, Goal.`) goes off when the query ends,
so it never traces the server.

**Not visible on the wire.** In Hercutalk every query that had a solution
stays alive until a `retry` exhausts it. The Prolog server keeps alive only a
query that left choice points. The answers are the same, and the stack of a
session does not grow with its cells.
