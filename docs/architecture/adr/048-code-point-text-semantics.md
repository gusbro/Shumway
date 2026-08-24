# ADR-048: A Prolog character is a code point

## Status

Accepted and implemented (2026-08, branch `astral-unicode`). This is the
astral-plane arc that ADR-047 explicitly deferred. Whether it ships inside
v1.0.0 is a release decision informed by the measurement in
[`docs/design/unicode-full-viability.md`](../../design/unicode-full-viability.md) §7
(zero measurable cost); the semantics themselves are settled here.

## Context

.NET's native text unit is the UTF-16 code unit; a character above U+FFFF
(emoji, CJK Extension B+, mathematical alphanumerics) occupies two units — a
surrogate pair. The engine carried such text intact as an opaque value, but
every *character-level* operation answered in units: `atom_length('😀ok')`
was 4, `atom_chars/2` produced two lone-surrogate "chars", `atom_concat/3`
enumerated a split point inside the pair, `get_char/2` needed two reads for
one character, and the standard order sorted astral atoms below
U+E000–U+FFFF ones. Construction from an astral code raised
`representation_error` (a 2026-08 guard; before that it silently truncated
to a different character). ISO's model — and Scryer, Trealla, SWI — treat
one character as one code point. The full study, including the option table
and why a code-point-native representation was rejected, is
`docs/design/unicode-full-viability.md`.

## Decision

**Character-level operations answer in code points. The cost is paid only
by text that actually contains a surrogate, detected by flags computed once
at creation:**

1. **Per-atom shape flag.** Every atom is born in `AtomTable.Intern` — the
   single choke point — and its constructor classifies the name once
   (`Utf16Text.Classify`): `Bmp` / `Astral` / `Malformed` (contains a lone
   surrogate). All-BMP atoms (`Atom.IsAllBmp`) keep the exact unit-based
   O(1) paths; astral atoms take code-point walks
   (`sub_atom/5` builds a `CpBounds` boundary table once, then slices O(1)).

2. **Per-PSTR astral bit.** Packed-list headers carry an astral flag at
   payload bit 58, computed while packing (`MakePstr` scans the units it
   copies), inherited conservatively by slices, and preserved by the GC's
   header rebuild — exactly like ADR-047's presentation bit at 59. The
   length field narrows 27 → 26 bits (67M units per PSTR). Uncons joins a
   leading surrogate pair into one element (`PstrHeadCodePoint`): the three
   uncons sites — `TryUnconsListLike`, `UnconsPstrToPair`, `UnifyPstrLis` —
   advance by the character's unit span, so `length/2`, `==`, `compare/3`,
   the writer and every walker built on uncons count characters for free.

3. **Streams.** The strict UTF-8 reader already decoded whole code points
   and split them into pairs; `PositionTrackingReader` re-joins them:
   `ReadCodePoint`/`PeekCodePoint`, with a two-slot pushback so peeking an
   astral character remains a true peek. `put_char`/`put_code` accept any
   scalar value. The lazy-input window reader never ends a chunk between a
   pair's halves.

4. **Lexer and writer.** `0'😀` denotes 0x1F600; a `\x…\` escape above the
   BMP builds the pair (a surrogate escape value is an error — it names no
   character). Extended identifier letters lex like every neighbouring
   engine: a non-ASCII letter starts an unquoted atom, upper/titlecase
   starts a variable, BMP and astral alike; the writer's quoting
   classification mirrors the lexer's exactly, so `writeq` round-trips.

5. **Standard order.** Atom ordering is by code point. All-BMP names (flag
   test, O(1)) keep vectorised ordinal comparison; otherwise
   `Utf16Text.CompareCodePointOrder` applies the standard remap (when the
   differing units are both ≥ 0xD800, surrogates rank above E000–FFFF).

6. **Character codes are Unicode scalar values**: 0..0x10FFFF minus the
   surrogate block. Astral codes build real characters everywhere
   (`char_code/2`, `atom_codes/2`, `number_codes/2`, `put_code/2`, `~c`);
   surrogate values and > 0x10FFFF raise `representation_error` as before.

**Malformed text** (a lone surrogate, manufacturable only from .NET
embedding strings or unit-level slicing of malformed input) unifies and
prints as an opaque value and reads unit-wise at character level — it is
never an occasion to throw from a walker.

## Consequences

- The Logtalk adapter honestly declares `unicode, full`; `arbitrary` runs
  43/43 with the astral-generating charsets and the yaml astral test
  un-skips. Trealla's 0779 (`number_chars` over `"0'𝄞"`) and 0556 (astral
  identifiers, `writeq` unquoted) both match expected output.
- Observable ordering change: `sort/2`/`msort/2`/`compare/3` results move
  for programs mixing astral atoms with U+E000–U+FFFF atoms — the astral
  ones now sort above, per code point.
- Measured cost on BMP workloads: none — `--alloc` byte-identical, wall
  ABBA within noise (study doc §7).
- `Cell.Pstr` requires the astral argument (no default): a slice must state
  what it inherits, a producer what it built — "forgot" is not
  representable.
- `unicode/*` sections of Logtalk's backend battery remain gated on
  `encoding_directive` (reading source files under `:- encoding/1`), which
  stays `unsupported` — a separate feature, deliberately out of this arc.
