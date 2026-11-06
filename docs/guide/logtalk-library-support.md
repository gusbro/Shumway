# Logtalk library support

Status of running **Logtalk 3.101.0's bundled libraries** on Shumway, measured
by executing each library's own lgtunit test suite (`tester.lgt`) on a fresh
Logtalk-on-Shumway boot: all 242 library testers swept, on a pristine clone
of the released tree, with zero patches to it. Logtalk itself boots and runs
fully on Shumway (see [`logtalk.md`](logtalk.md) for setup); this document is
about its **library** collection, which in 3.101.x is large and includes
machine-learning, geospatial, telemetry (CCSDS) and web-format libraries
beyond the classic data-structure set.

## Headline numbers

- **100% of the structurally supported set: 214 suites, all fully green:
  11,634 tests, 0 failures** (plus 123 tests the suites skip themselves on
  any backend; measured 2026-09).
- The compute-heavy ML classifiers can time out under a 4-worker parallel
  sweep on a 4-core machine and are green run alone (in the 2026-09 sweep
  one did, `lof_anomaly_detector`, 27/27 alone). Treat those timeouts as
  machine-load sensitivity, not failures.
- **`java` is structurally N/A**: it needs an in-process JVM. Its tester would fail
  honestly, but it measures the host platform gap, not Prolog conformance: the sweep
  harness skips it with a distinct marker instead of mis-scoring it.
- **The network and process libraries wait for a branch on Logtalk's side.** Shumway
  has [`library(sockets)`, `library(tls)` and `library(process)`](network-and-processes.md),
  but the sweep ran before them, so they count as not applicable (the sockets/HTTP
  service stack, and `memcached`, `gravatar`, `open_ai`, `open_id`, `rest`, `s3`). With a
  `shumway` branch in `socket.lgt` and `process.lgt` and the adapter's `sockets` feature
  on, in a clone (2026-11), these pass: `process` 14 with 1 skipped, `sockets` 8, `mqtt`
  179, `amqp` 143 with 45 skipped, `rest` 66, `http_server_core` 38, `open_id` 33, `smtp`
  23, `open_ai` 18, `http_process_transport` 17 (with `ncat` on the path), `http_client_core`
  12, `stomp` 11 with 13 skipped, `gravatar` 8, and `redis` 55 against a real server. The
  skipped tests of `amqp` and `stomp`, and all of `memcached`, need a server that was not
  running. `s3` writes `BasePath == '/'`, which
  [the reader refuses](user-guide.md#operator-atoms-as-operands).
- **`git` and `linda` declare themselves not applicable**: they need external binaries
  or threads. A couple remain genuinely too slow to score
  (`isolation_forest_anomaly_detector`, `simulated_annealing`: 30+ minutes even alone).

Highlights among the green suites: **random 216/216**, **types 149/149**,
**crypto 158/158**, **linear_algebra 72/72**, **ieee_754 77/77** and the whole
**CCSDS framing stack**, the JSON/YAML/TOML/Avro/Protobuf/MessagePack format
family, **json_path 81/81** (including the iregexp `\p{...}` Unicode property
filters), **coroutining 21/21** and **dif 6/6** over the native engine
predicates, **timeout 14/14** over the engine's `time_out/3`, **os 141/141**
(the one environment note: `os_operating_system_release_1_01` shells out to
`pwsh.exe`, so PowerShell 7 must be on PATH), **tzif 56/56** and
**mime_types 14/14** (on both of which SWI-Prolog itself fails tests on
Windows), grammars, meta/meta_compiler, and the classic data structures.

## How this was reached

The sweep is also a correctness campaign: every failure was either traced to
a real engine defect and fixed, or shown to be structural. The engine work it
produced (none of it Logtalk-specific) includes ADR-045 (text-mode CR-LF
translation; the bug was aborting whole test objects), the `close/1` output
cursor restore (ISO §8.11.6), `predicate_property/2` reporting
`meta_predicate(T)` templates (how Logtalk decides to wrap goal arguments for
their calling context), a `shell/1,2` double-`cmd.exe /C` fix,
`unicode_property/2`, and a silent 16-bit truncation in every codes-to-text
builder. The dialect choice itself was measured, not assumed: the same sweep
under `gnu` left 9 failures, under `xvm` 36, under `swi` zero.

Verification oracle: SWI-Prolog running the same Logtalk tree. A test that
fails on both engines is upstream/platform, not ours; every test that failed
only on Shumway was treated as a Shumway bug.

## Reproduce

Each suite: from its `library/<name>/` directory, with
`LOGTALKHOME`/`LOGTALKUSER` pointing at the Logtalk installation:

```
shumway $LOGTALKHOME/adapters/shumway.pl $LOGTALKHOME/paths/paths.pl \
        $LOGTALKHOME/core/core.pl
?- logtalk_load(tester).
```

Run suites **sequentially**, or in parallel ONLY with fully ISOLATED tree
copies (one LOGTALKHOME/LOGTALKUSER per worker): parallel runs over a
shared tree collide on the scratch files of shared dependencies (every
tester compiles lgtunit into its own directory).
