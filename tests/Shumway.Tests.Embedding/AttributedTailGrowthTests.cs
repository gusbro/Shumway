using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Builtins that grow a partial list meet an attributed tail. Its
/// hooks run once the builtin has returned, above any choice point the
/// builtin leaves, so a step that binds the tail before its choice point
/// keeps the binding through the retry and loses the hooks' work: a
/// constraint on the list's length falls out of step with the list. The
/// step continues from the wake batch instead, after the hooks
/// (docs/design/builtin-continues-after-wakeups.md), so the answers follow
/// the constraint and a rejected extension ends the enumeration; and an
/// integer index must grow an attributed tail as it grows a plain one.</summary>
public sealed class AttributedTailGrowthTests
{
    private static PrologEngine Engine()
    {
        var e = new PrologEngine { Out = new StringWriter(), Warnings = new StringWriter() };
        e.UseClpfd();
        e.UseCoroutining();
        return e;
    }

    /// <summary>Runs the query on its own thread: an enumeration over a
    /// partial list has no end, so a wrong answer could be a hang.</summary>
    private static bool Holds(string query)
    {
        bool? result = null;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = Engine().Query(query + ".").Success; }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(30)), $"did not terminate in time: {query}");
        if (error is not null) throw new Xunit.Sdk.XunitException($"{query} raised {error}");
        return result!.Value;
    }

    // ----- nth0/3, nth1/3: a free index enumerates past the attributed tail -----

    [Theory]
    // The reported case: the length of A follows A as nth0/3 grows it, so
    // length(B, 4) closes A at six cells and the six positions are found.
    [InlineData("@A #= @B + 2, nth0(X, A, a), length(B, 4), X == 5, !")]
    [InlineData("@A #= @B + 2, nth0(X, A, a), X >= 6, !, \\+ length(B, 4)")]
    [InlineData("@A #= @B + 2, nth1(X, A, a), length(B, 4), X == 6, !")]
    // The list itself is the attributed variable.
    [InlineData("@L #= N, nth0(X, L, a), X == 0, !, L = [a|T], var(T), fd_inf(N, 1)")]
    // The length keeps up with every retry, not only the first binding.
    [InlineData("@L #= N, nth0(X, L, a), X == 2, !, fd_inf(N, 3)")]
    [InlineData("@L #= N, nth1(X, L, a), X == 3, !, fd_inf(N, 3)")]
    // Once the length is fixed, the list is closed.
    [InlineData("@L #= N, N #=< 3, nth0(X, L, a), X == 2, !, L = [_,_,a|T], T == [], N == 3")]
    // A known prefix, then the attributed tail.
    [InlineData("@[foo|T] #= N, nth0(X, [foo|T], a), X == 2, !, T = [_,a|T2], var(T2), fd_inf(N, 3)")]
    // Backtracking out of the enumeration restores the length.
    [InlineData("@L #= N, \\+ \\+ (nth0(X, L, a), X == 2), var(L), fd_inf(N, 0)")]
    public void AFreeIndexGrowsAnAttributedTail(string query)
        => Assert.True(Holds(query), query);

    [Theory]
    // A goal frozen on the tail runs on each cell grown, before the element
    // is placed in it, as it does with a Prolog definition: the cell's head
    // is still a variable when the goal sees it.
    [InlineData("freeze(T, Woke = yes), L = [foo|T], nth0(X, L, a), X == 1, !, Woke == yes")]
    [InlineData("freeze(T, (T = [H|_], H \\== a)), L = [foo|T], nth0(X, L, a), !, X == 1")]
    [InlineData("freeze(T, (T = [H|_], H \\== a)), L = [foo|T], nth1(X, L, a), !, X == 2")]
    public void TheTailsCoroutinesRunOnEachShape(string query)
        => Assert.True(Holds(query), query);

    // ----- nth0/3, nth1/3: an integer index grows an attributed tail -----

    [Theory]
    [InlineData("@L #= N, L = [a|T], nth0(2, L, x), T = [_,x|T2], var(T2), fd_inf(N, 3)")]
    [InlineData("@L #= N, nth1(2, L, x), L = [_,x|T], var(T), fd_inf(N, 2)")]
    [InlineData("@L #= N, N #= 2, nth0(1, L, x), L = [_,x|T], T == []")]
    [InlineData("freeze(T, Woke = yes), L = [foo|T], nth0(3, L, x), Woke == yes")]
    public void AnIntegerIndexGrowsAnAttributedTail(string query)
        => Assert.True(Holds(query), query);

    [Theory]
    [InlineData("@L #= N, N #=< 2, nth0(2, L, x)")]
    [InlineData("freeze(T, T \\= [x|_]), L = [foo|T], nth0(1, L, x)")]
    public void AnIntegerIndexPastTheLengthFails(string query)
        => Assert.False(Holds(query), query);

    // ----- append/3: the split enumerates past the attributed tail -----

    [Theory]
    [InlineData("@L #= N, append(L, T, [a,b,c]), T == [], !, N == 3")]
    [InlineData("@L #= N, append(L, T, [a,b,c]), T == [b,c], !, N == 1")]
    [InlineData("@L #= N, findall(N-L, append(L, _, [a,b,c]), Ps), Ps == [0-[], 1-[a], 2-[a,b], 3-[a,b,c]]")]
    [InlineData("@[foo|T] #= N, append([foo|T], R, [foo,b,c]), R == [], !, N == 3")]
    [InlineData("@A #= @B, append(A, R, [a,b,c]), R == [c], !, B = [_,_|T], T == []")]
    [InlineData("freeze(T, Woke = yes), L = [a|T], append(L, R, [a,b,c]), R == [c], !, Woke == yes")]
    // dif/2 rejects the split that closes the tail at once.
    [InlineData("dif(T, []), L = [a|T], findall(R, append(L, R, [a,b,c]), Rs), Rs == [[c], []]")]
    public void TheSplitGrowsAnAttributedTail(string query)
        => Assert.True(Holds(query), query);

    // The walk reaches the attributed tail after two plain cells.
    [Fact]
    public void TheSplitUnderABoundedLengthFindsTheAnswersThatFit()
        => Assert.True(Holds("@L #= N, N #>= 2, findall(L-R, append(L, R, [a,b,c]), Ps), Ps == [[a,b]-[c], [a,b,c]-[]]"));

    // ----- the enumeration ends where the constraint ends it -----
    // A hook that rejects the next cell fails the step with no frame left,
    // as the second clause of a Prolog definition would: findall/3 returns.

    [Theory]
    [InlineData("@A #=< 6, findall(X, nth0(X, A, a), Xs), Xs == [0,1,2,3,4,5]")]
    [InlineData("@A #=< 6, findall(X, nth1(X, A, a), Xs), Xs == [1,2,3,4,5,6]")]
    [InlineData("@B #= 4, @A #= @B + 2, findall(X, nth0(X, A, a), Xs), Xs == [0,1,2,3,4,5]")]
    [InlineData("@L #= N, N #< 3, findall(X, nth1(X, L, a), Xs), Xs == [1,2]")]
    [InlineData("@[foo|T] #= N, N #=< 3, findall(X, nth0(X, [foo|T], a), Xs), Xs == [1,2]")]
    [InlineData("@L #= 0, findall(X, nth0(X, L, a), Xs), Xs == []")]
    [InlineData("freeze(T, fail), L = [foo|T], findall(X, nth0(X, L, foo), Xs), Xs == [0]")]
    [InlineData("freeze(T, T = []), L = [foo|T], findall(X, nth0(X, L, a), Xs), Xs == []")]
    [InlineData("@L #=< 1, findall(L-R, append(L, R, [a,b,c]), Ps), Ps == [[]-[a,b,c], [a]-[b,c]]")]
    [InlineData("@L #= N, N #>= 1, N #=< 2, findall(L, append(L, _, [a,b,c]), Ls), Ls == [[a], [a,b]]")]
    [InlineData("freeze(T, T = []), L = [a|T], findall(R, append(L, R, [a,b,c]), Rs), Rs == [[b,c]]")]
    public void ABoundedLengthEndsTheEnumeration(string query)
        => Assert.True(Holds(query), query);

    // The yield comes before a cut: the cut commits to the first position.
    [Theory]
    [InlineData("@A #=< 6, nth0(X, A, a), !, X == 0, A = [a|T], var(T)")]
    [InlineData("@A #=< 6, \\+ (nth0(X, A, a), !, X > 0)")]
    [InlineData("@L #= N, append(L, R, [a,b,c]), !, L == [], R == [a,b,c], N == 0")]
    public void TheYieldComesBeforeACut(string query)
        => Assert.True(Holds(query), query);

    // ----- reverse/2 -----

    [Fact]
    public void ReverseOfAnAttributedPartialListIsAnInstantiationError()
        => Assert.True(Holds("@L #= N, catch(reverse(L, _), error(E, _), true), E == instantiation_error"));
}
