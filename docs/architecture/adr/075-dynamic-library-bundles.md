# ADR-075: Dynamic Library Bundles

## Status

Accepted (2026-11-26), proposed 2026-11-23. Every decision is implemented,
and the tests below hold the cases the design names. Extends section 4
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

Every library written for that runtime, one module per source file and
bytecode only, is a bundle of 967 modules and 48,609 predicates: 65.8 MB,
9.4 MB with the bundle's own compression. Loading all of it as bundles load
today takes 9 s on the desktop and 541 MB. Reading and parsing the file is
0.4 s of that. The code is 1.5 s. The clauses the modules give to the
runtime's dynamic tables, 99,861 of them, take 1.1 s to install and 4.2 s
more at the first query, with 270 MB; 94% of them are tables of properties
that an ordinary run may never ask for. This is why neither the code nor the
dynamic clauses are loaded when the library is attached.

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

1. **A dynamic library is a library bundle, and its registry is what each
   entry already says of its module**: its name, whether it has a
   `:- module/2` directive, its exports and imports, the predicates it
   defines with their kind, its dynamic clauses, its operators and flags,
   and its initialization goals. Any bundle can be attached, and nothing
   special is asked when it is built: one linked with `--library` offers all
   of its modules, one linked from entry points what its link kept.
2. **Attaching it declares every module and loads no code.** Each module
   exists from then on, with its exports, its imports, its operators, its
   flags and its dynamic predicates declared. The code of its predicates
   and the clauses of its dynamic predicates stay in the bundle, which is
   read whole into memory. Decision 10 is the exception.
3. **A library is attached in two ways.** By `ensure_loaded(bundle(x))`, a
   directive or a goal, which finds `x.shum` on the library search path. By
   the linker: `shumway-link --dynlib x.shum` (`-dl`) resolves the inputs
   against the library (decision 5), and the bundle it writes attaches `x`
   when it loads. The option is repeatable, one per library, and takes a
   file or a name found in the `-L` directories. A directive
   `:- ensure_loaded(bundle(x))` in an input means the same to the linker.
   `library(x)` keeps its meaning: it loads `x`.

   The compiler records the directive in the module's `.shmo`, by name. The
   link finds `x.shum` on the library search path, before it looks there for
   the inputs' dependencies, so a module the library has is not linked in
   from a source. A link through the consult (`--consult`) takes the file
   the consult attached. A library the link does not find fails it; under
   `--allow-undefined` it is recorded with no uses, and the bundle attaches
   it when it loads, as the source does when it is consulted. A librarian
   archive whose member records it attaches it when it loads, found next to
   the archive or on the search path.
4. **The module is the unit of code that is brought in.** Bringing a module
   in loads the code of all its predicates and its Tier-1 code. When a
   running goal brings it in, the code is linked into that goal, as a
   bundle loaded by a goal is, and it stays linked for the queries that
   follow ([ADR-015](015-persistent-code-space.md), amendment of
   2026-11-24).

### How a call finds a predicate of a library

5. **A call the linker sees is resolved when it links.** The inputs' calls
   are resolved against the registries of the libraries given with
   `--dynlib`. A call that the inputs, the engine and those libraries leave
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

9. **A dynamic predicate is the unit of clauses that is brought in.** Its
   clauses are installed the first time it is touched, by a call,
   `clause/2`, `assertz/1`, `retract/1` or a listing, and then all of them
   are, from every module of every attached library that gives it clauses.
   Nobody sees the predicate with part of them. The bodies of those clauses
   call their module's predicates, which bring the module in when they run.
   Expansion hooks, and the operators and flags a module sets, are there
   from the attach. While a predicate has clauses pending, its first clause
   is one the engine put there: it installs them, as `assertz/1` would each,
   and calls the predicate again. A call pays nothing for this once they are
   installed, and a predicate with nothing pending never did. A module
   brought in at the attach (decision 10) leaves its clauses pending too, so
   the predicate gets them in the library's order.
10. **A module with initialization goals is brought in when the library is
    attached**, and its goals run then, in the library's order, after
    decision 9. A shared library of C does the same: binding is lazy, the
    constructors run when the library is loaded. Running them when the module
    is first used would make their effects, their errors and their order
    depend on what the program happened to call.
11. **The predicates of an attached module are defined.**
    `predicate_property/2`, `current_predicate/1` and `current_module/1`
    answer from the registry, and bring nothing in. `clause/2` and
    `listing/1` of a static predicate bring its module in; of a dynamic one,
    its clauses (decision 9).

### Tiers

12. **Each module comes in the form its bundle chose.** Bytecode is always
    there; Tier-1 code (IL, or WebAssembly modules) is optional. Linking
    against a library asks nothing about its tiers.
13. **The code a bundle carries calls into another bundle, or into another
    module of a dynamic library, by name**, in either direction and in
    either tier, its persisted IL and its baked WebAssembly. No region, no
    inlined fact and no direct call of that code crosses that line, so a
    module can be brought in, and a bundle replaced, without rebuilding its
    callers. The code the host compiles at run time follows decision 14.
14. **A module that came as bytecode is promoted by the host's rules**, the
    ones a consulted source gets: by count, or compiled after it is brought
    in where the host compiles a program after each consult. Nothing is
    special to a library. Brought in on demand, the cost follows what is
    used: the page compiled the 1,880 predicates of the application above
    in 5.6 to 7.1 s because it came as one module.

### A name offered twice

15. **A name that two modules would offer is a conflict, and the attach
    fails before anything comes in.** Two modules offer the same predicate
    when both would answer an unqualified call to it: two libraries, the
    program and a library in either order, and two modules of one library,
    which `shumway-link --library` refuses (`shared_library_name`). A
    dynamic predicate that both declare is one predicate; a static and a
    dynamic one are a conflict. The privates of a module with `:- module/2`
    never are. The attach raises `permission_error(import, procedure,
    M:N/A)` and names the module that defines the name. A clause that a
    consult gives to a name an attached library offers is ignored, with an
    error.

### What does not change

Linking everything into one bundle, and `--exe`. `--allow-undefined`, for
the cases it was made for: a program that defines predicates at run time,
and a host that provides them. A library that names predicates its host
lacks behind a feature test defines them itself, failing, and then links
without the option. The mechanism is the same on the desktop and in the
page.

## Consequences

- Attaching, and bringing each module in when it is used, pays for a bundle
  of many modules of which a program uses some. A runtime of one or two
  modules is linked with `--library` all the same, so that the link keeps
  all of it; its initialization goals bring it in at the attach, and it is
  loaded whole at once, as without this.
- A program's bundle holds its own code, and a missing predicate is found
  when it is linked or when it is loaded, not in the middle of a run.
- A runtime that keeps a table of the entities loaded, each entity's file
  giving its entry to the runtime's dynamic predicates, works unchanged and
  on demand: the first look at the table installs its entries, all of them,
  and the first call into an entity brings its module in. A table nobody
  asks for is never installed.
- Measured on the desktop with the 967 modules of the Context: attaching
  takes 1 s, 0.4 s of it reading the file, against 6.5 s with the clauses
  installed at the attach. The first message to an object of a library
  takes 0.13 s and installs the 5,500 clauses of the tables it reads; the
  next takes 3 ms. The initialization goals of that library add 1.9 s, most
  of it one module that looks for a program on the machine.
- The same in a page, the runtime with its WebAssembly baked and the 967
  modules as bytecode, 17.5 MB together in a `.tar.gz`: the library is
  attached in 3.1 to 3.2 s, the initialization goals included. The first
  message to an object of a library takes about 0.6 s, half of it the
  browser compiling WebAssembly the first time it runs, and the second
  0.19 s; from then on a message takes what any query of the page does.
- A file whose only content is an initialization goal that loads other
  files, a loader, would be brought in at once by decision 10 and should
  stay out of a dynamic library: the registry holds the dependencies.
- Attaching reads and parses the whole file, which needs no index: 0.4 s
  and 85 MB on the desktop for the bundle of 967 modules of the Context,
  0.14 s in a browser.
- A call by name into an attached library costs what a call to a predicate
  of the program does once the host compiles them (decision 14): measured
  on the desktop in Tier-1 IL, 41 ns a call for both, a library without
  `:- module/2` called by the bare name and a module with it imported by
  `use_module/1` alike. In Tier-0 every form costs the same. Not measured in
  WebAssembly.
- A module brought in by a goal running on a buffer the host has replaced
  is linked with the whole program by the next query. The goal that runs on
  the host's buffer keeps the code it brings in, the query that attaches
  included (ADR-015, amendments). A notebook's server is one query.
- In a page a table that gets hot is compiled to WebAssembly as a snapshot
  in the background, and one over 3,000 instructions is not compiled
  (ADR-054, amendment of 2026-11-25). A change to the table drops its
  snapshot, which is compiled again when the table gets hot again.
- A library linked with `--library --wasm` carries one WebAssembly module
  for its code, whose modules call each other through the table. Attached,
  it installs with the modules a program brought in, and again in the goal
  that brings in another, so a goal that never ends, a notebook's server,
  runs each module's code as it comes in; the query setup after that
  installs it against the program. A call into a module that has not come
  in finds no row in the table and brings the module in. Measured in a
  browser, a call between two of its modules costs what a call inside one
  does, and each module that comes in compiles the WebAssembly module again:
  about 28 ms a module, for a library of 40 modules and 1.9 MB. The helpers
  of the library's dynamic rules are a module of their own, installed once
  every module of the library is in. A link that is not a library bakes a
  module that jumps between its modules, which installs once all of them
  are in. The fact inlining of persisted IL crosses entries.
- An update of the runtime needs no rebuild of the programs linked against
  it while it still offers what they call. A predicate that keeps its name
  and changes its meaning is not detected.
- The bundle format gains the registry and the record of decision 5, and
  the object format the record of decision 3, which moves their stamp
  (ADR-009, amendment): every bundle and every object is built again.

## Not provided

- A module marked to run its initialization at first use, for a library
  where decision 10 brings in too much.
- A version a library declares, which would detect a predicate that keeps
  its name and changes its meaning.
- Detaching a library.

## Tests

The cases the design has to hold, and the classes that hold them:

- a program linked against a library with a call nobody defines fails to
  link; loaded over a library that no longer offers a predicate it calls,
  it fails to load and names it (`LinkingAgainstALibraryTests`);
- a module is not loaded by attaching, and is loaded whole by the first
  call into it, from the top level, from another bundle and through
  `call/1`, in the query that makes the call (`DynamicLibraryBundleTests`);
- a module with `:- module/2` is not brought in by an unqualified call, and
  is by `use_module/1`, by `m:Goal` and by a recorded import
  (`DynamicLibraryBundleTests`, `LinkingAgainstALibraryTests`);
- a dynamic predicate that two modules give clauses to has none installed
  after the attach and all of them at its first call, `clause/2`,
  `assertz/1` or `retract/1`, in the order of the modules;
  `predicate_property/2`, `current_predicate/1` and `current_module/1`
  answer after the attach and install nothing (`DynamicLibraryBundleTests`);
- a name that two modules offer fails the attach, between two libraries,
  between the program and a library in either order, and inside one library
  at its link, and a dynamic predicate that both declare does not
  (`DynamicLibraryNameConflictTests`);
- a bundle linked from entry points is attached and offers what its link
  kept, its entry called qualified as well (`DynamicLibraryBundleTests`);
- a module with initialization goals is brought in at the attach, and its
  goals run once, before those of the bundle that attached the library
  (`DynamicLibraryBundleTests`, `LinkingAgainstALibraryTests`);
- a module with compiled code calls one whose code is not in, and the
  reverse, in both tiers, and from a goal that is running: the library's
  persisted IL (`DynamicLibraryCompiledCodeTests`), code the host promoted
  to IL or to WebAssembly (`DynamicLibraryBundleTests`,
  `DynamicLibraryWasmTests`), and the library's baked WebAssembly,
  installed with the modules that are in, in the goal that brings one in,
  and calling into one that has not (`DynamicLibraryBakedWasmTests`,
  `LibraryBakeCliTests`);
- a library's baked modules are queued at the attach and again as a module
  comes in (`LibraryWasmQueueTests`).
