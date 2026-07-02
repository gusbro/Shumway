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
- [x] **E5** 🟡 `NativeReftype.cs:79,84` — `nelem` for atom/string is the UTF-8
      byte count, not the char count. *Resolved as by-design: the invariant native
      C relies on is `nelem == strlen(crep.cstr)` (byte length); under Arity's
      single-byte encodings byte==char count. Documented in code +
      generic-term-interop §10b.*
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
- [x] **E11** ⚪ Minor batch, all resolved:
      *(a) `string_search` 0-based VERIFIED CORRECT against ARITY.HLP ("Location is
      offset from 0"); bonus: added Arity's `string_search/4` (case flag).
      (b) `MapReturn` unknown scalar return now throws (a wrong calli signature can
      corrupt the stack) instead of silently defaulting to int; `char` return added.
      (c) `NativeTransform.cs:86` swallow is NOT a bug — it's the best-effort hint
      pre-pass; the real parse at TransformBlock throws a proper consult error on
      the same text.
      (d) `RecordedDatabase` refs widened int→long throughout (+ MetaBuiltins call
      sites) — no int32 overflow; refs live in 60-bit int cells anyway.
      (e) `AddrOfLocal` bare-ident acceptance is deliberate (runs only for
      prototype-typed pointer params where by-value is meaningless; corpus passes
      pointer-typed locals without `&`) — documented at the helper.
      Also fixed en passant: malformed PrologRuntimeException kinds in the
      recorded/string region ("type_error(atom, _)" as an ATOM kind → proper
      ("type_error","atom") kind/detail split, the chunk-323 bug pattern).*

## Wave 2 — Interop hot path

- [x] **A2** 🔴 `RegisterMarshalling.ReadRegisterAsTerm` — heap cell + full AST
      walk per argument on every native/foreign call. *Fixed: a REF register
      materializes from its heap home directly (no throwaway cell); immediate
      Int/Atom become their Term with zero heap traffic (atom-id cache seeded);
      only immediate non-scalars stage through a temp cell. Plus the new
      zero-allocation `DerefRegisterCell` primitive.*
- [x] **A3** 🔴 `MetaBuiltins.ReadSlot` / `NativeBlockCompiler.ReadReftypeSlot` —
      allocates `CompoundTerm("$foreign",[IntTerm])` to extract an int id.
      *Fixed: both (plus `NativeBlockRunner.ReadInput`'s reftype branch) read the
      dereferenced cell and check `Tag.Foreign` directly — zero allocation on
      fill_par/reftype_term.*
- [-] **C1** 🔴 `TermConverters.cs` scalar boxing — **REFUTED by measurement**:
      the guarded `(T)(object)value` pattern under `typeof(T) == typeof(int)` is
      the standard BCL idiom and RyuJIT eliminates the box/unbox pair in the
      specialized value-type instantiations. Verified empirically on .NET 10:
      0 bytes allocated over 100k calls. The file's "no boxing" comment is correct.
- [x] **C2** 🔴 `ConventionConverters.cs` — `MethodInfo.Invoke` + fresh `object[]`
      per conversion. *Fixed: BuildEntry compiles the resolved MethodInfos to
      delegates via Expression.Lambda (once per type; interpreter fallback keeps
      AOT correct); user exceptions now surface unwrapped (no
      TargetInvocationException translation needed).*
- [ ] **D2** 🔴 `NativeCall.BuildInvoker` — boxed `object[]` per call +
      `Unbox_Any`. **Deferred to a later round with reasoning**: both callers
      (interpreter env, IL boxed-args channel) traffic in `object` today, so a
      typed invoker needs per-signature delegate types + a typed IL-emit channel;
      the box/unbox pair is nanoseconds against the materialize + calli + demat
      that dominate the same call. Revisit with a corpus benchmark.
- [x] **D3** 🔴 `NativeReftype.AllocString`/`AllocCString`/`ReadString` — `GetBytes`
      byte[] per string per call. *Fixed: pooled buffers (`ArrayPool<byte>`) for
      encode and decode; only the result string allocates.*
- [x] **D4** 🟡 out-scalar/out-string HGlobal cells per call. *Fixed: per-engine
      16-slot native scratch block, bump-allocated with mark/restore (nested calls
      compose; engine single-threaded); HGlobal only on overflow. Both P/Invoke
      paths.*
- [x] **A1** 🔴 `NativeBlockRunner.RunBlock` — invariant index/kind/scalarFloat
      maps rebuilt per call. *Fixed: cached lazily on `NativeBlockEntry`
      (EnsureMaps); the '$native_run' dispatch uses the entry-based overload. The
      env/outputs dictionaries remain (the tree-walk interpreter is keyed by name
      throughout and is the fallback/AOT path only — the compiled-delegate path
      bypasses it entirely).*
- [x] **A4** 🟡 `MetaBuiltins.NativeRun` — block name materialized + string-keyed
      dict per dispatch. *Fixed: reads the raw atom cell and resolves through a
      per-host atom-id→entry cache (invalidated on block registration).*
- [x] **C3** 🟡 composite converters reflect per element. *Fixed the dynamic
      bridges: `ToTermDynamic`/`FromTermDynamic` cached delegates now COMPILED
      (engine.ToTerm<T>((T)v)) instead of wrapping MethodInfo.Invoke + object[]
      per element. The [PrologPredicate] bridge scalar path already benefits from
      A2 (no heap cell); emitting cell-direct reads in the source generator is
      deferred to a later round (small residual: one AST node per scalar arg).*
- [-] **C4** 🟡 `Solution.Get<T>` — **largely not actionable**: the converter
      tiers already cache per type (user dict probe + JIT-specialized scalar
      chain), and the per-solution Bindings dictionary is inherent to the
      Solution API contract (each solution owns different bindings). A streaming
      typed-cursor API is a feature idea, not a fix — rejected for now.
- [x] **C5** 🟡 Reftype snapshot for read-only methods. *Resolved by design: the
      `TermSlot` parameter IS the borrow path (zero copy, no writeback);
      `Reftype` means snapshot+writeback by contract. Documented in
      generic-term-interop §10c-bis so users pick correctly.*
- [ ] **D1** 🔴 `NativeReftype.Materialize/Free` — full AllocHGlobal graph per
      call. **Deferred to a later round with reasoning**: a cache/diff per
      TermSlot is unsound without knowing what the native side read or wrote
      (it may mutate the graph, so the cache is dirty after every call), and
      node pooling saves only the AllocHGlobal (~100ns/node) against the field
      writes + string encodes + dematerialize Term allocations that dominate the
      same walk. Revisit with a corpus benchmark showing materialize as a real
      bottleneck.
- [-] **D5** 🟡 `NativeReftypeAllocator.Fill` per-node delegate calls — **inherent
      to the contract**: every node must be created by the LIBRARY's `newreftype`
      so its heap owns the graph (that is the point of allocator mode); Arity's
      API has no batch form. Rejected.

## Wave 3 — WAM codegen (Arity shapes)

- [x] **W1** 🔴 `MetaTransform.cs` — `once/1`+`ignore/1` not rewritten (snips
      `[! G !]` = once): heap goal build + runtime meta-dispatch per snip.
      *Fixed: callable-goal once/ignore rewrite to synthesized helpers
      (`'$once_N'(Vars) :- G, !.`; ignore adds a bare-fact clause) — G compiles
      as inline WAM, `!` inside G correctly scoped to the helper (= once's
      barrier), bindings flow via the head. Var/non-callable goals still take
      the runtime path (ISO errors). PreludeIlDifferentialTests' once/ignore
      cases retargeted to variable goals so they keep exercising the prelude
      fallback (same precedent as forall/catch). 9 semantic tests incl. snips.*
- [x] **W2** 🔴 `ClauseCompiler.cs:122-130` — `!` preceded only by inline goals
      still classified deep cut. *Fixed: a `!` whose preceding goals are all
      `IsInlineBodyGoal` (arith guards, earlier cuts — no calls, no CPs, `_b0`
      intact) is a frameless neck_cut; only a `!` after a real call keeps the
      get_level + Y-slot machinery. Deliberately does NOT extend the
      chunk-model transparency / scheduler targeting (the failed Phase-25 arc).
      Note: `=/2` is not in IsInlineBodyGoal, so `X = foo, !` stays deep —
      widening that classification is a separate, riskier change (W9 candidate).*
- [-] **W3** 🔴 assert path pipeline cost — **already fixed by chunks 427-431**
      (the audit quoted the pre-427 efficiency doc): facts without mode info
      skip ClausePipeline entirely (`CompileRuntimeAssertClause` fast path), the
      ClauseCompiler instance is reused (`_assertClauseCompiler`), and the three
      pool snapshots are skipped when pool counts are unchanged
      (`RefreshLiteralPoolsIfGrown`). The residual — RULES still run the
      pipeline — is semantically required (control constructs must lower).
- [ ] **W4** 🟡 Tier-0 `;`/`->` lowering is helper-predicate + Call + CP even for
      deterministic ITE (Phase-29 fixed IL only). Inline shallow ITE.
- [ ] **W5** 🟡 `DcgTransform.cs:139-168` — 2 redundant `=/2` state-reconciliation
      goals per DCG disjunction branch. Substitute the out-var into branches.
- [ ] **W6** 🟡 missing fused `execute_builtin` (last-goal builtin =
      call_builtin + deallocate_proceed, 2 dispatches). **Deferred with
      reasoning**: `Opcode.ExecuteBuiltin` already exists (chunk 248, linker
      rewrite for foreigns) BUT the IL compiler REJECTS it in clause bodies
      (`IlPredicateCompiler.cs:909` "tail builtin — needs CallBuiltin
      machinery") — if the WAM compiler emitted it, those predicates would
      lose Tier-1 promotion, a far bigger regression than one saved Tier-0
      dispatch (frameless case only; with a frame it's 2 ops either way).
      Prerequisite: IL-side ExecuteBuiltin body support first.
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
