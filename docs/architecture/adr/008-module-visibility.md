# ADR-008: Module Visibility Model

## Status

Accepted ([Phase 1](../../history/phase-1-closure.md)).

## Context

Prolog systems differ significantly in how they handle modules and visibility:

- **GNU Prolog**: essentially no modules. Every predicate is in a single global namespace.
- **SWI-Prolog**: rich module system with explicit imports/exports, qualified names (`module:goal`), and metapredicates. Modules have separate namespaces; two modules can have predicates with the same name without conflict.
- **SICStus, YAP**: similar to SWI but with variations.

Shumway needs a module system that:

1. **Detects unused code statically** (linter benefit).
2. **Supports bundling**: a tool that produces a self-contained deployable from a set of source files needs to know exactly which predicates are reachable.
3. **Resolves calls at compile time when possible**, for both performance and IL compilation.
4. **Provides encapsulation**: predicates internal to a module should not be callable from outside.

This model is intended for building large applications (10,000+ LOC Prolog), often shipped as a single bundle. It needs to support strong static analysis: which predicates are public, which are internal, which are unreachable.

A SWI-style module system with arbitrary namespaces complicates this analysis: two modules can both have `foo/2` and they coexist. Resolving which `foo/2` is called requires reasoning about imports.

A different model is possible: **flat global namespace for public predicates, file-local namespace for everything else**. This is closer to how visibility works in compiled languages like C# or Java: things are local unless explicitly exported.

This is the model Shumway adopts; the design is documented here.

## Decision

Shumway uses a **two-layer visibility model**: predicates are local to their source file (module) by default, and can be promoted to a flat global namespace via the `:- public` directive.

### Module identity

**One source file = one module.** The module's name is determined by:

1. An explicit `:- module(name).` directive at the top of the file, if present.
2. Otherwise, the base name of the file (without extension).

```prolog
:- module(parser).         % explicit name
:- module(my_module).      % another example
% file 'utils.pl' without directive → module name is 'utils'
```

The module name is an atom. Multiple files cannot have the same module name (loading attempt would conflict).

### Visibility levels

Predicates have one of two visibility levels:

- **Local** (default): visible only within the module that defines them.
- **Public**: visible globally. **Must be unique** across all loaded modules.

```prolog
:- module(my_module).

:- public foo/2.            % foo/2 is public, callable from any module
:- public [bar/1, baz/3].   % list form for multiple

foo(X, Y) :- helper(X, Y).  % public
bar(X) :- foo(X, _).        % public
baz(A, B, C) :- ...         % public

helper(X, Y) :- ...         % local, not visible outside this module
```

### Static predicates are immutable

This is reaffirmed from ADR-007: predicates defined in a module are **static and immutable** unless explicitly declared `:- dynamic`. Attempting `assertz/1` or `retract/1` on a static predicate is a runtime error.

### Dynamic predicates respect visibility

A predicate can be both dynamic and have a visibility level:

```prolog
:- dynamic counter/1.       % local dynamic
:- public state/2.          % public, but still static (immutable)
:- dynamic state/2.         % public AND dynamic
```

The combination `:- public foo/N.` and `:- dynamic foo/N.` means the predicate is globally accessible AND can be modified via `assertz`/`retract` from any module.

### Visibility declarations: order is flexible

The `:- public` directive can appear **before or after** the predicate definition. The compiler accumulates declarations and resolves them at the end of the module's compilation.

```prolog
:- public foo/2.            % declare first
foo(X, Y) :- ...
```

Equivalent to:

```prolog
foo(X, Y) :- ...
:- public foo/2.            % declare after
```

Both forms work. This is convenient for modules that group related declarations at the top or the bottom.

### Resolution rules

When the compiler encounters a call to `goal(args)` in module M, it resolves the target predicate in this order:

1. **Local predicates of M** with matching name and arity. If found, the call is resolved at compile time to the local definition.

2. **Builtins** (system predicates registered at engine initialization). If found, the call may be compiled to a dedicated builtin opcode (`Is`, `UnifyEq`, etc.) or to `CallBuiltin`.

3. **Public predicates of other already-loaded modules**. If found, the call is resolved at compile time to the global definition.

4. **Unresolved**: the call cannot be resolved at compile time. Two sub-cases:
   - **Interactive / `consult` runtime**: the call is left as an "unresolved reference" and the resolution is deferred to runtime. At runtime, if no matching predicate exists, the engine raises `existence_error/1`.
   - **Bundle compilation**: the linker detects the unresolved reference and emits an error.

### Lazy linking for cross-module references

When a module is compiled, calls to predicates from other modules may not be resolvable yet (the other modules haven't been loaded). The compiler:

- Emits a placeholder `UnresolvedRef` in the bytecode (a `Call` instruction with a special operand that needs patching).
- Records the unresolved reference in the `CompiledModule.UnresolvedCalls` list.

After all modules are loaded (or at the end of a `Consult` operation), the engine attempts to resolve unresolved references. For each:

- Find a public predicate with matching functor in any loaded module.
- If found, patch the bytecode to point to it.
- If not found, leave as unresolved; runtime calls produce `existence_error`.

For bundles, the linker performs this resolution as a final phase. Any unresolved reference at bundle time is a hard error.

### Builtins as a special pseudo-module

Builtins live in a conceptual "system" module. They are:

- **Implicitly public**: callable from any module without ceremony.
- **Frozen**: cannot be overridden globally. A user defining `length/2` as public in their module would clash with the builtin `length/2`.
- **Shadowable locally**: a user can define `length/2` as **local** in their module, and that local version takes precedence for calls within that module. Other modules still see the builtin.

The shadowing rule applies to library-style builtins (those implemented in Prolog as bootstrap code). Core builtins (control flow, type testing, basic arithmetic) cannot be shadowed even locally; doing so is a compile-time error.

The distinction between "core" and "library" builtins is documented in `design/builtins-catalog.md`.

### Module loading

Modules are loaded by:

- `consult(file)` or `[file]`: parses the file, compiles its clauses, registers the module.
- `LoadBundle(path)`: loads a pre-compiled bundle (potentially containing multiple modules).

Loading a module:

1. Parses the source.
2. Identifies the module name (from `:- module/1` or filename).
3. **Checks for module name conflict**: if a module with this name is already loaded, the existing one is **completely replaced** (clear all its predicates, then load the new ones). This is the simple, correct behavior — and how reconsult/1 works today.
4. Compiles each clause to bytecode.
5. Validates visibility declarations: public predicates do not collide with other modules' public predicates or with core builtins.
6. Registers the module in the engine's module list.
7. Attempts to resolve any previously-unresolved references that might now match this module's public predicates.

### Predicate tables

Each engine maintains two structured tables:

```csharp
class Engine
{
    // Public predicates: flat namespace, key is FunctorId, unique across modules.
    private Dictionary<FunctorId, Predicate> _publicPredicates;
    
    // Module-local predicates: keyed by module, then by functor.
    private Dictionary<ModuleId, Dictionary<FunctorId, Predicate>> _localPredicates;
    
    // Module metadata
    private Dictionary<ModuleId, Module> _modules;
    
    // Built-ins: special table accessible globally
    private Dictionary<FunctorId, Predicate> _systemPredicates;
}
```

### Static call resolution in bytecode

Most calls are resolved at compile time:

```
foo:bar(X) → Call <address of bar in foo's local predicates>
some_public/2 → Call <address of some_public/2 in _publicPredicates>
is/2 → IsOpcode (specialized builtin opcode, no lookup)
length/2 → CallBuiltin <length builtin id>
```

The bytecode contains direct addresses (offsets into the `CodeArea`). No runtime lookup is needed for these calls.

For unresolved references (forward declarations across modules, or for dynamic predicates where the predicate may be redefined), a runtime lookup is performed:

```
unresolved_pred(X) → CallUnresolved <FunctorId>
```

The `CallUnresolved` opcode looks up the predicate in the appropriate table at runtime.

## Alternatives Considered

### SWI-Prolog-style modules with per-module namespaces

**Rejected.** Allows two modules to have `foo/2` simultaneously, requiring qualified names (`module:foo(X)`) and explicit import/export lists. The user-facing complexity is high, and the static analysis benefits (linting, bundling) are significantly weakened.

### No modules (GNU Prolog style)

**Rejected.** Every predicate is global. Large applications would have constant name clashes. No encapsulation. Linting becomes impossible because everything is reachable from everywhere.

### Lexical scoping with anonymous closures

**Rejected as out-of-scope.** Some experimental Prolog systems (Lambda Prolog, etc.) use lexical scoping. This is not standard Prolog and not in scope for Shumway.

### Module-qualified names as syntactic sugar

**Considered.** Allow `parser:tokenize(X)` as a way to call a local predicate of another module. **Rejected at the time**: it re-introduces the SWI-style complexity; the Phase-1 model was "public or nothing" for cross-module access.

If later a need arises to expose specific predicates "controlled" to a few modules (rather than the whole world), this can be considered as a phase 2+ feature (perhaps with `:- export(foo/2, [to(module1, module2)]).` or similar).

### Operators per module

**Rejected.** ISO standard treats operators as global. Shumway follows ISO: `:- op(...)` sets a global operator. Phase 2+ could add module-scoped operators if needed.

### Auto-import of all loaded modules' public predicates

**Accepted (by definition of "public").** Once a predicate is public, it's accessible from everywhere. This is exactly the model.

## Consequences

### Positive

- **Strong static analysis**: the call graph is fully resolvable at compile time (or at bundle time). Unused local predicates can be detected. Unresolved references are caught early.
- **No qualified names needed**: code is cleaner. `foo(X)` always refers to the unambiguous `foo` (either local to the module or globally public).
- **Bundle-friendly**: the bundler can compute reachability precisely. See ADR-009.
- **Performance**: static resolution means direct call addresses in bytecode. No runtime lookup for the common case.
- **Encapsulation**: a module's internal helpers are truly internal. Other modules cannot accidentally call them.

### Negative

- **No two libraries with the same public name**: if you load a bundle that defines `parse/2` as public, you cannot load another bundle with a different public `parse/2`. This restricts code reuse compared to SWI-style modules.
- **Renames are coarse**: if a public name conflicts, you must rename the predicate in one of the modules. There is no "import as" mechanism.
- **No partial visibility**: a predicate is either fully public or fully local. There's no way to expose to specific modules only.

### Mitigations

- **Documentation**: encourage developers to think of public predicates as a small API surface, with most code being local.
- **Linter warnings**: when a public predicate is defined in two loaded modules, the error message identifies both definitions clearly.
- **Phase 2+ extensions**: if the limitations become a problem in practice, partial visibility (`export to ...`) or qualified names can be considered.

## Implementation Notes

### Module representation

```csharp
public class Module
{
    public ModuleId Id;
    public string Name;          // the atom name
    public string? SourceFile;   // null if loaded from a bundle
    
    public Dictionary<FunctorId, Predicate> LocalPredicates;
    public Dictionary<FunctorId, Predicate> PublicPredicates;  // subset of locals + public marker
    
    public List<UnresolvedCall> UnresolvedCalls;
    public List<OperatorDeclaration> Operators;  // applied globally on load
    public Dictionary<FunctorId, ModeDeclaration> ModeDeclarations;
}

public struct UnresolvedCall
{
    public int BytecodeOffset;   // where in the bytecode the operand needs patching
    public FunctorId Target;     // what predicate we want to call
}
```

### Predicate metadata

```csharp
public abstract class Predicate
{
    public FunctorId Functor;
    public ModuleId DefinedInModule;
    public Visibility Visibility;
    public bool IsDynamic;
    public List<int>? AtomReferences;  // for atom GC mark phase
    
    public abstract bool Invoke(Engine engine, int argBase);
}

public enum Visibility { Local, Public }
```

### Resolution algorithm in detail

When the compiler encounters a goal `g(args)` from module M with arity N:

```csharp
FunctorId f = GetFunctorId(g, N);

// 1. Local predicates of M
if (M.LocalPredicates.TryGetValue(f, out var local))
    return ResolveTo(local);

// 2. Builtins (also handles special opcodes for core builtins)
if (_systemPredicates.TryGetValue(f, out var builtin))
{
    if (HasSpecializedOpcode(builtin))
        return EmitSpecializedOpcode(builtin);
    return ResolveTo(builtin);
}

// 3. Public predicates of other modules
if (_publicPredicates.TryGetValue(f, out var pub))
    return ResolveTo(pub);

// 4. Unresolved: emit deferred reference
M.UnresolvedCalls.Add(new UnresolvedCall { Target = f, BytecodeOffset = currentEmitOffset });
EmitDeferredCall(f);
```

### Validation on module load

After parsing and compiling a module, before registering it:

1. For each predicate marked `:- public`, check that no other module already has a public predicate with the same functor. If conflict, error.
2. For each predicate marked `:- public` with a core builtin functor, error ("cannot override core builtin X").
3. Apply operator declarations globally.

After registration, attempt to resolve unresolved references in this and other modules that might now match.

### Static predicate immutability enforcement

When `assertz(foo(...))` is called:

```csharp
public void Assertz(Term clause)
{
    FunctorId f = ExtractFunctor(clause);
    Predicate pred = ResolveDynamic(f, _currentModule);
    
    if (pred == null)
    {
        // No predicate exists; auto-declare as dynamic (with warning)
        if (_engine.Flags.StrictDynamicDeclarations)
            throw new ExistenceError(f);
        pred = CreateNewDynamicPredicate(f, _currentModule);
    }
    
    if (!pred.IsDynamic)
        throw new PermissionError("modify", "static_procedure", f);
    
    pred.AddClause(clause);
}
```

### Reload semantics

When a module is reloaded (via `consult/1` of a file already loaded as that module):

1. The old module's predicates are removed from the predicate tables.
2. Compiled IL for those predicates is dropped from per-engine caches. The global IL code cache may still hold the old bytecode-hash-keyed entries (they will be garbage-collected when no engine references them).
3. The new version is loaded.
4. Cross-module references to this module's predicates are re-resolved.

Reload is **all-or-nothing**: the entire module is replaced. Incremental reload (changing one clause) is not supported (assertz/retract on dynamic predicates is the incremental mechanism).

## Test Strategy

- **Local visibility**: define a predicate as local, try to call from another module, verify error.
- **Public visibility**: define as public, call from another module, verify success.
- **Public uniqueness**: define `foo/2` as public in two modules, verify error on loading the second.
- **Forward references**: module A calls public predicate of module B before B is loaded; verify resolution after B is loaded.
- **Builtin shadowing (local)**: define `length/2` as local in a module, call from within: local version. Call from another module: builtin version.
- **Core builtin shadowing**: attempt to redefine `=/2`, verify compile-time error.
- **Visibility directive order**: declare `:- public` before and after the definition, both work.
- **Module reload**: reload a module with changes, verify new definitions are used and old ones are gone.
- **Static predicate immutability**: attempt `assertz` on a static predicate, verify `permission_error`.
- **Auto-dynamic**: assertz on a previously unknown predicate, verify auto-declaration with warning.

## Related ADRs

- ADR-007 (Indexing): visibility affects whether indexing is applied (static yes; dynamic predicates gained indexing later — Phase 2 cross-query caching, then the in-place indexed layouts).
- ADR-009 (Bundler): the bundler relies heavily on visibility to compute reachability.
- ADR-011 (IL Compiler): static predicates with resolved calls compile to direct IL invocations; dynamic ones go through runtime dispatch.
