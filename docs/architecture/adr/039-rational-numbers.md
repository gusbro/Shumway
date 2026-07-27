# ADR-039: Rational numbers

## Status

Proposed (Phase 36).

## Context

Shumway's arithmetic covers three numeric types: inline integers (`Tag.Int`),
arbitrary-precision integers (`Tag.BigInt`, stored in a per-activation side
table), and IEEE-754 doubles (`Tag.Float`, spanning two cells). It has no
exact non-integer type. `/` on two integers always yields a float
(`ArithmeticEvaluator.Divide`).

SICStus, SWI (v9), and Scryer all provide **rationals** — exact `N/D` values —
as a first-class number type, with the operator `rdiv` for explicit rational
division and (SWI/Scryer) `/` producing a rational when the quotient isn't
integral. Third-party libraries assume they exist: loading Scryer's shipped
`library(clpz)` pulls in `library(arithmetic)`, whose source uses the `rdiv`
operator (`Real rdiv 1`), `rational/1`, `number_to_rational/2`, and
`rational_numerator_denominator/3`. Without `rdiv` even *parsing* the library
fails, which cascades to the whole clpz load. This is the concrete driver:
we want Scryer libraries to load and run unmodified (the Phase-36 library
compatibility goal).

Rationals are also correct-by-construction where floats lose precision — exact
linear arithmetic, CLP(Q)-style reasoning — so this is a genuine capability, not
only a compatibility shim.

## Decision

Add a first-class **rational** number type, `N rdiv D`, mirroring the existing
`BigInt` side-table representation.

### Cell representation — new tag `Tag.Rational` (0xE)

The 4-bit tag has two free values (`0xE`, `0xF`); rationals take `0xE`. A
rational cell's payload is an integer id into a new per-activation
`_rationalTable` — exactly the `BigInt` pattern (ADR-002: no managed reference
in a cell; the value lives in a side table reached by id).

A table entry is a `Rational` struct of two `BigInteger`s (`Num`, `Den`).
**Canonical form invariant:** `Den > 0`, `gcd(|Num|, Den) == 1`, and
`Den != 1`. A value whose reduced denominator is 1 is **not** a rational — it
collapses to `Int`/`BigInt` at construction. So every `Tag.Rational` cell is a
genuine fraction, there is exactly one representation per value, and `integer/1`
stays correct without inspecting the denominator.

Backtrack reclaim and heap GC treat `_rationalTable` exactly as `_bigIntTable`:
the table is a high-water stack trimmed on backtrack, and the collector reclaims
entries the same way it does big integers (the id in a live cell keeps its entry;
`RemoveRange` on undo).

### Value abstraction — `Number.Kind.Rat`

`Number` (the arithmetic evaluator's runtime value) gains a fourth arm,
`Kind.Rat`, carrying a `Rational`. The promotion lattice is:

```
Int  ⊂  Big  ⊂  Rat  ⊂  Float
```

- Rat combined with Int/Big → Rat (exact).
- Rat combined with Float → Float (a float anywhere floats the whole
  expression, as today) — matches SWI/Scryer.
- Every arm collapses downward when exact: `2 rdiv 1` → `Int 2`,
  `4 rdiv 2` → `Int 2`.

`Compare` orders Rat among the numbers by exact value (cross-multiplication
against Int/Big; `AsDouble()` only when a Float is involved).

### `rdiv` operator and `/` semantics

- **`rdiv`** — new operator, `yfx 400` (as in SWI/Scryer). `A rdiv B` on
  integers always produces a reduced rational (or an integer when exact). A
  float operand is a `type_error(integer, _)` (rationals are exact; `rdiv`
  is not defined on floats).
- **`/`** — governed by a new `prefer_rationals` prolog_flag:
  - **default `false`** — `/` on integers behaves as today (float result). All
    existing ISO-conformance output is unchanged. This is the GProlog / current
    behaviour.
  - **`true`** — `/` on integers yields an exact rational when the quotient is
    not integral (SWI/Scryer). A program or library that wants rationals sets
    the flag; Scryer-library loading can set it.

  The flag covers both engine conventions from one implementation. It does not
  affect `rdiv` (always exact) or `//` / `div` (always integer).

### Type tests and classification

`rational/1` succeeds for a `Tag.Rational` cell **and** for any integer
(an integer is a rational with denominator 1, per SWI/Scryer/ISO-Prolog-with-
rationals). `number/1` succeeds; `integer/1` and `float/1` fail for a genuine
fraction. Standard order of terms places rationals among the numbers, ordered
by value (Var < Number < Atom < String < Compound is unchanged; the Number
band now includes Rat).

### Reading and writing

No lexer change: like Scryer, a rational is a runtime value, not a source
literal — there is no `1/3`-as-rational token. Input is via evaluation
(`X is 1 rdiv 3`). `writeq` / `write` render a rational as the operator term
`Num rdiv Den` (re-readable, since `rdiv` is an operator that re-evaluates to
the same value). `write_canonical` does the same.

### New builtins

`rational/1` (type test), `rational/3` (`rational(R, N, D)` — decompose /
compose), `numerator/1` and `denominator/1` (evaluable functions),
`rationalize/1` (evaluable — nearest rational to a float within tolerance),
and the `library(arithmetic)` surface it needs (`number_to_rational/2,3`,
`rational_numerator_denominator/3`) — the last three are pure Prolog and can
live in that library once `rdiv` evaluates.

### What does NOT change

- The integer fast lanes (`a_int_bin` / `a_int_cmp`) are emitted only when the
  compiler proves both operands integer; rationals never reach them and they
  need no rational awareness.
- clpfd / clpz core arithmetic uses `//` (integer) — unaffected.
- `Tag.Float`'s two-cell layout, `Tag.BigInt`, and every existing numeric path
  are untouched. `0xF` remains free.

## Consequences

**Positive.** Exact rational arithmetic; Scryer/SWI/SICStus source
compatibility (unblocks `library(arithmetic)` and thus clpz); both engine `/`
conventions from one flag; zero conformance-suite churn at the default.

**Costs.** A new cell tag is a cross-cutting change (ADR-002 territory): every
site that switches on `Tag` for a numeric value — unify, `==`, `compare/3`,
standard order, term rendering, `copy_term`, the GC scan, the materializer /
dematerializer — gains a `Rational` case. The `Number` union widens, so every
arithmetic op re-examines its promotion. This is mechanical but broad; the
`BigInt` sites are the exact checklist to follow.

**Risk.** A missed `Tag` switch site would mishandle a rational (wrong compare,
lost on GC). Mitigation: rationals mirror `BigInt` one-for-one, so a grep for
every `Tag.BigInt` use is the coverage map; each gets a paired `Tag.Rational`
arm. Tests assert the canonical-form invariant, backtrack reclaim, GC survival,
standard order among mixed numbers, and round-trip through writeq.

**Deferred.** `prefer_rationals = true` conformance-output review (only matters
if we ever ship it on). Rational exponentiation edge cases (`R ^ -N`),
`gcd`/`rationalize` tolerance tuning — implement to Scryer's behaviour and test
against it.
