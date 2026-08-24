# Using Logtalk with Shumway

Shumway runs [Logtalk](https://logtalk.org/) — the OO logic-programming layer
that compiles to plain Prolog — as a backend compiler. The glue lives in this
repository under [`logtalk/`](../../logtalk/): a backend **adapter**
(`adapters/shumway.pl`) and a one-file **launcher**
(`integration/logtalk_shumway.pl`). The Logtalk tree itself is never patched:
everything Shumway needs travels in the adapter we ship. Verified against
**Logtalk 3.101.0** (the released tag) on Windows; nothing in the glue is
OS-specific.

## Setup

1. Install Logtalk (or unpack a source distribution — any directory works).
2. Copy the two glue files into the matching directories of that installation:

   ```
   copy logtalk\adapters\shumway.pl      <LOGTALK>\adapters\
   copy logtalk\integration\logtalk_shumway.pl  <LOGTALK>\integration\
   ```

3. Point the standard Logtalk environment variables at it:

   ```
   set LOGTALKHOME=<LOGTALK>
   set LOGTALKUSER=<LOGTALK>
   ```

## Running

From any directory (typically your project's):

```
shumway <LOGTALK>/adapters/shumway.pl <LOGTALK>/paths/paths.pl <LOGTALK>/core/core.pl
```

or equivalently via the launcher: `shumway <LOGTALK>/integration/logtalk_shumway.pl`.
At the prompt, Logtalk is up:

```prolog
?- logtalk_load(my_loader).
?- my_object::my_predicate(X).
```

Tier-1 IL promotion works under Logtalk (`SHUMWAY_IL_PROMOTE=32`); on the
standard `examples/benchmarks` suite it runs 4–7× faster than GProlog's
interpreter (the backend Logtalk uses on GProlog). Debugging works too: add
`--debug` / `--dap <port>` and set breakpoints in the Logtalk-generated
intermediate files, or in your `.lgt` sources' consulted forms.

## The dialect

Logtalk selects backend-specific code by `prolog_dialect`, and it does not
know Shumway (yet — that is Paulo Moura's call, not ours). The adapter
announces **`swi`**, chosen by running the full library test sweep under four
candidate dialects and keeping the one that closed at zero failures; the
whole comparison is documented in the adapter's header. Override it with the
`SHUMWAY_LOGTALK_DIALECT` environment variable. Everything else —
`prolog_version`, error messages, `current_prolog_flag` — keeps reporting
Shumway; only that one selector borrows SWI's name.

## Status

- **Test suites**: the full 242-tester sweep of Logtalk 3.101.0's library
  collection, on a pristine tree, closes at **100% of the structurally
  supported set — 204 suites all fully green, 11,417 tests, 0 failures**
  (plus five compute-heavy ML suites that pass when run without machine
  contention). The three libraries whose *operation* needs an OS capability
  Shumway does not provide — `process` (OS processes), `redis` (sockets),
  `java` (a JVM) — are excluded as structurally N/A, documented rather than
  hidden. Details and reproduction in
  [`logtalk-library-support.md`](logtalk-library-support.md).
- **Benchmarks** (`examples/benchmarks`): Shumway Tier-0 matches or beats
  GProlog-interpreted on every shape; Tier-1 wins 4–7× across the board.
  Message dispatch (`::`) runs at parity with plain calls — Logtalk
  static-binds it under `optimize(on)`.
- **Capabilities announced**: `tabling` (the engine's `:- table` works inside
  objects) and native coroutining (`dif/2`, `freeze/2`, `when/2`) with the
  meta-argument wrapping Logtalk expects — the compiler reads our
  `predicate_property/2` meta-predicate templates exactly as it does SWI's.
- The adapter carries the backend compatibility layer: the OS predicate
  spellings each dialect arm expects (`path_sysop/2,3`, `access_file/2`,
  `size_file/2`, …), time-limited calls (`call_with_timeout/2,3`,
  `call_with_time_limit/2`, `timed_call/2` — all over the engine's
  `time_out/3`), and small shims like `term_hash/2,4`. Everything else —
  `format/2,3`, `predicate_property/2` and the full ISO surface Logtalk's
  compiler needs — is native.

- **The `tests/prolog` ISO conformity battery** (192 testers, ~3,400
  counted tests): **3,219 passed / 70 failed** after the 2026-08 campaign
  (baseline before it: 2,096 / 933; 2,796 / 499 at the end of round two).
  The five `unicode/` testers, formerly gated on the `encoding/1`
  directive, now run: `directives/encoding_1` 3/3, `escape_sequences`
  12/12, `case_variables` 12/12, `builtins` 131/6, `encodings` 41/8 with
  no skips — above the SWI oracle on every tester (SWI: 2/1, 11/1, 12/12,
  127/10, 25/5 with 19 skips). Every remaining failure is one accepted
  divergence: those tests expect `stream_property/2` /
  `current_prolog_flag(encoding, _)` to answer in Logtalk's charset
  spellings (`'UTF-8'`); Shumway reports its own encoding names (`utf8`),
  exactly as SWI does — which fails the same tests.
  Of what remains, 25 failures are the `portray/1` hook (see the engine
  note below), 6 are the accepted float-conversion divergence, and the
  rest are single-tester items tracked in the repository history.

  Two divergences are deliberate and closed. The first: the battery's
  `lgt_*` tests expect the strict-ISO reading of the
  float-conversion functions — `ceiling(9)`, `floor(9)`, `round(9)`,
  `truncate(9)`, `float_integer_part(9)`, `float_fractional_part(9)` →
  `type_error(float, 9)`, which is what GNU Prolog does. Shumway stays in
  the **lenient camp with SWI and Scryer** (integers accepted, identity
  for the rounding functions): the SWI/Scryer library ecosystems this
  engine certifies against rely on it, and Scryer — the strictest of the
  modern systems — accepts it too. These six single-test failures in
  `functions/` are accepted, not pending.

  The second: `\e`, `\s`, `\d` in quoted tokens and a lone `0''` are SWI
  extensions, not ISO — GNU Prolog rejects them too, and Shumway's reader
  accepts them only inside the swi dialect load scope, because the strict
  reading is what the Neumerkel conformance suite pins. Four
  `syntax/numbers` failures come from that and are accepted.

  Still open on the engine side: `write_term/2,3`'s `portrayed(true)` and
  `print/1,2`'s `portray/1` hook are validated but do not call the hook. It
  needs a re-entrant solve from inside a writer builtin, and that is unsound
  today when the builtin runs under a nested sub-query — the caller's
  continuation fails after the nested solve returns even when the hook
  succeeded. It accounts for the `print_1`, `print_2`, `portray_1` and the
  portray part of the `write_term_3` / `format_2` / `format_3` failures.

## Notes

- The adapter is Shumway's own code (MIT, like the rest of the repository),
  written against Logtalk's backend-adapter interface. Upstreaming it to the
  Logtalk distribution is a possible future step.
- Logtalk compiles each entity to an intermediate `.pl` in its scratch
  directory and consults it; Shumway's consult-time machinery (live linking,
  dynamic registrations, IL promotion) handles that pipeline.
