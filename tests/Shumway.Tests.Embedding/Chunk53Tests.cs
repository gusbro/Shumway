using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 53: <see cref="CompiledPredicate.SourcePosition"/> threaded
/// through the WAM compiler, surfaced in
/// <see cref="PrologEngine.LastErrorStackTraceWithPositions"/>; plus
/// the <see cref="PrologEngine.PrecompiledClauseCache"/> that
/// LoadBundle populates from any bundle blob it loads.
///
/// <para>Honest scope notes:</para>
/// <list type="bullet">
/// <item>The source position is the predicate's *first clause* —
///   precise byte-offset-to-clause mapping (Meta dbg_info opcodes)
///   stays deferred; that wants a compiler pass that touches every
///   clause boundary, which we can land separately without breaking
///   the surface area this chunk introduces.</item>
/// <item>The precompiled-clause cache is exposed as a property; the
///   query setup path doesn't yet skip ModuleCompiler when the cache
///   covers everything (that needs careful interplay with module
///   mangling and dynamic clauses). What's here is the load-time
///   half; the query-time half lands later.</item>
/// </list>
/// </summary>
public class Chunk53Tests
{
    // ============================================================================
    // Source positions in stack traces
    // ============================================================================

    [Fact]
    public void StackFrame_CarriesFirstClausePosition()
    {
        // Run a query that errors inside a user predicate; the frame
        // for that predicate should carry the predicate's source pos.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public divider/2.\n" +
            "divider(N, D) :- _ is N / D.\n");
        Assert.Throws<PrologRuntimeException>(
            () => engine.Query("divider(10, 0)."));
        var frames = engine.LastErrorStackTraceWithPositions;
        Assert.NotEmpty(frames);
        var dividerFrame = frames.First(f => f.Name == "divider");
        // The clause is on the 2nd line of the consulted source (the
        // first line is the public directive).
        Assert.Equal(2, dividerFrame.Position.Line);
    }

    [Fact]
    public void StackFrame_ToString_FormatsNameAndPosition()
    {
        var pos = new SourcePosition(Line: 5, Column: 3, Offset: 42);
        var frame = new PrologEngine.StackFrame("foo", 2, pos);
        Assert.Equal("foo/2 at 5:3", frame.ToString());
    }

    [Fact]
    public void StackFrame_ToString_OmitsPositionForUnknownSource()
    {
        // SourcePosition.Start (line 1, col 1, offset 0) means "no
        // useful position". ToString should hide the noisy "at 1:1".
        var frame = new PrologEngine.StackFrame("synthetic", 0, SourcePosition.Start);
        Assert.Equal("synthetic/0", frame.ToString());
    }

    [Fact]
    public void StackFrame_MatchesPlainTraceShape()
    {
        // Plain and with-positions traces should contain the same
        // predicates in the same order.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public top/0.\n" +
            "top :- _ is 1 / 0.\n");
        try { engine.Query("top."); } catch (PrologRuntimeException) { }
        var plain = engine.LastErrorStackTrace;
        var withPos = engine.LastErrorStackTraceWithPositions;
        Assert.Equal(plain.Count, withPos.Count);
        for (int i = 0; i < plain.Count; i++)
        {
            Assert.Equal(plain[i].Name, withPos[i].Name);
            Assert.Equal(plain[i].Arity, withPos[i].Arity);
        }
    }

    // ============================================================================
    // PrecompiledClauseCache
    // ============================================================================

    [Fact(Skip = "Phase 14: LoadBundle no longer populates PrecompiledClauseCache "
        + "from bundle bytecode. The substitute-into-ModuleCompiler path proved "
        + "unsafe when ShmoCompiler's transform pipeline didn't fully align with "
        + "ConsultString's (Blint.pl's `(A -> B)` standalone case was the surfacing "
        + "example). Re-enable once ShmoCompiler runs the full ConsultString-equivalent "
        + "pipeline including ModuleRewrite + ModeSpecialization.")]
    public void PrecompiledClauseCache_PopulatedFromBundleBlob() { }

    [Fact]
    public void PrecompiledClauseCache_EmptyWithoutBlob()
    {
        var bundle = new Bundle(new[] { new BundleEntry("plain", "fact.") });
        byte[] bytes = BundleWriter.ToBytes(bundle, includeCompiledBytecode: false);
        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(bytes));
        Assert.Empty(engine.PrecompiledClauseCache);
    }

    [Fact]
    public void PrecompiledClauseCache_BundleStillFunctional()
    {
        // The cache is exposed for diagnostics; the bundle's actual
        // queries should keep answering correctly via the consulted
        // source path.
        var bundle = new Bundle(new[]
        {
            new BundleEntry("c",
                ":- public color/1.\ncolor(red). color(green). color(blue)."),
        });
        byte[] bytes = BundleWriter.ToBytes(bundle, includeCompiledBytecode: true);

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(bytes));

        Assert.True(engine.Query("color(red).").Success);
        Assert.False(engine.Query("color(purple).").Success);
        Assert.Equal(3, engine.QueryAll("color(_).").Count());
    }
}
