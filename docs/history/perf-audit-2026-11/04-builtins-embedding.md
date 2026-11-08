# Performance review 04 — builtins, embedding API, Prolog-level libraries

Static analysis only (no `dotnet` in the container). Every item below was verified by
reading the code at the cited lines on the current `main` (HEAD `7536c3c`). Items are
ranked by expected value for the three embedding workloads named in CLAUDE.md
(grammar processing, assert-heavy rules engines with many small queries from C#,
symbolic reasoning). Line numbers are those of the files as read; they drift with edits.

Prior work consulted so that nothing here re-proposes it: `docs/history/efficiency-audit-2026-06.md`,
`docs/benchmarks/cross-engine-comparison.md` (+ `analysis.md`, `baseline.md`),
`docs/architecture/invariants.md`, ADRs 010/015/023/024/034/039/041/044–049/051–053/063/064,
`docs/design/pstr-design.md`, `docs/guide/interop.md`, the phase closure docs 7, 8, 10–12, 21, 24,
27, 28, 36–43 (in particular phase 43's "Deferred" list), and `git log --oneline | grep -i <topic>`
per item (the repo history is 179 commits; the grep results are quoted in each "Novelty check").

Items already on the project's own deferred list (phase 43 closure) and therefore **not**
re-proposed: the `throw/1` ball going through a Term tree, a pool of solution buffers shared
across queries, big integers / rationals in images, images in clauses asserted at run time
(facts are done: `c4dbfcf`).

---

## Ranked findings

### 1. `clause/2` is O(whole program) per call: `StaticClausesFor` scans every clause of every module and interns a string per clause scanned

**Locations**
- `src/Shumway.Embedding/PrologEngine.DynamicStore.cs:996-1015` — `StaticClausesFor(fid)`: `foreach manifest in _modules.Values → foreach c in manifest.InspectableClauses → TryExtractDefinedHead(c, out n, out a) → FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, a) == functorId`.
- `src/Shumway.Embedding/ModuleManifest.cs:39-46` — `InspectableClauses` is a two-list iterator (`Clauses` then `ShippedClauses`), no index.
- `src/Shumway.Embedding/PrologEngine.Query.cs:2538-2576` — `TryExtractHead` / `TryExtractDefinedHead` hand back `string name` so the caller has to re-intern.
- `src/Shumway.Embedding/MetaBuiltins.TermInspection.cs:962-1010` — `'$clause_enum'/2` (the engine behind `clause/2`): `new List<Clause>(host.StaticClausesFor(fid))`, then `candidates.AddRange(DynamicClausesFor(fid))`, `MaterializeRegister(engine, 0)` (full Term tree of the head pattern), `candidates.RemoveAll(c => …HeadCanMatch(headPattern, ClauseHead(c)))` (AST walk per candidate), then an `IndexEnumCursor` over what is left.
- Compare `src/Shumway.Embedding/MetaBuiltins.Control.cs:1290-1306` (`RetractStep`) which already uses `host.ClauseIndexFor(fid)` + `PatternFirstArgKey` + `DynamicClauseIndex.FirstCandidateFrom` (`DynamicClauseIndex.cs:84`) to skip non-candidates without touching them.

**What it costs.** Every `clause(H, B)` call — whatever `H` is — walks *all* static clauses of *all* loaded modules (the prelude alone has ~1,500 clauses / 263 predicates, plus clpfd/clpr/coroutining/reif when loaded, plus the user program) and performs one `AtomTable.Intern(name, permanent: true)` (ConcurrentDictionary probe on a string hash) + one `FunctorTable.Intern` per clause scanned. For a dynamic predicate (the common case: `clause/2` on asserted facts, Logtalk's runtime tables, the tabling driver — see item 2) the static half is pure waste but still costs O(program) string hashing. Then the dynamic half is O(#clauses of the predicate): the whole clause list is copied, the pattern is materialized as an AST, and `HeadCanMatch` is run on every clause even when the first argument is bound and the predicate already has a `DynamicClauseIndex` (built for `retract/1`). A meta-interpreter (`solve(G) :- clause(G, B), solve(B)`) — the canonical "symbolic reasoning" shape — pays this on every inference step; a 1,000-fact dynamic table queried with a bound key pays 1,000 AST prefilters per lookup instead of one index probe.

**Proposed change**
1. Index static clauses by head functor id once per derivation generation: `Dictionary<int, List<Clause>> _staticClausesByFid`, built lazily and dropped when `_derivationGen` changes (the same invalidation `_runtimeImportMapCache` uses, `PrologEngine.Query.cs:2501-2522`). `StaticClausesFor` becomes a dictionary probe. Store the head fid on the `Clause` (or in the manifest) at consult time so no string is interned at lookup.
2. In `ClauseEnum`, when the head's first argument is keyable (`DynFirstArgKey.OfCall`), use `host.ClauseIndexFor(fid)` exactly as `RetractStep` does: enumerate candidate positions from the index instead of copying the list and running `HeadCanMatch` on every clause. Keep `HeadCanMatch` / `DefinitelyDiffers` as the second filter on the surviving candidates.
3. Skip the static walk entirely when `host.IsDynamic(fid) && !host.HasStaticClauses(fid)` (both already exist: `DynamicStore.cs:241`, `:2239`).

**Estimated gain.** Per `clause/2` call: from O(program + #clauses) with ~1,500–5,000 string interns to O(candidates). For a meta-interpreter over a dynamic knowledge base this is easily 10–100× on the `clause/2` step; it also removes the dominant term of item 2. Measure with `tests/Shumway.Tests.Benchmarks` — add a vanilla meta-interpreter over 500 facts and a `clause(p(K), B)` lookup loop; count `AtomTable.Intern` calls with the existing `LoadProfEnabled`-style counters or `--alloc`.

**Risk / invariants.** None of the invariants are touched: the logical update view is enforced by `check_visible`/born-died on dispatch and by the candidate snapshot here (the cursor's window already handles mutation). The static index must be dropped on consult/reconsult (derivation gen) — same discipline as the other per-derivation caches. `IsDeclaredPublic` / dialect checks stay.

**Novelty check.** `git log --oneline | grep -iE "clause/2|StaticClausesFor|clause enum"` → no hits. The 2026-06 audit has no `clause/2` item. The `DynamicClauseIndex` was introduced for retract only (`RetractStep` comment "74% of one"); `ClauseEnum` never adopted it. Phase 43 added the image-based `DefinitelyDiffers` prefilter (`25d44b8`, `abe49e4`) but kept the O(program) static walk and the per-clause AST filter.

---

### 2. The tabling engine stores tables as dynamic predicates and reads them through `clause/2`: every answer lookup is O(all answers of that predicate), every round is O(subgoals²), and the seen-set canonicalises each answer to a string

**Locations**
- `src/Shumway.Embedding/Prelude.cs:2290-2389` — `'$table_call'/3`, `'$table_key'/2` (`copy_term` + `numbervars` per call), `'$table_register'` (`clause('$tbl_subgoal'(Key,_,_,_), true)`), `'$table_absorb'` (`'$tbl_seen'(Key-A)`, `assertz('$tbl_ans'(Key,A))`, `assertz('$tbl_newd'(Key,A))`), `'$table_emit'(Key, Goal) :- clause('$tbl_ans'(Key, Goal), true)`, `'$tbl_consume'/3` (`clause('$tbl_ans'/'$tbl_delta'(Key, Goal), true)`), `'$table_seminaive'` (`'$table_count'` = `findall` over all subgoals + `length`, twice per round), `'$table_commit'` (`retractall` + `findall` + re-`assertz` of every delta), `'$table_round'` / `'$table_round_each'` (`findall` of all keys, then `clause('$tbl_subgoal'(K, G, _, Rec), true)` per key).
- `src/Shumway.Embedding/MetaBuiltins.Findall.cs:387-402` — `'$tbl_seen'/1`: `MaterializeRegister` (Term tree) → `Canonicalize` into a `StringBuilder` with a fresh `Dictionary<string,int>` → `host.RegisterTablingKey(sb.ToString())` → `HashSet<string>` (`PrologEngine.CodeSpace.cs:376`).
- `src/Shumway.Embedding/DynamicCodePatcher.cs:2826-2851` — `DynFirstArgKey.Of`: a compound first argument keys on functor/arity only (`StructKind`), so every `'$tbl_ans'(path(_,_), …)` fact of every subgoal of `path/2` lands in one bucket.

**What it costs.** Table reads go through item 1's `clause/2`: `'$table_emit'` and `'$tbl_consume'` build the candidate list of *every* `'$tbl_ans'` fact (all subgoals of the same tabled predicate share the `StructKind` bucket), materialize the pattern, and AST-filter each — O(total answers) per consumption, and a consumer is run once per recursive clause per subgoal per round. `'$table_round_each'` does one such O(subgoals) lookup per subgoal → O(subgoals²) per round; `'$table_count'` builds a list of all subgoals twice per round. `'$table_key'` copies and numbervars the goal on every tabled call. `'$tbl_seen'` allocates a Term tree, a StringBuilder, a Dictionary and a string per candidate answer, and the `HashSet<string>` grows with one string per answer ever seen (never shrinks except `abolish_all_tables`). Net effect: a tabled `path/2` over a graph with N nodes and E edges is O(N·E·rounds) *lookups each of cost O(answers)* — quadratic-to-cubic where the textbook semi-naive algorithm is near-linear in derived facts. This is the workload "symbolic reasoning" names (transitive closure, left-recursive grammars via `:- table`), and the gate already has to mark "the 2500-round tabled fixpoint" as `Category=Slow` (~1 min, CLAUDE.md).

**Proposed change.** Move the table store to C# (a `TableStore` per engine, keyed by variant hash), keeping the Prolog driver's control structure:
- Subgoal table: `Dictionary<VariantKey, Subgoal>` where the key is a structural hash of the heap term (walk cells, hash functor ids / atom ids / ints, number variables in order of first occurrence — the same canonical form `Canonicalize` computes, without the string). `'$table_key'` becomes a builtin returning an integer subgoal id; no `copy_term`/`numbervars`.
- Answers per subgoal as a `SolutionBuffer` (ADR-063 images, exactly what findall uses since #165: `src/Shumway.Core/SolutionBuffer.cs`), with a per-subgoal `HashSet<long>` of answer hashes for the seen test (collision → full image compare). `'$tbl_seen'` then allocates nothing per answer.
- `'$tbl_consume'` / `'$table_emit'` become a backtrackable builtin enumerating a subgoal's answers (or its delta range `[deltaFrom, deltaTo)` — a delta is just an index window into the answer buffer, so `'$table_commit'` is two integer assignments per subgoal instead of `retractall` + `findall` + N `assertz`).
- `'$table_round'` iterates the subgoal list; `'$table_count'` is `Count`.

**Estimated gain.** Asymptotic: per-lookup O(answers of the subgoal) instead of O(all answers of the predicate) and O(1) seen test; the fixpoint becomes O(derived facts · rounds) rather than quadratic in it. Concretely: expect ≥10× on the `Category=Slow` tabled fixpoint and on left-recursive `path/2` over a few hundred nodes; measure with those tests and a new benchmark (`tc/2` over a 500-node chain, `fib/2` tabled up to 2,000) using the existing `statistics(runtime, …)` discipline from `docs/benchmarks/`.

**Risk / invariants.** The tables are no longer dynamic predicates, so `abolish_table/1`, `abolish_all_tables/0`, table invalidation on reconsult (`'$tbl_seen_clear'`) and the well-founded machinery (`'$wfs_*'`, `Prelude.cs:2410-2453`) must be re-pointed at the store; non-ground answers (phase 7 chunk 108, variant tabling) need the variant hash to number variables, which the existing `Canonicalize` already does. Images are per-engine side-table data (floats/strings/bigints resolved through the literal pools as findall does), so the "no managed reference in a cell" invariant holds; the store is per-engine so thread-agility holds. This is a redesign of a subsystem that has no ADR of its own (phase 7 closure describes it; `docs/history/phase-7-closure.md:114-145`), so a short design note or ADR is appropriate, not mandatory by `decision-policy.md`.

**Novelty check.** `git log --oneline | grep -iE "tabl"` → no commits since the squash; phase 7 closure records "O(n log n) fixpoint passes" (chunk 105, the `'$tbl_seen'` set replacing a scan) and nothing later touches the store. `docs/history/efficiency-audit-2026-06.md` has no tabling item. The phase 43 solution-buffer work (#165) explicitly lists findall/bagof/setof/nb_setval/recorded as buffer users — tabling is not among them.

---

### 3. Remaining per-query fixed cost in `SetupQueryFromTermUnderGateCore`: string work per promoted predicate, per-query validation walks, and ~15 closures, paid by every `QueryAll`/`Query<T>` even when the product and links are cached

**Locations** (all `src/Shumway.Embedding/PrologEngine.Query.cs`)
- `:1818-1833` — `foreach (int ilFid in IlPromotion.PromotedFunctorIds())`: `FunctorTable.Lookup`, `AtomTable.GetById(…).Name`, `ModuleSeparatorIndex` (two `IndexOf`s), `name.Substring(0, dollar)` (allocates), `_modules.ContainsKey`, then usually `continue`. `PromotedFunctorIds()` is `_delegates.Keys` (`IlPromotionStore.cs:1701`) and, since Tier-1 is on by default with the prelude and the engine libraries shipping IL (phase 43 #141/#139), it holds every prelude predicate (≈263 heads, 183 publics) plus every promoted user/library predicate. The result does not depend on the query.
- `:1841-1855` — the same loop again over `_regionMemberAliases`.
- `:809` → `:2580-2611` — `ValidatePublicUniqueness()` per query: a fresh `Dictionary<int,string>` and a walk over every module's `PublicFunctors` (hundreds). Modules only change with a consult (`_derivationGen`).
- `:820-830` — two fresh `HashSet<int>` and a walk over `_dynStore.Functors` + `ClauseFunctors` per query (the hotness census).
- `:1954-1967, :1979-1981, :2010-2014, :2031-2064, :2078-2095, :2282-2296` — per-query closures: `ResolveLateHelper`, `DynChainSelect`, `CompiledCodeActive`, `RefreshLiteralPoolsCallback`, `NestedCatchResolver`, `InlineThrowResolver`, `MaterializeCellToTerm`, `ResolveAddressToLabel`, `OnGcMark`, `OnGcRelocate` (+ `new Tier1DispatcherAdapter` `:2163`, `new LinkedProgram` `:2193`, `new OperatorTableAdapter` `:1864`).
- `:446-463` + `:566-570` — `Query<T>(string)` parses the query text to count variables, then `SetupQuery` parses it again.
- `:2613-2639` — `BuildSolution` allocates two `Dictionary`s per solution (the `rootAddrs` one even for a query with no variables) and materializes every binding to a Term tree.
- `:1557-1558` — `new int[3]` pool-version record per query plus a `ConditionalWeakTable` entry (`:2018`; weak-keyed, so no leak — just one more allocation).

**What it costs.** The audit's Tier-A #5 ("per-QUERY setup re-transforms everything") was largely fixed (product cache, `LayeredIntMap`, persistent alias cache, debug-table cache — all verified present). What is left is still O(promoted predicates + public functors + dynamic functors) work with several hundred small string allocations per query, plus ~20 object allocations, on top of the unavoidable parse/compile/link of the goal (item 4). For the "many small queries from C#" pattern — a rules engine answering `QueryFirst<bool>("allowed(U, R).")` per request — a query that executes in a few µs pays tens of µs of setup; the alias loops alone are ~1,000 `Substring` allocations per query on an engine with clpfd loaded.

**Proposed change**
1. Maintain the IL-alias overlay incrementally: when `IlPromotionStore` installs a delegate (`:423 _delegates[functorId] = del`) compute `(bareFid, marker)` once and keep a `_promotionAliasVersion`; at setup, re-apply the overlay only when the version or the persistent link changed, otherwise reuse the previous overlay dictionary (the `_persistentAddressBaseCache` pattern at `:1681-1702`). Same for `_regionMemberAliases` (bundle load time).
2. Cache `ValidatePublicUniqueness` per `_derivationGen` (it only reads module manifests); cache the hotness census per `(_dynStore` mutation stamp`)` or fold it into `_productStaleDynamicFids` bookkeeping.
3. Replace the per-query closures with static lambdas / instance methods reading state off the `Activation` (e.g. `engine.HostProgram`), or with one per-engine `QueryRuntime` object created once and re-bound per activation.
4. `Query<T>(string)`: collect the variables from the term parsed in `SetupQuery` (return `varNames` from the setup tuple — it already exists) instead of parsing twice.
5. `BuildSolution`: allocate `rootAddrs` lazily (only when a binding is compound); for `Query<T>(text, var)` / `QueryFirst<T>` read the one variable straight off the heap with `RegisterMarshalling.TryReadAs` (already used by `SolveOnce<T>`, `PrologEngine.Reentrant.cs:86-88`) and skip `BuildSolution` entirely — the cross-engine doc's own caveat ("`Query<T>`'s answers still go through an intermediate `Term` tree") goes away for scalars, strings, lists of them and `[PrologTerm]` types.

**Estimated gain.** Honest: tens of µs and a few KB of garbage per query on a loaded engine; invisible on long queries, 20–50% of wall time on the µs-scale query-per-request pattern. Measure with the existing `LoadProfEnabled` buckets (`ProfMergeTicks`, `ProfActivationTicks`, `ProfQueryCompileTicks`) and `dotnet-counters` allocation rate over a `QueryFirst<int>("p(1,X).")` loop.

**Risk / invariants.** Pure caching of query-independent data; the invalidation keys exist (`_derivationGen`, `_staticLink` identity, promotion install). The alias overlay must still let a query-region real address shadow a persistent alias (`:1729-1731` ordering) — keep the overlay layered under the query map as now.

**Novelty check.** `git log --oneline | grep -iE "setup|alias|PromotedFunctorIds|public unique"` → only `9621c03` (defers a mutated dynamic's compile), `71ddf62` (a library public alias). The audit's #5 fix list (cache per consult-generation, `IsPoolFree`, merged maps) is done; the alias loops, `ValidatePublicUniqueness`, the hotness census and the closures are not mentioned anywhere.

---

### 4. There is no prepared / cached query: every `QueryAll(string)` re-parses, re-transforms (DCG/Meta/Phrase), re-rewrites, re-compiles and re-links the goal, and re-emits the launcher prefix

**Locations** (`src/Shumway.Embedding/PrologEngine.Query.cs`)
- `:566-570` parse; `:1511-1542` `PhraseTransform(MetaTransform(DcgTransform(syntheticClause)))` + `ModuleRewrite.Rewrite` per clause with a fresh `ModuleRewrite.Context`; `:1546-1555` `new ModuleCompiler{…}.Compile(queryClauses, …)`; `:1568-1594` the launcher `BytecodeEmitter` (identical bytes every query, re-emitted and re-validated); `:1705-1716` `new Linker().Link(queryPreds, …)`; `:1732-1764` fresh overlay dictionaries + `new List<SwitchTable>` merge; `:2133` `ChainPatcher.NoteQueryUnit(…, LinkAtomIds(queryLink))`.
- The cache key material already exists: `_derivationGen`, `_programStamp`, `_staticLink` identity, `_literalPools` versions (`RecordPoolVersions`, `:1557`), `_flags` (`EmitDebugInfo`, `DebugCodegen`, `ElideRedundantCuts`), the operator table/`_flags` the parser uses.

**What it costs.** The whole pipeline is O(goal size) but with a large constant: a parser, a lexer, three AST transforms, a rewrite context, a `ModuleCompiler` (which itself creates a `ClauseCompiler`, a `BytecodeEmitter`, pool views), a `Linker`, and ~10 dictionaries/lists. A host that runs the *same* goal text thousands of times (the rules-engine pattern, or a `Query<T>` inside a C# loop) rebuilds identical bytecode every time. `SolveOnce` (the re-entrant path) avoids this but only from inside a running query.

**Proposed change.** Two levels, both inside ADR-010's API surface:
1. A `PreparedQuery` object: `var q = engine.Prepare("allowed(U, R).")` runs parse → transform → compile once and keeps the `CompiledPredicate` list for the query region, the variable names and the `StringLiterals/FloatLiterals/BigIntLiterals` snapshot; `q.Run(("U", user), ("R", role))` or `q.Run(params object[])` does only what `:1668-1716` + `:1860-2336` do (reserve, link against the persistent map, activation) and seeds the query variables' heap cells from C# values through `Materializer`/`ToTerm`. Invalidated (re-prepared transparently) when `_derivationGen`/`_programStamp`/pool versions change.
2. Behind the existing API, an LRU (`text → prepared`) of, say, 64 entries keyed on the same stamps gives repeated `QueryAll(string)` callers the benefit without code changes.

Also precompute the launcher prefix bytes once (they are constant: `Call(0)+Halt+TrustMe+CallBuiltin(fail)`; `:1583-1585` even asserts the layout) and keep the merged switch-table list only when `_dynamicLink.SwitchTables` changed (the comment at `:1742-1745` explains why it is rebuilt; a version counter on the in-place replacement path would avoid it).

**Estimated gain.** For repeated small queries the setup drops from "compile + link + activate" to "link + activate": the compile half is the larger part on a short goal (parser + three transforms + compiler object graph). Expect 2–4× on the per-query fixed cost of item 3's workload; measure with `ProfQueryCompileTicks` vs `ProfActivationTicks` before/after.

**Risk / invariants.** A cached query region must be relinked (not reused by address) because `queryStart` depends on `ReservePersistent` and the persistent map may have changed; the `CompiledPredicate` objects are immutable bytes + call-site lists, which the linker already treats as such. Helper ids (`$q` prefix, `:1514`) are reused query-to-query by design, so a cached query compiled against `$q` helpers must be re-prepared if another prepared query's helpers would collide — give each prepared query its own helper prefix (`$qN`). Logical update view unaffected: a prepared query links at run time against the current persistent map.

**Novelty check.** `grep -rn "Prepared\|PreparedQuery\|query cache\|_queryCache\|CompiledQuery" src/Shumway.Embedding docs/guide docs/architecture/adr` → no hits. ADR-010 and `docs/guide/api-reference.md` describe only `Query*`/`SolveOnce`. The audit's #5 covered the *program* product, not the query clause itself (its text: "The per-query work is then only the synthetic `__query__` clause — the 'small bootstrap'").

---

### 5. `predsort/3` is an insertion sort: O(n²) calls of the user comparator

**Locations** — `src/Shumway.Embedding/Prelude.cs:1534-1547`: `predsort(P, List, Sorted) :- … '$predsort_all'(List, P, [], Sorted).` with `'$predsort_ins'` inserting each element into the accumulator by walking it and calling `P` at every position.

**What it costs.** n(n−1)/4 comparator calls on average, each a `call/4` meta-call (register load, dispatch, a choice point if `P` leaves one). Sorting 10,000 items by a user key costs ~25M meta-calls versus ~130k for a merge sort — three orders of magnitude. `predsort/3` is the standard idiom for sorting by a computed key in symbolic-reasoning code (agenda ordering, best-first search, Logtalk's `list::sort/3`), and SWI/GNU/Scryer all implement it as a merge sort.

**Proposed change.** Replace with the SWI-style merge sort in Prolog (`predsort/3` → `length`, split, recursive merge; ~25 lines), keeping the "drop elements compared `=`" semantics. A C# builtin is not needed: the comparator must be a Prolog meta-call anyway, and the Prolog merge sort compiles to Tier-1 like any other predicate.

**Estimated gain.** O(n²) → O(n log n): 10× at n = 100, 100× at n = 1,000. Measure with a predsort of 5,000 random pairs under `statistics(runtime, …)`.

**Risk.** None of the invariants are touched. Behaviour on an inconsistent comparator (non-transitive) may order differently from the insertion sort; that is unspecified by every Prolog's documentation.

**Novelty check.** `git log | grep -i predsort` → none. `docs/history/phase-19-closure.md:102,116` mention `predsort_ins/4` only as a Tier-1 promotion-eligibility example; phase 7 lists `predsort/3` among the library predicates added. No audit item.

---

### 6. `atomic_list_concat/2,3` and `'$alc_split'` are quadratic in Prolog, each step interning an intermediate atom

**Locations** (`src/Shumway.Embedding/Prelude.cs`)
- `:1601-1605` — `'$alc_concat'([X|Xs], Atom) :- '$atomic_to_atom'(X, AX), '$alc_concat'(Xs, Rest), atom_concat(AX, Rest, Atom).` — a right fold: element i produces an intermediate atom of length (L − prefix_i), so total copying is O(n·L) and n−1 transient atoms are interned (`AtomTable.Intern(aName + bName)` at `src/Shumway.Builtins/AtomListBuiltins.cs:449`, a string concat + a ConcurrentDictionary insert each).
- `:1655-1660` — `'$alc_join'` does two `atom_concat`s per element (separator then rest): 2n intermediate atoms.
- `:1664-1677` — `'$alc_split'`: `'$alc_first_sep'` runs `findall(B0, sub_atom(Atom, B0, _, _, Sep), Bs)` (every occurrence of the separator in the *remaining* atom, each enumerated through `IndexEnumCursor`), `min_list`, then `sub_atom` twice, then recurses on the tail — O(n · occurrences) with k intermediate tail atoms interned, where a single left-to-right scan is O(n).

**What it costs.** Text assembly and tokenisation in grammar-processing code (`atomic_list_concat(Parts, '', Atom)`, `atomic_list_concat(Fields, ',', Line)` to split CSV-ish input) is quadratic and pollutes the transient atom table with every intermediate (the atom GC reclaims them, but each costs an intern, a weak reference and later a sweep).

**Proposed change.** Builtins: `atomic_list_concat/2` and the join mode of `/3` as one `StringBuilder` pass over the list (reusing `SwiLenient.TryAtomicText` / `'$atomic_to_atom'`'s rules for numbers and strings) and one `Intern`; the split mode as a left-to-right `IndexOf(sep, pos, Ordinal)` loop building the result list with `engine.MakeTextList`-style cells (atoms per part). Keep the Prolog clauses only for the error checks (`'$alc_check_list'`), or move those too.

**Estimated gain.** O(n·L) → O(L) and n interns → 1; for a 100-element join that is ~50× fewer characters copied and 99 fewer transient atoms. Measure with a join of 1,000 short atoms and a split of a 10 KB line.

**Risk.** Error terms and the SWI-compat coercions (`'$atomic_to_atom'`) must be preserved byte-for-byte (the conformance suites pin them). No invariant interaction.

**Novelty check.** `git log | grep -iE "atomic_list|atomic_concat"` → only `da2da8e` ("atomic_concat/3 is back", the Arity predicate) and `3c49be0`. `docs/history/phase-7-closure.md:81` lists `atomic_list_concat/2,3` as "added"; nothing on its complexity.

---

### 7. Atom-text builtins intern through `Substring`/`StringBuilder` allocations; `sub_string/5` interns every substring as an atom first

**Locations**
- `src/Shumway.Core/AtomTable.cs:146-176` — `Intern(string name, …)` is the only entry; `_byName` (`:33`) and `_permanentByName` (`:61`) are `ConcurrentDictionary<string, …>`.
- `src/Shumway.Builtins/AtomListBuiltins.cs:505-521` (`atom_concat` with one side bound: `cName.Substring(…)` then `Intern`), `:579-582` (the split cursor: two `Substring`s + two `Intern`s per split), `:383-421` (`ReadCodesString`: a fresh `StringBuilder` per `atom_codes(-A, +Codes)` even for a packed run, where `ReadPstrChain` already yields the string).
- `src/Shumway.Builtins/MultiSolutionHelpers.cs:259-263` — `sub_atom` enumeration: `name.Substring(u0, u1-u0)` + `Intern` per candidate.
- `src/Shumway.Embedding/Prelude.cs:688-693` — `sub_string(S, B, L, A, Sub) :- atom_string(Atom, S), sub_atom(Atom, …, SubA), atom_string(SubA, Sub)`: every enumerated substring becomes a transient atom (`AtomCharBuiltins.cs:74-90`: `Intern` on one side, `MakePstr` on the other) before becoming a PSTR.

**What it costs.** A DCG/tokeniser working over atoms (`sub_atom/5` in enumeration mode, `atom_concat(X, Y, +Atom)`) allocates one string per candidate for the lookup alone; an existing atom still costs the `Substring` plus a `WeakReference.TryGetTarget`. `sub_string/5` adds an atom-table insert per candidate — O(n²) transient atoms for an enumeration over an n-character string, each later swept by the atom GC (`8cb5915` made the sweep cheaper but not free).

**Proposed change**
1. Add `AtomTable.Intern(ReadOnlySpan<char>)` / `TryLookup(ReadOnlySpan<char>, out Atom)` using `ConcurrentDictionary.GetAlternateLookup<ReadOnlySpan<char>>()` (.NET 9+, available on both `net10.0` targets; behind `#if NET9_0_OR_GREATER` with the current string path for `net48` — `Directory.Build.props:20-21`). A hit allocates nothing; a miss materialises the string once. Use it in `atom_concat`, `sub_atom`, `atom_codes`/`atom_chars` (hash the code-point run directly from the PSTR), `string_to_atom`, `split_string`.
2. Give `sub_string/5` its own enumeration over the text that hands back `MakePstr` slices (`'$sub_text_enum'`), never an atom.

**Estimated gain.** Removes one allocation per candidate on the atom paths (2 on splits) and an atom-table insert per candidate on `sub_string`; 20–40% on a `sub_atom`-driven scanner, more on `sub_string`. Measure with a `sub_atom(A, B, 1, _, C)` walk over a 100 KB atom and the same via `sub_string`, with `--alloc`/GC counters.

**Risk / invariants.** Atom ids stay global and stable (ADR-003); the alternate lookup is on the same dictionaries. `net48` keeps the string path (ADR-043 says Tier-0/builtins unchanged there — fine, it is a fallback not a divergence).

**Novelty check.** `grep -rn "AlternateLookup\|GetAlternateLookup" src docs` → none. The audit's Tier B lists "Transient `AtomTable.Intern` double string hash" (a different issue, about hashing twice on a miss). Phase 41 (ADR-048) made sub_atom code-point correct and phase 36/43 made its enumeration lazy; neither touched interning.

---

### 8. `length/2` walks a proper list twice (cycle check, then count) and tries four clauses in order

**Locations** — `src/Shumway.Embedding/Prelude.cs:625-641`: clause 1 (`integer(N), N < 0`), clause 2 (`nonvar(L), '$cyclic_spine'(L), !, …`), clause 3 (`nonvar(L), '$list_length'(L, M), !, …`), clause 4 (`'$length_walk'`). `'$cyclic_spine'` is `ListCursor.IsCyclic` = a full `SkipSpine` (`src/Shumway.Builtins/ListCursor.cs:37-64`); `'$list_length'` (`src/Shumway.Builtins/MultiSolutionHelpers.cs:56-63`) is another full `SkipSpine` and already *fails* on a cyclic spine.

**What it costs.** The overwhelmingly common call — `length(+ProperList, ?N)` — pays two O(n) spine walks (with `SpineGuard` cycle detection in both) plus three clause tries with choice-point creation and cuts, where one walk suffices. `length/2` is called in inner loops of list code everywhere (`nth`, `aggregate_all`, `countall`, grammar helpers, the tabling driver).

**Proposed change.** Reorder: try `'$list_length'` first (it fails on cyclic and on partial lists); only on failure ask `'$cyclic_spine'` to pick between `fail` / `resource_error` / `type_error`. Equivalently: a single `'$list_length_or_cyclic'(L, M, Cyclic)` builtin returning the verdict of one walk, and the four clauses collapse to one with an if-then-else (compiled inline, ADR-025). Keep the `integer(N), N < 0` check first (it is O(1)).

**Estimated gain.** ~2× on `length/2` over proper lists (one walk instead of two, no residual clause tries). Measure with a `length/2` loop over a 10,000-element list, `--alloc` and `statistics`.

**Risk.** None; the observable behaviour (Neumerkel's length cases cited in the comments) is preserved because the error cases are decided only after the fast path fails.

**Novelty check.** `git log | grep -i "length/2"` → none; the prelude comments document issue #108 / length#26-27 (correctness) and the `'$list_length'` fast path, not the double walk.

---

### 9. `aggregate_all(count|sum|max|min)` and `countall/2` build a findall list only to fold it

**Locations** — `src/Shumway.Embedding/Prelude.cs:970-994`: `countall(G, N) :- … findall(t, G, Ts), length(Ts, N).`, `aggregate_all(count, Goal, Count) :- findall(t, Goal, L), length(L, Count).`, `aggregate_all(sum(Expr), Goal, Sum) :- findall(Expr, Goal, L), '$sum_list'(L, 0, Sum).` (`max`/`min` are not defined here at all — a `aggregate_all(max(X), …)` call falls through to failure/other clauses).

**What it costs.** A count over k solutions copies k `t` atoms into a `SolutionBuffer`, builds a k-cell list on the heap, then walks it. Rules engines count matches constantly (`aggregate_all(count, violation(_), N)`). The sum variant copies every `Expr` solution as an image before evaluating it.

**Proposed change.** `'$findall_count'(Goal, N)`: run the findall collect loop (the `$findall_push`/`$findall_collect` pair, `MetaBuiltins.Findall.cs`) with a counting sink instead of a buffer — the buffer already tracks `Count` (`src/Shumway.Core/SolutionBuffer.cs:58`), so the smallest change is a collect variant that unifies the count and discards the buffer without materializing the list. Same shape for `sum`/`max`/`min` (fold the value on the fly, evaluating `Expr` in the solution's bindings). Add the missing `max(X)`/`min(X)`/`max(X,W)`/`min(X,W)`/`bag`/`set` clauses while there (SWI's set; Logtalk and library code expect them).

**Estimated gain.** Removes the O(k) heap list and its materialization: 2–3× on `aggregate_all(count, …)` with many solutions; constant memory instead of O(k).

**Risk.** None of the invariants; findall's cut/catch semantics are reused unchanged.

**Novelty check.** `git log | grep -i "aggregate_all"` → none. Phase 43 #165 reworked findall/bagof/setof buffers; `aggregate_all` was left as the Prolog wrapper above.

---

### 10. `EnginePool`'s only isolation policy is `FreshEngine`, which rebuilds a whole engine (prelude bundle load + consult) per rental; the existing `save/restore` machinery could give a cheap "reset dynamic state" policy

**Locations** — `src/Shumway.Embedding/EnginePool.cs:116-142` (`Acquire`: `_factory()` on `FreshEngine`), `src/Shumway.Embedding/PrologEngine.cs:1096-1107` (`InstallPrelude` → `LoadBundleCore(baked)`), `src/Shumway.Embedding/PrologEngine.DynamicStore.cs:772-812` (`SaveDb`/`RestoreDb`: snapshot of user dynamic clause lists; restore re-asserts clause by clause through `AppendDynamicClauseIncremental`).

**What it costs.** A request-per-rental rules engine that asserts facts about the request (the natural shape) must use `FreshEngine` to avoid leaking state, and so pays `new PrologEngine()` (bundle load of the prelude and libraries, "a new engine's first query 22 ms → 11 ms" per phase 43) plus the user `ConsultString` plus the first-query product build (`ProgramProductBuilds`) on every request. `ReuseState` avoids all that but leaks asserts/recorded/globals/flags between rentals.

**Proposed change.** A third `PoolReusePolicy.ResetState`: the pool calls `SaveDb()` after the factory, and on `Return` runs a bulk reset — clear user dynamics (`ClearDynamicClauses`), re-install the snapshot *into the store only* (no incremental chain append: there is no running activation at return time, so `_dynStore.AppendClause` + `InvalidateDynamicCache` suffice and the next setup links from the store), clear `GlobalVars`, the recorded database, `flag/3` store and `abolish_all_tables`. Expose the same as `engine.ResetToSnapshot()` for hosts that manage their own engines. Later, a cheaper variant can keep the persistent buffer and only truncate the dynamic region (ADR-015 already relays mutation through the store).

**Estimated gain.** Replaces ~10–20 ms of engine construction + consult per request with a store reset proportional to the snapshot size (µs for a handful of predicates); the first-query product build after a reset is the same as after any assert (the product cache survives when the dynamic functor set is unchanged, `PrologEngine.Query.cs:862-880`).

**Risk / invariants.** `assertz`/`retract` are not trailed and the reset is not a backtracking operation — it runs between queries, which is a safe point by construction (the same point `CompactDynamicCodeBuffer` uses, `:745-750`). Thread-agility holds (the pool serialises access). An ADR is not required (no invariant changes; it composes existing mechanisms), but ADR-010 should document the policy.

**Novelty check.** `git log | grep -iE "EnginePool|FreshEngine|ReuseState"` → none; the audit's Tier B "`new Engine` per query = ~512KB LOH … pool/reset" is about the *activation* heap (fixed by `HeapBufferPool`), not engine-level state reset. `docs/guide/api-reference.md`/`interop.md` document only the two policies.

---

### 11. CLP(FD): no propagation agenda — `clpfd_narrow` re-runs every propagator of a variable synchronously and recursively, with no "already scheduled" deduplication

**Locations** — `src/Shumway.Libraries/clpfd.pl:212-243` (`clpfd_narrow(X, NewDom)` → `put_attr` then `clpfd_run_narrowed(Props)` which `call`s every suspended propagator in turn), `:245-256` (`clpfd_post` = watch + run; `clpfd_watch` prepends the propagator to each variable's list), `:258-262` (`'$fd_set'` → `clpfd_run(Props)`), `:265-297` (`verify_attributes/4` runs all propagators on binding). `grep -n -i "queue|agenda|schedul" clpfd.pl` → no hits.

**What it costs.** Propagation is depth-first and unbounded: narrowing X runs propagator P1 which narrows Y which runs all of Y's propagators (including P1 again if it watches Y, and P2… which may narrow X again, re-running P1's siblings) before the first loop even reaches P2. On a dense model (n variables each watched by k propagators — `all_different` decompositions, scaled linear equations) the same propagator is re-executed many times per external event where a queue with a per-propagator "scheduled" bit runs it once per fixpoint round; and the recursion depth is the propagation chain length. The engine-level `$fd_linear` and Régin builtins reduce *per-run* cost (phase 28/43), not the number of runs.

**Proposed change.** The standard agenda: `clpfd_narrow` pushes the variable's propagators onto a global queue (`b_setval`/`nb_setval`-backed, or better a C# `Activation`-side queue with trail-aware push/pop — the wake-queue machinery of ADR-049 is a close relative) with a per-propagator "queued" flag stored in the propagator term's mutable slot (`'$nb_setarg'/3` exists); `clpfd_post` and the hook drain the queue to fixpoint at the top of the cascade. Idempotent propagators (the bounds ones) then run at most once per round.

**Estimated gain.** Model-dependent: large on dense models (SEND+MORE-shaped, queens with `all_different` pairwise), nil on sparse ones. Honest expectation: 1.3–2× on the `clpfd` queens/sendmore benchmarks in `tests/Shumway.Tests.Benchmarks`; measure propagator invocation counts before/after (a counter in `clpfd_run_narrowed`) alongside wall time, oracle-verified as `docs/benchmarks/cross-engine-comparison.md` prescribes.

**Risk / invariants.** The queue must be undone on backtracking (store it in a `b_setval` global or an attributed "agenda" variable so trail unwinding discards it); the cut/exception interaction (a propagator throwing mid-drain) mirrors what `clpfd_run` has today. ADR-051 (domains on the heap) and ADR-052 (attribute table observes) are untouched — only when propagators run changes, not where state lives. Scryer's own `clpz.pl` (used in the cross-engine comparison) carries its own queue, so that comparison is unaffected; this is about Shumway's own `library(clpfd)`.

**Novelty check.** Phase 6 closure (`docs/history/phase-6-closure.md:85-105`) describes bounds propagation per constraint; phase 28 (native domain layer), 43 (globals, `cumulative`, `@L`) and ADR-051 change representation and individual propagators. No doc or commit mentions a propagation queue/agenda for Shumway's clpfd.

---

### 12. Small per-call allocations in frequently-called builtins

Each is minor alone; together they are the allocation floor of the write/format/sort family. All verified:
- `src/Shumway.Builtins/SortBuiltins.cs:104-117` — `keysort/2`'s `ExtractPairKey` resolves the pair's functor to its *name string* (`AtomTable.GetById(atomId)?.Name`) and compares `name != "-"` for every element; compare the functor id with a cached `'-'/2` fid (as `ListBuiltins.cs:123` does for its helper) — removes a string compare and a table read per element.
- `src/Shumway.Builtins/IOBuiltins.cs:481-491, 812-819, 1030-1049` — `format/2,3` allocates a `List<Cell>` for the argument list, a `ColumnWriter` (with its own `StringBuilder` and `List<(int,char)>`) and a `TermRenderOptions` per `~w`/`~p`/`~q` directive on every call. Pool the `ColumnWriter`+list per `Activation` (clear-on-use, the `MaterializeScratchMap` pattern at `Materializer.cs:39-46`) and keep two static `TermRenderOptions` instances (numbervars on/off) — `format/2` is the hot output path of every report-generating rules engine.
- `src/Shumway.Builtins/SortBuiltins.cs:139, 165-184` — `sort/2`/`msort/2` sort a `List<Cell>` with a capturing `Comparison<Cell>` lambda (one closure per call, fine) but `BuildList` then writes 2n+1 cells with per-cell `SetHeap` calls; a `Span<Cell>` over the reserved range (the engine exposes raw heap access to the zero-copy interop path per `docs/guide/interop.md` §4) halves the bounds checks. Micro.
- `src/Shumway.Builtins/AtomListBuiltins.cs:358-359` — `atom_codes(+A, +Codes)` walks the list once in `IsProperListCell` and again in `ReadCodesString`; a single walk that compares against the atom's text as it goes (bail on first mismatch) avoids the `StringBuilder` entirely in the comparison mode.

**Estimated gain.** 1–3 fewer allocations per call on each; measurable only with allocation counters on tight loops (`format("~w~n", [X])` ×1e6).

**Novelty check.** None of these appear in the audit or the closures (`git log | grep -iE "keysort|format/2|ColumnWriter"` → none).

---

## Considered, but already done or explicitly deferred (do not re-propose)

- **findall/bagof/setof heap→AST→heap round trip** (audit Tier B) — done: one `SolutionBuffer` per call (#165, `455ad20`); interpreter-side cost reduced (`29852e6`).
- **recorded/3 as images** — done (#168, `1ba58be`).
- **Assert path literal-pool snapshots and per-assert pipeline** (audit Tier A #2) — largely done: facts are kept as head images and compiled by `FactImageCompiler` without the clause pipeline (`c4dbfcf`, `DynamicStore.cs:2314-2317`); `RefreshLiteralPoolsIfChanged` is version-guarded (`:2626`, `:3003-3007`); the `ClauseCompiler` is reused per engine (`_assertClauseCompiler`, `:2354`); a query after an assert brings only the mutated predicates up to date (`896446c`, `9621c03`). Rule clauses still go `MaterializeClauseRegister` → `ClausePipeline` → `ModuleRewrite` per assert (`:2331-2337`), which phase 43 lists as deferred ("images in clauses asserted at run time").
- **retract**: tail-copy per retract (audit Tier A #6) — done (window into the live list, `RetractCursor`); first-argument index for candidates — done (`DynamicClauseIndex`, `RetractStep`); pattern read off the heap without an AST — done.
- **Materializer re-interning atoms `permanent: true`** (audit #6) — done (`Materializer.cs:107-130`, lazily cached ids, transient interns).
- **Per-query re-transform of every module / merged map copies** (audit #5) — done: product cache, `ModuleTransformEntry`, `LayeredIntMap`, persistent address/alias/predicate caches, elision replay, import-map cache, debug-table cache. Item 3 above is what remains.
- **`new Engine` per query = LOH churn** (audit Tier B) — done (`HeapBufferPool`, `PooledActivationConfig`).
- **copy_term/2 through an AST** — done (`HeapTermCopy`).
- **sub_atom/5 materialising O(n²) decompositions** — done (lazy `'$sub_atom_enum'`, `Prelude.cs:678-684`); **atom_concat mode-directed split** — done.
- **length/2 native count** — `'$list_length'` exists; item 8 is about the redundant second walk, not the count.
- **Standard-order fast paths** (`59821ee`), **unify fast paths** (`1572dd7`), **error culprit read lazily in `=..`/`functor/3`** (`6d61a82`) — done.
- **Typed foreign predicates reading/writing common shapes without a Term tree** (#168, `b9a4342`) and **`SolveOnce<T>` direct read** — done; item 3.5 extends the same mechanism to `Query<T>`.
- **The `throw/1` ball as a Term tree; a cross-query pool of solution buffers** — on phase 43's deferred list; not re-proposed.
- **Tier-1/interpreter dispatch, functor-table lookups, trail/choice-point growth checks** — audit Tier A #1/#3/#4 and Tier B core items are out of this review's scope (core/Tier-0/Tier-1 agents).
- **Rejected by me after reading**: `write/1`/`print/1` already render straight to the `TextWriter` (`IOBuiltins.cs:18-22`, `TermRenderer.Render(engine, cell, writer, options)`) — no intermediate string; `read_term` builds one `Lexer`+`Parser` per sentence (`MetaBuiltins.Streams.cs:781-789`) but both are thin wrappers over the sentence string and the operator table is referenced, not copied — not worth a pool; `sort/2`'s `List.Sort` with a comparison delegate is the right primitive given `StandardOrderComparator`'s recursion limits; `between/3`, `nth0/nth1`, `append/3`, `reverse/2`, `last/2`, `msort/2`, `keysort/2` are already builtins with cursor-based backtracking.
