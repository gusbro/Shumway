using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>listing/1 and portray_clause print control constructs in the
/// standard alternative layout — a multi-line goal containing `;`/`->` used
/// to fall into the generic-compound branch and print the canonical
/// <c>;((C-&gt;T), E)</c>, which no reader should ever see — and quote atoms
/// that would not re-read as themselves ('All tests passed').</summary>
public sealed class ListingControlLayoutTests
{
    private static string Capture(string source, string spec)
    {
        var e = new PrologEngine();
        e.ConsultString(source);
        var sw = new StringWriter();
        e.Out = sw;
        e.Query($"listing({spec}).");
        return sw.ToString();
    }

    private const string Demo = """
        demo(L) :-
            findall(X, member(X, L), Xs),
            (   Xs == [] ->
                write('none here')
            ;   format(there, Xs),
                nl
            ),
            write(done).
        """;

    [Fact]
    public void IfThenElse_PrintsTheAlternativeLayout()
    {
        string output = Capture(Demo, "demo/1");
        Assert.DoesNotContain(";(", output);
        Assert.Contains("->", output);
        // The `;` sits at the start of its own aligned line.
        Assert.Contains("\n    ;   ", output.Replace("\r\n", "\n"));
    }

    [Fact]
    public void QuotedAtoms_SurviveTheListing()
    {
        string output = Capture(Demo, "demo/1");
        Assert.Contains("'none here'", output);
    }

    [Fact]
    public void TheListing_ReconsultsToTheSameBehaviour()
    {
        string output = Capture(Demo, "demo/1");
        // Strip the `:- dynamic` header comment lines the listing may add;
        // what remains must be valid Prolog defining the same predicate.
        string clauses = string.Join("\n",
            output.Replace("\r\n", "\n").Split('\n')
                  .Where(l => !l.StartsWith(":- dynamic")));
        var e2 = new PrologEngine();
        e2.ConsultString(":- dynamic(demo/1).\n" + clauses);
        var sw = new StringWriter();
        e2.Out = sw;
        Assert.True(e2.Query("demo([]).").Success);
        Assert.Contains("none here", sw.ToString());
        Assert.True(e2.Query("demo([a]).").Success);
    }
}
