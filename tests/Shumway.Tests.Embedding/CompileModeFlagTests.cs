using System.Linq;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>compile_mode prolog flag (debug / release). Release — the default —
/// omits the per-clause meta dbg_info markers, so the Tier-0 interpreter does
/// not dispatch a no-op on every clause entry; debug re-enables them for
/// clause-precise error positions.</summary>
public class CompileModeFlagTests
{
    [Fact]
    public void Default_Is_Release()
    {
        var engine = new PrologEngine();
        Assert.False(engine.Flags.EmitDebugInfo);
        Assert.True(engine.Query("current_prolog_flag(compile_mode, release).").Success);
        Assert.False(engine.Query("current_prolog_flag(compile_mode, debug).").Success);
    }

    [Fact]
    public void SetPrologFlag_Debug_Then_Release()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("set_prolog_flag(compile_mode, debug).").Success);
        Assert.True(engine.Flags.EmitDebugInfo);
        Assert.True(engine.Query("current_prolog_flag(compile_mode, debug).").Success);

        Assert.True(engine.Query("set_prolog_flag(compile_mode, release).").Success);
        Assert.False(engine.Flags.EmitDebugInfo);
    }

    [Fact]
    public void SetPrologFlag_BadValue_RaisesDomainError()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<ShumwayPrologException>(
            () => engine.Query("set_prolog_flag(compile_mode, bogus)."));
        Assert.Contains("domain_error", ex.Message);
        Assert.Contains("flag_value", ex.Message);
    }

    [Fact]
    public void ConsultDirective_SetsFlag_ForLaterPredicates()
    {
        // `:- set_prolog_flag(compile_mode, debug)` at the top takes effect for
        // predicates compiled afterwards in the same consult.
        var engine = new PrologEngine();
        engine.ConsultString(":- set_prolog_flag(compile_mode, debug).");
        Assert.True(engine.Flags.EmitDebugInfo);
    }

    [Fact]
    public void Release_Multiclause_ErrorPosition_FallsBackToPredicate()
    {
        // The mirror of Chunk55's clause-specific-position tests: in release the
        // dbg_info markers are absent, so a frame resolves to the predicate's
        // position rather than the erroring clause's line.
        var engine = new PrologEngine();   // default release
        engine.ConsultString(
            ":- public divider/2.\n" +
            "divider(zero, _) :- true.\n" +      // line 2
            "divider(nz, X) :- _ is 10 / X.\n"); // line 3 (this clause errors)
        Assert.Throws<PrologRuntimeException>(() => engine.Query("divider(nz, 0)."));
        var frame = engine.LastErrorStackTraceWithPositions.First(f => f.Name == "divider");
        // Predicate-level fallback = the predicate's first line (2), not the
        // erroring clause's line (3) — that precision needs compile_mode=debug.
        Assert.Equal(2, frame.Position.Line);
    }
}
