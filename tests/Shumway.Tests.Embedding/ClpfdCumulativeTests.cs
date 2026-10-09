using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>cumulative/1,2: at every moment the tasks running use at most the
/// limit. The labeled solutions equal a brute-force enumeration of the
/// relation; the pruning cases are SICStus's answers, and a task that lasts
/// 0 occupies no time, as in SICStus (clpz requires a positive duration).
/// The errors are Scryer's where SICStus only fails, and SICStus's for an
/// unknown option.</summary>
public sealed class ClpfdCumulativeTests
{
    private const string Program = """
        :- use_module(library(clpfd)).

        % The profile peaks where some task starts.
        fits(Tasks, L) :-
            \+ ( member(task(T, D0, _, _, _), Tasks), D0 > 0,
                 findall(C, ( member(task(S, D, _, C, _), Tasks), S =< T, T < S + D ), Cs),
                 sum_list(Cs, U), U > L ).

        mk([], [], []).
        mk([t(SL,SH,DL,DH,CL,CH)|Ts], [task(S,D,_,C,_)|Tasks], [S,D,C|Vs]) :-
            S in SL..SH, D in DL..DH, C in CL..CH, mk(Ts, Tasks, Vs).
        cu_fd(inst(L, Ts), Sols) :-
            findall(Vs, ( mk(Ts, Tasks, Vs), cumulative(Tasks, [limit(L)]), label(Vs) ), S0),
            msort(S0, Sols).

        gen([], [], []).
        gen([t(SL,SH,DL,DH,CL,CH)|Ts], [task(S,D,E,C,_)|Tasks], [S,D,C|Vs]) :-
            between(SL, SH, S), between(DL, DH, D), between(CL, CH, C), E is S + D,
            gen(Ts, Tasks, Vs).
        cu_brute(inst(L, Ts), Sols) :-
            findall(Vs, ( gen(Ts, Tasks, Vs), fits(Tasks, L) ), S0), msort(S0, Sols).

        % Twelve tasks of total work 61: at limit 4 no horizon under 16 holds them.
        twelve(H, Ts) :-
            maplist(twelve_task(H), [3,4,2,5,3,2,4,1,3,2,5,2], [2,1,3,2,1,2,1,3,2,2,1,3], Ts).
        twelve_task(H, D, C, task(S,D,E,C,_)) :- S in 0..H, E #=< H.

        tasks_starts(Tasks, Starts) :-
            Tasks = [task(S1,3,_,1,_), task(S2,2,_,1,_), task(S3,2,_,1,_)],
            Starts = [S1,S2,S3].
        """;

    private static PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.ConsultString(Program);
        return e;
    }

    private static void Holds(string goal)
        => Assert.True(Engine().Query(goal + ".").Success, goal);

    // Three tasks, small enough to enumerate: a start window of up to four
    // values, a duration and a use that are known or range over two values,
    // zero included.
    private static string Instance(int seed)
    {
        var r = new Random(seed);
        var tasks = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            int sl = r.Next(0, 4), sh = sl + r.Next(0, 4);
            int dl = r.Next(0, 3), dh = dl + r.Next(0, 2);
            int cl = r.Next(0, 3), ch = cl + r.Next(0, 2);
            tasks.Add($"t({sl},{sh},{dl},{dh},{cl},{ch})");
        }
        return $"inst({r.Next(1, 4)}, [{string.Join(", ", tasks)}])";
    }

    [Fact]
    public void LabeledSolutionsAreTheRelation()
    {
        var e = Engine();
        int withSolutions = 0;
        for (int seed = 1; seed <= 40; seed++)
        {
            string inst = Instance(seed);
            var r = e.Query($"cu_fd({inst}, F), cu_brute({inst}, B), F == B, length(B, N).");
            Assert.True(r.Success, inst);
            if ($"{r["N"]}" != "0") withSolutions++;
        }
        // ANTI-VACUITY: most instances have a schedule to compare.
        Assert.True(withSolutions >= 20, $"only {withSolutions} instances had solutions");
    }

    [Fact]
    public void TheDocumentedScheduleComesFirst()
        => Holds("tasks_starts(Tasks, Starts), Starts ins 0..10, cumulative(Tasks, [limit(2)]), "
            + "once(label(Starts)), Starts == [0,0,2]");

    [Theory]
    // A task that surely runs over [2,5) leaves no start that would overlap it.
    [InlineData("S1 in 0..2, S2 in 0..10, cumulative([task(S1,5,_,1,_), task(S2,2,_,1,_)]), fd_dom(S2, D)",
        "D", @"0 \/ 5..10")]
    [InlineData("S in 0..10, cumulative([task(2,3,_,1,_), task(S,2,_,1,_)]), fd_dom(S, D)",
        "D", @"0 \/ 5..10")]
    [InlineData("S1 in 0..2, S2 in 0..2, S3 in 0..10, "
        + "cumulative([task(S1,5,_,1,_), task(S2,5,_,1,_), task(S3,2,_,1,_)], [limit(2)]), fd_dom(S3, D)",
        "D", @"0 \/ 5..10")]
    [InlineData("S1 in 0..10, S2 in 0..10, cumulative([task(S1,3,_,1,_), task(S2,2,E2,1,_)]), S1 = 0, "
        + "fd_dom(S2, D)", "D", "3..10")]
    [InlineData("S1 in 0..10, S2 in 0..10, cumulative([task(S1,3,_,1,_), task(S2,2,E2,1,_)]), S1 = 0, "
        + "fd_dom(E2, D)", "D", "5..12")]
    [InlineData("S in 0..6, cumulative([task(0,4,_,1,_), task(1,2,_,1,_), task(S,2,_,1,_)], [limit(2)]), "
        + "fd_dom(S, D)", "D", "3..6")]
    // Both surely run at 1: the second may use what the first leaves.
    [InlineData("S1 in 0..1, S2 in 0..1, C in 0..3, "
        + "cumulative([task(S1,2,_,1,_), task(S2,2,_,C,_)], [limit(2)]), fd_dom(C, D)", "D", "0..1")]
    [InlineData("S in 0..3, C in 0..5, cumulative([task(S,1,_,C,_)], [limit(2)]), fd_dom(C, D)", "D", "0..2")]
    // No duration, no time occupied: the use does not count.
    [InlineData("S in 0..5, cumulative([task(S,0,E,5,_)], [limit(1)]), fd_dom(S, D)", "D", "0..5")]
    [InlineData("S in 0..5, cumulative([task(0,4,_,1,_), task(S,0,_,1,_)]), fd_dom(S, D)", "D", "0..5")]
    public void Propagates(string goal, string variable, string expected)
        => Holds($"{goal}, {variable} == ({expected})");

    [Theory]
    [InlineData("cumulative([])")]
    [InlineData("cumulative([task(0,2,_,1,_), task(2,2,_,1,_)])")]
    [InlineData("cumulative([task(0,0,_,1,_)], [limit(-1)])")]
    [InlineData("cumulative([task(0,2,_,1,_)], [limit(5), limit(1)])")]
    [InlineData("twelve(16, Ts), cumulative(Ts, [limit(4)]), once((maplist(arg(1), Ts, Ss), labeling([ff], Ss)))")]
    public void Succeeds(string goal) => Holds(goal);

    [Theory]
    [InlineData("cumulative([task(0,2,_,1,_), task(1,2,_,1,_)])")]
    [InlineData("S in 0..5, cumulative([task(S,2,_,3,_)], [limit(2)])")]
    [InlineData("cumulative([task(0,2,_,1,_)], [limit(1), limit(0)])")]
    [InlineData("S in 0..3, cumulative([task(S,1,_,1,_)], [limit(0)])")]
    [InlineData("cumulative([foo(1)])")]
    [InlineData("S in 0..3, cumulative([task(S,-1,_,1,_)])")]
    [InlineData("S in 0..3, cumulative([task(S,1,_,-1,_)])")]
    [InlineData("cumulative([task(0,1,_,1,_)], [limit(-1)])")]
    // No task surely runs anywhere, yet 6 units of work do not fit in 0..4.
    [InlineData("[S1,S2,S3] ins 0..2, cumulative([task(S1,2,_,1,_), task(S2,2,_,1,_), task(S3,2,_,1,_)])")]
    [InlineData("twelve(15, Ts), cumulative(Ts, [limit(4)])")]
    public void Fails(string goal)
        => Assert.False(Engine().Query(goal + ".").Success, goal);

    [Theory]
    [InlineData("cumulative(foo)", "type_error(list, foo)")]
    [InlineData("cumulative([task(a,1,_,1,_)])", "type_error(integer, a)")]
    [InlineData("cumulative([task(_,1,_,1,_)|_])", "instantiation_error")]
    [InlineData("cumulative([_])", "instantiation_error")]
    [InlineData("cumulative([task(_,1,_,1,_)])", "instantiation_error")]
    [InlineData("cumulative([task(0,1,_,1,_)], [limit(a)])", "type_error(integer, a)")]
    [InlineData("cumulative([task(0,1,_,1,_)], [limit(_)])", "instantiation_error")]
    [InlineData("cumulative([task(0,1,_,1,_)], [foo])", "domain_error(cumulative_option, foo)")]
    [InlineData("cumulative([task(0,1,_,1,_)], _)", "instantiation_error")]
    public void Raises(string goal, string error)
        => Holds($"catch(({goal}, fail), error({error}, _), true)");

    [Fact]
    public void APendingCumulativeReadsAsWritten()
        => Holds("S in 0..5, cumulative([task(S,2,E,1,T)]), copy_term([S,E], [S,E], Gs), "
            + "memberchk(cumulative([task(S,2,E,1,T)], [limit(1)]), Gs)");
}
