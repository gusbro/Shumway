# Phase 33+ — Findings backlog (exhaustive audit, 2026-06-30)

Source: six-way audit (errors / interpreter / WAM codegen / IL / LTO / interop)
with the Arity-compat workload as the primary lens. This file is the master
backlog: items get checked off as waves land. Waves 1–5 are the first pass;
later rounds continue until **every** item is attacked (fixed, or explicitly
rejected with a reason recorded here).

Legend: 🔴 high · 🟡 medium · ⚪ low. `[x]` done · `[-]` rejected/not-a-bug (with note).

---

## Wave 1 — Correctness critical (E-series)

- [x] **E1** 🔴 `NativeBlockRunner.cs` `PInvokeCall`/`PInvokeFromIl` — no `try/finally`
      around native marshalling: any exception between `AllocHGlobal` and the free
      loop leaks every already-allocated native buffer (cstrings, out-scalars,
      out-string cells, reftype handles). *Fixed: both paths restructured with
      try/finally; every buffer tracked at allocation time; read-backs stay on the
      success path.*
- [x] **E2** 🔴 `NativeBlockRunner.cs:382` + `NativeReftype.cs:162` — HGlobal-path
      `NativeReftype.Free` walks the graph recursively; if the native fn replaced a
      `cstr` or grew `pars` with its own malloc, we `FreeHGlobal` a foreign pointer
      → heap corruption. *Fixed: allocation-recording `Materialize` overload +
      `FreeRecorded` frees exactly Shumway's own set — native-swapped nodes are
      borrowed (never freed), unlinked buffers still freed. Real-DLL swap test.*
- [x] **E3** 🔴 `MetaBuiltins.cs:4646` `string_term/2` — parses with
      `OperatorTable.Default()` but renders with `engine.Operators`: not a faithful
      inverse under `:- op/3`. *Fixed: parses with the host's live table.*
- [x] **E4** 🟡 `NativeReftype.cs:67,71,113` — silent 32-bit truncation of 64-bit
      integers into `cint`. *Fixed: catchable `representation_error(native_cint_32)`
      on materialize when outside int32 (IntTerm + BigIntTerm).*
- [ ] **E5** 🟡 `NativeReftype.cs:79,84` — `nelem` for atom/string is the UTF-8
      byte count, not the char count. Decide vs Arity semantics and fix/document.
- [x] **E6** 🟡 `PrologEngine.NativeTextEncoding` — a non-byte-oriented encoding
      (UTF-16/32) silently corrupts NUL-terminated marshalling. *Fixed: setter
      rejects encodings where ASCII 'A' isn't a single non-zero byte.*
- [x] **E7** 🟡 `MetaBuiltins.cs:4381` `RequireGroundKey` — only checks the top
      tag; a compound key with an inner unbound var passes and silently never
      matches (VarTerm name-equality keys). *Fixed: iterative deep-ground walk →
      instantiation_error.*
- [x] **E8** 🟡 `MetaBuiltins.cs:4649` `string_term` — unguarded parse: malformed
      text throws a .NET ParseException instead of a Prolog error/failure.
      *Fixed: Parse/Lexer exceptions → catchable `syntax_error`.*
- [x] **E9** 🟡 `EnginePool.cs:107` — returned engines keep asserted clauses /
      recorded-DB / globals from the previous rental. *Fixed: `PoolReusePolicy`
      (`ReuseState` default, documented loudly; `FreshEngine` discards on return
      so every rental starts from factory state).*
- [x] **E10** 🟡 `Parser.cs:411-457` — under arity_compat a `{...}` body goal in a
      non-DCG clause is swallowed as a native block (CLP(R) `{}/1` conflict), and a
      codegen-less `'$native_goal'` degrades to silent success. *Fixed the silent
      success: `'$native_goal'/1` now throws a loud system_error if it survives to
      execution. The CLP(R)-vs-arity_compat `{}` routing is inherent to the flag
      (the two can't share an engine anyway) — documented trade-off retained.*
- [ ] **E11** ⚪ Minor batch: `string_search` 0- vs 1-based check vs Arity;
      `MapReturn` silent default to int; `NativeTransform.cs:86` swallowed
      CParseException; `RecordedDatabase._nextRef` int32 overflow;
      `AddrOfLocal` accepts a bare ident as out-pointer.

## Wave 2 — Interop hot path

- [ ] **A2** 🔴 `RegisterMarshalling.ReadRegisterAsTerm` — heap cell + full AST
      walk per argument on every native/foreign call. Scalar fast path reading the
      register cell directly (benefits every boundary).
- [ ] **A3** 🔴 `MetaBuiltins.ReadSlot` / `NativeBlockCompiler.ReadReftypeSlot` —
      allocates `CompoundTerm("$foreign",[IntTerm])` to extract an int id. Read the
      cell tag directly (`fill_par`/`reftype_term` hot path).
- [ ] **C1** 🔴 `TermConverters.cs` — scalar `ToTerm<T>`/`FromTerm<T>` box via
      `(object)` despite `typeof(T)` dispatch. Unboxed bridges.
- [ ] **C2** 🔴 `ConventionConverters.cs:64,88,111` — `MethodInfo.Invoke` +
      fresh `object[]` per conversion. Compile to delegates once (InvokerFor
      precedent).
- [ ] **D2** 🔴 `NativeCall.BuildInvoker` — boxed `object[]` per call +
      `Unbox_Any`. Strongly-typed per-signature invoker.
- [ ] **D3** 🔴 `NativeReftype.AllocString`/`AllocCString` — `GetBytes` byte[] +
      HGlobal per string per call. Pooled/stackalloc + `GetBytes(Span)`.
- [ ] **D4** 🟡 out-scalar/out-string HGlobal cells per call → stackalloc/pinned
      reusable.
- [ ] **A1** 🔴 `NativeBlockRunner.RunBlock` — five dictionaries per call +
      invariant index/kind maps rebuilt. Precompiled per-block plan.
- [ ] **A4** 🟡 `MetaBuiltins.NativeRun` — block name materialized + dict lookup
      per dispatch. Cache by atom id.
- [ ] **C3** 🟡 generated `[PrologPredicate]` bridges: 2 allocs + 2 boxes per
      scalar call; composite converters reflect per element.
- [ ] **C4** 🟡 `Solution.Get<T>` re-resolves converter per access; per-solution
      dictionary.
- [ ] **C5** 🟡 Reftype snapshot copies whole term both ways even for read-only
      interop methods → `[In]`/borrow convention.
- [ ] **D1** 🔴 `NativeReftype.Materialize/Free` — full AllocHGlobal graph per
      call → pool 32-byte nodes / cache+diff per TermSlot.
- [ ] **D5** 🟡 `NativeReftypeAllocator.Fill` — one marshalled delegate call per
      node.

## Wave 3 — WAM codegen (Arity shapes)

- [ ] **W1** 🔴 `MetaTransform.cs` — `once/1`+`ignore/1` not rewritten (snips
      `[! G !]` = once): heap goal build + runtime meta-dispatch per snip.
      Rewrite to `'$once_N'(Vars) :- G, !.` helper (negation-helper pattern).
- [ ] **W2** 🔴 `ClauseCompiler.cs:122-130` — `!` preceded only by inline goals
      (`=`, `is`, comparisons) still classified deep cut → frame + get_level +
      Y-slot instead of frameless neck_cut.
- [ ] **W3** 🔴 assert path — full 4-pass ClausePipeline + fresh pools/emitter +
      3 pool snapshots per assertz (24µs/4.3KB). Fact fast-path + reuse.
- [ ] **W4** 🟡 Tier-0 `;`/`->` lowering is helper-predicate + Call + CP even for
      deterministic ITE (Phase-29 fixed IL only). Inline shallow ITE.
- [ ] **W5** 🟡 `DcgTransform.cs:139-168` — 2 redundant `=/2` state-reconciliation
      goals per DCG disjunction branch. Substitute the out-var into branches.
- [ ] **W6** 🟡 missing fused `execute_builtin` (last-goal builtin =
      call_builtin + deallocate_proceed, 2 dispatches).
- [ ] **W7** 🟡 `ModuleCompiler.ComputePoolFree` — string/bigint literals exclude
      a predicate from cross-query cache (only floats stable). Make those pools
      append-only-stable and exempt.
- [ ] **W8** 🟡 `$get_cut_barrier` forces Y-slot + frame on clauses with a branch
      cut. Register-thread the barrier.
- [ ] **W9** ⚪ Minor batch: a_int fast-lane float/bigint literal kinds;
      var-clause duplication per bucket (keys×varClauses); body-local CSE;
      helper dedup by structural key.

## Wave 4 — IL dispatch & promotion

- [ ] **L1** 🔴 Stage B.4 — runtime `Call→CallIl` rewrite after promotion (no
      bundle path pays OnDispatch interface+dict+closure forever).
- [ ] **L2** 🔴 `IlPromotionStore.RunOnLargeStack` — synchronous 16MB
      thread-create + Join per compile on the query thread. Background compile
      queue with a persistent worker.
- [ ] **L5** 🟡 `EvictionChurnLimit=3` permanent Tier-0 banishment — bulk assertz
      of 3 clauses kills IL forever. Coalesce mutations / re-arm after stable
      reads.
- [ ] **L3** 🔴 16KB bytecode cap (Sigil O(n²)) — big fact tables permanently
      Tier-0. Lift via linear emitter or sub-range splitting.
- [ ] **L6** 🟡 `RegionMemberOk` rejects switched-chain/indexed-atom multi-clause
      members → region fragmentation.
- [ ] **L7** 🟡 `AIntBin/AIntCmp` — compile-time-constant kinds passed as runtime
      args re-branch per call. Specialize at emit.
- [ ] **L8** 🟡 chunk-216 indexed dispatch keeps WAM-backed lazy model (no
      strip-wam, first-call build). Bake IlIndexGraph as an IL jump table.
- [ ] **L9** ⚪ Minor batch: region self-delegate CSE gate ≥3→≥2;
      MaybeCollectHeap on non-allocating self-tail loops; ground-fact
      unify-with-constant fast path.

## Wave 5 — LTO / startup / size

- [ ] **T1** 🔴 prelude baked whole (~780 lines + IL), never reach-pruned against
      the program.
- [ ] **T2** 🔴 no compression anywhere in `.shum`.
- [ ] **T3** 🔴 persisted-IL `Assembly.Load`+patch+delegates per engine — needs a
      process-wide cache keyed like `_loadedNativeLibraries`.
- [ ] **T4** 🔴 WAM link (addresses, switch tables, `Call→CallBuiltin` rewrite)
      recomputed per query — bake into source-stripped bundles.
- [ ] **L4** 🟡 baked prelude ships `compiledIl: null` — prelude runs Tier-0
      until per-predicate re-promotion.
- [ ] **T5** 🟡 cross-module unfold = 3 meta-wrapper templates only; no general
      partial deduction; skips findall/call goal args; publics only.
- [ ] **T6** 🟡 triple representation per entry (source+WAM+IL) by default;
      ClauseTerms doubles `.shmo` size.
- [ ] **T7** 🟡 prelude compiled twice per link; ValidateOrThrow full consult per
      entry; CompileEntryToIl full engine per entry.
- [ ] **T8** ⚪ Minor batch: dead-arg elim / const-prop absent; unfold runs 2×
      per module; over-conservative roots.

## Later rounds (not yet waved)

Interpreter core:
- [ ] **I1** 🔴 PC lives in an engine field — store+reload per dispatched opcode.
      Thread a local `pc` through the switch, write back at goal boundaries.
- [ ] **I2** 🔴 findall/bagof/setof/copy_term round-trip through managed AST
      (2 deep copies per solution). Direct heap-to-heap copy.
- [ ] **I3** 🟡 retract materializes each shape-matching candidate per trial.
      Assert-time heap template or indexed candidate list.
- [ ] **I4** 🟡 dynamics below the JIT-index threshold walk linear chains with
      tombstone dispatch (indexing exists post-threshold; tune threshold /
      tombstone cost).
- [ ] **I5** 🟡 `Cut`→`CompactTrails` O(n) with no early-out when tops equal.
- [ ] **I6** 🟡 CP saves 10 control words incl. ViewGen for static predicates.
- [ ] **I7** 🟡 ProgramGeneration property read per dispatch tick.
- [ ] **I8** ⚪ Minor batch: Overflow branch per fetch; HasPendingWakeups cached
      bool; dbg_info out-of-band; CallBuiltin name/arity stamping deferral;
      _unifyPointer local threading; FlushPendingWakeupsSlow pooled scratch.

WAM↔IL boundary:
- [ ] **B1** 🟡 resume-marker encoding caps (functor id ~262K, 4096 cursors).
- [ ] **B2** 🟡 MaybeCollectHeap unconditional per marker resume.
- [ ] **B3** ⚪ double closure per promoted predicate; per-query dispatch cache
      rebuild.

Functional interop gaps:
- [ ] **D6** 🟡 `int**`/struct-by-value/array params rejected; allocator without
      setcflt/getargp throws instead of per-node fallback; IL compiler bails on
      reftype args that aren't globals; `$native_run` 32-var ceiling.
