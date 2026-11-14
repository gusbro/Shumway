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
| `call` | `{"code": "<cell text>"}` | per-term results (§3), or `""` for an empty cell | the core of everything |
| `backend` | none | atom: the backend's `prolog_dialect` | answers `shumway` |
| `enable_logging` | none | boolean: whether a log file was created | the log goes to the console or a MEMFS file |
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

The `data` object carries the error message and any output. The exact
fields are those built by `jupyter_jsonrpc::json_error_term/5`.

**`halt`:** `{ "status": "halt" }`. The server stopped, and the next execution
needs a new session.

If any term failed, the Jupyter reply for the whole cell is an error,
reported for the **first** error, as in Hercutalk.

### Error codes

| Code | Name |
|---|---|
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
| `predicate_atoms` | list of predicate names | completion data, no output | 1 |
| `print_table` | `{"VariableNames": [...], "ValuesLists": [[...], ...]}` | `text/markdown` table plus `text/plain` | 1 |
| `print_and_save_table` | as above, plus format and file | table; **the server writes the file** | 1 |
| `print_sld_tree` | DOT source | `image/svg+xml` via Graphviz WASM, plus DOT as `text/plain` | 2 |
| `print_transition_graph` | DOT source | as above | 2 |
| `show_data` | plot description (matplotlib-style) | **Vega-Lite** spec built by the server (§6) | 3 |
| `input_html` | HTML with callbacks | `text/html`, once the callback is configurable | 4 |
| `set_prolog_backend` | backend name | `not_applicable` on Shumway | |
| `display_data` **[ext]** | `{"mime": "...", "data": "..."}`, from `jupyter:display/2` | that MIME, plus `text/plain` | 1 |
| `residual_goals` **[ext]** | per variable, list of goal strings | appended to the bindings | 1 |

`residual_goals` only adds information; a client that does not know it
still shows `dom` as before. Its final shape is settled together with
Hercutalk (ADR-072).

## 5. Cell semantics (summary, authoritative in `jupyter_term_handling`)

- **Every term goes through `handle_query_term`.** The header comment of
  `jupyter_term_handling` still lists "clause definitions" as a term type, but
  in 0.47.0 the entry point `handle_term/5` sends every term to
  `handle_query_term/6`. **Program code is entered with cell magics**
  (`%%user`, `%%file`, …), which write a file and load it. Treat the code as
  the authority, and confirm with Paulo Moura that the comment is outdated.
- **Open for the Prolog variant (plan, O1):** whether a cell that contains
  only clauses is accepted directly, as in hhu-stups' `prolog_kernel`, as an
  **[ext]** that behaves like an implicit `%%user+`.
- **`$Var` in a query** is replaced by the latest value of `Var` from a
  previous query (`jupyter_variable_bindings`).
- **Queries:**
  - `retry`/`jupyter:retry` makes the previous goal compute its next
    solution (ADR-067 explains the nested loop);
  - `halt`/`jupyter:halt` stops the server;
  - calls to the special `jupyter` predicates;
  - `trace/0,1,2` and `leash/1`;
  - any other term when it is the only one in the cell.
- **Cell magics** are recognised by prefix at the start of the cell:
  `%%user`, `%%user+`, `%%file`, `%%file+`, `%%save`, `%%load`,
  `%%highlight`, `%%table`, `%%csv`, `%%tsv`, `%%tree`, `%%data`. There are
  also line magics. In the Prolog variant, `%%user` writes `user.pl` and
  consults it, where Hercutalk writes `user.lgt` and loads it.

## 6. Vega-Lite from `show_data` [ext, proposed upstream]

The server turns a `show_data` description into a Vega-Lite v5 spec. The
kernel emits it as `application/vnd.vegalite.v5+json` plus a `text/plain`
summary. Option names that cannot be mapped are reported in the cell output,
never dropped silently. The mapping table lives with the code and is
mirrored in `jupyterlite-features.yaml`.

## 7. Interrupt and restart

- **Interrupt:** the kernel calls `SessionCancel`. The current behaviour
  matches Hercutalk: the server does not survive, the kernel recreates the
  session, and the cell output says so. A future engine ADR may make
  cancellation catchable, so the session keeps its state.
- **Restart:** `SessionDispose`, then `SessionCreate`.
- **`halt`** (§3): same as restart, triggered by the server.

## 8. Input

When a goal reads from `user_input`, the session raises an input request.
The kernel sends Jupyter an `input_request`, then forwards the reply with
`SessionSupplyInput`, or calls `SessionSupplyEndOfFile` when the input is
closed. JSON requests never travel on `user_input` (§1).
