# Using Logtalk with Shumway

Shumway runs [Logtalk](https://logtalk.org/), the object-oriented layer that
compiles to Prolog, as one of its backends. **Logtalk 3.103.0 and later
support Shumway 0.9.3 and later** as they come: the distribution has the
adapter, the `shumwaylgt` script, the embedding scripts and the `shumway`
and `shumwaync` backends of `logtalk_tester`. The adapter announces the
`shumway` dialect, and Logtalk's libraries and tests have code for it.
Checked with the 3.103.0 release on Windows.

For an earlier release, see [Earlier Logtalk
releases](#earlier-logtalk-releases).

## Setup

1. Install Logtalk 3.103.0 or later. Its installers set the `LOGTALKHOME`
   and `LOGTALKUSER` environment variables; with a source distribution, set
   them to the directory it was unpacked in.
2. Put the `shumway` executable on the `PATH`. A build collects it in
   `dist/Release/` (see the [user guide](user-guide.md)).

## Running

From any directory, typically your project's:

```
shumwaylgt
```

That is `shumwaylgt.sh` on a POSIX system and `shumwaylgt.ps1` on Windows,
in `<LOGTALKHOME>/integration/`. It starts `shumway` with Logtalk loaded,
and passes its options on: `-q` for a quiet start, `-g Goal` for a goal to
run once Logtalk is up.

```prolog
?- logtalk_load(my_loader).
?- my_object::my_predicate(X).
```

Loading an entity again replaces its code and leaves the other entities as
they are. To debug, add `--debug` or `--dap <port>` and set breakpoints in
the intermediate `.pl` files Logtalk writes.

## A standalone executable

Logtalk's embedding scripts for Shumway, `shumway_logtalk_pl.sh` and
`shumway_logtalk_pl.ps1` in `scripts/embedding/shumway/`, save a
self-contained executable: an application, given by its loader (`-l`) and
its startup goal (`-g`), or a Logtalk top level when there is none. `-n`
names it (`logtalk` by default), and `-h` lists the options. For example:

```
shumway_logtalk_pl.sh -c -d ~/collect -s none
```

leaves a Logtalk top level in `~/collect/logtalk`. It starts without
compiling Logtalk's core files, and takes the same arguments as `shumway`.

### Linking by hand

The scripts write the compiler and runtime to `logtalk.pl` and the
application to `application.pl`. To link them yourself, include both from
one file, so they share one namespace:

```prolog
% wumpus.pl
:- include(logtalk).
:- include(application).
```

```
shumway-link --consult -o wumpus.shum --exe wumpus --goal "'::'(wumpus,main)" wumpus.pl
```

`--consult` runs the runtime's initialization goals while linking, and the
executable starts from the state they leave.

## Running test suites

- `logtalk_tester -p shumway` runs each test suite with `shumwaylgt`.
- `logtalk_tester -p shumwaync` runs it with `shumwaylgtnc`, an executable
  that includes Logtalk and so starts faster. The embedding script makes it:

  ```
  shumway_logtalk_pl.sh -c -d ~/collect -s none && mv ~/collect/logtalk ~/bin/shumwaylgtnc
  ```

## Status

- **Libraries**: every library test suite Shumway can run passes: 214
  suites, 11,634 tests, 0 failures (Logtalk 3.101.0, 2026-09). `java` needs
  a JVM, and `sockets` waits for a branch for Shumway on Logtalk's side
  (`process` has one since 3.103.0). Details in [Logtalk library
  support](logtalk-library-support.md).
- **Features**: tabling (the engine's `:- table` inside objects),
  coinduction (unification, comparison and copying terminate on cyclic
  terms) and coroutining (`dif/2`, `freeze/2`, `when/2`). Message sending
  (`::`) runs as fast as a plain call; Logtalk binds it statically under
  `optimize(on)`.
- **ISO conformity battery** (`tests/prolog`, 192 testers, 4,417 tests):
  4,339 passed, 28 failed, 50 skipped (Logtalk's development tree,
  2026-11). Each failure is a test that expects a reading Shumway does not
  take:
  - `functions/` (6): `floor(9)` and the other float functions accept an
    integer, where the tests expect `type_error(float, 9)`. With rational
    numbers an integral rational is an integer, and the strict reading
    would make `floor(4r2)` an error and `floor(5r2)` not.
  - `length_2` (7): `length(a, N)` and other non-lists fail, with no type
    error, as Neumerkel's conformance cases have it.
  - `read_term_3` (6): an output option that cannot unify, such as
    `variables(a)`, makes the read fail (Cor.3).
  - `term_comparison` (4): the order of two fresh variables is left to the
    implementation (7.2.1).
  - `current_input_1`, `current_output_1` and `stream_property_2` (4):
    asking about a closed stream fails; 8.11 lists no error for it.
  - `syntax/numbers` (1): a lone `0''` is read only in the `swi` dialect;
    the battery marks it de facto.

## Earlier Logtalk releases

A release before 3.103.0 has no adapter for Shumway. This repository's
[`logtalk/`](../../logtalk/) has one, checked with Logtalk 3.101.0:

1. Copy `logtalk/adapters/shumway.pl` and
   `logtalk/integration/logtalk_shumway.pl` into the matching directories
   of the installation.
2. Point `LOGTALKHOME` and `LOGTALKUSER` at it.
3. Run `shumway <LOGTALKHOME>/integration/logtalk_shumway.pl`.

Those releases do not know a `shumway` dialect, so this adapter announces
`swi`; the `SHUMWAY_LOGTALK_DIALECT` environment variable overrides it. It
is Shumway's (MIT); the adapter in Logtalk's distribution is Logtalk's
(Apache-2.0).
