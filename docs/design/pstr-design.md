# PSTR: packed lists of text

A PSTR is a **list**, stored packed. It is not a string type, and nothing that
observes a term may tell it apart from the cons list it denotes. That rule, its
rationale and the alternatives that were rejected are
[ADR-047](../architecture/adr/047-pstr-is-a-list.md); this document is the
implementation specification.

## Motivation

A list of `n` characters in cons form costs `2n + 1` heap cells: `n` LIS cells,
`n` value cells and the terminal `[]`. For 1 MB of text that is over two million
cells.

Packed, the same list costs `⌈n/3⌉ + 2` — a header, `⌈n/3⌉` buffer cells at 3
UTF-16 code units each, and a tail. Measured on a 4000-character atom: 8001
cells as cons, 1337 packed.

The second win matters more for grammars: **decomposition allocates nothing**.
`[H|T] = Text` does not build a cons cell; `T` is a new header value pointing
into the same buffer one position further along. A DCG walking a megabyte
performs a megabyte of O(1) steps and allocates zero cells for the walk.

## Layout

### Header cell — `Tag.Pstr` (0xB)

```
bits 63..60 : 0xB
bit  59     : presentation   1 = chars, 0 = codes
bits 58..32 : length in UTF-16 code units (27 bits, ~268 M)
bits 31..2  : heap index of the first buffer cell (30 bits)
bits  1..0  : offset within that first buffer cell (0..2)
```

- **Length is in code units, not codepoints.** That is what makes the tail
  position O(1): `tailIndex = bufferIdx + ⌈(length + offset) / 3⌉`. The
  codepoint count needs a walk to find surrogate pairs, and is only paid where
  it is asked for.
- **Offset** lets a PSTR start mid-cell, which is what makes unconsing free.
- **The presentation bit is at 59, not at 32.** Only the mask in
  `AsPstrLength` changes; `AsPstrBufferIndex` and `AsPstrOffset` keep the bit
  positions they have always had, and with them `GetPstrCodeUnitAt`,
  `ComputePstrTailIndex`, and every line of the GC that marks or relocates a
  PSTR. See "Interaction with the heap GC" below for why that is worth
  designing around.

### Buffer cells — `Tag.PstrBuffer` (0xC)

```
bits 63..60 : 0xC
bits 59..48 : reserved, zero
bits 47..32 : code unit 0
bits 31..16 : code unit 1
bits 15..0  : code unit 2
```

Three UTF-16 code units per cell. Four would need 64 payload bits and we have
60; splitting the high bit of each unit into a side table was considered and
rejected as complexity for a 25% memory difference on a representation that is
already 5× better than cons.

The dedicated tag (rather than reusing `Int`) is what lets the heap walk and the
atom GC step over buffer cells without consulting the header that owns them.

### Tail cell

One cell after the buffer. It may be:

| tail | meaning |
|---|---|
| `Atom([])` | a complete proper list |
| `Ref` unbound | a **partial** list — the open tail is what lazy stream reading needs |
| `Lis` | the list continues in cons form (a mixed list) |
| `Pstr` | the list continues packed — lazy concatenation |

## Invariants

1. **A PSTR is a value, never mutated in place.** Every operation that changes
   content produces a new header. GC relocation and the trail both depend on
   this: nothing trails a PSTR header, because nothing writes through one.
2. **A slice is a computed value, not an allocation.** Unconsing produces a
   header with `offset+1`, `length-1` and no heap traffic. A slice is only
   written to the heap when something binds it.
3. **The presentation travels with the datum.** No operation may consult
   `double_quotes` to decide what a PSTR's elements are; the flag decided that
   when the term was read, and it is recorded in bit 59.
4. **A chain may be mixed.** A chars PSTR whose tail is a codes PSTR is the
   perfectly legal list `[a,b,c,97,98]`. Every chain walker must stop at a kind
   change; see "Chain walkers" below.
5. **An empty PSTR is its tail.** A header with length 0 denotes exactly the
   term in its tail cell — usually `[]`. It is normalised away at the resolve
   chokepoints so that no comparison, no type test and no writer has to know
   about it.

## Reading a code unit

```csharp
public int GetPstrCodeUnitAt(int headerIdx, int i)
{
    Cell hdr = _heap[headerIdx];
    int absolute = hdr.AsPstrOffset + i;
    Cell buf = _heap[hdr.AsPstrBufferIndex + absolute / 3];
    return (absolute % 3) switch
    {
        0 => (int)((buf.Data >> 32) & 0xFFFF),
        1 => (int)((buf.Data >> 16) & 0xFFFF),
        _ => (int)(buf.Data & 0xFFFF),
    };
}
```

## Unconsing

The single most important operation, and the one that must have exactly one
implementation. Two cursors reach it — `GetListSlow`, used when a callee head
matches `[H|T]`, and the `unify_list` run an inline list pattern compiles to —
and when they drifted apart, `X = "abc", X = [97,98,99]` failed while the same
unification through the other cursor succeeded. Both now call the same helper.

Producing the head:

```
head = kind == Chars ? Cell.Atom(charAtomOf(codepoint))
                     : Cell.Int(codepoint)
```

Producing the tail, given `u` code units consumed:

- if `length - u == 0`, the tail is the PSTR's tail cell;
- otherwise a header with `bufferIdx + (offset+u)/3`, offset `(offset+u)%3`,
  length `length-u`, and **the same presentation bit**.

No cell is allocated in either case.

## Unification

| other operand | behaviour |
|---|---|
| `Pstr` | lengths and presentation must agree, then code units pairwise, then unify the tails. Same buffer and same offset short-circuits the content compare. |
| `Lis` | uncons the PSTR, unify head against head and tail against tail. |
| `Atom([])` | succeeds iff the PSTR is empty (invariant 5 normalises this away first). |
| `Ref` | bind by REF, as for any compound (ADR-002 binding policy). |
| anything else | fail. |

**Differing presentation fails.** `[a,b,c]` and `[97,98,99]` are different
lists; they must not unify just because they hold the same text.

## Comparison and structural equality

`==`, `compare/3`, `sort/2`, `msort/2`, `keysort/2`, `setof/3` and `bagof/3` all
route through the same two places, and both must treat a PSTR as `'.'/2`:

- **Type order**: a PSTR sits in the compound bucket, with `Lis` and `Str`. It
  is not a fourth atomic type.
- **Argument access is virtual**: a PSTR's head and tail are *computed*, not
  stored in consecutive heap slots, so the comparator asks for argument `i`
  through an accessor rather than indexing the heap.
- **Mixed pairs descend**: comparing a PSTR against a cons list walks both
  spines through a shared uncons, without caring which side is packed. A
  bulk PSTR-vs-PSTR compare stays as a fast path underneath, not as a
  different answer.

There is no "two PSTRs are equal" shortcut that skips content, and no
`default: return 0` fallthrough anywhere in the comparator — a silent tie is
what let the original defect live.

## Chain walkers

A tail may be another PSTR, so anything that reads a whole PSTR walks a chain.
Every walker must stop when the presentation changes, or it will read a codes
segment as chars and return a **silently wrong list**:

| walker | stops at |
|---|---|
| `ReadPstrChain` | kind change, or a non-PSTR tail |
| `AppendPstrChain` | idem |
| `GetPstrChainLength` | idem |
| `FillCharsFromPstrChain` | idem |
| `ArePstrCodesEqual` | idem |
| `PstrFinalTailCell` | idem |

`MakePstrConcat` requires equal presentation in both operands; it is the only
constructor that can create a chain link, so the mixed case can only arise
through unification binding a tail.

## Where packing happens

By decision, at explicit producers only — there is no runtime recognizer that
scans a list to discover it is text (ADR-047, decision 8).

**Producers:**

- string literals, at compile time, according to `double_quotes` as it stood
  when the clause was read;
- `atom_codes/2`, `atom_chars/2`, `number_codes/2`, `number_chars/2`, `name/2`,
  `split_string/4`, the stream text readers and `format/3` to a list — all
  through one helper, `MakeTextList(string, TextKind)`.

**Explicitly not producers:** `GetList`, `UnifyList`, `Bind`, `CopyLis`,
`HeapTermCopy`, `FindallSnapshot`, `assertz`, and the generic cons loop in the
materializer. They build whatever list they were asked to build.

**Not `phrase/2,3` either.** Converting the input list to a PSTR before feeding
the DCG is a net loss: the cons list already exists, packing it costs an O(n)
walk plus `n/3` new cells while the old `2n+1` are still live, and the DCG then
takes it apart character by character anyway. **The win is in creation, not in
consumption.** `phrase/2,3` only has to *accept* a PSTR, which unification gives
for free.

## Interaction with the heap GC (ADR-016)

The GC is a sliding mark-compact collector, so a PSTR's buffer moves and its
header is rewritten.

- **Marking**: a header keeps its buffer and its tail alive. The buffer cells
  hold character data and contain no references of any kind, so the mark phase
  steps over them without looking inside.
- **Atom GC**: buffer cells hold no atom ids either — under `chars` the atoms
  are created at uncons time, not stored in the buffer.
- **Relocation**: the header is rebuilt with the buffer's new index. This is the
  most dangerous line in the whole design: *dropping the presentation bit here
  turns a list of chars into a list of codes during a collection*, which is
  non-deterministic and only reproducible under memory pressure. Hence the
  layout choice that leaves the index and offset fields exactly where they were,
  and a round-trip test that collects with both kinds live.

## Atom pressure under `chars`

With `chars` as the default, every uncons interns a one-character atom, so a DCG
over 1 MB would intern a million. Latin-1 character atoms are pre-interned
permanently and served from an O(1) array; beyond Latin-1 the normal atom table
and its GC handle it.

## Prolog surface

**There is none, by design.** A PSTR is a list, so the list predicates are its
interface: `length/2`, `append/3`, `msort/2`, `nth0/3` and the rest work on it
because they work on lists. There is deliberately no `is_pstr/1`, no
`pstr_length/2`, no `sub_pstr/5` — every such predicate would be a way for a
program to observe the representation, which is exactly what ADR-047 forbids.

Two exceptions are internal, not user-facing:

- `'$is_char_list'/1` and `'$is_code_list'/1`, used by dialect shims and by
  `format/2` to pick a rendering; they answer about the list's *contents*, which
  is a legitimate question about the term, not about its storage.
- `partial_string/3` (planned, for lazy reading) — builds a PSTR with a given
  tail. It is a constructor, not an observer.

And one **diagnostic**, which reports cost rather than encoding:
`term_cells/2` gives the heap cells reachable from a term, counting shared
substructure once, so someone debugging memory gets a comparable number (8001
against 1337) instead of a boolean. `statistics/0` carries a packed-text line
for the same reason. There is deliberately no boolean probe — see ADR-047,
"Observability".

## C# surface

```csharp
int  MakePstr(string value);                 // build, tail = []
int  MakePstrConcat(int aIdx, int bIdx);     // lazy: b becomes a's tail
string AsPstrString(int headerIdx);          // complete PSTR only
string ReadPstrChain(Cell header, out Cell tail);
int  GetPstrChainLength(int headerIdx);
int  GetPstrTailIndex(int headerIdx);
int  GetPstrCodeUnitAt(int headerIdx, int i);
```

At the embedding boundary a PSTR is a **list** and nothing else: `TermKind.List`,
`IsList` true, enumerable element by element. `Term.TryAsText` reads the text
into a `string` without materializing nodes — an optimization the caller opts
into, not a different type. See ADR-010 as amended.

**The zero-copy tier is the exception**, and necessarily: a raw
`bool(Activation)` foreign reads the live `Cell[]`, so it sees `Tag.Pstr` the
same way it sees `Tag.Str` and ADR-017 inline compounds. The contract there is
that the walk goes through `TryUnconsListLike` / `IsListLike` rather than
`c.Tag == Tag.Lis`, because a hand-rolled cons walk over packed text does not
throw and does not fail — it traverses zero elements and returns a wrong answer.
`docs/guide/interop.md` §4 documents the patterns.

## Bytecode

`GetPstr` (0x50) and `PutPstr` (0x51) take a literal-pool index. **The pool is
keyed by `(Text, Kind)`**, so the presentation costs no operand, no new opcode
and no change of operand width — which is what keeps a change to the fundamental
representation of text out of the instruction set, and keeps bundles with
persisted IL linking.

`UnifyPstrHead` (0x52) is defined and interpreted but **never emitted**. It is
scheduled for removal along with `AdvancePstrHead`, which additionally mutates
the heap without trailing — harmless while nothing emits it, a loaded gun if
anything ever does.

## Lazy stream reading

A PSTR with an unbound tail is exactly the shape `phrase_from_file/2` needs, and
we have `freeze/2` and attributed variables already. The established technique,
in three conceptual lines: the list is a variable with a frozen goal; when the
DCG touches it, read a 4 KB window, build a PSTR whose tail is a **fresh frozen
variable**, recurse. A 1 GB file parses in bounded memory and the DCG never
knows.

What is missing: `partial_string/3`, `get_n_chars/3`, and
`phrase_from_stream/2` + `phrase_from_file/2,3` in the prelude. The alternative
— a single cell aliasing an `mmap`ed file — is weighed in ADR-047.

## Test strategy

- Round-trip: build from a .NET string, read back, compare.
- Empty PSTR decomposes to its tail; `"" == []`.
- 10 K characters, unconsed one at a time, content verified.
- `[H|T] = "abc"` through **both** cursors — the inline pattern and the callee
  head — verifying they agree. This is the shape of the original defect.
- A packed list against the equivalent cons list under `=`, `==`, `compare/3`,
  `length/2`, `msort/2` and `write/1`: identical in every one.
- A mixed chain (chars then codes) against its cons equivalent, same battery.
- GC round-trip with both presentations live.
- `copy_term/2` and `findall/3` over a **partial** PSTR preserve the tail.
- Memory: 1 MB of text occupies under 4 MB of heap.

## See also

- ADR-047: a packed string is a list — the decision and the alternatives.
- ADR-002: cell layout; ADR-016: the heap GC; ADR-010: the embedding boundary.
- `cell-layout-detail.md`: bit-level cell reference.
- `wam-instruction-set.md`: `GetPstr` / `PutPstr`.
