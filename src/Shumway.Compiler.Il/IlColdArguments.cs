using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>ADR-063: which arguments a predicate's local callers always pass
/// fresh, a variable first met at the call, read off the callers' bytecode:
/// the last write to the argument register before the call is a
/// <c>put_variable</c>. A head stream matching such an argument never runs
/// in read mode from those callers, so a region past the cap on locals may
/// leave it to the matcher at no cost to a read.</summary>
internal static class IlColdArguments
{
    /// <summary>By callee functor id, for the callees with a local call site:
    /// bit <c>i</c> set when argument <c>i</c> (of the first 63) is fresh at
    /// every site.</summary>
    public static Dictionary<int, long> FreshArguments(IReadOnlyDictionary<int, CompiledPredicate> callers)
    {
        var fresh = new Dictionary<int, long>();
        foreach (var caller in callers.Values) Scan(caller, fresh);
        return fresh;
    }

    // A linear walk; a branch or a call resets what is known, so a register
    // is fresh only when a put_variable wrote it in the straight run before
    // the call.
    private static void Scan(CompiledPredicate caller, Dictionary<int, long> fresh)
    {
        byte[] code = caller.BytecodeUnfused;
        var sites = caller.CallSites;
        long freshRegs = 0;
        bool unknown = false;   // a write to a register the walk does not read: nothing is fresh at the next call
        for (int pc = 0; pc < code.Length;)
        {
            var op = (Opcode)code[pc];
            int size = op == Opcode.Meta ? 6 : OpcodeTable.Get((byte)op).Size;
            if (size <= 0) return;
            switch (op)
            {
                case Opcode.PutVariableX:
                case Opcode.PutVariableY:
                    Mark(ref freshRegs, BytecodeIO.ReadInt32(code, pc + 5), true);
                    break;
                case Opcode.PutValueX:
                case Opcode.PutValueY:
                case Opcode.PutConstant:
                case Opcode.PutInteger:
                case Opcode.PutAtom:
                case Opcode.PutStructure:
                case Opcode.PutFloat:
                case Opcode.PutBigInt:
                case Opcode.PutBlit:
                case Opcode.PutPstr:
                    Mark(ref freshRegs, BytecodeIO.ReadInt32(code, pc + 5), false);
                    break;
                case Opcode.PutNil:
                case Opcode.PutList:
                case Opcode.PutListR:
                case Opcode.GetVariableX:
                case Opcode.UnifyVariableX:
                    Mark(ref freshRegs, BytecodeIO.ReadInt32(code, pc + 1), false);
                    break;
                case Opcode.PutStructureR:
                case Opcode.PutRational:
                    unknown = true;
                    break;
                case Opcode.Call:
                case Opcode.Execute:
                case Opcode.CallIl:
                case Opcode.ExecuteIl:
                case Opcode.CallBytecode:
                case Opcode.ExecuteBytecode:
                {
                    int fid = Callee(sites, pc);
                    if (fid >= 0)
                    {
                        int arity = FunctorTable.Lookup(fid).Arity;
                        long mask = unknown ? 0 : freshRegs & (arity >= 63 ? -1L : (1L << arity) - 1);
                        fresh[fid] = fresh.TryGetValue(fid, out long m) ? m & mask : mask;
                    }
                    freshRegs = 0;
                    unknown = false;
                    break;
                }
                case Opcode.CallBuiltin:
                case Opcode.ExecuteBuiltin:
                case Opcode.Proceed:
                case Opcode.TryMeElse:
                case Opcode.RetryMeElse:
                case Opcode.TrustMe:
                case Opcode.Jump:
                case Opcode.SoftCut:
                    freshRegs = 0;
                    unknown = false;
                    break;
            }
            pc += size;
        }
    }

    private static void Mark(ref long freshRegs, int reg, bool fresh)
    {
        if (reg < 0 || reg >= 63) return;
        if (fresh) freshRegs |= 1L << reg;
        else freshRegs &= ~(1L << reg);
    }

    private static int Callee(IReadOnlyList<CallSite> sites, int pc)
    {
        int lo = 0, hi = sites.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int off = sites[mid].OpcodeOffset;
            if (off == pc) return sites[mid].CalleeFunctorId;
            if (off < pc) lo = mid + 1;
            else hi = mid - 1;
        }
        for (int i = 0; i < sites.Count; i++)
            if (sites[i].OpcodeOffset == pc) return sites[i].CalleeFunctorId;
        return -1;
    }

    /// <summary>By <c>unify_blit</c> site of a code: the head argument whose
    /// part it matches, through the temporaries from the argument's own
    /// stream down; -1 when the walk cannot tell.</summary>
    public static Dictionary<int, int> PartArguments(byte[] code, int arity)
    {
        var owner = new Dictionary<int, int>();   // a temporary's argument
        var result = new Dictionary<int, int>();
        int current = -1;
        for (int pc = 0; pc < code.Length;)
        {
            var op = (Opcode)code[pc];
            int size = op == Opcode.Meta ? 6 : OpcodeTable.Get((byte)op).Size;
            if (size <= 0) return result;
            switch (op)
            {
                case Opcode.GetStructure:
                    current = ArgumentOf(BytecodeIO.ReadInt32(code, pc + 5), arity, owner);
                    break;
                case Opcode.GetList:
                    current = ArgumentOf(BytecodeIO.ReadInt32(code, pc + 1), arity, owner);
                    break;
                case Opcode.GetBlit:
                    current = BytecodeIO.ReadInt32(code, pc + 5);
                    break;
                case Opcode.UnifyVariableX:
                    if (current >= 0) owner[BytecodeIO.ReadInt32(code, pc + 1)] = current;
                    break;
                case Opcode.UnifyBlit:
                {
                    int temp = BytecodeIO.ReadInt32(code, pc + 5);
                    current = owner.TryGetValue(temp, out int a) ? a : -1;
                    result[pc] = current;
                    break;
                }
                case Opcode.SkipIfCopied:
                case Opcode.Meta:
                    break;
                default:
                    if (!IsHeadOp(op))
                    {
                        current = -1;
                        owner.Clear();   // temporaries are a clause's
                    }
                    break;
            }
            pc += size;
        }
        return result;
    }

    private static int ArgumentOf(int reg, int arity, Dictionary<int, int> owner)
        => reg < arity ? reg : owner.TryGetValue(reg, out int a) ? a : -1;

    private static bool IsHeadOp(Opcode op) => op is
        Opcode.GetAtom or Opcode.GetInteger or Opcode.GetFloat or Opcode.GetNil or Opcode.GetConstant
        or Opcode.GetVariableX or Opcode.GetVariableY or Opcode.GetValueX or Opcode.GetValueY
        or Opcode.GetBigInt or Opcode.GetRational or Opcode.GetPstr
        or Opcode.UnifyAtom or Opcode.UnifyInteger or Opcode.UnifyNil or Opcode.UnifyConstant
        or Opcode.UnifyVariableY or Opcode.UnifyValueX or Opcode.UnifyValueY or Opcode.UnifyVoid
        or Opcode.UnifyFloat or Opcode.UnifyBigInt or Opcode.UnifyRational
        or Opcode.UnifyStructure or Opcode.UnifyList;
}
