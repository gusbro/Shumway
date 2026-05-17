using System.Reflection;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>
/// Tier-1 IL compiler MVP. Translates a tiny subset of the WAM bytecode
/// — single-clause facts whose head args are all bare atoms, with no
/// body — into a <see cref="PredicateDelegate"/> via Sigil's typed IL
/// emission. The output runs without going through the bytecode
/// dispatch loop, giving a first taste of the Tier-1 path the rest of
/// ADR-011 will fill out.
///
/// <para>Supported opcodes (Phase-1 MVP): <c>get_atom</c>,
/// <c>proceed</c>. Everything else throws
/// <see cref="NotSupportedException"/> — gracefully fall back to Tier 0
/// in the caller.</para>
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

    /// <summary>Returns <c>true</c> iff <paramref name="predicate"/> is in
    /// the supported subset: single clause, bytecode made of zero or
    /// more <c>get_atom</c> / <c>get_integer</c> opcodes followed by
    /// exactly one <c>proceed</c>.</summary>
    public bool CanCompile(CompiledPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (predicate.ClauseCount != 1) return false;
        byte[] code = predicate.Bytecode;
        int pc = 0;
        bool sawProceed = false;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.GetAtom || op == Opcode.GetInteger)
            {
                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.Proceed)
            {
                sawProceed = true;
                pc += 1;
                continue;
            }
            return false;
        }
        return sawProceed;
    }

    /// <summary>Emits a <see cref="PredicateDelegate"/> for the predicate.
    /// The caller is responsible for first checking
    /// <see cref="CanCompile"/>; passing in an unsupported predicate
    /// raises <see cref="NotSupportedException"/> partway through
    /// emission.</summary>
    public PredicateDelegate Compile(CompiledPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (!CanCompile(predicate))
            throw new NotSupportedException(
                $"Predicate is outside the MVP IL subset (clauses="
                + $"{predicate.ClauseCount}, bytecode bytes={predicate.Bytecode.Length}).");

        var emit = Sigil.Emit<PredicateDelegate>.NewDynamicMethod(
            $"ShumwayIl_{predicate.FunctorId}_{predicate.Arity}");
        var failLabel = emit.DefineLabel("fail");

        byte[] code = predicate.Bytecode;
        int pc = 0;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (op == Opcode.GetAtom)
            {
                int atomId = BytecodeIO.ReadInt32(code, pc + 1);
                int regIdx = BytecodeIO.ReadInt32(code, pc + 5);

                // emit: if (!engine.UnifyRegisterWithCell(regIdx,
                //              Cell.Atom(atomId))) goto fail;
                emit.LoadArgument(0);                  // engine
                emit.LoadConstant(regIdx);             // arg 1: regIdx
                emit.LoadConstant(atomId);             // arg 2 setup
                emit.Call(CellAtomMethod);             // → Cell on stack
                emit.Call(EngineUnifyMethod);          // bool on stack
                emit.BranchIfFalse(failLabel);

                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.GetInteger)
            {
                int value = BytecodeIO.ReadInt32(code, pc + 1);
                int regIdx = BytecodeIO.ReadInt32(code, pc + 5);

                // emit: if (!engine.UnifyRegisterWithCell(regIdx,
                //              Cell.Int((long)value))) goto fail;
                emit.LoadArgument(0);
                emit.LoadConstant(regIdx);
                emit.LoadConstant((long)value);
                emit.Call(CellIntMethod);
                emit.Call(EngineUnifyMethod);
                emit.BranchIfFalse(failLabel);

                pc += OpcodeTable.Get(op).Size;
                continue;
            }
            if (op == Opcode.Proceed)
            {
                // emit: return true;
                emit.LoadConstant(true);
                emit.Return();
                pc += 1;
                continue;
            }
            // CanCompile rejected this above; defensive guard.
            throw new NotSupportedException(
                $"IL emission hit unsupported opcode 0x{(byte)op:X2} at pc={pc}.");
        }

        // Failure tail. Reached when one of the get_atom branches missed.
        emit.MarkLabel(failLabel);
        emit.LoadConstant(false);
        emit.Return();

        return emit.CreateDelegate();
    }
}
