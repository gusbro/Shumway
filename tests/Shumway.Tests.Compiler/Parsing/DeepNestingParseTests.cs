using System.Text;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Xunit;

namespace Shumway.Tests.Compiler.Parsing;

/// <summary>
/// The reader must accept a term nested as deeply as a writer can emit one.
///
/// <para><c>write_canonical/1</c> renders a list in functional notation —
/// <c>'.'(1,'.'(2,…))</c> — so a canonical ten-thousand-element list is a
/// ten-thousand-deep nest. Recursing once per level meant the engine's own
/// output could not be read back: the C# stack overflowed, which kills the
/// process rather than raising a syntax error. Functional-notation arguments
/// are therefore read with an explicit frame stack.</para>
///
/// <para>A regression here does not fail politely — it takes the test run
/// down with it.</para>
/// </summary>
public class DeepNestingParseTests
{
    private const int Deep = 20_000;

    private static Term Parse(string source) =>
        new Parser(new global::Shumway.Compiler.Lexer.Lexer(source)).ReadTerm();

    /// <summary>Builds <c>'.'(0,'.'(1,…,[]…))</c> as text.</summary>
    private static string CanonicalListText(int n)
    {
        var sb = new StringBuilder(n * 8);
        for (int i = 0; i < n; i++) sb.Append("'.'(").Append(i).Append(',');
        sb.Append("[]");
        sb.Append(')', n);
        return sb.ToString();
    }

    [Fact]
    public void CanonicalListNotation_OfAnyDepth()
    {
        Term t = Parse(CanonicalListText(Deep));
        // Walk the spine iteratively — the test must not recurse either.
        int count = 0;
        while (t is CompoundTerm c && c.Functor == "." && c.Args.Length == 2)
        {
            Assert.Equal(count, ((IntTerm)c.Args[0]).Value);
            t = c.Args[1];
            count++;
        }
        Assert.Equal(Deep, count);
        Assert.Equal("[]", Assert.IsType<AtomTerm>(t).Name);
    }

    [Fact]
    public void NestedInANonLastArgument()
    {
        // The nest is argument 1 of 2, so closing it does not close the
        // parent: the frame stack has to keep reading the parent's arguments.
        var sb = new StringBuilder();
        for (int i = 0; i < Deep; i++) sb.Append("f(").Append(i).Append(',');
        sb.Append("done");
        for (int i = 0; i < Deep; i++) sb.Append(", tag)");
        Term t = Parse(sb.ToString());
        int depth = 0;
        while (t is CompoundTerm c && c.Functor == "f")
        {
            Assert.Equal(3, c.Args.Length);
            Assert.Equal("tag", ((AtomTerm)c.Args[2]).Name);
            t = c.Args[1];
            depth++;
        }
        Assert.Equal(Deep, depth);
        Assert.Equal("done", Assert.IsType<AtomTerm>(t).Name);
    }

    [Fact]
    public void ADeepNestIsStillAnOperand()
    {
        // Closing a nest returns to an argument that may continue with an
        // operator — the frame stack must hand the compound back to the
        // operator loop, not treat it as a finished argument.
        Assert.Equal(
            new CompoundTerm("f", new Term[]
            {
                new CompoundTerm("+", new Term[]
                {
                    new CompoundTerm("g", new Term[] { new IntTerm(1) }),
                    new IntTerm(2),
                }),
                new IntTerm(3),
            }),
            Parse("f(g(1) + 2, 3)"));

        var sb = new StringBuilder("h(");
        for (int i = 0; i < Deep; i++) sb.Append("f(");
        sb.Append('0');
        sb.Append(')', Deep);
        sb.Append(" + 1)");
        Term t = Parse(sb.ToString());
        var outer = Assert.IsType<CompoundTerm>(t);
        Assert.Equal("h", outer.Functor);
        var sum = Assert.IsType<CompoundTerm>(outer.Args[0]);
        Assert.Equal("+", sum.Functor);
        Assert.Equal(1L, ((IntTerm)sum.Args[1]).Value);
    }

    [Fact]
    public void ZeroArgumentCompound_StillRejected()
    {
        var ex = Assert.Throws<ParseException>(() => Parse("f()"));
        Assert.Contains("at least one argument", ex.Message);
        // …and the same inside a nest, where the frame stack reports it.
        Assert.Throws<ParseException>(() => Parse("f(g(h()))"));
    }
}
