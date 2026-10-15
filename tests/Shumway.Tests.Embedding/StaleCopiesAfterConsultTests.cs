using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>consult/1 appends: a predicate another file extends has new
/// clauses, and compiled code that copied the old ones in (a region it is a
/// member of, a caller that inlined it) has to go. Kept, it answered for the
/// old definition while the interpreter answered for the new one.</summary>
public sealed class StaleCopiesAfterConsultTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"shumway-copies-{Guid.NewGuid():N}");

    public StaleCopiesAfterConsultTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    internal const string Loop = @"
:- public loop/1.
:- public q/1.
loop(0) :- !.
loop(N) :- q(N), N1 is N - 1, loop(N1).
nop.
";

    private string Source(string name, string text)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    // Compiled synchronously: loop/1 compiles at its first call, q/1 copied in.
    private static PrologEngine Engine(bool compiled)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = compiled ? 1 : 0;
        e.IlPromotion.BackgroundCompilation = false;
        return e;
    }

    private static int Fid(string name, int arity) => FunctorTable.Intern(AtomTable.Intern(name).Id, arity);

    [Theory]
    [InlineData(@"q(X) :- nop, X > 100.", @"q(X) :- X > -1.")]   // q/1 a member of loop/1's region
    [InlineData(@"q(1000).", @"q(_).")]                          // q/1 a fact loop/1 inlines
    public void ACalleeAnotherFileExtendsIsSeenByItsCompiledCaller(string q, string more)
    {
        string prog = Source("prog.pl", Loop + q + "\n");
        string extra = Source("more.pl", ":- public q/1.\n" + more + "\n");
        foreach (bool compiled in new[] { false, true })
        {
            var e = Engine(compiled);
            e.ConsultFile(prog);
            Assert.False(e.Query("loop(10).").Success);
            if (compiled) Assert.True(e.IlPromotion.IsPromoted(Fid("loop", 1)), "loop/1 did not compile");
            e.ConsultFile(extra);
            Assert.True(e.Query("loop(10).").Success, compiled ? "compiled" : "interpreted");
        }
    }

    [Fact]
    public void ACompiledCallerSeesInTheSameQueryWhatTheQueryConsulted()
    {
        string prog = Source("prog.pl", Loop + "q(X) :- nop, X > 100.\n");
        string extra = Source("more.pl", ":- public q/1.\nq(X) :- X > -1.\n");
        var e = Engine(compiled: true);
        e.ConsultFile(prog);
        Assert.False(e.Query("loop(10).").Success);
        Assert.True(e.IlPromotion.IsPromoted(Fid("loop", 1)), "loop/1 did not compile");
        string path = extra.Replace(Path.DirectorySeparatorChar, '/');
        Assert.True(e.Query($"consult('{path}'), loop(10).").Success);
    }
}

/// <summary>A background compile looks at its callees as they are when it
/// starts: one of them changing before it lands makes it stale.</summary>
// Exclusive: the test holds the process's compile worker.
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class StaleBackgroundCompileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"shumway-stale-{Guid.NewGuid():N}");

    public StaleBackgroundCompileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ACompileThatCopiedACalleeChangedSinceIsDropped()
    {
        string prog = Path.Combine(_dir, "prog.pl");
        File.WriteAllText(prog, StaleCopiesAfterConsultTests.Loop + "q(X) :- nop, X > 100.\n");
        string extra = Path.Combine(_dir, "more.pl");
        File.WriteAllText(extra, ":- public q/1.\nq(X) :- X > -1.\n");
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultFile(prog);

        using var running = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        IlCompileWorker.RunAsync(() => { running.Set(); release.Wait(); return null; }, (_, _) => { });
        Assert.True(running.Wait(60_000), "the worker did not reach the item that holds it");
        try
        {
            // Queues loop/1's compile, which copies q/1 as it is now.
            Assert.False(e.Query("loop(10).").Success);
            e.ConsultFile(extra);
        }
        finally { release.Set(); }

        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        Assert.True(e.Query("loop(10).").Success);
        // Compiled again, against the q/1 there is now.
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        int loop = FunctorTable.Intern(AtomTable.Intern("loop").Id, 1);
        Assert.True(e.IlPromotion.IsPromoted(loop), "loop/1 did not compile");
        Assert.True(e.Query("loop(10).").Success);
    }
}
