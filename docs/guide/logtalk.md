# Using Logtalk with Shumway

Shumway runs [Logtalk](https://logtalk.org/) (the OO logic-programming layer
that compiles to plain Prolog) as a backend compiler. **Logtalk 3.103.0 and
later support Shumway 0.9.3 and later** as they come: the distribution has
the backend adapter (`adapters/shumway.pl`), the `shumwaylgt` integration
script, the embedding scripts, and the `shumway` and `shumwaync` backends of
the `logtalk_tester` scripts. Checked with the 3.103.0 release on Windows.

For an earlier release, this repository keeps glue of its own under
[`logtalk/`](../../logtalk/): see [Earlier Logtalk
releases](#earlier-logtalk-releases).

## Setup

1. Install Logtalk 3.103.0 or later. Its installers set the `LOGTALKHOME`
   and `LOGTALKUSER` environment variables; with a source distribution, set
   them to the directory it was unpacked in.
2. Put the `shumway` executable on the `PATH`. A build collects it in
   `dist/Release/` (see the [user guide](user-guide.md)).

## Running

From any directory (typically your project's):

```
shumwaylgt
```

That is `shumwaylgt.sh` on a POSIX system and `shumwaylgt.ps1` on Windows,
in `<LOGTALKHOME>/integration/`. It loads the adapter, the Logtalk compiler
and runtime, the table of library paths and any `settings.lgt`, and passes
its options to `shumway`: `-q` for a quiet start, `-g Goal` for a goal to
run once Logtalk is up. The same without the script:
`shumway <LOGTALKHOME>/integration/logtalk_shumway.pl`.
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

## A standalone executable

Logtalk's embedding scripts for Shumway, `shumway_logtalk_pl.sh` and
`shumway_logtalk_pl.ps1` in `scripts/embedding/shumway/`, write the Logtalk
compiler and runtime to `logtalk.pl` and, given an application's loader
(`-l`), the application to `application.pl`. Then they link the files and
save a self-contained executable, named by `-n` (`logtalk` by default): the
application, with its startup goal (`-g`), or a Logtalk top level when there
is no application. `-h` lists the options. For example:

```
shumway_logtalk_pl.sh -c -d ~/collect -s none
```

leaves a Logtalk top level in `~/collect/logtalk`. It starts without
compiling Logtalk's core files, and takes the same arguments as `shumway`,
such as `-g` goals and `-q` for a quiet start.

### Linking by hand

The two files share one namespace, as they do when both are consulted into
`user`. The linker reads each file without a `:- module` directive as a
module of its own, so include both from one file:

```prolog
% wumpus.pl
:- include(logtalk).
:- include(application).
```

and link it through the consult pipeline:

```
shumway-link --consult -o wumpus.shum --exe wumpus --goal "'::'(wumpus,main)" wumpus.pl
```

`--consult` runs the runtime's `initialization/1` goals while linking, and
the executable starts from the state they leave in its dynamic predicates; a
file-at-a-time link carries them, and the executable runs them when it
starts, in the directory it starts in. `--goal` is the
application's startup goal. The `loader.pl` the scripts write when asked is
not needed: it consults the two files when the program starts, which the
link has already done.

The Logtalk top level the scripts save is the bundle `logtalk.shum`, the
compiler and the runtime, saved with `shumway --save-toplevel`. Loading the
bundle gives the same top level:

```
shumway logtalk.shum
```

See [A saved top level](user-guide.md#a-saved-top-level).

## Running test suites

Logtalk's `logtalk_tester` scripts take Shumway as a backend:

- `logtalk_tester -p shumway` runs each test suite with `shumwaylgt`;
- `logtalk_tester -p shumwaync` runs it with an executable that includes
  Logtalk, `shumwaylgtnc` on the `PATH`, which starts faster because it does
  not compile Logtalk's core files first:

  ```
  shumway_logtalk_pl.sh -c -d ~/collect -s none && mv ~/collect/logtalk ~/bin/shumwaylgtnc
  ```

## The dialect

Logtalk selects backend-specific code by the `prolog_dialect` flag. The
adapter of Logtalk 3.103.0 announces **`shumway`**, and Logtalk's libraries
and tests have code for it (`os` and `process` among the libraries, the
encoding testers among the tests).

The adapter in this repository's `logtalk/`, for earlier releases, which do
not know Shumway, announces **`swi`**: it was chosen by running the full
library test sweep under four candidate dialects and keeping the one that
closed at zero failures, and the adapter's header documents the comparison.
The `SHUMWAY_LOGTALK_DIALECT` environment variable overrides it. Everything
else (`prolog_version`, error messages, `current_prolog_flag`) reports
Shumway either way.

## Earlier Logtalk releases

A release before 3.103.0 has no adapter for Shumway. This repository's
[`logtalk/`](../../logtalk/) has one, with a one-file launcher, verified
against Logtalk 3.101.0:

1. Copy the two files into the matching directories of the installation:

   ```
   copy logtalk\adapters\shumway.pl             <LOGTALK>\adapters\
   copy logtalk\integration\logtalk_shumway.pl  <LOGTALK>\integration\
   ```

2. Point `LOGTALKHOME` and `LOGTALKUSER` at it.
3. Run `shumway <LOGTALK>/integration/logtalk_shumway.pl`.

## Status

- **Test suites**: the full 242-tester sweep of Logtalk 3.101.0's library
  collection, on a pristine tree with this repository's adapter, closes at
  **100% of the structurally supported set: 214 suites all fully green,
  11,634 tests, 0 failures** (measured 2026-09; the compute-heavy ML suites
  may need a run without machine contention). `java` needs a JVM, and is
  excluded as structurally N/A, documented rather than hidden. The
  `sockets` library needs a branch for Shumway on Logtalk's side; 3.103.0
  has one for `process`. Details and reproduction in
  [`logtalk-library-support.md`](logtalk-library-support.md).
- **Benchmarks** (`examples/benchmarks`): Shumway Tier-0 matches or beats
  GProlog-interpreted on every shape; Tier-1 wins 4–7× across the board.
  Message dispatch (`::`) runs at parity with plain calls; Logtalk
  static-binds it under `optimize(on)`.
- **Capabilities announced**: `tabling` (the engine's `:- table` works inside
  objects), `coinduction` (unification, comparison, copying and the term
  inspection built-ins terminate on cyclic terms) and native coroutining (`dif/2`, `freeze/2`, `when/2`) with the
  meta-argument wrapping Logtalk expects; the compiler reads our
  `predicate_property/2` meta-predicate templates exactly as it does SWI's.
- The adapter carries the backend compatibility layer: the OS predicate
  spellings each dialect arm expects (`path_sysop/2,3`, `access_file/2`,
  `size_file/2`, …), time-limited calls (`call_with_timeout/2,3`,
  `call_with_time_limit/2`, `timed_call/2`: all over the engine's
  `time_out/3`), and small shims like `term_hash/2,4`. Everything else:
  `format/2,3`, `predicate_property/2` and the full ISO surface Logtalk's
  compiler needs, is native.

- **The `tests/prolog` ISO conformity battery** (192 testers, 4,417
  tests): **4,339 passed / 28 failed / 50 skipped**, every tester
  reporting (measured 2026-11 against Logtalk's development tree;
  baseline before the 2026-08 campaign: 2,096 / 933). `control/if_3` is
  the one tester skipped whole: it runs only where `if/3` is a built-in.
  The `unicode/` testers pass in full. Logtalk 3.103.0 lists `shumway`
  among the systems of the `unicode/encodings` and
  `tests/logtalk/directives/encoding_1` testers; in 3.101.0 it has to be
  added to their `tester.lgt`.

  Each failure below was checked against the standard and against
  SWI-Prolog, SICStus, Scryer and GNU Prolog.

  One divergence is deliberate. The six single-test failures in
  `functions/` are the float-conversion functions: the battery's `lgt_*`
  tests expect the strict-ISO reading, `ceiling(9)`, `floor(9)`,
  `round(9)`, `truncate(9)`, `float_integer_part(9)`,
  `float_fractional_part(9)` → `type_error(float, 9)`, which is what GNU
  Prolog does. SICStus raises it for `float_integer_part/1` and
  `float_fractional_part/1` only. Shumway accepts an integer, as SWI (with
  its `iso` flag on or off) and Scryer do. With rational numbers an
  integral rational is an integer, so the strict reading would make
  `floor(4r2)` an error and `floor(5r2)` not, and libraries that round
  rationals, Scryer's `simplex` among them, rely on integers being
  accepted.

  `\e`, `\s`, `\d` in quoted tokens and a lone `0''` are SWI extensions,
  not ISO: GNU Prolog rejects them too, and Shumway's reader accepts them
  only inside the swi dialect load scope, because the strict reading is
  what the Neumerkel conformance suite pins.

  The rest follow the standard, or the conformance cases that read it,
  where these `lgt_*` and `commons_*` tests record what several other
  systems do:

  - `length_2` (7): `length(a, N)` and other non-lists, a cyclic list
    with a given length among them, fail rather than raise
    `type_error(list, ...)`, as Neumerkel's `length/2` cases have it
    (`length(2, 0)` is false). SICStus and Scryer fail too.
  - `term_comparison` (4): `X = s(X,_), Y = s(Y,_), X @< Y` and three
    like it depend on which of two fresh variables is older, and the
    standard (7.2.1) leaves the order of variables to the implementation.
    lgtunit calls the goal as a term built at run time, and Shumway builds
    the deeper subterm first, so the variable in `Y`'s term is the older
    one. SWI-Prolog, SICStus and Scryer follow the order of the text.
    Written as a clause body, the same goals give the answers the tests
    expect.
  - `read_term_3` (6): `variables/1`, `variable_names/1` and
    `singletons/1` are output options, so a value that cannot unify
    (`variables(a)`) makes the read fail; it is not a
    `domain_error(read_option, ...)` (Cor.3, Neumerkel's cases). SWI,
    SICStus and Scryer fail too; GNU Prolog raises the domain error.
  - `current_input_1`, `current_output_1` and `stream_property_2` (4):
    asking about a closed stream fails, as in SICStus and GNU Prolog; the
    error tables of 8.11 list no error for that case.

  The one `syntax/numbers` failure is the lone `0''` above
  (`lgt_number_30`, which the battery itself marks de facto rather than
  standard). No failure is left open.

## Notes

- Logtalk distributes its adapter for Shumway since 3.103.0, under its own
  licence (Apache-2.0). The adapter in this repository's `logtalk/` is
  Shumway's (MIT, like the rest of the repository), written against
  Logtalk's backend-adapter interface, for the releases before.
- Logtalk compiles each entity to an intermediate `.pl` in its scratch
  directory and the adapter loads it with `reconsult/1`: loading an entity
  again replaces its code, and takes back only the entries that file gave
  the runtime's multifile tables, so the other entities keep theirs.
  Shumway's consult-time machinery (live linking, dynamic registrations, IL
  promotion) handles that pipeline.
