using System.IO;
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


    [Fact]
    public void AQueryVariableSpelledLikeAnEngineOne_IsNotTheEngineOne()
    {
        // The user names a variable `_G11`, spelled exactly like the engine's
        // own name for a heap cell. The bug (#120) chained L to it and read
        // `L = _Any`; the engine variable now takes a fresh alphabetical name
        // instead, so the two never print alike and cannot be confused.
        string answer = Answer(Engine(), "length(L, 1), [_G11] = _Any.");

        Assert.DoesNotContain("_Any", answer);
        Assert.StartsWith("L = [", answer);
        // The engine variable is alphabetized, not the raw heap spelling and
        // not the mangled `_G11_` the old fix produced.
        Assert.DoesNotContain("_G11", answer);
        Assert.Matches(@"L = \[_[A-Z]\d*\]", answer);
    }

    /// <summary>And the user's spelling stays the user's: a genuinely separate
    /// engine variable is renamed, while the variable the user named `_G11`
    /// keeps that name, so both can be told apart on the page.</summary>
    [Fact]
    public void TheUsersSpellingSurvives_TheEngineVariableMoves()
    {
        string answer = Answer(Engine(), "length(L, 1), X = _G11.");

        // The user's spelling is preserved verbatim (ended at the name so a
        // trailing digit or underscore would fail).
        Assert.Matches(@"X = _G11(?![0-9_])", answer);
        // L's element is a different, anonymous variable: alphabetized, and
        // NOT claiming the user's `_G11`.
        Assert.DoesNotContain("[_G11]", answer);
        Assert.Matches(@"L = \[_[A-Z]\d*\]", answer);
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
