using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A goal that reads a piped standard input is told where the input
/// ends: <c>at_end_of_stream/0</c> waits for the next line, as it does for a
/// file, and holds once there is none. A server that reads its requests from
/// the standard input stops on it. At a console the predicate does not wait,
/// which is why the tests here pipe.</summary>
public sealed class ReplStdinEndTests
{
    private const string Ask = "( at_end_of_stream -> write(at_end) ; write(more) ), nl";

    [Fact]
    public void AfterTheLastLine_TheInputIsAtItsEnd()
    {
        // What is left of the query's own line is its line end.
        string output = ReplStdinEncodingTests.Piped(
            $"{Ask}, get_char(C), {Ask}.\n");

        Assert.Contains("more\nat_end\n", output.Replace("\r", ""));
    }

    [Fact]
    public void WithALineStillToCome_TheInputIsNotAtItsEnd()
    {
        string output = ReplStdinEncodingTests.Piped(
            $"get_char(_), {Ask}, get_char(C), write(C), nl.\nx\n");

        Assert.Contains("more\nx\n", output.Replace("\r", ""));
    }
}
