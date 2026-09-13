using System.IO;
using System.Text.RegularExpressions;
using Shumway.Embedding;
using Shumway.TopLevel;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The top level chains variables bound to the same value -- `X = Y,
/// Y = f(a)` rather than saying f(a) twice. What makes two values "the same"
/// has to be the terms, not the text they print as. Two unrelated values can
/// print alike, and chaining those states an equality that does not hold,
/// which is a wrong ANSWER and not a formatting blemish.
///
/// <para>Two ways they can print alike, both covered here: a query variable
/// spelled like an engine one (`_G11` against the engine's name for heap cell
/// 11, issue #120), and elision cutting two long values at the same place.
/// </para></summary>
public sealed class AnswerVariableIdentityTests
{
    private static PrologEngine Engine() => new() { Out = new StringWriter() };

    private static string Answer(PrologEngine engine, string query)
    {
        using var run = new TopLevelSession(engine).StartQuery(query);
        Assert.True(run.MoveNext(), $"no solution for {query}");
        return run.Format(200);
    }

    /// <summary>The engine name that <paramref name="shape"/> makes visible,
    /// where {0} stands for a plain variable. Derived rather than assumed, and
    /// derived from the SAME query shape on a FRESH engine: the heap address a
    /// variable lands on depends on everything asked before it, so a name
    /// lifted from a different query (or from the same engine a second time)
    /// would not collide and the test would pass without reproducing
    /// anything.</summary>
    private static string EngineNameIn(string shape)
    {
        string answer = Answer(Engine(), string.Format(shape, "Placeholder"));
        var m = Regex.Match(answer, @"_G\d+");
        Assert.True(m.Success, $"no engine variable name in `{answer}`");
        return m.Value;
    }

    [Fact]
    public void AQueryVariableSpelledLikeAnEngineOne_IsNotTheEngineOne()
    {
        // The reported query, with the collision made certain.
        const string shape = "length(L, 1), [{0}] = _Any.";
        string engineName = EngineNameIn(shape);
        string answer = Answer(Engine(), string.Format(shape, engineName));

        // ANTI-VACUITY: the collision has to be real, or nothing is proven.
        Assert.Contains(engineName, answer);
        // The bug: L and _Any were chained, so the answer read `L = _Any`.
        Assert.DoesNotContain("_Any", answer);
        Assert.StartsWith("L = [", answer);
    }

    /// <summary>And the user's spelling stays the user's: it is the ENGINE's
    /// variable that gives way, so both can be told apart on the page.</summary>
    [Fact]
    public void TheUsersSpellingSurvives_TheEngineVariableMoves()
    {
        const string shape = "length(L, 1), X = {0}.";
        string engineName = EngineNameIn(shape);
        string answer = Answer(Engine(), string.Format(shape, engineName));

        // Not Contains: `_G11` is a prefix of the moved `_G11_`, so the check
        // has to end at the name.
        Assert.Matches($"X = {Regex.Escape(engineName)}(?![0-9_])", answer);
        // L's element is a different variable and must not claim that name.
        Assert.DoesNotContain($"[{engineName}]", answer);
    }

    /// <summary>Elision is the other way two values come out looking alike.
    /// These lists differ only past the display cut.</summary>
    [Fact]
    public void TwoValuesCutToTheSameTextAreStillTwoValues()
    {
        var e = Engine();
        e.ElideAnswersForDisplay = true;
        e.Flags.AnswerMaxDepth = 3;
        string answer = Answer(e, "X = [1,2,3,4,aaa], Y = [1,2,3,4,bbb].");

        // ANTI-VACUITY: the cut has to have actually happened, or the two
        // texts were never alike and this proves nothing.
        Assert.Contains("| ...", answer);
        Assert.DoesNotContain("X = Y", answer);
        Assert.Contains("X = [", answer);
        Assert.Contains("Y = [", answer);
    }

    /// <summary>ANTI-VACUITY for the whole change: the chaining it guards is
    /// the point of the feature and still happens.</summary>
    [Theory]
    [InlineData("X = Y.", "X = Y")]
    [InlineData("X = f(a), Y = f(a).", "X = Y")]
    [InlineData("X = Y, Y = f(a).", "X = Y")]
    [InlineData("X = f(A), Y = f(A).", "X = Y")]
    [InlineData("length(L, 2), M = L.", "L = M")]
    public void ValuesThatReallyAreTheSame_AreStillChained(string q, string chain)
    {
        Assert.Contains(chain, Answer(Engine(), q));
    }

    /// <summary>The discrimination the fix turns on: same shape, different
    /// variables inside, so NOT the same value.</summary>
    [Fact]
    public void SameShapeOverDifferentVariables_IsNotChained()
    {
        string answer = Answer(Engine(), "X = f(A), Y = f(B).");
        Assert.DoesNotContain("X = Y", answer);
        Assert.Contains("X = f(", answer);
        Assert.Contains("Y = f(", answer);
    }
}
