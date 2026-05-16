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

    // ---------- Helpers ----------

    private void EmitInt(int value)
    {
        _bytes.Add((byte)(value & 0xFF));
        _bytes.Add((byte)((value >> 8) & 0xFF));
        _bytes.Add((byte)((value >> 16) & 0xFF));
        _bytes.Add((byte)((value >> 24) & 0xFF));
    }
}
