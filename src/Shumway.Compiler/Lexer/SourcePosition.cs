namespace Shumway.Compiler.Lexer;

/// <summary>
/// Identifies a location in a Prolog source file. <see cref="Line"/> and
/// <see cref="Column"/> are 1-based and intended for human-readable error messages;
/// <see cref="Offset"/> is the 0-based UTF-16 code-unit offset into the source string
/// (useful for slicing without recomputing).
///
/// <para><see cref="FileId"/> (ADR-035) is which file — a
/// <c>Shumway.Core.DebugSiteTable</c> id, 0 when unknown. It rides here, on the position,
/// rather than being handed to the compiler alongside the clauses, because by the time a
/// clause is compiled the consult that read it is long over: compilation happens at query
/// setup, and a compiler field saying "the file we are reading" is by then saying the
/// name of some other file entirely — or, as the first Visual Studio run showed,
/// <c>&lt;string&gt;</c> for every clause in the program. A position knows its own file,
/// and every transform that rebuilds a term already carries the position along.</para>
/// </summary>
public readonly record struct SourcePosition(int Line, int Column, int Offset, int FileId = 0)
{
    public static readonly SourcePosition Start = new(Line: 1, Column: 1, Offset: 0);

    public override string ToString() => $"{Line}:{Column}";
}
