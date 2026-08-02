# Logtalk library support

Status of running **Logtalk 3.101.0's bundled libraries** on Shumway, measured
by executing each library's own lgtunit test suite (`tester.lgt`) on a fresh
Logtalk-on-Shumway boot — 240 library test suites swept. Logtalk itself boots
and runs fully on Shumway (see [`logtalk.md`](logtalk.md) for setup); this
document is about its **library** collection, which in 3.101.x is large and
includes machine-learning, geospatial, telemetry (CCSDS) and web-format
libraries beyond the classic data-structure set.

## Headline numbers

- **93 of 240 test suites pass completely** (every test green).
- **102 pass partially** — **96.8% of ALL individual tests pass** (10,086 of
  10,414); most partial suites fail 1-5 tests on library-specific edges.
- **41 suites are not applicable or need infrastructure we do not provide**
  (network/OS/JVM bindings, or testers gated to a hardcoded backend whitelist).
- **4 suites time out** (heavy compute).

These numbers follow the ADR-041 determinism fix (dispatch-time clause
selection for dynamic chains): before it, 75 suites were green and 83% of
tests passed — the dominant failure was lgtunit determinism checks tripping
on spurious choice points (linear_algebra went 27/72 -> 72/72, types
148/149 -> 149/149, and the ML classifier suites went green in its wake).

## ✅ Fully green test suites (93)

application, arrangements, assignvars, avro, base32, base58, base64, base85,
c45_classifier, cartesian_products, ccsds_link_profiles, ccsds_packets,
character_sets, clo_span_pattern_miner, combinations, command_line_options,
crs_projections, cuid2, datalog, dates, dates_tz, deques, derangements,
dictionaries, expecteds, frequent_pattern_mining_protocols, genint,
geospatial, grammars, graphs, hashes, heaps, hierarchies, hmac, hook_flows,
http_cors, http_htmx, http_parameters, http_websocket_handshake, ids,
intervals, json, json_ld, json_lines, json_patch, json_path, json_pointer,
json_rpc, json_schema, knn_classifier, ksuid, **linear_algebra**, mcp_server,
message_pack, meta, meta_compiler, multisets, mutations,
naive_bayes_classifier, nanoid, nearest_centroid_classifier,
nested_dictionaries, nmea, open_api, optionals, options, partitions,
permutations, protobuf, queues, random, random_forest_classifier,
recorded_database, sequential_pattern_mining_protocols, sets, snowflakeid,
statistics, stemming, string_distance, strings, subsequences, term_io,
tle_orbits, toml, toon, tsv, **types**, ulid, uuid, validations, wkt_wkb,
yaml, zippers.

Highlights: the whole JSON family, YAML/TOML, Avro + Protobuf + MessagePack,
the classic data structures, grammars, meta/meta_compiler, **random 457/457**,
**types 149/149** and **linear_algebra 72/72** (both fully green since the
ADR-041 determinism fix), and several ML classifiers (kNN, naive Bayes,
random forest, C4.5, nearest centroid).

## 🟡 Partial — 96.8% of tests pass; the remaining tail

The former dominant cause — spurious choice points from unindexed dynamic
chains — is FIXED (ADR-041: dispatch-time clause selection). What remains
is a small tail of library-specific edges:

| suite | failing | nature |
|---|---|---|
| `ccsds_tc_services` (15/35), `ccsds_frames` (74/93) | bit-level telemetry framing | binary encode/decode edges |
| `crypto` (108/121) | hash primitives | no native crypto backend |
| `os` (130/156) | file-system corners | platform-specific semantics |
| `ieee_754` (70/79) | 7 float-representation edges | NaN payloads, rounding corners |
| `json_graph`, `jwt`, `reader`, `kernel_pca_projection`, `http_core`, … | 1–10 each | assorted library edges |

The integer bit-pattern arithmetic that used to fail `ieee_754` en masse
(`1 << 63` overflowing instead of promoting) is fixed.

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
