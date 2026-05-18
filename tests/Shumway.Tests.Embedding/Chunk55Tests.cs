using Shumway.Compiler.Lexer;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 55: Meta(DbgInfo, clauseIndex) opcodes emitted at each clause
/// boundary by <see cref="PredicateCompiler"/> and consumed by the
/// runtime stack-trace path so frames carry their precise per-clause
/// source position (the deferred half from chunk 53 — see the
/// <see cref="Chunk53Tests"/> docs comment).
///
/// <para>Also exercises the new bundle codec format (v2): the
/// predicate-level + per-clause source positions round-trip through
/// the compiled-bytecode blob.</para>
/// </summary>
public class Chunk55Tests
{
    // ============================================================================
    // Per-clause source positions in stack frames
    // ============================================================================

    [Fact]
    public void StackFrame_UsesClauseSpecificPosition_NotFirstClause()
    {
        // Build a predicate with two clauses on different lines; force the
        // SECOND clause to be the one that errors. The frame's position
        // should point at the second clause's line, not the first's.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public divider/2.\n" +
            "divider(zero, _) :- true.\n" +     // line 2: matches divider(zero, _).
            "divider(nz, X) :- _ is 10 / X.\n"); // line 3: matches divider(nz, _).
        Assert.Throws<PrologRuntimeException>(
            () => engine.Query("divider(nz, 0)."));
        var frames = engine.LastErrorStackTraceWithPositions;
        var dividerFrame = frames.First(f => f.Name == "divider");
        // The clause that errored is the SECOND one — line 3.
        Assert.Equal(3, dividerFrame.Position.Line);
    }

    [Fact]
    public void StackFrame_FirstClauseStillResolved_WhenItErrors()
    {
        // Symmetric check: when the first clause is what errors, the
        // frame still points at clause 1, not clause 2.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public divider/2.\n" +
            "divider(zero, X) :- _ is X / 0.\n" + // line 2
            "divider(nz, X) :- _ is 10 / X.\n");  // line 3
        Assert.Throws<PrologRuntimeException>(
            () => engine.Query("divider(zero, 5)."));
        var frames = engine.LastErrorStackTraceWithPositions;
        var dividerFrame = frames.First(f => f.Name == "divider");
        Assert.Equal(2, dividerFrame.Position.Line);
    }

    [Fact]
    public void StackFrame_SingleClause_UsesPredicatePosition()
    {
        // Backwards-compat: single-clause predicates have no Meta opcode
        // (the chain machinery isn't engaged) and the frame falls back
        // to the predicate's first-clause source position.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public single/1.\n" +
            "single(X) :- _ is X / 0.\n");      // line 2
        Assert.Throws<PrologRuntimeException>(
            () => engine.Query("single(7)."));
        var frames = engine.LastErrorStackTraceWithPositions;
        var frame = frames.First(f => f.Name == "single");
        Assert.Equal(2, frame.Position.Line);
    }

    // ============================================================================
    // CompiledPredicate.ClauseSourcePositions surface
    // ============================================================================

    [Fact]
    public void CompiledPredicate_ClauseSourcePositionsAlignedWithSourceOrder()
    {
        // Compile a 3-clause predicate directly through the compiler and
        // check the per-clause positions are recorded in source order.
        const string source =
            ":- public p/1.\n" +
            "p(a).\n" +     // line 2
            "p(b).\n" +     // line 3
            "p(c).\n";      // line 4
        var clauses = ParseClauses(source);
        var module = new ModuleCompiler().Compile(clauses);
        var pred = module.Predicates.First(
            p => AtomTable.GetById(FunctorTable.Lookup(p.FunctorId).AtomId)?.Name == "p");
        Assert.Equal(3, pred.ClauseSourcePositions.Count);
        Assert.Equal(2, pred.ClauseSourcePositions[0].Line);
        Assert.Equal(3, pred.ClauseSourcePositions[1].Line);
        Assert.Equal(4, pred.ClauseSourcePositions[2].Line);
    }

    [Fact]
    public void CompiledPredicate_SingleClause_RecordsOnePosition()
    {
        const string source =
            ":- public q/1.\n" +
            "q(only).\n";   // line 2
        var clauses = ParseClauses(source);
        var module = new ModuleCompiler().Compile(clauses);
        var pred = module.Predicates.First(
            p => AtomTable.GetById(FunctorTable.Lookup(p.FunctorId).AtomId)?.Name == "q");
        Assert.Single(pred.ClauseSourcePositions);
        Assert.Equal(2, pred.ClauseSourcePositions[0].Line);
    }

    // ============================================================================
    // Bundle codec round-trips ClauseSourcePositions (v2)
    // ============================================================================

    [Fact]
    public void BundleCodec_V2_RoundTripsClausePositions()
    {
        var bundle = new Bundle(new[]
        {
            new BundleEntry("multi",
                ":- public step/1.\n" +
                "step(one).\n" +     // line 2
                "step(two).\n" +     // line 3
                "step(three).\n"),   // line 4
        });
        byte[] bytes = BundleWriter.ToBytes(bundle, includeCompiledBytecode: true);

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(bytes));

        int fid = FunctorTable.Intern(
            AtomTable.Intern("step", permanent: true).Id, 1);
        Assert.True(engine.PrecompiledClauseCache.ContainsKey(fid));
        var cached = engine.PrecompiledClauseCache[fid];
        Assert.Equal(3, cached.ClauseSourcePositions.Count);
        Assert.Equal(2, cached.ClauseSourcePositions[0].Line);
        Assert.Equal(3, cached.ClauseSourcePositions[1].Line);
        Assert.Equal(4, cached.ClauseSourcePositions[2].Line);
    }

    // ============================================================================
    // Bundle blob skip-compile (chunk 55, PART 1)
    // ============================================================================

    [Fact]
    public void SkipCompile_ReusesCachedPredicateInstance_AfterLoadBundle()
    {
        // Build a bundle whose compiled blob contains greet/1. After
        // LoadBundle the cache has greet/1; the first query setup should
        // re-use the cached CompiledPredicate verbatim — by reference —
        // instead of running PredicateCompiler over the source again.
        var bundle = new Bundle(new[]
        {
            new BundleEntry("g",
                ":- public greet/1.\ngreet(hello). greet(world)."),
        });
        byte[] bytes = BundleWriter.ToBytes(bundle, includeCompiledBytecode: true);

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(bytes));

        int fid = FunctorTable.Intern(
            AtomTable.Intern("greet", permanent: true).Id, 1);
        CompiledPredicate cachedPred = engine.PrecompiledClauseCache[fid];

        // Drive a query so SetupQueryFromTerm runs ModuleCompiler. We
        // inspect the engine's linked predicates-by-address afterward to
        // confirm the same object is being used.
        Assert.True(engine.Query("greet(hello).").Success);

        var current = engine.CurrentPredicatesByAddressForTest;
        Assert.NotNull(current);
        var reused = current!.Values.FirstOrDefault(p => p.FunctorId == fid);
        Assert.NotNull(reused);
        Assert.Same(cachedPred, reused);
    }

    [Fact]
    public void SkipCompile_BundlePredicate_StillAnswersQueriesCorrectly()
    {
        // The reused cached predicate should still answer queries
        // identically to a freshly-compiled one — the linker patches the
        // cached predicate's call sites by functor id at link time, so
        // anything the predicate calls still resolves in the new program.
        var bundle = new Bundle(new[]
        {
            new BundleEntry("colors",
                ":- public color/1.\ncolor(red). color(green). color(blue)."),
        });
        byte[] bytes = BundleWriter.ToBytes(bundle, includeCompiledBytecode: true);

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(bytes));

        Assert.True(engine.Query("color(red).").Success);
        Assert.True(engine.Query("color(green).").Success);
        Assert.False(engine.Query("color(purple).").Success);
        Assert.Equal(3, engine.QueryAll("color(_).").Count());
    }

    // ============================================================================
    // Helpers
    // ============================================================================

    private static IEnumerable<Shumway.Compiler.Ast.Clause> ParseClauses(string source)
    {
        var reader = new Shumway.Compiler.Parsing.ClauseReader(
            new Shumway.Compiler.Lexer.Lexer(source),
            Shumway.Compiler.Parsing.OperatorTable.Default());
        return reader.ReadAll().ToList();
    }
}
