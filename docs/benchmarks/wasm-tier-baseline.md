# Wasm tier baseline (browser)

Where the wasm tier stands against the interpreted Tier-0 in the browser, as
of 2026-09-28, measured on commit `027ea53a`. This is the reference to compare
against when a change is meant to move the tier: rerun the two hooks below the
same way and put the new figures beside these.

For what the hooks are and how to run them, see
[browser.md](browser.md#running-these-in-a-browser).

## How it was measured

```bash
# Ratios: a publish WITHOUT the diagnostic build (its counters and clocks
# cost time on every chain).
dotnet publish src/Shumway.Web -c Release -p:ShumwayWasmTier=true

powershell -File src/Shumway.Web/WebShumwayHook.ps1 `
    -Hook '#wasmprobe=exits&n=20000&rounds=3' -Done 'probe exits: done|CRASHED'
powershell -File src/Shumway.Web/WebShumwayHook.ps1 `
    -Hook '#wasmclpz=2' -Done 'wasm clpz: Triska examples on Scryer|CRASHED'

# Counts: a diagnostic publish, one case.
dotnet publish src/Shumway.Web -c Release -p:ShumwayWasmTier=true -p:ShumwayDiag=true
powershell -File src/Shumway.Web/WebShumwayHook.ps1 `
    -Hook '#wasmclpz=1:sudoku' -Done 'wasm clpz: Triska examples on Scryer|CRASHED'
```

Headless Chrome, AOT publish, one desktop machine. Each hook runs Tier-0 and
the tier in the same page, ABBA within every round, each timed run after a
discarded warm one; a figure is the best run. Compare ratios, not
milliseconds: wall-clock numbers move between sessions (the same Tier-0 loop
has measured 554 and 805 ms on different days), while the ratio is taken
within one run. The report's last line is the cost of one clock read in that
browser (1.2 to 2.4 us seen), which bounds how sharp any time split can be.

## Module exits: `probes/exits.pl`

One kind of exit from a module per goal, 20,000 iterations, best of 3 rounds.

| probe | what each iteration does | Tier-0 | tier | ratio |
|---|---|---:|---:|---:|
| plain | integer arithmetic, a recursive call | 14 ms | 4 ms | x3.76 |
| b_getval | `b_getval/2` of a set key | 35 | 5 | x6.50 |
| acyclic | `acyclic_term/1` | 84 | 6 | x13.68 |
| sort | `sort/2` of a 3-element list | 69 | 9 | x7.72 |
| attvar =/2 | unify a variable with an attributed one | 34 | 4 | x7.79 |
| meta call | `call(G)` with `G = true` | 30 | 6 | x5.33 |
| struct =/2 | unify two compounds | 38 | 5 | x8.25 |
| fetch global | `'$fetch_global_var'/2` | 36 | 4 | x9.02 |
| dynamic call | a call to a dynamic predicate | 47 | 7 | x7.29 |
| attvar wake | bind a frozen variable, under `\+ \+` | 533 | 1714 | x0.31 |
| bb_get shim | Scryer's `bb_get/2` | 50 | 8 | x5.89 |
| bb put+get | `bb_b_put/2` then `bb_get/2` | 89 | 94 | x0.95 |

Geometric mean: x4.6. The two below x1 are the open items:

- **attvar wake**: binding the attributed variable steps aside (reason 24,
  the wakeup is the host's), so the interpreter runs the wake and the rest of
  the clause, and every iteration opens three chains.
- **bb put+get**: `b_setval/2` is a builtin exit on every iteration. The
  module cannot write the global-variable image, because the write must be
  trailed on the host's external trail.

## CLP(Z): `#wasmclpz`

Triska's examples over Scryer's `clpz.pl`, each checked by an oracle, best of
2 rounds. The counts are the steady-state counts divided by 10, so the
figures are per few solves; the ratio is what the hook measures.

| case | Tier-0 | tier | ratio |
|---|---:|---:|---:|
| queens10ff x5 | 497 ms | 230 ms | x2.16 |
| sendmore x2 | 49 | 34 | x1.42 |
| queens16 x2 | 382 | 186 | x2.05 |
| queens24 x2 | 723 | 225 | x3.22 |
| sudoku x1 | 2203 | 2226 | x0.99 |
| factorial x10 | 162 | 122 | x1.33 |

Geometric mean: x1.7. Every oracle passed. Loading clpz and the cases took
24.4 s; compiling everything under `jit_compile(all)`, 2,846 predicates, took
13 to 15 s. Under `all` clpz is compiled live, not from a baked bundle.

### Where sudoku's time goes

One sudoku solve on the tier, counted in the diagnostic build (deterministic,
so a change shows exactly):

| chains | deopts | builtin exits | foreign exits | tail exits |
|---:|---:|---:|---:|---:|
| 186,594 | 79,683 | 91,954 | 9,714 | 9,714 |

The time goes into leaving the module and coming back, not into running wasm.
In a desktop run of the same solve, about 95% of the deopts were reason 9 (the
meta-call cache had no row for the goal's module and functor), and the top
builtin exits were `compare/3`, which has no inline form, and `arg/3`, whose
inline form declines those shapes. Those three are the next targets for this
table.

## History of this table

Changes that landed the same day, before the tables above were taken. Their
before and after come from the runs that validated each change, so compare
each row within itself only.

| change | effect |
|---|---|
| `b_getval/2` and meta-called `true`/`fail` stay in the module | b_getval x0.14 to x5.9, meta call x0.21 to x7.7 (diagnostic build) |
| the browser tier stops reading the clock on every chain | bb put+get 127 to 86 ms on the tier (stock build) |

From here on, add a row when a change moves a figure (with the build it was
measured on), and replace the tables when enough of them have moved.
