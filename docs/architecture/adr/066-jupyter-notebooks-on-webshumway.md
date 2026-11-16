# ADR-066: Jupyter notebooks on WebShumway: scope, variants and identity

## Status

Proposed (2026-11-14). Design only; nothing is implemented. Records D1, D2,
D3 and D11 of [`jupyterlite-architecture.md`](../../design/jupyterlite-architecture.md).

## Context

WebShumway (ADR-042) runs the full engine in a static page. Notebooks are the
other common way people try and teach Prolog, and JupyterLite runs a whole
Jupyter front end as a static site, with kernels supplied by extensions. The
two fit: there is already no backend.

Logtalk's author maintains **Hercutalk** (`LogtalkDotOrg/logtalk-jupyter-kernel`),
a Jupyter kernel for Logtalk forked from hhu-stups' `prolog-jupyter-kernel`.
It has two halves:

- a Python half on top of `ipykernel`, which speaks Jupyter and launches the
  backend as a subprocess;
- a JSON-RPC server written in Logtalk, which runs in the backend and owns the
  cell semantics.

Hercutalk on the desktop already supports Shumway as a backend, through the
Logtalk adapter and the `shumwaylgt` script (issue #173).

## Decision

### D1: Web only

The work targets JupyterLite. Desktop notebooks are not in scope: Hercutalk
covers them with Shumway as the backend.

### D2: Two variants, two languages, one engine

- **Shumway Prolog:** plain Prolog on WebShumway. Built first, on its own.
- **Hercutalk web:** Logtalk on WebShumway, built later by reusing as much of
  the Prolog variant as possible.

Logtalk is a different language (objects, protocols, categories, `::`
messages, its own compiler and library) that happens to compile to Prolog.
Hercutalk can host plain Prolog notebooks, but every cell then goes through
Logtalk's compiler and its messages. The Prolog variant exists so that a
Prolog notebook is Prolog, and so that Shumway's own behaviour (builtins,
messages, `clpfd` with `@L`, conformance) is visible unfiltered. A smaller
download and a faster start on tablets come with that, but they are side
benefits.

Every point of the Prolog variant is decided by asking whether it keeps
Hercutalk web easy to build. A deviation needs a stated reason.

### D3: Ownership and dependency direction

Dependencies point one way: Logtalk and Hercutalk depend on Shumway, never
the reverse.

| Piece | Lives in |
|---|---|
| Engine, WebShumway runtime packages, kernel core, Prolog variant | Shumway |
| Logtalk adapter, `shumwaylgt`, embedded Logtalk core for Shumway | `logtalk3` |
| Hercutalk web (JupyterLite variant) | Hercutalk, or a separate integration repo |

How Hercutalk web is distributed is to be agreed with Paulo Moura. Logtalk
is Apache-2.0, so bundling the embedded core in a site is allowed. It is
nevertheless a separate artifact and stays out of Shumway's repository.

### D11: Kernel identity

The kernelspec name identifies the **implementation**; `language_info`
identifies the **language**.

| Field | Shumway Prolog | Hercutalk web |
|---|---|---|
| kernelspec name | `shumway_prolog` | `logtalk_kernel` (unchanged) |
| display name | `Prolog (Shumway)` | as in Hercutalk |
| `language_info.name` | `Prolog` | `Logtalk` |
| `file_extension` | `.pl` | `.lgt` |
| `mimetype` | `text/x-prolog` | `text/x-logtalk` |
| `codemirror_mode` | `prolog` | `logtalk` |

The Prolog `language_info` matches hhu-stups' `prolog_kernel` exactly, so
GitHub, nbviewer and JupyterLab recognise these notebooks as Prolog.

- **We do not take `prolog_kernel` as our name.** The behaviour comes from the
  same origin but is not identical, and borrowing the name would send bug
  reports to the wrong project.
- **Notebooks written for other Prolog kernels still open without prompting.**
  `shumway_prolog` is configured as the site's default kernel, and because the
  semantics share an origin, most of those notebooks run unchanged.
- **`kernel_info` declares `implementation: shumway` and its version.** The
  version that last ran a notebook is also written into its metadata, so a
  bug report arrives with context.
- **Hercutalk web keeps Hercutalk's exact identity.** It is the same
  implementation behind a different transport, so one `.ipynb` runs on the
  desktop and on the web.

## Consequences

- The Prolog variant has no dependency on Logtalk and can ship without it.
- Hercutalk web is cheap only if the Prolog variant stays protocol-compatible
  (ADR-067) and the kernel core stays server-agnostic (ADR-073).
- Notebooks are not shared between the two variants. That is intended: they
  are different languages.

## Alternatives considered

- **Hercutalk alone, with plain-Prolog cells.** Rejected because Logtalk would
  sit between the user and Prolog (D2).
- **Integration repo inside Shumway.** Rejected because it would make Shumway
  depend on Logtalk (D3).
- **Reusing the name `prolog_kernel`.** Rejected for the reasons in D11.

## Related

ADR-042 (WebShumway), ADR-067 to ADR-073.
