# ADR-012: Mode Inference Roadmap

## Status

Shipped ([Phase 3](../../history/phase-3-closure.md)).

The directive has been parsed and stored since Phase 1; mode inference and
mode-specialized code generation shipped in Phase 3.

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

The requirement was **directive-based mode declarations** (`:- mode foo(+, +, -)`) from the start (Phase 1), with **code specialization deferred**. This allowed:

- Programs to declare modes early, expressing intent.
- The compiler to validate and store these declarations.
- The specialization that later shipped in Phase 3 to exploit them without source changes.

## Decision

Shumway supports the `:- mode/1` directive with the following semantics:

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

Extended indicators (part of the original roadmap; never implemented — see
"Mode-aware compilation as shipped" below):

- `+S` : input, ground (no embedded variables).
- `-S` : output, will be bound to a ground term.
- `i` : input, integer specifically.
- `+list`, `+atom`, etc.: input with type expectation.

Only the basic `+`, `-`, `?` indicators are accepted.

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

### Declarations as metadata (Phase 1, still the storage model)

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

Through Phases 1–2 the code generator did not exploit mode information — the
declarations were metadata only, so sources could be written with the coming
optimization in mind without the early compiler carrying the complexity.

### Mode-aware compilation as shipped (Phase 3)

What the roadmap's exploitation step became when it landed:

- **Mode inference**: declared modes are read into a `ModeTable` and
  propagated where the compiler can use them.
- **det/semidet specialization — the implicit cut**: a predicate whose
  declared determinism is `det` or `semidet` commits at clause end, so a
  successful call leaves no choice point. This is the declaration's contract
  (`once/1` semantics); see "The trust boundary" below for why this is the
  ONLY thing a declaration is allowed to buy.
- **Determinism gates**: fast paths that require all-modes-deterministic
  consult the table (e.g. the assert fast path,
  `ModeTable.AllModesDeterministic`).

The rest of the roadmap's projection was **not built as designed**: per-mode
specialized code paths with call-site mode dispatch, typed/ground indicators,
and the "10–50× for deterministic predicates" estimate. Those gains arrived by
different, mode-independent designs instead — choice-point elimination via
ADR-029/030/031 (CP-free guard commit, redundant-cut elision) and whole-body
compilation via IL regions. The original full design is archived as
[`../../history/mode-inference-design.md`](../../history/mode-inference-design.md).

### Validation

The compiler validates mode declarations syntactically but does not verify them semantically:

- Arity must match the predicate.
- Indicators must be `+`, `-`, or `?`.
- Determinism, if present, must be one of `det`, `semidet`, `multi`, `nondet`.
- Multiple declarations for the same predicate are allowed (each declares a distinct mode).
- Mode declarations for non-existent predicates are warnings (the predicate may be defined later).

Runtime mode checks (call-mode mismatch raising an error, or a deopt path to
the general implementation) were part of the roadmap and were not implemented;
the cut-plus-check idea in "The trust boundary" below is the surviving form of
that thought.

### Multiple modes per predicate

A predicate may carry several mode declarations:

```prolog
:- mode append(+, +, -) is det.
:- mode append(+, -, +) is semidet.
:- mode append(-, -, +) is nondet.

append([], L, L).
append([H|T], L, [H|R]) :- append(T, L, R).
```

The roadmap projected one specialized version per declared mode with call-site
dispatch; as shipped, all declarations are stored but ONE version is compiled,
and the determinism annotations feed the implicit-cut specialization (a
predicate is treated as deterministic only when `ModeTable.AllModesDeterministic`
holds — every declared mode must agree).

### Interaction with ISO Prolog

Mode declarations are an extension to ISO Prolog. They are silently accepted by other Prolog implementations that don't support them (most parse them as `:- mode/1` calls and ignore them at runtime), and a Shumway program with mode declarations runs unchanged on such systems.

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

## The trust boundary: declarations restrict, they never license removal

Declared modes are consumed ONLY by this ADR's specializations (the det/semidet
implicit cut, gates like the assert fast path). The inferred-determinism
optimization arc (ADR-029/030/031/033/034 — cut elision, CP-free guard commit)
deliberately reads no modes. The reason is an asymmetry in failure modes:

- A declaration may soundly **restrict** behavior. The implicit cut makes the
  predicate *conform* to its contract: commit to the first solution. If the
  declaration is wrong — the predicate had more solutions — the outcome is
  still well-defined and local (`once/1` semantics): solutions the programmer
  declared nonexistent are pruned. The failure IS the declaration's meaning.
- A declaration may never **license removing safety**. Eliding a user-written
  cut, or not materializing a choice point, is only sound as a *theorem about
  the actual code* — which the fail-direct bytecode analysis proves. Justified
  by a false declaration instead, backtracking would explore clauses the cut
  would have killed: their side effects re-run (`assertz`/`retract`/IO do not
  un-execute — see the extra-backtracking invariant in
  `../invariants.md`), extra solutions appear, and the behavior matches
  neither the code nor the declaration.

In one line: **a declaration can prune (defined failure: fewer solutions) but
cannot justify removing protection (undefined failure: semantic corruption).**

Production engines draw the same line. Mercury optimizes on determinism
declarations only because its compiler *verifies* them statically — a wrong
`det` is a compile error, not undefined behavior. SWI-Prolog's historical
`:- mode` is documentation; its `det/1` and SSU (`=>`) turn declarations into
*runtime checks* that raise `determinism_error`. SICStus, GNU Prolog and YAP
derive determinacy from indexing and cuts — from the code, as ADR-030 does.
Ciao verifies assertions statically where it can and inserts runtime checks
where it cannot. No production engine elides choice points on an unchecked
declaration.

A practical consideration reinforced the design: the real-program corpus
(Blint, the Arity sources, the SWI/Scryer/Logtalk libraries) contains no mode
declarations at all, so an optimizer keyed on them would have optimized
nothing that actually runs; ADR-030's fixpoint gets those wins from the code
itself.

Possible future hardening (not implemented): an opt-in development flag in the
SWI `det/1` style, replacing the implicit cut with cut-plus-check — a live
alternative at the commit point raises a determinism error instead of pruning
silently. Release semantics would be unchanged.

## Alternatives Considered

### Full Mercury-style mode system from the start

**Rejected.** Mercury requires extensive infrastructure: mode analysis, determinism analysis, type inference, etc. Implementing this up front would have significantly delayed shipping. The incremental approach (declarations first, exploitation later) was more pragmatic.

### Mode inference (without declarations)

**Deferred.** Inferring modes from usage patterns is possible (analyze call sites and clause bodies). It's more complex than respecting declarations. The roadmap places this after Phase 3's declaration-based exploitation: once the infrastructure is in place to specialize on modes, inference can be added as a refinement.

### Modes as runtime annotations (no compile-time use)

**Rejected.** This is essentially what Phase 1 already does (modes are metadata), but the roadmap's intent is for these declarations to enable future optimization. The roadmap commitment is explicit.

### Different syntax (e.g., type signatures Mercury-style)

**Rejected.** The `:- mode foo(+, -) is det.` syntax is well-established in the Prolog community (SWI, SICStus). Inventing a different syntax would create confusion.

### Mandatory mode declarations for all predicates

**Rejected.** Forcing all predicates to declare modes is a barrier to entry. Shumway accepts undeclared predicates as `?` for all arguments and `nondet` overall. Declarations are an opt-in optimization aid.

## Consequences

### Positive

- **Forward compatibility paid off**: sources written with declarations before
  Phase 3 gained the specialization without modification when it shipped.
- **Documentation**: mode declarations express intent. Readers understand the predicate's contract.
- **Linter input**: a linter can check whether mode declarations match the predicate's actual usage.
- **No upfront cost**: the early compiler did not pay for mode-aware code generation.

### Negative

- **Adding declarations is voluntary**, and in practice almost nobody does:
  the real-program corpus carries none, which is why the mode-independent
  ADR-029..031 arc — not this ADR — is where deterministic-predicate
  performance actually comes from. Declared-mode specialization benefits
  exactly the code that declares.

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

The linter can warn about:

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

- The original Phase-3 design spec is archived as
  [`../../history/mode-inference-design.md`](../../history/mode-inference-design.md).
  What shipped from it: mode inference from `:- mode` directives and det/semidet
  specialization (the implicit cut). Its other ideas were delivered by different
  designs later — deterministic-clause optimization by ADR-029/030/031 (CP-free
  guard commit), whole-body inlining by region compilation
  ([`../../design/il-region-compilation.md`](../../design/il-region-compilation.md)).
  Typed modes, call-site mode dispatch and strict runtime mode checking were not
  implemented.
