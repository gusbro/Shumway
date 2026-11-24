# ADR-075: Linking a Bundle Against a Library It Does Not Contain

## Status

Proposed (2026-11-23). Design only; nothing is implemented. Extends section 4
of [ADR-038](038-library-loading.md), which already loads the dependencies a
librarian archive leaves out, to the bundles the linker writes, to libraries
without a module directive, and to a check at link time and at load time.

## Context

A program compiled for a runtime library can be delivered in two ways. Linked
with the library, the bundle is self-contained: this is what an executable
wants and it stays as it is. Linked apart, the library is one bundle and each
program compiled for it is another, and the program's calls into the library
resolve when it loads. A site that offers many programs over one runtime
needs the second form: the runtime is stored and downloaded once, and a
program is loaded when it is used.

The second form works today, with no guarantee:

- The linker cannot tell a call into the library from a call to a predicate
  that does not exist. The program links only with `--allow-undefined`, which
  turns every missing-predicate error into a warning, the real ones too.
- The program's bundle does not say which library it needs. Something else
  has to load the library first.
- Nothing checks that the library loaded is the one the program was compiled
  for. A program's compiled code may call internal predicates of its runtime,
  which change from one release of the runtime to the next. A mismatch shows
  as an `existence_error` in the middle of a run, or as a wrong answer.

Section 4 of ADR-038 has part of this for one case. A librarian archive
records the `use_module(library(D))` dependencies of its members and loads
the missing ones before its own code, and a call from its baked WebAssembly
module into a dependency is a relocation by name. That covers a library with
`:- module/2` that imports another. It does not cover a bundle the linker
writes, a dependency that no `use_module` states (a file without a module
directive calls its runtime by name), or any check.

Size decides how the tiers are used. Measured with one runtime library and
one application of 33 objects compiled for it:

| | with WebAssembly | bytecode only |
|---|---|---|
| runtime library | 67.1 MB | |
| application | 31.6 MB | 2.8 MB |
| application, in a `.tar.gz` with its source | 2.9 MB | 0.5 MB |

A user who compiled the runtime with every library written for it into one
bundle with WebAssembly reports 985 MB. Compiling that much in the page ends
the browser's runtime, out of memory. So the forms a site can use are a
runtime with its Tier-1 code baked, and libraries delivered as bytecode that
reach Tier-1 as their predicates get hot.

Verified in two browsers: a runtime bundle with baked WebAssembly and an
application bundle with bytecode only load one after the other, the
application calls the runtime, and the code the page compiles for the
application at run time calls the runtime's baked module.

## Decision

1. **A bundle may be linked against a library bundle.**
   `shumway-link --against lib.shum` reads what the library defines and
   resolves the inputs' calls against it. None of the library's code enters
   the output. A call that neither the inputs, the engine nor a library given
   this way defines is an error, as today. What the library defines is: its
   exports for a module with `:- module/2`, imported as `use_module` imports
   them; its public predicates; and, for a library without a module
   directive, every predicate of it, since they are `user`'s (ADR-056,
   amendment).
2. **The bundle records what it needs.** For each library it was linked
   against: the name `library(Name)` resolves, the predicates it calls from
   it, and the library's identity (decision 5). This is one record with the
   `use_module` dependencies of ADR-038 section 4, which gain the same two
   fields.
3. **Loading a bundle loads what it needs first.** Each library the bundle
   names that is not loaded is loaded through `library(Name)`, in the order
   of ADR-038 section 2, before the bundle's own code. Then every predicate
   the bundle calls from it must be defined: one that is not is an error of
   the load, which names the predicate and the library, and the bundle is not
   loaded. The library's initialization goals have run before the bundle's.
4. **A call between bundles is a dispatch by name.** No region, no inlined
   fact and no direct call crosses from one bundle into another, in either
   direction and in either tier, so a bundle can be replaced without
   rebuilding the ones that need it. This is what the linker and the baked
   modules already do for a predicate they do not hold.
5. **A library has an identity.** Proposed: a digest of the predicates it
   defines and their compiled code. A bundle loaded over a library with
   another identity than the one it was linked against is refused, with a
   message that says to build it again. See the open questions.
6. **Each bundle chooses how much of it is Tier-1.** A bundle always holds
   bytecode. Its Tier-1 code (IL, or WebAssembly modules) is optional and is
   the bundle's own business: linking against a library asks nothing about
   the library's tiers. A bundle with bytecode only runs on the interpreter,
   and its predicates are promoted by count, as a consulted program's are.
7. **A host does not compile a library bundle wholesale.** The page's
   default, compile the whole program after each consult, applies to the
   program in the workspace. A library that came as a bundle with bytecode
   only is promoted by count. Measured: the default compiled the 1,880
   predicates of the application above at the first query after its load, 5.6
   to 7.1 s; with a collection of libraries it is the out-of-memory case.

`--allow-undefined` stays for the cases it was made for: a program that
defines predicates at run time, and a host that provides them. A library that
names predicates its host lacks behind a feature test (thread predicates, for
one) defines them itself, failing, and then links without the option.

Linking everything into one bundle is unchanged, and `--exe` stays that.

## Open questions

- **How strict the identity is.** A digest of the code refuses a program
  after any rebuild of its library, including one that changed nothing the
  program uses. The alternatives: check only that the predicates the program
  calls are defined (decision 3 alone), or let a library declare a version
  that its author changes when its internals do. The strict form is the safe
  default for a runtime whose compiled programs call its internals.
- **The name of the option**, and whether a directive in the source
  (`:- ensure_loaded(library(Name))` in a file compiled alone) states the
  same dependency.
- **The librarian.** `shumway-lib` writes the archives of ADR-038 section 4
  and the linker writes library bundles; both would carry the record of
  decision 2. Whether one of them is enough for a library is not decided.
- **What a call across bundles costs** in each tier, against a call inside
  one module. Not measured.
- **Dependencies between the libraries of one runtime**, which their sources
  state in the runtime's own terms and not as `use_module`: whether the tool
  that compiles them passes them to the linker, or the linker finds them by
  resolving each library's calls against the others.

## Consequences

- A program bundle is small: it holds its own code. A site stores the
  runtime once.
- A missing predicate is found when the program is linked, and a wrong
  library when it is loaded, instead of in the middle of a run.
- The bundle format gains a record, which moves its stamp (ADR-009,
  amendment): every bundle is built again.
- A program linked against a library cannot be loaded without it. Whoever
  publishes the program publishes the library with it, or names where it is.

## Tests

To be written with the implementation. The cases the design has to hold:
a program linked against a library with a missing predicate fails to link; a
program loaded with no library on the search path fails to load and names
it; a library of another identity is refused; the library's initialization
runs first; a bytecode-only program over a library with baked Tier-1 code,
and the reverse, in both tiers; the same from a goal that is running.
