using System.Reflection;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Il;

public sealed partial class IlPredicateCompiler
{
    /// <summary>Emits IL that leaves a <see cref="PredicateDelegate"/> on
    /// the evaluation stack — the running predicate's own delegate, used
    /// as the callback target for <c>engine.PushIlChoicePoint</c>. Two
    /// implementations, both a direct <c>ldsfld / ldc / ldelem.ref</c> slot
    /// load (the DynamicMethod path used to be
    /// <c>call IndexedDelegateHolder.Get</c>, a ConcurrentDictionary probe
    /// per multi-clause region invocation, ~3% of engine time on the Tier-1
    /// profile):
    /// <list type="bullet">
    /// <item>DynamicMethod: the process-wide <see cref="IndexedDelegateHolder.Slots"/>
    /// array, indexed by the registration key.</item>
    /// <item>Persisted assembly: a static array field on the emitted type,
    /// resolved at load time.</item>
    /// </list></summary>
    internal delegate void SelfDelegateEmitter(Sigil.Emit<PredicateDelegate> emit);

    internal static SelfDelegateEmitter SelfFromHolder(int holderKey) =>
        e =>
        {
            e.LoadField(IndexedDelegateHolder.SlotsField);
            e.LoadConstant(holderKey);
            e.LoadElement<Func<Activation, int, bool>>();
        };

    internal static SelfDelegateEmitter SelfFromArrayField(
        System.Reflection.FieldInfo arrayField, int slot) =>
        e =>
        {
            e.LoadField(arrayField);
            e.LoadConstant(slot);
            e.LoadElement<PredicateDelegate>();
        };

    /// <summary>Side table that lets a freshly-emitted IL delegate
    /// reference itself for the <c>PushIlChoicePoint</c> call without
    /// running into the chicken-and-egg of "the delegate must exist
    /// before we can name it in IL". The IL embeds an integer key; at
    /// runtime the slot array resolves it to the stored delegate. The
    /// table is process-wide but write-once-per-key.</summary>
    internal static class IndexedDelegateHolder
    {
        // The store is a plain slot ARRAY indexed by
        // the (sequential, RegistrationLock-serialised) holder key, and
        // SelfFromHolder emits a direct `ldsfld / ldc / ldelem.ref` instead
        // of a call — the Tier-1 profile showed the previous
        // ConcurrentDictionary.TryGetValue as ~3% of engine time, one
        // hash+bucket probe per multi-clause region invocation; the
        // direct slot load removes the probe altogether. Publication safety: Register runs under
        // RegistrationLock; a grow copies the old entries and stores the
        // new delegate into the NEW array BEFORE Volatile.Write publishes
        // it, so any array version a reader can observe after delegate X
        // escaped (always through a fenced channel — the compile-result
        // queue or the promotion tables) already contains X's slot.
        public static Func<Activation, int, bool>?[] Slots = new Func<Activation, int, bool>?[256];
        private static readonly object _lock = new();

        internal static readonly System.Reflection.FieldInfo SlotsField =
            typeof(IndexedDelegateHolder).GetField(nameof(Slots))!;

        /// <summary>The lock the IL emission takes around the
        /// emit-and-register sequence so two concurrent compiles don't
        /// race on <c>_nextHolderKey</c>.</summary>
        public static object RegistrationLock => _lock;

        public static void Register(int key, PredicateDelegate del)
        {
            lock (_lock)
            {
                var wrapped = new Func<Activation, int, bool>(del);
                var arr = Slots;
                if (key >= arr.Length)
                {
                    var grown = new Func<Activation, int, bool>?[System.Math.Max(arr.Length * 2, key + 1)];
                    System.Array.Copy(arr, grown, arr.Length);
                    grown[key] = wrapped;
                    System.Threading.Volatile.Write(ref Slots, grown);
                }
                else
                {
                    arr[key] = wrapped;
                }
            }
        }

        public static Func<Activation, int, bool> Get(int key) => Slots[key]!;
    }

    /// <summary>Resolves a callee functor id to its current-query
    /// bytecode address by consulting <see cref="Activation.CurrentFunctorAddresses"/>.
    /// Called from IL-emitted Execute opcodes so the tail-call
    /// target stays correct across queries even when the link layout
    /// changes between them.</summary>
    public static class IlExecuteHelper
    {
        public static int Resolve(Activation engine, int functorId)
        {
            var map = engine.CurrentFunctorAddresses;
            if (map is null)
                throw new InvalidOperationException(
                    "IL Execute: engine has no CurrentFunctorAddresses set. "
                    + "The embedding layer must populate it at query setup.");
            if (!map.TryGetValue(functorId, out int address))
                throw PrologRuntimeException.UndefinedProcedure(functorId);
            // The address may be a CallTarget.ForUndefined
            // sentinel left by the linker (the IL caller's static
            // rewrite baked a direct Call/Execute against an
            // unresolved functor) AND the implicit_dynamic auto-
            // promote may since have materialised a trampoline.
            // Re-look-up the live entry; if it's still unresolved,
            // raise existence_error.
            if (Shumway.Core.CallTarget.IsUnresolved(address))
                throw PrologRuntimeException.UndefinedProcedure(functorId);
            return address;
        }
    }

    /// <summary>runtime helper that the IL emit calls from
    /// <c>CallBuiltin call/N</c> and <c>CallBuiltin '$call'/2</c> sites.
    /// Mirrors the bytecode interpreter's <c>DispatchCall</c>
    /// but returns a sentinel value so the IL caller can branch on
    /// the three outcomes: synchronous success (the goal was a control
    /// construct that resolved inline — cut, true, or a builtin that
    /// returned true), synchronous failure (fail or a builtin that
    /// returned false), or "dispatch this target via the
    /// threaded path" (an ordinary user predicate / a builtin replaced by
    /// a $call_* helper).
    ///
    /// <para>The IL caller sets up <c>Cp = resume_marker</c> only when
    /// the return is &gt;= 0 (an actual target address). For sync
    /// success the caller falls through to its next opcode; for sync
    /// fail the caller jumps to its fail label.</para>
    /// </summary>
    public static class IlMetaCallHelper
    {
        public const int SyncFail = -1;
        public const int SyncSuccess = -2;

        // Cached control-construct ids — the bytecode interpreter
        // re-interns these as private statics; we do the same so the
        // IL emit doesn't pay an Intern per dispatch.
        private static readonly int ConjFid =
            FunctorTable.Intern(AtomTable.Intern(",", permanent: true).Id, 2);
        private static readonly int DisjFid =
            FunctorTable.Intern(AtomTable.Intern(";", permanent: true).Id, 2);
        private static readonly int ArrowFid =
            FunctorTable.Intern(AtomTable.Intern("->", permanent: true).Id, 2);
        private static readonly int SoftArrowFid =   // ADR-037 — *->/2
            FunctorTable.Intern(AtomTable.Intern("*->", permanent: true).Id, 2);
        private static readonly int NegFid =
            FunctorTable.Intern(AtomTable.Intern("\\+", permanent: true).Id, 1);
        private static readonly int NotFid =
            FunctorTable.Intern(AtomTable.Intern("not", permanent: true).Id, 1);
        private static readonly int CutFid =
            FunctorTable.Intern(AtomTable.Intern("!", permanent: true).Id, 0);
        private static readonly int TrueFid =
            FunctorTable.Intern(AtomTable.Intern("true", permanent: true).Id, 0);
        private static readonly int FailFid =
            FunctorTable.Intern(AtomTable.Intern("fail", permanent: true).Id, 0);
        private static readonly int CallConjFid =
            FunctorTable.Intern(AtomTable.Intern("$call_conj", permanent: true).Id, 3);
        private static readonly int CallDisjFid =
            FunctorTable.Intern(AtomTable.Intern("$call_disj", permanent: true).Id, 3);
        private static readonly int CallArrowFid =
            FunctorTable.Intern(AtomTable.Intern("$call_arrow", permanent: true).Id, 3);
        private static readonly int CallSoftArrowFid =   // ADR-037 — bare *->/2
            FunctorTable.Intern(AtomTable.Intern("$call_softarrow", permanent: true).Id, 3);
        private static readonly int CallNegFid =
            FunctorTable.Intern(AtomTable.Intern("$call_neg", permanent: true).Id, 1);
        private static readonly int MqualFid =
            FunctorTable.Intern(AtomTable.Intern("$mqual", permanent: true).Id, 2);
        // ISO module-qualified goal `Module:Goal` — same (Module, Goal) shape as
        // $mqual; unwrapped so call(M:G, Extra) extends G, not the ':' functor.
        private static readonly int ColonFid =
            FunctorTable.Intern(AtomTable.Intern(":", permanent: true).Id, 2);

        /// <summary>Dispatches <c>call/N</c> with <paramref name="callArity"/>
        /// extra-arg count and the supplied cut barrier. Returns the
        /// callee's address (&gt;= 0), or <see cref="SyncSuccess"/>
        /// (the goal was <c>!</c>, <c>true</c>, or a builtin that
        /// returned true), or <see cref="SyncFail"/> (the goal was
        /// <c>fail</c>, or a builtin that returned false).
        ///
        /// <para>Side effects when returning a non-negative address:
        /// the X registers hold the dispatched goal's arguments
        /// (goal args + appended call/N extra args), and
        /// <c>engine.B0</c> is set to <paramref name="cutBarrier"/> so
        /// a neck_cut at the callee entry commits to the call's
        /// barrier rather than the IL caller's.</para>
        /// </summary>
        public static int Dispatch(Activation engine, int callArity, int cutBarrier)
        {
            Cell goal = DerefCell(engine, engine.GetRegister(0));

            // '$mqual'(Module, Goal): a module-tagged runtime-variable meta-goal
            // (see ModuleRewrite / BytecodeInterpreter.DispatchCall). Unwrap it,
            // updating X0; the module steers the user-address resolution below so
            // a bare goal functor resolves against that module's locals first.
            int resolutionModule = PrepareMqualGoal(engine, ref goal);

            // Save call/N's extra args before SetRegister reshuffles them.
            // Per-engine scratch: consumed into registers below,
            // before any recursion or builtin can re-enter.
            int extraCount = callArity - 1;
            Cell[] extra = extraCount <= 0
                ? System.Array.Empty<Cell>()
                : extraCount <= engine.MetaExtraScratch.Length
                    ? engine.MetaExtraScratch
                    : new Cell[extraCount];
            for (int i = 0; i < extraCount; i++)
                extra[i] = engine.GetRegister(i + 1);

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
                        FunctorTable.Lookup(engine.GetHeap(functorIdx).AsFunctorId);
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
                engine.SetRegister(i, engine.GetHeap(argBase + i));
            for (int i = 0; i < extraCount; i++)
                engine.SetRegister(goalArity + i, extra[i]);

            // shared meta-call route cache (see MetaRoute.cs).
            // Same cache the bytecode interpreter's DispatchCall fills; each
            // dispatcher executes a cached kind exactly as its own slow path.
            var addresses = engine.CurrentFunctorAddresses;
            var cache = engine.MetaRouteCache;
            if (cache is null || !ReferenceEquals(engine.MetaRouteCacheStamp, addresses))
            {
                cache = engine.MetaRouteCache =
                    new System.Collections.Generic.Dictionary<long, MetaRoute>();
                engine.MetaRouteCacheStamp = addresses;
            }
            // A module-tagged goal bypasses the route cache (its resolution
            // depends on the module too, and it is the uncommon variable path).
            bool routeCacheable = resolutionModule < 0 && (uint)totalArity <= 0xFFFF;
            long routeKey = ((long)atomId << 16) | (uint)totalArity;
            if (routeCacheable && cache.TryGetValue(routeKey, out var route))
            {
                switch (route.Kind)
                {
                    case MetaRouteKind.Cut:
                        engine.Cut(cutBarrier);
                        return SyncSuccess;
                    case MetaRouteKind.True:
                        return SyncSuccess;
                    case MetaRouteKind.Fail:
                        return SyncFail;
                    case MetaRouteKind.CallRecurse:
                        return Dispatch(engine,
                            Shumway.Builtins.BuiltinsRegistry.GetById(route.Arg).Arity,
                            engine.B);
                    case MetaRouteKind.DollarCall:
                        return Dispatch(engine, 1,
                            (int)DerefCell(engine, engine.GetRegister(1)).AsInt);
                    case MetaRouteKind.Builtin:
                        return InvokeBuiltinGoal(engine, route.Arg);
                    case MetaRouteKind.BarrierHelperJump:
                        engine.SetRegister(2, Cell.Int(cutBarrier));
                        engine.SetB0(cutBarrier);
                        return route.Arg;
                    case MetaRouteKind.Jump:
                        engine.SetB0(cutBarrier);
                        return route.Arg;
                }
            }

            int functorId = FunctorTable.Intern(atomId, totalArity);
            // Control-construct routing — `!` inside the
            // runtime goal commits to the call's barrier via the
            // $call_* helpers' arity-3 form (X[2] carries the barrier).
            var userKind = MetaRouteKind.Jump;
            if (functorId == ConjFid)
            {
                engine.SetRegister(2, Cell.Int(cutBarrier));
                functorId = CallConjFid;
                userKind = MetaRouteKind.BarrierHelperJump;
            }
            else if (functorId == DisjFid)
            {
                engine.SetRegister(2, Cell.Int(cutBarrier));
                functorId = CallDisjFid;
                userKind = MetaRouteKind.BarrierHelperJump;
            }
            else if (functorId == ArrowFid)
            {
                engine.SetRegister(2, Cell.Int(cutBarrier));
                functorId = CallArrowFid;
                userKind = MetaRouteKind.BarrierHelperJump;
            }
            else if (functorId == SoftArrowFid)   // ADR-037 — bare ( C *-> T )
            {
                engine.SetRegister(2, Cell.Int(cutBarrier));
                functorId = CallSoftArrowFid;
                userKind = MetaRouteKind.BarrierHelperJump;
            }
            else if (functorId == NegFid || functorId == NotFid)
            {
                functorId = CallNegFid;
            }

            // Cut as the runtime goal: commits to the call's barrier.
            // The interpreter's DispatchCall AdvancePc's after Cut;
            // for IL we just report sync success so the caller falls
            // through to its next opcode.
            if (functorId == CutFid)
            {
                if (routeCacheable)
                    cache[routeKey] = new MetaRoute(MetaRouteKind.Cut, 0);
                engine.Cut(cutBarrier);
                return SyncSuccess;
            }
            if (functorId == TrueFid)
            {
                if (routeCacheable)
                    cache[routeKey] = new MetaRoute(MetaRouteKind.True, 0);
                return SyncSuccess;
            }
            if (functorId == FailFid)
            {
                if (routeCacheable)
                    cache[routeKey] = new MetaRoute(MetaRouteKind.Fail, 0);
                return SyncFail;
            }

            // Builtin-as-goal. The recursion case (call(call(...))) is
            // handled by re-entering Dispatch with the recovered arity
            // — the inner call's X[0] already holds its own inner goal.
            if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(functorId, out int builtinId))
            {
                var builtin = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
                if (builtin.IsCall)
                {
                    // call(call(...)) — inner call's arity is the
                    // builtin's arity, barrier resets to engine.B
                    // (a fresh call boundary).
                    if (routeCacheable)
                        cache[routeKey] = new MetaRoute(MetaRouteKind.CallRecurse, builtinId);
                    return Dispatch(engine, builtin.Arity, engine.B);
                }
                if (builtin.IsDollarCall)
                {
                    if (routeCacheable)
                        cache[routeKey] = new MetaRoute(MetaRouteKind.DollarCall, builtinId);
                    int innerBarrier = (int)DerefCell(engine, engine.GetRegister(1)).AsInt;
                    return Dispatch(engine, 1, innerBarrier);
                }
                if (routeCacheable)
                    cache[routeKey] = new MetaRoute(MetaRouteKind.Builtin, builtinId);
                return InvokeBuiltinGoal(engine, builtinId);
            }

            // User predicate. Set the cut barrier the call's `!` will
            // commit to, then return the dispatch address — the IL
            // caller threads Cp = resume_marker, Pc = target,
            // IlTailCallPending = true.
            engine.SetB0(cutBarrier);
            // Module-relative resolution: a tagged goal resolves against the
            // meta-caller's module locals (module$name) first.
            if (resolutionModule >= 0 && addresses is not null)
            {
                int mangledFid = MangleFunctorId(resolutionModule, atomId, totalArity);
                if (addresses.TryGetValue(mangledFid, out int mangledAddr))
                    return mangledAddr;
                // ADR-038 — the module's import table (Source$name) before bare.
                var importMap = engine.CurrentImportMap;
                if (importMap is not null
                    && importMap.TryGetValue(
                        ((long)resolutionModule << 32) | (uint)functorId, out int importedFid)
                    && addresses.TryGetValue(importedFid, out int importedAddr))
                    return importedAddr;
            }
            if (addresses is null
                || !addresses.TryGetValue(functorId, out int address))
            {
                // Last chance: a runtime-assert MetaTransform helper linked by a
                // DIFFERENT activation — materialize it here (the Logtalk
                // suspended-outer-query shape; see ResolveLateHelper).
                int late = engine.ResolveLateHelper?.Invoke(functorId) ?? -1;
                if (late < 0)
                {
                    // honour the `unknown` flag (throws on error).
                    if (UnknownProcedure.Fails(engine, functorId))
                        return SyncFail;
                    throw PrologRuntimeException.UndefinedProcedure(functorId);   // unreachable
                }
                address = late;
            }
            if (routeCacheable)
                cache[routeKey] = new MetaRoute(userKind, address);
            return address;
        }

        /// <summary>Invokes a builtin reached as a runtime meta-call goal
        /// (shared by the slow path and the cached
        /// Builtin route).</summary>
        private static int InvokeBuiltinGoal(Activation engine, int builtinId)
        {
            var builtin = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
            engine.CurrentBuiltinName = builtin.Name;
            engine.CurrentBuiltinArity = builtin.Arity;
            try
            {
                return builtin.Impl(engine) ? SyncSuccess : SyncFail;
            }
            catch (PrologRuntimeException re)
            {
                re.StampBuiltin(builtin.Name, builtin.Arity);
                throw;
            }
        }

        /// <summary>Mirror of BytecodeInterpreter.PrepareMqualGoal: unwraps a
        /// <c>'$mqual'(Module, Goal)</c> tag on X0, distributes the module over a
        /// control construct's goal sub-args, and returns the module (or -1).</summary>
        private static int PrepareMqualGoal(Activation engine, ref Cell goal)
        {
            int module = -1;
            while (goal.Tag == Tag.Str)
            {
                int fidx = goal.AsHeapIndex;
                int fid = engine.GetHeap(fidx).AsFunctorId;
                // Both $mqual(Module, Goal) and the ISO Module:Goal qualifier share
                // the (Module, Goal) layout. `M:G` with a non-atom module is not a
                // valid qualification — leave it for the checks below.
                if (fid == MqualFid) { }
                else if (fid == ColonFid
                         && DerefCell(engine, engine.GetHeap(fidx + 1)).Tag == Tag.Atom) { }
                else break;
                Cell mCell = DerefCell(engine, engine.GetHeap(fidx + 1));
                if (mCell.Tag == Tag.Atom) module = mCell.AsAtomId;
                goal = DerefCell(engine, engine.GetHeap(fidx + 2));
            }
            if (module < 0) return -1;

            if (goal.Tag == Tag.Str)
            {
                int fid = engine.GetHeap(goal.AsHeapIndex).AsFunctorId;
                if (fid == ConjFid || fid == DisjFid || fid == ArrowFid || fid == SoftArrowFid)
                {
                    goal = DistributeMqual(engine, goal, module, arg0Goal: true, arg1Goal: true);
                    engine.SetRegister(0, goal);
                    return -1;
                }
                if (fid == NegFid || fid == NotFid)
                {
                    goal = DistributeMqual(engine, goal, module, arg0Goal: true, arg1Goal: false);
                    engine.SetRegister(0, goal);
                    return -1;
                }
            }
            engine.SetRegister(0, goal);
            return module;
        }

        private static Cell BuildMqual(Activation engine, int moduleAtomId, Cell goalCell)
        {
            int f = engine.AllocateHeap(3);
            engine.SetHeap(f, Cell.Functor(MqualFid));
            engine.SetHeap(f + 1, Cell.Atom(moduleAtomId));
            engine.SetHeap(f + 2, goalCell);
            return Cell.Str(f);
        }

        /// <summary>Mirror of BytecodeInterpreter.WrapGoal (ADR-037): distributes
        /// the module INTO an if-then-else (<c>-&gt;</c> / <c>*-&gt;</c>) rather than
        /// wrapping it whole, so the enclosing <c>;</c>'s structural if-then-else /
        /// soft-cut match still fires.</summary>
        private static Cell WrapGoal(Activation engine, int module, Cell goalCell)
        {
            Cell d = DerefCell(engine, goalCell);
            if (d.Tag == Tag.Str)
            {
                int f = engine.GetHeap(d.AsHeapIndex).AsFunctorId;
                if (f == ArrowFid || f == SoftArrowFid)
                    return DistributeMqual(engine, d, module, arg0Goal: true, arg1Goal: true);
            }
            return BuildMqual(engine, module, goalCell);
        }

        private static Cell DistributeMqual(
            Activation engine, Cell ctor, int module, bool arg0Goal, bool arg1Goal)
        {
            int src = ctor.AsHeapIndex;
            int fid = engine.GetHeap(src).AsFunctorId;
            var (_, arity) = FunctorTable.Lookup(fid);
            Cell a0 = arity > 0 ? engine.GetHeap(src + 1) : default;
            Cell a1 = arity > 1 ? engine.GetHeap(src + 2) : default;
            Cell w0 = arg0Goal && arity > 0 ? WrapGoal(engine, module, a0) : a0;
            Cell w1 = arg1Goal && arity > 1 ? WrapGoal(engine, module, a1) : a1;
            int f = engine.AllocateHeap(arity + 1);
            engine.SetHeap(f, Cell.Functor(fid));
            if (arity > 0) engine.SetHeap(f + 1, w0);
            if (arity > 1) engine.SetHeap(f + 2, w1);
            return Cell.Str(f);
        }

        private static int MangleFunctorId(int moduleAtomId, int nameAtomId, int arity)
        {
            string module = AtomTable.GetById(moduleAtomId)?.Name ?? "";
            string name = AtomTable.GetById(nameAtomId)?.Name ?? "";
            int mangledAtom = AtomTable.Intern(module + "$" + name, permanent: true).Id;
            return FunctorTable.Intern(mangledAtom, arity);
        }

        private static Cell DerefCell(Activation engine, Cell c) =>
            c.Tag == Tag.Ref ? engine.GetHeap(engine.Deref(c.AsHeapIndex)) : c;

        /// <summary>Reads <c>engine.GetRegister(reg)</c>, dereferences
        /// once if it's a <c>Tag.Ref</c>, and returns the embedded int
        /// payload. Used by the IL emit to fetch <c>$call/2</c>'s
        /// cut-barrier argument (X[1]) without the IL needing to
        /// inline the deref logic.</summary>
        public static int ReadIntRegister(Activation engine, int reg)
        {
            Cell c = engine.GetRegister(reg);
            if (c.Tag == Tag.Ref) c = engine.GetHeap(engine.Deref(c.AsHeapIndex));
            return (int)c.AsInt;
        }
    }

    /// <summary>Emits IL that loads <c>engine.GetRegister(0)</c>, derefs
    /// it if it's a REF, and leaves the resulting <see cref="Cell"/> on
    /// the evaluation stack.</summary>
    private static void EmitDerefA0(Sigil.Emit<PredicateDelegate> emit)
    {
        var a1Tmp = emit.DeclareLocal<Cell>("a1Tmp");
        var notRef = emit.DefineLabel("a1_not_ref");
        emit.LoadArgument(0);
        emit.LoadConstant(0);
        emit.Call(EngineGetRegisterMethod);
        emit.StoreLocal(a1Tmp);

        emit.LoadLocalAddress(a1Tmp);
        emit.Call(CellTagGetter);
        emit.LoadConstant((int)Tag.Ref);
        emit.UnsignedBranchIfNotEqual(notRef);

        // a1 is a REF: follow the chain. engine.GetHeap(engine.Deref(a1.AsHeapIndex)).
        emit.LoadArgument(0);
        emit.LoadArgument(0);
        emit.LoadLocalAddress(a1Tmp);
        emit.Call(CellAsHeapIndexGetter);
        emit.Call(EngineDerefMethod);
        emit.Call(EngineGetHeapMethod);
        emit.StoreLocal(a1Tmp);

        emit.MarkLabel(notRef);
        emit.LoadLocal(a1Tmp);
    }
}
