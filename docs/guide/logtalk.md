# Using Logtalk with Shumway

Shumway runs [Logtalk](https://logtalk.org/) (the OO logic-programming layer
that compiles to plain Prolog) as a backend compiler. The glue lives in this
repository under [`logtalk/`](../../logtalk/): a backend **adapter**
(`adapters/shumway.pl`) and a one-file **launcher**
(`integration/logtalk_shumway.pl`). The Logtalk tree itself is never patched:
everything Shumway needs travels in the adapter we ship. Verified against
**Logtalk 3.101.0** (the released tag) on Windows; nothing in the glue is
OS-specific.

## Setup

1. Install Logtalk (or unpack a source distribution: any directory works).
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
know Shumway (yet: that is Paulo Moura's call, not ours). The adapter
announces **`swi`**, chosen by running the full library test sweep under four
candidate dialects and keeping the one that closed at zero failures; the
whole comparison is documented in the adapter's header. Override it with the
`SHUMWAY_LOGTALK_DIALECT` environment variable. Everything else (
`prolog_version`, error messages, `current_prolog_flag`) keeps reporting
Shumway; only that one selector borrows SWI's name.

## Status

- **Test suites**: the full 242-tester sweep of Logtalk 3.101.0's library
  collection, on a pristine tree, closes at **100% of the structurally
  supported set: 214 suites all fully green, 11,634 tests, 0 failures**
  (measured 2026-09; the compute-heavy ML suites may need a run without
  machine contention). The three libraries whose *operation* needs an OS capability
  Shumway does not provide (`process` (OS processes), `redis` (sockets),
  `java` (a JVM)) are excluded as structurally N/A, documented rather than
  hidden. Details and reproduction in
  [`logtalk-library-support.md`](logtalk-library-support.md).
- **Benchmarks** (`examples/benchmarks`): Shumway Tier-0 matches or beats
  GProlog-interpreted on every shape; Tier-1 wins 4–7× across the board.
  Message dispatch (`::`) runs at parity with plain calls; Logtalk
  static-binds it under `optimize(on)`.
- **Capabilities announced**: `tabling` (the engine's `:- table` works inside
  objects) and native coroutining (`dif/2`, `freeze/2`, `when/2`) with the
  meta-argument wrapping Logtalk expects; the compiler reads our
  `predicate_property/2` meta-predicate templates exactly as it does SWI's.
- The adapter carries the backend compatibility layer: the OS predicate
  spellings each dialect arm expects (`path_sysop/2,3`, `access_file/2`,
  `size_file/2`, …), time-limited calls (`call_with_timeout/2,3`,
  `call_with_time_limit/2`, `timed_call/2`: all over the engine's
  `time_out/3`), and small shims like `term_hash/2,4`. Everything else:
  `format/2,3`, `predicate_property/2` and the full ISO surface Logtalk's
  compiler needs, is native.

- **The `tests/prolog` ISO conformity battery** (192 testers, 4,304
  counted tests): **4,258 passed / 46 failed**, every tester reporting
  (measured 2026-09; baseline before the 2026-08 campaign: 2,096 / 933).
  The five `unicode/` testers run with no skips: `directives/encoding_1`
  3/3, `escape_sequences` 12/12, `case_variables` 12/12, `builtins` 131/6,
  `encodings` 41/8, above the SWI oracle on every tester (SWI: 2/1, 11/1,
  12/12, 127/10, 25/5 with 19 skips). Those 14 failures are one accepted
  divergence: the tests expect `stream_property/2` /
  `current_prolog_flag(encoding, _)` to answer in Logtalk's charset
  spellings (`'UTF-8'`); Shumway reports its own encoding names (`utf8`),
  exactly as SWI does, which fails the same tests.

  Two more divergences are deliberate and closed. The six single-test
  failures in `functions/` are the float-conversion functions: the
  battery's `lgt_*` tests expect the strict-ISO reading, `ceiling(9)`,
  `floor(9)`, `round(9)`, `truncate(9)`, `float_integer_part(9)`,
  `float_fractional_part(9)` → `type_error(float, 9)`, which is what GNU
  Prolog does. Shumway stays in the **lenient camp with SWI and Scryer**
  (integers accepted, identity for the rounding functions): the SWI/Scryer
  library ecosystems this engine certifies against rely on it, and Scryer
  (the strictest of the modern systems) accepts it too. The four
  `unbounded` failures build terms of enormous arity with `functor/3`:
  a term's arity has no limit of its own here (`max_arity` is
  `unbounded`), so running into the memory it would take is
  `resource_error(finite_memory)`.

  `\e`, `\s`, `\d` in quoted tokens and a lone `0''` are SWI extensions,
  not ISO: GNU Prolog rejects them too, and Shumway's reader accepts them
  only inside the swi dialect load scope, because the strict reading is
  what the Neumerkel conformance suite pins.

  The rest follow the standard, or the conformance cases that read it,
  where these `lgt_*` and `commons_*` tests record what several other
  systems do:

  - `length_2` (6): `length(a, N)` and other non-lists fail rather than
    raise `type_error(list, ...)`, as Neumerkel's `length/2` cases have
    it (`length(2, 0)` is false).
  - `read_term_3` (6): `variables/1`, `variable_names/1` and
    `singletons/1` are output options, so a value that cannot unify
    (`variables(a)`) makes the read fail; it is not a
    `domain_error(read_option, ...)` (Cor.3, Neumerkel's cases).
  - `current_input_1`, `current_output_1`, `stream_property_2` (4) and
    `at_end_of_stream_1` (2): asking about a closed stream, or asking
    whether an output stream is at its end, fails; the error tables of
    8.11 list no error for those cases.
  - `atom_codes_2` (2) and `number_codes_2` (1): a list element that is
    not a character code is `representation_error(character_code)`, the
    error 8.16.5.3 and 8.16.8.3 name, whether or not the first argument
    is bound. The tests expect `type_error(integer, E)`; the battery's
    own note in `number_codes_2` says the standard specifies the
    representation error, and its `lgt_number_codes_2_26` and `_28`
    accept either.

  The one `syntax/numbers` failure is the lone `0''` above
  (`lgt_number_30`, which the battery itself marks de facto rather than
  standard). No failure is left open.

## Notes

- The adapter is Shumway's own code (MIT, like the rest of the repository),
  written against Logtalk's backend-adapter interface. Upstreaming it to the
  Logtalk distribution is a possible future step.
- Logtalk compiles each entity to an intermediate `.pl` in its scratch
  directory and consults it; Shumway's consult-time machinery (live linking,
  dynamic registrations, IL promotion) handles that pipeline.
