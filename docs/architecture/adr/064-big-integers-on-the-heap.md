# ADR-064: Big integers live on the heap

## Status

Accepted and implemented (2026-10-25). Changes the representation behind `Tag.BigInt`
(ADR-002, row 0x7), retires a side table and the producer of a trail entry
kind, and moves
the hot arithmetic off the platform's immutable big integer type, each of
which `docs/architecture/decision-policy.md` names a major decision.
Supersedes the big-integer half of ADR-053 (the rational half stands) and
amends the runtime paragraph of ADR-013. The tag value, the literal opcodes,
the literal pool, the `.shmo` and `.shum` formats and the embedding API do
not change.

## Context

A big integer is a `Tag.BigInt` cell whose payload is an index into
`Activation._bigIntTable`, a `List<BigInteger>`. `MakeBigInt` appends,
records a `BigIntAlloc` entry on the extra trail so backtracking truncates
the table, and the heap collector sweeps entries no cell reaches (ADR-053).
Every operation on two big integers is a platform `BigInteger` operation.

The factorial by an accumulator shows what that costs. It is the shape of
most big-integer programs, a linear step on a number that grows by a little
each time:

```prolog
fact(N, F) :- fact(N, 1, F).
fact(0, F, F) :- !.
fact(N, A, F) :- A1 is A*N, N1 is N-1, fact(N1, A1, F).
```

`fact(50000, F)` runs 50000 steps, each the product of a number of up to
708357 bits by a small one. The engine takes 1.76 s at best over
interleaved processes, and a single query peaks at 2.3 GB of working set
against 53 MB for the engine at rest. The multiplication is not the cost:
it is one linear pass, and in the profile the platform's own `Multiply`
holds under 1 % of the samples.

Where the time goes:

1. The immutable model. Each platform operation computes its result in a
   scratch buffer and then copies it into a fresh exact-size array. That
   copy holds 97 % of the exclusive samples, and the query allocates 2.2 GB
   of results, one array per step.
2. Retention. Every result stays in the side table, because nothing
   backtracks and the heap collector never runs: the Prolog heap barely
   grows while the table grows by up to 88 KB a step and holds every
   result of the query at once. Every array lands on memory never touched
   before, which is why the copy weighs what it does. The same loop on the platform type
   alone, outside the engine, takes 0.88 to 1.0 s when it drops each
   intermediate and 1.4 to 2.7 s when it keeps them all, as the table
   does. Forcing `garbage_collect/0` every 1000 or every 100 steps gains
   nothing measurable.

Neither is reachable while the value is a platform `BigInteger`: the copy
per operation is the type's contract, and the retention is the side
table's.

## Decision

**D1. A big integer is a block of cells on the heap.** The `Tag.BigInt`
cell keeps its tag and its 32-bit payload, which becomes the heap index of
the block's header, read with `AsHeapIndex` like a `Str`. The block is
`n + 1` consecutive cells, all tagged `RawInt`:

```
header   RawInt   payload = n << 1 | sign        n limbs; sign = 1 when negative
limb i   RawInt   payload = magnitude bits 60i .. 60i+59, i in 0 .. n-1
```

Limbs are little-endian and 60 bits wide, the top limb is nonzero, and the
canonical rule of ADR-013 stands: a value in `[-2^59, 2^59 - 1]` is an
`Int` cell and never a block, so every block has `n >= 1` and a one-limb
block holds a magnitude in `[2^59, 2^60)`. The factorial of 50000, 708357
bits, is 11807 cells, 7 % more than the 32-bit array it replaces.

The limbs are 60 bits because the heap is tagged. The collector reads the
tag of every heap cell it pops from the mark list and of every live cell it
relocates (ADR-016), so a limb must be a leaf under both, which is what
`RawInt` is for. A 64-bit limb would read as a `Ref` or a `Str` one time in
sixteen and be followed or rewritten. Measured on a linear kernel (one pass
multiplying two operands by small factors and adding them), the nibble costs
5 to 15 % against 64-bit limbs, against the two to three times the model
gives back. It also leaves 4 bits of headroom in every
product, so the carry chains need no overflow tests.

No new tag. `RawInt` is already the cell the collector treats as a raw word,
and the tag space has two values left (ADR-051).

**D2. Reading and writing go through two views.** `MakeBigInt(BigInteger)`
keeps its signature and writes a block at the heap top. `AsBigInt(Cell)`
keeps its signature and builds a `BigInteger` from the block, now a linear
conversion where it was a table lookup: every cold consumer (formatting,
interop, `findall/3`'s solution buffer, type tests, clpfd bounds) compiles
unchanged. The hot consumers get a third entry point, a limb view of the
block (heap index, limb count, sign) that copies nothing.

**D3. The hot arithmetic runs on limbs, in scratch, and writes the result
once.** A kernel set in `Shumway.Core` operates on spans of tagged words,
the `RawInt` nibble in the top four bits and the limb below, so a heap block
and a scratch buffer have one format and a result lands on the heap as a
block copy. The first set is what the profile says matters plus the operations
that are linear over limbs and short to write: compare, add, subtract,
multiply by one limb, negate, shift left and right, division and remainder
by a divisor below 2^30 (a short division by the reciprocal, no hardware
divide), and the conversions to and from `BigInteger`. Two large operands
multiply by Karatsuba over the limbs, the schoolbook below 32 limbs, and an
operand multiplied by itself (one block on both sides) takes a square kernel
that computes each cross product once. Measured in one process against the
platform multiply on the same values, Karatsuba on 60-bit limbs is 10 to
30 % faster at every size from 32 limbs to 12641, so the product of two
large operands does not bridge.

`Number` gains a kind for a limb view: over a heap block, or over a buffer
the value owns. A result is computed into a buffer rented from a small pool
(`LimbArith`, thread-static like the evaluation stack the values travel on),
and the `Number` that names it owns it until something consumes the value:
`ApplyBin` and `ApplyRel` return their operands' buffers once the result is
computed (unless the result still names one, as `max/2` does), and `ToCell`
returns the result's after copying it to the heap. Ownership by value rather
than by stack slot is what keeps a value such as `max(A, B)`, which is one of
its operands, from being overwritten while it is still on the stack. The
pool holds no state between goals, only free buffers kept at the largest
sizes seen, so a nested evaluation (the wake drain of ADR-049) never meets a
live one. A holder that outlives the evaluation, the flag store, detaches
the value into a `BigInteger` first.

A product of limbs by a limb-sized multiplier is not computed when it is
formed: it is a Scaled value, the base limbs and the multiplier, and the
operation that consumes it decides. A sum of two products of one sign runs
as the fused pass `a*ma + b*mb` over both bases at once; a product that
ends the evaluation is computed straight into its block on the heap; a
second multiplier combines with the first while their product stays below
2^60; anything else computes the product into a buffer first. The
factorial's `A1 is A*N` is therefore one pass that writes the new block in
place, with no buffer and no copy, where it was a scratch buffer, a fresh
array and a copy; an expression such as `3*X + 5*Y` with large `X` and
`Y` is one pass over both and one block copy, where it was three of each.

**D4. Everything else bridges.** An operation outside the kernel set
(division and remainder by a large divisor, gcd, powers past the long lane,
bit operations, decimal formatting, conversion to float) converts its
operands to `BigInteger`, computes there, and the result comes back as a
block through `MakeBigInt`. The conversion is linear; the operations behind
it are at least linear and mostly worse, so the bridge is never the bound.
The platform type keeps divide-and-conquer division behind the bridge; a
kernel replaces a bridged operation only when a measurement says the bridge
is what is being paid, as it did for division by a small divisor (2.4
times slower over the bridge than before this decision) and for the
product of two large operands (1.5 times).

**D5. A literal is a block too.** The literal pool keeps `BigInteger`
values and the codec of ADR-013 keeps writing them as byte strings, so no
bundle changes. `put_bigint` writes the value as a block at the heap top
and sets the register to its reference; `get_bigint` and `unify_bigint` in
read mode write the block and unify it with the argument, which reclaims
like any other cells (comparing against a block prebuilt at load, with
nothing written, is the plan's second stage). A big integer under a
compound joins `PreEmitMultiCellLiterals` beside floats and strings, built
into a temporary and referenced with `unify_value`, because a block cannot
be written inline where the argument cell goes, and `CanInlineCompound`
keeps such a compound out of the inline nested build of ADR-019, as it does
for a float. `unify_bigint` in write mode therefore meets only values in
the inline range, which are one `Int` cell, and says so if it meets a
block. The Tier-1 IL does not emit the three opcodes; a predicate holding
one stays on bytecode as before.

**D6. Backtracking and collection reclaim blocks like any other cells.** A
block above HB is reclaimed by the heap top reset; `TrailType.BigIntAlloc`
has no producer any more, and its code stays only because a wasm module
bakes the trail codes into its compaction (`RationalAlloc` stays in use).
The collector's
`BigInt` arm marks the header and its `n` limbs, bounds-guarded like the
PSTR arm, and relocates the reference through the forwarding table; the
side-table liveness bitmap and the big-integer half of the numeric sweep go
away. The collection trigger does not change: a block counts as its cells,
so the watermark that today never fires on a big-integer loop fires once
per megacell of results. A sliding compaction assigns forwarding addresses
over the whole heap, so a collection's cost grows with the cells allocated
since the last one, not with the live set, whatever the threshold:
measured on a loop of products over numbers of about 750k bits with the
collector as it was, a threshold of 100k cells, the default megacell and
100 megacells gave the same time within noise, and that time was a third
of the loop. The collector now
keeps its marks in a bitmap and its forwarding map as a count per 64-cell
word (ADR-016), so that cost is the words of the heap and the live cells,
and the loop no longer sees it.

**D7. Equality and order compare limbs.** Two `BigInt` cells are equal when
they name the same block or their headers and limbs match. Standard order
puts a negative block below every `Int` and a positive one above, and orders
two blocks by sign, limb count and limbs from the top. Mixed comparisons
with floats keep today's path through the bridge.

**D8. Both tiers end in the same kernels.** Tier-1 IL calls the evaluation
stack's helpers as it does today, so compiled arithmetic takes the kernels
with no new emission. The wasm tier declines big integers today, in
unification by escaping to the host and in `is/2` by deoptimising, and keeps
doing so: its escapes land in the same helpers. Operating on heap limbs
inside the module is possible later because the limbs are now where the
module can see them; it is not part of this decision.

**D9. Rationals do not move.** ADR-039's side table of `BigInteger` pairs
stands; an integral result still collapses through `MakeBigInt` to an `Int`
cell or a block.

## Consequences

- The working set of a deterministic big-integer loop is its live set, as
  for every other term. The retention ADR-053 found for big integers is
  gone by construction rather than swept.
- On the factorial of 50000, at best over interleaved processes on the same
  machine: 0.90 s from 1.76 s, and 1.37 s from 2.17 s for the first query
  of a process. A single query peaks at 89 MB of working set from 2.3 GB,
  against 53 MB at rest. The loop on the platform type alone, dropping
  every intermediate, takes 0.88 to 1.0 s and allocates 2.2 GB: the engine
  now runs the loop as fast as the platform's own while allocating nothing
  per step.
- Beyond the factorial, a battery of other shapes against the engine
  before this decision, warm, the minimum over interleaved processes, as a
  fraction of the old time: sums of two large numbers (Fibonacci) 0.31,
  products by a small factor (a smaller factorial) 0.40, a decimal accumulator 0.42,
  shifts 0.44, division by a small divisor 0.46, remainder by one 0.50, a
  power sum 0.66, products of two large operands 0.81 to 1.00, gcd 0.89;
  comparison, division of two large numbers, repeated squaring and decimal
  formatting within noise of 1.00. No shape measured is slower.
- A big integer costs 7 % more cells than bytes before, and its arithmetic
  runs on 60-bit limbs, 5 to 15 % slower per limb than 64-bit ones would.
- A big integer holds at most `int.MaxValue` bits, the cap .NET's own
  `BigInteger` has, on every target: .NET Framework computes larger values
  that .NET refuses, and `MakeBigInt` answers them, and any it cannot
  convert for lack of memory, with the catchable `resource_error(memory)`
  that the heap's own exhaustion raises.
- `AsBigInt` becomes linear. A cold consumer that calls it in a loop over a
  huge value pays that per call (`between/3` with bounds past 60 bits, for
  one); none of them is on a path a program spends its time in.
- A literal costs its size per execution, where it cost a table append;
  this is the price a float or a packed string literal pays today.
- The scratch buffers are thread-static like the stack that owns them and
  are retained at the largest size seen on that thread. A program that once
  computed a number of a hundred megabits keeps that buffer; the stack's
  lanes behave the same way at a smaller scale.
- `TrailType.BigIntAlloc` leaves the trail; the trail's shape does not
  change. Tests that pin the side table's lifetime are rewritten against
  the heap: a block survives a collection with its reference relocated, a
  dead block is reclaimed, backtracking reclaims a block above HB, and a
  failure-driven loop over big integers runs in bounded memory.
- `Math.BigMul(ulong, ulong, out ulong)` does not exist on .NET Framework;
  the kernels carry a four-product polyfill under `NETFRAMEWORK`, and the
  32-bit lane runs it slower and correct.
- A heap dump shows `RawInt` cells inside the heap. The renderer prints the
  value through `AsBigInt` as before.
- Documents amended with this ADR: ADR-002 (row 0x7), ADR-013 (runtime
  semantics), ADR-053 (scope), `invariants.md` (managed payloads, side
  tables, trail entry kinds), `docs/design/cell-layout-detail.md` (the
  BIGINT section), the quick-reference row in `CLAUDE.md`.

## Alternatives considered

**A side table of limb arrays.** The same kernels over `ulong[]` buffers in
the side table, without touching the heap. Keeps 64-bit limbs but keeps the
retention, so it depends on a second decision that makes the collector
weigh the table's bytes, and keeps every result a managed array that the
platform collector must find and reclaim; results past 85 KB go to its
large-object heap, which only its generation-2 collections reclaim. A side
table of buffers is a second heap with a worse collector.

**Trigger the heap collector on side-table bytes.** The smallest change:
count the bytes `MakeBigInt` appends since the last collection and collect
past a budget. Measured by forcing the collection every 1000 and every 100
steps of the factorial: no gain measurable, because the collection resets
liveness bitmaps and walks tables sized by the ids ever allocated, and
because the released arrays still wait for the platform's generation-2
collector. The heap form makes it unnecessary.

**A native multiprecision library.** The usual one is under a copyleft
licence, which the dependency policy excludes, and a native dependency does
not run on the browser lane, costs the AOT lane its single binary, and
needs a second build for .NET Framework. The public-domain alternative is
native too and no faster than these kernels on the operations that matter
here. The gap this ADR closes is not in the library in any case.

**64-bit limbs across the tag.** Impossible on a tagged heap: see D1.

**A new tag for limb cells.** Would let a dump tell a limb from a control
word; buys nothing the collector or the kernels need, and the tag space has
two values left.

**The heap as scratch.** Intermediates built above the heap top and only
the last one kept. Avoids the thread-static buffers and the final copy, at
the cost of growing the heap mid-evaluation and of every kernel re-reading
the heap array after a growth. Kept for the plan's second stage, where it
is a measurement against the buffers, not a precondition.

## Plan

The work lands in stages, each measured ABBA in one process against the
build before it, with the heap cells of `time/1` as the deterministic
check (a result costs exactly `n + 1` cells) and the peak working set
beside the time.

1. This ADR: the block, the views, the kernels of D3, the bridge, the
   literals, the collector and trail changes, equality and order, the
   rewritten lifetime tests and the new kernel and collector tests. One PR.
2. Done with the first: the deferred product and the fused pass for
   `a*x + b*y`, a final product written straight into its block, `^` with
   a small base and exponent in the long lane, and the collector's
   per-cell cost (ADR-016). Still open, each measured before it is kept:
   the per-operation overhead of the evaluator above the kernel floor;
   literals compared against a block prebuilt at load; copies without the
   bridge (the solution buffer, `HeapTermCopy`, and blocks admitted into
   the images of ADR-063, whose reference relocates like a PSTR header); a
   decimal formatting kernel if the bridge shows in a profile.
3. Only on a measured case: Karatsuba and the operations past it. The case
   came with the second stage: once the kernels took the small operations,
   the product of two large operands crossed the bridge twice and lost 1.5
   times to the engine before this decision. Done: Karatsuba and its
   square over limb spans with scratch, a recursion of logarithmic depth,
   the threshold of 32 limbs chosen by measurement, unbalanced operands in
   pieces of the shorter one's length. Toom-3 and transforms stay out until
   a program needs products of millions of bits. Nothing in the
   representation changed for it.

## Tests

- Kernels against `BigInteger` as the oracle: random operands of 1 to 2000
  limbs and the edges, carries across every limb boundary, results that
  collapse to `Int` at both ends of the 60-bit range, sign changes through
  subtraction, zero. Karatsuba and the square at thresholds down to 4, so
  the recursion runs on small operands, balanced and unbalanced, with
  all-ones limbs that carry out of every intermediate sum.
- The block: `MakeBigInt` round-trips every value `AsBigInt` reads back; a
  value in range collapses; two blocks of one value unify, of two values do
  not; the canonical rule holds for every result the kernels produce.
- Collection: a reachable block survives with its reference relocated and
  its limbs contiguous; one nested in a structure survives; a dead one is
  reclaimed and the heap top drops by its size; a stale `BigInt` cell on the
  stack with a header past the heap top is ignored.
- Backtracking: a block above HB is reclaimed; a failure-driven loop over
  big integers runs in bounded heap; `findall/3` over big-integer solutions
  returns the values.
- Literals: a big integer in a body compound, nested, under a reserved-write
  build, and in a head compound, on both tiers, in a bundle round trip.
- A long accumulation as a pin: a linear recurrence of a few hundred steps
  against a `BigInteger` oracle, and the heap a deterministic loop of
  thousands of steps leaves in use.
