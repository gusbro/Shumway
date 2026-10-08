# ADR-062: An Own IL Emitter in Place of Sigil

## Status

Accepted (2026-10-02). Replaces Sigil as the IL emitter of Tier-1 (ADR-011),
for runtime promotion, continuation methods (ADR-061) and persisted bundles.

## Context

Tier-1 emitted its IL through Sigil, a library that wraps `ILGenerator` with a
type-checked evaluation stack. Promotion runs while a program does (the REPL
promotes a predicate from Tier-0 as it gets hot), so its cost is time taken
from the program and latency before the compiled code helps.

Linking Blint (a 2,570-line program) with precompiled IL took 9.3 to 9.7 s
against 0.9 to 2 s without. A CPU profile of the link: 71% of the samples
passed through Sigil and 4.5% through `System.Reflection.Emit`; 58% were the
thread held for garbage collection. Sigil allocates per instruction (a
`StackTransition`, its type lists, LINQ-style copies) to track the type of
every stack slot, and when it seals a method it scans for a call followed by
`ret` and inserts the `tail.` prefix by copying its instruction buffer, once
per site: quadratic in the method's size. The size caps on promotion
(`IlPromotionStore.MaxIlPromotionBytecodeBytes`) and the stack overflow of its
return tracer on very large methods came from the same place.

Tier-1 uses a small part of Sigil: about 46 instruction methods, labels,
locals, the two factories (a `DynamicMethod`, a method of a `TypeBuilder`) and
the text of a method's instructions for `SHUMWAY_IL_DUMP`. It already turns
Sigil's optimizations off (`OptimizationOptions.None`, so branches keep their
long form) and builds methods with verification off.

## Decision

An emitter of Tier-1's own (`IlEmit`, `IlLabel`, `IlLocal` in
`Shumway.Compiler.Il`) over `ILGenerator`, with the methods Tier-1 calls and
their names, so that the emitters change only their types. It writes each
instruction to the `ILGenerator` as it comes, keeping at most one call
pending, and records nothing else per instruction.

It reproduces what Sigil did to the code, so that the emitted methods are the
same instruction for instruction:

- The `tail.` prefix on a `call` or `callvirt` that the method's `ret`
  follows with no label between them, when the callee returns a type the
  method's return type accepts (`bool`, `byte`, `short` and `char` count as
  `int`; never `void`), takes no pointer or by-reference parameter and is not
  an instance method of a value type. The pending call is written when the
  next instruction is known.
- The short forms Sigil chose: `ldarg.0`..`ldarg.3` and `.s`, the same for
  `ldloc`, `stloc` and `ldloca`, `ldc.i4.m1`..`ldc.i4.8` and `ldc.i4.s`.
  Branches keep their long form, as under `OptimizationOptions.None`.
- The element and conversion instructions by type (`ldelem.i4`, `ldelem` with
  a token for a struct, `conv.i8`, and so on).

It keeps the checks that caught emitter errors early, all linear: a label
used and never marked, a label marked twice, and an instruction after an
unconditional transfer with no label marked before it. A failed check throws
`IlEmitException`, which carries the instructions so far when they are
recorded; the linker skips the predicate, as it did on Sigil's exceptions. The
type of each stack slot is no longer checked; an ill-typed method now fails
when the JIT compiles it (`InvalidProgramException`) instead of when it is
built. The text for `SHUMWAY_IL_DUMP` is recorded only while a dump is on.

## Consequences

- Sigil leaves the dependencies (MS-PL; THIRD-PARTY-NOTICES updated), and
  with it the browser build's suppression of the trimmer's IL2104 for it.
- The reflection into Sigil's internals (its `DynamicMethod` for
  `InitLocals`, its unmarked labels) becomes plain API.
- `IlPredicateCompiler.DoVerify`, which turned Sigil's verifier on, is gone.
- The size caps that guarded against Sigil's cost can be measured again. What
  a promotion costs is now mostly the JIT's (see Validation).

## Validation

The same compiler was built twice, once over `IlEmit` and once over an adapter
that gives Sigil `IlEmit`'s API.

- **Same code.** Blint and the ten benchmark programs linked with
  precompiled IL by both builds: the persisted assemblies, disassembled, are
  identical (600,339 lines, 560 `tail.` prefixes on each side). The
  `SHUMWAY_IL_DUMP` text of 575 methods (the linked ones and the REPL's
  continuation methods) is identical as well.
- **The link.** `shumway-link -i` of Blint, ABBA, two rounds: 2.0 to 2.4 s
  against 7.1 to 9.2 s with Sigil. What remains of the emission is
  `System.Reflection.Emit` itself, 15% of the link's samples, most of it the
  persisted module's token lookup per call.
- **A promotion.** The process's CPU time for Blint linting itself twice in
  the REPL, everything promoted (`jit_compile(all)`), ABBA: 14.5 to 16.9 s
  against 36.0 to 47.2 s with Sigil; with continuation methods 21.3 to 24.0 s
  against 39.5 to 40.7 s. Of the 13.6 s of one run traced, 5.7 s were the
  JIT compiling the 119 generated methods (the largest regions 150 to 220 ms
  each) and 3.5 s the runtime's tiering of Shumway's own code.
