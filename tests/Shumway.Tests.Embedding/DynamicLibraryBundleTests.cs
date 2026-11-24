using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-075: a library bundle attached with <c>ensure_loaded(bundle(x))</c>.
/// Its modules are declared and none of their code is loaded; the first call
/// into a module brings that module in, whole, and no other.
/// </summary>
public sealed class DynamicLibraryBundleTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_dynlib_" + Guid.NewGuid().ToString("N"));

    public DynamicLibraryBundleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Links the modules, each compiled alone from its text, into
    /// one library bundle <c>name.shum</c>, as <c>shumway-link --library</c>
    /// does.</summary>
    private void BuildLibrary(string name, params (string Module, string Source)[] modules)
    {
        var objects = modules.Select(m => ShmoCompiler.CompileSource(m.Source, m.Module)).ToArray();
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = objects,
            EntryPoints = Array.Empty<PredicateRef>(),
            Library = true,
            AllowUndefined = true,
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        File.WriteAllBytes(Path.Combine(_dir, name + ".shum"), BundleWriter.ToBytes(link.Bundle!, includeCompiledBytecode: true));
    }

    private PrologEngine Engine()
    {
        var e = new PrologEngine { Out = new StringWriter(), Warnings = new StringWriter() };
        e.AddLibraryDirectory(_dir);
        return e;
    }

    private void TwoModules() => BuildLibrary("two",
        ("ma", @"
            pa(1).
            pa(2).
            twice(X, Y) :- ( X = a, Y = aa ; X = b, Y = bb ).
            "),
        ("mb", @"
            pb(X) :- pa(X).
            only_b(yes).
            "));

    [Fact]
    public void Attaching_DeclaresTheModules_AndLoadsNoCode()
    {
        TwoModules();
        var e = Engine();

        Assert.True(e.Query("ensure_loaded(bundle(two)).").Success);

        Assert.True(e.IsCodePending("ma"));
        Assert.True(e.IsCodePending("mb"));
        Assert.False(e.PrecompiledModules.ContainsKey("ma"));
        Assert.False(e.PrecompiledModules.ContainsKey("mb"));
    }

    // Each a first call into ma, from the top level, through call/1 and from
    // the code of the other module.
    [Theory]
    [InlineData("pa(X), X == 1", false)]
    [InlineData("G = twice(b, Y), call(G), Y == bb", false)]
    [InlineData("findall(X, pa(X), [1, 2])", false)]
    [InlineData("pb(X), X == 1", true)]
    public void TheFirstCallIntoAModule_BringsItIn_AndNoOther(string goal, bool bringsInB)
    {
        TwoModules();
        var separate = Engine();
        Assert.True(separate.Query("ensure_loaded(bundle(two)).").Success);
        Assert.True(separate.Query(goal + ".").Success);
        Assert.False(separate.IsCodePending("ma"));
        Assert.Equal(!bringsInB, separate.IsCodePending("mb"));
        Assert.True(separate.Query(goal + ".").Success, "the next query");

        // The same in the query that attaches the library.
        var one = Engine();
        Assert.True(one.Query($"ensure_loaded(bundle(two)), {goal}.").Success);
        Assert.Equal(!bringsInB, one.IsCodePending("mb"));
    }

    private void ThreeKinds() => BuildLibrary("kinds",
        ("mq", @"
            :- module(mq, [q/1]).
            q(X) :- hidden(X).
            hidden(42).
            "),
        ("leg", @"
            lp(7).
            "),
        ("starts", @"
            :- dynamic(started/1).
            :- initialization(assertz(started(yes))).
            sp(1).
            "));

    [Fact]
    public void AModuleWithModule2_IsNotBroughtInByAnUnqualifiedCall()
    {
        ThreeKinds();
        var e = Engine();
        Assert.True(e.Query(
            "ensure_loaded(bundle(kinds)), catch(q(_), error(existence_error(procedure, PI), _), true), PI == q/1.").Success);
        Assert.True(e.Query("catch(hidden(_), error(existence_error(procedure, _), _), true).").Success);
        Assert.True(e.IsCodePending("mq"));
    }

    // What names the module brings it in: a qualified call, written or built
    // at run time, and an import.
    [Theory]
    [InlineData("mq:q(X), X == 42")]
    [InlineData("G = mq:q(X), call(G), X == 42")]
    [InlineData("M = mq, M:q(X), X == 42")]
    [InlineData("mq:hidden(X), X == 42")]
    [InlineData("use_module(library(mq)), q(X), X == 42")]
    [InlineData("use_module(library(mq), [q/1]), q(X), X == 42")]
    public void AModuleWithModule2_IsBroughtInByWhatNamesIt(string goal)
    {
        ThreeKinds();
        var one = Engine();
        Assert.True(one.Query($"ensure_loaded(bundle(kinds)), {goal}.").Success);
        Assert.False(one.IsCodePending("mq"));
        Assert.True(one.IsCodePending("leg"));

        var separate = Engine();
        Assert.True(separate.Query("ensure_loaded(bundle(kinds)).").Success);
        Assert.True(separate.Query(goal + ".").Success);
        Assert.False(separate.IsCodePending("mq"));
    }

    [Theory]
    [InlineData("lp(X), X == 7")]
    [InlineData("leg:lp(X), X == 7")]
    [InlineData("G = leg:lp(X), call(G), X == 7")]
    public void AModuleWithoutModule2_IsBroughtInByACallFromAnywhere(string goal)
    {
        ThreeKinds();
        var e = Engine();
        Assert.True(e.Query($"ensure_loaded(bundle(kinds)), {goal}.").Success);
        Assert.False(e.IsCodePending("leg"));
        Assert.True(e.IsCodePending("mq"));
    }

    [Fact]
    public void AModuleWithInitializationGoals_IsBroughtInAtTheAttach_AndTheyRunOnce()
    {
        ThreeKinds();
        var e = Engine();
        Assert.True(e.Query("ensure_loaded(bundle(kinds)), findall(S, started(S), [yes]).").Success);
        Assert.False(e.IsCodePending("starts"));
        Assert.True(e.PrecompiledModules.ContainsKey("starts"));
        Assert.True(e.Query("ensure_loaded(bundle(kinds)), sp(1), findall(S, started(S), [yes]).").Success);
    }

    [Fact]
    public void APredicateNoModuleDefines_IsStillAnExistenceError()
    {
        TwoModules();
        var e = Engine();
        Assert.True(e.Query(
            "ensure_loaded(bundle(two)), catch(nobody_defines_this(1), error(existence_error(procedure, PI), _), true), "
            + "PI == nobody_defines_this/1.").Success);
        Assert.True(e.IsCodePending("ma"));
    }

    [Fact]
    public void ABundleThatIsNotThere_IsAnExistenceError()
    {
        var e = Engine();
        Assert.True(e.Query(
            "catch(ensure_loaded(bundle(no_such_bundle)), error(existence_error(source_sink, B), _), true), "
            + "B == bundle(no_such_bundle).").Success);
    }

    [Fact]
    public void AttachedByADirectiveOfAFile_ItsPredicatesAreCalledByThatFile()
    {
        TwoModules();
        var e = Engine();
        e.ConsultString(@"
            :- ensure_loaded(bundle(two)).
            mine(X) :- pb(X).
            ");
        Assert.True(e.IsCodePending("ma"));
        Assert.True(e.Query("findall(X, mine(X), [1, 2]).").Success);
        Assert.False(e.IsCodePending("ma"));
    }

    // ---- decision 9: the clauses of a dynamic predicate ----

    // Two modules give clauses to one dynamic predicate of user, a third
    // reads it, and a fourth owns a dynamic predicate of its own.
    private void Tables() => BuildLibrary("tables",
        ("t1", @"
            :- dynamic(tbl/2).
            tbl(a, 1).
            tbl(b, 2).
            "),
        ("t2", @"
            :- dynamic(tbl/2).
            tbl(c, 3).
            other(1).
            "),
        ("reader", @"
            read_tbl(L) :- findall(K-V, tbl(K, V), L).
            "),
        ("own", @"
            :- module(own, [states/1]).
            :- dynamic(st/1).
            st(1).
            st(2).
            states(L) :- findall(S, st(S), L).
            "));

    private PrologEngine WithTablesAttached()
    {
        Tables();
        var e = Engine();
        e.ConsultString(@"
            :- dynamic(tbl/2).
            tbl(z, 0).
            ");
        Assert.True(e.Query("ensure_loaded(bundle(tables)).").Success);
        return e;
    }

    [Fact]
    public void Attaching_InstallsNoClause_AndNeitherDoesWhatDoesNotTouchThePredicate()
    {
        var e = WithTablesAttached();
        Assert.True(e.HasPendingClauses("tbl", 2));
        Assert.True(e.HasPendingClauses("st", 1, owner: "own"));

        Assert.True(e.Query("true.").Success);
        Assert.True(e.Query("other(1).").Success);
        Assert.False(e.IsCodePending("t2"));
        Assert.True(e.Query("predicate_property(tbl(_, _), dynamic), current_predicate(tbl/2).").Success);
        Assert.True(e.HasPendingClauses("tbl", 2));
        Assert.True(e.HasPendingClauses("st", 1, owner: "own"));
    }

    // Each a first touch of tbl/2. Whatever it is, the predicate then has the
    // clauses it had, then those of the library in the order of its modules.
    [Theory]
    [InlineData(@"findall(K-V, tbl(K, V), L), L == [z-0, a-1, b-2, c-3]")]
    [InlineData(@"tbl(c, V), V == 3")]
    [InlineData(@"G = tbl(b, V), call(G), V == 2")]
    [InlineData(@"\+ tbl(nobody, _), findall(K, tbl(K, _), [z, a, b, c])")]
    [InlineData(@"read_tbl(L), L == [z-0, a-1, b-2, c-3]")]
    [InlineData(@"findall(K-V, clause(tbl(K, V), true), L), L == [z-0, a-1, b-2, c-3]")]
    [InlineData(@"assertz(tbl(q, 9)), findall(K, tbl(K, _), [z, a, b, c, q])")]
    [InlineData(@"asserta(tbl(q, 9)), findall(K, tbl(K, _), [q, z, a, b, c])")]
    [InlineData(@"retract(tbl(a, _)), findall(K, tbl(K, _), [z, b, c])")]
    [InlineData(@"retract(tbl(z, _)), retract(tbl(c, _)), findall(K, tbl(K, _), [a, b])")]
    [InlineData(@"retractall(tbl(b, _)), findall(K, tbl(K, _), [z, a, c])")]
    [InlineData(@"retractall(tbl(_, _)), \+ tbl(_, _)")]
    [InlineData(@"abolish(tbl/2), catch(tbl(_, _), error(existence_error(_, _), _), true)")]
    public void TheFirstTouchOfADynamicPredicate_InstallsEveryClause_InOrder(string goal)
    {
        var separate = WithTablesAttached();
        Assert.True(separate.Query(goal + ".").Success, "in a query of its own");
        Assert.False(separate.HasPendingClauses("tbl", 2));
        Assert.True(separate.HasPendingClauses("st", 1, owner: "own"));
        Assert.True(separate.Query(@"\+ clause(tbl(_, _), (!, _)).").Success, "the stub is gone");

        // The same in the query that attaches the library.
        Tables();
        var one = Engine();
        one.ConsultString(@"
            :- dynamic(tbl/2).
            tbl(z, 0).
            ");
        Assert.True(one.Query($"ensure_loaded(bundle(tables)), {goal}.").Success, "in the attaching query");
        Assert.False(one.HasPendingClauses("tbl", 2));
    }

    [Fact]
    public void ACallThatInstalls_GivesEachSolutionOnce_AndTheNextCallTheSame()
    {
        var e = WithTablesAttached();
        Assert.True(e.Query("findall(K, tbl(K, _), L1), findall(K, tbl(K, _), L2), L1 == [z, a, b, c], L1 == L2.").Success);
        Assert.True(e.Query("findall(K, tbl(K, _), [z, a, b, c]).").Success);
    }

    [Fact]
    public void ADynamicPredicateAModuleOwns_GetsItsClausesAtItsFirstTouch()
    {
        var byItsCode = WithTablesAttached();
        Assert.True(byItsCode.Query("own:states(L), L == [1, 2].").Success);
        Assert.False(byItsCode.HasPendingClauses("st", 1, owner: "own"));
        Assert.True(byItsCode.HasPendingClauses("tbl", 2));

        var qualified = WithTablesAttached();
        Assert.True(qualified.Query("findall(S, own:st(S), [1, 2]).").Success);
        Assert.False(qualified.HasPendingClauses("st", 1, owner: "own"));

        var asserted = WithTablesAttached();
        Assert.True(asserted.Query("assertz(own:st(3)), own:states(L), L == [1, 2, 3].").Success);
    }

    [Fact]
    public void AClauseConsultedAfterTheAttach_GoesAfterTheLibrarys()
    {
        var e = WithTablesAttached();
        e.ConsultString(@"
            :- dynamic(tbl/2).
            tbl(late, 7).
            ");
        Assert.True(e.Query("findall(K, tbl(K, _), L), L == [z, a, b, c, late].").Success);
    }

    [Fact]
    public void AListing_ShowsTheClauses_AndNotTheStub()
    {
        var e = WithTablesAttached();
        var o = new StringWriter();
        e.Out = o;
        Assert.True(e.Query("listing(tbl/2).").Success);
        string text = o.ToString();
        Assert.Contains("tbl(c, 3)", text.Replace("tbl(c,3)", "tbl(c, 3)"));
        Assert.DoesNotContain("dynlib", text);
    }

    // ---- from code promoted to Tier-1 ----

    // The no branch promotes probe/3 and calls into nothing of the library.
    private const string Prober = @"
        :- dynamic(tbl/2).
        tbl(z, 0).
        probe(no, x, x).
        probe(tbl, K, V) :- tbl(K, V).
        probe(code, K, V) :- other(K), V = K.
        warm :- probe(no, _, _), probe(no, _, _).
        ";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PromotedCode_InstallsTheClauses_AndBringsTheModuleIn(bool attachFirst)
    {
        Tables();
        var e = Engine();
        Assert.True(e.Query("jit_compile(all).").Success);
        e.ConsultString(Prober);
        if (attachFirst) Assert.True(e.Query("ensure_loaded(bundle(tables)).").Success);
        Assert.True(e.Query("warm, warm.").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotions did not settle");
        Assert.NotEmpty(e.IlPromotion.PromotedFunctorIds());
        if (!attachFirst) Assert.True(e.Query("ensure_loaded(bundle(tables)).").Success);
        Assert.True(e.HasPendingClauses("tbl", 2));
        Assert.True(e.IsCodePending("t2"));

        Assert.True(e.Query("findall(K, probe(tbl, K, _), L), L == [z, a, b, c].").Success);
        Assert.False(e.HasPendingClauses("tbl", 2));
        Assert.True(e.Query("findall(K, probe(tbl, K, _), L), L == [z, a, b, c].").Success, "the next query");

        Assert.True(e.Query("probe(code, K, V), K == 1, V == 1.").Success);
        Assert.False(e.IsCodePending("t2"));
        Assert.True(e.Query("probe(code, K, _), K == 1.").Success, "the next query");
    }
}
