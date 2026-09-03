using Shumway.Core;

namespace Shumway.Interpreter;

public sealed partial class BytecodeInterpreter
{
    /// <summary>
    /// Peephole fusion. Called by the {Try,Retry,Trust}MeElse
    /// handlers after they've done their choice-point work and computed
    /// the cursor of the byte that follows the dispatch opcode.
    ///
    /// <para>If that byte is <see cref="Opcode.CheckVisible"/> — the
    /// shape every dynamic-predicate chain entry has, since the chain
    /// emit always writes dispatch + check_visible — decode it inline
    /// and either advance past it (visible) or signal backtrack
    /// (invisible). Returns false ONLY when the caller must backtrack;
    /// in every other case (no CheckVisible at <paramref name="afterPc"/>,
    /// or visible), it updates PC and returns true.</para>
    ///
    /// <para>This is purely an interpreter speedup — it does NOT change
    /// any bytecode layout, emit-site, or opcode encoding. Skipping
    /// one switch trip + opcode-table lookup + profiler bump per
    /// chain step adds up on dynamic-predicate-heavy workloads
    /// (Blint saw 23.5M direct dispatch→CheckVisible pairs / run).</para>
    /// </summary>
    /// <param name="deadSkipTo">the dispatch opcode's own <c>next</c>
    /// operand (the following chain entry), or -1 (trust_me, no next). When the
    /// visibility check fails and this is >= 0, jump STRAIGHT to the next entry
    /// instead of failing into a full backtrack: the check is the FIRST thing after
    /// the dispatch opcode, so nothing has mutated since the choice point's state
    /// was pushed/restored — the backtrack would restore registers/trail to values
    /// they already hold. The CP's next-clause slot was already advanced by the
    /// dispatch opcode, so the direct jump leaves identical machine state, minus
    /// the redundant restore. On Blint this removes one full backtrack per DEAD
    /// chain entry (a retract-heavy dynamic predicate accumulates thousands —
    /// 1.56M of the 3.38M backtracks in a self-lint were exactly this).</param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private bool TryInlineCheckVisible(Shumway.Core.ProgramView code, byte[] codeArr, int codeLen, int afterPc,
        int deadSkipTo = -1)
    {
        if (afterPc + 17 > codeLen
            || (code.Overflow is null ? codeArr[afterPc] : code[afterPc])
               != (byte)Opcode.CheckVisible)
        {
            _engine.SetPc(afterPc);
            return true;
        }
        long born = ReadI64(code, codeArr, afterPc + 1);   // peeled
        long died = ReadI64(code, codeArr, afterPc + 9);
        long g = _engine.CurrentViewGen;
        if (born > g || died <= g)
        {
            if (deadSkipTo < 0) return false;        // trust_me: genuine fail
            // (An in-place tombstone unlink used to run here; it was
            // REVERTED — it corrupted dynamic unget-buffer tokenization
            // and its wall-clock win was neutral. The direct dead-entry
            // jump below is kept — bisected clean.)
            _engine.SetPc(deadSkipTo);
            return true;
        }
        // the last clause of a dynamic chain terminates at the
        // fail-stub, so its chain instruction is `retry_me_else <fail-stub>`
        // (never `trust_me`). A bare push/retry therefore leaves a choice
        // point whose only alternative is `call_builtin fail/0` — harmless on
        // backtracking, but it makes EVERY deterministic dynamic call report
        // as non-deterministic (a single dynamic fact `c(x)` called `c(x)`
        // left a CP). Once this clause is confirmed visible AND it is the last
        // one (its chain-next is the fail-stub), discard that dead choice
        // point with trust semantics — the choice point governing this clause
        // is the one try_me_else/retry_me_else just pushed/updated (nothing
        // runs between the chain instruction and this check), and its saved
        // machine state equals the current state (check_visible precedes head
        // unification), so TrustMe's restore is a no-op and only the pop
        // takes effect. Brings dynamic dispatch to parity with static
        // trust_me and is what lgtunit's deterministic/1 measures.
        if (deadSkipTo == _engine.DynamicFailStubAddr
            && _engine.DynamicFailStubAddr > 0
            && _engine.B >= 0)
        {
            _engine.TrustMe();
        }
        _engine.SetPc(afterPc + 17);
        return true;
    }

    /// <summary>unify-run fusion. After a unify-family opcode
    /// succeeds, the head/argument-matching code is almost always a RUN of more
    /// unify-family opcodes (Blint pairs: unify_list→unify_atom 945K,
    /// unify_atom→unify_list 782K, get_list→unify_value_x 666K, …): consume the
    /// whole run here in a tight loop with a small switch instead of going back
    /// around the main dispatch loop (marker check + bounds check + split-view
    /// branch + the big switch) once per opcode. Bodies are EXACT MIRRORS of the
    /// main-loop cases — keep them in sync when touching either.
    /// <c>unify_variable_y</c>,
    /// <c>unify_value_y</c> and <c>unify_structure</c> are in the run switch too
    /// (mirrored from the main loop, including the ReservedWrite / ADR-020
    /// branches and UnifyVariableY's AttVar capture), and their main-loop cases
    /// chain into this run like the X-forms always did. Profiler counts stay
    /// truthful: each opcode consumed here is recorded. On failure returns false
    /// WITHOUT touching Pc — the caller backtracks, which restores Pc from the
    /// choice point, exactly as the individual cases behave. On success Pc is
    /// written ONCE, at run exit.</summary>
    private bool RunUnifySequence(Shumway.Core.ProgramView code, byte[] codeArr, int codeLen, int pc)
    {
        while (pc < codeLen)
        {
            byte op = code.Overflow is null ? codeArr[pc] : code[pc];
            switch ((Opcode)op)
            {
                case Opcode.UnifyVariableX:
                {
                    Shumway.Core.Profiler.Opcode(op);
                    int target = ReadI32(code, codeArr, pc + 1);
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyVariableX(target);
                        pc += 5;
                        continue;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeapUnbound();
                        _engine.SetRegister(target, Cell.Ref(idx));
                    }
                    else
                    {
                        Cell src = _engine.GetHeap(ptr);
                        _engine.SetRegister(target,
                            src.Tag == Tag.AttVar ? Cell.Ref(ptr) : src);
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    pc += 5;
                    continue;
                }
                case Opcode.UnifyVariableY:   // mirror of the main-loop case
                {
                    Shumway.Core.Profiler.Opcode(op);
                    int target = ReadI32(code, codeArr, pc + 1);
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyVariableY(target);
                        pc += 5;
                        continue;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeapUnbound();
                        _engine.SetY(target, Cell.Ref(idx));
                    }
                    else
                    {
                        // See UnifyVariableX: a bare ATTVAR is captured
                        // as a REF to its home.
                        Cell src = _engine.GetHeap(ptr);
                        _engine.SetY(target,
                            src.Tag == Tag.AttVar ? Cell.Ref(ptr) : src);
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    pc += 5;
                    continue;
                }
                case Opcode.UnifyValueX:
                {
                    Shumway.Core.Profiler.Opcode(op);
                    int src = ReadI32(code, codeArr, pc + 1);
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyValueX(src);
                        pc += 5;
                        continue;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeap(1);
                        Cell v = _engine.GetRegister(src);
                        // A bare ATTVAR goes in as a REF to its home, the
                        // mirror of UnifyVariableX reading one out. Copying
                        // the cell would make a SECOND variable claiming the
                        // same attributes, and the attribute table keys on a
                        // cell's own address: the copy's lookup finds nothing.
                        _engine.SetHeap(idx,
                            v.Tag == Tag.AttVar ? Cell.Ref(v.AsHeapIndex) : v);
                        // occurs_check flag: post-store check (see main loop).
                        if (_engine.OccursMode != 0 && !_engine.OccursAllowsStoredCell(idx))
                            return false;
                    }
                    else if (!_engine.UnifyRegisterWithHeapAt(src, ptr))
                    {
                        return false;
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    pc += 5;
                    continue;
                }
                case Opcode.UnifyValueY:   // mirror of the main-loop case
                {
                    Shumway.Core.Profiler.Opcode(op);
                    int src = ReadI32(code, codeArr, pc + 1);
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyValueY(src);
                        pc += 5;
                        continue;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeap(1);
                        Cell v = _engine.GetY(src);
                        // A bare ATTVAR goes in as a REF to its home, the
                        // mirror of UnifyVariableX reading one out. Copying
                        // the cell would make a SECOND variable claiming the
                        // same attributes, and the attribute table keys on a
                        // cell's own address: the copy's lookup finds nothing.
                        _engine.SetHeap(idx,
                            v.Tag == Tag.AttVar ? Cell.Ref(v.AsHeapIndex) : v);
                        // occurs_check flag: post-store check (see main loop).
                        if (_engine.OccursMode != 0 && !_engine.OccursAllowsStoredCell(idx))
                            return false;
                    }
                    else if (!_engine.UnifyPermanentWithHeapAt(src, ptr))
                    {
                        return false;
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    pc += 5;
                    continue;
                }
                case Opcode.UnifyConstant:
                case Opcode.UnifyAtom:
                {
                    Shumway.Core.Profiler.Opcode(op);
                    Cell value = Cell.Atom(ReadI32(code, codeArr, pc + 1));
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyArgCell(value);
                        pc += 5;
                        continue;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeap(1);
                        _engine.SetHeap(idx, value);
                    }
                    else if (!_engine.UnifyHeapWithCell(ptr, value))
                    {
                        return false;
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    pc += 5;
                    continue;
                }
                case Opcode.UnifyInteger:
                {
                    Shumway.Core.Profiler.Opcode(op);
                    Cell value = Cell.Int(ReadI32(code, codeArr, pc + 1));
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyArgCell(value);
                        pc += 5;
                        continue;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeap(1);
                        _engine.SetHeap(idx, value);
                    }
                    else if (!_engine.UnifyHeapWithCell(ptr, value))
                    {
                        return false;
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    pc += 5;
                    continue;
                }
                case Opcode.UnifyNil:
                {
                    Shumway.Core.Profiler.Opcode(op);
                    Cell value = Cell.Atom(AtomTable.EmptyListId);
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyArgCell(value);
                        pc += 1;
                        continue;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        int idx = _engine.AllocateHeap(1);
                        _engine.SetHeap(idx, value);
                    }
                    else if (!_engine.UnifyHeapWithCell(ptr, value))
                    {
                        return false;
                    }
                    _engine.SetUnifyPointer(ptr + 1);
                    pc += 1;
                    continue;
                }
                case Opcode.UnifyList:
                {
                    Shumway.Core.Profiler.Opcode(op);
                    if (!_engine.UnifyList()) return false;
                    pc += 1;
                    continue;
                }
                case Opcode.UnifyStructure:   // mirror of the main-loop case
                {
                    Shumway.Core.Profiler.Opcode(op);
                    int functorId = ReadI32(code, codeArr, pc + 1);
                    if (!_engine.UnifyStructure(functorId)) return false;
                    pc += 5;
                    continue;
                }
                case Opcode.UnifyVoid:
                {
                    Shumway.Core.Profiler.Opcode(op);
                    int count = ReadI32(code, codeArr, pc + 1);
                    if (_engine.ReservedWrite)   // ADR-020
                    {
                        _engine.UnifyVoid(count);
                        pc += 5;
                        continue;
                    }
                    int ptr = _engine.UnifyPointer;
                    if (_engine.WriteMode)
                    {
                        for (int i = 0; i < count; i++)
                            _engine.AllocateHeapUnbound();
                    }
                    _engine.SetUnifyPointer(ptr + count);
                    pc += 5;
                    continue;
                }
                default:
                    _engine.SetPc(pc);
                    return true;
            }
        }
        _engine.SetPc(pc);
        return true;
    }

    /// <summary>clause-prologue / call-setup move runs. Consecutive
    /// <c>get_variable_y</c> (save args to permanents at clause entry: Blint
    /// pairs 862K+578K+294K) and consecutive <c>put_value_y</c> (load call args
    /// from permanents: 677K) never fail — fuse each run into one dispatch.
    /// Pc is written once at exit.</summary>
    private void RunGetVariableYRun(Shumway.Core.ProgramView code, byte[] codeArr, int codeLen, int pc)
    {
        while (pc + 9 <= codeLen
               && (code.Overflow is null ? codeArr[pc] : code[pc]) == (byte)Opcode.GetVariableY)
        {
            Shumway.Core.Profiler.Opcode((byte)Opcode.GetVariableY);
            int slot = ReadI32(code, codeArr, pc + 1);
            int arg = ReadI32(code, codeArr, pc + 5);
            _engine.SetY(slot, _engine.GetRegister(arg));
            pc += 9;
        }
        _engine.SetPc(pc);
    }

    private void RunPutValueYRun(Shumway.Core.ProgramView code, byte[] codeArr, int codeLen, int pc)
    {
        while (pc + 9 <= codeLen
               && (code.Overflow is null ? codeArr[pc] : code[pc]) == (byte)Opcode.PutValueY)
        {
            Shumway.Core.Profiler.Opcode((byte)Opcode.PutValueY);
            int slot = ReadI32(code, codeArr, pc + 1);
            int arg = ReadI32(code, codeArr, pc + 5);
            _engine.SetRegister(arg, _engine.GetY(slot));
            pc += 9;
        }
        _engine.SetPc(pc);
    }

    private bool TryBacktrack()
    {
        Shumway.Core.Profiler.Backtrack();
        // Wakeups belong to the computation being abandoned — drop any
        // that a failed clause queued but never ran.
        _engine.ClearPendingWakeups();
        // Loop so that an IL retry that itself fails immediately falls
        // through to the next choice point without burning stack. The
        // floor keeps an in-engine sub-goal's backtracking
        // from unwinding choice points the outer computation owns.
        while (_engine.B > _backtrackFloor)
        {
            if (_engine.TopChoicePointIsIl)
            {
                // Cancellation safe point for backtrackable-BUILTIN loops
                // (between/fail, repeat/fail) — they re-satisfy via a builtin
                // choice point (PushBuiltinChoicePoint → an IL CP) without ever
                // crossing a call-boundary MaybeCollectHeap, so this is the only
                // place the REPL's ESC can reach them. Clause-backtracking loops
                // re-satisfy through Call and are already cancellable there, so
                // they pay nothing here. Counter-throttled → negligible per-pop
                // cost even for Tier-1 IL clause backtracking.
                _engine.BacktrackSafePoint();
                // ADR-035 redo port for an IL choice point — under a debug
                // session that means a backtrackable builtin re-satisfying
                // (between/3, repeat/0, clause/2, …), since debuggable code
                // runs Tier-0. There is no bytecode retry address to report;
                // what the session needs is the reconciliation point, and B
                // still names the CP being resumed here.
                _engine.Debug?.OnRedo(_engine, -1);
                var (del, cursor) = _engine.PopIlChoicePointAndRestore();
                if (del(_engine, cursor))
                {
                    // Success: if the IL signalled a tail-call,
                    // leave Pc alone so the next dispatch picks up at the
                    // tail-call target. Otherwise resume at the caller's
                    // continuation, just like bytecode proceed would.
                    if (_engine.IlTailCallPending)
                    {
                        _engine.IlTailCallPending = false;
                        if (Activation.CpPushRing is { } r1)
                            r1[Activation.CpPushRingPos++ & (Activation.CpPushRingSize - 1)]
                                = ((long)-3 << 32) | (uint)_engine.P;
                    }
                    else
                    {
                        if (Activation.CpPushRing is { } r2)
                            r2[Activation.CpPushRingPos++ & (Activation.CpPushRingSize - 1)]
                                = ((long)-2 << 32) | (uint)_engine.Cp;
                        _engine.SetPc(_engine.Cp);
                    }
                    return true;
                }
                // The IL clause that cursor selected didn't unify — try
                // the next CP (which may be another IL CP that the just-
                // failed IL pushed before its match attempt).
                continue;
            }
            int arity = (int)_engine.GetStack(_engine.B + Activation.CpArityOffset).Data;
            int bp = (int)_engine.GetStack(_engine.B + Activation.CpBpOffset(arity)).Data;
            if (bp == Activation.SoftCutDeadBp)
            {
                // ADR-037 — this ELSE choice point was neutralised by soft_cut
                // once the condition succeeded. Restore its snapshot and pop it
                // (TrustMe), then keep backtracking: Else never runs, control
                // falls through to the choice point that preceded the *-> .
                _engine.TrustMe();
                continue;
            }
            // ADR-035 redo port. Raised BEFORE the jump, while B still names
            // the choice point being resumed — the session identifies which
            // goals died (those called after this CP was pushed) from it.
            _engine.Debug?.OnRedo(_engine, bp);
            if (Activation.CpPushRing is { } r3)
                r3[Activation.CpPushRingPos++ & (Activation.CpPushRingSize - 1)]
                    = ((long)-1 << 32) | (uint)bp;
            _engine.SetPc(bp);
            return true;
        }
        _engine.Debug?.OnFail(_engine);   // ADR-035 fail port: no CP left
        return false;
    }

    /// <summary>Returns the deref'd cell at <c>X[0]</c> (the first argument
    /// register), following REF chains so the caller sees the concrete tag.
    /// Used by every arg-0 <c>switch_on_*</c> opcode to decide where to
    /// dispatch.</summary>
    private Cell DerefA1() => DerefArg(0);

    /// <summary>Generalised <see cref="DerefA1"/>: returns the deref'd cell at
    /// <c>X[argIdx]</c>. The multi-arg indexing opcodes read
    /// arbitrary <c>A[k]</c> rather than just A1.</summary>
    private Cell DerefArg(int argIdx)
    {
        Cell c = _engine.GetRegister(argIdx);
        if (c.Tag == Tag.Ref)
            return _engine.GetHeap(_engine.Deref(c.AsHeapIndex));
        return c;
    }

    /// <summary>ADR-027 — walks a bounded sub-argument path from a deref'd
    /// argument <paramref name="cell"/>: hop <paramref name="sub0"/>, then (if
    /// <paramref name="sub1"/> &gt;= 0) hop sub1. Returns the deref'd cell
    /// reached, or false if any hop lands on a non-compound / out-of-range
    /// position — the caller then takes the switch default.</summary>
    private bool TrySubCell(Cell cell, int sub0, int sub1, out Cell result)
    {
        if (!TryHop(cell, sub0, out result)) return false;
        if (sub1 >= 0 && !TryHop(result, sub1, out result)) return false;
        return true;
    }

    /// <summary>One hop of a sub-argument path: indexes into a list cell
    /// (0 = head, 1 = tail; ADR-017 inline cons) or a struct (idx = argument
    /// position, bounds-checked against the functor arity). The result is
    /// deref'd. Returns false for any other tag or an out-of-range index.</summary>
    private bool TryHop(Cell cell, int idx, out Cell next)
    {
        next = default;
        if (cell.Tag == Tag.Lis)
        {
            if ((uint)idx > 1u) return false;
            next = DerefCell(_engine.GetHeap(cell.AsHeapIndex + idx));
            return true;
        }
        if (cell.Tag == Tag.Str)
        {
            int structIdx = cell.AsHeapIndex;
            int arity = FunctorTable.Lookup(_engine.GetHeap(structIdx).AsFunctorId).Arity;
            if ((uint)idx >= (uint)arity) return false;
            next = DerefCell(_engine.GetHeap(structIdx + 1 + idx));
            return true;
        }
        // A non-empty packed list is a cons: head/tail are computed cell
        // VALUES (no heap write) so the sub-path can key through it.
        if (cell.Tag == Tag.Pstr && cell.AsPstrLength > 0)
        {
            if ((uint)idx > 1u) return false;
            next = idx == 0
                ? _engine.PstrHeadElementCell(cell)
                : _engine.PstrTailCellValue(cell);
            return true;
        }
        return false;
    }

    private TextLiteral ResolveLiteral(int literalId)
    {
        if (literalId < 0 || literalId >= _stringLiterals.Count)
            throw new InvalidOperationException(
                $"String literal id {literalId} is out of range [0, {_stringLiterals.Count}). " +
                "Pass the literal pool to the BytecodeInterpreter constructor.");
        return _stringLiterals[literalId];
    }

    private double ResolveFloatLiteral(int literalId)
    {
        if (literalId < 0 || literalId >= _floatLiterals.Count)
            throw new InvalidOperationException(
                $"Float literal id {literalId} is out of range [0, {_floatLiterals.Count}). " +
                "Pass the float literal pool to the BytecodeInterpreter constructor.");
        return _floatLiterals[literalId];
    }

    // ADR-018 — resolves an a_eval_push leaf to a Number. kind ∈ {0 int (operand
    // is the value), 1 bigint-lit, 2 float-lit, 3 X-reg, 4 Y-slot}. For a
    // register / Y-slot the cell is deref'd and arithmetically evaluated, so a
    // variable bound to an unevaluated expression term is handled exactly as
    // is/2 would (recursively), and an unbound one raises instantiation_error.
    private System.Numerics.BigInteger ResolveBigIntLiteral(int literalId)
    {
        if (literalId < 0 || literalId >= _bigIntLiterals.Count)
            throw new InvalidOperationException(
                $"BigInt literal id {literalId} is out of range [0, {_bigIntLiterals.Count}). " +
                "Pass the BigInt literal pool to the BytecodeInterpreter constructor.");
        return _bigIntLiterals[literalId];
    }
}
