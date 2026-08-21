using Shumway.Embedding;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-047's lazy input: a DCG over a packed list whose tail is a frozen
/// variable, so the grammar pulls the next window only when it reaches the end
/// of the one it has.
///
/// <para><b>Memory is not bounded yet</b>, and there is deliberately no test
/// claiming it is: the lazy tails are attributed variables and the heap
/// collector stands down while any is live, so consumed windows accumulate.
/// That is a heap-GC arc, not a defect of what is tested here.</para>
/// </summary>
public class LazyPhraseTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "shumway-pio-" + Guid.NewGuid());

    public LazyPhraseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path.Replace('\\', '/');
    }

    private static PrologEngine WithLines()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- use_module(library(pio)).
            lines([L|Ls]) --> line(L), !, lines(Ls).
            lines([]) --> [].
            line([]) --> "\n", !.
            line([C|Cs]) --> [C], line(Cs).
            """);
        return e;
    }

    [Fact]
    public void AGrammarRunsOverAFileItNeverReadsWhole()
    {
        var e = WithLines();
        string f = Write("three.txt", "a\nb\nc\n");
        Assert.True(e.Query($"phrase_from_file(lines([[a],[b],[c]]), '{f}').").Success);
    }

    [Fact]
    public void TheWindowIsIdempotentUnderBacktracking()
    {
        // The grammar tries line//1's first clause (a newline), fails on the
        // first character, and tries the second — waking the SAME lazy cell
        // twice. A plain read would hand it the NEXT characters the second
        // time, and the parse would quietly see an input the file does not
        // contain. The window is far larger than the file here, so every one
        // of those re-wakes is a re-read of offset 0.
        var e = WithLines();
        string f = Write("back.txt", "abc\nde\n");
        Assert.True(e.Query($"phrase_from_file(lines([[a,b,c],[d,e]]), '{f}').").Success);
    }

    [Fact]
    public void ItSpansManyWindows()
    {
        // 4096 is the window; this file needs several, so the seam between
        // them is exercised — including a line that straddles one.
        var e = WithLines();
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 2000; i++) sb.Append("0123456789\n");
        string f = Write("many.txt", sb.ToString());

        var sol = e.Query($"phrase_from_file(lines(Ls), '{f}'), length(Ls, N).");
        Assert.True(sol.Success);
        // The last line consumes the trailing newline, so lines//1 sees 2000.
        Assert.Equal(2000L, ((Shumway.Compiler.Ast.IntTerm)sol["N"]!).Value);
    }

    [Fact]
    public void TheElementsFollowTheRequestedPresentation()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- use_module(library(pio)).
            two([A,B]) --> [A], [B].
            """);
        string f = Write("ab.txt", "ab");
        Assert.True(e.Query($"phrase_from_file(two([a,b]), '{f}').").Success);
        Assert.True(e.Query(
            $"phrase_from_file(two([0'a,0'b]), '{f}', [text_kind(codes)]).").Success);
    }

    [Fact]
    public void AnEmptyFileIsTheEmptyList()
    {
        var e = WithLines();
        string f = Write("empty.txt", "");
        Assert.True(e.Query($"phrase_from_file(lines([]), '{f}').").Success);
    }

    [Fact]
    public void TheFileIsClosedOnTheWayOutEvenWhenTheGrammarFails()
    {
        var e = WithLines();
        string f = Write("close.txt", "a\n");
        Assert.False(e.Query($"phrase_from_file(lines([[z]]), '{f}').").Success);
        // A stream left open would still be in the registry under its path.
        // (user_input is always a live read stream, so the name is the test.)
        Assert.False(e.Query($"current_stream('{f}', read, _).").Success);
    }

    [Fact]
    public void PartialStringBuildsAListWithTheTailItIsGiven()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("partial_string([a,b,c], L, T), T = [d], L == [a,b,c,d].").Success);
        Assert.True(e.Query("partial_string([0'a], L, T), L = [H|T2], H == 0'a, T2 == T.").Success);
        // Empty text makes the two the same list.
        Assert.True(e.Query("partial_string([], L, T), L == T.").Success);
    }
}
