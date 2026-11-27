using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-056: a module with <c>:- module/2</c> keeps its privates private. An
/// entry point or a call of the startup goal names what the module exports;
/// the linker promotes that to a bare name, and refuses a private.
/// </summary>
public sealed class PrivateEntryTests
{
    private const string Module = @"
:- module(m, [p/0]).
p :- q.
q :- write(q_ran), nl.
";

    private static LinkResult Link(string source, string module, bool allowUndefined = false,
        PredicateRef[]? entries = null, PredicateRef[]? goalCalls = null)
        => ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(source, module, ShmoBuildMode.Release) },
            EntryPoints = entries ?? Array.Empty<PredicateRef>(),
            GoalCallRefs = goalCalls ?? Array.Empty<PredicateRef>(),
            AllowUndefined = allowUndefined,
        });

    private static string Messages(LinkResult link)
        => string.Join("; ", link.Diagnostics.Select(d => d.Message));

    private static (PrologEngine Engine, StringWriter Output) Loaded(LinkResult link)
    {
        var output = new StringWriter();
        var engine = new PrologEngine { Out = output, Warnings = new StringWriter() };
        engine.LoadBundle(BundleReader.FromBytes(link.Bytes!));
        return (engine, output);
    }

    // The export keeps the module in the bundle; under --allow-undefined the
    // bundle is written, and the private is not callable by its bare name.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEntryThatNamesAPrivate_IsRefused(bool allowUndefined)
    {
        var link = Link(Module, "m", allowUndefined,
            entries: new[] { new PredicateRef("p", 0), new PredicateRef("q", 0) });
        Assert.False(link.Success);
        Assert.Contains(link.Diagnostics, d => d.Code == "private_entry"
            && d.Message.Contains("export it from 'm'", StringComparison.Ordinal));
        Assert.Equal(allowUndefined, link.Bytes is not null);
        if (link.Bytes is null) return;
        var (engine, _) = Loaded(link);
        Assert.True(engine.Query("p.").Success);
        Assert.True(engine.Query("catch(q, error(existence_error(procedure, q/0), _), true).").Success);
        Assert.False(engine.Query("catch(q, _, fail).").Success);
    }

    [Fact]
    public void AGoalThatCallsAPrivate_IsRefused()
    {
        var link = Link(Module, "m", goalCalls: new[] { new PredicateRef("q", 0) });
        Assert.False(link.Success);
        Assert.Contains(link.Diagnostics, d => d.Code == "private_goal");
    }

    [Fact]
    public void AnEntryThatNamesAnExport_IsPromoted()
    {
        var link = Link(Module, "m", entries: new[] { new PredicateRef("p", 0) });
        Assert.True(link.Success, Messages(link));
        var (engine, output) = Loaded(link);
        Assert.True(engine.Query("p.").Success);
        Assert.Equal("q_ran\n", output.ToString().Replace("\r\n", "\n"));
    }

    [Fact]
    public void AnEntryOfAModuleWithoutADirective_IsPromoted()
    {
        var link = Link(@"
main :- helper.
helper :- write(helper_ran), nl.
", "app", entries: new[] { new PredicateRef("main", 0) });
        Assert.True(link.Success, Messages(link));
        var (engine, output) = Loaded(link);
        Assert.True(engine.Query("main.").Success);
        Assert.Equal("helper_ran\n", output.ToString().Replace("\r\n", "\n"));
    }
}
