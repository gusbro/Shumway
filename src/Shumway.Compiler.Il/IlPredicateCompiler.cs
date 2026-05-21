using System.Reflection;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>
/// Tier-1 IL compiler. Translates supported WAM bytecode shapes into a
/// <see cref="PredicateDelegate"/> via Sigil's typed IL emission so the
/// promoted predicate runs without going through the bytecode dispatch
/// loop. The Tier-0/1 promotion infrastructure (counter, store,
/// dispatcher) lives in <see cref="Shumway.Embedding.IlPromotionStore"/>.
///
/// <para>Supported shapes (Phase 1):</para>
/// <list type="bullet">
/// <item><b>Single-clause facts</b> whose body uses only
///   <c>get_atom</c>, <c>get_integer</c>, <c>get_nil</c>,
///   <c>get_value_x</c>, and a trailing <c>proceed</c>.</item>
/// <item><b>Multi-clause indexed predicates</b> shaped as
///   <c>switch_on_term + switch_on_atom + per-clause bodies</c> where
///   every clause is the trivial <c>get_atom &lt;id&gt; A0 ; proceed</c>
///   form (i.e. each clause matches a distinct atom in argument 1).
///   This shape is what the WAM compiler emits for predicates like
///   <c>color(red). color(green). color(blue).</c></item>
/// </list>
///
/// <para>Predicates outside the supported subset cause
/// <see cref="CanCompile"/> to return <c>false</c>; <see cref="Compile"/>
/// throws <see cref="NotSupportedException"/>. Callers (the promotion
/// store) fall back to Tier 0 in either case.</para>
/// </summary>
public sealed class IlPredicateCompiler
{
    private static readonly MethodInfo CellAtomMethod =
        typeof(Cell).GetMethod(nameof(Cell.Atom), new[] { typeof(int) })!;
    private static readonly MethodInfo CellIntMethod =
        typeof(Cell).GetMethod(nameof(Cell.Int), new[] { typeof(long) })!;
    private static readonly MethodInfo EngineUnifyMethod =
        typeof(Engine).GetMethod(
            nameof(Engine.UnifyRegisterWithCell),
            new[] { typeof(int), typeof(Cell) })!;
    private static readonly MethodInfo EngineUnifyRegistersMethod =
        typeof(Engine).GetMethod(
            nameof(Engine.UnifyRegisters),
            new[] { typeof(int), typeof(int) })!;
    private static readonly MethodInfo EngineGetRegisterMethod =
        typeof(Engine).GetMethod(nameof(Engine.GetRegister), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineGetHeapMethod =
        typeof(Engine).GetMethod(nameof(Engine.GetHeap), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineDerefMethod =
        typeof(Engine).GetMethod(nameof(Engine.Deref), new[] { typeof(int) })!;
    private static readonly MethodInfo EnginePushIlCpMethod =
        typeof(Engine).GetMethod(
            nameof(Engine.PushIlChoicePoint),
            new[] { typeof(Func<Engine, int, bool>), typeof(int), typeof(int) })!;
    // Chunk 76 — PGO: instrumented IL calls this on each clause success.
    private static readonly MethodInfo IlProfileCountersBump =
        typeof(IlProfileCounters).GetMethod(nameof(IlProfileCounters.Bump))!;
    private static readonly MethodInfo CellTagGetter =
        typeof(Cell).GetProperty(nameof(Cell.Tag))!.GetGetMethod()!;
    private static readonly MethodInfo CellAsHeapIndexGetter =
        typeof(Cell).GetProperty(nameof(Cell.AsHeapIndex))!.GetGetMethod()!;
    private static readonly MethodInfo CellAsAtomIdGetter =
        typeof(Cell).GetProperty(nameof(Cell.AsAtomId))!.GetGetMethod()!;
    private static readonly MethodInfo EngineSetRegisterMethod =
        typeof(Engine).GetMethod(nameof(Engine.SetRegister), new[] { typeof(int), typeof(Cell) })!;
    private static readonly MethodInfo EngineGetYMethod =
        typeof(Engine).GetMethod(nameof(Engine.GetY), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineSetYMethod =
        typeof(Engine).GetMethod(nameof(Engine.SetY), new[] { typeof(int), typeof(Cell) })!;
    private static readonly MethodInfo EngineAllocateMethod =
        typeof(Engine).GetMethod(nameof(Engine.Allocate), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineDeallocateMethod =
        typeof(Engine).GetMethod(nameof(Engine.Deallocate), Type.EmptyTypes)!;
    private static readonly MethodInfo EngineNeckCutMethod =
        typeof(Engine).GetMethod(nameof(Engine.NeckCut), Type.EmptyTypes)!;
    private static readonly MethodInfo EngineSetPcMethod =
        typeof(Engine).GetMethod(
            nameof(Engine.SetPc),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(int) }, null)!;
    private static readonly MethodInfo EngineSetB0Method =
        typeof(Engine).GetMethod(
            nameof(Engine.SetB0),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(int) }, null)!;
    private static readonly MethodInfo EngineBGetter =
        typeof(Engine).GetProperty(nameof(Engine.B))!.GetGetMethod()!;
    private static readonly MethodInfo EngineIlTailCallPendingSetter =
        typeof(Engine).GetProperty(nameof(Engine.IlTailCallPending))!.GetSetMethod()!;
    private static readonly MethodInfo EngineCurrentFunctorAddressesGetter =
        typeof(Engine).GetProperty(nameof(Engine.CurrentFunctorAddresses))!.GetGetMethod()!;
    private static readonly MethodInfo IlExecuteHelperResolveMethod =
        typeof(IlExecuteHelper).GetMethod(nameof(IlExecuteHelper.Resolve))!;
    // ---------- get_structure / put_structure (chunk 48) ----------
    private static readonly MethodInfo EngineGetStructureMethod =
        typeof(Engine).GetMethod(nameof(Engine.GetStructure), new[] { typeof(int), typeof(int) })!;
    private static readonly MethodInfo EnginePutStructureMethod =
        typeof(Engine).GetMethod(nameof(Engine.PutStructure), new[] { typeof(int), typeof(int) })!;
    private static readonly MethodInfo EngineUnifyArgCellMethod =
        typeof(Engine).GetMethod(nameof(Engine.UnifyArgCell), new[] { typeof(Cell) })!;
    private static readonly MethodInfo EngineUnifyVariableXMethod =
        typeof(Engine).GetMethod(nameof(Engine.UnifyVariableX), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineUnifyValueXMethod =
        typeof(Engine).GetMethod(nameof(Engine.UnifyValueX), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineUnifyVariableYMethod =
        typeof(Engine).GetMethod(nameof(Engine.UnifyVariableY), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineUnifyValueYMethod =
        typeof(Engine).GetMethod(nameof(Engine.UnifyValueY), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineUnifyVoidMethod =
        typeof(Engine).GetMethod(nameof(Engine.UnifyVoid), new[] { typeof(int) })!;
    // ---------- get_list / put_list / pstr (chunk 49) ----------
    private static readonly MethodInfo EngineGetListMethod =
        typeof(Engine).GetMethod(nameof(Engine.GetList), new[] { typeof(int) })!;
    private static readonly MethodInfo EnginePutListMethod =
        typeof(Engine).GetMethod(nameof(Engine.PutList), new[] { typeof(int) })!;
    private static readonly MethodInfo EngineMakePstrMethod =
        typeof(Engine).GetMethod(nameof(Engine.MakePstr), new[] { typeof(string) })!;
    private static readonly MethodInfo EngineUnifyRegisterWithHeapAtMethod =
        typeof(Engine).GetMethod(
            nameof(Engine.UnifyRegisterWithHeapAt),
            new[] { typeof(int), typeof(int) })!;
    private static readonly MethodInfo IlGetPstrHelperMethod =
        typeof(IlRuntimeHelpers).GetMethod(nameof(IlRuntimeHelpers.GetPstr))!;
    private static readonly MethodInfo IlPutPstrHelperMethod =
        typeof(IlRuntimeHelpers).GetMethod(nameof(IlRuntimeHelpers.PutPstr))!;
    private static readonly MethodInfo IlCallHelperRunMethod =
        typeof(IlRuntimeHelpers).GetMethod(nameof(IlRuntimeHelpers.Call))!;
    // Meta-CP support (chunk 66): drive a backtrack from an IL
    // delegate's resume path to fetch the next solution from a
    // non-leaf callee.
    private static readonly MethodInfo IlRunBacktrackHelperMethod =
        typeof(IlRuntimeHelpers).GetMethod(nameof(IlRuntimeHelpers.RunBacktrack))!;
    private static readonly MethodInfo IlReadPreCallBHelperMethod =
        typeof(IlRuntimeHelpers).GetMethod(nameof(IlRuntimeHelpers.ReadPreCallB))!;
    private static readonly MethodInfo EngineAllocateHeapUnboundMethod =
        typeof(Engine).GetMethod(nameof(Engine.AllocateHeapUnbound), Type.EmptyTypes)!;
    private static readonly MethodInfo CellRefMethod =
        typeof(Cell).GetMethod(nameof(Cell.Ref), new[] { typeof(int) })!;
    private static readonly MethodInfo BuiltinsRegistryGetByIdMethod =
        typeof(Shumway.Builtins.BuiltinsRegistry).GetMethod(
            nameof(Shumway.Builtins.BuiltinsRegistry.GetById),
            new[] { typeof(int) })!;
    private static readonly MethodInfo BuiltinEntryImplGetter =
        typeof(Shumway.Builtins.BuiltinEntry).GetProperty(
            nameof(Shumway.Builtins.BuiltinEntry.Impl))!.GetGetMethod()!;
    private static readonly MethodInfo BuiltinImplInvokeMethod =
        typeof(Shumway.Builtins.BuiltinImpl).GetMethod(
            nameof(Shumway.Builtins.BuiltinImpl.Invoke))!;

    /// <summary>Returns <c>true</c> iff <paramref name="predicate"/> is in
    /// the supported subset. See the class docstring for the catalog.
    /// <paramref name="calleeMap"/> (chunk 50) lets the check inspect
    /// <c>Call</c> targets — an IL <c>Call</c> only compiles when the
    /// callee is itself a "leaf" predicate (single-clause, body-less,
    /// only head matching + proceed), so the synchronous sub-call can
    /// never push choice points that would survive past the IL caller.</summary>
    public bool CanCompile(CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (predicate.ClauseCount == 1) return CanCompileSingleClause(predicate, calleeMap);
        if (TryDescribeIndexedAtomPredicate(predicate, out _)) return true;
        return TryDescribeTryMeElseChain(predicate, calleeMap, out _);
    }

    /// <summary>Emits a <see cref="PredicateDelegate"/> for the predicate.
    /// The caller is responsible for first checking
    /// <see cref="CanCompile"/>; passing in an unsupported predicate
    /// throws <see cref="NotSupportedException"/>.</summary>
    public PredicateDelegate Compile(CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (predicate.ClauseCount == 1)
        {
            if (!CanCompileSingleClause(predicate, calleeMap))
                throw new NotSupportedException(
                    $"Single-clause predicate (fid={predicate.FunctorId}) is outside the IL subset.");
            return CompileSingleClause(predicate, calleeMap);
        }
        if (TryDescribeIndexedAtomPredicate(predicate, out var info))
            return CompileIndexedAtomPredicate(predicate, info!);
        if (TryDescribeTryMeElseChain(predicate, calleeMap, out var chain))
            return CompileTryMeElseChain(predicate, chain!, calleeMap);
        throw new NotSupportedException(
            $"Multi-clause predicate (fid={predicate.FunctorId}, clauses={predicate.ClauseCount}) "
            + "is outside the IL subset.");
    }

    // ============================================================================
    // Chunk 76 — PGO: two-phase profile-guided IL compilation
    // ============================================================================

    /// <summary>Profile key counter — allocated per instrumented
    /// predicate, indexing <see cref="IlProfileCounters"/>. Separate
    /// namespace from <see cref="_nextHolderKey"/>.</summary>
    private static int _nextProfileKey = 1;

    /// <summary>Result of a phase-1 PGO compile: the (instrumented)
    /// delegate plus the profile key the engine later passes to
    /// <see cref="CompileOptimized"/>. A <see cref="ProfileKey"/> of
    /// <c>-1</c> means the predicate's shape isn't PGO-eligible — it
    /// was compiled normally and no phase-2 recompile should fire.</summary>
    public readonly record struct PgoCompileResult(
        PredicateDelegate Delegate, int ProfileKey);

    /// <summary>Phase-1 PGO compile. For the indexed-atom shape this
    /// emits the <em>instrumented</em> form whose ground dispatch
    /// records which atom matched; for every other shape it's an
    /// ordinary <see cref="Compile"/> with <see cref="PgoCompileResult.ProfileKey"/>
    /// set to <c>-1</c>.</summary>
    public PgoCompileResult CompileInstrumented(
        CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (predicate.ClauseCount > 1
            && TryDescribeIndexedAtomPredicate(predicate, out var info))
        {
            lock (IndexedDelegateHolder.RegistrationLock)
            {
                int profileKey = _nextProfileKey++;
                IlProfileCounters.Allocate(profileKey, info!.Clauses.Count);
                var del = CompileIndexedAtomPredicateUnlocked(
                    predicate, info, profileKey, groundOrder: null);
                return new PgoCompileResult(del, profileKey);
            }
        }
        return new PgoCompileResult(Compile(predicate, calleeMap), -1);
    }

    /// <summary>Phase-2 PGO compile. Reads the hit counts accumulated
    /// under <paramref name="profileKey"/> and recompiles the
    /// indexed-atom predicate with the ground-dispatch <c>cmp</c> chain
    /// ordered most-frequently-matched-atom first. Releases the profile
    /// counters afterwards. Falls back to a plain compile when the
    /// shape isn't indexed-atom (defensive — the engine only calls this
    /// for keys produced by an indexed-atom phase 1).</summary>
    public PredicateDelegate CompileOptimized(
        CompiledPredicate predicate, int profileKey,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (profileKey < 0
            || !TryDescribeIndexedAtomPredicate(predicate, out var info))
        {
            return Compile(predicate, calleeMap);
        }
        long[]? counts = IlProfileCounters.Get(profileKey);
        int n = info!.Clauses.Count;
        var order = Enumerable.Range(0, n).ToArray();
        if (counts is not null)
        {
            // Descending by hit count; Array.Sort isn't stable but ties
            // among equally-cold atoms don't matter.
            Array.Sort(order, (a, b) => counts[b].CompareTo(counts[a]));
        }
        lock (IndexedDelegateHolder.RegistrationLock)
        {
            var del = CompileIndexedAtomPredicateUnlocked(
                predicate, info, profileKey: -1, groundOrder: order);
            IlProfileCounters.Release(profileKey);
            return del;
        }
    }

    // ============================================================================
    // Shape 1: single-clause facts
    // ============================================================================

    private static bool CanCompileSingleClause(CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        byte[] code = predicate.Bytecode;
        int pc = 0;
        bool sawTerminator = false;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.Execute)
            {
                // Execute is a body-tail terminator: control transfers
                // to the callee, which proceeds back to our caller's
                // continuation. The IL emission for Execute returns
                // from the delegate (with the IlTailCallPending flag
                // set), so any opcodes after it in the bytecode are
                // unreachable.
                sawTerminator = true;
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.Call)
            {
                // Non-tail Call: chunk 66 emits a meta-CP at every IL
                // Call site that drives Engine.BacktrackRunner on
                // resume to retry callee alternatives and rejoin the
                // body at a post-call cursor. No leaf restriction
                // needed — just confirm we have a calleeMap entry so
                // the runtime can resolve the functor.
                if (calleeMap is null) return false;
                int siteFid = FindCallSiteFunctorId(predicate.CallSites, pc);
                if (siteFid < 0) return false;
                if (!calleeMap.TryGetValue(siteFid, out _)) return false;
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.CallBuiltin)
            {
                // call/1..7 needs the interpreter's runtime goal dispatch
                // (chunk 86); the IL builtin-invoke path would bypass it
                // and fall back to the once-semantics builtin. Keep a
                // call-bearing clause in Tier 0.
                int builtinId = BytecodeIO.ReadInt32(code, pc + 1);
                if (Shumway.Builtins.BuiltinsRegistry.GetById(builtinId).Name == "call")
                    return false;
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (IsSupportedOpcode(op))
            {
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.Proceed)
            {
                sawTerminator = true;
                pc += 1;
                continue;
            }
            return false;
        }
        return sawTerminator;
    }

    private static int FindCallSiteFunctorId(
        IReadOnlyList<CallSite> sites, int opcodeOffset)
    {
        for (int i = 0; i < sites.Count; i++)
            if (sites[i].OpcodeOffset == opcodeOffset) return sites[i].CalleeFunctorId;
        return -1;
    }

    /// <summary>A "leaf" predicate is a single-clause predicate whose
    /// body is purely head matching + a trailing proceed — no body
    /// calls, no cut, no allocate. Calling it can't push choice points
    /// (no try_me_else) and can't escape with a tail call (no Execute
    /// / Call). The IL <c>Call</c> emission relies on this so the
    /// sub-call always runs to completion in one shot.</summary>
    private static bool IsLeafPredicate(CompiledPredicate pred)
    {
        if (pred.ClauseCount != 1) return false;
        byte[] code = pred.Bytecode;
        int pc = 0;
        bool sawProceed = false;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.Proceed) { sawProceed = true; pc += 1; continue; }
            if (IsHeadMatchingOpcode(op))
            {
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            return false;
        }
        return sawProceed;
    }

    private static bool IsHeadMatchingOpcode(Opcode op) => op switch
    {
        Opcode.GetAtom => true,
        Opcode.GetInteger => true,
        Opcode.GetNil => true,
        Opcode.GetValueX => true,
        Opcode.GetVariableX => true,
        Opcode.GetStructure => true,
        Opcode.GetList => true,
        Opcode.UnifyAtom => true,
        Opcode.UnifyInteger => true,
        Opcode.UnifyNil => true,
        Opcode.UnifyVariableX => true,
        Opcode.UnifyValueX => true,
        Opcode.UnifyVoid => true,
        _ => false,
    };

    /// <summary>Catalog of opcodes that <see cref="EmitClauseBody"/>
    /// knows how to translate to IL. Excludes the control-flow tail
    /// (<c>proceed</c>), which is handled inline by the emit loop.</summary>
    private static bool IsSupportedOpcode(Opcode op) => op switch
    {
        // Head matching.
        Opcode.GetAtom => true,
        Opcode.GetInteger => true,
        Opcode.GetNil => true,
        Opcode.GetValueX => true,
        Opcode.GetVariableX => true,
        Opcode.GetVariableY => true,
        Opcode.GetValueY => true,
        // Body argument setup.
        Opcode.PutAtom => true,
        Opcode.PutInteger => true,
        Opcode.PutNil => true,
        Opcode.PutValueX => true,
        Opcode.PutValueY => true,
        Opcode.PutVariableX => true,
        Opcode.PutVariableY => true,
        // Body control.
        Opcode.CallBuiltin => true,
        Opcode.Allocate => true,
        Opcode.Deallocate => true,
        Opcode.NeckCut => true,
        Opcode.Execute => true,
        // Compound argument structure (chunk 48).
        Opcode.GetStructure => true,
        Opcode.PutStructure => true,
        Opcode.UnifyAtom => true,
        Opcode.UnifyInteger => true,
        Opcode.UnifyNil => true,
        Opcode.UnifyVariableX => true,
        Opcode.UnifyValueX => true,
        Opcode.UnifyVariableY => true,
        Opcode.UnifyValueY => true,
        Opcode.UnifyVoid => true,
        // List head matching (chunk 49).
        Opcode.GetList => true,
        Opcode.PutList => true,
        // PSTR + Call (chunk 50).
        Opcode.GetPstr => true,
        Opcode.PutPstr => true,
        Opcode.Call => true,
        // Meta dbg_info (chunk 55) — pure compile-time metadata; the
        // emit path skips it without producing any IL.
        Opcode.Meta => true,
        _ => false,
    };

    private PredicateDelegate CompileSingleClause(CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        int callSiteCount = CountNonTailCallOpcodes(predicate.Bytecode);
        if (callSiteCount == 0)
        {
            // No meta-CP needed: pure head match + tail call (or no body).
            var emit = Sigil.Emit<PredicateDelegate>.NewDynamicMethod(
                $"ShumwayIl_{predicate.FunctorId}_{predicate.Arity}");
            EmitSingleClauseLeafBody(emit, predicate, calleeMap);
            return emit.CreateDelegate();
        }
        lock (IndexedDelegateHolder.RegistrationLock)
            return CompileSingleClauseWithMetaCpUnlocked(predicate, callSiteCount, calleeMap);
    }

    /// <summary>The shared single-clause-leaf body emit used by both the
    /// runtime path (<see cref="CompileSingleClause"/>, which builds a
    /// <c>DynamicMethod</c>) and the chunk-71 persisted-assembly path
    /// (<see cref="EmitToMethodBuilder"/>, which builds a static method
    /// on a <see cref="System.Reflection.Emit.TypeBuilder"/>). Pure head
    /// match + optional tail call, no IL choice points, no
    /// self-reference into <see cref="IndexedDelegateHolder"/>.</summary>
    private static void EmitSingleClauseLeafBody(
        Sigil.Emit<PredicateDelegate> emit,
        CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        var failLabel = emit.DefineLabel("fail");
        EmitClauseBody(emit, predicate.Bytecode, 0, predicate.Bytecode.Length,
            failLabel, predicate.CallSites,
            callSiteIndexCounter: null, resumeLabels: null,
            calleeMap: calleeMap);
        emit.MarkLabel(failLabel);
        emit.LoadConstant(false);
        emit.Return();
    }

    /// <summary>Chunk 71: defines a static method named
    /// <paramref name="methodName"/> on <paramref name="typeBuilder"/>
    /// and emits the predicate's IL into it. Returns the
    /// <c>MethodBuilder</c> so the caller can later bake the type and
    /// resolve the method via reflection.
    ///
    /// <para>Routes to the right emission shape based on the predicate:
    /// single-clause-leaf (no IL CPs), single-clause-with-meta-CP,
    /// indexed-atom, or general try-me-else chain. The latter three
    /// need a static self-reference — they read their own delegate
    /// from <paramref name="delegatesField"/>[<paramref name="slot"/>],
    /// which the loader populates at runtime from the same method's
    /// <c>MethodInfo.CreateDelegate</c>.</para>
    ///
    /// <para><paramref name="delegatesField"/> and
    /// <paramref name="slot"/> are unused (and may be passed as
    /// <c>null</c> / <c>-1</c>) for the leaf shape, which never emits
    /// an IL CP push.</para></summary>
    public System.Reflection.Emit.MethodBuilder EmitPersistedMethod(
        System.Reflection.Emit.TypeBuilder typeBuilder,
        string methodName,
        CompiledPredicate predicate,
        System.Reflection.FieldInfo? delegatesField,
        int slot,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        ArgumentNullException.ThrowIfNull(typeBuilder);
        ArgumentNullException.ThrowIfNull(methodName);
        ArgumentNullException.ThrowIfNull(predicate);

        var emit = Sigil.Emit<PredicateDelegate>.BuildMethod(
            typeBuilder,
            methodName,
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            System.Reflection.CallingConventions.Standard);

        SelfDelegateEmitter? emitSelf = delegatesField is null
            ? null
            : SelfFromArrayField(delegatesField, slot);

        if (predicate.ClauseCount == 1)
        {
            int callSiteCount = CountNonTailCallOpcodes(predicate.Bytecode);
            if (callSiteCount == 0)
            {
                EmitSingleClauseLeafBody(emit, predicate, calleeMap);
            }
            else
            {
                if (emitSelf is null)
                    throw new InvalidOperationException(
                        "Single-clause meta-CP predicate needs a delegates field for self-reference.");
                EmitSingleClauseMetaCpBody(emit, predicate, callSiteCount, calleeMap, emitSelf);
            }
        }
        else if (TryDescribeIndexedAtomPredicate(predicate, out var atomInfo))
        {
            if (emitSelf is null)
                throw new InvalidOperationException(
                    "Indexed-atom predicate needs a delegates field for self-reference.");
            EmitIndexedAtomBody(emit, predicate, atomInfo!, emitSelf);
        }
        else if (TryDescribeTryMeElseChain(predicate, calleeMap, out var chainInfo))
        {
            if (emitSelf is null)
                throw new InvalidOperationException(
                    "Try-me-else chain predicate needs a delegates field for self-reference.");
            EmitTryMeElseChainBody(emit, predicate, chainInfo!, calleeMap, emitSelf);
        }
        else
        {
            throw new NotSupportedException(
                $"Predicate (fid={predicate.FunctorId}, clauses={predicate.ClauseCount}) "
                + "is outside the IL subset.");
        }

        return emit.CreateMethod();
    }

    private PredicateDelegate CompileSingleClauseWithMetaCpUnlocked(
        CompiledPredicate predicate, int callSiteCount,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        int holderKey = _nextHolderKey;
        var emitSelf = SelfFromHolder(holderKey);
        var emit = Sigil.Emit<PredicateDelegate>.NewDynamicMethod(
            $"ShumwayIl_metacp_{predicate.FunctorId}_{predicate.Arity}");
        EmitSingleClauseMetaCpBody(emit, predicate, callSiteCount, calleeMap, emitSelf);
        var del = emit.CreateDelegate();
        IndexedDelegateHolder.Register(holderKey, del);
        _nextHolderKey = holderKey + 1;
        return del;
    }

    /// <summary>Shared meta-CP body emitter — used by both the
    /// DynamicMethod path (above) and the persisted path. The
    /// self-reference for re-pushing the meta-CP on each retry routes
    /// through <paramref name="emitSelf"/>.</summary>
    private static void EmitSingleClauseMetaCpBody(
        Sigil.Emit<PredicateDelegate> emit,
        CompiledPredicate predicate,
        int callSiteCount,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        SelfDelegateEmitter emitSelf)
    {
        var failLabel = emit.DefineLabel("fail");
        var startLabel = emit.DefineLabel("start");
        var resumeLabels = new Sigil.Label[callSiteCount];
        var postCallLabels = new Sigil.Label[callSiteCount];
        for (int i = 0; i < callSiteCount; i++)
        {
            resumeLabels[i] = emit.DefineLabel($"resume_{i + 1}");
            postCallLabels[i] = emit.DefineLabel($"post_call_{i + 1}");
        }

        // Cursor dispatch: 0 → start; N → resume_N.
        for (int i = 0; i < callSiteCount; i++)
        {
            emit.LoadArgument(1);
            emit.LoadConstant(i + 1);
            emit.BranchIfEqual(resumeLabels[i]);
        }
        emit.Branch(startLabel);

        // Resume bodies: read preCallB from X[0] (saved by the popped
        // meta-CP's arity=1 slot), drive a backtrack on the callee.
        // If the backtrack cascaded past our own Call site (engine.B
        // ended up at or below preCallB), the new solution was
        // produced by an outer CP — set IlTailCallPending so the
        // interpreter resumes at whatever Pc the cascade ended at,
        // and return true to propagate. Otherwise our callee still
        // has CPs to retry; re-push a fresh meta-CP, rejoin the body
        // at post_call_N.
        for (int i = 0; i < callSiteCount; i++)
        {
            emit.MarkLabel(resumeLabels[i]);
            var preCallBLocal = emit.DeclareLocal<int>($"preCallB_resume_{i + 1}");
            emit.LoadArgument(0);
            emit.Call(IlReadPreCallBHelperMethod);
            emit.StoreLocal(preCallBLocal);
            emit.LoadArgument(0);
            emit.Call(IlRunBacktrackHelperMethod);
            emit.BranchIfFalse(failLabel);
            // Three-way branch on (engine.B vs preCallB):
            //   B  >  preCallB → callee CPs still alive: re-push meta-CP
            //                    and rejoin body at post_call_N.
            //   B  == preCallB → callee just consumed its last CP via
            //                    trust_me: solution is valid, but no
            //                    meta-CP re-push (no more alternatives).
            //                    Rejoin body at post_call_N anyway.
            //   B  <  preCallB → cascade: an outer CP fired below our
            //                    Call site. Set IlTailCallPending and
            //                    propagate up.
            var cascadeLabel = emit.DefineLabel($"resume_{i + 1}_cascade");
            var noRePushLabel = emit.DefineLabel($"resume_{i + 1}_no_repush");
            emit.LoadArgument(0);
            emit.Call(EngineBGetter);
            emit.LoadLocal(preCallBLocal);
            emit.BranchIfLess(cascadeLabel);
            emit.LoadArgument(0);
            emit.Call(EngineBGetter);
            emit.LoadLocal(preCallBLocal);
            emit.BranchIfEqual(noRePushLabel);
            // B > preCallB: callee CPs survive; re-push meta-CP.
            emit.LoadArgument(0);
            emit.LoadConstant(0);
            emit.LoadLocal(preCallBLocal);
            emit.Convert<long>();
            emit.Call(CellIntMethod);
            emit.Call(EngineSetRegisterMethod);
            emit.LoadArgument(0);
            emitSelf(emit);
            emit.LoadConstant(i + 1);
            emit.LoadConstant(1);
            emit.Call(EnginePushIlCpMethod);
            emit.Branch(postCallLabels[i]);
            // B == preCallB: rejoin body without re-pushing meta-CP.
            emit.MarkLabel(noRePushLabel);
            emit.Branch(postCallLabels[i]);
            // B < preCallB: cascade. An outer CP produced the solution.
            // Signal tail-call so the interpreter keeps Pc where the
            // cascade halted (sentinel-Cp halt or otherwise).
            emit.MarkLabel(cascadeLabel);
            emit.LoadArgument(0);
            emit.LoadConstant(true);
            emit.Call(EngineIlTailCallPendingSetter);
            emit.LoadConstant(true);
            emit.Return();
        }

        emit.MarkLabel(startLabel);
        int idxCounter = 0;
        EmitClauseBody(emit, predicate.Bytecode, 0, predicate.Bytecode.Length,
            failLabel, predicate.CallSites,
            callSiteIndexCounter: () => ++idxCounter,
            resumeLabels: postCallLabels,
            emitSelfDelegate: emitSelf,
            calleeMap: calleeMap);

        emit.MarkLabel(failLabel);
        emit.LoadConstant(false);
        emit.Return();
    }

    /// <summary>Counts non-tail <c>Call</c> opcodes in a clause's
    /// bytecode (Opcode.Call only — Opcode.Execute is the tail-call
    /// form and doesn't need a meta-CP).</summary>
    private static int CountNonTailCallOpcodes(byte[] bytecode)
    {
        int count = 0;
        int pc = 0;
        while (pc < bytecode.Length)
        {
            byte b = bytecode[pc];
            if (b == (byte)Opcode.Call) count++;
            var info = OpcodeTable.Get(b);
            if (!info.IsDefined || info.Size == 0) break;
            pc += info.Size;
        }
        return count;
    }

    /// <summary>Emits IL for a contiguous span of supported-opcode
    /// clause-body bytes. <paramref name="failLabel"/> is jumped to on any
    /// unification failure; a successful <c>proceed</c> emits an inline
    /// <c>return true</c>. <paramref name="callSites"/> is consulted by
    /// the Execute emission to resolve each call site's callee functor
    /// id (which is stable across queries, unlike the absolute bytecode
    /// address embedded in the operand).
    ///
    /// <para><paramref name="calleeMap"/> turns on chunk-69 inlining of
    /// small leaf callees: when a Call or Execute site references a
    /// predicate that's in the map and passes <see cref="IsLeafPredicate"/>,
    /// the callee's body opcodes are emitted directly into the caller's
    /// IL stream instead of going through the
    /// <see cref="IlRuntimeHelpers.Call"/> / <c>IlExecuteHelper.Resolve</c>
    /// thunk. Saves a managed call, a Pc-set, and the bytecode-interpreter
    /// re-entry per call site.</para>
    /// <para><paramref name="suppressProceedReturn"/> applies inside the
    /// inlined-Call case: the callee's <c>proceed</c> becomes a fall-through
    /// (the caller has more body to execute after the inlined block)
    /// instead of <c>return true</c>.</para></summary>
    private static void EmitClauseBody(
        Sigil.Emit<PredicateDelegate> emit, byte[] code, int start, int end,
        Sigil.Label failLabel, IReadOnlyList<CallSite> callSites,
        Func<int>? callSiteIndexCounter = null,
        Sigil.Label[]? resumeLabels = null,
        SelfDelegateEmitter? emitSelfDelegate = null,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null,
        bool suppressProceedReturn = false)
    {
        int pc = start;
        while (pc < end)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.Meta)
            {
                // Dbg-info Meta opcode (chunk 55) — runtime no-op. Skip
                // the 6 bytes (opcode + sub-byte + 4-byte payload) without
                // emitting any IL.
                pc += 6;
                continue;
            }
            if (op == Opcode.GetAtom)
            {
                int atomId = BytecodeIO.ReadInt32(code, pc + 1);
                int regIdx = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(regIdx);
                emit.LoadConstant(atomId);
                emit.Call(CellAtomMethod);
                emit.Call(EngineUnifyMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetInteger)
            {
                int value = BytecodeIO.ReadInt32(code, pc + 1);
                int regIdx = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(regIdx);
                emit.LoadConstant((long)value);
                emit.Call(CellIntMethod);
                emit.Call(EngineUnifyMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetNil)
            {
                int regIdx = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(regIdx);
                emit.LoadConstant(AtomTable.EmptyListId);
                emit.Call(CellAtomMethod);
                emit.Call(EngineUnifyMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetValueX)
            {
                int srcReg = BytecodeIO.ReadInt32(code, pc + 1);
                int argReg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(srcReg);
                emit.LoadConstant(argReg);
                emit.Call(EngineUnifyRegistersMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetVariableX)
            {
                // X[dest] := X[arg]
                int dest = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(dest);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.Call(EngineGetRegisterMethod);
                emit.Call(EngineSetRegisterMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetVariableY)
            {
                // Y[slot] := X[arg]
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.Call(EngineGetRegisterMethod);
                emit.Call(EngineSetYMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetValueY)
            {
                // unify(Y[slot], X[arg])
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.Call(EngineGetYMethod);
                emit.Call(EngineUnifyMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutAtom)
            {
                int atomId = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.LoadConstant(atomId);
                emit.Call(CellAtomMethod);
                emit.Call(EngineSetRegisterMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutInteger)
            {
                int value = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.LoadConstant((long)value);
                emit.Call(CellIntMethod);
                emit.Call(EngineSetRegisterMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutNil)
            {
                int arg = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.LoadConstant(AtomTable.EmptyListId);
                emit.Call(CellAtomMethod);
                emit.Call(EngineSetRegisterMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutValueX)
            {
                // X[arg] := X[src]
                int src = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.LoadArgument(0);
                emit.LoadConstant(src);
                emit.Call(EngineGetRegisterMethod);
                emit.Call(EngineSetRegisterMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutValueY)
            {
                // X[arg] := Y[slot]
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.Call(EngineGetYMethod);
                emit.Call(EngineSetRegisterMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutVariableX)
            {
                // X[arg] := X[dest] := Cell.Ref(engine.AllocateHeapUnbound())
                int dest = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                // Allocate fresh unbound, save its REF cell in a local, then
                // assign it to both X[dest] and X[arg].
                var refLocal = emit.DeclareLocal<Cell>($"freshRef_pc{pc}");
                emit.LoadArgument(0);
                emit.Call(EngineAllocateHeapUnboundMethod);
                emit.Call(CellRefMethod);
                emit.StoreLocal(refLocal);
                // X[dest] = local
                emit.LoadArgument(0);
                emit.LoadConstant(dest);
                emit.LoadLocal(refLocal);
                emit.Call(EngineSetRegisterMethod);
                // X[arg] = local
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.LoadLocal(refLocal);
                emit.Call(EngineSetRegisterMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutVariableY)
            {
                // Y[slot] := X[arg] := Cell.Ref(engine.AllocateHeapUnbound())
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                var refLocal = emit.DeclareLocal<Cell>($"freshRefY_pc{pc}");
                emit.LoadArgument(0);
                emit.Call(EngineAllocateHeapUnboundMethod);
                emit.Call(CellRefMethod);
                emit.StoreLocal(refLocal);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.LoadLocal(refLocal);
                emit.Call(EngineSetYMethod);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.LoadLocal(refLocal);
                emit.Call(EngineSetRegisterMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.Allocate)
            {
                int n = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(n);
                emit.Call(EngineAllocateMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.Deallocate)
            {
                emit.LoadArgument(0);
                emit.Call(EngineDeallocateMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.NeckCut)
            {
                emit.LoadArgument(0);
                emit.Call(EngineNeckCutMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.CallBuiltin)
            {
                // entry = BuiltinsRegistry.GetById(id)
                // if (!entry.Impl(engine)) goto fail
                int builtinId = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadConstant(builtinId);
                emit.Call(BuiltinsRegistryGetByIdMethod);
                emit.Call(BuiltinEntryImplGetter);
                emit.LoadArgument(0);
                emit.Call(BuiltinImplInvokeMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetStructure)
            {
                int functorId = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(functorId);
                emit.LoadConstant(arg);
                emit.Call(EngineGetStructureMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutStructure)
            {
                int functorId = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(functorId);
                emit.LoadConstant(arg);
                emit.Call(EnginePutStructureMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.UnifyAtom)
            {
                int atomId = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(atomId);
                emit.Call(CellAtomMethod);
                emit.Call(EngineUnifyArgCellMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.UnifyInteger)
            {
                int value = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant((long)value);
                emit.Call(CellIntMethod);
                emit.Call(EngineUnifyArgCellMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.UnifyNil)
            {
                emit.LoadArgument(0);
                emit.LoadConstant(AtomTable.EmptyListId);
                emit.Call(CellAtomMethod);
                emit.Call(EngineUnifyArgCellMethod);
                emit.BranchIfFalse(failLabel);
                pc += 1;
                continue;
            }
            if (op == Opcode.UnifyVariableX)
            {
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.Call(EngineUnifyVariableXMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.UnifyValueX)
            {
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.Call(EngineUnifyValueXMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.UnifyVariableY)
            {
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.Call(EngineUnifyVariableYMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.UnifyValueY)
            {
                int slot = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(slot);
                emit.Call(EngineUnifyValueYMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.UnifyVoid)
            {
                int count = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(count);
                emit.Call(EngineUnifyVoidMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetList)
            {
                int arg = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.Call(EngineGetListMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutList)
            {
                int arg = BytecodeIO.ReadInt32(code, pc + 1);
                emit.LoadArgument(0);
                emit.LoadConstant(arg);
                emit.Call(EnginePutListMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetPstr)
            {
                int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(literalId);
                emit.LoadConstant(arg);
                emit.Call(IlGetPstrHelperMethod);
                emit.BranchIfFalse(failLabel);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.PutPstr)
            {
                int literalId = BytecodeIO.ReadInt32(code, pc + 1);
                int arg = BytecodeIO.ReadInt32(code, pc + 5);
                emit.LoadArgument(0);
                emit.LoadConstant(literalId);
                emit.LoadConstant(arg);
                emit.Call(IlPutPstrHelperMethod);
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.Call)
            {
                // Non-tail Call. With chunk 66 the IL site captures
                // engine.B (preCallB) before invoking the sub-call
                // helper, then on success pushes a meta-CP that saves
                // preCallB as Cell.Int(preCallB) in arity-1 of the
                // CP frame. On backtrack the resume path reads
                // preCallB back, drives Engine.BacktrackRunner to
                // fetch the callee's next solution, and re-enters
                // the body at the post-call label.
                int siteFunctorId = -1;
                for (int i = 0; i < callSites.Count; i++)
                {
                    if (callSites[i].OpcodeOffset == pc)
                    {
                        siteFunctorId = callSites[i].CalleeFunctorId;
                        break;
                    }
                }
                if (siteFunctorId < 0)
                    throw new InvalidOperationException(
                        $"Call opcode at pc={pc} has no matching call site in the predicate's metadata.");

                // Inlining (chunk 69): if the callee is a small static
                // leaf, emit its body opcodes inline instead of routing
                // through IlCallHelper.Run. Leaves push no CPs so no
                // meta-CP is needed; the post-call label still gets
                // marked for any outer logic but no choice point lives
                // there.
                if (calleeMap is not null
                    && calleeMap.TryGetValue(siteFunctorId, out var calleePred)
                    && IsLeafPredicate(calleePred))
                {
                    EmitClauseBody(emit, calleePred.Bytecode, 0, calleePred.Bytecode.Length,
                        failLabel, Array.Empty<CallSite>(),
                        calleeMap: calleeMap, suppressProceedReturn: true);
                    if (callSiteIndexCounter is not null && resumeLabels is not null)
                    {
                        int siteIdx = callSiteIndexCounter();
                        // Leaves leave no CPs behind so the meta-CP guard would
                        // have skipped the push anyway. Mark the resume label
                        // so any outer cascade logic still has a join point,
                        // but emit no CP-push machinery.
                        emit.MarkLabel(resumeLabels[siteIdx - 1]);
                    }
                    pc += OpcodeTable.Get(op).Size;
                    continue;
                }

                // preCallB = engine.B;
                var preCallBLocal = emit.DeclareLocal<int>($"preCallB_{pc}");
                emit.LoadArgument(0);
                emit.Call(EngineBGetter);
                emit.StoreLocal(preCallBLocal);

                // engine.SetB0(engine.B);
                emit.LoadArgument(0);
                emit.LoadArgument(0);
                emit.Call(EngineBGetter);
                emit.Call(EngineSetB0Method);

                // bool ok = IlCallHelper.Run(engine, siteFunctorId);
                emit.LoadArgument(0);
                emit.LoadConstant(siteFunctorId);
                emit.Call(IlCallHelperRunMethod);
                emit.BranchIfFalse(failLabel);

                if (callSiteIndexCounter is not null && resumeLabels is not null)
                {
                    int siteIdx = callSiteIndexCounter();
                    var skipPushLabel = emit.DefineLabel($"skip_metacp_{siteIdx}");
                    // if (engine.B <= preCallB) goto skip; (no leftover CPs)
                    // Signed comparison: preCallB can be -1 when no CPs
                    // existed pre-call, and unsigned would treat that as
                    // a huge value and always branch.
                    emit.LoadArgument(0);
                    emit.Call(EngineBGetter);
                    emit.LoadLocal(preCallBLocal);
                    emit.BranchIfLessOrEqual(skipPushLabel);

                    // engine.SetRegister(0, Cell.Int(preCallB));
                    emit.LoadArgument(0);
                    emit.LoadConstant(0);
                    emit.LoadLocal(preCallBLocal);
                    emit.Convert<long>();
                    emit.Call(CellIntMethod);
                    emit.Call(EngineSetRegisterMethod);

                    // engine.PushIlChoicePoint(self, cursor=siteIdx, arity=1)
                    emit.LoadArgument(0);
                    emitSelfDelegate!(emit);
                    emit.LoadConstant(siteIdx);
                    emit.LoadConstant(1);
                    emit.Call(EnginePushIlCpMethod);

                    emit.MarkLabel(skipPushLabel);
                    emit.MarkLabel(resumeLabels[siteIdx - 1]);
                }
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.Execute)
            {
                // Tail call. The operand in the bytecode is a per-query
                // resolved address that's only valid for the link that
                // produced it; if we cached this delegate and the engine
                // re-links the program for a later query, the address
                // would point at the wrong place. So instead we look up
                // the callee's address via the engine's current functor
                // address map (set per query) using the stable functor
                // id from the call site metadata.
                int siteFunctorId = -1;
                for (int i = 0; i < callSites.Count; i++)
                {
                    if (callSites[i].OpcodeOffset == pc)
                    {
                        siteFunctorId = callSites[i].CalleeFunctorId;
                        break;
                    }
                }
                if (siteFunctorId < 0)
                    throw new InvalidOperationException(
                        $"Execute opcode at pc={pc} has no matching call site in the predicate's metadata.");

                // Inlining (chunk 69): if the callee is a small static
                // leaf, emit its body opcodes inline instead of going
                // through the Pc-set / IlTailCallPending / outer-
                // dispatch dance. The callee's own proceed (= return
                // true) is exactly what the caller needs at the
                // tail-call site, so suppressProceedReturn stays false.
                if (calleeMap is not null
                    && calleeMap.TryGetValue(siteFunctorId, out var calleePredX)
                    && IsLeafPredicate(calleePredX))
                {
                    EmitClauseBody(emit, calleePredX.Bytecode, 0, calleePredX.Bytecode.Length,
                        failLabel, Array.Empty<CallSite>(),
                        calleeMap: calleeMap, suppressProceedReturn: false);
                    pc += OpcodeTable.Get(op).Size;
                    continue;
                }
                // int target = IlExecuteHelper.Resolve(engine, siteFunctorId);
                // engine.SetB0(engine.B); engine.SetPc(target);
                // engine.IlTailCallPending = true; return true;
                emit.LoadArgument(0);
                emit.LoadArgument(0);
                emit.Call(EngineBGetter);
                emit.Call(EngineSetB0Method);
                emit.LoadArgument(0);
                emit.LoadArgument(0);
                emit.LoadConstant(siteFunctorId);
                emit.Call(IlExecuteHelperResolveMethod);
                emit.Call(EngineSetPcMethod);
                emit.LoadArgument(0);
                emit.LoadConstant(true);
                emit.Call(EngineIlTailCallPendingSetter);
                emit.LoadConstant(true);
                emit.Return();
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.Proceed)
            {
                // In inlined-Call mode the caller has more body after the
                // inlined block; skip the return and fall through to the
                // next opcode in the caller's stream. In normal mode (and
                // in inlined-Execute mode) proceed = return true.
                if (!suppressProceedReturn)
                {
                    emit.LoadConstant(true);
                    emit.Return();
                }
                pc += 1;
                continue;
            }
            throw new NotSupportedException(
                $"IL emission hit unsupported opcode 0x{(byte)op:X2} at pc={pc}.");
        }
    }

    // ============================================================================
    // Shape 2: switch_on_atom indexed multi-clause
    // ============================================================================

    /// <summary>The result of parsing an indexed-atom predicate's
    /// bytecode: each clause's first-arg atom id and the byte offset of
    /// its body in the bytecode. Used both as a "yes I can compile this"
    /// signal and as the dispatch table the IL emission consumes.</summary>
    private sealed class IndexedAtomInfo
    {
        public required IReadOnlyList<(int AtomId, int BodyOffset)> Clauses { get; init; }
    }

    /// <summary>Per-clause layout extracted from a try_me_else chain
    /// (chunk 52): the [start, end) byte offsets of each clause's body
    /// in the predicate's bytecode. Cursor N during IL dispatch runs
    /// the body at <c>Clauses[N]</c>.</summary>
    private sealed class TryMeElseChainInfo
    {
        public required IReadOnlyList<(int Start, int End)> Clauses { get; init; }
    }

    /// <summary>Recognises the classical non-indexed multi-clause shape
    /// <c>try_me_else / retry_me_else* / trust_me</c> with each clause
    /// body in the IL subset. This is the WAM compiler's output for
    /// multi-clause predicates that don't take first-argument indexing
    /// (e.g. arity 0, or every clause's first arg is a variable). When
    /// recognised, <paramref name="info"/> reports the per-clause body
    /// byte ranges so <see cref="CompileTryMeElseChain"/> can emit a
    /// cursor switch + IL choice points.</summary>
    private static bool TryDescribeTryMeElseChain(
        CompiledPredicate predicate,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        out TryMeElseChainInfo? info)
    {
        info = null;
        byte[] code = predicate.Bytecode;
        if (code.Length == 0) return false;
        // First instruction must be try_me_else (size 9: opcode + bp +
        // arity). After that we expect alternating "clause body"
        // chunks separated by retry_me_else (size 5) and terminated by
        // trust_me (size 1) preceding the last clause.
        if ((Opcode)code[0] != Opcode.TryMeElse) return false;
        var clauseStarts = new List<int>();
        int pc = 0;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.TryMeElse || op == Opcode.RetryMeElse)
            {
                pc += OpcodeTable.Get(op).Size;
                clauseStarts.Add(pc);
                continue;
            }
            if (op == Opcode.TrustMe)
            {
                pc += 1;
                clauseStarts.Add(pc);
                continue;
            }
            // Skip clause-body opcodes until the next dispatch op or
            // end of bytecode. Body opcodes must all be in the IL
            // subset (the per-clause emission walks them again to emit
            // IL; we just need to size-walk here).
            if (!IsClauseBodyOpcode(op, predicate, pc, calleeMap)) return false;
            pc += OpcodeTable.Get(op).Size;
        }

        // Derive (Start, End) for each clause body.
        if (clauseStarts.Count != predicate.ClauseCount) return false;
        var ranges = new List<(int, int)>(clauseStarts.Count);
        for (int i = 0; i < clauseStarts.Count; i++)
        {
            int start = clauseStarts[i];
            int end = i + 1 < clauseStarts.Count
                ? FindDispatchOpBefore(code, clauseStarts[i + 1])
                : code.Length;
            ranges.Add((start, end));
        }
        info = new TryMeElseChainInfo { Clauses = ranges };
        return true;
    }

    /// <summary>True iff <paramref name="op"/> is part of the IL-supported
    /// clause-body opcode set (anything that <see cref="EmitClauseBody"/>
    /// emits). Used by <see cref="TryDescribeTryMeElseChain"/> to verify
    /// each clause body fits the IL subset without re-emitting.</summary>
    private static bool IsClauseBodyOpcode(
        Opcode op, CompiledPredicate predicate, int pc,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap)
    {
        if (op == Opcode.Proceed) return true;
        if (op == Opcode.Execute) return true;
        if (op == Opcode.Call)
        {
            // Same leaf-callee restriction as the single-clause path.
            if (calleeMap is null) return false;
            int siteFid = FindCallSiteFunctorId(predicate.CallSites, pc);
            if (siteFid < 0) return false;
            if (!calleeMap.TryGetValue(siteFid, out var callee)) return false;
            return IsLeafPredicate(callee);
        }
        return IsSupportedOpcode(op);
    }

    private static int FindDispatchOpBefore(byte[] code, int clauseStart)
    {
        // Dispatch opcodes immediately precede each clauseStart except
        // for the first (which starts at pc=9, after the leading
        // try_me_else). Sizes: try_me_else 9, retry_me_else 5, trust_me 1.
        if (clauseStart == 0) return 0;
        // Walk backwards: the dispatch is either trust_me (1) or
        // retry_me_else (5). We check the byte just before clauseStart.
        if (clauseStart - 1 >= 0 && (Opcode)code[clauseStart - 1] == Opcode.TrustMe)
            return clauseStart - 1;
        if (clauseStart - 5 >= 0 && (Opcode)code[clauseStart - 5] == Opcode.RetryMeElse)
            return clauseStart - 5;
        // For clause 0, dispatch is try_me_else (9 bytes) at pc=0.
        if (clauseStart - 9 >= 0 && (Opcode)code[clauseStart - 9] == Opcode.TryMeElse)
            return clauseStart - 9;
        return clauseStart;
    }

    /// <summary>Emits the IL for a non-indexed multi-clause predicate
    /// (try_me_else chain). cursor 0 runs clause 1 with an IL CP push
    /// pointing at cursor 1, cursor N runs clause N+1, etc. The last
    /// clause runs without a CP push, matching the trust_me semantics.
    /// The CP-push trampoline reuses the same <see cref="IndexedDelegateHolder"/>
    /// machinery as the indexed path.</summary>
    private PredicateDelegate CompileTryMeElseChain(
        CompiledPredicate predicate, TryMeElseChainInfo info,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        lock (IndexedDelegateHolder.RegistrationLock)
        {
            return CompileTryMeElseChainUnlocked(predicate, info, calleeMap);
        }
    }

    private PredicateDelegate CompileTryMeElseChainUnlocked(
        CompiledPredicate predicate, TryMeElseChainInfo info,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        int holderKey = _nextHolderKey;
        var emitSelf = SelfFromHolder(holderKey);

        var emit = Sigil.Emit<PredicateDelegate>.NewDynamicMethod(
            $"ShumwayIl_tryelse_{predicate.FunctorId}");
        EmitTryMeElseChainBody(emit, predicate, info, calleeMap, emitSelf);

        var del = emit.CreateDelegate();
        IndexedDelegateHolder.Register(holderKey, del);
        _nextHolderKey = holderKey + 1;
        return del;
    }

    /// <summary>Shared try-me-else-chain emit body used by both the
    /// DynamicMethod runtime path (above) and the chunk-71 persisted
    /// assembly path (<see cref="EmitPersistedTryMeElseChain"/>). All
    /// self-references for the per-clause IL CP push route through
    /// <paramref name="emitSelf"/>; callers pick the holder-based or
    /// field-based variant.</summary>
    private static void EmitTryMeElseChainBody(
        Sigil.Emit<PredicateDelegate> emit,
        CompiledPredicate predicate,
        TryMeElseChainInfo info,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap,
        SelfDelegateEmitter emitSelf)
    {
        var clauses = info.Clauses;
        var failLabel = emit.DefineLabel("fail");

        for (int i = 0; i < clauses.Count; i++)
        {
            var nextLabel = emit.DefineLabel($"after_clause_{i}");
            emit.LoadArgument(1);
            emit.LoadConstant(i);
            emit.UnsignedBranchIfNotEqual(nextLabel);

            // If there's a later clause, push an IL CP for it before
            // running this clause's body.
            if (i < clauses.Count - 1)
            {
                emit.LoadArgument(0);                      // engine
                emitSelf(emit);                            // → PredicateDelegate
                emit.LoadConstant(i + 1);                  // next cursor
                emit.LoadConstant(predicate.Arity);
                emit.Call(EnginePushIlCpMethod);
            }

            // Emit the clause body. EmitClauseBody walks the slice and
            // returns true on Proceed / sets IlTailCallPending on Execute.
            EmitClauseBody(emit, predicate.Bytecode, clauses[i].Start, clauses[i].End,
                failLabel, predicate.CallSites,
                emitSelfDelegate: emitSelf,
                calleeMap: calleeMap);

            emit.MarkLabel(nextLabel);
        }

        // cursor out of [0..N-1] → fail.
        emit.Branch(failLabel);
        emit.MarkLabel(failLabel);
        emit.LoadConstant(false);
        emit.Return();
    }

    /// <summary>Recognises the shape:
    /// <code>
    ///   switch_on_term VarLbl ConstLbl ListLbl StructLbl   (17 bytes)
    ///   [VarLbl: try / retry / trust chain over all clauses]
    ///   [ConstLbl: switch_on_atom tableId                  (5 bytes)]
    ///   [clause bodies: each `get_atom &lt;id&gt; A0 ; proceed`]
    /// </code>
    /// where the switch_on_atom table maps each clause's first-arg atom
    /// to its body offset, and every clause body is the trivial
    /// <c>get_atom &lt;id&gt; A0; proceed</c> form.</summary>
    private static bool TryDescribeIndexedAtomPredicate(
        CompiledPredicate predicate, out IndexedAtomInfo? info)
    {
        info = null;
        if (predicate.Arity != 1) return false;
        byte[] code = predicate.Bytecode;
        if (code.Length < 17) return false;
        if ((Opcode)code[0] != Opcode.SwitchOnTerm) return false;

        // VarLbl, ConstLbl, ListLbl, StructLbl operand offsets.
        int varLbl = BytecodeIO.ReadInt32(code, 1);
        int constLbl = BytecodeIO.ReadInt32(code, 5);
        // The shape we recognise has list and struct paths both pointing
        // at the var label (i.e. nothing concrete to dispatch). Allow them
        // to point anywhere — we only emit IL for atom dispatch — but
        // demand const points at a switch_on_atom.
        if (constLbl < 0 || constLbl >= code.Length) return false;
        if ((Opcode)code[constLbl] != Opcode.SwitchOnAtom) return false;

        int tableId = BytecodeIO.ReadInt32(code, constLbl + 1);
        if (tableId < 0 || tableId >= predicate.SwitchTables.Count) return false;

        // Verify the var-dispatch path is the standard try/retry/trust
        // chain — we don't need to walk it for IL emission (we'll handle
        // var-dispatch via IL CPs ourselves) but it's a sanity check that
        // we're looking at the shape we expect.
        if (varLbl < 0 || varLbl >= code.Length) return false;
        if ((Opcode)code[varLbl] != Opcode.Try) return false;

        var table = predicate.SwitchTables[tableId];
        // The switch table is sorted by atom id (the WAM compiler uses a
        // SortedDictionary) but the var-dispatch path must enumerate
        // clauses in *source* order — that's what every other Prolog
        // engine does. We recover source order by sorting on the body
        // offset, since the per-predicate bytecode lays clauses out in
        // source order.
        var clauses = new List<(int AtomId, int BodyOffset)>(table.Count);
        for (int i = 0; i < table.Count; i++)
        {
            int bodyOffset = table.Values[i];
            // Skip a leading Meta(DbgInfo) opcode (chunk 55) — the WAM
            // emitter places one at the start of each clause body for
            // stack-trace mapping; from the IL detector's perspective it's
            // pure metadata that lives before the actual head-matching ops.
            if (bodyOffset >= 0 && bodyOffset + 6 <= code.Length
                && (Opcode)code[bodyOffset] == Opcode.Meta)
            {
                bodyOffset += 6;
            }
            if (bodyOffset < 0 || bodyOffset + 10 > code.Length) return false;
            if ((Opcode)code[bodyOffset] != Opcode.GetAtom) return false;
            // get_atom <id>, <reg> ; proceed
            int reg = BytecodeIO.ReadInt32(code, bodyOffset + 5);
            if (reg != 0) return false;
            if (bodyOffset + 9 >= code.Length) return false;
            if ((Opcode)code[bodyOffset + 9] != Opcode.Proceed) return false;
            int atomId = BytecodeIO.ReadInt32(code, bodyOffset + 1);
            clauses.Add((atomId, bodyOffset));
        }
        // Empty switch tables are degenerate.
        if (clauses.Count == 0) return false;
        clauses.Sort((a, b) => a.BodyOffset.CompareTo(b.BodyOffset));
        info = new IndexedAtomInfo { Clauses = clauses };
        return true;
    }

    /// <summary>Emits the IL for an indexed-atom multi-clause predicate.
    /// The emitted delegate handles both the ground-A1 fast path (direct
    /// atom-id dispatch) and the unbound-A1 path (enumerate via the IL
    /// choice-point machinery from ADR-014).</summary>
    private PredicateDelegate CompileIndexedAtomPredicate(
        CompiledPredicate predicate, IndexedAtomInfo info)
    {
        // Take the holder lock for the entire emit-and-register sequence so
        // two concurrent Compile calls don't both observe the same
        // _nextHolderKey, embed it into their IL, and overwrite each other
        // in the holder. The lock is short-lived (one emit call) and only
        // contended when two engines promote at the same wall-clock moment.
        lock (IndexedDelegateHolder.RegistrationLock)
        {
            return CompileIndexedAtomPredicateUnlocked(predicate, info);
        }
    }

    private PredicateDelegate CompileIndexedAtomPredicateUnlocked(
        CompiledPredicate predicate, IndexedAtomInfo info,
        int profileKey = -1, int[]? groundOrder = null)
    {
        int holderKey = _nextHolderKey;
        var emitSelf = SelfFromHolder(holderKey);

        var emit = Sigil.Emit<PredicateDelegate>.NewDynamicMethod(
            $"ShumwayIl_indexed_{predicate.FunctorId}");
        EmitIndexedAtomBody(emit, predicate, info, emitSelf, profileKey, groundOrder);

        var del = emit.CreateDelegate();
        IndexedDelegateHolder.Register(holderKey, del);
        _nextHolderKey = holderKey + 1;
        return del;
    }

    /// <summary>Shared indexed-atom-shape emit body used by both the
    /// DynamicMethod runtime path (above) and the chunk-71 persisted
    /// assembly path. Self-references for the per-clause IL CP push
    /// route through <paramref name="emitSelf"/>.
    ///
    /// <para>Chunk 76 — PGO. <paramref name="profileKey"/> ≥ 0 emits
    /// the <em>instrumented</em> ground-dispatch: each atom match
    /// lands on its own success label that records a hit via
    /// <see cref="IlProfileCounters.Bump"/>. <paramref name="groundOrder"/>,
    /// when non-null, is a permutation of clause indices giving the
    /// order in which to emit the ground-dispatch <c>cmp</c> chain —
    /// the phase-2 <em>optimised</em> form puts the
    /// most-frequently-matched atom first. The ground dispatch is a
    /// pure lookup (whichever atom matches, the answer is the same),
    /// so reordering it is always semantics-preserving. The
    /// var-dispatch path is never reordered — its clause order is the
    /// observable solution order.</para></summary>
    private static void EmitIndexedAtomBody(
        Sigil.Emit<PredicateDelegate> emit,
        CompiledPredicate predicate,
        IndexedAtomInfo info,
        SelfDelegateEmitter emitSelf,
        int profileKey = -1,
        int[]? groundOrder = null)
    {
        var clauses = info.Clauses;
        // Build the dispatch arrays *outside* IL so the emitted method
        // doesn't have to allocate them per call.
        int[] atomIds = clauses.Select(c => c.AtomId).ToArray();

        var failLabel = emit.DefineLabel("fail");
        var varDispatchLabel = emit.DefineLabel("var_dispatch");
        var groundDispatchLabel = emit.DefineLabel("ground_dispatch");

        // cursor != 0 → re-entry, jump straight to var-dispatch switch.
        emit.LoadArgument(1);
        emit.LoadConstant(0);
        var notCursorZero = emit.DefineLabel("not_cursor_zero");
        emit.UnsignedBranchIfNotEqual(notCursorZero);

        // cursor == 0: deref A1 (read X[0], chase REF if needed) and
        // dispatch on its tag.
        EmitDerefA0(emit);
        // Now top of stack is a Cell. Save it in a local since we'll
        // need .Tag and .AsAtomId.
        var a1Local = emit.DeclareLocal<Cell>("a1");
        emit.StoreLocal(a1Local);

        // tag = a1.Tag
        emit.LoadLocalAddress(a1Local);
        emit.Call(CellTagGetter);
        // tag on stack as byte
        var tagLocal = emit.DeclareLocal<byte>("tag");
        emit.StoreLocal(tagLocal);

        // if (tag == Tag.Ref) goto var_dispatch
        emit.LoadLocal(tagLocal);
        emit.LoadConstant((int)Tag.Ref);
        emit.BranchIfEqual(varDispatchLabel);
        // if (tag == Tag.Atom) goto ground_dispatch
        emit.LoadLocal(tagLocal);
        emit.LoadConstant((int)Tag.Atom);
        emit.BranchIfEqual(groundDispatchLabel);
        // Any other tag → fail (no clause can match a list/struct/etc).
        emit.Branch(failLabel);

        // ground_dispatch: compare a1.AsAtomId against each clause's
        // atom id, returning true on a hit. The chain is a linear
        // sequence of cmp + branch-if-equal; for the small predicate
        // counts we typically deal with (5-20 clauses) it beats a hash
        // lookup in cache-friendliness.
        emit.MarkLabel(groundDispatchLabel);
        emit.LoadLocalAddress(a1Local);
        emit.Call(CellAsAtomIdGetter);
        var atomIdLocal = emit.DeclareLocal<int>("atomId");
        emit.StoreLocal(atomIdLocal);

        // The cmp chain is emitted in groundOrder when given (phase-2
        // PGO puts the hottest atom first); identity order otherwise.
        int n = atomIds.Length;
        int[] order = groundOrder ?? Enumerable.Range(0, n).ToArray();

        if (profileKey >= 0)
        {
            // Instrumented (phase-1): a per-clause success label that
            // records the hit before returning true.
            var successLabels = new Sigil.Label[n];
            for (int ci = 0; ci < n; ci++)
                successLabels[ci] = emit.DefineLabel($"ground_success_{ci}");
            foreach (int ci in order)
            {
                emit.LoadLocal(atomIdLocal);
                emit.LoadConstant(atomIds[ci]);
                emit.BranchIfEqual(successLabels[ci]);
            }
            emit.Branch(failLabel);
            for (int ci = 0; ci < n; ci++)
            {
                emit.MarkLabel(successLabels[ci]);
                emit.LoadConstant(profileKey);
                emit.LoadConstant(ci);
                emit.Call(IlProfileCountersBump);
                emit.LoadConstant(true);
                emit.Return();
            }
        }
        else
        {
            // Uninstrumented (no PGO, or phase-2 optimised): shared
            // success label. The switch_on_atom dispatch already
            // unified A1 with the matching atom, so just return true.
            var groundSuccess = emit.DefineLabel("ground_success");
            foreach (int ci in order)
            {
                emit.LoadLocal(atomIdLocal);
                emit.LoadConstant(atomIds[ci]);
                emit.BranchIfEqual(groundSuccess);
            }
            emit.Branch(failLabel);
            emit.MarkLabel(groundSuccess);
            emit.LoadConstant(true);
            emit.Return();
        }

        // var_dispatch and cursor>0 share the same dispatch logic: pick
        // the clause to try based on the cursor. cursor==0 enters here
        // when A1 is unbound; cursor>0 enters here after a backtrack.
        emit.MarkLabel(varDispatchLabel);
        emit.MarkLabel(notCursorZero);

        // The dispatch is a sequence of: "is cursor == N? if yes, push
        // CP for cursor=N+1 (unless last) and unify A0 with atom N".
        for (int i = 0; i < atomIds.Length; i++)
        {
            var nextLabel = emit.DefineLabel($"after_clause_{i}");
            emit.LoadArgument(1);
            emit.LoadConstant(i);
            emit.UnsignedBranchIfNotEqual(nextLabel);

            // If there's a later clause, push an IL CP for it before
            // attempting unification. The "this delegate" reference
            // routes through emitSelf — IndexedDelegateHolder for the
            // DynamicMethod path, a static array slot for the
            // persisted path (chunk 71).
            if (i < atomIds.Length - 1)
            {
                emit.LoadArgument(0);                  // engine
                emitSelf(emit);                        // → PredicateDelegate
                emit.LoadConstant(i + 1);              // next cursor
                emit.LoadConstant(1);                  // arity
                emit.Call(EnginePushIlCpMethod);
            }
            // engine.UnifyRegisterWithCell(0, Cell.Atom(atomIds[i]))
            emit.LoadArgument(0);
            emit.LoadConstant(0);                      // reg 0
            emit.LoadConstant(atomIds[i]);
            emit.Call(CellAtomMethod);
            emit.Call(EngineUnifyMethod);
            // Return whatever unify returned.
            emit.Return();

            emit.MarkLabel(nextLabel);
        }

        // cursor not in [0..N-1] → fail.
        emit.Branch(failLabel);

        emit.MarkLabel(failLabel);
        emit.LoadConstant(false);
        emit.Return();
    }

    /// <summary>A counter the IL emission embeds into the bytecode as a
    /// constant to look up the freshly-emitted delegate at runtime. This
    /// is the Tier-1 equivalent of a self-reference; Sigil doesn't expose
    /// the dynamic method's delegate during emission, so we route through
    /// a static side table keyed by an integer.</summary>
    private static int _nextHolderKey = 1;
    private static readonly MethodInfo IndexedDelegateHolderGet =
        typeof(IndexedDelegateHolder).GetMethod(nameof(IndexedDelegateHolder.Get))!;

    /// <summary>Emits IL that leaves a <see cref="PredicateDelegate"/> on
    /// the evaluation stack — the running predicate's own delegate, used
    /// as the callback target for <c>engine.PushIlChoicePoint</c>. Two
    /// implementations:
    /// <list type="bullet">
    /// <item>DynamicMethod: <c>LoadConstant(holderKey); Call(IndexedDelegateHolderGet)</c>,
    /// resolved at runtime from a process-wide dictionary.</item>
    /// <item>Persisted assembly: <c>LoadField(arrayField); LoadConstant(slot); LoadElement&lt;PredicateDelegate&gt;()</c>,
    /// resolved at load time from a static array field on the emitted type.</item>
    /// </list></summary>
    internal delegate void SelfDelegateEmitter(Sigil.Emit<PredicateDelegate> emit);

    internal static SelfDelegateEmitter SelfFromHolder(int holderKey) =>
        e =>
        {
            e.LoadConstant(holderKey);
            e.Call(IndexedDelegateHolderGet);
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
    /// runtime <see cref="Get"/> resolves it to the stored delegate. The
    /// table is process-wide but write-once-per-key, so there's no
    /// thread-safety concern beyond the lock around the dictionary.</summary>
    internal static class IndexedDelegateHolder
    {
        private static readonly Dictionary<int, PredicateDelegate> _byKey = new();
        private static readonly object _lock = new();

        /// <summary>The lock the IL emission takes around the
        /// emit-and-register sequence so two concurrent compiles don't
        /// race on <c>_nextHolderKey</c>.</summary>
        public static object RegistrationLock => _lock;

        public static void Register(int key, PredicateDelegate del)
        {
            lock (_lock) _byKey[key] = del;
        }

        public static Func<Engine, int, bool> Get(int key)
        {
            PredicateDelegate del;
            lock (_lock) del = _byKey[key];
            return new Func<Engine, int, bool>(del);
        }
    }

    /// <summary>Resolves a callee functor id to its current-query
    /// bytecode address by consulting <see cref="Engine.CurrentFunctorAddresses"/>.
    /// Called from IL-emitted Execute opcodes (chunk 47) so the tail-call
    /// target stays correct across queries even when the link layout
    /// changes between them.</summary>
    public static class IlExecuteHelper
    {
        public static int Resolve(Engine engine, int functorId)
        {
            var map = engine.CurrentFunctorAddresses;
            if (map is null)
                throw new InvalidOperationException(
                    "IL Execute: engine has no CurrentFunctorAddresses set. "
                    + "The embedding layer must populate it at query setup.");
            if (!map.TryGetValue(functorId, out int address))
                throw new InvalidOperationException(
                    $"IL Execute: callee functor id {functorId} is not in the engine's current address map. "
                    + "The callee may not be loaded in this query's program.");
            return address;
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
