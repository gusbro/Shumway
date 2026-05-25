namespace Shumway.Embedding;

/// <summary>One error produced by the
/// <see cref="ShmoCompiler.TryCompileSource"/> /
/// <see cref="ShmoCompiler.TryCompileFile"/> error-recovery path.
/// Carries the human-readable message plus the 1-based source
/// position so the CLI can render <c>file:line:col: message</c>
/// diagnostics in the standard compiler shape.</summary>
public sealed class ShmoCompileError
{
    public string Message { get; }
    public int Line { get; }
    public int Column { get; }

    public ShmoCompileError(string message, int line, int column)
    {
        Message = message;
        Line = line;
        Column = column;
    }

    public override string ToString() => $"{Line}:{Column}: {Message}";
}

/// <summary>Outcome of one
/// <see cref="ShmoCompiler.TryCompileSource"/> /
/// <see cref="ShmoCompiler.TryCompileFile"/> call.
/// <see cref="Object"/> is the compiled artifact iff there were
/// zero errors; otherwise <c>null</c> and the diagnostics are in
/// <see cref="Errors"/>.</summary>
public sealed class ShmoCompileResult
{
    public ShmoObject? Object { get; }
    public IReadOnlyList<ShmoCompileError> Errors { get; }
    public bool Success => Errors.Count == 0;

    public ShmoCompileResult(ShmoObject? obj, IReadOnlyList<ShmoCompileError> errors)
    {
        Object = obj;
        Errors = errors;
    }
}
