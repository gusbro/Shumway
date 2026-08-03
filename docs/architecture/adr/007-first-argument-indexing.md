# ADR-007: First-Argument Indexing (Phase 1)

## Status

Accepted (Phase 1) — and since extended well past this ADR.s scope: multi-argument indexing and dynamic-predicate index caching shipped in Phase 2, JIT indexing in Phase 3, in-place extensible indexed layouts for dynamics in Phases 10-11, and second-level / bucket indexing in ADR-027/028. What follows is the Phase-1 decision as made.

## Context

When a Prolog predicate has multiple clauses, the engine must determine which clauses can possibly match a given call. Without indexing, the engine tries each clause in order, attempting unification and backtracking on failure. For a predicate with N clauses and a call that matches the K-th clause, this is O(K) unification attempts per call.

For programs with large fact databases (typical in our target use case: grammar rules with many productions, knowledge bases, etc.), this becomes a performance bottleneck. A 1000-clause fact set with each call hitting a single clause means 500 failed unifications on average per call.

**Indexing** precomputes a lookup structure (typically based on the type and value of the first argument) that lets the engine skip directly to candidate clauses. With good indexing, lookup is O(1) or O(log N).

The classic WAM (Aït-Kaci) describes first-argument indexing using three instructions: `switch_on_term`, `switch_on_constant`, and `switch_on_structure`. This catches the common pattern of clauses that discriminate based on the first argument.

Real-world implementations extend this:

- **Multi-argument indexing** (SWI-Prolog, YAP): when the first argument is a variable in the call, try to index on the second, third, etc.
- **Deep indexing**: index on sub-terms (e.g., the head of a list).
- **JIT indexing** (SWI-Prolog): build index structures based on observed call patterns.
- **Dynamic predicate indexing**: invalidate and rebuild indexes when `assertz`/`retract` modifies the predicate.

Each level adds complexity. For Phase 1, the goal was to ship a correct and useful indexing system that covers the most common patterns. More sophisticated indexing is deferred to later phases.

## Decision

Phase 1 implements **first-argument indexing on static predicates only**.

### What is indexed

A predicate is eligible for indexing if:

- It is **static** (not `:- dynamic`).
- It has **more than one clause** (single-clause predicates need no index).
- The first argument has discriminating types or values across clauses.

### Instruction set

Four opcodes implement indexing:

```
switch_on_term VarAddr, ConstAddr, ListAddr, StructAddr
    Operands: 4 code addresses
    Dispatches based on the type of A1 (after deref):
        - REF (unbound): jump to VarAddr
        - ATOM, INT, FLOAT, BIGINT, STRING: jump to ConstAddr
        - LIS: jump to ListAddr
        - STR: jump to StructAddr
        - other tags (FOREIGN, PSTR, ...): typically jump to VarAddr (matches any)

switch_on_atom TableId
    Operand: id of a SwitchTable in CodeArea.SwitchTables
    Looks up A1's atom id in the table; jumps to the matching address or
    a default address if not found.

switch_on_integer TableId
    Operand: id of a SwitchTable in CodeArea.SwitchTables
    Looks up A1's integer value (after deref); jumps to the matching address
    or default.

switch_on_structure TableId
    Operand: id of a SwitchTable in CodeArea.SwitchTables
    Looks up A1's functor id (the head of the structure) in the table;
    jumps to the matching address or default.
```

### Switch table representation

```csharp
public class SwitchTable
{
    // For small tables, parallel arrays for cache-friendly linear search
    public int[] Keys;
    public int[] Values;
    public int Count;
    public int DefaultAddress;
    
    // For larger tables, a dictionary
    public Dictionary<int, int>? Dict;
    
    public int Lookup(int key)
    {
        if (Count <= 16 && Dict == null)
        {
            for (int i = 0; i < Count; i++)
                if (Keys[i] == key) return Values[i];
            return DefaultAddress;
        }
        return Dict!.TryGetValue(key, out int addr) ? addr : DefaultAddress;
    }
}
```

The threshold (16) is chosen because below it linear scan over a contiguous array is faster than `Dictionary` lookup (better cache locality, no hash computation). Above it, `Dictionary` wins.

### Compilation algorithm

For a predicate with clauses C1..Cn, the compiler:

1. **Analyze the first argument of each clause head**. Classify as one of:
   - Variable (matches any term).
   - Atom with specific value.
   - Integer with specific value.
   - Float with specific value (uncommon, typically variables here).
   - List (LIS-tagged value).
   - Structure with specific functor.
   - String (PSTR or STRING).
   - Other (FOREIGN, etc.).

2. **Partition clauses into buckets** based on first argument type:
   - Var bucket: clauses with variable first arg. **These match any call** and appear in every other bucket too.
   - Const bucket: clauses with constant first arg (atom or int).
   - List bucket: clauses with list first arg.
   - Struct bucket: clauses with structure first arg.

3. **Build switch tables** for Const and Struct buckets:
   - For atoms: group clauses by atom id. Each group becomes an entry in the switch_on_atom table.
   - For ints: similarly for switch_on_integer.
   - For structures: similarly for switch_on_structure (keyed by functor id).
   - Clauses with variable first arg are added to every group (they match any specific value too).

4. **Emit code**:
   - Emit `switch_on_term` at the predicate entry, dispatching to the four bucket entry points.
   - In each bucket entry, emit `switch_on_atom` / `switch_on_integer` / etc. as appropriate, or directly `try_me_else` chain if the bucket has only a few clauses.
   - For each group within a switch table, emit a `try`/`retry`/`trust` sequence over the clauses in that group.

### Example

```prolog
shape(circle, area).
shape(square, area).
shape(circle, perimeter).
shape(triangle, area).
```

All four clauses have an atom as first argument. Compiled (approximately):

```
shape/2:
    switch_on_term VarLabel, ConstLabel, FailLabel, FailLabel

VarLabel:
    try_me_else c2
c1: get_atom circle, A1
    get_atom area, A2
    proceed
c2: retry_me_else c3
    get_atom square, A1
    get_atom area, A2
    proceed
c3: retry_me_else c4
    get_atom circle, A1
    get_atom perimeter, A2
    proceed
c4: trust_me
    get_atom triangle, A1
    get_atom area, A2
    proceed

ConstLabel:
    switch_on_atom AtomTable  ; references SwitchTable id 0

AtomTable (SwitchTable id 0):
    circle    → CircleGroup
    square    → c2
    triangle  → c4
    default   → FailLabel

CircleGroup:
    try c1
    trust c3
```

When called with `?- shape(square, X)`:
1. `switch_on_term` dispatches to ConstLabel (A1 is an atom).
2. `switch_on_atom` finds `square` → jumps to `c2` directly.
3. No choice point created for the unsuccessful clauses.

When called with `?- shape(X, area)`:
1. `switch_on_term` dispatches to VarLabel (A1 is a variable).
2. The full `try_me_else` chain runs, trying each clause.

### Clauses with variable first argument

If some clauses have a variable as first argument (matching any term), they must be tried in every bucket:

```prolog
weird(X, foo) :- bar(X).        % X is variable
weird(apple, fruit).            % apple is atom
weird([H|T], list) :- baz(T).   % LIS
```

Compiled:

```
weird/2:
    switch_on_term VarLabel, ConstLabel, ListLabel, FailLabel

VarLabel:
    try_me_else c2
c1: [code for clause 1]
    retry_me_else c3
c2: [code for clause 2]
    trust_me
c3: [code for clause 3]

ConstLabel:
    ; A1 is a constant; clauses 1 and 2 are candidates
    ; (1 because it has variable arg matching anything; 2 because it has apple atom)
    switch_on_atom AtomTable

AtomTable:
    apple   → ConstAppleGroup
    default → ConstVarOnly

ConstAppleGroup:
    try c1     ; clause 1 (variable arg) is also a candidate
    trust c2   ; clause 2 (apple specifically)

ConstVarOnly:
    ; A1 is a constant other than apple; only clause 1 matches
    (jump directly to c1, no choice point needed since only one alternative)

ListLabel:
    ; clauses 1 and 3 are candidates
    try c1
    trust c3
```

### Dynamic predicates

In Phase 1, **dynamic predicates did not have indexing**. They use a plain `try_me_else` chain over all clauses. This avoids the complexity of invalidating and rebuilding indexes on `assertz`/`retract`.

Implementation strategy: when a predicate is declared `:- dynamic`, the compiler emits straightforward sequential code without `switch_on_*` instructions. The cost is linear time per call, but for dynamic predicates with few clauses (the typical case) this is acceptable.

**Phase 2 will add indexing for dynamic predicates**, with the following design intent:

- The first call after a modification rebuilds the index lazily.
- The index is invalidated on `assertz`/`retract`.
- For dynamic predicates with few clauses, the cost of rebuilding may exceed the benefit; a heuristic decides whether to build an index.

The bytecode encoding leaves room for this: the dynamic predicate.s Phase-1 bytecode is just `try_me_else` chains; in later work a separate code path or runtime decision can dispatch to an indexed version.

### Auto-declaration of dynamic predicates

When the first operation on a previously unknown predicate is an `assertz`/`asserta` (no prior `consult` or declaration), the predicate is **auto-declared as dynamic** with a warning. This matches SWI-Prolog's default and is the principle of least surprise.

A configuration flag `strict_dynamic_declarations` (default: false) can disable auto-declaration, requiring explicit `:- dynamic` directives.

## Alternatives Considered

### Multi-argument indexing

**Deferred to Phase 2.** When the first argument is a variable in the call, multi-argument indexing tries indexing on the second, third, etc. This is valuable but adds significant compiler complexity. Phase 1 ships with the simpler first-argument-only model.

### Hash-only indexing (no `switch_on_term`)

**Rejected.** Without `switch_on_term`, every indexed predicate would have a single hash table mixing atom, int, list, and structure keys. This complicates lookup and is less cache-friendly than separating by type first.

### Indexing for dynamic predicates

**Deferred (delivered in Phase 2 as a cross-query cache, later the in-place indexed layouts).** The complexity of maintaining index consistency under `assertz`/`retract` is significant. Phase 1 shipped without it; the Phase-2 cache delivered the roadmap.

### Indexing on every argument (not just first)

**Rejected.** Indexing on multiple arguments simultaneously requires multi-dimensional structures (or a sequence of single-argument indexes). Phase 2 will add multi-argument indexing as a fallback when the first argument is a variable, but indexing on every argument simultaneously is more complex and rarely needed in practice.

### JIT-compiled indexing (SWI-Prolog style)

**Deferred to Phase 3+.** JIT indexing observes runtime call patterns and builds indexes adaptively. It's powerful but requires significant infrastructure (profiling, rebuilding bytecode, etc.). Static first-argument indexing handles the most common patterns; JIT indexing is a refinement.

## Consequences

### Positive

- **Hot dispatch is O(1)**: indexed predicates with a discriminating first argument skip directly to matching clauses.
- **Choice points are avoided** for deterministic calls: when only one clause matches, no CP is created. This reduces trail and stack overhead.
- **Grammar processing benefits**: DCGs typically have many alternatives for a non-terminal; indexing makes them efficient.
- **Static analysis stays in compile time**: indexing is computed once, not maintained at runtime (in Phase 1).

### Negative

- **Dynamic predicates pay full cost**: without indexing, calls scale O(N) with clause count.
- **No discrimination on later arguments**: a predicate like `foo(X, 1)` vs. `foo(X, 2)` did not benefit from indexing in Phase 1 (multi-argument indexing later covers it).
- **Compiler complexity**: building the switch tables and `try`/`retry`/`trust` groupings is non-trivial.

### Mitigations

- **Phase 2 roadmap is explicit**: users with dynamic-predicate or multi-argument bottlenecks know when to expect improvements.
- **Linter can suggest** moving frequently-called dynamic predicates to static if their content doesn't change at runtime.
- **Documentation** explains which patterns benefit from indexing, helping users structure their code.

## Implementation Notes

### Switch table allocation

Switch tables live in `CodeArea.SwitchTables` (one per indexed predicate). They are referenced from instructions by integer id (the index in the list).

### Code address representation

In the bytecode, addresses are integers (byte offsets into the `CodeArea.Bytes` array). Forward references are resolved by the compiler's label-patching mechanism.

### Indexing for single-clause predicates

A predicate with one clause needs no indexing instructions. The compiler simply emits the clause body. The interpreter dispatches directly.

### Handling of `is_list` and similar deep checks

Indexing is shallow: it looks at the tag of the first argument's deref. It does not recurse into the structure. For predicates that discriminate based on sub-terms (e.g., `foo([1|_])` vs. `foo([2|_])`), indexing in Phase 1 sends both to a common bucket and the clause body.s `get_*` instructions handle the discrimination.

Second-level indexing — look at the head of a list, or the first argument of a structure — was deliberately left out of Phase 1; it later shipped as ADR-027.

### `switch_on_constant` is two opcodes

We split the classical `switch_on_constant` into `switch_on_atom` and `switch_on_integer` because atom ids and integer values share the same numeric space (both are ints) but have different semantics. Two opcodes make the compilation cleaner and the disassembly more readable.

### Indexing and PSTR / FOREIGN

PSTR and FOREIGN as first arguments in clause heads are treated as variable-like: they go in the VarBucket. Discrimination of PSTRs and foreign objects is uncommon in clause head patterns. If a use case emerges, dedicated indexing can be added later.

## Test Strategy

- **Single-clause predicate**: no indexing instructions emitted; direct execution.
- **Two-clause predicate, both with same first-arg constant**: no `switch_on_atom` needed (or `switch_on_atom` with one entry); `try_me_else` chain.
- **Multiple constants, all atoms**: `switch_on_atom` correctly built; lookup yields correct clause.
- **Mixed: variable + constant first args**: variable-arg clauses appear in every bucket.
- **Calls with various first-arg types**: dispatch through `switch_on_term` to correct bucket.
- **Default case**: lookup with unknown key falls back to default address (typically fail or var bucket).
- **Switch table size threshold**: tables with ≤16 entries use linear scan; larger use Dictionary; both produce correct results.
- **Indexed and non-indexed performance**: benchmark a 1000-fact predicate with indexing vs. without to confirm the expected speedup.
- **Dynamic predicate without indexing**: assertz adds a clause; subsequent calls find it; no indexing applied.

## Related ADRs

- ADR-006 (Bytecode Encoding): indexing instructions and switch tables are part of the bytecode.
- ADR-008 (Module Visibility): static vs dynamic distinction affects whether indexing is applied.
- ADR-011 (IL Compiler): the IL compiler must also handle indexing instructions when emitting code.

## Related Design Docs

- `design/wam-instruction-set.md`: detailed operand specs for indexing opcodes.
- Phase 2 design docs (future): multi-argument indexing, dynamic predicate indexing.
