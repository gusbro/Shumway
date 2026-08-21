# ADR-047: A packed string is a list

## Status

Accepted and implemented (2026-08, branch `pstr-chars`): the presentation bit,
the type and consumer sweep, the writer, the .NET boundary, the `chars` default,
the producers, lazy input, and the removal of `Tag.String`.

Two things it names are NOT delivered and are called out where they arise:
astral-plane correctness (a separate arc), and bounded memory for lazy input —
which needs a precise control-stack scan the engine does not have. See
"Consequences".

## Context

Shumway has had a packed text representation since Phase 1: `Tag.Pstr` stores 3
UTF-16 code units per heap cell instead of the `2n+1` cells a character list
costs in cons form. `pstr-design.md` opens by justifying it — 1 MB of text is
over two million cells as a cons list.

It is not being used. Measured, for a 4000-character atom, in heap cells:

| how the list is obtained | cells |
|---|---|
| `"..."` with `double_quotes=codes` / `chars` | 8001 |
| `atom_codes/2`, `atom_chars/2` | 8002 |
| `"..."` with `double_quotes=string` | **1337** |

The only way to obtain packed text is a literal in `string` mode. DCGs over
text — the use case CLAUDE.md names first — `atom_codes/2`, `atom_chars/2` and
every third-party library all pay `2n+1`.

The cause is a confusion at the root. The PSTR was **designed as a list**:
`pstr-design.md` says a PSTR whose tail is `[]` "is a complete proper list",
and `Activation.UnifyOps.cs` says "A partial string IS the code list it
represents". On top of that, a layer of predicates grew that classifies it as
an atomic *string*. The two views drifted, and five defects lived in the gap:

```prolog
X = "abc", X = [97,98,99]          % failed
X = "abc", Y = [97,98,99], X = Y   % succeeded — same unification, other path
   ... and then X == Y             % was false
compare(O, "abc", [97,98,99])      % gave (>)
```

plus every SWI-lenient text coercion of a PSTR throwing
`InvalidOperationException`, and `copy_term/2` and `findall/3` silently
dropping the tail of a *partial* string. Stage 1 fixed all five.

Fixing the defects is not the point of this ADR; it is the evidence. The
question this ADR answers is which of the two views is the design, so the drift
cannot recur, and so the representation can carry the text the engine actually
produces.

### Where the bytes can live

There are three ways to store the packed characters, and the choice is decided
for us by the fact that we collect:

| layout | what it needs from the engine |
|---|---|
| raw bytes inline in the cell array | cells are never relocated |
| a pointer to a block outside the heap | the block is reclaimed some other way (reference counting, or not at all) |
| **cells on the heap** (ours) | nothing — the buffer is ordinary heap, marked and moved like everything else |

The first two are in use by engines that pack strings, and both are closed to
us: we have a compacting heap GC (ADR-016), so cells move, and an invariant
forbids a managed reference inside a cell (ADR-002), so the .NET GC never has to
scan the heap. A buffer made of cells is what is left, and it is also what makes
packed text markable and relocatable at no extra cost.

### What the survey of other engines settled

Two questions were open when this arc started, and a survey of the engines that
pack strings (2026-08) closed both:

- **Pack codes as well as chars?** Yes — one of them does exactly that, so the
  `arity_compat` requirement (below) does not put us alone in the design space.
- **Recognize text at runtime?** No — neither packs anything except at explicit
  producers.

Both also default `double_quotes` to `chars`, which is decision 4.

## Decision

### 1. A PSTR *is* a list

`is_list/1` true, `compound/1` true, `atomic/1` **false**, `length/2` works,
`==`/`compare/3` compare element by element against a cons list of the same
content. A PSTR and the cons list it denotes are the same term, indistinguishable
by any Prolog observation. The representation is an implementation detail of
*how* the list is stored, never of *what* it is.

The corollary is the rule that governs every future change here: **no predicate
may branch on `Tag.Pstr` to produce a different answer than it would for the
equivalent cons list.** Branching for speed is fine and expected; branching for
semantics is the bug this ADR exists to prevent.

### 2. Both chars and codes are packed

A packed list of one-character atoms and a packed list of codes are both
representable. The presentation travels **with the datum**, in the header cell,
not in a flag consulted at decomposition time.

### 3. Soundness: the flag is read once, at read time

`double_quotes` is a **parse-time** flag. It decides what term a `"..."` literal
denotes at the moment it is read. It has no effect on any term that already
exists. Changing the flag halfway through a file cannot reinterpret PSTRs built
before it.

This is a correction, not a refinement: `pstr-design.md` specified the opposite
("PSTR `[H|T]` decomposition produces H as... depends on the flag"), which makes
the value of an existing term depend on mutable global state. Under it,
`X = "abc"` followed by a flag change would silently change what `X` is.

### 4. `double_quotes = chars` by default

Matching the engines surveyed above, and the direction the modern ecosystem
went. The ISO default of `codes` remains available and is what `arity_compat`
selects.

### 5. `string` is a compatibility alias, not a type

`double_quotes = string` produces a packed list of **chars**. There is no opaque
string type. `Tag.String` (0x8) is deleted — it has no producer in `src/`.

`string/1` survives as a **content** test: a non-empty proper list of characters
or of codes. Testing the tag would answer differently for a packed list and the
cons list it denotes, which are the same term — exactly the representation probe
decision 1 exists to prevent. The divergence from SWI is deliberate and narrow:
`string([a,b,c])` is true here and false there, because here it is the same term
as `"abc"` and no other answer is available. `string("")` is false — the empty
literal denotes `[]`, which is an atom.

### 6. The representation is not observable at the .NET boundary

Text-as-value is an atom and crosses as `string`. Text-as-sequence is a list and
crosses as a list — whether or not it happens to be packed. A C# method called
with a packed list and with the equivalent cons list must receive the same
thing; otherwise the two are not interchangeable at the boundary, which would
make the representation observable through the back door.

`Term.TryAsText` exists for reading text cheaply without materializing nodes, as
an *optimization the caller opts into*, not as a different type.

### 6b. …except at the zero-copy tier, where there is no boundary

Decision 6 governs the three tiers that *have* a boundary: typed foreign
predicates, typed queries, and the `Term` tree. The fourth tier — a raw
`bool(Activation)` foreign that reads and writes the live `Cell[]` — exists
precisely so that there is nothing in between, and it is where the engine wins
3–180× over a P/Invoke-embedded native Prolog. It cannot hide the
representation, and pretending otherwise would be worse than useless: code
written against a hidden model that turns out to be wrong is exactly how a
silent defect gets in.

So at that tier the contract is not "you cannot see it". It is:

> **Do not hand-roll the list walk.** Peel elements with
> `Activation.TryUnconsListLike` and classify with `Activation.IsListLike`,
> which handle a cons cell and a packed list identically and cost the same as
> the tag test they replace on the cons path.

The failure mode this prevents is the bad kind. A hand-written
`while (c.Tag == Tag.Lis)` over a packed list does not throw and does not fail —
it *exits immediately* and computes an answer over zero elements. The same
predicate called from two Prolog call sites then returns two different answers
for lists that are `==`, which is decision 1 broken from the outside.

Building is unaffected: cons cells remain valid, always, and nothing at this
tier is ever obliged to produce packed text. Producing it is an optimization,
available through `MakePstr`.

### 7. The writer decides by value, never by representation

`write/1` of a packed list and of the equivalent cons list must produce
identical output. The default is the ISO list form; the `"abc"` form is opt-in
through a `write_term/2` option, which the top level enables when displaying
answers. Two terms that are `==` must print identically.

### 8. Packing happens at explicit producers only

There is no runtime recognizer: no code path scans a list to discover that it is
text and pack it. Packing happens where text *enters* the engine — literals at
compile time, and the runtime text producers (`atom_codes/2`, `atom_chars/2`,
`number_codes/2`, `number_chars/2`, `name/2`, `split_string/4`, the stream text
readers, `format/3` to a list) through a single helper.

Three reasons, in order of weight:

- A recognizer would depend on allocation order, so two identical programs could
  differ in cell count and in what structure they share.
- It costs an O(n) scan on every list construction in a language where most
  lists are not text.
- It breaks "a PSTR is a value, never mutated in place", which GC relocation and
  the trail both depend on.

The engines surveyed pack at explicit producers as well.

## The encoding

60-bit payload, header cell:

```
bit  59     : presentation   1 = chars, 0 = codes
bits 58..32 : length         27 bits (~268 MB of text per PSTR)
bits 31..2  : bufferIdx      30 bits   UNCHANGED
bits  1..0  : offset         0..2      UNCHANGED
```

**The bit goes in 59, not in 32.** That way the only thing that changes is the
mask in `AsPstrLength`; `AsPstrBufferIndex` and `AsPstrOffset` are byte for byte
what they were, and with them `GetPstrCodeUnit`, `ComputePstrTailIndex` and the
whole of the GC's PSTR marking and relocation. Losing the bit in `Relocate`
would turn a list of chars into a list of codes *during a collection* —
non-deterministic, reproducible only under memory pressure — so the encoding is
chosen to keep that code path untouched.

Buffer cells (`Tag.PstrBuffer`, 0xC) are unchanged: 3 UTF-16 code units at bits
47..32, 31..16 and 15..0.

`enum TextKind { Codes = 0, Chars = 1 }` lives in `Shumway.Core` and is threaded
through every producer and every consumer.

### The bytecode does not change

Literal pools are `LiteralPool<T>`, append-only with stable ids. **Keying the
string pool by `(Text, Kind)`** gives `"abc"`-as-chars and `"abc"`-as-codes
distinct ids without touching `get_pstr`/`put_pstr`, without a new opcode,
without changing operand widths, and leaving
`IlRuntimeHelpers.GetPstr/PutPstr(Activation, int, int)` with the signature it
has — so bundles with persisted IL keep linking. Only the `.shmo`/`.shum` string
table gains a kind byte.

This is the load-bearing choice of the whole encoding: it is what keeps a change
to the fundamental representation of text out of the instruction set.

### Mixed chains

Lazy concatenation puts a PSTR in the tail of a PSTR. A chars PSTR whose tail is
a codes PSTR is the perfectly legal list `[a,b,c,97,98]`. Every chain walker
must therefore stop at a kind change rather than assume the chain is uniform,
and the concatenation constructor requires equal kinds. Missing one of these is
a silently wrong answer, not a crash — which is why they are enumerated in the
design doc and covered by a dedicated test.

### Atom pressure

With `chars` as the default, every uncons interns a one-character atom; a DCG
over 1 MB would intern a million. Latin-1 character atoms are pre-interned
permanently and served from an O(1) array.

## Observability: report the cost, not the encoding

Decisions 1 and 7 make the representation invisible to a program. That leaves a
real need unmet: someone debugging a memory problem legitimately wants to know
whether their lists are being stored efficiently.

Surveying the two engines that pack strings (2026-08), the precedent points in
two directions. One offers no storage test at all: the predicate that looks like
one is a *content* test, and answers true for a cons-built list of characters —
the equivalent of our `'$is_char_list'/1`. The other does expose the storage,
as a type test, and its own library code branches on it.

What decides it here is decision 1. Once a boolean distinguishes two lists that
unify and are `==`, it becomes something programs can branch on, and decision 1
stops being enforceable from outside the engine. So we answer the question the
user actually has, which is not "is this packed?" but "what is this costing
me?":

- **`term_cells/2`** — the number of heap cells reachable from a term, counting
  shared substructure once. It gives a comparable number (the 8001 against 1337
  that motivated this arc) rather than a boolean from which the cost has to be
  inferred, and it works for every term, not only for text.
- **A packed-text line in `statistics/0`**, which already reports heap cells in
  use.

**No boolean probe is added.** The decisive property of `term_cells/2` is that
there is nothing to branch on: no library will ever write
`( packed(X) -> ... ; ... )`, because the predicate does not answer that
question. Reporting resource usage is already non-logical territory —
`statistics/2` lives there — and it is the right neighbourhood; a type test is
not.

At the zero-copy tier the representation is visible anyway (decision 6b), so a
C# host that needs the distinction has it in the cell tag, where it belongs.

## Alternatives considered

### Raw bytes inline in the cell array

**Rejected.** It requires that cells are never relocated. We have a compacting
heap GC (ADR-016), and a partially-filled tail cell of raw UTF-8 has no valid
tag, so the heap walk could not step over it. The engines that use this layout
do not relocate cells.

### A refcounted pointer in the cell

**Rejected.** It puts a managed reference inside a cell, which ADR-002 forbids
precisely so the .NET GC never scans millions of cells. It is available to an
engine that reclaims the block by reference count instead.

### A runtime recognizer that packs any list that turns out to be text

**Rejected** — see decision 8.

### An opaque string type alongside the list

**Rejected.** It is what we had. It gives every text predicate two cases to get
right, and it is exactly the split that produced the five defects. The
compatibility need is met by the shim.

### Reading the flag at decomposition time

**Rejected** — see decision 3. This is what the original design specified and it
is unsound.

### Packing chars only

**Rejected.** `arity_compat` needs codes, and DCG parsing under that dialect
should be as cheap as under the modern one. Packing both is known to be
workable.

## Consequences

### Positive

- The five defects cannot recur: they were all instances of "PSTR is not a
  list", and there is no longer a second view to drift from.
- Text produced by the engine costs `n/3` cells instead of `2n+1` — the measured
  8001 -> 1337 for a 4000-character atom, at the producers, which is where the
  ratio is actually paid.
- `double_quotes = chars` becomes free, so the default can match the ecosystem.
- The writer, the type predicates and the boundary all get simpler, because each
  has one case instead of two.
- Lazy stream reading (`phrase_from_file/2,3`) becomes possible: a PSTR with an
  open tail is precisely the shape it needs, and we already have `freeze/2` and
  attributed variables.

### Negative

- **The standard order changes.** A PSTR moves into the compound bucket and
  compares as `'.'/2`. Programs that sort text together with other term types
  see a different order. This is the change with the widest blast radius, and it
  is a correction: the previous behaviour also made any two PSTRs compare equal
  without looking at their contents.
- `string/1` becomes false in the bare engine; SWI libraries that use it need the
  shim.
- `UnifyList` gains a branch on a hot path, mitigated by mirroring `GetList`'s
  split into an inlined fast path and a cold `NoInlining` body.
- Atom-table pressure under `chars`, mitigated by the Latin-1 pre-intern.

### Deliberately not addressed

- **Surrogate pairs.** The current code treats a lone code unit as a character.
  Astral-plane correctness is a separate arc with its own design.
- **`mmap`-backed buffers** — a stream option under which a single cell aliases
  an entire file. Attractive for `phrase_from_file/2` over huge inputs,
  but it needs a PSTR whose buffer lives outside the heap, which collides with
  relocation in *our* GC and with pinning in .NET's. Weighed against the
  `freeze/2` window when lazy reading is implemented.

## Test strategy

- The five defect queries, verbatim, as pins (`PstrListSemanticsTests`).
- GC round-trip with both kinds live — a lost presentation bit in `Relocate` is
  the highest-severity failure this design admits.
- A mixed chain (chars then codes) compared against the equivalent cons list
  under `==`, `compare/3`, `length/2` and `write/1`.
- Every reflected `MethodInfo` in `IlPredicateCompiler` resolves non-null — a
  `#if DEBUG`-only `MethodInfo` once broke Release IL silently.
- `--alloc` over the benchmark suite must show the producer win and must not
  regress Van Roy; wall-clock A/B back to back.
- Full gate plus the Neumerkel conformity suite and the Scryer/SWI/Logtalk
  library campaigns — `length/2`, `append/3` and `msort/2` are everywhere in
  them, so they are the real net for decision 1.

## Related ADRs

- ADR-002 (Cell Layout): the tag table and the no-managed-reference invariant;
  amended here for the header split and the removal of `Tag.String`.
- ADR-016 (Heap GC): why the buffer must be made of cells.
- ADR-010 (Embedding API): amended here for decision 6.
- ADR-040 (Multi-dialect shims): where `string/1` and SWI's text behaviour live.

## Related design docs

- `design/pstr-design.md`: the complete specification, rewritten alongside this
  ADR.
- `design/cell-layout-detail.md`: bit-level cell reference.
