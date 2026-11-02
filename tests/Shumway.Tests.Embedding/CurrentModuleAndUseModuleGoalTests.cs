using System.IO;
using System.Linq;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary><c>current_module/1</c> and the goal form of <c>use_module/2</c>.
/// Neither existed: a call raised existence_error, and the linker reported
/// both undefined in a program that only names them behind a runtime check
/// (Logtalk's core does, for backends with modules). The import list is read
/// the same way by the goal, the directives and the file-at-a-time compile;
/// before, a directive's list that was not a list imported nothing, and the
/// compile also dropped a <c>Name//Arity</c>.</summary>
public sealed class CurrentModuleAndUseModuleGoalTests : IDisposable
{
    private const string Library = @"
:- module(cmu_lib, [pa/1, pb/1]).
pa(1).
pb(2).
pc(3).
";

    // A nonterminal and an operator in the export list.
    private const string Grammar = @"
:- module(cmu_dcg, [greet//0, op(200, xfy, +++)]).
greet --> [hi].
";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shumway-cmu-" + Guid.NewGuid().ToString("N"));
    private readonly string _lib;
    private readonly string _dcg;

    public CurrentModuleAndUseModuleGoalTests()
    {
        Directory.CreateDirectory(_dir);
        _lib = Path.Combine(_dir, "cmu_lib.pl").Replace('\\', '/');
        File.WriteAllText(_lib, Library);
        _dcg = Path.Combine(_dir, "cmu_dcg.pl").Replace('\\', '/');
        File.WriteAllText(_dcg, Grammar);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static void Raises(PrologEngine e, string goal, string error) =>
        Assert.True(e.Query($"catch(({goal}), error(E, _), true), E == {error}.").Success,
            $"{goal} should raise {error}");

    [Fact]
    public void AModuleIsCurrentOnceLoaded()
    {
        var e = new PrologEngine();
        // The engine's own modules are $-named and not the program's.
        string own = e.Modules.Keys.First(k => k.StartsWith("$", StringComparison.Ordinal));
        Assert.False(e.Query($"current_module('{own}').").Success);
        Assert.True(e.Query("current_module(user).").Success);
        Assert.False(e.Query("current_module(cmu_lib).").Success);

        Assert.True(e.Query($"use_module('{_lib}').").Success);
        Assert.True(e.Query("current_module(cmu_lib).").Success);
        Assert.True(e.Query(@"
findall(M, current_module(M), Ms),
memberchk(cmu_lib, Ms), memberchk(user, Ms),
msort(Ms, Ms),
\+ ( member(X, Ms), sub_atom(X, 0, 1, _, '$') ).").Success);
    }

    [Fact]
    public void AModuleNameIsAnAtom()
    {
        var e = new PrologEngine();
        Raises(e, "current_module(1)", "type_error(atom, 1)");
        Raises(e, "current_module(f(x))", "type_error(atom, f(x))");
    }

    [Fact]
    public void UseModuleAsAGoalImportsOnlyTheListedPredicates()
    {
        var e = new PrologEngine();
        Assert.True(e.Query($"use_module('{_lib}', [pa/1]).").Success);
        Assert.True(e.Query("pa(X), X == 1.").Success);
        Raises(e, "pb(_)", "existence_error(procedure, pb/1)");
        Assert.True(e.Query("cmu_lib:pb(X), X == 2.").Success);
    }

    [Fact]
    public void AnImportTheModuleDoesNotExportIsRefused()
    {
        var e = new PrologEngine();
        Raises(e, $"use_module('{_lib}', [pa/1, pc/1])",
            "permission_error(access, private_procedure, cmu_lib:pc/1)");
        // Refused before anything was imported.
        Raises(e, "pa(_)", "existence_error(procedure, pa/1)");
    }

    [Fact]
    public void TheImportListIsCheckedBeforeTheLoad()
    {
        var e = new PrologEngine();
        Raises(e, "use_module(_, [pa/1])", "instantiation_error");
        Raises(e, $"use_module('{_lib}', _)", "instantiation_error");
        Raises(e, $"use_module('{_lib}', [pa/1|_])", "instantiation_error");
        Raises(e, $"use_module('{_lib}', [_])", "instantiation_error");
        Raises(e, $"use_module('{_lib}', [pa/_])", "instantiation_error");
        Raises(e, $"use_module('{_lib}', foo)", "type_error(list, foo)");
        Raises(e, $"use_module('{_lib}', [foo])", "type_error(predicate_indicator, foo)");
        Raises(e, $"use_module('{_lib}', [pa/(-1)])", "type_error(predicate_indicator, pa/(-1))");
        Assert.False(e.Query("current_module(cmu_lib).").Success);
    }

    [Theory]
    [InlineData("use_module", "foo", "type_error(list, foo)")]
    [InlineData("use_module", "[pa/1|_]", "instantiation_error")]
    [InlineData("use_module", "[pa/1, foo]", "type_error(predicate_indicator, foo)")]
    [InlineData("autoload", "[foo]", "type_error(predicate_indicator, foo)")]
    public void ADirectiveRefusesAMalformedImportList(string directive, string list, string error)
    {
        var e = new PrologEngine();
        var ex = Assert.Throws<InvalidOperationException>(
            () => e.ConsultString($":- {directive}('{_lib}', {list}).\n"));
        Assert.Contains($"malformed import list in :- {directive}/2: {error}", ex.Message);
        Assert.False(e.Query("current_module(cmu_lib).").Success);
    }

    [Fact]
    public void AFileAtATimeCompileRefusesAMalformedImportList()
    {
        var r = ShmoCompiler.TryCompileSource(":- use_module(cmu_lib, foo).\np.\n", "cmu_main");
        var error = Assert.Single(r.Errors);
        Assert.Equal(1, error.Line);
        Assert.Contains("malformed import list in :- use_module/2: type_error(list, foo)", error.Message);
    }

    // greet//0 is greet/2; op/3 adds nothing, the module's operators come anyway.
    [Fact]
    public void AnImportListNamesNonterminalsAndOperators()
    {
        var consulted = new PrologEngine();
        consulted.ConsultString($":- use_module('{_dcg}', [greet//0, op(_, _, _)]).\n");
        Assert.True(consulted.Query("phrase(greet, L), L == [hi].").Success);

        var goal = new PrologEngine();
        Assert.True(goal.Query($"use_module('{_dcg}', [greet//0, op(_, _, _)]).").Success);
        Assert.True(goal.Query("phrase(greet, L), L == [hi].").Success);
        Assert.True(goal.Query("X = (a +++ b), X == +++(a, b).").Success);

        var obj = ShmoCompiler.CompileSource(":- use_module(cmu_dcg, [greet//0, op(_, _, _)]).\n", "cmu_main");
        Assert.Contains(obj.Imports, i => i.Pred == new PredicateRef("greet", 2) && i.Source == "cmu_dcg");
    }

    [Fact]
    public void AProgramThatCallsThemLinks()
    {
        var r = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[]
            {
                ShmoCompiler.CompileSource(@"
:- public main/1.
:- public load/1.
main(Ms) :- findall(M, current_module(M), Ms).
load(F) :- use_module(F, [pa/1]).
", "cmu_main"),
            },
            EntryPoints = new[] { new PredicateRef("main", 1), new PredicateRef("load", 1) },
        });
        Assert.True(r.Success, string.Join(", ", r.Diagnostics.Select(d => d.Message)));
        var e = new PrologEngine();
        e.LoadBundle(BundleReader.FromBytes(r.Bytes!));
        Assert.True(e.Query($"load('{_lib}'), pa(X), X == 1.").Success);
        Assert.True(e.Query("main(Ms), memberchk(cmu_lib, Ms), memberchk(user, Ms).").Success);
    }

    // Compiled: mods/0 calls the builtin directly and backtracks into it.
    [Fact]
    public void CompiledCodeBacktracksOverTheModules()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString(@"
:- public mods/0.
:- dynamic seen/1.
mods :- current_module(M), assertz(seen(M)), fail.
mods.
");
        Assert.True(e.Query($"use_module('{_lib}').").Success);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(e.Query(@"
retractall(seen(_)), mods,
findall(M, seen(M), Ms), findall(M, current_module(M), Ms),
memberchk(cmu_lib, Ms).").Success, $"run {i}");
            Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        }
        Assert.True(e.IlPromotion.IsPromoted(FunctorTable.Intern(AtomTable.Intern("mods").Id, 0)),
            "mods/0 did not compile");
    }
}
