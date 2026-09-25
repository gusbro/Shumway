using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>==/2 and \==/2 decided inside the module.
///
/// <para>The old inline form needed Atom or Int on BOTH sides, so two
/// VARIABLES always left. That is where the exits were: queens 12 leaves for
/// ==/2 16,793 times per run and 16,763 of those fail, 42% of all its builtin
/// exits, because clpfd compares attributed variables and an attributed
/// variable is not atomic.</para>
///
/// <para>Correctness first, and the sharp edge is the PSTR: a zero-length one
/// IS its own tail, so it is identical to the atom [] despite the tags. The
/// cases below pin every way of reaching that pair from Prolog -- and NONE of
/// them reaches it with the PSTR still packed, because a literal "" parses
/// straight to [] and unification materialises a walked tail before the
/// comparison sees it. The emitter declines PSTR pairs anyway; that guard is
/// marked untested at its definition rather than pretended to be covered
/// here.</para></summary>
public sealed class InlineCompareTests(ITestOutputHelper o)
{
    private static long ExitsFor(string name)
    {
        foreach (var (n, a, hits) in WasmTierDelegate.BuiltinRanking())
            if (n == name && a == 2) return hits;
        return 0;
    }

    /// <summary>The trap: an empty PSTR normalises to its tail, so `"" == []`
    /// is TRUE even though one side is a string and the other an atom. The
    /// module must decline this pair rather than decide it by tags.</summary>
    [Fact]
    public void AnEmptyStringIsIdenticalToTheEmptyList()
    {
        const string Program = """
            same(A, B) :- A == B.
            diff(A, B) :- A \== B.
            """;
        var plain = new PrologEngine();
        plain.ConsultString(Program);
        Assert.True(plain.Query("X = \"\", Y = [], same(X, Y).").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Program);
        Assert.True(tiered.Query("X = \"\", Y = [], same(X, Y).").Success,
            "the tier decided an empty PSTR against an atom by tags");
        Assert.False(tiered.Query("X = \"\", Y = [], diff(X, Y).").Success);

        // A non-empty PSTR against a cons list: whatever the answer is, the
        // two engines have to give the SAME one. The oracle decides it, not
        // this test -- a hard-coded expectation here would just move the
        // goalposts for both. (It depends on the double_quotes flag: a string
        // is a list of CHARS, so a list of CODES is a different term.)
        // The empty PSTR that actually EXISTS as a cell: the tail left after
        // a packed string has been walked to its end. A literal "" is parsed
        // straight to the atom [], so it never reaches the comparison as a
        // PSTR and proves nothing about the guard.
        foreach (string q in new[]
        {
            $"X = \"ab\", X = [_, _|T], Y = [], same(T, Y).",
            $"X = \"ab\", X = [_, _|T], Y = [], diff(T, Y).",
            $"X = \"ab\", X = [_|T], Y = [b], same(T, Y).",
        })
        {
            bool oracle = plain.Query(q).Success;
            var t3 = TieredEngine.Build(Program).Engine;
            Assert.Equal(oracle, t3.Query(q).Success);
            o.WriteLine($"{q} -> {oracle}");
        }

        foreach (string other in new[] { "[a, b, c]", "[0'a, 0'b, 0'c]" })
        {
            string q = $"X = \"abc\", Y = {other}, same(X, Y).";
            bool oracle = plain.Query(q).Success;
            var t2 = TieredEngine.Build(Program).Engine;
            Assert.Equal(oracle, t2.Query(q).Success);
            o.WriteLine($"\"abc\" == {other} is {oracle}");
        }
    }

    /// <summary>Every shape, tiered against the interpreter. A disagreement
    /// anywhere here is a wrong answer out of compiled code.</summary>
    [Theory]
    // identical cells
    [InlineData("X = f(1), Y = X", true)]
    [InlineData("X = a, Y = a", true)]
    [InlineData("X = 1, Y = 1", true)]
    [InlineData("X = Y", true)]
    // simple against simple
    [InlineData("X = a, Y = b", false)]
    [InlineData("X = 1, Y = 2", false)]
    [InlineData("X = a, Y = 1", false)]
    [InlineData("Y = _, X = _", false)]
    // simple against a compound: the pair the old form could not decide
    [InlineData("X = _, Y = f(1)", false)]
    [InlineData("X = a, Y = f(1)", false)]
    [InlineData("X = 1, Y = [1]", false)]
    // an attributed variable is a variable: identity, and never equal to
    // another one
    [InlineData("put_attr(X, m, 1), Y = X", true)]
    [InlineData("put_attr(X, m, 1), put_attr(Y, m, 1)", false)]
    [InlineData("put_attr(X, m, 1), Y = a", false)]
    [InlineData("put_attr(X, m, 1), Y = f(X)", false)]
    // boxed values: equal values in different cells, so the host decides
    [InlineData("X = 1.5, Y = 1.5", true)]
    [InlineData("X = 1.5, Y = 2.5", false)]
    [InlineData("X = 1, Y = 1.0", false)]
    // compounds
    [InlineData("X = f(1), Y = f(1)", true)]
    [InlineData("X = f(1), Y = f(2)", false)]
    [InlineData("X = f(1), Y = g(1)", false)]
    [InlineData("X = [1, 2], Y = [1, 2]", true)]
    public void TheTierAgreesWithTheInterpreter(string setup, bool identical)
    {
        const string Program = """
            same(A, B) :- A == B.
            diff(A, B) :- A \== B.
            """;
        string eq = $"{setup}, same(X, Y).";
        string ne = $"{setup}, diff(X, Y).";

        var plain = new PrologEngine();
        plain.ConsultString(Program);
        // The expectation is checked against the INTERPRETER first: a wrong
        // InlineData would otherwise just move the goalposts for both.
        Assert.Equal(identical, plain.Query(eq).Success);
        Assert.Equal(!identical, plain.Query(ne).Success);

        var (tiered, _) = TieredEngine.Build(Program);
        Assert.Equal(identical, tiered.Query(eq).Success);
        var (tiered2, _) = TieredEngine.Build(Program);
        Assert.Equal(!identical, tiered2.Query(ne).Success);
    }

    /// <summary>Two attributed variables, compared until it is hot, never
    /// leave the module. This is the 16,763 queens was paying.</summary>
    [DiagFact]
    public void ComparingTwoAttributedVariablesNeverLeaves()
    {
        var (engine, _) = TieredEngine.Build("""
            spin(0, _, _).
            spin(N, X, Y) :- N > 0, atom_length(ab, _), X \== Y,
                             N1 is N - 1, spin(N1, X, Y).
            """);
        WasmTierDelegate.ResetDiag();

        Assert.True(engine.Query(
            "put_attr(X, m, 1), put_attr(Y, m, 2), spin(50, X, Y).").Success);

        o.WriteLine($"==/2 exits={ExitsFor("==")} \\==/2 exits={ExitsFor("\\==")} "
            + $"atom_length/2={ExitsFor("atom_length")}");
        // atom_length shares the clause, so a zero below cannot be a program
        // that never reached the tier.
        Assert.True(ExitsFor("atom_length") >= 50, "the clause never ran on the tier");
        Assert.Equal(0L, ExitsFor("\\=="));
    }

    /// <summary>Two compounds no longer leave -- the module carries a
    /// comparator now -- but a BIGNUM inside one still does. Two equal
    /// bignums can wear different cells, so cell identity is not term
    /// identity there and the walk must decline rather than answer. The
    /// count must be NON-zero, or the module is taking pairs it has no
    /// business taking.</summary>
    [DiagFact]
    public void ABignumInsideStillReachesTheHost()
    {
        var (engine, _) = TieredEngine.Build("""
            spin(0, _, _).
            spin(N, X, Y) :- N > 0, atom_length(ab, _), X == Y,
                             N1 is N - 1, spin(N1, X, Y).
            """);
        WasmTierDelegate.ResetDiag();

        Assert.True(engine.Query(
            // SEPARATELY computed: two Refs to one variable deref to one cell,
            // and the walk would never see two bignum cells at all.
            "B1 is 2 ^ 200, B2 is 2 ^ 200, X = f(B1), Y = f(B2), spin(20, X, Y).").Success);

        o.WriteLine($"==/2 exits={ExitsFor("==")}");
        Assert.True(ExitsFor("==") >= 20,
            "a bignum inside was decided by cell identity");
    }

    /// <summary>And the counterproof to THAT: without the bignum the same
    /// clause decides inside the module. Otherwise the test above would pass
    /// just as well with the comparator switched off.</summary>
    [DiagFact]
    public void TwoPlainCompoundsDoNotReachTheHost()
    {
        var (engine, _) = TieredEngine.Build("""
            spin(0, _, _).
            spin(N, X, Y) :- N > 0, atom_length(ab, _), X == Y,
                             N1 is N - 1, spin(N1, X, Y).
            """);
        Assert.True(engine.Query("X = f(1, 2), Y = f(1, 2), spin(20, X, Y).").Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query("X = f(1, 2), Y = f(1, 2), spin(20, X, Y).").Success);

        Assert.True(ExitsFor("atom_length") >= 20, "the clause never ran on the tier");
        Assert.Equal(0L, ExitsFor("=="));
    }
}
