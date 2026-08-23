namespace Shumway.Core;

/// <summary>
/// What the elements of a packed list (<see cref="Tag.Pstr"/>) are: character
/// atoms or character codes. ADR-047 — the presentation travels with the datum,
/// in the header cell, so no operation reads the <c>double_quotes</c> flag to
/// decide what an existing term's elements are.
/// </summary>
public enum TextKind : byte
{
    /// <summary>Elements unify as <see cref="Tag.Int"/> code points.</summary>
    Codes = 0,

    /// <summary>Elements unify as one-character <see cref="Tag.Atom"/>s.</summary>
    Chars = 1,
}
