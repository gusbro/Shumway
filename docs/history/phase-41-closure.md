# Phase 41 — conformance rounds, backtrackable wakeups, and the wasm spike

**Closed 2026-10-08**, tagged `phase-41` at `a34e86af` (main after PR #134,
2026-10-03): 257 commits and 57 merges from 88 pull requests (#5 to #134),
488 files, +47,198/−4,373 since `phase-40`. The `wasm-module-jit` branch
(PR #128) is phase 42. Much
of the work answered outside reports: issues filed by Ulrich Neumerkel, his
survey of other systems' trackers, Joachim Schimpf's ISO suite, and defects
found by real programs and the new quad transcripts. The version went from
1.0.0 back to 0.9.0 before any release (89dc6982), then to 0.9.1 (#28) and
0.9.2 (#61). New design records: ADR-048, ADR-049, ADR-050. Amended:
ADR-002, 004, 015, 016, 030, 031, 039, 041.

## Full Unicode (PR #6, ADR-048)

A Prolog character is a code point, above the BMP included. A per-atom flag
and a packed-string header bit mark text beyond the BMP, so BMP text keeps
its old paths at a measured cost of zero. `char_type/2` covers the full
Unicode tables, and `encoding/1` and UTF-16/32 streams arrived. Logtalk's
`unicode, full` suite passes 43/43.

## ISO conformance rounds

**Batteries.** Neumerkel's length/2 battery (#18) found a cyclic list hanging
`length/2` past every safe point, `length(2, 0)` raising instead of failing,
and oversized counts raising or being truncated. A runner for Schimpf's ISO
patterns (`tests/schimpf/`, #24) went from 840/945 to 926/945; the remaining
failures are divergences arbitrated against the ISO text and GNU, SWI and
Scryer. The Neumerkel harness was tightened: writer output must re-render
byte-identically (#63), a sentinel exposes a reader that waits (#64), and
ten rows the page opened without `<tr>` are read (#67).

**Numbers and text.** `number_chars/2` rejects trailing text by position
(#28). A digit separator may span layout and reach floats (#46). A float
literal beyond the range is `representation_error(max_float)`, or
`min_float` under a unary minus (#62, #70); `-0.0` denotes `0.0` (#63). The
BigInteger cap answers `resource_error(memory)`, and oversized shift counts
stopped giving wrong answers (#14). `read/1` raises at the character that
proves no token can follow (#64). An operator atom is not an operand in a
predicate indicator, `(-->)/2` (#67). The writer: `max_depth` counts an
operator's operand (#99), `"abc"||T` reads and writes `[a,b,c|T]` (#99), and
`write/1,2` and `print/1,2` apply numbervars (#134).

**The occurs_check flag** (#61) takes effect: set, unification uses the sound
unifier; off, it costs one byte test. Ten dead opcodes were removed.

**Errors.** Thirty throw sites built the formal as one atom, and `open/3` on
a directory let a host exception escape; both now raise balls a catcher can
match (#95). A cyclic culprit is the argument itself, and a ball keeps its
cycles (#77). Stream terms are judged by form: a query about a closed stream
fails, a partial stream term is an instantiation error (30e88f30, e24de0c3).
An instantiation error must be one a binding could make good (ac428e20).
`must_be/2` and `can_be/2` separate types from domains (#49). Every error a
builtin raises names the builtin in its context (#132).

**The bar.** #60 kept `|` out of the default table to hold syntax row #285;
#131 reversed that. `|` is `op(1105, xfy, '|')`, the declaration of the DCG
standard (TS 13211-3): `(a|b)` reads as `'|'(a,b)`, `current_op/3` reports
it, `op(0, xfy, '|')` removes it. Row #285 is knowingly lost.

## Backtrackable wakeups (ADR-049) and coroutining

**The interrupted-goal model.** A woken goal ran to its first solution in a
nested drain, so `freeze(X, member(Y,[1,2])), X = a, Y = 2` failed. Stage 1
(#55, Tier-0) saves the interrupted goal's registers, `B0`, `CP` and resume
point in an environment frame and continues at `'$wake_driver'/1`, which
runs the goals through `call/1` with real choice points. Stage 2 (#57) makes
the Tier-1 region boundaries the same interrupt. The hook runs committed,
its returned goals run free, and cut boundaries keep the once-drain
(ADR-049 §5). Inline arithmetic fires a pending wake before reading an
unbound operand (#59). Van Roy `--alloc` stayed identical (#55, #59, #73).
The promoted self-tail loop skipped the wake boundary, so a `freeze/2` could
go unhooked after promotion; pending wakes now carry their variable's home
(#73).

**freeze/2, when/2, dif/2.** A frozen goal can be any goal `call/1` takes
(#50), or a variable (#80). A cut in one frozen goal no longer prunes
another on the same variable (#95). `when/2` sees an aliasing (#6). A dif
whose unifier is one pair is stored as that pair, so a duplicate is dropped
and the older copy kept (#36, #39). Residuals show the current canonical
form, drop a dif another implies (#89), and include a constraint on an
unreachable variable (`freeze(_, false)`, #98).

**PR #134.** `frozen/2` binds its output last (issue #133) and returns goals
that re-create the constraints (the SICStus contract). Memory stays bounded
with attributed variables: extra-trail attribute entries take the HB check,
the attribute table drops records of variables bound untrailed, and a
CP-free guard commit compacts what it trailed (ADR-004, ADR-031); 30 rounds
of 100,000 steps went from 2,955 MB to 110 MB. Each restore drops the wakes
whose binding it undid (ADR-049 §8). ADR-030 elides a trailing cut only when
nothing before it binds, since a binding can wake a goal with alternatives.

## library(quads)

`library(quads)` (#74) consults a Neumerkel test transcript under
module-scoped operators (ADR-046) and runs it with `run_quads/0` or
`--quads` (#86, #96); `docs/guide/quads.md` documents it. Later rounds made
it check what a transcript claims: answers one by one, errors whole (#93,
#96), every description sentence, approximate floats (#103), `unexpected`,
`maybe`, `other_answer_sequence` and input sentinels (#113). Replacing every
goal with an undefined one failed all 169 quads (#96). Published
transcripts: length 37/37, phrase 58/58, variable_names 74/74 (#96).

## Libraries and builtins

**CLP(R)** (#6) gained `entailed/1`, `inf/2`, `sup/2`, `minimize/1`,
`maximize/1`, `dump/3` and `bb_inf/3,4` over a two-phase simplex, which
also replaced Fourier-Motzkin for satisfiability. **CLP(FD)** gained
`fd_labeling/2` and the heuristics GNU Prolog documents, checked against
GNU Prolog 1.5.0 (#82, #85); its entry points reject a variable where they
need a value (#80). A rational can be asserted, retracted and saved (#104,
ADR-039). A non-backtrackable global stores a heap-independent image (#105).
Consult refuses clauses for a control connective (#16) or, in the global
module, a registry builtin; bundle-booted engines keep builtins static
(#111). Also: `[F1, F2]` and `[user]` (#17), `'...'//0` and `seq//1` (#60),
`call_nth/2` (#129), `statistics/2` keys (#95), `max_procedure_arity`
(#107). In Trealla's library tree, 39 of 40 libraries load (#6).

## Engine robustness

**User data off the C# stack.** A .NET stack overflow cannot be caught, so
walks whose depth a program decides moved to explicit or mixed stacks:
eleven in #6 (consult and the `.shmo` codec among them), the reader on long
bodies and deep nesting (#110, `resource_error(term_nesting)` as the
limit), expansion (#95), dif (#103), renderers (#122), unification,
`copy_term/2` and the occurs-checked unifier (#123), at a cost within noise.
Cycles stop in `length/2` (#18, #110), `is_list/1` (#49), the text builtins
(#52), `findall/3` (#79) and `write_term/2` (#122). An ATTVAR cell names its
own slot, so it may be referenced but not copied; two sites copied one (#6,
#88).

**Stable addresses** (#116, ADR-015). Consulting two facts moved all 531
prelude predicates. The static region is laid out by ordinal: unchanged
code keeps its address, and a reconsult leaves the old version as a
bookkept dead region. A consult reports a bad clause and loads the rest
(#110). The heap GC roots only the callee's arguments where the callee is
known (#6, ADR-016), and scans every register under promoted IL (#126).

**Scans that grew with the program.** `bagof/3` and `setof/3` enumerate
witness groups lazily (#6: 18.1 s to 250 ms). Leaving a `catch/3` scanned
past every frame already left (#124: 100,000 deep, 12.0 s to 0.27 s). The
cut hook binary-searches its cleanup handlers (#125). ADR-041's selection
keys each entry once (#125: 20.01 s to 0.08 s at 32,000 clauses).
`retract/1` is linear in the predicate's size (#126: 13.4 s to 1.0 s).

## The top level

An answer is materialized only as deep as it is shown, and the projection
copy is skipped when nothing is attributed (46026745: 23.4 s to 2.5 s);
elision is one budget for the whole term (955cb157). Underscore-named
variables are not reported (#33); `a` and `f` page through answers (#34);
the printed error is the ball, and answers print quoted (#68); values that
print alike are compared as terms before chaining (#121); unbound variables
are named `_A`, `_B`, ... (#127).

## WebShumway

Isolation recovers after a deploy, a corrupted service-worker registration
is detected, and Cache Storage is best-effort (#9 to #13). Batched writes
and a 64 KB throttle let Stop work under an output flood (#31, #32).
Baked-bundle engines answer like live ones (#26, #27, #29). `restart.` gives
a clean engine (#17); reopening a changed file asks Replace or Keep mine
(#48). PR #6 brought offline use after one visit and the Trealla dialect.

## The WebAssembly Tier-1 spike (#117, ADR-050)

`Reflection.Emit` is unavailable in the browser, so WebShumway ran only an
interpreted Tier-0. #117 adds a second Tier-1 backend that compiles WAM to
WebAssembly modules working on the engine's own arrays through a mailbox.
ADR-050 records D1 to D7, among them: a raw `calli` through the calling
thread's function table; a module never calls managed code and returns a
verdict; Deopt hands the instruction to the interpreter, so an exclusion
costs speed, never correctness; the emitter is the `WebAssembly` NuGet
package (Apache-2.0, a new dependency), kept out of desktop builds.

On five small kernels (counter, nrev, tak, crypt, zebra) the tier ran 56x
to 292x faster than interpreted Tier-0 in the browser, a geometric mean near
100x (`docs/benchmarks/browser.md`; 92c7be53). Whole programs gain less,
because they leave the module more often (phase 42). A group module holds a whole program,
partitioned below the browser JIT's size cliff; the prelude is compiled at
build time. The tier stayed off by default (`-p:ShumwayWasmTier=true`).
`tests/Shumway.Tests.Wasm/` (183 tests) runs modules on the desktop against
Tier-0. The arc found #116, promotable query-stub helpers and an xfy-chain
regression.

## CI, tests and toolchain

`dotnet build` collects the toolchain into `dist/<configuration>/` (#6). CI
tests Release and builds the whole solution (bb2ff8a5). Test collections
run in parallel, so every gate run exercises several engines on concurrent
threads (#8). Persisted IL refers to builtins through name-relative patch
sites (#19). `:- native` invokers are shared per signature shape (#7).
Races fixed: lazy-debug arming (#20), builtin registration (#35), promotion
checks (#21); forensics shipped for the rest (#15, #35, #45).

## The gate at close

The phase closes at `a34e86af`, main after PR #134. The per-PR gates were
the project's usual ones. The last, PR #134's: Core 466, Interpreter 102,
Compiler 421, ISO conformance 517, Embedding 4,996 plus 3 Slow, Wasm 187
plus 1 Slow, net48 and `Shumway.Web` builds clean, zero warnings. Neumerkel
suites at #134: syntax every row but #285, number_chars 80, variable_names
63, length 37, phrase 58, cleanup 23, dif 26. Schimpf 926/945 (#24).
