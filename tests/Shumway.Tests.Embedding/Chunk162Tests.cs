using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 13 chunk 162: the <c>:- ensure_linked/1</c> directive. The
/// compiler records each indicator into
/// <see cref="ShmoObject.EnsureLinked"/>; the linker (chunk 163) then
/// treats those as additional reachability roots so predicates only
/// invoked via runtime meta-call survive dead-code elimination and
/// missing-target reporting.
/// </summary>
public class Chunk162Tests
{
    [Fact]
    public void EnsureLinked_SingleSpec_Recorded()
    {
        var obj = ShmoCompiler.CompileSource("""
            :- ensure_linked foo/2.
            p(X) :- foo(X, _).
            foo(_, _).
            """);
        var pref = Assert.Single(obj.EnsureLinked);
        Assert.Equal(new PredicateRef("foo", 2), pref);
    }

    [Fact]
    public void EnsureLinked_ListSpec_AllRecorded()
    {
        var obj = ShmoCompiler.CompileSource(":- ensure_linked [a/0, b/1, c/2].");
        Assert.Equal(3, obj.EnsureLinked.Count);
        Assert.Contains(new PredicateRef("a", 0), obj.EnsureLinked);
        Assert.Contains(new PredicateRef("b", 1), obj.EnsureLinked);
        Assert.Contains(new PredicateRef("c", 2), obj.EnsureLinked);
    }

    [Fact]
    public void EnsureLinked_MultipleDirectives_Combined()
    {
        var obj = ShmoCompiler.CompileSource("""
            :- ensure_linked foo/0.
            :- ensure_linked bar/1.
            """);
        Assert.Contains(new PredicateRef("foo", 0), obj.EnsureLinked);
        Assert.Contains(new PredicateRef("bar", 1), obj.EnsureLinked);
    }

    [Fact]
    public void EnsureLinked_RoundTripsThroughShmoIo()
    {
        var obj = ShmoCompiler.CompileSource("""
            :- ensure_linked target/3.
            p(X) :- call(target(X, a, b)).
            target(_, _, _).
            """);
        byte[] bytes = ShmoWriter.ToBytes(obj);
        var restored = ShmoReader.FromBytes(bytes);
        var pref = Assert.Single(restored.EnsureLinked);
        Assert.Equal(new PredicateRef("target", 3), pref);
    }

    [Fact]
    public void EnsureLinked_Malformed_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => ShmoCompiler.CompileSource(":- ensure_linked foo."));
    }

    [Fact]
    public void NoEnsureLinkedDirective_EmptyList()
    {
        var obj = ShmoCompiler.CompileSource("p(1).");
        Assert.Empty(obj.EnsureLinked);
    }
}
