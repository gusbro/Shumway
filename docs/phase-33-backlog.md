# Phase 33+ — Findings backlog (exhaustive audit, 2026-06-30)

Source: six-way audit (errors / interpreter / WAM codegen / IL / LTO / interop)
with the Arity-compat workload as the primary lens. This file is the master
backlog: items get checked off as waves land. Waves 1–5 are the first pass;
later rounds continue until **every** item is attacked (fixed, or explicitly
rejected with a reason recorded here).

Legend: 🔴 high · 🟡 medium · ⚪ low. `[x]` done · `[-]` rejected/not-a-bug (with note).

**Standing directive (user, 2026-07-02): every DEFERRED item gets a review pass
in a later round to see whether its blocking restriction can be LIFTED** — e.g.
W6 is deferred because the IL compiler rejects `ExecuteBuiltin` in bodies: study
teaching IL that shape with good performance instead of accepting the deferral.
Same for D1/D2 (benchmark the materialize/invoker costs), L7 (verify the JIT
constant-folding assumption with a disasm), W9(a-d), C3-remainder, B-series.
A deferral is a TODO with a prerequisite, not a closure.

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
- [-] **D2** 🔴 `NativeCall.BuildInvoker` — boxed `object[]` per call +
      `Unbox_Any`. **Rejected with measurement** (the deferral's revisit): a
      scalar `:- native` call is 2.02 µs / 327 B end-to-end in-query
      (failure-driven loop, baseline subtracted) — the box/unbox pair is noise
      inside that; a typed invoker needs per-signature delegate types + a typed
      IL channel for nanoseconds. The real cost was D1 (see below).
- [x] **D3** 🔴 `NativeReftype.AllocString`/`AllocCString`/`ReadString` — `GetBytes`
      byte[] per string per call. *Fixed: pooled buffers (`ArrayPool<byte>`) for
      encode and decode; only the result string allocates.*
- [x] **D4** 🟡 out-scalar/out-string HGlobal cells per call. *Fixed: per-engine
      native scratch, bump-allocated with mark/restore (nested calls compose;
      engine single-threaded). Originally a 16-slot block; SUPERSEDED by the D1
      chunked arena — out cells now come from the same arena as the reftype
      graphs, one mark restore frees everything. Both P/Invoke paths.*
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
- [x] **D1** 🔴 `NativeReftype.Materialize/Free` — full AllocHGlobal graph per
      call. *Fixed: per-engine chunked (64 KB) native ARENA, bump-allocated with
      a long mark (chunk<<32|offset) restored once in the finally — nodes, pars
      arrays, char* buffers and out cells all call-scoped, no graph-walking
      free. The deferral's "~100ns/node" estimate was REFUTED by the corpus
      benchmark it asked for: mat+free was 92.95 µs of a 96.67 µs 50-element-
      list marshal (96%) — AllocHGlobal/FreeHGlobal per node dominated
      everything. With the arena the end-to-end in-query list50 call dropped
      53–107 µs → 15.65 µs (~3.4–6.8×); small (2-node) 3.96 → 3.90 µs (graph
      too small to matter). Safety contract unchanged from the recorded-free
      mode it replaces (foreign pointers never touched); allocator mode
      (library heap) is untouched. The public `Materialize(Term)` API keeps
      HGlobal + walking `Free` for external callers.*
- [-] **D5** 🟡 `NativeReftypeAllocator.Fill` per-node delegate calls — **inherent
      to the contract**: every node must be created by the LIBRARY's `newreftype`
      so its heap owns the graph (that is the point of allocator mode); Arity's
      API has no batch form. Rejected.
- [x] **D-bonus** two latent phase-32 native-interop bugs the D1 gate exposed
      (they predate this wave — the tag-time gate must have run without a C
      compiler, so the P/Invoke end-to-end tests skipped silently):
      (1) `NativeBlockRunner.ReadScalar`'s single `?:`-chain typed the whole
      expression `double` (branches' best common type), so every integer
      out-scalar read boxed a Double and the compiled block's unbox-to-long
      threw InvalidCastException — rewritten as if/return so each branch boxes
      its own type. (2) `NativeBlockTyping` never resolved `:- c` typedefs for
      block-local declarations, so `s: pchar` typed as long and the compiled
      path bailed on every string use — typedefs now thread from the engine
      into both code generators (runtime delegate + build-time IL), and
      `ModelType` types non-char pointers as their out-scalar cell value
      instead of string. Plus an env-gated (`SHUMWAY_NATIVE_TRACE=1`) bail
      trace in `NativeBlockCompiler` for diagnosing future bails.

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
- [x] **W4** 🟡 Tier-0 `;`/`->` inline lowering — **ADR-025 APPROVED +
      IMPLEMENTED (stages a+c)**: `jump` opcode (dense block, tail renumbered),
      interpreter case, clause-local branch operands relocated via
      CompiledClause.DispatchSites → PredicateCompiler merge (all 5 assembly
      paths) → linker dispatch-site shift; ClauseCompiler emits eligible
      plain-goal ITE/disjunction inline (get_level; arity-0 try_me_else; cut;
      jump; trust_me) with branch vars forced permanent and BRANCH-AWARE
      Y-initialization tracking (snapshot/restore/intersect — the else path
      must not trust then-path inits). Gated by `PrologEngine.EnableInlineIte`
      (default OFF). **Remaining: stage (b) IL describe/emit, then flip the
      default** — today the IL compiler gracefully rejects the shape (Tier-0).
      14 semantic tests + differential vs the helper form.
      *STAGE (b) DONE (2026-07-04): the shape compiles to Tier-1 IL. Mid-body
      try_me_else → an arity-0 IL choice point pushed with `IlIteHelper.Resume`
      + the ELSE resume MARKER in the cursor slot (backtrack parks the marker
      as PC via ResumeAtReturnPc → the dispatch loop re-enters the delegate at
      the ELSE label — the chunk-218 protocol, persisted-patchable); trust_me
      marks the ELSE label; jump → unconditional br. One resume cursor per ITE,
      counted via its `jump` (never present in dispatch skeletons — the
      over-count-proof trick). The try_me_else-chain describer now FOLLOWS
      dispatch operands for clause boundaries (the linear me-else scan would
      mis-read inner ITEs); legacy SwitchedChain/IndexedAtom recognisers and
      the region/inline detectors reject jump-bearing bodies (still compiled
      by the main shapes). 8 promoted tests (Adr025StageBTests) incl.
      backtrack-into-ELSE, two-ITE bodies, multi-clause hosts, 4-config
      differential. Remaining: stage (d) A/B measurement → (e) default ON.*
- [x] **E12** 🔴 **HELPER-NAME COLLISION (latent, ≥ phase-32; found validating
      ADR-025)**: MetaTransform's synthesized-helper counter restarted per
      transform run, so the QUERY stub's `$disj_1` (findall collect loop, same
      user-module mangling + arity) collided with a consulted clause's
      `$disj_1` — the query-region definition shadowed the consulted helper and
      callers executed the WRONG body (`findall(R, classify(5,R), L)` over an
      if-then-else predicate threw instantiation_error; REPL-reproducible with
      a 1-line program). *Fixed with SCOPED naming: consult/assert transforms
      draw from the ENGINE's monotonic sequence (unique across consults;
      bounded atoms — a process-GLOBAL sequence was tried first and grew the
      functor table past the resume-marker fid cap mid-suite, breaking marker
      users); the query stub (the direct MetaTransform.Apply site in
      SetupQueryFromTerm) uses the reserved `$q` prefix, reused
      query-to-query. 8-test regression suite. NOTE: this RECONFIRMED B1 (the
      resume-marker ~262K fid cap) as a live ceiling — raise B1's priority.*
- [x] **W5** 🟡 `DcgTransform.cs:139-168` — 2 redundant `=/2` state-reconciliation
      goals per DCG disjunction branch. *Fixed: the shared endpoint is
      SUBSTITUTED into a branch whose own endpoint is a fresh `$Sn` variable
      (unique per transform — capture-safe rename); the explicit `=` remains
      only for a branch that consumed nothing (endpoint still sIn), which
      cannot be renamed. Matches the SWI/GProlog expander shape.*
- [x] **W6** 🟡 missing fused `execute_builtin` (last-goal builtin =
      call_builtin + deallocate_proceed, 2 dispatches). **Deferred with
      reasoning**: `Opcode.ExecuteBuiltin` already exists (chunk 248, linker
      rewrite for foreigns) BUT the IL compiler REJECTS it in clause bodies
      (`IlPredicateCompiler.cs:909` "tail builtin — needs CallBuiltin
      machinery") — if the WAM compiler emitted it, those predicates would
      lose Tier-1 promotion, a far bigger regression than one saved Tier-0
      dispatch (frameless case only; with a frame it's 2 ops either way).
      Prerequisite: IL-side ExecuteBuiltin body support first.
      *CLOSED (2026-07-03, deferred-item review). PREREQUISITE LIFTED: the IL
      compiler now accepts non-meta ExecuteBuiltin in bodies — eligibility
      (IsClauseBodyOpcode + CanCompileSingleClause terminator) + emit
      (dispatch, BuiltinReturnPc = engine.Cp for backtrackables — the
      caller-continuation tail contract — then proceed-return); META tail
      forms stay Tier-0 with an honest "ExecuteBuiltin(meta)" rejection.
      3 tests (Phase33W6Tests, hand-assembled bytecode through a bundle:
      interpreter + Warm-promoted IL, incl. tail between/3 full enumeration).
      THE FUSION ITSELF: REJECTED on census — 1 252 (test) / 1 353 (testGen)
      fusable static sites (CallBuiltin;Proceed frameless tails, ~11-13% of
      CallBuiltin sites), each saving ONE Tier-0 dispatch per execution; hot
      code promotes to Tier-1 where the fusion buys nothing, and the emission
      change ripples through every body-shape consumer (peepholes, chain
      patchers, describers). Revisit trigger: a Native-AOT (Tier-0-only)
      workload profiling hot on builtin-tailed frameless clauses. Facts
      pinned along the way: ExecuteBuiltin NEVER appears in
      CompiledPredicate.Bytecode today (the chunk-248 rewrite targets the
      linked program buffer, so Tier-1 promotion never saw it — the "bundles
      with foreign tails are Tier-0-locked" worry was unfounded); the
      interpreter's ExecuteBuiltin case doesn't route meta dispatch (loud
      dead-fallback guard, unreachable from the toolchain).*
- [x] **W7** 🟡 `ModuleCompiler.ComputePoolFree` — string/bigint literals exclude
      a predicate from cross-query cache (only floats stable). *Fixed after
      auditing the invariant: all three pools are per-engine `LiteralPool<T>`
      (append-only + deduplicating, "existing indices stay stable" documented),
      and the ONLY flow consulting the caches (query setup) always compiles
      against the engine's persistent `_literalPools`. Exempted GetBigInt/
      PutBigInt/UnifyBigInt + GetPstr/PutPstr alongside the float ops; the
      guard remains for any future LiteralId carrier outside the audited set.*
- [-] **W8** 🟡 `$get_cut_barrier` Y-slot + frame — **already conditional +
      remainder is by design**: chunk 408 gates the capture on
      `HasTransparentBranchCut(body)` (a cut-free body pays nothing — the
      audit's own suggested fix), and when a branch `!` exists the barrier
      variable crosses the helper call boundary, so a Y slot is REQUIRED by
      the chunk model — the register alternative is the ADR-021-rejected
      register allocator.
- [x] **W9** ⚪ Minor batch → moved to later rounds:
      (a) a_int fast-lane float/bigint literal kinds — real, needs encoding +
      both tiers; (b) var-clause duplication per bucket — interacts with the
      chunk-155x in-place chain machinery, risky; (c) body-local CSE — a new
      pass; (d) control-helper dedup by structural key — feasible (name-scoped
      canonical render). Also: widening `IsInlineBodyGoal` with `=/2` would
      extend the W2 neck-cut prefix to `X = foo, !` shapes (verify CP-safety
      of every =/2 emission form first).
      *CLOSED (2026-07-05) with one implementation and four evidence-based
      rejections. (e) `=/2` in IsInlineBodyGoal: IMPLEMENTED — CP-safety
      audited across BOTH lowerings (the Phase-26 inline get_*/unify_* form is
      plain unification; the call_builtin fallback for Y-var/both-nonvar
      shapes runs inline in the dispatch loop — Cp untouched, B0 untouched,
      no CP; attvar wakeups flushed by the cut dispatch as with arithmetic).
      Verified by disasm: `X = foo, !` now emits NeckCut where it emitted
      AllocateGetLevel+Cut; 2 regression tests incl. the Y-var fallback.
      (a) REJECTED by census: 1,081 arithmetic sites across test+testGen —
      exactly ONE has a float literal leaf, ZERO bigint. (d) REJECTED by
      census: 5,831 synthesized $disj/$neg helpers, 18-19% structural dupes
      (canonical-render census through the real ClausePipeline) ≈ ~100 KB
      pre-compression corpus-wide — post-T2/T6-Brotli the shipped delta is
      negligible; revisit only if a real workload's helper count explodes.
      (b) REJECTED: var-clause-per-bucket duplication is the standard WAM
      indexing design; sharing chain tails would entangle the chunk-155/156
      in-place mutation machinery for a size-only win. (c) REJECTED: a
      body-local CSE is a real compiler pass, not a minor — no corpus
      evidence of hot repeated subexpressions (ADR-021 discipline: quantify
      before building).*

## Wave 4 — IL dispatch & promotion

- [x] **L1** 🔴 Stage B.4 — runtime `Call→CallIl` rewrite after promotion.
      *Fixed (scoped): InstallCallIlRewrites now records every still-generic
      Call/Execute site by callee fid (persistent buffer only — the query
      overlay is rebuilt next setup and its array may be replaced mid-query;
      dynamic callees skipped to keep feeding JitIndexProfile). The
      `OnPromotionInstalled` hook (fired on the engine thread by both the
      sync promoting call and the async drain) publishes the delegate in
      `interp.IlByFunctorId` and patches the sites to CallIl/ExecuteIl —
      the REST OF THE RUNNING QUERY dispatches directly (previously the
      OnDispatch tax lasted until the next query setup = the whole run for a
      single-goal `--exe`). Clarified scope: the audit's "pays forever" was
      overstated — setup already rewrote; the gap was in-query.*
- [x] **L2** 🔴 synchronous 16MB thread-create + Join per compile. *Fixed in two
      parts: (1) ALL compiles (promotion, PGO phase-2, bundle) now run on ONE
      persistent process-wide large-stack worker (`IlCompileWorker`; re-entrant
      submits run inline) — the per-compile thread create/commit/Join is gone
      in every mode; (2) opt-in `BackgroundCompilation` queues the compile and
      keeps the predicate Tier-0 until the delegate drains in at a later
      dispatch (never stalls the query thread), with a per-fid mutation stamp
      so a dynamic snapshot mutated while in flight is DISCARDED at drain
      (logical-update-view safe), and `WaitForPendingPromotions` as the
      barrier. Default stays synchronous — 79 IsPromoted assertions across the
      suite rely on deterministic promotion timing; flipping the default is a
      recorded follow-up (needs that test churn scheduled).*
      *DEFAULT FLIPPED (2026-07-05): `BackgroundCompilation = true` — realizes
      the day-one CLAUDE.md contract ("promotion happens in a background
      thread"). The feared 86-assertion churn collapsed to a ONE-LINE settle:
      `IsPromoted(fid)` now waits out an in-flight compile of that functor
      (it is a diagnostic API — answering deterministically IS its contract),
      which made ~all suite assertions hold unchanged. Only 6 tests needed
      touching, each pinning a genuinely SYNC-specific contract: 3 PGO
      phase-mechanics tests (samples only accumulate in instrumented IL —
      count-deterministic only under sync), the store-level at-threshold test
      (crossing call returns the delegate — sync semantics; a background-mode
      sibling test added), the 256-byte size-gate test (now pins the
      background cap too), and L3's sync-vs-background cap comparison (pins
      sync explicitly). Full five-project gate green.*
- [x] **L5** 🟡 `EvictionChurnLimit=3` permanent Tier-0 banishment. *Fixed: the
      churn pin no longer goes through the permanent `_unpromotable` set — it
      re-arms after `ChurnRearmCalls` (default 4096) mutation-free invocations
      (eviction count resets to limit−1, so one more promote→evict cycle
      re-pins quickly). Any mutation resets the streak. The audit's "bulk
      assertz of 3 clauses = 3 evictions" claim was REFUTED: EvictDelegate
      early-returns when no delegate is present, so a pure bulk load counts
      ONE eviction; only real promote→evict cycles count. 2 tests (pin
      preserved + re-arm on read-hot).*
- [x] **L3** 🔴 16KB bytecode cap (Sigil O(n²)) — big fact tables permanently
      Tier-0. *Fixed (L2-leveraged): under `BackgroundCompilation` the cap
      relaxes to `MaxIlPromotionBytecodeBytesBackground` (64 KB) — a long Sigil
      emit off the query thread is latency, not a stall, so large Arity fact
      tables earn IL in background mode. The 16 KB sync cap stays (the
      synchronous promoting call would stall ~5 s at 27 KB). The TRUE fix (a
      linear-validation emitter / vendored Sigil with patched
      InsertInstruction) remains the recorded follow-up. Test: a ~24 KB
      1200-fact predicate — excluded in sync mode, promoted + correct in
      background.*
      *TRUE-FIX CLOSED AS UNNECESSARY (2026-07-05) — the O(N²) premise was
      RE-MEASURED and REFUTED against the current emitter: the chunk-363 O(1)
      jump tables + chunk-216 model-based indexed dispatch removed the long
      compare/branch chains that triggered Sigil's quadratic validation.
      Measured curve on fact tables (the only >64 KB corpus shapes): 6 KB →
      0.2 s, 96 KB → 1.0 s, 192 KB → 1.6 s, 384 KB → 3.6 s, 768 KB → 5.3 s —
      LINEAR. Background cap default raised 64 KB → 256 KB: the corpus's
      largest predicate (pty_name_l/3, 101.6 KB) now promotes out of the box
      (with L2's background default) at ~1 s of one-time worker latency.
      Sync cap 16 KB kept (explicit-sync callers opted into bounded stalls).
      Test: ~100 KB 4200-fact table promotes on a default engine.*
- [-] **L6** 🟡 `RegionMemberOk` rejects some multi-clause members — **REJECTED
      with corpus evidence** (2026-07-02 harness: SHUMWAY_DIAG build, the real
      IL compiler run over EVERY predicate of the Arity corpora
      `C:\temp\test` (10 244 preds) + `testGen` (18 445) + `testProcDotNet`
      (230)): region-emit = **7 510 regions with 49 040 members**, region-skips
      = **ZERO** across all three corpora. The member gate refuses nothing on
      real Arity code; widening it has no demonstrated payoff.
- [x] **L7** 🟡 `AIntBin/AIntCmp` — compile-time-constant kinds passed as runtime
      args. **Deferred pending a benchmark**: FusedBin/FusedCmp + TryReadInt +
      Deliver are all `AggressiveInlining` and the kinds arrive as CONSTANTS at
      the emitted call site, so RyuJIT inlines and constant-folds the kind
      branches away in the common case — hand-specializing at emit is likely
      redundant. Verify with a disasm/interleaved benchmark before building it.
      *VERIFIED WITH DISASM (2026-07-05, DOTNET_JitDisasm=ShumwayIl_* on the
      promoted delegate of an `M is N-1` self-loop, FullOpts): FusedBin /
      FusedCmp / TryReadInt / Deliver are fully inlined (63 inlinees; only the
      intentional FusedBinSlow/FusedCmpSlow cold calls remain), and the
      constant kinds folded completely — the hot path is deref → untag →
      `dec` (the literal folded into a single decrement) → range check, with
      the kind constants materialized ONLY on the cold path. Hand-specializing
      at emit is REJECTED: it could not beat this code. One real improvement
      found and applied: `Fits60` (the 60-bit range check) lacked
      AggressiveInlining and survived as a CALL in the integer hot loop (the
      JIT's inline budget was exhausted in the big delegate) — attributed,
      re-disasmed, call gone: the Tier-1 integer hot path is now call-free.*
- [ ] **L8** 🟡 chunk-216 indexed dispatch keeps WAM-backed lazy model.
      **Deferred — scope shrank on inspection, magnitude now quantified**: the
      `--strip-wam` half was ALREADY solved in Phase 27 (IlIndexGraph persisted
      via IndexGraphCodec); the residual is the IN-PROCESS promotion path's
      one-time first-dispatch model build. Corpus census (2026-07-02):
      **6 627 indexed-dispatch predicates totalling 4.25 MB** across
      test/testGen/testProcDotNet — a real population, but the build is lazy
      per predicate and one-time. Revisit with startup profiling. NOTE the
      outliers the census surfaced: `control_has_property/3` at 33.6 KB and
      `pty_name_l/3` at **101.6 KB** exceed BOTH promotion caps (16 KB sync /
      64 KB background) — those giant fact tables never promote at all, which
      strengthens L3's recorded true-fix (linear-validation emitter) as the
      real unlock.
- [-] **L10** 🟡 **NEW (corpus evidence, 2026-07-02): multi-arg indexed shapes
      (`switch_on_*_arg`) are NOT IL-describable** — the describers
      (TryDescribeIndexed / IndexedAtom / TryMeElseChain / SwitchedChain)
      reject every predicate whose dispatch uses the chunk-67 multi-arg
      opcodes: **~1 100 predicates in `test` and ~1 500 in `testGen`**.
      **REFUTED (2026-07-03) — the finding was an artifact of a
      DescribeRejection classifier bug.** The typed switch opcodes
      (SwitchOn{Atom,Integer,Structure}[Arg]) were missing from
      IsStructuralDispatchOpcode, so they landed in the "unsupported opcode"
      report for EVERY indexed predicate rejected for ANY reason, masking the
      true cause. Re-census with the real promotion-path setup (float pool
      set, per-fid): of testGen's 3 344 typed-switch predicates, 1 681 are
      IL-ok and ALL 1 663 rejects are `call->unresolved` — the census's own
      consult-failure artifact (callees on files needing the GX interop
      class); the residual 3 were the harness not setting the float pool.
      A synthetic multi-arg cascade (switch_on_term → switch_on_arg ×2 with
      SwitchOnAtomArg/SwitchOnIntegerArg tables) describes, compiles AND runs
      promoted. Fixed the classifier (typed switches are dispatch skeleton →
      excluded from the unsupported report; the true cause surfaces).
      3 regression tests (Phase33L10Tests): shape compiles, runs under
      promotion incl. bucket backtracking, and an unresolved-call reject now
      reports `call->unresolved` — not switch names. (Bonus fact pinned:
      Execute sites never consult the calleeMap — only non-last Calls gate
      on it.)
      *ADR-025 STAGES (d)+(e) CLOSED (2026-07-05): measurement first crashed
      boyer — two more bring-up bugs fixed (get_level_b opcode: the barrier
      must capture CURRENT B, not B0, which pre-ITE calls reset → over-cut a
      preceding generator; and the barrier Y slot folded into the live-Y trim
      analysis like the deep-cut slot — a pre-ITE call's trim let the cond's
      callee overwrite it). VERDICT (deterministic dispatch counts, identical
      CPs/backtracks/cells): Tier-0 inline WINS (boyer −5.8%, ite-rec −13.5%,
      disj −2.9% opcodes); Tier-1 promoted LOSES on boyer (+17% min ×3
      ABBA runs — the region-lowered helper is CP-free for the det commit;
      inline pays PushIlChoicePoint+Cut). DEFAULT STAYS OFF; EnableInlineIte
      documented as the Tier-0/AOT win. Follow-up: IL emit skipping the ITE
      CP for guard-only conds (fail-label→ELSE redirect) → revisit flip.
      Measurement lesson: Profiler.Reset is [Conditional] — the harness must
      ALSO define SHUMWAY_PROFILE or its Reset calls strip silently and
      counters accumulate (first read was a bogus 3×).*
      **FOLLOW-UP (user's correct re-test methodology, 2026-07-04) — the REAL
      coverage gap found and fixed: the per-entry calleeMap in the bundle IL
      build.** Re-measured through the real toolchain (compile → link
      `--with-compiled-il`, one bundle per corpus dir): only **26.1%** of user
      predicates got IL (6.8% among cross-module callers) because
      `CompileEntryToIl` warmed a SINGLE-entry engine — every cross-module
      callee was call->unresolved, so the CALLER was rejected. Fix:
      (1) `BuildWarmEngine` — ONE shared warm engine loads the WHOLE bundle
      (exactly what the runtime LoadBundle does); (2) `PersistedIlBuilder.Build`
      gains `emitOnly` — resolve against the full map, emit only the entry's
      own predicates (each predicate ships IL once, in its defining entry; the
      T7 prelude dedup falls out); (3) region membership scoped to the entry
      via `RegionMemberScopeFids` (ThreadStatic — bundle builds must not scope
      concurrent background promotions), set for BOTH the prune analysis and
      the Build emit (the chunk-401 analysis↔compile consistency rule).
      Also fixed en route: the Phase-17 sentinel PE scan was a sliding byte
      window that false-positived inside `switch` jump tables (two adjacent
      targets 0x320/0x17E lay out as `20 03 00 00 7E` = `ldc.i4 0x7E000003`) —
      now a PROPER CIL instruction walk (operand table from
      System.Reflection.Emit.OpCodes, switch tables skipped), making the
      exactly-once check sound. Full testGen through the real pipeline
      (309 modules, 17 085 user predicates, links in 106 s, runs):
      **76.9% IL coverage; cross-module callers 6.8% → 71.0%; typed-switch
      68.5%**. Cross-module strip-wam regression test (execution forced
      IL→IL across entries). Gate: all five projects green.
- [ ] **L9** ⚪ Minor batch: region self-delegate CSE gate ≥3→≥2;
      MaybeCollectHeap on non-allocating self-tail loops; ground-fact
      unify-with-constant fast path.

## Wave 5 — LTO / startup / size

- [x] **T1** 🔴 prelude baked whole, never reach-pruned. *Fixed (opt-in
      `--prune-prelude` / `LinkConfig.PrunePrelude`): the reachability walk
      records every indicator resolved against the prelude; at bake the set is
      closed over the prelude's own call graph and the prelude recompiles with
      a clause filter (helpers regenerate). An ENGINE-INFRASTRUCTURE set is
      always kept — the chunk-88 runtime meta-call helpers ($call_conj/disj/
      arrow/neg), $catch_run/1 (DATA-ONLY ref from catch/3 — invisible to the
      call graph, the reason it was made :- public), and the variable-goal
      fallbacks catch/3+forall/2+once/1+ignore/1 any QUERY may need; dynamic
      seeds always kept; statically-referenced infrastructure ($tbl_*) comes
      via the walk. Measured: a 2-predicate program bakes 30 of 192 prelude
      predicates (3.4 of 53.5 KB, −94%). Opt-in by design: runtime-constructed
      goals naming unreached prelude predicates raise existence_error —
      :- ensure_linked is the escape hatch (works, tested). 4 tests.*
- [x] **T2** 🔴 no compression anywhere in `.shum`. *Fixed: whole-BODY Brotli
      with a format flag byte after the version (0 raw / 1 brotli; bodies
      < 4 KB stay raw). One shared FinalizeImage/OpenBody pair in BundleFormat
      serves both writers (BundleWriter.ToBytes — also the Librarian +
      save_state path — and the linker's in-line serialiser) and the single
      reader. Whole-body on purpose: the redundancy is CROSS-entry (shared
      atom names / opcode patterns across modules). MEASURED on 119 real
      testGen modules archived: **6.05× smaller** (1.17 vs 7.08 MB);
      FromBytes incl. decompress 34 ms avg (one-time at load; ZERO runtime
      cost — in-memory structures identical). 3 tests (round-trip + runs,
      tiny-stays-raw, save_state snapshot round-trip).*
- [x] **T3** 🔴 persisted-IL `Assembly.Load`+patch+delegates per engine — needs a
      process-wide cache keyed like `_loadedNativeLibraries`.
      *DONE. `GetOrLoadPersistedIl` — static table keyed by SHA-256 of
      (CompiledIl + patches + entries); sound because patches resolve by NAME
      through the process-global atom/functor tables and the output (delegates,
      fids, resume markers) is engine-agnostic. On hit the engine only replays
      per-engine registrations (RegisterBoundDelegate, index graphs, region
      aliases). MEASURED (testGen `aggregat`, 219 KB IL): load #1 142 ms,
      loads #2+ **0.5 ms** (~280×), 1 real Assembly.Load across 11 engines —
      the EnginePool scenario. Test: T3 in Phase33Wave5Tests (strip-wam bundle
      so execution can't hide behind a bytecode fallback).*
- [x] **T4** 🔴 WAM link (addresses, switch tables, `Call→CallBuiltin` rewrite)
      recomputed per query — bake into source-stripped bundles.
      *DONE (re-scoped on evidence). The per-QUERY claim was stale — ADR-015's
      persistent code space + chunk 430's merged-map caches already killed it
      (only the tiny query overlay links per query). What remained was the
      per-ENGINE relink: every pool engine's first query re-linked the full
      static program. Now shared process-wide: `GetOrLinkStatic`, keyed by
      SHA-256 over (loadOffset, per-pred fid + post-remap bytecode + switch
      tables + call sites) — literal-pool divergence changes the bytes, so a
      differently-populated engine MISSES, never wrongly hits. Capacity 64,
      wholesale clear on overflow. Deterministic interleaved A/B (118-module
      testGen archive, 1.15 MB, ~18k preds): first query miss 115.6 ms /
      22.4 MB alloc vs hit 101.8 ms / 14.7 MB — the link was ~14 ms + 7.7 MB
      GC pressure per engine. Test: T4 in Phase33Wave5Tests (per-engine
      `LastStaticLinkWasSharedHit` flag, parallel-safe; consult-invalidation
      covered). CROSS-PROCESS bake into the bundle: REJECTED — atom/functor/
      literal ids are process-specific, so a baked linked image would need a
      full remap pass ≈ the link it replaces; poor ROI for the format work.
      Deferred remainder (reviewable): warm `FromBundle` still ~100-150 ms on
      the 118-module archive — per-entry decode + literal remap is per-engine
      because remapped ids point into the ENGINE's literal pools; sharing
      needs deterministic/global literal ids first. NEW finding for a later
      round: with all link caches hot, first query still ~100 ms on 18k preds
      (not the link) — profile SetupQueryFromTerm's remaining per-first-query
      work (cacheable-functor set, module rewrite, validate, IL warm).*
- [x] **L4** 🟡 baked prelude ships `compiledIl: null` — prelude runs Tier-0
      until per-predicate re-promotion.
      *RESOLVED BY COMPOSITION. `--with-compiled-il` + `BakePrelude` IL-compiles
      the $prelude entry like any other (BundleWriter ToBytes path), LoadBundle
      binds its delegates (shared process-wide by T3), and the T7 strip-wam
      test PROVES prelude-as-IL end-to-end (msort's WAM stripped → the query
      can only succeed through the prelude entry's IL). The default no-IL link
      staying Tier-0 is the user's choice, not a gap. The old deferral reasons
      (consistency + strip-wam soundness, [[prelude-startup-precompile]]) were
      retired by chunk 402 + the differential suite. NEW follow-up recorded:
      bare `new PrologEngine()` still re-Sigils hot prelude predicates PER
      ENGINE (IlPromotionStore is per-engine; delegates are engine-agnostic) —
      a process-wide runtime-promotion cache keyed by bytecode content would
      close it for engine pools that don't use IL bundles.*
- [-] **T5** 🟡 cross-module unfold = 3 meta-wrapper templates only; no general
      partial deduction; skips findall/call goal args; publics only.
      *REJECTED with corpus census (4 dirs, 585 modules): a widened rule
      (single-clause publics, distinct-var heads, no cut, bodies of direct
      goals — the sound widening; wrapper-module-local callees must stay out,
      they'd mis-resolve in the caller) finds 87 cross-module sites in test /
      219 in testGen / 0 in testProc(DotNet), plus 55/69 more candidates
      needing alpha-renaming machinery. The decisive point: those sites are
      already DIRECT calls — the meta-dispatch elimination that made the
      3-template unfold pay (Blint −90 K dispatches) does not apply; unfolding
      saves one call frame per execution. "Skips findall/call goal args" is by
      design (goal args are DATA — rewriting is observable); "publics only" is
      structural (locals are invisible cross-module). Revisit only if a real
      workload profiles hot on such a wrapper.*
- [x] **T6** 🟡 triple representation per entry (source+WAM+IL) by default;
      ClauseTerms doubles `.shmo` size.
      *DONE (re-scoped on evidence). "Triple" was stale: release .shmo strips
      source already (corpus: 0% source). Real composition over 310 testGen
      modules: bytecode 43.7% + ClauseTerms 37.7% + metadata 18.6%. FILTERING
      ClauseTerms is unsound — it is not just the unfold channel: source-
      stripped entry-point promotion and the unfold's caller recompile both
      rebuild whole modules from it. Fix: the .shmo body now goes through the
      SAME raw-or-Brotli framing as the .shum (flag byte at offset 8, shared
      BundleFormat.FinalizeImage/OpenBody). Corpus: 24.5 → 4.8 MB (5.1×).
      Librarian archives store the (now compressed) images verbatim — extract
      stays byte-for-byte. 2 tests.*
- [x] **T7** 🟡 prelude compiled twice per link; ValidateOrThrow full consult per
      entry; CompileEntryToIl full engine per entry.
      *DONE, three fixes. (1) The prelude ShmoObject is a process constant —
      now compiled ONCE (static Lazy, read-only in the linker); it was 2× per
      library link + 1× per plain link. No-IL 2-module link: 189 → 4 ms.
      (2) ValidateOrThrow skips entries carrying compiled bytecode (their
      ground truth was already compiled + diagnosed; the full re-consult per
      entry only ever helped hand-built source-only bundles, which keep it).
      (3) The big one: CompileEntryToIl included the warm engine's ENTIRE
      static cache in every entry's IL — a multi-module --with-compiled-il
      link Sigil-compiled and serialised the ~180-method prelude ONCE PER
      ENTRY (~107 KB each). With a baked $prelude entry, user entries now
      exclude prelude-owned predicates BY NAME ("$prelude$…" locals incl. the
      E12 fresh-id helpers, bare publics/dynamics from the compiled prelude
      object); calls dispatch by fid to the $prelude entry's delegates — the
      mechanism every cross-module call already uses. 2-module IL link:
      3732 → ~950 ms; user entries 108 KB/181 methods → 3 KB/1 method. The
      per-entry full engine itself stays (correctness isolation; its static
      link is now a T4 shared-cache hit anyway). 2 tests incl. strip-wam
      (execution forced through the deduplicated IL).*
- [x] **T8** ⚪ Minor batch: dead-arg elim / const-prop absent; unfold runs 2×
      per module; over-conservative roots.
      *Unfold-2× FIXED: cross-contribution is now detected with ONE publics-
      only rewrite (own/visiblePublics registries have disjoint domains), not
      a local-baseline pass + merged pass + per-clause diff; the old "full
      changed where local didn't" test also silently MISSED a clause carrying
      both a local and a cross wrapper site — the new detection catches it
      (strictly more unfolding, semantics-preserving). Dead-arg elim /
      const-prop: REJECTED as "minors" — those are real compiler passes, not
      batch items; no corpus evidence of need yet (ADR-021's Class-B lesson:
      quantify before building). Over-conservative roots: REJECTED — public
      wrappers must stay callable by runtime-constructed goals; dropping them
      is the T1 prune's opt-in contract, not a default.*

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
- [x] **B1** 🔴 resume-marker encoding caps. *Fixed: markers are now dense ids into a process-global interned side table of (fid, cursor) pairs (`marker = Base + denseId`) — no functor-id or cursor cap (capacity ≈1.07B distinct pairs); lock-free decode / locked intern, same discipline as the atom/functor tables. Process-global because markers bake into IL delegates shared across engines. `ResumeMarkerCursorStride` survives only as the IL emitters per-predicate cursor-count BUDGET (emit-shape policy). 4 tests incl. round-trip beyond both old caps + growth stability.*
- [ ] **B2** 🟡 MaybeCollectHeap unconditional per marker resume.
- [ ] **B3** ⚪ double closure per promoted predicate; per-query dispatch cache
      rebuild.

Functional interop gaps:
- [ ] **D6** 🟡 `int**`/struct-by-value/array params rejected; allocator without
      setcflt/getargp throws instead of per-node fallback; IL compiler bails on
      reftype args that aren't globals; `$native_run` 32-var ceiling.
