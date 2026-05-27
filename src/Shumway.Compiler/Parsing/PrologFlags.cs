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

    /// <summary>Command-line argument list, surfaced as the
    /// <c>argv</c> Prolog flag. The host (REPL or embedding caller)
    /// is responsible for populating this; the engine itself doesn't
    /// touch it. Defaults to an empty list. Each entry is materialised
    /// as a Prolog atom — matching SWI / SICStus / GNU.</summary>
    public IReadOnlyList<string> Argv { get; set; } = Array.Empty<string>();

    /// <summary>ISO <c>unknown</c> flag. Controls the engine's
    /// reaction to a call to an undefined predicate: <c>error</c>
    /// (raise <c>existence_error/2</c>, the default), <c>fail</c>
    /// (silently fail), or <c>warning</c> (emit a warning then
    /// fail). The engine itself currently always errors; the value
    /// surfaced here is informational until the engine wires it
    /// through dispatch.</summary>
    public string Unknown { get; set; } = "error";

    /// <summary>ISO <c>occurs_check</c> flag. <c>false</c> (the
    /// default and what Shumway implements) means unification skips
    /// the occurs check; <c>true</c> would enable it, <c>error</c>
    /// would unify-with-error. Informational for now.</summary>
    public string OccursCheck { get; set; } = "false";

    /// <summary>Shumway-specific flag <c>implicit_dynamic</c>. When
    /// <c>true</c> (the default), <c>assertz/1</c> and <c>asserta/1</c>
    /// on a predicate that has no clauses and no <c>:- dynamic</c>
    /// declaration auto-promote it to a dynamic predicate, matching
    /// SWI-Prolog / SICStus / GNU Prolog behaviour (none of those
    /// engines require <c>:- dynamic foo/N.</c> upfront — the first
    /// assert on an undefined predicate creates it as dynamic).
    /// When <c>false</c>, Shumway preserves the stricter ISO
    /// interpretation: an undeclared predicate raises
    /// <c>permission_error(modify, static_procedure, _)</c>.
    ///
    /// <para>Auto-promotion never fires when the predicate already
    /// has static clauses (consulted from source) or is a registered
    /// builtin — both raise <c>permission_error</c> regardless of the
    /// flag's value, matching every Prolog system's behaviour.</para>
    ///
    /// <para>The flag is settable via
    /// <c>set_prolog_flag(implicit_dynamic, true|false)</c>; the
    /// default (<c>true</c>) is chosen to maximise compatibility with
    /// programs written for other Prolog implementations.</para></summary>
    public bool ImplicitDynamic { get; set; } = true;
}
