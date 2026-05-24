using System.Collections.Generic;

namespace Shumway.Compiler.Parsing;

/// <summary>
/// ISO <c>double_quotes</c> flag values — controls how the parser
/// interprets a double-quoted literal like <c>"abc"</c>.
/// </summary>
public enum DoubleQuotesMode
{
    /// <summary>List of integer character codes — <c>"abc"</c> →
    /// <c>[97, 98, 99]</c>. ISO default.</summary>
    Codes,
    /// <summary>List of one-character atoms — <c>"abc"</c> →
    /// <c>[a, b, c]</c>. Common in SWI Prolog.</summary>
    Chars,
    /// <summary>The string as a single atom — <c>"abc"</c> →
    /// <c>'abc'</c>.</summary>
    Atom,
    /// <summary>Shumway's native PSTR representation — kept as the
    /// default because it's the cheapest representation the engine
    /// has (no list cell allocations per character).</summary>
    String,
}

/// <summary>
/// Mutable, parser-visible flag state (chunk 58). The host engine owns
/// one of these and threads a reference through to every
/// <see cref="Parser"/> it constructs so query-time / consult-time
/// <c>set_prolog_flag</c> calls take effect on subsequent parses.
/// Only <c>double_quotes</c> is parser-relevant in Phase 1; future
/// flags (e.g. <c>occurs_check</c>) can land on this same object.
/// </summary>
public sealed class PrologFlags
{
    public DoubleQuotesMode DoubleQuotes { get; set; } = DoubleQuotesMode.String;

    /// <summary>Chunk 152 — ISO §6.4.2 character-conversion flag. When
    /// <c>true</c>, the lexer maps every character it reads outside
    /// of quoted contexts (quoted atoms, strings, character-code
    /// literals, comments) through <see cref="CharConversion"/>
    /// before tokenizing. ISO doesn't pin a default; matching GNU
    /// Prolog and SWI we keep it <c>off</c> so consult of programs
    /// that don't set it sees no surprise transformations.</summary>
    public bool CharConversionEnabled { get; set; }

    /// <summary>The conversion table itself: a character maps to its
    /// replacement, missing entries pass through unchanged. The
    /// <c>:- char_conversion(In, Out)</c> directive and the runtime
    /// <c>char_conversion/2</c> builtin write here; the
    /// <c>current_char_conversion/2</c> builtin reads from here.
    /// An identity mapping (In == Out) removes the entry per ISO
    /// §8.14.9.</summary>
    public Dictionary<char, char> CharConversion { get; } = new();
}
