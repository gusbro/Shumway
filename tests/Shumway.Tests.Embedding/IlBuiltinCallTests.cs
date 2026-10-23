using Shumway.Builtins;
using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Tier-1 code compiled in this process calls a builtin that is a
/// static method directly, and a builtin registered as a lambda through its
/// delegate. Exclusive: the emitter's callee record is process-wide.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class IlBuiltinCallTests
{
    private static int Fid(string n, int a) =>
        FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, a);

    private static int _lambdaCalls;

    [Fact]
    public void AStaticBuiltinIsCalledDirectlyAndALambdaThroughItsDelegate()
    {
        var e = new PrologEngine();   // registers the builtins
        int lambdaId = BuiltinsRegistry.Register("$il_builtin_call_probe", 1,
            engine => engine.UnifyRegisterWithCell(0, Cell.Int(++_lambdaCalls)));
        var lambda = BuiltinsRegistry.GetById(lambdaId).Impl;
        Assert.NotNull(lambda.Target);
        Assert.True(BuiltinsRegistry.TryGetByFunctor(Fid("atom_length", 2), out int atomLengthId));
        var atomLength = BuiltinsRegistry.GetById(atomLengthId).Impl;
        Assert.Null(atomLength.Target);

        IlEmit.RecordCallees = true;
        try
        {
            e.IlPromotion.Threshold = 1;
            e.IlPromotion.BackgroundCompilation = false;
            e.ConsultString(@"
:- public p/3.
p(A, L, N) :- atom_length(A, L), '$il_builtin_call_probe'(N).
");
            for (int i = 0; i < 3; i++) Assert.True(e.Query("p(hello, 5, _).").Success);
            Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
            Assert.True(e.IlPromotion.IsPromoted(Fid("p", 3)), "p/3 must be Tier-1 IL");
            int before = _lambdaCalls;
            Assert.True(e.Query("p(abc, L, N), L == 3, integer(N).").Success);
            Assert.False(e.Query("p(abc, 4, _).").Success);
            Assert.True(_lambdaCalls > before, "the lambda builtin did not run");
        }
        finally
        {
            IlEmit.RecordCallees = false;
        }
        Assert.Contains(atomLength.Method, IlEmit.CalleesSeen.Keys);
        Assert.DoesNotContain(lambda.Method, IlEmit.CalleesSeen.Keys);
    }
}
