using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 114 (Phase 8, ADR-015 chunk A): the dynamic-database generation
/// counter. <see cref="PrologEngine.DbGeneration"/> is a monotonic clock
/// bumped by every dynamic-store mutation — the foundation the later
/// ADR-015 chunks capture to implement the ISO logical update view.
/// </summary>
public class Chunk114Tests
{
    private static PrologEngine Dyn()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        return e;
    }

    [Fact]
    public void Assertz_AdvancesTheGeneration()
    {
        var e = Dyn();
        long before = e.DbGeneration;
        e.Query("assertz(d(1)).");
        Assert.True(e.DbGeneration > before);
    }

    [Fact]
    public void Asserta_AdvancesTheGeneration()
    {
        var e = Dyn();
        long before = e.DbGeneration;
        e.Query("asserta(d(1)).");
        Assert.True(e.DbGeneration > before);
    }

    [Fact]
    public void Retract_AdvancesTheGeneration()
    {
        var e = Dyn();
        e.Query("assertz(d(1)).");
        long before = e.DbGeneration;
        e.Query("retract(d(1)).");
        Assert.True(e.DbGeneration > before);
    }

    [Fact]
    public void Abolish_AdvancesTheGeneration()
    {
        var e = Dyn();
        e.Query("assertz(d(1)).");
        long before = e.DbGeneration;
        e.Query("abolish(d/1).");
        Assert.True(e.DbGeneration > before);
    }

    [Fact]
    public void PureQuery_DoesNotAdvanceTheGeneration()
    {
        var e = Dyn();
        e.Query("assertz(d(1)).");
        long before = e.DbGeneration;
        // A query that only reads the database leaves the clock alone.
        Assert.True(e.Query("d(1).").Success);
        Assert.Equal(before, e.DbGeneration);
    }

    [Fact]
    public void EachAssertedClause_AdvancesTheGenerationOnce()
    {
        var e = Dyn();
        long before = e.DbGeneration;
        e.Query("assertz(d(1)), assertz(d(2)), assertz(d(3)).");
        Assert.Equal(before + 3, e.DbGeneration);
    }
}
