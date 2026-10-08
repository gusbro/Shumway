using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A mounted tree's <c>library(charsio)</c> is replaced by the
/// engine's own surface only when it is Trealla's, which rides natives the
/// engine does not have. Scryer's loads as it is: the scryer shim provides
/// what it calls, and it is where read_from_chars/2 and the rest come from.
/// Both files call <c>'$char_type'</c>, so that cannot be what tells them
/// apart; Trealla's <c>'$get_chars'</c> does.</summary>
public sealed class CharsioOverrideTests
{
    private static string Library(string charsioSource)
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway_charsio_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "charsio.pl"), charsioSource);
        return dir;
    }

    [Fact]
    public void AScryerShapedCharsioLoads()
    {
        string dir = Library("""
            :- module(charsio, [char_type/2, mounted_marker/0]).
            char_type(C, T) :- '$char_type'(C, T).
            mounted_marker.
            """);
        try
        {
            var e = new PrologEngine();
            e.AddLibraryDirectory(dir, "scryer");
            e.ConsultString(":- use_module(library(charsio)).");
            Assert.True(e.Query("mounted_marker.").Success);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ATreallaShapedCharsioIsReplaced()
    {
        string dir = Library("""
            :- module(charsio, [get_n_chars/3, mounted_marker/0]).
            get_n_chars(S, N, Cs) :- '$get_chars'(S, N, Cs).
            mounted_marker.
            """);
        try
        {
            var e = new PrologEngine();
            e.AddLibraryDirectory(dir, "trealla");
            e.ConsultString(":- use_module(library(charsio)).");
            Assert.True(e.Query(
                "catch((mounted_marker, fail), error(existence_error(procedure, mounted_marker/0), _), true).")
                .Success);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
