# ISO conformity suites (Neumerkel)

Runners for Ulrich Neumerkel's ISO-Prolog conformity test sets
(<https://www.complang.tuwien.ac.at/ulrich/iso-prolog/conformity_testing>):
the **Syntax (Part I)** suite (365 tests), **number_chars/2** (67),
**variable_names/1** (63), **dif/2** (26), **length/2** (37),
**phrase/2,3** (58) and **setup_call_cleanup/3** (23).

The whole toolchain is Prolog and runs on the engine under test: it
**fetches** the seven sources (five HTML pages plus the machine-readable
`length_quad.pl` / `phrase_quad.pl`), **extracts** the test rows,
**generates** fact files, and **runs** the suites. Results go to the screen
and to `artifacts/results.txt`.

The repository carries **no third-party content**: everything derived from
the pages lives in `artifacts/`, which is generated at run time and
git-ignored. (The sources are fetched from the live site; if you want to
run offline, place the seven files in `artifacts/` by hand — the fetch
stage skips files that already exist.)

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
| `quad_suites.pl` | length (37) + phrase (58), parsed from the site's MACHINE-READABLE `*_quad.pl` files by one shared parser; `length_run` / `phrase_run` |
| `cleanup_suite.pl` | setup_call_cleanup (23), extracted from the draft page's example blocks; `cleanup_run` |
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
| `conformity_timed_call(G, Ms, O)` | bounded call for tests whose sanctioned outcomes include `loops`: O is succeeds/fails/error(C)/`timeout`, and timeout at 15 s IS the loops outcome (engines without a timeout facility run unbounded — their bounded default stacks make these tests err quickly) |
| `conformity_deep/0` | true when loops-or-resource tests must run UNBOUNDED and actually END in the resource error (`CONFORMITY_DEEP=1` where the engine can read the environment). On Shumway the default heap is unlimited — constrain the process for a quick proof, e.g. `DOTNET_GCHeapHardLimit=0x20000000` (512 MB) |

Per-engine notes:

- **Shumway** — `http_download/2` is a builtin; `library(coroutining)`
  provides dif/freeze.
- **Scryer** — `library(http/http_open)` for the fetch;
  `library(format)`, `library(dif)`, `library(freeze)`, plus
  `library(lists)` and `library(iso_ext)` for the length/cleanup suites.
  `library(dcgs)` loads LATE, through the `conformity_pre_quads` hook:
  it defines `op(1100, xfy, '|')`, which would break syntax test 285,
  and the engine refuses `op/3` on `|` so it cannot be removed again.
  Neither `consult/1` nor `include/1` works as a *directive*; the driver
  consults inside `initialization`.
- **SWI** — `library(http/http_open)` + binary `copy_stream_data`; the
  driver sets `double_quotes=codes` (SWI's default `string` would break
  every `"..."` test).
- **GNU Prolog** — no HTTP library: the fetch shells out to `curl` via
  `system/1` (or pre-fetch by hand). No `dif/2`: that suite reports as
  skipped. `number_chars` #46 (a cyclic char list) segfaults the engine —
  an uncatchable crash — so the driver lists it in `conformity_skips`.

## Results (2026-08-31)

| Suite | Shumway | Scryer 0.10.0 (e7ac3ae) | GNU Prolog 1.5.0 | SWI 10.0.2 |
|---|---|---|---|---|
| syntax (365) | **365** | 357 | 357 | 276 |
| number_chars (67) | **67** | 66 | 59 (+1 crash-skip) | 50 |
| variable_names (63) | **63** | 60 | 57 | 38 |
| dif (26) | **26** | 25 | n/a | 25 |
| length (37) | **37*** | 36 (+1 skip) | 26 (+5 crash-skips) | 24 (+1 skip) |
| phrase (58) | **58** | 57 | 52 | 45 |
| cleanup (23) | **23** | 23 | 12 | 23 |

\* #21/#22/#30 (`length(L,L)`-shaped and a freeze-driven infinite list)
sanction `loops` among their outcomes and pass the default run through
`conformity_timed_call` (still running at 15 s = looping). The
`CONFORMITY_DEEP=1` run upgrades the resource-preferred pair #21/#22 to
unbounded — they must actually END in the catchable `resource_error` —
verified on Shumway 37/37 under `DOTNET_GCHeapHardLimit=0x40000000`
(1 GB; the cap makes the exhaustion arrive in seconds). #30 stays
time-bounded even under deep: its freeze-driven loop meets a resource
wall only after hours of attribute wakeups.

Per-engine skips on the new suites: Scryer and SWI skip length #30
(Scryer has no timeout facility for the loops check; SWI's alarm cannot
interrupt the uninterruptible kernel loop the goal drives it into); GNU
Prolog skips length #21/#22 (a FATAL global-stack abort on 1.5.0 where
1.6.0 raises resource_error) and the cyclic #26/#27/#28 (the same
uncatchable-crash family as its number_chars #46).

Cross-check: the conformity page's own scoreboard lists Scryer 0.10.0 at
357, and this runner reproduces that number exactly. It lists GNU Prolog
at 360 for 1.6.0, which GNU distributes as "unstable" (sources plus
ARM64 installers only); the 1.5.0 tested here is the current stable
release, which likely accounts for the 3-test difference.

## Licensing note

The test DATA (queries and expected answers) belongs to the conformity
pages' author and is **not** redistributed here: it is downloaded from the
live site into the git-ignored `artifacts/` directory when the suite runs.
The code in this directory — extractors, generators, harnesses, drivers —
is Shumway's own (MIT, like the rest of the repository).
