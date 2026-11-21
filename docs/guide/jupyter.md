# Jupyter notebooks

Shumway runs Prolog in Jupyter notebooks, in the browser. The notebooks are
a [JupyterLite](https://jupyterlite.readthedocs.io/) site on the runtime of
[WebShumway](webshumway.md): the engine runs in the page, in WebAssembly,
and nothing is installed or sent to a server. The kernel is
**Prolog (Shumway)**.

The site is published at
[gusbro.github.io/Shumway/jupyter/](https://gusbro.github.io/Shumway/jupyter/),
and the demo links to it (**Jupyter notebooks**, in its header).

## Opening a notebook

The site opens on its file browser, with notebooks to start from:

| Notebook | What it shows |
|---|---|
| `first-steps.ipynb` | Queries, answers and `%%user` |
| `grammars.ipynb` | DCGs, and a table of what a grammar parses |
| `constraints.ipynb` | `library(clpfd)`: domains, labeling, puzzles |
| `tables-and-pictures.ipynb` | Tables, and pictures a program draws |
| `graphs.ipynb` | Terms as trees, transition graphs |
| `plots.ipynb` | One plot of each kind |

**File → New → Notebook** makes another, with the kernel Prolog (Shumway).
A notebook saved by another Prolog kernel opens with this one, and nothing
asks which.

The page runs the engine on threads, which needs the page to be
cross-origin isolated. On a host that cannot send the headers for it, such
as GitHub Pages, the site isolates itself with a service worker, and the
first visit may reload once to do it. It works in Chromium-based browsers,
Firefox and Safari; see [If the runtime stops answering](#if-the-runtime-stops-answering).

## Queries and answers

A cell holds a query, or several, each ended by a period. Each answer is
written as the top level writes it:

```prolog
X = 1, Y = f(X).
```
```
X = 1,
Y = f(1).
```

A query that fails says `false.`, and one that raises says the error; the
cell is then marked as an error. What a query writes comes before its
answer, and a cell with two queries answers each in turn:

```prolog
write(hello), nl, X = 1.
Y = 2.
```
```
hello
X = 1.
Y = 2.
```

**More solutions.** An answer that ends with ` ;` is of a query that can
give another solution, and one that ends with a full stop is its last:

```prolog
member(X, [a, b, c]).
```
```
X = a ;
```

Under such an answer are the choices of a top level:

| Button | What it does |
|---|---|
| **Next** | shows the next solution, under the same cell. In command mode the key `;` does the same for the active cell |
| **5** | goes on to the next multiple of five solutions: four after one, five after five |
| **All** | shows every remaining solution |
| **Stop** | ends the query: it gives no more solutions, and its answer ends with a full stop |

While **5** or **All** runs, only **Stop** can be pressed, and it ends the
query when the solutions being computed come: a query is never cut half
way, so the session goes on with all it had. A solution that does not come
is ended with **Interrupt**, which restarts the session.

When several queries can give another solution, the buttons of the latest
are the ones that can be pressed; those of an earlier one can be pressed
again when the later ones have no more solutions or were stopped. Running a
cell again ends the query its run before left. The solutions asked for are
saved with the notebook, and an answer of a session that ended has no
buttons.

A cell that is `retry.` also asks the latest query for its next solution,
and shows it under itself:

```prolog
retry.
```
```
X = b ;
```

**Earlier values.** In a query, `$X` stands for the value `X` had in an
earlier query:

```prolog
X = 3.
```
```prolog
Y is $X * 2.
```
```
Y = 6,
X = 3.
```

**Constraints** are shown where bindings would be:

```prolog
use_module(library(clpfd)).
```
```prolog
X in 1..5, X #> 3.
```
```
X in 4..5.
```

**Input.** A goal that reads, such as `read(X)`, asks for a line in a box
below the cell.

## Defining predicates

A cell that starts with a line `%%user` holds clauses: it writes them to
the notebook's own file, `NAME.user.pl` for `NAME.ipynb`, and loads it.

```prolog
%%user
parent(tom, bob).
parent(bob, ann).
grandparent(X, Z) :- parent(X, Y), parent(Y, Z).
```
```prolog
grandparent(tom, Who).
```
```
Who = ann.
```

A clause written as a query is not run; the cell says where it belongs:

```
A clause is not a query. To define predicates, start the cell with %%user.
```

`%%user+` adds the cell to that file and loads it again, and `%%file NAME`
writes the cell to a file of its own. The magics below list the rest.

## Magics

A cell magic is the first line of a cell; a line magic is the whole cell.

| Cell magic | What it does |
|---|---|
| `%%user` | Writes the rest of the cell to `NAME.user.pl` (`user.pl` in a console) and loads it |
| `%%user+` | Appends the rest of the cell to that file and loads it |
| `%%file FILE` | Writes the rest of the cell to FILE and loads it |
| `%%file+ FILE` | Appends the rest of the cell to FILE and loads it |
| `%%load FILE` | The same as `%%file FILE` |
| `%%save FILE` | Writes the rest of the cell to FILE, without loading it |
| `%%table` | Shows each query of the cell as the table of its solutions |
| `%%csv FILE` | As `%%table`, and writes the table to FILE with commas between the values |
| `%%tsv FILE` | As `%%table`, with tabs between the values |
| `%%highlight` | Shows the cell as code, without running it |
| `%%tree` | Draws each term of the cell as a tree, as `show_term/1` |
| `%%data` | Draws the plot the goal of the cell gives, as `show_data/1` |

| Line magic | What it does |
|---|---|
| `%bindings` | Prints the latest value of each variable of earlier queries |
| `%queries` | Prints the queries that had a solution |
| `%pwd` | Prints the working directory of the session |
| `%help` | Prints the description of the predicates of module `jupyter` |
| `%versions` | Prints the versions of the engine and of the notebook server |
| `%flags` | Shows a table of the Prolog flags and their values |
| `%magic` | Prints this list |

## The `jupyter` predicates

The predicates of module `jupyter` work with the notebook. `jupyter:help.`
describes each, and the kernel shows a predicate's description when asked
where it is written (**Shift+Tab** in JupyterLab). Some also answer
without the module: `print_table/1`, `print_and_save_table/3`,
`print_queries`, `show_term/1`, `show_graph/2`, `show_data/1`, `retry` and
`halt`.

| Predicate | What it does |
|---|---|
| `print_table(Goal)` | A table with a row for each solution of Goal and a column for each of its variables |
| `print_table(Rows, Names)` | A table of a list of rows, under the names of its columns |
| `print_and_save_table(Goal, Format, File)` | As `print_table/1`, and writes it to File, `csv` or `tsv` |
| `display(Mime, Data)` | Shows Data as `image/svg+xml`, `text/html`, `text/markdown` or JSON |
| `trace(Goal)` | Prints the calls, exits, retries and failures of Goal as it runs |
| `show_term(Term)` | Draws Term as a tree |
| `print_transition_graph(Pred, From, To, Label)` | Draws an edge for each solution of Pred |
| `show_graph(Nodes, Edges)` | Draws the edges of one predicate between the nodes of another |
| `show_data(Goal)` | Draws the plot Goal describes |
| `import_library(Spec)` | Brings in a collection of libraries |
| `print_queries`, `print_query_time`, `print_variable_bindings` | What ran: the queries, the time of the latest, the values `$Var` reads |
| `pwd`, `cd(Dir)` | The session's working directory |
| `versions`, `version(Ma, Mi, Pa, Status)` | The versions of the engine and of the notebook server |
| `update_completion_data` | Gives the kernel the names to complete with, after loading code |
| `halt` | Stops the session; the next cell starts a new one |

The names of the predicates a notebook defined complete with **Tab**.

## Tables, graphs and plots

**Tables.** `print_table/1` and `%%table` show a table, written as
markdown, so it reads in a saved notebook too:

```prolog
jupyter:print_table(parent(P, C)).
```

| P | C |
|---|---|
| tom | bob |
| bob | ann |

`%%csv FILE` and `print_and_save_table/3` write the table to a file as
well.

**Pictures a program draws.** `jupyter:display(Mime, Data)` shows SVG, HTML,
markdown or JSON that a query builds as text:

```prolog
jupyter:display('text/markdown', '**bold** text').
```

**Graphs.** `show_term(f(a, g(1, b)))` draws the term as a tree, its
arguments left to right, and `%%tree` draws the terms of a cell.
`print_transition_graph/3,4` draws a predicate's solutions as edges, and
`show_graph/2` draws nodes and edges given by two predicates, each with its
own label, shape and colour:

```prolog
%%user
step(idle, coin, ready).
step(ready, push, idle).
step(ready, coin, ready).
```
```prolog
jupyter:print_transition_graph(step/3, 1, 3, 2).
```

The kernel draws the graph as SVG in the page, and the output keeps its
text in DOT beside the picture. `graphs.ipynb` has an example of each.

**Plots.** `show_data(Goal)` and `%%data` draw a plot. The goal binds
`Data`, or `_Data`, to a list of `Key-Value` pairs: `type` names the kind of
plot, and the other keys are the options of the matplotlib function of that
name.

```prolog
%%data
Data = [type-bar, title-'Fruit sold', x-[apples, pears, plums],
        height-[12, 7, 9], color-[orangered, gold, purple]]
```

The kinds are plot, scatter, step, stem, ecdf, bar, barh, hist, pie,
stackplot, errorbar, boxplot, eventplot, loglog, semilogx and semilogy. An
option the notebook does not draw is named below the plot, as in
`% Not drawn here: hatch.` `plots.ipynb` has one plot of each kind.

A saved notebook keeps each output in a form that needs no kernel to be
shown: a table as markdown, a graph as SVG beside its DOT, and a plot as its
Vega-Lite description. A viewer that does not draw Vega-Lite shows a plot as
its text, such as `bar plot: Fruit sold`.

## Files

The file browser is what the engine sees, under `/drive`. Each notebook's
session starts in the notebook's directory: a file saved or edited in the
file browser is there for `consult/1` in the next cell, and a file a query
writes appears in the file browser.

```prolog
%%file colours.pl
colour(red).
colour(blue).
```
```prolog
findall(C, colour(C), L).
```
```
L = [red, blue].
```
```prolog
open('out.txt', write, _S), write(_S, hello), close(_S).
```

A variable whose name starts with an underscore is not shown in the
answer. `jupyter:pwd` and `jupyter:cd(Dir)` read and change the session's
directory.

## Libraries

`use_module(library(Name))` loads the engine's libraries, as anywhere else:
`clpfd`, `lists` and the others of the
[predicate reference](predicates.md).

Collections of other systems' libraries come in with
`jupyter:import_library/1`:

- `jupyter:import_library(scryer)`, `swi` or `trealla`: the library of that
  system, from the place WebShumway suggests for it;
- `jupyter:import_library(Dialect:Location)` or
  `jupyter:import_library(Location)`: a GitHub directory, a Prolog file, or
  a `.zip` or `.tar.gz` archive, given by its URL or as a file of the
  notebook. A site that does not let other sites read it is named as the
  reason it cannot be read.

After it, `use_module(library(Name))` loads the libraries of the
collection. The browser keeps the collections, for every notebook and for
[WebShumway](webshumway.md#libraries), which shares them: one imported in
the demo is there for the next cell of a notebook.

## Where your work is kept

The notebooks and files are kept by the browser, in the site's storage. They
stay from one visit to the next, in that browser, and go when the site's
data is cleared. **File → Download** saves a notebook as a file, and the
file browser uploads one.

An example notebook you changed and saved is your copy from then on: the
browser shows your copy, and a newer version that the site publishes stays
behind it, with nothing to say so. To have the new version, rename or
duplicate yours, then delete the example: the site's version comes back.
Deleting it without keeping a copy loses your changes.

## Sessions

Each notebook has a session of its own: an engine, with what the notebook
defined and loaded, in its directory. A page holds up to ten sessions; a
notebook beyond them says how to free a place. A session keeps running after
its notebook's tab is closed, until it is shut down in the **Running
Terminals and Kernels** panel.

**Restart** (in the **Kernel** menu) starts a new session, with nothing of
what the old one defined. `%%user` files stay in the file browser, and a
`%%user` cell loads its file again.

**Interrupt** stops the running query, and the session ends with it: the
next cell starts a new session, and the cell says so. An interrupt that
kept the session would keep, too, whatever the stopped query had done, such
as an `assert` whose `retract` never ran, with no way to see it; a new
session leaves nothing half done. An interrupt that comes before the cell's
query starts runs nothing, and the session stays.

**The tracer.** `jupyter:trace(Goal)` prints the ports of Goal as it runs:

```prolog
jupyter:trace(append([1], [2], L)).
```
```
   Call: (1) append([1], [2], _G122751)
   Exit: (1) append([1], [2], [1, 2])
L = [1, 2].
```

A notebook cannot answer at a breakpoint, so the tracer prints every port
and never stops at one. A cell that is `trace`, `trace/1`, `trace/2` or
`leash/1` says to call `jupyter:trace(Goal)`.

### If the runtime stops answering

In Safari, and in the other browsers built on WebKit, a thread of the
runtime that runs the engine now and then misses a call from the page; a
.NET app with nothing of Shumway in it does the same. The page notices a
call that is late and wakes the runtime's threads, so the cell answers a
few seconds later than it would have. Should the runtime still not answer,
or not start, the cell that was waiting says so, and so does every cell
after it in the page:

```
The page's runtime stopped answering, and only reloading the page starts it again. Save the notebook first: its files stay, and the sessions start again with nothing loaded.
```

Saving the notebook still works then, and reloading the page starts the
runtime again. A query may take as long as it needs: the notebook tells a
runtime that stopped from one that is busy.

## Compatibility

The kernel speaks the protocol of Hercutalk, Logtalk's Jupyter kernel, and
answers its magics and `jupyter` predicates as it does, which a corpus of
sessions recorded from it checks. Hercutalk's widgets are not supported
yet. Shumway adds `jupyter:display/2`, `jupyter:import_library/1` and the
constraints left on an answer's variables. A notebook saved by hhu-stups'
Prolog kernel opens with this one.

## A site of your own

[`jupyterlite/README.md`](../../jupyterlite/README.md) says how the site is
built and checked, and what its options set: where the runtime is, how many
sessions a page holds, and how long a step waits for the runtime before
asking it whether it still answers.
