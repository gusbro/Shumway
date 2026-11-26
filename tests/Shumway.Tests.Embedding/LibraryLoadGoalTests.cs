using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The goal of a library link (<c>--goal</c> with <c>--library</c>) is the
/// last initialization goal of a module: the bundle runs it when it loads,
/// after that module's own, in that module.
/// </summary>
public sealed class LibraryLoadGoalTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_load_goal_" + Guid.NewGuid().ToString("N"));

    public LibraryLoadGoalTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static ShmoObject Object(string module, string source)
        => ShmoCompiler.CompileSource(source, module, ShmoBuildMode.Release);

    private static LinkResult Link(string goal, string? module, params ShmoObject[] objects)
        => ShmoLinker.Link(new LinkConfig
        {
            Objects = objects,
            EntryPoints = Array.Empty<PredicateRef>(),
            Library = true,
            LoadGoal = goal,
            LoadGoalModule = module,
        });

    private static string Messages(LinkResult link)
        => string.Join("; ", link.Diagnostics.Select(d => d.Message));

    private static PrologEngine Engine(StringWriter output)
        => new() { Out = output, Warnings = new StringWriter() };

    private const string App = @"
greet :- write(goal), nl.
:- initialization((write(own), nl)).
";

    [Theory]
    [InlineData("LoadBundle")]
    [InlineData("use_module")]
    public void TheGoal_RunsOnceWhenTheBundleLoads_AfterItsModulesInitialization(string load)
    {
        var link = Link("greet", null, Object("app", App));
        Assert.True(link.Success, Messages(link));
        var output = new StringWriter();
        var engine = Engine(output);
        if (load == "LoadBundle")
            engine.LoadBundle(BundleReader.FromBytes(link.Bytes!));
        else
        {
            File.WriteAllBytes(Path.Combine(_dir, "app.shum"), link.Bytes!);
            engine.AddLibraryDirectory(_dir);
            Assert.True(engine.Query("use_module(library(app)).").Success);
        }
        Assert.Equal("own\ngoal\n", output.ToString().Replace("\r\n", "\n"));
    }

    // A private of a module with module/2 is the module's to call: the goal
    // reaches it, and nothing outside the module does.
    [Fact]
    public void TheGoal_CallsAPrivateOfItsModule_WhichStaysPrivate()
    {
        var link = Link("q", null, Object("m", @"
:- module(m, [p/0]).
p :- q.
q :- write(q_ran), nl.
"));
        Assert.True(link.Success, Messages(link));
        var output = new StringWriter();
        var engine = Engine(output);
        engine.LoadBundle(BundleReader.FromBytes(link.Bytes!));
        Assert.Equal("q_ran\n", output.ToString().Replace("\r\n", "\n"));
        Assert.True(engine.Query("catch(q, error(existence_error(procedure, q/0), _), true).").Success);
    }

    [Theory]
    [InlineData(null, "b")]
    [InlineData("a", "a")]
    public void TheGoal_RunsInTheModuleNamed_OrTheLastObject(string? module, string expected)
    {
        var link = Link("who", module,
            Object("a", "who :- write(a), nl.\n"), Object("b", "who :- write(b), nl.\n"));
        Assert.True(link.Success, Messages(link));
        var output = new StringWriter();
        Engine(output).LoadBundle(BundleReader.FromBytes(link.Bytes!));
        Assert.Equal(expected + "\n", output.ToString().Replace("\r\n", "\n"));
    }

    [Fact]
    public void AGoalThatCallsNothingDefined_FailsTheLink()
    {
        var link = Link("gret", null, Object("app", App));
        Assert.False(link.Success);
        Assert.Contains(link.Diagnostics, d => d.Code == "initialization_not_found"
            && d.Message.StartsWith("--goal calls gret/0", StringComparison.Ordinal));
    }

    [Fact]
    public void AGoalWithoutALibraryLink_IsRefused()
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { Object("app", App) },
            EntryPoints = new[] { new PredicateRef("greet", 0) },
            LoadGoal = "greet",
        });
        Assert.False(link.Success);
        Assert.Contains(link.Diagnostics, d => d.Code == "load_goal_not_library");
    }
}
