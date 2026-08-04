# Bundle format (`.shum`)

The on-disk format of a Shumway bundle. `src/Shumway.Embedding/BundleFormat.cs`
is authoritative (it carries the layout as a doc-comment); `BundleWriter` /
`BundleReader` are the writer and reader. This complements ADR-009.

## Framing

Little-endian throughout. A bundle is a small fixed header followed by a body
that is either raw or a single Brotli stream:

```
[0..3]   Magic 'S' 'H' 'U' 'M'
[4..7]   Format version (uint32) — CurrentVersion
[8]      Compression flag: 0 = raw body, 1 = the whole body is ONE Brotli stream
[9..]    Body (raw, or Brotli-decompressed)
```

Compression is whole-body, not per-entry, because the redundancy is
cross-entry (shared atom names, repeated opcode patterns across modules), and
the reader is sequential anyway. Bodies below `CompressionThresholdBytes`
(4 KB) are stored raw. Decompression happens once, at `LoadBundle`; the runtime
pays nothing after that.

## Body

```
Module count (uint32)
then, per module:
  nameLength        uint32,  nameBytes        utf-8
  sourceLength      uint32,  sourceBytes      utf-8   (empty when source-stripped)
  compiledLength    uint32,  compiledBytes            (CompiledModuleCodec; 0 = none)
  compiledIlLength  uint32,  compiledIlBytes          (PersistedAssemblyBuilder .dll; 0 = none)
  definedCount      uint32,  definedEntries { name:string, arity:uint32, vis:byte }*
  ilPatchLength     uint32,  ilPatchBytes             (persisted-IL sentinel patch table; 0 = no IL)
  ilEntriesLength   uint32,  ilEntriesBytes           (per-method name/arity/slot)
  dynamicSeedCount  uint32,  seeds { name:string, arity:uint32, clause:TermCodec }*
```

- **`definedEntries`** enable the source-less `LoadBundle` path: a Release
  bundle can dispatch with no Prolog source present.
- **`compiledBytes`** is a per-module WAM-bytecode blob (`CompiledModuleCodec`);
  **`compiledIlBytes`** is a persisted Tier-1 IL assembly
  (`--with-compiled-il`), rewritten to runtime ids at load through the
  **`ilPatchBytes`** sentinel table (ADR-017 name-relative persisted IL).
- **`dynamicSeeds`** carry the clauses of `:- dynamic foo/N.` predicates
  (`TermCodec`-encoded) so a dynamic predicate with source clauses dispatches
  from a bundle exactly as after a consult.

Beyond the module list the body also carries any archive members
(`shumway-lib` packs verbatim `.shmo` images), foreign / native library names
(`--foreign-dll` / `--native-dll`, auto-loaded at `LoadBundle`), and, for a
saved state, a snapshot trailer. See `BundleFormat.cs` for the exact trailer
order.

## Versioning

The format is **frozen pre-release**: `BundleReader` requires **exactly**
`BundleFormat.CurrentVersion` and rejects anything else — there is no supported
version range and no backward-compatibility path until the first public
release. Bumping the layout means bumping `CurrentVersion`; do not add
`version >=` conditionals to the reader.
