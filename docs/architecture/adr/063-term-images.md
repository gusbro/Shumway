# ADR-063: A Ground Term of a Clause Body Is Copied from an Image

## Status

Accepted (2026-10-19). Extends the instruction set (ADR-006) with one
instruction and the literal pools (ADR-015) with one pool. No cell tag, no
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

## Not in this decision

Heads keep their `get_*` matching: a `get_blit` (unify against the image in
read mode, copy and bind in write mode) is a later extension. So is an image
with variables, copied and then patched with the registers that hold the
variables: it would cover most body compounds, not only ground ones. Big
integers and rationals stay out of images while their payloads name a
per-activation table. A clause asserted at run time interns its images in
the pool as it interns its string literals, and the pool does not free; the
threshold keeps small terms out of it.

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

A first form took images in heads too: a fact with a thirty-element list
in its head got a copy before its `get_list`, and nreverse, which calls
such a fact in write mode inside a region, ran 6 to 8% slower in both
orders, while flatten and qsort, whose data facts build through helper
calls, ran faster by a third. Heads are out until an instruction that
matches in read mode and copies in write mode exists.

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
