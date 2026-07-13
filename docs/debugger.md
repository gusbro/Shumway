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

Double-click the resulting `vs\Shumway.Debugger.Vsix\bin\Release\Shumway.Debugger.vsix`.

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

## Interop: one stack across three languages

A program that calls a C# foreign predicate — which may itself P/Invoke into C — debugs as
one program. Stop inside the C# and the Call Stack shows:

```
ForeignLib.Scaling.Scale                 <- your C#
ForeignLib.Scaling._Scale_PrologBridge   <- the generated bridge
step/2                                   <- the Prolog that called it
run/2
main/0
```

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

## What the debugger costs

Nothing, when it is not running. The port hooks are behind a per-engine flag; a release
build has no debug metadata and never tests it. Under a session the engine runs Tier-0
(the bytecode interpreter) for debuggable modules, which is slower than the IL it would
otherwise promote to — that is the price of being able to stop between two goals.

## Known limits

- **Detach is not supported.** Stop the process instead.
- A breakpoint on a line with no callable goal binds to the next goal that has one; if
  there is none in the clause, it silently does not bind.
- Locals are read-only and flat: no compound expansion in the tree, no editing a binding.
- A watch names a variable of the selected clause. It does not evaluate a goal — running
  Prolog inside a stopped engine is a different thing, and one with side effects.
- Conditional breakpoints are not implemented.
