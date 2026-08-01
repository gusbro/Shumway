# Logtalk library support

Status of running **Logtalk 3.101.0's bundled libraries** on Shumway, measured
by executing each library's own lgtunit test suite (`tester.lgt`) on a fresh
Logtalk-on-Shumway boot — 240 library test suites swept. Logtalk itself boots
and runs fully on Shumway (see [`logtalk.md`](logtalk.md) for setup); this
document is about its **library** collection, which in 3.101.x is large and
includes machine-learning, geospatial, telemetry (CCSDS) and web-format
libraries beyond the classic data-structure set.

## Headline numbers

- **75 of 240 test suites pass completely** (every test green).
- **119 pass partially** — across the sequentially re-verified suites, **83% of
  individual tests pass** (5,145 of 6,176); most partial suites fail a handful
  of tests, concentrated in the numeric cluster below.
- **~43 suites are not applicable or need infrastructure we don't provide**
  (network/OS/JVM bindings, or testers gated to a hardcoded backend whitelist).
- **3 suites time out** (heavy compute: `c45_classifier`,
  `isolation_forest_anomaly_detector`, `simulated_annealing`).

## ✅ Fully green test suites (75)

application, arrangements, assignvars, avro, base32, base58, base64, base85,
cartesian_products, ccsds_link_profiles, ccsds_packets, character_sets,
combinations, command_line_options, crs_projections, cuid2, datalog, dates,
deques, derangements, dictionaries, expecteds,
frequent_pattern_mining_protocols, genint, geospatial, grammars, graphs,
heaps, hierarchies, hook_flows, http_websocket_handshake, ids, intervals,
json, json_ld, json_lines, json_patch, json_path, json_pointer, json_rpc,
json_schema, ksuid, mcp_server, meta, meta_compiler, multisets, mutations,
nanoid, open_api, optionals, options, partitions, permutations, protobuf,
queues, random, recorded_database, sequential_pattern_mining_protocols, sets,
snowflakeid, stemming, string_distance, strings, subsequences, term_io,
tle_orbits, toml, toon, tsv, ulid, uuid, validations, wkt_wkb, yaml, zippers.

Highlights: the whole JSON family, YAML/TOML/CSV-adjacent formats, Avro +
Protobuf, the classic data structures (sets/heaps/queues/dictionaries/
graphs), grammars, meta/meta_compiler, and **random — 457/457 including all
17 generator test sets, identical to a mature backend**. `types` passes
148/149 (one known determinism edge).

## 🟡 Partial — most tests pass, failures concentrated in one cluster

The failures concentrate in two engine-level causes:

- **Spurious choice points** — the dominant one. lgtunit's
  deterministic-goal tests report "test goal succeeded
  non-deterministically": the engine leaves a choice point where the goal
  is semantically deterministic. This is what fails most of
  `linear_algebra` (45/72), the ML classifiers/regressions/projections
  (`adaptive_boosting_classifier`, `logistic_regression_classifier`,
  `linear_svm_classifier`, `ridge_regression`, `truncated_svd_projection`,
  `nmf_projection`, …) and the known `types` 148/149 edge. The lever is
  indexing / choice-point elision for the clause shapes these libraries
  use.
- **Integer bit-pattern arithmetic** — fixed: `1 << 63` used to overflow
  silently to a negative instead of promoting to an unbounded integer.
  `ieee_754` went from 19 to 7 failures with that one fix; the remaining 7
  are genuine float-representation edges (NaN payloads, rounding corners).

Everything else in the partial group fails 1–5 tests on library-specific
edges (e.g. `arbitrary` 42/43, `os` file-system corners).

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

Run suites **sequentially** — parallel runs collide on the scratch files of
shared dependencies (every tester compiles lgtunit into its own directory).
