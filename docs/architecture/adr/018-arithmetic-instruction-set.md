# ADR-018: Arithmetic instruction set (RPN evaluation stack)

## Status

Shipped ([Phase 25](../../history/wam-vs-gprolog-blint.md)).

The `a_eval_*` RPN instruction set and the
fused `a_int_bin`/`a_int_cmp` integer fast lane shipped in both tiers. New
top-level opcodes are a "major decision" under
[the decision policy](../decision-policy.md), which is why the design was
settled in this ADR before the code landed. It supersedes and retires the
goal-rewriting arithmetic inlining that preceded it (`ArithInline` + the
`$arith2` / `$arith1` builtins).

## Context

ISO Prolog arithmetic — `X is Expr`, and the six comparisons (`=:=`, `=\=`,
`<`, `>`, `=<`, `>=`) — evaluates an expression *term*. Shumway has gone
through two representations:

1. **Build-the-term** (original): the compiler emits `put_structure` /
   `put_value` to build `-(X,1)` etc. on the heap, then calls `is/2` /
   the comparison, which walks the heap term. Every operator node is a
   heap structure (functor + args).

2. **Goal-rewriting inlining** (chunks 295/296): `ArithInline` rewrites
   `X is A op B` into a flat sequence of `'$arith2'(T, Op, A, B)` builtin
   goals, hoisting each nested sub-expression into a synthetic Prolog
   variable `$G<n>`. This stopped the *expression terms* being built, but
   every intermediate `$G<n>` is a Prolog variable, and a Prolog variable's
   home is a heap cell (`put_variable_*` → `AllocateHeapUnbound`).

Representation 2 is a real win where the expression is shallow (tak's
`X-1` allocates nothing now). But a *deeply nested* expression trades term
cells for synth-variable home cells almost one-for-one. The worst case is
sendmore's test goal:

```
1000*S + 100*E + 10*N + D + 1000*M + 100*O + 10*R + E
  =:= 10000*M + 1000*O + 100*N + 10*E + Y
```

~30 operator nodes ⇒ ~30 synthetic variables ⇒ ~30 heap cells **per
evaluation** of the goal, and the goal is evaluated on every leaf of a
deep generate-and-test search. `--alloc` after chunk 296: sendmore is
**2 991 248 cells/iter**, the single largest allocator in the Van Roy
suite, essentially *all* of it these synth-variable homes.

The arithmetic is *pure* — no logical variables are produced, nothing is
trailed, the intermediates are transient numbers. Representing them as
Prolog variables (heap cells, deref chains, the unification machinery) is
pure overhead.

### What other engines do

Every performance-oriented Prolog compiles arithmetic to a **dedicated
stack machine** evaluated over a small internal number stack — no heap
term, no logical intermediates:

- **SWI-Prolog**: a family of VM instructions (`A_INTEGER`, `A_VAR`,
  `A_ADD`, `A_MUL`, `A_FUNC2`, `A_IS`, `A_LT`, …) push/pop an internal
  `Number` stack; `X is Expr` and comparisons compile straight to them.
- **GNU Prolog**: `gplc` inlines arithmetic to mini-assembly over C
  registers/locals — no Prolog-level term at all (a large part of why it
  is ~15× faster than Shumway on tak/crypt today).
- **SICStus / YAP**: dedicated arithmetic instructions + an internal
  evaluation stack.

The WAM **control stack** (frames, choice points) is *not* the right place
for this — it holds machine state, not transient numbers. The standard is
a separate, tiny evaluation stack of number values.

## Decision

Add an **arithmetic instruction set**: a small family of opcodes that
evaluate an expression in postfix (RPN) order over a per-engine evaluation
stack of `Number` values. The compiler emits these for `X is Expr` and the
six comparisons; the goal-rewriting `ArithInline` / `$arith2` / `$arith1`
path is retired.

`Number` is already a `readonly struct` (Int / BigInt / Float, ADR-013
promotion rules), so a `Number[]` stack holds entries inline — pushing is a
struct copy, **zero managed allocation** for Int/Float (and no *new*
allocation for BigInt, whose backing array already exists). Crucially, the
arithmetic never touches the WAM heap: **zero `Cell` allocation** for any
expression, however deeply nested.

### The instruction set

A compact set (exact opcode bytes are an implementation detail; they take
free slots in the 0x01–0xFD fixed-size range, ADR-006):

| Instruction | Operands | Effect |
|---|---|---|
| `a_push` | `kind:byte`, `operand:int32` | push a leaf: `kind` ∈ {int (operand = value), bigint-lit, float-lit, x-reg, y-slot}. For x-reg / y-slot, deref and **evaluate** the cell to a `Number` first (see below). |
| `a_bin` | `op:byte` | pop *b*, pop *a*, push `a op b` (`op` selects +, −, *, /, //, mod, rem, min, max, **, ^, /\, \/, xor, <<, >>, gcd, atan2). |
| `a_un` | `op:byte` | pop *a*, push `op(a)` (`op` selects −, +, abs, sign, \\, sqrt, sin…, integer). |
| `a_is` | `kind:byte`, `target:int32` | pop the result, unify it with X[target] / Y[target] — the `is/2` store. |
| `a_cmp` | `op:byte` | pop *b*, pop *a*, compare (`op` ∈ {=:=, =\=, <, >, =<, >=}); succeed, or fail (backtrack). |

So `X1 is X - 1` compiles to `a_push x-reg X; a_push int 1; a_bin sub;
a_is x-reg X1` — four instructions, **no heap, no synthetic variables**.
sendmore's `=:=` becomes one straight-line RPN sequence over the eval
stack, allocating nothing.

`a_bin` / `a_un` reuse the existing `ArithmeticEvaluator` arithmetic
(`Add`, `Subtract`, …) for full ISO promotion (long → BigInt on overflow,
float contagion) and the existing error kinds. `a_cmp` reuses
`Number.Compare`. So the *semantics* are exactly today's; only the
*dispatch* changes.

### The operand-is-an-expression edge case

ISO `is/2` evaluates its right side *recursively*: if a variable in
arithmetic position is bound to an unevaluated term (e.g.
`Y = 2+3, X is Y - 1`), that term is evaluated. `a_push x-reg`/`y-slot`
therefore does not blindly read a number — it derefs the cell and, if it
is a compound, evaluates it with the existing recursive
`ArithmeticEvaluator.Evaluate` (which itself walks the heap term). The
common case (the cell is a number) is a single tag check + push; only the
rare bound-to-a-term case pays the recursive walk — identical to today.

### Error context

The dispatch site for `a_is` stamps the offending-builtin indicator as
`is/2`; `a_cmp` stamps the comparison operator's indicator. This *fixes*
the small wart the goal-rewriting left (chunk 296: a compound operand of a
comparison reported `is/2` instead of the comparison) — with dedicated
instructions the interpreter knows which construct it is executing.

## Blast radius

| Area | Change |
|---|---|
| `Opcode` enum + size table (ADR-006) | five new opcodes with their fixed sizes |
| `Engine` | a `Number[] _arithStack` + top index (reused across evaluations; grows on the rare deep expression); push/pop helpers |
| Bytecode interpreter | dispatch arms for the five opcodes (the evaluator core — `a_bin`/`a_un` over the stack, `a_is` unify, `a_cmp` compare/fail) |
| Compiler | an `ArithCompile` pass replacing `ArithInline`: walks `X is Expr` / comparison goals, emits the postfix instruction sequence; reuses the operator → op-byte tables. Non-evaluable functors still fall back (emit `is/2` / the comparison as a normal call so the same `type_error(evaluable, _)` is raised). |
| Retire | `ArithInline`, the `$arith2` / `$arith1` builtins and their registration. `ArithmeticEvaluator.EvaluateBinary` / `EvaluateUnary` stay public (the interpreter calls them) |
| Tier-1 IL | the IL compiler emits the same RPN over the eval stack (follow-up; until then an arithmetic-bearing predicate is simply not promoted, as several shapes already are) |
| Tests | the existing arithmetic ISO conformance + the chunk-130 context tests must stay green; new tests for deep-expression `--alloc` = 0 |

The change is concentrated and does **not** touch the cell layout
(ADR-002), the trail (ADR-004), unification, or the heap GC. It only adds
opcodes and a side evaluation stack.

## Alternatives considered

- **A. Keep the goal-rewriting (chunks 295/296).** Already shipped, no new
  opcodes, but leaves the synth-variable home overhead — sendmore stays at
  ~3M cells. The thing this ADR is replacing.
- **B. Eliminate synth-variable homes inside the goal-rewriting** (make
  `$G<n>` register temporaries that `$arith2` writes directly). Possible,
  but it needs the chunk-liveness analysis to special-case `$arith2`
  (which only clobbers X0..X3, not all registers) so a cross-call register
  survives — fragile, and it still pays builtin-dispatch + register
  shuffling per operator. The instruction set is simpler *and* strictly
  faster.
- **C. Full GNU-style native inlining** (compile arithmetic to .NET IL /
  machine code with no interpreter loop). The fastest, but only reachable
  through Tier-1 IL and only for promoted predicates; the Tier-0 bytecode
  interpreter — which every fresh query runs — still needs *an*
  instruction set. C is a Tier-1 follow-up on top of this ADR, not a
  replacement.
- **D. Arithmetic instruction set (this ADR).** The standard
  (SWI/GNU/SICStus/YAP) design; zero arithmetic heap allocation; reuses the
  existing `Number` arithmetic; concentrated blast radius.

## Consequences

**Positive**

- **Zero `Cell` allocation for arithmetic**, regardless of nesting.
  Expected `--alloc`: sendmore from ~3.0M toward the cost of the search
  alone, crypt / tak / queens shed their remaining arithmetic cells.
- Faster than the goal-rewriting even ignoring allocation: no per-operator
  builtin dispatch, no variable binding/trailing, no deref chains — just
  push/pop a struct array.
- Fixes the comparison error-context wart from chunk 296.
- Brings arithmetic in line with how every fast Prolog implements it.

**Negative / cost**

- Five new opcodes — a permanent addition to the instruction set and its
  size table; needs the Tier-1 IL emitter to learn them eventually
  (until then, arithmetic predicates stay Tier-0, which is correct today).
- A second, small machine stack on the engine (transient `Number` values).
  Tiny and bounded by expression depth.

**Invariants preserved**

- ADR-002 cell layout, ADR-004 trail, ADR-006 fixed-size encoding (the new
  opcodes are fixed-size), unification, and the heap GC are all untouched.
- ISO arithmetic semantics are unchanged — the same `Number` arithmetic,
  the same promotion and error kinds; only the dispatch path differs.

## Validation

- `--alloc`: every arithmetic-bearing benchmark loses its arithmetic cells
  — sendmore, crypt, tak, queens drop sharply; pure-list benchmarks
  unchanged. The deep-expression case (a hand-written `X is <30-node
  expr>` in a loop) must show **0** cells from the arithmetic.
- Full ISO arithmetic conformance (`ArithmeticConformance`) green,
  including overflow→BigInt, float promotion, `evaluation_error`,
  `instantiation_error`, `type_error(evaluable, _)`.
- The chunk-130 error-context tests green (`is/2` context for `is`, the
  operator for comparisons).
- The bound-to-an-expression edge case (`Y = 2+3, X is Y - 1`) evaluates
  recursively, as today.
- Full suite (Core / Compiler / ISO / Embedding) green; wall-clock as a
  secondary check (read with the back-to-back A/B discipline — see the
  `--alloc` metric as canonical).
