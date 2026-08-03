# Third-party notices

Shumway is licensed under the [MIT License](LICENSE). The following components
have their own terms.

## Binary dependencies (NuGet; not vendored in this repository)

- **Sigil** — IL emission helper used by the Tier-1 runtime compiler.
  Microsoft Public License (MS-PL).

## The `vs/` Visual Studio debugger projects (opt-in build)

- Shumway's own code under `vs/` is MIT like the rest of the repository.
- Those projects reference **Microsoft.VisualStudio.Debugger.Engine** and
  related Visual Studio SDK components, which are licensed under the
  **Microsoft Visual Studio SDK license terms**, not MIT. They are consumed
  as NuGet references and redistributed only inside the VSIX per those terms.
- The Concord integration follows patterns from **PTVS** (Python Tools for
  Visual Studio, Apache License 2.0) and **Iris** sample plumbing (MIT),
  acknowledged here.

## Acknowledgments

- **Logtalk** (<https://logtalk.org/>, Apache License 2.0) — Shumway ships a
  backend adapter for Logtalk under `logtalk/`. The adapter is Shumway's own
  code (MIT), written against Logtalk's public backend-adapter interface; no
  Logtalk source is vendored in this repository.
