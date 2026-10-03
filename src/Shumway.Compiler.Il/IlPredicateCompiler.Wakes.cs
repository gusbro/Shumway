using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Il;

// ADR-049 under ADR-061: compiled code wakes where Tier-0 does, in front of
// every builtin but =/2 and of every call it inlines. The interrupt's resume
// marker re-enters the method that armed it, before the same point: a
// delegate at a wake cursor, the cold method at an instruction boundary.
public sealed partial class IlPredicateCompiler
{
    /// <summary>Cursors from here up to <see cref="CpsBoundaryBase"/> re-enter a
    /// delegate at one of its wake points.</summary>
    private const int WakeCursorBase = Activation.CompiledReentryCursorBase;

    /// <summary>The cursor from which the cold method of a predicate with
    /// continuation methods is entered at an instruction boundary.</summary>
    public const int ColdCursorBase = CpsBoundaryBase;

    /// <summary>Wake points are compiled with continuation methods on: the
    /// delegates and the methods of that mode wake, regions do not.</summary>
    private static bool WakePoints => CpsMode && !DebugMode && _persistPatches is null;

    // A delegate's wake points, in cursor order, and the dispatch its cursor
    // switch falls through to; the copies of the CP-free constructs a wake
    // left, emitted after the method's code.
    private sealed class WakeState
    {
        public readonly List<IlLabel> Labels = new();
        public IlLabel? Dispatch;
        public readonly List<Action> Copies = new();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IlEmit, WakeState> WakeStates = new();

    private static WakeState WakeStateOf(IlEmit emit)
    {
        if (!WakeStates.TryGetValue(emit, out var s))
        {
            s = new WakeState();
            WakeStates.Add(emit, s);
        }
        return s;
    }

    // Constructs whose state lives in IL locals (a CP-free guard, a
    // fail-direct chain, an inlined fact), in any method: no wake label is
    // valid inside one.
    [ThreadStatic] private static int _opaqueDepth;

    /// <summary>A construct compiled inline whose rest a wake inside it
    /// continues in a copy, compiled as ordinary code: a CP-free guard
    /// (ADR-031), a fail-direct chain in it, a callee inlined in either.</summary>
    private abstract class InlineScope
    {
        public InlineScope? Outer;
        /// <summary>Where the outer construct continues once this one is done.</summary>
        public int CallerPcAfter;
    }

    /// <summary>A CP-free guard's prefix. In a copy, the guard whose choice
    /// point is already pushed (<see cref="MaterializeCp"/> null).</summary>
    private sealed class GuardScope : InlineScope
    {
        public Action? MaterializeCp;
        public required Action<int, int, IlLabel, string> EmitCopySlice;
        public required int CutEnd;
        public required IlLabel AfterCommit;
        public required IlLabel FailLabel;
    }

    /// <summary>A callee inlined at a call site: its code and where its body ends.</summary>
    private sealed class LeafScope : InlineScope
    {
        public required byte[] Code;
        public required int End;
        public required IReadOnlyDictionary<int, CompiledPredicate>? CalleeMap;
    }

    /// <summary>A fail-direct callee inlined as a chain of alternatives
    /// without a choice point, and the clause being emitted.</summary>
    private sealed class ChainScope : InlineScope
    {
        public required CompiledPredicate Callee;
        public required List<FailDirectClause> Clauses;
        public required IReadOnlyDictionary<int, CompiledPredicate>? CalleeMap;
        public required IlLocal[] ArgSaves;
        public required IlLocal Bt, Xt, H, E;
        public int Clause;
        public bool PreCut;
    }

    /// <summary>In a copy, a fail-direct callee's clause compiled as ordinary
    /// code, its choice point already pushed: a wake in a chain nested in it
    /// continues with the rest of this clause and then joins the copy that
    /// holds the callee's remaining clauses.</summary>
    private sealed class ChainRestScope : InlineScope
    {
        public required CompiledPredicate Callee;
        public required List<FailDirectClause> Clauses;
        public required FailDirectClause Clause;
        public required IReadOnlyDictionary<int, CompiledPredicate>? CalleeMap;
        public required IlLabel Join;
        public required IlLabel Entry;
        public required IlLabel FailLabel;
    }

    [ThreadStatic] private static InlineScope? _scope;

    private static T PushScope<T>(T scope) where T : InlineScope
    {
        scope.Outer = _scope;
        _scope = scope;
        return scope;
    }

    private static void PopScope(InlineScope scope) => _scope = scope.Outer;

    /// <summary>At the start of a method: no construct is open.</summary>
    private static void ResetWakeEmission()
    {
        _opaqueDepth = 0;
        _scope = null;
    }

    /// <summary>After a method's code: the copies its wakes asked for, each of
    /// which may ask for more.</summary>
    private static void EmitWakeCopies(IlEmit emit)
    {
        if (!WakeStates.TryGetValue(emit, out var s)) return;
        for (int i = 0; i < s.Copies.Count; i++) s.Copies[i]();
        s.Copies.Clear();
    }

    /// <summary>Where a delegate's cursor switch falls through, and at a leaf's
    /// entry: a wake cursor goes to the method's wake dispatch.</summary>
    private static void EmitWakeCursorCheck(IlEmit emit)
    {
        if (!WakePoints || _cps is not null) return;
        var s = WakeStateOf(emit);
        s.Dispatch ??= emit.DefineLabel("wake_dispatch");
        emit.LoadArgument(1);
        emit.LoadConstant(WakeCursorBase);
        emit.BranchIfGreaterOrEqual(s.Dispatch);
    }

    /// <summary>After a delegate's code: the switch over its wake points.</summary>
    private static void EmitWakeDispatch(IlEmit emit)
    {
        if (!WakeStates.TryGetValue(emit, out var s) || s.Dispatch is not { } dispatch) return;
        emit.MarkLabel(dispatch);
        if (s.Labels.Count > 0)
        {
            emit.LoadArgument(1);
            emit.LoadConstant(WakeCursorBase);
            emit.Subtract();
            emit.Switch(s.Labels.ToArray());
        }
        emit.LoadConstant(false);
        EmitReturn(emit);
    }

    /// <summary>A point the dispatch loop can re-enter this predicate at: an
    /// instruction boundary of the cold method, reserved at the same number in
    /// every continuation method (the label only in the cold method), or a
    /// wake cursor of a delegate.</summary>
    private static int ReserveReentry(IlEmit emit, out IlLabel? label)
    {
        if (_cps is { } c)
        {
            int id = c.Boundary++;
            label = null;
            if (c.Cold)
            {
                label = emit.DefineLabel($"reentry_b{id}");
                c.ColdLabels.Add(label);
            }
            return CpsBoundaryBase + id;
        }
        var s = WakeStateOf(emit);
        if (s.Dispatch is null)
            throw new InvalidOperationException("ADR-049: a re-entry in a method with no wake cursor check.");
        label = emit.DefineLabel($"reentry_w{s.Labels.Count}");
        s.Labels.Add(label);
        return WakeCursorBase + s.Labels.Count - 1;
    }

    /// <summary>The wake point in front of a builtin or an inlined call, its
    /// <paramref name="arity"/> argument registers staged. Nothing pending: one
    /// predicted branch. A hot method leaves for its cold method at the
    /// instruction's boundary; the cold method and a delegate arm the
    /// interrupt with a marker that re-enters them here.</summary>
    private static void EmitWakePoint(IlEmit emit, int arity, IlLabel failLabel, int pc)
    {
        if (!WakePoints) return;
        if (_opaqueDepth > 0)
        {
            EmitScopedWakePoint(emit, pc);
            return;
        }
        int cursor;
        if (_cps is { } c)
        {
            if (c.Current < 0)
                throw new InvalidOperationException("ADR-061: a wake point outside an instruction boundary.");
            cursor = CpsBoundaryBase + c.Current;
        }
        else
        {
            var s = WakeStateOf(emit);
            if (s.Dispatch is null)
                throw new InvalidOperationException("ADR-049: a wake point in a method with no wake cursor check.");
            int k = s.Labels.Count;
            var label = emit.DefineLabel($"wake_{k}");
            s.Labels.Add(label);
            emit.MarkLabel(label);
            cursor = WakeCursorBase + k;
        }
        emit.LoadArgument(0);
        emit.LoadConstant(arity);
        EmitResumeMarker(emit, _emitOwnerFid, cursor);
        EmitHelperCall(emit, EngineWakeBoundaryAtMethod);
        var verdict = emit.DeclareLocal<int>($"wake_at_v_{NextLabelSeq()}");
        emit.StoreLocal(verdict);
        emit.LoadLocal(verdict);
        emit.LoadConstant(2);
        emit.BranchIfEqual(failLabel);
        var goOn = emit.DefineLabel($"wake_at_go_{NextLabelSeq()}");
        emit.LoadLocal(verdict);
        emit.BranchIfFalse(goOn);
        emit.LoadConstant(true);   // armed: the dispatch loop runs the driver from P
        EmitReturn(emit);
        emit.MarkLabel(goOn);
    }

    /// <summary>A wake point inside a CP-free guard's prefix: at the guard's
    /// level, in fail-direct chains inlined there (one in another), or in a
    /// callee inlined in any of them. Something pending: the choice points
    /// the constructs skipped are pushed now, outermost first (sound anywhere
    /// in them: each raised HB at its entry, so every binding it made is
    /// trailed), and execution continues in a copy of their rests compiled as
    /// ordinary code, innermost first, where this point is an ordinary wake
    /// point. A hot method reaches the cold method's copy through a boundary
    /// reserved here, at the same number in every method. A nesting with no
    /// copy is not compiled: the predicate stays on Tier-0.</summary>
    private static void EmitScopedWakePoint(IlEmit emit, int pc)
    {
        // Innermost first, up to the construct whose rest ends the copy.
        var frames = new List<InlineScope>();
        int opaque = 0;
        for (var f = _scope; ; f = f.Outer)
        {
            if (f is null)
                throw new NotSupportedException("ADR-049: a wake point inside a construct with no copy.");
            frames.Add(f);
            if (f is ChainScope)
            {
                opaque++;
                if (!WamCps)
                    throw new NotSupportedException("ADR-049: a chain's late choice point needs WAM choice points.");
            }
            if (f is GuardScope { MaterializeCp: not null }) opaque++;
            if (f is GuardScope or ChainRestScope) break;
        }
        if (opaque != _opaqueDepth)
            throw new NotSupportedException("ADR-049: a wake point inside a construct with no copy.");
        var fail = frames[^1] switch
        {
            GuardScope g => g.FailLabel,
            ChainRestScope r => r.FailLabel,
            _ => throw new InvalidOperationException(),
        };

        var skip = emit.DefineLabel($"swake_skip_{NextLabelSeq()}");
        emit.LoadArgument(0);
        EmitHelperCall(emit, EngineHasPendingWakeupsGetter);
        emit.BranchIfFalse(skip);
        // Outermost first: each choice point goes over the ones outside it.
        var chains = new Dictionary<ChainScope, (int Clause, bool CpPushed, IlLabel? FirstAlt, int FirstAltCursor)>();
        for (int i = frames.Count - 1; i >= 0; i--)
        {
            if (frames[i] is GuardScope { MaterializeCp: { } materialize }) materialize();
            if (frames[i] is not ChainScope chain) continue;
            bool cpPushed = false;
            IlLabel? firstAlt = null;
            int altCursor = -1;
            if (chain.PreCut)
            {
                // The callee's neck cut prunes to here: what is on top now.
                emit.LoadArgument(0);
                emit.LoadArgument(0);
                EmitHelperCall(emit, EngineBGetter);
                EmitHelperCall(emit, EngineSetB0Method);
                if (chain.Clause < chain.Clauses.Count - 1)
                {
                    cpPushed = true;
                    altCursor = ReserveReentry(emit, out firstAlt);
                    emit.LoadArgument(0);
                    emit.LoadConstant(chain.Callee.Arity);
                    EmitResumeMarker(emit, _emitOwnerFid, altCursor);
                    emit.LoadLocal(chain.Bt);
                    emit.LoadLocal(chain.Xt);
                    emit.LoadLocal(chain.H);
                    emit.LoadLocal(chain.E);
                    EmitHelperCall(emit, EnginePushLateChoicePointMethod);
                    for (int r = 0; r < chain.Callee.Arity; r++)
                    {
                        emit.LoadArgument(0);
                        emit.LoadConstant(r);
                        emit.LoadLocal(chain.ArgSaves[r]);
                        EmitHelperCall(emit, EngineSetTopCpArgRegisterMethod);
                    }
                }
            }
            chains[chain] = (chain.Clause, cpPushed, firstAlt, altCursor);
        }

        IlLabel? copy;
        if (_cps is { Cold: false })
        {
            ReserveReentry(emit, out _);
            EmitColdExitTo(emit, _cps.Boundary - 1);
            copy = null;
        }
        else if (_cps is not null)
        {
            ReserveReentry(emit, out copy);
            emit.Branch(copy!);
        }
        else
        {
            copy = emit.DefineLabel($"swake_copy_{NextLabelSeq()}");
            emit.Branch(copy);
        }
        emit.MarkLabel(skip);
        if (copy is not { } copyLabel) return;

        WakeStateOf(emit).Copies.Add(() =>
        {
            emit.MarkLabel(copyLabel);
            string salt = $"_wc{NextLabelSeq()}";
            int at = pc;
            foreach (var f in frames)
            {
                switch (f)
                {
                    case LeafScope leaf:
                        EmitClauseBody(emit, leaf.Code, at, leaf.End, fail, Array.Empty<CallSite>(),
                            calleeMap: leaf.CalleeMap, suppressProceedReturn: true, localSalt: salt + "l");
                        break;
                    case ChainScope chain:
                        var (clause, cpPushed, firstAlt, altCursor) = chains[chain];
                        EmitChainRest(emit, chain, clause, at, cpPushed, firstAlt, altCursor, fail, salt);
                        break;
                    case ChainRestScope rest:
                        EmitChainClause(emit, rest.Callee, rest.Clauses, rest.Clause, rest.CalleeMap,
                            at, rest.Join, rest.Entry, fail, salt + "r");
                        return;
                    case GuardScope guard:
                        EmitGuardRest(emit, guard, at, salt);
                        return;
                }
                at = f.CallerPcAfter;
                salt += "o";
            }
        });
    }

    /// <summary>A guard's prefix from <paramref name="pc"/> to its cut, its
    /// choice point pushed: an ordinary clause, whose cut is a real cut; then
    /// the code after the commit.</summary>
    private static void EmitGuardRest(IlEmit emit, GuardScope guard, int pc, string salt)
    {
        var pushed = PushScope(new GuardScope
        {
            EmitCopySlice = guard.EmitCopySlice, CutEnd = guard.CutEnd,
            AfterCommit = guard.AfterCommit, FailLabel = guard.FailLabel,
        });
        guard.EmitCopySlice(pc, guard.CutEnd, guard.FailLabel, salt);
        PopScope(pushed);
        emit.Branch(guard.AfterCommit);
    }

    /// <summary>A fail-direct callee's clause from <paramref name="pc"/> as
    /// ordinary code, then a branch to <paramref name="join"/>; its tail call
    /// to itself branches to <paramref name="entry"/>, the callee compiled as
    /// an ordinary predicate (EmitChainRest).</summary>
    private static void EmitChainClause(IlEmit emit, CompiledPredicate callee,
        List<FailDirectClause> clauses, FailDirectClause cl,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap, int pc,
        IlLabel join, IlLabel entry, IlLabel fail, string salt)
    {
        if (cl.CrossTailFid >= 0)
            throw new NotSupportedException("ADR-049: a wake point inside a fail-direct chain with a cross tail call.");
        var context = PushScope(new ChainRestScope
        {
            Callee = callee, Clauses = clauses, Clause = cl, CalleeMap = calleeMap,
            Join = join, Entry = entry, FailLabel = fail,
        });
        EmitClauseBody(emit, callee.BytecodeUnfused, pc, cl.TermPc, fail, callee.CallSites,
            calleeMap: calleeMap, suppressProceedReturn: true,
            forceLeafRuleInline: true, localSalt: salt);
        PopScope(context);
        if (cl.SelfTail)
        {
            // The staging and the deallocate ran in the slice.
            emit.Branch(entry);
            return;
        }
        if (cl.DeallocProceed)
        {
            emit.LoadArgument(0);
            EmitHelperCall(emit, EngineDeallocateMethod);
        }
        emit.Branch(join);
    }

    /// <summary>A fail-direct callee from <paramref name="pc"/> in clause
    /// <paramref name="clause"/>, then as an ordinary predicate: its later
    /// clauses are alternatives of the choice point pushed at the wake (when
    /// <paramref name="cpPushed"/>), each entered by backtracking, restoring it
    /// and pushing the next; a tail call to itself enters it again from its
    /// first clause, its arguments staged. Emitted once per copy, so a copy
    /// holds no CP-free chain of the same callee. Then the caller continues.</summary>
    private static void EmitChainRest(IlEmit emit, ChainScope chain, int clause, int pc,
        bool cpPushed, IlLabel? firstAlt, int firstAltCursor, IlLabel fail, string salt)
    {
        var clauses = chain.Clauses;
        int last = clauses.Count - 1;
        bool loops = false;
        foreach (var c in clauses) loops |= c.SelfTail;
        var join = emit.DefineLabel($"chain_join{salt}");
        var entry = emit.DefineLabel($"chain_entry{salt}");
        // The alternatives the copy enters by backtracking: those after the
        // wake's clause, and all of them when the callee calls itself again.
        int firstReachable = loops ? 1 : cpPushed ? clause + 1 : last + 1;
        var alts = new IlLabel?[clauses.Count];
        var cursors = new int[clauses.Count];
        for (int j = firstReachable; j <= last; j++)
        {
            if (cpPushed && j == clause + 1)
            {
                (alts[j], cursors[j]) = (firstAlt, firstAltCursor);
                continue;
            }
            cursors[j] = ReserveReentry(emit, out alts[j]);
        }
        void PushNext(int j)
        {
            emit.LoadArgument(0);
            emit.LoadConstant(chain.Callee.Arity);
            EmitResumeMarker(emit, _emitOwnerFid, cursors[j]);
            EmitHelperCall(emit, EnginePushChoicePointMethod);
        }
        void SetBarrier()
        {
            emit.LoadArgument(0);
            emit.LoadArgument(0);
            EmitHelperCall(emit, EngineBGetter);
            EmitHelperCall(emit, EngineSetB0Method);
        }

        EmitChainClause(emit, chain.Callee, clauses, clauses[clause], chain.CalleeMap,
            pc, join, entry, fail, salt + "c");
        if (loops)
        {
            // The callee entered afresh by its tail call to itself.
            emit.MarkLabel(entry);
            emit.LoadArgument(0);
            EmitHelperCall(emit, EngineBacktrackSafePointMethod);
            SetBarrier();
            if (last > 0) PushNext(1);
            EmitChainClause(emit, chain.Callee, clauses, clauses[0], chain.CalleeMap,
                clauses[0].Start, join, entry, fail, salt + "e");
        }
        for (int j = Math.Max(1, firstReachable); j <= last; j++)
        {
            emit.MarkLabel(alts[j]!);
            emit.LoadArgument(0);
            EmitHelperCall(emit, EngineTrustMeMethod);
            SetBarrier();
            if (j < last) PushNext(j + 1);
            EmitChainClause(emit, chain.Callee, clauses, clauses[j], chain.CalleeMap,
                clauses[j].Start, join, entry, fail, salt + $"a{j}");
        }
        if (!loops) emit.MarkLabel(entry);   // no tail call reaches it
        emit.MarkLabel(join);
    }

    /// <summary>A hot method's exit to its cold method at <paramref name="boundary"/>,
    /// where the machine's state is all in the activation although the
    /// emission is inside a construct that keeps state in IL locals.</summary>
    private static void EmitColdExitTo(IlEmit emit, int boundary)
    {
        var c = _cps!;
        if (RegisterFileOf(emit) is { } rf)
        {
            rf.ColdBoundary ??= emit.DeclareLocal<int>("cold_boundary");
            rf.SharedColdExit ??= emit.DefineLabel("shared_cold_exit");
            emit.LoadConstant(CpsBoundaryBase + boundary);
            emit.Branch(rf.SharedColdExit);
            return;
        }
        emit.LoadArgument(0);
        emit.LoadConstant(CpsBoundaryBase + boundary);
        emit.Call(c.ColdEmit!);
        emit.Return();
    }

    private static readonly System.Reflection.MethodInfo EnginePushLateChoicePointMethod =
        typeof(Activation).GetMethod(nameof(Activation.PushLateChoicePoint))!;

    /// <summary>The builtins in front of which Tier-0 wakes and compiled code
    /// does not: <c>=/2</c>, a unification.</summary>
    private static bool IsWakeBuiltin(Shumway.Builtins.BuiltinEntry e)
        => !(e.Arity == 2 && e.Name == "=");
}
