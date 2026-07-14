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

## What you see

**The call stack** is your predicates — the ones you wrote, with the names you wrote.
Module-local predicates are mangled internally (`interop$step/2`); the stack shows
`step/2`.

The stack is recomposed from the engine's own environment chain, not from the C# frames of
the interpreter, so it is a *Prolog* stack, one frame per active predicate.

**Last-call optimisation is off by default under the debugger**, which is why a
tail-recursive predicate shows the frames that led to it instead of a single frame. Turn
it back on (to see the stack the release build would really have) with the prolog flag:

```prolog
?- set_prolog_flag(debug_lco, on).
```

**Locals** show each variable of the selected frame under the name it has in the source,
rendered as a term. An unbound variable shows as `_G3`.

**The bottom frame is your query** — `?- top(A)`, the goal you typed, with its variables.
It is not a predicate (it has no `Name/Arity`), and double-clicking it opens nothing: you
did not write it in a file. It is there because you are standing in it.

**Stepping stays in your program.** The prelude, the libraries and the top level's own
plumbing are `:- disable_debug`: they run, they just never stop you. So F10 over a goal
that calls `member/2` steps *over* it, and never leaves you standing in `copy_term/3`
wondering how you got there.

**A step lands on a goal** — the next thing your program is about to *do*, at the line you
wrote it on. Not on a goal's exit: an exit fires with the machine standing inside the
predicate that just succeeded, so stopping there would jump the caret to the last line of
some other clause, which is what "F10 stops at the end of a clause I was not looking at"
was. The exit of an *enclosing* clause does stop you, because that clause is one you were
stepping through. Builtins are goals like any other: `X is N - 1` and `writeln(X)` are
stops, and there is nothing inside them to step into.

**Stepping past the end of the query** is not a stop: the query hands back its answer and
stands still, no port is coming, and the debugger drops the step and lets the program run
on. Type `;` at the prompt for the next solution — your breakpoints are still armed.

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
- Locals are read-only and flat: no compound expansion in the tree, no editing a binding.
- A watch names a variable of the selected clause. It does not evaluate a goal — running
  Prolog inside a stopped engine is a different thing, and one with side effects.
- Conditional breakpoints are not implemented.
