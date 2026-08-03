# ADR-012: Mode Inference Roadmap

## Status

Accepted — implemented: the directive is parsed and stored since v1; mode inference and mode-specialized code generation shipped in Phase 3.

## Context

A Prolog predicate can be called in different **modes**: combinations of which arguments are bound (input, denoted `+`) and which are unbound (output, denoted `-`) at call time. The same predicate may be deterministic in one mode and non-deterministic in another:

```prolog
length([1, 2, 3], L).      % mode (+, -): L is computed, deterministic
length(L, 3).               % mode (-, +): L is generated, non-deterministic
length([1, 2, 3], 3).       % mode (+, +): verification, deterministic
```

Knowing the mode at compile time allows dramatic optimizations:

- **No choice points**: deterministic modes don't need to allocate choice points or trail entries.
- **No backtracking infrastructure**: if a goal is provably deterministic, it's effectively a function call.
- **Type specialization**: if an input is known to be an integer, the code can skip type-checking and dispatch.
- **Direct register-to-register operations**: arithmetic and comparison in deterministic modes can be compiled to native ops directly.

This is the core insight behind Mercury, a Prolog-derived language that achieves performance close to C by requiring mode declarations and exploiting them aggressively. SWI-Prolog and SICStus also use mode information when available, though less aggressively than Mercury.

The user has requested that Shumway support **directive-based mode declarations** (`:- mode foo(+, +, -)`) from v1, with **code specialization deferred** to later phases. This allows:

- Programs to declare modes early, expressing intent.
- The compiler to validate and store these declarations.
- Future phases (3+) to exploit them without requiring source code changes.

## Decision

Shumway supports the `:- mode/1` directive from v1 with the following semantics:

### Syntax

A mode declaration consists of `:- mode` followed by a term that describes one or more callable modes for a predicate:

```prolog
:- mode foo(+, -).
:- mode foo(-, +).
:- mode foo(+, +).
```

A single predicate can have multiple mode declarations, each describing a valid mode of use.

The argument indicators are:

- `+` : input. The argument is expected to be bound (non-variable) at call time.
- `-` : output. The argument is expected to be unbound (variable) at call time; it will be bound by the predicate.
- `?` : either. The argument may be bound or unbound. This is the default if no mode declaration is given.

Extended indicators (planned for future phases, accepted but not validated in v1):

- `+S` : input, ground (no embedded variables).
- `-S` : output, will be bound to a ground term.
- `i` : input, integer specifically.
- `+list`, `+atom`, etc.: input with type expectation.

For v1, only the basic `+`, `-`, `?` indicators are accepted.

### Determinism annotation (extension)

In addition to mode patterns, the directive optionally accepts a determinism annotation:

```prolog
:- mode foo(+, -) is det.        % deterministic
:- mode foo(+, +) is semidet.    % may fail but doesn't backtrack on success
:- mode foo(-, +) is nondet.     % may produce multiple solutions
:- mode foo(+, ?) is multi.      % always produces at least one, possibly more
```

Determinism categories (from Mercury):

- `det`: exactly one solution.
- `semidet`: zero or one solution.
- `multi`: one or more solutions.
- `nondet`: zero or more solutions.

If no determinism is specified, the default is `nondet` (the most permissive).

### Phase 1 behavior

The compiler parses mode declarations and stores them in module metadata:

```csharp
public class ModeDeclaration
{
    public FunctorId Functor;
    public ModeIndicator[] ArgModes;
    public Determinism Determinism;
    public string SourceLocation;
}

public enum ModeIndicator
{
    Input,        // +
    Output,       // -
    Either,       // ?
    // future: typed variants
}

public enum Determinism
{
    Det,
    SemiDet,
    Multi,
    NonDet,
    NoneDeclared,  // no annotation
}
```

These declarations are stored in `Module.ModeDeclarations` (a dictionary keyed by `FunctorId`).

In v1, **the code generator does not exploit mode information**. The compiled bytecode and IL are identical to what would be generated without mode declarations. The declarations are metadata only.

This decision allows source code to be written with future optimization in mind, without forcing the v1 compiler to be more complex than necessary.

### Phase 3 behavior

When Phase 3 implements mode-aware compilation:

1. For each predicate with mode declarations, generate a specialized code path for each declared mode.
2. At call sites, where the compiler can determine which mode applies (via dataflow analysis or runtime check), dispatch to the specialized version.
3. For `det` and `semidet` modes:
   - Don't allocate choice points.
   - Don't write to the trail (except for value-change trail entries, which are rare).
   - Compile to tight straight-line code with branches for failure.
4. For typed input declarations: skip type tests in the body, assume the declared type.
5. For ground input declarations: skip variable handling, assume fully-instantiated terms.

The expected performance improvement for deterministic predicates is **10–50×** over the interpreter, putting performance in the range of Mercury or native-compiled languages for typical workloads.

### Validation in v1

The v1 compiler validates mode declarations syntactically but does not verify them semantically:

- Arity must match the predicate.
- Indicators must be `+`, `-`, or `?`.
- Determinism, if present, must be one of `det`, `semidet`, `multi`, `nondet`.
- Multiple declarations for the same predicate are allowed (each declares a distinct mode).
- Mode declarations for non-existent predicates are warnings (the predicate may be defined later).

In Phase 3, runtime checks may be added: if a predicate is called in a mode that doesn't match any declaration, raise an error (in strict mode) or generate a deopt code path that uses the general-purpose implementation.

### Multiple modes per predicate

A predicate with several mode declarations effectively becomes several specialized versions:

```prolog
:- mode append(+, +, -) is det.
:- mode append(+, -, +) is semidet.
:- mode append(-, -, +) is nondet.

append([], L, L).
append([H|T], L, [H|R]) :- append(T, L, R).
```

In Phase 3, the compiler emits three specialized versions of `append/3`, one for each mode. At call sites, the compiler determines (statically when possible, dynamically otherwise) which version to invoke.

For v1, all three declarations are stored but only the generic version is generated.

### Interaction with ISO Prolog

Mode declarations are an extension to ISO Prolog. They are silently accepted by other Prolog implementations that don't support them (most parse them as `:- mode/1` calls and ignore them at runtime).

Shumway's behavior is consistent: a program with mode declarations works in all phases. In Phase 3+, it runs faster.

### Documentation in source

The convention is to place mode declarations near the predicate definition:

```prolog
:- mode reverse(+, -) is det.
reverse(L, R) :- reverse(L, [], R).

:- mode reverse(+, +, -) is det.
reverse([], Acc, Acc).
reverse([H|T], Acc, R) :- reverse(T, [H|Acc], R).
```

This makes the modes visible to readers and to the compiler.

## Alternatives Considered

### Full Mercury-style mode system in v1

**Rejected.** Mercury requires extensive infrastructure: mode analysis, determinism analysis, type inference, etc. Implementing this for v1 would significantly delay shipping. The incremental approach (declarations now, exploitation later) is more pragmatic.

### Mode inference (without declarations)

**Deferred.** Inferring modes from usage patterns is possible (analyze call sites and clause bodies). It's more complex than respecting declarations. The roadmap places this after Phase 3's declaration-based exploitation: once the infrastructure is in place to specialize on modes, inference can be added as a refinement.

### Modes as runtime annotations (no compile-time use)

**Rejected.** This is essentially what Phase 1 already does (modes are metadata), but the user's intent is for these declarations to enable future optimization. The roadmap commitment is explicit.

### Different syntax (e.g., type signatures Mercury-style)

**Rejected.** The `:- mode foo(+, -) is det.` syntax is well-established in the Prolog community (SWI, SICStus). Inventing a different syntax would create confusion.

### Mandatory mode declarations for all predicates

**Rejected.** Forcing all predicates to declare modes is a barrier to entry. Shumway accepts undeclared predicates as `?` for all arguments and `nondet` overall. Declarations are an opt-in optimization aid.

## Consequences

### Positive

- **Forward compatibility**: source code written today with mode declarations benefits when Phase 3 ships, without modification.
- **Documentation**: mode declarations express intent. Readers understand the predicate's contract.
- **Linter input**: the linter can check whether mode declarations match the predicate's actual usage.
- **No upfront cost**: v1 doesn't pay for mode-aware code generation; it's a v3 investment.

### Negative

- **v1 doesn't realize the performance benefit**: code with mode declarations runs at the same speed as code without. Some users may be disappointed.
- **Adding declarations is voluntary**: many users won't bother. Phase 3 will optimize only the declared predicates, leaving others slow.

### Mitigations

- **Documentation**: tutorial materials emphasize the value of mode declarations.
- **Tooling support**: an IDE plugin or linter that suggests mode declarations based on observed usage patterns could lower the bar in Phase 3+.
- **Phase 3 may include simple inference**: a pass that infers modes from call sites and clause structure can apply to undeclared predicates, providing some benefit without explicit declarations.

## Implementation Notes

### Parsing the directive

The Prolog parser handles `:- mode/1` directives like other directives (it's a clause with the special functor `mode`). The argument is parsed as a term and then validated against the expected structure:

```csharp
public void HandleModeDirective(Term arg)
{
    // arg is something like: foo(+, -)  or  is(foo(+, -), det)
    
    if (arg.IsCompound && arg.Functor == "is" && arg.Arity == 2)
    {
        var modeTerm = arg.GetArg(0);
        var detTerm = arg.GetArg(1);
        var det = ParseDeterminism(detTerm);
        AddModeDeclaration(modeTerm, det);
    }
    else
    {
        AddModeDeclaration(arg, Determinism.NoneDeclared);
    }
}
```

### Storage in modules

Mode declarations are stored in the module they appear in, in `Module.ModeDeclarations`. When the module is loaded, the engine merges these into a global mode information table for cross-module references (if applicable in Phase 3).

### Bundles

Mode declarations are serialized into bundles (in the existing `Modes` section described in ADR-009). They are loaded into the engine when the bundle is loaded, available for future Phase 3 optimizations.

### Linter integration

In v1, the linter can warn about:

- Mode declarations for non-existent predicates (typo, or unused declaration).
- Predicates with multiple declarations whose modes overlap inconsistently (rare, but possible to detect).

In Phase 3, additional warnings are possible:

- Calls to a predicate in a mode not declared for it.
- Predicates that could benefit from mode declarations (based on call site analysis).

### Determinism semantics in Phase 3

When determinism information is present and exploited:

- `det`: the predicate must always succeed exactly once. The compiler can omit failure-handling code and can inline the call as a method invocation.
- `semidet`: zero or one solution. No choice point is needed; failure is a `goto` to the next alternative.
- `multi`: at least one solution. Choice points may be created.
- `nondet`: full general case; standard WAM execution.

For `det` and `semidet` predicates, much of the WAM machinery (trail, choice points) can be elided in the compiled code. This is the source of the dramatic performance improvement.

## Test Strategy

### Phase 1 (declaration support)

- **Parsing**: declarations of various forms parse correctly.
- **Validation**: invalid forms (wrong arity, bad indicators) produce clear errors.
- **Storage**: declarations are accessible via the module's metadata.
- **Multiple declarations**: multiple modes for the same predicate are all stored.
- **Determinism annotation**: with and without `is det/semidet/multi/nondet`, parses correctly.
- **Bundle round-trip**: declarations are preserved through bundling and loading.

### Phase 3 (when implemented)

- **Specialized code generation**: for each mode, the generated IL is correct.
- **Determinism exploitation**: `det` predicates run faster (no CP allocation).
- **Mode dispatch**: at call sites with known input modes, the correct specialized version is called.
- **Runtime mode check (strict mode)**: calling in an undeclared mode raises a runtime error.
- **Fallback**: when neither declarations nor dataflow can determine the mode, the generic version is used.

## Related ADRs

- ADR-006 (Bytecode Encoding): mode declarations are stored as metadata, not as bytecode.
- ADR-008 (Module Visibility): declarations are per-module.
- ADR-009 (Bundler): declarations are included in bundles.
- ADR-011 (IL Compiler): the IL compiler is the primary consumer of mode information in Phase 3.

## Related Design Docs

- `design/mode-inference-phase3.md` (to be created in Phase 3): detailed algorithms for mode-aware code generation, call-site mode dispatch, and runtime mode checks.
