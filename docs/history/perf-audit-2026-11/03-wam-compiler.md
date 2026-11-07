# Shumway performance review — 03: Prolog → WAM compiler and code generation

Scope: `src/Shumway.Compiler/` (clause pipeline, variable classification, instruction
selection, indexing, det analysis, DCG/meta transforms) and the WAM instruction set.
Static analysis only (no `dotnet` in the container); every claim below was checked by
reading the code at the cited `file:line` (HEAD as of 2026-11-04). Items are ranked by
expected value for real programs, honest about the tier they help (Tier-1 IL is on by
default, so a Tier-0-only win matters mostly for cold code, Native AOT and the wasm
fallback).

What was read first, so nothing below re-proposes closed work: the 2026-06 efficiency
audit, `wam-vs-gprolog-blint.md` / `-bench.md`, `mode-inference-design.md`, the whole
phase-33 backlog (W1–W9, L-, I-, B-series), ADR-006/007/012/013/017–021/025–034/037/
041/049/063, the phase-2/3/25–43 closures, and `git log` greps per item (listed under
each "novelty check"). ADR-021's rejection reasons (wakeups and head-time hooks clobber
the X file; choice points save only argument registers) were read and are respected:
**nothing below moves a variable out of a Y slot.**

---

## 1. Guard-driven type indexing: use leading `var/atom/compound/…(A_k)` tests as bucket membership

**Locations.** `src/Shumway.Compiler/Wam/PredicateCompiler.cs:510-576`
(`ArgKind`, `ClassifyArg`, `ClassifyTerm` — a variable head argument is `ArgKind.Var`,
unconditionally), `:907-941` (bucket build: `VarIdxs` are merged into *every* typed
bucket by `MergeWithVar`), `:1027-1035` and `:1047-1064` (the var-only chain is the
miss target for every empty category), `:1288-1294` (the full and var-only chains).
Interpreter routing that the proposal relies on:
`src/Shumway.Interpreter/BytecodeInterpreter.cs:1585-1606` (`switch_on_term`: Ref/AttVar
and every other-tag → `varAddr`; Atom/Int/Float → `constAddr`; Lis / non-empty Pstr →
`listAddr`; Str → `structAddr`), `:2508-2523` (`call_builtin` flushes pending wakeups
before a non-unification builtin runs).

**Current behaviour.** The pervasive term-walking shape

```prolog
simplify(X, X)   :- var(X), !.
simplify(X, X)   :- atomic(X), !.
simplify(X+Y, Z) :- !, simplify(X, X1), simplify(Y, Y1), add(X1, Y1, Z).
simplify(T, T).
```

compiles (clause 3 makes A1 indexable) to a `switch_on_term` whose Const and List
labels go to the var-only chain `{c0, c1, c3}` and whose `+/2` struct bucket is
`{c0, c1, c2, c3}`. A call `simplify(foo, R)` therefore runs `try c0` (CP push, 2
registers saved) → head → `call_builtin var/1` → fail → backtrack → `retry c1` → ok.
`simplify(A+B, R)` wastes two full clause tries (var, atomic) before reaching c2. For a
chain with k guarded clauses, every call pays up to k−1 CP pushes + builtin dispatches
+ backtracks in Tier-0; in Tier-1 ADR-031 removes the CP for `Guard, !` chain clauses
but still executes each guard's builtin call and the restore stub (ADR-031
"deterministic non-meta CallBuiltin (type tests, == — the old case E)").

**Proposal (no new opcode, no table-format change — bucket membership only).** In
`ClassifyArg`, when head arg k is a variable `V` that occurs exactly once in the head
and the clause body's first goal is a type test on `V`, classify the clause with a
*type mask* instead of `Var`, and let the bucket merge honour it:

| leading guard on `V` | keep in Var chain? | keep in Const buckets/miss? | List | Struct |
|---|---|---|---|---|
| `var(V)` | yes | **no** | **no** | **no** |
| `atom(V)` | **no** (only `Tag.Atom` satisfies it, always routed to Const) | yes | no | no |
| `atomic(V)`, `integer(V)`, `number(V)`, `float(V)` | yes (BigInt/Rational/Foreign take the `_ => varAddr` arm at `:1606`) | yes | **no** | **no** |
| `compound(V)` | **no** | **no** | yes | yes |
| `is_list(V)` | yes (partial lists are unbound) | yes (`[]` is an atom) | yes | **no** |

(An empty packed string never reaches the switch as `Tag.Pstr`: `Deref` normalises it to
its tail — `src/Shumway.Core/Activation.cs:766-781`, `NormalizeEmptyPstr` — so the
`Tag.Pstr when AsPstrLength > 0` arm is the only PSTR arm that fires, and `atom(V)` is
indeed satisfied only by a cell routed to Const.)
| `callable(V)` | yes | yes | yes | yes (removes nothing; skip) |
| `nonvar(V)` | yes (the `_` arm again) | yes | yes | yes (removes nothing unless the `_` arm is tightened) |

Soundness argument: a clause is only dropped from a bucket whose *tag* makes the guard
fail with certainty, and nothing can bind or change `V` between predicate entry and the
guard — `V` occurs once in the head (no aliasing binding), no body goal precedes the
test, and the wakeup flush at `call_builtin` (`:2512`) can only *bind* an attvar (which
is routed to the Var chain anyway, where the `var(V)` clause stays and fails at runtime
exactly as today). Dynamic predicates (`CompileIndexedDynamic`) are excluded; the IL
describers see ordinary chains with fewer members, no new shape. Optional second step
(interpreter + IL dispatch, small): route `BigInt`/`Rational`/`Foreign` to `constAddr`
so `nonvar`/`integer`/`number` guards can leave the Var chain too.

**Estimated gain.** Per call of such a predicate: 1–3 fewer CP pushes + failed builtin
dispatches + backtracks in Tier-0 (often 30–50% of the predicate's dispatches); in
Tier-1, the guard calls and restore stubs of the skipped clauses vanish. Whole-program
effect depends on how much time a program spends in type-dispatching walkers (term
simplifiers, pretty printers, `copy`/`subst`-style utilities, `simple_analyzer`,
`reducer`, `unify` in the Van Roy set): a few percent typical, more on
interpreter-style programs. Measure with `shumway-disasm --audit` (count of VAR_HEADED
predicates whose clauses start with a type test — the census is cheap to add), then
deterministic counters (CP pushes, backtracks, opcodes via `SHUMWAY_PROFILE`) on
`simple_analyzer`/`reducer`/`unify` and Blint, Tier-0 and Tier-1 separately.

**Risk / invariants.** Pure compiler change; dispatch opcodes, table encoding and the
"var-headed clauses join every bucket" invariant are preserved for unguarded clauses.
Must match the engine's `var/1` semantics on `AttVar` (routed to the Var chain, so no
exposure). Not an ADR-level change (no opcode, no format); ADR-007/027/028 unaffected.

**Novelty check.** `grep -ri "type.test\|guard.*index\|switch detection"` over
ADR-007/027/028/041, phase-33/36/40 closures: only ADR-031's CP-free *guard commit*
(Tier-1, per clause, not dispatch) mentions type tests. `git log --oneline | grep -i
"type.test\|guard"`: no indexing-related commit. ADR-041 is dispatch-time *dynamic*
clause selection; ADR-028's sibling/sub-key buckets key on ground sub-terms, never on
body guards.

---

## 2. Control-construct helpers pass every named variable; pass only the shared ones

**Locations.** `src/Shumway.Compiler/Parsing/MetaTransform.cs:883-890`
(`SynthesizeDisjunctionHelper`: `freeVars` = all named vars of both branches),
`:986-992` (`once`/`ignore`), `:1045-1053` (`$cutb`), `:1066-1084` (`\+` helper),
`:1412-1427` (`CollectNamedVars`: every `VarTerm` except `_`); the cost side:
`src/Shumway.Interpreter/BytecodeInterpreter.cs:1411-1416` (`try_me_else` →
`PushChoicePoint(arity, …)` saves `arity` argument registers),
`src/Shumway.Compiler/Wam/ClauseCompiler.Body.cs:1536-1542` (a first-occurrence temp
argument is `put_variable_x` → `AllocateHeapUnbound`, `BytecodeInterpreter.cs:2407-2417`
for the Y flavour).

**Current behaviour.**

```prolog
p(X, R) :- ( q(X, Y), r(Y, R) ; s(X, R) ).
```

lowers to `p(X, R) :- '$disj_1'(X, Y, R).` with a 2-clause `'$disj_1'/3`. `Y` is local
to the disjunction, but the caller still emits `put_variable_x Xn, A1` (one dispatch,
one fresh heap cell) and the helper's `try_me_else` saves and, on backtracking into the
else branch, restores three registers instead of two. The same happens for every
`\+ (member(E, L), E > 3)` (`E` local), every `findall(X, member(X, L), Xs)` collect
loop (`X` local to the synthesized `$disj`), every `once/ignore` and `catch` helper.

**Proposal.** Compute the helper's head as *named vars of the construct ∩ named vars
occurring elsewhere in the clause* (head, other goals, enclosing constructs — the
`$get_cut_barrier` K variable and bagof witnesses naturally qualify as shared). A
branch-local variable is created fresh inside whichever helper clause runs, which is
observationally identical: on backtracking into the other branch the passed-in variable
would have been unbound again by the trail, and a fresh one is unbound by construction.
Keep the current all-vars behaviour under `DebugCodegen` (ADR-035 wants every source
variable readable in the frame).

**Estimated gain.** Per execution of a construct with m local variables: m fewer
`put_variable` dispatches and heap cells in the caller, m fewer cells saved per CP push
and restored per backtrack, and sometimes a frameless helper clause where the extra Y
slot was the only permanent. Disjunctions and `\+` are among the most frequent control
constructs in real code (phase-33 W9(d) census: 5,831 synthesized helpers in one corpus),
so expect a measurable drop in `--alloc` cells on `chat_parser`, `boyer`, `simple_analyzer`
(1–5% of cells) and a smaller opcode-count drop; Tier-1 benefits identically (helpers are
compiled from the same clauses, and region lowering of a 3-arg helper saves more
registers than a 2-arg one).

**Risk.** Semantics are preserved for ISO; the only observable difference is variable
*naming* in printed unbound locals (`_G123` identity), which no test should pin. The
witness computation of `RewriteBagof` and the `$findall_record_s` template must keep
using the full variable set — only the helper-head construction changes. ADR-025
inline-ITE (default OFF) is the other way to eliminate the helper; this item improves
the default path.

**Novelty check.** `git log --oneline | grep -i "free var\|shared var\|helper.*arity\|
local var"`: nothing. Phase-33 W8/W9 discuss helper *dedup* and the cut-barrier slot,
not arity; `efficiency-audit-2026-06.md` has no helper item.

---

## 3. Order permanent (Y) variables by last occurrence so environment trimming is monotone

**Locations.** `src/Shumway.Compiler/Wam/ClauseCompiler.cs:742-793`
(`ClassifyPermanents` returns permanents in *first-occurrence* order), `ClauseCompiler.Body.cs:1707-1714`
(`CompileState` assigns `Ys[permanents[i]] = i` in that order), `:1176-1214`
(`ComputeLivePermsAfterEachGoal`: live count = highest live Y index + 1),
`:1125` and `:1159` (the trim operand on `call_builtin`/`call`),
`src/Shumway.Core/Activation.Frames.cs:260-272` (`TrimEnv` moves `_stackTop` to
`E + EnvSize(n)`, clamped under a protecting CP). The deep-cut slot and the ADR-025
barrier slots are appended after the named permanents (`ClauseCompiler.cs:268-275`).

**Current behaviour.** The trim operand is "one past the highest live Y", so trimming
only reclaims slots whose *index* is high. With first-occurrence order the head's output
variables (used in the last goal) get the lowest indices — good — but intermediates get
indices in appearance order, which is not their death order:

```prolog
p(A, R) :- q(A, X), r(X, Y), s(Y, R).     % X=Y1, Y=Y2, R=Y0 today
```

liveAfter = [3, 3, …]: after `r` the frame is still 3 because `Y` (Y2) is live although
`X` (Y1) is dead. With Warren's ordering (sort permanents by last-occurrence goal index,
descending; ties by first occurrence) `R=Y0, Y=Y1, X=Y2` gives liveAfter = [3, 2]: the
frame only ever shrinks, which is the property trimming is designed around (ADR-005
mentions LCO/trimming as a reason for the unified stack). The deep-cut slot should be
placed by the index of the last `!` that reads it, the ITE barrier slots by their goal
index, instead of always last.

**Estimated gain.** No change in dispatch count; stack footprint and CP packing in deep
non-tail recursion (parsers, `boyer`, `deriv`, tree walkers), plus fewer dead Y slots
scanned as roots by the ADR-016 conservative stack scan. Expect single-digit-percent
peak-stack reductions, measurable deterministically (`_stackTop` high-water mark per
query) on `chat_parser`, `boyer`, `simple_analyzer`. Honest: modest; it is cheap and
classical, not a hot-path win.

**Risk.** Y numbering changes → disassembler golden tests and `Phase26PermanentEscapeTests`-style
pins churn; `RunGetVariableYRun` (the interpreter's fused `allocate; get_variable_y…`
run) is unaffected (it keys on opcode adjacency, not slot numbers). Tier-1's lazy
Y-slots (phase 28) make the win Tier-0/stack-only.

**Novelty check.** `grep -ri "last.occurrence\|last.use\|trim"` across ADR-005/021 and
phase closures: only the chunk-352 `deallocate` trim and TrimEnv itself; `git log |
grep -i "trim\|y.slot"`: no ordering change ever landed.

---

## 4. Compile-time cost: the clause compiler walks every clause ~10 times with string-keyed, allocating collections

**Locations.** `src/Shumway.Compiler/Wam/ClauseCompiler.cs` — 15 `new Stack<Term>` /
walk sites (`ClassifyPermanents` 742-793 allocates a `Dictionary<string, HashSet<int>>`
with one `HashSet<int>` per variable per clause; `CountVarOccurrences` 894-912;
`CollectVarNames` per head arg 869-876; `CollectForcedSaves` 1128-1150 allocates a
`Stack` per body argument; `UpdateMaxLiveYIdxFromTerm` 1216-1236 one `Stack` per goal;
`ForceIteVarsPermanent`; `AllNamedVariables`), `ClauseCompiler.Body.cs:595-646`
(`StructuralKey`: a `StringBuilder` serialization of every head compound for CSE, even
when no other compound could match), `:1634-1645` (`AtomTable.Intern(name)` string hash
per emitted `get_atom`/`put_atom`/functor), `ClauseCompiler.cs:473-474`
(`FirstScheduledCallIndex(goals)` recomputed per goal) and `:465` (`inlineItes.Contains(i)`
per goal). `src/Shumway.Compiler/Wam/PredicateCompiler.cs:194` (`clauses.Select(...).ToArray()`),
`:283-284` (per-argument LINQ classification re-interning atoms per clause), `:928-939`
(`MergeWithVar`: a `HashSet<int>` + `List<int>` + sort per bucket — one per distinct key
of a fact table), `:795-835` (`Consider`: three `SortedDictionary`s per candidate
dimension per bucket). `src/Shumway.Compiler/Wam/DeterminismAnalysis.cs:433-450,
531-536, 545-554` (`GoalIndicator`/`HeadIndicator` build `name + "/" + arity` strings
for every goal on every fixpoint iteration and in three separate passes).

**Current behaviour / why it costs.** The only data a chunk classification needs per
variable is (first chunk, last chunk, count); a `HashSet<int>` per variable is pure
allocation. Variables are identified by `string` name everywhere, so every lookup hashes
a string. For a 10k-clause consult or an assert-heavy embedded workload (rules still run
the full pipeline — phase-33 W3 only fast-paths facts), this is the dominant part of
`ModuleCompiler.ProfPredTicks`. Fact tables (Arity corpora up to 100 KB of bytecode per
predicate, L3 in the backlog) pay the per-key collection churn in `CompileIndexed`.

**Proposal.** (a) One pre-pass that numbers the clause's variables (`VarTerm` → dense
int id, cached on the term or in a per-clause array) and records first/last chunk, head
occurrence count, and body occurrence count; every later walk uses int arrays. (b) Skip
`StructuralKey` unless the head has ≥2 compounds with the same functor/arity (cheap
pre-check). (c) Replace `Dictionary<string,HashSet<int>>`-style maps with arrays indexed
by variable id; reuse `Stack<Term>` buffers on the `ClauseCompiler` instance (it is
already reused per predicate and per assert). (d) In `PredicateCompiler`, build buckets
with `List<int>` + a merged-var span instead of `HashSet` per key; replace the LINQ
`Select/ToArray/ToDictionary`. (e) Intern each goal's indicator once (a `(nameAtomId,
arity)` struct key) in `DeterminismAnalysis` instead of string concatenation per
iteration.

**Estimated gain.** 1.5–2× on the ClauseCompiler/PredicateCompiler share of consult
time (measure with `SHUMWAY_LOAD_PROF` counters `ProfPredTicks`/`ProfElideTicks` before
and after on a large consult and on an `assertz`-of-rules loop); no runtime effect.

**Risk.** Mechanical refactor; the only semantic trap is Y-slot assignment order (must
stay first-occurrence unless item 3 is also adopted) and the CSE key semantics.

**Novelty check.** `efficiency-audit-2026-06.md` Tier A-2/A-5 and phase-33 W3 covered
the *assert pipeline* (pool snapshots, object reuse, fact fast path) and query setup —
not the per-clause walks; `git log | grep -i "consult.*perf\|load.*prof"`: the
`SHUMWAY_LOAD_PROF` counters exist precisely to measure this and nothing consumed them
for the clause compiler.

---

## 5. Every runtime `assertz` of a rule with `;`/`->`/`\+`/`once`/`findall` mints a *permanent* atom

**Locations.** `src/Shumway.Compiler/Parsing/MetaTransform.cs:530-542` (`HelperName`:
`$disj_<id>` from `HelperIdProvider`), `src/Shumway.Embedding/PrologEngine.Libraries.cs:1483`
(`NextMetaHelperId() => ++_metaHelperSeq`, monotonic per engine),
`src/Shumway.Embedding/PrologEngine.DynamicStore.cs:2331` (the runtime assert path passes
it), `src/Shumway.Compiler/Wam/ClauseCompiler.Body.cs:1634-1645` (`InternPredicate` →
`AtomTable.Intern(name, permanent: true)` for every predicate name, helpers included);
ADR-003 §tiers ("Permanent: never collected").

**Current behaviour.** A long-running engine that asserts and retracts rules containing
control constructs (rule engines, learned-clause systems, REPL sessions) grows the atom
and functor tables by one permanent entry per construct per assert, forever; retract and
abolish cannot reclaim them. Phase-33 E12 made the sequence per-engine instead of
process-global to keep the *functor-id* space bounded across engines, but within one
engine it is unbounded.

**Proposal.** Either intern helper names transiently (they are only ever named by the
clause that owns them and by the predicate table — the ADR-003 transient tier is built
for exactly that: "atoms of code compiled by … assert"), or give runtime-asserted
helpers a structural name reused across identical constructs (phase-33 W9(d) rejected
dedup for *size*, but for runtime assert it bounds the table). Verify first that the
predicate table does not need the name to be permanent (the comment at `:1634` says the
tables are keyed by functor id, which an `Atom` claim can keep alive while the clause
lives).

**Estimated gain.** Memory only (tens of bytes per assert plus table growth and the
resume-marker side-table pressure E12 hit); no throughput change. Measure: assert/retract
a rule with a disjunction 1M times and watch `AtomTable` count.

**Risk.** ADR-003 territory (atom tiers); the change is within the ADR's stated intent,
but the claim/retire protocol for predicate-name atoms must be confirmed — treat as
needing an ADR-003 addendum if names turn transient.

**Novelty check.** Phase-33 E12 and W9(d) discuss helper naming and dedup, neither the
permanence of the atoms; `grep -rn permanent docs/architecture/adr/003*` lists bundles
and builtin names as the intended permanent set, not runtime helpers.

---

## 6. Nested arithmetic compiles to the a_eval stack even when a chain of `a_int_bin` into scratch registers would do (Tier-0)

**Locations.** `src/Shumway.Compiler/Wam/ClauseCompiler.Body.cs:1327-1336` (fused
`a_int_bin` only for a *flat* `Target is A op B` over leaves), `:1062-1080` (fused
`a_int_cmp` only when both comparison operands are leaves), `:1384-1431`
(`CompileArithExpr`: anything nested → `a_eval_push`/`a_eval_bin` RPN), `:1468-1481`
(`TryResolveTarget` kind 5 = store into a fresh X register — no heap cell).

**Current behaviour.** `N mod 2 =:= 0` emits `a_eval_push N; a_eval_push 2; a_eval_bin mod;
a_eval_push 0; a_eval_cmp` (5 dispatches); `X is A*A + B*B` emits 7. The fused
`a_int_bin` can already deliver into a *register* (kind 5), i.e. the hoisting that
ADR-018 rejected is rejected only because `$arith2` hoisted into *heap* variables
(ADR-018 "Representation 2"); a register-targeted hoist allocates nothing.

**Proposal.** In `CompileArithIs`/the comparison path, when the expression is a tree of
lane operators over leaves, emit a left-to-right chain of `a_int_bin` into anonymous X
registers (`AllocateAnonymousSlot`) and finish with `a_int_bin`/`a_int_cmp` on the last
temp: `N mod 2 =:= 0` → `a_int_bin mod N,2 → Xt; a_int_cmp Xt, 0` (2 dispatches).
Evaluation order and error behaviour are unchanged (left operand first, same
`FusedBinSlow` fallbacks for overflow/float).

**Estimated gain.** Tier-0 only: 2–3× fewer dispatches for nested arithmetic (`crypt`,
`sendmore`, `queens`, `tak`-style guards with expressions). Tier-1 already consumes the
a_eval sequence through `ArithBounds` (`src/Shumway.Compiler/Wam/ArithBounds.cs`) to run
an overflow-check-light integer lane, which a chain of independent `a_int_bin`s would
*lose* — so either keep the a_eval form for Tier-1 (describe the chain back, or gate the
rewrite on Tier-0-only targets such as Native AOT / wasm Tier-0 / `--release` Tier-0
bundles) or extend `ArithBounds` to a_int chains. Measure with deterministic opcode
counts on `sendmore`/`crypt` in Tier-0 and an ABBA on Tier-1 to prove no regression.

**Risk.** Register pressure (one anonymous X per intermediate; the CP saves only `arity`
registers, so temps are safe across the arithmetic — nothing calls out), and the Tier-1
interaction above. No opcode change.

**Novelty check.** ADR-018 history (sections "Representation 2" and the a_int lane) and
`git log | grep -i "a_int\|a_eval\|arith"`; phase-33 W9(a)/L7 cover literal kinds and
JIT constant folding, not nested-expression fusion into registers.

---

## 7. Widen the inline-goal prefix with non-binding test builtins (type tests, `==`/`\==`/`@<`…) for neck-cut and frameless classification

**Locations.** `src/Shumway.Compiler/Wam/ClauseCompiler.cs:980-988` (`IsInlineBodyGoal`:
only `!`, `is`, `=`, the six arithmetic comparisons), `:242-253` (`inlinePrefixLen` →
deep cut otherwise), `:290-297` (`needFrame`), `src/Shumway.Compiler/Wam/DeterminismAnalysis.cs:521-528`
(`IsInlineLike`, kept in sync by comment). Runtime facts: `call_builtin` runs inline in
the dispatch loop with `Cp`/`B0` untouched (the W9(e) audit recorded at
`ClauseCompiler.cs:967-979`), and a type/identity test binds nothing, so no wakeup can
be scheduled by it.

**Current behaviour.** `check(X) :- var(X), !, throw(error(instantiation_error, _)).` or
`is_empty(T) :- T == nil, !.` compile with `allocate_get_level 1`, `call_builtin`,
`cut Y0`, `deallocate…` — a frame and a barrier slot for a cut that is still a neck cut.
Because the chunk model (ADR-021) makes any variable used after the cut permanent
anyway, the win is confined to clauses whose post-cut body shares no variable with the
head (error guards, `fail`/`throw` tails, boolean tests), which is why this ranks low.

**Proposal.** Add the ISO type tests and the identity/order tests (the
`ClauseFold.IsTypeTestName`/`IsIdentTest` sets at `ClauseFold.cs:267-275`) to
`IsInlineBodyGoal` and `IsInlineLike`, after confirming each builtin's registry entry is
non-backtrackable, non-meta, non-binding. `WarrenScheduleFirstGoal` must then skip them
like arithmetic (`:1014-1016`).

**Estimated gain.** 2–3 fewer dispatches and no frame on the affected clauses (Tier-0);
in Tier-1 these guards are already CP-free (ADR-031 case E), so this is mostly a Tier-0
and code-size item. Measure with `shumway-disasm` counts of `allocate_get_level` whose
only pre-cut goals are tests.

**Risk.** Low; the W9(e) `=/2` widening is the precedent and its audit list applies.

**Novelty check.** Phase-33 W2 ("deliberately does NOT extend …") and W9(e) widened the
prefix to `=/2` only; nothing mentions type tests; `git log | grep -i "inline.*goal\|
neck"` confirms.

---

## 8. Fold `T =.. [f, A, B]` and `functor(T, f, N)` with a literal functor into inline unification

**Locations.** `src/Shumway.Compiler/Wam/ClauseCompiler.Body.cs:1082-1094` (inline `=/2`
lowering), `:1111-1133` (every other builtin, `=..`/`functor/3` included, is a
`call_builtin` with argument staging — the list `[f, A, B]` is *built on the heap* first:
`put_list` + `unify_*` ×3 cells, then dispatched, then unified).

**Current behaviour / proposal.** When the right side of `=..` is a proper list whose
head is an atom literal, `T =.. [f, A, B]` ≡ `T = f(A, B)` in every mode (bound `T`:
both fail or succeed identically; unbound `T`: both bind `f(A, B)`; `[f]` ≡ `T = f`; no
error case differs because the error cases of `=..` need a non-atom head or a partial
list, which are excluded). Likewise `functor(T, f, 2)` ≡ `T = f(_, _)` for an atom
literal and a small non-negative integer literal. Rewrite them in `CompileBodyGoal`
before the builtin dispatch so the existing inline `=/2` path (`TryCompileUnifyInline`)
and ADR-063 images handle them.

**Estimated gain.** Low: these shapes are rare in hot code (the common `Goal =.. [Name |
Args]` with a variable name is not foldable). Where they occur: one list build + one
builtin dispatch replaced by a `get_structure`/`put_structure` stream.

**Novelty check.** `git log | grep -i "univ\|=\.\.\|functor/3\|fold.*builtin"`: only the
error-culprit commit; neither audit mentions it.

---

## Considered and found already done, rejected, or not worth it

- **Chunk-transparency of inline guards (keeping `X` in a register across `X > 0`)** —
  ADR-021 REJECTED on the Blint survey (1.5% of permanents are Class B) and on the
  unsoundness record (wakeup flush at the next call clobbers non-argument registers;
  head-time hooks). A comparison-only prefix (no `=`/`is`, so no bindings and no wakeups)
  is sound by the ADR's own reasoning, but the ADR explicitly asks for new survey
  evidence before revisiting; not proposed.
- **Stack-resident unbound permanents / `put_unsafe_value`** — impossible under the cell
  invariant "REF cells point within the heap" (`docs/architecture/invariants.md:40`);
  the heap-allocated Y design (chunk 313) is a consequence. A new address space would be
  an ADR-002/004 change; not proposed.
- **`execute_builtin` fusion for last-goal builtins** — rejected on census in phase-33 W6.
- **Fusing `deallocate; execute` into the existing `DeallocateExecute` opcode** — deferred
  for the link-time `Execute → ExecuteIl` rewrite (`PredicateCompiler.cs:82-88`); the
  opcode exists unused; still needs an `ExecuteIl` twin, so not re-proposed here (Tier-0
  dispatch agent's domain).
- **Skipping the per-clause `Meta/DbgInfo` no-op** — it is only emitted under
  `compile_mode=debug` (`PrologFlags.EmitDebugInfo` defaults to `false`,
  `src/Shumway.Compiler/Parsing/PrologFlags.cs:217`); the `PredicateCompiler` default of
  `true` is overridden by every engine path. No cost in release code.
- **Reordering head constant tests before `get_variable_y` stores (fail fast)** — would
  break the interpreter's fused `allocate; get_variable_y…` run (`RunGetVariableYRun`,
  `BytecodeInterpreter.cs:1278`); net negative in Tier-0.
- **Register preferencing extensions** (allow `_` at the target register; Warren
  scheduler for later goals) — the scheduler already covers moves; the residual
  `put_value` gap is the allocator ADR-021 closed.
- **Nested compound builds, void batching, CSE, neck-cut transparency, arithmetic fast
  lane, ADR-030 cut elision, ADR-031 CP-free guards, ADR-027/028 deep indexing, DCG
  disjunction reconciliation (W5) and leading-terminal hoist, `once`/`ignore`/`findall`/
  `forall`/`catch` in-engine rewrites, static `call/N` rewrite, ADR-063 images** — all
  verified present at the cited sites; the DCG translation (`DcgTransform.cs`) is at
  parity with SWI/GNU expansion and no further gap was found.
- **First-argument `Other` kinds (float/PSTR/bigint heads) treated as var** — rare in
  heads; a PSTR head routes to the list bucket only via the cons-chain hoist; not worth
  a change.
- **Body-local CSE, helper dedup by structural key, float/bigint `a_int` literal kinds**
  — rejected on census in phase-33 W9; not re-proposed.
