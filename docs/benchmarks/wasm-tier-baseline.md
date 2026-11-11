# Wasm tier baseline (browser)

Where the wasm tier stands against the interpreted Tier-0 in the browser, as
of 2026-11-11, measured on commit `88345b2b` beside main `c1cbb343`: the head
of the 2026-11 wasm arc and the commit it started from, both published that
day with the same SDK and runtime. This is the reference to compare against
when a change is meant to move the tier: rerun the hooks below the same way
and put the new figures beside these.

For what the hooks are and how to run them, see
[browser.md](browser.md#running-these-in-a-browser). What the arc changed, and
why, is in the design notes:
[what leaves the module](../design/wasm-tier1-plan.md#what-leaves-the-module-the-2026-11-arc).

## How it was measured

```bash
# Ratios: a publish WITHOUT the diagnostic build (its counters and clocks
# cost time on every chain).
dotnet publish src/Shumway.Web -c Release

powershell -File src/Shumway.Web/WebShumwayHook.ps1 `
    -Hook '#wasmprobe=exits&n=200000&rounds=3' -Done 'probe exits: done|CRASHED'
powershell -File src/Shumway.Web/WebShumwayHook.ps1 `
    -Hook '#wasmprobe=catch&n=20000&rounds=3' -Done 'probe catch: done|CRASHED'
powershell -File src/Shumway.Web/WebShumwayHook.ps1 `
    -Hook '#wasmclpz=2' -Done 'wasm clpz: Triska examples on Scryer|CRASHED'

# Counts: a diagnostic publish, one case.
dotnet publish src/Shumway.Web -c Release -p:ShumwayDiag=true
powershell -File src/Shumway.Web/WebShumwayHook.ps1 `
    -Hook '#wasmclpz=1:sudoku' -Done 'wasm clpz: Triska examples on Scryer|CRASHED'
```

Headless Chrome, AOT publish, one desktop machine on mains power with the
processor capped at 99% (its clock is bimodal otherwise). Each hook runs
Tier-0 and the tier in the same page, ABBA within every round, each timed run
after a discarded warm one; a figure is the best run. Two commits are compared
by publishing each and running the hooks A B B A, a page per pass. Compare
ratios, not milliseconds: wall-clock numbers move between sessions, while the
ratio is taken within one run.

The exits probe runs at 200,000 iterations. At 20,000 its fast rows take 7 to
15 ms on the tier, and the start of a module V8 has just compiled weighs in
that: between main and `4287b613` those rows moved by 1 to 4 ms at 20,000
iterations and by nothing at 200,000.

## Module exits: `probes/exits.pl`

One kind of exit from a module per goal, 200,000 iterations, best of 3 rounds.
The arc's two passes agree within 1 ms on every row; main's took a pass each
past the hook's time limit, so its figures are the best of the rounds it
reported.

| probe | what each iteration does | Tier-0 | tier | ratio | main's tier | main's ratio |
|---|---|---:|---:|---:|---:|---:|
| plain | integer arithmetic, a recursive call | 150 ms | 13 | x11.8 | 15 | x10.1 |
| b_getval | `b_getval/2` of a set key | 465 | 17 | x26.9 | 20 | x23.6 |
| acyclic | `acyclic_term/1` | 413 | 19 | x22.1 | 21 | x20.9 |
| sort | `sort/2` of a 3-element list | 926 | 73 | x12.8 | 80 | x13.0 |
| attvar =/2 | unify a variable with an attributed one | 345 | 16 | x21.4 | fails | |
| meta call | `call(G)` with `G = true` | 1074 | 19 | x57.1 | 17 | x64.2 |
| struct =/2 | unify two compounds | 330 | 17 | x19.8 | 17 | x19.5 |
| fetch global | `'$fetch_global_var'/2` | 468 | 19 | x25.0 | 23 | x20.5 |
| dynamic call | a call to a dynamic predicate | 689 | 33 | x20.8 | 28 | x25.0 |
| attvar wake | bind a frozen variable, under `\+ \+` | 6072 | 1266 | x4.8 | 42151, fails | x0.15 |
| bb_get shim | Scryer's `bb_get/2` | 872 | 58 | x15.1 | 69 | x12.7 |
| bb put+get | `bb_b_put/2` then `bb_get/2` | 1497 | 153 | x9.8 | 2923 | x0.50 |
| meta =</2 | `call(=<, 0, N)` | 1442 | 23 | x62.9 | 1104 | x1.31 |

Geometric mean x19.3; over the eleven rows main completes, x21.6 against
main's x11.2. Every row is now ahead of Tier-0.

- **attvar =/2 and attvar wake** fail on main with
  `existence_error(procedure, term_residual_goals/2)`: a library bundle did
  not load the shim libraries its modules import, which Scryer's
  `copy_term/3` of an attributed variable needs. The arc fixed that, and the
  wakeup now runs from the module's chain.
- **bb put+get and meta =</2** were the rows below or near x1: `b_setval/2`
  and the comparison builtins left the module on every iteration, and on
  main both grow faster than the iteration count.
- **meta call and dynamic call** are the rows behind main, by 2 and 5 ms:
  the cost of the shared verdict epilogue, which moves between probes with
  the shape of the code (see the history below).

## catch/3: `probes/catch.pl`

The same work five ways, 20,000 iterations: a 9-variable term and the goals
built from it. `ideal` does the work; `callconj` does it through call/1 of a
conjunction; `catchonly` inside `catch/3` with no throw; `throwlit` throws a
literal ball out of it; `wla` is the shape of clp(Z)'s
`with_local_attributes/3`, which copies its result and throws it out.

| goal | Tier-0 | tier | ratio | main's tier | main's ratio |
|---|---:|---:|---:|---:|---:|
| ideal | 836 ms | 339 | x2.47 | 383 | x2.15 |
| callconj | 1319 | 353 | x3.73 | 2751 | x0.49 |
| catchonly | 888 | 665 | x1.33 | 1884 | x0.56 |
| throwlit | 1200 | 1166 | x1.03 | 6668 | x0.41 |
| wla | 1771 | 1280 | x1.38 | 10934 | x0.31 |

On main the tier ran every goal but `ideal` slower than Tier-0. A caught ball is now
planted from an image instead of a term built per catcher, and call/1 of a
construct enters its helper from the module. The catch frames stay the
host's: a predicate dominated by catch/3 stays on Tier-0 by the crossing
census, and what is left of `throwlit` is the throw's own resolution in the
host.

## CLP(Z): `#wasmclpz`

Triska's examples over Scryer's `clpz.pl`, each checked by an oracle (48 of
48 in every pass), best of 2 rounds.

| case | Tier-0 | tier | ratio | main's tier | main's ratio |
|---|---:|---:|---:|---:|---:|
| queens10ff x5 | 849 ms | 177 | x4.78 | 277 | x3.04 |
| sendmore x2 | 83 | 35 | x2.37 | 49 | x1.70 |
| queens16 x2 | 665 | 129 | x5.15 | 215 | x3.12 |
| queens24 x2 | 1228 | 201 | x6.12 | 400 | x3.07 |
| sudoku x1 | 3497 | 484 | x7.22 | 2273 | x1.54 |
| factorial x10 | 285 | 87 | x3.28 | 195 | x1.50 |

Geometric mean x4.5, against main's x2.2. The two columns come from two
sessions of the same day; the commit both measured in between, `4287b613`,
gave the same figures in each (sudoku 486 and 488 ms).

Loading clpz and the cases takes 31 s against main's 58, `jit_compile(all)`
8 s against 18, and the modules the run leaves hold 87.1 MB against main's
83.7.

### Where sudoku's time goes

One sudoku solve on the tier, counted in a diagnostic desktop run of the same
solve (deterministic, so a change shows exactly):

| entries | deopts | builtin requests | foreign exits | hops inside wasm |
|---:|---:|---:|---:|---:|
| 874 | 0 | 783 | 227 | 1,434,653 |

On 2026-09-28 the same solve took 186,594 chains, 79,683 deopts and 91,954
builtin exits. What leaves now: `'$put_to_attr_list'/3` (295, making a plain
variable attributed, which needs the host's attribute record),
`'$copy_term_without_attr_vars'/2` (227) and the 227 foreign exits
(clp(Z)'s `with_local_attributes/3`, refused for its catch/3), and four
requests per wakeup of clp(Z)'s `/3` hooks (64 wakeups). At 3 to 5 us a
crossing that is 1 to 2% of the solve: the rest of its time is spent inside
the module.

## History of this table

Each change was measured against the commit before it, A B B A between two
sites on the day it landed; compare each row within itself only.

| change | effect |
|---|---|
| `b_getval/2` and meta-called `true`/`fail` stay in the module (2026-09) | b_getval x0.14 to x5.9, meta call x0.21 to x7.7 (diagnostic build) |
| the browser tier stops reading the clock on every chain (2026-09) | bb put+get 127 to 86 ms on the tier (stock build) |
| a query's setup stops paying for the whole program; a library bake sorts its refusals out first, in one engine, and finds an address by binary search (`0c48a825` to `d09f27da`) | first visit to clpz 50 to 31.5 s; a second visit 4.1 s |
| the module binds an attributed variable, runs the wake driver from its chain and answers the driver's questions (`690b6aa3` to `70240c9d`) | attvar wake 1743 to 214 ms on the tier |
| meta-calls finish in the module: `Module:Goal`, `'$call'/2` with an integer barrier, qualified meta-arguments, control constructs, compare/3 and arg/3 (`649bd985` to `2f13c655`) | sudoku 2105 to 846 ms on the tier, deopts per solve 26,561 to 0 |
| a findall over a generator in the module (`5f3aced3`) | sudoku 768 to 607 ms |
| `b_setval/2` and `bb_b_put/2` write the global image in the module (`22fffcb7`) | bb put+get 137 to 21 ms (n=20,000) |
| a crossing staged without locks (`b40e7c93`) | a probe with one builtin request per iteration 850 to 394 ms |
| a caught ball planted from an image (`3f88ede2`) | throwlit 2752 to 1187 ms, sudoku 519 to 484 ms |
| call/1 of a control construct in the module (`f86fb29c`) | callconj 739 to 349 ms |
| arithmetic comparisons by meta-call in the module (`4287b613`) | meta =</2 82 to 9 ms (n=20,000) |
| a verdict tail-calls one epilogue per module, a flags check spills in place (`8009873f`, `88345b2b`) | the clpz run's modules 107.9 to 87.1 MB, `jit_compile(all)` 10.1 to 8.3 s; transfer probes 1 to 3% slower, a dynamic call 7% |

From here on, add a row when a change moves a figure (with the build it was
measured on), and replace the tables when enough of them have moved.
