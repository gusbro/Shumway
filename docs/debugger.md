# Debugging Prolog in Visual Studio

Shumway ships a source-level debugger for Visual Studio 2026: breakpoints in `.pl`
files, a call stack of your own predicates, the variables of each frame, and stepping
through the four Prolog ports. When your program calls out to C# or to native C, those
frames appear in the *same* stack, above the Prolog frames that called them.

Design and rationale: [ADR-035](architecture/adr/035-source-level-debugger.md).

---

## Installing

Build and install the extension (Windows, Visual Studio 2026 with the "Visual Studio
extension development" workload):

```
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" ^
    vs\Shumway.Debugger.sln /p:Configuration=Release
```

Then install it — **with Visual Studio closed** — using the script, which uninstalls the old
one first:

```
powershell -ExecutionPolicy Bypass -File vs\install-vsix.ps1 -Configuration Release
```

(Build the *Release* VSIX with `/p:DeployExtension=false`. Without it the build also deploys
into the experimental hive that the smoke scripts use, on top of the Debug copy and with the
same identity — and Visual Studio resolves that by loading *neither*, with a "could not load
the extension" dialog and no clue as to why.)

**Do not double-click the .vsix to upgrade.** The installer compares *versions*, not
contents: rebuild the extension without bumping `Version` in `source.extension.vsixmanifest`
and it answers "this extension is already installed to all applicable products" and leaves
the old one in place. That is the single most common way to end up debugging with a mismatched
pair.

**The extension and the engine are a pair, and they must be rebuilt together.** They talk
over a shared memory buffer whose layout both sides compile in, so an extension that predates
an engine change cannot read it — no Prolog call stack, no breakpoints arming, just the
engine's own C#. The extension says so in the call stack when it can ("the engine speaks
channel format vN, this extension speaks vM — rebuild and reinstall the VSIX"). If you are
looking at C# where you expected Prolog, reinstall the VSIX first and ask questions after.

The `vs\` solution builds with **desktop MSBuild only** — it references the VS SDK, which
`dotnet build` cannot resolve. It is deliberately not part of `Shumway.slnx`: nothing in
the engine depends on it, and a Linux build never sees it.

Then tell the extension where the engine is, in **Tools > Options > Shumway > Prolog
Debugger**:

| Setting | Meaning |
|---|---|
| Path to shumway.exe | The engine that runs your file. If empty, taken from `SHUMWAY_EXE`, then from `PATH`. |
| Additional arguments | Passed to the engine before the file. If empty, taken from `SHUMWAY_ARGS`. |

## Debugging a file

Open a `.pl` file, put a breakpoint on a goal (F9), and pick **Debug Prolog File** from
the editor's context menu. That launches `shumway.exe` on the file under the debugger and
stops at your breakpoint.

Everything else is ordinary Visual Studio: F5 continues, F10/F11 step, the Call Stack and
Locals windows work, and double-clicking a frame navigates to its source.

The command is on the editor context menu and appears only for `.pl` files.

## Attaching to an engine you started yourself

You can also debug a `shumway.exe` (or a host application embedding Shumway) that is
already running. Two things to get right:

**Start it with `--debug`.**

```
shumway.exe --debug my-program.pl
```

Debuggability is a property of the *code*, decided when it is compiled — so the flag is
read before the first file is consulted. Without it the program compiles the way a release
build does: there are no stop sites, no breakpoint can bind, and there are no Prolog frames
to show. Attaching to an engine started without `--debug` looks like a debugger that does
nothing.

**Attach as managed code.** Debug > Attach to Process, pick the process, and make sure
"Attach to:" says **Managed (.NET Core)** (press *Select...* if it does not).

You can attach to an engine that is doing nothing — sitting at the prompt, waiting for a
query — and set breakpoints on predicates that have never run. They bind, and they are hit
when you finally run the goal.

There is no "Shumway" entry in that list, and there is not meant to be. Shumway does not
implement a debug engine: it *extends* the managed one. The Concord components layer onto
the CLR debug session — which is exactly what makes one mixed Prolog + C# + native stack
possible, and why the launch command above hands the process to the ordinary CoreCLR
engine rather than to an engine of ours.

## Debugging an engine embedded in your own application

The REPL is only the first caller of one embedding method. When Shumway is one part of a
larger .NET program — a rules engine, a grammar, a bit of symbolic reasoning inside a system
that is mostly C# — you turn on debugging on the engine you created, in your own process:

```csharp
var engine = new PrologEngine();
using var _ = engine.EnableDebugging();     // BEFORE you consult anything
engine.ConsultFile("rules.pl");
// ... your application runs; attach Visual Studio to THIS process (Managed .NET Core)
```

`EnableDebugging()` is exactly what `--debug` does for the REPL: it turns on debug codegen
(named variables, a frame per goal, source positions), turns last-call optimisation off so
the stack is whole, and opens the channel a debugger attaches to. Call it **before you
consult the code you want to debug** — debuggability is decided when the code is compiled,
and code compiled before the call has already thrown away what the debugger shows. Keep the
returned session alive for as long as you want to be debuggable, and dispose it to stop.

There is one debugger per process, so `EnableDebugging` throws if a session is already open.
`DebugOptions` covers the rest: `LastCallOptimisation` (default off), `WaitForAttach` (block
until a debugger is attached and ready — for a process launched *in order* to be debugged;
the REPL's `--debug-wait`), and `SourceFiles` (announce the files you are about to consult,
so a breakpoint drawn before the process stops anywhere still binds).

**Loading a `.shum` bundle.** A bundle — a `.shum` file, or one embedded in a .NET DLL — is
debuggable too, if it was built debug and still carries its module sources. Enable debugging
before you load it:

```csharp
var engine = new PrologEngine();
using var _ = engine.EnableDebugging();
engine.LoadBundle("app.shum");
```

For each module the bundle still has the source of, the debugger shows the code **from that
embedded source** — the exact text the module was compiled from, written out to a file
Visual Studio opens, so a breakpoint resolves to what is really in the bundle and not to a
`.pl` on your disk that may have drifted. For a module whose source was stripped at build
time, resolution falls back to the ordinary rule: the module's name is a `.pl` file, found
on disk by that name. (A release-compiled bundle has no debug information at all and is not
debuggable, with or without its source — build it debug.)

**A stand-alone `--exe`.** `shumway-link --exe` bakes a program into a single executable
that runs one goal at startup. Pass `--debug` and that executable is built debuggable: its
modules compile debuggable and it materialises their embedded source at launch, so a
debugger attached to the running process sets breakpoints and steps exactly as the REPL
does — the Prolog is just one part of a shipped binary.

```
shumway-compile --debug greet.pl -o greet.shmo
shumway-link greet.shmo --goal main --exe greet --debug
./greet            # runs normally (silent; SHUMWAY_DEBUG_DIAG=1 prints
                   #   "shumway: debug mode active.")
                   # attach Visual Studio to the process at any time to debug it
```

The executable runs normally and can be attached to whenever you like. Add `--debug-wait`
instead of `--debug` to make it **block at startup** until a debugger has attached and armed
its breakpoints, then **stop at the entry point** — the first goal of the program — so you
land there ready to step, without setting a breakpoint first. (`--debug` alone never stops on
its own; you attach and set breakpoints while it runs.) Because the
executable shows the source it carries, `--debug` requires the bundle to carry it: compile
the inputs with `shumway-compile --debug` (release `.shmo` objects are source-stripped) and
link without `--strip`. The linker checks this before building and fails with a clear message
otherwise, rather than ship an undebuggable "debug" exe.

## What you see

**The call stack** is your predicates — the ones you wrote, with the names you wrote.
Each frame shows the *call it is*:

```
inventory:total([item(book, 25) | []], 10, _G5)!2
inventory:total([item(pen, 10) | [item(book, 25)]], 0, _G5)!2
inventory:main(_G5)!1
?- main(T)
```

`inventory` is the module — the file's base name, without the `.pl`. The arguments are the
head's, with their **current values**: they instantiate as the clause runs, so stepping is
visible in the stack itself. An argument the head wrote as `_` shows as `_`; one nobody has
bound yet shows as `_Gn` — and a variable shared between frames (the `Total` threaded through
the recursion above) shows the same `_Gn` in every one of them, because it *is* the same
variable. `!2` says the predicate's **second clause** is the one being evaluated, counting
from 1 in source order. Each argument is cut to 64 characters for the stack line; the full
value (up to 512) is in Locals. A frame whose code was not compiled debuggable falls back to
`pred/arity`. Module-local predicates are mangled internally (`interop$step/2`); the stack
shows `step`.

The stack is recomposed from the engine's own environment chain, not from the C# frames of
the interpreter, so it is a *Prolog* stack, one frame per active predicate. Bindings shared
between frames are rendered once and serialized once — a deep recursion sharing a big term
costs the term once, not once per frame.

**Last-call optimisation is off by default under the debugger**, which is why a
tail-recursive predicate shows the frames that led to it instead of a single frame. Turn
it back on (to see the stack the release build would really have) with the prolog flag:

```prolog
?- set_prolog_flag(debug_lco, on).
```

**Locals** show each variable of the selected frame under the name it has in the source,
rendered as a term. An unbound variable shows as `_G3`; a variable whose goal has not run yet
shows as `_`, because it has no value yet. A long term is cut off after 512 characters — the
Locals window shows one line, and a variable holding a parsed file is not readable there
anyway.

**Attributed variables show their constraints.** A CLP(FD)/CLP(R)/`dif`/`freeze` variable
is, as a term, just an unbound `_G12` — its constraints live in attributes, not in the
term. At a stop the engine projects them (the same `attribute_goals` projection the REPL
uses to print `A in 6..9.`) and appends one read-only row per constrained variable at the
end of Locals:

```
X = _G12
Y = _G15
X ⟨constraints⟩ = X in 1..6, X#<Y
Y ⟨constraints⟩ = Y in 3..7
```

A constraint mentioning two variables (`X#<Y`) is shown once, under the first of them.
The rows are read-only — to *narrow* a variable, post a constraint from the Immediate
window (below). Works for every attribute library that defines a projection hook
(`attribute_goals/4` or the Scryer/SWI `attribute_goals//1`), so clpfd, clpr,
coroutining (`dif`/`freeze`/`when`) and dialect libraries like clpz all display. A hook
that fails or hangs costs the constraints display, never the stop.

**A deep stack shows both ends.** Stopped two thousand frames into a recursion you get the
innermost eighty (where the machine is), a line saying `... 1,900 frames omitted ...`, and the
outermost twenty (how the program got in). Nobody reads two thousand frames of the same clause,
and rendering them all is the expensive part of a stop.

**The files are the engine's, not the editor's.** Every `.pl` the engine consults — on the
command line, from a `:- consult`, or typed at the top level — is announced to the debugger as
it happens, so its frames are navigable and its breakpoints bind the first time you stop. You
never have to open a file by hand to make the debugger notice it.

**The bottom frame is your query** — `?- top(A)`, the goal you typed, with its variables.
It is not a predicate (it has no `Name/Arity`), and double-clicking it opens nothing: you
did not write it in a file. It is there because you are standing in it.

**Stepping stays in your program.** The prelude, the libraries and the top level's own
plumbing are `:- disable_debug`: they run, they just never stop you. So F10 over a goal
that calls `member/2` steps *over* it, and never leaves you standing in `copy_term/3`
wondering how you got there.

**A step lands on a goal** — the next thing your program is about to *do*, at the line you
wrote it on — and only on a goal: an exit port never stops a step. An exit fires with the
machine still standing in the clause that just finished, so stopping there leaves the caret
on a line that already ran — the last line of some other clause (the first report), or the
same line you were on when you pressed F10 on a clause's last goal (the second). Step over
the last goal, or Step Out, and you land on the next goal an enclosing clause runs, however
many clause-ends unwind in between. A *fail* does stop you — a goal running out of
solutions is the thing that just happened, and there is no next goal to show instead.
Builtins are goals like any other: `X is N - 1` and `writeln(X)` are stops, and there is
nothing inside them to step into. So are the goals that compile inline — `!`, `is`, `=` and
the comparisons: a step lands on the `!` *before* it commits and on the guard *before* it
fails, which is when you want to be looking at the variables.

**Stepping past the end of the query** is not a stop: the query hands back its answer and
stands still, no port is coming, and the debugger drops the step and lets the program run
on. Type `;` at the prompt for the next solution — your breakpoints are still armed.

## Running goals: the Immediate window

Stopped anywhere, you can **run a goal**. Type it into the Immediate window:

```prolog
double(N, R)
R = 42
```

The goal runs *in the engine you are debugging*, in a fresh activation, with the variables
of the **selected frame** substituted by their current values — `N` above is not a name the
goal happened to share, it is the `N` you can see in Locals, and it went in as `21`. The
answer is the first solution's bindings; a goal with nothing to bind answers `true` or
`false`. Select a different frame and the same goal means something different, because the
variables do.

An **attributed** frame variable goes in *with its constraints*: the engine transplants
its attribute graph onto the evaluation's copy, so `get_attr(X, clpfd, A)`,
`copy_term(X, C, G)` and `frozen(Z, G)` answer the real thing, and posting a new
constraint (`X #< 5`) narrows the copy and propagates. What you post lives in the
evaluation — the suspended program's own variable is never touched (binding an
attributed variable into the frame stays refused: its unification hooks cannot run in a
suspended machine).

It is a real query against the live database, so **side effects are real**:

```prolog
assertz(seen(N))
true
seen(Q)
Q = 21
```

That clause is now in the database. It is still there when you press F5 and the program runs
on — which is the point: you can plant a fact, retract one, or run a diagnostic predicate,
and then watch the program take the path that follows from it. It has the semantics of a
query started at that moment, because that is what it is.

**A breakpoint inside the goal stops you inside the goal.** If what you run reaches a
breakpoint, the debugger stops there — a *nested* break, on top of the one you were already
in, exactly as a C# call from the Immediate window behaves. The call stack shows the whole
thing at once:

```
inventory:step(21)!1          <- where the evaluated goal is now
inventory:run(21)!1
inventory:go!1
[Immediate: go]               <- the goal you typed
inventory:step(21)!1          <- where you were when you typed it
inventory:run(21)!1
inventory:go!1
?- go
```

The `[Immediate: ...]` frame is the boundary: above it is the goal you asked for, below it is
the program you interrupted. Step around up there as you would anywhere. F5 releases the
nested stop, the goal runs on to its answer, and you are back where you started.

A goal that never finishes is not a hang: the evaluation gives up after **15 seconds** and
says so, and the program is where it was. If you would rather a goal never stop — a
diagnostic that walks over your own breakpoints — set `SHUMWAY_DEBUG_EVAL_QUIET=1` in the
debuggee's environment and breakpoints reached *during an evaluation* are ignored.

Typing a bare variable name still just shows its value, and costs nothing: that answer was
already in the snapshot, and no code runs to produce it.

## Interop: one stack across three languages

A program that calls a C# foreign predicate — which may itself P/Invoke into C — debugs as
one program. Stop inside the C# and the Call Stack shows:

```
ForeignLib.Scaling.Scale                 <- your C#
ForeignLib.Scaling._Scale_PrologBridge   <- the generated bridge
step/2                                   <- the Prolog that called it
run/2
main/0
?- main
```

The Prolog half of that stack is real, not remembered. While your C# runs, the engine
thread is frozen inside the call and can be asked nothing — so it writes the stack down on
its way *into* every foreign predicate, and marks it as no longer current on the way out.
Stop anywhere else in C# (a thread of your own, a callback the engine did not make) and
you get no Prolog frames at all, which is the honest answer: the engine is not standing
there. The cost is one stack walk per foreign call, paid only while a debugger is attached.

Stepping in the C# is the C# debugger's — F10/F11 there behave exactly as they do in any
.NET program. Step past the end of the foreign predicate and you are back in Prolog, at
ports.

To make it work, the engine has to be told where the interop assemblies are, exactly as it
would on the command line. Put them in **Additional arguments**:

```
--foreign-dll C:\path\to\MyForeigns.dll --native-dll C:\path\to\native.dll
```

**Native C frames** (the `native.dll` at the bottom) appear only if you turn on native
debugging: Debug > Options > **Enable native code debugging**, or `"nativeDebugging":
true` in a launch profile. Without it the native call still runs — you just do not see its
frames.

The worked example lives in `vs\smoke\interop\` (a Prolog module → a `[PrologPredicate]`
C# method → a `DllImport`'d C function), together with `vs\smoke\run-interop-smoke.ps1`,
which drives Visual Studio through exactly this scenario.

## Opting a module out: `:- disable_debug.`

A module marked

```prolog
:- disable_debug.
```

keeps full release compilation under the debugger (Tier-1 IL, regions, LCO, CP-free
guards) and appears in the call stack as a single opaque frame — the Prolog equivalent of
"Just My Code". Breakpoints inside it do not bind, and stepping into it behaves as
stepping over.

The prelude and the bundled libraries are implicitly `disable_debug`: you step through
your program, not through `maplist/3`.

## Stopping from the program: `debugger_break/0`

The shortest path from a program to a stopped debugger. Put it in the clause you care
about:

```prolog
suspicious(X, Y) :-
    debugger_break,
    complicated(X, Y).
```

Run under `--debug`, attach, and the next time that clause is reached you are standing in
it, with its stack and its variables. It needs no breakpoint, no symbols and no binding —
it asks the runtime to break, and the debugger answers.

**With no debugger attached it does nothing and succeeds**, so you can leave it in the
program. (It does still need the code to have been compiled for debugging — that is what
gives the stop a stack to show.)

## Pausing

Break All does not freeze the engine where it stands. It asks it to stop at the **next
goal**, and stops there — which is microseconds away in a running program, and is the first
point at which a Prolog call stack exists at all. A machine caught at an arbitrary
instruction is halfway through a unification or three levels inside a builtin: it has no
stack to show, and the last one it had is not where it is.

So the stack you get from a pause is a real one, at a real point in your program, and you
can step from it.

If Prolog is not running when you pause — the engine is blocked in a read, the query is
over, the thread is deep in your own C# — no goal is coming, and Visual Studio freezes the
process as it normally would. You then see the C# stack, which in that case is the truth.
No Prolog frames are invented for it.

## What the debugger costs

Nothing, when it is not running. The port hooks are behind a per-engine flag; a release
build has no debug metadata and never tests it.

Under `--debug` the engine runs Tier-0 (the bytecode interpreter) for debuggable modules,
keeps every environment frame (LCO off), and passes a port at every goal — measured at
roughly 1.5–2× the release time on a real program. That is the price of being able to stop
between two goals, and it is bounded: nothing is rendered, walked, or captured unless
somebody actually stops. If a run under the debugger is *dramatically* slower than that,
it is a bug — say so.

## Known limits

- **Detach is not supported.** Stop the process instead.
- A breakpoint on a line with no callable goal binds to the next goal that has one; if
  there is none in the clause, it silently does not bind.
- Locals are flat: no compound expansion in the tree. (Editing a binding works — see the
  Watch/Locals destructive edit; an attributed variable's `⟨constraints⟩` rows are
  read-only.)
