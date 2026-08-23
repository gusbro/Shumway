using Shumway.Compiler.Parsing;

namespace Shumway.Embedding;

/// <summary>ADR-040 — the multi-dialect shim registry. Each Prolog system whose
/// libraries we can host is a "dialect pack": the library names it provides (as
/// Shumway-equivalent source, or <c>""</c> for a prelude-covered no-op) plus the
/// parse defaults its sources assume (notably <c>double_quotes</c>). Replaces the
/// flat, Scryer-only <see cref="CompatLibraries"/> switch — that data is now the
/// <c>scryer</c> pack.
///
/// <para>Resolution prefers the ACTIVE dialect (ADR-040 selection), then falls
/// back to every pack. So an undeclared dialect still resolves a name unique to
/// one system, and <b>coexistence is the default</b>: a Scryer library and an SWI
/// library load side by side, each parsed with its own <c>double_quotes</c>. The
/// active dialect only disambiguates a name that two packs both define.</para></summary>
internal static class DialectRegistry
{
    /// <summary>A dialect's shim: its name, the parse-time <c>double_quotes</c> its
    /// sources assume, and a resolver from a library name to its shim source
    /// (<c>Found=false</c> when this pack does not provide the name).</summary>
    internal sealed record Pack(
        string Name,
        DoubleQuotesMode DoubleQuotes,
        System.Func<string, (bool Found, string Source)> Resolve);

    // The scryer pack IS the existing CompatLibraries data; Scryer's default is
    // double_quotes = chars.
    private static readonly Pack Scryer = new(
        "scryer", DoubleQuotesMode.Chars,
        name => CompatLibraries.TryGet(name, out string s) ? (true, s) : (false, ""));

    // The swi pack — SWI's double_quotes default is codes. The list-oriented
    // libraries SWI programs import are covered by our prelude, so importing them
    // is a no-op that just marks them available (a real SWI .pl on the search path
    // resolves from the FILE first — this pack is the fallback for names we cover
    // natively). apply_macros is a compile-time optimiser: a pure no-op for us.
    // A fuller SWI shim (yall lambdas, real assoc, …) is future data here.
    private static readonly Pack Swi = new(
        "swi", DoubleQuotesMode.Codes,
        name => name switch
        {
            "apply" or "apply_macros" or "lists" or "pairs" or "ordsets"
                or "error" or "debug" or "aggregate" or "assoc"
                or "yall" => (true, ""),
            // Import-scoped, like SWI itself: limit/2 maps onto the engine's
            // call_with_limit/2; offset/2 re-exports the bare prelude public.
            "solution_sequences" => (true,
                ":- module(solution_sequences, [limit/2, offset/2]).\n"
                + "limit(N, Goal) :- call_with_limit(N, Goal).\n"),
            _ => (false, ""),
        });

    // The trealla pack — Trealla's default is double_quotes = chars. Its
    // library sources are pure Prolog over ordinary builtins (no '$' C
    // internals the way Scryer's are), so a configured tree
    // (-L trealla:dir) resolves most names from the FILE; this pack covers
    // what an unconfigured engine can still honour. freeze/when live in our
    // coroutining library, clpz maps onto native clpfd (`in`/`ins`/label).
    private static readonly Pack Trealla = new(
        "trealla", DoubleQuotesMode.Chars,
        name => name switch
        {
            // NOT "dcgs" and NOT "format": Trealla's dcgs has seq//1 & co
            // and its format IS the format_//2 non-terminal — beyond the
            // prelude's phrase/2,3 and format/2,3. Falling through lets the
            // scryer pack's real shims serve them (a no-op here also STARVES
            // the format native-override, which re-resolves "format" under
            // this dialect scope).
            "lists" or "charsio" or "error"
                or "pairs" or "ordsets" or "debug" or "gensym"
                or "iso_ext" or "terms" => (true, ""),
            "freeze" or "when" => (true, ":- use_module(library(coroutining)).\n"),
            "clpz" => (true, ":- use_module(library(clpfd)).\n"),
            // Trealla's clpz references arithmetic:popcount/2 in its
            // reification residuals; the rest of their arithmetic.pl is
            // evaluable-function machinery our `is` covers natively.
            "arithmetic" => (true,
                ":- module(arithmetic).\n:- public popcount/2.\npopcount(N, C) :- C is popcount(N).\n"),
            _ => (false, ""),
        });

    private static readonly Pack[] Packs = { Scryer, Swi, Trealla };

    /// <summary>True when <paramref name="name"/> is a registered dialect.</summary>
    internal static bool IsKnownDialect(string name) =>
        System.Array.Exists(Packs, p => p.Name == name);

    /// <summary>The <c>double_quotes</c> a dialect's sources are parsed with —
    /// used to scope the flag while loading a library tagged with that dialect
    /// (D5.2). Chars for an unknown name (the conservative Scryer default).</summary>
    internal static DoubleQuotesMode DoubleQuotesOf(string dialect)
    {
        foreach (var p in Packs)
            if (p.Name == dialect) return p.DoubleQuotes;
        return DoubleQuotesMode.Chars;
    }

    /// <summary>Resolves library <paramref name="name"/>. If
    /// <paramref name="activeDialect"/> names a pack it is tried first; then every
    /// pack, so coexistence is the default. On success <paramref name="source"/> is
    /// the shim source (<c>""</c> for a prelude no-op), <paramref
    /// name="doubleQuotes"/> the dialect's parse default (for scoping the consult),
    /// and <paramref name="dialect"/> the pack that matched.</summary>
    internal static bool TryResolve(string? activeDialect, string name,
        out string source, out DoubleQuotesMode doubleQuotes, out string dialect)
    {
        if (activeDialect is not null)
            foreach (var p in Packs)
                if (p.Name == activeDialect && TryPack(p, name, out source, out doubleQuotes, out dialect))
                    return true;
        foreach (var p in Packs)
        {
            if (p.Name == activeDialect) continue;   // already tried above
            if (TryPack(p, name, out source, out doubleQuotes, out dialect))
                return true;
        }
        source = ""; doubleQuotes = DoubleQuotesMode.Chars; dialect = "";
        return false;
    }

    private static bool TryPack(Pack p, string name,
        out string source, out DoubleQuotesMode doubleQuotes, out string dialect)
    {
        var (found, src) = p.Resolve(name);
        if (found) { source = src; doubleQuotes = p.DoubleQuotes; dialect = p.Name; return true; }
        source = ""; doubleQuotes = p.DoubleQuotes; dialect = p.Name;
        return false;
    }
}
