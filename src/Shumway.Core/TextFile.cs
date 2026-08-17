namespace Shumway.Core;

/// <summary>Reading a whole file the way a Prolog TEXT stream reads it
/// (ADR-045). <c>consult/1</c>, <c>:- include/1</c> and the offline
/// compilers slurp a source file with <c>File.ReadAllText</c> rather than
/// through a stream handle; without this they would see the CR that
/// <c>open/3</c> + <c>read/2</c> hides, so the same file could yield two
/// different quoted atoms depending on how it was loaded.</summary>
public static class TextFile
{
    /// <summary>Applies the text-stream newline rule to already-read text:
    /// a CR-LF pair becomes <c>\n</c> where the platform calls for it. A
    /// LONE CR is left alone — only the pair is a line terminator.</summary>
    public static string NormalizeNewlines(string text) =>
        PositionTrackingReader.TranslateNewlinesByDefault && text.Contains('\r')
            ? text.Replace("\r\n", "\n")
            : text;

    /// <summary>Reads a Prolog source file as a text stream would deliver
    /// it.</summary>
    public static string ReadAllText(string path) =>
        NormalizeNewlines(System.IO.File.ReadAllText(path));
}
