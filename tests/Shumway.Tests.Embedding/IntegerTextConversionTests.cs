using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>An integer read from text is the integer the reader reads from the
/// same digits, at every size: one that fits an inline cell (60 bits) is inline,
/// a larger one lives on the heap (ADR-064). Between 2^59 and 2^63 the
/// conversions built an inline cell that cannot hold the value, and the host's
/// exception left the query (#173); past 2^63 name/2 and number_string/2 gave a
/// float.</summary>
public sealed class IntegerTextConversionTests
{
    private static PrologEngine Engine() => new() { Out = new StringWriter() };

    [Theory]
    [InlineData("576460752303423487")]               // 2^59 - 1, the largest inline
    [InlineData("576460752303423488")]               // 2^59, the smallest on the heap
    [InlineData("-576460752303423488")]              // -2^59, the smallest inline
    [InlineData("-576460752303423489")]
    [InlineData("9223372036854775807")]              // 2^63 - 1
    [InlineData("-9223372036854775808")]             // -2^63
    [InlineData("9223372036854775808")]              // 2^63
    [InlineData("123456789012345678901234567890")]
    public void EveryConversionFromTextGivesTheIntegerTheReaderReads(string digits)
    {
        var e = Engine();

        Assert.True(e.Query($@"X = {digits},
            atom_codes('{digits}', Cs), atom_chars('{digits}', Chs),
            number_codes(A, Cs), A == X,
            number_chars(B, Chs), B == X,
            atom_number('{digits}', C), C == X,
            name(D, Cs), D == X,
            number_string(E, ""{digits}""), E == X.").Success);
    }
}
