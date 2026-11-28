using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The hint a link prints for a source it compiles one file at a time names
/// <c>--consult</c> only when the consult would read or run the source
/// otherwise: a library it loads defines an expansion hook or has a goal
/// directive, or the compile failed and the source uses an operator a library
/// it loads defines, or declares a predicate named with its module. A
/// condition of the source is answered as the consult answers it.
/// </summary>
public sealed class ConsultHintTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_consult_hint_" + Guid.NewGuid().ToString("N"));

    public ConsultHintTests() => Directory.CreateDirectory(Path.Combine(_dir, "lib"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string Arrows = @"
:- module(arrows, [op(700, xfx, ===>), (===>)/2]).
A ===> A.
";

    private void Library(string name, string text) =>
        File.WriteAllText(Path.Combine(_dir, "lib", name + ".pl"), text);

    // The lines the plan reports, and whether it gave a configuration.
    private (List<string> Lines, bool Prepared) Link(string source)
    {
        string path = Path.Combine(_dir, "app.pl");
        File.WriteAllText(path, source);
        var lines = new List<string>();
        var plan = new LinkPlan
        {
            Library = true,
            LibraryDirs = new[] { Path.Combine(_dir, "lib") },
            Report = lines.Add,
        };
        bool prepared = plan.Prepare(new[] { path }, out _) is not null;
        return (lines, prepared);
    }

    private static string? Hint(List<string> lines) =>
        lines.FirstOrDefault(l => l.Contains("hint:", StringComparison.Ordinal));

    [Fact]
    public void ASourceThatOnlyNamesTheHooks_LinksWithoutAHint()
    {
        const string source = @"
:- multifile(term_expansion/2).
r(X) :- X = term_expansion(a, b).
s(G) :- G = goal_expansion(a, b).
t('use_module(library(lists))').
";
        var (lines, prepared) = Link(source);
        Assert.True(prepared, string.Join("\n", lines));
        Assert.Null(Hint(lines));
    }

    [Fact]
    public void ALibraryWithAQualifiedTermExpansion_IsNamed()
    {
        Library("rewrite", @"
:- module(rewrite, []).
user:term_expansion(old(X), new(X)).
");
        var (lines, prepared) = Link(@"
:- use_module(library(rewrite)).
old(1).
");
        Assert.True(prepared, string.Join("\n", lines));
        string? hint = Hint(lines);
        Assert.NotNull(hint);
        Assert.Contains("library 'rewrite'", hint);
        Assert.Contains("term_expansion/2", hint);
        Assert.Contains("--consult", string.Join("\n", lines));
    }

    [Fact]
    public void ALibraryThatIsNoModule_WithAGoalExpansion_IsNamed()
    {
        Library("shortcut", @"
goal_expansion(twice(X), (X, X)).
twice(G) :- G, G.
");
        var (lines, prepared) = Link(@"
:- use_module(library(shortcut)).
p :- twice(true).
");
        Assert.True(prepared, string.Join("\n", lines));
        string? hint = Hint(lines);
        Assert.NotNull(hint);
        Assert.Contains("library 'shortcut'", hint);
        Assert.Contains("goal_expansion/2", hint);
    }

    [Fact]
    public void ALibraryThatOnlyDeclaresAHook_GivesNoHint()
    {
        Library("declares", @"
:- module(declares, [d/0]).
:- multifile(term_expansion/2).
:- dynamic(term_expansion/2).
d.
");
        var (lines, prepared) = Link(@"
:- use_module(library(declares)).
p :- d.
");
        Assert.True(prepared, string.Join("\n", lines));
        Assert.Null(Hint(lines));
    }

    [Fact]
    public void ALibraryOperatorTheSourceUses_IsNamedWhenTheCompileFails()
    {
        Library("arrows", Arrows);
        var (lines, prepared) = Link(@"
:- use_module(library(arrows)).
q(X) :- a ===> X.
");
        Assert.False(prepared);
        Assert.Contains(lines, l => l.Contains("error:", StringComparison.Ordinal));
        string? hint = Hint(lines);
        Assert.NotNull(hint);
        Assert.Contains("library 'arrows'", hint);
        Assert.Contains("===>", hint);
    }

    [Fact]
    public void ACompiledLibrary_IsReadForItsOperators()
    {
        string pl = Path.Combine(_dir, "arrows.pl");
        File.WriteAllText(pl, Arrows);
        var compiled = ShmoCompiler.TryCompileFile(pl);
        Assert.True(compiled.Success, string.Join("\n", compiled.Errors.Select(e => e.Message)));
        ShmoWriter.WriteToFile(compiled.Object!, Path.Combine(_dir, "lib", "arrows.shmo"));

        var (lines, prepared) = Link(@"
:- use_module(library(arrows)).
q(X) :- a ===> X.
");
        Assert.False(prepared);
        string? hint = Hint(lines);
        Assert.NotNull(hint);
        Assert.Contains("===>", hint);
    }

    [Fact]
    public void ALibraryOperatorTheSourceDoesNotUse_GivesNoHint()
    {
        Library("arrows", Arrows);
        var (lines, prepared) = Link(@"
:- use_module(library(arrows)).
q(X) :- X = .
");
        Assert.False(prepared);
        Assert.Contains(lines, l => l.Contains("error:", StringComparison.Ordinal));
        Assert.Null(Hint(lines));
    }

    [Fact]
    public void AnOperatorTheReaderAlreadyHas_GivesNoHint()
    {
        Library("same", @"
:- module(same, [op(700, xfx, =), same/2]).
same(A, A).
");
        var (lines, prepared) = Link(@"
:- use_module(library(same)).
q(X) :- X = 1, X = .
");
        Assert.False(prepared);
        Assert.Contains(lines, l => l.Contains("error:", StringComparison.Ordinal));
        Assert.Null(Hint(lines));
    }

    [Fact]
    public void ALibraryWhoseGoalDirectiveTheLinkLeavesOut_IsNamed()
    {
        Library("counter", @"
:- module(counter, [next/1]).
:- dynamic(n/1).
:- assertz(n(0)).
next(X) :- retract(n(X)), Y is X + 1, assertz(n(Y)).
");
        var (lines, prepared) = Link(@"
:- use_module(library(counter)).
p(X) :- next(X).
");
        Assert.True(prepared, string.Join("\n", lines));
        string? hint = Hint(lines);
        Assert.NotNull(hint);
        Assert.Contains("library 'counter'", hint);
        Assert.Contains("assertz/1", hint);
    }

    [Fact]
    public void ADeclarationNamingAPredicateWithItsModule_IsNamedWhenTheCompileFails()
    {
        var (lines, prepared) = Link(@"
:- multifile(user:portray/1).
p(1).
");
        Assert.False(prepared);
        Assert.Contains(lines, l => l.Contains("error:", StringComparison.Ordinal));
        string? hint = Hint(lines);
        Assert.NotNull(hint);
        Assert.Contains("user:portray/1", hint);
    }

    private const string AsksForPresent = @"
:- if(exists_source(library(present))).
found.
:- else.
not_found.
:- endif.
";

    // The predicates the link compiles from app.pl, through the consult or
    // one file at a time.
    private List<string> Defined(string source, bool consult, params string[] libraryDirs)
    {
        string path = Path.Combine(_dir, "app.pl");
        File.WriteAllText(path, source);
        var lines = new List<string>();
        var plan = new LinkPlan
        {
            Consult = consult,
            Library = true,
            LibraryDirs = libraryDirs,
            Report = lines.Add,
        };
        LinkConfig? config = plan.Prepare(new[] { path }, out _);
        Assert.True(config is not null, string.Join("\n", lines));
        return config!.Objects.SelectMany(o => o.Defined).Select(d => d.Indicator.Name).ToList();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistsSource_OfALibraryOnTheSearchPath_IsFound(bool consult)
    {
        Library("present", @"
:- module(present, [here/0]).
here.
");
        var defined = Defined(AsksForPresent, consult, Path.Combine(_dir, "lib"));
        Assert.Contains("found", defined);
        Assert.DoesNotContain("not_found", defined);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistsSource_OfALibraryBesideTheSource_IsFound(bool consult)
    {
        File.WriteAllText(Path.Combine(_dir, "present.pl"), @"
:- module(present, [here/0]).
here.
");
        var defined = Defined(AsksForPresent, consult);
        Assert.Contains("found", defined);
        Assert.DoesNotContain("not_found", defined);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistsSource_OfAMissingLibrary_IsNotFound(bool consult)
    {
        var defined = Defined(AsksForPresent, consult, Path.Combine(_dir, "lib"));
        Assert.Contains("not_found", defined);
        Assert.DoesNotContain("found", defined);
    }

    [Fact]
    public void ALibraryWithNeitherHooksNorOperators_GivesNoHint()
    {
        Library("plain", @"
:- module(plain, [one/1]).
:- dynamic(seen/1).
:- initialization(true).
one(1).
");
        var (lines, prepared) = Link(@"
:- use_module(library(plain)).
q(X) :- one(X), X = .
");
        Assert.False(prepared);
        Assert.Contains(lines, l => l.Contains("error:", StringComparison.Ordinal));
        Assert.Null(Hint(lines));
    }
}
