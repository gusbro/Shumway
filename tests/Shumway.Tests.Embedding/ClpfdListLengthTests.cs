using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary><c>@L</c>, the length of a list as a CLP(FD) variable
/// (docs/design/clpfd-list-length.md): the open tail carries the link and
/// moves with it as the list grows; the length's propagator walks the list
/// when the length narrows.</summary>
public sealed class ClpfdListLengthTests
{
    private static PrologEngine Fd()
    {
        var e = new PrologEngine { Out = new StringWriter(), Warnings = new StringWriter() };
        e.UseClpfd();
        return e;
    }

    private static bool Holds(string goal) => Fd().Query(goal + ".").Success;

    private static string Caught(string goal)
    {
        var e = Fd();
        var printed = new StringWriter();
        e.Out = printed;
        Assert.True(e.Query($"catch(({goal}), error(E, _), true), writeq(E).").Success, goal);
        return printed.ToString().Trim();
    }

    /// <summary>The residual goals of Vars, rendered without spaces.</summary>
    private static string Residue(string goal, string vars)
    {
        var e = Fd();
        var s = e.Query($"{goal}, copy_term({vars}, _, Gs__).");
        Assert.True(s.Success, goal);
        return AstTermRenderer.Render(s["Gs__"]!, 4000, e.Operators).Replace(" ", "");
    }

    // ----- posting -----

    [Theory]
    [InlineData("@[a,b,c] #= N, N == 3")]
    [InlineData("@[] #= N, N == 0")]
    [InlineData("@L #= N, fd_inf(N, 0), fd_sup(N, sup)")]
    [InlineData("@[a,b|_] #= N, fd_inf(N, 2)")]
    [InlineData("@L #= N, @L #= M, M == N")]
    [InlineData("@[a|T] #= N, @T #= M, N = 5, M == 4")]
    [InlineData("@L #= N, fd_var(N), \\+ fd_var(L)")]
    public void Posting(string goal) => Assert.True(Holds(goal), goal);

    // ----- from the list to the length -----

    [Theory]
    [InlineData("@L #= N, L = [a|_], fd_inf(N, 1)")]
    [InlineData("@L #= N, L = [a,b,c], N == 3")]
    [InlineData("@L #= N, L = [a|T], T = [b|T2], fd_inf(N, 2), T2 = [], N == 2")]
    [InlineData("@L #= N, L = [a|T], T = [b|T2], T2 = [c], N == 3")]
    [InlineData("@L #= N, N #=< 2, L = [a,b|T], T == []")]
    [InlineData("@L #= N, L = [a|T], T = U, U = [b], N == 2")]
    public void FromTheListToTheLength(string goal) => Assert.True(Holds(goal), goal);

    [Theory]
    [InlineData("@L #= N, N #=< 2, L = [a,b,c]")]
    [InlineData("@L #= N, N #= 2, L = [a,b,c]")]
    [InlineData("@L #= 1, L = []")]
    public void TooLongOrTooShortFails(string goal) => Assert.False(Holds(goal), goal);

    // ----- from the length to the list -----

    [Theory]
    [InlineData("@L #= 3, L = [_,_,_]")]
    [InlineData("@L #= 0, L == []")]
    [InlineData("@L #> N, N #> 5, L = [_,_,_,_,_,_,_|T], var(T)")]
    [InlineData("@L #>= N, N #> 5, L = [_,_,_,_,_,_|T], var(T)")]
    [InlineData("A in -10..10, @L #= A, fd_dom(A, 0..10)")]
    [InlineData("@L #= N, N in 2..4, L = [a|T], T = [_|T2], var(T2), fd_dom(N, 2..4)")]
    [InlineData("@L #= N, L = [a|_], N in 0..1, L = [a]")]
    [InlineData("@L + 1 #> 3, L = [_,_,_|T], var(T)")]
    [InlineData("2 * @L #= @M - 1, M = [a,b,c,d,e], L = [_,_]")]
    [InlineData("@L #= N, N = 2, L = [_,_]")]
    public void FromTheLengthToTheList(string goal) => Assert.True(Holds(goal), goal);

    // ----- same_length/2, in one line -----

    [Theory]
    [InlineData("@A #= @B, A = [x,y], B = [_|T], B = [_,_], T = [_]")]
    [InlineData("@A #= @B, B = [1,2,3], A = [_,_,_]")]
    [InlineData("@A #= @B, A = [], B == []")]
    [InlineData("@A #= @B, A = [_|_], B = [_|T], var(T)")]
    public void SameLength(string goal) => Assert.True(Holds(goal), goal);

    [Fact]
    public void SameLengthOfDifferentLengthsFails()
        => Assert.False(Holds("@A #= @B, A = [1,2], B = [1,2,3]"));

    // ----- two lists sharing a tail -----

    [Theory]
    [InlineData("@L #= N, @M #= K, L = M, N = 3, K == 3, M = [_,_,_]")]
    [InlineData("@[a|T] #= N, @[b,c|U] #= K, T = U, K = 5, N == 4")]
    [InlineData("@L #= N, @M #= K, L = [a|T], M = [b,c|T], K = 4, N == 3, T = [_,_]")]
    public void TailsAliased(string goal) => Assert.True(Holds(goal), goal);

    // ----- beside library(coroutining) -----
    // The tail carries both attributes; each library's hook runs on its own.

    private static bool HoldsWithCoroutining(string goal)
    {
        var e = Fd();
        e.UseCoroutining();
        return e.Query(goal + ".").Success;
    }

    [Theory]
    [InlineData("@[foo|T] #= N, dif(T, []), N = 2, T = [_]")]
    [InlineData("@L #= N, freeze(L, Woke = yes), N = 2, Woke == yes, L = [_,_]")]
    [InlineData("@L #= N, when(ground(L), Ground = yes), N = 1, var(Ground), L = [x], Ground == yes")]
    [InlineData("dif(T, []), @[foo|T] #= N, N #> 1, T = [_|_]")]
    public void BesideCoroutining(string goal) => Assert.True(HoldsWithCoroutining(goal), goal);

    [Theory]
    [InlineData("@[foo|T] #= N, dif(T, []), N = 1")]
    [InlineData("dif(T, []), @[foo|T] #= N, N #=< 1")]
    public void ClosingTheTailAgainstDifFails(string goal) => Assert.False(HoldsWithCoroutining(goal), goal);

    // ----- what is no list -----

    [Theory]
    [InlineData("@foo #= N", "type_error(list,foo)")]
    [InlineData("@[a|b] #= N", "type_error(list,[a|b])")]
    [InlineData("L = [a|L], @L #= N", "type_error(list,")]
    [InlineData("@L #= N, L = [a|L]", "type_error(list,")]
    [InlineData("X in 1..3, @X #= N", "type_error(list,")]
    [InlineData("@L #= N, L = [a|T], T = 7", "type_error(list,")]
    [InlineData("@L #= N, L = [a|T], X in 1..3, T = X", "type_error(list,")]
    [InlineData("@L #= N, L = [a|T], X in 1..3, X = T", "type_error(list,")]
    [InlineData("@L #= N, L #= 3", "type_error(integer,")]
    [InlineData("@L #= N, N = foo", "type_error(integer,foo)")]
    public void NoList(string goal, string error)
        => Assert.StartsWith(error, Caught(goal));

    // ----- the answer -----

    [Fact]
    public void TheAnswerSaysTheLink()
    {
        // copy_term/3 renames the variables: the shapes are checked, not the names.
        string open = Residue("@L #= N", "[L,N]");
        Assert.Contains("in0..sup", open);
        Assert.Matches(@"@_\w+#=_\w+", open);
        Assert.Equal(1, open.Count(c => c == '@'));
        // Elements that are not characters: a list of characters renders packed.
        string grown = Residue("@L #= N, L = [foo|_]", "[L,N]");
        Assert.Contains("in1..sup", grown);
        Assert.Contains("@[foo|", grown);
        // The tail says nothing of its own; the link is reached through it,
        // since copy_term/3 follows the length inside the tail's attribute.
        string tail = Residue("@[foo|T] #= N", "T");
        Assert.Contains("@[foo|", tail);
        Assert.Equal(1, tail.Count(c => c == '@'));
        // Two lengths of one list: one link, said once, and an offset.
        string two = Residue("@[foo|T] #= N, @T #= M", "[T,N,M]");
        Assert.Contains("+1#=", two);
        Assert.Contains("@[foo|", two);
        Assert.Equal(1, two.Count(c => c == '@'));
    }

    // ----- labeling, and what backtracking restores -----

    [Fact]
    public void LabelingTheLengthClosesTheList()
    {
        Assert.True(Holds("findall(L, (@L #= N, N in 1..2, label([N])), Ls), Ls = [[_],[_,_]]"));
        Assert.True(Holds("findall(N, (@L #= N, L = [a|_], N #< 3, label([N])), Ns), Ns == [1,2]"));
    }

    // @L names the length variable, so it is something to label.
    [Theory]
    [InlineData("findall(L, (@L #< 3, label([@L])), Ls), Ls = [[],[_],[_,_]]")]
    [InlineData("findall(L, (@L #< 3, labeling([down], [@L])), Ls), Ls = [[_,_],[_],[]]")]
    [InlineData("findall(T, (@[a|T] #=< 2, label([@[a|T]])), Ts), Ts = [[],[_]]")]
    [InlineData("@L #= N, N in 1..2, label([@L]), N == 1, L = [_]")]
    [InlineData("label([@[a,b]])")]
    [InlineData("findall(A-B, (@A #< 2, @B #< 2, @A #\\= @B, label([@A, @B])), Ps), Ps = [[]-[_], [_]-[]]")]
    public void TheLengthCanBeLabeled(string goal) => Assert.True(Holds(goal), goal);

    [Theory]
    [InlineData("@L #= N, ( L = [a|_], fd_inf(N, 1), fail ; fd_inf(N, 0) )")]
    [InlineData("@L #= N, ( L = [a|_], fail ; L = [], N == 0 )")]
    [InlineData("@L #= N, ( N = 2, L = [_,_], fail ; var(L), fd_inf(N, 0) )")]
    [InlineData("@L #= N, L = [a|T], ( T = [b|_], fd_inf(N, 2), fail ; T = [], N == 1 )")]
    public void BacktrackingRestoresTheLink(string goal) => Assert.True(Holds(goal), goal);

    // ----- against length/2 -----

    /// <summary>For a known prefix of K cells and a domain Lo..Hi, labeling
    /// the length reaches every length length/2 would, in order, and each
    /// answer is a proper list of that length.</summary>
    [Theory]
    [InlineData(0, 0, 4)]
    [InlineData(2, 0, 4)]
    [InlineData(3, 1, 2)]
    [InlineData(1, 3, 3)]
    [InlineData(0, 2, 2)]
    public void AgreesWithLength(int known, int lo, int hi)
    {
        string bind = known == 0
            ? "true"
            : $"L = [{string.Join(",", Enumerable.Repeat("_", known))}|_]";
        var s = Fd().Query(
            $"findall(N, (@L #= N, N in {lo}..{hi}, {bind}, label([N]), length(L, N)), Ns), "
            + $"findall(N, (between({lo}, {hi}, N), N >= {known}), Ns).");
        Assert.True(s.Success, $"known {known}, {lo}..{hi}");
    }
}
