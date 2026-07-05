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
constant-folding assumption with a disasm), W9(a-d), B-series.
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
      A2 (no heap cell). **C3-remainder now done**: the source generator emits
      `RegisterMarshalling.ReadInt64Register` / `ReadInt32Register` for `+`-mode
      `long`/`int` params — a cell-direct read (Int cell → payload, zero alloc;
      Ref → instantiation_error; anything else falls back to `FromTerm<T>` for
      exact semantics). Measured on a 3-arg scalar foreign in a failure-driven
      loop: `padd/3` 120 B/call → **0.00 B/call**. Embedding gate 2702/0/3 green
      under the testhost AV trap.*
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
- [-] **L8** 🟡 chunk-216 indexed dispatch keeps WAM-backed lazy model.
      **Closed — bounded structurally, no action (2026-07-03)**: the
      `--strip-wam` half was ALREADY solved in Phase 27 (IlIndexGraph persisted
      via IndexGraphCodec); the residual in-process cost is the one-time
      first-dispatch `IlIndexGraph.Build` — inspected: a DFS over the SWITCH
      NODES only (not the clause bodies), O(switch nodes + table entries),
      strictly smaller than one linear pass of the predicate's bytecode. It is
      paid lazily per promoted predicate, alongside that predicate's Sigil
      compile, which L3's measured curve puts at ~33 ms/KB — orders of
      magnitude above a memory walk of the same bytes. Even the corpus-wide
      worst case (every one of the census's 6 627 indexed predicates promoted:
      4.25 MB of switch-table walks) is milliseconds total against minutes of
      the compiles that would accompany it. The census outliers
      (`pty_name_l/3` 101.6 KB) were unlocked by L3's cap raise (256 KB) and
      now promote. Startup profiling would measure compile time, not this.
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
      documented as the Tier-0/AOT win.
      *FOLLOW-UP RESOLVED (2026-07-05, commit e76e26a) — the "guard-only CP
      skip" idea was REFUTED by boyer's actual shape (its cond is the
      axiom/2 CALL, not a guard). A 2×2 attribution ({regions}×{inline ITE},
      stable 20k-rep runs) decomposed the regression: (1) LOST BRANCH-TAIL
      LCO — CompileInlineIte compiled every branch goal as non-last (call +
      jump + shared epilogue, O(depth) frames); (2) the ELSE resume
      trampoline (IlIteHelper.Resume + marker → dispatch-loop round trip per
      failed cond vs a chain CP's direct delegate invoke); (3) region
      exclusion (~17% on boyer). Fixed (1)+(2): branch last goals compile as
      LAST goals when the ITE is last (branches self-terminate); the ITE
      try_me_else carries the body-CP arity sentinel
      (OpcodeTable.InlineIteCpArity −1) which the cursor counter, recogniser
      guards and RegionBodyOpcodesOk key on (the old jump-only region gate
      let the new shape crash the region emit); the IL CP pushes the OWN
      delegate + ELSE cursor. Head-to-head standalone: +40% → +13.6%.
      REMAINING before the flip: the ~14% residual (correlated with an
      unattributed +185KB managed alloc/query on boyer) and ITE-in-regions
      (wire the ELSE cursor through the region plan — the machinery exists
      as BuiltinResume cursors).*
      *ITE-IN-REGIONS DONE + residual attributed (2026-07-05/06): the ELSE
      cursor rides the BuiltinResume site kind (CollectBuiltinResumePcs also
      collects sentinel try_me_else pcs → the plan assigns cursors in pc
      order with zero planner changes); the emit's TryMeElse case resolves
      region cursors via CursorBySite and pushes the REGION delegate; the
      member emits now thread emitSelfDelegate; RegionBodyOpcodesOk accepts
      the ITE opcodes. Structural confirmation: boyer's rewrite/2 with
      inline ITE now allocates cells+managed EXACTLY like the helper region
      (was: like a standalone). Along the way the LCO change's EMPTY-BRANCH
      bug was found and fixed (FlattenConjunction elides `true`, so a last
      `-> true` branch emitted nothing and fell into ELSE / off the clause —
      a hang): an empty last branch now closes the clause explicitly.
      Alloc attribution: a targeted microbench shows the inline protocol
      allocates 0 B/op on BOTH paths — the boyer +185KB was another
      region-exclusion artifact, gone with the wiring. The residual is now
      pure Tier-1 code shape: regON inline vs regON helper +12.6% wall on
      boyer (one interleaved run; HEAD baseline same session +17.4%).
      3 new Adr025StageBTests (call-cond all paths / backtrack across
      commit / 200k-deep branch-tail LCO in a region). FLIP still not
      justified for Tier-1; EnableInlineIte is now REGION-SAFE (previously
      it silently cost the whole region). Found pre-existing, now-visible:
      boyer's region allocates 66.8 vs 43.8 MB managed per 20k-rep query vs
      standalone (+53%, intra-run deterministic, exists at HEAD) — a future
      region-alloc item.*
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
- [x] **L9** ⚪ Minor batch — assessed each against the code (measure-before-touch,
      per the I7/I8 precedent); one implemented, two are risk-outweighs-win /
      already-done:
      - *Region self-delegate CSE gate ≥3→≥2* — **DONE, refined by loader kind.**
        The Stage-11 hoist in `EmitRegionInto` is shared by two paths with different
        self-loaders: the persisted path uses `SelfFromArrayField` (3 cheap IL ops,
        pure size play — hoist shrinks only at ≥3, `2·P−4>0`), the runtime-promotion
        path uses `SelfFromHolder` (2 IL ops but each a `ConcurrentDictionary` lookup
        at RUNTIME on the CP-push/backtracking path). The single `≥3` gate was tuned
        for the array-field size break-even and missed a real runtime win for the
        holder path — replacing a per-push dict probe with a hoisted local load, worth
        the +1 IL op at P=2, exactly the call the chunk-426 inline-fact hoist already
        makes for its holder-only pushes. Now gated by `selfDelType`: holder
        (`Func<Engine,int,bool>`) at ≥2, array-field (`PredicateDelegate`) stays at ≥3,
        so the persisted-bundle size discipline is untouched. Behaviour-identical
        (internal codegen); justified structurally rather than by a wall-clock delta
        (a fractional-dict-lookup change at P=2 is below this laptop's thermal noise),
        mirroring how the inline-fact ≥2 gate was justified. Full 5-project gate green
        (Embedding 2762), no dump.
      - *MaybeCollectHeap on non-allocating self-tail loops* — **NOT DONE
        (risk-outweighs-win).** `Engine.MaybeCollectHeap` is the cooperative
        cancellation safe point, documented (chunk 428) as checked at EVERY safe point
        precisely so heap-light loops stay ESC-cancellable (Phase 31 — lazy Y-slots
        made many loops non-allocating, and a watermark-only check left them
        uncancellable). Skipping it on a non-allocating self-tail loop would make that
        loop uncancellable, regressing the Phase-31 ESC feature, for a ~1-op saving —
        it is already an `AggressiveInlining` volatile read + predicted-not-taken
        branch. Left as is.
      - *Ground-fact unify-with-constant fast path* — **ALREADY DONE (chunk 170).**
        `Engine.UnifyRegisterWithCell` (the target of every `get_atom`/`get_integer`/
        `get_constant`/`get_nil` in Tier-0) already fast-paths the constant case: an
        unbound register binds directly to the immediate (trailing only below HB, no
        heap alloc, no general `Unify`); a bound-same-value register early-outs; the
        `UnifyCells` fallback does no materialize for a bytecode literal. A ground
        fact's constant args take the fast path today.

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

## OPEN — intermittent native AV (needs a minidump session)

- [ ] **0xC0000005 in `shumway_native_calli`** during
      `EndToEnd_NativeDll_OutScalarPointers`, ONLY under the full parallel
      Embedding suite (~50% of full runs; seen 3× on 2026-07-03/05). NOT
      reproducible sequentially (60 000 compiled out-scalar calls clean,
      fresh engine per round) nor with all Native* classes running
      concurrently (4/4 green). Predates the ITE work; first observed after
      D1 but D1's arena never crosses a chunk boundary in this test
      (16 B/call, mark-restored), so the arena-boundary theory is weak.
      **Investigation (2026-07-05):** 6 more dump-armed full-suite runs on a
      warm build all passed — BOTH real crashes were the FIRST run after a
      rebuild (max overlap of cold compiles). Sequence files show identical
      within-class order and disjoint cross-class neighbors → timing, not a
      specific test interaction. **Found and fixed a real concurrent-compile
      race with exactly this profile**: `_emitOwnerFid` was the one piece of
      mutable IL-emit state left plain-static while compiles run
      concurrently on the shared IlCompileWorker AND engine threads (bundle
      / persisted builds) — a clobber bakes another predicate's fid into a
      delegate's resume markers, so a post-backtrack resume re-enters the
      WRONG delegate at an arbitrary cursor. Now `[ThreadStatic]` like the
      rest (_persistPatches/_ilFloatPool/_nativeInline). Causality for THIS
      AV is plausible-not-proven (the PInvoke test engine itself has
      Threshold 0; the path would be indirect via process-wide persisted-IL
      caches). Crash capture (DOTNET_DbgEnableMiniDump, full type) is now
      baked into the standard Embedding gate script — any recurrence
      auto-produces an analyzable dump. GCStress=3 probe on the two P/Invoke
      tests was too slow to complete (inconclusive, killed).
      **Post-fix validation (2026-07-05): 4 consecutive rebuild-then-full-
      suite runs (the exact trigger condition, 3 with a real timestamp-touch
      rebuild) all clean, dumps armed, zero dumps.** Status: WATCH — the
      race fix (commit e88097e) is the probable cause; the permanent dump
      capture decides it: a recurrence produces an analyzable dump, a
      sustained quiet stretch closes the item.
      **RECURRED post-race-fix (2026-07-06)** — same test, again the first
      run after a rebuild → the _emitOwnerFid race was NOT the cause. AND
      the dump did not fire: createdump handles a MANAGED AV (validated
      end-to-end with a synthetic Marshal.WriteInt32 crash under the same
      wrapper — dump written) but not this fatal-native-frame path
      (0xC0000005 inside the calli target). Gate scripts now also set
      DOTNET_CreateDumpDiagnostics/VerboseDiagnostics/EnableCrashReport for
      the next occurrence; WER LocalDumps needs admin (denied). Also seen
      once under full-suite load: ChurnGuard_PinsToTier0AfterRepeatedMutation
      failed (4/4 green alone — background-compile interleaving flake).
      **Standing trap (2026-07-06):** procdump64 (downloaded to
      %TEMP%\claude\procdump) is now armed by the standard Embedding gate
      script — `-ma -e -w testhost.exe` waits for the testhost and writes a
      full dump to %TEMP%\claude\avdumps on ANY unhandled SEH (covers the
      fatal-native path createdump misses; createdump itself validated
      end-to-end for managed AVs). dotnet-dump installed globally for
      analysis. 4 more armed trigger rounds (touch-rebuild → full suite)
      all clean — 0/8 recurrences since the _emitOwnerFid fix; the trap
      decides the item without further dedicated hunting.

## IL round 2 (2026-07-05, user-directed) — profile-driven Tier-1 pass

A dedicated optimization round over the generated IL + the Tier-0↔Tier-1
transition machinery, driven by dotnet-trace sampling of a Tier-1-heavy mixed
vanroy probe (Threshold=1, promotions settled and verified, regions default).
Method: profile → attack the top attributable frame → A/B (frozen SHA-distinct
publishes, interleaved ABBA, min-of-7 in-process) → re-profile.

- [x] **R2-1 — ArithEvalStack fast lanes actually inline** (commit 3ea5414).
      `PushIntLane` was a REAL CALL from the Tier-1 delegates (~3% exclusive,
      ~10% of engine-thread time — one call per integer operand the compiled
      `a_eval_*` RPN pushes): the L7 lesson one leaf deeper — PushReg/PushY/Bin
      are AggressiveInlining but the helper they all delegate to was not. Also:
      `PopCell` carried a try/catch (= uninlineable outright) and runs per
      compiled `is/2`; IsReg/IsPerm/SetReg/SetPerm/Cmp un-annotated. All fixed
      with the chunk-354/355 fast-lane/cold-slow-half pattern. **Measured:
      crypt ×300 A 496–524 ms vs B 378–399 ms — B's worst beats A's best,
      −24%** on the arithmetic-RPN-bound workload; queens/qsort neutral as
      expected structurally.
- [x] **R2-2 — self-delegate holder dict → slot array** (commit 0df91a4).
      Post-R2-1 profile showed `ConcurrentDictionary<int,Func>.TryGetValue`
      called DIRECTLY from region IL (~3% of engine time): SelfFromHolder
      emitted `call IndexedDelegateHolder.Get` — a hash+bucket probe per
      multi-clause/indexed region invocation (chunk 232 had replaced a
      contended lock with the CD; this removes the probe). Keys are sequential
      ints under RegistrationLock → the store is now a growable slot ARRAY and
      the emit is `ldsfld/ldc/ldelem.ref` (the SelfFromArrayField shape).
      Grow-copies-then-Volatile.Write publication; safe because a delegate
      only escapes through fenced channels after Register. **Evidence:
      deterministic profile delta — the TryGetValue frame VANISHED from the
      post-change top-20**; wall-clock directionally positive but within
      thermal noise, claim rests on the structural argument (L9/ADR-021
      discipline).

Round observations (recorded, not actioned): the remaining per-hop transition
cost is minimal (marker decode + array probe + delegate invoke; nothing
dict/lock-shaped left on the dispatch path per the post-change profile).
`AreStructurallyEqual` ~3% is boyer's real ==/\== work (I9 iterative walk).
`SharedArrayPool<Char>.Trim` lock contention on the finalizer thread is an
artifact of the R1-benign Cell[] Gen2 churn, not engine-thread time.
`SetupQueryFromTerm` 2.45% inclusive per-query setup = the T4-residual
already recorded. PushChoicePoint/RestoreCommon ~3% = the ADR-026-analyzed
frame machinery, known tight.

## Later rounds (not yet waved)

Region runtime:
- [-] **R1** 🟡 **CLOSED — attributed benign (2026-07-06).** Type-level
      AllocationTick tracing (dotnet-trace gc-verbose + a TraceEvent
      histogram, scratchpad allocrpt) on real boyer under BOTH configs:
      99.9% of all managed allocation in both is `Shumway.Core.Cell[]` —
      the engine's own WAM heap/stack arrays re-allocating via
      growth-doubling (~16 GB sampled in 35 s each config; every other
      type is megabytes). The boyer "+53%" and the microbench "24 B/op
      standalone" were BOTH this: transient array-growth amortization
      whose peak pattern differs slightly per config (one doubling more or
      fewer per query), not per-op dispatch objects — GC.GetAllocatedBytes
      cannot distinguish. No lever: wall-clock already favors regions and
      array growth is watermark-governed and transient. (Residual noted:
      Dictionary<int,List<Clause>> bucket churn is per-QUERY setup, known.)
      Original text follows for the measurement trail.
- [x] ~~**R1** 🟡~~ boyer's REGION allocates +53% managed vs the same predicates
      standalone (66.8 vs 43.8 MB per 20k-rep query — intra-run
      deterministic, measured identically pre/post the ITE wiring, i.e.
      PRE-EXISTING). Wall-clock still favors regions: headroom, not a
      regression.
      **First attribution pass (2026-07-06) INVERTED the expectation**: in
      every isolated shape the REGION path allocates 0 B/op and the
      STANDALONE path pays 24–96 B/op (in-query loops, baseline-subtracted,
      min-of-5): det-chain 0 vs 24, chain-bt 0 vs 24, indexed 0 vs 24,
      builtin-resume 0 vs 24, univ 0 vs 24, mini-boyer 0 vs 96; leaf
      (no local member) 0 vs 0 and listrec (self-recursive 2-clause member)
      0 vs 0. So (a) boyer's +53% under regions is NOT a generic region
      tax — it does not reproduce in any isolated shape and needs
      TYPE-LEVEL allocation tracing (dotnet-trace AllocationTick /
      PerfView) on real boyer to attribute; and (b) NEW: the standalone
      path allocates ~24 B per local-member call in most shapes (0 under
      regions; absent for leaf/self-recursive shapes) — a small distinct
      hunt, likely in the threaded-resume or dispatch path. dotnet-trace
      is installed globally; the iteattr scratchpad harness has the
      per-shape loop technique ready.

Interpreter core:
- [x] **I1** 🔴 PC store+reload / redundant loop-top checks per dispatched opcode.
      **DONE — via a lower-risk framing than "thread pc off the field".** Rather
      than eliminate the store+reload (which the CPU already store-forwards) and
      risk a stale `_engine.P`, the win was the three PER-ITERATION loop-top
      checks — the `ProgramGeneration` read + code-view reload, the resume-marker
      test, and the code-bounds test — that ran before EVERY opcode. A
      straight-line opcode (a fixed in-clause `pc` advance) provably cannot
      change the program, land on a resume marker, or exceed the bounds, so it
      now sets an `inClause` flag that makes the next iteration skip all three.
      Default-false = fail-safe: every control transfer (Call/Execute/Proceed,
      backtrack, IL, marker, and crucially CallBuiltin/ExecuteBuiltin which use
      `AdvancePc` — NOT `SetPc(pc+N)` — because a builtin may `assertz` and bump
      the generation) leaves the flag false, so it re-checks; and `_engine.P` is
      still written by every op, so it is never stale. The 45 straight-line
      `SetPc(pc + N)` sites (unification / arithmetic / cut / structure-build —
      verified none run a builtin) opt in. Measured on nrev(300) (list-unify
      heavy, min-of-20, ABBA back-to-back): I1 ~1113–1245 ms vs baseline
      ~1328–1348 ms — I1's WORST run beats baseline's BEST, **~8–16% faster** on
      dispatch-bound code. 5-project gate green (the lone Embedding failure,
      `Chunk39.Engine_UnpromotablePredicate_StaysOnTier0Forever`, is a
      PRE-EXISTING parallel-suite flaky — it fails identically on baseline with
      I1 stashed; recorded as I10).
- [x] **I10** ⚪ `Chunk39Tests.Engine_UnpromotablePredicate_StaysOnTier0Forever`
      was flaky under class/suite parallelism — the promotion churn/re-arm
      (ADR-023 / L5) assertion is timing-dependent on the shared background
      `IlCompileWorker`. **DONE.** Root cause, common to a small FAMILY of
      promotion-side-effect tests: with the default `BackgroundCompilation = true`
      and `Threshold = 1`, a threshold-crossing call QUEUES the IL compile on the
      shared worker and stays Tier-0; the side effect the test checks lands only
      when that compile later drains. For Chunk39 the eviction only counts toward
      the churn limit when a delegate is actually present at `assertz` time, so a
      worker backed up under suite parallelism means the compile hasn't installed
      before the mutation → evict is a no-op → the churn pin is never reached →
      the final calls re-promote → `IsPromoted` true → fail. The churn-pin logic
      itself lives in `RecordInvocation` ahead of the background/sync branch and
      is mode-independent, so the fix keeps the DEFAULT background path and just
      adds the completion barrier the engine already exposes for exactly this:
      `engine.IlPromotion.WaitForPendingPromotions()` after each round's warm
      queries (before the mutation) drains the in-flight compile and installs the
      delegate, so every promote→evict cycle is real and the pin is reached
      deterministically regardless of load — no product behaviour is disabled or
      hidden; the async promotion still runs, it is only synchronised. (An earlier
      cut used `BackgroundCompilation = false`; switched to the barrier so the test
      still exercises the real default mode.) A **5×-repeat run under parallel load
      surfaced a second,
      independent instance of the same family**: `NativeBundleTests.Tier1Inline_
      ArithmeticWithLocal_Runs` (and its latent sibling `Tier1Inline_Cmp_Runs`),
      which assert the static `IlPredicateCompiler.NativeBlocksInlined` counter
      incremented — the inline happens during the queued IL compile, so under load
      the queries succeed on Tier-0 `$native_run` dispatch but the counter never
      bumps → `> before` fails. Fixed with the same `WaitForPendingPromotions()`
      barrier before the count assertion (not by disabling background mode). (The
      rest of the family was already hardened: `Chunk76Tests` PGO uses sync mode in
      its shared `NewColorEngine`; `NativeReftypeTests` already gates its inline-count
      assertion behind `WaitForPendingPromotions()` — the same barrier reused here.)
      Test-only change, no product code — the engine's query results were correct
      throughout; only the timing-dependent optimisation side-effect assertions were
      unsynchronised. Verified: Chunk39 12/12 solo; full Embedding suite **green under
      repeated parallel-load runs** (2762 passed), no dump with the AV trap armed —
      where the pre-fix suite failed in roughly half of runs.
- [x] **I2** 🔴 findall/bagof/setof/copy_term round-trip through managed AST.
      **Part (a) — `copy_term/2` — DONE.** New `HeapTermCopy` does a direct
      heap→heap copy (no intermediate AST tree): Ref/AttVar→fresh plain var
      (variable sharing via a var map), Str/Lis→fresh slab (structure sharing +
      cycles preserved and made to terminate by register-before-recurse), the
      list spine walked iteratively (chunk-111 no-overflow), Atom/Int/Foreign
      verbatim, and the side-table leaves (Float 2-cell / BigInt / Pstr)
      delegated one-node-at-a-time to the proven AST path (fresh side-table
      entry, identical semantics). Measured: ground `f/5`+list **1296→584
      B/op (−55%)**, shared-var `p/5` **1240→712 B/op (−43%)**, both faster.
      Then the two identity maps were **pooled on the engine** (clear-on-use,
      depth-guarded — the chunk-432 pattern): **1296→72 B/op (−94%)** ground and
      **1240→72 B/op (−94%)** shared-var. 11 dedicated tests (ground / fresh+
      shared vars / shared tail / float+bigint / string / 100k list no-overflow
      / nested / cyclic-terminates / copy independence); 5-project gate green
      under the AV trap (Embedding 2713) — twice (base + pooling). *(A finding
      along the way: `==/2` — `AreStructurallyEqual` — has no cycle detection
      and overflows on a cyclic term; pre-existing, unrelated, noted for a
      future item.)* Remaining ~72 B is the `CopyLis` spine list — eliminable
      with incremental cons linking (no scratch list) as a further refinement.
      **Part (b) — findall record→collect — DONE (I2b).** `findall/3,4` records
      each solution as a backtrack-safe `Cell[]` cell image (`FindallSnapshot`,
      a relative heap image mirroring `HeapTermCopy`'s layout: fresh vars,
      DAG/cycle sharing, iterative list spine) instead of a managed AST, then
      re-emits it at collect by a block copy with one additive shift. A
      value-leaf template (float / bigint / string / pstr / foreign) can't be
      imaged flatly, so it falls back to the AST path per solution. The
      backtrack-safe destination question resolved by the cell image itself (a
      detached `Cell[]`, GC-owned like the AST it replaces, but with NO per-node
      managed object — the three scratch collections are pooled clear-on-use on
      the engine, so only the per-solution `ToArray` allocates). `bagof/setof`
      stay on the AST path (they inspect witnesses for grouping); findall got
      its own `$findall_record_s` record builtin so the shared `$findall_record`
      keeps producing Terms for them. Measured on `findall(f(X,g(X),h(X,X)),
      between(1,N,X), L)` (min-of-20 ABBA back-to-back): WALL 9.75/11.45 ms vs
      baseline 12.93/13.87 ms (I2b worst beats baseline best) — **~15-25%
      faster**; marginal alloc **440→144 B/solution (−67%)**. 14 Phase33I2b
      tests (int/compound/list/nested-compound templates, fresh-var-per-solution,
      shared partial-list tail, value-leaf float/bigint fallback, mixed
      fast+fallback in one frame, nested findall, findall/4, 3000-deep list,
      bagof/setof unaffected). Full 5-project gate green (Embedding 2739), no
      dump under the AV trap.
- [-] **I3** 🟡 retract materializes each shape-matching candidate per trial.
      **Largely mitigated (chunk 421 + 431)**: `FindRetractMatch` runs
      `DefiniteMismatch` — a depth-4 structural pre-filter (distinct atoms /
      ints / principal functors / atomic-vs-compound, incl. first-argument
      discrimination) that SKIPS materialize-and-unify for any candidate that
      provably cannot unify; the snapshot copy is pooled + shared across the
      enumeration (alloc −70%). Residual: candidates that share the top functor
      to depth 4 still materialize per trial. Closing that needs a real
      first-argument index over the candidate list — a feature of uncertain ROI
      (retract is not a steady-state hot path once alloc is cut). Deferred as a
      feature, not a fix.
- [-] **I4** 🟡 dynamics below the JIT-index threshold walk linear chains with
      tombstone dispatch. **Measured — no change warranted.** The threshold
      (`JitIndexing.Threshold`, default 16) gates ONLY the first `Threshold`
      calls; after that the predicate is indexed. Benchmark (30-clause dynamic
      `d/2`, bound-key dispatch, 200k-call loop, min-of-5): indexed **0.38
      µs/call** vs never-indexed linear scan **2.66 µs/call** — the index is
      worth **7×**, and it already exists + works. But the THRESHOLD's own
      impact is bounded by `Threshold × (linear − indexed)` = a **one-time
      ~36 µs** warmup for this shape — fully amortized for any hot predicate,
      trivially bounded for a cold one. Lowering it would shave a few µs of
      warmup for predicates called 5–15× with many clauses, at the cost of
      building an index for predicates called a handful of times then abandoned
      (wasted switch-table build). 16 is a sound balance; tuning it is
      noise-level. Structural + measured closure.
- [x] **I5** 🟡 `Cut`→`CompactTrails` O(n) with no early-out when tops equal.
      *Fixed: an early `return` when `parentBindingTop == _bindingTrailTop &&
      parentExtraTop == _extraTrailTop`. When nothing was trailed since the
      parent CP both compaction walks are empty AND — since the trail only
      grows between CPs — no catch-frame snapshot can sit above the unchanged
      top, so the whole body (including the O(_catchFrames) snapshot-clip loop
      that ran on EVERY cut) is a proven no-op. Deterministic cuts under deep
      catch nesting no longer pay O(catch frames) for nothing.*
- [x] **I6** 🟡 CP saves 10 control words incl. ViewGen for static predicates.
      **CLOSED — REJECTED via ADR-026 (measured ceiling below noise).** The
      focused pass ran (2026-07-05): full soundness analysis + design blueprint
      + ceiling measurement, recorded in
      [ADR-026](architecture/adr/026-variable-width-choice-points.md).
      Key findings: (a) `CurrentViewGen` has exactly ONE reader — `CheckVisible`
      (plain handler + the `TryInlineCheckVisible` peel); `ViewGenOf` has zero
      callers — so the slot is semantically needed only on CPs pushed inside
      dynamic-chain dispatch (between `enter_dynamic` and the body `execute`);
      every other CP restores it redundantly. (b) A sound two-width design
      exists (width bit in the arity word + an `_inDynamicChain` engine flag
      set by enter_dynamic / resynced on every CP restore / cleared on call
      dispatch — opcode-peek and new-opcode discriminators rejected: adjacency
      is not an invariant post-asserta-demotion, and a `TryMeElseDyn` family
      would double every 155a-g chain-walker match site). (c) The MEASURED
      ceiling: unsound narrow-frame hack, static-only CP-heavy workloads,
      SHA-verified frozen A/B builds, interleaved A-B-B-A-A-B-B-A — queens
      identical (39 vs 39 ms min), crypt overlapping ranges, and the purest
      CP-churn synthetic (member-fail, ~6M push+restore pairs) had the WRONG
      SIGN (baseline 1241 vs narrow 1286 ms min): noise. Arithmetic agrees:
      12M saved memory ops ≈ 5-12 ms ≈ 0.3-1% on the most favourable synthetic
      possible, ~0 on real code (regions/neck-cut already elide most CPs —
      the ADR-021 Class-B lesson verbatim). Against: ~13 raw arity-word
      reader mask sites, each a silent stack-corruption hazard (chunk-404
      class), plus a hot-path flag store. Shipped from the ADR: the stale
      Engine.cs CP-layout comment fixed (it omitted B0 and mis-stated the
      ViewGen semantics). Revisit triggers recorded in the ADR.
- [-] **I7** 🟡 ProgramGeneration property read per dispatch tick.
      **Deferred pending a benchmark** (measure-before-touch, per the C1/L7/B2
      precedent): the per-tick check is already a single field read + compare +
      predicted-not-taken branch (`gen != cachedGen`), and it is the ONLY thing
      that lets the loop notice a mid-dispatch `assert`/`retract` re-linking the
      code view. Moving it off the hot path means scattering the re-check across
      every program-mutating opcode — a correctness hazard (miss one → stale
      code view → wrong answer / crash) for a ~2-instruction saving with no
      measured cost. Not touching hot dispatch on speculation.
- [-] **I8** ⚪ Minor batch. **Assessed — each is already-done, marginal, or
      risk-outweighs-win; none slammed in mid-AV-hunt:**
      - *Overflow branch per fetch* — already peeled: chunk 170 hoists the
        single-buffer `code.Primary` into `codeArr`, so the steady-state fetch
        is a direct `byte[]` index with no per-tick Split branch.
      - *dbg_info out-of-band* — already effective: release `compile_mode`
        (ADR-018 / Phase 25) omits the per-clause `meta dbg_info` opcodes.
      - *HasPendingWakeups cached bool* — already a `List.Count` field read;
        a cached bool saves ~1 op but adds a missed-wakeup invariant hazard
        (attvar/CLP correctness). Not worth it.
      - *CallBuiltin name/arity stamping deferral* — the eager
        `CurrentBuiltinName`/`Arity` writes feed `IsoError.X(engine)` thrown
        INSIDE a builtin impl (before the catch), so they can't move to the
        cold catch without mis-attributing direct-throw errors. ~2 field
        writes; regression risk in error tests. Left as is.
      - *FlushPendingWakeupsSlow pooled scratch* — the `savedRegs` array is
        genuinely per-wakeup garbage, but the method is RE-ENTRANT (a wakeup
        goal runs via MetaCallInEngine → dispatch → another flush at its
        boundaries); a single pooled buffer would clobber the outer save.
        Safe pooling needs a depth-indexed free-list in the delicate attr
        path — a focused change with its own attr/CLP stress, not a minor.
      - *_unifyPointer local threading* — micro; deferred with the rest.
- [x] **I9** 🟡 `==/2` / `\==/2` stack-overflowed on a cyclic (rational) term
      — an UNCATCHABLE `StackOverflowException` that killed the process, not a
      Prolog error. Found via the AV trap during I2 (a test compared a cyclic
      copy with `==`; the dump was 2265 frames of `AreStructurallyEqual`).
      Cyclic terms arise from occurs-check-off `=/2` (`X = f(X)`), so it is
      reachable from user code. **DONE.** `AreStructurallyEqual` no longer
      recurses: the former mutually-recursive `AreStrStructurallyEqual` /
      `AreLisStructurallyEqual` descent is now a single iterative walk
      (`StructuralCompareIterative`) over an explicit pooled work-stack, so C#
      stack use is O(1) at any term depth (fixes deep-but-acyclic terms too,
      which also overflowed). Cycle handling is **lazy**: leaves compare inline
      and a list-of-primitives never touches the work-stack, so for the first
      2^16 descent steps there is zero bookkeeping — the hot path runs at
      spine-loop speed. Only a term that exceeds that budget (a cyclic/rational
      term does immediately; no realistic acyclic term does) engages a
      visited-pair set, after which re-encountering a pair in progress means
      "equal so far" — the greatest-fixpoint (co-inductive) reading SWI also
      gives — and the walk terminates. Measured on `L == L` over a 2000-element
      list (2000×, min-of-20 ABBA): I9 51.6 ms vs baseline 51.2 / 59.5 ms — at
      parity (a naive always-on visited set was 12× slower; the lazy design
      recovers it). 12 Phase33I9 tests (distinct cyclic terms equal / unequal /
      self / deeper-unrolling / cyclic lists / long acyclic no-false-cycle /
      value-leaf args); no dump under the trap. Full 5-project gate green.
- [x] **I11** 🟡 `StandardOrderComparator.Compare` (`compare/3`, `@<` `@>`
      `@=<` `@>=`, and thus `sort/2` `msort/2` `keysort/2` `predsort/3`) shared
      I9's defect: `CompareCompounds` recursed per arg AND per list element with
      no guard, so it stack-overflowed the host uncatchably on **both** a cyclic
      (rational) term and a **long acyclic list** (no spine loop). **DONE — via
      a hybrid that keeps the sort hot path fast.** A first cut made the whole
      comparison iterative (explicit work-stack) — correct and crash-safe, but
      it spun up the stack for *every* compound comparison and regressed sort of
      small compounds. The shipped design threads a C#-recursion `depth`: shallow
      terms (the hot path — pairs, small compounds) stay on the fast recursive
      descent and pay only a depth check, and only past `RecursionLimit` (512,
      well below the ~6800-frame overflow point) does a sub-term escalate to the
      iterative walk, which handles arbitrary depth (O(1) C# stack) and engages
      a lazy visited-pair set past 2^16 steps so a cycle terminates
      co-inductively (consistent with `==`; the standard order of two infinite
      terms is taken as their bisimulation). Measured comparator-isolated (build
      once, sort 500×, min-of-15 ABBA): keysort 624-662 vs baseline 585-635 ms,
      msort-of-compounds 932-950 vs baseline 854-995 ms — **at parity** (fully
      overlapping ranges). 13 Phase33I11 tests (type order, compare/3 result
      atom, arity/name/arg ordering, leftmost-difference, sort/msort/keysort,
      lists-of-lists, 40000-element list compare no-overflow, cyclic-term /
      cyclic-list compares terminate). Full 5-project gate green (Embedding
      2752), no dump under the AV trap.
- [x] **I12** 🟡 Compiling a clause with a **deeply-nested argument** (a long
      list) stack-overflowed the host uncatchably — `assertz/1` of a large ground
      list crashed at compile time. **DONE — for the list case, comprehensively.**
      The exposure was NOT one method but a **sweep of per-term-node recursions**
      across `ClauseCompiler`, each fixed to an explicit-stack iterative walk
      (found one at a time by binary-searching the crash depth and reading the
      captured stack): `ClassifyPermanents` / `ClassifyPermanentsInlineTransparent`
      (permanent classification — reverse-push preserves the first-occurrence
      order that drives Y-slot assignment), `CountVarOccurrences`,
      `CollectVarNames` (head-var collection), `CompileUnifyArgInline` (the ADR-019
      last-arg inline build — the list-spine loop), the argument scheduler's
      `CollectForcedSaves` and `UpdateMaxLiveYIdxFromTerm`, and the ADR-020
      reserve-build eligibility checks `HasNonLastNestedCompound` /
      `AllNestedCompoundsInlinable`; plus `StructuralKey` (CSE keying) bounded to
      depth 64 (CSE of a huge sub-term is pointless and was building an O(size)
      key string). Verified end-to-end on the default stack: `assertz` of a
      **100 000**-element list as a fact head AND as a body-goal argument both
      compile and dispatch (readback length correct). 3 Phase33I12 tests
      (deep-list fact / deep-list rule body / shallow regression). Full 5-project
      gate green (Embedding 2755), no dump. **A measurement lesson:** the hunt was
      derailed for many iterations by a stale `iteattr.exe` — a clean build moved
      the output to `bin/x64/Release/` but the run script kept invoking the old
      `bin/Release/` binary, so fixes appeared to have no effect; always confirm
      the artifact path after a config change.
- [x] **I13** 🟡 `TermReader` (Embedding) recursed one C# frame per compound
      level, so materialising a deeply-nested **non-list** heap term — `assertz`
      of a 100 000-deep `s(s(…))` / a long left-associative `a+b+c+…` — overflowed
      the host uncatchably at materialize time (during `assertz`, or when a query's
      deep binding was read back into a .NET `Term`). Chunk 111 had made only the
      LIST *spine* iterative. **DONE.** The three mutually-recursive helpers
      (`Materialize` / `MaterializeCompoundAt` / `MaterializeLis`) are replaced by a
      single **explicit-stack post-order tree walk**: an Expand frame derefs a heap
      index and, for a compound/cons, pushes an assemble frame plus one Expand per
      child (in reverse, so the leftmost child is built first and lands deepest on
      the result stack); the assemble frame pops its children and constructs the
      `CompoundTerm`. C# stack depth is now O(1) for any shape or depth. Cycle
      detection is meaning-preserved — the active set holds exactly the addresses
      on the current root→node path, added on Expand and removed on assemble
      (matching the old recursive try/finally scoping), so a shared-but-acyclic
      sub-term (a DAG) still materialises twice rather than being mistaken for a
      cycle, and `X = f(X)` still terminates with the synthetic `_C` marker. The
      work/result stacks and cycle set moved to a per-thread scratch pool inside
      `TermReader` (transient scratch, not engine state — thread-agility preserved;
      re-entrancy guarded by a busy flag), so the now-dead `Engine.TermWalkScratchSet`
      / `TermWalkDepth` fields were removed. 7 Phase33I13 tests (deep `s`-nest as a
      query binding / deep left-assoc expr / deep-nest assertz+readback / cyclic
      `f(X)` → marker no overflow / shared-acyclic DAG materialises twice / shallow
      mixed regression / deep-list regression). Verified on the default stack:
      `s(s(…))` nesting to **100 000** survives (iteattr probe). Alloc A/B on the
      findall hot path (list-of-compounds + scalar bindings, min-of-5): 2192776 →
      2187176 B/iter — marginally **lower** than the old spine walk, i.e. at parity.
      Full 5-project gate green (Core 436 / Interpreter 105 / Compiler 302 /
      ISO 277 / Embedding 2762), no dump with the AV trap armed.

WAM↔IL boundary:
- [x] **B1** 🔴 resume-marker encoding caps. *Fixed: markers are now dense ids into a process-global interned side table of (fid, cursor) pairs (`marker = Base + denseId`) — no functor-id or cursor cap (capacity ≈1.07B distinct pairs); lock-free decode / locked intern, same discipline as the atom/functor tables. Process-global because markers bake into IL delegates shared across engines. `ResumeMarkerCursorStride` survives only as the IL emitters per-predicate cursor-count BUDGET (emit-shape policy). 4 tests incl. round-trip beyond both old caps + growth stability.*
- [-] **B2** 🟡 MaybeCollectHeap unconditional per marker resume. **Rejected —
      already resolved by chunk 428's hot/cold split**: the call inlines to a
      volatile `_cancelRequested` read + one fused compare (predicted
      not-taken) — ~2 instructions against the marker decode + array index +
      indirect delegate invoke on the same path. Removing it would also open
      a safe-point gap: it is the cancellation/GC check on the IL-callee
      RETURN path (a heavily-allocating leaf callee's next check is otherwise
      its caller's next call boundary). Cost unmeasurable, removal risky —
      keep it.
- [x] **B3** ⚪ double closure per promoted predicate; per-query dispatch cache
      rebuild. *Fixed: the dispatch (`engine => del(engine,0)`) and resume
      (`(engine,cursor) => del(engine,cursor)`) wrappers now live on
      IlPromotionStore for the ENGINE lifetime (created once per delegate
      install; every install/replace/evict — drain, sync promote, PGO swap,
      Warm, RegisterBoundDelegate, EvictDelegate — drops the cached pair so a
      swapped delegate is never shadowed). The per-query
      Tier1DispatcherAdapter allocates no closures, and its functor-keyed
      calleeMap (previously built EAGERLY per query — O(predicates) dict
      inserts per SetupQueryFromTerm) is now lazy: built only when a dispatch
      reaches a compile decision, i.e. never in the warm steady state where
      everything is already promoted or rejected.*

Functional interop gaps:
- [x] **D6** 🟡 four sub-items, resolved by evidence (2026-07-03):
      - `int**`/struct-by-value/array params — **rejected on corpus census**:
        the 63 GXPROLOG-side prototypes across testProc contain ZERO
        occurrences (all params are scalars, char*, char**, scalar
        out-pointers, reftype; the one array param `ltosp(.., TEXT *vec[], ..)`
        is in the C-side `#else` branch, never crossed from Prolog).
        Quantify-before-building: no demand, loud unsupported-type errors
        already in place.
      - allocator without setcflt/getargp per-node fallback — **rejected as
        unsound**: allocator mode exists precisely so the WHOLE graph lives in
        the library's heap (freepar frees it); falling back to HGlobal for
        some nodes recreates the mixed-allocator graph the mode prevents.
        The current loud throw on the missing export is the correct behavior.
      - IL bails on non-global reftype args — **kept as designed**: the
        interpreter fallback handles them correctly; the corpus convention is
        always the `parNref` reftype global (fill_par pattern), so the IL
        path's global-name channel covers 100% of real blocks.
      - `$native_run` 32-var ceiling — **fixed**: registration raised to
        arity 65 (64 Prolog vars), and NativeTransform now raises a clear
        consult-time error ("uses N Prolog variables; a native block supports
        at most 64") instead of a bewildering runtime
        existence_error($native_run/N). Tests: 40-var block runs past the old
        cap; 70-var block errors at consult.
