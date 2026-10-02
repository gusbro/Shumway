using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>An integer a_eval sequence checks each operand once, against the
/// width ArithBounds allows, instead of checking every operation; a comparison
/// is exact past 60 bits. An int past 60 bits that meets a float converts to
/// the nearest double, as the interpreter converts the same bignum.</summary>
public sealed class ArithOperandWidthTests
{
    private const string Corpus = """
        k(X, R) :- R is X * 1000 + 7.
        g(X, Y) :- X + Y > 0.
        m(X, Y, R) :- R is X * Y.
        s(A, B, C) :- 1000*A + 100*B + 10*C =:= 10000*A - 8990.
        f(X, Y, R) :- R is X * 1000 + Y.
        c(X, Y) :- X * 1000 < Y.
        """;

    private static string Try(string goal)
        => $"catch(({goal} -> R = yes ; R = no), error(R1, _), R = R1)";

    private static readonly string[] Goals =
    {
        Try("k(5, R0)") + ", X = R0.",
        "B is 2^50, " + Try("k(B, R0)") + ", X = R0.",
        "B is 2^58, " + Try("k(B, R0)") + ", X = R0.",
        "B is -(2^53), " + Try("k(B, R0)") + ", X = R0.",
        "M is 2^59 - 1, " + Try("g(M, M)") + ".",
        "N is -(2^59), " + Try("g(N, N)") + ".",
        "B is 2^31, " + Try("m(B, B, R0)") + ", X = R0.",
        "B is -(2^31), " + Try("m(B, B, R0)") + ", X = R0.",
        "B is 2^31 - 1, C is -B, " + Try("m(B, C, R0)") + ", X = R0.",
        Try("s(1, 0, 1)") + ".", Try("s(1, 1, 0)") + ".",
        "A is 2^50, " + Try("s(A, 1, 2)") + ".",
        // Unchecked, 2^58 * 1000 wraps 64 bits to a negative number, and a
        // comparison has no result check to catch it.
        "B is 2^58, " + Try("c(B, 0)") + ".",
        // The int product passes 60 bits and then meets a float. (2^52 - 11) *
        // 1000 is 264 past a multiple of 512, the float spacing there: the
        // nearest float is the multiple above, truncation the one below.
        Try("f(3, 0.5, R0)") + ", X = R0.",
        "B is 2^52 + 1, " + Try("f(B, 0.5, R0)") + ", X = R0.",
        "B is 2^52 - 11, " + Try("f(B, 0.5, R0)") + ", X = R0.",
        "B is 2^52 - 11, Y is float(4503599627370485248), " + Try("c(B, Y)") + ".",
    };

    private static string Answer(PrologEngine e, string goal)
    {
        var r = e.Query(goal);
        if (!r.Success) return "false";
        return string.Join(", ", r.Bindings.Where(kv => kv.Key is "R" or "X")
            .OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} = {kv.Value}"));
    }

    private static string Name(int fid)
    {
        var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
        string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
        return $"{name[(name.LastIndexOf('$') + 1)..]}/{arity}";
    }

    [Fact]
    public void TheTierAnswersAsTheInterpreterDoes()
    {
        var plain = new PrologEngine();
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Corpus);

        var (tiered, members) = TieredEngine.Build(Corpus);
        foreach (string g in Goals)
            Assert.Equal(Answer(plain, g), Answer(tiered, g));
        // ANTI-VACUITY: a predicate joins the world on its first call, and one
        // the module compiler declines stays on Tier-0.
        var inModule = members.Select(m => Name(m.Predicate.FunctorId)).ToHashSet();
        foreach (string pi in new[] { "k/2", "g/2", "m/3", "s/3", "f/3", "c/2" })
            Assert.True(inModule.Contains(pi), $"{pi} is not in the module");
        foreach (string check in new[] {
            "B is 2^50, k(B, X), X =:= 2^50 * 1000 + 7.",
            "B is 2^58, k(B, X), X =:= 2^58 * 1000 + 7.",
            "M is 2^59 - 1, g(M, M).", "N is -(2^59), \\+ g(N, N).",
            "B is 2^31, m(B, B, X), X =:= 2^62.", "B is -(2^31), m(B, B, X), X =:= 2^62.",
            "s(1, 0, 1).", "\\+ s(1, 1, 0).", "f(3, 0.5, X), X =:= 3000.5.",
            "B is 2^58, \\+ c(B, 0).",
            "B is 2^52 - 11, f(B, 0.5, X), X =:= 4503599627370485248.0.",
            "B is 2^52 - 11, Y is float(4503599627370485248), \\+ c(B, Y)." })
            Assert.True(tiered.Query(check).Success, check);
    }
}
