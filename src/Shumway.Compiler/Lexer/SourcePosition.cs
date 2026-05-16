namespace Shumway.Compiler.Lexer;

/// <summary>
/// Identifies a location in a Prolog source file. <see cref="Line"/> and
/// <see cref="Column"/> are 1-based and intended for human-readable error messages;
/// <see cref="Offset"/> is the 0-based UTF-16 code-unit offset into the source string
/// (useful for slicing without recomputing).
/// </summary>
public readonly record struct SourcePosition(int Line, int Column, int Offset)
{
    public static readonly SourcePosition Start = new(Line: 1, Column: 1, Offset: 0);

    public override string ToString() => $"{Line}:{Column}";
}
