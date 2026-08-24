# Unicode `full` (astral planes) — viability study

Status: **study only** (branch `astral-unicode`, 2026-08-24). No code. The
outcome decides whether the arc runs, and whether before or after v1.0.0.

---

## 1. Where the engine stands today (measured on this build)

An astral code point (anything above U+FFFF: emoji, CJK Extension B+,
mathematical alphanumerics, historical scripts, musical notation) **passes
through the engine opaquely** — the lexer, atom table, writer and streams
carry it intact inside an atom, because .NET strings simply carry its two
UTF-16 units. The moment any *character-level* operation touches it, the
engine either answers in UTF-16 units or refuses:

| Operation | Today, on `'😀ok'` (U+1F600) | A code-point engine says |
|---|---|---|
| `atom_length/2` | `4` | `3` |
| `atom_chars/2` | `['\uD83D', '\uDE00', o, k]` — two *lone surrogate* "chars", not valid characters | `['😀', o, k]` |
| `atom_codes/2` | `[55357, 56832, 111, 107]` | `[128512, 111, 107]` |
| `char_code(C, 128512)` | `representation_error(character_code)` | `C = '😀'` |
| `sub_atom/5` | offsets and slices in units; the 1-char prefix is half a surrogate | code-point offsets |
| `get_char/2` | **two** reads, each half a surrogate | one read |
| builders from codes > 0xFFFF | `representation_error` (deliberate guard, 2026-08 — before that they truncated *silently* to a different character) | build the character |

The guard work of 2026-08 turned every silent lie on the *construction*
side into a loud error, and `BmpCharacterCodeTests` pins that. The
*decomposition* side (length/chars/codes/sub_atom/get_char) still answers
in units — those are the remaining lies.

**The precise boundary is granularity, not random access** — *the atom as
a value is sound; the atom as a sequence lies*:

- Sound (whole-atom granularity): unification, `==`, keys/hashing,
  `write`/`writeq`/`print` and read-back (byte-verified UTF-8 round
  trip), `atom_concat` in construction mode, `upcase_atom`/
  `downcase_atom` (string-to-string — measured: `'𐐨b'` upcases to
  `'𐐀B'`, the astral Deseret pair mapped correctly), and the .NET
  embedding boundary.
- Lying (character granularity), all with the same root cause: point
  access (`sub_atom` offsets), traversal (`atom_chars`/`atom_codes`,
  `get_char`, therefore any DCG over the text), counting
  (`atom_length`), construction from codes (the guarded errors),
  **cut-point enumeration** — `atom_concat(P, S, '😀x')` enumerates 4
  splits instead of 3, manufacturing two lone-surrogate atoms at the
  mid-pair cut (measured) — and the **standard order**: unit-wise
  comparison sorts astral-bearing atoms *before* U+E000–U+FFFF ones
  (high surrogates D800–DBFF < E000), so a mixed `sort/2` is not in
  code-point order. Only the relative order is affected.

The Logtalk adapter declares `unicode, unsupported` — deliberately, because
its test generator branches only on unsupported-vs-anything, and declaring
`bmp` opts into `unicode_full` charsets that generate astrals (measured:
`bmp` → arbitrary 40/43). Only JIProlog and Tau declare `bmp`; it is a path
nobody walks.

## 2. Why it would matter (and how much)

- **ISO semantics**: a Prolog character is one code point. Every
  neighbouring engine answers the table above the code-point way — Scryer
  (Rust `char` *is* a code point), Trealla (UTF-8 with code-point
  iteration), SWI (wide-atom internals). We are the odd one out at the
  character level.
- **Real data**: emoji in JSON/YAML/CSV payloads, CJK-Extension names,
  `𝒶`-style math identifiers (Trealla's tests 0556/0779 use exactly
  these). A DCG over such data cannot be written correctly today: the
  grammar sees half-surrogates.
- **Campaign yield**: Logtalk `arbitrary` 43/43 with flag `full` (today
  skipped), one `yaml` test, the Trealla astral family.

**The honest counterweight**: pass-through already works, so only programs
doing character-level surgery *on astral-bearing text* hit the gap — and
since the guard, they hit an error, not a wrong answer. No user or
campaign is currently blocked by it; the pressure is conformance and
data-robustness, not a bug backlog.

## 3. The .NET falencia, precisely

.NET's native text unit is the **UTF-16 code unit**, not the code point:

- `string.Length`, `s[i]`, `Substring`, `IndexOf` all count units. An
  astral character occupies two (`char.IsSurrogatePair`).
- There is **no O(1) code-point indexing** over a UTF-16 string, by
  construction. Any code-point-correct `atom_length`/`sub_atom` over an
  astral-bearing atom is O(n) in units or needs a precomputed side index.
- What the BCL offers: `char.ConvertFromUtf32`/`ConvertToUtf32`/
  `IsSurrogatePair` (available on net48 too — the engine's three existing
  uses are exactly these), `System.Text.Rune` (clean code-point iteration,
  **absent on .NET Framework 4.8** — our opt-in target — so the arc would
  hand-roll pair arithmetic; the engine currently has zero Rune uses), and
  `StringInfo` (grapheme *clusters* — more than we need, and allocating).

So the platform doesn't make it impossible — it makes the ISO character a
**per-operation decision** instead of the representation's native unit,
which is why Rust/C engines get this for free and we don't. Switching the
internal representation (UTF-32 atoms, or UTF-8) would fix the unit
mismatch at the root but was considered and rejected: it doubles memory or
taxes every .NET-string interop point, forces a PSTR redesign (ADR-047
packs UTF-16 units 3-per-cell), and rewrites the whole text surface for a
0.1% case.

## 4. The candidate design (from the 2026-08 sketch, unchanged)

**A per-atom "all-BMP" flag, computed at intern time** — the intern already
scans the name while copying, so the flag is free to compute.

- BMP atom (≥99.9% of real programs): every builtin takes the *current*
  code path, unchanged, O(1) where it is O(1) today. Cost: one predicted
  branch per operation.
- Astral-bearing atom: the operation takes a slow path that walks by code
  points (`ConvertToUtf32` over pairs).

Surface to convert (inventory): `atom_length`, `atom_chars`/`atom_codes`
(both directions), `char_code`, `sub_atom` (its *offsets* change meaning —
code points, not units), `get_char`/`peek_char`/`put_char` (the strict
`Utf8TextReader` already decodes full code points and splits them into a
surrogate pair — the stream char layer would re-join them, so the decode
work is conveniently already done), the lexer (`0'c`, quoted atoms), PSTR
(packs UTF-16 units; the chars-presentation uncons must yield one CHAR per
code point — its walkers span four Core files), and the **standard order
of terms** (unit-wise comparison misorders astrals against U+E000–U+FFFF;
guaranteeing code-point order is an observable `sort/2` change).

Hot-path discipline: `sub_atom`, `atom_length` and the PSTR uncons are the
grammar-processing core — the flag branch must be A/B'd back-to-back
before the design is fixed (the `MaybeCollectHeap` deadline check went
through the same gate and measured as noise; this is the same shape of
cost, but on hotter paths).

## 5. Options on the table

| | What | Cost | Buys |
|---|---|---|---|
| **A** | The per-atom flag arc (§4) | A mid-size arc: ~10 builtin families + lexer + PSTR + order review + A/B + the netfx lane; comparable to half the PSTR arc | ISO code-point semantics, Logtalk `full`, the astral test family, emoji-robust data processing |
| **B** | Ship v1.0.0 as **BMP-complete** (status quo) | Zero | Honest position: strict errors instead of silent lies (already shipped), documented flag `unsupported`; revisit post-1.0 |
| **C** | Lexer-only: BMP letter classification for identifiers (`Ö`, `Œ` — Trealla 0252) | Small, independent of astrals | Latin-1/BMP identifiers parse; no character-level change |
| **D** | Code-point-native representation | Rejected (see §3) | — |

## 6. Recommendation

**B for v1.0.0, A as the first post-1.0 arc, C opportunistically.**

The guard work already bought the defensible half: the engine never lies
about astral text anymore — it refuses loudly, which is a legitimate
"BMP-complete" position (better than Tau/JIProlog's `bmp`, the only other
declarers). Option A is well-designed and viable — nothing in §3 blocks
it — but it is a real arc touching the hottest text paths right before a
release, and its yield is conformance breadth rather than a blocking
defect. Running it as the first post-1.0 arc keeps v1 on schedule and
gives the A/B gate room to breathe. C is small enough to slip into any
round if BMP identifiers are wanted sooner.
