using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 417 — the ISO <c>unknown</c> prolog flag wired through dispatch
/// (default <c>error</c>, per ISO and SWI/GNU/SICStus), plus the pre-scan
/// consistency fix: an <c>assertz(zzz(..))</c> literal later in a query no
/// longer makes <c>zzz</c> observable as an empty dynamic predicate before
/// the assertz runs.
/// </summary>
public class Chunk417Tests
{
    private static PrologEngine Make(string source = "ok.\n")
    {
        var engine = new PrologEngine();
        engine.ConsultString(source);
        return engine;
    }

    // ----- the reported bug: catch + later same-functor assertz -----

    [Fact]
    public void CaughtExistenceError_ThenAssertz_SameFunctor_MatchesSwi()
    {
        // SWI: call(zzz(1)) raises existence_error (caught), assertz
        // defines it, the retry succeeds. The pre-scan used to pre-declare
        // zzz/1 from the assertz literal, turning the first call into a
        // clean FAILURE (empty dynamic predicate) — catch then re-failed
        // and the whole query was false.
        var e = Make();
        var s = e.Query(
            "catch(call(zzz(1)), E, true), assertz(zzz(1)), call(zzz(1)).");
        Assert.True(s.Success);
        Assert.Contains("existence_error", s["E"]!.ToString());
    }

    [Fact]
    public void CaughtExistenceError_DirectCallVariant()
    {
        var e = Make();
        var s = e.Query(
            "catch(zzz(1), E, true), assertz(zzz(1)), zzz(X).");
        Assert.True(s.Success);
        Assert.Contains("existence_error", s["E"]!.ToString());
        Assert.Equal("1", s["X"]!.ToString());
    }

    [Fact]
    public void UndefinedBeforeAssertz_RaisesEvenWithLaterAssertzLiteral()
    {
        // The pre-scan artifact must be unobservable: a goal sequenced
        // before the assertz sees zzz/1 as UNDEFINED (error), not as an
        // empty dynamic predicate (fail).
        var e = Make();
        Assert.Throws<Shumway.Core.PrologRuntimeException>(() =>
            e.Query("( zzz(1) -> true ; true ), assertz(zzz(1))."));
    }

    // ----- chunk-207 patterns must keep working without the pre-scan -----

    [Fact]
    public void AssertzThenMetaCall_SameQuery()
    {
        var e = Make();
        Assert.True(e.Query("assertz(pepe), call(pepe).").Success);
    }

    [Fact]
    public void AssertzThenDirectCall_SameQuery()
    {
        var e = Make();
        Assert.True(e.Query("assertz(pepe2), pepe2.").Success);
    }

    // ----- the unknown flag's three values -----

    [Fact]
    public void DefaultIsError_DirectAndMetaCall()
    {
        var e = Make();
        Assert.Throws<Shumway.Core.PrologRuntimeException>(() => e.Query("zzz(1)."));
        Assert.Throws<Shumway.Core.PrologRuntimeException>(() => e.Query("call(zzz(1))."));
    }

    [Fact]
    public void UnknownFail_DirectCallFailsSilently()
    {
        var e = Make();
        Assert.False(e.Query("set_prolog_flag(unknown, fail), zzz(1).").Success);
    }

    [Fact]
    public void UnknownFail_MetaCallFailsSilently_MidQuery()
    {
        // set_prolog_flag takes effect MID-QUERY (the builtin updates the
        // live engine, not just the host flags for the next query).
        var e = Make();
        Assert.True(e.Query(
            "set_prolog_flag(unknown, fail), ( call(zzz(1)) -> fail ; true ).").Success);
    }

    [Fact]
    public void UnknownFail_PersistsToNextQuery()
    {
        var e = Make();
        Assert.True(e.Query("set_prolog_flag(unknown, fail).").Success);
        Assert.False(e.Query("zzz(1).").Success);
        Assert.True(e.Query("set_prolog_flag(unknown, error).").Success);
        Assert.Throws<Shumway.Core.PrologRuntimeException>(() => e.Query("zzz(1)."));
    }

    [Fact]
    public void UnknownWarning_FailsAfterWarning()
    {
        var e = Make();
        Assert.True(e.Query(
            "set_prolog_flag(unknown, warning), ( zzz(1) -> fail ; true ).").Success);
    }

    [Fact]
    public void CurrentPrologFlag_ReflectsValue()
    {
        var e = Make();
        var s = e.Query("current_prolog_flag(unknown, V).");
        Assert.True(s.Success);
        Assert.Equal("error", s["V"]!.ToString());
        Assert.True(e.Query("set_prolog_flag(unknown, fail).").Success);
        var s2 = e.Query("current_prolog_flag(unknown, V).");
        Assert.Equal("fail", s2["V"]!.ToString());
    }

    [Fact]
    public void ExplicitDynamicEmpty_StillFailsUnderError()
    {
        // ISO: an explicitly declared dynamic predicate with no clauses
        // FAILS — the unknown flag governs UNDECLARED procedures only.
        var e = Make(":- dynamic d/1.\nok.\n");
        Assert.False(e.Query("d(1).").Success);
        Assert.False(e.Query("call(d(1)).").Success);
    }

    [Fact]
    public void UnknownFail_TailCallSite()
    {
        // Execute-shaped (tail) call site to an undefined predicate.
        var e = Make("top :- zzz(1).\nok.\n");
        Assert.True(e.Query("set_prolog_flag(unknown, fail).").Success);
        Assert.False(e.Query("top.").Success);
    }

    [Fact]
    public void UnknownFail_BacktracksIntoEarlierChoicePoints()
    {
        // The fail must be an ordinary failure: earlier choice points
        // still enumerate.
        var e = Make("gen(1). gen(2).\nok.\n");
        Assert.True(e.Query("set_prolog_flag(unknown, fail).").Success);
        var all = e.QueryAll("gen(X), ( zzz(X) -> true ; true ).").ToList();
        Assert.Equal(2, all.Count);
    }
}
