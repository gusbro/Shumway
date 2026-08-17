using Shumway.Core;

namespace Shumway.Interpreter;

public sealed partial class BytecodeInterpreter
{
    /// <summary>Runs any <c>verify_attributes</c> wakeups queued by a
    /// just-completed unification. Checked at every goal
    /// boundary — Call / Execute / CallBuiltin / Proceed. The
    /// <c>'$wakeup_attributes'/1</c> driver runs in the *live* engine
    /// (via <see cref="RunGoalInEngine"/>) so the hooks observe the real
    /// attributed variables. Returns false when a hook — or a goal it
    /// returned — failed, which the caller turns into a backtrack so the
    /// triggering unification fails. A no-op (returns true) when nothing
    /// is queued, the overwhelmingly common case.
    ///
    /// <para>split into an aggressively-inlined guard over a
    /// NoInlining slow body (the <see cref="Activation.FlushWakeupsForIlCut"/>
    /// precedent), so the 12 goal-boundary call sites pay only the inline
    /// queue-count check when nothing is queued instead of a call into a
    /// method too large to inline.</para></summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private bool FlushPendingWakeups(ProgramView code)
        => !_engine.HasPendingWakeups || FlushPendingWakeupsSlow(code);

    private int _drainCleanupsFid = -1;

    /// <summary>Runs the setup_call_cleanup drain (<c>'$drain_cleanups'/0</c>)
    /// when the engine has enqueued cleanups from a teardown path (an external
    /// cut, an exception unwinding from below, query end). Modelled on
    /// <see cref="FlushPendingWakeups"/>: a nested once-driver over a fixed goal,
    /// registers + B snapshotted and restored. A Cleanup exception propagates out
    /// as a normal exception (SWI semantics). Cheap no-op when nothing pends.</summary>
    private void FlushPendingCleanups(ProgramView code)
    {
        if (!_engine.HasPendingCleanups) return;
        FlushPendingCleanupsSlow(code);
    }

    /// <summary>setup_call_cleanup/3 teardown: enqueue every still-live handler
    /// (the query is over, or the caller stopped asking with choice points still
    /// live) and run their cleanups. Called from the query driver's finally. A
    /// Cleanup exception propagates out as a normal exception (SWI).</summary>
    public void RunTeardownCleanups(ProgramView code)
    {
        if (!_engine.HasCleanupHandlers && !_engine.HasPendingCleanups) return;
        _engine.FireAllRemainingCleanups();
        FlushPendingCleanups(code);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void FlushPendingCleanupsSlow(ProgramView code)
    {
        if (_drainCleanupsFid < 0)
            _drainCleanupsFid = Shumway.Core.FunctorTable.Intern(
                Shumway.Core.AtomTable.Intern("$drain_cleanups", permanent: true).Id, 0);
        var addrs = _engine.CurrentFunctorAddresses;
        if (addrs is null || !addrs.TryGetValue(_drainCleanupsFid, out int drainAddr))
            return;   // prelude not linked into this query — leave pending

        int regCount = _engine.RegisterCount;
        Cell[] savedRegs = new Cell[regCount];
        for (int i = 0; i < regCount; i++) savedRegs[i] = _engine.GetRegister(i);
        int savedB = _engine.B;

        bool ok = RunGoalInEngine(code, drainAddr);

        if (ok && _engine.B > savedB) _engine.Cut(savedB);   // once-semantics
        for (int i = 0; i < regCount; i++) _engine.SetRegister(i, savedRegs[i]);
    }

    /// <summary>Cold body of <see cref="FlushPendingWakeups"/> — only reached
    /// when wakeups are actually queued.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool FlushPendingWakeupsSlow(ProgramView code)
    {
        Shumway.Core.Profiler.Note("wakeup_flush");
        if (!_engine.HasAnyAttributeHook)
        {
            // No attribute-unification hook of any shape is linked — neither a
            // per-module verify_attributes/3 or /4 nor a bare one — so attributed
            // variables stay hookless (the foundation).
            _engine.ClearPendingWakeups();
            return true;
        }

        // The wakeup processing clobbers X registers and may push choice
        // points; snapshot the registers and the CP level so the goal
        // boundary we resume into is left exactly as it was.
        int regCount = _engine.RegisterCount;
        Cell[] savedRegs = new Cell[regCount];
        for (int i = 0; i < regCount; i++) savedRegs[i] = _engine.GetRegister(i);
        int savedB = _engine.B;

        bool ok = RunWakeups(code);

        if (ok && _engine.B > savedB) _engine.Cut(savedB);   // once-semantics
        for (int i = 0; i < regCount; i++) _engine.SetRegister(i, savedRegs[i]);
        return ok;
    }

    /// <summary>Drains the wakeup queue: for each batch, runs
    /// every module's <c>verify_attributes/4</c> hook, then every goal
    /// the hooks returned — all in the live engine. A hook's goal can
    /// unify further attributed variables and queue more wakeups, so the
    /// queue is drained in a loop.</summary>
    private bool RunWakeups(ProgramView code)
    {
        while (_engine.HasPendingWakeups)
        {
            var batch = _engine.TakePendingWakeups();
            for (int n = 0; n < batch.Count; n++)
                Shumway.Core.Profiler.Note("wakeup_hook_run");
            // All modules' hooks run first, then every returned goal —
            // the SICStus/Scryer ordering, so each hook sees the
            // pre-goal state.
            var goalLists = new Cell[batch.Count];
            for (int i = 0; i < batch.Count; i++)
            {
                var (moduleId, attrValueIdx, otherIdx) = batch[i];
                // ADR-040 — resolve THIS module's hook per module: its own
                // verify_attributes/3 (Scryer style) or /4 (module-local first,
                // bare fallback). Two dialects' libraries each own their hook.
                int v3 = _engine.Verify3FunctorId(moduleId);
                int v4 = v3 >= 0 ? -1 : _engine.Verify4FunctorId(moduleId);
                int goalsVarIdx = _engine.AllocateHeapUnbound();
                Cell verifyGoal;
                if (v3 >= 0)
                    verifyGoal = BuildVerify3Goal(v3, moduleId, attrValueIdx, otherIdx, goalsVarIdx);
                else if (v4 >= 0)
                    verifyGoal = BuildVerifyGoal(v4, moduleId, attrValueIdx, otherIdx, goalsVarIdx);
                else
                {
                    // This module is hookless — nothing to run; the bind already
                    // happened. Leave Goals unbound (RunGoalList treats it as empty).
                    goalLists[i] = Cell.Ref(goalsVarIdx);
                    continue;
                }
                if (!MetaCallInEngine(code, verifyGoal)) return false;
                goalLists[i] = Cell.Ref(goalsVarIdx);
            }
            for (int i = 0; i < batch.Count; i++)
                if (!RunGoalList(code, goalLists[i])) return false;
        }
        return true;
    }

    /// <summary>Builds <c>verify_attributes(Module, AttrValue, Value,
    /// Goals)</c> on the heap and returns the goal cell. <c>Goals</c> is
    /// the fresh variable at <paramref name="goalsVarIdx"/> the hook
    /// binds to its returned goal list.</summary>
    private Cell BuildVerifyGoal(int v4Functor, int moduleId, int attrValueIdx, int otherIdx, int goalsVarIdx)
    {
        int f = _engine.AllocateHeap(5);
        _engine.SetHeap(f,     Cell.Functor(v4Functor));
        _engine.SetHeap(f + 1, Cell.Atom(moduleId));
        _engine.SetHeap(f + 2, Cell.Ref(attrValueIdx));
        _engine.SetHeap(f + 3, Cell.Ref(otherIdx));
        _engine.SetHeap(f + 4, Cell.Ref(goalsVarIdx));
        return Cell.Str(f);
    }

    /// <summary>Builds the Scryer-style hook
    /// <c>Module:verify_attributes(ProxyVar, Value, Goals)</c> for a module whose
    /// hook is <c>verify_attributes/3</c> (functor id <paramref name="v3Functor"/>).
    /// The hook reads the variable's attributes itself (via <c>get_atts</c>), so we
    /// hand it a fresh attributed variable carrying the same value the module had on
    /// the now-bound variable (snapshotted at <paramref name="attrValueIdx"/> before
    /// the bind). <c>Value</c> is the term the variable was bound to.
    ///
    /// <para>Limitation: attribute WRITES the hook makes to ProxyVar do not stick
    /// (the real variable is already bound) — the deferred-wakeup design cannot run
    /// the hook before the bind. Hooks that only read attributes and return Goals
    /// (the common case) work; ones that narrow the bound variable's own attributes
    /// in-place do not.</para></summary>
    private Cell BuildVerify3Goal(
        int v3Functor, int moduleId, int attrValueIdx, int otherIdx, int goalsVarIdx)
    {
        int proxy = _engine.AllocateHeapUnbound();
        _engine.PutAttr(proxy, moduleId, attrValueIdx);
        int f = _engine.AllocateHeap(4);
        _engine.SetHeap(f,     Cell.Functor(v3Functor));
        _engine.SetHeap(f + 1, Cell.Ref(proxy));
        _engine.SetHeap(f + 2, Cell.Ref(otherIdx));
        _engine.SetHeap(f + 3, Cell.Ref(goalsVarIdx));
        return Cell.Str(f);
    }

    /// <summary>Meta-calls every goal in a hook's returned list, in
    /// order. An unbound or empty list runs nothing; a non-list term is
    /// a malformed hook result and fails.</summary>
    private bool RunGoalList(ProgramView code, Cell listCell)
    {
        Cell cursor = DerefCell(listCell);
        while (cursor.Tag == Tag.Lis)
        {
            int headIdx = cursor.AsHeapIndex;
            if (!MetaCallInEngine(code, _engine.GetHeap(headIdx))) return false;
            cursor = DerefCell(_engine.GetHeap(headIdx + 1));
        }
        // [] or an unbound tail → no (more) goals; anything else is malformed.
        return cursor.Tag == Tag.Ref
            || cursor.Tag == Tag.AttVar
            || (cursor.Tag == Tag.Atom && cursor.AsAtomId == AtomTable.EmptyListId);
    }

    /// <summary>Runs one goal term in the live engine. Handles
    /// the <c>,/2</c> conjunction and the <c>true</c> / <c>fail</c>
    /// constants; any other goal is dispatched as a plain call — a
    /// builtin runs directly, a user/prelude predicate runs via
    /// <see cref="RunGoalInEngine"/>. An undefined predicate raises an
    /// existence error.</summary>
    private bool MetaCallInEngine(ProgramView code, Cell goal)
    {
        goal = DerefCell(goal);
        int functorId;
        int argBase;
        int arity;
        switch (goal.Tag)
        {
            case Tag.Atom:
                functorId = FunctorTable.Intern(goal.AsAtomId, 0);
                arity = 0;
                argBase = -1;
                break;
            case Tag.Str:
                int fIdx = goal.AsHeapIndex;
                functorId = _engine.GetHeap(fIdx).AsFunctorId;
                (_, arity) = FunctorTable.Lookup(functorId);
                argBase = fIdx + 1;
                break;
            case Tag.Ref:
            case Tag.AttVar:
                throw new PrologRuntimeException("instantiation_error");
            default:
                throw new PrologRuntimeException("type_error", "callable");
        }

        if (functorId == ConjFunctorId)
            return MetaCallInEngine(code, _engine.GetHeap(argBase))
                && MetaCallInEngine(code, _engine.GetHeap(argBase + 1));
        if (functorId == TrueFunctorId) return true;
        if (functorId == FailFunctorId) return false;

        // Plain goal: load X0..X[arity-1] from the goal's arguments.
        for (int i = 0; i < arity; i++)
            _engine.SetRegister(i, _engine.GetHeap(argBase + i));

        if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(functorId, out int builtinId))
        {
            var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
            _engine.CurrentBuiltinName = entry.Name;
            _engine.CurrentBuiltinArity = entry.Arity;
            try { return entry.Impl(_engine); }
            catch (PrologRuntimeException re)
            { re.StampBuiltin(entry.Name, entry.Arity); throw; }
        }

        var addrs = _engine.CurrentFunctorAddresses;
        if (addrs is not null && addrs.TryGetValue(functorId, out int addr))
        {
            if (JumpDiag) CheckJumpTarget(code, addr, functorId, "meta_call");
            return RunGoalInEngine(code, addr);
        }

        // Last chance: materialize a cross-activation runtime-assert helper.
        int lateAddr = _engine.ResolveLateHelper?.Invoke(functorId) ?? -1;
        if (lateAddr >= 0)
        {
            if (JumpDiag) CheckJumpTarget(code, lateAddr, functorId, "late_helper");
            return RunGoalInEngine(code, lateAddr);
        }

        // honour the `unknown` flag (throws on error).
        return !Shumway.Core.UnknownProcedure.Fails(_engine, functorId);
    }

    /// <summary>Backtrackable runtime dispatch for <c>call/1..7</c>.
    /// The goal in <c>X0</c> — with <c>call/N</c>'s extra arguments
    /// <c>X1..X[callArity-1]</c> appended — is decoded and run as a real
    /// goal in the live engine: a user or prelude predicate is entered with
    /// a tail jump so it keeps its choice points and the call's
    /// continuation flows on success; a builtin runs inline. Control
    /// constructs in a runtime goal reach the prelude <c>$call_conj</c>,
    /// <c>$call_disj</c>, <c>$call_arrow</c>, <c>$call_neg</c> helpers.
    ///
    /// <para><paramref name="barrier"/> is the choice-point level a
    /// <c>!</c> reached as the goal cuts back to. For a
    /// top-level <c>call/N</c> it is B at entry, so a bare <c>call(!)</c>
    /// is a no-op; the conj/disj/arrow helpers thread it on through
    /// <c>'$call'/2</c> so a <c>!</c> inside a runtime compound goal
    /// commits exactly as far as the enclosing call — no further.</para>
    ///
    /// <para>Returns false only on an unrecoverable failure (no choice
    /// point remains).</para></summary>
    private bool DispatchCall(ProgramView code, int callArity, int barrier)
    {
        // Sizing diagnostic (profile builds only): how many goals are dispatched by
        // runtime term inspection — the cost class the link-time
        // MetaWrapperUnfold removes (ranked as a next-arc candidate in
        // ADR-021's closing profile).
        Shumway.Core.Profiler.Note("meta_dispatch (DispatchCall)");
        int pc = _engine.P;
        Cell goal = DerefCell(_engine.GetRegister(0));

        // '$mqual'(Module, Goal): a runtime-variable meta-goal tagged with the
        // module of the clause that meta-called it. Unwrap it, updating X0 to the
        // real goal; the module (or -1) steers the user-address resolution below
        // so a bare goal functor resolves against that module's locals first.
        int resolutionModule = PrepareMqualGoal(ref goal);

        // Save call/N's extra arguments before the registers are reloaded.
        // The per-engine scratch is safe here: the extras are consumed into
        // registers below, before any recursion or builtin can re-enter.
        int extraCount = callArity - 1;
        Cell[] extra = extraCount <= 0
            ? System.Array.Empty<Cell>()
            : extraCount <= _engine.MetaExtraScratch.Length
                ? _engine.MetaExtraScratch
                : new Cell[extraCount];
        for (int i = 0; i < extraCount; i++)
            extra[i] = _engine.GetRegister(i + 1);

        int atomId;
        int goalArity;
        int argBase;
        switch (goal.Tag)
        {
            case Tag.Atom:
                atomId = goal.AsAtomId;
                goalArity = 0;
                argBase = -1;
                break;
            case Tag.Str:
                int functorIdx = goal.AsHeapIndex;
                (atomId, goalArity) =
                    FunctorTable.Lookup(_engine.GetHeap(functorIdx).AsFunctorId);
                argBase = functorIdx + 1;
                break;
            case Tag.Ref:
            case Tag.AttVar:
                throw new PrologRuntimeException("instantiation_error");
            default:
                throw new PrologRuntimeException("type_error", "callable");
        }

        int totalArity = goalArity + extraCount;
        for (int i = 0; i < goalArity; i++)
            _engine.SetRegister(i, _engine.GetHeap(argBase + i));
        for (int i = 0; i < extraCount; i++)
            _engine.SetRegister(goalArity + i, extra[i]);

        // route cache. A repeat goal functor skips the intern,
        // the control-construct compares and the registry/address probes.
        // See MetaRoute.cs for the lifetime/soundness argument.
        var addresses = _engine.CurrentFunctorAddresses;
        var cache = _engine.MetaRouteCache;
        if (cache is null || !ReferenceEquals(_engine.MetaRouteCacheStamp, addresses))
        {
            cache = _engine.MetaRouteCache =
                new System.Collections.Generic.Dictionary<long, Shumway.Core.MetaRoute>();
            _engine.MetaRouteCacheStamp = addresses;
        }
        // A module-tagged goal bypasses the route cache: its resolution depends
        // on the module too, and the tagged path is the (uncommon) variable
        // meta-call, not the hot direct-call path.
        bool routeCacheable = resolutionModule < 0 && (uint)totalArity <= 0xFFFF;   // key packs arity in 16 bits
        long routeKey = ((long)atomId << 16) | (uint)totalArity;
        if (routeCacheable && cache.TryGetValue(routeKey, out var route))
        {
            switch (route.Kind)
            {
                case Shumway.Core.MetaRouteKind.Cut:
                    _engine.Cut(barrier);
                    _engine.AdvancePc(9);
                    return true;
                case Shumway.Core.MetaRouteKind.True:
                    _engine.AdvancePc(9);
                    return true;
                case Shumway.Core.MetaRouteKind.Fail:
                    return TryBacktrack();
                case Shumway.Core.MetaRouteKind.CallRecurse:
                    return DispatchCall(code,
                        Shumway.Builtins.BuiltinsRegistry.GetById(route.Arg).Arity,
                        _engine.B);
                case Shumway.Core.MetaRouteKind.DollarCall:
                case Shumway.Core.MetaRouteKind.Builtin:
                    return InvokeBuiltinGoal(route.Arg);
                case Shumway.Core.MetaRouteKind.BarrierHelperJump:
                    _engine.SetRegister(2, Cell.Int(barrier));
                    goto case Shumway.Core.MetaRouteKind.Jump;
                case Shumway.Core.MetaRouteKind.Jump:
                    return JumpToUserGoal(code, pc, route.Arg);
            }
        }

        int functorId = FunctorTable.Intern(atomId, totalArity);

        // A control construct in a runtime goal routes to its prelude
        // helper. conj/disj/arrow are cut-transparent, so they take the
        // barrier as a third argument (X2): a `!` threaded down through
        // them commits to the enclosing call. \+ is opaque to
        // cut, so $call_neg needs no barrier.
        var userKind = Shumway.Core.MetaRouteKind.Jump;
        if (functorId == ConjFunctorId)
        {
            Shumway.Core.Profiler.Note("meta_dispatch: control construct");
            _engine.SetRegister(2, Cell.Int(barrier));
            functorId = CallConjFunctorId;
            userKind = Shumway.Core.MetaRouteKind.BarrierHelperJump;
        }
        else if (functorId == DisjFunctorId)
        {
            Shumway.Core.Profiler.Note("meta_dispatch: control construct");
            _engine.SetRegister(2, Cell.Int(barrier));
            functorId = CallDisjFunctorId;
            userKind = Shumway.Core.MetaRouteKind.BarrierHelperJump;
        }
        else if (functorId == ArrowFunctorId)
        {
            Shumway.Core.Profiler.Note("meta_dispatch: control construct");
            _engine.SetRegister(2, Cell.Int(barrier));
            functorId = CallArrowFunctorId;
            userKind = Shumway.Core.MetaRouteKind.BarrierHelperJump;
        }
        else if (functorId == SoftArrowFunctorId)   // ADR-037 — bare ( C *-> T )
        {
            Shumway.Core.Profiler.Note("meta_dispatch: control construct");
            _engine.SetRegister(2, Cell.Int(barrier));
            functorId = CallSoftArrowFunctorId;
            userKind = Shumway.Core.MetaRouteKind.BarrierHelperJump;
        }
        else if (functorId == NegFunctorId || functorId == NotFunctorId)
        {
            Shumway.Core.Profiler.Note("meta_dispatch: control construct");
            functorId = CallNegFunctorId;
        }

        // ! as the whole goal: commit to the barrier the enclosing call
        // established. For a top-level call(!) the barrier is B
        // at call entry, so Cut() removes nothing; for a `!` threaded in
        // from a $call_* helper it cuts the runtime goal's choice points,
        // and no further — the parent's CPs sit at or below the barrier.
        if (functorId == CutFunctorId)
        {
            if (routeCacheable)
                cache[routeKey] = new Shumway.Core.MetaRoute(Shumway.Core.MetaRouteKind.Cut, 0);
            _engine.Cut(barrier);
            _engine.AdvancePc(9);
            return true;
        }
        if (functorId == TrueFunctorId)
        {
            if (routeCacheable)
                cache[routeKey] = new Shumway.Core.MetaRoute(Shumway.Core.MetaRouteKind.True, 0);
            _engine.AdvancePc(9);
            return true;
        }
        if (functorId == FailFunctorId)
        {
            if (routeCacheable)
                cache[routeKey] = new Shumway.Core.MetaRoute(Shumway.Core.MetaRouteKind.Fail, 0);
            return TryBacktrack();
        }

        // Module-relative resolution FIRST — before the builtin check: a tagged
        // goal resolves against the meta-caller's module locals (module$name),
        // so a runtime-variable meta-call reaches a module-local predicate the
        // same way a compile-time one is mangled. Order matters: an
        // export-qualified module may define its OWN version of a builtin-named
        // predicate (Scryer's iso_ext defines copy_term/3, forall/2, succ/2) —
        // `iso_ext:copy_term(...)` must run iso_ext$copy_term, not the engine
        // builtin, exactly as its compile-time internal calls do. A module that
        // defines no such local (nor imports one) falls through to the builtin.
        if (resolutionModule >= 0 && addresses is not null)
        {
            int mangledFid = MangleFunctorId(resolutionModule, atomId, totalArity);
            if (addresses.TryGetValue(mangledFid, out int mangledAddr))
                return JumpToUserGoal(code, pc, mangledAddr);
            // ADR-038 — the module's import table: a bare goal it doesn't define
            // locally resolves to Source$name before the bare-global namespace.
            var importMap = _engine.CurrentImportMap;
            if (importMap is not null
                && importMap.TryGetValue(
                    ((long)resolutionModule << 32) | (uint)functorId, out int importedFid)
                && addresses.TryGetValue(importedFid, out int importedAddr))
                return JumpToUserGoal(code, pc, importedAddr);
        }

        if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(functorId, out int builtinId))
        {
            var builtin = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
            // call(call(...)): recurse rather than invoking the call
            // builtin. The inner call is itself a fresh cut barrier, so
            // capture B again rather than passing the outer `barrier`.
            if (builtin.IsCall)
            {
                if (routeCacheable)
                    cache[routeKey] = new Shumway.Core.MetaRoute(
                        Shumway.Core.MetaRouteKind.CallRecurse, builtinId);
                return DispatchCall(code, builtin.Arity, _engine.B);
            }
            if (routeCacheable)
                cache[routeKey] = new Shumway.Core.MetaRoute(
                    builtin.IsDollarCall
                        ? Shumway.Core.MetaRouteKind.DollarCall
                        : Shumway.Core.MetaRouteKind.Builtin,
                    builtinId);
            return InvokeBuiltinGoal(builtinId);
        }

        if (addresses is not null && addresses.TryGetValue(functorId, out int address))
        {
            if (routeCacheable)
                cache[routeKey] = new Shumway.Core.MetaRoute(userKind, address);
            return JumpToUserGoal(code, pc, address);
        }

        // Last chance: materialize a cross-activation runtime-assert helper.
        int lateHelper = _engine.ResolveLateHelper?.Invoke(functorId) ?? -1;
        if (lateHelper >= 0) return JumpToUserGoal(code, pc, lateHelper);

        // No negative caching: an unresolved functor can become resolvable
        // later in the same query (auto-promotion).
        // honour the `unknown` flag (throws on error).
        if (Shumway.Core.UnknownProcedure.Fails(_engine, functorId))
            return TryBacktrack();
        throw PrologRuntimeException.UndefinedProcedure(functorId);   // unreachable
    }

    /// <summary>Invokes a builtin reached as a runtime meta-call goal
    /// (shared by DispatchCall's slow path and its cached
    /// Builtin/DollarCall routes).</summary>
    private bool InvokeBuiltinGoal(int builtinId)
    {
        var builtin = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        _engine.CurrentBuiltinName = builtin.Name;
        _engine.CurrentBuiltinArity = builtin.Arity;
        bool ok;
        try { ok = builtin.Impl(_engine); }
        catch (PrologRuntimeException re)
        { re.StampBuiltin(builtin.Name, builtin.Arity); throw; }
        if (!ok)
            return TryBacktrack();
        _engine.AdvancePc(9);
        return true;
    }

    /// <summary>Transfers control to a user predicate reached as a runtime
    /// meta-call goal (shared by DispatchCall's slow path and
    /// its cached Jump/BarrierHelperJump routes).</summary>
    private bool JumpToUserGoal(ProgramView code, int pc, int address)
    {
        // Last-call optimisation: when this call is the clause's final
        // goal, tail-jump so the goal returns to the clause's caller.
        // Setting Cp to the Proceed sitting right after this
        // CallBuiltin would spin — Proceed does not advance Cp.
        Opcode following = (Opcode)code[pc + 9];
        if (following == Opcode.Deallocate)
            _engine.Deallocate();              // last goal, frame: pop it
        else if (following != Opcode.Proceed)
            _engine.SetCp(pc + 9);             // non-last: resume after the call
        _engine.SetB0(_engine.B);
        // Cp untouched (Deallocate / Proceed follows) => the goal returns
        // straight to our caller: a tail call, for the debug ports (ADR-035).
        bool tail = following == Opcode.Deallocate || following == Opcode.Proceed;
        if (Activation.CpPushRing is { } jr)
            jr[Activation.CpPushRingPos++ & (Activation.CpPushRingSize - 1)]
                = ((long)-4 << 32) | (uint)address;
        DispatchToTier1OrBytecode(address, tail);
        return true;
    }

    /// <summary>Unwraps a <c>'$mqual'(Module, Goal)</c> tag on the goal in X0,
    /// updating <paramref name="goal"/> and X0 to the real goal and returning the
    /// module atom id (innermost wins on nesting) or -1 when untagged. When the
    /// real goal is a control construct, the module is distributed into its goal
    /// sub-args (so they keep resolving in that module once the <c>$call_*</c>
    /// helper runs them) and -1 is returned.</summary>
    private int PrepareMqualGoal(ref Cell goal)
    {
        int module = -1;
        while (goal.Tag == Tag.Str)
        {
            int fidx = goal.AsHeapIndex;
            int fid = _engine.GetHeap(fidx).AsFunctorId;
            // Both the engine's $mqual(Module, Goal) tag and the ISO Module:Goal
            // qualifier share the same (Module, Goal) layout. A user-written
            // `M:G` with a non-atom module is not a valid qualification — leave it
            // for the callable/type checks below rather than unwrapping it.
            if (fid == MqualFunctorId) { }
            else if (fid == ColonFunctorId
                     && DerefCell(_engine.GetHeap(fidx + 1)).Tag == Tag.Atom) { }
            else break;
            Cell mCell = DerefCell(_engine.GetHeap(fidx + 1));
            if (mCell.Tag == Tag.Atom) module = mCell.AsAtomId;
            goal = DerefCell(_engine.GetHeap(fidx + 2));
        }
        if (module < 0) return -1;

        if (goal.Tag == Tag.Str)
        {
            int fid = _engine.GetHeap(goal.AsHeapIndex).AsFunctorId;
            if (fid == ConjFunctorId || fid == DisjFunctorId
                || fid == ArrowFunctorId || fid == SoftArrowFunctorId)
            {
                goal = DistributeMqual(goal, module, arg0Goal: true, arg1Goal: true);
                _engine.SetRegister(0, goal);
                return -1;
            }
            if (fid == NegFunctorId || fid == NotFunctorId)
            {
                goal = DistributeMqual(goal, module, arg0Goal: true, arg1Goal: false);
                _engine.SetRegister(0, goal);
                return -1;
            }
        }
        _engine.SetRegister(0, goal);
        return module;
    }

    /// <summary>Tags a goal-position sub-arg with the resolution module. Normally
    /// wraps it as <c>'$mqual'(Module, Goal)</c>; but when the goal is itself an
    /// if-then-else (<c>-&gt;</c> / <c>*-&gt;</c>) — the shape a <c>;</c> matches
    /// structurally in <c>'$call_disj'</c> to give it if-then-else / soft-cut
    /// semantics — it distributes the module INTO the construct's Cond/Then
    /// instead. A wrapping <c>$mqual</c> there would hide the <c>-&gt;</c>/<c>*-&gt;</c>
    /// from that match (falling to the plain-disjunction clauses, which run BOTH
    /// branches / raise <c>existence_error(*-&gt;/2)</c>).</summary>
    private Cell WrapGoal(int module, Cell goalCell)
    {
        Cell d = DerefCell(goalCell);
        if (d.Tag == Tag.Str)
        {
            int f = _engine.GetHeap(d.AsHeapIndex).AsFunctorId;
            if (f == ArrowFunctorId || f == SoftArrowFunctorId)
                return DistributeMqual(d, module, arg0Goal: true, arg1Goal: true);
        }
        return BuildMqual(module, goalCell);
    }

    /// <summary>Allocates <c>'$mqual'(Module, Goal)</c> on the heap.</summary>
    private Cell BuildMqual(int moduleAtomId, Cell goalCell)
    {
        int f = _engine.AllocateHeap(3);
        _engine.SetHeap(f, Cell.Functor(MqualFunctorId));
        _engine.SetHeap(f + 1, Cell.Atom(moduleAtomId));
        _engine.SetHeap(f + 2, goalCell);
        return Cell.Str(f);
    }

    /// <summary>Rebuilds a binary/unary control construct with its goal-position
    /// sub-args re-tagged <c>'$mqual'(Module, sub)</c>, so a module travels into
    /// the sub-goals a runtime-variable meta-goal resolves.</summary>
    private Cell DistributeMqual(Cell ctor, int module, bool arg0Goal, bool arg1Goal)
    {
        int src = ctor.AsHeapIndex;
        int fid = _engine.GetHeap(src).AsFunctorId;
        var (_, arity) = FunctorTable.Lookup(fid);
        // Capture the source args before BuildMqual allocates (so the reserved
        // ctor block and the $mqual blocks never interleave mid-write).
        Cell a0 = arity > 0 ? _engine.GetHeap(src + 1) : default;
        Cell a1 = arity > 1 ? _engine.GetHeap(src + 2) : default;
        Cell w0 = arg0Goal && arity > 0 ? WrapGoal(module, a0) : a0;
        Cell w1 = arg1Goal && arity > 1 ? WrapGoal(module, a1) : a1;
        int f = _engine.AllocateHeap(arity + 1);
        _engine.SetHeap(f, Cell.Functor(fid));
        if (arity > 0) _engine.SetHeap(f + 1, w0);
        if (arity > 1) _engine.SetHeap(f + 2, w1);
        return Cell.Str(f);
    }

    /// <summary>Builds the mangled <c>module$name/arity</c> functor id used to
    /// resolve a bare meta-goal against its meta-caller's module locals.</summary>
    private static int MangleFunctorId(int moduleAtomId, int nameAtomId, int arity)
    {
        string module = AtomTable.GetById(moduleAtomId)?.Name ?? "";
        string name = AtomTable.GetById(nameAtomId)?.Name ?? "";
        int mangledAtom = AtomTable.Intern(module + "$" + name, permanent: true).Id;
        return FunctorTable.Intern(mangledAtom, arity);
    }

    /// <summary>Dereferences a cell, following REF chains to the term it
    /// names (or to an unbound REF / ATTVAR).</summary>
    private Cell DerefCell(Cell c) =>
        c.Tag == Tag.Ref ? _engine.GetHeap(_engine.Deref(c.AsHeapIndex)) : c;

    /// <summary>When a Call/Execute target is an
    /// unresolved-procedure sentinel baked into the bytecode at link
    /// time, check whether the predicate has been auto-promoted
    /// mid-query (the <c>implicit_dynamic</c> flag's runtime path
    /// materialised a trampoline after the call site was already
    /// linked). If the current address map now holds a real address
    /// for the functor, use it. Otherwise raise the standard
    /// <c>existence_error(procedure, Name/Arity)</c>.</summary>
    private int ResolveTargetMaybeAutoPromoted(int target)
    {
        if (!CallTarget.IsUnresolved(target)) return target;
        int fid = CallTarget.FunctorIdOf(target);
        var map = _engine.CurrentFunctorAddresses;
        if (map is not null
            && map.TryGetValue(fid, out int latest)
            && !CallTarget.IsUnresolved(latest))
        {
            // Restrict resolution to predicates whose layout starts
            // with `enter_dynamic` — i.e. a dynamic trampoline emitted
            // by the auto-promotion path. A non-dynamic predicate
            // present in CurrentFunctorAddresses under the same fid
            // (e.g. a module-local predicate that the link layer
            // deliberately did NOT expose to this call site) must
            // still raise the standard existence_error rather than
            // breaking module visibility.
            var prog = _engine.CurrentProgram;
            if (prog is not null
                && latest >= 0
                && latest < prog.Length
                && (Opcode)prog[latest] == Opcode.EnterDynamic)
                return latest;
            // a mid-query consult (consult/1 from a live query)
            // live-links STATIC predicates into the running query's code
            // space; a call site compiled at THIS query's setup (before the
            // consult) baked the undefined sentinel for them. The consult
            // made these fids globally visible exactly as a top-level
            // consult would, so resolving the sentinel to the live-linked
            // static address is sound — the fid is on the explicit
            // visibility set the live-link populated, not an accidental
            // module-local collision.
            if (_engine.LiveConsultVisibleFids is { } visible
                && visible.Contains(fid))
                return latest;
            // a --strip-wam predicate has no WAM address; its map entry is
            // a resume MARKER (a standalone delegate's (fid, 0), or a region member's
            // (rootFid, memberEntryCursor) alias). Accept it — the Call/Execute handler
            // SetPc's it and the dispatch loop's marker route invokes the IL. Module
            // visibility is not widened: the sentinel's fid was chosen by the LINK
            // layer (mangled for a local), so resolving that exact fid's own alias
            // grants nothing the link didn't already grant. Cold path — sentinels only.
            if (Activation.IsResumeMarker(latest))
                return latest;
        }
        // Last chance: a runtime-assert MetaTransform helper linked by a
        // DIFFERENT activation — materialize it into this one on demand.
        int late = _engine.ResolveLateHelper?.Invoke(fid) ?? -1;
        if (late >= 0) return late;
        // honour the `unknown` flag — error throws here,
        // fail/warning hand the caller the fail sentinel.
        if (Shumway.Core.UnknownProcedure.Fails(_engine, fid))
            return UnknownFailTarget;
        throw PrologRuntimeException.UndefinedProcedure(fid);   // unreachable
    }

    /// <summary>sentinel returned by
    /// <see cref="ResolveTargetMaybeAutoPromoted"/> when the target is an
    /// undefined procedure and the <c>unknown</c> flag says fail: the
    /// Call/Execute handlers backtrack instead of dispatching.</summary>
    private const int UnknownFailTarget = int.MinValue;

    /// <summary>Runs the predicate at <paramref name="target"/> as a goal
    /// in the <em>current</em> engine — same heap, trail, stack and
    /// attribute table — then resumes the caller. Unlike
    /// <see cref="RunSubroutine"/> this is safe for a goal that pushes
    /// choice points or fails: a backtrack floor pins inner backtracking
    /// at the entry choice-point level, and on success any choice points
    /// the goal left are cut away (once semantics). Returns true iff the
    /// goal succeeded. The caller saves/restores X registers.</summary>
    private bool RunGoalInEngine(ProgramView code, int target)
    {
        int savedPc    = _engine.P;
        int savedCp    = _engine.Cp;
        int savedB0    = _engine.B0;
        int savedB     = _engine.B;
        int savedFloor = _backtrackFloor;
        int entryCatchFrames = _engine.CatchFrameCount;

        // Inner backtracking may not unwind past the entry CP level.
        _backtrackFloor = savedB;
        _engine.SetB0(savedB);               // a cut inside the goal stops here
        _engine.SetCp(SubroutineSentinelCp); // the goal's final proceed → Halted
        if (Activation.CpPushRing is { } gr)
            gr[Activation.CpPushRingPos++ & (Activation.CpPushRingSize - 1)]
                = ((long)-5 << 32) | (uint)target;
        _engine.SetPc(target);

        InterpreterResult result;
        while (true)
        {
            try { result = Dispatch(code); break; }
            catch (TopLevelFailure) { result = InterpreterResult.Failed; break; }
            catch (Exception ex) when (ResolveNestedCatch(ex, entryCatchFrames, out int recovery))
            {
                // A catch/3 frame opened INSIDE this nested goal caught the
                // ball. The C# unwinding already destroyed the inner Dispatch
                // frames, but THIS driver frame — which owns the interrupted
                // caller's continuation (saved Pc/Cp/B0 below) — must survive:
                // resume the recovery in our own loop. A ball whose matching
                // frame is OUTSIDE the nested goal (or matches nothing)
                // rethrows via the filter and unwinds this driver too, which
                // is then correct — the outer rollback discards us wholesale.
                _engine.SetPc(recovery);
            }
        }

        _backtrackFloor = savedFloor;
        _engine.SetPc(savedPc);
        _engine.SetCp(savedCp);
        _engine.SetB0(savedB0);

        if (result == InterpreterResult.Halted)
        {
            // Once-semantics: discard any choice points the goal left so
            // the outer computation never backtracks into it.
            if (_engine.B > savedB) _engine.Cut(savedB);
            return true;
        }
        return false;
    }

    internal static readonly bool JumpDiag =
        System.Environment.GetEnvironmentVariable("SHUMWAY_JUMP_DIAG") == "1";

    /// <summary>SHUMWAY_JUMP_DIAG=1 tripwire: a resolved goal address about
    /// to be jumped to must hold a real opcode, not padding/mid-instruction
    /// bytes. Names the functor and resolution path when it doesn't.</summary>
    private void CheckJumpTarget(ProgramView code, int addr, int functorId, string via)
    {
        if (Activation.IsResumeMarker(addr)) return;
        if (addr >= 0 && addr < code.Length && code[addr] != 0) return;
        var (atomId, arity) = FunctorTable.Lookup(functorId);
        System.Console.Error.WriteLine(
            $"[JUMP-DIAG] {via}: {AtomTable.GetById(atomId)?.Name}/{arity}"
            + $" -> 0x{addr:X} byte={(addr >= 0 && addr < code.Length ? code[addr] : -1)}"
            + $" ({_engine.ResolveAddressToLabel?.Invoke(addr) ?? "?"}) len=0x{code.Length:X}");
    }

    /// <summary>Exception-filter helper for <see cref="RunGoalInEngine"/>:
    /// true (with the recovery address) iff the host's
    /// <see cref="Activation.NestedCatchResolver"/> matched the ball against a
    /// catch frame at or above <paramref name="minFrameIndex"/> — the resolver
    /// rolls the machine back to the frame as a side effect of matching.</summary>
    private bool ResolveNestedCatch(Exception ex, int minFrameIndex, out int recovery)
    {
        recovery = -1;
        var resolver = _engine.NestedCatchResolver;
        if (resolver is null) return false;
        recovery = resolver(ex, minFrameIndex);
        return recovery >= 0;
    }
}
