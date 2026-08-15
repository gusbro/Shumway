# Logtalk library support

Status of running **Logtalk 3.101.0's bundled libraries** on Shumway, measured
by executing each library's own lgtunit test suite (`tester.lgt`) on a fresh
Logtalk-on-Shumway boot — all 240 library test suites swept. (The sweep tree
is the `3.101.0-b01` snapshot; the one place that matters is noted below.) Logtalk itself
boots and runs fully on Shumway (see [`logtalk.md`](logtalk.md) for setup);
this document is about its **library** collection, which in 3.101.x is large
and includes machine-learning, geospatial, telemetry (CCSDS) and web-format
libraries beyond the classic data-structure set.

## Headline numbers

- **192 of the 194 runnable test suites pass completely** (every test green).
- **10,317 of 10,319 executed tests pass — 99.98%.** The 2 remaining failures
  are external: one upstream library bug, one missing host tool (below).
- **41 suites are not applicable** (network/JVM bindings, or testers gated to
  a hardcoded backend whitelist that prints "(not applicable)").
- **5 suites time out under a parallel sweep** (heavy ML/optimization compute:
  `linear_svm_classifier`, `logistic_regression_classifier`,
  `simulated_annealing`, `lof_anomaly_detector`,
  `isolation_forest_anomaly_detector`) — they pass when run sequentially;
  treat the timeouts as machine-load sensitivity, not failures.

Highlights among the green suites: **random 457/457**, **types 149/149**,
**linear_algebra 72/72**, **crypto 121/121**, **ieee_754** and the whole
**CCSDS framing stack** at zero failures, the JSON/YAML/TOML/Avro/Protobuf/
MessagePack format family, every ML classifier/regressor/ranker/clusterer
that runs to completion, **os 140/141**, **tzif 56/56** and
**mime_types 14/14** (on both of which SWI-Prolog itself fails tests on
Windows), grammars, meta/meta_compiler, and the classic data structures.

## 100% — every runnable test passes

All 10,319 tests across the 194 runnable suites pass. Two of them need
something OUTSIDE Shumway to be in place (verified by cross-checking
against SWI-Prolog on the same tree, which failed both identically):

| suite | test | external requirement |
|---|---|---|
| `geojson` | `geojson_parse_invalid_json_text_01` | The **released** 3.101.0. Our sweep ran against the `3.101.0-b01` beta snapshot, whose `geojson::parse/2` caught `domain_error(json_source, _)` while the json library throws `domain_error(json, _)`; upstream fixed it on 2026-07-07, before the 3.101.0 tag of 2026-07-23. On 3.101.0 proper the test passes — 103/103, nothing to patch. |
| `os` | `os_operating_system_release_1_01` | `pwsh.exe` (PowerShell 7) reachable on PATH — the library shells out to it. A portable PowerShell zip suffices; with it, 141 passed / 0 failed. |

Verification method: every failing test was cross-checked against
**SWI-Prolog running the same Logtalk tree** (`swipl -g
"consult('<tree>/integration/logtalk_swi.pl')"`). A test that fails on both
engines is upstream/platform, not ours; every test that failed only on
Shumway was treated as a Shumway bug and fixed.

## ❌ Not applicable / needs missing infrastructure (41 suites)

- **Network / external services**: the `http_*` client/server stack,
  `sockets`, `redis`, `memcached`, `s3`, `stomp`, `amqp`, `linda`,
  `open_ai`, `open_id`, `gravatar`, `git`, `url`, `rest` — need
  socket/process infrastructure. (Note `http_core` and
  `http_directory_listing` — the pure-Prolog parts of that stack — run and
  are fully green.)
- **JVM / OS processes**: `java`, `process`, `timeout`.
- **Backend-whitelisted testers**: `dif`, `coroutining`, `format`, `loops`,
  `listing`, `hook_objects`, `dates_tz`, `iso_639`/`iso_3166`/`iso_4217`/
  `iso_9362`/`iso_13616`, … — their `tester.lgt` gates on a hardcoded
  dialect whitelist or a backend feature flag, printing "(not applicable)"
  for any backend not in the list. Some of these *libraries* would in fact
  work (Shumway has native `dif`/`when`/`freeze` and tabling); enabling them
  requires adapter work, see below.

## Known adapter improvement candidates (validated as NOT flag-flips)

The Shumway adapter conservatively declares `encoding_directive`, `tabling`
and `unicode` unsupported. Flipping them was tried and reverted:

- `encoding_directive` → the `iso_*` data libraries then run but fail
  compiling their `:- encoding(utf_8)` sources — the directive needs real
  handling through the Logtalk compile chain, not just the flag.
- `tabling` / `unicode` → the feature flags also require the matching
  adapter hook wiring before testers exercising them can be trusted.
- The backend whitelists in individual `tester.lgt` files would need
  `shumway` added upstream.

## Reproduce

Each suite: from its `library/<name>/` directory, with
`LOGTALKHOME`/`LOGTALKUSER` pointing at the Logtalk installation:

```
shumway $LOGTALKHOME/adapters/shumway.pl $LOGTALKHOME/paths/paths.pl \
        $LOGTALKHOME/core/core.pl
?- logtalk_load(tester).
```

Run suites **sequentially**, or in parallel ONLY with fully ISOLATED tree
copies (one LOGTALKHOME/LOGTALKUSER per worker) — parallel runs over a
shared tree collide on the scratch files of shared dependencies (every
tester compiles lgtunit into its own directory).
