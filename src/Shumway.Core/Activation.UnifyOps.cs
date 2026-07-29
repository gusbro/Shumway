using System.Numerics;

namespace Shumway.Core;

public sealed partial class Activation
{
    // ----- Registers -----

    public Cell GetRegister(int idx) => _registers[idx];
    public void SetRegister(int idx, Cell value)
    {
        if (idx >= _registers.Length) EnsureRegisterCapacity(idx + 1);
        _registers[idx] = value;
    }

    /// <summary>Ensures the X-register bank can hold at least
    /// <paramref name="required"/> registers, doubling the backing
    /// array if needed. The initial register count
    /// (<see cref="ActivationConfig.InitialRegisterCount"/>) covers the
    /// vast majority of predicates; this growth path catches the tail
    /// where a single complex clause has more live temporaries than
    /// the initial bank holds. Existing register values are preserved.</summary>
    private void EnsureRegisterCapacity(int required)
    {
        int newSize = _registers.Length;
        while (newSize < required) newSize *= 2;
        var grown = new Cell[newSize];
        Array.Copy(_registers, grown, _registers.Length);
        _registers = grown;
    }

    // ----- Unify against registers / permanents -----
    //
    // The base Unify(int, int) operates on heap addresses. Register and permanent
    // (Y-slot) cells live outside the heap, so the helpers below "materialise" them:
    // if the cell already holds a REF we reuse its heap target directly; otherwise we
    // copy the atomic value into a freshly-allocated heap cell. This costs at most one
    // extra heap cell per atomic operand and keeps the unify implementation single-form.

    /// <summary>Unifies the cells held in <c>X[<paramref name="aRegIdx"/>]</c> and
    /// <c>X[<paramref name="bRegIdx"/>]</c>.</summary>
    public bool UnifyRegisters(int aRegIdx, int bRegIdx)
        => UnifyCells(_registers[aRegIdx], _registers[bRegIdx]);

    /// <summary>Occurs-check variant of <see cref="UnifyRegisters"/> —
    /// drives ISO <c>unify_with_occurs_check/2</c>.</summary>
    public bool UnifyRegistersWithOccursCheck(int aRegIdx, int bRegIdx)
        => UnifyCellsWithOccursCheck(_registers[aRegIdx], _registers[bRegIdx]);

    /// <summary>Unifies the cell held in <c>X[<paramref name="regIdx"/>]</c> with an
    /// immediate <paramref name="value"/> (typically a bytecode-literal cell such as
    /// <see cref="Cell.Atom"/> or <see cref="Cell.Int"/>).</summary>
    public bool UnifyRegisterWithCell(int regIdx, Cell value)
    {
        // fast paths for the two common shapes — the
        // register is unbound, or the register already holds the
        // exact same value. Both occur on every char_code /
        // peek_char dispatch (the output register is fresh; the
        // input register is bound and we compare). Either path
        // avoids the heap slot allocation + general Unify call.
        Cell rc = _registers[regIdx];
        if (rc.Tag == Tag.Ref)
        {
            int home = rc.AsHeapIndex;
            int deref = Deref(home);
            Cell d = _heap[deref];
            if (d.Tag == Tag.Ref && d.AsHeapIndex == deref)
            {
                // Truly unbound — bind it to the immediate value,
                // trailing the binding if it sits below HB. Same
                // young-to-old discipline the full Unify path uses.
                _heap[deref] = value;
                if (deref < _hb) TrailBinding(deref);
                return true;
            }
            // Bound register: same-value early-out. Saves the alloc
            // for the (very common) recheck pattern in char_code /
            // tokenizer guards.
            if (d.Tag == value.Tag && d.Data == value.Data) return true;
        }
        else if (rc.Tag == value.Tag && rc.Data == value.Data)
        {
            return true;
        }
        // General case: cell-based, no materialise (ADR-017). value is a
        // bytecode literal, so it has no heap home of its own.
        return UnifyCells(rc, value);
    }

    /// <summary>Unifies the cell held in <c>Y[<paramref name="permSlot"/>]</c> of the
    /// current environment frame with <c>X[<paramref name="regIdx"/>]</c>.</summary>
    // guard throw hoisted to ThrowNoEnv (see GetY) on the
    // three UnifyPermanent* entry points so the guards inline.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool UnifyPermanentWithRegister(int permSlot, int regIdx)
    {
        if (_e < 0) ThrowNoEnv();
        return UnifyCells(_stack[_e + EnvY1Offset + permSlot], _registers[regIdx]);
    }

    /// <summary>Unifies <c>X[<paramref name="regIdx"/>]</c> with the heap cell at
    /// <paramref name="heapIdx"/>. Used by <c>unify_value_x</c> in read mode.</summary>
    public bool UnifyRegisterWithHeapAt(int regIdx, int heapIdx)
        => UnifyCells(_registers[regIdx], Cell.Ref(heapIdx));

    /// <summary>Unifies <c>Y[<paramref name="permSlot"/>]</c> with an immediate
    /// <paramref name="value"/> cell. Used by the ADR-018 <c>a_eval_is</c> opcode
    /// when the <c>is/2</c> target is a permanent variable.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool UnifyPermanentWithCell(int permSlot, Cell value)
    {
        if (_e < 0) ThrowNoEnv();
        return UnifyCells(_stack[_e + EnvY1Offset + permSlot], value);
    }

    /// <summary>Unifies <c>Y[<paramref name="permSlot"/>]</c> with the heap cell at
    /// <paramref name="heapIdx"/>. Used by <c>unify_value_y</c> in read mode.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool UnifyPermanentWithHeapAt(int permSlot, int heapIdx)
    {
        if (_e < 0) ThrowNoEnv();
        return UnifyCells(_stack[_e + EnvY1Offset + permSlot], Cell.Ref(heapIdx));
    }

    /// <summary>Unifies the heap cell at <paramref name="heapIdx"/> with the immediate
    /// <paramref name="value"/>. Used by <c>unify_constant/atom/integer/nil</c> in read
    /// mode (the value is a literal from the bytecode).</summary>
    public bool UnifyHeapWithCell(int heapIdx, Cell value)
    {
        // Fast path for read-mode unification of a compound argument against a
        // ground atomic literal (atom / inline int / nil) from a bytecode
        // constant — the unify_atom / unify_integer / unify_constant opcodes.
        // Deref the target once and:
        //   - unbound plain var  → bind directly to the literal (skips the
        //     throwaway heap slot the general path would allocate and bind);
        //   - bound atom or int  → Data equality is exact semantic equality
        //     (atoms compare by id, ints are inline, the tag bits are part of
        //     Data so a type mismatch also compares unequal).
        // Everything else — attributed vars (the attr_unify_hook must fire),
        // BigInt / String / Pstr (table-id ≠ value), Float (two cells), or a
        // compound — takes the general alloc-then-Unify path unchanged.
        //
        // NOTE (--alloc finding): on the Van Roy suite this saves
        // zero allocations — head-level literal args go through the already-
        // optimised UnifyRegisterWithCell (get_atom/get_nil/
        // get_integer), and these benchmarks have no literals nested inside
        // compound head args (the only shape that reaches here). Kept because
        // it is correct, harmless, and a real win for programs that DO match
        // nested literals (e.g. DCG / parser heads like foo([a|T], ...)).
        //
        // The fast path applies ONLY when `value` is a genuine atomic literal
        // (Atom / inline Int). It must NOT trigger for a `value` that is a
        // REF — unify_float passes Cell.Ref(pairIdx) here (bug):
        // a Ref value against an unbound target needs Unify's young-to-old
        // BindVarToVar discipline, and against a bound value needs the full
        // recursive unify; binding the target straight to the Ref breaks
        // backtracking and float matching. Guard on the value's tag.
        if (value.Tag is Tag.Atom or Tag.Int)
        {
            int addr = Deref(heapIdx);
            Cell c = _heap[addr];
            Tag t = c.Tag;
            if (t == Tag.Ref)
            {
                Bind(addr, value);
                return true;
            }
            if (t == Tag.Atom || t == Tag.Int)
                return c.Data == value.Data;
        }

        int valueSlot = AllocateHeap(1);
        _heap[valueSlot] = value;
        return Unify(heapIdx, valueSlot);
    }

    // ----- Compound / list construction (write-mode entry points) -----

    /// <summary>
    /// Implements <c>put_structure</c>: allocates a FUNCTOR cell on the heap and stores an
    /// inline STR cell pointing at it in <c>X[<paramref name="regIdx"/>]</c> (ADR-017: no
    /// separate on-heap STR header), then enters write mode with <see cref="UnifyPointer"/>
    /// at the position where the first argument will be written.
    /// </summary>
    public void PutStructure(int functorId, int regIdx)
    {
        if (regIdx >= _registers.Length) EnsureRegisterCapacity(regIdx + 1);
        // ADR-017 phase 2: the STR tag rides inline in the register, pointing
        // straight at the FUNCTOR cell; the args follow. A structure is
        // functor + n args, not STR-header + functor + n args. Whole-structure
        // unification no longer pays a materialise copy thanks to the
        // cell-based UnifyCells path.
        int f = AllocateHeap(1);
        _heap[f] = Cell.Functor(functorId);
        _registers[regIdx] = Cell.Str(f);
        _writeMode = true;
        _reservedWrite = false;
        _unifyPointer = f + 1;
    }

    /// <summary>ADR-020 <c>put_structure_r</c>: reserve <paramref name="argCount"/>
    /// + 1 contiguous cells (functor + args) upfront and enter reserved write
    /// mode with a fresh write-pointer stack, so a non-last nested compound arg
    /// can write its ref into a pre-reserved slot and resume the parent. The
    /// reserve size is baked by the compiler — no functor-table lookup.</summary>
    public void PutStructureReserved(int functorId, int regIdx, int argCount)
    {
        if (regIdx >= _registers.Length) EnsureRegisterCapacity(regIdx + 1);
        int f = AllocateHeap(argCount + 1);
        _heap[f] = Cell.Functor(functorId);
        _registers[regIdx] = Cell.Str(f);
        _writeMode = true;
        _reservedWrite = true;
        _unifyPointer = f + 1;
        _writeSp = 0;
        PushWriteFrame(0, argCount);
    }

    /// <summary>ADR-020 <c>put_list_r</c>: reserve the 2-cell cons upfront and
    /// enter reserved write mode (the cons head may be a non-last nested
    /// compound).</summary>
    public void PutListReserved(int regIdx)
    {
        if (regIdx >= _registers.Length) EnsureRegisterCapacity(regIdx + 1);
        int pair = AllocateHeap(2);
        _registers[regIdx] = Cell.Lis(pair);
        _writeMode = true;
        _reservedWrite = true;
        _unifyPointer = pair;
        _writeSp = 0;
        PushWriteFrame(0, 2);
    }

    private void PushWriteFrame(int resume, int remaining)
    {
        if (_writeSp >= _writeResume.Length)
        {
            System.Array.Resize(ref _writeResume, _writeResume.Length * 2);
            System.Array.Resize(ref _writeRemaining, _writeRemaining.Length * 2);
        }
        _writeResume[_writeSp] = resume;
        _writeRemaining[_writeSp] = remaining;
        _writeSp++;
    }

    /// <summary>ADR-020: one arg of the current (top) reserved frame was just
    /// written and <see cref="_unifyPointer"/> already advanced. Decrement the
    /// top frame and cascade-pop every frame that reaches zero, restoring
    /// <see cref="_unifyPointer"/> to the popped frame's parent-resume slot.
    /// When the base frame pops the build is complete and reserved mode ends.</summary>
    private void OnReservedArgWritten()
    {
        _writeRemaining[_writeSp - 1]--;
        while (_writeSp > 0 && _writeRemaining[_writeSp - 1] == 0)
        {
            int resume = _writeResume[_writeSp - 1];
            _writeSp--;
            if (_writeSp > 0) _unifyPointer = resume;
            else _reservedWrite = false;
        }
    }

    /// <summary>
    /// Implements <c>get_structure</c>: derefs <c>X[<paramref name="regIdx"/>]</c> and
    /// either enters write mode (allocating a fresh compound and binding the unbound
    /// variable to it) or read mode (when the dereferenced cell is a matching STR) or
    /// fails. The <see cref="UnifyPointer"/> is positioned at the first argument cell.
    /// </summary>
    // split like GetList — an AggressiveInlining
    // fast path for the read-mode case where the register directly holds
    // an inline STR cell (ADR-017: the Str tag rides in the referring
    // slot, so no deref, no allocation — the common case when matching an
    // already-built compound), and a NoInlining cold body for everything
    // else (deref, var-binding write mode, attvar, fail).
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool GetStructure(int functorId, int regIdx)
    {
        _reservedWrite = false;   // ADR-020: head matching is never reserved
        Cell regCell = _registers[regIdx];
        if (regCell.Tag == Tag.Str)
        {
            int functorIdx = regCell.AsHeapIndex;
            if (_heap[functorIdx].AsFunctorId != functorId)
                return false;
            _writeMode = false;
            _unifyPointer = functorIdx + 1;
            return true;
        }
        return GetStructureSlow(functorId, regCell);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool GetStructureSlow(int functorId, Cell regCell)
    {
        int finalAddr = -1;
        Cell finalCell = regCell;
        // A register may hold a REF or — with attvars —
        // a bare ATTVAR cell (its payload is its own home index, so
        // Deref of it is the identity). Both name a heap home.
        if (regCell.Tag is Tag.Ref or Tag.AttVar)
        {
            finalAddr = Deref(regCell.AsHeapIndex);
            finalCell = _heap[finalAddr];
        }

        if (finalCell.Tag == Tag.AttVar)
        {
            // Attributed var — write mode. Keep the on-heap STR header:
            // BindAttVarToValue stores a Ref to a heap home for compound
            // values, and the attr machinery expects it (the heap GC bails
            // while any attvar is live, so the extra cell is immaterial).
            // the attvar machinery fires its unify hook from the queued wakeup.
            int h = AllocateHeap(2);
            _heap[h] = Cell.Str(h + 1);
            _heap[h + 1] = Cell.Functor(functorId);
            BindAttVarToValue(finalAddr, h, _heap[h]);
            _writeMode = true;
            _unifyPointer = h + 2;
            return true;
        }
        if (finalCell.Tag == Tag.Ref)
        {
            // ADR-017 phase 2: bind the plain var directly to an inline STR
            // cell pointing at the FUNCTOR cell; the args follow. No separate
            // on-heap STR header (functor + n args, not STR + functor + n).
            int f = AllocateHeap(1);
            _heap[f] = Cell.Functor(functorId);
            Bind(finalAddr, Cell.Str(f));
            _writeMode = true;
            _unifyPointer = f + 1;
            return true;
        }
        if (finalCell.Tag == Tag.Str)
        {
            int functorIdx = finalCell.AsHeapIndex;
            if (_heap[functorIdx].AsFunctorId != functorId)
                return false;
            _writeMode = false;
            _unifyPointer = functorIdx + 1;
            return true;
        }
        return false;
    }

    // ----- Unify-mode-aware dispatch helpers -----
    //
    // Each of these matches the bytecode interpreter's <c>unify_*</c>
    // opcode behaviour: in write mode allocate one heap slot and write
    // a cell; in read mode unify the cell at <c>UnifyPointer</c> against
    // the supplied value. The unify pointer advances by one in either
    // case. The Tier-1 IL compiler emits a call into the matching
    // helper instead of inlining the dispatch — saves IL volume at the
    // cost of one extra method call per unify operand.

    /// <summary>Mode-aware <c>unify_*</c> for ground value cells
    /// (atom / int / nil / float-via-ref / etc.).</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool UnifyArgCell(Cell value)
    {
        int ptr = _unifyPointer;
        if (_writeMode)
        {
            if (_reservedWrite)
            {
                // ADR-020: the cell is pre-reserved — write in place, then
                // decrement / cascade-pop the write-pointer frame stack.
                _heap[ptr] = value;
                _unifyPointer = ptr + 1;
                OnReservedArgWritten();
                return true;
            }
            int idx = AllocateHeap(1);
            _heap[idx] = value;
        }
        else
        {
            if (!UnifyHeapWithCell(ptr, value)) return false;
        }
        _unifyPointer = ptr + 1;
        return true;
    }

    /// <summary><c>unify_variable_x</c>: first occurrence of a temp
    /// variable inside a compound. In write mode allocate an unbound
    /// var on the heap and stash a REF to it in X[slot]; in read mode
    /// copy the cell at <c>UnifyPointer</c> into X[slot].</summary>
    // split into a small read-mode fast path (AggressiveInlining, so
    // the JIT inlines it into the Tier-1 IL delegate and the interpreter — the
    // common head-matching case: capture the cell at the unify pointer into a
    // temp register, no heap allocation, no register grow) and a cold slow path
    // (write mode, or a slot beyond the register bank). Surfaced as ~13% of a
    // list-processing tight loop in a dotnet-trace profile.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void UnifyVariableX(int slot)
    {
        if (!_writeMode && slot < _registers.Length)
        {
            int ptr = _unifyPointer;
            // A bare ATTVAR at the unify pointer is a variable at its home;
            // capture it as a REF to that home (a copied ATTVAR's payload would
            // no longer name its own slot). A plain REF / value copies fine.
            Cell src = _heap[ptr];
            _registers[slot] = src.Tag == Tag.AttVar ? Cell.Ref(ptr) : src;
            _unifyPointer = ptr + 1;
            return;
        }
        UnifyVariableXSlow(slot);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void UnifyVariableXSlow(int slot)
    {
        // The IL emit calls UnifyVariableX directly; unlike the bytecode
        // interpreter's opcode handler (which routes through SetRegister) we
        // don't get the auto-grow for free, so grow the bank when a
        // structure-unification slot exceeds it (Blint had unify_variable_x
        // slot=256 with a 256-register default bank).
        if (slot >= _registers.Length) EnsureRegisterCapacity(slot + 1);
        int ptr = _unifyPointer;
        if (_writeMode)
        {
            if (_reservedWrite)
            {
                // ADR-020: fresh unbound var in the pre-reserved cell at ptr.
                _heap[ptr] = Cell.UnboundVar(ptr);
                _registers[slot] = Cell.Ref(ptr);
                _unifyPointer = ptr + 1;
                OnReservedArgWritten();
                return;
            }
            int idx = AllocateHeap(1);
            _heap[idx] = Cell.UnboundVar(idx);
            _registers[slot] = Cell.Ref(idx);
        }
        else
        {
            Cell src = _heap[ptr];
            _registers[slot] = src.Tag == Tag.AttVar ? Cell.Ref(ptr) : src;
        }
        _unifyPointer = ptr + 1;
    }

    /// <summary><c>unify_value_x</c>: subsequent occurrence of a temp
    /// variable inside a compound. Same as <see cref="UnifyArgCell"/>
    /// with the cell drawn from <c>X[slot]</c>.</summary>
    public bool UnifyValueX(int slot)
    {
        Cell regVal = _registers[slot];
        return UnifyArgCell(regVal);
    }

    /// <summary><c>unify_variable_y</c>: first occurrence of a
    /// permanent variable inside a compound.</summary>
    public void UnifyVariableY(int slot)
    {
        int ptr = _unifyPointer;
        if (_writeMode)
        {
            if (_reservedWrite)
            {
                _heap[ptr] = Cell.UnboundVar(ptr);
                SetY(slot, Cell.Ref(ptr));
                _unifyPointer = ptr + 1;
                OnReservedArgWritten();
                return;
            }
            int idx = AllocateHeap(1);
            _heap[idx] = Cell.UnboundVar(idx);
            SetY(slot, Cell.Ref(idx));
        }
        else
        {
            // See UnifyVariableX: a bare ATTVAR is captured as a REF to
            // its home so its identity survives the copy.
            Cell src = _heap[ptr];
            SetY(slot, src.Tag == Tag.AttVar ? Cell.Ref(ptr) : src);
        }
        _unifyPointer = ptr + 1;
    }

    /// <summary><c>unify_value_y</c>: subsequent occurrence of a
    /// permanent variable.</summary>
    public bool UnifyValueY(int slot)
    {
        Cell yVal = GetY(slot);
        return UnifyArgCell(yVal);
    }

    /// <summary><c>unify_void</c>: skips <paramref name="count"/>
    /// anonymous variable slots. In write mode allocates fresh unbound
    /// cells; in read mode just advances the pointer.</summary>
    public void UnifyVoid(int count)
    {
        if (_writeMode)
        {
            if (_reservedWrite)
            {
                // ADR-020: the cells are pre-reserved — initialise each as a
                // fresh unbound var in place and decrement / cascade-pop per arg.
                for (int i = 0; i < count; i++)
                {
                    int p = _unifyPointer;
                    _heap[p] = Cell.UnboundVar(p);
                    _unifyPointer = p + 1;
                    OnReservedArgWritten();
                }
                return;
            }
            for (int i = 0; i < count; i++)
            {
                int idx = AllocateHeap(1);
                _heap[idx] = Cell.UnboundVar(idx);
            }
        }
        _unifyPointer += count;
    }

    /// <summary>
    /// Implements <c>put_list</c>: allocates a LIS cell pointing to the head position,
    /// stores a REF in <c>X[<paramref name="regIdx"/>]</c>, and enters write mode with
    /// <see cref="UnifyPointer"/> at the head position.
    /// </summary>
    public void PutList(int regIdx)
    {
        if (regIdx >= _registers.Length) EnsureRegisterCapacity(regIdx + 1);
        // ADR-017: store the LIS tag inline in the register, pointing
        // directly at the 2-cell [head, tail] pair the following two
        // unify_* opcodes will write (in write mode they AllocateHeap
        // sequentially starting at the current top). No separate on-heap
        // LIS header cell — a cons is 2 cells, not 3.
        int pair = _heapTop;
        _registers[regIdx] = Cell.Lis(pair);
        _writeMode = true;
        _reservedWrite = false;
        _unifyPointer = pair;
    }

    // ----- Fused cons helpers for the Tier-1 IL emit (2026-07) -----
    //
    // The IL tier's twin of the interpreter superinstructions: the
    // WAM window `get_list Ai; unify_* ; unify_*` — the complete match/build of
    // one cons cell — becomes ONE call instead of three. The generic trio pays
    // three call boundaries plus _writeMode/_unifyPointer field traffic between
    // them, and the WRITE half (building a list) lived entirely in NoInlining
    // slow paths; here both halves are straight-line, and the write half does
    // ONE two-cell bump instead of two single-cell allocations. Anything off
    // the two fast shapes (attvar, PSTR, bound non-list, register growth)
    // delegates to the exact generic sequence, so semantics are preserved by
    // construction. Emitted by IlPredicateCompiler's peephole; the bytecode
    // itself is unchanged (no new opcodes).

    /// <summary><c>get_list Ai; unify_variable_x H; unify_variable_x T</c> —
    /// destructure (read) or build (write) a cons whose head and tail are both
    /// fresh temp variables.</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool GetListVarXVarX(int reg, int h, int t)
    {
        Cell regCell = _registers[reg];
        var regs = _registers;
        if (regCell.Tag == Tag.Lis
            && (uint)h < (uint)regs.Length && (uint)t < (uint)regs.Length)
        {
            int ptr = regCell.AsHeapIndex;
            Cell hc = _heap[ptr];
            regs[h] = hc.Tag == Tag.AttVar ? Cell.Ref(ptr) : hc;
            Cell tc = _heap[ptr + 1];
            regs[t] = tc.Tag == Tag.AttVar ? Cell.Ref(ptr + 1) : tc;
            return true;
        }
        return GetListVarXVarXSlow(reg, h, t);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool GetListVarXVarXSlow(int reg, int h, int t)
    {
        Cell regCell = _registers[reg];
        if (regCell.Tag == Tag.Ref
            && h < _registers.Length && t < _registers.Length)
        {
            int home = Deref(regCell.AsHeapIndex);
            Cell fc = _heap[home];
            if (fc.Tag == Tag.Ref && fc.AsHeapIndex == home)
            {
                // WRITE: one bump for the pair; bind the var to an inline LIS
                // (ADR-017 — no on-heap header), exactly GetListSlow's layout.
                int pair = AllocateHeap(2);
                _heap[pair] = Cell.UnboundVar(pair);
                _heap[pair + 1] = Cell.UnboundVar(pair + 1);
                Bind(home, Cell.Lis(pair));
                _registers[h] = Cell.Ref(pair);
                _registers[t] = Cell.Ref(pair + 1);
                return true;
            }
        }
        // Generic route — attvar, PSTR, bound non-list, register-bank growth.
        if (!GetList(reg)) return false;
        UnifyVariableX(h);
        UnifyVariableX(t);
        return true;
    }

    /// <summary><c>get_list Ai; unify_value_x V; unify_variable_x T</c> —
    /// the cons whose head is an already-seen value (the classic list-builder
    /// clause head <c>[H|R]</c> after <c>H</c> was extracted from another
    /// argument — nreverse's <c>conc</c>, partition outputs).</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool GetListValXVarX(int reg, int v, int t)
    {
        Cell regCell = _registers[reg];
        if (regCell.Tag == Tag.Lis && (uint)t < (uint)_registers.Length)
        {
            int ptr = regCell.AsHeapIndex;
            if (!UnifyHeapWithCell(ptr, _registers[v])) return false;
            Cell tc = _heap[ptr + 1];
            _registers[t] = tc.Tag == Tag.AttVar ? Cell.Ref(ptr + 1) : tc;
            return true;
        }
        return GetListValXVarXSlow(reg, v, t);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool GetListValXVarXSlow(int reg, int v, int t)
    {
        Cell regCell = _registers[reg];
        if (regCell.Tag == Tag.Ref && t < _registers.Length)
        {
            int home = Deref(regCell.AsHeapIndex);
            Cell fc = _heap[home];
            if (fc.Tag == Tag.Ref && fc.AsHeapIndex == home)
            {
                // WRITE: store the value cell verbatim (unify_value_x write
                // semantics — UnifyArgCell's write arm), tail fresh.
                int pair = AllocateHeap(2);
                _heap[pair] = _registers[v];
                _heap[pair + 1] = Cell.UnboundVar(pair + 1);
                Bind(home, Cell.Lis(pair));
                _registers[t] = Cell.Ref(pair + 1);
                return true;
            }
        }
        if (!GetList(reg)) return false;
        if (!UnifyValueX(v)) return false;
        UnifyVariableX(t);
        return true;
    }

    /// <summary><c>get_structure f/2 Ai; unify_variable_x A; unify_variable_x B</c> —
    /// the arity-2 twin of <see cref="GetListVarXVarX"/> (serialize's
    /// <c>pair(X,Y)</c> tree nodes and every binary-constructor head).</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool GetStruct2VarXVarX(int functorId, int reg, int a, int b)
    {
        Cell regCell = _registers[reg];
        var regs = _registers;
        if (regCell.Tag == Tag.Str
            && (uint)a < (uint)regs.Length && (uint)b < (uint)regs.Length)
        {
            int f = regCell.AsHeapIndex;
            if (_heap[f].AsFunctorId != functorId) return false;
            Cell ac = _heap[f + 1];
            regs[a] = ac.Tag == Tag.AttVar ? Cell.Ref(f + 1) : ac;
            Cell bc = _heap[f + 2];
            regs[b] = bc.Tag == Tag.AttVar ? Cell.Ref(f + 2) : bc;
            return true;
        }
        return GetStruct2VarXVarXSlow(functorId, reg, a, b);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool GetStruct2VarXVarXSlow(int functorId, int reg, int a, int b)
    {
        Cell regCell = _registers[reg];
        if (regCell.Tag == Tag.Ref
            && a < _registers.Length && b < _registers.Length)
        {
            int home = Deref(regCell.AsHeapIndex);
            Cell fc = _heap[home];
            if (fc.Tag == Tag.Ref && fc.AsHeapIndex == home)
            {
                // WRITE: functor + both args in ONE bump; inline STR bind
                // (ADR-017 phase 2 — no on-heap header).
                int f = AllocateHeap(3);
                _heap[f] = Cell.Functor(functorId);
                _heap[f + 1] = Cell.UnboundVar(f + 1);
                _heap[f + 2] = Cell.UnboundVar(f + 2);
                Bind(home, Cell.Str(f));
                _registers[a] = Cell.Ref(f + 1);
                _registers[b] = Cell.Ref(f + 2);
                return true;
            }
        }
        if (!GetStructure(functorId, reg)) return false;
        UnifyVariableX(a);
        UnifyVariableX(b);
        return true;
    }

    /// <summary><c>get_structure f/2 Ai; unify_value_x A; unify_value_x B</c> —
    /// both args already-seen values (serialize's tree-rebuild heads).</summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool GetStruct2ValXValX(int functorId, int reg, int a, int b)
    {
        Cell regCell = _registers[reg];
        if (regCell.Tag == Tag.Str)
        {
            int f = regCell.AsHeapIndex;
            if (_heap[f].AsFunctorId != functorId) return false;
            return UnifyHeapWithCell(f + 1, _registers[a])
                && UnifyHeapWithCell(f + 2, _registers[b]);
        }
        return GetStruct2ValXValXSlow(functorId, reg, a, b);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool GetStruct2ValXValXSlow(int functorId, int reg, int a, int b)
    {
        Cell regCell = _registers[reg];
        if (regCell.Tag == Tag.Ref)
        {
            int home = Deref(regCell.AsHeapIndex);
            Cell fc = _heap[home];
            if (fc.Tag == Tag.Ref && fc.AsHeapIndex == home)
            {
                int f = AllocateHeap(3);
                _heap[f] = Cell.Functor(functorId);
                _heap[f + 1] = _registers[a];
                _heap[f + 2] = _registers[b];
                Bind(home, Cell.Str(f));
                return true;
            }
        }
        if (!GetStructure(functorId, reg)) return false;
        return UnifyValueX(a) && UnifyValueX(b);
    }

    /// <summary>
    /// Implements <c>get_list</c>: enters write mode against an unbound argument, read
    /// mode against a LIS, or fails. The <see cref="UnifyPointer"/> is positioned at
    /// the head cell.
    /// </summary>
    // split into a small read-mode fast path (AggressiveInlining: the
    // register directly holds an inline LIS cell — ADR-017 — so no deref, no
    // allocation; the common case when consuming an existing list) and a cold
    // slow path (deref, var-binding write mode, attvar, fail). ~10% of a
    // list-processing tight loop in a dotnet-trace profile.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool GetList(int regIdx)
    {
        _reservedWrite = false;   // ADR-020: head matching is never reserved
        Cell regCell = _registers[regIdx];
        if (regCell.Tag == Tag.Lis)
        {
            _writeMode = false;
            _unifyPointer = regCell.AsHeapIndex;
            return true;
        }
        return GetListSlow(regCell);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool GetListSlow(Cell regCell)
    {
        int finalAddr = -1;
        Cell finalCell = regCell;
        // REF or a bare ATTVAR cell — both name a heap home;
        // Deref of an ATTVAR is the identity since it isn't a REF.
        if (regCell.Tag is Tag.Ref or Tag.AttVar)
        {
            finalAddr = Deref(regCell.AsHeapIndex);
            finalCell = _heap[finalAddr];
        }

        if (finalCell.Tag == Tag.AttVar)
        {
            // Attributed var: keep the on-heap LIS header. BindAttVarToValue
            // stores a Ref to a heap home for compound values, and the attr
            // machinery expects that home. Rare path (and the heap GC bails
            // while any attvar is live), so the extra cell is immaterial.
            int h = AllocateHeap(1);
            _heap[h] = Cell.Lis(h + 1);
            BindAttVarToValue(finalAddr, h, _heap[h]);
            _writeMode = true;
            _unifyPointer = h + 1;
            return true;
        }
        if (finalCell.Tag == Tag.Ref)
        {
            // ADR-017: bind the plain var directly to an inline LIS cell
            // pointing at the [head, tail] pair the following unify_* will
            // write — no separate header cell (2-cell cons).
            int pair = _heapTop;
            Bind(finalAddr, Cell.Lis(pair));
            _writeMode = true;
            _unifyPointer = pair;
            return true;
        }
        if (finalCell.Tag == Tag.Lis)
        {
            _writeMode = false;
            _unifyPointer = finalCell.AsHeapIndex;
            return true;
        }
        if (finalCell.Tag == Tag.Pstr && finalCell.AsPstrLength > 0)
        {
            // A partial string IS the code list it represents: lazily uncons
            // the first [Code|Tail] pair for the following unify-run
            // (mirrors UnifyPstrLis — heads are UTF-16 code units). Without
            // this, a callee head-matching [H|T] failed on a PSTR argument
            // even though inline =/2 unified it fine.
            int pair = AllocateHeap(2);
            _heap[pair] = Cell.Int(GetPstrCodeUnit(finalCell, 0));
            if (finalCell.AsPstrLength == 1)
            {
                _heap[pair + 1] = Cell.Ref(ComputePstrTailIndex(finalCell));
            }
            else
            {
                int absoluteStart = finalCell.AsPstrOffset + 1;
                _heap[pair + 1] = Cell.Pstr(
                    finalCell.AsPstrLength - 1,
                    finalCell.AsPstrBufferIndex
                        + absoluteStart / Cell.PstrCodeUnitsPerBuffer,
                    absoluteStart % Cell.PstrCodeUnitsPerBuffer);
            }
            _writeMode = false;
            _unifyPointer = pair;
            return true;
        }
        return false;
    }

    /// <summary>ADR-019 <c>unify_structure</c>: build (write) or match (read) a
    /// nested compound at the parent's current argument and descend into its
    /// args. Write mode allocates the nested FUNCTOR cell, writes an inline STR
    /// ref to it into the parent's current arg slot, and moves
    /// <see cref="UnifyPointer"/> to the nested's first arg. Read mode derefs the
    /// parent's current arg and binds it (var → fresh structure, switch to write)
    /// or matches it (STR with the same functor, stay read), failing otherwise.
    /// Emitted only for a nested compound in the LAST argument position, so the
    /// parent is never resumed — the build stays linear, no write-pointer stack.</summary>
    public bool UnifyStructure(int functorId)
    {
        if (_writeMode)
        {
            if (_reservedWrite)
            {
                // ADR-020: nested compound inside a reserved build. The parent's
                // slot at _unifyPointer is pre-reserved; write the STR ref there,
                // reserve the nested (functor + args) at the heap top, push a
                // frame so the cascade resumes the parent when the nested fills.
                // arity is looked up here (the minority reserved path), not in the
                // hot on-demand path below.
                var (_, arity) = FunctorTable.Lookup(functorId);
                int nested = AllocateHeap(arity + 1);
                _heap[nested] = Cell.Functor(functorId);
                _heap[_unifyPointer] = Cell.Str(nested);
                int parentResume = _unifyPointer + 1;
                _writeRemaining[_writeSp - 1]--;   // parent slot filled (no cascade yet)
                PushWriteFrame(parentResume, arity);
                _unifyPointer = nested + 1;
                return true;
            }
            int slot = AllocateHeap(1);          // parent's current arg slot
            int f = AllocateHeap(1);             // nested FUNCTOR cell (contiguous)
            _heap[f] = Cell.Functor(functorId);
            _heap[slot] = Cell.Str(f);
            _unifyPointer = f + 1;
            return true;
        }
        int addr = Deref(_unifyPointer);
        Cell cell = _heap[addr];
        if (cell.Tag == Tag.AttVar)
        {
            int h = AllocateHeap(2);
            _heap[h] = Cell.Str(h + 1);
            _heap[h + 1] = Cell.Functor(functorId);
            BindAttVarToValue(addr, h, _heap[h]);
            _writeMode = true;
            _unifyPointer = h + 2;
            return true;
        }
        if (cell.Tag == Tag.Ref)
        {
            int f = AllocateHeap(1);
            _heap[f] = Cell.Functor(functorId);
            Bind(addr, Cell.Str(f));
            _writeMode = true;
            _unifyPointer = f + 1;
            return true;
        }
        if (cell.Tag == Tag.Str)
        {
            int functorIdx = cell.AsHeapIndex;
            if (_heap[functorIdx].AsFunctorId != functorId) return false;
            _unifyPointer = functorIdx + 1;      // stays read mode
            return true;
        }
        return false;
    }

    /// <summary>ADR-019 <c>unify_list</c>: build (write) or match (read) a nested
    /// list cell at the parent's current argument, descending to its head. The
    /// list-tail counterpart of <see cref="UnifyStructure"/>; uses the ADR-017
    /// inline 2-cell cons (no header). Last-argument position only.</summary>
    public bool UnifyList()
    {
        if (_writeMode)
        {
            if (_reservedWrite)
            {
                // ADR-020: nested cons inside a reserved build. Write the LIS ref
                // into the pre-reserved parent slot, reserve the 2-cell cons at
                // the heap top, descend to its head.
                int pair = AllocateHeap(2);
                _heap[_unifyPointer] = Cell.Lis(pair);
                int parentResume = _unifyPointer + 1;
                _writeRemaining[_writeSp - 1]--;
                PushWriteFrame(parentResume, 2);
                _unifyPointer = pair;
                return true;
            }
            int slot = AllocateHeap(1);          // parent's current arg slot
            _heap[slot] = Cell.Lis(_heapTop);    // points at the cons (next 2 cells)
            _unifyPointer = _heapTop;
            return true;
        }
        int addr = Deref(_unifyPointer);
        Cell cell = _heap[addr];
        if (cell.Tag == Tag.AttVar)
        {
            int h = AllocateHeap(1);
            _heap[h] = Cell.Lis(h + 1);
            BindAttVarToValue(addr, h, _heap[h]);
            _writeMode = true;
            _unifyPointer = h + 1;
            return true;
        }
        if (cell.Tag == Tag.Ref)
        {
            int pair = _heapTop;
            Bind(addr, Cell.Lis(pair));
            _writeMode = true;
            _unifyPointer = pair;
            return true;
        }
        if (cell.Tag == Tag.Lis)
        {
            _unifyPointer = cell.AsHeapIndex;    // stays read mode
            return true;
        }
        return false;
    }

    private int MaterializeRegister(int regIdx)
    {
        Cell c = _registers[regIdx];
        // REF and ATTVAR both carry a heap home index in
        // their payload, so either already names a heap cell.
        if (c.Tag is Tag.Ref or Tag.AttVar) return c.AsHeapIndex;
        int slot = AllocateHeap(1);
        _heap[slot] = c;
        return slot;
    }

    /// <summary>Public wrapper around <see cref="MaterializeRegister"/>
    /// for diagnostic instrumentation (e.g. <c>RetractTrace</c>) that
    /// needs to inspect the same heap address the unify path would
    /// produce. Allocates a fresh heap cell for non-REF/AttVar
    /// register values exactly like the private overload —
    /// idempotent if called twice in a row only when the register
    /// already names a heap home.</summary>
    public int MaterializeRegisterForTrace(int regIdx)
        => MaterializeRegister(regIdx);

}
