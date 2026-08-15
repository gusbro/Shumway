# Using Logtalk with Shumway

Shumway runs [Logtalk](https://logtalk.org/) — the OO logic-programming layer
that compiles to plain Prolog — as a backend compiler. The glue lives in this
repository under [`logtalk/`](../logtalk/): a backend **adapter**
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

## Notes

- The adapter is Shumway's own code (MIT, like the rest of the repository),
  written against Logtalk's backend-adapter interface. Upstreaming it to the
  Logtalk distribution is a possible future step.
- Logtalk compiles each entity to an intermediate `.pl` in its scratch
  directory and consults it; Shumway's consult-time machinery (live linking,
  dynamic registrations, IL promotion) handles that pipeline.
