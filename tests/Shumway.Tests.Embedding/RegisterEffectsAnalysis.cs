using System.Reflection;
using System.Reflection.Emit;
using Shumway.Compiler.Il;
using Shumway.Core;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-060 item 3: what a method does to the machine registers,
/// read off its IL and the IL of everything it calls. A field access names
/// the register; a call the analysis cannot follow (virtual, through a
/// delegate, a function pointer) makes the result unknown, which the caller
/// treats as reading and writing every register.</summary>
internal static class RegisterEffectsAnalysis
{
    public sealed record Result(MachineRegs Reads, MachineRegs Writes, bool Throws, bool Unknown,
        IReadOnlyList<string> Notes)
    {
        /// <summary>The row a helper with these effects needs: an unknown
        /// method reads and writes everything; one that can throw also reads
        /// what the catch resolution reads before it rolls the machine back
        /// (the trial unification of the ball at the current heap top, and the
        /// trail unwind from the current top).</summary>
        public HelperEffects Row => Unknown
            ? HelperEffects.Everything
            : new(Throws ? Reads | ThrowReads : Reads, Writes);

        public const MachineRegs ThrowReads = MachineRegs.HeapTop | MachineRegs.Hb | MachineRegs.TrailTop;
    }

    private static readonly Dictionary<string, MachineRegs> FieldRegisters = new()
    {
        [nameof(Activation._e)] = MachineRegs.E,
        [nameof(Activation._cp)] = MachineRegs.Cp,
        [nameof(Activation._b)] = MachineRegs.B,
        [nameof(Activation._b0)] = MachineRegs.B0,
        [nameof(Activation._stackTop)] = MachineRegs.StackTop,
        [nameof(Activation._heapTop)] = MachineRegs.HeapTop,
        [nameof(Activation._hb)] = MachineRegs.Hb,
        [nameof(Activation._bindingTrailTop)] = MachineRegs.TrailTop,
        [nameof(Activation._stack)] = MachineRegs.StackArray,
        ["_heap"] = MachineRegs.HeapArray,
        [nameof(Activation._registers)] = MachineRegs.RegisterArray,
        ["_bindingTrail"] = MachineRegs.TrailArray,
    };

    // Exceptions a helper throws by design, which a region's caller handles
    // with the machine's state restored from the fields. An argument or
    // state exception is a bug, not a path the state must survive.
    private static bool ThrownByDesign(Type t)
        => typeof(PrologRuntimeException).IsAssignableFrom(t)
           || typeof(PrologHaltException).IsAssignableFrom(t)
           || typeof(OperationCanceledException).IsAssignableFrom(t);

    private static readonly Dictionary<short, OpCode> OpCodesByValue = BuildOpCodes();

    private static Dictionary<short, OpCode> BuildOpCodes()
    {
        var d = new Dictionary<short, OpCode>();
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (f.GetValue(null) is OpCode op) d[op.Value] = op;
        return d;
    }

    private sealed class Walk
    {
        public readonly Dictionary<MethodBase, Result> Done = new();
        public readonly HashSet<MethodBase> InProgress = new();
    }

    public static Result Analyze(MethodBase root) => new Walk().Of(root, out _);

    /// <summary>The effects of <paramref name="m"/> and of everything it
    /// calls. <paramref name="complete"/> is false when a call in progress
    /// (a cycle) was met, in which case the result is not memoized: it is
    /// right for the root that asked, not for the method on its own.</summary>
    private static Result Of(this Walk w, MethodBase m, out bool complete)
    {
        complete = true;
        if (w.Done.TryGetValue(m, out var done)) return done;
        if (!w.InProgress.Add(m))
        {
            complete = false;
            return new Result(MachineRegs.None, MachineRegs.None, false, false, Array.Empty<string>());
        }
        try
        {
            var r = w.Body(m, ref complete);
            if (complete) w.Done[m] = r;
            return r;
        }
        finally
        {
            w.InProgress.Remove(m);
        }
    }

    private static bool IsOurs(Type? t)
        => t?.Assembly.GetName().Name is { } n && n.StartsWith("Shumway", StringComparison.Ordinal);

    private static Result Body(this Walk w, MethodBase m, ref bool complete)
    {
        MachineRegs reads = MachineRegs.None, writes = MachineRegs.None;
        bool throws = false, unknown = false;
        var notes = new List<string>();
        string Where(string what) => $"{m.DeclaringType?.Name}.{m.Name}: {what}";

        var body = m.GetMethodBody();
        byte[]? il = body?.GetILAsByteArray();
        if (il is null)
        {
            // No body: abstract, extern, or a runtime-provided method such
            // as a delegate's Invoke.
            if (IsOurs(m.DeclaringType) || typeof(Delegate).IsAssignableFrom(m.DeclaringType))
            {
                unknown = true;
                notes.Add(Where("no IL body"));
            }
            return new Result(reads, writes, throws, unknown, notes);
        }
        var module = m.Module;
        Type[]? typeArgs = m.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null;
        Type[]? methodArgs = m is MethodInfo { IsGenericMethod: true } gm ? gm.GetGenericArguments() : null;

        int pc = 0;
        while (pc < il.Length)
        {
            short value = il[pc];
            if (value == 0xFE) value = (short)(0xFE00 | il[pc + 1]);
            var op = OpCodesByValue[value];
            pc += op.Size;
            int token = 0;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: pc += 1; break;
                case OperandType.InlineVar: pc += 2; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: pc += 8; break;
                case OperandType.InlineSwitch:
                    pc += 4 + 4 * BitConverter.ToInt32(il, pc); break;
                default:
                    token = BitConverter.ToInt32(il, pc); pc += 4; break;
            }

            if (op == OpCodes.Ldfld || op == OpCodes.Ldflda || op == OpCodes.Stfld)
            {
                FieldInfo field;
                try { field = module.ResolveField(token, typeArgs, methodArgs)!; }
                catch (Exception) { unknown = true; notes.Add(Where("unresolved field")); continue; }
                if (field.DeclaringType != typeof(Activation)
                    || !FieldRegisters.TryGetValue(field.Name, out var reg)) continue;
                if (op == OpCodes.Stfld) writes |= reg;
                else if (op == OpCodes.Ldfld) reads |= reg;
                else { reads |= reg; writes |= reg; }   // an address: either
            }
            else if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj)
            {
                MethodBase callee;
                try { callee = module.ResolveMethod(token, typeArgs, methodArgs)!; }
                catch (Exception) { unknown = true; notes.Add(Where("unresolved method")); continue; }
                if (op == OpCodes.Newobj && callee.DeclaringType is { } created
                    && typeof(Exception).IsAssignableFrom(created))
                {
                    if (ThrownByDesign(created)) { throws = true; notes.Add(Where($"throws {created.Name}")); }
                    continue;
                }
                if (typeof(Delegate).IsAssignableFrom(callee.DeclaringType) && callee.Name == "Invoke")
                {
                    unknown = true; notes.Add(Where("invokes a delegate")); continue;
                }
                if (!IsOurs(callee.DeclaringType)) continue;   // the framework has no access to the fields
                if (callee.IsVirtual && !callee.IsFinal && !(callee.DeclaringType?.IsSealed ?? false))
                {
                    unknown = true; notes.Add(Where($"virtual call {callee.DeclaringType?.Name}.{callee.Name}")); continue;
                }
                var sub = w.Of(callee, out bool subComplete);
                complete &= subComplete;
                reads |= sub.Reads; writes |= sub.Writes;
                throws |= sub.Throws; unknown |= sub.Unknown;
                if (sub.Unknown || sub.Throws)
                    foreach (var n in sub.Notes) if (notes.Count < 8) notes.Add(n);
            }
            else if (op == OpCodes.Calli || op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn)
            {
                unknown = true; notes.Add(Where(op.Name ?? "calli"));
            }
        }
        return new Result(reads, writes, throws, unknown, notes);
    }
}
