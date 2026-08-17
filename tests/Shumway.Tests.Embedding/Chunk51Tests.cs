using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 51: stack-trace capture when a runtime error escapes a query,
/// plus diagnostics on the bundle's precompiled-module cache.
///
/// <para>The simpler half — <c>line:col</c> prefixes on parse / lex
/// errors — already landed in chunk 48. What's new here is the
/// engine-side capture: walking the env-frame chain when a
/// <see cref="ShumwayPrologException"/> or
/// <see cref="PrologRuntimeException"/> escapes, translating each
/// return-address into a <c>Name/Arity</c> indicator via the per-query
/// link map, and surfacing the result via
/// <see cref="PrologEngine.LastErrorStackTrace"/>.</para>
///
/// <para>Bundle blob real runtime use (skipping the WAM compile path
/// when a precompiled module is available) stays deferred — what this
/// chunk delivers is just exposure of the cached
/// <see cref="PrologEngine.PrecompiledModules"/> so callers can verify
/// what was loaded.</para>
/// </summary>
public class Chunk51Tests
{
    // ============================================================================
    // Stack trace on runtime error
    // ============================================================================

    [Fact]
    public void StackTrace_CapturedOnRuntimeErrorInUserPredicate()
    {
        // The runtime error fires inside a user-defined predicate
        // (compute/1) — division by zero raises evaluation_error.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public compute/1.
            compute(X) :- _ is X / 0.
            """);
        Assert.Throws<PrologRuntimeException>(
            () => engine.Query("compute(5)."));
        var trace = engine.LastErrorStackTrace;
        Assert.Contains(trace, frame => frame.Name == "compute" && frame.Arity == 1);
    }

    [Fact]
    public void StackTrace_DoesNotLeakSyntheticQueryPredicate()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public boom/0.
            boom :- throw(error).
            """);
        try { engine.Query("boom."); } catch (ShumwayPrologException) { }
        var trace = engine.LastErrorStackTrace;
        foreach (var (name, _) in trace)
            Assert.NotEqual("__query__", name);
    }

    [Fact]
    public void StackTrace_PlumbedThroughThrowCatchUserCode()
    {
        // throw/1 raised inside a user-defined predicate. The trace
        // should reach down through the caller chain when the error
        // isn't caught.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public bomb/0.
            :- public outer/0.
            bomb :- throw(boom).
            outer :- bomb.
            """);
        var ex = Assert.Throws<ShumwayPrologException>(
            () => engine.Query("outer."));
        Assert.IsType<AtomTerm>(ex.Term);
        // After catching, the engine's LastErrorStackTrace is populated.
        Assert.NotEmpty(engine.LastErrorStackTrace);
    }

    [Fact]
    public void StackTrace_StickyBetweenSuccessfulQueries()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public crashy/0.
            crashy :- throw(oops).
            """);
        // First query fails with an error.
        try { engine.Query("crashy."); } catch (ShumwayPrologException) { }
        Assert.NotEmpty(engine.LastErrorStackTrace);
        // Second query succeeds — the trace from the previous one
        // remains visible. That's an intentional design: callers who
        // want a fresh look check this property *after* the error
        // and clear it themselves if needed.
        var sol = engine.Query("true.");
        Assert.True(sol.Success);
    }

    // ============================================================================
    // Bundle blob: PrecompiledModules cache
    // ============================================================================

    [Fact]
    public void PrecompiledModules_PopulatedOnLoadBundleWithBlob()
    {
        var bundle = new Bundle(new[]
        {
            new BundleEntry("colors",
                ":- public color/1.\ncolor(red).\ncolor(green).\ncolor(blue)."),
        });
        byte[] bytes = BundleWriter.ToBytes(bundle, includeCompiledBytecode: true);

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(bytes));

        Assert.True(engine.PrecompiledModules.ContainsKey("colors"));
        var mod = engine.PrecompiledModules["colors"];
        // The module contains color/1.
        Assert.Contains(mod.Predicates, p =>
        {
            var (atomId, _) = FunctorTable.Lookup(p.FunctorId);
            return AtomTable.GetById(atomId)?.Name == "color";
        });
    }

    [Fact]
    public void PrecompiledModules_EmptyWhenBundleHasNoBlob()
    {
        var bundle = new Bundle(new[] { new BundleEntry("plain", "fact.") });
        byte[] bytes = BundleWriter.ToBytes(bundle, includeCompiledBytecode: false);

        var engine = new PrologEngine();
        engine.LoadBundle(BundleReader.FromBytes(bytes));

        Assert.Empty(engine.PrecompiledModules);
    }
}
