using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 346 (Phase 28): compatibility fixes surfaced by running a real
/// third-party program (a Lines of Action board game) through Shumway — the
/// kind of gap a vanilla program hits that the test corpus did not. Each was a
/// missing or too-strict library behaviour:
/// <list type="bullet">
/// <item><c>nth0/3</c> / <c>nth1/3</c> with a variable index now enumerate
/// (SWI/SICStus), instead of raising instantiation_error — see also
/// <c>Chunk131cTests</c>.</item>
/// <item><c>sumlist/2</c> — the older SWI alias of <c>sum_list/2</c>.</item>
/// <item><c>format/1</c> — <c>format(Fmt)</c> with no arguments.</item>
/// <item><c>format/2</c> directives <c>~c</c> (character), numeric prefixes
/// (<c>~Nc</c>), and <c>~t</c> / <c>~|</c> column control (accepted, so a
/// column-aligned format string runs rather than raising domain_error).</item>
/// </list>
/// </summary>
public class Chunk346Tests
{
    private static string OutOf(string goal)
    {
        var e = new PrologEngine();
        var sw = new System.IO.StringWriter();
        e.Out = sw;
        Assert.True(e.Query(goal).Success);
        return sw.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void Nth_VariableIndex_Enumerates()
    {
        var e = new PrologEngine();
        var rows = e.QueryAll("nth1(N, [x, y, z], E).")
            .Select(s => (((IntTerm)s["N"]).Value, ((AtomTerm)s["E"]).Name)).ToList();
        Assert.Equal(new[] { (1L, "x"), (2L, "y"), (3L, "z") }, rows);

        // The board-iteration shape: enumerate a list of lists.
        var cells = e.QueryAll(
            "nth0(R, [[a,b],[c,d]], Row), nth0(C, Row, Cell).").Count();
        Assert.Equal(4, cells);   // 2x2 grid
    }

    [Fact]
    public void Sumlist_AliasOfSumList()
    {
        var sol = new PrologEngine().Query("sumlist([1,2,3,4], S).");
        Assert.True(sol.Success);
        Assert.Equal(new IntTerm(10), sol["S"]);
    }

    [Fact]
    public void Format1_NoArguments()
    {
        Assert.Equal("hi\n", OutOf("format(\"hi~n\")."));
    }

    [Fact]
    public void Format_CharAndPrefixAndColumn()
    {
        Assert.Equal("A", OutOf("format(\"~c\", [0'A])."));
        Assert.Equal("***", OutOf("format(\"~3c\", [0'*])."));     // ~Nc repeats
        // ~t with no column stop is a no-op; the whole string still prints.
        Assert.Equal("none\n", OutOf("format(\"~tnone~n\")."));
        // ~| column stop is accepted (unaligned, not an error).
        Assert.Equal("ab", OutOf("format(\"a~tb\")."));
    }
}
