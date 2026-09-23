using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary><c>min/2</c> and <c>max/2</c> over two integers, compiled.
///
/// <para>Refusing one arithmetic operation refuses the whole PREDICATE, and
/// that is what these cost: clp(Z)'s cis_min_/3 and cis_max_/3 were each
/// thrown out over a single instruction, and between them they were 171 of
/// the 199 calls a compiled chain could not continue into -- one chain
/// closure and one fresh staging each.</para>
///
/// <para>On two integers the result IS one of the operands, so there is
/// nothing to range-check. A float operand is a different question -- the
/// standard's min/2 returns the operand, not a promoted copy -- and that
/// stays the engine's.</para></summary>
public sealed class IntMinMaxTests(ITestOutputHelper o)
{
    private const string Corpus = """
        lo(A, B, R) :- R is min(A, B).
        hi(A, B, R) :- R is max(A, B).
        % Fused into a_int_bin when the operands are plain: the shape the
        % refusal was about.
        both(A, B, L, H) :- L is min(A, B), H is max(A, B).
        % Negatives, equals, and the sixty-bit edge, where a wrong
        % comparison shows up and a small one does not.
        edge(R) :- R is max(576460752303423487, 576460752303423486).
        edge2(R) :- R is min(-576460752303423488, -576460752303423487).
        % A float operand is the engine's: min/2 answers with the OPERAND,
        % so 1 and 1.0 are not interchangeable answers.
        % Taken as ARGUMENTS, or the compiler folds the whole expression
        % and nothing reaches the emitted code at all.
        mixed(A, B, R) :- R is min(A, B).
        mixed2(A, B, R) :- R is max(A, B).
        % And a bignum, which no sixty-bit lane can hold.
        big(R) :- B is 2 ^ 200, R is max(B, 1), R =:= B.
        """;

    [Theory]
    [InlineData("lo(3, 7, R), R == 3.")]
    [InlineData("lo(7, 3, R), R == 3.")]
    [InlineData("lo(4, 4, R), R == 4.")]
    [InlineData("hi(3, 7, R), R == 7.")]
    [InlineData("hi(7, 3, R), R == 7.")]
    [InlineData("lo(-9, -2, R), R == -9.")]
    [InlineData("hi(-9, -2, R), R == -2.")]
    [InlineData("both(5, 2, L, H), L == 2, H == 5.")]
    [InlineData("edge(R), R == 576460752303423487.")]
    [InlineData("edge2(R), R == -576460752303423488.")]
    [InlineData("mixed(1, 1.0, R), R == 1.")]
    [InlineData("mixed2(2, 1.5, R), R == 2.")]
    [InlineData("big(R).")]
    public void TheTierAnswersWhatTheInterpreterAnswers(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query(goal).Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success, $"the tier disagrees on {goal}");
    }

    /// <summary>The predicate is COMPILED now, which is the whole point: a
    /// refusal is per predicate, not per instruction.</summary>
    [Fact]
    public void ThePredicateIsNoLongerRefused()
    {
        var (tiered, members) = TieredEngine.Build(Corpus);
        // Promotion happens on the first CALL, not at build time, so each
        // one has to be reached before it can be counted as compiled.
        Assert.True(tiered.Query("both(5, 2, L, H), L == 2.").Success);
        Assert.True(tiered.Query("lo(3, 7, R), R == 3.").Success);
        Assert.True(tiered.Query("hi(3, 7, R), R == 7.").Success);
        var names = new System.Collections.Generic.List<string>();
        foreach (var m in members)
        {
            var (aid, ar) = Shumway.Core.FunctorTable.Lookup(m.Predicate.FunctorId);
            names.Add($"{Shumway.Core.AtomTable.GetById(aid)?.Name}/{ar}");
        }
        o.WriteLine(string.Join(" ", names));
        Assert.Contains("user$both/4", names);
        Assert.Contains("user$lo/3", names);
        Assert.Contains("user$hi/3", names);
    }

    /// <summary>And it runs inside the module: an integer min/max leaves no
    /// deopt behind. The counterproof is the float case, which must.
    /// </summary>
    [DiagFact]
    public void IntegersStayInTheModuleAndFloatsDoNot()
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query("both(5, 2, L, H), L == 2.").Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query("both(5, 2, L, H), L == 2.").Success);
        long ints = WasmTierDelegate.DiagDeopts;

        Assert.True(tiered.Query("mixed(1, 1.0, R), R == 1.").Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query("mixed(1, 1.0, R), R == 1.").Success);
        long floats = WasmTierDelegate.DiagDeopts;

        o.WriteLine($"integer deopts={ints} float deopts={floats}");
        Assert.Equal(0L, ints);
        Assert.True(floats > 0, "a float operand must stay the engine's");
    }
}
