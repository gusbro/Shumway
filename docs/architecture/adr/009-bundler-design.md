# ADR-009: Bundler Design

## Status

Accepted (Phase 1 CLI, Phase 2 API) — partially superseded. The `.shum` bundle
format, `LoadBundle` and the bundling API live on and grew (compiled-IL
bundles, archives, snapshots); the `shumway-bundler` CLI described below was
RETIRED in Phase 23, replaced by the separate-compilation toolchain —
`shumway-compile` / `shumway-link` / `shumway-lib` (ADR-038 era, Phase 13+).
The CLI sections stand as the record of the original tool.

## Context

Shumway is intended to power real applications, often shipped as bundles of compiled code rather than as source files plus an engine. A bundle is a self-contained, pre-compiled package of Prolog modules that an application can load and use without parsing or compiling source at startup.

Requirements for the bundler:

1. **Self-contained output**: a bundle file (or set of files) that contains everything needed to execute the included predicates.
2. **Selective inclusion**: not every loaded module needs to be in the bundle. The bundler computes which predicates are reachable from a set of entry points and includes only those.
3. **Validation**: detect issues at bundle time (unresolved references, public-name collisions, missing entry points) before deployment.
4. **Dynamic-predicate awareness**: meta-programming patterns (assertz at runtime, meta-call) cannot be statically traced. The bundler must include enough state to support these patterns.
5. **Versioning**: bundles loaded by future versions of Shumway should be detected and handled (rejected with a clear error, or migrated).
6. **CLI in Phase 1, API in Phase 2**: the bundler is a separate tool first, integrable into build pipelines later.

The bundler's reachability analysis intersects with the module visibility model (ADR-008). Public predicates are entry points by convention; local predicates are included only if reachable from a public predicate.

A subtle but important issue: **dynamic predicates may be modified at runtime** in ways the static analysis cannot trace. For example, an `event_dispatcher` predicate may have its handlers registered via `assertz` at runtime, with the handlers being predicates in some module. The bundler cannot know which predicates will eventually be registered.

The user provided a clear rule for handling this: **all dynamic predicates of any module included in the bundle are themselves included**, regardless of static reachability. This reflects the assumption that if a module is in the bundle, all of its dynamic predicates may be needed.

## Decision

The Shumway bundler is a tool that produces a binary bundle file (`.shum`) from a set of source files and entry points. It performs static analysis with explicit handling of dynamic predicates.

### Inputs and outputs

**Inputs**:

- A set of source files (`.pl`) and/or pre-compiled module files.
- A list of **entry points**: predicates that are guaranteed to be needed (`functor/arity` form). At minimum, one entry point.
- Configuration: output path, debug info level, optional features.

**Outputs**:

- A bundle file containing bytecode, tables, and metadata.
- A report listing included predicates, warnings, and errors.

### Reachability algorithm

The bundler computes the set of predicates to include via a fixed-point iteration:

```
included = empty set
included_modules = empty set
queue = entry_points

while queue is not empty:
    pred = queue.dequeue()
    if pred is already in included: continue
    
    included.add(pred)
    included_modules.add(pred.module)
    
    for each call site in pred's body:
        callee = resolve(call_site)
        if callee != null and callee is static:
            queue.enqueue(callee)
        # Dynamic calls are not traced here; the dynamic rule below handles them.
    
    for each builtin called:
        if builtin requires support code: include it
        (builtins themselves are part of the runtime, not the bundle)

# Apply the dynamic-inclusion rule
for each module in included_modules:
    for each pred in module.AllPredicates:
        if pred.IsDynamic and pred not in included:
            included.add(pred)
            # Process pred's static dependencies too
            for each call site in pred's initial clauses:
                callee = resolve(call_site)
                if callee != null and callee is static and callee not in included:
                    queue.enqueue(callee)

# Re-process the queue if dynamic-inclusion added items
while queue is not empty:
    # Same loop as above; can recursively pull in more modules and their dynamics
    ...

# Result: included is the final set of predicates
```

The key property: **including a dynamic predicate may pull in its module, which may pull in more dynamics, which may pull in more modules, until a fixed point is reached.**

### What goes into a bundle

A bundle file contains:

1. **Header**:
   - Magic bytes: `SHUM` (4 bytes).
   - Format version (4 bytes).
   - Engine version targeted (4 bytes).
   - Flags (4 bytes, reserved).

2. **Atom table snapshot**:
   - All atoms referenced from any included predicate (functor names, constant atoms, etc.).
   - Each atom: id + name string.
   - On load, the engine re-interns these atoms in its global table (ids may differ from the bundle's compile-time ids; the bundle's bytecode is patched accordingly during load, or uses a remapping table).

3. **Functor table snapshot**:
   - All functors referenced (atom id + arity).
   - Same remapping consideration as atoms.

4. **Auxiliary tables**:
   - BigInt literals.
   - String literals.
   - Float literals (for FLOAT cells that need reconstruction at load).

5. **Modules**:
   - Each module's metadata (name, original source file path).
   - Per-module predicate list with visibility and dynamic flags.

6. **Bytecode**:
   - The compiled bytecode of all included predicates.
   - Switch tables for indexing instructions.
   - PSTR-related literals (string buffers, etc.).

7. **Predicate entries**:
   - A table mapping `FunctorId` (in bundle's id space) → bytecode address.
   - Separated by module-local and global-public.

8. **Operator declarations**:
   - All `:- op/3` from included modules, applied globally on load.

9. **Mode declarations** (metadata; the determinism annotations later drove the Phase-3 implicit-cut specialization):
   - `:- mode foo(+, -)` annotations.

10. **Debug info** (optional, configurable level):
    - Source file paths.
    - PC → source location mappings.

11. **Entry points**:
    - The list of public predicates that were specified as entry points to the bundler.

### Validation phases

The bundler runs the following validations:

1. **Parse and compile all source files**. Syntax errors abort.
2. **Public uniqueness**: no two modules declare the same `functor/arity` as public.
3. **Entry points exist**: each entry point in the config resolves to a public predicate.
4. **Reachability analysis**: compute the included set as described above.
5. **Resolve all references**: every call site in an included predicate resolves to:
   - Another included predicate, or
   - A builtin (runtime-provided), or
   - A dynamic predicate (resolution deferred to runtime).
6. **Unresolved references are errors**: any static call to a predicate not in the included set, not a builtin, and not dynamic, is an error.

### Errors and warnings

**Errors** (abort bundling):

- Syntax errors in source files.
- Duplicate public predicate declarations across modules.
- Entry point not found.
- Unresolved static reference (call to a predicate that doesn't exist anywhere).
- Cyclic module reload (not expected in normal use).

**Warnings** (do not abort, but reported):

- Unreachable predicate not in entry points (excluded from bundle).
- Module included only because of the dynamic-inclusion rule (no static path from entry points).
- Meta-call detected (the bundler cannot trace what's called).

### CLI tool: `shumway-bundler`

The Phase 1 CLI tool has the following usage:

```
shumway-bundler [options] <source files...>

Required:
  --entry-points <pred/arity,...>     Comma-separated list of entry point predicates.
  --output <path>                      Output bundle path.

Optional:
  --debug-level <none|basic|full>     Debug info detail. Default: basic.
  --strict-dynamic                     Require explicit :- dynamic declarations.
  --warning-as-error                   Treat warnings as errors.
  --verbose                            Verbose progress output.
  --report <path>                      Write the inclusion/warning report to a file.
  --version                            Print version and exit.
```

Example usage:

```bash
shumway-bundler \
  --entry-points "main/0,handle_request/2" \
  --output app.shum \
  --debug-level basic \
  rules.pl parser.pl utils.pl
```

Exit codes:

- `0`: success (no errors; warnings may have been emitted).
- `1`: errors (bundle not produced).
- `2`: warnings (only with `--warning-as-error`).
- `3`: usage error (bad CLI arguments).

### Bundle loading

The runtime loads a bundle via:

```csharp
engine.LoadBundle("app.shum");
```

Loading steps:

1. **Header check**: magic, version, engine compatibility. Mismatch → error.
2. **Atom table merge**: the bundle's atom ids are remapped to the engine's global atom table. Atoms are interned; mappings stored locally for bytecode patching.
3. **Functor table merge**: similarly.
4. **Auxiliary tables**: loaded into the engine.
5. **Modules**: registered in the engine's module list. Predicate tables populated.
6. **Bytecode patching**: if atom/functor ids differ, the bytecode is patched (or a small remapping table is created for the loaded module).
7. **Operator declarations**: applied globally.
8. **Validation**: ensure no conflict with already-loaded predicates (e.g., another bundle defining the same public predicate). Conflicts are errors.

After loading, the bundle's predicates are immediately callable. Static predicates have direct addresses in bytecode; dynamic predicates use runtime lookup as usual.

### Phase 2: API for build-pipeline integration

In phase 2, the bundler is also exposed as a .NET API for integration with MSBuild, CI/CD, or programmatic use:

```csharp
public class Bundler
{
    public BundleResult Bundle(BundleConfig config);
    public Task<BundleResult> BundleAsync(BundleConfig config);
}

public class BundleConfig
{
    public List<string> SourceFiles { get; set; }
    public List<string> EntryPoints { get; set; }
    public string OutputPath { get; set; }
    public DebugLevel DebugLevel { get; set; }
    public bool StrictDynamic { get; set; }
    // ...
}

public class BundleResult
{
    public bool Success { get; }
    public IReadOnlyList<BundleError> Errors { get; }
    public IReadOnlyList<BundleWarning> Warnings { get; }
    public BundleReport Report { get; }
}
```

The CLI is then a thin wrapper around this API.

### Phase 2: IL-compiled bundles

A bundle can include **pre-compiled IL code** (a `.dll` alongside or embedded in the `.shum` file). This is generated by the IL compiler in phase 2 using `PersistedAssemblyBuilder` (see ADR-011).

A bundle with IL has:

- The standard bundle contents (bytecode + tables).
- An assembly file (`.dll`) containing the IL-compiled forms of included static predicates.
- Metadata mapping each predicate's functor to its method in the assembly.

On load:

- The standard bundle parts are loaded as in phase 1.
- The assembly is loaded via `Assembly.LoadFrom`.
- For each predicate with both bytecode and IL, the engine uses the IL form preferentially.

This eliminates JIT compilation overhead at startup for the main predicates and is the recommended deployment mode for large applications.

## Alternatives Considered

### No dynamic-inclusion rule (strict reachability only)

**Rejected.** Without this rule, `assertz`-based patterns and dynamic dispatch would silently fail at runtime: the dynamic predicate would have no entry in the bundle. The user explicitly identified this as critical for their use cases.

### Include everything from all loaded modules

**Rejected.** Defeats the purpose of bundling. Large applications would ship code they don't need.

### Per-module bundles

**Considered.** Each module becomes its own `.shum` file, loadable independently. **Rejected at the time** (multi-object needs were later served by `.shum` archives — the `shumway-lib` librarian — rather than per-module bundles): it introduces complexity in cross-module reference resolution (the loaded bundle may not have the referenced public predicate available). A single-bundle model is simpler and sufficient for typical use.

This could be revisited in phase 2+ for plug-in architectures or modular applications.

### Source-distribution bundles (no bytecode)

**Rejected.** A "source bundle" would be just a zip of `.pl` files. This is trivially producible without a tool and doesn't provide the value (validation, optimization, fast load) that a true bundle does.

### JSON or text-based bundle format

**Rejected.** Bytecode and large tables are best represented in binary. A text format would be larger, slower to load, and more error-prone.

## Consequences

### Positive

- **Production-ready deployment**: applications can ship a single bundle and a Shumway runtime, with no source distribution.
- **Strong validation**: bundle build time catches errors that would otherwise appear at deployment.
- **Performance**: bundles load quickly (no parsing, no compilation). With phase 2 IL bundles, near-instant startup.
- **Reachability tracing**: the bundler produces a clear report of what's in and why.
- **Dynamic patterns supported**: the dynamic-inclusion rule ensures meta-programming continues to work.

### Negative

- **The dynamic-inclusion rule can include unused dynamics**: if a module is included via the dynamic rule but its dynamic predicates are never actually used, they take space in the bundle.
- **Bundle size for large applications**: 50,000-LOC Prolog programs may produce multi-MB bundles. Manageable but not trivial.
- **Reload not supported in bundles**: once loaded, a bundle's predicates are fixed. To update, the application restarts. (`consult/1` of a source file can still add new dynamic predicates atop a loaded bundle.)

### Mitigations

- **Report shows dynamic-only inclusions**: developers can audit which modules are included solely because of the dynamic rule.
- **Phase 2 enhancement**: dead-code analysis at load time (some predicates included but provably unreachable) could refine the inclusion.

## Implementation Notes

### Bundle file format details

Detailed bytes-on-disk format is documented in `design/bundle-format.md` (to be created in implementation phase). The high-level structure is:

```
[Header: 16 bytes]
[Atom table section: variable]
[Functor table section: variable]
[BigInt table: variable]
[String table: variable]
[Float table: variable]
[Modules section: variable]
[Bytecode section: variable]
[Switch tables section: variable]
[Operator declarations: variable]
[Mode declarations: variable]
[Debug info: variable, optional]
[Predicate entries table: variable]
[Entry points: variable]
[Footer: CRC32 of preceding contents]
```

Each section starts with a length prefix and (optionally) a type tag. Forward compatibility: unknown section types are skipped.

### Atom/functor id remapping

When a bundle is loaded, the bundle's atom/functor ids may not match the engine's. Two strategies:

1. **Patch the bytecode**: rewrite all atom/functor operands to use the engine's ids. One pass over the bytecode, fast.
2. **Remapping table**: keep a per-bundle map and consult it at lookup time. Slower but no modification to the bytecode.

Phase 1 uses strategy 1 (patch on load). Bytecode is mutable until the bundle is fully integrated; afterwards it's read-only.

### Source file paths in bundles

If debug info is included, source file paths are stored in the bundle. For deployment, these are typically relative or stripped (especially the project root). The bundler's CLI has `--strip-paths` and `--source-root <path>` options to control this.

### Versioning

Bundle format version is in the header. The engine's loader checks compatibility:

- Same major version: load.
- Different major version: error, but suggest the corresponding Shumway version.

Format version is bumped when:

- Cell layout changes (unlikely).
- Bytecode encoding changes (unlikely).
- Table structures change incompatibly.

Phase 1 starts at version `1.0`.

### Bundling a bundle (composition)

A bundle can be loaded into an engine, and then the engine can be re-bundled with additional source files. This is useful for layered deployments (base library bundle + application-specific code). The bundler accepts bundle files as inputs alongside source files.

### Determinism

The bundler is deterministic given the same inputs: the output bytes are identical. This enables caching in build systems.

## Test Strategy

- **Single-module bundle**: bundle one source file, load, verify queries work.
- **Multi-module with cross-references**: bundle modules A and B where A calls public predicates of B, verify resolution.
- **Reachability**: bundle a program with both reachable and unreachable predicates, verify only reachable ones are included.
- **Dynamic-inclusion rule**: bundle a program where module M has dynamic predicate `d`, and entry points reach M; verify `d` is included.
- **Dynamic chain**: bundle a program where dynamic `d` in M1 calls `f` in M2 (statically), and M1 is reached; verify M2 and `f` are pulled in.
- **Public collision**: bundle two source files both declaring `foo/2` as public, verify error.
- **Unresolved reference**: bundle a file with a call to a non-existent public predicate, verify error.
- **Entry point not found**: specify a non-existent entry point, verify error.
- **Round-trip**: bundle a program, load the bundle in a fresh engine, run a battery of queries that exercise all the predicates. Compare results with running the same queries against the source-loaded version.
- **Atom remapping**: load a bundle in an engine that already has some atoms; verify bytecode is correctly patched.
- **Determinism**: bundle the same inputs twice, byte-compare outputs.

## Related ADRs

- ADR-006 (Bytecode Encoding): the bundle stores bytecode.
- ADR-008 (Module Visibility): visibility determines what's a public entry point and what's local.
- ADR-011 (IL Compiler): phase 2 IL bundles use `PersistedAssemblyBuilder`.

## Related Design Docs

- `design/bundle-format.md` (to be created): binary format details.
