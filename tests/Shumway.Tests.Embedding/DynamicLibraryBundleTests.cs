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

    // ---- what bringing a module in costs the next query ----

    [Fact]
    public void BringingAModuleIn_LeavesTheDerivation_AndEveryCompileOfADynamicPredicate()
    {
        Tables();
        var e = Engine();
        e.ConsultString(@"
            :- dynamic(tbl/2).
            tbl(z, 0).
            :- dynamic(ruled/1).
            ruled(X) :- X > 10.
            ");
        Assert.True(e.Query("ensure_loaded(bundle(tables)).").Success);
        Assert.True(e.Query("findall(K, tbl(K, _), [z, a, b, c]), ruled(11).").Success);
        int tbl = Shumway.Core.FunctorTable.Intern(Shumway.Core.AtomTable.Intern("tbl", permanent: true).Id, 2);
        int ruled = Shumway.Core.FunctorTable.Intern(Shumway.Core.AtomTable.Intern("ruled", permanent: true).Id, 1);
        Assert.True(e.Query("true.").Success);
        var facts = e.DynamicPredicateCache[tbl];
        var rule = e.DynamicPredicateCache[ruled];
        int derivation = e._derivationGen;
        long layouts = e.PersistentInvalidations;
        long compiles = e.DynamicPredicateCompiles;

        Assert.True(e.IsCodePending("t2"));
        Assert.True(e.Query("other(1).").Success);
        Assert.False(e.IsCodePending("t2"));
        Assert.True(e.Query("findall(K, tbl(K, _), [z, a, b, c]), ruled(11), other(1).").Success);

        Assert.Equal(layouts, e.PersistentInvalidations);
        Assert.Equal(derivation, e._derivationGen);
        Assert.Equal(compiles, e.DynamicPredicateCompiles);
        Assert.Same(facts, e.DynamicPredicateCache[tbl]);
        Assert.Same(rule, e.DynamicPredicateCache[ruled]);
    }

    // ---- the module's code stays linked (ADR-015, amendment) ----

    // Two modules with a predicate each that is indexed by a switch table,
    // and one a consulted file calls before the library is attached.
    private void Indexed() => BuildLibrary("indexed",
        ("ix1", @"
            sw1(a, 1). sw1(b, 2). sw1(c, 3). sw1(d, 4). sw1(e, 5). sw1(f, 6). sw1(g, 7). sw1(h, 8).
            "),
        ("ix2", @"
            sw2(p, 11). sw2(q, 12). sw2(r, 13). sw2(s, 14). sw2(t, 15). sw2(u, 16). sw2(v, 17). sw2(w, 18).
            via2(K, V) :- sw2(K, V).
            "),
        ("ix3", @"
            called_from_a_file(K, V) :- sw1(K, V0), V is V0 * 100.
            "));

    private PrologEngine WithIndexedAttached()
    {
        Indexed();
        var e = Engine();
        e.ConsultString(@"
            caller(K, V) :- called_from_a_file(K, V).
            ");
        Assert.True(e.Query("ensure_loaded(bundle(indexed)).").Success);
        // The query after the attach lays the program out: from here the
        // next one runs on the host's buffer.
        Assert.True(e.Query("true.").Success);
        return e;
    }

    [Fact]
    public void AModuleBroughtIn_StaysLinked_AndNoQueryLinksTheProgramAgain()
    {
        var e = WithIndexedAttached();
        long layouts = e.PersistentInvalidations;
        long adopted = e.AdoptedLinks;

        Assert.True(e.Query("sw1(c, V), V == 3.").Success);
        Assert.Equal(adopted + 1, e.AdoptedLinks);
        Assert.NotEmpty(e._adoptedTables);
        Assert.True(e.Query("findall(K-V, sw1(K, V), L), length(L, 8), sw1(h, 8).").Success);

        Assert.True(e.Query("via2(s, V), V == 14.").Success);
        Assert.Equal(adopted + 2, e.AdoptedLinks);
        // From here every call finds them, written in the query or made at
        // run time: none comes through the undefined-predicate path.
        long lookups = e.AttachedLibraryLookups;
        Assert.True(e.Query("sw1(e, A), A == 5, sw2(w, B), B == 18, via2(p, C), C == 11.").Success);
        Assert.True(e.Query("G = sw1(g, V), call(G), V == 7, findall(K, via2(K, _), [p, q, r, s, t, u, v, w]).").Success);
        Assert.Equal(lookups, e.AttachedLibraryLookups);
        Assert.True(e.Query(@"\+ sw1(z, _), \+ sw2(z, _), findall(K, sw2(K, _), [p, q, r, s, t, u, v, w]).").Success);

        Assert.Equal(layouts, e.PersistentInvalidations);
        Assert.False(e.IsCodePending("ix1"));
        Assert.False(e.IsCodePending("ix2"));
        Assert.True(e.IsCodePending("ix3"));
    }

    [Fact]
    public void ACallLinkedBeforeTheModuleCameIn_NamesItFromThenOn()
    {
        var e = WithIndexedAttached();
        long layouts = e.PersistentInvalidations;

        Assert.True(e.Query("caller(b, V), V == 200.").Success);
        Assert.False(e.IsCodePending("ix3"));
        Assert.False(e.IsCodePending("ix1"));
        long lookups = e.AttachedLibraryLookups;
        Assert.True(lookups > 0, "the first call did not come through the undefined-predicate path");

        Assert.True(e.Query("caller(c, V), V == 300.").Success);
        Assert.True(e.Query("findall(V, caller(_, V), L), length(L, 8).").Success);
        Assert.Equal(lookups, e.AttachedLibraryLookups);
        Assert.Equal(layouts, e.PersistentInvalidations);
    }

    [Fact]
    public void ALaterLayOutOfTheProgram_LinksTheAdoptedCodeWithTheRest()
    {
        var e = WithIndexedAttached();
        Assert.True(e.Query("sw1(c, 3), via2(s, 14).").Success);
        Assert.NotEmpty(e._adoptedStatics);

        e.ConsultString("consulted_later(1).");
        Assert.True(e.Query("consulted_later(1), sw1(d, V), V == 4, via2(u, W), W == 16.").Success);
        Assert.Empty(e._adoptedStatics);
        Assert.Empty(e._adoptedTables);
        Assert.True(e.Query("caller(a, V), V == 100, findall(K, sw2(K, _), [p, q, r, s, t, u, v, w]).").Success);
    }

    // A dynamic predicate that turns hot is laid out indexed in place, with
    // its tables after the dynamic region's: the adopted code keeps its own.
    [Fact]
    public void APredicateThatTurnsHotBesideAdoptedCode_IsIndexedInPlace()
    {
        Indexed();
        var e = Engine();
        e.JitIndexing.Threshold = 2;
        e.ConsultString(@"
            :- dynamic(hot/2).
            hot(k1, 1). hot(k2, 2). hot(k3, 3). hot(k4, 4). hot(k5, 5). hot(k6, 6).
            ");
        Assert.True(e.Query("ensure_loaded(bundle(indexed)).").Success);
        Assert.True(e.Query("true.").Success);
        Assert.True(e.Query("sw1(c, 3), via2(s, 14).").Success);
        Assert.NotEmpty(e._adoptedTables);
        long layouts = e.PersistentInvalidations;
        long flips = e.HotnessFlipsInPlace;

        // Hot at its second call: the setup of the third lays it out.
        for (int i = 0; i < 3; i++)
            Assert.True(e.Query("hot(k4, V), V == 4.").Success);

        Assert.Equal(flips + 1, e.HotnessFlipsInPlace);
        Assert.Equal(layouts, e.PersistentInvalidations);
        Assert.True(e.Query("hot(k6, 6), sw1(h, 8), via2(w, 18), findall(K, sw1(K, _), [a, b, c, d, e, f, g, h]).").Success);
        Assert.True(e.Query("assertz(hot(k7, 7)), hot(k7, V), V == 7, sw2(p, 11).").Success);
    }

    [Fact]
    public void BroughtInByTheQueryThatAttached_TheProgramIsLaidOutAgain()
    {
        Indexed();
        var e = Engine();
        long adopted = e.AdoptedLinks;
        Assert.True(e.Query("ensure_loaded(bundle(indexed)), sw1(c, V), V == 3.").Success);
        Assert.Equal(adopted, e.AdoptedLinks);
        Assert.True(e.Query("sw1(f, V), V == 6, via2(q, W), W == 12.").Success);
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

    // A module with module/2 and many predicates it does not export.
    private void ManyLocals(int n)
    {
        var src = new System.Text.StringBuilder(":- module(ml, [front/1]).\n:- dynamic(note/1).\nfront(X) :- local_0(X).\n");
        for (int i = 0; i < n; i++) src.Append("local_").Append(i).Append("(").Append(i).Append(").\n");
        BuildLibrary("many", ("ml", src.ToString()));
    }

    private static int Fid(string name, int arity)
        => Shumway.Core.FunctorTable.Intern(Shumway.Core.AtomTable.Intern(name).Id, arity);

    [Fact]
    public void Attaching_IndexesNoModuleQualifiedName()
    {
        // A call to ml$local_i names its module: the attach does not build
        // that name for each of the module's predicates.
        ManyLocals(300);
        var e = Engine();
        Assert.True(e.Query("ensure_loaded(bundle(many)).").Success);
        Assert.True(e.IsCodePending("ml"));
        Assert.True(e.AttachedIndexCount < 10, $"{e.AttachedIndexCount} entries");
        Assert.True(e.Query("ml:local_299(X), X == 299.").Success);
        Assert.False(e.IsCodePending("ml"));
    }

    [Fact]
    public void AModuleWhoseCodeWaits_IsMarkedNonDebuggable_AsItsCodeComesIn()
    {
        ManyLocals(3);
        var e = Engine();
        Assert.True(e.Query("ensure_loaded(bundle(many)).").Success);
        // Its dynamic predicates can run before its code is in.
        Assert.Contains(Fid("ml$note", 1), e._nonDebuggableFunctors);
        Assert.DoesNotContain(Fid("ml$local_1", 1), e._nonDebuggableFunctors);

        Assert.True(e.Query("ml:front(X), X == 0.").Success);
        Assert.Contains(Fid("ml$local_1", 1), e._nonDebuggableFunctors);
        Assert.Contains(Fid("ml$front", 1), e._nonDebuggableFunctors);
    }
}
