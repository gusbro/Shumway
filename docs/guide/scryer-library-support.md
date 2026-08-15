# Scryer Prolog library support

Status of running Scryer Prolog's libraries **unmodified** on Shumway.
Measured against a Scryer checkout's `lib/`: all 46 top-level libraries are
loaded on a fresh engine under the `scryer` dialect, and 33 are exercised at
runtime with representative queries (the opt-in `ScryerEndToEndValidation`
test — see the end of this document). The `http/`, `numerics/`,
`serialization/` subdirectories are not covered.

**Bottom line: 46 of 46 libraries load clean — zero warnings, zero failures —
and everything whose functionality can exist on Shumway works, including
clp(Z), which is certified byte-identical to Scryer's own answers. What does
not work needs a capability the engine does not have (delimited continuations,
Rust FFI/network bindings, crypto natives).**

## How to use Scryer libraries

Point the engine at a Scryer `lib/` tree with the `scryer` dialect tag:

- REPL / CLI: `shumway -L "scryer:C:/Scryer/lib" myprogram.pl`
- Embedding: `engine.AddLibraryDirectory(dir, "scryer")`

Then `:- use_module(library(X)).` works as in Scryer. Libraries load with
`double_quotes = chars` (Scryer's default) scoped to the load; text is chars
lists throughout, as their APIs expect.

A Scryer compat shim auto-loads with the first `scryer`-dialect module. Where
the SWI shim supplies SWI system predicates, this one mostly supplies
**emulations of Scryer's Rust-VM native instructions** (the `'$...'` calls its
libraries bottom out in) as bare-global predicates, so the libraries' own pure
Prolog runs unmodified: `$random_integer`, `$crypto_random_byte`, `$getenv`,
`$file_exists` and the file-system family, `$char_type` with Scryer's full
category vocabulary, plus `builtins.pl` helpers (`must_be_number/2`,
`can_be_number/2`) that Scryer's bootstrap makes implicitly visible on their
VM.

Two libraries route to Shumway equivalents instead (native override — the file
is recognised by a marker and the load discarded): `format` (its rendering
core needs `builtins:parse_write_options`; the pack's `format/2,3` shim
serves) and `time` (wraps `$cpu_now`; Shumway's native `time/1` + `sleep/1`
serve — `current_time`/`format_time//2` are not available).

## Writing code against these libraries

Three things differ from writing the same code in Scryer itself. They are the
usual first stumbles.

**1. Set `double_quotes` to `chars` in YOUR file.** Scryer's APIs are
chars-based throughout, and the `chars` default is applied *while a
scryer-dialect library loads* — it does not extend to your own source, which
uses Shumway's default (`string`). Without the flag you hand a PSTR (or codes)
to an API expecting a chars list, and it silently fails or returns raw
character codes:

```prolog
:- set_prolog_flag(double_quotes, chars).      % ← put this first
:- use_module(library(csv)).
```

**2. A native Shumway builtin wins over an imported library predicate of the
same name.** Importing a library does not shadow a builtin, so where Scryer
implements something in Prolog that Shumway has natively, you get Shumway's —
with Shumway's argument vocabulary. The one that bites: `char_type/2` is a
Shumway builtin using the SWI category names, so `library(charsio)`'s Scryer
categories are not reachable from your code.

| you write | you get |
|---|---|
| `char_type(C, decimal_digit(W))` | **fails** — not a Shumway category |
| `char_type(C, digit(W))` | works — the equivalent |
| `char_type(C, lower(U))` / `upper(L)` | work (same spelling in both) |

The Scryer *libraries themselves* are unaffected: internally they bottom out
in the shim's `$char_type`, which does implement Scryer's full vocabulary.

**3. Lambdas are `library(lambda)`, not yall.** `\X^Y^Goal` is available;
`[X,Y]>>Goal` is the SWI pack's syntax and is not in scope here.

```prolog
?- maplist(\X^Y^(Y #= X*X), [1,2,3,4], Sq).    % Sq = [1,4,9,16]
```

### Worked examples

All verified on Shumway with `-L "scryer:C:/Scryer/lib"`.

```prolog
:- set_prolog_flag(double_quotes, chars).
:- use_module(library(clpz)).
:- use_module(library(reif)).
:- use_module(library(dcgs)).
:- use_module(library(csv)).
:- use_module(library(ugraphs)).

% clp(Z) — SEND + MORE = MONEY
puzzle([S,E,N,D]+[M,O,R,E] = [M,O,N,E,Y]) :-
    Vs = [S,E,N,D,M,O,R,Y], Vs ins 0..9, all_different(Vs),
    S*1000+E*100+N*10+D + M*1000+O*100+R*10+E #= M*10000+O*1000+N*100+E*10+Y,
    M #\= 0, S #\= 0.
%  ?- puzzle(P), term_variables(P, Vs), label(Vs).
%  P = [9,5,6,7]+[1,0,8,5] = [1,0,6,5,2].

%  reif — reified control, no cut, no negation
%  ?- tfilter(=(a), [a,b,a,c], L).        L = [a,a].
%  ?- if_(1 = 1, R = yes, R = no).        R = yes.

%  dcgs — seq//1 and ...//0 split text declaratively
%  ?- phrase((seq(User), "@", seq(Host)), "ana@example.org").
%     User = [a,n,a], Host = [e,x,a,m,p,l,e,.,o,r,g].
%  ?- phrase((..., "key", ...), "there is a key here").   % succeeds

%  csv — header + typed rows (33 and 41 come back as INTEGERS)
%  ?- phrase(parse_csv(Rows), "name,age\nana,33\nluis,41\n").
%     Rows = frame([[n,a,m,e],[a,g,e]], [[[a,n,a],33],[[l,u,i,s],41]]).

%  ugraphs
%  ?- vertices_edges_to_ugraph([a,b,c,d], [a-b,b-c,a-c,c-d], G),
%     top_sort(G, TS), neighbours(a, G, N), transitive_closure(G, TC).
%     TS = [a,b,c,d], N = [b,c], and a reaches [b,c,d].
```

## ✅ Supported — loads clean and runtime-validated

| library | exercised |
|---|---|
| `lists` | `member/2`, `append/3`, `length/2` |
| `assoc` | `empty_assoc` → `put_assoc` → `get_assoc` |
| `between` | `between/3`, `numlist/3` |
| `clpb` | `taut(X + ~X, 1)` (boolean constraints) |
| `clpz` | `X #= 3+4` — **certified byte-identical answers vs Scryer** (queens/permutations oracle) |
| `csv` | `phrase(parse_csv(Rows), Cs)` → `frame/2` |
| `dcgs` | `seq//1` over chars |
| `debug` | `* Goal` (goal generalization), `$`/`$-` ops |
| `dif` | `dif/2` posting + failing |
| `error` | `must_be/2` with ISO error terms |
| `freeze` | `freeze/2` firing on binding |
| `gensym` | `gensym/2` |
| `iso_ext` | `bb_put/2` + `bb_get/2` (blackboard) |
| `lambda` | `\X^Y^Goal` lambdas via `maplist/3` |
| `ordsets` | `ord_union/3` |
| `pairs` | `pairs_keys_values/3` |
| `queues` | `list_queue/2`, `queue_length/2` |
| `reif` | `if_/3`, `tfilter/3` (reified control) |
| `si` | `atom_si/1`, `integer_si/1` (sound type tests) |
| `simplex` | `gen_state/1` + `constraint/3` |
| `terms` | `numbervars/3` |
| `ugraphs` | `add_vertices/3` |
| `xpath` | `xpath/3` over a term DOM (pure — no sgml needed) |
| `arithmetic` | `lcm/3`, `msb/2` |
| `charsio` | loads; `char_type/2` calls reach **Shumway's builtin**, not the library — SWI category names (`digit(W)`, `lower(U)`, `upper(L)`), not Scryer's (`decimal_digit(W)`). See "Writing code against these libraries" |
| `format` | `format/2,3` (via the pack shim) |
| `files` | `file_exists/1` and the FS family, chars↔atom converted |
| `os` | `getenv/2` |
| `random` | `random/1`, `random_integer/3` (upper bound exclusive, as in Scryer) |
| `time` | `time/1`, `sleep/1` |
| `uuid` | `uuidv4_string/1` — valid v4 UUIDs. **NOT cryptographically secure** (seedable PRNG source) |
| `when` | `when/2` post + fire |
| `crypto` | the pure parts (`hex_bytes/2`, `crypto_n_random_bytes/2`†) |

† random bytes come from a PRNG — fine for ids and simulation, **not for key
material**. `atts` is validated indirectly: it is the foundation the whole
clpz/dif/freeze stack runs on.

## ❌ Not supported — needs a capability Shumway does not have

The engine capabilities behind these gaps, so it is clear what is missing and
that none of it is a defect to report:

| capability | status in Shumway | what it blocks here |
|---|---|---|
| **delimited continuations** (`reset/3`, `shift/1`) | not implemented — an execution-model feature, not a library | `cont`, and Scryer's tabling which is built on it (Shumway's native `:- table` covers the feature) |
| **Rust-side FFI / sockets / TLS / process control** | absent by construction — Shumway's foreign interface is .NET (`[PrologPredicate]`) and C (`:- native`, P/Invoke), not Scryer's Rust ABI | `ffi`, `sockets`, `tls`, `wasm`, `process`, `sgml` |
| **cryptographic primitives** | no crypto backend; the randomness on offer is a seedable PRNG, **not a CSPRNG** | `crypto` hashes / HKDF / curves. `hex_bytes/2` and byte generation work, but never for key material |
| **introspection of Scryer's own VM** | not applicable — Shumway has its own bytecode and `shumway-disasm` | `diag` (`wam_instructions/2`) |

With those in mind, the libraries below load (harmlessly) but their purpose
cannot be served:

| group | libraries | why |
|---|---|---|
| **delimited continuations** | `cont` (`reset/3`, `shift/1`) | a VM execution-model feature |
| `tabling` | Scryer's tabling is built on `cont` | **Shumway's own native `:- table` covers the feature** — `:- table p/2` works through our tabling engine |
| **native bindings** | `ffi`, `sockets`, `tls`, `wasm`, `sgml` (`load_html`), `process` | Rust-side FFI / network / OS |
| **crypto natives** | `crypto` hashes, HKDF, curves | Rust crypto backend (the pure parts DO work, see above) |
| **VM introspection** | `diag` (`wam_instructions/2`) | decompiles Scryer's WAM; Shumway has `shumway-disasm` for its own |
| **bootstrap internals** | `builtins`, `loader`, `ops_and_meta_predicates` | Scryer-internal modules; load as inert data |
| `pio` | `phrase_from_file` needs their stream layer | unverified; plain `phrase/2,3` is native here |

## Regenerate the validation

```
SHUMWAY_SCRYER_LIB=C:/Scryer/lib SHUMWAY_TRIAGE_OUT=<file> dotnet test
tests/Shumway.Tests.DialectInterop/ --filter FullyQualifiedName~ScryerEndToEndValidation
```
