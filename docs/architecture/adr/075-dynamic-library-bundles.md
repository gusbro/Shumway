# ADR-075: Dynamic Library Bundles

## Status

Proposed (2026-11-23). Design only; nothing is implemented. Extends section 4
of [ADR-038](038-library-loading.md), which loads the dependencies a
librarian archive leaves out, and builds on the amendment of 2026-11-23 to
[ADR-056](056-private-predicates-stay-private.md).

## Context

A program compiled for a runtime library can be delivered in two ways.
Linked with the library, the bundle is self-contained: this is what an
executable wants, and it stays as it is. Linked apart, the library is one
bundle and each program compiled for it is another. A site that offers many
programs over one runtime needs the second form: the runtime is stored and
downloaded once.

The second form works today, with no guarantee:

- The linker cannot tell a call into the library from a call to a predicate
  that does not exist. The program links only with `--allow-undefined`,
  which turns every missing-predicate error into a warning, the real ones
  too.
- The program's bundle does not say which library it needs. Something else
  has to load the library first, and all of it.
- Loading a library loads every module of it, used or not.

Size decides the shape. Measured with one runtime library and one
application of 33 objects compiled for it:

| | with WebAssembly | bytecode only |
|---|---|---|
| runtime library | 67.1 MB | |
| application | 31.6 MB | 2.8 MB |
| application, in a `.tar.gz` with its source | 2.9 MB | 0.5 MB |

A user who compiled the runtime with every library written for it into one
bundle with WebAssembly reports 985 MB, and compiling that much in the page
ends the browser's runtime, out of memory. The shape that fits is a runtime
with its Tier-1 code baked, and the libraries as bytecode, each brought in
when it is used and promoted as its predicates get hot.

Verified in two browsers: a runtime bundle with baked WebAssembly and an
application bundle with bytecode only load one after the other, the
application calls the runtime, and the code the page compiles for the
application at run time calls the runtime's baked module.

The model is the shared library of C, with one difference. A shared library
is code already compiled. A bundle holds predicates, each module in the form
its author chose: bytecode always, Tier-1 code when it was baked, and the
bytecode of one module reaches Tier-1 at run time while another's is there
from the start.

## Decision

### What a dynamic library is

1. **A dynamic library is a bundle with a registry.** The registry lists the
   bundle's modules and, for each: its name, whether it has a `:- module/2`
   directive, the predicates it offers with their properties, the clauses it
   gives to predicates of other modules, the operators and flags it sets
   outside itself, the modules it needs, and whether it has initialization
   goals.
2. **Attaching it reads the registry.** No code of the library is loaded,
   except what decisions 9 and 10 bring in.
3. **A library is attached in two ways.** By `ensure_loaded(bundle(x))`, a
   directive or a goal, which finds `x.shum` on the library search path. By
   the linker: `shumway-link --against x.shum` resolves the inputs against
   the library (decision 5), and the bundle it writes attaches `x` when it
   loads. A directive `:- ensure_loaded(bundle(x))` in an input means the
   same to the linker. `library(x)` keeps its meaning: it loads `x`.
4. **The module is the unit that is brought in.** Bringing a module in loads
   all of it: its predicates, its dynamic clauses, its operators, its Tier-1
   code. When a running goal brings it in, its code is linked into that
   goal, as a bundle loaded by a goal is.

### How a call finds a predicate of a library

5. **A call the linker sees is resolved when it links.** The inputs' calls
   are resolved against the registries of the libraries given with
   `--against`. A call that the inputs, the engine and those libraries leave
   undefined is an error, as today. The bundle records, for each library,
   the predicates it calls from it.
6. **Loading that bundle brings in what it references.** The library is
   attached, each predicate the bundle recorded has to be in the registry,
   and the modules that define them are brought in before the bundle's own
   code. A predicate that the registry does not have is an error of the
   load, which names the predicate and the library. The library is not
   compared in any other way: one that still offers the predicates is
   accepted, as a shared library of C that still has the symbols is.
7. **A call made at run time finds the module on demand.** A goal built at
   run time, or any call to a predicate that is not defined, consults the
   registries of the attached libraries, in the order they were attached,
   before `existence_error` is raised (or before the call fails, when the
   `unknown` flag says so). When a module offers the predicate it is brought
   in and the call goes on. This is the path of an undefined predicate: a
   call that resolves pays nothing.
8. **What each kind of module offers.**
   - A module without a `:- module/2` directive offers every predicate it
     defines, the compiler's helpers excepted. They are `user`'s (ADR-056,
     amendment), so the call of decision 7 comes from anywhere.
   - A module with `:- module/2` offers its exports, and there is no
     autoload: an unqualified call to an export nobody imported is an
     `existence_error`, as it is for a loaded module. It is brought in by
     `use_module/1,2` naming it, by a qualified call `m:Goal`, and by the
     imports the linker recorded.

### What is there from the moment it is attached

What a program observes does not depend on which modules have been brought
in.

9. **What a module gives outside itself is installed when the library is
   attached**: the clauses it gives to a multifile or dynamic predicate of
   another module, its expansion hooks, and the operators and flags a module
   without a module directive sets for `user`. The bodies of those clauses
   call the module's own predicates, which bring it in when they run.
10. **A module with initialization goals is brought in when the library is
    attached**, and its goals run then, in the library's order, after
    decision 9. A shared library of C does the same: binding is lazy, the
    constructors run when the library is loaded. Running them when the module
    is first used would make their effects, their errors and their order
    depend on what the program happened to call.
11. **The predicates of an attached module are defined.**
    `predicate_property/2`, `current_predicate/1` and `current_module/1`
    answer from the registry. What the registry cannot answer brings the
    module in first: `clause/2`, a count of clauses, `listing/1`, and an
    `assertz/1` or `retract/1` on a dynamic predicate the module owns.

### Tiers

12. **Each module comes in the form its bundle chose.** Bytecode is always
    there; Tier-1 code (IL, or WebAssembly modules) is optional. Linking
    against a library asks nothing about its tiers.
13. **A call into another bundle, or into another module of a dynamic
    library, is a dispatch by name**, in either direction and in either
    tier. No region, no inlined fact and no direct call crosses that line,
    so a module can be brought in, and a bundle replaced, without rebuilding
    its callers.
14. **A module that came as bytecode is promoted by count**, as a consulted
    program is. A host that compiles the whole program after each consult
    does not do so for a library's module. Measured: that default compiled
    the 1,880 predicates of the application above at the first query after
    its load, 5.6 to 7.1 s.

### What does not change

Linking everything into one bundle, and `--exe`. `--allow-undefined`, for
the cases it was made for: a program that defines predicates at run time,
and a host that provides them. A library that names predicates its host
lacks behind a feature test defines them itself, failing, and then links
without the option. The mechanism is the same on the desktop and in the
page.

## Consequences

- A program's bundle holds its own code, and a missing predicate is found
  when it is linked or when it is loaded, not in the middle of a run.
- A runtime that keeps a table of the entities loaded, each entity's file
  giving its entry to the runtime's dynamic predicates, works unchanged and
  on demand: the entries are there from the moment the library is attached,
  and the first call into an entity brings its module in.
- A file whose only content is an initialization goal that loads other
  files, a loader, would be brought in at once by decision 10 and should
  stay out of a dynamic library: the registry holds the dependencies.
- An update of the runtime needs no rebuild of the programs linked against
  it while it still offers what they call. A predicate that keeps its name
  and changes its meaning is not detected; a version a library declares
  could cover it later.
- The bundle format gains the registry and the record of decision 5, which
  moves its stamp (ADR-009, amendment): every bundle is built again.

## Open questions

- **Which tool writes a dynamic library, and with which option.** And what
  `ensure_loaded(bundle(x))` does with a bundle that has no registry: load
  it whole is the obvious answer.
- **Where the registry and each module sit in the file**, so that a module
  is read without reading the bundle, in a browser's storage too. A bundle
  with every module is still downloaded whole.
- **Two attached libraries that offer the same predicate.** The order of
  attaching decides in this proposal; an error at the second attach is the
  alternative.
- **A module marked to run its initialization at first use**, for a library
  where decision 10 brings in too much. Not provided now.
- **What a dispatch by name across bundles costs** against a call inside one
  module, in each tier. Not measured.
- **The undefined-predicate path of Tier-1 code**, which has to reach the
  registries as the interpreter's does. To be verified.
- **Detaching a library** is not provided.

## Tests

To be written with the implementation. The cases the design has to hold:

- a program linked against a library with a call nobody defines fails to
  link; loaded over a library that no longer offers a predicate it calls,
  it fails to load and names it;
- a module is not loaded by attaching, and is loaded whole by the first
  call into it, from the top level, from another bundle and through
  `call/1`, in the query that makes the call;
- a module with `:- module/2` is not brought in by an unqualified call, and
  is by `use_module/1`, by `m:Goal` and by a recorded import;
- the clauses a module gives another module's predicate are there after the
  attach, and so is the answer of `predicate_property/2`,
  `current_predicate/1` and `current_module/1`; `clause/2` and `assertz/1`
  bring the module in;
- a module with initialization goals is brought in at the attach, and its
  goals run once, before those of the bundle that attached the library;
- a bytecode module over a module with baked Tier-1 code, and the reverse,
  in both tiers, and from a goal that is running.
