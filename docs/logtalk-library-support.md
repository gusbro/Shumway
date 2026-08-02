# Logtalk library support

Status of running **Logtalk 3.101.0's bundled libraries** on Shumway, measured
by executing each library's own lgtunit test suite (`tester.lgt`) on a fresh
Logtalk-on-Shumway boot — 240 library test suites swept. Logtalk itself boots
and runs fully on Shumway (see [`logtalk.md`](logtalk.md) for setup); this
document is about its **library** collection, which in 3.101.x is large and
includes machine-learning, geospatial, telemetry (CCSDS) and web-format
libraries beyond the classic data-structure set.

## Headline numbers

- **105 of 240 test suites pass completely** (every test green).
- **87 pass partially** with only **129 failing tests corpus-wide** — **98.7%
  of all individual tests pass** (10,014 of 10,143 executed).
- **41 suites are not applicable** (network/JVM bindings, or testers gated to
  a hardcoded backend whitelist that prints "(not applicable)").
- **7 time out** under the parallel sweep — heavy ML/optimization compute;
  several of these passed in a sequential run, so treat the timeouts as
  load-sensitivity, not failures.

The two engine arcs this campaign drove — unbounded-integer shift promotion
and ADR-041 (dispatch-time clause selection: chains select on the first
argument; a single-entry chain is selected choice-point-free) — moved the
corpus from 75 green suites and 83% of tests to these numbers. Notably,
suites previously misattributed to missing capabilities turned out to be
that determinism bug: crypto is 121/121, the CCSDS framing stack is fully
green, ieee_754 has zero failures.

## ✅ Fully green test suites (105)

Everything previously green plus the suites the determinism fix released:
the whole JSON family (incl. json_graph 50/50), YAML/TOML, Avro + Protobuf +
MessagePack, the classic data structures, grammars, meta/meta_compiler,
**random 457/457**, **types 149/149**, **linear_algebra 72/72**,
**ieee_754 0 failures**, **crypto 121/121**, the **CCSDS framing stack**
(ccsds_frames 93/93, ccsds_tc_services 35/35), **reader 64/64**, several ML
classifiers (kNN, naive Bayes, random forest, C4.5, nearest centroid), and
the id/format/geospatial families.

## 🟡 Under review — non-passing tests NOT known to be structural

Per project policy, a failing test that is not explained by a missing
capability is presumed to be a Shumway bug until proven otherwise. The
current review list (129 failing tests corpus-wide):

| suite | failing | first-look note |
|---|---|---|
| `os` | 11/156 | file-system corners; partially platform-semantics, needs per-test triage |
| `jwt` | 8/37 | NOT crypto (crypto itself is 121/121) — review |
| `tzif`, `mime_types` | 5 each | binary timezone parsing / table lookups — review |
| `union_find`, `http_core` | 4 each | union_find is a pure data structure — review first |
| `dimension_reduction_protocols` | 3 | review |
| ~30 suites (ML regressions/rankers/clusterers, cbor, csv, geojson, time_scales, …) | 1–2 each | likely a shared numeric/edge cause — sample a few, look for a common root |

7 timeouts under the parallel sweep (`c45_classifier`,
`linear_svm_classifier`, `logistic_regression_classifier`, heavy anomaly
detectors, `simulated_annealing`, `http_directory_listing`) — several of
these pass in a sequential run; treat as compute/load sensitivity, not
failures, and verify sequentially when triaging.

## ❌ Not applicable / needs missing infrastructure

- **Network / external services**: the `http_*` stack, `sockets`, `redis`,
  `memcached`, `s3`, `stomp`, `amqp`, `linda`, `open_ai`, `open_id`,
  `gravatar`, `git`, `url` — need socket/process infrastructure.
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
