using System.Linq;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>
/// Persistent literal pools (ADR-015 chunk B). Passing a shared
/// <see cref="LiteralPools"/> to successive <see cref="ModuleCompiler.Compile"/>
/// calls makes literal ids stable across compilations — the precondition
/// for caching a separately-linked static code region whose bytecode
/// embeds those ids.
/// </summary>
public class LiteralPoolsTests
{
    private static CompiledModule Compile(string source, LiteralPools? pools)
        => new ModuleCompiler().Compile(
            new ClauseReader(source).ReadAll().ToList(),
            cache: null, unindexedFunctors: null, pools: pools);

    [Fact]
    public void WithoutSharedPools_EachCompilationStartsFresh()
    {
        Compile("a(3.14).", pools: null);
        var second = Compile("b(2.71).", pools: null);

        Assert.DoesNotContain(3.14, second.FloatLiterals);
        Assert.Contains(2.71, second.FloatLiterals);
    }

    [Fact]
    public void WithSharedPools_LiteralsAccumulate()
    {
        var pools = new LiteralPools();
        Compile("a(3.14).", pools);
        var second = Compile("b(2.71).", pools);

        Assert.Contains(3.14, second.FloatLiterals);
        Assert.Contains(2.71, second.FloatLiterals);
    }

    [Fact]
    public void WithSharedPools_AnExistingLiteralKeepsItsId()
    {
        var pools = new LiteralPools();
        var first = Compile("a(3.14).", pools);
        // The second compilation re-uses 3.14 and introduces 2.71.
        var second = Compile("""
            b(2.71).
            c(3.14).
            """, pools);

        int idInFirst = first.FloatLiterals.ToList().IndexOf(3.14);
        int idInSecond = second.FloatLiterals.ToList().IndexOf(3.14);
        Assert.Equal(idInFirst, idInSecond);
    }
}
