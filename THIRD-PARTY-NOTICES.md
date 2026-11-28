# Third-party notices

Shumway is licensed under the [MIT License](LICENSE). The following components
have their own terms.

## The `vs/` Visual Studio debugger projects (opt-in build)

- Shumway's own code under `vs/` is MIT like the rest of the repository.
- Those projects reference **Microsoft.VisualStudio.Debugger.Engine** and
  related Visual Studio SDK components, which are licensed under the
  **Microsoft Visual Studio SDK license terms**, not MIT. They are consumed
  as NuGet references and redistributed only inside the VSIX per those terms.
- The Concord integration follows patterns from **PTVS** (Python Tools for
  Visual Studio, Apache License 2.0) and **Iris** sample plumbing (MIT),
  acknowledged here.

## The notebook site (`jupyterlite/`)

- The site is built with **JupyterLite** and **JupyterLab** (BSD 3-Clause
  License), installed when it is built and published with it; neither is
  vendored in this repository.
- Its kernel extension bundles **dagre** and **graphlib**
  (`@dagrejs/dagre`, `@dagrejs/graphlib`, MIT License), which lay out the
  graphs a notebook draws. The site downloads them only for a notebook that
  draws one.

## Acknowledgments

- **Logtalk** (<https://logtalk.org/>, Apache License 2.0) — Shumway ships a
  backend adapter for Logtalk under `logtalk/`. The adapter is Shumway's own
  code (MIT), written against Logtalk's public backend-adapter interface; no
  Logtalk source is vendored in this repository.
