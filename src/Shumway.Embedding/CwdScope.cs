namespace Shumway.Embedding;

/// <summary>ADR-071: what an engine's working directory is. Chosen by the
/// host with <see cref="PrologEngine.SetCwdScope"/>, at any time; a program
/// reads it as the flag <c>cwd_scope</c> and cannot change it.</summary>
public enum CwdScope
{
    /// <summary>The directory of the process, shared by every engine in it.
    /// The default.</summary>
    Process,

    /// <summary>A directory of the engine's own. While it lasts the engine
    /// neither reads nor changes the process directory.</summary>
    Engine,
}
