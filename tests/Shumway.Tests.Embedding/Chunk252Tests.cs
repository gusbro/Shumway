using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 252: <see cref="Solution.ToString(int)"/> pretty-prints
/// bindings whose compact form exceeds the column budget. Compact
/// terms render as the parameterless <see cref="Solution.ToString()"/>
/// would; the multi-line form only kicks in when needed.
/// </summary>
public class Chunk252Tests
{
    [Fact]
    public void Compact_BindingsFit_NoBreaks()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X = foo, Y = 42.");
        // Both fit easily under any reasonable width.
        string s = sol.ToString(80);
        Assert.Equal("X = foo,\nY = 42", s);
    }

    [Fact]
    public void Compact_FitsUnderWidth_StaysOneLine()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("L = [1, 2, 3, 4, 5].");
        // [1, 2, 3, 4, 5] is well under 80 columns.
        Assert.Equal("L = [1, 2, 3, 4, 5]", sol.ToString(80));
    }

    [Fact]
    public void LongList_Breaks()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public make/1.
            make([alpha, beta, gamma, delta, epsilon, zeta, eta, theta, iota, kappa]).
            """);
        var sol = engine.Query("make(L).");
        // Tight width forces multi-line.
        string s = sol.ToString(40);
        // The list opens after "L = ", so the [ is at col 4 and
        // elements indent two past it (col 6). Just verify the
        // shape: multi-line, contains every element, closes with ].
        Assert.Contains("\n", s);
        Assert.Contains("alpha,\n", s);
        Assert.Contains("kappa", s);
        Assert.EndsWith("]", s.TrimEnd());
    }

    [Fact]
    public void LongCompound_Breaks()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public make/1.
            make(record(field_one_with_a_long_name, field_two_also_quite_long, field_three_also_long)).
            """);
        var sol = engine.Query("make(R).");
        string s = sol.ToString(40);
        Assert.Contains("record(\n", s);
        Assert.Contains("field_one_with_a_long_name", s);
        Assert.EndsWith(")", s.TrimEnd());
    }

    [Fact]
    public void NestedLong_BreaksRecursively()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public outer/1.
            outer(pair(inner(a, b, c, d, e, f, g), [one, two, three, four, five, six, seven])).
            """);
        var sol = engine.Query("outer(T).");
        string s = sol.ToString(30);
        // Parent compound broke.
        Assert.Contains("pair(\n", s);
        // At least one of the children also broke.
        Assert.True(s.Contains("inner(\n") || s.Contains("[\n  one"));
    }

    [Fact]
    public void Compact_ToString_IgnoresWidth()
    {
        // Default ToString() (no width arg) stays compact regardless
        // of how long the binding is — embedding-API consumers that
        // log to a file want predictable single-line output.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public make/1.
            make([a, b, c, d, e, f, g, h, i, j, k, l, m, n, o, p, q]).
            """);
        var sol = engine.Query("make(L).");
        string compact = sol.ToString();
        Assert.DoesNotContain("\n", compact);
    }

    [Fact]
    public void SuccessWithNoBindings_ReturnsTrue()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("true.");
        Assert.Equal("true", sol.ToString(80));
    }

    [Fact]
    public void FailedQuery_ReturnsFalse()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("fail.");
        Assert.Equal("false", sol.ToString(80));
    }

    [Fact]
    public void NarrowBudget_FallsBackToCompact()
    {
        // When the indent budget gets too small to break usefully,
        // the printer accepts the overflow and emits compact —
        // breaking with no room would produce uglier output.
        var engine = new PrologEngine();
        var sol = engine.Query("L = [one, two, three, four, five].");
        // Width = 10, "L = " takes 4, leaving 6 columns for L's value
        // — way too narrow to break a 5-element list usefully.
        string s = sol.ToString(10);
        // Still produces something readable (the list compact form).
        Assert.Contains("[one, two, three, four, five]", s);
    }
}
