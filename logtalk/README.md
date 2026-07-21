# Logtalk support for Shumway

The glue files that make Shumway a [Logtalk](https://logtalk.org/) backend
Prolog compiler, mirroring the layout of a Logtalk installation:

- `adapters/shumway.pl` — the backend adapter (what Logtalk's core consults to
  learn the host Prolog's capabilities). Derived from Logtalk's GNU Prolog
  adapter; Apache-2.0, header preserved.
- `integration/logtalk_shumway.pl` — the one-file launcher (consults adapter +
  paths + core via `:- initialization`).

**Install**: copy both files into the matching directories of your Logtalk
installation. Full instructions, verified-version notes, and test-suite /
benchmark status: [`../docs/logtalk.md`](../docs/logtalk.md).
