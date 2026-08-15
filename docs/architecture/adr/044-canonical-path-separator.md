# ADR-044: canonical path separator (`/` everywhere Prolog can see)

## Status

Accepted (2026-08-14). Landing before 1.0.0 reaches `main`, so no released
version changes behavior: the version bump and this convention ship together.

## Context

Shumway's path-producing builtins returned the host's native form. On
Windows that means backslashes:

```
?- working_directory(D, D).            D = 'C:\temp\'
?- absolute_file_name('x.pl', A).      A = 'C:\temp\x.pl'
```

Three separate problems turned out to be this one problem:

1. **The output does not survive being written back as a Prolog term.**
   Backslash is the escape character inside a quoted atom, so `'C:\temp\x'`
   is malformed and `'C:\t'` is a tab. Twenty-three of our own test files
   carry a `Replace("\\", "\\\\")` (or `Replace('\\', '/')`) purely to build
   a query out of a path. Any user program that assembles paths with
   `atom_concat/3` or takes them apart with `sub_atom/5` hits the same wall.

2. **Portable Prolog code cannot treat a path as data.** The same program
   that splits a path on `/` under Linux has to split on `\` under Windows,
   for no reason intrinsic to the program.

3. **The ecosystem already agreed on `/`, and we were the outlier.**
   Measured, not assumed:

   | engine | `working_directory` | `absolute_file_name` |
   |---|---|---|
   | SWI-Prolog 10 (Windows) | `c:/temp/` | `c:/temp/x.pl` |
   | GNU Prolog 1.5 (Windows) | forward-slash form | forward-slash form |
   | Shumway (before this ADR) | `C:\temp\` | `C:\temp\x.pl` |

   SWI additionally exposes `prolog_to_os_filename/2` for the conversion
   back. Logtalk's `library(os)` is built on the same assumption: it carries
   an `internal_os_path/2` conversion, and its GNU arm does no conversion at
   all because GNU is already forward-slash. Running Logtalk on Shumway
   needed a Shumway-specific arm largely because of this mismatch.

## Decision

**`/` is Shumway's canonical path separator on every platform, in every
Prolog-visible position.** The native separator is an operating-system
detail handled at the .NET boundary, not a value the Prolog program sees.

### 1. Output — always canonical

Every builtin that PRODUCES a path yields `/`-separated text:
`working_directory/2`, `absolute_file_name/2`,
`prolog_load_context(directory, _)` and `prolog_load_context(file, _)`, the
`file_name(_)` of `stream_property/2`, and `current_stream/3`'s filename.
(`directory_files/2` is unaffected: it answers with entry NAMES, not paths.)

An error term's culprit is NOT canonicalized: ISO says the culprit is the
offending argument, so it echoes exactly what the caller passed — rewriting
it would misreport the call.

### 2. Input — both forms accepted, and NEVER rewritten on Unix

A path argument may use `/` or, on Windows, `\`; both reach the same file
(the Win32 API accepts either, and .NET passes them through).

On Unix the backslash is a legal filename character, so input is passed
through verbatim there — translating it would make a legitimate file name
unreachable. The translation is Windows-only and one-directional
(native → canonical on the way out).

### 3. Windows special prefixes are left alone

UNC (`\\server\share`), device (`\\.\nul`) and extended-length (`\\?\...`)
paths keep their backslashes on output. Their prefixes are part of a Win32
naming syntax rather than a separator convention, and rewriting them can
change which object is named.

### 4. `prolog_to_os_filename/2` for the way back

```prolog
?- absolute_file_name('x.pl', A), prolog_to_os_filename(A, OS).
A = 'C:/temp/x.pl', OS = 'C:\\temp\\x.pl'.
```

SWI's name and semantics, because that is what portable code (Logtalk's
`library(os)` among it) already calls. On Unix it is the identity.

### 5. A directory ends with `/`

`working_directory/2` returns `C:/temp/`; `absolute_file_name/2` of a
directory returns `C:/temp/`. Before this ADR the two disagreed
(`C:\temp\` versus `C:\temp`), which is its own bug.

## Consequences

- Path text produced by Shumway can be written and read back as an ordinary
  quoted atom with no escaping. The `Replace("\\", "\\\\")` scaffolding in
  tests and examples goes away.
- Programs that compose or decompose paths behave identically on Windows
  and Unix.
- Third-party Prolog (SWI's and Scryer's libraries, Logtalk's `library(os)`)
  gets the path shape it was written against.
- Programs that pinned the Windows form of Shumway's output change. Since
  1.0.0 has not been released, no published behavior is broken; after 1.0.0
  a change of this kind would need the release-compatibility treatment.
- Passing a canonical path to an external tool that insists on backslashes
  (a `.bat`, a native DLL) needs `prolog_to_os_filename/2` — the reason it
  exists.

## Alternatives rejected

**Keep the native separator.** Matches the host shell, and nothing to
implement. Rejected: it is precisely what makes path text non-re-readable
as a term, and it leaves us the only engine of the four measured that does
it.

**Canonical `/` but also translate on input under Unix.** Symmetric and
tidy-looking. Rejected: a backslash is a legal character in a Unix file
name, so the translation would make some files unreachable — a correctness
loss for a cosmetic gain.

**A prolog flag to choose the convention.** Rejected: path shape would then
vary between programs on one engine, so library code could not rely on
either form — the worst of both. The single escape hatch is a conversion
predicate, not a mode.
