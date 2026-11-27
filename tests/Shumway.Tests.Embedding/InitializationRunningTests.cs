using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// <see cref="PrologEngine.InitializationRunning"/>: a host is told when each
/// <c>initialization/1</c> goal starts and ends, a file's and a bundle's, the
/// goal of a library link included, nested when one loads another; a plain
/// directive is part of the consult and tells nothing. The test writes the
/// notices into the output, so their order against what the goals print is
/// what it checks.
/// </summary>
public sealed class InitializationRunningTests
{
    private static (PrologEngine Engine, StringWriter Output) Engine()
    {
        var output = new StringWriter();
        var engine = new PrologEngine { Out = output, Warnings = new StringWriter() };
        engine.InitializationRunning = running => output.Write(running ? "<" : ">");
        return (engine, output);
    }

    [Fact]
    public void AFilesInitializationGoal_IsTold_AndADirectiveIsNot()
    {
        var (engine, output) = Engine();
        engine.ConsultString(@"
:- initialization(write(i)).
:- write(d).
");
        Assert.Equal("d<i>", output.ToString());
    }

    [Fact]
    public void ABundlesInitializationGoals_AreTold_ItsLoadGoalLast()
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(@"
greet :- write(goal).
:- initialization(write(own)).
", "app", ShmoBuildMode.Release) },
            EntryPoints = Array.Empty<PredicateRef>(),
            Library = true,
            LoadGoal = "greet",
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        var (engine, output) = Engine();
        engine.LoadBundle(BundleReader.FromBytes(link.Bytes!));
        Assert.Equal("<own><goal>", output.ToString());
    }

    [Fact]
    public void AnInitializationGoalThatConsults_NestsTheOneItLoads()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway_init_running_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string inner = Path.Combine(dir, "inner.pl").Replace('\\', '/');
            File.WriteAllText(inner, @"
:- initialization(write(b)).
");
            var (engine, output) = Engine();
            engine.ConsultString($@"
:- initialization((write(a), consult('{inner}'))).
");
            Assert.Equal("<a<b>>", output.ToString());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
