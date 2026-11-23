# Hercutalk web: Logtalk notebooks in the browser, from two repositories

A plan to agree with Paulo Moura. It says what Hercutalk web is, where each
piece lives, who does what, how each side builds and publishes, and what has
to be measured or decided first. Nothing in it is built.

**State (2026-11-21).** The Prolog variant is published
([gusbro.github.io/Shumway/jupyter](https://gusbro.github.io/Shumway/jupyter/)):
a JupyterLite site whose kernel runs Prolog on WebShumway. This plan is the
second variant of ADR-066 D2. The facts about Hercutalk are read from
`logtalk-jupyter-kernel` 0.47.1 (73e2175).

## 1. What Hercutalk web is

The same Logtalk notebooks, with the same kernel identity (`logtalk_kernel`,
language `Logtalk`, `.lgt`), run in a static web page with no server behind
it. One `.ipynb` runs on the desktop and on the web (ADR-066 D11).

Hercutalk has two halves: a Python kernel on `ipykernel`, which starts the
backend as a process and talks to it over its standard input and output, and
a JSON-RPC server written in Logtalk, which owns what a cell means. On the
web there is no Python and no process:

| Hercutalk on the desktop | Hercutalk web |
|---|---|
| The Python kernel (`logtalk_kernel/*.py`) | Shumway's kernel core, in TypeScript, a JupyterLite extension |
| A backend process, one of eight systems | WebShumway: the engine compiled to WebAssembly, one session for each kernel |
| The Logtalk server (`logtalk_kernel/logtalk_server/*.lgt`) | The same sources, unchanged but for section 4 |
| Standard input and output of the process | Two streams of the session, in memory |
| Graphviz's `dot`, matplotlib, a local HTTP server for widgets | Drawn in the page: section 3 |

The Logtalk server is the part that stays. That is what keeps the two
editions one kernel: a cell means the same in both because the same code
decides it.

## 2. Where each piece lives

Dependencies point one way: Logtalk and Hercutalk depend on Shumway, never
the reverse (ADR-066 D3). Nothing of Logtalk enters Shumway's repository.

| Piece | Repository | Owner |
|---|---|---|
| The engine, the WebAssembly app and its JavaScript (sessions, threads, the service worker that isolates the page, the files mirror) | `gusbro/Shumway` | Shumway |
| The kernel core: Jupyter's messages to and from the server's protocol, tables, graphs, plots, completion, inspection, input, interrupt | `gusbro/Shumway` | Shumway |
| The Prolog variant and its server in Prolog | `gusbro/Shumway` | Shumway |
| The Logtalk adapter for Shumway, `shumwaylgt`, the embedding of a precompiled Logtalk core | `LogtalkDotOrg/logtalk3` | Logtalk |
| The Logtalk server of the kernel | `LogtalkDotOrg/logtalk-jupyter-kernel` | Logtalk |
| The Logtalk variant: its identity, its server in a form a session loads, its site and its example notebooks | `LogtalkDotOrg/logtalk-jupyter-kernel`, or a repository beside it | Logtalk |
| The Logtalk mode of the editor | `LogtalkDotOrg/jupyterlab-logtalk-codemirror-extension` | Logtalk |

## 3. What the page does in place of the Python half

The kernel core already speaks Hercutalk's protocol: the Prolog variant's
server was written against it and is compared with Hercutalk's answers on a
corpus of sessions. What the Python half does with a response, and what the
core does:

| Response | Python half | Kernel core today |
|---|---|---|
| bindings, output, errors (`logtalk_message`), `halt` | shown as text | the same |
| `print_table` | a Markdown table | the same |
| `print_transition_graph`, `print_sld_tree` | `dot` draws the DOT as SVG | draws the same DOT in the page, with no Graphviz; the DOT stays beside the SVG |
| `show_data` | matplotlib | a Vega-Lite plot built from the same description; `polar`, `hexbin` and `hist2d` are not drawn |
| `predicate_atoms`, `jupyter_predicate_docs` | completion and inspection | the same |
| `print_and_save_table` | writes the CSV or TSV file | **to do**: the core writes it through the session's files |
| `input_html` (widgets, forms) | shows the HTML, whose script posts to a local HTTP server the kernel runs | **to do**, and needs section 4, item 2 |
| `set_prolog_backend` | starts another backend | not applicable: one backend. The core says so |

What a cell writes to a file appears in JupyterLite's file browser, and a
file edited there is seen by the next cell. That exists.

## 4. What changes in Hercutalk's server

Three changes, each small and each harmless on the desktop:

1. **The streams of the protocol.** `jupyter_jsonrpc` reads requests from
   `current_input` and writes responses to `current_output`. In a page the
   requests come on a stream of their own, so that a `read/1` in a cell
   reads what the user types and not the next request. The server takes the
   two streams to use; on the desktop they stay the standard ones.
2. **The callback of widgets and forms.** `jupyter_inputs` writes into the
   HTML a `fetch` to `http://IP:Port`. In a page there is no such server.
   The HTML calls a callback whose text the kernel sets: the `fetch` on the
   desktop, a function of the page on the web. `jupyter_widgets`,
   `jupyter_forms` and `jupyter_inputs` are Apache-2.0 and stay so.
3. **The `protocol` method**, optional: `{"version": 1, "implementation":
   "hercutalk"}`. The core asks it first to refuse a server it cannot talk
   to. Without it the core takes the server for version 1.

Offered, not needed: the extensions the Prolog server has, each additive
(`docs/design/jupyterlite-protocol.md`). `residual_goals` shows the
constraints left on an answer. `alive`, `more` and `stop` (ADR-074) put a
top level's choices under an answer: Next, 5, All and Stop.

## 5. What changes in Shumway

1. **A session loads a server the site gives.** Today a session loads the
   Prolog server, which is inside the app. It will load the server the
   site's configuration names, fetched from the site in the form section 7
   decides, and start it with the goal the variant names. It loads it as
   the top level loads a program named on its command line: the
   predicates of a file with no `:- module/2` are then callable from
   `user`, as when the file is consulted. A `consult/1` of the bundle keeps
   them local to the module the link gives the file, as ADR-008 has it. To
   verify when it is built: that a bundle the browser compiled as a
   library loads the same way.
2. **Variants are data.** The extension registers one kernel for each
   variant the site's configuration lists: the kernelspec, `language_info`,
   the texts for success and failure, the query that gives the names to
   complete, the server and its start goal. The Prolog variant becomes the
   first entry. A server with no `protocol` method is taken for version 1.
3. **The page's side of section 3**: the file of `print_and_save_table`,
   and the widgets' callback.
4. **Logtalk's own files.** A notebook that calls
   `logtalk_load(library(...))` needs the library's sources where the
   engine reads files. The site ships them as an archive, mounted when a
   session first needs them. The mechanism exists for Prolog libraries
   (`jupyter:import_library/1`).
5. **A package** (section 6). The server ships with a bundle compiled
   ahead beside its source (section 7), so also the toolchain as something
   a build elsewhere can download: `shumway-link`, which makes the bundle.
6. **An archive that carries a library compiled.** The import of a
   collection took the sources of an archive and nothing else. It takes
   the bundle beside a source too, and a bundle says in its first bytes
   which format of compiled code it has. Done, on the branch
   `baked-library-in-collection`.

## 6. Packages, and who publishes what

A JupyterLite kernel is installed with `pip` into the environment that
builds a site: `jupyter lite build` then includes it. A kernel on WebShumway
needs more than its extension, and that is why neither variant can be
listed today:

- the runtime, about 62 MB of WebAssembly, served from the site's own
  origin: its threads are workers, and a worker's script is same-origin;
- a page that is cross-origin isolated, which on a static host takes a
  service worker and a script at the top of every page.

Shumway's own site does both with a build script. A package has to do them
for any site.

| Package | Published by | Holds |
|---|---|---|
| `jupyterlite-shumway-kernel` (name to agree) | Shumway, on PyPI, from a tag | the extension (kernel core and the Prolog variant), the runtime, and a JupyterLite addon that copies the runtime into the site, isolates its pages and writes the configuration |
| `jupyterlite-logtalk-kernel` (name to agree) | Logtalk, on PyPI | the Logtalk variant as data, its server in the form section 7 decides, the archive of Logtalk's library, its notebooks, and an addon that adds the variant to the site's configuration. It depends on the package above |

With variants as data, the Logtalk package has no TypeScript and no build
with Node: what is Logtalk's is a description, its server and files. The
alternative is ADR-073's first idea, the core as an npm package and a
Logtalk extension of its own that imports it. It gives the Logtalk side its
own code in the page, and the cost of building it and of following
JupyterLite's API as it moves. This plan proposes data first, and the npm
package when something needs code.

**Sites.**

| Site | Built by | From |
|---|---|---|
| Shumway's, Prolog notebooks | Shumway's repository, as today | its own sources |
| Hercutalk web, Logtalk notebooks | Logtalk's repository | `pip install jupyterlite-core jupyterlite-logtalk-kernel`, `jupyter lite build`, published where Logtalk chooses |
| Anyone's | their repository | either package, or both: the two kernels share one runtime |

**Listing.** JupyterLite's documentation lists kernels ("Adding kernels").
Each side asks for its own to be listed once its package is on PyPI.

## 7. Versions that have to match

| Between | Rule |
|---|---|
| The kernel core and a server | the `protocol` method: a version the core does not speak is refused, with a message |
| The runtime and what the Logtalk package ships of its server | depends on the form it ships in: below |
| Logtalk and Shumway | as today for the desktop: the adapter in `logtalk3` |
| JupyterLite | the Shumway package says which versions of `jupyterlite-core` it is built for; the Logtalk package inherits it |

**The server's form decides how tightly the two packages are tied.** The
Logtalk package has to carry the Logtalk core and the kernel's server in a
form a session can load. There are three:

| Form | The Logtalk package depends on | What it costs |
|---|---|---|
| A bundle compiled ahead, with Shumway's linker, and nothing else | the versions of Shumway with the format of compiled code the bundle has. A bundle says which in its first bytes, and a Shumway with another refuses it | a release of Shumway that changes the format leaves the Logtalk package unusable until it is built and published again |
| Prolog source, as Logtalk's embedding writes it, compiled by the browser the first time and kept in its storage | a wide range: any Shumway the Logtalk adapter supports | the first visit compiles, 23 to 47 s, and so does the first after the format changes |
| Both in one archive: the source, and beside it the bundle compiled ahead | the same wide range | a few seconds on the first visit. After a release of Shumway that changes the format, the first visit compiles from the source, until the Logtalk package is built again |

**This plan proposes the third.** The bundle is a convenience and the source
is what the package depends on, so the two packages are released
independently of each other and neither side follows the other's calendar.
A format of compiled code is not a version: it changes when the instruction
set or the layout of a bundle does, which is not at every release, and a
bundle built once is good for every Shumway with the same one. When it does
change, nothing breaks: the page finds the bundle is of another format,
leaves it out and compiles the source, once, as it does today for any
Prolog library.

The archive is a `.tar.gz` or a `.zip` with `x.pl` and `x.shum` side by side.
A bundle for the browser is written without the bundle's own compression,
which the runtime there cannot read, and the archive's compression does that
work: 93 MB of bundle and 2.5 MB of source are 8.7 MB to download.

Measured (section 9): from the archive with the bundle, 2.2 s to import and
1.7 s to load the first time in Chromium, 3.8 s and 3.3 s in Firefox.
Compiled in the browser with its WebAssembly modules, as the site compiles a
library, 23 to 33 s the first time and 1.6 to 3.4 s on the next visit in
Chromium, 47 s and 5.4 s in Firefox. Compiled to bytecode alone, 6 to 11 s
the first time and 1 to 2 s on the next visit, in the three browsers.

## 8. How each side knows it works

- **The same answers.** Shumway has the harness that runs a notebook server
  with no browser, and a corpus of sessions recorded from Hercutalk on the
  desktop. Hercutalk web is the same server, so the same requests must give
  the same responses in the harness as on the desktop. Shumway makes the
  harness something Logtalk's build can run.
- **In the browser.** Shumway's checks drive a JupyterLab page of a built
  site in Chromium, Firefox and WebKit. They are given the notebooks to
  run, so Logtalk's site runs them on its own.
- **The feature table** (`docs/design/jupyterlite-features.yaml`) has a
  column for Hercutalk web, each row with its test.

## 9. To measure before the design is fixed

Each of these can change the plan, and none needs a change in Hercutalk to
be measured: Shumway does them first, on its own.

| Question | Why it matters |
|---|---|
| How long the browser takes to compile the Logtalk core and the server from source the first time, and how long a session takes to load the result after | it decides the server's form (section 7), and with it whether the two packages are released independently |
| How big is a bundle of the Logtalk core and the server compiled ahead, and how long does a session take to load it in a page | a session of the Prolog variant starts in 0.8 to 1.5 s the first time in a page and in under half a second after; an interrupt restarts the session, so the cost is paid again each time |
| How much memory a Logtalk session takes | the Prolog variant holds ten sessions in a page; a tablet may hold fewer of Logtalk's |
| How fast a cell is, which goes through Logtalk's compiler | whether the bundle needs its WebAssembly compiled ahead |
| Whether the page's service worker can live beside JupyterLite's own | a third party's site may have a kernel that needs JupyterLite's |
| How big the wheel is, and whether the runtime should be a package of its own | PyPI's limit on a file's size |
| How much of Logtalk's library the site ships, and how big it is | the first `logtalk_load(library(...))` waits for it |
| Whether the server's capture of a goal's output, through a file in the working directory, holds with several sessions in a page | each session has its own working directory; to be seen with the server as it is |

**The first two rows, measured on 2026-11-21.** The program is Logtalk
3.103.0's core and Hercutalk 0.47.1's server, as Logtalk's embedding
script for Shumway writes them: one file of 2.46 MB and 41,112 lines,
with the server's eight libraries and twelve objects, on Shumway's main
at 69f2436e. Each figure comes from a notebook session or from the demo's
page.

| | Chromium | Firefox | WebKit |
|---|---|---|---|
| The source compiled into a bundle and kept, the first time | 6.2 s | 11.1 s | 6.2 s |
| That bundle loaded on the next visit | 1.1 s | 1.9 s | no storage in the test browser |
| The source consulted in a session | 3.0 to 3.5 s | 4.9 to 6.4 s | 3.6 to 3.8 s |
| A bundle compiled ahead, of bytecode only, loaded in a session | 0.47 to 0.71 s | 0.61 to 0.82 s | 0.65 to 0.80 s |
| A cell after any of them | about 20 ms | about 30 ms | about 90 ms |

- **The first row is a compile without the WebAssembly module.** With it,
  as the site compiles a library, the module held 5,247 predicates in
  104 MB on 69f2436e. Building it took the runtime past its 2 GB of
  memory, and the runtime stopped: the page had to be reloaded.
- **A bundle compiled ahead** is 4.9 MB of bytecode, 0.86 MB with gzip.
  With its WebAssembly module it was 109 MB on 69f2436e, 8.7 MB with gzip,
  more than a notebook's files can hold. A bundle for the browser is
  written uncompressed: the runtime there has no Brotli codec.
- **The source needed two things.** Clauses of a multifile predicate that
  several libraries extend end up apart when the files are joined, which
  Shumway refuses by default: the source sets `discontiguous_check` to
  `warning`. And the libraries' loader files come with it, and their
  initialization goals look for the libraries' sources each time the
  program loads.

**With the WebAssembly modules, measured on 2026-11-22.** The same program,
on the branch `wasm-bounded-modules`, in the demo's page.

| | Chromium | Firefox | WebKit |
|---|---|---|---|
| The source compiled into a bundle with its modules and kept, the first time | 23 to 33 s | 47 s | 25 s |
| That bundle loaded on the next visit | 1.6 to 3.4 s | 5.4 s | no storage in the test browser |
| What the page compiles after the bundle loads | nothing | nothing | nothing |
| The source consulted in the page, and its program compiled there | 5.1 s and 11.8 s | not measured | not measured |

- **The page builds the module.** Building a module held about 29 times
  its size in memory: every function stayed as a list of instruction
  objects until the whole module was written. Each function is written
  when it is built now, and the build of this module peaks at about 0.5 GB
  on the desktop, where it took 2.9 GB.
- **The module is 74 MB**, from 104. Binding an attributed variable was
  inline at every place a unification could meet one, a fifth of the
  module, and is a function of the module now.
- **One predicate stays on the interpreter.** `$lgt_compile_body/5` has
  26,000 instructions, and its function would be larger than the 7,654,321
  bytes a browser takes for one. A module with such a function is refused
  whole, so the predicate is left out of it.
- **The program's dynamic predicates come compiled too.** Their rules take
  helper predicates when the program is linked, 363 here, numbered by each
  engine as it links. The bundle left them out, and the page compiled 311
  of them at every visit, 1.5 to 2.5 s after the library loaded. They are
  in the bundle now, in a module of their own, named by the rule each
  belongs to. So is a snapshot of each of 35 dynamic predicates, installed
  when the predicate is first called and again after a consult.
- **The bundle in the browser's storage is 94 MB**: the module of the
  static predicates, the helpers' and 35 snapshots. A page closed while it
  stores one leaves a file that is not a bundle, and the library is
  compiled again on the next visit. So is a bundle whose modules another
  build of the runtime made: the format of a module changes between
  builds, and such a bundle was kept and its modules refused at every
  visit.
- **The archive with the bundle, measured on 2026-11-23** in the demo's
  page, on the branch `baked-library-in-collection`, from a server on the
  same machine, so with no time on the network:

  | | Chromium | Firefox |
  |---|---|---|
  | The archive imported: fetched, unpacked and stored | 2.2 s | 3.8 s |
  | The library loaded the first time, nothing compiled | 1.7 s | 3.3 s |
  | Loaded on the next visit | 3.0 s | 4.3 s |

  With a bundle of another format in the archive the import leaves it out,
  the library compiles the first time (31 s in Chromium) and loads in 2.1 s
  on the next visit. Not measured yet: the same in a notebook's session, and
  the memory a session takes.
- **A notebook compiles what its cells define.** A session did not: its
  program ran on the interpreter however often it ran. Each predicate is
  compiled when it has been called 16 times, and a library a cell loads
  runs from the module it was compiled with.

## 10. Who does what

| | Shumway | Logtalk |
|---|---|---|
| 1 | The measurements of section 9, with Hercutalk's server as it is and a local patch for its streams | Review of this plan: the decisions of section 11 |
| 2 | A session loads the site's server; variants as data; the harness as a tool | The streams of the protocol; the `protocol` method |
| 3 | The package, its addon and its release; the toolchain as a download, for the bundle the server ships with | The embedding of the core and the server, in the form chosen; the variant's description |
| 4 | Its own site built from the package | The Logtalk package, its site, its notebooks |
| 5 | The widgets' callback in the page; the file of a saved table | The widgets' callback in the server |
| 6 | Asks for its kernel to be listed in JupyterLite's documentation | The same, for its own |

Steps 1 and 2 end with Hercutalk web running in a site built by hand. Steps
3 and 4 end with it published. Step 5 can come at any point after 2.

## 11. Decisions to take together

1. **Variants as data**, or a Logtalk extension of its own (section 6).
2. **Where the Logtalk variant lives**: in `logtalk-jupyter-kernel`, beside
   the Python kernel whose server it shares, or in a repository of its own.
3. **The names** of the two packages, and where Hercutalk web is published.
4. **The three changes of section 4**, and which of the offered extensions
   Hercutalk takes.
5. **The server's form** (section 7). This plan proposes the source with
   a bundle compiled ahead beside it. The Logtalk package then works with
   any Shumway its source does, and is built again only to give the fast
   first visit back after a release of Shumway that changes the format of
   compiled code. Hercutalk is published by hand and has no CI, so making
   the bundle would be a workflow in Logtalk's repository, or a script run
   by hand.
6. **What Hercutalk web says of itself**: its `implementation` and version
   in `kernel_info`, when the server is Hercutalk's and the kernel is
   Shumway's core.

## Related

ADR-066 (the two variants, ownership), ADR-067 (the protocol and the
server), ADR-068 (sessions), ADR-069 (isolation), ADR-072 (what the kernel
shows; the widgets' callback), ADR-073 (the extension's layout; nothing
published in its phase), ADR-074 (more solutions),
`docs/design/jupyterlite-protocol.md`, `docs/design/jupyterlite-plan.md`.
