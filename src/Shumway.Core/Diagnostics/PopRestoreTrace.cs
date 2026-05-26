using System.Diagnostics;

namespace Shumway.Core.Diagnostics;

/// <summary>Opt-in trace points around
/// <see cref="Engine.PopIlChoicePointAndRestore"/>. Activated by the
/// <c>SHUMWAY_RETRACT_TRACE</c> compile constant (set via the
/// <c>ShumwayRetractTrace=true</c> MSBuild property). Zero runtime
/// cost when the symbol is not defined.</summary>
public static class PopRestoreTrace
{
    private const string TraceSymbol = "SHUMWAY_RETRACT_TRACE";

    [Conditional(TraceSymbol)]
    public static void PrePop(Engine engine, int b)
    {
        int arity = (int)engine.GetStack(b + Engine.CpArityOffset).Data;
        Cell savedArg0 = arity > 0
            ? engine.GetStack(b + Engine.CpArg1Offset)
            : default;
        Console.Error.WriteLine(
            $"[poprestore] PRE-POP: B={b} arity={arity} "
            + $"savedReg0={Describe(savedArg0)}");
    }

    [Conditional(TraceSymbol)]
    public static void PostRestore(Engine engine, int arity)
    {
        Cell r0 = arity > 0 ? engine.GetRegister(0) : default;
        Console.Error.WriteLine(
            $"[poprestore] POST-RESTORE: arity={arity} reg[0]={Describe(r0)}");
    }

    private static string Describe(Cell c)
    {
        return c.Tag switch
        {
            Tag.Ref => $"Ref({c.AsHeapIndex})",
            Tag.Atom => $"Atom({c.AsAtomId})",
            Tag.Int => $"Int({c.AsInt})",
            Tag.Functor => $"Functor({c.AsFunctorId})",
            Tag.Str => $"Str(->{c.AsHeapIndex})",
            Tag.Lis => $"Lis(->{c.AsHeapIndex})",
            _ => $"{c.Tag}({c.AsHeapIndex})",
        };
    }
}
