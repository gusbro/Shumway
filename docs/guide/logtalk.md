# Using Logtalk with Shumway

Shumway runs [Logtalk](https://logtalk.org/) — the OO logic-programming layer
that compiles to plain Prolog — as a backend compiler. The glue lives in this
repository under [`logtalk/`](../logtalk/): a backend **adapter**
(`adapters/shumway.pl`) and a one-file **launcher**
(`integration/logtalk_shumway.pl`). Verified against **Logtalk 3.101.0** on
Windows; nothing in the glue is OS-specific.

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

## Status

- **Test suites**: all 240 of Logtalk 3.101.0 library testers swept —
  **192 of the 194 runnable suites fully green, 99.98 % of all individual
  tests pass** (10,317 of 10,319; the 2 remaining failures are an upstream
  geojson bug and a missing host tool, both verified by running the same
  tests on SWI — see
  [`logtalk-library-support.md`](logtalk-library-support.md)). Highlights:
  random 457/457, types 149/149, linear_algebra 72/72, crypto 121/121, the
  CCSDS stack and ieee_754 at zero failures; on `os`, `tzif` and
  `mime_types` Shumway passes tests SWI itself fails on Windows.
- **Benchmarks** (`examples/benchmarks`): Shumway Tier-0 matches or beats
  GProlog-interpreted on every shape; Tier-1 wins 4–7× across the board.
  Message dispatch (`::`) runs at parity with plain calls — Logtalk
  static-binds it under `optimize(on)`.
- The adapter carries small shims for predicates Shumway lacks natively
  (`term_hash/2,4`); everything else — including `format/2,3`,
  `predicate_property/2` and the full ISO surface Logtalk's compiler needs —
  is native.

## Notes

- The adapter is derived from Logtalk's GNU Prolog adapter (`gnu.pl`) and
  keeps its Apache-2.0 license header. Upstreaming it to the Logtalk
  distribution is a possible future step.
- Logtalk compiles each entity to an intermediate `.pl` in its scratch
  directory and consults it; Shumway's consult-time machinery (live linking,
  dynamic registrations, IL promotion) handles that pipeline.
