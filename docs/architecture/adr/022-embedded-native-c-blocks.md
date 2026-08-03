# ADR-022: Embedded native C blocks (`:- c` / `{...}`) — IL lowering to a foreign interop class

## Status

Accepted — implemented (Phase 30): `:- c` prototypes and `{...}` blocks compile
to IL, run at runtime and persist in bundles. The document below is the design
as agreed before implementation began.

## Context

Arity-Prolog sources (the GeneXus corpus under `C:\temp\test` — 245 files, ~137
with a `:- c` region, ~500 `{...}` blocks) use an embedded-C FFI:

- A `:- c.` … `:- prolog.` region declares a **C environment**: global variables
  / buffers (`char lbuf[255];`), function prototypes (`int strcmp(const char*,
  const char*);`), and typedefs (`typedef char *pchar;`, the `t_reftype` struct).
  In practice the regions also carry large preprocessed MS headers (noise); only
  the typedefs / prototypes / globals actually referenced matter.
- A `{ … }` block inside a Prolog clause body is a **native goal**: a linear
  sequence of statements that reads/writes Prolog variables and calls C
  functions. Example (`strcmp_p/3`, strings.pl):

  ```prolog
  strcmp_p(LS, RS, X) :-
      string_length_bytes(LS, LLen0), ...,      % Prolog goals before
      { 'MakeCString'(lbuf, LLen, &LS);          % read LS (a string) into lbuf
        'MakeCString'(rbuf, RLen, &RS);
        X is 'strcmp'(lbuf, rbuf) },             % unify X with the int result
      !.                                          % Prolog goal after
  ```

Today the Shumway parser robustly delimits both constructs but **drops their
semantics**: the `:- c` region is skipped raw (`Lexer.SkipNativeCodeSection`),
and the `{...}` block is skipped raw and replaced by the goal `true`
(`Lexer.SkipNativeGoalBlock` + the parser's `{` case) — chunks 436/437/438.

Shumway targets .NET/IL, not C object code. We want these native blocks to
actually run, lowered to IL.

**Corpus findings that shape the design:**

- Blocks are **linear** — *no C control flow* (`if`/`while`/`for`/`return` seen
  in extraction were leaked C function bodies from `:- c` regions, not block
  goals). Statement forms: typed local decl `Var: type`; Prolog bind `Var is
  CExpr`; C assignment `lhs = CExpr`; C call `'Func'(args)`. Separators `,` and
  `;` are interchangeable.
- Expression forms: function calls `'Name'(args)`, var refs, int literals, `&Var`
  (output / address-of, ~common), and rarely `*deref`, arithmetic, string
  literals, `(void)`.
- Types: `int`/`long`/`short`/`char`, `pchar`/`cstring` (`char*`),
  `pint`/`ppchar`, and struct-pointer typedefs (`preftype`/`t_reftype`) that
  carry a **whole Prolog term** marshalled to C.

## Decision

### 1. Two-parser architecture

The Shumway Prolog parser stays the outer driver and keeps owning region/block
**delimitation** (it is already robust against the many `:- c`/`:- prolog`
alternations and nested C braces). The only change at the two skip points:
**capture the raw span instead of discarding it.**

A NEW dedicated **C-subset parser** consumes the captured raw text and produces a
C-AST, with two entry points:

- `:- c` text → a per-module/program **symbol table** (globals, buffers,
  prototypes, typedefs).
- `{...}` text → a **native-goal statement sequence**.

The Prolog parser understands no C; the C parser understands no Prolog or regions.
Coupling is limited to the two hooks (`SkipNativeCodeSection`, `SkipNativeGoalBlock`
+ the parser `{` case, which today emits `AtomTerm("true")`).

### 2. Type + mode inference from the surrounding Prolog guards

Each Prolog variable named in a native block gets its .NET type and direction from
the clause's Prolog mode/type guards:

| Guard | Meaning |
|-------|---------|
| `string(X)` / `atom(X)` | X is a **string** (.NET `string`) |
| `integer(X)` | X is an **int / long** |
| `float(X)` | X is a **float** (double) |
| `term(X)` | X is a **whole term** (reftype tier — deferred) |
| `var(X)` *before the block* | X is unbound → **output** (assigned inside) |
| `nonvar(X)` *before the block* | X is bound → **input** |

Type guards may appear **before** (inputs) or **after** (outputs, e.g.
`integer(Len)` post-block). The guards keep executing as ordinary runtime goals —
the compiler *reads* them as annotations and does not remove them.

Inference is **mandatory**: if the compiler cannot determine mode+type for a
variable used in native code, it is a **compile error** (no silent default).

### 3. A native block is a synthesized foreign — tier-agnostic

Each `{...}` block compiles to a self-contained **deterministic routine** (a
`BuiltinImpl`-shaped `Func<Engine,bool>`) whose arguments are the block's free
Prolog variables. Its body: marshal inputs (Prolog→native), call the interop
methods, unify outputs (may fail), return success/fail.

The enclosing predicate emits a `CallBuiltin <block-id>` at the block's position.
So a **Tier-0 (WAM) predicate** runs the block via normal builtin dispatch, and a
**Tier-1 (IL) predicate** calls (or inlines) it the same way. **The block's IL is
orthogonal to the predicate's tier** — there is no need to force native-using
predicates to IL. This reuses the existing CallBuiltin/ExecuteBuiltin + foreign
machinery (chunks 237/248); a `{...}` is, in effect, a compiler-synthesized
foreign predicate.

### 4. Marshalling — atoms ARE .NET strings, so the string tier collapses

In the IL world a Prolog atom is already a .NET `string`, so Arity's char-buffer
copy dance disappears:

- `MakeCString` and `MakePrologString[Ex]` are **intrinsics**, not user methods.
  `MakeCString` lowers to *extracting* (unboxing) the .NET string from the logical
  cell; `MakePrologString` lowers to *unifying* a .NET string into the logical
  variable. They never appear in the interop class.
- Tiers land incrementally: **int / float / string first**; the **whole-term
  (`reftype`)** tier — passed via the Prolog helpers `fill_par` / `reftype_term`
  plus reftype globals — is **deferred** to a later stage.

### 5. C functions → `public static` methods of a foreign interop class

A C call `'Func'(args)` lowers to a call to `Shumway.Native.Interop.Func(...)`:

- Namespace defaults to **`Shumway.Native`**; a **linker option** overrides it.
  Class name **`Interop`**.
- The class lives in the **user's foreign DLL** (the chunk-247 `--foreign-dll`
  path). The emitted IL references it **cross-assembly** — exactly how Shumway's
  IL already calls `Engine` / `Cell` / `BuiltinsRegistry` in other assemblies.
  These references resolve by **name + signature at load** via the CLR; they need
  **no atom-id-style patching** (unlike inline atom/functor constants), so they
  are simpler than the rest of the persisted IL.
- The linker reflects the DLL at link time → `MethodInfo`/`FieldInfo` → emits
  `call` / `ldsfld` / `stsfld`, and **validates against the `:- c` prototypes**
  (missing or mismatched member → link error). At runtime the DLL auto-loads
  (chunk 247) and the CLR resolves the references.

### 6. C globals → `static` fields of the SAME interop class; C linkage for free

A C global defined in a `:- c` region (`char lbuf[255];`, `reftype
pTranslateRef1;`) is a **`static` field of `Shumway.Native.Interop`** in the
user's DLL — the same class as the methods, so the methods reach the globals
directly.

C **linkage falls out of the single shared class**: there is one `Interop` class
with the fields; every module's IL references the same fields by name → the global
is program-wide automatically. An `extern` in one region and the definition in
another both reference the same static field; one definition, references resolved
at link.

Globals **must be fields, not method-locals**: one routine assigns a reference and
another reads it across blocks / clauses / modules. Only a block-local `Var: type`
temporary (declared and used within one block) stays local to that block's
routine.

### 7. C# signature derivation — optional scaffolding only

The `:- c` prototype yields the expected C# static-method signature by simple
type-mapping rules: `int`/`short`→`int`; `long`/`unsigned long`/`int64_t`→`long`;
`float`→`float`, `double`→`double`; `char*`/`const char*`/`pchar`/`cstring`/`psz`→
`string`; `void` return→`void`; `(void)` arg→none; output `&`/pointer params →
*to be defined* (`ref`/`out` vs return value).

An **optional** linker flag emits a C# stub (the `Interop` global fields + method
signatures) as scaffolding / contract. A real project implements the methods in
its own DLL and ignores the stub. Shumway **never requires generating C#** — it
only *references* the class.

## Consequences

- Native-heavy Arity sources run; reuses foreign-DLL + builtin machinery; clean
  two-parser separation; C semantics (global linkage) honored; blocks work in
  both tiers.
- **Native AOT limitation**: a block routine needs codegen — a `DynamicMethod` at
  consult, or baked IL in the bundle at link. Under Native AOT (no runtime
  codegen; persisted IL falls back to bytecode) native blocks need
  AOT-precompilation or are unsupported. This is a documented **AOT corner,
  independent of the Tier-0/Tier-1 choice**.
- **Deferred**: the whole-term (`reftype`) marshalling tier and the
  `fill_par`/`reftype_term` handle flow; the exact mapping of output params
  (`&Var` / pointers) to `ref`/`out` vs a return value; the rare `*deref`,
  arithmetic, and string-literal expression forms.
- **Open**: C-parser error reporting mapped back to source positions; validation
  precedence when a `:- c` prototype and the actual DLL member disagree.

## Alternatives considered

- **Generate the whole `Interop` class as C# source (a `partial` split between a
  user C# assembly and a Reflection.Emit assembly).** Rejected: a `partial class`
  is a C#-compiler construct confined to one assembly; the CLR type identity is
  `(assembly, namespace, name)`, so an IL-emitted member cannot join a user's
  partial class in another assembly. The user owns the class in their DLL;
  Shumway only references it. C# generation survives **only** as optional
  scaffolding (§7).
- **Compile embedded C to native object code.** Out of scope — the target is
  .NET/IL.
- **Keep skipping native blocks (status quo `true` no-op).** Drops semantics;
  insufficient for the real Arity corpus.

## Related

- ADR-010 (embedding API) and the chunk-247/248 `--foreign-dll` mechanism — the
  resolution path this design reuses and extends.
- ADR-011 (IL compiler architecture) — where the block-routine emit lives.
