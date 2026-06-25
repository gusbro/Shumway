# Generic Prolog-term interop (the reftype tier)

This page covers passing **whole Prolog terms** — compounds, lists, nested
structures, not just scalars — between Prolog and your .NET code, the Arity
`reftype` way. It builds on [embedded native C](embedded-native-c.md) (the
`:- c` / `{ … }` machinery and `UseNativeInterop`); read that first.

> The int / float / string tier (page above) marshals a *value* in and out of a
> block. This tier hands your .NET function a **cursor over a real Prolog term** so
> it can inspect any shape and build any result — with no copy.

---

## 1. The model: a reftype is a cursor, not a copy

In Arity a `reftype` is a C struct that mirrors a Prolog term (a tag, an arity, an
argument array, a value). The C code copies a term into that struct, works on it,
and copies it back — because Arity's C runs in a separate process and cannot touch
the Prolog heap.

Shumway's interop runs **in-process**, so there is no copy. A `reftype` /
`preftype` is a lightweight **`TermSlot`** — a cursor over the actual term in the
heap. Your .NET function reads its shape and builds into it directly. This is the
interop speed advantage; nothing is serialized to a struct.

---

## 2. The flow

A predicate that calls into .NET with a term and gets one back has a fixed shape:

```prolog
:- set_prolog_flag(arity_compat, true).
:- c.
reftype buf;                 % a term slot (see §3)
int    swap_pair(reftype);   % your .NET function, takes the slot
:- prolog.

swap(P, Q) :-
    { Ptr: preftype; Ptr is &buf },     % 1. get the slot cursor
    fill_par(P, Ptr),                   % 2. term  → slot   (P into the cursor)
    { ret: int; ret = 'swap_pair'(buf); Ret is ret },   % 3. call .NET with the slot
    Ret =:= 1,
    reftype_term(Q, Ptr).               % 4. slot → term    (cursor into Q)
```

1. **`Ptr is &buf`** — `Ptr` becomes the cursor for the slot `buf`.
2. **`fill_par(Term, Ptr)`** — stores the Prolog term into the slot.
3. **the native call** — your function gets the slot and reads / builds the term.
4. **`reftype_term(Term, Ptr)`** — materializes the slot's term and unifies it.

`fill_par/2` and `reftype_term/2` are built in; you do not define them (their Arity
`prlg_ifce.pl` definitions, if present, are recognized and replaced).

---

## 3. Declaring term slots

A **term slot** is named, and you get it with `&name`. Slots are declared as
`reftype` globals in a `:- c` region:

```prolog
:- c.
reftype buf;          % your own slot
reftype par1ref;      % Arity's predefined buffers are just reftype globals too
:- prolog.
```

Recognition is **by type, not by name**: any global declared `reftype` (or
`preftype` / `t_reftype`) is a slot. The `par1ref … par10ref` buffers Arity
programs use are nothing special — they are `reftype` globals declared in
`prlg_ifce.pl`. Declare your own the same way and use them identically.

A slot follows C global linkage: declare it in one module, `extern reftype buf;`
to reference it from another, one shared slot per name. Slots persist across
queries (an Arity buffer is reused between calls; `fill_par` overwrites it).

---

## 4. The .NET side — two APIs over the same cursor

Your interop function receives a `Shumway.Embedding.TermSlot` for each `reftype`
parameter. There are two equivalent APIs over it; pick per function.

### 4a. The Arity `*_c` compatibility API

Static methods with the Arity names (pointers become `out`, C buffers become
`string`), so existing Arity C# runs almost unchanged. `using static`:

```csharp
namespace Shumway.Native;

using Shumway.Embedding;
using static Shumway.Embedding.ReftypeApi;

public static class Interop
{
    // swap_pair: pair(A, B) → pair(B, A)   (A, B integers)
    public static int swap_pair(TermSlot r)
    {
        if (findtype_c(r) != 5) return 0;                  // 5 = functor (§6)
        getfunctor_c(r, out var name, out var arity);
        if (name != "pair" || arity != 2) return 0;

        getfuncarg_c(r, 1, out var a); getint_c(a, out var av);   // read args
        getfuncarg_c(r, 2, out var b); getint_c(b, out var bv);

        putfunctor_c("pair", 2, r);                        // build pair(bv, av)
        getfuncarg_c(r, 1, out var n1); putint_c(bv, n1);
        getfuncarg_c(r, 2, out var n2); putint_c(av, n2);
        return 1;
    }
}
```

The full set: `findtype_c`, `getint_c` / `putint_c`, `getflt_c` / `putflt_c`,
`gettxt_c` / `puttxt_c`, `putatm_c`, `getfunctor_c` / `putfunctor_c`,
`getfuncarg_c`, `equrefs_c`. The `get*` return `bool` (false when the slot isn't
that kind); the `put*` are `void`.

### 4b. The native Shumway API

The same operations as methods on `TermSlot` — idiomatic for new code:

```csharp
public static int swap_pair(TermSlot r)
{
    if (r.FindType() != TermSlot.Functor) return 0;
    r.GetFunctor(out var name, out var arity);
    if (name != "pair" || arity != 2) return 0;

    r.Arg(1)!.GetInt(out var av);
    r.Arg(2)!.GetInt(out var bv);

    r.PutFunctor("pair", 2);
    r.Arg(1)!.PutInt(bv);
    r.Arg(2)!.PutInt(av);
    return 1;
}
```

Methods: `FindType()`, `GetInt`/`PutInt`, `GetFloat`/`PutFloat`,
`GetText`/`PutAtom`, `GetFunctor`/`PutFunctor`, `Arg(n)` (the n-th argument slot,
1-based), `TermEquals`. The ntype constants are `TermSlot.Undef` … `Functor`.

Register the class before consulting / loading, exactly as for the value tier:

```csharp
engine.UseNativeInterop(typeof(Shumway.Native.Interop));
```

---

## 5. Reading and building — the cursor pattern

**Reading.** `FindType()` / `findtype_c` tells you the shape; then the matching
getter. For a compound, `Arg(n)` / `getfuncarg_c(r, n, out arg)` gives a cursor for
argument `n`, which you read recursively.

**Building.** To produce a result you build *into* the slot:

- a scalar — `PutInt` / `PutFloat` / `PutAtom` (or `put*_c`);
- a compound — `PutFunctor(name, arity)` reserves the functor and its argument
  slots; then `Arg(n)` (or `getfuncarg_c`) gives each argument slot, which you fill
  the same way, recursing for nested structure.

`reftype_term/2` then materializes whatever you built and unifies it. Building
mirrors Arity's `putfunctor` + `getfuncarg` recursion, but constructs the real
Prolog term directly — no struct in between.

A list is the functor `'.'/2` (with `[]` the empty list): `findtype_c` reports `5`,
`getfunctor_c` reports `./2`, and `getfuncarg_c` walks head / tail.

---

## 6. The ntype codes

`findtype_c` / `FindType()` return:

| code | kind | notes |
|------|------|-------|
| 0 | undef | an unbound variable |
| 1 | integer | |
| 2 | floating | |
| 3 | atom | |
| 4 | string | an atom reads back as **4** (Arity uses "string" for atoms) |
| 5 | functor | a compound; a list is `'.'/2` |
| 6 | nontype | treated as undef |

Both `putatm_c` and `puttxt_c` build an atom (in Shumway an Arity "string" is an
atom). The constants are also on `TermSlot` (`TermSlot.Integer`, `…Functor`, etc.).

---

## 7. A complete example

```csharp
// Interop.cs
namespace Shumway.Native;
using Shumway.Embedding;
using static Shumway.Embedding.ReftypeApi;

public static class Interop
{
    public static int swap_pair(TermSlot r)
    {
        if (findtype_c(r) != 5) return 0;
        getfunctor_c(r, out var name, out var arity);
        if (name != "pair" || arity != 2) return 0;
        getfuncarg_c(r, 1, out var a); getint_c(a, out var av);
        getfuncarg_c(r, 2, out var b); getint_c(b, out var bv);
        putfunctor_c("pair", 2, r);
        getfuncarg_c(r, 1, out var n1); putint_c(bv, n1);
        getfuncarg_c(r, 2, out var n2); putint_c(av, n2);
        return 1;
    }
}
```

```csharp
// Program.cs
using Shumway.Embedding;

var engine = new PrologEngine();
engine.UseNativeInterop(typeof(Shumway.Native.Interop));

engine.ConsultString("""
    :- set_prolog_flag(arity_compat, true).
    :- c.
    reftype buf;
    int     swap_pair(reftype);
    :- prolog.

    swap(P, Q) :-
        { Ptr: preftype; Ptr is &buf },
        fill_par(P, Ptr),
        { ret: int; ret = 'swap_pair'(buf); Ret is ret },
        Ret =:= 1,
        reftype_term(Q, Ptr).
    """);

System.Console.WriteLine(engine.Query("swap(pair(1, 2), Q).")["Q"]); // pair(2, 1)
```

---

## 8. String holders (Arity buffers)

Arity programs pass C **strings** through reusable global buffers (`char par1str[]`,
`char* buf`) using `make_c_string` / `make_prolog_string`. Shumway models a buffer
global as a **string holder** — the same slot machinery, holding a string:

```prolog
:- c.
char* buf;          % a reusable string holder (a slot)
:- prolog.
fmt(In, Out) :-
    { H: pchar; H is buf },          % H = the holder slot
    make_c_string(H, 100, In, _),    % holder := In        (set, a copy)
    make_prolog_string(H, Out).      % Out = the holder    (read)
```

- A global declared `char*` / `char[]` in a `:- c` region is a holder slot; a
  variable assigned from it (`H is buf`) is a holder cursor.
- **`make_c_string(Holder, _, Value, _)`** stores `Value` into the holder (a copy —
  successive fills of the same buffer do **not** alias their Prolog values).
- **`make_prolog_string(Holder, Var)`** reads the holder's current value into `Var`.
- When the first argument is a plain **atom** (a value, not a holder — e.g. a
  predicate parameter that already holds a string), both degrade to identity
  (`make_prolog_string(CStr, Var)` unifies `Var = CStr`). So both the buffer pattern
  and direct value conversions work.

(The max-length / actual-length arguments of `make_c_string` are vestigial in .NET.)

---

## 9. Notes and limitations

- **Set up your interop class before consulting / loading.** As for the value
  tier, an interop function is resolved at run time; a block that calls a function
  your class does not provide fails when it runs (it is never a silent no-op).
- **Bundles work unchanged.** A reftype predicate compiles into a `.shmo` / `.shum`
  like any native predicate; the block's slots are created on first reference at
  run time, so a source-stripped release bundle (and the generated `--exe`) run it
  identically — no `:- c` declarations need to ship.
- **Reftype blocks compile to IL.** On first execution a reftype block compiles to
  a delegate (no per-call dictionaries / boxing / tree-walk); and when a reftype
  predicate promotes to Tier-1 IL, the whole flow becomes one IL method — the
  blocks are inlined and `fill_par` / `reftype_term` are fused in, so there is no
  per-call dispatch. A hot loop runs the reftype flow at full IL speed. (The small
  interpreter remains only as a fallback for constructs the code generators don't
  handle and for Native AOT.)
- **Calling native C through a trampoline.** The cursor is for logic *in C#*. If
  your C# only forwards to a native C function (P/Invoke), that C cannot touch the
  Shumway heap — a future *materializer* tier (not yet implemented) will copy a
  term to and from a physical `Reftype` struct for that case. See
  [ADR-024](architecture/adr/024-generic-term-interop.md).

See `docs/architecture/adr/024-generic-term-interop.md` for the design and
rationale.
