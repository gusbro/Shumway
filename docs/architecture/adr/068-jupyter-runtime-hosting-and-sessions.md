# ADR-068: Hosting the runtime in JupyterLite: one engine per kernel

## Status

Proposed (2026-11-14). Design only; nothing is implemented. Records D6 and
D7. This changes the threading model of the web host, which
`decision-policy.md` names a major decision. It must be accepted before
`Shumway.Web` is refactored.

## Context

- **Hosting.** ADR-042 §3: the .NET runtime runs with `WasmEnableThreads`,
  every `[JSExport]` returns a `Task`, engine work is serialised behind one
  gate, and cancellation stays outside the gate. *Hosting a JS worker that
  owns the runtime was tried first and does not boot.*
- **Today's host is a singleton.** `WebShumwayApp` holds one engine with its
  own thread (`_engineThread`), `QueryCancel`, `EngineReset`, and page input
  (`PageInput.cs`: `AskForInput`, `SupplyInput`, `SupplyEndOfFile`).
- **Jupyter needs isolation.** JupyterLite can have several notebooks open in
  one page, each with its own kernel, and Jupyter semantics require each
  kernel's state to be isolated. The runtime, however, loads **once per
  page**.
- **Several engines in one runtime already work.** ADR-042 §7: compilation
  builds its own ephemeral engine next to the session's, and "two engines at
  once is the threading model working as designed".

## Decision

### D6: The runtime lives in the JupyterLite page

The kernel extension loads the runtime into the page, as the WebShumway
demo does. There is no Web Worker; the TypeScript kernel calls `[JSExport]`
methods directly.

- **Interrupt** maps to the session's `QueryCancel` (with the consequences in
  ADR-067 D5).
- **Restart** discards the session and creates a new one.
- **`read/1` and friends** use the session's page input. When the engine
  asks for a line, the kernel sends Jupyter an `input_request`, and its
  `input_reply` is supplied as the line. Closing the input supplies end of
  file.

### D7: One engine per kernel, one runtime per page, with a limit

- **Sessions replace the singleton.** The static engine becomes a *session*
  object: an engine, its thread, the request and response pipes of ADR-067,
  a cancellation source, page input and a working directory (ADR-070/071).
  Sessions are keyed by Jupyter's `kernelId`.
- **Lifetime.** A session is created when the kernel starts (load the bundle,
  boot the server) and lives until shutdown or restart. Cells never create
  engines.
- **The limit.** A configurable maximum number of live sessions per page.
  Creating one beyond the limit **fails with a clear message**; idle sessions
  are never evicted silently, because the user would lose state without
  noticing.
- **The default** is set by measurement (plan, verification V2): the memory
  of an engine with the server loaded, on a desktop browser and on an iPhone.
- **The WebShumway demo** is rewritten as a host with exactly one session, so
  there is one code path.

Sketch of the export surface (names are not binding):

```
Task<string> SessionCreate(string kernelId, string bundle, string cwd)
Task<string> SessionRequest(string kernelId, string jsonLine)   // ADR-067
Task<bool>   SessionCancel(string kernelId)
Task         SessionSupplyInput(string kernelId, string line)
Task         SessionSupplyEndOfFile(string kernelId)
Task         SessionDispose(string kernelId)
```

The engine reports, through a `[JSImport]` callback carrying the
`kernelId`, that it wants input or that it wrote output. This is the
per-session counterpart of today's `ui.askForInput`.

## Consequences

- Each notebook keeps its own database, flags and operators, and nothing
  leaks between kernels, with one exception: the working directory, which is
  process-global today (ADR-071).
- Per-session state, previously static, has to be audited for anything else
  that is still global. Anything found goes into this ADR's implementation
  notes.
- Memory grows with each open notebook, which is the reason for the limit.
- ADR-042 §3 still holds per session. It is the number of sessions that
  changes.

## Alternatives considered

- **One Shumway kernel per page.** Simplest, but it breaks as soon as a user
  opens two notebooks. Rejected.
- **A runtime per kernel in Web Workers.** Rejected: ADR-042 records that a
  worker-hosted runtime does not boot, and each one would cost a full
  runtime's memory.
- **Silently evicting idle sessions at the limit.** Rejected because of
  invisible state loss.

## Related

ADR-042 §3 and §7, ADR-067, ADR-070, ADR-071.
