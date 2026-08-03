# ADR-024: Generic Prolog-Term Interop (the reftype tier)

## Status

Accepted — implemented (cursor tier in Phase 30; the
materializer/dematerializer tier completed in Phase 32). This is the
term/reftype tier that ADR-022 deferred: its int/float/string tier shipped
first, and whole-term marshalling — Arity's `reftype` / `preftype` machinery —
got this dedicated design ([ADR-022](022-embedded-native-c-blocks.md)).

## Context

Arity-Prolog source crosses the C↔Prolog boundary with **whole terms**, not just
scalars. The vehicle is a C struct that is an isomorphic representation of a Prolog
term — the **reftype** (`prlg_ifce.pl`):

```c
union u_crep { char* cstr; int cint; double cflt; };
typedef struct t_reftype {
    int64_t ntype;            // tag (see ntype codes below)
    int64_t nelem;            // arity (functor) | string length
    struct t_reftype** pars;  // argument array (functor)
    union u_crep crep;        // value
} *reftype, t_reftype;
typedef reftype* preftype;    // a handle (pointer to a reftype)
```

`prlg_ifce.pl` is a Prolog-level library — defined with `{ … }` native blocks that
read/write these struct fields (`(*Ref)->ntype`, `(crep)..cint`, `getargp(N,Ref)`,
`newreftype(...)`, `freepar`, `setcflt`) — that converts terms to and from the
struct:

- **`reftype_term/2`** (struct → term): reads `->ntype`, dispatches, reads the
  scalar (`cint`/`cflt`/`cstr`) or, for a functor, the name (`cstr`) + arity
  (`nelem`) and recurses over `getargp(N, Ref)` for each argument.
- **`fill_par/2` → `fill_reftype/3` → `fill_args/4`** (term → struct): `freepar`
  then `newreftype(...)` for the term's kind, recursing into the arguments.
- `reftype_functor/4`, `preftype/1` support those.

The **usage pattern** in real sources (`i_form_e.pl`, `i_gxprg.pl`) is uniform —
build a struct, call C, read the struct back; **no callback** (C never re-unifies
in Prolog mid-call):

```prolog
{ PtrExp is &par1ref },                              % a global reftype buffer
fill_par(Exp, PtrExp),                               % term  → struct
{ ret = 'i_form_exp'(Mod, For, ptype, par1ref) },    % call C with the struct
reftype_term(Exp, PtrExp),                           % struct (C modified it) → term
```

The user's C functions (e.g. `i_form_exp`, `i_nxgxprgs`) manipulate the reftype
through an **accessor API**: `getint_c`, `putint_c`, `gettxt_c`, `puttxt_c`,
`putatm_c`, `getflt_c`, `putflt_c`, `getfunctor_c`, `putfunctor_c`,
`getfuncarg_c`, `findtype_c`, `equrefs_c`. That accessor API is the real interface.

The buffers are a small fixed set of `extern reftype par1ref … par10ref` globals.

### The asymmetry that drives the design

This whole apparatus exists in Arity for **one reason**: the C code runs in a
separate process and **cannot touch the Prolog heap**, so it must **copy** the term
to/from a C struct. In Shumway the "C" is **.NET running in-process** with direct
access to the engine and the heap. **The copy is unnecessary** — and eliminating it
is exactly the interop advantage over GNU Prolog the project targets.

### ntype codes (the shared source of truth)

| ntype | kind     | Shumway mapping |
|-------|----------|-----------------|
| 0     | undef    | unbound variable |
| 1     | integer  | integer cell |
| 2     | floating | float cell |
| 3     | atom     | atom (same as 4 — see below) |
| 4     | string   | atom (Arity "string" is an atom) |
| 5     | functor  | compound (functor + args) |
| 6     | nontype  | treated as undef |

These codes are the contract shared by both the cursor API (`findtype_c`) and the
future materializer, so the two never diverge.

## Decision

A reftype/preftype is a **cursor over a Prolog term in the heap**, not a copied
struct. Whole-term interop is zero-copy: the .NET side reads and builds the real
term directly.

### 1. `TermRef` — a cursor, not a struct

`reftype` / `preftype` map to a lightweight **`TermRef`** handle (an index into a
small per-engine slot table; no managed object graph, no copy). A slot holds a
single heap cell (the term's root, or — during construction — an argument
position to be filled).

### 2. Any `reftype` global is a runtime term slot

A global declared with type `reftype` / `t_reftype` in a `:- c` region is a
**runtime-owned term slot** — NOT a field of the user's interop class, and NOT a
fixed hard-coded list. Recognition is **by type, not by name**: at consult / load
the runtime creates one slot per such global and maps `name → slot`; `&name`
resolves to that slot's `TermRef`.

The `par1ref … par10ref` globals are simply the ones declared in `prlg_ifce.pl`
(`extern reftype par1ref;` …) — the library's buffers, captured by the general
rule like any other. A program declares its **own** extra slots the same way:

```prolog
:- c.
reftype my_buffer;        % a term slot of one's own
:- prolog.
m(T, R) :-
    { Ptr: preftype; Ptr is &my_buffer },
    fill_par(T, Ptr), { R is 'my_c_fn'(my_buffer) }, reftype_term(T2, Ptr).
```

**Scope** follows C global linkage (ADR-022): the owning module declares
`reftype my_buffer;`, another module references it with `extern reftype
my_buffer;`, and there is one shared slot per name (as for `par1ref`). There is no
separate module-private slot — Arity's C globals have none either; a unique name
per module suffices.

There is no manual memory management: the heap GC owns the cells; `freepar` /
`newreftype` become slot operations.

### 3. The interface predicates are intrinsics (recognized by name)

`reftype_term/2`, `reftype_term/3`, `reftype_functor/4`, `fill_par/2`,
`fill_reftype/3`, `fill_args/4`, and `preftype/1` are recognized **by name/arity**
as a built-in *Arity term-interface prelude*. When `prlg_ifce.pl` is consulted,
those predicates resolve to the builtins and **the source clauses are dropped**
(with an INFO diagnostic). This is what avoids ever compiling the reftype-struct
operators (`->`, `..`, `getargp`, `newreftype`, `freepar`, `setcflt`) — they only
ever appear inside the predicates we replace.

- `fill_par(Term, Ref)` — put `Term` into slot `Ref`.
- `reftype_term(Term, Ref)` — unify `Term` with whatever the slot points at.

### 4. Two accessor APIs over the same `TermRef`

Both sit on the same zero-copy cursor; the user picks per function.

- **Native Shumway API** — idiomatic: `GetTag(ref)`, `GetInt(ref)`,
  `GetFloat(ref)`, `GetAtom(ref)`, `GetFunctor(ref, out name, out arity)`,
  `GetArg(ref, n)`, `PutInt(ref, v)`, `PutAtom(ref, s)`, `PutCompound(ref, name,
  arity)`, `PutArg(ref, n, child)`, `Unify(ref, term)`.
- **Arity `*_c` compatibility API** — same signatures as Arity, implemented on top
  of the native API, so existing C# written against it runs almost unchanged:
  `findtype_c`, `getint_c`, `putint_c`, `getflt_c`, `putflt_c`, `gettxt_c`,
  `puttxt_c`, `putatm_c`, `getfunctor_c`, `putfunctor_c`, `getfuncarg_c`,
  `equrefs_c`.

### 5. Construction maps to incremental heap building

A term is immutable in Shumway, so the Arity "modify the struct in place" idiom
maps to a build cursor:

- `putint_c(v, ref)` / `putflt_c` / `putatm_c` / `puttxt_c` — allocate the cell,
  point the slot at it.
- `putfunctor_c(name, arity, ref)` — allocate the functor cell + `arity` argument
  slots (fresh variables); point the slot at the functor.
- `getfuncarg_c(ref, n, &argref)` — return a sub-`TermRef` for argument slot `n`,
  filled recursively with `put*`.
- `reftype_term(Term, ref)` at the end unifies `Term` with the built root.

This is the exact Arity pattern (`putfunctor` + `getfuncarg` + recursion) realized
as zero-copy heap construction.

### 6. Minor mappings

- **Atom vs string.** A Shumway atom reads back as ntype **4 (STRING)** from
  `findtype_c` (Arity uses "string" for nearly everything). Building with either
  `putatm_c` or `puttxt_c` produces an atom.
- **Lists.** A list is the functor `'.'/2` (and `[]`), transparent: `findtype_c` →
  5, `getfunctor_c` → `./2`, `getfuncarg_c` walks head/tail. The cursor derefs
  Shumway's inline-list representation (ADR-017) under the hood.
- **Variables.** An unbound variable reads as ntype **0 (UNDEF)**; the slot points
  at the REF cell. A `put*` builds a value and `reftype_term` binds the variable.

## Future tier — materializer ↔ dematerializer (deferred, designed-for)

The cursor is for logic that lives **in C#**. When C# is only a **trampoline to a
native C function** (P/Invoke), the native C **cannot touch the Shumway heap** —
the cross-language copy problem returns. For that case a later tier adds:

- a physical .NET `Reftype` struct (ntype/nelem/pars/crep — identical layout to
  Arity's `t_reftype`, blittable for marshalling to native C);
- **Materialize**: Prolog term → `Reftype` struct (a real copy), to hand to native C;
- **Dematerialize**: `Reftype` struct (native C modified it) → Prolog term. Here
  **ntype 3 (atom) and ntype 4 (string) both become a Prolog atom** — they are the
  same thing in Shumway.

This is the ADR-022 "option B" as an **optional** layer on top of the cursor, not a
replacement. The current design must not block it; the ntype-code table (§ ntype
codes) is the shared contract, and the `Reftype` field layout is reserved so it can
be bolted on without reworking the cursor. **Not implemented now.**

## Documentation (required at implementation close)

Done — the programmer-facing guide is [`docs/generic-term-interop.md`](../../guide/generic-term-interop.md):
declaring `reftype` term slots (including one's own), the `fill_par` /
`reftype_term` flow, both accessor APIs (native Shumway + the `*_c` compatibility
layer) with a worked C# example each, the ntype codes, and the build-cursor
pattern. The worked example (`swap_pair`) is kept honest by a test
(`NativeReftypeTests.DocExample_SwapPair_Works`).

## Performance (the IL path)

A reftype block is not tree-walked on a hot path. On first execution it compiles
to a delegate (Expression → JIT IL); and when a reftype predicate promotes to
Tier-1 IL the whole flow becomes one IL method — the blocks are inlined and
`fill_par` / `reftype_term` are fused in (the term ↔ slot marshalling emitted
inline), so there is no per-call `$native_run` / builtin dispatch. Measured on a
3M-iteration warm loop with a trivial interop method (the mechanism dominates):
interpreter 2180 ns/iter, compiled delegate 1225 ns/iter (1.78×), Tier-1 inline +
fusion 481 ns/iter (4.53× over the interpreter, 2.55× over the delegate). As one
IL method the JIT optimizes the slot operations, marshalling and calls together,
well beyond removing the dispatch hops alone.

## Consequences

- Whole-term interop is zero-copy: a .NET interop function reads and builds the
  real Prolog term in the heap. This is the read/build path the project's
  "outperform GProlog in interop-heavy workloads" goal needs.
- `prlg_ifce.pl` consults cleanly without compiling the reftype-struct tier — its
  interface predicates become builtins; its native blocks are never reached.
- The engine gains a per-engine `TermRef` slot table, the named-interface builtins,
  and the two accessor APIs.
- A new ADR (this one) governs the term-interface surface; the future materializer
  tier extends it rather than changing it.

## Alternatives considered

- **Auto-map the struct to a .NET `Reftype` and marshal recursively (ADR-022
  option B) as the primary path.** Rejected as primary: it reintroduces the full
  recursive term↔struct copy that the cursor eliminates, for no benefit when the
  logic is in C#. Kept as the **future materializer tier** for the native-C
  trampoline case, where a physical struct is genuinely required.
- **Compile the reftype-struct tier directly (the `{ … }` blocks with `->`, `..`,
  `getargp`, `newreftype`).** Rejected: it would compile a faithful copy of Arity's
  manual struct machinery — heap-allocated tagged structs, pointer arrays, manual
  free — when the engine already has the term in the heap. The named-intrinsic
  replacement makes the entire tier unnecessary.
- **A single accessor API.** Rejected: the requirement is BOTH the `*_c`
  compatibility layer, so existing C# runs unchanged, AND the option to write
  against the native API for new code. Both over one `TermRef` cost nothing (the `*_c` layer is thin).
