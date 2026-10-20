# ADR-063: A Ground Term of a Clause Body Is Copied from an Image

## Status

Accepted (2026-10-19). Extends the instruction set (ADR-006) with three
instructions and the literal pools (ADR-015) with one pool. No cell tag, no
heap layout and no collector change (ADR-002, ADR-016).

## Context

A clause body builds every compound it passes at every call, one instruction
per cell: `put_structure` or `put_list`, then a `set_*` or `unify_*` per
argument. ADR-019 and ADR-020 arrange that work (inline nesting, reserved
frames) and remove temporaries, but each cell is still one instruction on
Tier-0 and, in a large build on Tier-1, one helper call. A ground term, with
no variable in it, is the same cells at every call.

In Blint, `builtin_predicate/2` passes a list of 4,015 cells to `on/2` and is
called 579 times per run: 2.3 million of the 7.2 million heap cells a run
allocates, a third of its allocation, in one predicate, with the compiled
method of that predicate a standalone one too large for a region. The
Van Roy programs and clpz have the pattern only in small terms (6 to 9
cells), where the inline build is already cheap.

Nothing in the engine keeps a term beyond a query: the collector compacts
the whole heap from cell 0, a cell holds absolute heap indices, and a big
integer or a rational names an entry of a per-activation table. A segment
of the heap fixed under the collector would charge every activation for the
program's tables whether it uses them or not, and was rejected on that
ground.

## Decision

1. The clause compiler takes each maximal ground subterm of a body argument
   that has at least `ImageThreshold` cells (8 by default, a switch for the
   measurement) and contains no big integer or rational, interns it in the
   program's image pool, and emits `put_blit image, reg`. The pool is a
   fourth literal pool beside strings, floats and big integers: per engine,
   append-only, deduplicating by structure, so an image id is as stable as
   a string literal's (the cross-query cache audit of ADR-015 admits the
   instruction on that ground). A ground subterm in argument position of a
   compound being built is pre-emitted to a temporary register and
   referenced with `unify_value`, the way a float or a string literal is
   today: the write-mode machinery does not change. Only a stream that
   builds a fresh term takes an image: the compiler marks the body
   arguments and the parts deferred from them, and a head's stream or an
   inline `=/2`'s, which may run in read mode, keeps its matching.

2. An image is the term's cells exactly as the engine lays them on the heap
   (the two-cell cons of ADR-017, the float pair, the packed string of
   ADR-047 with its buffer and tail), with every heap index in it relative
   to the image's first cell. The pool keeps the cells and the tag of the
   root (`Str` or `Lis`).

3. `put_blit` reserves the image's cells at the heap top once, copies them,
   and relocates: a `Str` or `Lis` payload, the buffer index of a `Pstr`
   header and the pair index of a `Float` header gain the base. Atoms,
   functors, integers, buffer cells and float pairs copy as they are. The
   register takes `Str` or `Lis` of the base. The cells are new and above
   HB, and no older variable is bound, so nothing is trailed.

4. Tier-1 calls one helper per `put_blit`, with a helper-table row that
   reloads the heap top and the heap array (the reservation may grow the
   heap). The wasm tier carries each image as a passive data segment of the
   module, copies it with `memory.init` and relocates in a loop, the same
   steps by tag. The data count section a module with passive segments
   needs before its code is inserted into the written bytes, since the
   emitter's library writes none, and the desktop world compiles the bytes
   rather than a module object, which would drop it.

5. A bundle carries the image pool cell by cell, an atom or a functor by
   its name, and the loader interns them into the engine's pool, remapping
   the bytecode's ids as it remaps a string's. Persisted IL cannot be
   remapped at load, and a persisted module is loaded once per process and
   shared by every engine, so its `put_blit` names the process's image
   table instead: the patch site carries the image, the loader interns it
   there and bakes that id. Two id spaces, each consistent: the bytecode's
   and a run-time emit's index the engine's pool, a persisted emit's the
   process's table.

6. Each call still gets its own copy. Term identity, backtracking (the heap
   top is reset as after any build), garbage collection (an image's cells
   are ordinary cells, built whole between two safe points) and the clause
   source that `clause/2` and `listing/1` show are unchanged.

## Why this is sound

A ground term has no variable, so copying its cells is building it. An
image is closed: no reference in it leaves it, so relocation by one base is
exact. The heap top advances once, before the copy, so a collection never
sees a half-built image, and there is no safe point inside the copy. The
instruction has no read mode: a body argument is always built.

## Holes: an image with variables

The decision above took ground terms. A census of body compounds with at
least the threshold's cells, over a corpus of 556 application modules, the
clpz library and Blint, found the ground ones to be a minority of the
cells built: 37% to 45% of the cells of body construction in the
application modules were compounds with variables, with two or three
variables in the median, 23% in clpz, 4% in Blint. So an image holds
variables too:

1. An anonymous variable is a REF cell to its own offset: relocated, an
   unbound variable, and nothing more. A variable repeated inside the image
   is a REF to the offset of its first occurrence. Neither needs a word at
   copy time: the image is what the heap would hold after building the
   term with its variables unbound.

2. A named variable's first occurrence in the image is a hole, decided by
   the compiler exactly as the unify stream would decide the occurrence: a
   variable seen before (a permanent of the frame already initialised, a
   temporary in a register) is a hole the copy fills from that register,
   as `unify_value` writes one in write mode, a bare attributed variable as
   a REF to its home; a variable first seen here is a hole the copy points
   the register at, as `unify_variable` does, the image's cell being the
   variable. The hole table lives in the image, so the literal id names the
   site: two sites with the same term and the same registers share one
   image, two with different registers are two entries. `put_blit` keeps
   its shape, and so does the bundle, cell by cell with the holes after.

3. A compound is imaged when it has at least the threshold's cells and
   holes for at most half of them. The copy of such an image is one
   reservation, one relocating pass and a store or a load per hole, against
   one instruction per cell.

Tier-0 and the IL tier share the copy (`Activation.BuildImage` fills the
holes after relocating; the IL helper's row already names the register
array); the wasm tier emits a store or a load per hole after its relocation
loop. The soundness argument changes in one place: a reference may now
leave the image, through a hole filled from a register. It is a write into
the youngest cells of the heap, not a binding of any variable: nothing is
trailed, no wakeup is due, and the occurs check has nothing to find in a
term that did not exist before the copy.

Two consequences for the tiers' bookkeeping. The wasm tier refuses a
predicate whose text is dominated by host crossings, counting its
compilable instructions; a `put_blit` now counts for its image's cells,
the stores it stands for, since as one instruction the prelude's `length/2`
read as crossings and left the tier. And the prelude and the libraries are
baked at build time, so for them the threshold and the holes are decided
by the build's environment, not by a running engine's switch
(`SHUMWAY_IMAGE_HOLES`, 0 for off), which governs the code compiled after
it.

## Heads: the copy before the matching

A census of head argument compounds at or past the threshold, over the
same corpus, found them 42% of the head cells in the clpz library, 58% to
75% in the application modules, and most of a data fact's cells in the
Van Roy programs that carry one. A head's stream may run in read mode,
matching a bound argument cell by cell, so an image cannot replace it;
but the argument comes unbound as often as it comes bound, and then the
stream builds, one instruction per cell, what a copy would give at once.

`get_blit image, Ai, skip` goes before the argument's stream. The argument
unbound, it builds the image (holes filled from the registers the stream
would have used, an attributed variable's wakeups queued), binds the
variable as `get_structure` binds one in write mode, and jumps past the
stream; bound, or no variable at all, the stream matches as it always
did. The stream keeps its breadth-first order across arguments, which
fails fast on a principal functor that does not match: the prefix covers
the argument's own stream, contiguous by that order, and each part the
order defers (a nested compound not in last position) gets a
`skip_if_copied Xk, L` before it. The copy sets the temporaries of those
parts to a mark, a raw integer no stream leaves in a register, and the
skip jumps when it finds the mark; the stream, in read mode, overwrites
the temporaries with cells before any skip looks.

Which variables the copy can serve is a matter of stream order: every
plain argument binds to its register in the head loop first, then each
compound argument's own stream runs, then the deferred parts. A variable
first met as a plain argument is a hole filled from that register, there
throughout the head; one first met in this argument's own stream or
deferred parts is a hole the copy points the register at, the one the
stream assigned; one first met in an earlier argument's own stream is
filled from the register that stream gave it. A variable first captured
in a later argument's stream, or in an earlier argument's deferred part,
has no register when the copy runs and the stream would overwrite the one
the copy gave: such an argument keeps the stream alone. The census put
these at 9% of the candidates in clpz and 22% in the Scryer libraries.

Tier-0 and the IL tier share the copy and the bind (`Activation.GetBlit`);
the wasm tier copies and binds in the module, steps aside to the
interpreter for an attributed variable, and branches on the mark. The
switch is `SHUMWAY_IMAGE_HEADS`, 0 for off.

## Not in this decision

A matcher that unifies a bound argument against the image, without the
stream, would spare Tier-0 its dispatch per cell; in the IL tier it would
compete with the specialised code the stream compiles to. It is a later
measurement, behind a switch of its own. Big integers and rationals stay
out of images while their payloads name a per-activation table. A clause
asserted at run time interns its images in the pool as it interns its
string literals, and the pool does not free; the threshold keeps small
terms out of it.

## Expected gain (before measuring)

Blint: the build of `builtin_predicate/2`'s list at about 1 ns per cell
instead of 15 to 20 ns, some 35 ms of a 530 ms run, 6 to 7%; the method
leaves the standalone class and can join a region. Heap cells per run do not
change, so the measurement is time, with the count of cells built by images
as the structural check. The other programs of the corpus: unchanged within
the noise.

## Results

One build, the threshold at 0 against 8, two copies of each, eight rounds
interleaved, heap cells identical in every program:

| program | time at the minimum | median |
|---|---|---|
| Blint | 0.84 (0.528 s to 0.442 s) | 0.85 |
| nreverse | 1.01 | 1.01 |
| the other Van Roy programs, clpz | within 3% | within the noise |

With holes, against the same tip without them, one process per build,
eight rounds ABBA over two copies, heap cells identical: Blint, the clpz
puzzles and zebra stay within the noise of the run, which the programs
with no candidate compound put at 0.95 to 1.05; none of the twelve builds
such terms on its hot path. The mechanism itself, one build with the holes
switched off against on, a loop building the census's three shapes (22
cells with three variables seen before, 12 with one first seen and
repeated, 40 of anonymous variables): 0.66 at the minimum and 0.60 at the
median on the IL tier, 0.80 and 0.83 on Tier-0.

Heads, one build with the switch off against on, eight rounds ABBA over
two copies, heap cells identical: a loop that looks a data fact up
unbound, then bound, a list of eight elements and a tree of eleven cells,
0.87 at the minimum and 0.86 at the median on the IL tier, 0.96 and 0.88
on Tier-0; lists of 32 elements and trees of eight leaves, 0.76 and 0.79
(0.86 and 0.83 on Tier-0); lists of 100 elements and trees of sixteen
leaves, 0.71 and 0.74 (0.77 and 0.76). The copy pays from the threshold
on and more as the term grows. Over the corpus, nreverse, whose data fact
is a thirty-element list, 0.92 at the minimum and 0.98 at the median;
the other programs within the noise of the run, clpz at 1.02 and 0.98
over eight rounds. A first form, a copy before the stream with no mode
test, had made nreverse 6 to 8% slower: the stream ran in read mode after
the copy. Blint's heap once grew by 108 cells: a sub-term CSE shares with
an argument's register was copied, and the planner now leaves such an
argument to the stream.

## Risks

The threshold is a cost model: an image has a fixed cost (a call, a
reservation, a loop) that an inline build of a few cells does not. The
wasm module's memory grows by the images it carries. A disassembly shows
an image id where it showed a build; the disassembler prints the term.

## Validation

Tests: the compiler emits `put_blit` for a ground body term at the
threshold and the plain build under it; the interpreter relocates each kind
of cell (a nested list, a packed string, a float, an atom and an integer in
one image); the three tiers give the same answers on a corpus of large
ground literals; a bundle round-trips the pool; a collection after the copy
keeps the term; failing back past the copy frees it. Counter-proof: the
copy without relocation gives wrong terms. Measurement: one process, two
builds, eight rounds, Blint and the corpus.
