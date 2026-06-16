using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>Determines whether a builtin is "backtrackable" — i.e. it pushes a
/// choice point at runtime — by statically analysing its implementation method's
/// IL for a transitive call to a CP-creating sink (<see cref="Engine"/>'s
/// <c>PushBuiltinChoicePoint</c> / <c>PushIlChoicePoint</c>, or
/// <see cref="IndexEnumCursor.Start"/>). Derived, not declared: this replaces a
/// hand-maintained name list whose every omission was a SILENT Tier-1 IL
/// solution-loss bug (the IL emit skips the resume-marker setup for a builtin it
/// thinks is deterministic, so the cursor resumes at PC 0).
///
/// <para><b>Why reflection is safe here.</b> <c>IsBacktrackable</c> is read
/// ONLY by the IL compiler, which runs only in non-AOT contexts — the linker (a
/// build tool) and runtime promotion (gated on
/// <see cref="RuntimeFeature.IsDynamicCodeSupported"/>). Under Native AOT the IL
/// compiler never runs, so this is never reached; the guard below short-circuits
/// regardless. Results are cached globally per method, so each builtin's IL is
/// walked at most once per process.</para>
///
/// <para>Auto-handles non-deterministic <c>[PrologPredicate]</c> foreigns too:
/// their generated bridge transitively calls <c>NonDetForeignCursor</c> →
/// <c>PushBuiltinChoicePoint</c>, so they are detected without any per-foreign
/// declaration.</para></summary>
internal static class BacktrackableDetector
{
    private static readonly ConcurrentDictionary<MethodInfo, bool> _cache = new();
    private static readonly HashSet<MethodBase> _sinks = BuildSinks();

    // Operand byte counts per opcode, derived from System.Reflection.Emit.OpCodes
    // so the IL stepper is correct without a hand-written table. -1 = unknown
    // opcode, -2 = InlineSwitch (variable: 4 + 4*count).
    private static readonly int[] _oneByte = new int[256];
    private static readonly int[] _twoByte = new int[256];

    static BacktrackableDetector()
    {
        for (int i = 0; i < 256; i++) { _oneByte[i] = -1; _twoByte[i] = -1; }
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.GetValue(null) is not OpCode oc) continue;
            int len = OperandLen(oc.OperandType);
            int low = (ushort)oc.Value & 0xFF;
            if (oc.Size == 1) _oneByte[low] = len;
            else _twoByte[low] = len;   // two-byte form: 0xFE <low>
        }
    }

    private static HashSet<MethodBase> BuildSinks()
    {
        var set = new HashSet<MethodBase>();
        foreach (var m in typeof(Engine).GetMethods())
            if (m.Name is "PushBuiltinChoicePoint" or "PushIlChoicePoint")
                set.Add(m);
        foreach (var m in typeof(IndexEnumCursor).GetMethods())
            if (m.Name == "Start")
                set.Add(m);
        return set;
    }

    public static bool IsBacktrackable(BuiltinImpl impl)
    {
        // AOT never reads IsBacktrackable (the IL compiler doesn't run); short-
        // circuit so the reflection-over-IL below is never reached there.
        if (!RuntimeFeature.IsDynamicCodeSupported) return false;
        return _cache.GetOrAdd(impl.Method, m => Reaches(m, new HashSet<MethodBase>(), 0));
    }

    private const int MaxDepth = 6;

    private static bool Reaches(MethodBase method, HashSet<MethodBase> seen, int depth)
    {
        if (method is null || depth > MaxDepth || !seen.Add(method)) return false;
        if (_sinks.Contains(method)) return true;

        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch { return false; }   // abstract / no managed body / not introspectable
        if (il is null) return false;

        foreach (var callee in EnumerateCalls(method, il))
            if (callee is not null && IsShumwayMethod(callee) && Reaches(callee, seen, depth + 1))
                return true;
        return false;
    }

    private static bool IsShumwayMethod(MethodBase m)
    {
        string? ns = m.DeclaringType?.Namespace;
        return ns is not null && ns.StartsWith("Shumway", System.StringComparison.Ordinal);
    }

    // Walks the IL stream (stepping each instruction by its operand size) and
    // yields the target of every call (0x28) / callvirt (0x6F) / newobj (0x73).
    private static IEnumerable<MethodBase?> EnumerateCalls(MethodBase method, byte[] il)
    {
        Type[] typeArgs = method.DeclaringType is { IsGenericType: true } dt
            ? dt.GetGenericArguments() : System.Type.EmptyTypes;
        Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : System.Type.EmptyTypes;
        Module module = method.Module;

        int pc = 0;
        while (pc < il.Length)
        {
            byte op = il[pc];
            int operandLen;
            bool isCall = false;
            if (op == 0xFE)                       // two-byte opcode
            {
                if (pc + 1 >= il.Length) break;
                operandLen = _twoByte[il[pc + 1]];
                pc += 2;
            }
            else
            {
                operandLen = _oneByte[op];
                isCall = op is 0x28 or 0x6F or 0x73;   // call / callvirt / newobj
                pc += 1;
            }

            if (operandLen == -1) break;          // unknown opcode → give up on this method
            if (operandLen == -2)                 // InlineSwitch: int count + count*int4
            {
                if (pc + 4 > il.Length) break;
                int count = System.BitConverter.ToInt32(il, pc);
                pc += 4 + count * 4;
                continue;
            }

            if (isCall && pc + 4 <= il.Length)
            {
                int token = System.BitConverter.ToInt32(il, pc);
                MethodBase? callee = null;
                try { callee = module.ResolveMethod(token, typeArgs, methodArgs); }
                catch { /* token not a method (e.g. vararg call site) → ignore */ }
                yield return callee;
            }
            pc += operandLen;
        }
    }

    private static int OperandLen(OperandType t) => t switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI
            or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => -2,
        _ => 4,   // InlineBrTarget/Field/I/Method/Sig/String/Tok/Type, ShortInlineR
    };
}
