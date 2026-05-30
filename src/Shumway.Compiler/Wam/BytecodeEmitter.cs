using Shumway.Core;

namespace Shumway.Compiler.Wam;

/// <summary>
/// Small helper for assembling a byte buffer of WAM bytecode. Each typed
/// <c>Emit*</c> method writes the opcode followed by its operands in the
/// little-endian unaligned encoding defined in ADR-006, matching what the
/// interpreter expects to read.
///
/// <para>The emitter doesn't validate that operand values fit in 32 bits — the
/// compiler should reject literal integers that overflow earlier. It does track
/// the current write position via the <see cref="Position"/> property so the
/// caller can record label offsets for back-patching.</para>
/// </summary>
public sealed class BytecodeEmitter
{
    private readonly List<byte> _bytes = new();

    public int Position => _bytes.Count;

    public byte[] ToBytes() => _bytes.ToArray();

    // ---------- Control flow ----------

    public void EmitHalt() => _bytes.Add((byte)Opcode.Halt);

    public void EmitProceed() => _bytes.Add((byte)Opcode.Proceed);

    public void EmitCall(int targetAddress, int numLivePermanents)
    {
        _bytes.Add((byte)Opcode.Call);
        EmitInt(targetAddress);
        EmitInt(numLivePermanents);
    }

    public void EmitExecute(int targetAddress)
    {
        _bytes.Add((byte)Opcode.Execute);
        EmitInt(targetAddress);
    }

    public void EmitCallBuiltin(int builtinId, int numLivePermanents)
    {
        _bytes.Add((byte)Opcode.CallBuiltin);
        EmitInt(builtinId);
        EmitInt(numLivePermanents);
    }

    public void EmitAllocate(int numPermanents)
    {
        _bytes.Add((byte)Opcode.Allocate);
        EmitInt(numPermanents);
    }

    public void EmitDeallocate() => _bytes.Add((byte)Opcode.Deallocate);

    /// <summary>Chunk 220 — fused Allocate+GetLevel (clause prologue with
    /// deep cut). Replaces the 5+5=10 bytes of two separate opcodes with
    /// a single 10-byte instruction in canonical layout
    /// <c>[op:1] [count:4] [slot:4] [Nop:1]</c>. The interpreter dispatch
    /// is halved for the most common prologue pattern in indexed
    /// predicates (14M occurrences per Blint run).</summary>
    public void EmitAllocateGetLevel(int numPermanents, int slot)
    {
        _bytes.Add((byte)Opcode.AllocateGetLevel);
        EmitInt(numPermanents);
        EmitInt(slot);
        _bytes.Add((byte)Opcode.Nop);
    }

    /// <summary>Chunk 220 — fused Deallocate+Proceed (clause epilogue
    /// when the frame was allocated). 2-byte layout
    /// <c>[op:1] [Nop:1]</c>; the interpreter handler does deallocate +
    /// the full proceed semantics (FlushPendingWakeups + SetPc(Cp), with
    /// Cp&lt;0 → Halted). 6.7M occurrences per Blint run.</summary>
    public void EmitDeallocateProceed()
    {
        _bytes.Add((byte)Opcode.DeallocateProceed);
        _bytes.Add((byte)Opcode.Nop);
    }

    // ---------- Choice-point dispatch ----------

    public void EmitTryMeElse(int nextClauseAddress, int arity)
    {
        _bytes.Add((byte)Opcode.TryMeElse);
        EmitInt(nextClauseAddress);
        EmitInt(arity);
    }

    public void EmitRetryMeElse(int nextClauseAddress)
    {
        _bytes.Add((byte)Opcode.RetryMeElse);
        EmitInt(nextClauseAddress);
    }

    public void EmitTrustMe() => _bytes.Add((byte)Opcode.TrustMe);

    /// <summary>1-byte no-op (ADR-015 chunk C step 4) — padding bytes for
    /// asserta's in-place rewrite of a clause's chain instruction.</summary>
    public void EmitNop() => _bytes.Add((byte)Opcode.Nop);

    /// <summary>ADR-015 chunk C — samples the host's <c>DbGeneration</c>
    /// into <c>engine.CurrentViewGen</c>. Emitted at the entry of every
    /// dynamic predicate so the surrounding <c>try_me_else</c> captures
    /// the call's view-generation into the choice point.</summary>
    public void EmitEnterDynamic() => _bytes.Add((byte)Opcode.EnterDynamic);

    /// <summary>ADR-015 chunk C — per-clause visibility filter for a
    /// dynamic predicate. Backtracks if the captured view-gen lies outside
    /// the half-open range <c>[born, died)</c>. <c>retract</c> patches the
    /// <c>died</c> slot in place.</summary>
    public void EmitCheckVisible(long born, long died)
    {
        _bytes.Add((byte)Opcode.CheckVisible);
        var span = new byte[16];
        BytecodeIO.WriteInt64(span, 0, born);
        BytecodeIO.WriteInt64(span, 8, died);
        _bytes.AddRange(span);
    }

    /// <summary>Indexed try: create a CP whose BP is the next opcode and jump
    /// to <paramref name="targetAddress"/>. Used in the body of an indexing
    /// bucket where each instruction points at a specific candidate
    /// clause.</summary>
    public void EmitTry(int targetAddress, int arity)
    {
        _bytes.Add((byte)Opcode.Try);
        EmitInt(targetAddress);
        EmitInt(arity);
    }

    public void EmitRetry(int targetAddress)
    {
        _bytes.Add((byte)Opcode.Retry);
        EmitInt(targetAddress);
    }

    public void EmitTrust(int targetAddress)
    {
        _bytes.Add((byte)Opcode.Trust);
        EmitInt(targetAddress);
    }

    // ---------- First-argument indexing ----------

    public void EmitSwitchOnTerm(int varAddr, int constAddr, int listAddr, int structAddr)
    {
        _bytes.Add((byte)Opcode.SwitchOnTerm);
        EmitInt(varAddr);
        EmitInt(constAddr);
        EmitInt(listAddr);
        EmitInt(structAddr);
    }

    public void EmitSwitchOnAtom(int tableId)
    {
        _bytes.Add((byte)Opcode.SwitchOnAtom);
        EmitInt(tableId);
    }

    public void EmitSwitchOnInteger(int tableId)
    {
        _bytes.Add((byte)Opcode.SwitchOnInteger);
        EmitInt(tableId);
    }

    public void EmitSwitchOnStructure(int tableId)
    {
        _bytes.Add((byte)Opcode.SwitchOnStructure);
        EmitInt(tableId);
    }

    // ---------- Multi-arg indexing (Phase 2) ----------

    public void EmitSwitchOnArg(int argIdx, int varAddr, int constAddr, int listAddr, int structAddr)
    {
        _bytes.Add((byte)Opcode.SwitchOnArg);
        EmitInt(argIdx);
        EmitInt(varAddr);
        EmitInt(constAddr);
        EmitInt(listAddr);
        EmitInt(structAddr);
    }

    public void EmitSwitchOnAtomArg(int argIdx, int tableId)
    {
        _bytes.Add((byte)Opcode.SwitchOnAtomArg);
        EmitInt(argIdx);
        EmitInt(tableId);
    }

    public void EmitSwitchOnIntegerArg(int argIdx, int tableId)
    {
        _bytes.Add((byte)Opcode.SwitchOnIntegerArg);
        EmitInt(argIdx);
        EmitInt(tableId);
    }

    public void EmitSwitchOnStructureArg(int argIdx, int tableId)
    {
        _bytes.Add((byte)Opcode.SwitchOnStructureArg);
        EmitInt(argIdx);
        EmitInt(tableId);
    }

    // ---------- Cut family ----------

    public void EmitNeckCut() => _bytes.Add((byte)Opcode.NeckCut);

    public void EmitGetLevel(int permSlot)
    {
        _bytes.Add((byte)Opcode.GetLevel);
        EmitInt(permSlot);
    }

    public void EmitCut(int permSlot)
    {
        _bytes.Add((byte)Opcode.Cut);
        EmitInt(permSlot);
    }

    /// <summary>Appends a raw byte sequence (typically a single clause's compiled
    /// bytecode) to the emitter's buffer. Used by <c>PredicateCompiler</c> when
    /// inlining each clause's body between the choice-point dispatch
    /// instructions.</summary>
    public void AppendBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        _bytes.AddRange(bytes);
    }

    // ---------- Meta ----------

    /// <summary>Emits a <see cref="Opcode.Meta"/> instruction carrying a
    /// <see cref="MetaSubOpcode.DbgInfo"/> sub-byte and a 4-byte entry id
    /// payload. The interpreter treats Meta as a runtime no-op; the
    /// payload is consumed by the stack-trace path to find each clause's
    /// source position (chunk 55).</summary>
    public void EmitMetaDbgInfo(int entryId)
    {
        _bytes.Add((byte)Opcode.Meta);
        _bytes.Add((byte)MetaSubOpcode.DbgInfo);
        EmitInt(entryId);
    }

    // ---------- Get-family (head matching) ----------

    public void EmitGetVariableX(int destSlot, int argSlot)
    {
        _bytes.Add((byte)Opcode.GetVariableX);
        EmitInt(destSlot);
        EmitInt(argSlot);
    }

    public void EmitGetValueX(int srcSlot, int argSlot)
    {
        _bytes.Add((byte)Opcode.GetValueX);
        EmitInt(srcSlot);
        EmitInt(argSlot);
    }

    public void EmitGetAtom(int atomId, int argSlot)
    {
        _bytes.Add((byte)Opcode.GetAtom);
        EmitInt(atomId);
        EmitInt(argSlot);
    }

    public void EmitGetInteger(int value, int argSlot)
    {
        _bytes.Add((byte)Opcode.GetInteger);
        EmitInt(value);
        EmitInt(argSlot);
    }

    public void EmitGetNil(int argSlot)
    {
        _bytes.Add((byte)Opcode.GetNil);
        EmitInt(argSlot);
    }

    public void EmitGetStructure(int functorId, int argSlot)
    {
        _bytes.Add((byte)Opcode.GetStructure);
        EmitInt(functorId);
        EmitInt(argSlot);
    }

    public void EmitGetList(int argSlot)
    {
        _bytes.Add((byte)Opcode.GetList);
        EmitInt(argSlot);
    }

    public void EmitGetFloat(int floatLitId, int argSlot)
    {
        _bytes.Add((byte)Opcode.GetFloat);
        EmitInt(floatLitId);
        EmitInt(argSlot);
    }

    public void EmitGetBigInt(int bigIntLitId, int argSlot)
    {
        _bytes.Add((byte)Opcode.GetBigInt);
        EmitInt(bigIntLitId);
        EmitInt(argSlot);
    }

    public void EmitGetPstr(int stringLitId, int argSlot)
    {
        _bytes.Add((byte)Opcode.GetPstr);
        EmitInt(stringLitId);
        EmitInt(argSlot);
    }

    public void EmitGetVariableY(int destPermSlot, int argSlot)
    {
        _bytes.Add((byte)Opcode.GetVariableY);
        EmitInt(destPermSlot);
        EmitInt(argSlot);
    }

    public void EmitGetValueY(int srcPermSlot, int argSlot)
    {
        _bytes.Add((byte)Opcode.GetValueY);
        EmitInt(srcPermSlot);
        EmitInt(argSlot);
    }

    // ---------- Put-family (argument preparation for calls) ----------

    public void EmitPutVariableX(int destSlot, int argSlot)
    {
        _bytes.Add((byte)Opcode.PutVariableX);
        EmitInt(destSlot);
        EmitInt(argSlot);
    }

    public void EmitPutValueX(int srcSlot, int argSlot)
    {
        _bytes.Add((byte)Opcode.PutValueX);
        EmitInt(srcSlot);
        EmitInt(argSlot);
    }

    public void EmitPutAtom(int atomId, int argSlot)
    {
        _bytes.Add((byte)Opcode.PutAtom);
        EmitInt(atomId);
        EmitInt(argSlot);
    }

    public void EmitPutInteger(int value, int argSlot)
    {
        _bytes.Add((byte)Opcode.PutInteger);
        EmitInt(value);
        EmitInt(argSlot);
    }

    public void EmitPutNil(int argSlot)
    {
        _bytes.Add((byte)Opcode.PutNil);
        EmitInt(argSlot);
    }

    public void EmitPutStructure(int functorId, int argSlot)
    {
        _bytes.Add((byte)Opcode.PutStructure);
        EmitInt(functorId);
        EmitInt(argSlot);
    }

    public void EmitPutList(int argSlot)
    {
        _bytes.Add((byte)Opcode.PutList);
        EmitInt(argSlot);
    }

    public void EmitPutFloat(int floatLitId, int argSlot)
    {
        _bytes.Add((byte)Opcode.PutFloat);
        EmitInt(floatLitId);
        EmitInt(argSlot);
    }

    public void EmitPutBigInt(int bigIntLitId, int argSlot)
    {
        _bytes.Add((byte)Opcode.PutBigInt);
        EmitInt(bigIntLitId);
        EmitInt(argSlot);
    }

    public void EmitPutPstr(int stringLitId, int argSlot)
    {
        _bytes.Add((byte)Opcode.PutPstr);
        EmitInt(stringLitId);
        EmitInt(argSlot);
    }

    public void EmitPutVariableY(int destPermSlot, int argSlot)
    {
        _bytes.Add((byte)Opcode.PutVariableY);
        EmitInt(destPermSlot);
        EmitInt(argSlot);
    }

    public void EmitPutValueY(int srcPermSlot, int argSlot)
    {
        _bytes.Add((byte)Opcode.PutValueY);
        EmitInt(srcPermSlot);
        EmitInt(argSlot);
    }

    // ---------- Unify-mode family (compound / list args) ----------

    public void EmitUnifyAtom(int atomId)
    {
        _bytes.Add((byte)Opcode.UnifyAtom);
        EmitInt(atomId);
    }

    public void EmitUnifyConstant(int atomId)
    {
        _bytes.Add((byte)Opcode.UnifyConstant);
        EmitInt(atomId);
    }

    public void EmitUnifyInteger(int value)
    {
        _bytes.Add((byte)Opcode.UnifyInteger);
        EmitInt(value);
    }

    public void EmitUnifyNil() => _bytes.Add((byte)Opcode.UnifyNil);

    public void EmitUnifyVariableX(int slot)
    {
        _bytes.Add((byte)Opcode.UnifyVariableX);
        EmitInt(slot);
    }

    public void EmitUnifyValueX(int slot)
    {
        _bytes.Add((byte)Opcode.UnifyValueX);
        EmitInt(slot);
    }

    public void EmitUnifyVariableY(int permSlot)
    {
        _bytes.Add((byte)Opcode.UnifyVariableY);
        EmitInt(permSlot);
    }

    public void EmitUnifyValueY(int permSlot)
    {
        _bytes.Add((byte)Opcode.UnifyValueY);
        EmitInt(permSlot);
    }

    public void EmitUnifyVoid(int count)
    {
        _bytes.Add((byte)Opcode.UnifyVoid);
        EmitInt(count);
    }

    public void EmitUnifyFloat(int floatLitId)
    {
        _bytes.Add((byte)Opcode.UnifyFloat);
        EmitInt(floatLitId);
    }

    public void EmitUnifyBigInt(int bigIntLitId)
    {
        _bytes.Add((byte)Opcode.UnifyBigInt);
        EmitInt(bigIntLitId);
    }

    // ---------- Helpers ----------

    /// <summary>Overwrites a 4-byte little-endian integer at
    /// <paramref name="offset"/>. Used for back-patching forward branch
    /// addresses once their targets are known.</summary>
    public void WriteIntAt(int offset, int value)
    {
        _bytes[offset]     = (byte)(value & 0xFF);
        _bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
        _bytes[offset + 2] = (byte)((value >> 16) & 0xFF);
        _bytes[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private void EmitInt(int value)
    {
        _bytes.Add((byte)(value & 0xFF));
        _bytes.Add((byte)((value >> 8) & 0xFF));
        _bytes.Add((byte)((value >> 16) & 0xFF));
        _bytes.Add((byte)((value >> 24) & 0xFF));
    }
}
