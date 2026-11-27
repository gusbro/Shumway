# ADR-076: A Bundle Says How It Was Built

## Status

Proposed (2026-11-27). Amends the stamp amendment of
[ADR-009](009-bundler-design.md) (2026-11-22), and changes what
[ADR-075](075-dynamic-library-bundles.md) and the page do with a library
bundle of another format.

## Context

A `.shum` and a `.shmo` open with `[magic 4][version 4][stamp 8]`, and a
reader refuses a file whose stamp is not its own (ADR-009, amendment of
2026-11-22). The stamp is a fingerprint of the instruction set, the operand
kinds, the cell tags and a mark raised by hand when a section's wire form
changes. It covers the whole file, and it moves every time an instruction is
added or renumbered.

What the stamp protects is the compiled code. What a file was built from,
its sources and the options of the tool that built it, does not depend on the
instruction set at all, but it sits inside the part the stamp covers, so a
file of another format loses it with the code.

Two hosts fall back on a source when a library's bundle is of another format:

- `use_module(library(X))` loads `X.pl` when it is on the search path, with a
  warning, and asks the host's `LibrarySourceResolver` first.
- A page that imports a collection compiles the library from `src/X.pl` with
  the librarian.

Both compile `X.pl` as a root, which is what the bundle was built from only
when the bundle came from compiling `X.pl` alone. A bundle the linker built
is something else, and what it lost comes back wrong without a word:

- A `--goal` given with `--library` is the last initialization goal of the
  last input, in its module (ADR-056, amendment of 2026-11-26). It is in no
  source. A library linked with it and compiled again from `X.pl` loads and
  never runs the goal. A page compiling a collection whose `.shum` was of
  another format did exactly that.
- A bundle linked from five inputs, `shumway-link a.pl b.pl c.pl d.pl e.pl`,
  is the five in their order. `X.pl` alone may load some of them, all of them
  in another order, or none.
- `--entry`, `--strip`, `--allow-undefined`, `--wasm` and the `--dynlib`
  libraries change the product and are in no source either.

## Decision

### 1. Two fields after the stamp, readable in every format

A `.shum` and a `.shmo` open with

```
[magic 4][version 4][stamp 8][rules 4][recipe length 4][recipe][flag 1][body]
```

`rules` is the version of the rules a file is rebuilt under (decision 2), a
`uint32` that starts at 1. `recipe length` is the length in bytes of the
recipe that follows (decision 3). The magic, the version, the stamp, `rules`
and `recipe length` keep their places and their encoding in every format
from now on: they are the one part of the format that is a compatibility
surface before a public release. Everything after the recipe changes in place
as before, under the stamp.

The version number moves from 6 to 7 for the `.shum` and from 3 to 4 for
the `.shmo`, with Shumway 0.9.4, and is frozen there. A file of an earlier
version has no recipe, and the number is how a reader knows: read as version
7, its flag byte and body would be taken for a rules version and a length.

### 2. The rules version is raised by hand

A recipe says what to compile and how; the rules say what that means. The
rules version is raised when the same recipe would build a different program:
an option of a tool means something else, a default changes (a flag a source
is compiled under, how a module name resolves, what a dialect sets), or the
recipe's own encoding changes. It is not raised when the instruction set or
a section's wire form changes: that is what the stamp is for.

An engine names the rules versions it rebuilds (`BundleRecipe.Accepts`). The
first accepts 1. A later engine with rules 2 may accept 1 too, applying the
rules of 1 to such a recipe, or it may not; that is decided when 2 is
written, and said in the amendment that raises it.

A file whose stamp is the engine's loads its code as today, whatever its
rules version: the rules matter only to rebuild.

### 3. What the recipe holds, under rules 1

- The tool that built the file: `shumway-link`, `shumway-lib`,
  `shumway-compile`, or a host that compiled a library (the page).
- The inputs in order. For each, what it is (a source, an object, a member
  of an archive), its module, the name of its source, its dialect, and how it
  was compiled: release or debug, with or without its source, by consult or
  alone.
- Whether the link consulted its sources (`--consult`). A link by consult
  loads its roots into one engine and links every module the load brought
  in; the recipe holds the roots in their order, and the modules they
  brought in are brought in again by the rebuild's consult, as they were by
  the link's.
- For a link, the options that change the product: `--library`, each
  `--entry`, `--goal` with the module it runs in, `--strip`,
  `--allow-undefined`, `--wasm`, the name of each `--dynlib` library, and the
  engine libraries the code calls.
- For each source, a digest of its text and the time it was last written.
- The sources themselves, for a file built with `--with-source`, compressed
  with Deflate, which every runtime reads, a browser's included. They move
  here from the objects and the entries, where the stamp would take them
  with the code: a bundle keeps one copy of each.

A recipe holds no path of the machine that built it: a source is named the
way a library search path finds it.

### 4. One function rebuilds a file from its recipe

`BundleRecipe.Rebuild` reads the recipe of a file of another format and runs
again what the recipe says, under the rules it names, with this engine's
compiler and linker. A source is found by its name, beside the file, in the
collection's `src/`, or on the library search path, and the file may carry
it too. Of the two, the newer is compiled, as `library(X)` takes the newer of
a source and its bundle: a source edited since the build is what the rebuild
compiles, and the warning says so. The product is a file of this format,
whose recipe records what was compiled.

A source taken out of a file, the `src/X.pl` a page writes from a bundle or
one a tool extracts, is written with the time the recipe records for it.
Written with the time it is taken out, it would be newer than the file it
came from, and the file would lose to its own source.

Where it is used:

- `use_module(library(X))` that finds a bundle of another format rebuilds it
  and loads what that builds, with a warning that says it did. It does not
  write the file back into a library directory: linking it again keeps it.
- A page that imports a collection, or puts back the libraries it keeps,
  rebuilds a library whose bundle is of another format, and stores what that
  builds.
- `shumway-link` and `shumway-lib` given an object of another format rebuild
  it when its source is found, and say so.

A library the page compiled itself, from `X.pl` alone, has that as its recipe,
so it is rebuilt as it is compiled today.

### 5. A file that cannot be rebuilt is refused, with the reason

In place of loading something else, the file is refused, and the message says
which of these it is:

- it is of an earlier version and carries no recipe: build it again;
- its rules version is one this engine does not rebuild: link it again with
  this engine's tools;
- a source the recipe names is not there and the file does not carry it: the
  message names the source;
- the rebuild failed: the message is the compiler's or the linker's.

A file the recipe says nothing can rebuild from, a saved state
(`save_state/1`), is refused as today.

A library of a collection whose bundle is refused this way is not compiled
from `X.pl` behind the user's back: that is how a `--goal` was lost.

### 6. What the stamp covers

The stamp is computed as before and covers what follows the recipe. The
mark for the wire form is raised once for the new header; `BundleStampTests`
pins the image of a fixed bundle, recipe included.

## Consequences

- A bundle built from now on outlives its format: an engine that accepts its
  rules rebuilds it, so the instruction set can change without every library
  of a collection, and every page's stored library, going back to its
  source by hand.
- The goal of a library linked with `--goal`, the order of its inputs and the
  options of its link survive a change of format.
- Every `.shum` and `.shmo` built before this is refused once more, and
  carries no recipe.
- The header grows by eight bytes and the recipe. A recipe without sources is
  a few hundred bytes; one with sources holds what `--with-source` held, once.

## Not provided

- Rebuilding an executable or a class library (`--exe`, `--dll`). They are
  not read by the engine; a new engine runs the program they were built
  from once that is linked again.
- Rebuilding the native code a bundle depends on (`--foreign-dll`,
  `--native-dll`). The recipe names them, and a rebuild needs them where the
  link found them.

## Tests

To write with the implementation:

- the header of a `.shum` and a `.shmo`: the rules version and the recipe
  read the same with the stamp changed;
- a library linked with `--library --goal` from five sources, its stamp
  changed: rebuilt, the five in order, the goal run once in its module;
- rebuilt with its sources carried, and with them found by name beside it;
  a source found that is newer than the one carried is the one compiled;
- a source taken out of a bundle has the time its recipe records, and the
  bundle stays the library;
- a library linked with `--consult`: rebuilt by consult, with the modules
  its roots brought in;
- each refusal of decision 5, with its message;
- the page: a collection whose bundle is of another format rebuilds it, and
  one whose bundle cannot be rebuilt is refused and not compiled from `X.pl`;
- a rules version the engine does not accept is refused.
