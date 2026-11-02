using Shumway.Compiler.Lexer;
using Shumway.Core;

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

    /// <summary>The file <see cref="Line"/> is in when it is not the one being
    /// compiled: a file pulled in by <c>:- include/1</c>. Null otherwise.</summary>
    public string? File { get; }

    public ShmoCompileError(string message, int line, int column, string? file = null)
    {
        Message = message;
        Line = line;
        Column = column;
        File = file;
    }

    /// <summary>A diagnostic at <paramref name="position"/>, in the included file
    /// the position names, if any.</summary>
    internal static ShmoCompileError At(string message, SourcePosition position)
        => new(message, position.Line, position.Column,
            position.FileId != 0 ? DebugSiteTable.FileName(position.FileId) : null);

    /// <summary>Where the diagnostic is, <c>file:line:col</c>:
    /// <paramref name="compiledFile"/> unless it is in an included file, which
    /// is named relative to the working directory when it lies under it.</summary>
    public string Where(string compiledFile)
        => $"{(File is null ? compiledFile : DisplayPath(File))}:{Line}:{Column}";

    private static string DisplayPath(string path)
    {
        string cwd = Directory.GetCurrentDirectory();
        if (cwd.Length > 0 && cwd[cwd.Length - 1] != Path.DirectorySeparatorChar)
            cwd += Path.DirectorySeparatorChar;
        return path.StartsWith(cwd, StringComparison.OrdinalIgnoreCase)
            ? path.Substring(cwd.Length)
            : path;
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

    /// <summary>non-fatal diagnostics (same shape as
    /// <see cref="Errors"/>). Currently produced for unknown directives
    /// under <c>arity_compat</c>; warnings never fail the compile and
    /// don't affect <see cref="Success"/>.</summary>
    public IReadOnlyList<ShmoCompileError> Warnings { get; }

    public bool Success => Errors.Count == 0;

    public ShmoCompileResult(ShmoObject? obj, IReadOnlyList<ShmoCompileError> errors,
        IReadOnlyList<ShmoCompileError>? warnings = null)
    {
        Object = obj;
        Errors = errors;
        Warnings = warnings ?? Array.Empty<ShmoCompileError>();
    }
}
