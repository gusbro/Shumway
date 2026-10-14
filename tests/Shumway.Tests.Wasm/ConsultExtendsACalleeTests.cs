using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>consult/1 appends: a predicate another file extends has new
/// clauses, and the tier's code for its callers sees them, as the
/// interpreter does. The IL tier's counterpart is
/// StaleCopiesAfterConsultTests.</summary>
public sealed class ConsultExtendsACalleeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"shumway-wasm-copies-{Guid.NewGuid():N}");

    public ConsultExtendsACalleeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Loop = @"
:- public loop/1.
:- public q/1.
loop(0) :- !.
loop(N) :- q(N), N1 is N - 1, loop(N1).
nop.
";

    private static PrologEngine Engine(bool tier)
    {
        if (tier) return TieredEngine.BuildWithWorld("").Engine;
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 0;
        return engine;
    }

    [Theory]
    [InlineData(@"q(X) :- nop, X > 100.", @"q(X) :- X > -1.")]
    [InlineData(@"q(1000).", @"q(_).")]
    public void ACalleeAnotherFileExtendsIsSeenByItsCaller(string q, string more)
    {
        string prog = Path.Combine(_dir, "prog.pl");
        File.WriteAllText(prog, Loop + q + "\n");
        string extra = Path.Combine(_dir, "more.pl");
        File.WriteAllText(extra, ":- public q/1.\n" + more + "\n");
        foreach (bool tier in new[] { false, true })
        {
            var e = Engine(tier);
            e.ConsultFile(prog);
            Assert.False(e.Query("loop(10).").Success);
            if (tier) Assert.NotEmpty(e.IlPromotion.PromotedFunctorIds());
            e.ConsultFile(extra);
            Assert.True(e.Query("loop(10).").Success, tier ? "wasm tier" : "interpreted");
        }
    }

    [Fact]
    public void ACallerSeesInTheSameQueryWhatTheQueryConsulted()
    {
        string prog = Path.Combine(_dir, "prog.pl");
        File.WriteAllText(prog, Loop + "q(X) :- nop, X > 100.\n");
        string extra = Path.Combine(_dir, "more.pl");
        File.WriteAllText(extra, ":- public q/1.\nq(X) :- X > -1.\n");
        string path = extra.Replace(Path.DirectorySeparatorChar, '/');
        foreach (bool tier in new[] { false, true })
        {
            var e = Engine(tier);
            e.ConsultFile(prog);
            Assert.False(e.Query("loop(10).").Success);
            if (tier) Assert.NotEmpty(e.IlPromotion.PromotedFunctorIds());
            Assert.True(e.Query($"consult('{path}'), loop(10).").Success, tier ? "wasm tier" : "interpreted");
        }
    }
}
