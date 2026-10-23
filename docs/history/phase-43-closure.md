# Phase 43: term images, from clause bodies to the C# boundary

**Closed 2026-10-23**, tagged `phase-43`. 34 pull requests (#135 to #168) and
four commits on main, 2026-10-08 to 2026-10-23; 392 files, +22,511/−2,421
since `phase-42`. Version 0.9.3.

The phase opened with rounds on CLP(FD), attributed variables and the
prelude, and turned on Tier-1 for every engine. It then became the arc of
term images ([ADR-063](../architecture/adr/063-term-images.md)): a term the
engine copies goes as a block of cells relocated by one delta, not cell by
cell and not through a tree of C# objects. That now holds for a clause body,
a head argument, a solution of findall, a record and a value crossing to C#.

---

## Term images (ADR-063)

- **Body terms** (#152). A ground compound of a body argument with at least
  8 cells (`SHUMWAY_IMAGE_THRESHOLD`) is interned in a fourth literal pool and
  built by `put_blit`: reserve, copy, relocate. Tier-1 calls one helper; the
  wasm tier carries the image as a passive data segment. Blint 0.84 of the
  previous time; in Blint one predicate passed a 4,015-cell list 579 times per
  run.
- **Holes** (#155). An image may hold variables: an anonymous one is a REF to
  its own offset, a repeated one a REF to its first occurrence, a named one a
  hole filled from or pointed at its register. A census of 556 application
  modules found 37% to 45% of their construction cells in compounds with
  variables.
- **Heads** (#156, #160). `get_blit image, Ai, skip` copies an unbound head
  argument instead of building it; `skip_if_copied` guards each part the
  breadth-first order defers. Head arguments have a threshold of their own
  (`SHUMWAY_IMAGE_HEAD_THRESHOLD`), defaulting to the body's: under eight
  cells the copy costs more than the inlined stream.
- **The matcher** (#157). `unify_blit` unifies a term with an image in one
  pass, and the region planner sends to it the head streams that put a region
  past the JIT's cap on locals. A fix on main (2ca6ad02) makes every cell of a
  named variable a hole and has the matcher take the blocks in the stream's
  order.
- **Read-only head streams** (#158). After `get_blit` an argument is bound,
  so the IL tier emits its stream for read mode alone: bound reads twice as
  fast. The JIT's fifth limit, 2,000 basic blocks, joined the planner's.
- **The wasm tier under the occurs check** (#159) falls back to the bytecode at
  every entry, so its copies never meet the flag.
- **Literal pools are released with the code** (#161, ADR-015 addendum). An
  entry is pinned by static code or counted by the code units that name it,
  and swept when nothing does, so a program that asserts and retracts
  distinct literals stays bounded. Asserted packed text stays packed. A wasm
  module carries only the helpers it calls (2.01x to 1.48x of the batch's
  bytes for clpfd compiled a predicate at a time).
- **Solutions** (#165). findall/3,4, bagof/3, setof/3 and the
  non-backtrackable globals keep their solutions in one `SolutionBuffer` per
  call, floats and packed strings included, up to 40x faster where those
  used to go through an AST. Two bugs fixed: a caught throw sent later
  solutions into an abandoned buffer (`L = [a, 2]` for `[1, 2]`), and a term
  300,000 levels deep overflowed the .NET stack.
- **Records** (#168). recorded/3 keeps each record as a compacted image and
  prefilters candidates on its cells: writes 0.30 of main, write and read 0.27,
  pattern lookup 0.71, a 20-cell record in 640 bytes instead of 1.46 KB.

## Tier-1 IL

- **On by default** (#141). Every engine promotes a predicate after 32
  calls, as the top level did; a threshold of 0 keeps it on Tier-0. The
  prelude ships compiled with its IL (a prelude-heavy loop 2.54 s to 1.22 s, a
  new engine's first query 22 ms to 11 ms), and so do clpfd, clpr, coroutining
  and reif (#139). Baking found three bugs in how a bundle's code meets the
  runtime tier, fixed there; #141 fixed two wake points of an inlined guard
  chain (ADR-031, ADR-049).
- **Regions planned under the JIT's limits** (#151, #153). The emitter counts
  what the JIT counts, and a cost model plans a region before emission: no
  region of Blint falls to minimal optimization. `IlInlineTable`, generated
  from the JIT's own inlining events, gives the inlines each call expands
  into, and the planner keeps a region under the cap of 1,024 locals, read
  from the process as the runtime reads it. With the other changes of #151
  (callee markers as constants, the IL integer lane on the wasm lane's
  operators, a cached answer for a dynamic predicate with no snapshot), Blint runs in
  0.73 of its time at the phase's start.
- **Builtins called directly** (#166) when the implementation is a static
  method within reach, at run time and in bundles: builtin loops 0.87 to
  0.89, Blint 0.94 to 0.99.
- **A compile that someone waits for goes first** (#143): a first call behind
  3,000 queued background compiles, 29 s to 0.3 s.
- **Copies follow consults** (#146). Compiled code that copied a predicate,
  as a region member or an inlined callee, is evicted when a consult changes
  that predicate; a region member without code of its own runs its root's.

## The interpreter

- **findall loops up to 40% faster** (#167). `fail/0` backtracks at its call,
  a builtin's retry resumes without the dispatcher's lookup, and findall
  reuses its buffer within a query.
- **Every tier names the builtin an error came from**, from one registry id
  stored before the call. Compiled code used to leave the context unbound or
  name the last builtin the interpreter ran.

## The C# boundary

- **Typed interop without a Term tree** (#168). Generated `[PrologPredicate]`
  bridges and `SolveOnce<T>` read and write scalars, strings, lists and arrays
  of them and `[PrologTerm]` types straight off the heap, and fall back to
  the Term path for any other shape, so errors are unchanged. Against main, an
  int list of 100 read in 0.064 of the time and an atom list of 50 in 0.093.
- **The interop table re-measured.** With each mechanism's own empty call
  subtracted and two run lengths per figure, convenience costs about what
  P/Invoke marshalling into GNU Prolog costs (faster on atom lists and
  compounds), and the zero-copy path is 3 to 30x faster. The 3 to 180x
  published since phase 37 came from subtracting the typed bridge's empty call
  from raw calls; the same harness on the phase-37 build gives the same
  zero-copy costs. The empty foreign call went from 217 ns to 24 ns since
  phase 37.

## CLP(FD), reif, attributed variables

- **CLP(FD)** (#136, #139): `element/3`, `global_cardinality/2`, `circuit/1`,
  `tuples_in/2`, `cumulative/1,2`, `labeling/2` with `min/max(Expr)`, an
  `all_distinct/1` that prunes every value no assignment gives (20 queens,
  `abs` diagonals: 0.018 of the time), division, products bounded by
  division, reflection, and `type_error(integer, b)` for binding a constrained
  variable to a non-integer.
- **`@L`** (#148): the length of a list as a CLP(FD) variable, propagated both
  ways (`docs/design/clpfd-list-length.md`).
- **library(reif)** (#137), written from its documentation, and clpfd's six
  comparisons as reified conditions. An engine library links and loads in
  the middle of a query.
- **Attributed variables.** A cell stored outside its home is a REF to it
  (#135): `msort/2` of a list of constrained variables made unconstrained
  copies. A list builtin that grows an attributed tail arms its choice point
  first (#149) and continues from the wake batch (#150,
  `Activation.QueueWakeGoal`), so `@A #=< 6, nth0(X, A, a)` ends.

## Programs, modules and the debugger

- **Reloading a file** keeps what other files gave its multifile predicates;
  a dynamic call after a nested query selects from its own program; codes
  lists follow Technical Corrigendum 2 (#162).
- **A query keeps its own program** (#163, #164): its address map, debugger
  tables and breakpoints, also when a file it consults runs directives, and
  code linked into it while it runs stops at breakpoints and is named in
  stack traces.
- **Arity sources** read again (#154): bare operator atoms as operands and
  quoted C strings in native blocks, over a corpus of 556 modules.
- **The prelude** drops `sumlist/2` (now in the SWI shim), `if/3` and
  `atomic_concat/3` (#140), and carries library(error)'s throwers (#147).

## WebShumway

- An engine library in an imported collection is shown as provided by
  Shumway (#144). Library sources stay open, and each step stores only what
  it changed (#145).
- The editor takes Tab; stored libraries come back in the background (the page
  held up 2.7 s, now 0.2 to 0.4 s); a library's bundle holds its own code
  (a 46-library collection stored in 38.7 MB instead of 139 MB, compiled in
  60 to 65 s instead of 162 to 175 s); `FunctorTable.Intern` no longer leaves
  holes under concurrent interning; the wasm tier unifies a packed string
  with a list (#147).

## Tooling and the gate

- The wasm suite runs in four processes: 860 s to 237 s (#138).
- Tests check the promotion wait they rely on, with 60 s (#142).
- `IlInlineTable`'s fallback guess passes the trimmer (5cbfd0ef).

## Deferred

- From ADR-063's "Not in this decision": a matcher and the read-only stream
  in the wasm module, big integers and rationals in images, and images in
  clauses asserted at run time.
- The ball of `throw/1` still goes through a Term tree: it is paid once per
  exception and crosses C# frames.
- The IL meta-call compares control-construct names as strings, where the
  interpreter compares atom ids; the expected gain is nil.
- A clpz-heavy process spends about 55 s of JIT CPU per engine, noted for a
  later look.
- A pool of solution buffers shared across queries.
- Per-register lattice analysis and pre-resolved heads in guards were
  measured (a census and a spike) and closed: within the noise.
- The default form of Tier-1 (ADR-061, from phase 42) is still undecided.

---

## The gate at close

`tests/gate.ps1 -Level full`: Core 529, Interpreter 102, Compiler 463 (+2
skipped), ISO conformance 518, Embedding 6,226 (+3 skipped), Wasm 806 (+118
skipped); the net48 and web builds with zero warnings. The Release JIT
classes (`IlInlineTableTests`, `RegionJitLimitTests`, `IlWakeCheckTests`,
`IlHelperTableTests`) green. Embedding on net48 in Release, 6,062 in every
lane. CI green on all three lanes (net10, net48-x64, net48-x86).
