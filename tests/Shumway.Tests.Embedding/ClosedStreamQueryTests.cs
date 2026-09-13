using System;
using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A closed stream is still a stream-term, so the three predicates
/// that merely ASK about a stream -- current_input/1, current_output/1,
/// stream_property/2 -- must FAIL for one, not raise. Their error tables
/// (8.11.1.3, 8.11.2.3, 8.11.8.3) list no existence_error at all, and a
/// domain error is only right for a term that can never be a stream-term.
/// Predicates that USE a stream (close/1, set_input/1, write/2) keep raising
/// existence_error; that split is the whole point and is asserted here in
/// both directions.
///
/// <para>What makes a stream-term is its FORM, not the registry: a closed
/// stream is gone from the registry and is still one. So an id that never
/// named a stream is a stream-term too, and behaves exactly like a closed one
/// -- queries fail, uses raise existence_error. Deciding it by asking the
/// registry instead would make the domain vary as the program runs, and would
/// split two terms of identical form; it would also disagree with how an alias
/// atom is already treated, where form gives the domain and the registry gives
/// existence. Verified against GNU Prolog, which answers all of the
/// below identically.</para></summary>
public sealed class ClosedStreamQueryTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(),
            "shumway_closedq_" + Guid.NewGuid().ToString("N") + ".txt");

    private static string Esc(string p) => p.Replace("\\", "\\\\");

    /// <summary>Opens a scratch file, closes it, and runs <paramref name="goal"/>
    /// with S bound to the now-closed stream-term.</summary>
    private static bool WithClosedStream(string goal)
    {
        string f = TempPath();
        try
        {
            var e = new PrologEngine();
            return e.Query(
                $"open('{Esc(f)}', write, S), close(S), {goal}").Success;
        }
        finally { File.Delete(f); }
    }

    [Theory]
    [InlineData("current_input(S)")]
    [InlineData("current_output(S)")]
    [InlineData("stream_property(S, _)")]
    [InlineData("stream_property(S, mode(_))")]
    public void AQueryAboutAClosedStream_FailsAndDoesNotRaise(string goal)
    {
        // Not `\+ goal`: an error would escape that too. Catching first is
        // what tells a clean failure apart from a raise.
        Assert.False(WithClosedStream($"catch(({goal} -> true ; fail), E, true), "
                                      + "(var(E) -> fail ; true)."));
        Assert.True(WithClosedStream($@"\+ {goal}."));
    }

    /// <summary>Anti-vacuity: the same four goals SUCCEED on a live stream, so
    /// the failures above are the closed-ness and not a broken predicate.</summary>
    [Fact]
    public void TheSameQueries_SucceedOnALiveStream()
    {
        string f = TempPath();
        try
        {
            var e = new PrologEngine();
            Assert.True(e.Query("current_input(S), current_input(S).").Success);
            Assert.True(e.Query("current_output(S), current_output(S).").Success);
            Assert.True(e.Query(
                $"open('{Esc(f)}', write, S), stream_property(S, _), "
                + "stream_property(S, mode(write)), close(S).").Success);
        }
        finally { File.Delete(f); }
    }

    /// <summary>The other half of the split: USING a closed stream still
    /// raises existence_error(stream, S). If this ever turns into failure the
    /// fix above has gone too far.</summary>
    [Theory]
    [InlineData("close(S)")]
    [InlineData("set_input(S)")]
    [InlineData("write(S, x)")]
    public void UsingAClosedStream_StillRaisesExistenceError(string goal)
    {
        Assert.True(WithClosedStream(
            $"catch({goal}, error(E, _), true), E = existence_error(stream, _)."));
    }

    /// <summary>An id that never named a stream has the FORM of a stream-term,
    /// so it is not a domain error: the query just fails, as it does for a
    /// closed stream. A typo is still caught, but by the predicates that USE
    /// the stream (below), which is where it can do harm.</summary>
    [Theory]
    [InlineData("current_input('$stream'(999999))")]
    [InlineData("current_output('$stream'(999999))")]
    [InlineData("stream_property('$stream'(999999), _)")]
    public void AnIdThatNeverNamedAStream_QueriesLikeAClosedOne(string goal)
    {
        var e = new PrologEngine();
        Assert.False(e.Query($"catch(({goal} -> true ; fail), E, true), "
                             + "(var(E) -> fail ; true).").Success);
        Assert.True(e.Query($@"\+ {goal}.").Success);
    }

    /// <summary>...and USING it raises existence_error, the same as a closed
    /// stream: the term IS a stream-term, there is just no such stream.</summary>
    [Theory]
    [InlineData("close('$stream'(999999))")]
    [InlineData("write('$stream'(999999), x)")]
    public void UsingAnIdThatNeverNamedAStream_RaisesExistenceError(string goal)
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            $"catch({goal}, error(E, _), true), E = existence_error(stream, _).").Success);
    }

    /// <summary>The domain check that DOES survive, and it is about form only:
    /// '$stream'(foo) has no id, so it is no stream-term at all.</summary>
    [Theory]
    [InlineData("current_input('$stream'(foo))")]
    [InlineData("stream_property('$stream'(foo), _)")]
    // current_input/1's domain is `stream`, not `stream_or_alias`: an alias
    // atom is not admissible either, and the error names which domain.
    [InlineData("current_input(user_input)")]
    [InlineData("current_input(4.5)")]
    public void ATermThatIsNoStreamTerm_IsADomainErrorOnStream(string goal)
    {
        var e = new PrologEngine();
        var sol = e.Query($"catch({goal}, error(E, _), true), E = domain_error(D, _).");
        Assert.True(sol.Success);
        Assert.Equal("stream", ((AtomTerm)sol["D"]!).Name);
    }
}
