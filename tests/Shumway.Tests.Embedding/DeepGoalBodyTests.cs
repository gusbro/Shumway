using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>
/// A goal body handed to a meta-call is a term the program built, so its
/// depth is the program's: a frozen goal list, a conjunction a fold put
/// together. The call/N boundary walks its control skeleton (the §7.6.2
/// conversion, the §7.8.3 check, the module distribution), and none of
/// those walks may take a C# frame per level: a .NET stack overflow kills
/// the process. Each shape runs on a thread with a small stack, as the
/// browser has, so a walk that recurses per level overflows well inside
/// <see cref="Depth"/>; a regression takes the test run down.
/// </summary>
public sealed partial class DeepGoalBodyTests(ITestOutputHelper o)
{
    // Past the walks' recursion budget, and far past what a recursive walk
    // survives on SmallStack.
    private const int Depth = 2000;
    private const int SmallStack = 256 * 1024;

    internal const string Corpus = """
        :- use_module(library(coroutining)).
        :- public run/1.
        run(G) :- call(G).
        % G as the leftmost goal of a left-nested conjunction N levels deep:
        % ((((G, true), true) ...), true).
        pad(0, G, G) :- !.
        pad(N, G, (B, true)) :- N1 is N - 1, pad(N1, G, B).
        % N goals G, right-nested.
        rconj(1, G, G) :- !.
        rconj(N, G, (G, B)) :- N1 is N - 1, rconj(N1, G, B).
        % G as the then-branch of N nested if-then-elses.
        thens(0, G, G) :- !.
        thens(N, G, (true -> T)) :- N1 is N - 1, thens(N1, G, T).
        % N goals frozen on one variable.
        frozen_n(0, _) :- !.
        frozen_n(N, X) :- freeze(X, bump), N1 is N - 1, frozen_n(N1, X).
        bump :- nb_getval(bumps, C0), C is C0 + 1, nb_setval(bumps, C).
        """;

    /// <summary>(query, what it exercises). Every query must succeed.</summary>
    public static TheoryData<string, string> Shapes() => new()
    {
        { $"pad({Depth}, true, B), run(B).",
          "a left-nested conjunction" },
        { $"rconj({Depth}, true, B), run(B).",
          "a right-nested conjunction" },
        { $"pad({Depth}, (member(Y, [1,2,3]), X, Y >= 2), B), run((X = !, B)), Y == 2.",
          "a variable deep in the body converts to call(V): its ! cuts only itself" },
        { $"pad({Depth}, (member(Y, [1,2,3]), X, Y >= 2), B), X = !, \\+ run(B).",
          "a variable bound before the call is the goal it holds: its ! cuts the body" },
        { $"thens({Depth}, X = 1, T), run(user:T), X == 1.",
          "a module distributed into nested if-then-elses" },
        { $"nb_setval(bumps, 0), frozen_n({Depth}, X), X = 1, nb_getval(bumps, {Depth}).",
          "a variable with that many frozen goals is bound" },
    };

    internal static T OnSmallStack<T>(Func<T> body)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception ex) { error = ex; }
        }, SmallStack);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException($"raised: {error}");
        return result;
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Tier0(string query, string what)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 0;
        e.ConsultString(Corpus);
        o.WriteLine(what);
        Assert.True(OnSmallStack(() => e.Query(query).Success), query);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Il(string query, string what)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString(Corpus);
        o.WriteLine(what);
        // run/1 promotes on its first dispatch; the meta-call is then the IL
        // dispatcher's. Without the promotion this would be Tier-0 again.
        Assert.True(e.Query("run(true).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        int run = FunctorTable.Intern(AtomTable.Intern("run").Id, 1);
        Assert.True(e.IlPromotion.IsPromoted(run), "run/1 did not promote");
        Assert.True(OnSmallStack(() => e.Query(query).Success), query);
    }

    public sealed partial class Bridge
    {
        // solve_left(N): from C#, solve a left-nested conjunction of N goals
        // on the live activation (the in-engine meta-call).
        [PrologPredicate("solve_left/1")]
        public static bool SolveLeft(Activation engine, int n)
        {
            Term body = new AtomTerm("true");
            for (int i = 1; i < n; i++)
                body = new CompoundTerm(",", new[] { body, new AtomTerm("true") });
            return ((PrologEngine)engine.Host!).SolveOnce(engine, body);
        }
    }

    [Fact]
    public void SolveOnce_OfADeepConjunction()
    {
        var e = new PrologEngine();
        e.RegisterPredicates<Bridge>();
        Assert.True(OnSmallStack(() => e.Query($"solve_left({Depth}).").Success));
    }
}
