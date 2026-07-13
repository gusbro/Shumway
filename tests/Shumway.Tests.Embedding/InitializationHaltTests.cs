using System;
using System.IO;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-035 D4 surfaced this: `:- initialization(Goal)` where Goal HALTS must end the load,
/// and say so, rather than reporting the goal as failed and carrying on.
///
/// <para>halt/0-1 does not reach the consult as an exception — <c>QueryAll</c> catches the
/// <see cref="PrologHaltException"/>, records the code in
/// <see cref="PrologEngine.LastHaltExitCode"/> and reports the goal as FAILED. So a program
/// that ran, did its work and asked to exit was indistinguishable from one that fell over:
/// the load continued, the warning said "initialization goal failed", and (in the REPL) the
/// process that had been told to halt went on to sit at a top-level prompt nobody typed at.
/// That is the exact shape of a .pl launched by the IDE — it runs, and it ends.</para>
/// </summary>
public class InitializationHaltTests
{
    private static string WriteTemp(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), "shumway_init_" + Guid.NewGuid().ToString("N") + ".pl");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void AnInitializationGoalThatHaltsEndsTheLoad()
    {
        var engine = new PrologEngine();
        string path = WriteTemp(@"
:- initialization(main).

main :- assertz(ran(yes)), halt.
");
        try
        {
            PrologHaltException halted = Assert.Throws<PrologHaltException>(() => engine.ConsultFile(path));
            Assert.Equal(0, halted.ExitCode);
            // The goal RAN — this is a halt, not a failure.
            Assert.Equal(0, engine.LastHaltExitCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheExitCodeOfHalt1SurvivesTheLoad()
    {
        var engine = new PrologEngine();
        string path = WriteTemp(":- initialization(halt(3)).\n");
        try
        {
            PrologHaltException halted = Assert.Throws<PrologHaltException>(() => engine.ConsultFile(path));
            Assert.Equal(3, halted.ExitCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnInitializationGoalThatMerelyFailsStillOnlyWarns()
    {
        // The other half of the contract, and the reason the halt case had gone unnoticed:
        // a failing goal must NOT end the load.
        var engine = new PrologEngine();
        string path = WriteTemp(@"
:- initialization(fail).

after(here).
");
        try
        {
            engine.ConsultFile(path);   // does not throw
            Assert.Null(engine.LastHaltExitCode);
            Assert.Single(engine.QueryAll("after(X)."));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
