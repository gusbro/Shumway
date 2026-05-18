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
}
