# C# ↔ Prolog interop

Shumway runs the Prolog engine **in the same process and managed heap as your C#
code**. That changes what interop can be: a term is not an opaque handle on the
far side of a marshalling boundary — it *is* a value in a `Cell[]` your C# can
read and write directly. This guide is the router for the whole topic: the
four managed mechanisms below in depth, and where the two native-code
mechanisms (their own documents) fit.

| Mechanism | Direction | Cost | Use when |
|---|---|---|---|
| [Typed foreign predicates](#1-typed-foreign-predicates-convenience) | Prolog → C# | Allocates (Term-AST) | Convenience, cold paths |
| [Typed queries](#2-typed-queries) | C# → Prolog | Allocates (Term-AST) | Driving the engine from a host |
| [Re-entrant `SolveOnce`](#3-re-entrant-solveonce) | C# → Prolog, mid-query | Low | A foreign method calling back into Prolog |
| [**Zero-copy cell access**](#4-zero-copy-cell-access-hot-path) | both | **Lowest** | **Hot paths: traversing/building terms** |
| [Embedded native C blocks](embedded-native-c.md) | Prolog → C#/native | Compiled to IL | Arity-style `{ … }` blocks inside clause bodies |
| [Reftype term marshalling](generic-term-interop.md) | Prolog ↔ native C | Snapshot / P/Invoke | Whole terms into native `t_reftype` graphs |

The first two are *convenience* interop: you work with ordinary C# values
(`int`, `string`, `List<long>`, your own records) and Shumway converts. The last
is the *hot-path* mechanism: you touch the engine's cells directly, no
conversion, no allocation. Both matter — pick convenience for the 95% that isn't
performance-critical, and zero-copy for the inner loop.

---

## 1. Typed foreign predicates (convenience)

Annotate a C# method and register its type. The source generator writes the
bridge that decodes arguments and encodes the return.

```csharp
public static partial class MyPredicates
{
    [PrologPredicate("inc/2")]                 // inc(+X, -Y)
    public static int Inc(int x) => x + 1;

    [PrologPredicate("sum_list_c/2")]          // sum_list_c(+List, -Sum)
    public static long SumList(List<long> xs)
    {
        long s = 0;
        foreach (var x in xs) s += x;
        return s;
    }
}

engine.RegisterPredicates(typeof(MyPredicates));
// ?- inc(41, Y).          Y = 42
// ?- sum_list_c([1,2,3], S).   S = 6
```

Parameter modes map from C# modifiers: plain → `+`, `out T` → `-`, `ref T?` →
`?`. Return `IEnumerable<T>` with `NonDeterministic = true` for a backtrackable
predicate. Errors: throw `PrologRuntimeException` to raise a catchable `error/2`.

**Advantage:** you write plain C#; no knowledge of the WAM. **Disadvantage:**
each composite argument/return is decoded through an intermediate `Term` tree —
`List<long>` means one `IntTerm` object per element, plus the `List`. That is
real GC traffic. Fine off the hot path; see §4 for the inner loop.

---

## 2. Typed queries

Drive the engine from a C# host and read results as C# types.

```csharp
foreach (var sol in engine.QueryAll("member(X, [a,b,c])."))
    Console.WriteLine(sol.Get<string>("X"));      // a, b, c

int y = engine.QueryFirst<int>("inc(41, Y).", "Y");   // 42
foreach (var p in engine.Query<Person>("lookup(P).", "P")) { ... }
```

`Get<T>` / `Query<T>` use the same converter tier as §1 (user converters →
scalars → composites → `[PrologTerm]` convention). Same trade-off: ergonomic,
allocates a `Term` tree per binding read.

`QueryAll` opens a **top-level query** each call (it links the transient region,
sets up a fresh machine). That per-call setup is fine to pay once when a host
asks the engine to solve a goal; it is *not* what you want in a tight loop of
tiny calls. For calling Prolog repeatedly from inside a running query, use §3.

---

## 3. Re-entrant `SolveOnce`

The embedding pattern is often `C# → main → C#(method) → Prolog(goal) → …`: a
foreign method, running mid-query, wants to call a Prolog goal *back on the live
engine* — not spin up a new top-level query. `SolveOnce` does exactly that,
reusing the already-linked program.

```csharp
[PrologPredicate("classify/2")]                       // classify(+X, -Tag)
public static string Classify(Activation engine, int x)
{
    var host = (PrologEngine)engine.Host!;
    // call a Prolog predicate re-entrantly; read one output
    host.SolveOnce<string>(engine,
        new CompoundTerm("category_of", new Term[] { new IntTerm(x), new VarTerm("T") }),
        "T", out string tag);
    return tag;
}
```

A `[PrologPredicate]` method may take an `Activation` parameter anywhere in its
signature (the bridge passes the live engine). Overloads:

- `SolveOnce<T>(engine, goal, outVar, out T value)` — lean; reads one named
  output, no `Solution` object (the cheapest form).
- `SolveOnce(engine, goal, out Solution sol)` — general; full bindings.
- `SolveOnce(engine, goal)` — semidet check, discards bindings.

Bindings the goal makes persist on the shared heap and are visible to the outer
computation (and correctly undone if it later backtracks past the call).

**Database mutation.** The goal you pass is a real goal on the live engine, so a
foreign method can mutate the Prolog database — `assertz`, `asserta`, `retract`,
`abolish` — through the same mechanism:

```csharp
[PrologPredicate("remember/1")]                       // remember(+N)
public static bool Remember(Activation engine, int n)
{
    var host = (PrologEngine)engine.Host!;
    return host.SolveOnce(engine, new CompoundTerm("assertz",
        new Term[] { new CompoundTerm("fact", new Term[] { new IntTerm(n) }) }));
}
```

The change is effective immediately, **visible to later goals of the same query**
(the ISO logical update view), and **persists** into subsequent queries — the
dynamic store lives on the engine, shared with the activation.

**Advantage:** ~60× cheaper per call than a top-level `QueryAll` (reuses the
linked program); nests correctly (`C# → Prolog → C# → Prolog`). **Disadvantage:**
building the goal `Term` and reading a composite output still go through the
Term-AST tier — for scalar in/out this is light, for large lists prefer §4.

---

## 4. Zero-copy cell access (hot path)

This is the mechanism the other three are conveniences over, and the one that
makes an in-process engine worth having. A term lives in the engine's managed
`Cell[]` heap. From a foreign predicate you get the live `Activation` and can
**read and write those cells directly** — no `Term` tree, no `List<T>`, no copy.
A native engine embedded over a P/Invoke boundary *cannot* do this: its cells are
in unmanaged memory C# may not touch, so every term must be marshalled across.
Here the C# side is less pretty, but it runs at cache speed.

Use the **raw foreign form** — a `bool(Activation)` method. The generator leaves
it alone; `RegisterPredicates` registers it as a builtin whose arity comes from
the attribute. It reads its arguments from the argument registers and unifies its
results back.

### Cell primitives

Everything below is on `Shumway.Core` (`Activation`, `Cell`, `Tag`, `AtomTable`,
`FunctorTable`):

| Call | Meaning |
|---|---|
| `engine.GetRegister(i)` | argument `i` (the i-th predicate argument), as a `Cell` |
| `engine.Deref(heapIdx)` | follow the bound-variable chain, returns the final heap index |
| `engine.GetHeap(idx)` / `SetHeap(idx, cell)` | read / write a heap cell |
| `engine.AllocateHeap(n)` | reserve `n` contiguous heap cells, returns the base index |
| `engine.UnifyRegisterWithCell(i, cell)` | unify argument `i` with a cell (bind the output) |
| `Cell.Int(v)` `Cell.Atom(id)` `Cell.Lis(pairIdx)` `Cell.Str(fnIdx)` `Cell.Functor(fid)` | build cells |
| `cell.AsInt` `cell.AsAtomId` `cell.AsHeapIndex` `cell.Tag` | read a cell |
| `engine.TryUnconsListLike(c, out h, out t)` | peel one element off **any** list — cons or packed text |
| `Activation.IsListLike(c)` | true for a non-empty list of either storage |

Cell layout (ADR-002 / ADR-017):

- **List** `[H|T]`: a `Tag.Lis` cell whose `AsHeapIndex` points at a 2-cell pair
  `[head, tail]`. The empty list is `Cell.Atom(AtomTable.EmptyListId)`. A list of
  text may instead be **packed** into a `Tag.Pstr` header — same list, denser
  storage; see "Lists of text" below.
- **Compound** `f(A0,…,An)`: a `Tag.Str` cell whose `AsHeapIndex` points at a
  `Tag.Functor` cell, immediately followed by the argument cells.
- **Unbound variable**: `Tag.Ref`. Always `Deref` before inspecting a cell.

A one-line deref helper you will reuse:

```csharp
static Cell Dr(Activation e, Cell c) => c.Tag == Tag.Ref ? e.GetHeap(e.Deref(c.AsHeapIndex)) : c;
```

### Traversing a term

Walk a list and sum its integers — no allocation, one pass over the live cells:

```csharp
[PrologPredicate("sum_direct/2")]                     // sum_direct(+List, -Sum)
public static bool SumDirect(Activation e)
{
    Cell c = Dr(e, e.GetRegister(0));                 // argument 0: the list
    long sum = 0;
    while (c.Tag == Tag.Lis)
    {
        int pair = c.AsHeapIndex;                     // [head, tail]
        sum += Dr(e, e.GetHeap(pair)).AsInt;          // head, an integer
        c = Dr(e, e.GetHeap(pair + 1));               // advance to tail
    }
    // c is now [] (proper list) or a var (partial) — check if you need to
    return e.UnifyRegisterWithCell(1, Cell.Int(sum)); // bind argument 1
}
```

> **A list is not always `Tag.Lis`.** The loop above is correct for a list of
> integers built by ordinary means, and wrong for a list of **text**. Read the
> next subsection before walking any list that might hold characters or codes.

### Lists of text: use the cursor, not the tag

A list of characters or codes may be stored **packed** — one `Tag.Pstr` header
plus 3 UTF-16 code units per cell, instead of the `2n+1` cells a cons list
costs. It is still a list: it unifies with `[H|T]`, `is_list/1` is true of it,
and it is `==` to the cons list of the same content (ADR-047). Only the storage
differs, and at this tier you can see the storage — that is the whole point of
this tier.

That means a hand-written `while (c.Tag == Tag.Lis)` over text **silently
computes an answer over zero elements**. It does not throw and does not fail;
the loop just never starts. The same predicate then returns different answers
for two Prolog lists that are `==`, depending on how each was built.

Peel elements with the cursor instead. It handles both shapes and costs the same
as the tag test on the cons path:

```csharp
[PrologPredicate("count_letter_a/2")]                 // count_letter_a(+Text, -N)
public static bool CountLetterA(Activation e)
{
    Cell c = Dr(e, e.GetRegister(0));
    long n = 0;
    while (e.TryUnconsListLike(c, out Cell head, out Cell tail))
    {
        Cell h = Dr(e, head);
        if (h.Tag == Tag.Int && h.AsInt == 'a') n++;          // codes
        else if (h.Tag == Tag.Atom && h.AsAtomId == AId) n++; // chars
        c = Dr(e, tail);
    }
    return e.UnifyRegisterWithCell(1, Cell.Int(n));
}
```

| instead of | use |
|---|---|
| `c.Tag == Tag.Lis` | `Activation.IsListLike(c)` |
| `e.GetHeap(pair)` / `e.GetHeap(pair + 1)` | `e.TryUnconsListLike(c, out head, out tail)` |
| assuming `[]` terminates | `e.NormalizeListCell(c)`, then test for `[]` |

Two notes on the loop above:

- **The head's tag depends on the list, not on the storage.** A list of codes
  yields `Tag.Int`, a list of chars yields `Tag.Atom` — exactly as the cons list
  would. Compare atom **ids** (`AtomTable.Intern("a").Id`, hoisted into a static
  like `AId`), never strings.
- **Do not assume the tail is `[]`.** A packed list may be *partial* — its tail
  an unbound variable — which is what makes lazy stream reading possible. The
  cursor returns that tail to you as a `Tag.Ref`; treat it as you would a
  partial cons list.

If you only need the text as a .NET string, do not walk it at all:

```csharp
Cell c = Dr(e, e.GetRegister(0));
if (c.Tag == Tag.Pstr) { string s = e.ReadPstrChain(c, out Cell tail); /* ... */ }
```

**Building is unaffected.** Cons cells are always valid; nothing here is ever
obliged to produce packed text. When you do want the cheaper representation,
`MakePstr` allocates it and returns the header's heap index:

```csharp
return e.UnifyRegisterWithCell(1, e.GetHeap(e.MakePstr(text)));
```

Reading a compound is the same idea — index past the functor cell:

```csharp
[PrologPredicate("rec_id/2")]                         // rec_id(+rec(Id,_,_), -Id)
public static bool RecId(Activation e)
{
    Cell rec = Dr(e, e.GetRegister(0));               // a rec/3 structure
    int fn = rec.AsHeapIndex;                          // fn = functor cell; args at fn+1, fn+2, fn+3
    long id = Dr(e, e.GetHeap(fn + 1)).AsInt;          // first argument
    return e.UnifyRegisterWithCell(1, Cell.Int(id));
}
```

Atoms are interned integers: `Dr(e, headCell).AsAtomId` gives you the id, and
comparing ids is a plain integer compare — no string is materialised. Only call
`AtomTable.GetById(id)?.Name` when you actually need the .NET string (it returns
the already-interned string; no per-call transcode).

### Building a term

Write the cells the engine will use, directly. Build a list `[1..n]`:

```csharp
[PrologPredicate("iota_direct/2")]                    // iota_direct(+N, -List)
public static bool IotaDirect(Activation e)
{
    int n = (int)Dr(e, e.GetRegister(0)).AsInt;
    Cell tail = Cell.Atom(AtomTable.EmptyListId);      // []
    for (int i = n; i >= 1; i--)                        // build tail-first
    {
        int pair = e.AllocateHeap(2);                  // a fresh [head, tail] cell pair
        e.SetHeap(pair, Cell.Int(i));                  // head
        e.SetHeap(pair + 1, tail);                     // tail
        tail = Cell.Lis(pair);                         // a Lis cell pointing at the pair
    }
    return e.UnifyRegisterWithCell(1, tail);
}
```

Build a compound `rec(Id, [items…], Name)`:

```csharp
static readonly int RecFid  = FunctorTable.Intern(AtomTable.Intern("rec").Id, 3);
static readonly int NameId  = AtomTable.Intern("name_atom").Id;

[PrologPredicate("make_rec/3")]                       // make_rec(+Id, +List, -rec(...))
public static bool MakeRec(Activation e)
{
    int id = (int)Dr(e, e.GetRegister(0)).AsInt;
    Cell list = Dr(e, e.GetRegister(1));               // reuse the caller's list cell as-is
    int fn = e.AllocateHeap(4);                        // functor + 3 args
    e.SetHeap(fn,     Cell.Functor(RecFid));
    e.SetHeap(fn + 1, Cell.Int(id));
    e.SetHeap(fn + 2, list);
    e.SetHeap(fn + 3, Cell.Atom(NameId));
    return e.UnifyRegisterWithCell(2, Cell.Str(fn));
}
```

**Advantage:** the fastest interop there is — you read and write the engine's own
memory, so there is nothing to copy and nothing for the GC to collect. In the
benchmarks (§below) it beats a native engine embedded over P/Invoke by 3–180× on
list and term work, precisely because the native engine *must* copy where Shumway
does not. **Disadvantage:** you are writing at the level of the abstract machine —
you must respect the cell layout, deref before inspecting, and get unification
right; there is no type safety and mistakes corrupt the heap. Reserve it for the
inner loop, keep the surface (§1–3) for everything else.

---

## Native C interop

For calling **native C** (not .NET) with whole terms — P/Invoke to a C library
that manipulates term snapshots — Shumway has a separate materializer/reftype
tier (`:- native`, `:- c`, the `reftype` cursor). That is its own subject; see
[generic-term-interop.md](generic-term-interop.md) and
[embedded-native-c.md](embedded-native-c.md).

---

## Performance

Measured Prolog→C# per crossing, marshalling only (loop/dispatch baseline
subtracted), against **GNU Prolog embedded in the same C# host via P/Invoke** (a
native engine — every crossing must marshal). Oracle-verified; representative
figures, ns/call.

| Operation | GProlog (P/Invoke) | Shumway convenience (§1) | Shumway zero-copy (§4) |
|---|---:|---:|---:|
| integer scalar | ~95 | ~30 | (n/a — scalars don't copy) |
| int list, read (len 100) | ~700 | ~24000 | **~190** |
| int list, build (len 100) | ~1400 | ~11500 | **~750** |
| atom list, read (len 50) | ~3400 | ~12000 | **~20** |
| atom list, build (len 50) | ~5100 | ~10900 | **~280** |
| compound, traverse | ~800 | — | **~free** |
| compound, build | ~680 | — | **~290** |

Two honest readings:

- The **convenience** path (§1–2) is **3–34× slower** than P/Invoke marshalling
  on composites — the Term-AST intermediate is pure overhead here. It buys
  ergonomics, not speed. Use it where the crossing is not the bottleneck.
- The **zero-copy** path (§4) is **3–180× faster** than P/Invoke marshalling, and
  list/term *traversal* is essentially free (just chasing the engine's own
  pointers). This is the case a native embedded engine structurally cannot match,
  and the reason to run Prolog in-process.

Scalars are a wash (both cheap; convenience even edges ahead). The takeaway is not
"Shumway is faster at interop" flatly — it is: **for hot-path term manipulation,
touching the engine's cells directly wins decisively; for everything else, the
convenience API is there and its cost doesn't matter.**
