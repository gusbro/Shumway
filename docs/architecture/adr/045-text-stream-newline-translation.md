# ADR-045: text streams translate the platform newline; binary streams never do

## Status

Accepted (2026-08-14; ships with 0.9.0).

Lands with [ADR-044](044-canonical-path-separator.md)
before any release, so no published version changes behavior.

## Context

A Windows text file ends its lines with CR-LF. Shumway read those two bytes
back as two characters, so a program reading a file written on Windows saw a
`\r` before every `\n`:

```
?- open('crlf.txt', read, S), get_char(S, _), get_char(S, _),
   get_char(S, _), get_char(S, C4).
C4 = '\r'                                  % expected '\n'
```

Every other engine in our reference set hides that byte. This is C stdio's
*text mode*, which GNU Prolog inherits directly and SWI implements itself:
on Windows a CR-LF pair reads as the single character `\n`; on Unix there is
nothing to translate because the external form already is `\n`.

The divergence surfaced from Logtalk's own test suite, which branches its
expectations on `prolog_dialect`. Announcing ourselves as `gnu`
([the adapter decision](../../guide/logtalk.md)) means we are held to GNU's
answer, and we were failing it:

```prolog
:- if(( \+ dialect(b), \+ dialect(gnu), \+ dialect(ji),
        \+ dialect(sicstus), \+ dialect(swi), \+ dialect(xsb) )).
    test(reader_file_to_codes_2_02, true(Codes == [97,98,99,13,10|_])).   % CR-LF
:- else.
    test(reader_file_to_codes_2_02, true(Codes == [97,98,99,10|_])).      % LF
:- endif.
```

Ten failures across `reader` (8), `csv` (1) and `yaml` (1) were this one
defect. It was also suppressing whole test objects, not merely failing
assertions: `yaml`'s 55-test object aborted before printing a summary,
so the suite under-reported its own size.

ISO 13211-1 supports the translation rather than forbidding it. A text
stream is defined as a sequence of characters organized into lines; the
mapping between that sequence and the file's external representation is
explicitly implementation-defined. A binary stream is defined as a sequence
of bytes, which leaves no such latitude.

## Decision

**A text-mode read collapses a CR-LF pair into the single character `\n` on
Windows. A binary-mode read never alters a byte.** Both halves matter: the
rule is what the ISO text/binary distinction is *for*.

Three details fix the edges:

1. **Only the pair is a line terminator.** A lone CR is data and passes
   through unchanged, so a classic-Mac file reads exactly as it did before.
   This is C stdio's rule, not a simplification of it.

2. **The translation is Windows-only**, matching GNU on *both* platforms: a
   CR-LF file read under Linux yields CR-LF there too, because that is what
   GNU does there. Translating everywhere would be more uniform but would
   make us differ from the engine we are matching.

3. **Writes are untouched.** They already emit `\n` (`StreamWriter { NewLine
   = "\n" }`), which a previous round established as GNU parity by
   measurement. This ADR is about reads only; the two directions are
   deliberately not symmetric, and that asymmetry is GNU's.

## Implementation

One place: `PositionTrackingReader` in `src/Shumway.Core/StreamHandle.cs`.
Every text read handle is already wrapped in it by `StreamHandle`'s
`TextReader` constructor, and binary handles hold a raw `Stream` with no
reader at all — so "text converts, binary does not" is structural here
rather than a rule each builtin has to remember.

`Peek` consumes only when it sees a CR. Any other character is answered
straight from the inner reader, which preserves the cheap
"is input available" probe that the REPL's reader and
`at_end_of_stream/0,1` depend on. End-of-input is deliberately not buffered:
a reader like the REPL's may answer -1 now and yield more once the user
types.

A second family of readers takes the same rule for consistency rather than
for a behavior fix. `consult/1`, `:- include/1`, the offline compilers and
`shumway-disasm` do not go through a stream handle at all — they slurp the
file with `File.ReadAllText` — and now go through
`Shumway.Core.TextFile.ReadAllText` instead.

Nothing observable changes at the *parser* level, and it is worth being
precise about why rather than claiming a fix: a raw newline inside a quoted
atom is an ISO error whichever bytes it is made of, the lexer already
recognised CR-LF after a line-continuation backslash, and everywhere else CR
is layout. What the change does buy is that a source file has one reading
across both load routes no matter what the lexer's rules become, and that
the source text embedded in a `.shmo` / `.shum` no longer depends on the
line endings of the machine that compiled it.

A source string handed to `ConsultString` in memory is left exactly as the
caller wrote it: a string is not a file, and this ADR is about the external
representation.

Positions stay self-consistent because they are logical, not byte offsets:
a CR-LF advances `position(N)` by one, and `set_stream_position/2` re-consumes
`N` characters through the same translation.

## Consequences

- The ten Logtalk failures are gone; `reader` 64/64, `csv` 35/35,
  `yaml` 55/55 + 20/28 (8 skipped by the suite itself).
- A program that genuinely wants the bytes asks for them — `type(binary)`,
  which is the ISO-sanctioned way to say so.
- Text reads on Windows no longer take the bulk `Read(char[], …)` fast path;
  they go character-at-a-time through the buffered inner reader. The affected
  callers (`get_char/1,2`, `read_term/2,3`) were already character-at-a-time,
  and `consult/1` does not use this path.

## Alternatives rejected

**Translate on every platform.** More uniform for portable code, and
defensible — but it makes a CR-LF file read differently under Shumway and
GNU on Linux. We are matching a reference implementation; matching it only
where convenient is worse than not matching it.

**A per-stream `newline(…)` open option, defaulting to no translation.**
Keeps byte-faithfulness as the default and makes the behavior explicit.
Rejected because it leaves the default wrong: portable code that never heard
of the option is exactly the code that breaks. The option remains available
later as an override if a real need appears.

**Fix it in the Logtalk adapter.** It is not a Logtalk problem — Logtalk
merely detected it. Any program reading a Windows text file hit it.
