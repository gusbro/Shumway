using System.Reflection;
using System.Reflection.Emit;
using Shumway.Compiler.Il;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>What the IL emitter (ADR-062) checks about calls to methods it
/// does not build and about labels.
///
/// <para>A bundle's continuation methods call stubs defined on the same
/// persisted type, as MethodBuilders. To give such a call the <c>tail.</c>
/// prefix the emitter read the callee's parameters, which a MethodBuilder of
/// .NET Framework does not answer before its type is created: every
/// predicate's continuation methods failed there, halfway through, and the
/// half-emitted methods failed the whole assembly with "Bad label content in
/// ILGenerator". .NET 10's persisted builder answers, so only the net48 lane
/// saw it; a MethodBuilder of an ordinary dynamic type does not, on any
/// runtime, which is how these tests reach it.</para></summary>
public sealed class IlEmitCallsAndLabelsTests
{
    private static TypeBuilder NewType() =>
        AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("IlEmitCallsAndLabels_" + Guid.NewGuid().ToString("N")),
                AssemblyBuilderAccess.RunAndCollect)
            .DefineDynamicModule("m")
            .DefineType("T", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

    // A stub of the continuation stubs' shape, defined and not created.
    private static MethodBuilder Stub(TypeBuilder type)
    {
        var m = type.DefineMethod("Stub", MethodAttributes.Public | MethodAttributes.Static,
            typeof(bool), new[] { typeof(Activation), typeof(int) });
        var il = m.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
        return m;
    }

    private static IlEmit Caller(TypeBuilder type) =>
        IlEmit.BuildMethod(type, "Caller", MethodAttributes.Public | MethodAttributes.Static,
            CallingConventions.Standard, record: true);

    [Fact]
    public void ACallerVouchedTailCall_ToAMethodNotYetCreated_TakesTheTailPrefix()
    {
        var type = NewType();
        var stub = Stub(type);
        var emit = Caller(type);
        emit.LoadArgument(0);
        emit.LoadConstant(0);
        emit.CallTailCallable(stub);
        emit.Return();
        emit.CreateMethod();
        Assert.Contains("tail.", emit.Instructions());
        // The type builds, and the call runs.
        var created = type.CreateType()!;
        Assert.True((bool)created.GetMethod("Caller")!.Invoke(null, new object?[] { null, 0 })!);
    }

    // The trap the vouched call avoids: asking a MethodBuilder for its
    // parameters before its type exists.
    [Fact]
    public void AnOrdinaryCall_ToAMethodNotYetCreated_CannotBeJudged()
    {
        var type = NewType();
        var stub = Stub(type);
        var emit = Caller(type);
        emit.LoadArgument(0);
        emit.LoadConstant(0);
        emit.Call(stub);
        Assert.Throws<NotSupportedException>(() => emit.Return());
    }

    [Fact]
    public void ALabelOfAnotherMethod_IsRefused()
    {
        var a = IlEmit.NewDynamicMethod("a", record: false);
        var b = IlEmit.NewDynamicMethod("b", record: false);
        var foreign = a.DefineLabel("foreign");
        var e1 = Assert.Throws<IlEmitException>(() => b.Branch(foreign));
        Assert.Contains("belongs to another method", e1.Message);
        Assert.Throws<IlEmitException>(() => b.MarkLabel(foreign));
        Assert.Throws<IlEmitException>(() => b.Switch(foreign));
        // ANTI-VACUITY: the method's own label is accepted.
        var own = b.DefineLabel("own");
        b.LoadConstant(true);
        b.BranchIfTrue(own);
        b.LoadConstant(false);
        b.Return();
        b.MarkLabel(own);
        b.LoadConstant(true);
        b.Return();
        Assert.True(b.CreateDelegate(initLocals: false)(null!, 0));
    }
}
