using Shumway.Embedding;

namespace Shumway.TopLevel;

/// <summary>
/// One query in progress: a pull-based cursor over its solutions.
///
/// <para>Pull-based on purpose. A top level does not know how many solutions
/// the user wants — it shows one and waits to be asked for the next. The
/// console asks with <c>;</c>, a web UI asks with a button, and neither has to
/// know how the other decides. The engine's search runs on the calling thread
/// and stops at its next safe point when the token is cancelled.</para>
/// </summary>
public sealed class QueryRun : IDisposable
{
    private readonly PrologEngine _engine;
    private readonly IEnumerator<Solution> _solutions;
    private readonly CancellationTokenSource _cts;
    private bool _disposed;

    internal QueryRun(
        PrologEngine engine, IEnumerator<Solution> solutions,
        IReadOnlyList<string> userVariables, bool parsed, CancellationTokenSource cts)
    {
        _engine = engine;
        _solutions = solutions;
        _cts = cts;
        UserVariables = userVariables;
        Parsed = parsed;
    }

    /// <summary>The query's named variables, in source order. Empty when the
    /// goal has none, or when it did not parse.</summary>
    public IReadOnlyList<string> UserVariables { get; }

    /// <summary>False when the text did not parse: the run then executes the raw
    /// text so the engine reports the syntax error exactly as it always has.
    /// Such a run yields no formatted bindings.</summary>
    public bool Parsed { get; }

    /// <summary>The solution <see cref="MoveNext"/> last produced.</summary>
    public Solution Current => _solutions.Current;

    /// <summary>True when the engine knows the current solution is the last one,
    /// so a top level can print <c>.</c> instead of offering <c>;</c>.</summary>
    public bool IsLast => _solutions.Current.IsLast;

    /// <summary>Advances to the next solution. Returns false when the query has
    /// no more — the caller distinguishes "no more" from "aborted" by checking
    /// the cancellation token it passed in.</summary>
    public bool MoveNext() => _solutions.MoveNext();

    /// <summary>Requests that the running search stop. The engine observes this
    /// at its next safe point, so it is prompt rather than instantaneous.</summary>
    public void Cancel() => _cts.Cancel();

    /// <summary>Renders <see cref="Current"/> for display: bindings plus any
    /// residual constraints, wrapped to <paramref name="width"/> columns.</summary>
    public string Format(int width)
        => SolutionFormatter.Format(_engine, _solutions.Current, UserVariables, width);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _solutions.Dispose();
        _cts.Dispose();
    }
}
