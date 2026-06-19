# Embedded native C in Shumway (`:- c` / `{ … }`)

Shumway supports Arity-Prolog-style **embedded native code**: a Prolog clause body
can contain `{ … }` blocks of C-like statements that read and write the clause's
Prolog variables and call native functions, and a `:- c.` … `:- prolog.` region
declares the C environment those blocks use.

Because Shumway targets .NET, there is **no C compiler involved**. A `{ … }` block
is compiled into code that marshals values between Prolog and .NET and calls
`public static` methods of a .NET class you provide — by default a class named
`Shumway.Native.Interop`. The embedded C is, in effect, a small DSL for calling
into your .NET interop layer with Prolog↔.NET marshalling generated for you.

> **Status.** This page describes the int / float / string tier, working both
> in-process (via `ConsultString`) and through the separate-compilation / bundle
> pipeline (`shumway-compile` → `shumway-link`, including source-stripped Release
> bundles and the generated `--exe`). The term/reftype tier and IL emission are in
> progress — see *Limitations*.

---

## 1. Enabling the feature

Embedded native code is part of Arity compatibility mode. Turn it on at the top of
your source:

```prolog
:- set_prolog_flag(arity_compat, true).
```

(or compile with `shumway-compile --arity`). With the flag off, `{ X }` keeps its
ISO `{}/1` meaning and `:- c` is just an unknown directive.

There are three pieces to a native-using program:

1. a `:- c` **region** — the C declarations;
2. one or more `{ … }` **blocks** — the native goals, inside clause bodies;
3. your **interop class** — the `public static` C# methods that back the C
   functions.

---

## 2. The `:- c` region — declarations

Everything between `:- c.` and the terminating `:- prolog.` is C declaration text.
Shumway extracts what it needs and skips the rest (preprocessor lines, comments,
structs, function-pointer typedefs…), so a region full of preprocessed headers is
fine. What is used:

```prolog
:- c.
char    lbuf[255];                         % a global buffer
int     strcmp(const char*, const char*);  % a function prototype
typedef char *pchar;                       % a typedef
:- prolog.
```

- **Function prototypes** give the parameter and return **types** used to infer the
  variables a block marshals, and the **C# signature** you must implement.
- **Globals / buffers** become `static` fields of the interop class (you provide
  them).
- **Typedefs** resolve type names (`pchar` → `char*` → a .NET `string`).

A declared global is global to the whole program (C linkage): a region in one
module may `extern`-declare it and reference it.

---

## 3. The `{ … }` block — native goals

A block is a goal in the clause body, executed in place; the clause continues after
it. It is a **linear** sequence of statements (no C control flow) separated by `;`
or `,` (interchangeable):

| Form | Example | Meaning |
|------|---------|---------|
| Local declaration | `ret: long` | a native local of the given C type |
| Prolog bind | `X is 'strcmp'(a, b)` | evaluate the right side natively, **unify** the result into the Prolog variable `X` |
| C assignment | `ret = 'f'(x)` | native assignment (no Prolog unification) |
| Call | `'f'(args)` | a native call as a statement |

Expressions support native calls `'Name'(args)` (the quotes are optional), variable
and global references, integer literals, simple arithmetic (`+ - * /`), `&Var`
(address-of — used by the string intrinsics below), and string literals. `%` starts
a line comment **inside** a block (Arity convention).

```prolog
string_length_bytes(S, L) :-
    atom(S),
    { Len: int;
      Len is 'strlen'(S) },      % Len gets strlen(S)
    integer(Len), !,
    L = Len.
```

---

## 4. You must let the compiler infer each variable's type and mode

For every Prolog variable a block marshals, Shumway must determine its **.NET
type** and its **direction** (input read on entry / output unified on exit). It
does this from the surrounding **Prolog guards** plus the block's own structure. If
it cannot, **consulting fails with an error** (§7) — a block is never silently
ignored.

So: **guard the variables your block uses.**

| Guard | Meaning for the variable |
|-------|--------------------------|
| `integer(X)` | integer (`int`/`long`) |
| `float(X)` | float (`double`) |
| `atom(X)` / `string(X)` | string |
| `var(X)` *(before the block)* | unbound → **output** (the block assigns it) |
| `nonvar(X)` *(before the block)* | already bound → **input** |

- Put input guards **before** the block (e.g. `integer(Mod)`, `atom(S)`).
- For an output, put `var(X)` before, **or** a type guard after the block
  (e.g. `…, { Len is … }, integer(Len)`).

The type can also come from a block-local `Var: type` declaration, from the C type
of an `is` right-hand side (a local, a prototype's return type, an integer
literal), or from the string intrinsics below — so an explicit guard is not always
required, but adding one is the reliable way to make a block compile.

### The string intrinsics (you do **not** implement these)

In Arity these copy strings to/from C `char` buffers. In .NET a Prolog atom already
*is* a `string`, so they are **intrinsics** Shumway lowers directly:

- `'MakeCString'(buffer, length, &Var)` — marks `Var` a **string input**. The
  buffer and length are vestigial in .NET; you ignore them.
- `'MakePrologString'(source, &Var)` / `'MakePrologStringEx'(source, &Var)` — marks
  `Var` a **string output**, bound to the source string.

They are recognised by name; do not put them in your interop class.

---

## 5. The interop class — your `public static` methods

Every native function a block calls (other than the intrinsics) must be a
`public static` method of your interop class. The expected **signature** is derived
from the `:- c` prototype:

| C type | .NET type |
|--------|-----------|
| `int`, `short` | `int` |
| `long`, `unsigned long`, `int64_t` | `long` |
| `float` | `float`; `double` → `double` |
| `char*`, `const char*`, `pchar`, `cstring`, `psz` | `string` |
| `void` (return) | `void` |

```prolog
:- c.
int strcmp(const char*, const char*);
:- prolog.
```

```csharp
namespace Shumway.Native;

public static class Interop
{
    public static int strcmp(string a, string b) => string.CompareOrdinal(a, b);
    // globals declared in `:- c` go here too, as static fields.
}
```

A C global declared in the `:- c` region (e.g. `char par2str[10240];`) is a
`static` field of this same class, so your methods can read and write it directly.

---

## 6. Registering the interop class — `UseNativeInterop` vs. auto-discovery

Tell the engine which class to use **before** consulting native-using source:

```csharp
var engine = new PrologEngine();
engine.UseNativeInterop(typeof(Shumway.Native.Interop));   // recommended
engine.ConsultString(source);
```

This is the recommended path: it is explicit, has **no discovery cost**, and the
class may have **any name** (it need not be `Shumway.Native.Interop`).

If you never call `UseNativeInterop`, the engine **auto-discovers** a class named
exactly `Shumway.Native.Interop` on the first time it needs to resolve a native
function. Be aware of the trade-offs:

- it performs a **one-time reflection scan of every loaded assembly**, which can be
  noticeable in a large application with many assemblies;
- it only finds a type with that exact full name — a differently named class is not
  discovered (use `UseNativeInterop` for those);
- if no such class is found, every block that calls a native function fails to
  consult (§7).

So in any real application, call `UseNativeInterop(typeof(YourClass))` explicitly.

---

## 7. Errors are loud — blocks are never silently ignored

A native block that the compiler cannot handle raises a **consult error** — it is
never silently turned into a no-op. A no-op'd block would make the program
misbehave without you noticing, so consulting fails instead, with a message naming
the problem. This happens when:

- the block uses **unsupported syntax** — C control flow, or the deferred
  term/reftype tier (`->`, `..`, `preftype`);
- a **variable's type or mode cannot be inferred** (add a guard, §4); or
- it **calls a native function your interop class does not provide** (the message
  names the function; register the class with `UseNativeInterop` and implement the
  method).

So set up your interop class **before** consulting, and make sure every native
function a block calls exists. If a source uses a native construct Shumway does not
yet support, it will not consult until that support lands — by design.

---

## 8. A complete example

```csharp
// MyInterop.cs
namespace Demo;

public static class TextInterop
{
    public static int strcmp(string a, string b) => System.Math.Sign(string.CompareOrdinal(a, b));
    public static long sum(long a, long b) => a + b;
}
```

```csharp
// Program.cs
using Shumway.Embedding;

var engine = new PrologEngine();
engine.UseNativeInterop(typeof(Demo.TextInterop));

engine.ConsultString("""
    :- set_prolog_flag(arity_compat, true).
    :- c.
    int  strcmp(const char*, const char*);
    long sum(int, int);
    :- prolog.

    compare(A, B, R) :- atom(A), atom(B), { R is 'strcmp'(A, B) }, integer(R).
    calc(A, B, R)    :- integer(A), integer(B),
                        { T: long; T is 'sum'(A, B); R is T * 2 }, integer(R).
    """);

System.Console.WriteLine(engine.Query("compare(abc, abd, R).").Get<long>("R")); // -1
System.Console.WriteLine(engine.Query("calc(3, 4, R).").Get<long>("R"));        // 14
```

---

## 9. Bundles and separate compilation

Native blocks survive the whole `.pl → .shmo → .shum` pipeline. The compiler
rewrites each `{ … }` block to a portable internal dispatch and records the
block's marshalling data in the object/bundle; at load the engine repopulates its
block table, so even a **source-stripped Release bundle** (and the native `--exe`
it produces) runs the blocks. There is one rule to remember:

- **Register your interop class before loading the bundle.** Interop resolution is
  not baked at compile time (the compiler doesn't know your class), so call
  `engine.UseNativeInterop(typeof(YourClass))` *before* `engine.LoadBundle(...)`,
  exactly as for `ConsultString`. A block that calls a function the running
  engine's interop class does not provide raises a hard error when it executes
  (§7) — never a silent no-op.

A native block inside a `:- dynamic` predicate is rejected at compile time (its
clauses are rehydrated without the native transform, so the block would be
inert). Put native blocks in static predicates.

> **Interop resolution is checked at run time, not at link time — by design.** The
> linker cannot know which interop class the running engine will register (you may
> call `UseNativeInterop` at run time with any class), so validating a block's
> calls against a `--foreign-dll` at link time would reject programs that are in
> fact correct. Resolution therefore happens when a block executes, where it is a
> hard error if a called function is missing (§7).

---

## 10. Performance and limitations

- **Compiled to IL.** A block is never tree-walked on a hot path:
  - in a **Tier-1 IL** predicate (runtime promotion, or a `--with-compiled-il`
    bundle), the block is **inlined directly into the predicate's IL** — its
    marshalling, arithmetic and interop calls become IL in the predicate's own
    method, with no `$native_run` dispatch. In a persisted bundle the interop call
    is a direct cross-assembly call, bound by the CLR at load (the build resolves
    the interop class — `Shumway.Native.Interop`, auto-discovered, or whatever the
    build engine registered);
  - otherwise (Tier-0, or a block the inliner declines) the block is compiled to a
    delegate (an Expression tree → JIT IL) on first execution — no per-call
    dictionaries, boxing or tree-walk, interop calls direct;
  - the small interpreter remains only as a fallback for constructs neither code
    generator handles and for Native AOT (no run-time IL generation).

  None of this changes the surface you write.
- **int / float / string tier.** Whole-term marshalling (Arity's `reftype` /
  `preftype` machinery, `fill_par` / `reftype_term`, `->` and `..`) and C control
  flow are not supported yet; a source using them raises a consult error (§7)
  rather than running incorrectly.

See `docs/architecture/adr/022-embedded-native-c-blocks.md` for the design and
rationale.
