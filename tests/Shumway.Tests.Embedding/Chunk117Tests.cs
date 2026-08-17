using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 117 (Phase 8, ADR-015 chunk B): the persistent code space.
/// Static predicates are linked once into a cached region; each query
/// links only its transient region (dynamic predicates + the synthetic
/// query clause) against it.
///
/// <para>The delicate case is a static predicate that calls a dynamic
/// one: the dynamic callee lives in the per-query region, so its address
/// is unknown when the static region is linked. Those call sites are
/// re-patched per query once the dynamic addresses are known.</para>
/// </summary>
public class Chunk117Tests
{
    [Fact]
    public void StaticPredicateCallingDynamic_Resolves()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- dynamic d/1.
            uses(X) :- d(X).
            """);          // static 'uses' calls dynamic 'd'
        engine.Query("assertz(d(7)).");
        Assert.True(engine.Query("uses(7).").Success);
    }

    [Fact]
    public void StaticPredicateCallingDynamic_AcrossManyQueries()
    {
        // The static region (with 'uses') is linked once and reused; the
        // static -> dynamic call site is re-patched on every query.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- dynamic d/1.
            uses(X) :- d(X).
            """);
        for (int i = 1; i <= 20; i++)
        {
            engine.Query($"assertz(d({i})).");
            Assert.True(engine.Query($"uses({i}).").Success);
        }
        Assert.Equal(20, engine.QueryAll("uses(X).").Count());
    }

    [Fact]
    public void Queries_StillWorkAfterReConsult()
    {
        // ConsultString changes the static program and invalidates the
        // cached static link; the next query rebuilds it.
        var engine = new PrologEngine();
        engine.ConsultString("greet(hello).");
        Assert.True(engine.Query("greet(hello).").Success);

        engine.ConsultString("""
            greet(goodbye).
            extra(here).
            """);
        Assert.True(engine.Query("greet(hello).").Success);
        Assert.True(engine.Query("greet(goodbye).").Success);
        Assert.True(engine.Query("extra(here).").Success);
    }

    [Fact]
    public void StaticToStaticCalls_StillResolve()
    {
        // Within the static region, calls resolve at link time as before.
        var engine = new PrologEngine();
        engine.ConsultString("""
            a(N) :- b(N).
            b(N) :- c(N).
            c(42).
            """);
        Assert.True(engine.Query("a(42).").Success);
        Assert.False(engine.Query("a(0).").Success);
    }
}
