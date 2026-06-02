# Shumway benchmarks

Performance comparison suite against other Prolog engines.

## `vanroy/` — Van Roy benchmark suite

Ten programs from Peter Van Roy's PhD work on the Aquarius Prolog
compiler (UC Berkeley, 1990). Used in essentially every Prolog
performance paper from the late 80s onward, also distributed with
GNU Prolog (`gprolog/examples/bench/`) and SWI-Prolog. Public
domain.

| File | Workload | Measures |
|---|---|---|
| `nreverse.pl` | naive reverse of 30-elem list | unification + O(n²) list traversal |
| `qsort.pl` | quicksort of 50-elem list | partition + recursive descent |
| `queens.pl` | 8-queens, first solution | deep backtracking + arithmetic |
| `tak.pl` | Takeuchi tak(18, 12, 6) | recursion-heavy + arithmetic, det |
| `serialize.pl` | Warren's serialize (25-char input) | term construction + tree traversal |
| `flatten.pl` | flatten nested list | recursive descent + append |
| `sendmore.pl` | SEND + MORE = MONEY | generate-and-test, deep backtracking |
| `zebra.pl` | Zebra puzzle | constraint-like generate-and-test |
| `boyer.pl` | Boyer-Moore prover (small subset) | term rewriting + many clauses |
| `crypt.pl` | small cryptarithmetic puzzle | digit search, smaller than sendmore |

### Source conventions

- **Self-contained**: each file carries its own `select_`, `conc`,
  `member_` etc. so it runs identically on Shumway, GNU Prolog and
  SWI-Prolog without depending on prelude-specific predicates.
- **`bench/0`**: one iteration of the workload.
- **`bench/1`**: `bench(N)` runs N iterations via tail recursion.

### Manual invocation

```
# Shumway
dotnet run --project src/Shumway.Repl/ -- benchmarks/vanroy/nreverse.pl
?- bench(10000).

# GNU Prolog
gprolog --consult-file benchmarks/vanroy/nreverse.pl
| ?- bench(10000).

# SWI-Prolog
swipl benchmarks/vanroy/nreverse.pl
?- bench(10000).
```

### Automated multi-engine comparison

See `tests/Shumway.Tests.Benchmarks/` for the harness that runs
each benchmark against Shumway / GNU Prolog / SWI-Prolog and
emits a side-by-side report (chunk 281+).
