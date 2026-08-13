# ISO conformity suites (Neumerkel)

Runners for Ulrich Neumerkel's ISO-Prolog conformity test sets
(<https://www.complang.tuwien.ac.at/ulrich/iso-prolog/conformity_testing>):
the **Syntax (Part I)** suite (365 tests), **number_chars/2** (67),
**variable_names/1** (63) and **dif/2** (26).

The whole toolchain is Prolog and runs on the engine under test: it
**fetches** the four pages, **extracts** the test rows from the HTML,
**generates** fact files, and **runs** the suites. Results go to the screen
and to `artifacts/results.txt`.

The repository carries **no third-party content**: everything derived from
the pages lives in `artifacts/`, which is generated at run time and
git-ignored. (The pages are fetched from the live site; if you want to run
offline, place the four HTML files in `artifacts/` by hand — the fetch stage
skips files that already exist.)

## Running

Always from this directory (paths are relative).

| Engine | Command |
|---|---|
| Shumway | `dotnet run --project ../../src/Shumway.Repl -c Release -- run_shumway.pl` |
| Scryer | `scryer-prolog run_scryer.pl` |
| SWI | `swipl run_swi.pl` |
| GNU Prolog | compile first (see below), then run `conformity_gprolog.exe` |

GNU Prolog on Windows is a GUI-subsystem binary — do not pipe its stdio.
The reliable way is a `gplc`-compiled console executable, with stacks large
enough for the 240 KB page scan:

```
gplc --no-top-level --global-size 256000 --local-size 64000 --trail-size 64000 \
     run_gprolog.pl -o conformity_gprolog.exe
```

(`gplc` links via MSVC — run it from a `vcvars64` environment. On Unix,
`gplc` alone is enough, or `gprolog --consult-file run_gprolog.pl` from a
real terminal.)

## Layout

| File | Contents |
|---|---|
| `run_<engine>.pl` | per-engine DRIVER: the glue hooks + loads the rest + `conformity_main` |
| `conformity.pl` | engine-agnostic main: fetch → extract → generate → run; reporting |
| `html_scan.pl` | shared scanning and ISO-only utility predicates (`cf_`/`scan_` prefixes) |
| `syntax_suite.pl` | the 365: extract + generate + harness + `syntax_run` / `syntax_audit` |
| `number_chars_suite.pl` | the 67: extract + generate + `nc_run` |
| `variable_names_suite.pl` | the 63: generate + `vn_run` |
| `dif_suite.pl` | the 26: generate + `dif_run` |
| `artifacts/` | generated: pages, tsv, facts, temp files, `results.txt` (git-ignored) |

Entry points are marked with `%% entry:` header comments (Scryer rejects a
`:- public` directive with `domain_error(directive)`, so the files carry
none).

## Portability

The common files are **strict ISO Prolog** — every non-ISO convenience was
replaced: Edinburgh `tell/told/see/seen` by `open/4` + `set_output/1` /
`set_input/1`; `with_output_to/2` by an ISO-streams capture
(`cf_capture/3`); `read_term_from_atom/3` by a temp-file `read_term/3`;
`forall`, `msort`, `numbervars` by tiny `cf_` implementations; the pages
are read as **binary** (`get_byte/2` — their ISO-8859-1 bytes ARE Latin-1
code points), so no stream-encoding options are needed. Read failures are
classified WITHOUT inspecting the engine's error terms (their payload is
implementation-defined): a portable sentence scanner pre-computes, per
input unit, how many complete sentences it holds and whether its tail is a
valid-but-unterminated prefix ("waits") or lexically broken.

Everything engine-specific lives in the driver, which must define four
hooks (neither GNU Prolog nor Scryer supports `:- if` conditional
compilation, so one small file per engine replaces it):

| Hook | Purpose |
|---|---|
| `conformity_engine(Name)` | engine atom for the report |
| `conformity_fetch(URL, File)` | download URL's raw bytes to File |
| `conformity_skips(List)` | `Suite-Id` pairs the engine cannot RUN (crashes) |
| `cf_format/2,3` | `format` adaptation (Scryer wants a list format string; the rest take an atom) |

Per-engine notes:

- **Shumway** — `http_download/2` is a builtin; `library(coroutining)`
  provides dif/freeze.
- **Scryer** — `library(http/http_open)` for the fetch;
  `library(format)`, `library(dif)`, `library(freeze)`. Neither
  `consult/1` nor `include/1` works as a *directive*; the driver consults
  inside `initialization`.
- **SWI** — `library(http/http_open)` + binary `copy_stream_data`; the
  driver sets `double_quotes=codes` (SWI's default `string` would break
  every `"..."` test).
- **GNU Prolog** — no HTTP library: the fetch shells out to `curl` via
  `system/1` (or pre-fetch by hand). No `dif/2`: that suite reports as
  skipped. `number_chars` #46 (a cyclic char list) segfaults the engine —
  an uncatchable crash — so the driver lists it in `conformity_skips`.

## Results (2026-08-12, this machine)

| Suite | Shumway | Scryer e7ac3ae | GNU Prolog 1.5.0 | SWI 10.0.2 |
|---|---|---|---|---|
| syntax (365) | **365** | 357 | 357 | 276 |
| number_chars (67) | **67** | 66 | 59 (+1 crash-skip) | 50 |
| variable_names (63) | **63** | 60 | 57 | 38 |
| dif (26) | **26** | 25 | n/a | 25 |

Cross-check: the conformity page's own scoreboard lists Scryer at 357 —
this runner reproduces that number exactly. (It lists GNU at 360; the
3-test gap is version / protocol fine print.)

## Licensing note

The test DATA (queries and expected answers) belongs to the conformity
pages' author and is **not** redistributed here: it is downloaded from the
live site into the git-ignored `artifacts/` directory when the suite runs.
The code in this directory — extractors, generators, harnesses, drivers —
is Shumway's own (MIT, like the rest of the repository).
