using Shumway.Embedding;

namespace Shumway.TopLevel;

/// <summary>
/// The engine-facing half of a Prolog top level, with no opinion about how the
/// user is talking to it. Everything a REPL does that is not console I/O lives
/// here: wrapping a query so residual constraints survive, handing back its
/// solutions one at a time, formatting them, and completing predicate names.
///
/// <para>The console REPL and the browser front-end are two skins over this.
/// Whatever they share belongs here; anything that reads a key or writes to a
/// terminal does not.</para>
/// </summary>
public sealed class TopLevelSession
{
    /// <summary>The engine this session drives. Callers configure it directly —
    /// notably <see cref="PrologEngine.Out"/>, which must be set BEFORE the
    /// first query: query setup builds the stream registry, and
    /// <c>user_output</c> keeps whatever writer it was handed then.</summary>
    public PrologEngine Engine { get; }

    public TopLevelSession(PrologEngine engine)
        => Engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <summary>Loads clauses from Prolog source text.</summary>
    public void Consult(string source) => Engine.ConsultString(source);

    /// <summary>Starts <paramref name="queryText"/> and returns a cursor over its
    /// solutions. The search itself is pull-based — the caller decides when to
    /// take each solution, and how many.
    ///
    /// <para>Text that does not parse is handed to the engine as raw text so the
    /// engine reports the syntax error, which means this THROWS the parser's
    /// exception rather than returning: the string form of a query parses
    /// eagerly, before it yields anything. The caller renders that the way it
    /// renders any other engine error. (<see cref="QueryRun.Parsed"/> is false
    /// for the narrower case of text this session could not wrap but the engine
    /// still accepts.)</para></summary>
    public QueryRun StartQuery(string queryText)
    {
        ArgumentNullException.ThrowIfNull(queryText);

        var cts = new CancellationTokenSource();
        if (!QueryWrapper.TryWrap(Engine, queryText, out var wrapped, out var userVars))
            return new QueryRun(
                Engine, Engine.QueryAll(queryText).GetEnumerator(),
                Array.Empty<string>(), parsed: false, cts);

        // The goal about to run is not the goal the user typed — it is theirs
        // conjoined with copy_term/3. Tell the debugger what was actually typed,
        // or a query frame reads back with the top level's plumbing stapled on.
        Engine.QueryLabel = queryText.TrimEnd().TrimEnd('.');

        return new QueryRun(
            Engine, Engine.QueryAll(wrapped, cts.Token).GetEnumerator(),
            userVars, parsed: true, cts);
    }

    /// <summary>Predicate names starting with <paramref name="prefix"/>, sorted and
    /// deduplicated — for a REPL's Tab completion or an editor's autocomplete.</summary>
    public IReadOnlyList<string> Complete(string prefix)
        => PredicateCompletion.Matching(Engine, prefix);
}
