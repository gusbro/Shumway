using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>What print_message/2 writes to user_error: Kind: Message, the
/// text of format(F, A), an unbound message as any other term, and nothing
/// for an informational message while the verbose flag is silent. The tests
/// swap Console.Error, so the class runs apart from the parallel ones.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class PrintMessageOutputTests
{
    private static string CaptureError(Action a)
    {
        var prev = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try { a(); } finally { Console.SetError(prev); }
        return sw.ToString();
    }

    // The verbose flag: normal by default, silent under the top level's -q.
    // While silent, print_message/2 keeps informational messages to itself,
    // and only those; other values are a domain error.
    [Fact]
    public void Verbose_SilentQuietsInformationalMessagesOnly()
    {
        bool normal = false, silent = false, refused = false;
        // user_error is the console's as the engine is made: make it inside.
        string printed = CaptureError(() =>
        {
            var e = new PrologEngine();
            normal = e.Query("current_prolog_flag(verbose, normal).").Success;
            e.Query("print_message(informational, shown).");
            silent = e.Query("set_prolog_flag(verbose, silent), current_prolog_flag(verbose, silent).").Success;
            e.Query("print_message(informational, hidden).");
            e.Query("print_message(warning, still_shown).");
            refused = e.Query("catch(set_prolog_flag(verbose, loud), error(domain_error(flag_value, _), _), true).").Success;
        });
        Assert.True(normal && silent && refused);
        Assert.Contains("shown", printed);
        Assert.DoesNotContain("hidden", printed);
        Assert.Contains("still_shown", printed);
    }

    // Kind: Message, the text of format(F, A) for one, and an unbound message
    // printed as any other term: it once bound to format(F, A) and raised.
    [Fact]
    public void PrintMessage_PrintsTheTermTheFormatOrTheVariable()
    {
        bool unbound = false;
        string printed = CaptureError(() =>
        {
            var e = new PrologEngine();
            e.Query("print_message(warning, hello(world)).");
            e.Query("print_message(warning, format('~w and ~w', [a, b])).");
            unbound = e.Query("print_message(warning, _).").Success;
        });
        Assert.Contains("warning: hello(world)", printed);
        Assert.Contains("warning: a and b", printed);
        Assert.True(unbound, printed);
        Assert.Matches(@"warning: _\w*", printed);
    }
}
