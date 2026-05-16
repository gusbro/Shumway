using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Basic I/O builtins. All output goes through <see cref="Engine.Out"/>,
/// which defaults to <see cref="System.Console.Out"/> but can be swapped
/// for a <see cref="System.IO.StringWriter"/> (or any other writer) by the
/// embedding caller — particularly useful for capturing program output in
/// tests.
/// </summary>
public static class IOBuiltins
{
    /// <summary><c>write(X)</c> — writes the canonical text representation
    /// of X to the engine's output sink, no trailing newline.</summary>
    public static bool Write(Engine engine)
    {
        TermRenderer.Render(engine, engine.GetRegister(0), engine.Out);
        return true;
    }

    /// <summary><c>nl</c> — writes a single newline character.</summary>
    public static bool Nl(Engine engine)
    {
        engine.Out.WriteLine();
        return true;
    }

    /// <summary><c>writeln(X)</c> — equivalent to <c>write(X), nl</c>.</summary>
    public static bool Writeln(Engine engine)
    {
        TermRenderer.Render(engine, engine.GetRegister(0), engine.Out);
        engine.Out.WriteLine();
        return true;
    }
}
