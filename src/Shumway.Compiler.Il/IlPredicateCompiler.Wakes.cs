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

    /// <summary>Compiled code wakes where Tier-0 does, except under the
    /// debugger.</summary>
    internal static bool WakePoints => !DebugMode;

    // The predicates whose bytecode the code emitted in a persist batch enters
    // at a wake: a bundle keeps their WAM under --strip-wam.
    [ThreadStatic] private static HashSet<int>? _bytecodeEntered;

    /// <summary>ADR-049: the predicates whose bytecode the persisted code
    /// emitted since the last call hands the activation to at a wake.</summary>
    public static int[] TakeBytecodeEntered()
    {
        if (_bytecodeEntered is not { Count: > 0 } set) return Array.Empty<int>();
        var fids = set.ToArray();
        set.Clear();
        return fids;
    }

    /// <summary>The functor id of a predicate whose bytecode the code enters.</summary>
    private static void EmitBytecodeFid(IlEmit emit, int fid)
    {
        _bytecodeEntered?.Add(fid);
        EmitFunctorId(emit, fid);
    }

    /// <summary>Without continuation methods (regions, and the delegates of
    /// that mode), a wake point hands the activation to the interpreter: a
    /// region keeps state across a point that a re-entry would not find.</summary>
    private static bool DeoptWakes => !CpsMode;

    // The region member whose code is being emitted, or 0 outside a region.
    [ThreadStatic] private static int _emitMemberFid;

    // A call's continuation and the argument staging after it: a wake point
    // there resumes at the continuation, where no argument register is live
    // and the staging runs again, binding nothing (DeoptWakes). The window as
    // it stands before the instruction being emitted is _wakeResume.
    [ThreadStatic] private static (int Fid, int Cursor)? _resumeWindow, _wakeResume;

    /// <summary>A call's continuation, at <paramref name="cursor"/> of the
    /// method the marker of <paramref name="fid"/> enters.</summary>
    private static void OpenResumeWindow(int fid, int cursor) => _resumeWindow = (fid, cursor);

    /// <summary>Before each instruction: the window it may wake in, and whether
    /// the window stays open past it (argument staging only).</summary>
    private static void AdvanceResumeWindow(Opcode op)
    {
        _wakeResume = _resumeWindow;
        if (!IsStaging(op)) _resumeWindow = null;
    }

    private static void CloseResumeWindow() => _resumeWindow = _wakeResume = null;

    // After a call nothing unifies before the next goal but =/2 and builtins,
    // which end the window: a unify_* here builds the term a put_* opened.
    private static bool IsStaging(Opcode op) => op is
        Opcode.PutVariableX or Opcode.PutVariableY or Opcode.PutValueX or Opcode.PutValueY
        or Opcode.PutConstant or Opcode.PutInteger or Opcode.PutAtom or Opcode.PutNil
        or Opcode.PutStructure or Opcode.PutList or Opcode.PutFloat or Opcode.PutBigInt
        or Opcode.PutStructureR or Opcode.PutListR or Opcode.PutPstr or Opcode.PutRational
        or Opcode.UnifyVariableX or Opcode.UnifyVariableY or Opcode.UnifyValueX or Opcode.UnifyValueY
        or Opcode.UnifyConstant or Opcode.UnifyInteger or Opcode.UnifyAtom or Opcode.UnifyNil
        or Opcode.UnifyVoid or Opcode.UnifyFloat or Opcode.UnifyBigInt or Opcode.UnifyStructure
        or Opcode.UnifyList or Opcode.UnifyRational or Opcode.Meta;

    /// <summary>The predicate whose bytecode the code being emitted is.</summary>
    private static int CodeFid => _emitMemberFid != 0 ? _emitMemberFid : _emitOwnerFid;

    // A delegate's wake points, in cursor order, and the dispatch its cursor
    // switch falls through to; the alternatives of the choice points a wake
    // pushed, emitted after the method's code.
    private sealed class WakeState
    {
        public readonly List<IlLabel> Labels = new();
        public IlLabel? Dispatch;
        public readonly List<Action> Alternatives = new();
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

    /// <summary>A construct compiled inline that a wake inside it leaves for
    /// the interpreter: a CP-free guard (ADR-031), a fail-direct chain in it,
    /// a callee inlined in either.</summary>
    internal abstract class InlineScope
    {
        public InlineScope? Outer;
        /// <summary>For an inlined callee, the pc after its call site in the
        /// outer construct's code: where the callee returns.</summary>
        public int CallerPcAfter;
    }

    /// <summary>A CP-free guard's prefix, and the push of the choice point it
    /// skipped.</summary>
    internal sealed class GuardScope : InlineScope
    {
        public required Action MaterializeCp;
        /// <summary>Its state is in IL locals (CpsOpaque); a guard whose marks
        /// are in the activation's fields is not (ADR-061).</summary>
        public bool Opaque;
        /// <summary>The predicate runs from its bytecode's address; a dynamic
        /// predicate's snapshot does not.</summary>
        public bool HasBytecode;
    }

    /// <summary>A callee inlined at a call site, or at a tail call
    /// (<see cref="Tail"/>), where it returns where the caller does.</summary>
    internal sealed class LeafScope : InlineScope
    {
        public required CompiledPredicate Callee;
        public bool Tail;
    }

    /// <summary>A fail-direct callee inlined as a chain of alternatives
    /// without a choice point, and the clause being emitted.</summary>
    internal sealed class ChainScope : InlineScope
    {
        public required CompiledPredicate Callee;
        public required List<FailDirectClause> Clauses;
        public required IlLocal[] ArgSaves;
        public required IlLocal Bt, Xt, H, E;
        public int Clause;
        public bool PreCut;
        /// <summary>ADR-033: the method's one copy of the callee, entered from
        /// call sites that pushed a continuation pair; what is outside it is
        /// known only from that stack.</summary>
        public GuardContEmitContext? Shared;
    }

    /// <summary>ADR-049: a call site that pushes a continuation pair for a
    /// shared copy, and what a wake inside the copy needs of it: where the
    /// callee returns, and the guard or the copy the site is in, at the site.</summary>
    internal sealed class ContSite
    {
        public required int ContainerFid;
        public required int CallerPcAfter;
        public GuardScope? Guard;
        public ChainScope? Chain;
        public int Clause;
        public bool PreCut;
        public bool Allocated;
        public int Slot;
    }

    /// <summary>ADR-049: records a site that pushes a continuation pair. A site
    /// whose construct a wake could not leave makes the method's wakes in
    /// shared copies uncompilable.</summary>
    private static void RegisterContSite(GuardContEmitContext ctx, int okCursor, int pcAfter)
    {
        if (!WakePoints) return;
        switch (_scope)
        {
            case GuardScope { HasBytecode: true } g:
                ctx.Sites[okCursor] = new ContSite
                {
                    ContainerFid = CodeFid, CallerPcAfter = pcAfter, Guard = g,
                };
                return;
            case ChainScope { Shared: not null } c when !c.Callee.IsDynamicSnapshot:
                try
                {
                    var (allocated, slot) = FrameState(c.Callee.BytecodeUnfused, c.Clauses[c.Clause].Start, pcAfter);
                    ctx.Sites[okCursor] = new ContSite
                    {
                        ContainerFid = c.Callee.FunctorId, CallerPcAfter = pcAfter, Chain = c,
                        Clause = c.Clause, PreCut = c.PreCut, Allocated = allocated, Slot = slot,
                    };
                }
                catch (NotSupportedException ex)
                {
                    ctx.Unusable ??= ex.Message;
                }
                return;
            default:
                ctx.Unusable ??= "ADR-049: a shared copy called from outside a guard or a shared copy.";
                return;
        }
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
        _arithDepth = 0;
    }

    // The depth of the RPN stack where an a_eval sequence emitted op by op
    // is: an arithmetic goal starts at a push from depth 0.
    [ThreadStatic] private static int _arithDepth;

    /// <summary>Inside a CP-free construct whose choice point a wake has to
    /// push.</summary>
    private static bool InsideCpFreeConstruct()
    {
        for (var f = _scope; f is not null; f = f.Outer)
            if (f is ChainScope or GuardScope) return true;
        return false;
    }

    /// <summary>After a method's code: the alternatives its wakes pushed, each
    /// of which may push the next.</summary>
    private static void EmitWakeAlternatives(IlEmit emit)
    {
        if (!WakeStates.TryGetValue(emit, out var s)) return;
        for (int i = 0; i < s.Alternatives.Count; i++) s.Alternatives[i]();
        s.Alternatives.Clear();
    }

    /// <summary>Where a delegate's cursor switch falls through, and at a leaf's
    /// entry: a wake cursor goes to the method's wake dispatch.</summary>
    private static void EmitWakeCursorCheck(IlEmit emit) => EmitWakeCursorCheck(emit, () => emit.LoadArgument(1));

    private static void EmitWakeCursorCheck(IlEmit emit, Action loadCursor)
    {
        if (!WakePoints || _cps is not null) return;
        var s = WakeStateOf(emit);
        s.Dispatch ??= emit.DefineLabel("wake_dispatch");
        loadCursor();
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
        if (DeoptWakes || _opaqueDepth > 0 || (InsideCpFreeConstruct() && _cps is not { Cold: false }))
        {
            EmitScopedWakePoint(emit, pc);
            return;
        }
        // In a hot method, a guard whose marks are in the fields leaves for the
        // cold method at this boundary, where the point is scoped: the hot
        // method stays free of calls that return (ADR-061 item 6).
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

    /// <summary>The operands an arithmetic goal starting at <paramref name="pc"/>
    /// reads (its a_eval_push run up to the a_eval_is or a_eval_cmp), and the
    /// argument registers live across it: its X operands and an is/2 target in
    /// an X register.</summary>
    private static (int Kind, int Val)[] ArithGoalOperands(byte[] code, int pc, out int liveRegs)
    {
        var ops = new List<(int, int)>();
        liveRegs = 0;
        for (int p = pc; ;)
        {
            var op = (Opcode)code[p];
            if (op == Opcode.AEvalPush)
            {
                int kind = BytecodeIO.ReadInt32(code, p + 1), val = BytecodeIO.ReadInt32(code, p + 5);
                if (kind is 3 or 4) ops.Add((kind, val));
                if (kind == 3) liveRegs = Math.Max(liveRegs, val + 1);
                p += 9;
            }
            else if (op is Opcode.AEvalBin or Opcode.AEvalUn) p += 5;
            else
            {
                if (op == Opcode.AEvalIs && BytecodeIO.ReadInt32(code, p + 1) == 3)
                    liveRegs = Math.Max(liveRegs, BytecodeIO.ReadInt32(code, p + 5) + 1);
                return ops.ToArray();
            }
        }
    }

    /// <summary>The argument registers live across a fused arithmetic op: its X
    /// operands, and its target when it unifies with an X register.
    /// <paramref name="packed"/> = aKind | bKind &lt;&lt; 8 | tKind &lt;&lt; 16.</summary>
    private static int FusedLiveRegs(int packed, int aVal, int bVal, int tVal)
    {
        int live = 0;
        if ((packed & 0xFF) == 3) live = Math.Max(live, aVal + 1);
        if (((packed >> 8) & 0xFF) == 3) live = Math.Max(live, bVal + 1);
        if (((packed >> 16) & 0xFF) == 3) live = Math.Max(live, tVal + 1);
        return live;
    }

    /// <summary>A fused op's operands that a wake could bind: its registers and
    /// Y slots, not its literals.</summary>
    private static (int Kind, int Val)[] FusedOperands(int packed, int aVal, int bVal)
    {
        var ops = new List<(int, int)>(2);
        if ((packed & 0xFF) is 3 or 4) ops.Add((packed & 0xFF, aVal));
        if (((packed >> 8) & 0xFF) is 3 or 4) ops.Add(((packed >> 8) & 0xFF, bVal));
        return ops.ToArray();
    }

    /// <summary>ADR-049: a wake pending in front of an arithmetic goal that
    /// reads an unbound operand fires as an interrupt that re-runs the goal
    /// from its start; a goal whose operands are bound continues the stretch
    /// of unifications.</summary>
    private static void EmitArithWakePoint(IlEmit emit, IlLabel failLabel, int pc,
        (int Kind, int Val)[] operands, int liveRegs)
    {
        if (operands.Length == 0) return;   // literals: nothing a wake binds
        var skip = emit.DefineLabel($"arith_wake_skip_{NextLabelSeq()}");
        var wake = emit.DefineLabel($"arith_wake_{NextLabelSeq()}");
        emit.LoadArgument(0);
        EmitHelperCall(emit, EngineHasPendingWakeupsGetter);
        emit.BranchIfFalse(skip);
        foreach (var (kind, val) in operands)
        {
            emit.LoadArgument(0);
            emit.LoadConstant(kind);
            emit.LoadConstant(val);
            EmitHelperCall(emit, ArithOperandUnboundMethod);
            emit.BranchIfTrue(wake);
        }
        emit.Branch(skip);
        emit.MarkLabel(wake);
        EmitWakePoint(emit, liveRegs, failLabel, pc);
        emit.MarkLabel(skip);
    }

    /// <summary>A wake point inside a CP-free guard's prefix: at the guard's
    /// level, in fail-direct chains inlined there (one in another), or in a
    /// callee inlined in any of them; without continuation methods, any wake
    /// point. Something pending: the machine is made Tier-0's at this point
    /// and the interpreter runs the rest of the activation from it, waking
    /// there. The choice points the constructs
    /// skipped are pushed, outermost first (sound anywhere in them: each
    /// raised HB at its entry, so every binding it made is trailed); each
    /// inlined callee returns after its call site, and the cut level its
    /// clause took is the choice point below its own. A chain's later clauses
    /// are alternatives that run in the callee's bytecode. A nesting that
    /// does not end in a guard, or one with no bytecode to run, is not
    /// compiled: the predicate stays on Tier-0.</summary>
    private static void EmitScopedWakePoint(IlEmit emit, int pc)
    {
        // The inlined callees, outermost first, and the guard around them, or
        // the shared copy whose callers the continuation stack names.
        var levels = new List<InlineScope>();
        GuardScope? guard = null;
        GuardContEmitContext? shared = null;
        int opaque = 0;
        for (var f = _scope; f is not null; f = f.Outer)
        {
            if (f is GuardScope g)
            {
                guard = g;
                if (g.Opaque) opaque++;
                break;
            }
            if (f is ChainScope) opaque++;
            levels.Insert(0, f);
            if (f is ChainScope { Shared: { } ctx })
            {
                shared = ctx;
                break;
            }
        }
        if ((guard is null && shared is null && !DeoptWakes) || opaque != _opaqueDepth)
            throw new NotSupportedException("ADR-049: a wake point inside a construct with no guard.");
        if (guard is { HasBytecode: false })
            throw new NotSupportedException("ADR-049: a wake point in a snapshot's guard.");
        int n = levels.Count;
        var fids = new int[n];
        var allocated = new bool[n];
        var slots = new int[n];
        for (int i = 0; i < n; i++)
        {
            var (callee, start) = levels[i] switch
            {
                ChainScope c => (c.Callee, c.Clauses[c.Clause].Start),
                LeafScope l => (l.Callee, 0),
                _ => throw new InvalidOperationException(),
            };
            if (callee.IsDynamicSnapshot)
                throw new NotSupportedException("ADR-049: a wake point in an inlined snapshot.");
            fids[i] = callee.FunctorId;
            int at = i + 1 < n ? levels[i + 1].CallerPcAfter : pc;
            (allocated[i], slots[i]) = FrameState(callee.BytecodeUnfused, start, at);
        }

        if (n == 0 && guard is null && shared is null && _wakeResume is { } window)
        {
            EmitWindowWakePoint(emit, window);
            return;
        }
        System.Threading.Interlocked.Increment(ref HandoverPoints);
        var skip = emit.DefineLabel($"swake_skip_{NextLabelSeq()}");
        emit.LoadArgument(0);
        EmitHelperCall(emit, EngineHasPendingWakeupsGetter);
        emit.BranchIfFalse(skip);
        // The frames the callees allocated, innermost first from E.
        var env = new IlLocal?[n];
        IlLocal? inner = null;
        for (int i = n - 1; i >= 0; i--)
        {
            if (!allocated[i]) continue;
            emit.LoadArgument(0);
            if (inner is null)
            {
                EmitHelperCall(emit, EngineEGetter);
            }
            else
            {
                emit.LoadLocal(inner);
                EmitHelperCall(emit, EngineEnvPrevMethod);
            }
            env[i] = inner = emit.DeclareLocal<int>($"swake_e{i}_{NextLabelSeq()}");
            emit.StoreLocal(inner);
        }
        if (shared is not null)
        {
            // The levels outside the copy, from the continuation stack; the
            // copy returns where the CP register then says.
            shared.WakeFrame ??= emit.DeclareLocal<int>("wake_frame");
            shared.WakeReturn ??= emit.DeclareLocal<int>("wake_return");
            shared.WakeLevels ??= emit.DefineLabel("wake_levels");
            emit.LoadArgument(0);
            if (inner is null)
            {
                EmitHelperCall(emit, EngineEGetter);
            }
            else
            {
                emit.LoadLocal(inner);
                EmitHelperCall(emit, EngineEnvPrevMethod);
            }
            emit.StoreLocal(shared.WakeFrame);
            var back = emit.DefineLabel($"swake_back_{NextLabelSeq()}");
            emit.LoadConstant(shared.WakeReturns.Count);
            shared.WakeReturns.Add(back);
            emit.StoreLocal(shared.WakeReturn);
            emit.Branch(shared.WakeLevels);
            emit.MarkLabel(back);
        }
        else
        {
            // Outermost first: each choice point goes over the ones outside it.
            guard?.MaterializeCp();
        }
        IlLocal? prev = null;
        for (int i = 0; i < n; i++)
        {
            var cont = emit.DeclareLocal<int>($"swake_cp{i}_{NextLabelSeq()}");
            if (levels[i] is LeafScope { Tail: true })
            {
                // Returns where its caller does.
                if (prev is not null)
                {
                    emit.LoadLocal(prev);
                }
                else
                {
                    emit.LoadArgument(0);
                    EmitHelperCall(emit, EngineCpGetter);
                }
            }
            else if (i == 0 && shared is not null)
            {
                emit.LoadArgument(0);
                EmitHelperCall(emit, EngineCpGetter);
            }
            else
            {
                emit.LoadArgument(0);
                EmitBytecodeFid(emit, i == 0 ? CodeFid : fids[i - 1]);
                emit.LoadConstant(levels[i].CallerPcAfter);
                EmitHelperCall(emit, EngineCodeAddressOfMethod);
            }
            emit.StoreLocal(cont);
            prev = cont;
            emit.LoadArgument(0);
            emit.LoadLocal(cont);
            EmitHelperCall(emit, EngineSetCpMethod);
            var chain = levels[i] as ChainScope;
            if (chain is { PreCut: true })
            {
                // The callee's neck cut prunes to here: what is on top now.
                emit.LoadArgument(0);
                emit.LoadArgument(0);
                EmitHelperCall(emit, EngineBGetter);
                EmitHelperCall(emit, EngineSetB0Method);
            }
            if (env[i] is { } e)
            {
                emit.LoadArgument(0);
                emit.LoadLocal(e);
                emit.LoadLocal(cont);
                emit.LoadConstant(slots[i]);
                emit.LoadArgument(0);
                EmitHelperCall(emit, EngineBGetter);
                EmitHelperCall(emit, EngineRetargetFrameMethod);
            }
            if (chain is { PreCut: true } && chain.Clause < chain.Clauses.Count - 1)
                EmitLateChoicePoint(emit, chain, chain.Clause);
        }
        emit.LoadArgument(0);
        EmitBytecodeFid(emit, n == 0 ? CodeFid : fids[n - 1]);
        emit.LoadConstant(pc);
        EmitHelperCall(emit, EngineDeoptToMethod);
        // Not the call's result: a call the return follows takes the tail.
        // prefix, which costs the whole method.
        emit.LoadConstant(true);
        EmitReturn(emit);
        emit.MarkLabel(skip);
    }

    /// <summary>A wake point in a call's continuation window: the interrupt
    /// re-enters at the continuation, where the staging runs again.</summary>
    private static void EmitWindowWakePoint(IlEmit emit, (int Fid, int Cursor) window)
    {
        System.Threading.Interlocked.Increment(ref WindowPoints);
        var skip = emit.DefineLabel($"wwake_skip_{NextLabelSeq()}");
        emit.LoadArgument(0);
        EmitHelperCall(emit, EngineHasPendingWakeupsGetter);
        emit.BranchIfFalse(skip);
        emit.LoadArgument(0);
        emit.LoadConstant(0);
        EmitResumeMarker(emit, window.Fid, window.Cursor);
        EmitHelperCall(emit, EngineWakeBoundaryAtMethod);
        var verdict = emit.DeclareLocal<int>($"wwake_v_{NextLabelSeq()}");
        emit.StoreLocal(verdict);
        emit.LoadLocal(verdict);
        emit.BranchIfFalse(skip);
        // 2: the drain failed; else armed, and the dispatch loop runs the driver.
        emit.LoadLocal(verdict);
        emit.LoadConstant(1);
        emit.CompareEqual();
        EmitReturn(emit);
        emit.MarkLabel(skip);
    }

    /// <summary>Whether a clause's code from <paramref name="start"/> up to
    /// <paramref name="at"/> leaves a frame allocated, and the Y slot of the
    /// cut level it took (-1 for none).</summary>
    private static (bool Allocated, int LevelSlot) FrameState(byte[] code, int start, int at)
    {
        bool allocated = false;
        int slot = -1;
        for (int p = start; p < at;)
        {
            var op = (Opcode)code[p];
            if (op == Opcode.Meta) { p += 6; continue; }
            switch (op)
            {
                case Opcode.Allocate:
                    allocated = true;
                    break;
                case Opcode.AllocateGetLevel:
                    allocated = true;
                    slot = BytecodeIO.ReadInt32(code, p + 5);
                    break;
                case Opcode.GetLevel:
                    slot = BytecodeIO.ReadInt32(code, p + 1);
                    break;
                case Opcode.GetLevelB:
                    // An if-then-else barrier taken without the choice points
                    // pushed now would sit below them.
                    throw new NotSupportedException("ADR-049: a wake point after an if-then-else barrier in an inlined callee.");
                case Opcode.Deallocate:
                    allocated = false;
                    slot = -1;
                    break;
            }
            int size = OpcodeTable.Get(op).Size;
            if (size <= 0) throw new InvalidOperationException($"ADR-049: no size for {op} at {p}.");
            p += size;
        }
        return (allocated, slot);
    }

    /// <summary>ADR-049: after a method's shared copies, the levels a wake
    /// inside one has outside it. The continuation stack holds a pair per
    /// active copy, down to the one the guard's call site pushed; its OK
    /// cursor names the site. The guard's choice point is pushed, then each
    /// copy's, outermost first (a copy's level is the clause that holds the
    /// next site up), each copy returning after the site that called it; the
    /// guard's pairs are dropped, and the wake goes on with the copy it is in.</summary>
    private static void EmitWakeLevels(IlEmit emit, GuardContEmitContext ctx)
    {
        if (ctx.WakeLevels is not { } start) return;
        if (ctx.Unusable is { } why) throw new NotSupportedException(why);
        System.Threading.Interlocked.Increment(ref WakeLevelRoutines);
        string salt = $"_wl{NextLabelSeq()}";
        var top = emit.DeclareLocal<int>($"wl_top{salt}");
        var g = emit.DeclareLocal<int>($"wl_g{salt}");
        var j = emit.DeclareLocal<int>($"wl_j{salt}");
        var frames = emit.DeclareLocal<int[]>($"wl_frames{salt}");
        var cont = emit.DeclareLocal<int>($"wl_cont{salt}");
        int cursors = ctx.ContLabels.Count;
        // A switch over the OK cursors: the sites mapped by pick, the rest to
        // the next instruction.
        IlLabel[] Table(Func<ContSite, IlLabel?> pick, IlLabel other)
        {
            var t = new IlLabel[cursors];
            for (int c = 0; c < cursors; c++)
                t[c] = ctx.Sites.TryGetValue(c, out var s) && pick(s) is { } l ? l : other;
            return t;
        }
        void OkAt(Action index)
        {
            emit.LoadArgument(0);
            index();
            EmitHelperCall(emit, EngineGuardContOkAtMethod);
        }
        // Where the callee of a site returns, into cont.
        void EmitConts(Action index, IlLabel next)
        {
            var labels = new Dictionary<ContSite, IlLabel>();
            foreach (var s in ctx.Sites.Values) labels[s] = emit.DefineLabel($"wl_cont_{NextLabelSeq()}");
            OkAt(index);
            emit.Switch(Table(s => labels[s], next));
            emit.Branch(next);
            foreach (var (s, l) in labels)
            {
                emit.MarkLabel(l);
                emit.LoadArgument(0);
                EmitBytecodeFid(emit, s.ContainerFid);
                emit.LoadConstant(s.CallerPcAfter);
                EmitHelperCall(emit, EngineCodeAddressOfMethod);
                emit.StoreLocal(cont);
                emit.Branch(next);
            }
        }

        emit.MarkLabel(start);
        emit.LoadArgument(0);
        EmitHelperCall(emit, EngineGuardContTopGetter);
        emit.StoreLocal(top);
        emit.LoadArgument(0);
        emit.LoadLocal(top);
        EmitHelperCall(emit, EngineWakeScratchMethod);
        emit.StoreLocal(frames);

        // The guard's pair: the first one down from the top a guard's site pushed.
        var find = emit.DefineLabel($"wl_find{salt}");
        var found = emit.DefineLabel($"wl_found{salt}");
        var down = emit.DefineLabel($"wl_down{salt}");
        emit.LoadLocal(top);
        emit.LoadConstant(1);
        emit.Subtract();
        emit.StoreLocal(g);
        emit.MarkLabel(find);
        OkAt(() => emit.LoadLocal(g));
        emit.Switch(Table(s => s.Guard is not null ? found : null, down));
        emit.MarkLabel(down);
        emit.LoadLocal(g);
        emit.LoadConstant(1);
        emit.Subtract();
        emit.StoreLocal(g);
        emit.Branch(find);
        emit.MarkLabel(found);

        // Frames, innermost first: level j is the copy holding the site of pair j + 1.
        var p1 = emit.DefineLabel($"wl_p1{salt}");
        var p1Next = emit.DefineLabel($"wl_p1n{salt}");
        var p1Done = emit.DefineLabel($"wl_p1d{salt}");
        var keep = emit.DefineLabel($"wl_keep{salt}");
        emit.LoadLocal(top);
        emit.LoadConstant(2);
        emit.Subtract();
        emit.StoreLocal(j);
        emit.MarkLabel(p1);
        emit.LoadLocal(j);
        emit.LoadLocal(g);
        emit.BranchIfLess(p1Done);
        OkAt(() => { emit.LoadLocal(j); emit.LoadConstant(1); emit.Add(); });
        emit.Switch(Table(s => s.Allocated ? keep : null, p1Next));
        emit.Branch(p1Next);
        emit.MarkLabel(keep);
        emit.LoadLocal(frames);
        emit.LoadLocal(j);
        emit.LoadLocal(ctx.WakeFrame!);
        emit.StoreElement<int>();
        emit.LoadArgument(0);
        emit.LoadLocal(ctx.WakeFrame!);
        EmitHelperCall(emit, EngineEnvPrevMethod);
        emit.StoreLocal(ctx.WakeFrame!);
        emit.MarkLabel(p1Next);
        emit.LoadLocal(j);
        emit.LoadConstant(1);
        emit.Subtract();
        emit.StoreLocal(j);
        emit.Branch(p1);
        emit.MarkLabel(p1Done);

        // The guard's choice point.
        var p2Start = emit.DefineLabel($"wl_p2s{salt}");
        var guards = new Dictionary<GuardScope, IlLabel>();
        foreach (var s in ctx.Sites.Values)
            if (s.Guard is { } gs && !guards.ContainsKey(gs))
                guards[gs] = emit.DefineLabel($"wl_guard_{NextLabelSeq()}");
        OkAt(() => emit.LoadLocal(g));
        emit.Switch(Table(s => s.Guard is { } gs ? guards[gs] : null, p2Start));
        emit.Branch(p2Start);
        foreach (var (gs, l) in guards)
        {
            emit.MarkLabel(l);
            gs.MaterializeCp();
            emit.Branch(p2Start);
        }
        emit.MarkLabel(p2Start);

        // Outermost first: each copy returns after the site of its pair, and
        // its clause is the one holding the site of the next pair up.
        var p2 = emit.DefineLabel($"wl_p2{salt}");
        var p2Level = emit.DefineLabel($"wl_p2l{salt}");
        var p2Next = emit.DefineLabel($"wl_p2n{salt}");
        var p2Done = emit.DefineLabel($"wl_p2d{salt}");
        emit.LoadLocal(g);
        emit.StoreLocal(j);
        emit.MarkLabel(p2);
        emit.LoadLocal(j);
        emit.LoadLocal(top);
        emit.LoadConstant(1);
        emit.Subtract();
        emit.BranchIfGreaterOrEqual(p2Done);
        EmitConts(() => emit.LoadLocal(j), p2Level);
        emit.MarkLabel(p2Level);
        emit.LoadArgument(0);
        emit.LoadLocal(cont);
        EmitHelperCall(emit, EngineSetCpMethod);
        var levelLabels = new Dictionary<ContSite, IlLabel>();
        foreach (var s in ctx.Sites.Values)
            if (s.Chain is not null) levelLabels[s] = emit.DefineLabel($"wl_level_{NextLabelSeq()}");
        OkAt(() => { emit.LoadLocal(j); emit.LoadConstant(1); emit.Add(); });
        emit.Switch(Table(s => levelLabels.TryGetValue(s, out var l) ? l : null, p2Next));
        emit.Branch(p2Next);
        foreach (var (s, l) in levelLabels)
        {
            emit.MarkLabel(l);
            var chain = s.Chain!;
            if (s.PreCut)
            {
                emit.LoadArgument(0);
                emit.LoadArgument(0);
                EmitHelperCall(emit, EngineBGetter);
                EmitHelperCall(emit, EngineSetB0Method);
            }
            if (s.Allocated)
            {
                emit.LoadArgument(0);
                emit.LoadLocal(frames);
                emit.LoadLocal(j);
                emit.LoadElement<int>();
                emit.LoadLocal(cont);
                emit.LoadConstant(s.Slot);
                emit.LoadArgument(0);
                EmitHelperCall(emit, EngineBGetter);
                EmitHelperCall(emit, EngineRetargetFrameMethod);
            }
            if (s.PreCut && s.Clause < chain.Clauses.Count - 1)
                EmitLateChoicePoint(emit, chain, s.Clause);
            emit.Branch(p2Next);
        }
        emit.MarkLabel(p2Next);
        emit.LoadLocal(j);
        emit.LoadConstant(1);
        emit.Add();
        emit.StoreLocal(j);
        emit.Branch(p2);
        emit.MarkLabel(p2Done);

        // The copy the wake is in returns after the site of the top pair.
        var p3 = emit.DefineLabel($"wl_p3{salt}");
        EmitConts(() => { emit.LoadLocal(top); emit.LoadConstant(1); emit.Subtract(); }, p3);
        emit.MarkLabel(p3);
        emit.LoadArgument(0);
        emit.LoadLocal(cont);
        EmitHelperCall(emit, EngineSetCpMethod);
        emit.LoadArgument(0);
        emit.LoadLocal(g);
        EmitHelperCall(emit, EngineResetGuardContTopMethod);
        emit.LoadLocal(ctx.WakeReturn!);
        emit.Switch(ctx.WakeReturns.ToArray());
        emit.LoadConstant(false);
        EmitReturn(emit);
    }

    /// <summary>The choice point a chain skipped, its later clauses the
    /// alternatives, pushed with the marks and arguments of its entry.</summary>
    private static void EmitLateChoicePoint(IlEmit emit, ChainScope chain, int clause)
    {
        int cursor = ReserveReentry(emit, out var alt);
        emit.LoadArgument(0);
        emit.LoadConstant(chain.Callee.Arity);
        EmitResumeMarker(emit, _emitOwnerFid, cursor);
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
        if (alt is not null) EnqueueAlternative(emit, chain.Callee, chain.Clauses, clause + 1, alt);
    }

    /// <summary>For tests: the methods compiled with the routine above, and
    /// the wake points compiled to hand an activation to the interpreter.</summary>
    internal static int WakeLevelRoutines, HandoverPoints, WindowPoints;

    private static void EnqueueAlternative(IlEmit emit, CompiledPredicate callee,
        List<FailDirectClause> clauses, int j, IlLabel label)
        => WakeStateOf(emit).Alternatives.Add(() => EmitAlternative(emit, callee, clauses, j, label));

    /// <summary>Clause <paramref name="j"/> of a chain as the alternative of
    /// the choice point a wake pushed for it: the next clause's choice point
    /// pushed, the clause runs in the callee's bytecode.</summary>
    private static void EmitAlternative(IlEmit emit, CompiledPredicate callee,
        List<FailDirectClause> clauses, int j, IlLabel label)
    {
        emit.MarkLabel(label);
        emit.LoadArgument(0);
        EmitHelperCall(emit, EngineTrustMeMethod);
        emit.LoadArgument(0);
        emit.LoadArgument(0);
        EmitHelperCall(emit, EngineBGetter);
        EmitHelperCall(emit, EngineSetB0Method);
        if (j < clauses.Count - 1)
        {
            int cursor = ReserveReentry(emit, out var next);
            emit.LoadArgument(0);
            emit.LoadConstant(callee.Arity);
            EmitResumeMarker(emit, _emitOwnerFid, cursor);
            EmitHelperCall(emit, EnginePushChoicePointMethod);
            EnqueueAlternative(emit, callee, clauses, j + 1, next!);
        }
        emit.LoadArgument(0);
        EmitBytecodeFid(emit, callee.FunctorId);
        emit.LoadConstant(clauses[j].Start);
        EmitHelperCall(emit, EngineDeoptToMethod);
        // Not the call's result: a call the return follows takes the tail.
        // prefix, which costs the whole method.
        emit.LoadConstant(true);
        EmitReturn(emit);
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
    private static readonly System.Reflection.MethodInfo EngineCodeAddressOfMethod =
        typeof(Activation).GetMethod(nameof(Activation.CodeAddressOf))!;
    private static readonly System.Reflection.MethodInfo EngineDeoptToMethod =
        typeof(Activation).GetMethod(nameof(Activation.DeoptTo))!;
    private static readonly System.Reflection.MethodInfo EngineEnvPrevMethod =
        typeof(Activation).GetMethod(nameof(Activation.EnvPrev))!;
    private static readonly System.Reflection.MethodInfo EngineRetargetFrameMethod =
        typeof(Activation).GetMethod(nameof(Activation.RetargetFrame))!;
    private static readonly System.Reflection.MethodInfo EngineGuardContOkAtMethod =
        typeof(Activation).GetMethod(nameof(Activation.GuardContOkAt))!;
    private static readonly System.Reflection.MethodInfo EngineWakeScratchMethod =
        typeof(Activation).GetMethod(nameof(Activation.WakeScratch))!;
    private static readonly System.Reflection.MethodInfo EngineGuardContTopGetter =
        typeof(Activation).GetProperty(nameof(Activation.GuardContTop))!.GetGetMethod()!;
    private static readonly System.Reflection.MethodInfo EngineResetGuardContTopMethod =
        typeof(Activation).GetMethod(nameof(Activation.ResetGuardContTop))!;

    /// <summary>The builtins in front of which Tier-0 wakes and compiled code
    /// does not: <c>=/2</c>, a unification.</summary>
    private static bool IsWakeBuiltin(Shumway.Builtins.BuiltinEntry e) => !e.IsUnification;
}
